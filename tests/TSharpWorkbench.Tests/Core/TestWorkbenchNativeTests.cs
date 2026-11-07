using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchNativeTests
{
    [Theory]
    [InlineData("SDLGpuDriver-Metal")]
    [InlineData("unknown")]
    public void UnqualifiedModesAreRefusedWithoutFallback(string mode)
    {
        var error = Assert.Throws<ArgumentException>(() => WorkbenchCapabilities.Validate(mode));
        Assert.Contains("no fallback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedNativeScenarioDoesNotLaunchOrWriteLogicalPng()
    {
        string root = Path.Combine(Path.GetTempPath(), "wb-refusal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var scenario = new TestScenario("sample/unsupported", "Unsupported", "sample", "Not qualified for native mode.")
                .Checkpoint("desktop").Descriptor;
            var batch = await new ScenarioRunner().RunAsync("missing-target.exe", [scenario], new RunOptions(root, ["80x25"]) { Driver = "SDLDriver" });
            var result = Assert.Single(batch.Results);
            Assert.Equal(ScenarioOutcomes.Skipped, result.Outcome);   // Driver prerequisite
            Assert.Empty(result.Checkpoints);
            Assert.Empty(Directory.GetFiles(batch.RunRoot, "*.png"));
            Assert.Contains("no logical fallback", result.Reason);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, "run.json")));
            Assert.Equal("SDLDriver", json.RootElement.GetProperty("requestedDriver").GetString());
            Assert.Contains("renderer-target", File.ReadAllText(batch.Report!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LogicalBaselineManifestIsRefusedBeforeNativeTargetLaunch()
    {
        var scenario = new TestScenario("commander/menu/f10-open-close", "Menu", "smoke", "Native subset.").Checkpoint("desktop").Descriptor;
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new ScenarioRunner().RunAsync("missing-target.exe", [scenario],
            new RunOptions("unused", Baselines: "missing-baseline.json") { Driver = "SDLDriver" }));
        Assert.Contains("Logical visual baselines", error.Message);
    }
}
