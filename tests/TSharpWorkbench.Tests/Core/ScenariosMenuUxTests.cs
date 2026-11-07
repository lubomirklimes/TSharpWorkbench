using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

// Menu-bar mnemonics (Alt+S Scenarios), the Scenarios menu shape and routing, Remove that only
// detaches, the removed "Native-runnable only" filter, environment-qualified baselines and Skipped results.
// The real WorkbenchApp runs on NullDriver; keys and commands go through the shared event queue as a user's would.
// Optional logical captures (Captures): TSWB_SCENARIOS_MENU_CAPTURE=<directory>, TSWSC1_PHASE=before|after.
[Collection("NonParallel")]
public sealed class ScenariosMenuUxTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;

    // ── logical captures (before / after) ────────────────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task Captures(int width, int height)
    {
        if (Environment.GetEnvironmentVariable("TSWB_SCENARIOS_MENU_CAPTURE") is not { Length: > 0 }) return;
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        try
        {
            Directory.CreateDirectory(docs);
            string a = Write(docs, "alpha.scenario.json", ProxyDocument("ext/alpha", "Alpha"));
            string b = Write(docs, "beta.scenario.json", ProxyDocument("ext/beta", "Beta"));
            string sample = SaveProject(Path.Combine(root, "p"), "Sample.twproj", new WorkbenchProject { Name = "Sample target project", Target = Target, OutputRoot = Path.Combine(root, "runs-s") });
            string proxy = SaveProject(Path.Combine(root, "p"), "Proxy.twproj", new WorkbenchProject { Name = "Proxy documents", Target = Target, OutputRoot = Path.Combine(root, "runs-p"), ScenarioFiles = [a, b] });
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(sample), sample));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                await bridge.Invoke(() => app.Notifications.Clear());
                await Shot(bridge, "workspace");
                foreach (var (menu, state) in new[] { ("Scenarios", "scenarios-menu"), ("Test", "test-menu") })
                {
                    ushort key = 0; await bridge.Invoke(() => key = TopMenu(app, menu).KeyCode);
                    Press(key); await Until(bridge, () => MenuOpen(app)); await Shot(bridge, state);
                    Press(Keys.kbEsc); await Until(bridge, () => !MenuOpen(app));
                }
                Send(WorkbenchDialog.Results); await Settle(bridge); await Task.Delay(300);
                if (await Probe(bridge, () => app.DeskTop!.current is WorkbenchResultsDialog)) { await Shot(bridge, "results"); Press(Keys.kbEsc); await Settle(bridge); }

                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(proxy), proxy));
                main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.ProjectPath == proxy && p.TestList.Strings.Count >= 2 ? p : null, 60);
                await bridge.Invoke(() => app.Notifications.Clear());
                Send(WorkbenchDialog.ManageScenarios); await Wait(bridge, () => main.ScenariosWindow);
                await Shot(bridge, "scenarios-window");
                Send(WorkbenchDialog.RemoveScenario); await Wait(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle));
                await Shot(bridge, "remove-confirmation");
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle) is null);
                Send(WorkbenchDialog.OpenScenario); await Wait(bridge, () => main.ScenarioEditors.FirstOrDefault());
                await Shot(bridge, "scenario-editor");
                await bridge.Invoke(() => main.CloseScenarioEditors());
            });
        }
        finally { Delete(root); }
    }

    // ── Phase A: menu bar, Scenarios menu, Remove, filter removal ─────────────────────────

    private static readonly (string Name, ushort Key, char Letter)[] Bar =
    [
        ("Project", Keys.kbAltP, 'P'), ("Tests", Keys.kbAltT, 'T'), ("Runs", Keys.kbAltR, 'R'),
        ("Baselines", Keys.kbAltB, 'B'), ("Settings", Keys.kbAltG, 'G'), ("Window", Keys.kbAltW, 'W'), ("Help", Keys.kbAltH, 'H'),
    ];
    private static char Mnemonic(string name) => char.ToUpperInvariant(name[name.IndexOf('~') + 1]);

    [Fact]
    public void MainMenuMatchesTheApprovedStructureAndRoutes()
    {
        using var driver = new DriverScope(120, 36);
        string root = Temp();
        try
        {
            using var app = new WorkbenchApp(Target, Path.Combine(root, "runs"), new(Target, Path.Combine(root, "runs"), null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
            var top = Items(app.MenuBar!.Menu!.Items).ToArray();
            Assert.Equal(Bar.Select(b => b.Name), top.Select(i => i.Name!.Replace("~", "")));
            Assert.Equal(Bar.Select(b => b.Letter), top.Select(i => Mnemonic(i.Name!)));
            Assert.Equal(Bar.Select(b => b.Key), top.Select(i => i.KeyCode));
            string[][] expected =
            [
                ["New...|Ctrl+N", "Open...|Ctrl+O", "Save|Ctrl+S", "Save As...", "Close", "-", "Properties...|F3", "-", "Exit|Alt+X"],
                ["Test Explorer...", "-", "Scenario Manager..."],
                ["Run Explorer...", "-", "New Run...|F5", "Cancel Run", "-", "Results...|F6", "History...", "-", "Open Output Folder"],
                ["Baseline Explorer...", "-", "Review Candidates...", "Compare..."],
                ["Project...|F4", "Environments...", "-", "Target Diagnostics...", "Prepare Target Copy...", "Fix Original Target..."],
                ["Test Explorer", "Scenario Manager|Alt+2", "Run Explorer|Alt+3", "Results|Alt+4",
                    "Baseline Explorer|Alt+5", "Messages|Alt+6", "-", "Next Window", "Previous Window", "Size/Move|Ctrl+F5", "Zoom", "Close Window|Alt+F3"],
            ];
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], Items(top[i].SubMenu!.Items).Select(item =>
                    item.Name is null ? "-" : item.Name.Replace("~", "") + (item.Param is { } p ? "|" + p : "")));

            var help = Items(top[^1].SubMenu!.Items).Where(item => item.Name is not null).ToArray();
            Assert.Equal("Index", help[0].Name!.Replace("~", ""));
            Assert.Equal("About TSharpWorkbench", help[^1].Name!.Replace("~", ""));
            Assert.Equal(WorkbenchDialog.ProjectInfo, Commands(0).ElementAt(5));
            Assert.Equal([WorkbenchTestExplorer.Command, WorkbenchDialog.ManageScenarios], Commands(1));
            Assert.Equal([WorkbenchDialog.ProgressWindow, WorkbenchDialog.Run, WorkbenchDialog.CancelRun, WorkbenchDialog.Results, WorkbenchDialog.RunHistory, WorkbenchDialog.OpenOutput], Commands(2));
            Assert.Equal([WorkbenchDialog.BaselineBrowse, WorkbenchDialog.ReviewCandidates, WorkbenchDialog.CompareResult], Commands(3));
            Assert.Equal([WorkbenchDialog.Advanced, WorkbenchApp.Environments, WorkbenchDialog.DiagnoseTarget,
                WorkbenchDialog.PrepareTargetCommand, WorkbenchDialog.FixIssuesCommand], Commands(4));
            Assert.Equal([WorkbenchTestExplorer.Command, WorkbenchDialog.ManageScenarios,
                WorkbenchDialog.ProgressWindow, WorkbenchDialog.Results, WorkbenchApp.ComparisonWindow, WorkbenchApp.MessagesWindow,
                Views.cmNext, Views.cmPrev, Views.cmResize, Views.cmZoom, Views.cmClose], Commands(5));
            Assert.Equal([Keys.kbCtrlN, Keys.kbCtrlO, Keys.kbCtrlS, Keys.kbNoKey, Keys.kbNoKey, Keys.kbNoKey, Keys.kbAltX], KeyCodes(0));
            Assert.Equal([Keys.kbNoKey, Keys.kbF5, Keys.kbNoKey, Keys.kbF6, Keys.kbNoKey, Keys.kbNoKey], KeyCodes(2));
            app.Idle();
            Assert.False(TView.CommandEnabled(WorkbenchDialog.CancelRun));
            Assert.False(TView.CommandEnabled(WorkbenchDialog.Advanced));
            Assert.True(TView.CommandEnabled(WorkbenchApp.OpenProject));

            IEnumerable<ushort> Commands(int index) => Items(top[index].SubMenu!.Items).Where(item => item.Name is not null).Select(item => item.Command);
            IEnumerable<ushort> KeyCodes(int index) => Items(top[index].SubMenu!.Items).Where(item => item.Name is not null).Select(item => item.KeyCode);
            // Every submenu's letters are unique; no menu item opens the external editor; the Test menu has no native filter.
            foreach (var menu in top) { var letters = Items(menu.SubMenu!.Items).Where(i => i.Name is not null).Select(i => Mnemonic(i.Name!)).ToArray(); Assert.Equal(letters.Length, letters.Distinct().Count()); }
            var all = top.SelectMany(t => Items(t.SubMenu!.Items)).Where(i => i.Name is not null).ToArray();
            Assert.DoesNotContain(all, i => i.Command == WorkbenchDialog.OpenScenarioExternal);
            Assert.DoesNotContain(all, i => i.Name!.Contains("native", StringComparison.OrdinalIgnoreCase) && i.Name.Contains("only"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task EveryMenuBarLetterOpensItsMenuFromTheWorkspaceScenariosWindowAndEditor()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            string doc = Write(Path.Combine(root, "docs"), "alpha.scenario.json", ProxyDocument("ext/alpha", "Alpha"));
            string project = SaveProject(Path.Combine(root, "p"), "keys.twproj", new WorkbenchProject { Name = "Keys", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [doc] });
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 1 ? p : null, 60);
                async Task Check(string where)
                {
                    foreach (var (name, key, _) in Bar)
                    {
                        Press(key);
                        var box = await Wait(bridge, () => Children(app).OfType<TMenuBox>().FirstOrDefault());
                        await bridge.Invoke(() =>
                        {
                            var expected = Items(TopMenu(app, name).SubMenu!.Items).First().Name;
                            Assert.True(box.Menu!.Items!.Name == expected, $"{where}: Alt+{name[0]} opened the menu starting with '{box.Menu.Items.Name}', not {name}");
                        });
                        Press(Keys.kbEsc); await Until(bridge, () => !MenuOpen(app));
                    }
                }
                await Check("Workspace");
                Send(WorkbenchDialog.ManageScenarios); var window = await Wait(bridge, () => main.ScenariosWindow);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, window));
                await Check("Scenarios window");
                Press(Keys.kbEnter); await Wait(bridge, () => main.ScenarioEditors.FirstOrDefault());
                await Check("Scenario editor");
                await bridge.Invoke(() => main.CloseScenarioEditors());
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task RemoveScenarioOnlyDetachesTheFileAndEditOpensTheInternalEditor()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        try
        {
            string a = Write(docs, "alpha.scenario.json", ProxyDocument("ext/alpha", "Alpha"));
            string b = Write(docs, "beta.scenario.json", ProxyDocument("ext/beta", "Beta"));
            byte[] original = File.ReadAllBytes(a);
            string project = SaveProject(Path.Combine(root, "p"), "remove.twproj", new WorkbenchProject { Name = "Remove", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [a, b] });
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 2 ? p : null, 60);
                // The Workspace's focused external scenario is the selection: Edit opens the internal editor for its file.
                await bridge.Invoke(() => { main.TestList.Select(); main.TestList.FocusItemNum(Array.FindIndex(main.TestList.Strings.ToArray(), s => s.Contains("Alpha"))); });
                Press(Keys.kbAltS); await Until(bridge, () => MenuOpen(app)); Press('e');
                var editor = await Wait(bridge, () => main.ScenarioEditors.FirstOrDefault());
                await bridge.Invoke(() => Assert.Equal(a, editor.Document.Path));
                await bridge.Invoke(() => main.CloseScenarioEditors());
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                // Remove: the confirmation says the file is not deleted; Cancel (the default) changes nothing.
                await bridge.Invoke(() => { main.TestList.Select(); main.TestList.FocusItemNum(Array.FindIndex(main.TestList.Strings.ToArray(), s => s.Contains("Alpha"))); });
                Press(Keys.kbAltS); await Until(bridge, () => MenuOpen(app)); Press('r');
                await Wait(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle));
                await bridge.Invoke(() =>
                {
                    string text = string.Join("\n", Rows());
                    Assert.Contains("only detaches the file from the project", text);
                    Assert.Contains("The JSON file is NOT deleted", text);
                });
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle) is null);
                await bridge.Invoke(() => Assert.Equal(2, main.ScenarioFileList.Length));
                // Confirming detaches the reference only: the file is byte-identical on disk and can be attached again.
                Send(WorkbenchDialog.RemoveScenario); await Wait(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle));
                Press(Keys.kbAltR); await Until(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle) is null);
                await bridge.Invoke(() => Assert.Equal([b], main.ScenarioFileList));
                Assert.True(File.Exists(a)); Assert.Equal(original, File.ReadAllBytes(a));
                await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Message.Contains("the file was not deleted")));
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task EditAndRemoveExplainAMissingSelection()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                // A compiled catalog without attached files: both commands stay enabled and say what to do.
                var main = await Loaded(app, bridge, 16);
                await bridge.Invoke(() => { Assert.True(TView.CommandEnabled(WorkbenchDialog.OpenScenario)); Assert.True(TView.CommandEnabled(WorkbenchDialog.RemoveScenario)); app.Notifications.Clear(); });
                Send(WorkbenchDialog.OpenScenario); Send(WorkbenchDialog.RemoveScenario); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Empty(main.ScenarioEditors); Assert.Null(Modal(app, WorkbenchDialog.RemoveTitle));
                    Assert.Equal(2, app.Notifications.History.Count(n => n.Level == NoticeLevel.Warning && n.Message.StartsWith("This project has no attached scenario files")));
                });
            });
            string doc = Write(Path.Combine(root, "docs"), "alpha.scenario.json", ProxyDocument("ext/alpha", "Alpha"));
            // Attached files, but a compiled scenario focused in the Workspace: it is named, nothing opens.
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                await bridge.Invoke(() => { main.TestList.Select(); main.TestList.FocusItemNum(0); app.Notifications.Clear(); });
                await bridge.Invoke(() => main.ApplyProject(main.SessionProject() with { ScenarioFiles = [doc] }, Path.Combine(root, "x.twproj")));
                await bridge.Invoke(() => { main.TestList.Select(); main.TestList.FocusItemNum(0); app.Notifications.Clear(); });
                Send(WorkbenchDialog.OpenScenario); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Message.Contains("is a compiled scenario of the target")));
            });
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData("NullDriver", "functional")]
    [InlineData("SDLDriver", "rendering")]
    [InlineData("Win32ConsoleDriver", "rendering")]
    [InlineData("SDLGpuDriver", "rendering")]
    public async Task EveryScenarioStaysVisibleAndMarkableForEveryDriver(string mode, string testType)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            string project = SaveProject(Path.Combine(root, "p"), "drivers.twproj", new WorkbenchProject { Name = "Drivers", Target = Target, OutputRoot = Path.Combine(root, "runs"), Mode = mode, TestType = testType });
            // Settings written by an older Workbench with the removed filter switched on are ignored.
            WorkbenchPreferences.Update(Path.Combine(root, "settings.json"), p => p.WithViewState(project, new WorkbenchViewState("", null, [], true)));
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.ProjectPath == project && p.TestList.Strings.Count >= 16 ? p : null, 60);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(16, main.TestList.range);
                    Assert.Empty(Descendants(main).OfType<TCheckBoxes>());
                    Assert.DoesNotContain("native-runnable", main.CaptionText, StringComparison.OrdinalIgnoreCase);
                    main.TestList.Select();
                    for (int i = 0; i < main.TestList.range; i++) { main.TestList.FocusItemNum(i); main.TestList.Toggle(main.TestList.Current!.Id); }
                    Assert.Equal(16, main.TestList.MarkedIds.Count);
                });
            });
        }
        finally { Delete(root); }
    }

    // ── Phase B: baseline environment identity and selection ──────────────────────────────
    // Platform identities other than this machine's are SIMULATED: the gate is given an explicit BaselineEnvironment; nothing
    // here ran on Linux or macOS. The store logic is platform-neutral (it never asks the OS), which is what these tests prove.

    private const string EnvScenario = "demo/env", EnvCheckpoint = "desktop";
    private static readonly ScenarioDescriptor EnvDemo = new(EnvScenario, "Env", "demo", "demo", ScenarioGrid.Standard, [new("checkpoint", EnvCheckpoint)]);
    private static readonly TargetIdentity EnvIdentity = new("Demo", "1.0", null, null, null, "NullDriver", 80, 25);
    private static readonly BaselineEnvironment WindowsSdl = new("Windows", "SDLDriver"), LinuxSdl = new("Linux", "SDLDriver"),
        MacSdl = new("macOS", "SDLDriver"), WindowsConsole = new("Windows", "Win32ConsoleDriver"), WindowsNull = new("Windows", "NullDriver"), LinuxNull = new("Linux", "NullDriver");

    private static ScreenSnapshot Screen(string text, int width = 80, int height = 25)
    {
        var cells = new TScreenChar[width * height];
        for (int i = 0; i < cells.Length; i++) cells[i] = new(' ', 0x17);
        for (int x = 0; x < text.Length; x++) cells[x] = new(text[x], 0x1f);
        return new(width, height, cells);
    }
    private static VisualBaselineSet EnvStore(string root, params string[] grids)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "visual-baselines.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = VisualBaselineSet.ManifestFormat, normalizations = Array.Empty<VisualNormalizationRule>(),
            baselines = new[] { new VisualBaselineEntry(EnvScenario, EnvCheckpoint, grids.Length == 0 ? ["80x25"] : grids, "environment isolation") },
        }, ScenarioRunner.Json));
        return VisualBaselineSet.Load(path);
    }
    private static int runs;
    private static async Task<(VisualCheckpointResult Result, string Run)> Gate(VisualBaselineSet set, string root, BaselineEnvironment? env, ScreenSnapshot screen, string grid = "80x25")
    {
        string run = Path.Combine(root, "run" + Interlocked.Increment(ref runs)); Directory.CreateDirectory(run);
        string stem = VisualBaselineSet.Key(EnvScenario, EnvCheckpoint, grid);
        return ((await new VisualGate(set, run, env).EvaluateAsync(EnvDemo, EnvCheckpoint, grid, grid, screen, stem, new((string?)null), EnvIdentity))!, run);
    }
    private static string Key(string grid = "80x25") => VisualBaselineSet.Key(EnvScenario, EnvCheckpoint, grid);

    [Fact]
    public async Task WindowsAndLinuxBaselinesAreSeparateAndNeverComparedWithEachOther()
    {
        string root = Temp();
        try
        {
            var set = EnvStore(Path.Combine(root, "store"));
            ScreenSnapshot windows = Screen("C:\\Users\\demo  Windows file panel"), linux = Screen("/home/demo  Linux file panel");
            // Windows + SDL (simulated identity) approves first: the flat slot, recording its environment.
            var (missing, run) = await Gate(set, root, WindowsSdl, windows);
            Assert.Equal(VisualStatuses.BaselineMissing, missing.Status);
            set.Approve(run, [Key()], "approve-1", false);
            var flat = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllBytes(set.ApprovedMetadata(Key())), VisualBaselineSet.Strict)!;
            Assert.Equal(WindowsSdl, BaselineEnvironment.Of(flat));
            byte[] windowsCells = File.ReadAllBytes(set.ApprovedCells(Key()));
            // Linux + SDL with a different screen: not a Mismatch against the Windows baseline - no matching baseline.
            var (linuxMissing, linuxRun) = await Gate(set, root, LinuxSdl, linux);
            Assert.Equal(VisualStatuses.BaselineMissing, linuxMissing.Status);
            Assert.Contains("No approved baseline for Linux / SDLDriver / logical", linuxMissing.Reason);
            Assert.Contains("Not comparable with the approved baseline of Windows / SDLDriver / logical (platform Windows differs)", linuxMissing.Reason);
            Assert.Equal(0, linuxMissing.DifferingCells);
            Assert.Equal("approved/linux-sdldriver-logical/" + Key() + ".tvs1", linuxMissing.Approved);
            using (var diff = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(linuxRun, linuxMissing.Diff!))))
            {
                Assert.Equal("Linux", diff.RootElement.GetProperty("environment").GetProperty("platform").GetString());
                Assert.False(diff.RootElement.GetProperty("expected").TryGetProperty("cellsSha256", out _));   // nothing was compared
            }
            // Approving the Linux candidate writes the Linux slot only; the Windows baseline is byte-identical.
            set.Approve(linuxRun, [Key()], "approve-1", false);
            Assert.True(File.Exists(Path.Combine(set.ApprovedDirectory, "linux-sdldriver-logical", Key() + ".tvs1")));
            Assert.Equal(windowsCells, File.ReadAllBytes(set.ApprovedCells(Key())));
            // Each environment matches its own baseline, and only its own.
            Assert.Equal(VisualStatuses.Match, (await Gate(set, root, LinuxSdl, linux)).Result.Status);
            Assert.Equal(VisualStatuses.Match, (await Gate(set, root, WindowsSdl, windows)).Result.Status);
            Assert.Equal(VisualStatuses.Mismatch, (await Gate(set, root, WindowsSdl, linux)).Result.Status);
            Assert.Equal(VisualStatuses.Mismatch, (await Gate(set, root, LinuxSdl, windows)).Result.Status);
            // macOS has none: both others are named with the differing dimension; nothing is borrowed.
            var mac = (await Gate(set, root, MacSdl, windows)).Result;
            Assert.Equal(VisualStatuses.BaselineMissing, mac.Status);
            Assert.Contains("Linux / SDLDriver / logical (platform Linux differs)", mac.Reason);
            Assert.Contains("Windows / SDLDriver / logical (platform Windows differs)", mac.Reason);
            // Status per environment.
            Assert.Equal("Approved", set.Status(LinuxSdl).Single().Status);
            Assert.Equal(VisualStatuses.BaselineMissing, set.Status(MacSdl).Single().Status);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task DriversAndGridsAreSeparateAndAConsoleBaselineNeverStandsInForSdl()
    {
        string root = Temp();
        try
        {
            var set = EnvStore(Path.Combine(root, "store"), "80x25", "120x36");
            var screen = Screen("panels");
            var (_, run) = await Gate(set, root, WindowsConsole, screen);
            set.Approve(run, [Key()], "approve-1", false);
            // Same OS, same grid, same cells: SDL still has no matching baseline (driver differs), and NullDriver neither.
            var sdl = (await Gate(set, root, WindowsSdl, screen)).Result;
            Assert.Equal(VisualStatuses.BaselineMissing, sdl.Status);
            Assert.Contains("(driver Win32ConsoleDriver differs)", sdl.Reason);
            Assert.Equal(VisualStatuses.BaselineMissing, (await Gate(set, root, WindowsNull, screen)).Result.Status);
            Assert.Equal(VisualStatuses.Match, (await Gate(set, root, WindowsConsole, screen)).Result.Status);
            // The grid is part of the key: 120x36 has no baseline in any environment.
            var big = (await Gate(set, root, WindowsConsole, Screen("panels", 120, 36), "120x36")).Result;
            Assert.Equal(VisualStatuses.BaselineMissing, big.Status);
            Assert.DoesNotContain("Not comparable", big.Reason);
            // A candidate records its driver (non-headless only); NullDriver candidates keep the pre-existing format.
            var (_, sdlRun) = await Gate(set, root, WindowsSdl, screen);
            Assert.Contains("\"driver\": \"SDLDriver\"", File.ReadAllText(Path.Combine(sdlRun, Key() + ".candidate.json")));
            var (_, nullRun) = await Gate(set, root, WindowsNull, screen);
            Assert.DoesNotContain("\"driver\"", File.ReadAllText(Path.Combine(nullRun, Key() + ".candidate.json")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task LegacyApprovedFilesKeepTheirMeaningAndAreNeverReusedForAnotherEnvironment()
    {
        string root = Temp();
        try
        {
            var set = EnvStore(Path.Combine(root, "store"));
            var screen = Screen("legacy");
            // An older approval: headless NullDriver, flat slot, no "driver" field (format unchanged).
            var (_, run) = await Gate(set, root, WindowsNull, screen);
            set.Approve(run, [Key()], "approve-1", false);
            string meta = File.ReadAllText(set.ApprovedMetadata(Key()));
            Assert.DoesNotContain("\"driver\"", meta); Assert.Contains("\"platform\": \"Windows\"", meta);
            Assert.Equal([Key() + ".baseline.json", Key() + ".png", Key() + ".tvs1"], Directory.GetFiles(set.ApprovedDirectory).Select(Path.GetFileName).Order().ToArray());
            Assert.Empty(Directory.GetDirectories(set.ApprovedDirectory));
            // Read as Windows / NullDriver / logical: it matches there, and only there.
            Assert.Equal(VisualStatuses.Match, (await Gate(set, root, WindowsNull, screen)).Result.Status);
            var linux = (await Gate(set, root, LinuxNull, screen)).Result;
            Assert.Equal(VisualStatuses.BaselineMissing, linux.Status); Assert.Contains("platform Windows differs", linux.Reason);
            var proxySdl = (await Gate(set, root, WindowsSdl, screen)).Result;
            Assert.Equal(VisualStatuses.BaselineMissing, proxySdl.Status); Assert.Contains("driver NullDriver differs", proxySdl.Reason);
            // The default (no environment given) is this machine with NullDriver: unchanged behaviour on Windows.
            if (OperatingSystem.IsWindows()) Assert.Equal(VisualStatuses.Match, (await Gate(set, root, null, screen)).Result.Status);
            // A copy of the Windows files placed in the Linux folder is refused, never silently used.
            string linuxFolder = Path.Combine(set.ApprovedDirectory, LinuxNull.Folder); Directory.CreateDirectory(linuxFolder);
            foreach (string file in Directory.GetFiles(set.ApprovedDirectory)) File.Copy(file, Path.Combine(linuxFolder, Path.GetFileName(file)));
            var copied = (await Gate(set, root, LinuxNull, screen)).Result;
            Assert.Equal(VisualStatuses.BaselineInvalid, copied.Status);
            Assert.Contains("records environment Windows / NullDriver / logical", copied.Reason);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task CliStatusNamesTheEnvironmentAndApproveReportsTheEnvironmentSlot()
    {
        string root = Temp();
        try
        {
            var set = EnvStore(Path.Combine(root, "store"));
            var (_, linuxRun) = await Gate(set, root, LinuxSdl, Screen("linux"));
            var (_, windowsRun) = await Gate(set, root, WindowsSdl, Screen("windows"));
            set.Approve(windowsRun, [Key()], "approve-1", false);
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await Cli.RunAsync(["baseline", "approve", "--baselines", set.ManifestPath, "--run", linuxRun, "--candidate", Key(), "--confirm", "approve-1"], output, error));
            Assert.Contains("Approved: approved/linux-sdldriver-logical/" + Key() + ".tvs1", output.ToString());
            output = new StringWriter();
            Assert.Equal(0, await Cli.RunAsync(["baseline", "status", "--baselines", set.ManifestPath, "--driver", "SDLDriver"], output, error));
            Assert.Contains("Environment: " + VisualGate.Platform() + " / SDLDriver / logical", output.ToString());
            Assert.Contains("Approved\t" + Key(), output.ToString());   // this machine is Windows in the qualification run
        }
        finally { Delete(root); }
    }

    // ── Phase C: Skipped vs Blocked vs missing baseline ───────────────────────────────

    [Fact]
    public async Task UnrunnableNativeScenarioIsSkippedWhileBlockedAndNotSelectedStayDistinct()
    {
        string root = Temp();
        try
        {
            var unsupported = new TestScenario("sample/unsupported", "Unsupported", "sample", "Not an audited native workflow.").Checkpoint("desktop").Descriptor;
            var batch = await new ScenarioRunner().RunAsync("missing-target.exe", [unsupported], new RunOptions(Path.Combine(root, "native"), ["80x25"]) { Driver = "SDLDriver" });
            var skipped = Assert.Single(batch.Results);
            Assert.Equal(ScenarioOutcomes.Skipped, skipped.Outcome);
            Assert.StartsWith(ScenarioOutcomes.SkippedPrefix, skipped.Reason);
            Assert.Empty(skipped.Checkpoints); Assert.False(skipped.TargetExited);
            Assert.Equal(1, batch.ExitCode);   // nothing was verified
            using (var run = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(batch.RunRoot, "run.json"))))
                Assert.Equal((0, 0, 1), (run.RootElement.GetProperty("passed").GetInt32(), run.RootElement.GetProperty("failed").GetInt32(), run.RootElement.GetProperty("skipped").GetInt32()));
            Assert.Contains("1 skipped (platform/driver unsupported)", File.ReadAllText(Path.Combine(batch.RunRoot, "report.md")));
            // An unsupported grid and a capability the run did not allow stay Blocked (not platform/driver prerequisites).
            var narrow = unsupported with { Id = "sample/narrow", Grid = new ScenarioGrid(140, 40, 140, 40, ["140x40"]) };
            var grid = await new ScenarioRunner().RunAsync("missing-target.exe", [narrow], new RunOptions(Path.Combine(root, "grid"), ["80x25"]));
            Assert.Equal(ScenarioOutcomes.Blocked, Assert.Single(grid.Results).Outcome);
            // Not selected = no result at all.
            Assert.DoesNotContain(batch.Results, r => r.ScenarioId == narrow.Id);
            // The Win32 console host is a genuine platform prerequisite.
            Assert.Equal(OperatingSystem.IsWindows(), NativeExecution.Refusal(unsupported, "Win32ConsoleDriver")?.Contains("requires Windows") != true);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void SkippedCountsInResultsButIsNeitherAFailureNorAPass()
    {
        ScenarioResult R(string outcome) => new("s/" + outcome, "80x25", outcome, outcome == ScenarioOutcomes.Skipped ? ScenarioOutcomes.SkippedPrefix + "x" : "", [], false, true, true);
        var mixed = new BatchResult("", [R(ScenarioOutcomes.Passed), R(ScenarioOutcomes.Skipped)]);
        Assert.Equal(0, mixed.ExitCode);
        Assert.Equal(1, new BatchResult("", [R(ScenarioOutcomes.Skipped)]).ExitCode);
        Assert.Equal(1, new BatchResult("", [R(ScenarioOutcomes.Passed), R(ScenarioOutcomes.Blocked)]).ExitCode);
        Assert.Equal((1, 0, 0, 0), WorkbenchDialog.Categories(mixed));
        Assert.Equal(1, WorkbenchDialog.Skipped(mixed));
        Assert.Equal("1 passed / 0 failed / 0 blocked / 1 skipped", WorkbenchDialog.Counts(mixed));
        Assert.Equal("PASSED", ResultText.RunVerdict(mixed));
        Assert.Equal("SKIPPED", ResultText.RunVerdict(new BatchResult("", [R(ScenarioOutcomes.Skipped)])));
        Assert.Equal(("~", "Skipped"), (ResultText.Marker(mixed.Results[1]), ResultText.Execution(mixed.Results[1])));
        Assert.False(WorkbenchResultsDialog.IsFailure(mixed, mixed.Results[1]));
        Assert.True(WorkbenchResultsDialog.IsFailure(mixed, R(ScenarioOutcomes.Blocked)));
    }

    [Fact]
    public async Task ASkippedExecutionIsNotAVisualFailureButAMissingBaselineStillIs()
    {
        string root = Temp();
        try
        {
            var set = EnvStore(Path.Combine(root, "store"));
            string run = Path.Combine(root, "skip"); Directory.CreateDirectory(run);
            var gate = new VisualGate(set, run, WindowsNull);
            var summary = gate.Finish([new ScenarioResult(EnvScenario, "80x25", ScenarioOutcomes.Skipped, ScenarioOutcomes.SkippedPrefix + "x", [], false, true, true)]);
            var entry = Assert.Single(summary.Entries);
            Assert.Equal(VisualStatuses.NotRun, entry.Status); Assert.Contains("skipped", entry.Reason);
            // A missing baseline is never a skip: the scenario ran, and the gate reports it (approvable through the store).
            var (missing, missingRun) = await Gate(set, root, WindowsNull, Screen("x"));
            Assert.Equal(VisualStatuses.BaselineMissing, missing.Status);
            Assert.NotNull(missing.Candidate);
            Assert.Contains(VisualStatuses.BaselineMissing, VisualStatuses.Failing);
            Assert.Equal([Key()], set.Approve(missingRun, [Key()], "approve-1", false));
        }
        finally { Delete(root); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    internal static TMenuItem TopMenu(WorkbenchApp app, string name) => Items(app.MenuBar!.Menu!.Items).Single(i => i.Name!.Contains(name) || i.Name.Replace("~", "") == name);
    private static async Task<bool> Probe(Bridge bridge, Func<bool> predicate) { bool value = false; await bridge.Invoke(() => value = predicate()); return value; }
    internal static string ProxyDocument(string id, string title) => $$"""
        {
          "schema": "tsharpworkbench.scenario",
          "version": 1,
          "id": "{{id}}",
          "title": "{{title}}",
          "description": "Test document (no platform or driver restriction).",
          "group": "{{id.Split('/')[0]}}",
          "backend": "proxy-logical",
          "expectedOutcome": "passed",
          "steps": [ { "kind": "key", "key": "kbF10" }, { "kind": "checkpoint", "id": "initial" } ]
        }
        """;
    internal static string Write(string folder, string name, string text) { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); File.WriteAllText(path, text); return path; }

    // Logical captures: the NullDriver cell screen of the real WorkbenchApp (diagnostic bitmap font; never a desktop screenshot).
    internal static async Task Shot(Bridge bridge, string state)
    {
        await Settle(bridge);
        if (Environment.GetEnvironmentVariable("TSWB_SCENARIOS_MENU_CAPTURE") is not { Length: > 0 } root) return;
        string phase = Environment.GetEnvironmentVariable("TSWSC1_PHASE") is { Length: > 0 } p ? p : "before";
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
                test = nameof(ScenariosMenuUxTests), renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
}
