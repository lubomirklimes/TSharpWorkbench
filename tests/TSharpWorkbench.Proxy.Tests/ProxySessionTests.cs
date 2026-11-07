using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

[Trait("Category", "ProxySession")]
public sealed class ProxySessionTests
{
    private static string Target => Environment.GetEnvironmentVariable("TSWB_PROXY_TARGET") ?? throw new InvalidOperationException("Set TSWB_PROXY_TARGET.");
    private static string Results => Environment.GetEnvironmentVariable("TSWB_PROXY_RESULTS") ?? throw new InvalidOperationException("Set TSWB_PROXY_RESULTS.");
    private static string Pilot => Environment.GetEnvironmentVariable("TSWB_PROXY_SCENARIO") ?? throw new InvalidOperationException("Set TSWB_PROXY_SCENARIO.");
    private static TargetSession Session(CancellationToken token = default, int ms = 3000) => new(Target, null, "80x25", token, ms, 15, "NullDriver", true);

    [Theory]
    [InlineData("80x25")]
    [InlineData("140x40")]
    public async Task OrdinaryBinaryRunsExternalJsonWithMultipleUiCaptures(string grid)
    {
        byte[] before = File.ReadAllBytes(Path.ChangeExtension(Target, ".dll"));
        using (var standalone = Process.Start(new ProcessStartInfo(Target) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "--smoke" },
            Environment = { ["TSHARPVISION_DRIVER"] = "NullDriver", ["TSHARPVISION_DRIVER_DECORATOR"] = "", ["TSHARPVISION_PROXY_SESSION"] = "" } })!)
        {
            await standalone.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, standalone.ExitCode);
            Assert.Contains("ACTIVE_DRIVER=TSharpVision.Drivers.NullDriver", await standalone.StandardOutput.ReadToEndAsync());
        }
        var runner = new ScenarioRunner();
        var selected = await runner.DiscoverAsync(Target, [Pilot]);
        Assert.Single(selected); Assert.Equal("", selected[0].External!.FixtureId);
        var batch = await runner.RunAsync(Target, selected, new RunOptions(Results, [grid]));
        var result = Assert.Single(batch.Results);
        Assert.True(result.Outcome == ScenarioOutcomes.Passed, result.Reason);
        Assert.True(result.TargetExited); Assert.True(result.CleanupSucceeded); Assert.True(result.FixtureIntegrityVerified);
        Assert.Equal(3, result.Checkpoints.Length);
        long revision = 0; int uiThread = 0;
        foreach (var cp in result.Checkpoints)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, cp.Metadata)));
            var proxy = doc.RootElement.GetProperty("proxy");
            Assert.True(proxy.GetProperty("revision").GetInt64() > revision); revision = proxy.GetProperty("revision").GetInt64();
            int thread = proxy.GetProperty("uiThread").GetInt32(); Assert.True(thread > 0);
            if (uiThread != 0) Assert.Equal(uiThread, thread); uiThread = thread;
            Assert.Equal("logical", doc.RootElement.GetProperty("captureLayer").GetString());
            if (cp != result.Checkpoints[0]) Assert.True(proxy.GetProperty("writesMatchRoot").GetBoolean());
        }
        Assert.Equal(before, File.ReadAllBytes(Path.ChangeExtension(Target, ".dll")));
        string deps = File.ReadAllText(Path.ChangeExtension(Target, ".deps.json"));
        Assert.DoesNotContain("TSharpWorkbench.Testing", deps); Assert.DoesNotContain("Workbench", deps);
    }

    [Fact]
    public async Task NegativeAssertionUsesFiveFreshCapturesWithoutReplayingInput()
    {
        var runner = new ScenarioRunner();
        var scenario = (await runner.DiscoverAsync(Target, [Pilot]))[0];
        var checkpoint = scenario.Steps[2] with { Assertions = [new AssertionSpec("wrong", "screen-contains", Text: "Impossible count")], Retry = new() };
        scenario = scenario with { Steps = [scenario.Steps[1], checkpoint] };
        var batch = await runner.RunAsync(Target, [scenario], new RunOptions(Results, ["80x25"]));
        var result = Assert.Single(batch.Results); Assert.Equal(ScenarioOutcomes.Failed, result.Outcome); Assert.True(result.CleanupSucceeded);
        var cp = Assert.Single(result.Checkpoints);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, cp.Metadata)));
        Assert.Equal(5, doc.RootElement.GetProperty("attempts").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("attempts")[4].GetProperty("elapsedMilliseconds").GetInt64() >= 1000);
        Assert.Single(doc.RootElement.GetProperty("interactionsSincePreviousCheckpoint").EnumerateArray());
        var cells = ScreenSnapshot.Deserialize(File.ReadAllBytes(Path.Combine(batch.RunRoot, cp.Cells)));
        Assert.Contains("Count: 1", Text(cells)); Assert.DoesNotContain("Count: 2", Text(cells));
        Assert.Equal(5, doc.RootElement.GetProperty("proxy").GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task ResizeDetectsNewEpochAndInputStateUsesExistingPlanner()
    {
        await using var session = Session(); var hello = await session.ConnectAsync();
        Assert.Equal("proxy-logical", hello.Capabilities!.Mode); Assert.True(hello.Capabilities.Resize); Assert.False(hello.Capabilities.NativeCapture);
        Assert.Equal("NullDriver", hello.Target!.Driver);
        await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
        var first = (await session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, "initial")).Capture!;
        long sequence = 0;
        foreach (var step in new InputStep[] { new("key-down", "kbF2"), new("key-up", "kbF2"), new("modifier", "shift-down"),
            new("modifier", "shift-up"), new("mouse", "move@1,1"), new("text", "abc"), new("release", "all"), new("resize", "140x40") })
        {
            var reply = await session.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: step);
            Assert.Equal(++sequence, reply.Interaction!.Sequence); Assert.Equal("accepted-queue", reply.Interaction.Acknowledgement);
        }
        var resized = (await session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, "resized", evidence: ["view-tree", "menu", "status"])).Capture!;
        Assert.Equal(140, resized.Width); Assert.Equal(40, resized.Height); Assert.True(resized.Proxy!.Epoch > first.Proxy!.Epoch);
        Assert.True(resized.Proxy.BufferReplaced); Assert.Contains("Count: 1", Text(ScreenSnapshot.Deserialize(Convert.FromBase64String(resized.Cells))));
        Assert.NotNull(resized.Inspection!.ViewTree);
        await session.SendAsync(Protocol.Operations.Complete, Protocol.States.Completed);
        await session.SendAsync(Protocol.Operations.Shutdown, Protocol.States.ShutdownAccepted); await session.WaitForExitAsync();
    }

    [Fact]
    public async Task UnsupportedOperationsRejectAndCompiledFallbackCannotOccur()
    {
        await using var session = Session(); await session.ConnectAsync();
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync("Unknown", Protocol.States.Completed));
        await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: new("action", "not-registered")));
        await session.SendAsync(Protocol.Operations.Shutdown, Protocol.States.ShutdownAccepted); await session.WaitForExitAsync();
    }

    [Fact]
    public async Task AbruptExitDuringCheckpointAndEofCleanupAreBounded()
    {
        var session = Session();
        try
        {
            await session.ConnectAsync(); await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                // Accepted-queue ACK can race a process that exits immediately while consuming the key.
                await session.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: new("key", "kbF8"));
                await session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, "after-exit");
            });
        }
        finally { await session.DisposeAsync(); }
        Assert.True(session.Cleanup); Assert.Equal(23, session.ExitCode);
        var eof = Session(); await eof.ConnectAsync(); await eof.DisposeAsync(); Assert.True(eof.Cleanup); Assert.Equal(0, eof.ExitCode);
    }

    [Fact]
    public async Task UiDeadlineAndControllerCancellationDoNotLeaveOwnedChildren()
    {
        var deadline = Session(ms: 500);
        try
        {
            await deadline.ConnectAsync(); await deadline.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
            await deadline.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: new("key", "kbF6"));
            await Assert.ThrowsAsync<TimeoutException>(() => deadline.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, "blocked"));
        }
        finally { await deadline.DisposeAsync(); }
        Assert.True(deadline.Cleanup);
        using var cancel = new CancellationTokenSource(); var session = Session(cancel.Token);
        try
        {
            await session.ConnectAsync(); await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
            await session.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: new("key", "kbF6"));
            cancel.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, "cancelled"));
        }
        finally { await session.DisposeAsync(); }
        Assert.True(session.Cleanup);
    }

    [Fact]
    public async Task UnresponsiveOwnedChildIsTerminatedAfterGraceTimeout()
    {
        var session = Session(ms: 500);
        try
        {
            await session.ConnectAsync(); await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
            await session.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: new("key", "kbF7"));
            await Assert.ThrowsAsync<TimeoutException>(() => session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, "blocked"));
        }
        finally { await session.DisposeAsync(); }
        Assert.True(session.Cleanup); Assert.Equal("owned target terminated after grace timeout", session.StopOutcome);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("oversized")]
    [InlineData("replay")]
    [InlineData("console-wrong-secret")]
    [InlineData("console-oversized")]
    [InlineData("console-replay")]
    public async Task RawEndpointFailsClosedOnAuthenticationFramingAndReplay(string attack)
    {
        string instance = Guid.NewGuid().ToString("N"), secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(Target) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("TSHARPVISION_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        bool console = attack.StartsWith("console-");
        if (console) attack = attack["console-".Length..];
        start.Environment["TSHARPVISION_DRIVER"] = console ? "Win32ConsoleDriver" : "NullDriver";
        if (console) start.Environment["TSHARPVISION_OWNED_CONSOLE"] = "1";
        start.Environment[TestTargetHost.SizeVariable] = "80x25";
        start.Environment["TSHARPVISION_DRIVER_DECORATOR"] = "TSharpWorkbench.Proxy.WorkbenchProxyDriverFactory, TSharpWorkbench.Proxy";
        start.Environment["TSHARPVISION_PROXY_SESSION"] = "1";
        start.Environment[TestTargetHost.InstanceVariable] = instance; start.Environment[TestTargetHost.SecretVariable] = secret;
        using var owner = console ? OwnedConsoleProcess.Start(start) : null;
        using var child = owner?.Process ?? Process.Start(start)!;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "tsharp-diagnostics-" + instance, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var bound = new CancellationTokenSource(5000); await pipe.ConnectAsync(bound.Token);
            var request = new Request(Protocol.Version, Protocol.Schema, instance, "unique", attack == "wrong-secret" ? new string('0',64) : secret, Protocol.Operations.Handshake);
            if (attack == "oversized")
            {
                await pipe.WriteAsync(BitConverter.GetBytes(Protocol.MaximumBytes + 1), bound.Token);
                await Assert.ThrowsAnyAsync<IOException>(() => Protocol.ReadAsync<Reply>(pipe, bound.Token));
            }
            else
            {
                await Protocol.WriteAsync(pipe, request, bound.Token);
                var reply = await Protocol.ReadAsync<Reply>(pipe, bound.Token);
                if (attack == "replay")
                {
                    Assert.Equal(Protocol.States.Ready, reply.State);
                    await Protocol.WriteAsync(pipe, request, bound.Token); reply = await Protocol.ReadAsync<Reply>(pipe, bound.Token);
                }
                Assert.Equal(Protocol.States.Rejected, reply.State);
            }
            await child.WaitForExitAsync(bound.Token); Assert.Equal(0, child.ExitCode);
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }

    [Fact]
    public void ResponsesWithWrongIdentityOrSchemaAreNeverAccepted()
    {
        Assert.Throws<IOException>(() => TargetSession.Validate(new("other", Protocol.States.Ready, Protocol.Schema, "instance"), "wanted", "instance", Protocol.States.Ready));
        Assert.Throws<IOException>(() => TargetSession.Validate(new("wanted", Protocol.States.Ready, "older", "instance"), "wanted", "instance", Protocol.States.Ready));
    }

    [Fact]
    public async Task PartialStartupFailureIsCleanedWithoutFallback()
    {
        var session = new TargetSession(Target, null, "80x25", default, 1000, 10, "MissingBackend", true);
        try { await Assert.ThrowsAsync<IOException>(() => session.ConnectAsync()); }
        finally { await session.DisposeAsync(); }
        Assert.True(session.Cleanup); Assert.NotEqual(0, session.ExitCode);
    }

    [Fact]
    public async Task SdlOrdinaryTargetRetainsBackendIdentityAndLogicalEvidence()
    {
        var runner = new ScenarioRunner();
        var selected = await runner.DiscoverAsync(Target, [Pilot]);
        var batch = await runner.RunAsync(Target, selected, new RunOptions(Results, ["120x37"]) { ProxyBackend = "SDLDriver" });
        var result = Assert.Single(batch.Results); Assert.True(result.Outcome == ScenarioOutcomes.Passed, result.Reason);
        Assert.True(result.TargetExited); Assert.True(result.CleanupSucceeded);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, result.Checkpoints[1].Metadata)));
        Assert.Equal("SDLDriver", doc.RootElement.GetProperty("driver").GetString());
        Assert.Equal("logical", doc.RootElement.GetProperty("captureLayer").GetString());
        Assert.Contains("SDLDriver", doc.RootElement.GetProperty("proxy").GetProperty("actualBackend").GetString());
    }

    private static string Text(ScreenSnapshot cells) => string.Join("\n", Enumerable.Range(0,cells.Height)
        .Select(y => new string(Enumerable.Range(0,cells.Width).Select(x => cells[x,y].Character).ToArray())));
}
