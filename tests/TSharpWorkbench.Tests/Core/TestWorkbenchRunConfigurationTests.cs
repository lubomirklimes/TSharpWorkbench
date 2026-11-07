using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchRunConfigurationTests
{
    [Fact]
    public void OldProjectsDefaultAndRunSnapshotsDoNotExposeMutableState()
    {
        var project=new WorkbenchProject{Target="sample.exe",SelectedIds=["sample"],ScreenSizes=["80x25"]};
        var descriptor=new ScenarioDescriptor("sample","Sample","description","group",ScenarioGrid.Standard,[new(ScenarioStep.Checkpoint,"initial")]);
        var snapshot=new WorkbenchRunRequest(project,[descriptor],"build");
        project.ScreenSizes[0]="140x40";snapshot.Project.SelectedIds[0]="changed";snapshot.Scenarios[0].Steps[0]=new(ScenarioStep.Key,"kbEsc");
        Assert.Equal("80x25",snapshot.Options().Sizes![0]);Assert.Equal("sample",snapshot.Project.SelectedIds[0]);Assert.Equal(ScenarioStep.Checkpoint,snapshot.Scenarios[0].Steps[0].Kind);
        Assert.Null(snapshot.Options().Baselines);
        Assert.Equal("functional",JsonSerializer.Deserialize<WorkbenchProject>("{}")!.TestType);
        Assert.Throws<ArgumentException>(()=>(project with{TestType="invalid"}).Validate());
    }

    // Workbench grids 120x36 (supported minimum) and 140x40; target grids (80x25, custom) are chosen in the dialog.
    [Theory]
    [InlineData(120,36)]
    [InlineData(140,40)]
    public void RunConfigurationAndModelessWorkflow(int width,int height)
    {
        string root=Environment.GetEnvironmentVariable("TSWB_COMMANDER_GPU_UX_EVIDENCE")??Path.Combine(Path.GetTempPath(),"wb-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string output=Path.Combine(Path.GetDirectoryName(root)!,"executions-"+Path.GetFileName(root),$"session-{width}");Directory.CreateDirectory(output);
        string target=TestWorkbenchHostTests.SampleTarget;
        using var driver=new DriverScope((ushort)width,(ushort)height);
        using var app=new WorkbenchApp(target,output,new(target,output,null,"80x25"){StoragePath=Path.Combine(root,$"prefs-{width}.json"),SelectedIds=["sample/status/context"]});
        var bridge=new Bridge();app.DeskTop!.Insert(bridge);Exception? failure=null;
        Task worker=Task.Run(async()=>
        {
            try
            {
                var main=await Wait(bridge,()=>app.Project is {Busy:false} p&&p.TestList.Strings.Count>9?p:null);
                string before="";await bridge.Invoke(()=>{before=main.CaptureProject().Fingerprint();Capture("workspace");});
                Task configuring=bridge.Invoke(()=>Key(app,Keys.kbF5));var dialog=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                await bridge.Invoke(()=>
                {
                    Capture("run-configuration");Assert.Equal(0,main.state&Views.sfModal);Assert.Same(dialog.RunButton,dialog.current);
                    var type=dialog.Mode;type.Focus();Key(type,Keys.kbDown);Assert.Equal(1u,type.value);Key(type,Keys.kbUp);Assert.Equal(0u,type.value);
                    var check=dialog.Sizes;check.Focus();Key(check,Keys.kbSpace);Assert.Equal(0u,check.value);Key(check,Keys.kbSpace);Assert.Equal(1u,check.value);
                    // Limits are edited in place (no nested Run Options); Esc discards them with the rest.
                    dialog.StepTimeout.SetData("2000");Key(dialog,Keys.kbTab);
                });
                await bridge.Invoke(()=>Key(dialog,Keys.kbEsc));await configuring;
                await bridge.Invoke(()=>{Assert.Equal(before,main.CaptureProject().Fingerprint());Assert.Null(main.SessionRun.Timeout);});
                configuring=bridge.Invoke(()=>Raw(app,WorkbenchDialog.Run));dialog=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                await bridge.Invoke(()=>{dialog.Sizes.value=4;dialog.Sizes.DrawView();dialog.CustomSizes.SetData("64x20");Key(dialog,Keys.kbTab);Capture("custom-grid");Command(dialog,Views.cmOK);});await configuring;
                await bridge.Invoke(()=>{Assert.True(main.Busy);var running=Children(app.DeskTop).OfType<WorkbenchRunMonitor>().Single();Assert.Equal(0,running.state&Views.sfModal);Assert.Same(main,app.DeskTop.current);Capture("running");Command(app,WorkbenchDialog.Results);Assert.Contains("Running",Text());Capture("live-results");Command(app,WorkbenchApp.ProjectWindow);Assert.Same(main,app.DeskTop.current);main.TestList.Toggle("sample/dialog/basic");Capture("browse-during-run");});
                await Wait(bridge,()=>!main.Busy?"done":null);
                string run=Directory.GetDirectories(output,"run-*").Single();var loaded=WorkbenchRunHistory.Load(Path.Combine(run,"run.json"));Assert.Single(loaded.Batch.Results);Assert.Equal("64x20",loaded.Batch.Results[0].Size);Assert.Equal("Passed",loaded.Batch.Results[0].Outcome);Assert.Null(loaded.Batch.Visual);
                // Running uses the session configuration only: the project defaults keep 80x25 (marks changed, so dirty for that reason).
                await bridge.Invoke(()=>{Assert.Equal("64x20",main.SessionRun.ScreenSizes.Single());Assert.Equal("80x25",main.CaptureProject().ScreenSizes.Single());Assert.DoesNotContain(main.ChangedGroups(),g=>g.StartsWith("run defaults",StringComparison.Ordinal));main.TestList.Toggle("sample/dialog/basic");Command(app,WorkbenchDialog.Results);});
                var results=await Wait(bridge,()=>Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await bridge.Invoke(()=>{Assert.False(typeof(TDialog).IsAssignableFrom(results.GetType()));Assert.Equal(0,results.state&Views.sfModal);Capture("results");Command(app,WorkbenchApp.ProjectWindow);Assert.Same(main,app.DeskTop.current);results.Select();results.Locate(new(1,0,width-1,height-2));Capture("results-resized");Key(results,Keys.kbEsc);Assert.DoesNotContain(results,Children(app.DeskTop));});
                // A small visual store owned exclusively by this test. The approved production stores are read-only.
                string store=Path.Combine(output,"store","visual-baselines.json");Directory.CreateDirectory(Path.GetDirectoryName(store)!);
                File.WriteAllText(store,JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat,[],[new("sample/status/context","desktop-status",["64x20"],"status proof")]),ScenarioRunner.Json));
                await bridge.Invoke(()=>{main.TestList.ClearMarks();main.TestList.Toggle("sample/status/context");});
                // The baseline store is a project property; Run Configuration shows its state.
                Task properties=bridge.Invoke(()=>Raw(main,WorkbenchDialog.Advanced));var props=await Wait(bridge,()=>Modal("Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(()=>{props.StoreField.SetData(store);Command(props,Views.cmOK);});await properties;
                configuring=bridge.Invoke(()=>Raw(app,WorkbenchDialog.Run));dialog=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                await bridge.Invoke(()=>{dialog.Mode.value=1;dialog.Mode.DrawView();Key(dialog,Keys.kbTab);Assert.Contains("1 gated checkpoint: 0 approved, 1 missing",dialog.Inline);Capture("visual-configuration");Command(dialog,Views.cmOK);});await configuring;
                await Wait(bridge,()=>!main.Busy?"done":null);
                string missing=NewestRun();var missingBatch=WorkbenchRunHistory.Load(Path.Combine(missing,"run.json")).Batch;Assert.Equal(1,missingBatch.Visual!.Count(VisualStatuses.BaselineMissing));
                string key=VisualBaselineSet.Key("sample/status/context","desktop-status","64x20");
                // Explicit test-owner setup using the existing API; this never touches a production store.
                VisualBaselineSet.Load(store).Approve(missing,[key],"approve-1",false);
                await bridge.Invoke(()=>{Command(app,WorkbenchDialog.Results);Capture("missing-baseline-results");Key(Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Single(),Keys.kbEsc);});
                await RunAgain(true);string match=NewestRun();Assert.Equal(1,WorkbenchRunHistory.Load(Path.Combine(match,"run.json")).Batch.Visual!.Count(VisualStatuses.Match));Assert.True(File.Exists(Path.Combine(match,key+".candidate.json")));
                await bridge.Invoke(()=>Command(app,WorkbenchDialog.Results));
                // Compare acts on the Results selection (the gated checkpoint) without a picker.
                await bridge.Invoke(()=>Command(app,WorkbenchDialog.CompareResult));
                var comparison=await Wait(bridge,()=>Children(app.DeskTop).OfType<WorkbenchComparisonWindow>().SingleOrDefault());
                await bridge.Invoke(()=>{Capture("logical-comparison");Command(comparison,0x7d71);Capture("comparison-expected");Command(comparison,0x7d72);Capture("comparison-actual");comparison.Zoom();Capture("comparison-zoomed");Key(comparison,Keys.kbEsc);});
                // Deliberate mismatch in disposable store; metadata hash updated so this is a cell difference, not corruption.
                var set=VisualBaselineSet.Load(store);string approved=set.ApprovedCells(key);var screen=ScreenSnapshot.Deserialize(File.ReadAllBytes(approved));var cells=Enumerable.Range(0,screen.Width*screen.Height).Select(i=>screen[i%screen.Width,i/screen.Width]).ToArray();cells[0]=new('!',cells[0].Attr);byte[] changed=new ScreenSnapshot(screen.Width,screen.Height,cells).Serialize();File.WriteAllBytes(approved,changed);
                string meta=set.ApprovedMetadata(key);var file=JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllText(meta),ScenarioRunner.Json)!;File.WriteAllText(meta,JsonSerializer.Serialize(file with{CellsSha256=ScreenSnapshot.Hash(changed)},ScenarioRunner.Json));
                await RunAgain();Assert.Equal(1,WorkbenchRunHistory.Load(Path.Combine(NewestRun(),"run.json")).Batch.Visual!.Count(VisualStatuses.Mismatch));
                await bridge.Invoke(()=>{Command(app,WorkbenchDialog.Results);Command(Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Single(),WorkbenchResultsDialog.ToggleFailed);Capture("mismatch-results");});
                await bridge.Invoke(()=>Command(app,WorkbenchDialog.CompareResult));
                comparison=await Wait(bridge,()=>Children(app.DeskTop).OfType<WorkbenchComparisonWindow>().SingleOrDefault());await bridge.Invoke(()=>{Command(comparison,0x7d74);Capture("difference-cell");Key(comparison,Keys.kbEsc);});
                // Baseline replacement is a separate, explicit owner decision. Cancelling writes nothing.
                string approvedHash=ScreenSnapshot.Hash(File.ReadAllBytes(approved));
                // One candidate review dialog; its mode (Replace) follows the store. Esc cancels without writing.
                Task approval=bridge.Invoke(()=>Command(app,WorkbenchDialog.ReplaceBaseline));
                var review=await Wait(bridge,()=>Modal(WorkbenchCandidateDialog.ReplaceTitle));await bridge.Invoke(()=>{Capture("candidate-review");Key(review,Keys.kbEsc);});await approval;
                Assert.Equal(approvedHash,ScreenSnapshot.Hash(File.ReadAllBytes(approved)));
                // A real visual execution without relevant gated captures never has a visual PASS.
                await bridge.Invoke(()=>{main.TestList.ClearMarks();main.TestList.Toggle("sample/dialog/basic");});
                await RunAgain();var noGate=WorkbenchRunHistory.Load(Path.Combine(NewestRun(),"run.json")).Batch;Assert.False(noGate.Visual!.Passed);Assert.All(noGate.Visual.Entries,e=>Assert.Equal(VisualStatuses.NotRun,e.Status));
                await bridge.Invoke(()=>{Command(app,WorkbenchDialog.Results);Assert.Contains("no gated captures",Text());Capture("no-gated-captures");});
                // Native mode on the sample target is refused inline before launch: Run stays disabled.
                configuring=bridge.Invoke(()=>Raw(app,WorkbenchDialog.Run));dialog=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                await bridge.Invoke(()=>{dialog.Mode.value=2;dialog.Mode.DrawView();Key(dialog,Keys.kbTab);Assert.True(dialog.RunButton.GetState(Views.sfDisabled));Assert.False(dialog.Valid(Views.cmOK));Capture("unsupported-native");});
                await bridge.Invoke(()=>Key(dialog,Keys.kbEsc));await configuring;
                // Cancellation retains modeless ownership until the bounded runner cleanup completes; Close never cancels.
                await bridge.Invoke(()=>{main.TestList.ClearMarks();main.TestList.Toggle("selftest/failure/blocked-ui");});
                configuring=bridge.Invoke(()=>Raw(app,WorkbenchDialog.Run));dialog=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                await bridge.Invoke(()=>{dialog.Mode.value=0;dialog.Mode.DrawView();Key(dialog,Keys.kbTab);Command(dialog,Views.cmOK);});await configuring;
                await Wait(bridge,()=>Text().Contains("checkpoint initial")?"checkpoint":null);
                await bridge.Invoke(()=>{var running=Children(app.DeskTop).OfType<WorkbenchRunMonitor>().Single();running.Close();Assert.True(main.Busy);Assert.Contains(running,Children(app.DeskTop));Command(running,WorkbenchDialog.CancelRun);Assert.True(main.Busy);Capture("cancelling-cleanup");});
                await Wait(bridge,()=>!main.Busy?"done":null);var cancelled=WorkbenchRunHistory.Load(Path.Combine(NewestRun(),"run.json")).Batch;Assert.Equal("Aborted",cancelled.Results.Single().Outcome);Assert.True(cancelled.Results.Single().CleanupSucceeded);
                await bridge.Invoke(()=>{Capture("cancelled");main.CloseApproved();Command(app,Views.cmQuit);});

                async Task RunAgain(bool candidates=false)
                {
                    Task task=bridge.Invoke(()=>Raw(app,WorkbenchDialog.Run));var d=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                    await bridge.Invoke(()=>{if(candidates){d.VisualPolicy.value=1;d.VisualPolicy.DrawView();Key(d,Keys.kbTab);Capture("candidate-options");}Command(d,Views.cmOK);});await task;await Wait(bridge,()=>!main.Busy?"done":null);
                }
                string NewestRun()=>Directory.GetDirectories(output,"run-*").OrderByDescending(Directory.GetCreationTimeUtc).First();
            }
            catch(Exception ex){failure=ex;await bridge.Invoke(()=>{app.DeskTop.TopView()?.EndModal(Views.cmCancel);app.EndModal(Views.cmQuit);});}
        });
        int code=AppLifecycleGuard.Run(app);Assert.True(worker.Wait(TimeSpan.FromSeconds(10)),"worker did not stop");Assert.Null(failure);Assert.Equal(0,code);
        TDialog? Modal(string title)=>app.DeskTop.TopView() is TDialog d&&d.title==title?d:null;
        void Capture(string name)
        {
            app.StatusLine?.Update();var s=ScreenSnapshot.CopyRoot();string stem=Path.Combine(root,$"{name}-{width}x{height}");File.WriteAllBytes(stem+".png",LogicalPng.Encode(s));File.WriteAllBytes(stem+".tvs1",s.Serialize());File.WriteAllText(stem+".json",JsonSerializer.Serialize(new{name,width,height,captureLayer="logical",driver="NullDriver"}));
        }
    }
    private static void Raw(TView view,ushort c){var ev=new TEvent{What=Events.evCommand};ev.message.command=c;view.HandleEvent(ref ev);}
    private static TRadioButtons[] Radios(TGroup group)=>Children(group).OfType<TRadioButtons>().OrderBy(r=>r.origin.y).ThenBy(r=>r.origin.x).ToArray();
    private static string Text(){var s=ScreenSnapshot.CopyRoot();return new string(Enumerable.Range(0,s.Width*s.Height).Select(i=>s[i%s.Width,i/s.Width].Character).ToArray());}
    private static IEnumerable<TView> Children(TGroup group){if(group.last is null)yield break;var first=group.last.Next!;for(var v=first;;v=v.Next!){yield return v;if(v.Next==first)break;}}
    private static void Command(TView view,ushort c){var ev=new TEvent{What=Events.evCommand};ev.message.command=c;view.HandleEvent(ref ev);}
    private static void Key(TView view,ushort k){var ev=new TEvent{What=Events.evKeyDown};ev.keyDown.keyCode=k;ev.keyDown.charScan=new(k);view.HandleEvent(ref ev);}
    private static async Task<T> Wait<T>(Bridge bridge,Func<T?> predicate)where T:class{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));while(true){T? value=null;await bridge.Invoke(()=>value=predicate()).WaitAsync(timeout.Token);if(value is not null)return value;await Task.Delay(20,timeout.Token);}}
    private sealed class Bridge:TView
    {
        private sealed class Work(Action action):IInfo{internal Action Action{get;}=action;}
        internal Bridge():base(new TRect(0,0,0,0)){options=0;state&=unchecked((ushort)~Views.sfVisible);}
        internal Task Invoke(Action action){var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Post(0x7ddd,new Work(()=>{try{action();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}}));return done.Task;}
        public override void HandleEvent(ref TEvent ev){if(ev.What==Events.evCommand&&ev.message.command==0x7ddd&&ev.message.infoPtr is Work w){ClearEvent(ref ev);w.Action();}else base.HandleEvent(ref ev);}
    }
}
