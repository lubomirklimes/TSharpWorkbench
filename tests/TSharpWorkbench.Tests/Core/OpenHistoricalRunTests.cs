using System.Reflection;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

/// <summary>Results > Open Run drives the real TFileDialog from the configured output root.</summary>
[Collection("NonParallel")]
public sealed class OpenHistoricalRunTests
{
    [Fact]
    public void LoaderRejectsDirectoriesMissingFilesAndArbitraryJson()
    {
        string root=Path.Combine(Path.GetTempPath(),"tsw fix1 loader "+Guid.NewGuid().ToString("N"));
        try
        {
            string runs=Path.Combine(root,"output","runs");Directory.CreateDirectory(Path.Combine(runs,"run-empty"));
            File.WriteAllText(Path.Combine(runs,"notes.json"),"{\"notes\":\"not a run\"}");
            string bogus=Path.Combine(root,"bogus");Directory.CreateDirectory(Path.Combine(bogus,"run.json"));
            string arbitrary=Path.Combine(root,"arbitrary");Directory.CreateDirectory(arbitrary);
            File.WriteAllText(Path.Combine(arbitrary,"run.json"),"{\"notes\":\"not a run\"}");File.WriteAllText(Path.Combine(arbitrary,"report.md"),"# not a run");
            Assert.Contains("not the directory",Assert.Throws<InvalidDataException>(()=>WorkbenchRunHistory.Load(Path.Combine(runs,"run-empty"))).Message);
            Assert.Contains("not the directory",Assert.Throws<InvalidDataException>(()=>WorkbenchRunHistory.Load(Path.Combine(bogus,"run.json"))).Message);
            Assert.Contains("Select run.json",Assert.Throws<InvalidDataException>(()=>WorkbenchRunHistory.Load(Path.Combine(runs,"notes.json"))).Message);
            Assert.Contains("not found",Assert.Throws<InvalidDataException>(()=>WorkbenchRunHistory.Load(Path.Combine(runs,"run-empty","run.json"))).Message);
            // A file named run.json is still parsed as a manifest; the name alone is never sufficient.
            Assert.ThrowsAny<Exception>(()=>WorkbenchRunHistory.Load(Path.Combine(arbitrary,"run.json")));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public async Task OpenRunBrowsesOutputRootNavigatesValidatesAndLoads()
    {
        string root=Path.Combine(Path.GetTempPath(),"tsw fix1 "+Guid.NewGuid().ToString("N"));
        string output=Path.Combine(root,"output"),runs=Path.Combine(output,"runs"),staging=Path.Combine(root,"staging");
        string target=TestWorkbenchHostTests.SampleTarget;
        try
        {
            Directory.CreateDirectory(runs);
            // Real runner output, renamed so run folders do not share the runner's naming scheme.
            var runner=new ScenarioRunner();var catalog=await runner.DiscoverAsync(target);
            var alpha=await runner.RunAsync(target,[catalog.Single(s=>s.Id=="sample/status/context")],["80x25"],staging);
            Assert.Equal(ScenarioOutcomes.Passed,alpha.Results.Single().Outcome);
            var beta=await runner.RunAsync("missing-target.exe",[new ScenarioDescriptor("beta","Beta","description","group",ScenarioGrid.Standard,[new(ScenarioStep.Checkpoint,"initial")])],["80x25"],staging);
            Directory.Move(alpha.RunRoot,Path.Combine(runs,"run-alpha"));Directory.Move(beta.RunRoot,Path.Combine(runs,"run-beta"));
            Directory.CreateDirectory(Path.Combine(runs,"run-empty"));
            File.WriteAllText(Path.Combine(runs,"notes.json"),"{\"notes\":\"not a run\"}");
            string sep=Path.DirectorySeparatorChar.ToString();
            string cwd=Environment.CurrentDirectory;

            // The configured output root is "runs"; it holds run folders, not run.json itself.
            await Drive(runs,async(app,main,bridge)=>
            {
                string before="";await bridge.Invoke(()=>before=main.CaptureProject().Fingerprint());

                // A. Initial location and C. navigation with a stable filter.
                var (opening,picker)=await Open(app,bridge);
                await bridge.Invoke(()=>
                {
                    Assert.Equal(runs+sep,picker.Directory);Assert.Equal("*.json",picker.WildCard);
                    Assert.Equal(new[]{"..","notes.json","run-alpha","run-beta","run-empty"}.Order(),Entries(picker).Order());
                });
                await Enter(bridge,picker,"run-alpha"+sep);await Until(bridge,()=>picker.Directory==Path.Combine(runs,"run-alpha")+sep);
                await bridge.Invoke(()=>{Assert.Equal("*.json",picker.WildCard);Assert.Contains("run.json",Entries(picker));Assert.DoesNotContain("report.md",Entries(picker));});
                await Enter(bridge,picker,".."+sep);await Until(bridge,()=>picker.Directory==runs+sep);
                await bridge.Invoke(()=>Assert.Equal("*.json",picker.WildCard));
                await Enter(bridge,picker,"run-empty"+sep);await Until(bridge,()=>picker.Directory==Path.Combine(runs,"run-empty")+sep);
                await bridge.Invoke(()=>Assert.Equal(new[]{".."},Entries(picker)));
                await Enter(bridge,picker,".."+sep);await Until(bridge,()=>picker.Directory==runs+sep);

                // E. Cancellation leaves results and project untouched.
                await bridge.Invoke(()=>Key(picker,Keys.kbEsc));await opening;
                await bridge.Invoke(()=>{Assert.Null(Modal(app,"Open completed run"));Assert.Empty(History(main));Assert.Equal(before,main.CaptureProject().Fingerprint());});

                // B/D. An unrelated JSON file is visible but cannot be loaded as a run.
                (opening,picker)=await Open(app,bridge);
                await Enter(bridge,picker,"notes.json");await opening;
                await bridge.Invoke(()=>{Assert.Empty(History(main));Assert.Contains("Select run.json",Text());});

                // D. A directory typed by hand navigates instead of being accepted as a manifest.
                (opening,picker)=await Open(app,bridge);
                await bridge.Invoke(()=>{picker.fileName!.SetData(Path.Combine(runs,"run-beta"));Command(picker,Views.cmFileOpen);});
                await Until(bridge,()=>picker.Directory==Path.Combine(runs,"run-beta")+sep);
                await bridge.Invoke(()=>Assert.Same(picker,Modal(app,"Open completed run")));

                // D/F. Selecting run.json loads it through the real history workflow with the absolute path.
                await Enter(bridge,picker,"run.json");await opening;
                await bridge.Invoke(()=>
                {
                    var history=History(main);Assert.Equal(Path.Combine(runs,"run-beta"),Assert.Single(history).RunRoot);
                    Assert.True(Path.IsPathRooted(history[0].RunRoot));
                    Assert.Single(Children(app.DeskTop!).OfType<WorkbenchResultsDialog>());
                });
                (opening,picker)=await Open(app,bridge);
                await Enter(bridge,picker,"run-alpha"+sep);await Until(bridge,()=>picker.Directory==Path.Combine(runs,"run-alpha")+sep);
                await Enter(bridge,picker,"run.json");await opening;
                await bridge.Invoke(()=>
                {
                    var history=History(main);Assert.Equal(2,history.Count);Assert.Equal(Path.Combine(runs,"run-alpha"),history[1].RunRoot);Assert.Equal(1,ResultIndex(main));
                    Assert.Equal(ScenarioOutcomes.Passed,history[1].Results.Single().Outcome);
                    Assert.Contains("sample/status/context",Text());
                });

                // E. Cancelling after a load keeps the loaded selection.
                (opening,picker)=await Open(app,bridge);
                await bridge.Invoke(()=>Command(picker,Views.cmCancel));await opening;
                await bridge.Invoke(()=>{Assert.Equal(2,History(main).Count);Assert.Equal(1,ResultIndex(main));Assert.Equal(before,main.CaptureProject().Fingerprint());});
            });

            // A. Missing output root: start at the nearest existing ancestor; the process directory is never changed.
            await Drive(Path.Combine(output,"missing","deeper"),async(app,main,bridge)=>
            {
                var (opening,picker)=await Open(app,bridge);
                await bridge.Invoke(()=>{Assert.Equal(output+sep,picker.Directory);Assert.Equal(new[]{"..","runs"}.Order(),Entries(picker).Order());});
                await Enter(bridge,picker,"runs"+sep);await Until(bridge,()=>picker.Directory==runs+sep);
                await bridge.Invoke(()=>Key(picker,Keys.kbEsc));await opening;
            });
            Assert.Equal(cwd,Environment.CurrentDirectory);
            Assert.True(File.Exists(Path.Combine(runs,"run-alpha","run.json")));Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(runs,"run-empty")));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}

        async Task Drive(string outputRoot,Func<WorkbenchApp,WorkbenchDialog,Bridge,Task> body)
        {
            using var driver=new DriverScope(80,25);
            using var app=new WorkbenchApp(target,outputRoot,new(target,outputRoot,null,"80x25"){StoragePath=Path.Combine(root,"prefs-"+Guid.NewGuid().ToString("N")+".json")});
            var bridge=new Bridge();app.DeskTop!.Insert(bridge);Exception? failure=null;
            Task worker=Task.Run(async()=>
            {
                try
                {
                    var main=await Wait(bridge,()=>app.Project is {Busy:false} p&&p.TestList.Strings.Count>0?p:null);
                    await body(app,main,bridge);
                    await bridge.Invoke(()=>{main.CloseApproved();Command(app,Views.cmQuit);});
                }
                catch(Exception ex){failure=ex;await bridge.Invoke(()=>{app.DeskTop.TopView()?.EndModal(Views.cmCancel);app.EndModal(Views.cmQuit);});}
            });
            int code=AppLifecycleGuard.Run(app);Assert.True(worker.Wait(TimeSpan.FromSeconds(10)),"worker did not stop");Assert.Null(failure);Assert.Equal(0,code);
        }
    }

    private static async Task<(Task Opening,TFileDialog Picker)> Open(WorkbenchApp app,Bridge bridge)
    {
        Task opening=bridge.Invoke(()=>Command(app,WorkbenchDialog.OpenRun));
        return (opening,await Wait(bridge,()=>Modal(app,"Open completed run") as TFileDialog));
    }
    // Focus a list row (the input line follows it) and press Enter, as a keyboard user would.
    private static Task Enter(Bridge bridge,TFileDialog picker,string entry)=>bridge.Invoke(()=>
    {
        var list=picker.fileList!;int index=Entries(picker).ToList().IndexOf(entry.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(index>=0,entry+" not listed: "+string.Join(", ",Entries(picker)));
        list.Select();list.FocusItem(index);Key(picker,Keys.kbEnter);
    });
    private static string[] Entries(TFileDialog picker)=>Enumerable.Range(0,picker.fileList!.range).Select(i=>picker.fileList.GetText(i,255).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
    private static List<BatchResult> History(WorkbenchDialog main)=>(List<BatchResult>)typeof(WorkbenchDialog).GetField("history",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main)!;
    private static int ResultIndex(WorkbenchDialog main)=>(int)typeof(WorkbenchDialog).GetField("resultIndex",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main)!;
    private static TDialog? Modal(WorkbenchApp app,string title)=>app.DeskTop!.TopView() is TDialog d&&d.title==title?d:null;
    private static string Text(){var s=ScreenSnapshot.CopyRoot();return new string(Enumerable.Range(0,s.Width*s.Height).Select(i=>s[i%s.Width,i/s.Width].Character).ToArray());}
    private static IEnumerable<TView> Children(TGroup group){if(group.last is null)yield break;var first=group.last.Next!;for(var v=first;;v=v.Next!){yield return v;if(v.Next==first)break;}}
    private static void Command(TView view,ushort c){var ev=new TEvent{What=Events.evCommand};ev.message.command=c;view.HandleEvent(ref ev);}
    private static void Key(TView view,ushort k){var ev=new TEvent{What=Events.evKeyDown};ev.keyDown.keyCode=k;ev.keyDown.charScan=new(k);view.HandleEvent(ref ev);}
    private static Task Until(Bridge bridge,Func<bool> predicate)=>Wait(bridge,()=>predicate()?"":null);
    private static async Task<T> Wait<T>(Bridge bridge,Func<T?> predicate)where T:class{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));while(true){T? value=null;await bridge.Invoke(()=>value=predicate()).WaitAsync(timeout.Token);if(value is not null)return value;await Task.Delay(20,timeout.Token);}}
    private sealed class Bridge:TView
    {
        private sealed class Work(Action action):IInfo{internal Action Action{get;}=action;}
        internal Bridge():base(new TRect(0,0,0,0)){options=0;state&=unchecked((ushort)~Views.sfVisible);}
        internal Task Invoke(Action action){var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Post(0x7ddd,new Work(()=>{try{action();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}}));return done.Task;}
        public override void HandleEvent(ref TEvent ev){if(ev.What==Events.evCommand&&ev.message.command==0x7ddd&&ev.message.infoPtr is Work w){ClearEvent(ref ev);w.Action();}else base.HandleEvent(ref ev);}
    }
}
