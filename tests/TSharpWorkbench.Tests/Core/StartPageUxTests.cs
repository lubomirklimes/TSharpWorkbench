using System.Reflection;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Start Page identity, Workspace without the top info block, Project > Information..., the contextual
// F4 Properties key, the ordered Workspace status line and the About attribution.
// The real WorkbenchApp runs on NullDriver; keys and commands go through the shared event queue as a user's would.
// Optional logical captures (Captures): TSWB_START_PAGE_CAPTURE=<directory>, TSWUXC1_PHASE=before|after.
[Collection("NonParallel")]
public sealed class StartPageUxTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;

    // ── logical captures (before / after) ────────────────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task Captures(int width, int height)
    {
        if (Environment.GetEnvironmentVariable("TSWB_START_PAGE_CAPTURE") is not { Length: > 0 }) return;
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            string named = SaveProject(Path.Combine(root, "projects"), "MyTest2.twproj", new WorkbenchProject { Name = "MyTest1", Target = Target, OutputRoot = Path.Combine(root, "runs-a") });
            string other = SaveProject(Path.Combine(root, "projects", "a much longer folder name for the recent list"), "Sample.twproj",
                new WorkbenchProject { Name = "Sample target project", Target = Target, OutputRoot = Path.Combine(root, "runs-b") });
            WorkbenchPreferences.Update(Path.Combine(root, "settings.json"), p => p.WithRecent(named).WithRecent(other));
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await Capture(bridge, "start-page");
                Send(WorkbenchApp.About); await Wait(bridge, () => Modal(app, "About TSharpWorkbench"));
                await Capture(bridge, "about"); Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "About TSharpWorkbench") is null);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(other), other));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                await Settle(bridge);
                await Capture(bridge, "workspace");
                Press(Keys.kbAltP); await Until(bridge, () => MenuOpen(app)); await Capture(bridge, "project-menu"); Press(Keys.kbEsc); await Until(bridge, () => !MenuOpen(app));
                if (typeof(WorkbenchDialog).GetField("ProjectInfo", BindingFlags.NonPublic | BindingFlags.Static) is { } field)
                {
                    Send((ushort)field.GetValue(null)!); await Wait(bridge, () => Modal(app, "Project Information"));
                    await Capture(bridge, "project-info"); Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Information") is null);
                }
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // ── A. Start Page ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task StartPageShowsIdentityVersionAndRecentsAndOpensByKeyboard(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            // The display name comes from the project's "name" metadata, not from the file name (MyTest1 in MyTest2.twproj).
            string named = SaveProject(Path.Combine(root, "projects"), "MyTest2.twproj", new WorkbenchProject { Name = "MyTest1", Target = Target, OutputRoot = Path.Combine(root, "runs") });
            WorkbenchPreferences.Update(Path.Combine(root, "settings.json"), p => p.WithRecent(named));
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                var start = await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => app.Notifications.Clear()); await Settle(bridge);
                string version = typeof(WorkbenchApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
                string framework = typeof(TView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
                await bridge.Invoke(() =>
                {
                    Assert.Equal("Start Page", start.title);
                    Assert.Equal(["TSharpWorkbench", "UI Testing & Automation for TSharpVision", "Version " + version], WorkbenchStartWindow.Identity);
                    string screen = string.Join("\n", Rows());
                    foreach (string line in WorkbenchStartWindow.Identity) Assert.Contains(line, screen);
                    // The version is the Workbench's own assembly version; the framework version is never shown here.
                    if (framework != version) Assert.DoesNotContain("Version " + framework, screen);
                    foreach (string removed in new[] { "No project is open", "A project names", "Enter opens", "Esc closes", "Lubomir", "Copyright" })
                        Assert.DoesNotContain(removed, screen);
                    // The Enter / Esc guidance moved to the status line, F-keys first.
                    string status = Rows()[^1];
                    Assert.StartsWith(" F1 Help  F10 Menu  Ctrl+N New project  Ctrl+O Open project  Enter Open  Esc Close", status);
                    Assert.Equal(WorkbenchApp.NoProjectContext, app.GetHelpCtx());
                    // Recent list: identity, selection and focus are unchanged; the window fits and wastes no rows.
                    Assert.Equal("MyTest1", Assert.Single(start.Entries).Name);
                    Assert.Same(start.Recent, start.current);
                    Assert.Contains(Rows(), r => r.Contains("MyTest1") && r.Contains("MyTest2.twproj"));
                    Assert.True(start.origin.y >= 0 && start.origin.y + start.size.y <= app.DeskTop!.size.y && start.origin.x + start.size.x <= width);
                    var buttons = Children(start).OfType<TButton>().ToArray();
                    Assert.Equal(["~O~pen", "Open pro~j~ect...", "~N~ew project..."], buttons.OrderBy(b => b.origin.x).Select(b => b.Title).ToArray());
                    Assert.All(buttons, b => Assert.Equal(start.size.y - 3, b.origin.y));
                    Assert.Equal(4, start.Recent.size.y); // one entry: the minimum list height
                });
                // Esc closes the Start Page; the empty desktop keeps its own keys without Enter / Esc.
                Press(Keys.kbEsc); await Until(bridge, () => app.Start is null);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(WorkbenchApp.EmptyDesktopContext, app.GetHelpCtx());
                    string status = Rows()[^1];
                    Assert.Contains("Ctrl+O Open project", status); Assert.DoesNotContain("Enter Open", status); Assert.DoesNotContain("Esc Close", status);
                });
                await bridge.Invoke(() => app.ShowStart()); await Wait(bridge, () => app.Start);
                Press(Keys.kbEnter);
                var main = await Loaded(app, bridge, 16);
                await bridge.Invoke(() => { Assert.Null(app.Start); Assert.Equal(named, main.ProjectPath); Assert.StartsWith("MyTest1", main.title); });
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // ── B/C. Workspace without the header block and buttons; keys and status line ─────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task WorkspaceHasNoHeaderOrButtonsAndKeepsEveryRouteByKeyboard(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                await bridge.Invoke(() =>
                {
                    var views = Children(main).Where(v => v is not TFrame).ToArray();
                    Assert.Empty(views.OfType<TButton>());
                    // The panes start on the first interior row and end above the caption, which is the last interior row.
                    Assert.Equal(2, main.Tree.origin.y); Assert.Equal(4, main.TestList.origin.y);   // Tool row, empty row, headers
                    Assert.Equal(main.size.y - 2, main.DetailPane.origin.y + main.DetailPane.size.y);
                    Assert.Equal(main.size.y - 2, main.Tree.origin.y + main.Tree.size.y);
                    string[] rows = Rows();
                    foreach (string removed in new[] { "Target    ", "Output    ", "Run setup ", "Last run   " }) Assert.DoesNotContain(rows, r => r.Contains(removed));
                    Assert.Contains("Groups", rows[2]); Assert.Contains("Marked 0 of 16", rows[height - 3]);
                    // The summary still exists (Project > Information...).
                    Assert.Contains("Target    TSharpWorkbench.SampleTarget", main.HeaderText);
                    Assert.Same(main.TestList, main.current);
                });
                // Status line order, readable at every grid, also while a notice is shown.
                await bridge.Invoke(() => app.Notifications.Warning("A notice that is long enough to need the room of the hint area on the status line."));
                await Settle(bridge);
                // F3 Info joins in ascending order; beside a notice the keys through F10 Menu stay.
                await bridge.Invoke(() => Assert.StartsWith(" F1 Help  F2 Refresh  F3 Info  F4 Properties  F5 Run  F6 Results  F10 Menu", Rows()[^1]));
                await bridge.Invoke(() => app.Notifications.Clear()); await Settle(bridge);
                if (width >= 120) await bridge.Invoke(() => Assert.StartsWith(" F1 Help  F2 Refresh  F3 Info  F4 Properties  F5 Run  F6 Results  F10 Menu  Space Mark  Ins Mark next", Rows()[^1]));

                // F4 and the old Alt+O open Project Properties; Esc returns to the Workspace with focus where it was.
                foreach (ushort key in new[] { Keys.kbF4, Keys.kbAltO })
                {
                    Press(key); var props = await Wait(bridge, () => Modal(app, "Project Properties"));
                    Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Properties") is null);
                    await bridge.Invoke(() => { Assert.Same(main, app.DeskTop!.current); Assert.Same(main.TestList, main.current); });
                }
                // F5 and the old Alt+U open Run Configuration and never start a run; Esc cancels it.
                foreach (ushort key in new[] { Keys.kbF5, Keys.kbAltU })
                {
                    Press(key);
                    await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
                    await bridge.Invoke(() => Assert.False(main.Busy));
                    // F4 inside a modal dialog does not reach the application: the dialog stays, nothing else opens.
                    Press(Keys.kbF4); await Settle(bridge);
                    await bridge.Invoke(() => Assert.True(app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle));
                    Press(Keys.kbEsc);
                    await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                    await bridge.Invoke(() => Assert.False(main.Busy));
                }
                // F6 and the old Alt+L: Results (no run yet: the usual explanation).
                foreach (ushort key in new[] { Keys.kbF6, Keys.kbAltL })
                {
                    int before = 0; await bridge.Invoke(() => before = app.Notifications.History.Count);
                    Press(key); await Settle(bridge);
                    await bridge.Invoke(() => Assert.Contains(app.Notifications.History.Skip(before), n => n.Message.StartsWith("No results yet")));
                }
                // Tab cycles the four panes only (removed the Native-runnable only filter).
                await bridge.Invoke(() =>
                {
                    main.Tree.Select(); var seen = new List<TView>();
                    for (int i = 0; i < 4; i++) { main.SelectNext(false); seen.Add(main.current!); }
                    Assert.Same(main.Tree, seen[^1]); Assert.Equal(4, seen.Distinct().Count());
                });
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task F4IsBoundOnlyByTheWorkspaceStatusLineAndTheApplicationAndNeedsAProject()
    {
        using (var driver = new DriverScope())
        using (var plain = new TApplication())
        using (var status = WorkbenchApp.CreateStatusLine(plain.GetExtent()))
        {
            var bound = new List<ushort>();
            for (var def = status.Defs; def is not null; def = def.Next)
                for (var item = def.Items; item is not null; item = item.Next)
                    if (item.KeyCode == Keys.kbF4) { bound.Add(def.Min); Assert.Equal(WorkbenchDialog.Advanced, item.Command); }
            Assert.Equal([WorkbenchDialog.Context], bound);
        }
        var menu = typeof(WorkbenchApp).GetMethod("KeyCommand", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = Keys.kbF4; ev.keyDown.charScan = new CharScanType(Keys.kbF4);
        Assert.Equal(WorkbenchDialog.Advanced, (ushort)menu.Invoke(null, [ev])!);
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                int before = 0; await bridge.Invoke(() => before = app.Notifications.History.Count);
                Press(Keys.kbF4); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.StartsWith("No project is open", app.Notifications.History.Skip(before).Single().Message);
                    Assert.Null(Modal(app, "Project Properties"));
                    // Project menu: Information... before Properties..., which prints F4.
                    var project = Items(app.MenuBar!.Menu!.Items).First().SubMenu.Items;
                    var items = Items(project).Where(i => i.Name is not null).ToArray();
                    int info = Array.FindIndex(items, i => i.Command == WorkbenchDialog.ProjectInfo);
                    Assert.Equal("~I~nformation...", items[info].Name);
                    Assert.Equal(WorkbenchDialog.Advanced, items[info + 1].Command); Assert.Equal("F4", items[info + 1].Param);
                    Assert.Equal(items.Length, items.Select(i => char.ToUpperInvariant(i.Name![i.Name.IndexOf('~') + 1])).Distinct().Count());
                    Assert.False(TView.CommandEnabled(WorkbenchDialog.ProjectInfo));
                });
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // ── B. Project Information ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public async Task ProjectInformationShowsLiveValuesAndRoutesToProperties(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            string deep = Path.Combine(root, "a rather long folder name", "and another long folder name to wrap", "project");
            string project = SaveProject(deep, "info.twproj", new WorkbenchProject { Name = "Info sample", Target = Target, OutputRoot = Path.Combine(deep, "runs") });
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Loaded(app, bridge, 16);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                Send(WorkbenchDialog.ProjectInfo);
                var dialog = (WorkbenchProjectInfoDialog)await Wait(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                string Text() => Flat(dialog);
                string Joined() => NoSpace(dialog);
                await bridge.Invoke(() =>
                {
                    Assert.True(dialog.origin.x >= 0 && dialog.origin.y >= 0 && dialog.origin.x + dialog.size.x <= width && dialog.origin.y + dialog.size.y <= app.DeskTop!.size.y);
                    Assert.Contains("Project Info sample", Text());
                    // Long paths wrap under their label and stay complete.
                    Assert.Contains(Squeeze(project), Joined()); Assert.Contains(Squeeze(Target), Joined()); Assert.Contains(Squeeze(Path.Combine(deep, "runs")), Joined());
                    Assert.All(dialog.Lines, l => Assert.True(l.Length <= dialog.List.size.x, l));
                    Assert.Contains("Target state Ready", Text());
                    Assert.Contains("Scenarios 16 loaded", Text());
                    Assert.Contains("Scenario files none attached", Text());
                    Assert.Contains("Marked 0 of 16", Text());
                    Assert.Contains("Last run none in this session", Text());
                    Assert.Contains("Run setup Functional | headless target (NullDriver)", Text());
                    Assert.Same(dialog.List, dialog.current);
                    // Live update: a mark made meanwhile (e.g. by a running workflow) shows without reopening.
                    main.TestList.Toggle(main.TestList.Current!.Id);
                });
                await Until(bridge, () => Text().Contains("Marked 1 of 16") && Text().Contains("(unsaved changes"));
                // Read-only with Close only; Properties (F4) and Target diagnostics are on the Project menu.
                await bridge.Invoke(() => Assert.Single(Descendants(dialog).OfType<TButton>(), b => b.Title == "Close"));
                Press(Keys.kbAltP); Press(Keys.kbAltD); await Settle(bridge);
                await bridge.Invoke(() => Assert.Same(dialog, app.DeskTop!.TopView()));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                await bridge.Invoke(() => { Assert.Null(main.ProjectInfoDialog); Assert.Same(main, app.DeskTop!.current); });
                Press(Keys.kbF4);
                await Wait(bridge, () => Modal(app, "Project Properties"));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ProjectInformationHandlesAnUntitledProjectAMissingTargetAndAFinishedRun()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                // A finished run: Last run names the time, verdict, counts and run folder.
                await bridge.Invoke(() => { main.TestList.FocusItemNum(0); main.TestList.Toggle(main.TestList.Current!.Id); });
                Send(WorkbenchDialog.Run);
                await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 120);
                Send(WorkbenchDialog.ProjectInfo);
                var dialog = (WorkbenchProjectInfoDialog)await Wait(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                await bridge.Invoke(() =>
                {
                    string text = Flat(dialog);
                    Assert.Contains("Project file not saved yet", text);
                    Assert.Matches(@"Last run \d{4}-\d\d-\d\d \d\d:\d\d PASSED \(1 passed / 0 failed / 0 blocked\) run-", text);
                });
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                await bridge.Invoke(() => main.TestList.ClearMarks());
                // A target that disappeared: the state says so instead of diagnosing.
                string gone = Path.Combine(root, "gone", "Missing.exe");
                string project = SaveProject(root, "missing.twproj", new WorkbenchProject { Name = "Missing target", Target = gone, OutputRoot = Path.Combine(root, "runs-m") });
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var missing = await Wait(bridge, () => app.Project is { ProjectPath: not null } p && p != main ? p : null);
                Send(WorkbenchDialog.ProjectInfo);
                dialog = (WorkbenchProjectInfoDialog)await Wait(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                await bridge.Invoke(() =>
                {
                    string text = Flat(dialog);
                    Assert.Contains("(missing)", text);
                    Assert.Contains("Target state not checked: the target executable is missing", text);
                    Assert.Contains("Last run none in this session", text);
                });
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ANotReadyTargetIsReportedOnceWithItsExactProblemAndTheRoutes()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            // The sample target without its framework assembly: Framework missing (FW001), a blocking problem.
            string copy = Path.Combine(root, "broken");
            Directory.CreateDirectory(copy);
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(Target)!))
                if (!Path.GetFileName(file).Equals("TSharpVision.dll", StringComparison.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            string exe = Path.Combine(copy, Path.GetFileName(Target));
            string project = SaveProject(root, "broken.twproj", new WorkbenchProject { Name = "Broken", Target = exe, OutputRoot = Path.Combine(root, "runs-b") });
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Loaded(app, bridge, 16);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { ProjectPath: not null, Busy: false } p && p.ProjectPath == project ? p : null, 60);
                await Until(bridge, () => app.Notifications.History.Any(n => n.Message.StartsWith("Target not ready")));
                await bridge.Invoke(() =>
                {
                    var notice = app.Notifications.History.Single(n => n.Message.StartsWith("Target not ready"));
                    Assert.Equal(NoticeLevel.Warning, notice.Level);
                    Assert.Equal("Target not ready: Framework missing. Project > Information... (F3) shows the project state; Project > Target diagnostics... explains each cause.", notice.Message);
                    Assert.Contains("Framework missing", main.HeaderText);
                });
                // Further updates (refresh, marks, opening the summary) do not repeat it: no permanent clutter.
                Send(WorkbenchDialog.ProjectInfo);
                var dialog = (WorkbenchProjectInfoDialog)await Wait(bridge, () => Modal(app, WorkbenchProjectInfoDialog.DialogTitle));
                await bridge.Invoke(() => Assert.Contains("Target state Framework missing", Flat(dialog)));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                await bridge.Invoke(() => { main.RefreshHeader(); main.RefreshHeader(); });
                await Settle(bridge);
                await bridge.Invoke(() => Assert.Single(app.Notifications.History, n => n.Message.StartsWith("Target not ready")));
            });
        }
        finally { Delete(root); }
    }

    // ── D. About ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AboutNamesProductVersionAuthorCopyrightAndTheRepositoryLicense()
    {
        string license = File.ReadAllText(Path.Combine(RepositoryRoot(), "LICENSE"));
        Assert.StartsWith("MIT License", license); Assert.Contains("Lubomir Klimes", license);
        string version = typeof(WorkbenchApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        string[] about = WorkbenchHelp.About();
        Assert.Equal(["TSharpWorkbench", "UI Testing & Automation for TSharpVision", "Version " + version], about[..3]);
        Assert.StartsWith("TSharpVision ", about[3]);
        Assert.Contains("Created by Lubomir Klimes", about); Assert.Contains("Copyright © 2021–2026 Lubomir Klimes", about);
        // The years are the LICENSE file's.
        Assert.Contains("Copyright (c) " + WorkbenchHelp.CopyrightYears.Replace('–', '-') + " Lubomir Klimes", license);
        Assert.Contains("MIT License (see the LICENSE file)", about);
        Assert.DoesNotContain(about, l => l.Contains("github", StringComparison.OrdinalIgnoreCase)); // no canonical URL in the repository
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                Send(WorkbenchApp.About); await Wait(bridge, () => Modal(app, "About TSharpWorkbench"));
                await bridge.Invoke(() => { string screen = string.Join("\n", Rows()); foreach (string line in about.Where(l => l.Length > 0)) Assert.Contains(line, screen); });
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "About TSharpWorkbench") is null);
                // The keyboard reference names F4 and the Start Page.
                string keys = string.Join("\n", WorkbenchHelp.Keyboard());
                Assert.Contains("F4         Project Properties", keys); Assert.Contains("Start Page", keys); Assert.Contains("Project > Information...", keys);
                Assert.DoesNotContain("Details, buttons", keys);
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    // The summary as one line (wrapped values joined, runs of spaces collapsed); without any spaces for path checks.
    private static string Flat(WorkbenchProjectInfoDialog dialog) => System.Text.RegularExpressions.Regex.Replace(string.Join(" ", dialog.Lines), @"\s+", " ");
    private static string NoSpace(WorkbenchProjectInfoDialog dialog) => Squeeze(string.Concat(dialog.Lines));
    private static string Squeeze(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");

    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "uxc1-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    private static IEnumerable<TMenuItem> Items(TMenuItem? first) { for (var i = first; i is not null; i = i.Next) yield return i; }
    private static bool MenuOpen(WorkbenchApp app) => Children(app).Any(v => v is TMenuBox) || app.current is TMenuView or TMenuBox;
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string[] Rows()
    {
        var s = ScreenSnapshot.CopyRoot();
        return Enumerable.Range(0, s.Height).Select(y => new string(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character).ToArray())).ToArray();
    }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    private static IEnumerable<TView> Descendants(TGroup group)
    {
        foreach (var child in Children(group)) { yield return child; if (child is TGroup g) foreach (var d in Descendants(g)) yield return d; }
    }
    // Logical captures: the NullDriver cell screen of the real WorkbenchApp (diagnostic bitmap font; never a desktop screenshot).
    private static async Task Capture(Bridge bridge, string state)
    {
        await Settle(bridge);
        if (Environment.GetEnvironmentVariable("TSWB_START_PAGE_CAPTURE") is not { Length: > 0 } root) return;
        string phase = Environment.GetEnvironmentVariable("TSWUXC1_PHASE") is { Length: > 0 } p ? p : "before";
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string name = $"{phase}-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.png"), png);
            File.WriteAllLines(Path.Combine(root, name + ".logical-nulldriver.txt"),
                Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character).ToArray()).TrimEnd()));
            File.WriteAllText(Path.Combine(root, name + ".logical-nulldriver.json"), JsonSerializer.Serialize(new
            {
                name, phase, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical-nulldriver", driver = "NullDriver",
                test = nameof(StartPageUxTests), renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body, bool showStart = false)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            using var app = new WorkbenchApp(showStart ? "" : Target, output, new(showStart ? "" : Target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") }, showStart: showStart);
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
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge, int count) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= count ? p : null, 60);
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    private static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(60); await bridge.Invoke(() => { }); }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
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
