using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchProjectTests
{
    [Fact]
    public void ProjectRoundTripUsesPortableReferencesAndRejectsInvalidConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), "twproj-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "sample.twproj"), cwd = Environment.CurrentDirectory;
        try
        {
            var original = new WorkbenchProject { Name = "Sample", Target = Path.Combine(root,"sample.exe"), OutputRoot = Path.Combine(root,"runs"),
                BaselineStore = Path.Combine(root,"baselines.json"), SelectedIds = ["sample/status/context"], Group = "status", Search = "context",
                ScreenSizes = ["64x20", "100x30"], CustomWidth = 64, CustomHeight = 20, RunScope = "group", Timeout = 5000 };
            original.Save(path); string json = File.ReadAllText(path);
            Assert.DoesNotContain(root.Replace("\\", "\\\\"), json); Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(original.Fingerprint(), WorkbenchProject.Load(path).Fingerprint()); Assert.Equal(cwd, Environment.CurrentDirectory);
            Assert.Throws<ArgumentException>(() => original.Save(path + ".json"));
            Assert.Throws<ArgumentException>(() => (original with { CustomWidth = GridLimits.MaxWidth + 1 }).Save(path));
            Assert.Throws<ArgumentException>(() => (original with { Mode = "ConsoleDriver" }).Save(path));
            Assert.Throws<ArgumentException>(() => (original with { Timeout = -1 }).Save(path));
            File.WriteAllText(path, json.Replace("\"version\": 1", "\"version\": 99"));
            Assert.Contains("version", Assert.Throws<ArgumentException>(() => WorkbenchProject.Load(path)).Message);
            File.WriteAllText(path, json.Replace("\"selectedIds\": [", "\"selectedIds\": null, \"unknown\": ["));
            Assert.Throws<JsonException>(() => WorkbenchProject.Load(path));
            File.WriteAllText(path,"{ corrupt"); Assert.ThrowsAny<JsonException>(() => WorkbenchProject.Load(path));
            Assert.Empty(Directory.GetFiles(root,"*.tmp"));
        }
        finally { Directory.Delete(root,true); }
    }

    // Workbench grids 120x36 (supported minimum) and 140x40 (primary); target grids are chosen in Run Configuration.
    [Theory]
    [InlineData(120,36)]
    [InlineData(140,40)]
    public void ModelessWorkspaceLifecycleTreeDetailsAndActualRun(int width, int height)
    {
        string evidence = Environment.GetEnvironmentVariable("TSWB_COMMANDER_CONSOLE_UX_EVIDENCE") ?? Path.Combine(Path.GetTempPath(),"wb-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        string initialCwd = Environment.CurrentDirectory;
        string target = TestWorkbenchHostTests.SampleTarget, output = Path.GetDirectoryName(evidence)!;
        string[] previousRuns = Directory.GetDirectories(output,"run-*");
        string path = Path.Combine(evidence,$"sample-{width}.twproj");
        using var driver = new DriverScope((ushort)width,(ushort)height);
        using var app = new WorkbenchApp(target,output,new(target,output,null,"80x25") { StoragePath=Path.Combine(evidence,$"settings-{width}.json") });
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                var main = await Wait(bridge, () => app.Project is { Busy:false } p && p.TestList.Strings.Count >= 9 ? p : null);
                Task menu = bridge.Invoke(()=>Command(app,Views.cmMenu));
                await Wait(bridge,()=> (app.MenuBar!.state & Views.sfModal)!=0 ? "menu" : null);
                await bridge.Invoke(()=>QueueKey(app.MenuBar!,Keys.kbDown));
                await Wait(bridge,()=>Text().Contains("Close project") ? "open" : null);
                await bridge.Invoke(()=>{ Capture("project-menu"); QueueKey(app.MenuBar!,Keys.kbEsc); QueueKey(app.MenuBar!,Keys.kbEsc); }); await menu;
                await bridge.Invoke(() =>
                {
                    Assert.IsAssignableFrom<TWindow>(main); Assert.False(typeof(TDialog).IsAssignableFrom(main.GetType()));
                    Assert.Equal(0, main.state & Views.sfModal); Capture("workspace");
                    Assert.Contains("sample/dialog/basic", Details(main));
                    main.Tree.Select(); Key(main,Keys.kbDown); Assert.NotEqual(0, main.Tree.focused);
                    Assert.Equal(7, main.TestList.range); Assert.Contains("(7)",main.Tree.GetText(main.Tree.focused,200));
                    int item = Enumerable.Range(1,main.Tree.range-1).First(i=>main.Tree.GetText(i,200).Contains("selftest"));
                    main.Tree.FocusItemNum(item); int expanded = main.Tree.range;
                    Key(main,Keys.kbLeft); Assert.True(main.Tree.range < expanded); Capture("tree-collapsed");
                    Key(main,Keys.kbRight); Assert.Equal(expanded,main.Tree.range); Capture("tree-expanded");
                    item = Enumerable.Range(1,main.Tree.range-1).Single(i=>main.Tree.GetText(i,200).Contains("status"));
                    var click = new TEvent { What=Events.evMouseDown }; click.mouse.where=main.Tree.MakeGlobal(new(2,item-main.Tree.topItem)); click.mouse.buttons=(byte)Events.mbLeftButton;
                    var release = click; release.What=Events.evMouseUp; release.mouse.buttons=0; main.Tree.PutEvent(ref release);
                    main.Tree.HandleEvent(ref click); Assert.Equal("sample/status", main.Tree.SelectedGroup); Assert.Empty(main.TestList.MarkedIds);
                    main.TestList.Select(); Key(main,Keys.kbSpace); Assert.NotEmpty(main.TestList.MarkedIds); Capture("marked-details");
                    main.Tree.FocusItemNum(0); Assert.NotEmpty(main.TestList.MarkedIds);
                    main.TestList.FocusItemNum(main.TestList.range-1); Assert.Contains(main.TestList.Current!.Id, Details(main));
                    main.DetailPane.Select(); Key(main,Keys.kbPgDn); Key(main,Keys.kbPgDn);
                    Assert.True(main.DetailPane.topItem>0); Capture("details-scrolled");
                    using var other = new TWindow(new(5,2,55,12),"Other modeless window",2); app.DeskTop.Insert(other); other.Select();
                    Assert.Same(other, app.DeskTop.current); Command(app,WorkbenchApp.ProjectWindow); Assert.Same(main,app.DeskTop.current); other.Close();
                    main.Locate(new(2,1,104,31)); Capture("resized"); main.Zoom(); Capture("maximized"); main.Zoom(); main.Locate(app.DeskTop.GetExtent());
                    Command(app,WorkbenchDialog.MarkAll); Assert.DoesNotContain("selftest/failure/wrong-expectations",main.TestList.MarkedIds);
                    Command(app,WorkbenchDialog.ClearSelection); Assert.Empty(main.TestList.MarkedIds); main.TestList.Toggle("sample/status/context");
                    Assert.True(main.SaveProject(false,path)); Assert.False(main.Dirty);
                });
                // Target screen sizes live only in Run Configuration (removed the workspace grid controls).
                Task sizing=bridge.Invoke(()=>Raw(main,WorkbenchDialog.Run));
                var sizes=await Wait(bridge,()=>app.DeskTop.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(()=>{ var grids=sizes.Sizes; grids.value=3; grids.DrawView(); Key(sizes,Keys.kbEsc); }); await sizing;
                Task properties = bridge.Invoke(()=>Command(main,WorkbenchDialog.Advanced));
                var modal = await Wait(bridge,()=>Modal("Project Properties"));
                await bridge.Invoke(()=>{ Capture("custom-properties"); Key(modal,Keys.kbEsc); }); await properties;
                Task running=bridge.Invoke(()=>Raw(main,WorkbenchDialog.Run));
                sizes=await Wait(bridge,()=>app.DeskTop.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(()=>{ var grids=sizes.Sizes; grids.value=3; grids.DrawView(); Command(sizes,Views.cmOK); }); await running;
                // Closing during a run asks first; Keep running (Esc) leaves the run and the project untouched.
                Task closing=bridge.Invoke(()=>{ Assert.True(main.Busy); main.Close(); });
                modal=await Wait(bridge,()=>Modal(WorkbenchDialog.RunningTitle));
                await bridge.Invoke(()=>{ Capture("running-close-protected"); Key(modal,Keys.kbEsc); }); await closing;
                await bridge.Invoke(()=>Assert.Same(main,app.Project));
                await Wait(bridge,()=>!main.Busy ? "done" : null);
                await bridge.Invoke(()=>
                {
                    Assert.Contains("Finished:",Text()); Capture("run-finished");
                    string run=Directory.GetDirectories(output,"run-*").Except(previousRuns).Single();
                    using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(run,"run.json")));
                    Assert.Equal(2,report.RootElement.GetProperty("results").GetArrayLength());
                    Assert.All(report.RootElement.GetProperty("results").EnumerateArray(),r=>Assert.Equal("Passed",r.GetProperty("outcome").GetString()));
                });
                Task results=bridge.Invoke(()=>Command(app,WorkbenchDialog.Results));
                var resultWindow=await Wait(bridge,()=>Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await bridge.Invoke(()=>{ Capture("results"); Key(resultWindow,Keys.kbEsc); }); await results;
                await bridge.Invoke(()=>{ Key(main,Keys.kbEsc); Assert.Same(main,app.Project); main.TestList.Toggle("sample/dialog/basic"); Assert.True(main.Dirty); });
                Task close=bridge.Invoke(()=>Command(app,WorkbenchApp.CloseProject));
                modal=await Wait(bridge,()=>Modal(WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(()=>{ Capture("dirty-cancel"); Key(modal,Keys.kbEsc); }); await close; Assert.Same(main,app.Project);
                Task create=bridge.Invoke(()=>Command(app,WorkbenchApp.NewProject));
                modal=await Wait(bridge,()=>Modal(WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(()=>{ Capture("dirty-save"); Command(modal,Views.cmYes); }); await create;
                // New project opens Project Properties for the untitled project; cancel it.
                modal=await Wait(bridge,()=>Modal("Project Properties"));
                await bridge.Invoke(()=>Key(modal,Keys.kbEsc));
                await Wait(bridge,()=>Modal("Project Properties") is null ? "closed" : null);
                await bridge.Invoke(()=>{ Assert.NotSame(main,app.Project); Assert.True(File.Exists(path)); Command(app,WorkbenchApp.CloseProject); Assert.Null(app.Project); Capture("empty-workspace"); });
                Task open=bridge.Invoke(()=>Command(app,WorkbenchApp.OpenProject));
                var picker=await Wait(bridge,()=>app.DeskTop.TopView() as TFileDialog);
                await bridge.Invoke(()=>{ Capture("project-open"); picker.SetData(path); Command(picker,Views.cmFileOpen); }); await open;
                main=await Wait(bridge,()=>app.Project is {Busy:false} p && p.TestList.Strings.Count>=9?p:null);
                await bridge.Invoke(()=>{ Assert.False(main.Dirty); Assert.Contains("sample/dialog/basic",main.TestList.MarkedIds); Capture("project-reopened"); });
                Task saveAs=bridge.Invoke(()=>Command(app,WorkbenchApp.SaveAsProject));
                picker=await Wait(bridge,()=>app.DeskTop.TopView() as TFileDialog);
                string copy=Path.Combine(evidence,$"sample-copy-{width}.twproj");
                await bridge.Invoke(()=>{ picker.SetData(copy); Capture("project-save-as"); Command(picker,Views.cmFileOpen); }); await saveAs;
                await bridge.Invoke(()=>{ Assert.Equal(copy,main.ProjectPath); Assert.True(File.Exists(copy)); main.TestList.Toggle("sample/dialog/basic"); });
                close=bridge.Invoke(()=>Command(app,WorkbenchApp.CloseProject)); modal=await Wait(bridge,()=>Modal(WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(()=>{ Capture("dirty-discard"); Command(modal,Views.cmNo); }); await close;
                string corrupt=Path.Combine(evidence,$"corrupt-{width}.twproj"); File.WriteAllText(corrupt,"{ invalid");
                open=bridge.Invoke(()=>Command(app,WorkbenchApp.OpenProject)); picker=await Wait(bridge,()=>app.DeskTop.TopView() as TFileDialog);
                await bridge.Invoke(()=>{ picker.SetData(corrupt); Command(picker,Views.cmFileOpen); });
                modal=await Wait(bridge,()=>Modal("Cannot Open Project"));
                await bridge.Invoke(()=>{ Capture("corrupt-project-error"); Key(modal,Keys.kbEsc); }); await open;
                await bridge.Invoke(()=>
                {
                    Assert.Null(app.Project);
                    var stale=WorkbenchProject.Load(copy) with { SelectedIds=["sample/status/context","obsolete/missing"] };
                    string stalePath=Path.Combine(evidence,$"stale-{width}.twproj"); stale.Save(stalePath); app.ActivateProject(stale,stalePath);
                });
                main=await Wait(bridge,()=>app.Project is {Busy:false} p && p.TestList.Strings.Count>=9?p:null);
                await bridge.Invoke(()=>{ Assert.True(main.Dirty); Assert.Equal(new[]{"sample/status/context"},main.TestList.MarkedIds); Assert.Contains("Missing/stale",Text()); Capture("stale-project-ids"); });
                Task saveFailure=bridge.Invoke(()=>Assert.False(main.SaveProject(false,Path.Combine(evidence,"missing-parent","failed.twproj"))));
                modal=await Wait(bridge,()=>Modal("Cannot Save Project"));
                await bridge.Invoke(()=>{ Capture("save-failed"); Key(modal,Keys.kbEsc); }); await saveFailure;
                await bridge.Invoke(()=>
                {
                    Assert.True(main.Dirty); main.TestList.ClearMarks(); main.TestList.Toggle("selftest/failure/blocked-ui");
                    Command(main,WorkbenchDialog.Run);
                });
                await Wait(bridge,()=>Text().Contains("checkpoint initial")?"running":null);
                Task quit=bridge.Invoke(()=>Command(app,Views.cmQuit));
                modal=await Wait(bridge,()=>Modal(WorkbenchDialog.RunningTitle));
                await bridge.Invoke(()=>Command(modal,Views.cmYes));
                modal=await Wait(bridge,()=>Modal(WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(()=>{ Assert.False(main.Busy); Capture("quit-cleanup-confirmation"); Key(modal,Keys.kbEsc); }); await quit;
                await bridge.Invoke(()=>{ Assert.Same(main,app.Project); main.CloseApproved(); Command(app,Views.cmQuit); });
            }
            catch(Exception ex)
            {
                failure=ex; await bridge.Invoke(()=>{ app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); });
            }
        });
        int code=AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(10)),"Workspace worker did not finish."); Assert.Null(failure); Assert.Equal(0,code);
        Assert.Equal(initialCwd, Environment.CurrentDirectory);
        TDialog? Modal(string title)=>app.DeskTop.TopView() is TDialog d && d.title==title?d:null;
        void Capture(string name)
        {
            app.StatusLine?.Update();
            var screen=ScreenSnapshot.CopyRoot(); string stem=Path.Combine(evidence,$"{name}-{width}x{height}");
            File.WriteAllBytes(stem+".png",LogicalPng.Encode(screen)); File.WriteAllBytes(stem+".tvs1",screen.Serialize());
            File.WriteAllText(stem+".json",JsonSerializer.Serialize(new{name,width=screen.Width,height=screen.Height,driver="NullDriver",captureLayer="logical"}));
        }
    }
    private static string Details(WorkbenchDialog main)=>string.Join("\n",Enumerable.Range(0,main.DetailPane.range).Select(i=>main.DetailPane.GetText(i,1000)));
    private static string Text() { var s=ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0,s.Width*s.Height).Select(i=>s[i%s.Width,i/s.Width].Character).ToArray()); }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if(group.last is null) yield break; var first=group.last.Next!;
        for(TView? item=first;item is not null;item=item.Next) { yield return item; if(item.Next==first) break; }
    }
    private static void Command(TView view,ushort command) { var ev=new TEvent {What=Events.evCommand}; ev.message.command=command;
        if(command==WorkbenchDialog.Run && (view is WorkbenchDialog w && w.TestList.MarkedIds.Count>0 || view is WorkbenchApp a && a.Project?.TestList.MarkedIds.Count>0))
        {var accept=new TEvent{What=Events.evCommand};accept.message.command=Views.cmOK;view.PutEvent(ref accept);}
        view.HandleEvent(ref ev); }
    // Sends a command without the Run auto-accept of Command, so the Run Configuration dialog can be edited first.
    private static void Raw(TView view,ushort command) { var ev=new TEvent {What=Events.evCommand}; ev.message.command=command; view.HandleEvent(ref ev); }
    private static void Key(TView view,ushort key) { var ev=new TEvent {What=Events.evKeyDown}; ev.keyDown.keyCode=key; ev.keyDown.charScan=new(key); view.HandleEvent(ref ev); }
    private static void QueueKey(TView view,ushort key) { var ev=new TEvent {What=Events.evKeyDown}; ev.keyDown.keyCode=key; ev.keyDown.charScan=new(key); view.PutEvent(ref ev); }
    private static async Task<T> Wait<T>(Bridge bridge,Func<T?> predicate) where T:class
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while(true) { T? value=null; await bridge.Invoke(()=>value=predicate()).WaitAsync(timeout.Token); if(value is not null)return value; await Task.Delay(20,timeout.Token); }
    }
    private sealed class Bridge:TView
    {
        private sealed class Work(Action action):IInfo { internal Action Action {get;}=action; }
        internal Bridge():base(new TRect(0,0,0,0)) {options=0;state&=unchecked((ushort)~Views.sfVisible);}
        internal Task Invoke(Action action)
        {
            var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd,new Work(()=>{try{action();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}}));return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if(ev.What==Events.evCommand && ev.message.command==0x7ddd && ev.message.infoPtr is Work work){ClearEvent(ref ev);work.Action();}else base.HandleEvent(ref ev); }
    }
}
