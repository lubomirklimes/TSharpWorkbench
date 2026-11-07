using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class ExternalScenarioTests
{
    private static string Root => Environment.GetEnvironmentVariable("TSWB_EXTERNAL_RESULTS") ?? throw new InvalidOperationException("Set TSWB_EXTERNAL_RESULTS to a repository TestResults run directory.");
    private static string Folder() { string p = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static string Document(string steps = """[{"kind":"checkpoint","id":"external-initial"}]""") =>
        """{"schema":"tsharpworkbench.scenario","version":1,"id":"external/sample","title":"External flow","description":"Authored independently","fixture":"sample/dialog/basic","expectedOutcome":"passed","steps":STEPS}""".Replace("STEPS", steps);
    private static string FileIn(string root, string content) { string p = Path.Combine(root, "scenario.json"); File.WriteAllText(p, content); return p; }
    // These documents bind to ordinary compiled scenarios (legacy fixtures). Binding is strict by default,
    // so every legacy binding here goes through the explicit compatibility allowance, one compiled ID at a time.
    private static readonly string[] SampleDialog = ["sample/dialog/basic"], DeferredLabel = ["selftest/fixture/deferred-label"];
    private static ScenarioDescriptor Fixture() => new TestScenario("sample/dialog/basic", "Fixture", "sample", "Fixture").Grid(60,20).Checkpoint("compiled-only").Descriptor;

    [Theory]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"version\":1", "\"version\":null")]
    [InlineData("\"version\":1", "\"version\":1,\"version\":1")]
    [InlineData("\"schema\":\"tsharpworkbench.scenario\"", "\"schema\":\"other\"")]
    [InlineData("\"kind\":\"checkpoint\"", "\"kind\":\"shell\"")]
    [InlineData("\"kind\":\"checkpoint\"", "\"kind\":\"checkpoint\",\"command\":\"whoami\"")]
    [InlineData("\"expectedOutcome\":\"passed\",", "")]
    [InlineData("\"steps\":[", "\"steps\":null,\"unknown\":[")]
    public void RejectsInvalidDocumentsWithSourcePath(string old, string replacement)
    {
        string root = Folder(), path = FileIn(root, Document().Replace(old,replacement));
        var ex = Assert.Throws<ExternalScenarioException>(() => ExternalScenarios.Load(path));
        Assert.Contains(path, ex.Message);
    }

    [Theory]
    [InlineData("[{\"kind\":\"key\",\"key\":\"kbUnknown\"},{\"kind\":\"checkpoint\",\"id\":\"ok\"}]")]
    [InlineData("[{\"kind\":\"checkpoint\",\"id\":\"dup\"},{\"kind\":\"checkpoint\",\"id\":\"dup\"}]")]
    [InlineData("[{\"kind\":\"mouse\",\"input\":\"click-left@999,0\"},{\"kind\":\"checkpoint\",\"id\":\"ok\"}]")]
    [InlineData("[{\"kind\":\"resize\",\"width\":0,\"height\":25},{\"kind\":\"checkpoint\",\"id\":\"ok\"}]")]
    [InlineData("[{\"kind\":\"checkpoint\",\"id\":\"ok\",\"retry\":{\"attempts\":0,\"intervalMilliseconds\":250}}]")]
    [InlineData("[{\"kind\":\"checkpoint\",\"id\":\"ok\",\"assertions\":[{\"id\":\"a\",\"kind\":\"text-at\",\"text\":\"x\"}]}]")]
    [InlineData("[{\"kind\":\"checkpoint\",\"id\":\"ok\",\"assertions\":[{\"id\":\"a\",\"kind\":\"modal\",\"view\":{}}]}]")]
    public void RejectsBadStepsAndAssertions(string steps)
    {
        string path=FileIn(Folder(),Document(steps));
        Assert.Throws<ExternalScenarioException>(()=>ExternalScenarios.Load(path));
    }

    [Fact]
    public void ResolvesFixturesAndRegisteredAdaptersAndPreservesSafety()
    {
        var compiled = new TestScenario("sample/dialog/basic","Fixture","sample","d").Grid(60,20)
            .Safety(new(FixtureFiles:true,Network:true)).Action("safe",_=>{}).Point("button",_=>new TPoint(1,1)).Checkpoint("compiled").Descriptor;
        string path=FileIn(Folder(),Document("""[{"kind":"action","adapter":"safe"},{"kind":"mouse","input":"click-left@:button"},{"kind":"checkpoint","id":"authored"}]"""));
        var file=ExternalScenarios.Load(path);
        Assert.Contains("not a fixture-only registration",Assert.Throws<ExternalScenarioException>(()=>ExternalScenarios.Merge([compiled],[file])).Message);
        Assert.Throws<ExternalScenarioException>(()=>ExternalScenarios.Merge([compiled],[file],["sample/dialog/other"]));
        var merged=ExternalScenarios.Merge([compiled],[file],SampleDialog);
        Assert.Equal(2,merged.Length);Assert.Same(compiled,merged[0]);
        Assert.Equal(compiled.Safety,merged[1].Safety); Assert.Equal(compiled.Id,merged[1].External!.FixtureId);
        Assert.Equal("authored",merged[1].Steps[^1].Name);Assert.DoesNotContain(merged[1].Steps,s=>s.Name=="compiled");
        Assert.Throws<ExternalScenarioException>(()=>ExternalScenarios.Merge([compiled],[file,file],SampleDialog));
        Assert.Throws<ExternalScenarioException>(()=>ExternalScenarios.Merge([Fixture()],[file],SampleDialog));
        Assert.Throws<ExternalScenarioException>(()=>ExternalScenarios.Merge([], [file],SampleDialog));
        Assert.False(NativeExecution.Supports(merged[1],"SDLDriver"));
    }

    [Fact]
    public void ProjectRoundtripIsPortableAndOldProjectsStillLoad()
    {
        string root=Folder(), source=FileIn(root,Document()), path=Path.Combine(root,"test.twproj");
        var project=new WorkbenchProject {Target=TestWorkbenchHostTests.SampleTarget,OutputRoot=root,ScenarioFiles=[source]};
        project.Save(path);Assert.Contains("\"scenario.json\"",File.ReadAllText(path));
        Assert.Equal(source,Assert.Single(WorkbenchProject.Load(path).ScenarioFiles));
        File.WriteAllText(path,"""{"schema":"TSharpVision.TestWorkbench","version":1,"name":"Legacy","target":"target.exe","outputRoot":"runs"}""");
        Assert.Empty(WorkbenchProject.Load(path).ScenarioFiles);
        Assert.Throws<ArgumentException>(()=>(project with {ScenarioFiles=[source,source]}).Validate());
    }

    [Fact]
    public async Task JsonFlowExecutesOwnOrderedInputsMultipleCheckpointsAndCliSelection()
    {
        string root=Folder();
        string path=FileIn(root,Document("""[{"kind":"checkpoint","id":"authored-start"},{"kind":"key","key":"kbF3"},{"kind":"checkpoint","id":"authored-dialog","assertions":[{"id":"visible","kind":"screen-contains","text":"Sample dialog"}]},{"kind":"text","text":"External JSON"},{"kind":"key","key":"kbEsc"},{"kind":"checkpoint","id":"authored-end","assertions":[{"id":"closed","kind":"modal"}]}]"""));
        string projectPath=Path.Combine(root,"sample.twproj");
        new WorkbenchProject {Target=TestWorkbenchHostTests.SampleTarget,OutputRoot=root,ScenarioFiles=[path],ScreenSizes=["80x25"],SelectedIds=["external/sample"],LegacyFixtures=SampleDialog}.Save(projectPath);
        var output=new StringWriter();var error=new StringWriter();
        Assert.Equal(0,await Cli.RunAsync(["run",projectPath,"--results",Path.Combine(root,"results.json")],output,error));
        var batch=JsonSerializer.Deserialize<BatchResult>(File.ReadAllText(Path.Combine(root,"results.json")),ScenarioRunner.Json)!;
        var result=Assert.Single(batch.Results);Assert.Equal(ScenarioOutcomes.Passed,result.Outcome);
        Assert.True(result.FixtureIntegrityVerified);Assert.True(result.CleanupSucceeded);
        Assert.Equal(new[]{"authored-start","authored-dialog","authored-end"},result.Checkpoints.Select(c=>c.Checkpoint));
        using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot,result.Checkpoints[1].Metadata)));
        Assert.Equal("kbF3",metadata.RootElement.GetProperty("interactionsSincePreviousCheckpoint")[0].GetProperty("name").GetString());
        output.GetStringBuilder().Clear(); Assert.Equal(0,await Cli.RunAsync(["list",projectPath],output,error));
        Assert.Contains("external/sample",output.ToString());Assert.Contains("sample/dialog/basic",output.ToString());
    }

    [Fact]
    public async Task FailedAssertionsRecaptureWithoutReplayingInputAndRetainFinalEvidence()
    {
        string root=Folder(),path=FileIn(root,Document("""[{"kind":"key","key":"kbF3"},{"kind":"checkpoint","id":"retry-me","retry":{"attempts":3,"intervalMilliseconds":10},"assertions":[{"id":"wrong","kind":"screen-contains","text":"Impossible nonexistent dialog"}]},{"kind":"key","key":"kbEnter"},{"kind":"checkpoint","id":"never-executed"}]"""));
        var runner=new ScenarioRunner();var catalog=await runner.DiscoverAsync(TestWorkbenchHostTests.SampleTarget,[path],SampleDialog);
        var batch=await runner.RunAsync(TestWorkbenchHostTests.SampleTarget,[catalog.Single(s=>s.Id=="external/sample")],new RunOptions(root,["80x25"]));
        var result=Assert.Single(batch.Results);Assert.Equal(ScenarioOutcomes.Failed,result.Outcome);Assert.Contains("wrong",result.Reason);
        Assert.True(result.CleanupSucceeded); Assert.True(result.FixtureIntegrityVerified); Assert.Equal(7,Cli.ProjectExitCode(batch));
        var checkpoint=Assert.Single(result.Checkpoints);
        using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot,checkpoint.Metadata)));
        var attempts=metadata.RootElement.GetProperty("attempts");Assert.Equal(3,attempts.GetArrayLength());
        Assert.True(attempts[2].GetProperty("elapsedMilliseconds").GetInt64()>=20);
        Assert.Single(metadata.RootElement.GetProperty("interactionsSincePreviousCheckpoint").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(batch.RunRoot,checkpoint.Cells)));
    }

    [Fact]
    public async Task CliErrorsAndResultCodesAreDistinct()
    {
        string root=Folder(),path=FileIn(root,"{ broken"),project=Path.Combine(root,"bad.twproj");
        var output=new StringWriter();var error=new StringWriter();
        Assert.Equal(4,await Cli.RunAsync(["run",project],output,error));
        new WorkbenchProject {Target=TestWorkbenchHostTests.SampleTarget,OutputRoot=root,ScenarioFiles=[path],SelectedIds=["external/sample"]}.Save(project);
        Assert.Equal(5,await Cli.RunAsync(["run",project],output,error));Assert.Contains(path,error.ToString());
        foreach (var (outcome,code) in new[]{(ScenarioOutcomes.Blocked,6),(ScenarioOutcomes.Failed,7),(ScenarioOutcomes.TimedOut,8),(ScenarioOutcomes.Error,9)})
            Assert.Equal(code,Cli.ProjectExitCode(new("",[new("x","80x25",outcome,"reason",[],false,true,true)])));
    }

    [Fact]
    public void OutOfGridCellsAreRejectedBeforeExecution()
    {
        string path=FileIn(Folder(),Document("""[{"kind":"mouse","input":"click-left@90,1"},{"kind":"checkpoint","id":"ok"}]"""));
        var scenario=ExternalScenarios.Merge([Fixture()],[ExternalScenarios.Load(path)],SampleDialog)[1];
        Assert.Throws<ExternalScenarioException>(()=>ExternalScenarios.ValidateExecution(scenario,"80x25"));
        ExternalScenarios.ValidateExecution(scenario,"140x40");
    }
    [Fact]
    public async Task RetryEventuallyPassesAfterLongerAsynchronousReadiness()
    {
        string root=Folder(), path=FileIn(root,Document("""[{"kind":"action","adapter":"begin-deferred-label"},{"kind":"checkpoint","id":"authored-ready","retry":{"attempts":10,"intervalMilliseconds":250},"assertions":[{"id":"ready","kind":"screen-contains","text":"Deferred fixture is ready"}]}]""").Replace("sample/dialog/basic","selftest/fixture/deferred-label"));
        var runner=new ScenarioRunner();var catalog=await runner.DiscoverAsync(TestWorkbenchHostTests.SampleTarget,[path],DeferredLabel);
        var batch=await runner.RunAsync(TestWorkbenchHostTests.SampleTarget,[catalog.Single(s=>s.Id=="external/sample")],new RunOptions(root,["80x25"]));
        var result=Assert.Single(batch.Results);Assert.Equal(ScenarioOutcomes.Passed,result.Outcome);
        using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot,result.Checkpoints[0].Metadata)));
        var attempts=metadata.RootElement.GetProperty("attempts");
        Assert.Equal("PASS",attempts[attempts.GetArrayLength()-1].GetProperty("assertions")[0].GetProperty("outcome").GetString());
        Assert.True(result.FixtureIntegrityVerified);Assert.True(result.CleanupSucceeded);
    }

    [Theory]
    [InlineData(80,25)]
    [InlineData(140,40)]
    public async Task TuiProjectLoadsExternalIdentitiesGroupsSelectionAndDetails(int width,int height)
    {
        string root=Folder(), source=FileIn(root,Document()), path=Path.Combine(root,"ui.twproj");
        var project=new WorkbenchProject {Target=TestWorkbenchHostTests.SampleTarget,OutputRoot=root,ScenarioFiles=[source],SelectedIds=["external/sample"],Group="external",LegacyFixtures=SampleDialog};
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
                    await bridge.Invoke(()=>ready=app.Project is {Busy:false} p && p.TestList.Strings.Contains("External flow")).WaitAsync(deadline.Token);
                    if(!ready)await Task.Delay(20,deadline.Token);
                }
                await bridge.Invoke(()=>
                {
                    var workspace=app.Project!;
                    Assert.Contains("external/sample",workspace.TestList.MarkedIds);
                    Assert.Equal(source,Assert.Single(workspace.CaptureProject().ScenarioFiles));
                    string details=string.Join("\n",Enumerable.Range(0,workspace.DetailPane.range).Select(i=>workspace.DetailPane.GetText(i,1000)));
                    Assert.Contains("External JSON:",details);Assert.Contains("sample/dialog/basic",details);
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
                Assert.Equal("external/sample", runMetadata.RootElement.GetProperty("results")[0].GetProperty("scenarioId").GetString());
                Assert.Equal("Passed", runMetadata.RootElement.GetProperty("results")[0].GetProperty("outcome").GetString());
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                await bridge.Invoke(()=>{app.Project?.CloseApproved();app.EndModal(Constants.Views.cmQuit);});
            }
        });
        AppLifecycleGuard.Run(app);await worker;Assert.Null(failure);
    }

    [Fact]
    public async Task RetryGapIsInterruptedBySessionDeadline()
    {
        var previous = TargetSession.Deadline;
        try
        {
            TargetSession.Deadline = TimeSpan.FromMilliseconds(80);
            await using var session = new TargetSession(TestWorkbenchHostTests.SampleTarget, null, "80x25", default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DelayAsync(30000));
            Assert.True(session.Expired);
        }
        finally { TargetSession.Deadline = previous; }
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
