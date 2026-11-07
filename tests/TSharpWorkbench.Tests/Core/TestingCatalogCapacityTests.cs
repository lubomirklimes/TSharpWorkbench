using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestingCatalogCapacityTests
{
    private static ScenarioDescriptor[] Catalog(int count) => Enumerable.Range(0, count).Select(i =>
        new ScenarioDescriptor($"synthetic/{i:000}", $"Scenario {i}", new string('d', 400),
            i % 2 == 0 ? "Synthetic/Even" : "Synthetic/Odd", new ScenarioGrid(140, 40, 140, 40, ["140x40"]), [new(ScenarioStep.Checkpoint, "initial")])).ToArray();

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(32)] [InlineData(33)]
    [InlineData(64)] [InlineData(147)] [InlineData(250)]
    public async Task DiscoveryFrameValidationSelectionAndBatchIdentity(int count)
    {
        var catalog = Catalog(count);
        // Same bounded transport serialization and validation as DiscoverAsync;
        // no Commander process or provider is used by these synthetic catalogs.
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, new Reply("hello", "Ready", Protocol.Schema, Scenarios: catalog), default);
        stream.Position = 0;
        var found = (await Protocol.ReadAsync<Reply>(stream, default)).Scenarios!;
        ScenarioRunner.ValidateCatalog(found);
        Assert.Equal(catalog.Select(s => s.Id), found.Select(s => s.Id));
        Assert.Equal(count == 0 ? 0 : 3 - (count == 1 ? 1 : 0), Cli.Groups(found).Length);
        if (count == 0)
        {
            Assert.Throws<ArgumentException>(() => ScenarioSelection.Resolve(found, [], null, true));
            await Assert.ThrowsAsync<ArgumentException>(() => new ScenarioRunner().RunAsync("unused", [], ["80x25"], "unused"));
            return;
        }
        var all = ScenarioSelection.Resolve(found, [], null, true);
        var group = ScenarioSelection.Resolve(found, [], "Synthetic/Even", false);
        Assert.Equal((count + 1) / 2, group.Length);
        var options = Cli.Parse(["--scenario", catalog[^1].Id]);
        Assert.Equal(catalog[^1].Id, ScenarioSelection.Resolve(found, options["--scenario"].Split(','), null, false).Single().Id);
        using var driver = new DriverScope();
        using var app = new TApplication();
        var scroll = new TScrollBar(new(35, 1, 36, 8));
        using var list = new ScenarioList(new(1, 1, 35, 8), scroll);
        app.DeskTop!.Insert(scroll); app.DeskTop.Insert(list); list.LoadCatalog(found); list.Select();
        foreach (int index in new[] { 0, 31, 32, 63, 64, count - 1 }.Distinct().Where(i => i < count))
        {
            list.FocusItemNum(index); int top = list.topItem;
            var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = Keys.kbSpace;
            list.HandleEvent(ref ev);
            Assert.Contains(found[index].Id, list.MarkedIds);
            Assert.StartsWith("[x]", list.GetText(index, 80));
            Assert.Equal(index, list.focused); Assert.Equal(top, list.topItem);
        }
        int markedCount = list.MarkedIds.Count;
        list.Toggle(found[^1].Id); Assert.Equal(markedCount - 1, list.MarkedIds.Count);
        list.Toggle(found[^1].Id);
        var marked = ScenarioSelection.Resolve(found, list.MarkedIds, null, false);
        Assert.Equal(markedCount, marked.Length);
        // Unsupported grid intentionally blocks before any target launch, while the
        // real shared batch loop must retain every ID, result and artifact identity.
        string root = Path.Combine(Path.GetTempPath(), "catalog-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var selection in new[] { marked, group, all })
            {
                var batch = await new ScenarioRunner().RunAsync("unused", selection, ["80x25"], root);
                Assert.Equal(selection.Select(s => s.Id), batch.Results.Select(r => r.ScenarioId));
                Assert.All(batch.Results, r => Assert.Equal("Blocked", r.Outcome));
                Assert.Equal(1, batch.ExitCode);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        list.LoadCatalog(found.Reverse().ToArray()); Assert.Empty(list.MarkedIds);
        list.Toggle(found[^1].Id);
        Assert.StartsWith("[x]", list.GetText(0, 80));
        list.LoadCatalog(Catalog(1)); Assert.Empty(list.MarkedIds); Assert.Equal(0, list.focused);
    }

    [Fact]
    public async Task SyntheticProcessDiscoveryCliAndSuccessfulRunsReach250()
    {
        string root = Path.Combine(Path.GetTempPath(), "catalog-process-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("CATALOG_CAPACITY_COUNT");
        try
        {
            string testing = System.Security.SecurityElement.Escape(typeof(ScenarioRunner).Assembly.Location)!;
            string core = System.Security.SecurityElement.Escape(typeof(TView).Assembly.Location)!;
            await File.WriteAllTextAsync(Path.Combine(root, "target.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="TSharpWorkbench.Testing"><HintPath>{testing}</HintPath></Reference><Reference Include="TSharpVision"><HintPath>{core}</HintPath></Reference></ItemGroup></Project>
                """);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Core", "Fixtures", "CatalogCapacityTarget.cs.txt"), Path.Combine(root, "Program.cs"));
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "build", "target.csproj", "-o", "out", "--nologo", "-v", "quiet" }) start.ArgumentList.Add(arg);
            using var build = System.Diagnostics.Process.Start(start)!;
            var stdout = build.StandardOutput.ReadToEndAsync(); var stderr = build.StandardError.ReadToEndAsync();
            await build.WaitForExitAsync();
            Assert.True(build.ExitCode == 0, await stdout + await stderr);
            string target = Path.Combine(root, "out", OperatingSystem.IsWindows() ? "target.exe" : "target");
            var runner = new ScenarioRunner();
            foreach (int count in new[] { 0, 1, 32, 33, 64, 147, 250 })
            {
                Environment.SetEnvironmentVariable("CATALOG_CAPACITY_COUNT", count.ToString());
                var found = await runner.DiscoverAsync(target);
                Assert.Equal(count, found.Length);
            }
            var catalog = await runner.DiscoverAsync(target);
            Assert.Equal(0, await Cli.RunAsync(["list", "--target", target]));
            Assert.Equal(0, await Cli.RunAsync(["groups", "--target", target]));
            Assert.Equal(0, await Cli.RunAsync(["run", "--target", target, "--scenario", "synthetic/032,synthetic/064,synthetic/249", "--size", "80x25", "--output", Path.Combine(root, "cli")]));
            foreach (var selection in new[]
            {
                ScenarioSelection.Resolve(catalog, ["synthetic/031", "synthetic/032", "synthetic/063", "synthetic/064", "synthetic/249"], null, false),
                ScenarioSelection.Resolve(catalog, [], "Synthetic/Even", false),
                ScenarioSelection.Resolve(catalog, [], null, true)
            })
            {
                var batch = await runner.RunAsync(target, selection, ["80x25"], Path.Combine(root, "runs"));
                Assert.Equal(0, batch.ExitCode);
                Assert.Equal(selection.Select(s => s.Id), batch.Results.Select(r => r.ScenarioId));
                foreach (var result in batch.Results)
                {
                    Assert.True(result.TargetExited && result.FixtureIntegrityVerified && result.CleanupSucceeded);
                    using var metadata = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(batch.RunRoot, result.Checkpoints.Single().Metadata)));
                    Assert.Equal(result.ScenarioId, metadata.RootElement.GetProperty("scenarioId").GetString());
                    // A bare application: the whole screen is captured with no window on the desktop.
                    Assert.False(metadata.RootElement.GetProperty("surface").GetProperty("topViewIsWindow").GetBoolean());
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CATALOG_CAPACITY_COUNT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public void InvalidAndDuplicateDescriptorsRemainRejected()
    {
        var good = Catalog(1)[0];
        foreach (var bad in new[] { good with { Id = " " }, good with { Id = "bad\nID" },
            good with { Grid = good.Grid with { Defaults = [] } }, good with { Grid = good.Grid with { Defaults = ["invalid"] } },
            good with { Grid = null! }, good with { Grid = good.Grid with { MinWidth = 150 } }, good with { Grid = good.Grid with { MaxHeight = 101 } },
            good with { Kind = "gallery" }, good with { ExpectedResult = "Maybe" },
            good with { Steps = [new(ScenarioStep.Resize, "80x25"), new(ScenarioStep.Checkpoint, "a")] },
            good with { Steps = [new(ScenarioStep.Checkpoint, "a", Assertions: [new("Bad Id", AssertionKinds.ScreenContains)])] },
            good with { Steps = [new(ScenarioStep.Checkpoint, "a", Assertions: [new("x", "no-such-kind")])] },
            good with { Steps = [new(ScenarioStep.Checkpoint, "a", Evidence: ["pixels"])] },
            good with { Steps = [new(ScenarioStep.Key, "kbEsc", Evidence: ["menu"]), new(ScenarioStep.Checkpoint, "a")] },
            good with { Metadata = new() { ["Bad Key"] = "v" } },
            good with { Name = "" }, good with { Group = "" }, good with { Steps = null! }, good with { Steps = [] },
            good with { Steps = [new(ScenarioStep.Key, "kbEsc")] }, good with { Steps = [new(ScenarioStep.Key, "kbNoSuchKey"), new(ScenarioStep.Checkpoint, "a")] },
            good with { Steps = [new(ScenarioStep.Checkpoint, "a"), new(ScenarioStep.Checkpoint, "a")] },
            good with { Steps = [new(ScenarioStep.Checkpoint, "Upper")] }, good with { Steps = [new("mouse", "x"), new(ScenarioStep.Checkpoint, "a")] } })
            Assert.Throws<IOException>(() => ScenarioRunner.ValidateCatalog([bad]));
        Assert.Throws<IOException>(() => ScenarioRunner.ValidateCatalog([good, good]));
        Assert.Throws<IOException>(() => ScenarioRunner.ValidateCatalog([null!]));
    }
}
