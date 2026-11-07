using System.Reflection;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// The owner's window-number mapping.
//   Alt+1 Workspace, Alt+2 Scenarios (the one Manage Scenarios window), Alt+3 Run Configuration when idle or the existing
//   Run Monitor while a run is active (never starts a run), Alt+4 Results (latest, reused), Alt+5 Baseline Comparison,
//   Alt+6 Messages. The Window menu lists the six with the printed shortcut of the command each Alt+digit routes to.
// The real WorkbenchApp runs on NullDriver; keys and commands go through the shared event queue as a user's would.
// Every project, run folder and baseline store is a disposable temporary copy.
// Optional logical captures (NavigationCaptures): TSWB_PUBLISH_NAV_CAPTURE=<directory>, TSWB_PUBLISH_NAV_PHASE=before|after.
[Collection("NonParallel")]
public sealed class NavigationTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;
    private const string Status = "sample/status/context", Blocked = "selftest/failure/blocked-ui", Gated = "desktop-status";

    // The new mapping: Alt+N -> routed command (and the Window menu item that prints "Alt+N").
    private static readonly (char Key, ushort Command, string Window)[] Mapping =
    [
        ('1', WorkbenchApp.ProjectWindow, "Workspace"),
        ('2', WorkbenchDialog.ManageScenarios, "Scenarios"),
        ('3', WorkbenchDialog.ProgressWindow, "Run"),
        ('4', WorkbenchDialog.Results, "Results"),
        ('5', WorkbenchApp.ComparisonWindow, "Comparison"),
        ('6', WorkbenchApp.MessagesWindow, "Messages"),
    ];

    // ── Window menu / shortcut parity ────────────────────────────────────────────────

    [Fact]
    public async Task WindowMenuListsAllSixWithPrintedShortcutsMatchingTheRoutedCommand()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() =>
                {
                    var top = Items(app.MenuBar!.Menu!.Items).ToArray();
                    // The menu bar is unchanged, with no stolen mnemonics.
                    Assert.Equal(["Project", "Scenarios", "Test", "Results", "Baselines", "Window", "Help"], top.Select(i => i.Name!.Replace("~", "")).ToArray());
                    Assert.Equal(top.Length, top.Select(i => Mnemonic(i.Name!)).Distinct().Count());
                    var window = Items(top[5].SubMenu.Items).Where(i => i.Name is not null).ToArray();
                    // The first six entries: the numbered windows, in Alt order, each printing its Alt+N.
                    for (int i = 0; i < Mapping.Length; i++)
                    {
                        var (key, command, name) = Mapping[i];
                        Assert.Contains(name, window[i].Name!.Replace("~", ""));
                        Assert.Equal($"Alt+{key}", window[i].Param);
                        Assert.Equal(command, window[i].Command);
                        // Parity: the printed shortcut routes to exactly the item's command.
                        Assert.Equal(command, RoutedCommand(key));
                    }
                    // Next / Previous / Size/Move / Zoom / Close stay.
                    var rest = window.Skip(Mapping.Length).Select(i => i.Command).ToArray();
                    Assert.Equal([Views.cmNext, Views.cmPrev, Views.cmResize, Views.cmZoom, Views.cmClose], rest);
                    // Window-menu mnemonics are unique (today Messages and Zoom both use M).
                    Assert.Equal(window.Length, window.Select(i => Mnemonic(i.Name!)).Distinct().Count());
                    // Exactly one printed Alt+N per number across all menus; the Scenarios menu prints the new Alt+2.
                    var all = top.SelectMany(t => Items(t.SubMenu.Items)).Where(i => i.Param is { } p && p.StartsWith("Alt+") && p.Length == 5 && char.IsDigit(p[4])).ToArray();
                    foreach (var (key, command, _) in Mapping) Assert.All(all.Where(i => i.Param == $"Alt+{key}"), i => Assert.Equal(command, i.Command));
                    var manage = Items(top[1].SubMenu.Items).Single(i => i.Command == WorkbenchDialog.ManageScenarios);
                    Assert.Equal("Alt+2", manage.Param);
                    // F-keys keep their commands.
                    Assert.Equal(("F5", Keys.kbF5), Items(top[2].SubMenu.Items).Where(i => i.Command == WorkbenchDialog.Run).Select(i => (i.Param, i.KeyCode)).Single());
                    Assert.Equal(("F6", Keys.kbF6), Items(top[3].SubMenu.Items).Where(i => i.Command == WorkbenchDialog.Results && i.KeyCode == Keys.kbF6).Select(i => (i.Param, i.KeyCode)).Single());
                    // Alt+7..9 are not application shortcuts (TProgram's numbered-window selection keeps them).
                    foreach (char key in "789") Assert.Equal(0, RoutedCommand(key));
                });
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task WindowMenuFitsEveryGridWithoutTruncation(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                Press(Keys.kbAltW); await Until(bridge, () => MenuOpen(app));
                await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    var box = Children(app).OfType<TMenuBox>().Single();
                    Assert.True(box.origin.x >= 0 && box.origin.x + box.size.x <= width && box.origin.y + box.size.y <= height, "Window menu outside the screen");
                    // The rows of the menu box only (the desktop behind repeats words such as Results or Scenarios).
                    string[] rows = Rows(width, height).Skip(box.origin.y).Take(box.size.y)
                        .Select(r => r.Substring(box.origin.x, Math.Min(box.size.x, r.Length - box.origin.x))).ToArray();
                    var items = Items(Items(app.MenuBar!.Menu!.Items).ToArray()[5].SubMenu.Items).Where(i => i.Name is not null).ToArray();
                    foreach (var item in items)
                    {
                        string label = item.Name!.Replace("~", "");
                        var row = rows.FirstOrDefault(r => r.Contains(label + " ") || r.Contains(label + "│"));
                        Assert.True(row is not null, $"'{label}' not shown in full at {width}x{height}");
                        if (item.Param is { Length: > 0 } param) Assert.Contains(param, row);
                    }
                });
                Press(Keys.kbEsc); await Settle(bridge);
            });
        }
        finally { Delete(root); }
    }

    // ── No project ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoProjectAltDigitsExplainBrieflyAndOnlyMessagesOpens()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                var start = await Wait(bridge, () => app.Start);
                foreach (char key in "12345")
                {
                    int before = 0; await bridge.Invoke(() => before = app.Notifications.History.Count);
                    PressAlt(key); await Settle(bridge);
                    await bridge.Invoke(() =>
                    {
                        var notice = app.Notifications.History.Skip(before).Single();
                        Assert.Equal(NoticeLevel.Warning, notice.Level);
                        Assert.StartsWith("No project is open", notice.Message);
                        Assert.Contains("Ctrl+O", notice.Message);
                        Assert.True(notice.Message.Length <= 120, "the message is short");
                        Assert.Null(app.Project); Assert.Null(app.Messages);
                        Assert.False(app.DeskTop!.TopView() is TDialog { } d && !ReferenceEquals(d, start), "a dialog opened");
                    });
                }
                // Messages needs no project.
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() => { Assert.Same(messages, app.DeskTop!.current); Assert.Equal(6, messages.number); });
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // ── Alt+3: Run Configuration when idle, the existing Run Monitor while a run is active ──

    [Fact]
    public async Task Alt3WhenIdleOpensRunConfigurationAndNeverStartsARun()
    {
        string root = Temp(), home = Path.Combine(root, "home"), runs = Path.Combine(root, "runs");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                string[] marks = [];
                await bridge.Invoke(() => { main.TestList.Toggle(Status); marks = main.TestList.MarkedIds.ToArray(); });
                PressAlt('3');
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
                await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Busy); Assert.Empty(RunFolders(runs));
                    Assert.Empty(Children(app.DeskTop!).OfType<WorkbenchRunMonitor>());
                });
                // Esc: nothing runs, nothing changes.
                Press(Keys.kbEsc); await Until(bridge, () => !ReferenceEquals(app.DeskTop!.TopView(), dialog));
                await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Busy); Assert.Empty(RunFolders(runs));
                    Assert.Equal(marks, main.TestList.MarkedIds.ToArray());
                    Assert.Same(main, app.DeskTop!.current);
                });
                // The Window menu entry routes the same way.
                Send(WorkbenchDialog.ProgressWindow);
                var again = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
                Press(Keys.kbEsc); await Until(bridge, () => !ReferenceEquals(app.DeskTop!.TopView(), again));
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.False(main.Busy); Assert.Empty(RunFolders(runs)); });
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Alt3DuringAnActiveRunSelectsTheSameMonitorAndStartsNothing()
    {
        string root = Temp(), home = Path.Combine(root, "home"), runs = Path.Combine(root, "runs");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                await bridge.Invoke(() => { main.TestList.Toggle(Status); main.TestList.Toggle(Blocked); });
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                // Three sizes: the blocked scenario (3 s per execution) keeps the run active.
                await bridge.Invoke(() => { dialog.Sizes.value = 7; dialog.Sizes.DrawView(); dialog.CustomSizes.SetData("100x30"); Nudge(dialog); });
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => main.Busy ? Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault() : null);
                await bridge.Invoke(() => { Assert.Same(main, app.DeskTop!.current); Assert.Equal(3, monitor.number); });
                await Until(bridge, () => RunFolders(runs).Length == 1, 30);
                for (int round = 0; round < 2; round++)
                {
                    PressAlt('1'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                    PressAlt('3'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, monitor));
                    await Settle(bridge);
                    await bridge.Invoke(() =>
                    {
                        Assert.True(main.Busy);
                        Assert.IsNotType<WorkbenchRunDialog>(app.DeskTop!.TopView()); Assert.IsNotType<WorkbenchRunBasicDialog>(app.DeskTop!.TopView());
                        Assert.Single(Children(app.DeskTop!).OfType<WorkbenchRunMonitor>());
                        Assert.Single(RunFolders(runs));
                    });
                }
                // Esc on the monitor returns to the Workspace and never cancels.
                Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                await bridge.Invoke(() => Assert.True(main.Busy));
                // Alt+4 during the run: Results of the run in progress (one instance).
                PressAlt('4'); var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => { Assert.Same(results, app.DeskTop!.current); Assert.Equal(4, results.number); });
                await bridge.Invoke(() => Command(monitor, WorkbenchDialog.CancelRun));
                await Until(bridge, () => !main.Busy, 60);
                // Idle again (the finished monitor still open): Alt+3 opens Run Configuration, never a run.
                PressAlt('3');
                var config = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
                Press(Keys.kbEsc); await Until(bridge, () => !ReferenceEquals(app.DeskTop!.TopView(), config));
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.False(main.Busy); Assert.Single(RunFolders(runs)); });
            });
        }
        finally { Delete(root); }
    }

    // ── Reuse, numbers, focus; nothing runs, approves, saves or changes the selection ────

    [Fact]
    public async Task ShortcutsReuseOneInstanceMatchWindowNumbersAndChangeNothing()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                var (main, monitor, results, comparison, project, store, runs) = await VisualRun(app, bridge, root);
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                PressAlt('2'); var scenarios = await Wait(bridge, () => main.ScenariosWindow);
                string fingerprint = "", projectText = File.ReadAllText(project), storeText = File.ReadAllText(store);
                string[] marks = []; bool dirty = false; int focused = 0;
                await bridge.Invoke(() => { fingerprint = main.CaptureProject().Fingerprint(); marks = main.TestList.MarkedIds.ToArray(); dirty = main.Dirty; focused = main.TestList.focused; });
                var expected = new (char Key, TWindow Window, int Number)[] { ('1', main, 1), ('2', scenarios, 2), ('4', results, 4), ('5', comparison, 5), ('6', messages, 6) };
                for (int round = 0; round < 2; round++)
                    foreach (var (key, window, number) in expected)
                    {
                        PressAlt(key); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, window));
                        await bridge.Invoke(() => Assert.Equal(number, window.number));
                    }
                await bridge.Invoke(() =>
                {
                    Assert.Equal(3, monitor.number);
                    Assert.Single(Children(app.DeskTop!).OfType<WorkbenchDialog>()); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchScenariosWindow>());
                    Assert.Single(Children(app.DeskTop!).OfType<WorkbenchRunMonitor>()); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchResultsDialog>());
                    Assert.Single(Children(app.DeskTop!).OfType<WorkbenchComparisonWindow>()); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchMessagesWindow>());
                    // Every numbered window has a distinct number; the Run Configuration dialog is modal and unnumbered.
                    var numbers = Children(app.DeskTop!).OfType<TWindow>().Where(w => w.number != 0).Select(w => w.number).ToArray();
                    Assert.Equal(numbers.Length, numbers.Distinct().Count());
                    Assert.Equal(fingerprint, main.CaptureProject().Fingerprint());
                    Assert.Equal(marks, main.TestList.MarkedIds.ToArray()); Assert.Equal(focused, main.TestList.focused); Assert.Equal(dirty, main.Dirty);
                    Assert.False(main.Busy);
                });
                Assert.Equal(projectText, File.ReadAllText(project));
                Assert.Equal(storeText, File.ReadAllText(store));
                Assert.Single(RunFolders(runs));
                // Closed windows reopen from their shortcut: Results shows the latest run, Scenarios the one window.
                PressAlt('4'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, results));
                Press(Keys.kbEsc); await Until(bridge, () => main.ResultsWindow is null);
                PressAlt('4'); var reopened = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => { Assert.Same(reopened, app.DeskTop!.current); Assert.Equal(4, reopened.number); Assert.Same(main.ViewedRun, LatestRun(main)); });
                PressAlt('2'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, scenarios));
                Press(Keys.kbEsc); await Until(bridge, () => main.ScenariosWindow is null);
                PressAlt('2'); var scenarios2 = await Wait(bridge, () => main.ScenariosWindow);
                await bridge.Invoke(() => { Assert.Same(scenarios2, app.DeskTop!.current); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchScenariosWindow>()); });
                Assert.Single(RunFolders(runs));
            });
        }
        finally { Delete(root); }
    }

    // ── Modal dialogs keep Alt+digit; focused child and editor windows navigate ─────────

    [Fact]
    public async Task ModalDialogKeepsAltDigitsAndNothingElseOpens()
    {
        string root = Temp(), home = Path.Combine(root, "home"), runs = Path.Combine(root, "runs");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                Send(WorkbenchDialog.Advanced);
                var props = await Wait(bridge, () => Modal(app, "Project Properties"));
                string[] inputs = [];
                await bridge.Invoke(() => inputs = Descendants(props).OfType<TInputLine>().Select(i => i.Data).ToArray());
                foreach (char key in "123456") { PressAlt(key); await Settle(bridge); }
                await bridge.Invoke(() =>
                {
                    Assert.Same(props, app.DeskTop!.TopView());
                    Assert.Equal(inputs, Descendants(props).OfType<TInputLine>().Select(i => i.Data).ToArray());
                    Assert.Null(main.ScenariosWindow); Assert.Null(app.Messages); Assert.Null(main.ResultsWindow);
                    Assert.False(main.Busy); Assert.Empty(RunFolders(runs));
                });
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Properties") is null);
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task AltDigitsInsideTheSearchBoxAndTheScenarioEditorNavigate()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string doc = Write(docs, "nav.scenario.json", ProxyDocument("ext/nav", "Navigation"));
        string path = SaveProject(root, "nav-editor.twproj", new WorkbenchProject { Name = "Nav editor", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [doc] });
        string before = File.ReadAllText(doc);
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await Loaded(app, bridge, 1);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 1 ? p : null, 60);
                // Search box focused: Alt+6 opens Messages; no digit is typed into the search.
                await bridge.Invoke(() => main.SearchBox.Focus());
                await Until(bridge, () => ReferenceEquals(main.current, main.SearchBox));
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() => { Assert.Same(messages, app.DeskTop!.current); Assert.Equal("", main.SearchBox.Data); });
                PressAlt('1'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                await bridge.Invoke(() => Assert.Equal("", main.SearchBox.Data));
                // Scenario editor focused: Alt+2 reaches the Scenarios window; the editor stays open and unchanged.
                var editor = await Wait(bridge, () => main.OpenScenarioEditor(doc));
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, editor));
                PressAlt('2'); var scenarios = await Wait(bridge, () => main.ScenariosWindow);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, scenarios));
                await bridge.Invoke(() => { Assert.Contains(editor, main.ScenarioEditors); Assert.Equal(2, scenarios.number); });
                await bridge.Invoke(() => { editor.Select(); });
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, editor));
                PressAlt('1'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                await bridge.Invoke(() => Assert.Single(main.ScenarioEditors));
                await bridge.Invoke(() => main.CloseScenarioEditors());
            });
            Assert.Equal(before, File.ReadAllText(doc));
        }
        finally { Delete(root); }
    }

    // ── Messages: always reachable, history kept across close/reopen ───────────────────

    [Fact]
    public async Task MessagesIsRetainedAfterCloseAndReopen()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => { app.Notifications.Info("nav first"); app.Notifications.Warning("nav second"); });
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(6, messages.number);
                    Assert.Contains("nav second", messages.Entries.GetText(0, 200));
                    Assert.Contains("nav first", messages.Entries.GetText(1, 200));
                });
                Press(Keys.kbEsc); await Until(bridge, () => app.Messages is null);
                await bridge.Invoke(() => app.Notifications.Error("nav third"));
                int count = 0; await bridge.Invoke(() => count = app.Notifications.History.Count);
                PressAlt('6'); var again = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() =>
                {
                    Assert.NotSame(messages, again); Assert.Same(again, app.DeskTop!.current); Assert.Equal(6, again.number);
                    Assert.Equal(count, again.Entries.range);
                    Assert.Contains("nav third", again.Entries.GetText(0, 200));
                    Assert.Contains("nav second", again.Entries.GetText(1, 200));
                    Assert.Contains("nav first", again.Entries.GetText(2, 200));
                });
                // Alt+6 while Messages is current: the same window.
                PressAlt('6'); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(again, app.Messages); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchMessagesWindow>()); });
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // ── Printed numbers in help and hints ───────────────────────────────────────────────

    [Fact]
    public void HelpHintsAndReadmeUseTheNewNumbers()
    {
        string help = string.Join("\n", WorkbenchHelp.Keyboard());
        Assert.Contains("Alt+1..6", help);
        Assert.Contains("Scenarios (Alt+2)", help);
        Assert.Contains("Run Monitor (Alt+3)", help);
        Assert.Contains("Results (Alt+4)", help);
        Assert.Contains("Comparison (Alt+5)", help);
        Assert.Contains("Messages (Alt+6)", help);
        // Old numbers anywhere in the product sources or the user documentation.
        string[] stale =
        [
            "Manage Scenarios (Alt+6)", "Manage scenarios (Alt+6)", "Scenarios window (Alt+6)", "Scenarios (Alt+6)", "Messages (Alt+5)", "Messages window (Alt+5)",
            "Run Monitor (Alt+2)", "Alt+2 monitor", "Alt+2 shows it again", "Alt+2 returns", "Results (Alt+3)", "Comparison (Alt+4)", "(Alt+6)\"",
            "Alt+2 Run monitor", "Alt+3 Results", "Alt+4 Comparison", "Alt+5 Messages", "Alt+6 Scenarios", "window 2, Alt+2", "window 3, F6 / Alt+3", "window 4, Alt+4",
            "**Messages** window (Alt+5)", "scenarios...** (Alt+6)", "F6** (window 3)", "Workspace, Run monitor", "Scenarios window (window 6)",
            "Run Monitor (window 2", "Results (window 3", "Baseline Comparison (window 4", "Scenarios (Alt+6,",
        ];
        string repo = RepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(repo, "src", "TSharpWorkbench"), "*.cs")
            .Append(Path.Combine(repo, "src", "TSharpWorkbench", "README.md")).Append(Path.Combine(repo, "src", "TSharpWorkbench", "PROJECT-FORMAT.md"))
            .Append(Path.Combine(repo, "docs", "installation-and-first-run.md")).Where(File.Exists);
        var found = files.SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => stale.Any(s => x.line.Contains(s, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetRelativePath(repo, x.f)}:{x.i + 1}: {x.line.Trim()}").ToArray();
        Assert.True(found.Length == 0, "Old window numbers:\n" + string.Join("\n", found));
    }

    // ── Logical captures (Before / After) ───────────────────────────────────────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task NavigationCaptures(int width, int height)
    {
        if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_NAV_CAPTURE") is not { Length: > 0 }) return;
        string root = Temp(), home = Path.Combine(root, "home");
        try
        {
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                // Windows are opened through their commands and bridge selection, independent of the Alt mapping.
                var (main, monitor, results, comparison, _, _, _) = await VisualRun(app, bridge, root);
                Send(WorkbenchApp.MessagesWindow); var messages = await Wait(bridge, () => app.Messages);
                Send(WorkbenchDialog.ManageScenarios); var scenarios = await Wait(bridge, () => main.ScenariosWindow);
                async Task Front(TView window, string state)
                {
                    await bridge.Invoke(() => { window.Select(); if (!ReferenceEquals(app.DeskTop!.current, window)) app.DeskTop.SetCurrent(window, TView.selectMode.normalSelect); });
                    await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, window));
                    await Capture(bridge, state);
                }
                await Front(main, "workspace");
                Press(Keys.kbAltP); await Until(bridge, () => MenuOpen(app)); await Capture(bridge, "project-menu"); Press(Keys.kbEsc); await Until(bridge, () => !MenuOpen(app));
                Press(Keys.kbAltW); await Until(bridge, () => MenuOpen(app)); await Capture(bridge, "window-menu"); Press(Keys.kbEsc); await Until(bridge, () => !MenuOpen(app));
                await Front(scenarios, "scenarios");
                await Front(monitor, "run-monitor");
                await Front(results, "results");
                await Front(comparison, "comparison");
                await Front(messages, "messages");
            });
        }
        finally { Delete(root); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    // One finished visual run with candidates: Workspace, Run Monitor, Results and Baseline Comparison all exist.
    private static async Task<(WorkbenchDialog Main, WorkbenchRunMonitor Monitor, WorkbenchResultsDialog Results, WorkbenchComparisonWindow Comparison, string Project, string Store, string Runs)>
        VisualRun(WorkbenchApp app, Bridge bridge, string root)
    {
        string runs = Path.Combine(root, "visual-runs"), store = Store(root);
        string project = SaveProject(root, "nav.twproj", new WorkbenchProject
            { Name = "Navigation sample", Target = Target, OutputRoot = runs, SelectedIds = [Status], BaselineStore = store, TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"] });
        await Loaded(app, bridge, 16);
        await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
        var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
        Send(WorkbenchDialog.Run);
        await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
        Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 120);
        var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
        Send(WorkbenchDialog.Results); var results = await Wait(bridge, () => main.ResultsWindow);
        await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, results));
        Press(Keys.kbAltC); var comparison = await Wait(bridge, () => main.Comparison);
        await Settle(bridge);
        return (main, monitor, results, comparison, project, store, runs);
    }
    private static object? LatestRun(WorkbenchDialog main) =>
        typeof(WorkbenchDialog).GetField("history", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(main) is List<BatchResult> h && h.Count > 0 ? h[^1] : null;

    // The application's Alt+digit routing (WorkbenchApp.KeyCommand), as the event loop applies it.
    private static ushort RoutedCommand(char digit)
    {
        var method = typeof(WorkbenchApp).GetMethod("KeyCommand", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WorkbenchApp.KeyCommand(TEvent) not found");
        ushort key = (ushort)(Keys.kbAlt1 + (digit - '1') * 0x100);
        var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key);
        return (ushort)method.Invoke(null, [ev])!;
    }

    private static string Store(string root)
    {
        string store = Path.Combine(root, "store", "visual-baselines.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
            [new(Status, Gated, ["80x25"], "Disposable store")]), ScenarioRunner.Json));
        return store;
    }
    private static string ProxyDocument(string id, string title) => $$"""
        {
          "schema": "tsharpworkbench.scenario",
          "version": 1,
          "id": "{{id}}",
          "title": "{{title}}",
          "description": "Navigation test document.",
          "group": "{{id.Split('/')[0]}}",
          "backend": "proxy-logical",
          "expectedOutcome": "passed",
          "steps": [ { "kind": "key", "key": "kbF10" }, { "kind": "checkpoint", "id": "initial" } ]
        }
        """;
    private static string[] RunFolders(string runs) => Directory.Exists(runs) ? Directory.GetDirectories(runs, "run-*") : [];
    private static string Write(string folder, string name, string text) { string path = Path.Combine(folder, name); File.WriteAllText(path, text); return path; }
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "p3a-nav-" + Guid.NewGuid().ToString("N")[..10]);
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
    private static char Mnemonic(string name) { int i = name.IndexOf('~'); return i < 0 ? '\0' : char.ToUpperInvariant(name[i + 1]); }
    private static bool MenuOpen(WorkbenchApp app) => Children(app).Any(v => v is TMenuBox) || app.current is TMenuView or TMenuBox;
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string[] Rows(int width, int height)
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
        if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_NAV_CAPTURE") is not { Length: > 0 } root) return;
        string phase = Environment.GetEnvironmentVariable("TSWB_PUBLISH_NAV_PHASE") is { Length: > 0 } p ? p : "before";
        string appHash = File.Exists(Path.Combine(RepositoryRoot(), "src", "TSharpWorkbench", "WorkbenchApp.cs"))
            ? ScreenSnapshot.Hash(File.ReadAllBytes(Path.Combine(RepositoryRoot(), "src", "TSharpWorkbench", "WorkbenchApp.cs"))) : "";
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string name = $"{phase}-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.tvs1"), cells);
            File.WriteAllLines(Path.Combine(root, name + ".logical-nulldriver.txt"),
                Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character).ToArray()).TrimEnd()));
            File.WriteAllText(Path.Combine(root, name + ".logical-nulldriver.json"), JsonSerializer.Serialize(new
            {
                name, phase, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical-nulldriver", driver = "NullDriver",
                test = nameof(NavigationTests), renderer = LogicalPng.Renderer, workbenchAppSha256 = appHash,
                cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
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
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void PressAlt(char digit) => Press((ushort)(Keys.kbAlt1 + (digit - '1') * 0x100));
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
