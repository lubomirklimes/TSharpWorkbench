using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Results master/detail, verdict separation, checkpoints and evidence, run history navigation, Re-run through
// Run Configuration, Baseline Comparison and the single candidate review. The real WorkbenchApp runs on NullDriver at
// Workbench grids with the compiled SampleTarget; runs, run folders and baseline stores are disposable copies; the
// operating-system "open" is replaced by a recorder so no viewer is launched.
[Collection("NonParallel")]
public sealed class ResultsUxTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;
    private const string Status = "sample/status/context", Dialog = "sample/dialog/basic", Failure = "selftest/failure/wrong-expectations", Blocked = "selftest/failure/blocked-ui";
    private const string Gated = "desktop-status";

    // A run with every execution verdict, produced by the real runner into the Workbench runs folder: Passed, Blocked
    // (unsupported grid), Failed (expected-failure scenario) and Cancelled (Aborted after cancellation). The status
    // checkpoint is gated by a disposable store without approved baselines (visual MISSING, candidate kept).
    private static async Task<BatchResult> MixedRun(string runs, string store)
    {
        var runner = new ScenarioRunner(); var catalog = await runner.DiscoverAsync(Target);
        using var cancel = new CancellationTokenSource();
        var selected = new[] { Status, Failure, Blocked }.Select(id => catalog.Single(s => s.Id == id)).ToArray();
        return await runner.RunAsync(Target, selected, new RunOptions(runs, ["80x25", "20x6"], Baselines: store),
            message => { if (message.StartsWith("Failed: " + Failure, StringComparison.Ordinal)) cancel.Cancel(); }, cancel.Token);
    }
    private static async Task<BatchResult> PassedRun(string runs)
    {
        var runner = new ScenarioRunner(); var catalog = await runner.DiscoverAsync(Target);
        return await runner.RunAsync(Target, [catalog.Single(s => s.Id == Dialog)], ["80x25"], runs);
    }
    private static string Store(string root, params string[] grids)
    {
        string store = Path.Combine(root, "store", "visual-baselines.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
            [new(Status, Gated, grids.Length == 0 ? ["80x25"] : grids, "Disposable store")]), ScenarioRunner.Json));
        return store;
    }
    private static string Hash(string path) => ScreenSnapshot.Hash(File.ReadAllBytes(path));
    private static Dictionary<string, string> Hashes(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).ToDictionary(f => Path.GetFileName(f), Hash) : [];

    // Reproduction of the TButton contract: a button's availability follows the application command set.
    // sfDisabled alone is overwritten by the next cmCommandSetChanged broadcast; a disabled command keeps it disabled.
    [Fact]
    public void ButtonAvailabilityFollowsTheCommandSet()
    {
        const ushort command = 0x7dfd;
        using var driver = new DriverScope(140, 40);
        using var app = new TApplication();
        var button = new TButton(new(1, 1, 12, 3), "Probe", command, ButtonConstants.bfNormal); app.DeskTop!.Insert(button);
        try
        {
            button.SetState(Views.sfDisabled, true);
            TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null);
            Assert.False(button.GetState(Views.sfDisabled));          // state-only disabling does not survive the broadcast
            TView.DisableCommand(command);
            TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null);
            Assert.True(button.GetState(Views.sfDisabled));           // the command set is the source of truth
            TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null);
            Assert.True(button.GetState(Views.sfDisabled));
            TView.EnableCommand(command);
            TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null);
            Assert.False(button.GetState(Views.sfDisabled));
        }
        finally { TView.EnableCommand(command); }
    }

    // ── A. Results master/detail ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResultsMasterDetailVerdictsCheckpointsEvidenceAndHistory()
    {
        string root = Temp(); var opened = new List<(string Path, bool Folder)>();
        var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, f) => opened.Add((p, f));
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            var older = await PassedRun(runs);
            var mixed = await MixedRun(runs, store);
            // The run has every verdict (Cancelled is Aborted by the runner, never Failed).
            Assert.Equal(["Passed", "Blocked", "Failed", "Aborted"], mixed.Results.Select(r => r.Outcome).Distinct().ToArray());
            var before = Hashes(mixed.RunRoot);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                // Results > Previous run with no session results loads the newest run of the runs folder.
                Send(WorkbenchDialog.EarlierRun);
                var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(4, results.number); Assert.Equal(0, results.state & Views.sfModal);
                    Assert.Equal((140, 38), (results.size.x, results.size.y));
                    Assert.Same(results, app.DeskTop!.current); Assert.Same(results.ExecutionsList, results.current);
                    string header = results.HeaderText;
                    Assert.Contains(Path.GetFileName(mixed.RunRoot), header); Assert.Contains("run 2 of 2 in runs", header); Assert.Contains("(historical run)", header);
                    Assert.Contains("Verdict   FAILED", header); Assert.Contains(WorkbenchDialog.Counts(mixed), header);
                    Assert.Contains("Visual: 0 match, 1 missing", header);
                    var rows = results.ExecutionRows;
                    Assert.Equal(mixed.Results.Length, rows.Length);
                    // Separate Result and Visual columns; Cancelled and Blocked never appear as Failed.
                    Assert.Contains(rows, r => r.StartsWith(" +  Passed", StringComparison.Ordinal) && r.Contains("MISSING") && r.Contains(Status));
                    Assert.Contains(rows, r => r.StartsWith(" !  BLOCKED", StringComparison.Ordinal) && r.Contains("20x6"));
                    Assert.Contains(rows, r => r.StartsWith(" X  Failed (expected)", StringComparison.Ordinal) && r.Contains(Failure));
                    Assert.Contains(rows, r => r.StartsWith(" -  CANCELLED", StringComparison.Ordinal));
                    Assert.DoesNotContain(rows, r => r.Contains("CANCELLED") && r.Contains("FAILED"));
                    Assert.Contains("Result", results.ColumnHeader); Assert.Contains("Visual", results.ColumnHeader); Assert.Contains("Time", results.ColumnHeader);
                    // Default focus: the first problem execution (here the passed execution whose visual result is MISSING).
                    Assert.Equal(0, results.ExecutionsList.focused); Assert.Equal("Overview", results.InspectorTitle);
                    Assert.Contains(results.InspectorRows, r => r.StartsWith("Result     Passed", StringComparison.Ordinal));
                    Assert.Contains(results.InspectorRows, r => r.StartsWith("Visual     MISSING at checkpoint " + Gated, StringComparison.Ordinal));
                    Assert.Contains(results.InspectorRows, r => r.StartsWith("Duration   not recorded", StringComparison.Ordinal));
                });
                await Shot(bridge, "results-default-historical-140x40");
                // Problems filter: only non-passing or visually failing executions.
                await bridge.Invoke(() => Command(results, WorkbenchResultsDialog.ToggleFailed));
                await bridge.Invoke(() => Assert.Equal(mixed.Results.Count(r => WorkbenchResultsDialog.IsFailure(mixed, r)), results.ExecutionRows.Length));
                await Shot(bridge, "results-problems-filter-140x40");
                await bridge.Invoke(() => Command(results, WorkbenchResultsDialog.ToggleFailed));
                // Tab order: Executions, Checkpoints, Details, actions; Shift+Tab returns.
                var visited = new List<object>();
                for (int i = 0; i < 4; i++) { Press(Keys.kbTab); await Settle(bridge); await bridge.Invoke(() => visited.Add(results.current!)); }
                await bridge.Invoke(() =>
                {
                    Assert.Same(results.CheckpointList, visited[0]); Assert.Same(results.Inspector, visited[1]); Assert.IsType<TButton>(visited[2]);
                });
                Press(Keys.kbShiftTab); Press(Keys.kbShiftTab); Press(Keys.kbShiftTab); await Settle(bridge);
                await bridge.Invoke(() => Assert.Same(results.CheckpointList, results.current));
                // Enter on an execution inspects it; Enter on the failed execution selects its failed checkpoint.
                int failedRow = 0;
                await bridge.Invoke(() => { failedRow = Array.FindIndex(results.ExecutionRows, r => r.Contains("Failed (expected)")); results.ExecutionsList.Select(); results.ExecutionsList.FocusItemNum(failedRow); });
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Same(results.CheckpointList, results.current); Assert.Same(results, app.DeskTop!.current);
                    Assert.StartsWith("Checkpoint dialog-open", results.InspectorTitle);
                    var rows = results.InspectorRows;
                    Assert.Contains(rows, r => r.Contains("FAIL") && r.Contains("wrong-focus"));
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("expected", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("actual", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("Logical PNG", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.Contains("cells rendered by Workbench"));
                });
                await Shot(bridge, "results-checkpoint-assertions-failed-140x40");
                // A blocked execution has no checkpoints: the definition's checkpoints are listed as not reached.
                await bridge.Invoke(() => { results.ExecutionsList.Select(); results.ExecutionsList.FocusItemNum(Array.FindIndex(results.ExecutionRows, r => r.Contains("BLOCKED"))); Nudge(results); });
                await bridge.Invoke(() =>
                {
                    Assert.Contains(results.CheckpointRows, r => r.Contains("not reached"));
                    Assert.Contains(results.InspectorRows, r => r.StartsWith("Reason", StringComparison.Ordinal) && r.Contains("does not support grid 20x6"));
                    Assert.Contains(results.InspectorRows, r => r.Contains("Open image: choose a recorded checkpoint"));
                    Assert.True(results.ActionButtons[0].GetState(Views.sfDisabled));
                });
                await Shot(bridge, "results-blocked-not-reached-140x40");
                await bridge.Invoke(() => { results.ExecutionsList.FocusItemNum(Array.FindIndex(results.ExecutionRows, r => r.Contains("CANCELLED"))); Nudge(results); });
                await bridge.Invoke(() => Assert.Contains(results.InspectorRows, r => r.StartsWith("Result     CANCELLED", StringComparison.Ordinal)));
                await Shot(bridge, "results-cancelled-execution-140x40");
                // Evidence rows of the gated checkpoint: logical PNG, TVS1, metadata, comparison and candidate, with provenance.
                await bridge.Invoke(() =>
                {
                    results.ExecutionsList.FocusItemNum(0); Nudge(results);
                    results.CheckpointList.FocusItemNum(Array.FindIndex(results.CheckpointRows, r => r.StartsWith(Gated, StringComparison.Ordinal))); Nudge(results);
                    var rows = results.InspectorRows;
                    foreach (string label in new[] { "Logical PNG", "Cells (TVS1)", "Metadata", "Comparison", "Candidate PNG" })
                        Assert.Contains(rows, r => r.TrimStart().StartsWith(label, StringComparison.Ordinal));
                    Assert.DoesNotContain(rows, r => r.TrimStart().StartsWith("Native", StringComparison.Ordinal) && !r.TrimStart().StartsWith("Native:", StringComparison.Ordinal));
                    Assert.True(results.ActionButtons[1].GetState(Views.sfDisabled));
                    Assert.Contains(rows, r => r.Contains("Native: no native evidence was recorded"));
                    Command(results, WorkbenchResultsDialog.OpenImage);
                });
                await bridge.Invoke(() => Assert.EndsWith(".png", Assert.Single(opened).Path));
                await Shot(bridge, "results-evidence-available-140x40");
                // Enter on an evidence row opens that file; a file deleted after loading is shown missing and never crashes.
                await bridge.Invoke(() =>
                {
                    int tvs1 = Array.FindIndex(results.InspectorRows, r => r.TrimStart().StartsWith("Cells (TVS1)", StringComparison.Ordinal));
                    results.Inspector.Select(); results.Inspector.FocusItemNum(tvs1);
                });
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => Assert.EndsWith(".tvs1", opened[^1].Path));
                string png = Path.Combine(mixed.RunRoot, mixed.Results[0].Checkpoints.Single(c => c.Checkpoint == Gated).Png);
                File.Delete(png);
                await bridge.Invoke(() => { results.CheckpointList.Select(); Nudge(results); results.Reload(); });
                await bridge.Invoke(() =>
                {
                    Assert.Contains(results.InspectorRows, r => r.Contains("! missing"));
                    int count = opened.Count; Command(results, WorkbenchResultsDialog.OpenImage);
                    Assert.Equal(count, opened.Count);
                    Assert.Contains("is missing from the run folder", app.Notifications.HintText());
                });
                await Shot(bridge, "results-evidence-missing-140x40");
                // Copy path and Run folder use the existing clipboard and system open; nothing is written to the run.
                await bridge.Invoke(() => { Command(results, WorkbenchResultsDialog.CopyPath); Command(results, WorkbenchResultsDialog.Locations); });
                await bridge.Invoke(() => { Assert.True(opened[^1].Folder); Assert.Equal(mixed.RunRoot, opened[^1].Path); });
                // Earlier/later navigation through the run folders; an unreadable folder is skipped and reported.
                Press(Keys.kbCtrlLeft); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Contains(Path.GetFileName(older.RunRoot), results.HeaderText); Assert.Contains("run 1 of 2 in runs", results.HeaderText); Assert.Contains("Verdict   PASSED", results.HeaderText); });
                await Shot(bridge, "results-earlier-run-140x40");
                Press(Keys.kbCtrlLeft); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Contains(Path.GetFileName(older.RunRoot), results.HeaderText); Assert.Contains("No earlier run in runs", app.Notifications.HintText()); });
                string broken = Path.Combine(runs, "run-zzzz-broken"); Directory.CreateDirectory(broken);
                File.WriteAllText(Path.Combine(broken, "run.json"), "{ not json"); File.WriteAllText(Path.Combine(broken, "report.md"), "# broken");
                Press(Keys.kbCtrlRight); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains(Path.GetFileName(mixed.RunRoot), results.HeaderText));
                await Shot(bridge, "results-later-run-140x40");
                Press(Keys.kbCtrlRight); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Contains(Path.GetFileName(mixed.RunRoot), results.HeaderText);
                    Assert.Contains("No later readable run in runs; 1 unreadable run folder was skipped", app.Notifications.HintText());
                });
                // Window numbering and focus: Alt+1 Workspace, Alt+4 the same Results, F6 the same, Esc closes.
                PressAlt('1'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                PressAlt('4'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, results));
                Press(Keys.kbF6); await Settle(bridge);
                await bridge.Invoke(() => Assert.Single(Children(app.DeskTop!).OfType<WorkbenchResultsDialog>()));
                Press(Keys.kbEsc); await Until(bridge, () => main.ResultsWindow is null);
                await bridge.Invoke(() => Assert.False(main.Dirty));
            });
            // Viewing and navigation never change a run folder (the deleted PNG is the test's own deletion).
            var after = Hashes(mixed.RunRoot);
            Assert.Equal(before.Keys.Order().Where(k => !k.EndsWith(".png") || after.ContainsKey(k)), after.Keys.Order());
            Assert.All(after, kv => Assert.Equal(before[kv.Key], kv.Value));
        }
        finally { WorkbenchDialog.Opener = previous; Delete(root); }
    }

    [Theory]
    [InlineData(140, 40, 9)]
    [InlineData(160, 50, 12)]
    [InlineData(120, 36, 9)]
    public async Task ResultsLayoutFitsWorkbenchGrids(int width, int height, int listRows)
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            await MixedRun(runs, Store(root));
            await Drive(width, height, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                Send(WorkbenchDialog.EarlierRun);
                var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() =>
                {
                    int w = results.size.x, h = results.size.y;
                    Assert.Equal((width, height - 2), (w, h));
                    Assert.Equal(listRows, results.ExecutionsList.size.y);
                    // Panes and buttons stay inside the frame and never overlap; one free row above the action row and the
                    // navigation row ends above the bottom frame (owner spacing rule).
                    var blocks = new List<TView> { results.ExecutionsList, results.CheckpointList, results.Inspector };
                    blocks.AddRange(results.ActionButtons); blocks.AddRange(results.NavigationButtons);
                    foreach (var v in blocks) Assert.True(v.origin.x >= 1 && v.origin.y >= 1 && v.origin.x + v.size.x <= w - 1 && v.origin.y + v.size.y <= h - 1, v.GetType().Name + " outside");
                    for (int i = 0; i < blocks.Count; i++) for (int j = i + 1; j < blocks.Count; j++) Assert.False(Intersect(blocks[i], blocks[j]), $"{i} overlaps {j}");
                    Assert.All(results.ActionButtons, b => Assert.Equal(h - 5, b.origin.y));
                    Assert.All(results.NavigationButtons, b => Assert.Equal(h - 3, b.origin.y));
                    Assert.Equal(h - 6, results.Inspector.origin.y + results.Inspector.size.y);
                    Assert.True(results.Inspector.size.y >= 9, "inspector rows");
                });
                await Shot(bridge, $"results-default-{width}x{height}");
            });
        }
        finally { Delete(root); }
    }

    // ── B. Re-run ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RerunOpensRunConfigurationForReviewAndStaysSessionOnly()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "rerun.twproj", new WorkbenchProject
                { Name = "Re-run sample", Target = Target, OutputRoot = runs, SelectedIds = [Status, Dialog], ScreenSizes = ["80x25", "140x40"], Inspect = "all", Timeout = 9000 });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 120);
                string first = Directory.GetDirectories(runs, "run-*").Single(), firstJson = Hash(Path.Combine(first, "run.json"));
                string session = "", defaults = "", marks = "";
                await bridge.Invoke(() => { session = main.SessionRun.Fingerprint(); defaults = main.CaptureProject().Fingerprint(); marks = string.Join(",", main.TestList.MarkedIds.Order()); Assert.False(main.Dirty); });
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() =>
                {
                    Assert.Contains(results.ExecutionRows, r => r.Contains(" s"));     // durations measured by the Workbench for session runs
                    results.ExecutionsList.FocusItemNum(Array.FindIndex(results.ExecutionRows, r => r.Contains(Dialog) && r.Contains("140x40"))); Nudge(results);
                    Assert.Contains(results.InspectorRows, r => r.StartsWith("Duration", StringComparison.Ordinal) && r.Contains("measured by the Workbench"));
                });
                // Re-run opens Run Configuration with the execution preselected; nothing runs until Run.
                Task configuring = bridge.Invoke(() => Command(results, WorkbenchResultsDialog.Rerun));
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Busy); Assert.Single(Directory.GetDirectories(runs, "run-*"));
                    Assert.Equal(3u, dialog.Scope.value); Assert.True(dialog.RerunSelected);
                    Assert.Equal(2u, dialog.Sizes.value);                                  // the execution's grid only
                    Assert.Equal("9000", dialog.StepTimeout.Data.Trim()); Assert.Equal(1u, dialog.Inspection.value);  // prior setup restored
                    Assert.True(dialog.SaveDefaults.GetState(Views.sfDisabled)); Assert.Equal(0u, dialog.SaveDefaults.value);
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith($"Re-run of {Dialog} at 140x40 from ", StringComparison.Ordinal));
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith("Runs 1 scenario x 1 screen size = 1 execution", StringComparison.Ordinal));
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith("Re-run: the session configuration and the project defaults stay unchanged", StringComparison.Ordinal));
                    Assert.Same(dialog.RunButton, dialog.current); Assert.Empty(dialog.Problems);
                });
                await Shot(bridge, "rerun-run-configuration-140x40");
                // Cancel: nothing ran and nothing changed.
                Press(Keys.kbEsc); await configuring;
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Busy); Assert.Single(Directory.GetDirectories(runs, "run-*"));
                    Assert.Equal(session, main.SessionRun.Fingerprint()); Assert.Equal(defaults, main.CaptureProject().Fingerprint()); Assert.False(main.Dirty);
                });
                // The user may change the setup before running; Run creates a new run folder with one execution.
                configuring = bridge.Invoke(() => Command(results, WorkbenchResultsDialog.Rerun));
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.Sizes.value = 1; dialog.Sizes.DrawView(); Nudge(dialog); dialog.RunButton.Select(); });
                Press(Keys.kbEnter); await configuring;
                await Until(bridge, () => !main.Busy && Directory.GetDirectories(runs, "run-*").Length == 2, 120);
                string second = Directory.GetDirectories(runs, "run-*").Single(d => d != first);
                var batch = WorkbenchRunHistory.Load(Path.Combine(second, "run.json")).Batch;
                Assert.Equal((Dialog, "80x25"), (batch.Results.Single().ScenarioId, batch.Results.Single().Size));
                Assert.Equal(firstJson, Hash(Path.Combine(first, "run.json")));
                using (var request = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(second, "workbench-request.json"))))
                {
                    Assert.Equal("rerun", request.RootElement.GetProperty("scope").GetString());
                    Assert.Equal(9000, request.RootElement.GetProperty("timeout").GetInt32());
                }
                await bridge.Invoke(() =>
                {
                    Assert.Equal(session, main.SessionRun.Fingerprint()); Assert.Equal(defaults, main.CaptureProject().Fingerprint());
                    Assert.Equal(marks, string.Join(",", main.TestList.MarkedIds.Order())); Assert.False(main.Dirty);
                    Assert.Contains(Path.GetFileName(second), results.HeaderText);
                });
                await Shot(bridge, "results-after-rerun-140x40");
                // A scenario missing from the current catalog is a visible Re-run problem; nothing is substituted.
                configuring = bridge.Invoke(() => ((IResultsHost)main).Rerun(batch, batch.Results[0] with { ScenarioId = "missing/scenario" }));
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Contains(dialog.Problems, p => p.Area == RunArea.Scope && p.Text.StartsWith("missing/scenario is not in the current catalog", StringComparison.Ordinal));
                    Assert.True(dialog.RunButton.GetState(Views.sfDisabled));
                });
                await Shot(bridge, "rerun-unavailable-scenario-140x40");
                Press(Keys.kbEsc); await configuring;
                // A historical run whose recorded driver is unavailable here: reported, never replaced by another driver.
                string copy = Path.Combine(runs, "run-zzzz-native-copy"); Directory.CreateDirectory(copy);
                foreach (string file in Directory.GetFiles(second)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                File.WriteAllText(Path.Combine(copy, "run.json"), File.ReadAllText(Path.Combine(copy, "run.json")).Replace("\"requestedDriver\": \"NullDriver\"", "\"requestedDriver\": \"SDLDriver\""));
                File.WriteAllText(Path.Combine(copy, "workbench-request.json"), File.ReadAllText(Path.Combine(copy, "workbench-request.json")).Replace("\"testType\": \"functional\"", "\"testType\": \"rendering\""));
                Press(Keys.kbCtrlRight); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains("run-zzzz-native-copy", results.HeaderText));
                configuring = bridge.Invoke(() => Command(results, WorkbenchResultsDialog.Rerun));
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(2u, dialog.Mode.value);
                    Assert.Contains(dialog.Problems, p => p.Text.Contains("Native driver is unavailable"));
                    Assert.True(dialog.RunButton.GetState(Views.sfDisabled));
                });
                await Shot(bridge, "rerun-unavailable-driver-140x40");
                Press(Keys.kbEsc); await configuring;
                await bridge.Invoke(() => { Assert.Equal(session, main.SessionRun.Fingerprint()); Assert.False(main.Dirty); });
            });
        }
        finally { Delete(root); }
    }

    // ── C/D. Comparison and candidate review ─────────────────────────────────────────

    [Fact]
    public async Task ComparisonAndCandidateReviewAreExplicitAndSafe()
    {
        string root = Temp(); var opened = new List<(string Path, bool Folder)>();
        var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, f) => opened.Add((p, f));
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            string project = SaveProject(root, "visual.twproj", new WorkbenchProject
            {
                Name = "Visual sample", Target = Target, OutputRoot = runs, SelectedIds = [Status], BaselineStore = store,
                TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"]
            });
            var set = VisualBaselineSet.Load(store);
            string key = VisualBaselineSet.Key(Status, Gated, "80x25"), approved = set.ApprovedCells(key);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                async Task<string> RunVisual()
                {
                    var earlier = Directory.Exists(runs) ? Directory.GetDirectories(runs, "run-*") : [];
                    Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                    Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 120);
                    return Directory.GetDirectories(runs, "run-*").Except(earlier).Single();
                }
                async Task<WorkbenchCandidateDialog> Review()
                {
                    await bridge.Invoke(() => Send(WorkbenchDialog.ReviewCandidates));
                    return await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog);
                }
                string missingRun = await RunVisual();
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => Assert.Contains(results.ExecutionRows, r => r.Contains("MISSING")));
                // Comparison of a missing baseline: the candidate (actual) only, Diff and Expected unavailable.
                await bridge.Invoke(() => Command(results, WorkbenchResultsDialog.CompareCheckpoint));
                var comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(5, comparison.number); Assert.Equal(WorkbenchComparisonWindow.LogicalTitle, comparison.title);
                    Assert.Null(comparison.Model.Expected); Assert.NotNull(comparison.Model.Actual); Assert.Equal(1, comparison.ViewMode);
                    Assert.Contains(key, comparison.HeaderText); Assert.Contains(Path.GetFileName(missingRun), comparison.HeaderText);
                    Assert.Contains("MISSING baseline", comparison.HeaderText); Assert.Contains("logical TVS1 cells", comparison.HeaderText);
                });
                // Availability survives command-set broadcasts (after the run, Results/Comparison activation,
                // a real command-set change through the application's Idle, and an explicit broadcast).
                async Task ButtonsAfterCommandSetChanged(Action check)
                {
                    await bridge.Invoke(check);
                    await bridge.Invoke(() => { TView.DisableCommand(0x7dfe); TView.EnableCommand(0x7dfe); });
                    await Settle(bridge);
                    await bridge.Invoke(() => { TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null); check(); });
                }
                void ComparisonButtons()
                {
                    var buttons = comparison.ActionButtons;
                    TButton Of(ushort command) => buttons.Single(b => b.Command == command);
                    Assert.True(Of(WorkbenchComparisonWindow.Diff).GetState(Views.sfDisabled)); Assert.True(Of(WorkbenchComparisonWindow.Expected).GetState(Views.sfDisabled));
                    Assert.False(TView.CommandEnabled(WorkbenchComparisonWindow.Diff));
                    Assert.False(Of(WorkbenchComparisonWindow.Actual).GetState(Views.sfDisabled)); Assert.False(Of(WorkbenchComparisonWindow.Review).GetState(Views.sfDisabled));
                }
                void ResultsButtons()
                {
                    // Overview of the MISSING execution: Open image and Native need a checkpoint; Compare and Review apply.
                    Assert.Equal("Overview", results.InspectorTitle);
                    Assert.True(results.ActionButtons[0].GetState(Views.sfDisabled)); Assert.True(results.ActionButtons[1].GetState(Views.sfDisabled));
                    Assert.False(TView.CommandEnabled(WorkbenchResultsDialog.OpenImage));
                    Assert.False(results.ActionButtons[2].GetState(Views.sfDisabled)); Assert.False(results.ActionButtons[3].GetState(Views.sfDisabled));
                }
                await ButtonsAfterCommandSetChanged(ComparisonButtons);
                PressAlt('4'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, results));
                await ButtonsAfterCommandSetChanged(ResultsButtons);
                await bridge.Invoke(() => { results.CheckpointList.FocusItemNum(Array.FindIndex(results.CheckpointRows, r => r.StartsWith(Gated, StringComparison.Ordinal))); Nudge(results); });
                await ButtonsAfterCommandSetChanged(() => { Assert.False(results.ActionButtons[0].GetState(Views.sfDisabled)); Assert.True(TView.CommandEnabled(WorkbenchResultsDialog.OpenImage)); Assert.True(results.ActionButtons[1].GetState(Views.sfDisabled)); });
                await bridge.Invoke(() => { results.CheckpointList.FocusItemNum(0); Nudge(results); });
                PressAlt('5'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, comparison));
                await ButtonsAfterCommandSetChanged(ComparisonButtons);
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                await bridge.Invoke(() => { Assert.Same(results, main.ResultsWindow); Assert.True(TView.CommandEnabled(WorkbenchComparisonWindow.Diff)); });

                // New approval: Enter on the check box (Cancel default), Esc and the close icon never write.
                var dialog = await Review();
                await bridge.Invoke(() =>
                {
                    Assert.Equal(WorkbenchCandidateDialog.ApproveTitle, dialog.title);
                    Assert.True(dialog.ActionButton.GetState(Views.sfDisabled)); Assert.Same(dialog.Confirm, dialog.current);
                    Assert.Contains("MISSING: no approved baseline", dialog.Text); Assert.Contains(key, dialog.Text); Assert.Contains("integrity verified", dialog.Text);
                    Assert.Contains("No other baseline or environment changes", dialog.Text); Assert.False(dialog.Valid(Views.cmYes));
                });
                await Shot(bridge, "candidate-review-new-approval-140x40");
                Press(Keys.kbEnter); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchCandidateDialog);
                Assert.False(Directory.Exists(set.ApprovedDirectory));
                dialog = await Review(); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchCandidateDialog);
                dialog = await Review(); Send(Views.cmClose); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchCandidateDialog);
                Assert.False(Directory.Exists(set.ApprovedDirectory));
                // Ticking enables Approve; the confirmation has Cancel as default; cancelling it writes nothing.
                dialog = await Review();
                await bridge.Invoke(() => { dialog.Confirm.Press(0); Nudge(dialog); Assert.False(dialog.ActionButton.GetState(Views.sfDisabled)); Command(dialog, Views.cmYes); });
                var confirm = await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)));
                await bridge.Invoke(() => Assert.Equal("Cancel", (confirm.current as TButton)?.Title));
                await Shot(bridge, "approval-confirmation-140x40");
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)) is null);
                Assert.False(Directory.Exists(set.ApprovedDirectory));
                await Shot(bridge, "approval-cancelled-140x40");
                // Explicit approval writes exactly this checkpoint's baseline.
                dialog = await Review();
                await bridge.Invoke(() => { dialog.Confirm.Press(0); Nudge(dialog); Command(dialog, Views.cmYes); });
                confirm = await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)));
                await bridge.Invoke(() => Command(confirm, Views.cmYes));
                await Until(bridge, () => File.Exists(approved));
                Assert.Equal(Hash(Path.Combine(missingRun, key + ".candidate.tvs1")), Hash(approved));
                Assert.Equal([key + ".baseline.json", key + ".png", key + ".tvs1"], Directory.GetFiles(set.ApprovedDirectory).Select(f => Path.GetFileName(f)).Order().ToArray());
                await bridge.Invoke(() => Assert.Contains(results.InspectorRows, r => r.Contains("Approved just now")));

                // A later run matches; then a deliberate mismatch in the disposable store.
                await RunVisual();
                await bridge.Invoke(() => { Assert.Contains(results.ExecutionRows, r => r.Contains("Match")); Command(results, WorkbenchResultsDialog.CompareCheckpoint); });
                comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() => { Assert.Equal(0, comparison.DifferenceCount); Assert.Contains("Match", comparison.HeaderText); });
                await Shot(bridge, "comparison-logical-match-140x40");
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                var screen = ScreenSnapshot.Deserialize(File.ReadAllBytes(approved));
                var cells = Enumerable.Range(0, screen.Width * screen.Height).Select(i => screen[i % screen.Width, i / screen.Width]).ToArray();
                cells[0] = new('!', cells[0].Attr); cells[1] = new(cells[1].Character, (byte)(cells[1].Attr ^ 0x0f));
                byte[] changed = new ScreenSnapshot(screen.Width, screen.Height, cells).Serialize(); File.WriteAllBytes(approved, changed);
                string meta = set.ApprovedMetadata(key); var file = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllText(meta), ScenarioRunner.Json)!;
                File.WriteAllText(meta, JsonSerializer.Serialize(file with { CellsSha256 = ScreenSnapshot.Hash(changed) }, ScenarioRunner.Json));
                string mismatchRun = await RunVisual();
                await bridge.Invoke(() => { Assert.Contains(results.ExecutionRows, r => r.Contains("MISMATCH")); Command(results, WorkbenchResultsDialog.CompareCheckpoint); });
                comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(2, comparison.DifferenceCount); Assert.Equal(0, comparison.CurrentDifference); Assert.Equal(2, comparison.ViewMode);
                    Assert.Contains("MISMATCH", comparison.HeaderText); Assert.Contains("difference 1 of 2", comparison.HeaderText);
                });
                await Shot(bridge, "comparison-logical-mismatch-140x40");
                Press('n'); await Settle(bridge);
                await bridge.Invoke(() => Assert.Equal(1, comparison.CurrentDifference));
                Press('p'); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(0, comparison.CurrentDifference); Command(comparison, WorkbenchComparisonWindow.Expected); Assert.Equal(0, comparison.ViewMode); });
                await Shot(bridge, "comparison-logical-expected-view-140x40");
                // Replacement from the comparison window: the mode follows the store; the replaced target is named.
                Task reviewing = bridge.Invoke(() => Command(comparison, WorkbenchComparisonWindow.Review));
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog);
                await bridge.Invoke(() => { Assert.Equal(WorkbenchCandidateDialog.ReplaceTitle, dialog.title); Assert.Contains("APPROVED baseline exists", dialog.Text); });
                await Shot(bridge, "candidate-review-replacement-140x40");
                // A candidate replaced after the review opened is refused before writing; details go to Messages.
                string beforeReplace = Hash(approved);
                await bridge.Invoke(() => { dialog.Confirm.Press(0); Nudge(dialog); });
                File.AppendAllText(Path.Combine(mismatchRun, key + ".candidate.json"), " ");
                await bridge.Invoke(() => Command(dialog, Views.cmYes));
                confirm = await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(true)));
                await Shot(bridge, "replacement-confirmation-140x40");
                await bridge.Invoke(() => Command(confirm, Views.cmYes));
                var error = await Wait(bridge, () => Modal(app, "Cannot Replace Baseline"));
                Assert.Equal(beforeReplace, Hash(approved));
                await Shot(bridge, "approval-failed-stale-candidate-140x40");
                await bridge.Invoke(() => Command(error, Views.cmYes));
                await reviewing;
                var messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Level == NoticeLevel.Error && n.Message.Contains("candidate changed since the review opened")));
                await Shot(bridge, "messages-after-approval-failure-140x40");
                await bridge.Invoke(() => messages.Close());
                // A run that keeps an intact candidate can be replaced explicitly.
                string replaceRun = await RunVisual();
                await bridge.Invoke(() => { main.ResultsWindow!.Select(); Send(WorkbenchDialog.ReviewCandidates); });
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog);
                await bridge.Invoke(() => { dialog.Confirm.Press(0); Nudge(dialog); Command(dialog, Views.cmYes); });
                confirm = await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(true)));
                await bridge.Invoke(() => Command(confirm, Views.cmYes));
                await Until(bridge, () => Hash(approved) != beforeReplace);
                Assert.Equal(Hash(Path.Combine(replaceRun, key + ".candidate.tvs1")), Hash(approved));
                // Corrupt candidate cells are refused when the review opens.
                File.AppendAllText(Path.Combine(replaceRun, key + ".candidate.tvs1"), "x");
                await bridge.Invoke(() => Send(WorkbenchDialog.ReviewCandidates));
                await Until(bridge, () => app.Notifications.HintText().Contains("failed their integrity check"));
                await bridge.Invoke(() => Assert.IsNotType<WorkbenchCandidateDialog>(app.DeskTop!.TopView()));
                // Baseline Store Status shows an invalid store instead of failing.
                string manifest = File.ReadAllText(store);
                File.WriteAllText(store, "{ broken");
                Task status = bridge.Invoke(() => Send(WorkbenchDialog.BaselineBrowse));
                var statusDialog = await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.StoreStatusTitle));
                await bridge.Invoke(() => Assert.Contains("INVALID", Text()));
                await Shot(bridge, "baseline-store-invalid-140x40");
                Press(Keys.kbEsc); await status;
                File.WriteAllText(store, manifest);
                await bridge.Invoke(() => Send(WorkbenchDialog.BaselineBrowse));
                statusDialog = await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.StoreStatusTitle));
                await bridge.Invoke(() => Assert.Contains("1 approved, 0 missing", Text()));
                await Shot(bridge, "baseline-store-status-140x40");
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchBaselineDialogs.StoreStatusTitle) is null);
            });
        }
        finally { WorkbenchDialog.Opener = previous; Delete(root); }
    }

    // A historical run whose original store is unknown (runner output without a Workbench request) stays readable;
    // Compare and Review explain the unavailable store instead of guessing one.
    [Fact]
    public async Task HistoricalRunWithoutStoreExplainsUnavailableComparison()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs"); await MixedRun(runs, Store(root));
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                Send(WorkbenchDialog.EarlierRun); var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => Command(results, WorkbenchResultsDialog.CompareCheckpoint));
                await Until(bridge, () => app.Notifications.HintText().Contains("original baseline store of this historical run is unavailable"));
                await bridge.Invoke(() => { Assert.Null(main.Comparison); Command(results, WorkbenchResultsDialog.ReviewCandidate); });
                await Until(bridge, () => app.Notifications.HintText().Contains("unavailable"));
                await bridge.Invoke(() => Assert.IsNotType<WorkbenchCandidateDialog>(app.DeskTop!.TopView()));
            });
        }
        finally { Delete(root); }
    }

    // ── E. focus, active runs and remembered geometry ────────────────────────────────

    [Fact]
    public async Task ResultsDuringActiveRunAndRememberedGeometry()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs"); var older = await PassedRun(runs);
            string project = SaveProject(root, "active.twproj", new WorkbenchProject
                { Name = "Active sample", Target = Target, OutputRoot = runs, SelectedIds = [Blocked], ScreenSizes = ["80x25"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy && Text().Contains("Running"), 30);
                var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                // Results during a run: the live entry, earlier runs readable, Esc never cancels.
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => { Assert.Contains("RUNNING", results.HeaderText); Assert.True(main.Busy); });
                await Shot(bridge, "results-live-run-140x40", optional: true);
                Press(Keys.kbCtrlLeft); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Contains(Path.GetFileName(older.RunRoot), results.HeaderText); Assert.True(main.Busy); });
                Press(Keys.kbEsc); await Until(bridge, () => main.ResultsWindow is null);
                await bridge.Invoke(() => Assert.True(main.Busy || monitor.State is RunState.Finished));
                await Until(bridge, () => !main.Busy, 120);
                // Remembered geometry: Results and Run Monitor reopen where the user left them.
                await Front(app, bridge, monitor);
                await bridge.Invoke(() => monitor.Locate(new(10, 5, 80, 22)));
                Press(Keys.kbAltF3); await Until(bridge, () => !Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().Any());
                Press(Keys.kbF6); results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => results.Locate(new(2, 1, 132, 35)));
                Press(Keys.kbEsc); await Until(bridge, () => main.ResultsWindow is null);
                var saved = WorkbenchPreferences.Load(Path.Combine(root, "settings.json"));
                Assert.Equal(new TRect(10, 5, 80, 22), saved.Window("monitor")); Assert.Equal(new TRect(2, 1, 132, 35), saved.Window("results"));
                Press(Keys.kbF6); results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => Assert.Equal(new TRect(2, 1, 132, 35), results.GetBounds()));
                Press(Keys.kbEsc); await Until(bridge, () => main.ResultsWindow is null);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter);
                monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await bridge.Invoke(() => Assert.Equal(new TRect(10, 5, 80, 22), monitor.GetBounds()));
                await Until(bridge, () => !main.Busy, 120);
                await bridge.Invoke(() => Assert.False(main.Dirty));
            });
        }
        finally { Delete(root); }
    }

    // ── C. native evidence (ordinary TVDemo Proxy target, source-mode deployment) ─────

    // A real SDL window run with renderer-pixel evidence: the native artifact keeps its own label and boundary next to
    // the logical PNG, Native opens it, and Compare opens the Native Evidence Comparison with open actions (native
    // captures have no approved baselines). Skipped without scripts/prepare-tvdemo.ps1 output.
    public sealed class TvDemoFactAttribute : FactAttribute
    {
        public TvDemoFactAttribute() { if (!File.Exists(TvDemoProject)) Skip = "Run scripts/prepare-tvdemo.ps1 (source mode) first."; }
    }
    private static string TvDemoProject => Path.Combine(RepositoryRoot(), "artifacts", "tvdemo", "current", "tvdemo.twproj");
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    [TvDemoFact]
    public async Task NativeEvidenceKeepsProvenanceAndNativeComparisonOpensArtifacts()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Temp(); var opened = new List<(string Path, bool Folder)>();
        var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, f) => opened.Add((p, f));
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "tvdemo.twproj", WorkbenchProject.Load(TvDemoProject) with
                { OutputRoot = runs, SelectedIds = ["tvdemo/menu"], ScreenSizes = ["80x25"], ProxyBackend = "SDLDriver", ProxyCapture = ProxyCaptureKinds.Renderer });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 6 ? p : null, 90);
                Press(Keys.kbF5); var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => Assert.Empty(dialog.Problems));
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 180);
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                string native = "";
                await bridge.Invoke(() =>
                {
                    Assert.Contains(results.ExecutionRows, r => r.StartsWith(" +  Passed", StringComparison.Ordinal));
                    Assert.Contains("SDL window (SDLDriver) + renderer pixels", results.HeaderText);
                    Assert.Contains(results.InspectorRows, r => r.Contains("Proxy requested: SDLDriver"));
                    results.CheckpointList.FocusItemNum(1); Nudge(results);
                    var rows = results.InspectorRows;
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("Logical PNG", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.Contains("cells rendered by Workbench"));
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("Native pixels", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.Contains("SDL renderer pixels (native)"));
                    Assert.False(results.ActionButtons[1].GetState(Views.sfDisabled));
                    Assert.True(results.ActionButtons[3].GetState(Views.sfDisabled));
                    Command(results, WorkbenchResultsDialog.NativeEvidence);
                    native = Assert.Single(opened).Path;
                    Assert.DoesNotContain(results.SelectedCheckpoint!.Png, native);
                });
                await Shot(bridge, "results-tvdemo-native-evidence-140x40", state: "TVDemo SDL window run: logical and native evidence rows with provenance");
                await bridge.Invoke(() => Command(results, WorkbenchResultsDialog.CompareCheckpoint));
                var comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(WorkbenchComparisonWindow.NativeTitle, comparison.title); Assert.True(comparison.Model.Native);
                    Assert.Contains(comparison.Model.Evidence, e => e.Native && e.Layer.Contains("SDL renderer pixels"));
                    Assert.Contains(comparison.Model.Evidence, e => !e.Native && e.Label == "Logical PNG");
                    Assert.Contains("no approved baselines", comparison.HeaderText);
                    Command(comparison, WorkbenchComparisonWindow.OpenNative); Assert.Equal(native, opened[^1].Path);
                    Command(comparison, WorkbenchComparisonWindow.OpenLogical); Assert.EndsWith(results.SelectedCheckpoint!.Png, opened[^1].Path);
                });
                await Shot(bridge, "comparison-native-evidence-140x40", state: "Native Evidence Comparison: native renderer pixels and logical PNG, opened externally");
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
            });
        }
        finally { WorkbenchDialog.Opener = previous; Delete(root); }
    }

    // ── harness (shared shape with RunUxTests) ──────────────────────────────────

    private static async Task Drive(int width, int height, string root, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs");
        string settings = Path.Combine(root, "settings.json");
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(Target, output, new(Target, output, null, "80x25") { StoragePath = settings });
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try { await body(app, bridge); await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); }); }
            catch (Exception ex)
            {
                failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                await bridge.Invoke(() =>
                {
                    for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                    if (app.Project is { Busy: true }) Command(app.Project, WorkbenchDialog.CancelRun);
                    app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                }).WaitAsync(TimeSpan.FromSeconds(10));
            }
        });
        AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(30)), "Worker did not stop.");
        Assert.Null(failure);
    }
    private static bool Intersect(TView a, TView b) =>
        a.origin.x < b.origin.x + b.size.x && b.origin.x < a.origin.x + a.size.x && a.origin.y < b.origin.y + b.size.y && b.origin.y < a.origin.y + a.size.y;
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "ui-" + Guid.NewGuid().ToString("N")[..12]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
    // Optional logical screenshots for the inventory: TSWB_RESULTS_EVIDENCE=<flat directory>. Each capture is the
    // NullDriver cell screen of the real WorkbenchApp: <name>.logical.png + .tvs1 + .json (provenance and hashes).
    private static async Task Shot(Bridge bridge, string name, bool optional = false, string? state = null)
    {
        if (Environment.GetEnvironmentVariable("TSWB_RESULTS_EVIDENCE") is not { Length: > 0 } root) return;
        await Settle(bridge);
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, captureType = "logical", driver = "NullDriver", grid = $"{screen.Width}x{screen.Height}", test = nameof(ResultsUxTests),
                expectedState = state ?? name, transient = optional, renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Press(char letter) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = letter; ev.keyDown.charScan = new CharScanType(letter); TEventQueue.Enqueue(ev); }
    private static void PressAlt(char digit) => Press((ushort)(Keys.kbAlt1 + (digit - '1') * 0x100));
    // Alt+3 reaches the Run Monitor only while a run is active (idle Alt+3 opens Run Configuration);
    // a finished monitor is selected directly, as Window > Next window or a mouse click would.
    private static async Task Front(WorkbenchApp app, Bridge bridge, TView window)
    {
        await bridge.Invoke(() => { window.Select(); if (!ReferenceEquals(app.DeskTop!.current, window)) app.DeskTop.SetCurrent(window, TView.selectMode.normalSelect); });
        await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, window));
    }
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
