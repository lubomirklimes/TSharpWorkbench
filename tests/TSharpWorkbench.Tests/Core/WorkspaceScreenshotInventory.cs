using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Opt-in logical screenshots of the changed Workbench UI states, reusing the inventory approach:
// the real WorkbenchApp runs in-process on NullDriver at the Workbench grid; every capture is the logical cell screen
// written as TVS1 + logical PNG + JSON into one flat directory. Fixtures are disposable copies of the maintained
// TVDemo and Commander example projects; their runs and the only baseline store live in a temporary workspace.
[Collection("NonParallel")]
public sealed class WorkspaceScreenshotInventory
{
    private const string Variable = "TSWB_WORKSPACE_SCREENSHOTS";

    public sealed class InventoryFactAttribute : FactAttribute
    {
        public InventoryFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
                Skip = "Set " + Variable + " to the flat screenshot directory (inventory only).";
        }
    }

    [InventoryFact] public void PrimaryGrid140x40() => new Session(140, 40).Run(s => s.Primary());
    [InventoryFact] public void SecondaryGrid160x50() => new Session(160, 50).Run(s => s.Layout(full: false));
    [InventoryFact] public void MinimumGrid120x36() => new Session(120, 36).Run(s => s.Layout(full: true));

    private sealed record Entry(string File, string Cells, string Metadata, string Window, string State, string Source,
        string Grid, string Fixture, string Mechanism, string Classification, bool Reached, string? Limitation);

    private sealed class Session(int width, int height)
    {
        private readonly string output = Path.GetFullPath(Environment.GetEnvironmentVariable(Variable)!);
        private readonly string repo = RepositoryRoot();
        private readonly string workspace = Path.Combine(Path.GetTempPath(), $"ui-{width}x{height}-{Guid.NewGuid():N}"[..26]);
        private readonly List<Entry> entries = [];
        private readonly List<string> notes = [];
        private WorkbenchApp app = null!;
        private Bridge bridge = null!;
        private string fixture = "none (Start window)";
        private string tvdemo = "", commander = "";

        internal void Run(Func<Session, Task> walk)
        {
            Directory.CreateDirectory(output); Directory.CreateDirectory(workspace);
            tvdemo = Prepare("examples/TVDemo/tvdemo.twproj", "tvdemo");
            commander = Prepare("examples/TSharpCommander/commander.twproj", "commander");
            string settings = Path.Combine(workspace, "preferences.json");
            new WorkbenchPreferences("", "", null, "80x25") { RecentProjects = [tvdemo, commander] }.Save(settings);
            try
            {
                using var driver = new DriverScope((ushort)width, (ushort)height);
                string runs = Path.Combine(workspace, "untitled-runs");
                using (app = new WorkbenchApp("", runs, new("", runs, null, "80x25") { StoragePath = settings }, showStart: true))
                {
                    bridge = new Bridge(); app.DeskTop!.Insert(bridge);
                    Exception? failure = null;
                    Task worker = Task.Run(async () =>
                    {
                        try { await walk(this); }
                        catch (Exception ex) { failure = ex; }
                        finally { await bridge.Invoke(() => { for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel); app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); }); }
                    });
                    AppLifecycleGuard.Run(app);
                    Assert.True(worker.Wait(TimeSpan.FromSeconds(30)), "Inventory worker did not stop.");
                    WriteManifest();
                    Assert.Null(failure);
                }
            }
            finally
            {
                if (Environment.GetEnvironmentVariable("TSWB_WORKSPACE_KEEP") is not { Length: > 0 }) TryDelete(workspace);
            }
        }

        // ── primary 140x40 walk-through ─────────────────────────────────────────────

        internal async Task Primary()
        {
            var start = await Until(() => app.Start);
            await Shot("start-recent-projects", "Start", "no project; two recent projects", "tui (no --project)");
            Press(Keys.kbEsc); await Until(() => app.Start is null ? "" : null);
            await Shot("desktop-empty", "Empty desktop", "Start closed with Esc; Ctrl+N / Ctrl+O on the status line", "Esc in Start");
            await bridge.Invoke(() => app.ShowStart()); await Until(() => app.Start);
            Press(Keys.kbEnter);
            var main = await Loaded(6);
            fixture = "examples/TVDemo/tvdemo.twproj (disposable copy)";
            await Shot("workspace-tvdemo", "Workspace (1)", "TVDemo catalog loaded; focused first scenario; Details pane", "Start > Enter on recent project");
            await Menu("menu-project", Keys.kbAltP, "Save as...", "Project menu");
            await Menu("menu-test", Keys.kbAltT, "Refresh scenarios", "Test menu");
            await Menu("menu-results", Keys.kbAltR, "Open run...", "Results menu");
            await Menu("menu-baselines", Keys.kbAltB, "Baseline store status", "Baselines menu");
            await Menu("menu-window", Keys.kbAltW, "Run monitor", "Window menu");
            await Menu("menu-help", Keys.kbAltH, "Index", "Help menu");

            await bridge.Invoke(() => { main.Tree.Select(); main.Tree.FocusItemNum(1); Nudge(main); });
            await UntilText("Group: tvdemo");
            await Shot("workspace-tvdemo-group-selected", "Workspace (1)", "Groups pane: tvdemo focused; caption names the group", "Groups pane arrows");
            await bridge.Invoke(() => { main.SearchBox.SetData("dialog"); Nudge(main); });
            await Until(() => main.TestList.range == 1 ? "" : null);
            await Shot("workspace-tvdemo-search", "Workspace (1)", "Search 'dialog' filters the list; marks kept", "Search input");
            await bridge.Invoke(() => { main.SearchBox.SetData(""); Nudge(main); main.Tree.FocusItemNum(0); Nudge(main); main.TestList.Select(); main.TestList.FocusItemNum(1); });
            Press(Keys.kbIns); Press(Keys.kbIns); await Until(() => main.TestList.MarkedIds.Count == 3 ? "" : null);
            await Shot("workspace-tvdemo-marked", "Workspace (1)", "three marked (Ins marks and moves down); dirty marker in title", "Space / Enter / Ins");
            Send(WorkbenchDialog.Details); await Until(() => ReferenceEquals(main.current, main.DetailPane) ? "" : null);
            await Shot("workspace-tvdemo-details-focused", "Workspace (1)", "Test > Scenario details: focus in the Details pane", "Test > Scenario details / Alt+D");
            await bridge.Invoke(() => main.TestList.Select());

            Send(WorkbenchDialog.Advanced); var properties = (WorkbenchPropertiesDialog)await Modal("Project Properties");
            await Shot("project-properties-tvdemo", "Project Properties", "TVDemo values; read-only arguments, environment, scenario files", "Project > Properties... / Alt+O");
            string store = Path.Combine(workspace, "tvdemo", "store", "visual-baselines.json");
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
                [new("tvdemo/menu", "menu", ["80x25"], "Disposable store")]), ScenarioRunner.Json));
            await bridge.Invoke(() =>
            {
                properties.StoreField.SetData(store); properties.AllowField.value = 1; properties.AllowField.DrawView();
                properties.ExecutableField.SetData(Path.Combine(workspace, "other-target.exe")); Nudge(properties); properties.StoreField.Select();
            });
            await Shot("project-properties-capabilities-store", "Project Properties", "baseline store and Network set; inline consequence of an executable change", "Project Properties fields");
            await Dismiss();

            Press(Keys.kbF1); await Until(() => app.HelpWindow);
            await Shot("keyboard-reference", "Keyboard Reference", "F1", "F1 / Help > Keyboard reference");
            await Dismiss();
            Send(WorkbenchApp.TestModes); await Until(() => app.HelpWindow);
            await Shot("help-test-modes", "Test Modes and Drivers", "static, platform-aware", "Help > Test modes and drivers");
            await Dismiss();
            Send(WorkbenchApp.About); await Modal("About TSharpWorkbench");
            await Shot("help-about", "About TSharpWorkbench", "product identity and assembly versions", "Help > About TSharpWorkbench");
            await Dismiss();

            Send(WorkbenchApp.CloseProject); await Modal(WorkbenchDialog.UnsavedTitle);
            await Shot("unsaved-changes-cancel-default", "Unsaved Project Changes", "Close project with changed marks; Cancel is the default", "Project > Close project");
            Press(Keys.kbEnter); await Until(() => Top(WorkbenchDialog.UnsavedTitle) is null ? "" : null);

            // A real run, then a rejected command while Results is in front.
            await bridge.Invoke(() => { main.TestList.ClearMarks(); main.TestList.Toggle("tvdemo/menu"); });
            Press(Keys.kbF5); await Modal("Run Configuration"); Send(Views.cmOK);
            await Until(() => main.Busy ? "" : null, 30); await Until(() => !main.Busy ? "" : null, 240);
            Press(Keys.kbF6); var results = await Until(() => Children(app.DeskTop!).OfType<WorkbenchResultsDialog>().SingleOrDefault()); await Settle(200);
            Send(WorkbenchDialog.CompareResult); await UntilText("! This execution has no logical baseline comparison");
            await Shot("notification-results-front", "Results (4) with status-line notification", "Baselines > Compare refused while Results is active", "Baselines > Compare with baseline");
            Press(Keys.kbEsc); await Until(() => Children(app.DeskTop!).OfType<WorkbenchResultsDialog>().Any() ? null : "");
            Send(WorkbenchApp.MessagesWindow); await Until(() => app.Messages);
            await Shot("messages", "Messages (6)", "session history with details of the newest notice", "Alt+6 / Window > Messages");
            Press(Keys.kbEsc); await Until(() => app.Messages is null ? "" : null);

            Press(Keys.kbCtrlO); var picker = (TFileDialog)await Modal("Open project");
            await Shot("project-open-dialog", "Open project (file dialog)", "starts in the project folder with *.twproj", "Ctrl+O");
            await bridge.Invoke(() => picker.fileName!.SetData(commander));
            Send(Views.cmFileOpen);
            if (await Until(() => Top(WorkbenchDialog.UnsavedTitle), 10, optional: true) is not null) Press(Keys.kbAltD);
            main = await Loaded(4, commander);
            fixture = "examples/TSharpCommander/commander.twproj (disposable copy)";
            await Shot("workspace-commander", "Workspace (1)", "Commander catalog loaded", "Project > Open project...");
            await bridge.Invoke(() => { main.TestList.FocusItemNum(3); main.DetailPane.Select(); });
            await Shot("workspace-commander-details-focused", "Workspace (1)", "Commander fixture scenario; Details pane focused", "Tab / Alt+D");
        }

        // ── secondary and minimum grids: layout checks of the main surfaces ─────────

        internal async Task Layout(bool full)
        {
            await Until(() => app.Start);
            await Shot("start-recent-projects", "Start", "no project; two recent projects", "tui (no --project)");
            Press(Keys.kbEnter);
            var main = await Loaded(6);
            fixture = "examples/TVDemo/tvdemo.twproj (disposable copy)";
            await Shot("workspace-tvdemo", "Workspace (1)", "TVDemo catalog loaded", "Start > Enter");
            Send(WorkbenchDialog.Details); await Until(() => ReferenceEquals(main.current, main.DetailPane) ? "" : null);
            await Shot("workspace-tvdemo-details-focused", "Workspace (1)", "Details pane focused", "Test > Scenario details");
            await bridge.Invoke(() => main.TestList.Select());
            Send(WorkbenchDialog.Advanced); await Modal("Project Properties");
            await Shot("project-properties-tvdemo", "Project Properties", "TVDemo values", "Project > Properties...");
            await Dismiss();
            if (full)
            {
                await bridge.Invoke(() => main.TestList.Toggle("tvdemo/about"));
                Send(WorkbenchApp.CloseProject); await Modal(WorkbenchDialog.UnsavedTitle);
                await Shot("unsaved-changes-cancel-default", "Unsaved Project Changes", "Cancel is the default", "Project > Close project");
                Press(Keys.kbEsc); await Until(() => Top(WorkbenchDialog.UnsavedTitle) is null ? "" : null);
                Press(Keys.kbF1); await Until(() => app.HelpWindow);
                await Shot("keyboard-reference", "Keyboard Reference", "F1", "F1");
                await Dismiss();
                Send(WorkbenchApp.MessagesWindow); await Until(() => app.Messages);
                await Shot("messages", "Messages (6)", "session history", "Alt+6");
                Press(Keys.kbEsc); await Until(() => app.Messages is null ? "" : null);
            }
            Press(Keys.kbCtrlO); var picker = (TFileDialog)await Modal("Open project");
            await bridge.Invoke(() => picker.fileName!.SetData(commander));
            Send(Views.cmFileOpen);
            if (await Until(() => Top(WorkbenchDialog.UnsavedTitle), 10, optional: true) is not null) Press(Keys.kbAltD);
            main = await Loaded(4, commander);
            fixture = "examples/TSharpCommander/commander.twproj (disposable copy)";
            await Shot("workspace-commander", "Workspace (1)", "Commander catalog loaded", "Project > Open project...");
        }

        // ── capture and navigation helpers ──────────────────────────────────────────

        private async Task<WorkbenchDialog> Loaded(int count, string? path = null) =>
            await Until(() => app.Project is { Busy: false } p && p.TestList.Strings.Count >= count && (path is null || p.ProjectPath == path) ? p : null, 90);

        private async Task Menu(string name, ushort hotKey, string probe, string window)
        {
            Press(hotKey); await UntilText(probe); await Settle();
            await Shot(name, window + " (drop-down)", "project loaded", "Alt+" + window[0]);
            Press(Keys.kbEsc); await Until(() => Text().Contains(probe) ? null : ""); Press(Keys.kbEsc); await Settle();
        }

        private async Task Shot(string name, string window, string state, string source)
        {
            await Settle();
            await bridge.Invoke(() =>
            {
                app.StatusLine?.Update(); app.StatusLine?.DrawView();
                var screen = ScreenSnapshot.CopyRoot();
                string size = $"{width}x{height}";
                Assert.Equal(size, $"{screen.Width}x{screen.Height}");
                string stem = $"{name}-{size}";
                byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
                File.WriteAllBytes(Path.Combine(output, stem + ".png"), png);
                File.WriteAllBytes(Path.Combine(output, stem + ".tvs1"), cells);
                File.WriteAllText(Path.Combine(output, stem + ".json"), JsonSerializer.Serialize(new
                {
                    name = stem, window, state, source, width = screen.Width, height = screen.Height, captureLayer = "logical",
                    workbenchDriver = "NullDriver", renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png), fixture
                }, new JsonSerializerOptions { WriteIndented = true }));
                entries.Add(new(stem + ".png", stem + ".tvs1", stem + ".json", window, state, source, size, fixture,
                    "in-process WorkbenchApp on NullDriver; ScreenSnapshot.CopyRoot -> TVS1 + LogicalPng", "logical", true, null));
            });
        }

        private void WriteManifest()
        {
            string path = Path.Combine(Path.GetDirectoryName(output)!, $"logical-{width}x{height}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { grid = $"{width}x{height}", entries, notes },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }

        private async Task<TDialog> Modal(string title) => await Until(() => Top(title));
        private TDialog? Top(string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
        private async Task Dismiss()
        {
            var top = app.DeskTop!.TopView();
            Press(Keys.kbEsc);
            await Until(() => !ReferenceEquals(app.DeskTop!.TopView(), top) ? "" : null);
            await Settle();
        }
        private async Task UntilText(string text, int seconds = 30) => await Until(() => Text().Contains(text) ? "" : null, seconds);
        private async Task<T> Until<T>(Func<T?> predicate, int seconds = 30, bool optional = false) where T : class
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                while (true)
                {
                    T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token);
                    if (value is not null) return value;
                    await Task.Delay(25, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (optional) { return null!; }
        }
        private async Task Settle(int milliseconds = 80) { await bridge.Invoke(() => { }); await Task.Delay(milliseconds); await bridge.Invoke(() => { }); }
        private static void Press(ushort key)
        {
            var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key);
            TEventQueue.Enqueue(ev);
        }
        private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
        private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }

        private string Prepare(string example, string folder)
        {
            var project = WorkbenchProject.Load(Path.Combine(repo, example));
            string directory = Path.Combine(workspace, folder); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Path.GetFileName(example));
            (project with { OutputRoot = Path.Combine(directory, "runs") }).Save(path);
            return path;
        }
        private static string RepositoryRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found above the test output.");
        }
        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Text()
    {
        var s = ScreenSnapshot.CopyRoot();
        return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray());
    }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break;
        var first = group.last.Next!;
        for (var v = first; ; v = v.Next!) { yield return v; if (v.Next == first) break; }
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
        {
            if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); }
            else base.HandleEvent(ref ev);
        }
    }
}
