using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchFinalIntegrationTests
{
    [TestWorkbenchCommanderTests.CommanderTheory]
    [InlineData(80,25)]
    [InlineData(140,40)]
    public void FinalDialogAndCommanderDispatch(int width,int height)
    {
        string target=Environment.GetEnvironmentVariable("TSWB_COMMANDER_TARGET")!;
        string root=Environment.GetEnvironmentVariable("TSWB_COMMANDER_EXTERNAL_EVIDENCE")!;
        string output=Path.Combine(root,"..","executions-final",$"host-{width}");Directory.CreateDirectory(output);
        string store=Environment.GetEnvironmentVariable("TSWB_COMMANDER_EXTERNAL_BASELINES")!;
        using var driver=new DriverScope((ushort)width,(ushort)height);
        using var app=new WorkbenchApp(target,output,new(target,output,null,"80x25"){StoragePath=Path.Combine(root,$"prefs-{width}.json"),SelectedIds=["commander/menu/f10-open-close"]});
        var bridge=new Bridge();app.DeskTop!.Insert(bridge);Exception? failure=null;
        Task worker=Task.Run(async()=>
        {
            try
            {
                var main=await Wait(bridge,()=>app.Project is {Busy:false} p&&p.TestList.Strings.Count>100?p:null);
                string fingerprint="";await bridge.Invoke(()=>fingerprint=main.CaptureProject().Fingerprint());
                // One Run Configuration dialog; driver, evidence, sizes and problems are edited and shown in place.
                Task configuring=bridge.Invoke(()=>Key(app,Keys.kbF5));var dialog=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                await bridge.Invoke(()=>{Capture("functional-custom-disabled");Assert.Equal(["~R~un","Cancel"],Children(dialog).OfType<TButton>().OrderBy(b=>b.origin.x).Select(b=>b.Title).ToArray());Assert.True(dialog.CustomSizes.GetState(Views.sfDisabled));
                    dialog.Mode.Focus();Key(dialog.Mode,Keys.kbDown);Capture("visual-missing-manifest");Key(dialog.Mode,Keys.kbDown);Capture("native-driver");
                    Assert.True(dialog.Mode.ButtonState(2));Assert.Equal(5,dialog.NativeDrivers.Strings.Count);dialog.NativeDrivers.Focus();Key(dialog.NativeDrivers,Keys.kbDown);dialog.HandleEvent(ref Nothing);
                    Assert.Contains("conhost",string.Join("\n",dialog.NativeDrivers.Strings));Capture("console-selected");
                    dialog.Sizes.value=7;dialog.Sizes.DrawView();dialog.CustomSizes.SetData("1");dialog.HandleEvent(ref Nothing);Assert.False(dialog.CustomSizes.GetState(Views.sfDisabled));
                    Assert.True(dialog.RunButton.GetState(Views.sfDisabled));Assert.Contains("! Custom size '1'",dialog.Inline);Capture("invalid-custom-summary");
                    Assert.False(dialog.Valid(Views.cmOK));Assert.Same(dialog,app.DeskTop!.TopView());
                    dialog.CustomSizes.SetData("100x30");dialog.Sizes.value=1;dialog.Sizes.DrawView();main.TestList.Toggle("commander/input/dialog-mouse");dialog.HandleEvent(ref Nothing);
                    Assert.Contains(dialog.Problems,p=>p.Area==RunArea.Scope&&p.Text.Contains("cannot run"));Capture("unsupported-mark-warning");
                    main.TestList.Toggle("commander/input/dialog-mouse");dialog.Scope.Focus();Key(dialog.Scope,Keys.kbDown);Capture("current-group-summary");Key(dialog.Scope,Keys.kbDown);Capture("all-eligible-summary");
                    dialog.Scope.SetData(0);dialog.Mode.SetData(1);dialog.HandleEvent(ref Nothing);Capture("visual-without-store");
                    dialog.Summary.Select();Key(dialog.Summary,Keys.kbPgDn);Assert.Same(dialog.Summary,dialog.current);Capture("summary-scrolled");Key(dialog,Keys.kbEsc);});await configuring;
                await bridge.Invoke(()=>Assert.Equal(fingerprint,main.CaptureProject().Fingerprint()));
                // The baseline store is a project property (Project Properties), never a Run Configuration editor.
                Task properties=bridge.Invoke(()=>Command(main,WorkbenchDialog.Advanced));var props=await Wait(bridge,()=>Modal("Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(()=>{props.StoreField.SetData(store);Command(props,Views.cmOK);});await properties;
                // Six representative executions only at compact host; larger host tests the same UI without repeating native matrices.
                if(width==80&&Environment.GetEnvironmentVariable("TSWB_COMMANDER_EXTERNAL_SKIP_DISPATCH")!="1")
                {
                    await Run("functional","NullDriver","commander/menu/f10-open-close");
                    await Run("visual","NullDriver","commander/input/dialog-mouse");
                    foreach(string mode in new[]{"Win32ConsoleDriver","SDLDriver","SDLGpuDriver","SDLGpuDriver-D3D12Fallback"})await Run("rendering",mode,"commander/menu/f10-open-close");
                }
                await bridge.Invoke(()=>{main.CloseApproved();Command(app,Views.cmQuit);});

                async Task Run(string type,string mode,string id)
                {
                    await bridge.Invoke(()=>{main.TestList.ClearMarks();main.TestList.Toggle(id);});
                    Task opening=bridge.Invoke(()=>Command(app,WorkbenchDialog.Run));var run=await Wait(bridge,()=>Modal(WorkbenchRunDialog.DialogTitle) as WorkbenchRunDialog);
                    await bridge.Invoke(()=>{run.Mode.SetData(type=="functional"?0:type=="visual"?1:2);run.HandleEvent(ref Nothing);
                        if(type=="rendering"){run.NativeDrivers.SetData(run.NativeDrivers.Strings.IndexOf(WorkbenchCapabilities.NativeChoice(mode)));run.HandleEvent(ref Nothing);}});
                    string[] before=Directory.GetDirectories(output,"run-*");
                    await bridge.Invoke(()=>{Capture(type+"-"+mode+"-ready");Assert.Empty(run.Problems);Command(run,Views.cmOK);});await opening;
                    await bridge.Invoke(()=>{Capture(type+"-"+mode+"-progress");Assert.Contains(Children(app.DeskTop),v=>v is WorkbenchRunMonitor&&(v.state&Views.sfModal)==0);});
                    await Wait(bridge,()=>!main.Busy?"finished":null);
                    string directory=Directory.GetDirectories(output,"run-*").Except(before).Single();var batch=WorkbenchRunHistory.Load(Path.Combine(directory,"run.json")).Batch;
                    var result=Assert.Single(batch.Results);Assert.Equal("Passed",result.Outcome);Assert.True(result.CleanupSucceeded);Assert.True(result.FixtureIntegrityVerified);Assert.Equal(mode,batch.Driver);
                    if(type=="visual"){Assert.Equal(1,batch.Visual!.Count(VisualStatuses.Match));Assert.True(batch.Visual.Passed);}
                    await bridge.Invoke(()=>{Command(app,WorkbenchDialog.Results);Capture(type+"-"+mode+"-results");var results=Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Single();Assert.Equal(0,results.state&Views.sfModal);Key(results,Keys.kbEsc);});
                    string saved=Path.Combine(root,"final-preferences.twproj");await bridge.Invoke(()=>{var project=main.SessionProject();project.Save(saved);Assert.Equal(project.Fingerprint(),WorkbenchProject.Load(saved).Fingerprint());});
                }
            }
            catch(Exception ex){failure=ex;await bridge.Invoke(()=>{app.DeskTop.TopView()?.EndModal(Views.cmCancel);app.EndModal(Views.cmQuit);});}
        });
        int code=AppLifecycleGuard.Run(app);Assert.True(worker.Wait(TimeSpan.FromSeconds(10)));Assert.Null(failure);Assert.Equal(0,code);
        TDialog? Modal(string title)=>app.DeskTop.TopView() is TDialog d&&d.title==title?d:null;
        void Capture(string name)
        {
            app.StatusLine?.Update();var screen=ScreenSnapshot.CopyRoot();string stem=Path.Combine(root,$"{name}-{width}x{height}");
            File.WriteAllBytes(stem+".png",LogicalPng.Encode(screen));File.WriteAllBytes(stem+".tvs1",screen.Serialize());File.WriteAllText(stem+".json",JsonSerializer.Serialize(new{name,width,height,captureLayer="logical",driver="NullDriver",source="actual Workbench event loop",target}));
        }
    }
    private static TEvent Nothing=new(){What=Events.evNothing};
    private static void Click(TView view)
    {
        var point=view.MakeGlobal(new(3,0));var up=new TEvent{What=Events.evMouseUp};up.mouse.where=point;view.PutEvent(ref up);
        var down=new TEvent{What=Events.evMouseDown};down.mouse.where=point;down.mouse.buttons=1;view.HandleEvent(ref down);
    }
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
