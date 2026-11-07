using System.Reflection;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Workspace search and filter row, Project menu groups, Project Information without
// cross-links, Prepare a copy without the SDL option, About copyright, the taller Start Page with its blank row, and F3
// Project Information (application key, menu, Workspace status line).
// The real WorkbenchApp runs on NullDriver; keys and commands go through the shared event queue as a user's would.
// Optional logical captures (Captures): TSWB_PROJECT_INFORMATION_CAPTURE=<directory>, TSWC1_PHASE=before|after. The Prepare / Target
// Diagnostics states use an ordinary target (TSWB_PRODUCT_TARGET) and the payload (TSWB_PROXY_PAYLOAD) when they are set.
[Collection("NonParallel")]
public sealed class ProjectInformationUxTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;

    // ── logical captures (before / after) ────────────────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task Captures(int width, int height)
    {
        if (Environment.GetEnvironmentVariable("TSWB_PROJECT_INFORMATION_CAPTURE") is not { Length: > 0 }) return;
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            string one = SaveProject(Path.Combine(root, "projects"), "MyTest2.twproj", new WorkbenchProject { Name = "MyTest1", Target = Target, OutputRoot = Path.Combine(root, "runs-a") });
            // One recent project first, then a populated list (eight, the most the preferences keep).
            WorkbenchPreferences.Update(Path.Combine(root, "settings.json"), p => p.WithRecent(one));
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await Capture(bridge, "start-page-one");
            }, showStart: true);

            string sample = SaveProject(Path.Combine(root, "projects", "a much longer folder name for the recent list"), "Sample.twproj",
                new WorkbenchProject { Name = "Sample target project", Target = Target, OutputRoot = Path.Combine(root, "runs-b") });
            string? ordinaryExe = FixIssuesTests.Original();
            string? ordinary = ordinaryExe is null ? null : SaveProject(Path.Combine(root, "projects", "ordinary"), "Ordinary.twproj",
                new WorkbenchProject { Name = "Ordinary application", Target = ordinaryExe, OutputRoot = Path.Combine(root, "runs-c") });
            WorkbenchPreferences.Update(Path.Combine(root, "settings.json"), p =>
            {
                for (int i = 1; i <= 5; i++) p = p.WithRecent(SaveProject(Path.Combine(root, "projects", "more"), $"Project{i}.twproj", new WorkbenchProject { Name = $"Project {i}", Target = Target, OutputRoot = Path.Combine(root, "runs-" + i) }));
                if (ordinary is not null) p = p.WithRecent(ordinary);
                return p.WithRecent(sample);
            });
            if (TvDemoDeploymentUxTests.Payload() is { } payload) Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, payload);
            TargetDiagnostics.Preparation = new TargetPreparationService(Path.Combine(root, "prepared"), TvDemoDeploymentUxTests.Payload());
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await Capture(bridge, "start-page-many");
                Send(WorkbenchApp.About); await Wait(bridge, () => Modal(app, "About TSharpWorkbench"));
                await Capture(bridge, "about"); Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "About TSharpWorkbench") is null);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(sample), sample));
                await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                await bridge.Invoke(() => app.Notifications.Clear());
                await Capture(bridge, "workspace");
                Press(Keys.kbAltP); await Until(bridge, () => MenuOpen(app)); await Capture(bridge, "project-menu"); Press(Keys.kbEsc); await Until(bridge, () => !MenuOpen(app));
                Send(WorkbenchDialog.ProjectInfo); await Wait(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                await Capture(bridge, "project-info"); Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle) is null);
                if (ordinary is null) return;
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(ordinary), ordinary));
                await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.ProjectPath == ordinary ? p : null, 60);
                await bridge.Invoke(() => app.Notifications.Clear());
                Send(WorkbenchDialog.DiagnoseTarget); await Wait(bridge, () => Modal(app, WorkbenchTargetDiagnosticsDialog.DialogTitle), 60);
                await Capture(bridge, "target-diagnostics"); Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchTargetDiagnosticsDialog.DialogTitle) is null);
                Send(WorkbenchDialog.PrepareTargetCommand); await Wait(bridge, () => Modal(app, WorkbenchPrepareTargetDialog.DialogTitle), 60);
                await Settle(bridge); await Task.Delay(1500); await Capture(bridge, "prepare-target");
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchPrepareTargetDialog.DialogTitle) is null, 60);
            }, showStart: true);
        }
        finally { Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, null); Delete(root); }
    }

    // ── UX-START-2: the taller Start Page with its empty row ─────────────────────────────

    [Theory]
    [InlineData(80, 25, 1)]
    [InlineData(120, 36, 1)]
    [InlineData(140, 40, 1)]
    [InlineData(160, 50, 1)]
    [InlineData(120, 36, 8)]
    [InlineData(160, 50, 8)]
    [InlineData(80, 20, 8)]
    public async Task StartPageIsTallerWithAnEmptyRowAfterTheIdentity(int width, int height, int recents)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            WorkbenchPreferences.Update(Path.Combine(root, "settings.json"), p =>
            {
                for (int i = 1; i <= recents; i++) p = p.WithRecent(SaveProject(Path.Combine(root, "projects"), $"P{i}.twproj", new WorkbenchProject { Name = $"Project {i}", Target = Target, OutputRoot = Path.Combine(root, "runs-" + i) }));
                return p;
            });
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                var start = await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => app.Notifications.Clear()); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    string[] rows = Rows();
                    int top = start.origin.y + 1;   // desktop row 0 is screen row 1 (menu bar)
                    string Row(int y) => rows[top + y].Substring(start.origin.x, start.size.x);
                    string Inner(int y) => Row(y)[1..^1];
                    Assert.Contains(" Start Page ", Row(0));
                    // Identity centred, then at least one completely empty row before "Recent projects".
                    int version = Enumerable.Range(1, start.size.y - 2).First(y => Inner(y).Trim().StartsWith("Version "));
                    Assert.Equal(WorkbenchStartWindow.Identity, new[] { Inner(version - 2).Trim(), Inner(version - 1).Trim(), Inner(version).Trim() });
                    foreach (int y in new[] { version - 2, version - 1, version })
                    {
                        string line = Inner(y); int left = line.Length - line.TrimStart().Length, right = line.Length - line.TrimEnd().Length;
                        Assert.True(Math.Abs(left - right) <= 1, $"not centred: '{line}'");
                    }
                    Assert.True(string.IsNullOrWhiteSpace(Inner(version + 1)), $"row after Version is not empty: '{Inner(version + 1)}'");
                    int heading = Enumerable.Range(version + 1, start.size.y - version - 2).First(y => Inner(y).Contains("Recent projects"));
                    Assert.True(heading - version >= 2);
                    if (height >= 25) { Assert.Equal(WorkbenchStartWindow.Gap + 1, heading - version); Assert.True(string.IsNullOrWhiteSpace(Inner(1)), "top margin row"); }
                    // The list (name and path, scroll bar), then the three buttons; the window fits the desktop.
                    Assert.Equal(heading + 1, start.Recent.origin.y);
                    Assert.Contains(rows, r => r.Contains("Project 1") && r.Contains("P1.twproj"));
                    Assert.Contains(Children(start), v => v is TScrollBar);
                    var buttons = Children(start).OfType<TButton>().OrderBy(b => b.origin.x).ToArray();
                    Assert.Equal(["~O~pen", "Open pro~j~ect...", "~N~ew project..."], buttons.Select(b => b.Title).ToArray());
                    Assert.All(buttons, b => Assert.Equal(start.size.y - 3, b.origin.y));
                    Assert.True(start.Recent.origin.y + start.Recent.size.y < start.size.y - 3, "no empty row above the buttons");
                    Assert.True(start.origin.y >= 0 && start.origin.y + start.size.y <= app.DeskTop!.size.y && start.origin.x + start.size.x <= width);
                    int rowsForList = Math.Clamp(recents, 4, 8);
                    if (height >= 25) Assert.Equal(rowsForList + 12, start.size.y);   // Had rowsForList + 10
                    Assert.True(start.Recent.size.y >= Math.Min(rowsForList, 2));
                    Assert.Same(start.Recent, start.current);
                });
                await CaptureAs(bridge, $"start-page-{recents}");
                Press(Keys.kbEnter);
                await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // ── UX-START-2: F3 Project Information ────────────────────────────────────────────

    [Fact]
    public async Task F3OpensProjectInformationOnceAndOnlyWithAProjectAndNeverInsideAModal()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                // No project: the usual no-project feedback, nothing opens.
                await Wait(bridge, () => app.Start);
                Press(Keys.kbEsc); await Until(bridge, () => app.Start is null);
                await bridge.Invoke(() => app.Notifications.Clear());
                Press(Keys.kbF3); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Null(Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                    Assert.Contains(app.Notifications.History, n => n.Level == NoticeLevel.Warning && n.Message.StartsWith("No project is open"));
                });
                // With a project: F3 is exactly Project > Information...; repeated presses never stack a second dialog.
                string project = SaveProject(Path.Combine(root, "p"), "f3.twproj", new WorkbenchProject { Name = "F3 sample", Target = Target, OutputRoot = Path.Combine(root, "runs-f3") });
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                Press(Keys.kbF3);
                var dialog = (WorkbenchProjectInfoDialog)await Wait(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                Press(Keys.kbF3); Press(Keys.kbF3); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Same(dialog, app.DeskTop!.TopView());
                    Assert.Equal(1, Modals(app, WorkbenchProjectInfoDialog.DialogTitle));
                    Assert.Same(dialog, main.ProjectInfoDialog);
                    Assert.Contains(dialog.Lines, l => l.Contains("F3 sample"));
                    Assert.Equal(["Close"], Descendants(dialog).OfType<TButton>().Select(b => b.Title).ToArray());
                });
                await CaptureAs(bridge, "f3-project-info");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                // Menu parity: the same command, F3 printed on Project > Information...
                Press(Keys.kbAltP); await Until(bridge, () => MenuOpen(app));
                Press((ushort)'i'); await Wait(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                // Inside an application-modal dialog F3 does nothing (Project Properties stays on top, nothing stacks).
                Press(Keys.kbF4);
                var props = await Wait(bridge, () => Modal(app, "Project Properties"));
                Press(Keys.kbF3); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(props, app.DeskTop!.TopView()); Assert.Equal(0, Modals(app, WorkbenchProjectInfoDialog.DialogTitle)); });
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                // F4 / F5 / F6 are unchanged.
                Press(Keys.kbF5);
                await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                await bridge.Invoke(() => { Assert.False(main.Busy); app.Notifications.Clear(); });
                Press(Keys.kbF6); await Settle(bridge);
                await bridge.Invoke(() => Assert.True(app.DeskTop!.current is WorkbenchResultsDialog || app.Notifications.History.Any(n => n.Message.Contains("result", StringComparison.OrdinalIgnoreCase))));
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ProjectMenuIsGroupedWithF3AndF4AndTheTargetActions()
    {
        using var driver = new DriverScope(120, 36);
        string root = Temp();
        try
        {
            using var app = new WorkbenchApp(Target, Path.Combine(root, "runs"), new(Target, Path.Combine(root, "runs"), null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
            var project = Items(app.MenuBar!.Menu!.Items).Single(i => i.Name == "~P~roject").SubMenu!;
            string[] shape = Items(project.Items).Select(i => i.Name is null ? "-" : i.Name + (i.Param is { Length: > 0 } p ? " " + p : "")).ToArray();
            Assert.Equal(["~N~ew project... Ctrl+N", "~O~pen project... Ctrl+O", "~S~ave Ctrl+S", "Save ~a~s...", "~C~lose project", "-",
                "~I~nformation... F3", "~P~roperties... F4", "-",
                "Target ~d~iagnostics...", "P~r~epare a copy...", "~F~ix original target...", "-", "E~x~it Alt+X"], shape);
            Assert.Equal(WorkbenchDialog.ProjectInfo, Items(project.Items).Single(i => i.Name == "~I~nformation...").Command);
            var letters = Items(project.Items).Where(i => i.Name is not null).Select(i => char.ToLowerInvariant(i.Name![i.Name.IndexOf('~') + 1])).ToArray();
            Assert.Equal(letters.Length, letters.Distinct().Count());
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task WorkspaceStatusLineIsAscendingWithF3AndKeepsNoticesReadable(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                await bridge.Invoke(() => app.Notifications.Clear()); await Settle(bridge);
                string all = " F1 Help  F2 Refresh  F3 Info  F4 Properties  F5 Run  F6 Results  F10 Menu  Space Mark  Ins Mark next";
                await bridge.Invoke(() => Assert.StartsWith(all, Rows()[^1]));
                await CaptureAs(bridge, "workspace-status");
                // With a warning the notice keeps its room; trailing keys are hidden first, the F-keys stay in order.
                await bridge.Invoke(() => app.Notifications.Warning("Target not ready: Proxy missing. A deliberately long warning for the width check."));
                await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    string status = Rows()[^1];
                    Assert.StartsWith(" F1 Help  F2 Refresh  F3 Info  F4 Properties  F5 Run  F6 Results  F10 Menu", status);
                    Assert.Contains("Target not ready", status);
                    if (width < 140) Assert.DoesNotContain("Ins Mark next", status);
                });
                await CaptureAs(bridge, "workspace-status-warning");
            });
        }
        finally { Delete(root); }
    }

    // ── UX-CLEANUP-2: Workspace search and filter row ───────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task SearchAndFilterSitOnTheirOwnRowAboveTheHeadersAndStillWork(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                await bridge.Invoke(() =>
                {
                    var search = main.SearchBox;
                    Assert.Equal(1, search.origin.y);
                    Assert.Equal(main.TestList.origin.y - 1, 3);                   // headers on row 3, the list from row 4
                    string[] rows = Rows();
                    int screenRow(int y) => main.origin.y + 1 + y;
                    Assert.True(string.IsNullOrWhiteSpace(rows[screenRow(2)].Substring(main.origin.x + main.TestList.origin.x, main.TestList.size.x)), "row 2 separates the tool row from the headers");
                    Assert.Contains("Scenario", rows[screenRow(3)]);
                    // The "Native-runnable only" filter is gone from the tool row.
                    Assert.DoesNotContain("Native-runnable", rows[screenRow(1)]);
                    Assert.True(search.origin.x + search.size.x <= main.size.x - 3);
                    Assert.True(search.size.x >= 20, "search field is wide enough");
                });
                await CaptureAs(bridge, "workspace-search-row");
                // Search filters by name/ID; marks survive.
                await bridge.Invoke(() => { main.TestList.FocusItemNum(0); main.TestList.Toggle(main.TestList.Current!.Id); main.SearchBox.SetData("menu"); });
                Press(Keys.kbTab); await Settle(bridge);
                await bridge.Invoke(() => { main.RefreshHeader(); });
                await Until(bridge, () => main.CaptionText.Contains("Showing ") && !main.CaptionText.Contains("Showing 16"));
                await bridge.Invoke(() => main.SearchBox.SetData(""));
                Press(Keys.kbTab); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Contains("Marked 1 of", main.CaptionText); Assert.Contains("Showing 16", main.CaptionText); });
            });
        }
        finally { Delete(root); }
    }

    // ── UX-CLEANUP-2: Prepare a copy without the SDL option; About ───────────────────────────

    [Fact]
    public void AboutCopyrightMatchesTheLicenseYears()
    {
        string license = File.ReadAllText(Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "LICENSE"));
        Assert.Contains("Copyright (c) 2021-2026 Lubomir Klimes", license);
        Assert.Contains("Copyright © 2021–2026 Lubomir Klimes", WorkbenchHelp.About());
        Assert.Equal("Version " + WorkbenchHelp.WorkbenchVersion, WorkbenchHelp.About()[2]);
    }

    [Fact]
    public void PrepareACopyAlwaysDeploysSdlAndOffersOnlyTheConsoleOption()
    {
        if (TvDemoDeploymentUxTests.Payload() is null || FixIssuesTests.Original() is null) return;
        using var driver = new DriverScope(140, 40);
        string root = Temp();
        try
        {
            var service = new TargetPreparationService(Path.Combine(root, "prepared"), TvDemoDeploymentUxTests.Payload());
            var inspection = service.Inspect(FixIssuesTests.Original()!);
            // Asking for the headless Proxy only still deploys the SDL component (the Proxy loads its driver assembly).
            var plan = service.CreatePlan(inspection, PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy);
            Assert.True(plan.Capabilities.HasFlag(PreparationCapabilities.Sdl));
            Assert.Contains(plan.Entries, e => e.Path == "TSharpVision.Drivers.SDL.dll" && e.Action == PlanAction.Add);
            Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task PrepareTargetWindowShowsNoSdlOption()
    {
        if (TvDemoDeploymentUxTests.Payload() is not { } payload || FixIssuesTests.Original() is not { } exe) return;
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, payload);
            TargetDiagnostics.Preparation = new TargetPreparationService(Path.Combine(root, "prepared"), payload);
            string project = SaveProject(Path.Combine(root, "p"), "ordinary.twproj", new WorkbenchProject { Name = "Ordinary", Target = exe, OutputRoot = Path.Combine(root, "runs-o") });
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.ProjectPath == project ? p : null, 60);
                Send(WorkbenchDialog.PrepareTargetCommand);
                var dialog = (WorkbenchPrepareTargetDialog)await Wait(bridge, () => Modal(app, WorkbenchPrepareTargetDialog.DialogTitle), 60);
                await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    string screen = string.Join("\n", Rows());
                    Assert.DoesNotContain("SDL window and GPU support", screen);
                    Assert.Contains("Windows console support", screen);
                    Assert.Contains("SDL (window and GPU)", screen);
                    Assert.True(dialog.Capabilities.HasFlag(PreparationCapabilities.Sdl));
                    dialog.Components.value = 0;
                    Assert.Equal(PreparationCapabilities.Proxy | PreparationCapabilities.Sdl, dialog.Capabilities);
                });
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchPrepareTargetDialog.DialogTitle) is null, 60);
            }, showStart: true);
        }
        finally { Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, null); Delete(root); }
    }

    private static Task CaptureAs(Bridge bridge, string state) => Capture(bridge, state);

    // ── helpers ──────────────────────────────────────────────────────────────────────

    internal static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    internal static string Temp() => Path.Combine(Path.GetTempPath(), "uxc2-" + Guid.NewGuid().ToString("N")[..10]);
    internal static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal static IEnumerable<TMenuItem> Items(TMenuItem? first) { for (var i = first; i is not null; i = i.Next) yield return i; }
    internal static bool MenuOpen(WorkbenchApp app) => Children(app).Any(v => v is TMenuBox) || app.current is TMenuView or TMenuBox;
    internal static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    internal static int Modals(WorkbenchApp app, string title) => Children(app.DeskTop!).Count(v => v is TDialog d && d.title == title);
    internal static string[] Rows()
    {
        var s = ScreenSnapshot.CopyRoot();
        return Enumerable.Range(0, s.Height).Select(y => new string(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character).ToArray())).ToArray();
    }
    internal static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    internal static IEnumerable<TView> Descendants(TGroup group)
    {
        foreach (var child in Children(group)) { yield return child; if (child is TGroup g) foreach (var d in Descendants(g)) yield return d; }
    }
    // Logical captures: the NullDriver cell screen of the real WorkbenchApp (diagnostic bitmap font; never a desktop screenshot).
    internal static async Task Capture(Bridge bridge, string state)
    {
        await Settle(bridge);
        if (Environment.GetEnvironmentVariable("TSWB_PROJECT_INFORMATION_CAPTURE") is not { Length: > 0 } root) return;
        string phase = Environment.GetEnvironmentVariable("TSWC1_PHASE") is { Length: > 0 } p ? p : "before";
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string name = $"{phase}-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.tvs1"), cells);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.png"), png);
            File.WriteAllLines(Path.Combine(root, name + ".logical-nulldriver.txt"),
                Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character).ToArray()).TrimEnd()));
            File.WriteAllText(Path.Combine(root, name + ".logical-nulldriver.json"), JsonSerializer.Serialize(new
            {
                name, phase, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical-nulldriver", driver = "NullDriver",
                test = nameof(ProjectInformationUxTests), renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    internal static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body, bool showStart = false, string? target = null)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        target ??= Target;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            using var app = new WorkbenchApp(showStart ? "" : target, output, new(showStart ? "" : target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") }, showStart: showStart);
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        if (app.Project is { Busy: true }) Command(app.Project, WorkbenchDialog.CancelRun);
                        app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                    }).WaitAsync(TimeSpan.FromSeconds(20));
                }
            });
            AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(240)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }
    internal static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge, int count) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= count ? p : null, 60);
    internal static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    internal static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    internal static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    internal static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(60); await bridge.Invoke(() => { }); }
    internal static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    internal static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    internal sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
