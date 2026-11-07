using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class ScenarioSelectionAndRunnerTests
{
    private static ScenarioDescriptor Entry(string id, string group) => new(id, id, "description", group,
        ScenarioGrid.Standard, [new(ScenarioStep.Checkpoint, "initial")]);

    [Fact]
    public void SelectionUsesIdsAndGroupBoundariesAndRejectsEmptyUnknown()
    {
        var catalog = new[] { Entry("one", "App/Dialogs/Files"), Entry("two", "App/Dialogs/Files"), Entry("three", "App/Dialogs2") };
        Assert.Equal(new[] { "one" }, ScenarioSelection.Resolve(catalog, ["one", "one"], null, false).Select(s => s.Id));
        Assert.Equal(2, ScenarioSelection.Resolve(catalog, [], "App/Dialogs", false).Length);
        Assert.Equal(3, ScenarioSelection.Resolve(catalog, [], null, true).Length);
        Assert.Equal(2, ScenarioSelection.Resolve(catalog, ["one", "three"], null, false).Length);
        Assert.Throws<ArgumentException>(() => ScenarioSelection.Resolve(catalog, ["missing"], null, true));
        Assert.Throws<ArgumentException>(() => ScenarioSelection.Resolve(catalog, [], "App/Dialog", false));
        Assert.Throws<ArgumentException>(() => ScenarioSelection.Resolve(catalog, [], null, false));
        Assert.Throws<ArgumentException>(() => ScenarioSelection.ParseSizes("300x60"));
        Assert.Equal(new[] { "180x60" }, ScenarioSelection.ParseSizes("180x60"));
        Assert.Throws<ArgumentException>(() => ScenarioSelection.ParseSizes(""));
        Assert.Equal(new[] { "80x25", "140x40" }, ScenarioSelection.ParseSizes("both"));
    }

    [Fact]
    public async Task BlockedAndEmptyBatchesNeverSucceedAndArtifactsAreRelative()
    {
        string root = Path.Combine(Path.GetTempPath(), "scenario-runner-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var scenario = Entry("generic", "Generic");
            scenario = scenario with { Grid = new ScenarioGrid(140, 40, 140, 40, ["140x40"]) };
            BatchResult batch = await new ScenarioRunner().RunAsync("unused-for-blocked-target", [scenario], ["80x25"], root);
            Assert.Equal(1, batch.ExitCode); Assert.Equal("Blocked", batch.Results.Single().Outcome);
            Assert.Empty(batch.Results[0].Checkpoints);
            Assert.Equal(new[] { "report.md", "run.json" }, Directory.GetFileSystemEntries(batch.RunRoot).Select(Path.GetFileName).Order());
            Assert.Equal(1, new BatchResult(root, []).ExitCode);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var aborted = await new ScenarioRunner().RunAsync("unused", [scenario], ["80x25"], root, token: cancelled.Token);
            Assert.Equal("Aborted", aborted.Results[0].Outcome); Assert.NotEqual(batch.RunRoot, aborted.RunRoot);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BatchContinuesAfterIsolatedLaunchFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "scenario-runner-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var batch = await new ScenarioRunner().RunAsync("missing-target.exe", [Entry("one", "App"), Entry("two", "App")], ["80x25"], root);
            Assert.Equal(2, batch.Results.Length); Assert.All(batch.Results, r => Assert.Equal("Error", r.Outcome));
            Assert.Equal(1, batch.ExitCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Drives the retired workspace (target/output fields and Browse buttons on the workspace, grid
    // radios, modal Results/Result details) that no longer exists. The current equivalents are WorkspaceUxTests
    // (pickers, marking), ProductQualificationTests (keyboard workflows, Results inspector) and the inventory.
    [Fact(Skip = "Retired: older workspace controls; covered by WorkspaceUxTests and ProductQualificationTests.")]
    public void TuiUsesNormalControlsAndRunsRealTargetThroughSharedRunner()
    {
        string? target = Environment.GetEnvironmentVariable("TSWB_DIAGNOSTIC_TEST_TARGET");
        // This integration smoke is explicitly enabled by the focused validation command.
        Assert.False(string.IsNullOrEmpty(target), "Set TSWB_DIAGNOSTIC_TEST_TARGET to the staged diagnostic Commander executable.");
        string output = Environment.GetEnvironmentVariable("TSWB_DIAGNOSTIC_TEST_OUTPUT") ?? throw new InvalidOperationException("Set TSWB_DIAGNOSTIC_TEST_OUTPUT.");
        using var driver = new DriverScope();
        using var app = new WorkbenchApp(target!, output, new(target!, output, null, "80x25") { StoragePath = Path.Combine(output, "test-preferences.json") });
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                var main = await WaitFor<WorkbenchDialog>(bridge, () => app.Project);
                await bridge.Invoke(() => driver.Driver.SimulateResize(140,40));
                await WaitFor<WorkbenchDialog>(bridge, () => main.size.x == 140 && main.size.y == 38 ? main : null);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(1, ScreenText().Split("Groups (Run group)").Length - 1);
                    Assert.Equal(1, ScreenText().Split("Browse a diagnostic executable").Length - 1);
                    SaveScreen(output, "workbench-140x40");
                });
                await bridge.Invoke(() => driver.Driver.SimulateResize(80,25));
                await WaitFor<WorkbenchDialog>(bridge, () => main.size.x == 80 && main.size.y == 23 ? main : null);
                // Exercise actual nested picker loops, cancellation, and full Unicode paths.
                string pickedRoot = Path.GetFullPath(Path.Combine(output, "browse žluťoučký 状態")); Directory.CreateDirectory(pickedRoot);
                string pickedExe = Path.Combine(pickedRoot, "target 状態.exe"); File.WriteAllText(pickedExe, "selection only");
                var targetField = Children(main).OfType<TInputLine>().Single(v => v.origin.y == 2);
                var outputField = Children(main).OfType<TInputLine>().Single(v => v.origin.y == 5);
                // Enter on the focused adjacent button uses ordinary TButton dispatch.
                await bridge.Invoke(() => { var button = Children(main).OfType<TButton>().Single(b => b.Command == WorkbenchDialog.BrowseTarget); button.Select(); Key(button, Keys.kbEnter); });
                var filePicker = await WaitFor<TFileDialog>(bridge, () => app.DeskTop.TopView() as TFileDialog);
                await bridge.Invoke(() => Key(filePicker, Keys.kbEsc));
                await WaitFor<WorkbenchDialog>(bridge, () => app.Project);
                await bridge.Invoke(() => Assert.Equal(target, targetField.Data));
                Task pickTarget = bridge.Invoke(() => Command(main, WorkbenchDialog.BrowseTarget));
                filePicker = await WaitFor<TFileDialog>(bridge, () => app.DeskTop.TopView() as TFileDialog);
                await bridge.Invoke(() => { filePicker.SetData(pickedExe); Command(filePicker, Views.cmFileOpen); }); await pickTarget;
                await bridge.Invoke(() =>
                {
                    Assert.Equal(pickedExe, targetField.Data);
                    var pendingList = Children(main).OfType<ScenarioList>().Single(); pendingList.LoadCatalog([Entry("one", "App")]); pendingList.Toggle("one");
                    Command(main, WorkbenchDialog.Load); // An existing non-executable file must also recover.
                });
                await WaitFor<string>(bridge, () => ScreenText().Contains("Operation ended:") ? "failed launch recovered" : null);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(pickedExe, targetField.Data);
                    Assert.Equal(new[] { "one" }, Children(main).OfType<ScenarioList>().Single().MarkedIds);
                    targetField.SetData(target!);
                });
                // A mouse-down on the Output Browse button follows the native button path.
                await bridge.Invoke(() =>
                {
                    var button = Children(main).OfType<TButton>().Single(b => b.Command == WorkbenchDialog.BrowseOutput);
                    var mouse = new TEvent { What = Events.evMouseDown }; mouse.mouse.where = button.MakeGlobal(new TPoint(2,0));
                    var up = mouse; up.What = Events.evMouseUp; TEventQueue.Enqueue(up);
                    button.HandleEvent(ref mouse);
                });
                var directoryPicker = await WaitFor<WorkbenchDirectoryDialog>(bridge, () => app.DeskTop.TopView() as WorkbenchDirectoryDialog);
                await bridge.Invoke(() => Key(directoryPicker, Keys.kbEsc));
                await WaitFor<WorkbenchDialog>(bridge, () => app.Project);
                await bridge.Invoke(() => Assert.Equal(output, outputField.Data));
                string cwd = Environment.CurrentDirectory;
                Task pickOutput = bridge.Invoke(() => Command(main, WorkbenchDialog.BrowseOutput));
                directoryPicker = await WaitFor<WorkbenchDirectoryDialog>(bridge, () => app.DeskTop.TopView() as WorkbenchDirectoryDialog);
                await bridge.Invoke(() => { directoryPicker.dirInput!.SetData(pickedRoot); Command(directoryPicker, Views.cmOK); }); await pickOutput;
                await bridge.Invoke(() => { Assert.Equal(pickedRoot, outputField.Data); Assert.Equal(cwd, Environment.CurrentDirectory); outputField.SetData(output); });
                await bridge.Invoke(() =>
                {
                    app.StatusLine!.Update();
                    var f2 = new TEvent { What = Events.evKeyDown };
                    f2.keyDown.keyCode = Keys.kbF2; f2.keyDown.charScan = new CharScanType(Keys.kbF2);
                    app.StatusLine.HandleEvent(ref f2);
                    Assert.Equal(WorkbenchDialog.Load, f2.message.command);
                    main.HandleEvent(ref f2);
                });
                await WaitFor<ScenarioList>(bridge, () => Children(main).OfType<ScenarioList>().SingleOrDefault(s => s.Strings.Count >= 6));
                await bridge.Invoke(() =>
                {
                    var boxes = Children(main).OfType<ScenarioList>().Single();
                    Assert.Equal(new[] { "New File", "Copy Destination", "Overwrite Conflict" }, boxes.Strings.Take(3));
                    boxes.Select();
                    Key(boxes, Keys.kbDown); Key(boxes, 0x0020);
                    Assert.Single(boxes.MarkedIds);
                    Key(boxes, Keys.kbUp); Key(boxes, 0x0020);
                    Assert.Equal(2, boxes.MarkedIds.Count);
                    Key(boxes, Keys.kbDown); Key(boxes, 0x0020); Key(boxes, Keys.kbUp);
                    Assert.Single(boxes.MarkedIds);
                    for (int i = 1; i < boxes.Strings.Count; i++) Key(boxes, Keys.kbDown);
                    Assert.Equal(boxes.Strings.Count - 1, boxes.focused);
                    Key(boxes, 0x0020);
                    Assert.Equal(2, boxes.MarkedIds.Count); Assert.StartsWith("[x]", boxes.GetText(boxes.focused, 80));
                    Key(boxes, 0x0020); boxes.FocusItemNum(0);
                    SaveScreen(output, "workbench-selection");
                });
                Task details = bridge.Invoke(() => Command(main, WorkbenchDialog.Details));
                var modal = await WaitFor<TDialog>(bridge, () => app.DeskTop.TopView() is TDialog d && !ReferenceEquals(d, main) ? d : null);
                await bridge.Invoke(() => Key(modal, Keys.kbEsc)); await details;
                await bridge.Invoke(() =>
                {
                    var groups = main.Tree; groups.Select(); Key(groups, Keys.kbDown); Key(groups, Keys.kbDown);
                    Assert.Equal(2, groups.focused);
                    Command(main, WorkbenchDialog.Run);
                });
                await WaitFor<string>(bridge, () => ScreenText().Contains("Finished:") ? "finished" : null);
                Assert.NotEmpty(Directory.GetFiles(output, "*.png", SearchOption.AllDirectories));
                Task results = bridge.Invoke(() => Command(main, WorkbenchDialog.Results));
                modal = await WaitFor<TDialog>(bridge, () => app.DeskTop.TopView() is TDialog d && !ReferenceEquals(d, main) ? d : null);
                await bridge.Invoke(() => SaveScreen(output, "workbench-results"));
                await bridge.Invoke(() => Key(modal, Keys.kbEnter));
                var resultDetails = await WaitFor<TDialog>(bridge, () => app.DeskTop.TopView() is TDialog d && !ReferenceEquals(d, main) && d != modal ? d : null);
                await bridge.Invoke(() => SaveScreen(output, "workbench-result-details"));
                await bridge.Invoke(() => Key(resultDetails, Keys.kbEsc)); await results;
                await bridge.Invoke(() =>
                {
                    Children(main).OfType<TRadioButtons>().Single().value = 1;
                    Command(main, WorkbenchDialog.Run); // modeless workspace execution is an explicit command
                });
                await WaitFor<string>(bridge, () => ScreenText().Contains("Finished:") && Directory.GetDirectories(output, "run-*").Length == 2 ? "finished" : null);
                await bridge.Invoke(() => { main.CloseApproved(); Command(app, Views.cmQuit); });
            }
            catch (Exception ex)
            {
                failure = ex;
                await bridge.Invoke(() => { app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); });
            }
        });
        int code = AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "TUI worker did not finish.");
        Assert.Null(failure); Assert.Equal(0, code);
        Assert.Equal(2, Directory.GetDirectories(output, "run-*").Length);
    }

    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break;
        TView first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next)
        { yield return item; if (item.Next == first) break; }
    }
    [TargetFact]
    public void TuiQuitCancelsWorkAndWaitsForCleanup()
    {
        using var driver = new DriverScope();
        using var app = new WorkbenchApp(Environment.GetEnvironmentVariable("TSWB_DIAGNOSTIC_TEST_TARGET")!, Path.GetTempPath(), new("", "", null, "80x25") { StoragePath = Path.Combine(Environment.GetEnvironmentVariable("TSWB_DIAGNOSTIC_TEST_OUTPUT")!, "quit-preferences.json") });
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                var main = await WaitFor<WorkbenchDialog>(bridge, () => app.Project);
                await bridge.Invoke(() => { Command(main, WorkbenchDialog.Load); Command(app, Views.cmQuit); });
            }
            catch (Exception ex) { failure = ex; await bridge.Invoke(() => app.EndModal(Views.cmQuit)); }
        });
        Assert.Equal(0, AppLifecycleGuard.Run(app));
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5))); Assert.Null(failure);
    }
    private static string ScreenText()
    {
        var snapshot = ScreenSnapshot.CopyRoot();
        return new string(Enumerable.Range(0, snapshot.Width * snapshot.Height).Select(i => snapshot[i % snapshot.Width, i / snapshot.Width].Character).ToArray());
    }
    private static void SaveScreen(string output, string name)
    {
        string directory = Path.Combine(output, "workbench-screens"); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), LogicalPng.Encode(ScreenSnapshot.CopyRoot()));
    }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Key(TView view, ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); view.HandleEvent(ref ev); }
    private static async Task<T> WaitFor<T>(Bridge bridge, Func<T?> predicate) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token);
            if (value is not null) return value;
            await Task.Delay(20, timeout.Token);
        }
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

public sealed class TargetFactAttribute : FactAttribute
{
    public TargetFactAttribute()
    { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSWB_DIAGNOSTIC_TEST_TARGET"))) Skip = "Set TSWB_DIAGNOSTIC_TEST_TARGET and TSWB_DIAGNOSTIC_TEST_OUTPUT for the explicit Commander TUI smoke."; }
}
