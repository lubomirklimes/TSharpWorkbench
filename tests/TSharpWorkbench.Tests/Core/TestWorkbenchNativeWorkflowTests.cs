using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchNativeWorkflowTests
{
    public sealed class NativeWorkflowAttribute : TheoryAttribute
    {
        public NativeWorkflowAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_TARGET")))
                Skip = "Set TSWB_COMMANDER_NATIVE_TARGET to the current diagnostic Commander apphost and TSWB_COMMANDER_NATIVE_OUTPUT to an artifact root.";
        }
    }

    [NativeWorkflow]
    [InlineData(80, 25, "SDLGpuDriver")]
    [InlineData(140, 40, "SDLGpuDriver")]
    [InlineData(80, 25, "SDLDriver")]
    [InlineData(80, 25, "Win32ConsoleDriver")]
    [InlineData(140, 40, "SDLDriver")]
    [InlineData(140, 40, "Win32ConsoleDriver")]
    public void SelectNativeRunReviewUnsupportedAndCancel(int width, int height, string targetDriver)
    {
        string target = Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_TARGET")!;
        string output = Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_OUTPUT")!;
        string evidence = Path.Combine(output, $"run-ux-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{width}x{height}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidence);
        using var scope = new DriverScope((ushort)width, (ushort)height);
        // Since the product flows are external JSON documents (CommanderExternal.twproj, read-only copy).
        var (external, projectPath) = CommanderExternalCatalog.Project(evidence, target, output, "commander/menu/f10-open-close");
        var project = external with { ScreenSizes = [$"{width}x{height}"] }; project.Save(projectPath); project = WorkbenchProject.Load(projectPath);
        var preferences = new WorkbenchPreferences(target, output, null, $"{width}x{height}") { StoragePath = Path.Combine(evidence, "settings.json") };
        using var app = new WorkbenchApp(target, output, preferences, project, projectPath);
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null; int nativeRows = 0;
        Task worker = Task.Run(async () =>
        {
            try
            {
                var main = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => Text().Contains("Loaded 215 scenarios") ? "loaded" : null);
                await bridge.Invoke(() => { main.Tree.FocusItemNum(0);nativeRows=main.TestList.range;Assert.Equal(main.TestList.Strings.Count,nativeRows);Capture("loaded"); });
                Task choosing = bridge.Invoke(() => Command(main, WorkbenchDialog.SelectMode));
                TDialog modes = await Wait(bridge, () => Modal(WorkbenchRunDialog.DialogTitle));
                // Native driver mode and its driver are chosen in the single Run Configuration dialog.
                await bridge.Invoke(() => { var run=(WorkbenchRunDialog)modes; run.Mode.SetData(2);Key(modes,Keys.kbTab);
                    run.NativeDrivers.SetData(run.NativeDrivers.Strings.IndexOf(WorkbenchCapabilities.NativeChoice(targetDriver)));Key(modes,Keys.kbTab);Assert.Empty(run.Problems); });
                await bridge.Invoke(() => { modes.DrawView();Capture("mode-selection");
                    var summary=((WorkbenchRunDialog)modes).Summary;summary.FocusItemNum(summary.range-1);summary.DrawView();Capture("mode-summary-boundary-output");Command(modes,Views.cmOK); });
                await choosing;
                // The Run Monitor (window 2) and the Results inspector (window 3) replace the legacy texts and modal
                // Result details; the native driver/GPU provenance lines are rows of the execution Overview.
                await Wait(bridge, () => main.Busy && Children(app.DeskTop).OfType<WorkbenchRunMonitor>().Any() ? "running" : null);
                await bridge.Invoke(() => Capture("running"));
                await Wait(bridge, () => !main.Busy ? "done" : null);
                await bridge.Invoke(() => Command(main, WorkbenchDialog.Results));
                var results = await Wait(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await bridge.Invoke(() =>
                {
                    Assert.Contains("1 passed", results.HeaderText); Assert.Equal(0,results.state&Views.sfModal); Capture("native-results");
                    Assert.Equal("Overview", results.InspectorTitle);
                    string[] rows = results.InspectorRows;
                    Assert.Contains(rows, r => r.Contains("driver: "));
                    Assert.Contains(rows, r => r.Contains("cleanup succeeded"));
                    if (NativeExecution.IsGpu(targetDriver))
                    {
                        Assert.Contains(rows, r => r.Contains("gpu.device.name:"));
                        results.Inspector.Select(); results.Inspector.FocusItemNum(Array.FindIndex(rows, r => r.Contains("backend:"))); results.Inspector.DrawView(); Capture("gpu-provenance-details");
                    }
                    Capture("result-details");
                    Key(results, Keys.kbEsc);
                });
                await Wait(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any() ? null : "closed");
                await bridge.Invoke(() => { Assert.Equal(nativeRows,main.TestList.range);if(targetDriver=="Win32ConsoleDriver")Assert.Contains(Enumerable.Range(0,main.TestList.range).Select(i=>main.TestList.GetText(i,200)),text=>text.Contains("Jobs")); });
                await bridge.Invoke(() => main.TestList.Toggle("external/commander/input/dialog-mouse"));
                // An unsupported native selection is an inline Run Configuration problem; Run stays disabled (no "Cannot run" modal).
                Task refusing = bridge.Invoke(() => Command(main, WorkbenchDialog.Run));
                var refusedRun = (WorkbenchRunDialog)await Wait(bridge, () => Modal(WorkbenchRunDialog.DialogTitle));
                await bridge.Invoke(() => { Assert.Contains(refusedRun.Problems, p => p.Text.Contains("cannot run")); Assert.True(refusedRun.RunButton.GetState(Views.sfDisabled)); Capture("unsupported-selection"); Key(refusedRun,Keys.kbEsc); });
                await refusing;
                await bridge.Invoke(() => { Assert.Contains("external/commander/input/dialog-mouse",main.TestList.MarkedIds); main.TestList.Toggle("external/commander/input/dialog-mouse"); });
                // Help > Test modes and drivers replaces the former "Test mode capabilities" list.
                Task unsupported = bridge.Invoke(() => Command(app, WorkbenchApp.TestModes));
                var help = await Wait(bridge, () => app.HelpWindow);
                await bridge.Invoke(() => { Capture("unsupported-console"); Key(help.Viewer, Keys.kbEsc); });
                await unsupported;
                var previousRuns=Directory.GetDirectories(output,"run-*").ToHashSet();
                await bridge.Invoke(() => Command(main,WorkbenchDialog.Run));
                await Wait(bridge, () => Directory.GetDirectories(output,"run-*").Except(previousRuns).Any(dir=>Directory.GetFiles(dir,"*.png").Length>0)?"native checkpoint captured":null);
                await bridge.Invoke(() => { Command(main, WorkbenchDialog.CancelRun); Capture("cancel-requested"); });
                await Wait(bridge, () => !main.Busy ? "done" : null);
                await bridge.Invoke(() => Command(main, WorkbenchDialog.Results));
                results = await Wait(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await bridge.Invoke(() => { Assert.Contains(results.ExecutionRows, r => r.Contains("CANCELLED")); Assert.Contains("CANCELLED", results.HeaderText); Capture("cancelled-results");
                    if(NativeExecution.IsGpu(targetDriver))
                    {
                        string run=Directory.GetDirectories(output,"run-*").Except(previousRuns).Single();
                        using var json=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run,"run.json")));
                        var lifecycle=json.RootElement.GetProperty("nativeLifecycle")[0];
                        Assert.True(lifecycle.GetProperty("gpuCleanupVerified").GetBoolean());
                        Assert.True(lifecycle.GetProperty("fixtureIntegrity").GetBoolean());
                        Assert.DoesNotContain("terminated",lifecycle.GetProperty("stopOutcome").GetString());
                    } Key(results, Keys.kbEsc); });
                await bridge.Invoke(() => { main.CloseApproved(); Command(app, Views.cmQuit); });
            }
            catch (Exception ex)
            {
                failure = ex;
                await bridge.Invoke(() => { app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); });
            }
        });
        int code = AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(10)), "Native Workbench UI worker did not finish.");
        Assert.Null(failure); Assert.Equal(0, code);

        TDialog? Modal(string title) => app.DeskTop.TopView() is TDialog d && d.GetTitle(255) == title ? d : null;
        void Capture(string name)
        {
            var screen = ScreenSnapshot.CopyRoot(); string stem = Path.Combine(evidence, name);
            File.WriteAllBytes(stem + ".png", LogicalPng.Encode(screen));
            File.WriteAllBytes(stem + ".tvs1",screen.Serialize());
            File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new { name, width, height, target, targetDriver, workbenchDriver = "NullDriver",
                captureLayer = "logical", note = "Actual Workbench UI event loop; target native images are in the scenario run folders." }, ScenarioRunner.Json));
        }
    }

    [NativeWorkflow]
    [InlineData(true)]
    public async Task CancelWhileActualCommanderModalIsOpen(bool _)
    {
        string target = Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_TARGET")!;
        string output = Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_OUTPUT")!;
        var runner = new ScenarioRunner();
        var scenario = await CommanderExternalCatalog.SingleAsync(runner, target, "commander/keyboard/copy-rename-here");
        using var cancel = new CancellationTokenSource();
        bool modal = false;
        var batch = await runner.RunAsync(target, [scenario], new RunOptions(output, ["80x25"]) { Driver = "SDLDriver" }, message =>
        {
            if (message.Trim() == "checkpoint copy-here") { modal = true; cancel.Cancel(); }
        }, cancel.Token);
        Assert.True(modal);
        var result = Assert.Single(batch.Results);
        Assert.Equal(ScenarioOutcomes.Aborted, result.Outcome);
        Assert.True(result.CleanupSucceeded); Assert.True(result.FixtureIntegrityVerified);
        Assert.Single(result.Checkpoints); // desktop pixels retained before the modal opened
    }

    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break;
        TView first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0,s.Width*s.Height).Select(i=>s[i%s.Width,i/s.Width].Character).ToArray()); }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command;
        if(command==WorkbenchDialog.Run && (view is WorkbenchDialog w && w.TestList.MarkedIds.Count>0 || view is WorkbenchApp a && a.Project?.TestList.MarkedIds.Count>0))
        {var accept=new TEvent{What=Events.evCommand};accept.message.command=Views.cmOK;view.PutEvent(ref accept);}
        view.HandleEvent(ref ev); }
    private static void Key(TView view, ushort key) { var ev = TestKeys.KeyDown(key); view.HandleEvent(ref ev); }
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true) { T? found = null; await bridge.Invoke(() => found = predicate()).WaitAsync(timeout.Token); if (found is not null) return found; await Task.Delay(10,timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0,0,0,0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd,new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev) { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}

