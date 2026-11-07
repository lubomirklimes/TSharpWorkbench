using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchConsoleTests
{
    public sealed class ConsoleIntegrationAttribute : TheoryAttribute
    {
        public ConsoleIntegrationAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSWB_COMMANDER_CONSOLE_TARGET")))
                Skip = "Set TSWB_COMMANDER_CONSOLE_TARGET and TSWB_COMMANDER_CONSOLE_OUTPUT for real owned Windows Console integration.";
        }
    }

    [ConsoleIntegration]
    [InlineData("commander/menu/f10-open-close", "80x25")]
    [InlineData("commander/menu/f10-open-close", "140x40")]
    [InlineData("commander/menu-status/nested-submenu", "80x25")]
    [InlineData("commander/menu-status/menu-opens-jobs", "80x25")]
    [InlineData("commander/keyboard/copy-rename-here", "80x25")]
    public async Task OwnedConsoleStartupInputCaptureNormalExit(string id, string grid)
    {
        string target = Environment.GetEnvironmentVariable("TSWB_COMMANDER_CONSOLE_TARGET")!;
        string output = Environment.GetEnvironmentVariable("TSWB_COMMANDER_CONSOLE_OUTPUT")!;
        var runner = new ScenarioRunner();
        var batch = await runner.RunAsync(target, [await CommanderExternalCatalog.SingleAsync(runner, target, id)], new RunOptions(output, [grid]) { Driver = "Win32ConsoleDriver" });
        var result = Assert.Single(batch.Results);
        Assert.True(result.Outcome == ScenarioOutcomes.Passed, result.Reason + " | " + batch.RunRoot);
        Assert.True(result.TargetExited); Assert.True(result.CleanupSucceeded); Assert.True(result.FixtureIntegrityVerified);
        Assert.NotEmpty(result.Checkpoints);
        foreach (var checkpoint in result.Checkpoints)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, checkpoint.Metadata)));
            var native = doc.RootElement.GetProperty("native");
            Assert.Equal("Win32ConsoleDriver", native.GetProperty("driver").GetString());
            Assert.Equal("host-window", native.GetProperty("captureLayer").GetString());
            Assert.Equal("ConsoleWindowClass", native.GetProperty("windowClass").GetString());
        }
    }
    [Fact]
    public async Task ConsoleRefusesMouseScenarioBeforeLaunch()
    {
        var scenario = new TestScenario("commander/input/menu-mouse", "Mouse", "input", "Unsupported Console mouse").Click(TestPoint.At(1,1)).Checkpoint("desktop").Descriptor;
        Assert.True(NativeExecution.Supports(scenario, "SDLDriver"));
        Assert.False(NativeExecution.Supports(scenario, "Win32ConsoleDriver"));
        string output = Path.Combine(Path.GetTempPath(), "wb-refusal-"+Guid.NewGuid().ToString("N"));
        try
        {
            var batch = await new ScenarioRunner().RunAsync("missing.exe", [scenario], new RunOptions(output,["80x25"]) { Driver="Win32ConsoleDriver" });
            Assert.Equal(ScenarioOutcomes.Skipped, Assert.Single(batch.Results).Outcome);   // Driver prerequisite
            Assert.Empty(Directory.GetFiles(batch.RunRoot,"*.png"));
        }
        finally { if(Directory.Exists(output)) Directory.Delete(output,true); }
    }

    [ConsoleIntegration]
    [InlineData(true)]
    public async Task CancelInRealConsoleModal(bool _)
    {
        string target=Environment.GetEnvironmentVariable("TSWB_COMMANDER_CONSOLE_TARGET")!;
        string output=Environment.GetEnvironmentVariable("TSWB_COMMANDER_CONSOLE_OUTPUT")!;
        var runner=new ScenarioRunner(); var scenario=await CommanderExternalCatalog.SingleAsync(runner,target,"commander/keyboard/copy-rename-here");
        using var cancel=new CancellationTokenSource(); bool modal=false;
        var batch=await runner.RunAsync(target,[scenario],new RunOptions(output,["80x25"]){Driver="Win32ConsoleDriver"},message=>
        {if(message.Trim()=="checkpoint copy-here"){modal=true;cancel.Cancel();}},cancel.Token);
        var result=Assert.Single(batch.Results); Assert.True(modal); Assert.Equal(ScenarioOutcomes.Aborted,result.Outcome);
        Assert.True(result.CleanupSucceeded);Assert.True(result.FixtureIntegrityVerified);Assert.Single(result.Checkpoints);
    }

    [Fact]
    public void ConsoleProjectPersistsCustomGridAndNativeIdentity()
    {
        string root=Path.Combine(Path.GetTempPath(),"wb-project-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"console.twproj");
            var project=new WorkbenchProject{Target=Path.Combine(root,"Commander.exe"),OutputRoot=Path.Combine(root,"runs"),Mode="Win32ConsoleDriver",TestType="rendering",ScreenSizes=["80x25","140x40","100x30"],CustomWidth=100,CustomHeight=30};
            project.Save(path);var loaded=WorkbenchProject.Load(path);
            Assert.Equal(project.Fingerprint(),loaded.Fingerprint());
        }
        finally{Directory.Delete(root,true);}
    }

}

