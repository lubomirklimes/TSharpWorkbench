using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// The single Run Configuration dialog, the session run configuration, the Run Monitor, cancellation
// accounting, Esc/Close safety and project operations during a run. The real WorkbenchApp runs on NullDriver at
// Workbench grids with the compiled SampleTarget; keys go through the shared event queue as a user's would.
// Every project, run folder and baseline store is a disposable temporary copy.
[Collection("NonParallel")]
public sealed class RunUxTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;
    private const string Status = "sample/status/context", Dialog = "sample/dialog/basic", Blocked = "selftest/failure/blocked-ui";

    // ── A. Run Configuration ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(140, 40, 120, 34)]
    [InlineData(160, 50, 128, 38)]
    [InlineData(120, 36, 118, 34)]
    public async Task RunConfigurationLayoutFocusAndTabOrder(int width, int height, int dialogWidth, int dialogHeight)
    {
        string root = Temp();
        try
        {
            await Drive(width, height, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                string before = "", session = "";
                await bridge.Invoke(() => { main.TestList.Toggle(Status); before = main.CaptureProject().Fingerprint(); session = main.SessionRun.Fingerprint(); });
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(WorkbenchRunDialog.DialogTitle, dialog.title);
                    Assert.Equal((dialogWidth, dialogHeight), (dialog.size.x, dialog.size.y));
                    Assert.Same(dialog.RunButton, dialog.current);
                    Assert.False(dialog.RunButton.GetState(Views.sfDisabled));
                    TGroupBox[] boxes = [dialog.WhatBox, dialog.ModeBox, dialog.SizesBox, dialog.DriverBox, dialog.EvidenceBox, dialog.BaselinesBox, dialog.LimitsBox];
                    // Sections never overlap each other, the summary or the action row, and stay inside the frame.
                    var blocks = boxes.Cast<TView>().Append(dialog.Summary).Append(dialog.SaveDefaults).Append(dialog.RunButton).ToArray();
                    for (int i = 0; i < blocks.Length; i++)
                    {
                        Assert.True(blocks[i].origin.x >= 1 && blocks[i].origin.y >= 1 && blocks[i].origin.x + blocks[i].size.x <= dialog.size.x - 1
                            && blocks[i].origin.y + blocks[i].size.y <= dialog.size.y - 1, $"{blocks[i].GetType().Name} outside the dialog");
                        for (int j = i + 1; j < blocks.Length; j++) Assert.False(Intersect(blocks[i], blocks[j]), $"{i} overlaps {j}");
                    }
                    foreach (var box in boxes)
                        foreach (var child in Children(box).Where(c => c.size.x > 0 && c.GetType().Name != "Background"))
                        {
                            var content = box.ContentRect;
                            Assert.True(child.origin.x >= content.a.x && child.origin.y >= content.a.y && child.origin.x + child.size.x <= content.b.x
                                && child.origin.y + child.size.y <= content.b.y, $"{box.Title}: {child.GetType().Name} clipped");
                        }
                    // One action row (Run, Cancel): an empty row above, the shadow row above the bottom frame.
                    var buttons = Children(dialog).OfType<TButton>().Where(b => b.GetState(Views.sfVisible)).OrderBy(b => b.origin.x).ToArray();   // Prepare target... only while the target blocks
                    Assert.Equal(["<< ~B~asic", "~R~un", "Cancel"], buttons.Select(b => b.Title).ToArray());   // Adds << Basic
                    Assert.All(buttons, b => Assert.Equal(dialog.size.y - 3, b.origin.y));
                    Assert.True(dialog.SaveDefaults.origin.y < dialog.size.y - 4);
                    Assert.Equal(0u, dialog.SaveDefaults.value);
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith("Runs 1 scenario x 1 screen size = 1 execution", StringComparison.Ordinal));
                    Assert.Contains("Ready to run.", dialog.SummaryLines);
                    Assert.Contains("Native driver unavailable", dialog.Inline);
                    Assert.False(dialog.Mode.ButtonState(2));
                    // The compiled sample catalog has no ordinary application scenarios: window drivers stay visible, disabled.
                    Assert.True(dialog.ProxyDrivers.ButtonState(0)); Assert.False(dialog.ProxyDrivers.ButtonState(1));
                    Assert.All(Enumerable.Range(1, 3), i => Assert.False(dialog.Evidence.ButtonState(i)));
                });
                await Shot(bridge, $"run-configuration-default-{width}x{height}");
                // Tab and Shift+Tab reach every section; the boxes hand focus to their own controls.
                async Task<List<string>> Walk(ushort key, int count)
                {
                    var visited = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        Press(key); await Settle(bridge);
                        await bridge.Invoke(() => visited.Add(dialog.current switch { TGroupBox box => box.Title, { } v => v.GetType().Name, null => "" }));
                    }
                    return visited;
                }
                var forward = await Walk(Keys.kbTab, 16);
                foreach (string title in new[] { "What to run", "Test mode", "Target screen sizes", "Target display driver", "Limits and inspection" })
                    Assert.Contains(title, forward);
                Assert.Contains(forward, t => t.StartsWith("Native evidence", StringComparison.Ordinal));
                Assert.Contains(nameof(TListBox), forward); Assert.Contains(nameof(TCheckBoxes), forward);
                // Visual baseline controls are disabled outside Visual regression; in Visual mode the section is reachable.
                Assert.DoesNotContain("Visual baselines", forward);
                await bridge.Invoke(() => { dialog.Mode.value = 1; dialog.Mode.DrawView(); Nudge(dialog); });
                Assert.Contains("Visual baselines", await Walk(Keys.kbShiftTab, 16));
                await bridge.Invoke(() => { dialog.Mode.value = 0; dialog.Mode.DrawView(); Nudge(dialog); });
                await bridge.Invoke(() => dialog.RunButton.Select());
                Press(Keys.kbShiftTab); await Settle(bridge);
                await bridge.Invoke(() => Assert.Same(dialog.BasicButton, dialog.current));   // << Basic precedes Run in reading order
                Press(Keys.kbShiftTab); await Settle(bridge);
                await bridge.Invoke(() => Assert.Same(dialog.SaveDefaults, dialog.current));
                // Keyboard operation inside a group: arrows change the scope radio, Space toggles a size.
                await bridge.Invoke(() => dialog.Scope.Focus());
                Press(Keys.kbDown); Press(Keys.kbDown); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(2u, dialog.Scope.value); Assert.Same(dialog.WhatBox, dialog.current); dialog.Sizes.Focus(); });
                Press(Keys.kbSpace); await Settle(bridge);
                await bridge.Invoke(() => Assert.NotEqual(1u, dialog.Sizes.value & 1));
                // Esc cancels: the session configuration and the project data are unchanged.
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
                await bridge.Invoke(() => { Assert.Equal(before, main.CaptureProject().Fingerprint()); Assert.Equal(session, main.SessionRun.Fingerprint()); Assert.False(main.Busy); });
                // The close icon cancels the same way.
                Press(Keys.kbF5); dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.Mode.value = 1; dialog.Mode.DrawView(); Nudge(dialog); });
                Send(Views.cmClose); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
                await bridge.Invoke(() => { Assert.Equal(session, main.SessionRun.Fingerprint()); Assert.Equal(before, main.CaptureProject().Fingerprint()); });
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ValidationProblemsAreInlineAndDisableRun()
    {
        string root = Temp();
        try
        {
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => main.TestList.Toggle(Status));
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                // Invalid custom grid: inline ! line, summary problem, Run disabled, Enter does not run.
                await bridge.Invoke(() => { dialog.Sizes.value = 1 | 4; dialog.CustomSizes.SetData("300x10"); Nudge(dialog); });
                await bridge.Invoke(() =>
                {
                    Assert.True(dialog.RunButton.GetState(Views.sfDisabled));
                    Assert.Contains(dialog.Problems, p => p.Area == RunArea.Sizes && p.Text.Contains("'300x10'"));
                    Assert.Contains("! Custom size '300x10'", dialog.Inline);
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith("! Cannot run:", StringComparison.Ordinal));
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith("! Run is unavailable: 1 problem", StringComparison.Ordinal));
                });
                await Shot(bridge, "run-configuration-invalid-custom-grid-140x40");
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(dialog, app.DeskTop!.TopView()); Assert.False(main.Busy); });
                // An unsupported size for the selected scenario is explained next to the sizes.
                var descriptor = (await new ScenarioRunner().DiscoverAsync(Target, [], CancellationToken.None)).Single(s => s.Id == Status);
                string tooSmall = $"{GridLimits.MinWidth}x{GridLimits.MinHeight}";
                if (!descriptor.Grid.Supports(tooSmall))
                {
                    await bridge.Invoke(() => { dialog.CustomSizes.SetData(tooSmall); Nudge(dialog); });
                    await bridge.Invoke(() => { Assert.Contains(dialog.Problems, p => p.Area == RunArea.Sizes && p.Text == $"{Status}: {tooSmall} unsupported (range {descriptor.Grid.Describe()})"); Assert.Contains($"! {Status}: {tooSmall}", dialog.Inline); });
                }
                // Several custom grids are kept; the execution count is scenarios x sizes.
                await bridge.Invoke(() => { dialog.CustomSizes.SetData("64x20, 100x30"); Nudge(dialog); });
                await bridge.Invoke(() =>
                {
                    Assert.Empty(dialog.Problems); Assert.False(dialog.RunButton.GetState(Views.sfDisabled));
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith("Runs 1 scenario x 3 screen sizes = 3 executions", StringComparison.Ordinal) && l.Contains("80x25, 64x20, 100x30"));
                });
                await Shot(bridge, "run-configuration-valid-custom-grids-140x40");
                // Out-of-range limits use the existing bounds.
                await bridge.Invoke(() => { dialog.StepTimeout.SetData("50"); Nudge(dialog); });
                await bridge.Invoke(() => { Assert.Contains(dialog.Problems, p => p.Area == RunArea.Limits); Assert.Contains("! ms", dialog.Inline); dialog.StepTimeout.SetData("10000"); Nudge(dialog); });
                // Visual regression without a store: a baseline problem, never a nested picker.
                await bridge.Invoke(() => { dialog.Mode.value = 1; dialog.Mode.DrawView(); Nudge(dialog); });
                await bridge.Invoke(() =>
                {
                    Assert.Contains(dialog.Problems, p => p.Area == RunArea.Baselines && p.Text.Contains("Project Properties"));
                    Assert.False(dialog.VisualPolicy.GetState(Views.sfDisabled));
                    Assert.Contains("not configured", dialog.Inline);
                });
                await Shot(bridge, "run-configuration-visual-no-store-140x40");
                // Native driver mode stays visible but cannot be chosen for this compiled catalog without native scenarios.
                await bridge.Invoke(() => { dialog.Mode.value = 2; dialog.Mode.DrawView(); Nudge(dialog); });
                await bridge.Invoke(() =>
                {
                    Assert.Contains(dialog.Problems, p => p.Area == RunArea.Mode && p.Text.StartsWith("Native driver is unavailable", StringComparison.Ordinal));
                    Assert.True(dialog.NativeDrivers.GetState(Views.sfVisible)); Assert.False(dialog.ProxyDrivers.GetState(Views.sfVisible));
                    Assert.True(dialog.VisualPolicy.GetState(Views.sfDisabled));
                    Assert.True(dialog.RunButton.GetState(Views.sfDisabled));
                });
                await Shot(bridge, "run-configuration-native-unavailable-140x40");
                // Evidence needs a window driver; an incompatible saved combination is a visible problem, not a fallback.
                await bridge.Invoke(() => { dialog.Mode.value = 0; dialog.Mode.DrawView(); dialog.Evidence.value = 1; dialog.Evidence.DrawView(); Nudge(dialog); });
                await bridge.Invoke(() => Assert.Contains(dialog.Problems, p => p.Area == RunArea.Evidence && p.Text.Contains("needs a window driver")));
                await bridge.Invoke(() => { dialog.ProxyDrivers.value = 1; dialog.ProxyDrivers.DrawView(); Nudge(dialog); });
                await bridge.Invoke(() => Assert.Contains(dialog.Problems, p => p.Area == RunArea.Driver && p.Text.Contains("compiled diagnostic")));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
                await bridge.Invoke(() => Assert.Equal("NullDriver", main.SessionRun.ProxyBackend));
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task VisualBaselineStoreStateIsShownFromProjectProperties()
    {
        string root = Temp();
        try
        {
            string store = Path.Combine(root, "store", "visual-baselines.json");
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
                [new(Status, "desktop-status", ["80x25"], "Disposable store")]), ScenarioRunner.Json));
            string project = SaveProject(root, "visual.twproj", new WorkbenchProject
            {
                Name = "Visual sample", Target = Target, OutputRoot = Path.Combine(root, "runs"), SelectedIds = [Status], BaselineStore = store,
                TestType = "visual", ScreenSizes = ["80x25"]
            });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(1u, dialog.Mode.value);
                    Assert.Contains("1 gated checkpoint: 0 approved, 1 missing", dialog.Inline);
                    Assert.Contains(Path.Combine("store", "visual-baselines.json"), dialog.Inline);
                    Assert.Empty(dialog.Problems);
                    Assert.Contains(dialog.SummaryLines, l => l.StartsWith("Visual: compare 1 gated checkpoint", StringComparison.Ordinal));
                    // Store editing stays in Project Properties: no input field or Browse button in Run Configuration.
                    Assert.DoesNotContain(Children(dialog.BaselinesBox), v => v is TInputLine or TButton);
                    dialog.VisualPolicy.value = 1; dialog.VisualPolicy.DrawView(); Nudge(dialog);
                });
                await bridge.Invoke(() => Assert.Contains(dialog.SummaryLines, l => l.Contains("nothing is approved automatically")));
                await Shot(bridge, "run-configuration-visual-store-configured-140x40");
                // 160x50 equivalent of the same state is captured by the inventory; here the store stays read-only.
                string hash = ScreenSnapshot.Hash(File.ReadAllBytes(store));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
                Assert.Equal(hash, ScreenSnapshot.Hash(File.ReadAllBytes(store)));
                await bridge.Invoke(() => Assert.False(main.Dirty));
            });
        }
        finally { Delete(root); }
    }

    // ── B. session configuration and project dirty state ─────────────────────────────

    [Fact]
    public async Task SessionConfigurationNeverDirtiesUnlessSavedAsDefaults()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "session.twproj", new WorkbenchProject
                { Name = "Session sample", Target = Target, OutputRoot = runs, SelectedIds = [Status], ScreenSizes = ["80x25"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => Assert.False(main.Dirty));
                // F5 -> change the sizes -> Run: only the session changes.
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.Sizes.value = 4; dialog.Sizes.DrawView(); dialog.CustomSizes.SetData("64x20"); Nudge(dialog); });
                await bridge.Invoke(() => Assert.Contains(dialog.SummaryLines, l => l.StartsWith("Session only:", StringComparison.Ordinal)));
                await Shot(bridge, "run-configuration-save-defaults-unchecked-140x40");
                await bridge.Invoke(() => dialog.RunButton.Select());
                Press(Keys.kbEnter);
                await Until(bridge, () => main.Busy); await Until(bridge, () => !main.Busy, 60);
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Dirty);
                    Assert.DoesNotContain("*", main.title ?? "");
                    Assert.Equal(["64x20"], main.SessionRun.ScreenSizes);
                    Assert.Equal(["80x25"], main.CaptureProject().ScreenSizes);
                    Assert.Contains("(session only)", main.HeaderText);
                });
                Assert.Equal("64x20", LastRun(runs).Results.Single().Size);
                // The session persists between runs; F5 Enter repeats it.
                Press(Keys.kbF5);
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { Assert.Equal(4u, dialog.Sizes.value); Assert.Equal("64x20", dialog.CustomSizes.Data); Assert.Same(dialog.RunButton, dialog.current); });
                Press(Keys.kbEnter);
                await Until(bridge, () => main.Busy); await Until(bridge, () => !main.Busy, 60);
                await bridge.Invoke(() => Assert.False(main.Dirty));
                // Cancel keeps the previous session configuration.
                Press(Keys.kbF5);
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.Sizes.value = 3; dialog.Sizes.DrawView(); Nudge(dialog); });
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
                await bridge.Invoke(() => Assert.Equal(["64x20"], main.SessionRun.ScreenSizes));
                // Ctrl+S writes the project defaults, never the session-only settings; the CLI keeps reading them.
                await bridge.Invoke(() => Assert.True(main.SaveProject(false)));
                Assert.Equal(["80x25"], WorkbenchProject.Load(project).ScreenSizes);
                var stdout = new StringWriter();
                Assert.Equal(0, await Cli.RunAsync(["run", project], stdout, new StringWriter()));
                Assert.Equal("80x25", LastRun(runs).Results.Single().Size);
                // Save as project defaults: the project becomes dirty because the defaults differ.
                Press(Keys.kbF5);
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { Assert.Equal(0u, dialog.SaveDefaults.value); dialog.SaveDefaults.value = 1; dialog.SaveDefaults.DrawView(); Nudge(dialog); });
                await bridge.Invoke(() => Assert.Contains(dialog.SummaryLines, l => l.StartsWith("These settings also become the project defaults", StringComparison.Ordinal)));
                await Shot(bridge, "run-configuration-save-defaults-checked-140x40");
                await bridge.Invoke(() => dialog.RunButton.Select());
                Press(Keys.kbEnter);
                await Until(bridge, () => main.Busy); await Until(bridge, () => !main.Busy, 60);
                await bridge.Invoke(() =>
                {
                    Assert.True(main.Dirty); Assert.Equal(["64x20"], main.CaptureProject().ScreenSizes);
                    Assert.Contains("run defaults", string.Join(";", main.ChangedGroups()));
                    Assert.DoesNotContain("(session only)", main.HeaderText);
                    // Never written automatically: Ctrl+S remains the explicit save.
                });
                Assert.Equal(["80x25"], WorkbenchProject.Load(project).ScreenSizes);
                await bridge.Invoke(() => Assert.True(main.SaveProject(false)));
                Assert.Equal(["64x20"], WorkbenchProject.Load(project).ScreenSizes);
                // Saving identical defaults does not dirty the project.
                Press(Keys.kbF5);
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.SaveDefaults.value = 1; dialog.SaveDefaults.DrawView(); Nudge(dialog); dialog.RunButton.Select(); });
                Press(Keys.kbEnter);
                await Until(bridge, () => main.Busy); await Until(bridge, () => !main.Busy, 60);
                await bridge.Invoke(() => Assert.False(main.Dirty));
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ActiveRunRequestIsAnImmutableSnapshot()
    {
        var session = new WorkbenchProject { Target = "sample.exe", SelectedIds = [Status], ScreenSizes = ["80x25", "64x20"], Timeout = 2000 };
        var descriptor = new ScenarioDescriptor(Status, "Status", "description", "sample", ScenarioGrid.Standard, [new(ScenarioStep.Checkpoint, "initial")]);
        var request = new WorkbenchRunRequest(session, [descriptor], "build");
        var changed = WorkbenchDialog.WithRunFields(session, session with { ScreenSizes = ["140x40"], Timeout = 5000, TestType = "visual" });
        session.ScreenSizes[0] = "100x30";
        Assert.Equal(["80x25", "64x20"], request.Options().Sizes!);
        Assert.Equal(2000, request.Options().OperationMilliseconds);
        Assert.Equal("functional", request.Project.TestType);
        Assert.True(WorkbenchDialog.RunFieldsDiffer(changed, session));
        Assert.False(WorkbenchDialog.RunFieldsDiffer(session with { Name = "other", SelectedIds = [] }, session));
    }

    // ── C. Run Monitor, cancellation and category accounting ─────────────────────────

    [Fact]
    public async Task RunMonitorFocusCloseEscAndCancelledAccounting()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => { main.TestList.Toggle(Status); main.TestList.Toggle(Blocked); });
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                // Three sizes: the blocked scenario (3 s per execution) keeps the run active while the monitor is exercised.
                await bridge.Invoke(() => { dialog.Sizes.value = 7; dialog.Sizes.DrawView(); dialog.CustomSizes.SetData("100x30"); Nudge(dialog); });
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => main.Busy ? Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault() : null);
                await bridge.Invoke(() =>
                {
                    // Opening never steals focus from the Workspace; geometry 73x16 bottom-right, clear of Groups/Scenarios.
                    Assert.Same(main, app.DeskTop!.current);
                    Assert.Equal((73, 16), (monitor.size.x, monitor.size.y));
                    Assert.Equal((app.DeskTop.size.x - 74, app.DeskTop.size.y - 17), (monitor.origin.x, monitor.origin.y));
                    Assert.False(Intersect(monitor, Rect(main.Tree))); Assert.False(Intersect(monitor, Rect(main.TestList)));
                    Assert.Equal(3, monitor.number); Assert.Equal(0, monitor.state & Views.sfModal);
                    Assert.Same(monitor.ResultsButton, monitor.current);
                    Assert.False(monitor.CancelButton.GetState(Views.sfDisabled));
                    Assert.Contains("Functional | headless (NullDriver)", monitor.Text);
                });
                await Shot(bridge, "run-monitor-starting-140x40", optional: true);
                await Until(bridge, () => monitor.Text.Contains("Running") && monitor.Text.Contains(Blocked), 40);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(RunState.Running, monitor.State);
                    Assert.Equal(3, monitor.View!.Passed); Assert.Equal(3L, monitor.Progress.Value); Assert.Equal(6L, monitor.Progress.Maximum);
                    Assert.Contains("3 of 6", monitor.Text);
                });
                await Shot(bridge, "workspace-run-active-140x40");
                // Esc in the Workspace does nothing: the run continues.
                Press(Keys.kbEsc); await Settle(bridge);
                await bridge.Invoke(() => { Assert.True(main.Busy); Assert.Equal(RunState.Running, monitor.State); });
                // Alt+3 (run active) activates the same monitor; Esc there returns to the Workspace and never cancels.
                PressAlt('3'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, monitor));
                await Shot(bridge, "run-monitor-running-140x40");
                Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                await bridge.Invoke(() => { Assert.True(main.Busy); Assert.Equal(RunState.Running, monitor.State); });
                PressAlt('3'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, monitor));
                await bridge.Invoke(() => Assert.Single(Children(app.DeskTop!).OfType<WorkbenchRunMonitor>()));
                // Close (Alt+F3) while active: the monitor stays, the run continues, focus returns.
                Press(Keys.kbAltF3); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                await bridge.Invoke(() => { Assert.Contains(monitor, Children(app.DeskTop!)); Assert.True(main.Busy); Assert.Contains("stays open while tests run", app.Notifications.HintText()); });
                // Explicit Cancel run: Cancelling until the owned target closes; one request only.
                PressAlt('3'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, monitor));
                await bridge.Invoke(() => Command(monitor, WorkbenchDialog.CancelRun));
                await bridge.Invoke(() =>
                {
                    if (main.Busy) { Assert.Equal(RunState.Cancelling, monitor.State); Assert.True(monitor.CancelButton.GetState(Views.sfDisabled)); Assert.Contains("CANCELLING", monitor.Text); }
                });
                await Shot(bridge, "run-monitor-cancelling-140x40", optional: true);
                Send(WorkbenchDialog.CancelRun); await Settle(bridge);
                await Until(bridge, () => !main.Busy, 60);
                var batch = LastRun(runs);
                Assert.All(batch.Results.Where(r => r.ScenarioId == Status), r => Assert.Equal("Passed", r.Outcome));
                Assert.Contains(batch.Results, r => r.ScenarioId == Blocked && r.Outcome == "Aborted");
                var (passedCount, failedCount, blockedCount, cancelledCount) = WorkbenchDialog.Categories(batch);
                int done = 6 - cancelledCount;
                await bridge.Invoke(() =>
                {
                    Assert.Equal(RunState.Cancelled, monitor.State);
                    // Aborted executions are Cancelled, never Failed; real results before the cancellation are kept.
                    Assert.Equal(0, failedCount);
                    Assert.Equal((passedCount, 0, blockedCount, cancelledCount), (monitor.View!.Passed, monitor.View.Failed, monitor.View.Blocked, monitor.View.Cancelled));
                    Assert.Contains($"CANCELLED ({done} of 6 completed)", monitor.Text);
                    Assert.Equal((long)done, monitor.Progress.Value); Assert.True(monitor.Progress.Value < monitor.Progress.Maximum);
                    Assert.True(monitor.CancelButton.GetState(Views.sfDisabled));
                    Assert.Equal($"Cancelled ({done} of 6 completed)", WorkbenchDialog.Completion(batch));
                    Assert.Equal($"{passedCount} passed / 0 failed / {blockedCount} blocked / {cancelledCount} cancelled", WorkbenchDialog.Counts(batch));
                    Assert.Contains($"{cancelledCount} cancelled", main.HeaderText);
                });
                await Shot(bridge, "run-monitor-cancelled-140x40");
                Send(WorkbenchDialog.Results);
                var results = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await Until(bridge, () => Text().Contains($"Cancelled ({done} of 6 completed)"));
                await Shot(bridge, "results-cancelled-summary-140x40");
                Press(Keys.kbEsc); await Until(bridge, () => !Children(app.DeskTop!).OfType<WorkbenchResultsDialog>().Any());
                // A finished monitor stays open until the user closes it; then Close closes it.
                await bridge.Invoke(() => Assert.Contains(monitor, Children(app.DeskTop!)));
                await Front(app, bridge, monitor);
                Press(Keys.kbAltF3); await Until(bridge, () => !Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().Any());
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task FinishedRunShowsFinishedStateAndResultsFlow()
    {
        string root = Temp();
        try
        {
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => { main.TestList.Toggle(Status); main.TestList.Toggle(Dialog); });
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await Until(bridge, () => !main.Busy, 60);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(RunState.Finished, monitor.State);
                    Assert.Contains("Finished: all 2 passed", monitor.Text);
                    Assert.Equal(monitor.Progress.Maximum, monitor.Progress.Value);
                    Assert.Contains("Cleanup finished", monitor.Text);
                });
                await Front(app, bridge, monitor);
                await Shot(bridge, "run-monitor-finished-140x40");
                // Results is the default button of the monitor: Enter opens Results.
                Press(Keys.kbEnter);
                await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await Until(bridge, () => Text().Contains("2 completed | 2 passed / 0 failed / 0 blocked"));
            });
        }
        finally { Delete(root); }
    }

    // ── D. project operations during an active run ───────────────────────────────────

    [Fact]
    public async Task TestsAreRunningProtectsCloseOpenNewAndExit()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "lifecycle.twproj", new WorkbenchProject
                { Name = "Lifecycle sample", Target = Target, OutputRoot = runs, SelectedIds = [Blocked], ScreenSizes = ["80x25", "140x40"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                async Task StartRun()
                {
                    Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                    Press(Keys.kbEnter); await Until(bridge, () => main.Busy && Text().Contains("Running"));
                }
                async Task<TDialog> Confirmation(Action trigger)
                {
                    await bridge.Invoke(trigger);
                    var d = await Wait(bridge, () => Modal(app, WorkbenchDialog.RunningTitle));
                    await bridge.Invoke(() => Assert.Equal("~K~eep running", (d.current as TButton)?.Title));
                    return d;
                }
                await StartRun();
                // Alt+X: Keep running is the default; Enter keeps running.
                var confirm = await Confirmation(() => Send(Views.cmQuit));
                await bridge.Invoke(() => Assert.Contains(Children(confirm).OfType<TButton>(), b => b.Title == "Cancel run and e~x~it"));
                await Shot(bridge, "tests-are-running-exit-140x40");
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.RunningTitle) is null);
                await bridge.Invoke(() => { Assert.True(main.Busy); Assert.Same(main, app.Project); });
                // Project > Close: Esc keeps running.
                confirm = await Confirmation(() => Send(WorkbenchApp.CloseProject));
                await bridge.Invoke(() => Assert.Contains(Children(confirm).OfType<TButton>(), b => b.Title == "Cancel run and ~c~ontinue"));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchDialog.RunningTitle) is null);
                await bridge.Invoke(() => { Assert.True(main.Busy); Assert.Same(main, app.Project); });
                // Project > Open: asked before the file picker; the close icon keeps running.
                confirm = await Confirmation(() => Send(WorkbenchApp.OpenProject));
                Send(Views.cmClose); await Until(bridge, () => Modal(app, WorkbenchDialog.RunningTitle) is null);
                await bridge.Invoke(() => { Assert.True(main.Busy); Assert.Null(Modal(app, "Open project")); });
                // Project > New with an explicit cancellation: cleanup first, then the Unsaved Changes check (Cancel default).
                await bridge.Invoke(() => main.TestList.Toggle(Status));
                confirm = await Confirmation(() => Send(WorkbenchApp.NewProject));
                await bridge.Invoke(() => Command(confirm, Views.cmYes));
                var unsaved = await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Busy);
                    Assert.Equal("Cancel", (unsaved.current as TButton)?.Title);
                    Assert.Contains(LastRun(runs).Results, r => r.Outcome == "Aborted");
                    Assert.True(Directory.Exists(LastRun(runs).RunRoot));
                });
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                await bridge.Invoke(() => { Assert.Same(main, app.Project); Assert.True(main.Dirty); });
                // Duplicate cancellation requests are refused while cleanup runs.
                await StartRun();
                await bridge.Invoke(() => { Send(WorkbenchDialog.CancelRun); Send(WorkbenchDialog.CancelRun); });
                await Until(bridge, () => !main.Busy, 60);
                Assert.Contains(app.Notifications.History, n => n.Message.StartsWith("Cancellation is already in progress", StringComparison.Ordinal));
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ExitDuringRunCancelsWaitsForCleanupThenAsksAboutUnsavedChanges()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "exit.twproj", new WorkbenchProject
                { Name = "Exit sample", Target = Target, OutputRoot = runs, SelectedIds = [Blocked], ScreenSizes = ["80x25", "140x40"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy && Text().Contains("Running"));
                await bridge.Invoke(() => main.TestList.Toggle(Status));
                Press(Keys.kbAltX);
                var confirm = await Wait(bridge, () => Modal(app, WorkbenchDialog.RunningTitle));
                await bridge.Invoke(() => Command(confirm, Views.cmYes));
                var unsaved = await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle), 60);
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Busy);
                    Assert.All(LastRun(runs).Results, r => Assert.True(r.CleanupSucceeded || r.Outcome == "Aborted"));
                    Assert.Equal("Cancel", (unsaved.current as TButton)?.Title);
                });
                await Shot(bridge, "unsaved-after-cancelled-exit-140x40");
                // Discard is an explicit choice; the application then exits.
                await bridge.Invoke(() => Command(unsaved, Views.cmNo));
            }, exits: true);
        }
        finally { Delete(root); }
    }

    // ── E. unexpected termination ────────────────────────────────────────────────────

    [Fact]
    public async Task RunEndedUnexpectedlyIsAPlainErrorWithDetailsInMessages()
    {
        string root = Temp();
        try
        {
            string store = Path.Combine(root, "store", "visual-baselines.json");
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
                [new(Status, "desktop-status", ["80x25"], "Disposable store")]), ScenarioRunner.Json));
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "ended.twproj", new WorkbenchProject
            {
                Name = "Ended sample", Target = Target, OutputRoot = runs, SelectedIds = [Status, Blocked], BaselineStore = store,
                TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"]
            });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Loaded(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await Until(bridge, () => monitor.Text.Contains(Blocked), 40);
                // The baseline policy changes while candidates are captured: a genuine runner-side failure, not a test failure.
                File.AppendAllText(store, " ");
                var error = await Wait(bridge, () => Modal(app, "Run Ended Unexpectedly"), 60);
                await bridge.Invoke(() =>
                {
                    string text = string.Join("\n", Children(error).OfType<TStaticText>().Select(t => { t.GetText(out string s); return s; }));
                    Assert.Contains("Cause", text); Assert.Contains("Kept", text); Assert.Contains("Target", text); Assert.Contains("Next", text);
                    Assert.Contains("Baseline policy changed", text);
                    Assert.DoesNotContain("Exception", text); Assert.DoesNotContain("   at ", text);
                    Assert.Equal(RunState.EndedUnexpectedly, monitor.State);
                    Assert.Contains(app.Notifications.History, n => n.Level == NoticeLevel.Error && n.Details.Contains("InvalidDataException"));
                    Assert.Equal("OK", (error.current as TButton)?.Title);
                });
                await Shot(bridge, "run-ended-unexpectedly-140x40");
                await bridge.Invoke(() => Command(error, Views.cmYes));
                var messages = await Wait(bridge, () => app.Messages);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, messages) && !main.Busy);
                await bridge.Invoke(() => Assert.Contains("InvalidDataException", string.Join("\n", Rows(messages.DetailLines))));
                await Shot(bridge, "messages-after-run-failure-140x40");
                await bridge.Invoke(() => { Assert.Equal(RunState.EndedUnexpectedly, monitor.State); Assert.Contains("ENDED UNEXPECTEDLY", monitor.Text); });
                await Front(app, bridge, monitor);
                await Shot(bridge, "run-monitor-ended-unexpectedly-140x40");
            });
        }
        finally { Delete(root); }
    }

    // ── F. ordinary application (Proxy) catalog: driver/evidence availability and a real TVDemo run ───────────

    // Uses the maintained TVDemo example on the source-mode deployment from scripts/prepare-tvdemo.ps1 (disposable copy,
    // temporary runs folder). Skipped when that deployment is absent.
    public sealed class TvDemoTheoryAttribute : TheoryAttribute
    {
        public TvDemoTheoryAttribute() { if (!File.Exists(TvDemoProject)) Skip = "Run scripts/prepare-tvdemo.ps1 (source mode) first."; }
    }
    private static string TvDemoProject => Path.Combine(RepositoryRoot(), "artifacts", "tvdemo", "current", "tvdemo.twproj");

    [TvDemoTheory]
    [InlineData(140, 40, true)]
    [InlineData(160, 50, false)]
    [InlineData(120, 36, false)]
    public async Task OrdinaryProxyDriversEvidenceAndHeadlessTvDemoRun(int width, int height, bool run)
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "tvdemo.twproj", WorkbenchProject.Load(TvDemoProject) with { OutputRoot = runs });
            await Drive(width, height, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 6 ? p : null, 90);
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                string grid = $"{width}x{height}";
                await bridge.Invoke(() =>
                {
                    Assert.Same(dialog.RunButton, dialog.current); Assert.Empty(dialog.Problems);
                    // Ordinary application scenarios: every target display driver is available on Windows; no native-driver scenarios.
                    Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(i < 3 || OperatingSystem.IsWindows(), dialog.ProxyDrivers.ButtonState(i)));
                    Assert.False(dialog.Mode.ButtonState(2));
                    Assert.Contains("Target only; never changes the Workbench window.", dialog.Inline);
                });
                await Shot(bridge, $"run-configuration-tvdemo-headless-{grid}", state: "TVDemo ordinary application: Headless target, evidence None");
                if (!run) { Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog); return; }
                // Evidence choices follow ProxyCaptureKinds.ForBackend for each target display driver.
                string[] names = ["headless", "sdl-window", "sdl-gpu-window", "windows-console"];
                for (int backend = 1; backend < 4; backend++)
                {
                    int b = backend;
                    await bridge.Invoke(() => { dialog.ProxyDrivers.Press(b); Nudge(dialog); });
                    await bridge.Invoke(() =>
                    {
                        string[] allowed = ProxyCaptureKinds.ForBackend(WorkbenchCapabilities.ProxyBackends[b]);
                        for (int i = 1; i < 4; i++) Assert.Equal(allowed.Contains(ProxyCaptureKinds.All[i]), dialog.Evidence.ButtonState(i));
                        Assert.Contains(dialog.SummaryLines, l => l.Contains("The target runs with " + WorkbenchCapabilities.TargetDriverName(WorkbenchCapabilities.ProxyBackends[b])));
                    });
                    await Shot(bridge, $"run-configuration-tvdemo-{names[b]}-{grid}", state: $"target display driver {WorkbenchCapabilities.ProxyBackends[b]}; evidence choices enabled per backend");
                }
                // An evidence kind the chosen driver cannot capture is a visible problem, never a fallback.
                await bridge.Invoke(() => { dialog.Evidence.Press(2); Nudge(dialog); });
                await bridge.Invoke(() => Assert.Contains(dialog.Problems, p => p.Area == RunArea.Evidence && p.Text.Contains("not available with Windows console")));
                await Shot(bridge, $"run-configuration-tvdemo-evidence-unsupported-{grid}", state: "GPU completed frame with Windows console: inline problem, Run disabled");
                await bridge.Invoke(() => { dialog.Evidence.Press(0); dialog.ProxyDrivers.Press(0); Nudge(dialog); dialog.RunButton.Select(); });
                await bridge.Invoke(() => Assert.Empty(dialog.Problems));
                // Source-mode ordinary Proxy smoke: the real TVDemo target on the headless backend through Run Configuration.
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await Shot(bridge, $"run-monitor-tvdemo-starting-{grid}", optional: true, state: "Run Monitor opened without focus (Starting/Running)");
                await Until(bridge, () => !main.Busy, 120);
                var batch = LastRun(runs);
                Assert.Equal(["80x25", "140x40"], batch.Results.Select(r => r.Size).ToArray());
                Assert.All(batch.Results, r => { Assert.Equal("Passed", r.Outcome); Assert.True(r.CleanupSucceeded); });
                await bridge.Invoke(() => { Assert.Equal(RunState.Finished, monitor.State); Assert.False(main.Dirty); });
                await Front(app, bridge, monitor);
                await Shot(bridge, $"run-monitor-tvdemo-finished-{grid}", state: "TVDemo proxy run finished: 2 passed");
                Press(Keys.kbEnter);
                await Until(bridge, () => Text().Contains("2 completed | 2 passed"));
                await Shot(bridge, $"results-tvdemo-after-run-{grid}", state: "Results after the TVDemo run (counts in the header)");
            });
        }
        finally { Delete(root); }
    }
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    // ── harness ──────────────────────────────────────────────────────────────────────

    private static async Task Drive(int width, int height, string root, Func<WorkbenchApp, Bridge, Task> body, bool exits = false)
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
            try
            {
                await body(app, bridge);
                if (!exits) await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
            }
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
    private static TRect Rect(TView view)
    {
        var a = view.owner!.MakeGlobal(view.origin); var d = view.owner.owner!.MakeLocal(a);
        return new(d.x, d.y, d.x + view.size.x, d.y + view.size.y);
    }
    private static bool Intersect(TView a, TRect b) => a.origin.x < b.b.x && b.a.x < a.origin.x + a.size.x && a.origin.y < b.b.y && b.a.y < a.origin.y + a.size.y;
    private static bool Intersect(TView a, TView b) => Intersect(a, new TRect(b.origin.x, b.origin.y, b.origin.x + b.size.x, b.origin.y + b.size.y));
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static BatchResult LastRun(string runs) =>
        WorkbenchRunHistory.Load(Path.Combine(Directory.GetDirectories(runs, "run-*").OrderByDescending(Directory.GetCreationTimeUtc).First(), "run.json")).Batch;
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "ui-" + Guid.NewGuid().ToString("N")[..12]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string[] Rows(TListBox list) => Enumerable.Range(0, list.range).Select(i => list.GetText(i, 400)).ToArray();
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
    // Optional logical screenshots for the inventory: TSWB_RUN_UX_EVIDENCE=<flat directory>. Each capture is the
    // NullDriver cell screen of the real WorkbenchApp: <name>.logical.png + .tvs1 + .json (provenance and hashes).
    // `optional` marks transient states (Starting, Cancelling) that a fast run may pass before the capture.
    private static async Task Shot(Bridge bridge, string name, bool optional = false, string? state = null)
    {
        if (Environment.GetEnvironmentVariable("TSWB_RUN_UX_EVIDENCE") is not { Length: > 0 } root) return;
        await Settle(bridge);
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, captureType = "logical", driver = "NullDriver", grid = $"{screen.Width}x{screen.Height}", test = nameof(RunUxTests),
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
