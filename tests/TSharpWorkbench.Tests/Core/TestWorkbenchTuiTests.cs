using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// The interactive Workbench against the TSharpVision-owned sample target. The TUI is driven through its own
// commands and controls; execution goes through the same ScenarioRunner the CLI uses.
[Collection("NonParallel")]
public sealed class TestWorkbenchTuiTests
{
    // The Workbench UI is checked at its supported minimum and primary grids; the target still runs at the
    // 80x25 and custom grids requested in Run Configuration.
    [Theory]
    [InlineData(120,36)]
    [InlineData(140,40)]
    public void TuiLoadsTheSampleCatalogShowsDetailsRunsCustomGridsAndShowsAssertionResults(int width, int height)
    {
        string target = TestWorkbenchHostTests.SampleTarget;
        string output = Path.Combine(Path.GetTempPath(), "wb-tui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            using var driver = new DriverScope((ushort)width,(ushort)height);
            using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = Path.Combine(output, "preferences.json") });
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            string[] details = [], result = [], results = [];
            string successRun = "";
            Task worker = Task.Run(async () =>
            {
                try
                {
                    var main = await WaitFor(bridge, () => app.Project);
                    await bridge.Invoke(() => Command(main, WorkbenchDialog.Load));
                    var list = await WaitFor(bridge, () => !main.Busy ? Children(main).OfType<ScenarioList>().SingleOrDefault(s => s.Strings.Count >= 9) : null);
                    await bridge.Invoke(() => Capture("loaded",width,height));
                    // Details of the focused (first) scenario in the Details pane: description, grid range, steps and assertions.
                    await bridge.Invoke(() =>
                    {
                        Command(main, WorkbenchDialog.Details); Assert.Same(main.DetailPane, main.current);
                        details = [main.DetailTitle, .. Enumerable.Range(0, main.DetailPane.range).Select(i => main.DetailPane.GetText(i, 300))];
                        main.TestList.Select();
                    });
                    await bridge.Invoke(() =>
                    {
                        var groups = main.Tree;
                        groups.FocusItemNum(Enumerable.Range(0,groups.range).Single(i => groups.GetText(i,200).Contains("status (")));
                        var search = main.SearchBox;
                        search.SetData("context"); Command(main,WorkbenchDialog.SelectGroup);
                        Assert.Equal(1,list.range); Capture("group-filtered",width,height);
                        Command(main,WorkbenchDialog.ClearSelection);
                        groups.Select(); Key(main,Keys.kbEnter); Assert.Empty(list.MarkedIds);
                        list.Select(); Key(main,Keys.kbSpace); Assert.Contains("sample/status/context",list.MarkedIds);
                        var click = new TEvent { What=Events.evMouseDown }; click.mouse.doubleClick=true;
                        click.mouse.where=list.MakeGlobal(new TPoint(1,0)); click.mouse.buttons=(byte)Events.mbLeftButton;
                        list.HandleEvent(ref click); Assert.Empty(list.MarkedIds);
                        Key(main,Keys.kbEnter); Assert.Contains("sample/status/context",list.MarkedIds);
                        Key(main,Keys.kbTab); Assert.NotSame(list,main.current);
                        Key(main,Keys.kbShiftTab); Assert.Same(list,main.current);
                        Capture("marked",width,height);
                    });
                    await RunWith(bridge, app, main, 1u | 4u, "64", "20"); // 80x25 and the custom 64x20, chosen in Run Configuration
                    await WaitFor(bridge, () => ScreenText().Contains("Now        sample/") ? "running" : null);
                    await bridge.Invoke(() => Capture("progress",width,height));
                    await WaitFor(bridge, () => ScreenText().Contains("Finished:") ? "done" : null);
                    successRun = Directory.GetDirectories(output,"run-*").Single();
                    await bridge.Invoke(() => Command(main, WorkbenchDialog.Results));
                    var listDialog = await WaitFor(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                    // Enter on an execution inspects it in place (Checkpoints and Details panes); no modal dialog opens.
                    await bridge.Invoke(() =>
                    {
                        results = listDialog.ExecutionRows; Capture("success-results",width,height);
                        listDialog.ExecutionsList.FocusItemNum(Array.FindIndex(results, l => l.Contains("64x20")));
                        Key(listDialog, Keys.kbEnter);
                        Assert.Same(listDialog.CheckpointList, listDialog.current); Assert.Same(listDialog, app.DeskTop.current);
                        result = [.. listDialog.InspectorRows, .. listDialog.CheckpointRows];
                        Key(listDialog, Keys.kbEsc);
                    });
                    Task advanced = bridge.Invoke(() => Command(main,WorkbenchDialog.Advanced));
                    var settings = await WaitFor(bridge, () => app.DeskTop.TopView() is TDialog d && !ReferenceEquals(d, main) ? d : null);
                    await bridge.Invoke(() => { Capture("advanced",width,height); Key(settings,Keys.kbEsc); }); await advanced;
                    Task capabilities = bridge.Invoke(() => Command(main,WorkbenchDialog.Capabilities));
                    var modes = await WaitFor(bridge, () => app.DeskTop.TopView() is TDialog d && !ReferenceEquals(d, main) ? d : null);
                    await bridge.Invoke(() => { Capture("unavailable-modes",width,height); Key(modes,Keys.kbEsc); }); await capabilities;
                    await bridge.Invoke(() =>
                    {
                        Command(main,WorkbenchDialog.ClearSelection);
                        list.Toggle("selftest/failure/wrong-expectations");
                    });
                    await RunWith(bridge, app, main, 1u, "64", "20");
                    await WaitFor(bridge, () => ScreenText().Contains("Finished:") ? "done" : null);
                    await bridge.Invoke(() => Command(main,WorkbenchDialog.Results));
                    var failedList = await WaitFor(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                    await bridge.Invoke(() =>
                    {
                        Command(failedList,WorkbenchResultsDialog.ToggleFailed); Assert.Single(failedList.ExecutionRows); Capture("failed-results",width,height);
                        // Enter selects the failed checkpoint; its assertions show the recorded expected and actual values.
                        Key(failedList,Keys.kbEnter);
                        var lines = failedList.InspectorRows; Assert.Contains(lines,l => l.Contains("expected")); Assert.Contains(lines,l => l.Contains("FAIL"));
                        Capture("failed-details",width,height); Key(failedList,Keys.kbEsc);
                    });
                    foreach (string failureRun in Directory.GetDirectories(output,"run-*").Where(r => r != successRun)) PreserveRun(failureRun,$"failure-{width}x{height}");
                    await bridge.Invoke(() =>
                    {
                        Command(main,WorkbenchDialog.ClearSelection); list.Toggle("selftest/failure/blocked-ui");
                        Command(main,WorkbenchDialog.Run);
                    });
                    await WaitFor(bridge, () => ScreenText().Contains("checkpoint initial") ? "running" : null);
                    await bridge.Invoke(() => { Command(main,WorkbenchDialog.CancelRun); Capture("cancelling",width,height); });
                    await WaitFor(bridge, () => ScreenText().Contains("CANCELLED") ? "done" : null);
                    string cancelledRun = Directory.GetDirectories(output,"run-*").OrderByDescending(Directory.GetCreationTimeUtc).First();
                    using (var run = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(cancelledRun,"run.json"))))
                    {
                        var execution = run.RootElement.GetProperty("results")[0];
                        Assert.Equal("Aborted",execution.GetProperty("outcome").GetString());
                        Assert.True(execution.GetProperty("cleanupSucceeded").GetBoolean());
                        // TargetExited records graceful protocol exit; cancellation can terminate the owned process.
                        Assert.Empty(System.Diagnostics.Process.GetProcessesByName("TSharpWorkbench.SampleTarget"));
                    }
                    PreserveRun(cancelledRun,$"cancelled-{width}x{height}");
                    await bridge.Invoke(() => Command(main,WorkbenchDialog.Load));
                    await WaitFor(bridge, () => !main.Busy && list.Strings.Count >= 9 ? "loaded" : null);
                    await bridge.Invoke(() => Assert.Contains("selftest/failure/blocked-ui",list.MarkedIds));
                    // Changing the executable in Project Properties states the consequence inline before OK.
                    string otherTarget = Path.Combine(output,"other-target.exe"); File.WriteAllBytes(otherTarget, []);
                    Task changeTarget = bridge.Invoke(() => Command(main,WorkbenchDialog.Advanced));
                    var changeSettings = await WaitFor(bridge, () => app.DeskTop.TopView() as WorkbenchPropertiesDialog);
                    await bridge.Invoke(() =>
                    {
                        changeSettings.ExecutableField.SetData(otherTarget);
                        var nothing = new TEvent { What = Events.evNothing }; changeSettings.HandleEvent(ref nothing);
                        Assert.Contains("clears the 1 marked scenario", changeSettings.NoticeText);
                        Command(changeSettings,Views.cmOK);
                    });
                    await changeTarget;
                    await WaitFor(bridge, () => !main.Busy ? "settled" : null);
                    await bridge.Invoke(() =>
                    {
                        Assert.Empty(list.MarkedIds); Assert.Empty(list.Strings);
                        Capture("missing-target",width,height);
                    });
                    await bridge.Invoke(() => { main.CloseApproved(); Command(app, Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = ex;
                    await bridge.Invoke(() => { app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); });
                }
            });
            int code = AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(10)), "TUI worker did not finish.");
            Assert.Null(failure); Assert.Equal(0, code);

            Assert.Contains(details, l => l.Contains("sample/dialog/basic"));
            Assert.Contains(details, l => l.Contains("grids 60x20..200x100"));
            Assert.Contains(details, l => l.Contains("assert input-focused"));
            Assert.Equal(2, results.Length);
            Assert.All(results, l => Assert.Contains(" +  Passed", l));
            Assert.Contains(result, l => l.StartsWith("Result     Passed", StringComparison.Ordinal));
            Assert.Contains(result, l => l.StartsWith("Report", StringComparison.Ordinal));
            Assert.Contains(result, l => l.StartsWith("desktop-status", StringComparison.Ordinal) && l.Contains("passed"));
            string run = successRun;
            Assert.True(File.Exists(Path.Combine(run, "sample-status-context--note-status--64x20.png")));
            Assert.True(File.Exists(Path.Combine(run, "report.md")));
            Assert.Equal("80x25", WorkbenchPreferences.Load(Path.Combine(output, "preferences.json")).Size);
            PreserveRun(run,$"success-{width}x{height}");
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }

    // Opens Run Configuration, chooses the target screen sizes there (80x25 = 1, 140x40 = 2, Custom = 4) and runs.
    private static async Task RunWith(Bridge bridge, WorkbenchApp app, WorkbenchDialog main, uint sizes, string customWidth, string customHeight)
    {
        Task configuring = bridge.Invoke(() => { var ev = new TEvent { What = Events.evCommand }; ev.message.command = WorkbenchDialog.Run; main.HandleEvent(ref ev); });
        var dialog = await WaitFor(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
        await bridge.Invoke(() =>
        {
            dialog.Sizes.value = sizes; dialog.Sizes.DrawView(); dialog.CustomSizes.SetData(customWidth + "x" + customHeight);
            Command(dialog, Views.cmOK);
        });
        await configuring;
    }

    internal static void Capture(string name,int width,int height)
    {
        if (Environment.GetEnvironmentVariable("TSWB_WORKFLOW_EVIDENCE") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        var screen = ScreenSnapshot.CopyRoot();
        string stem = Path.Combine(root,$"{name}-{width}x{height}");
        File.WriteAllBytes(stem+".png",LogicalPng.Encode(screen));
        File.WriteAllBytes(stem+".tvs1",screen.Serialize());
        File.WriteAllText(stem+".json",System.Text.Json.JsonSerializer.Serialize(new { name, width=screen.Width,height=screen.Height,captureLayer="logical",driver="NullDriver",renderer=LogicalPng.Renderer }));
    }
    private static void PreserveRun(string run,string name)
    {
        if (Environment.GetEnvironmentVariable("TSWB_WORKFLOW_EVIDENCE") is not { Length: > 0 } root) return;
        // Each execution's artifacts stay in one flat run folder.
        string destination = Path.Combine(Path.GetDirectoryName(root)!,Path.GetFileName(root)+"-"+name);
        Directory.CreateDirectory(destination);
        foreach(string file in Directory.GetFiles(run)) File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),true);
    }

    private static string[] Lines(TGroup dialog)
    {
        var list = Children(dialog).OfType<TListBox>().Single();
        return Enumerable.Range(0, list.range).Select(i => list.GetText(i, 300)).ToArray();
    }

    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break;
        TView first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next)
        { yield return item; if (item.Next == first) break; }
    }

    private static string ScreenText()
    {
        var snapshot = ScreenSnapshot.CopyRoot();
        return new string(Enumerable.Range(0, snapshot.Width * snapshot.Height).Select(i => snapshot[i % snapshot.Width, i / snapshot.Width].Character).ToArray());
    }

    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command;
        if(command==WorkbenchDialog.Run && (view is WorkbenchDialog w && w.TestList.MarkedIds.Count>0 || view is WorkbenchApp a && a.Project?.TestList.MarkedIds.Count>0))
        {var accept=new TEvent{What=Events.evCommand};accept.message.command=Views.cmOK;view.PutEvent(ref accept);}
        view.HandleEvent(ref ev); }
    private static void Key(TView view, ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); view.HandleEvent(ref ev); }

    private static async Task<T> WaitFor<T>(Bridge bridge, Func<T?> predicate) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
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
