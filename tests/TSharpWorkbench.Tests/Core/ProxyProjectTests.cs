using System.Text.Json;
using TSharpVision;
using TSharpWorkbench.Testing;
using Xunit;
namespace TSharpWorkbench.Tests.Core;
[Collection("NonParallel")]
public sealed class ProxyProjectTests
{
    [Theory]
    [InlineData(80,25,"NullDriver","none")]
    [InlineData(140,40,"NullDriver","none")]
    [InlineData(80,25,"Win32ConsoleDriver","native-console-cells")]
    [InlineData(140,40,"Win32ConsoleDriver","native-console-cells")]
    [InlineData(80,25,"SDLDriver","renderer-pixels")]
    public async Task TuiExecutesOrdinaryProxyProject(int width,int height,string backend,string capture)
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("TSWB_PROXY_RESULTS")!, "tui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string source = Environment.GetEnvironmentVariable("TSWB_PROXY_SCENARIO")!, path=Path.Combine(root,"ui.twproj");
        var project=new WorkbenchProject {Target=Environment.GetEnvironmentVariable("TSWB_PROXY_TARGET")!,OutputRoot=root,ScenarioFiles=[source],SelectedIds=["proxy/counter"],Group="external",ProxyBackend=backend,ProxyCapture=capture};
        project.Save(path);
        using var driver=new TSharpWorkbench.Tests.Infrastructure.DriverScope((ushort)width,(ushort)height);
        using var app=new WorkbenchApp("",root,new("",root,null,"80x25") {StoragePath=Path.Combine(root,"settings.json")});

        var bridge=new UiBridge();app.DeskTop!.Insert(bridge);
        Exception? failure=null;
        Task worker=Task.Run(async ()=>
        {
            try
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                bool opened=false;
                while(!opened)
                {
                    await bridge.Invoke(()=>opened=app.Project is not null).WaitAsync(deadline.Token);
                    if(!opened)await Task.Delay(20,deadline.Token);
                }
                await bridge.Invoke(()=>app.ActivateProject(WorkbenchProject.Load(path),path));
                bool ready=false;
                while(!ready)
                {
                    await bridge.Invoke(()=>ready=app.Project is {Busy:false} p && p.TestList.Strings.Contains("Ordinary counter")).WaitAsync(deadline.Token);
                    if(!ready)await Task.Delay(20,deadline.Token);
                }
                await bridge.Invoke(()=>
                {
                    var workspace=app.Project!;
                    Assert.Contains("proxy/counter",workspace.TestList.MarkedIds);
                    Assert.Equal(source,Assert.Single(workspace.CaptureProject().ScenarioFiles));
                    string details=string.Join("\n",Enumerable.Range(0,workspace.DetailPane.range).Select(i=>workspace.DetailPane.GetText(i,1000)));
                    Assert.Contains("External JSON:",details);Assert.Contains("proxy-logical",details);
                    Assert.IsAssignableFrom<TWindow>(workspace);
                    var accept = new TEvent { What = Constants.Events.evCommand }; accept.message.command = Constants.Views.cmOK;
                    workspace.PutEvent(ref accept);
                    var run = new TEvent { What = Constants.Events.evCommand }; run.message.command = WorkbenchDialog.Run;
                    workspace.HandleEvent(ref run);
                });
                bool finished = false;
                while (!finished)
                {
                    await bridge.Invoke(() => finished = app.Project is { Busy: false }).WaitAsync(deadline.Token);
                    if (!finished) await Task.Delay(20, deadline.Token);
                }
                string runPath = Assert.Single(Directory.GetFiles(root, "run.json", SearchOption.AllDirectories));
                using var runMetadata = JsonDocument.Parse(File.ReadAllText(runPath));
                Assert.Equal("proxy/counter", runMetadata.RootElement.GetProperty("results")[0].GetProperty("scenarioId").GetString());
                Assert.Equal("Passed", runMetadata.RootElement.GetProperty("results")[0].GetProperty("outcome").GetString());
                if(capture!="none")
                {
                    var batch=WorkbenchRunHistory.Load(runPath).Batch;
                    Assert.Contains("Native evidence: "+capture,string.Join("\n",WorkbenchDialog.ProxyDetails(batch,batch.Results[0])));
                    using var cp=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(batch.RunRoot,batch.Results[0].Checkpoints[0].Metadata)));
                    Assert.True(File.Exists(Path.Combine(batch.RunRoot,cp.RootElement.GetProperty("proxyNative").GetProperty("artifact").GetString()!)));
                }
                if(backend=="Win32ConsoleDriver")
                {
                    var batch=WorkbenchRunHistory.Load(runPath).Batch;
                    string details=string.Join("\n",WorkbenchDialog.ProxyDetails(batch,batch.Results[0]));
                    Assert.Contains("CREATE_NEW_CONSOLE",details);Assert.Contains("Proxy actual: TSharpVision.Drivers.Console.Win32ConsoleDriver",details);
                    Assert.Contains("Native buffer: 80x25; window: 80x25",details);
                }
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                await bridge.Invoke(()=>{app.Project?.CloseApproved();app.EndModal(Constants.Views.cmQuit);});
            }
        });
        AppLifecycleGuard.Run(app);await worker;Assert.Null(failure);
    }

    // The single Run Configuration dialog selects the target display driver and native evidence in place
    // (no nested selector); the accepted values persist in the project and reach the immutable run request.
    private static RunDialogContext ProxyContext(WorkbenchProject defaults) =>
        new("All scenarios", _ => 1, [], "no audited compiled native scenarios in this catalog.", true, _ => new([], ["Runs 1 scenario."], "", "", ""), defaults);

    [Theory]
    [InlineData(140,40,1,"SDLDriver")]
    [InlineData(160,50,2,"SDLGpuDriver")]
    [InlineData(120,36,3,"Win32ConsoleDriver")]
    public void RunConfigurationSelectsPersistsAndSnapshotsProxyBackend(int width,int height,int down,string backend)
    {
        using var driver=new TSharpWorkbench.Tests.Infrastructure.DriverScope((ushort)width,(ushort)height);
        using var app=new TApplication();
        var project=new WorkbenchProject {Target="proxy-target.exe"};
        using var dialog=new WorkbenchRunDialog(app.DeskTop!,project,ProxyContext(project));
        // Outside a modal loop the dialog has no focused branch; press the radio item as a click would.
        dialog.ProxyDrivers.Press(down);var nudge=new TEvent();dialog.HandleEvent(ref nudge);
        Assert.True(dialog.Valid(Constants.Views.cmOK));
        Assert.Equal(backend,dialog.Accepted.ProxyBackend);
        string root=Path.Combine(Path.GetTempPath(),"ui-proxy-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,backend+".twproj");dialog.Accepted.Save(path);
            var saved=WorkbenchProject.Load(path);
            Assert.Equal(backend,new WorkbenchRunRequest(saved,[],"test").Options().ProxyBackend);
        }
        finally { Directory.Delete(root,true); }
        app.ShutDown();
    }

    [Fact]
    public void RunConfigurationSelectsAndPersistsNativeCaptureKind()
    {
        using var driver=new TSharpWorkbench.Tests.Infrastructure.DriverScope(140,40);
        using var app=new TApplication();
        var project=new WorkbenchProject {Target="proxy-target.exe",ProxyBackend="SDLDriver"};
        using var dialog=new WorkbenchRunDialog(app.DeskTop!,project,ProxyContext(project));
        // Only the evidence kinds of the chosen backend are enabled (ProxyCaptureKinds.ForBackend).
        Assert.True(dialog.Evidence.ButtonState(1));Assert.False(dialog.Evidence.ButtonState(2));Assert.False(dialog.Evidence.ButtonState(3));
        dialog.Evidence.Press(1);var nudge=new TEvent();dialog.HandleEvent(ref nudge);
        Assert.True(dialog.Valid(Constants.Views.cmOK));Assert.Equal("renderer-pixels",dialog.Accepted.ProxyCapture);
        string root=Path.Combine(Path.GetTempPath(),"ui-evidence-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"native-selector.twproj");
            dialog.Accepted.Save(path);
            Assert.Equal("renderer-pixels",new WorkbenchRunRequest(WorkbenchProject.Load(path),[],"test").Options().ProxyCapture);
        }
        finally { Directory.Delete(root,true); }
        app.ShutDown();
    }
    private sealed class UiBridge:TView
    {
        private sealed class Work(Action action):IInfo {internal Action Action {get;}=action;}
        internal UiBridge():base(new TRect(0,0,0,0)){options=0;state&=unchecked((ushort)~Constants.Views.sfVisible);}
        internal Task Invoke(Action action)
        {
            var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7dda,new Work(()=>{try{action();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}}));return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        {
            if(ev.What==Constants.Events.evCommand&&ev.message.command==0x7dda&&ev.message.infoPtr is Work work){ClearEvent(ref ev);work.Action();}
            else base.HandleEvent(ref ev);
        }
    }

}
