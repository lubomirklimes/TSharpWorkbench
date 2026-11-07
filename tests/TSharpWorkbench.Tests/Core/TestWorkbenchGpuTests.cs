using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;
namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchGpuTests
{
    public sealed class GpuAttribute : TheoryAttribute
    {
        public GpuAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_TARGET")))
                Skip = "Requires TSWB_COMMANDER_NATIVE_TARGET and TSWB_COMMANDER_NATIVE_OUTPUT; actual owned Commander GPU execution.";
        }
    }
    [Gpu]
    [InlineData("SDLGpuDriver", "80x25")]
    [InlineData("SDLGpuDriver", "140x40")]
    [InlineData("SDLGpuDriver-D3D12Fallback", "80x25")]
    [InlineData("SDLGpuDriver-D3D12Fallback", "140x40")]
    [InlineData("SDLGpuDriver-Vulkan", "80x25")]
    public async Task RealCommanderGpuProvenanceAndNormalResourceRelease(string mode,string grid)
    {
        string target=Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_TARGET")!;
        var runner=new ScenarioRunner();var catalog=await CommanderExternalCatalog.DiscoverAsync(runner,target);
        string[] ids=mode=="SDLGpuDriver-Vulkan"?["commander/menu/f10-open-close"]
            :grid=="140x40"?["commander/keyboard/copy-rename-here"]
            :["commander/menu/f10-open-close","commander/keyboard/copy-rename-here","commander/menu-status/nested-submenu","commander/input/menu-mouse"];
        var batch=await runner.RunAsync(target,catalog.Where(s=>ids.Select(CommanderExternalCatalog.Id).Contains(s.Id)).ToArray(),
            new RunOptions(Path.Combine(Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_OUTPUT")!,NativeExecution.Backend(mode)+"-"+NativeExecution.GpuPath(mode)+"-"+grid),[grid]){Driver=mode});
        foreach(var result in batch.Results)
        {
            Assert.True(result.Outcome==ScenarioOutcomes.Passed,result.Reason+" | "+batch.RunRoot);
            Assert.True(result.TargetExited);Assert.True(result.FixtureIntegrityVerified);Assert.True(result.CleanupSucceeded);
            foreach(var checkpoint in result.Checkpoints)
            {
                using var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(batch.RunRoot,checkpoint.Metadata)));
                var native=doc.RootElement.GetProperty("native");
                Assert.Equal("SDLGpuDriver",native.GetProperty("driver").GetString());
                Assert.Equal(mode,native.GetProperty("requestedMode").GetString());
                Assert.Equal(NativeExecution.Backend(mode),native.GetProperty("backend").GetString());
                Assert.Equal(NativeExecution.GpuPath(mode),native.GetProperty("renderingPath").GetString());
                Assert.Equal("hardware adapter",native.GetProperty("gpu.adapterKind").GetString());
                Assert.True(long.Parse(native.GetProperty("swapchain.framesPresented").GetString()!)>0);
                Assert.Contains("snapshot",native.GetProperty("captureBoundary").GetString());
            }
        }
        using var run=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(batch.RunRoot,"run.json")));
        foreach(var entry in run.RootElement.GetProperty("nativeLifecycle").EnumerateArray())
        {Assert.True(entry.GetProperty("gpuCleanupVerified").GetBoolean());Assert.Equal(0,entry.GetProperty("exitCode").GetInt32());Assert.DoesNotContain("terminated",entry.GetProperty("stopOutcome").GetString());}
        Assert.Equal(batch.Results.Length,WorkbenchRunHistory.Load(Path.Combine(batch.RunRoot,"run.json")).Batch.Results.Length);
        Assert.Contains("GPU worker",File.ReadAllText(batch.Report!));
    }

    [Gpu]
    [InlineData("SDLGpuDriver", "desktop")]
    [InlineData("SDLGpuDriver", "copy-here")]
    [InlineData("SDLGpuDriver-D3D12Fallback", "copy-here")]
    public async Task CancellationRetainsFixtureAndStopsOwnedGpuTarget(string mode, string checkpoint)
    {
        string target=Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_TARGET")!;
        var runner=new ScenarioRunner(); var catalog=await CommanderExternalCatalog.DiscoverAsync(runner,target);
        using var cancel=new CancellationTokenSource(); bool reached=false;
        var batch=await runner.RunAsync(target,[catalog.Single(s=>s.Id==CommanderExternalCatalog.Id("commander/keyboard/copy-rename-here"))],
            new RunOptions(Environment.GetEnvironmentVariable("TSWB_COMMANDER_NATIVE_OUTPUT")!,["80x25"]){Driver=mode},message=>
            { if(message.Trim()=="checkpoint "+checkpoint){reached=true;cancel.Cancel();} },cancel.Token);
        var result=Assert.Single(batch.Results); Assert.True(reached);Assert.Equal(ScenarioOutcomes.Aborted,result.Outcome);
        Assert.True(result.CleanupSucceeded,result.Reason);Assert.True(result.FixtureIntegrityVerified,result.Reason);
        Assert.Equal(checkpoint=="desktop"?0:1,result.Checkpoints.Length);
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot,"run.json")));
        var lifecycle=doc.RootElement.GetProperty("nativeLifecycle")[0];
        Assert.True(lifecycle.GetProperty("gpuCleanupVerified").GetBoolean());
        Assert.DoesNotContain("terminated",lifecycle.GetProperty("stopOutcome").GetString());
    }
    [Theory]
    [InlineData("SDLGpuDriver")]
    [InlineData("SDLGpuDriver-D3D12Fallback")]
    [InlineData("SDLGpuDriver-Vulkan")]
    public async Task UnsupportedGpuStepsAreBlockedBeforeTargetLaunch(string mode)
    {
        var scenario=new TestScenario("commander/menu/f10-open-close","Unsupported","menu","No text adapter").Text("no").Checkpoint("desktop").Descriptor;
        Assert.False(NativeExecution.Supports(scenario,mode));
        string root=Path.Combine(Path.GetTempPath(),"wb-refusal-"+Guid.NewGuid().ToString("N"));
        try
        {
            var batch=await new ScenarioRunner().RunAsync("missing.exe",[scenario],new RunOptions(root,["80x25"]){Driver=mode});
            Assert.Equal(ScenarioOutcomes.Skipped,Assert.Single(batch.Results).Outcome);   // Driver prerequisite
            Assert.Empty(Directory.GetFiles(batch.RunRoot,"*.png"));
            Assert.Throws<ArgumentException>(()=>WorkbenchCapabilities.Validate(mode+"-invalid"));
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Theory]
    [InlineData("SDLGpuDriver")]
    [InlineData("SDLGpuDriver-D3D12Fallback")]
    [InlineData("SDLGpuDriver-Vulkan")]
    public void GpuProjectRoundtripRetainsOtherModeMarksAndCustomGrid(string mode)
    {
        string root=Path.Combine(Path.GetTempPath(),"wb-project-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var project=new WorkbenchProject{Mode=mode,TestType="rendering",ScreenSizes=["80x25","140x40","100x30"],SelectedIds=["commander/menu/f10-open-close","commander/input/dialog-mouse"]};
            string path=Path.Combine(root,"gpu.twproj");project.Save(path);var loaded=WorkbenchProject.Load(path);
            Assert.Equal(project.SelectedIds,loaded.SelectedIds);Assert.Equal(project.ScreenSizes,loaded.ScreenSizes);Assert.Equal(mode,loaded.Mode);
            Assert.Equal("SDLGpuDriver",NativeExecution.ActualDriver(mode));
        }
        finally { Directory.Delete(root,true); }
    }
}
