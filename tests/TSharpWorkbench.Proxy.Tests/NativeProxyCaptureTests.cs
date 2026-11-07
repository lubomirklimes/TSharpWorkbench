using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

public sealed class NativeProxyCaptureTests
{
    private static string Target => Environment.GetEnvironmentVariable("TSWB_PROXY_TARGET")!;
    private static string Results => Environment.GetEnvironmentVariable("TSWB_PROXY_RESULTS")!;

    [Theory]
    [InlineData("SDLDriver", "renderer-pixels", "80x25")]
    [InlineData("SDLDriver", "renderer-pixels", "140x40")]
    [InlineData("SDLGpuDriver", "gpu-completed", "80x25")]
    [InlineData("SDLGpuDriver", "gpu-completed", "140x40")]
    [InlineData("Win32ConsoleDriver", "native-console-cells", "80x25")]
    [InlineData("Win32ConsoleDriver", "native-console-cells", "140x40")]
    public async Task OrdinaryTargetNativeModalResizeAndRepeatedCheckpoints(string backend, string kind, string grid)
    {
        var runner = new ScenarioRunner();
        var source = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TSWB_PROXY_SCENARIO"))!, "windows-multistep.scenario.json");
        var scenarios = await runner.DiscoverAsync(Target, [source]);
        scenarios = scenarios.Select(s => s with { Steps = [..s.Steps,
            new(ScenarioStep.Resize,"80x25"), new(ScenarioStep.Checkpoint,"shrunken"),
            new(ScenarioStep.Checkpoint,"shrunken-stable")] }).ToArray();
        var batch = await runner.RunAsync(Target, scenarios, new RunOptions(Path.Combine(Results,"native-e2e"),[grid])
            {ProxyBackend=backend,ProxyCapture=kind});
        var result = Assert.Single(batch.Results);
        Assert.True(result.Outcome == ScenarioOutcomes.Passed, result.Reason);
        Assert.True(result.CleanupSucceeded); Assert.True(result.TargetExited);
        Assert.Equal(7,result.Checkpoints.Length);
        var hashes = new List<string>(); long lastFrame = 0;
        foreach(var cp in result.Checkpoints)
        {
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(batch.RunRoot,cp.Metadata)));
            var root = metadata.RootElement;
            Assert.Equal("logical",root.GetProperty("captureLayer").GetString());
            var native = root.GetProperty("proxyNative");
            Assert.Equal(kind,native.GetProperty("kind").GetString());
            var details = native.GetProperty("details");
            Assert.Equal(root.GetProperty("proxy").GetProperty("revision").GetInt64().ToString(),details.GetProperty("rootRevision").GetString());
            byte[] data = File.ReadAllBytes(Path.Combine(batch.RunRoot,native.GetProperty("artifact").GetString()!));
            Assert.InRange(data.Length,32,1024*1024);
            Assert.Equal(ScreenSnapshot.Hash(data),native.GetProperty("sha256").GetString());
            hashes.Add(ScreenSnapshot.Hash(data));
            if(kind == ProxyCaptureKinds.Console)
            {
                using var cells = JsonDocument.Parse(data);
                var chars = cells.RootElement.GetProperty("characters").EnumerateArray().Select(c=>(char)c.GetInt32()).ToArray();
                var logical = ScreenSnapshot.Deserialize(File.ReadAllBytes(Path.Combine(batch.RunRoot,cp.Cells)));
                var attrs = cells.RootElement.GetProperty("attributes").EnumerateArray().Select(c=>c.GetInt32()).ToArray();
                for(int i=0;i<chars.Length;i++)
                {
                    var expected=logical[i%logical.Width,i/logical.Width];
                    Assert.Equal(expected.Character == 0 ? ' ' : expected.Character, chars[i]);
                    Assert.Equal((int)(byte)expected.Attr,attrs[i]);
                }
                Assert.True(root.GetProperty("consoleOutputMatchesRoot").GetBoolean());
            }
            else
            {
                int w=BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)),h=BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
                Assert.Equal(root.GetProperty("width").GetInt32()*int.Parse(details.GetProperty("cellWidth").GetString()!),w);
                Assert.Equal(root.GetProperty("height").GetInt32()*int.Parse(details.GetProperty("cellHeight").GetString()!),h);
                byte[] rgb=DecodeRgb(data);
                Assert.True(rgb.Distinct().Count()>4,"Readback must contain actual varied rendered content.");
                // Independent partial oracle: blank-cell backgrounds from logical VGA attributes.
                // This validates colours and spatial alignment, not font raster equivalence.
                var logical=ScreenSnapshot.Deserialize(File.ReadAllBytes(Path.Combine(batch.RunRoot,cp.Cells)));
                int cw=int.Parse(details.GetProperty("cellWidth").GetString()!),ch=int.Parse(details.GetProperty("cellHeight").GetString()!);
                string[] cursor=details.GetProperty("cursor").GetString()!.Split('/')[0].Split(',');
                int checkedCells=0;
                for(int y=0;y<logical.Height;y++)for(int x=0;x<logical.Width;x++)
                {
                    if(logical[x,y].Character!=' ' || x==int.Parse(cursor[0])&&y==int.Parse(cursor[1]))continue;
                    int at=(y*ch+ch/2)*(w*4+1)+1+(x*cw+cw/2)*4;
                    uint colour=(uint)(rgb[at]<<16|rgb[at+1]<<8|rgb[at+2]);
                    Assert.Equal(LogicalPng.Color(logical[x,y].Attr.Background>>4),colour);checkedCells++;
                }
                Assert.True(checkedCells>100);
                if(kind == ProxyCaptureKinds.Gpu)
                {
                    Assert.Equal("shader",details.GetProperty("renderingPath").GetString());
                    Assert.Contains("WaitForGPUFences completed",details.GetProperty("completion").GetString());
                    long frame=long.Parse(details.GetProperty("rendererRevision").GetString()!);
                    Assert.True(frame>lastFrame);lastFrame=frame;
                }
            }
        }
        Assert.Equal(hashes[^2],hashes[^1]); // Repeated capture after downsizing is stable.
        Assert.NotEqual(hashes[0],hashes[1]); // Actual ordinary-app text changed.
        Assert.NotEqual(hashes[1],hashes[2]); // Actual overlapping modal rendered.
    }

    private static byte[] DecodeRgb(byte[] png)
    {
        using var compressed = new MemoryStream();
        for(int at=8;at<png.Length;)
        {
            int count=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if(png.AsSpan(at+4,4).SequenceEqual("IDAT"u8)) compressed.Write(png,at+8,count);
            at+=count+12;
        }
        compressed.Position=0;using var z=new ZLibStream(compressed,CompressionMode.Decompress);
        using var raw=new MemoryStream();z.CopyTo(raw);return raw.ToArray();
    }

    [Theory]
    [InlineData("NullDriver","renderer-pixels")]
    [InlineData("SDLDriver","gpu-completed")]
    [InlineData("Win32ConsoleDriver","renderer-pixels")]
    public async Task UnsupportedKindsFailBeforeOutputOrLaunch(string backend,string kind)
    {
        var runner=new ScenarioRunner();
        var selected=await runner.DiscoverAsync(Target,[Environment.GetEnvironmentVariable("TSWB_PROXY_SCENARIO")!]);
        string output=Path.Combine(Results,"unsupported-"+Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ArgumentException>(()=>runner.RunAsync(Target,selected,new RunOptions(output,["80x25"]){ProxyBackend=backend,ProxyCapture=kind}));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("SDLDriver","normal")]
    [InlineData("SDLGpuDriver","normal")]
    [InlineData("SDLDriver","crash")]
    [InlineData("SDLGpuDriver","crash")]
    [InlineData("SDLDriver","timeout")]
    [InlineData("SDLGpuDriver","timeout")]
    [InlineData("SDLDriver","cancellation")]
    [InlineData("SDLGpuDriver","cancellation")]
    public async Task CaptureThenLifecycleFailureReleasesOwnedTarget(string backend,string mode)
    {
        using var cancelled=new CancellationTokenSource();
        var session=new TargetSession(Target,null,"80x25",cancelled.Token,1000,20,backend,true);
        try
        {
            var hello=await session.ConnectAsync();
            string kind=backend=="SDLDriver"?ProxyCaptureKinds.Renderer:ProxyCaptureKinds.Gpu;
            Assert.Contains(kind,hello.Capabilities!.CaptureKinds);
            if(mode=="normal") await Task.Delay(300); // Deliberate idle observation; capture synchronization still uses the worker/fence.
            await session.SendAsync(Protocol.Operations.Start,Protocol.States.Started);
            await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"before",captureKind:kind);
            if(mode=="normal")
            {
                await session.SendAsync(Protocol.Operations.Shutdown,Protocol.States.ShutdownAccepted);
                await session.WaitForExitAsync();
            }
            else if(mode=="crash")
            {
                // The target may exit before its transport thread writes the Input acknowledgement.
                await Assert.ThrowsAnyAsync<Exception>(async()=>
                {
                    await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key","kbF8"));
                    await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"after",captureKind:kind);
                });
            }
            else
            {
                await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key","kbF6"));
                if(mode=="cancellation")cancelled.CancelAfter(100);
                await Assert.ThrowsAnyAsync<Exception>(()=>session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"after",captureKind:kind));
            }
        }
        finally {await session.DisposeAsync();}
        Assert.True(session.Cleanup,session.CleanupFailure);
        if(mode=="normal")Assert.Equal(0,session.ExitCode);
        if(mode=="crash")Assert.Equal(23,session.ExitCode);
        File.WriteAllText(Path.Combine(Results,$"native-lifecycle-{backend}-{mode}.json"),JsonSerializer.Serialize(new {backend,mode,session.Cleanup,session.ExitCode,session.StopOutcome}));
    }
}
