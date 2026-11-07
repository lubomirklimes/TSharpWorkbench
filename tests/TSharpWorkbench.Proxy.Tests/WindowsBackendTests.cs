using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

/// <summary>Explicit Windows backend qualification. Requires the deployment documented in Windows qualification workflow.</summary>
public sealed class WindowsBackendTests
{
    private static string Target => Environment.GetEnvironmentVariable("TSWB_PROXY_TARGET")!;
    private static string Results => Environment.GetEnvironmentVariable("TSWB_PROXY_RESULTS")!;

    [Theory]
    [InlineData("NullDriver", "80x25")]
    [InlineData("NullDriver", "140x40")]
    [InlineData("SDLDriver", "80x25")]
    [InlineData("SDLDriver", "140x40")]
    [InlineData("SDLGpuDriver", "80x25")]
    [InlineData("SDLGpuDriver", "140x40")]
    [InlineData("Win32ConsoleDriver", "80x25")]
    [InlineData("Win32ConsoleDriver", "140x40")]
    public async Task GridInputModalResizeAndShutdown(string backend, string grid)
    {
        await using var session = new TargetSession(Target, null, grid, default, 5000, 40, backend, true);
        try
        {
            var hello = await session.ConnectAsync();
            if (backend == "Win32ConsoleDriver")
            {
                var native = Assert.IsType<ConsoleHostEvidence>(hello.Target!.Console);
                Assert.Equal(session.OwnedProcessId, native.ProcessId);
                Assert.True(native.WindowVisible);
                Assert.Contains("CREATE_NEW_CONSOLE", native.HostMode);
                Assert.Equal(0x18u, native.InputMode & 0x18);
                Assert.Equal(0u, native.InputMode & 6);
            }
            Assert.Equal(backend, hello.Target!.Driver);
            var (width, height) = ScenarioSelection.ParseSize(grid);
            Assert.Equal(width, hello.Target.Width); Assert.Equal(height, hello.Target.Height);
            Assert.True(hello.Capabilities!.Resize); Assert.Equal(backend != "NullDriver", hello.Capabilities.NativeCapture);
            await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
            var captures = new List<CheckpointCapture>();
            async Task<CheckpointCapture> Capture(string name)
            {
                var cp=(await session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, name)).Capture!;
                if(captures.Count>0)
                {
                    Assert.True(cp.Proxy!.Revision>captures[^1].Proxy!.Revision);
                    Assert.Equal(captures[0].Proxy!.UiThread, cp.Proxy.UiThread);
                }
                if (backend == "Win32ConsoleDriver")
                {
                    Assert.NotNull(cp.Console);
                    Assert.Equal(cp.Width, cp.Console.WindowWidth); Assert.Equal(cp.Height, cp.Console.WindowHeight);
                    Assert.Equal(cp.Width, cp.Console.BufferWidth); Assert.Equal(cp.Height, cp.Console.BufferHeight);
                    Assert.InRange(cp.Console.CursorX, 0, cp.Width-1); Assert.InRange(cp.Console.CursorY, 0, cp.Height-1);
                    Assert.True(cp.ConsoleOutputMatchesRoot, "Actual Win32 output must match logical root.");
                }
                captures.Add(cp); return cp;
            }
            long sequence=0;
            async Task Input(string kind,string name)
            {
                var reply=await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new(kind,name));
                Assert.Equal(++sequence,reply.Interaction!.Sequence);
                Assert.Equal("accepted-queue",reply.Interaction.Acknowledgement);
            }
            var first=await Capture("initial");
            foreach(var step in new InputStep[] {new("key-down","kbF2"),new("key-up","kbF2"),new("key","kbF2"),
                new("modifier","shift-down"),new("modifier","shift-up"),new("mouse","move@1,1"),
                new("mouse","down-left@1,1"),new("mouse","up-left@1,1"),new("mouse","wheel-down@1,1"),new("release","all")})
                await Input(step.Kind,step.Name);
            var count=await Capture("inputs"); Assert.Contains("Count: 2",Text(count)); Assert.True(count.Proxy!.WritesMatchRoot);
            int seen=Convert.ToInt32(System.Text.RegularExpressions.Regex.Match(Text(count), @"Events: ([0-9A-F]+)").Groups[1].Value,16);
            int expected=TSharpVision.Constants.Events.evKeyDown|TSharpVision.Constants.Events.evKeyUp
                |TSharpVision.Constants.Events.evModifierChanged|TSharpVision.Constants.Events.evMouseMove
                |TSharpVision.Constants.Events.evMouseDown|TSharpVision.Constants.Events.evMouseUp|TSharpVision.Constants.Events.evMouseWheel;
            Assert.Equal(expected,seen&expected);
            await Input("key","kbF3"); var modal=await Capture("modal"); Assert.Contains("Ordinary modal",Text(modal));
            await Input("key","kbEsc"); var closed=await Capture("closed"); Assert.Contains("Modal closed",Text(closed));
            string next=grid=="80x25"?"140x40":"80x25";
            await Input("resize",next); var resized=await Capture("resized");
            var (rw,rh)=ScenarioSelection.ParseSize(next);
            Assert.Equal(rw,resized.Width); Assert.Equal(rh,resized.Height);
            Assert.True(resized.Proxy!.Epoch>first.Proxy!.Epoch); Assert.True(resized.Proxy.BufferReplaced);
            var stable=await Capture("stable"); Assert.True(stable.Proxy!.WritesMatchRoot);
            Assert.Equal(resized.Proxy.Epoch,stable.Proxy.Epoch);
            Assert.Equal(Text(resized),Text(stable));
            Directory.CreateDirectory(Results);
            File.WriteAllText(Path.Combine(Results,backend+"-"+grid+".json"),JsonSerializer.Serialize(new {hello,captures},ScenarioRunner.Json));
            await session.SendAsync(Protocol.Operations.Complete,Protocol.States.Completed);
            await session.SendAsync(Protocol.Operations.Shutdown,Protocol.States.ShutdownAccepted);
            await session.WaitForExitAsync();
        }
        catch
        {
            await session.CollectDiagnosticsAsync();
            File.WriteAllText(Path.Combine(Results,backend+"-"+grid+"-failure.txt"),session.Diagnostics??"No exited-target diagnostics.");
            throw;
        }
    }

    [Fact]
    public async Task NativeProviderCombinationRejectsBeforeCreatingArtifactsOrLaunching()
    {
        var runner=new ScenarioRunner();
        var selected=await runner.DiscoverAsync(Target,[Environment.GetEnvironmentVariable("TSWB_PROXY_SCENARIO")!]);
        string output=Path.Combine(Results,"must-not-exist-"+Guid.NewGuid().ToString("N"));
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>runner.RunAsync(Target,selected,new RunOptions(output,["80x25"]){Driver="SDLDriver",ProxyBackend="SDLDriver"}));
        Assert.Contains("another backend",error.Message);Assert.False(Directory.Exists(output));
    }

    private static string Text(CheckpointCapture cp)
    {
        var cells=ScreenSnapshot.Deserialize(Convert.FromBase64String(cp.Cells));
        return string.Join("\n",Enumerable.Range(0,cells.Height).Select(y=>new string(Enumerable.Range(0,cells.Width).Select(x=>cells[x,y].Character).ToArray())));
    }
}
