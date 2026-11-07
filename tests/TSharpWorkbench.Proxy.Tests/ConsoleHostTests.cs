using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

public sealed class ConsoleHostTests
{
    private static string Target => Environment.GetEnvironmentVariable("TSWB_PROXY_TARGET")!;
    private static string Results => Environment.GetEnvironmentVariable("TSWB_PROXY_RESULTS")!;
    private static TargetSession Session(CancellationToken token=default, int ms=1500, string grid="80x25") =>
        new(Target,null,grid,token,ms,20,"Win32ConsoleDriver",true);

    [Theory]
    [InlineData("normal")]
    [InlineData("eof")]
    [InlineData("crash")]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    [InlineData("force")]
    [InlineData("quit")]
    public async Task OwnedLifecycleAndWindowRelease(string mode)
    {
        string parent = ParentFacts();
        using var cancelled = new CancellationTokenSource();
        var session = Session(cancelled.Token);
        ConsoleHostEvidence? native = null;
        Process? owned = null;
        try
        {
            var hello = await session.ConnectAsync(); native=hello.Target!.Console!;
            await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ConnectAsync());
            owned=Process.GetProcessById(native.ProcessId); _=owned.Handle;
            Assert.NotEqual(Environment.ProcessId,native.ProcessId);
            Assert.True(IsWindow((IntPtr)native.WindowHandle));
            await session.SendAsync(Protocol.Operations.Start,Protocol.States.Started);
            if (mode=="normal")
            {
                await session.SendAsync(Protocol.Operations.Shutdown,Protocol.States.ShutdownAccepted);
                await session.WaitForExitAsync();
            }
            else if(mode=="quit")
            {
                await Assert.ThrowsAnyAsync<Exception>(async()=>{
                    await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key","kbAltX"));
                    await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"after-quit");
                });
            }
            else if(mode=="crash")
            {
                await Assert.ThrowsAnyAsync<Exception>(async()=>{
                    await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key","kbF8"));
                    await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"after-crash");
                });
            }
            else if(mode is "timeout" or "cancellation" or "force")
            {
                await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key",mode=="force"?"kbF7":"kbF6"));
                if(mode=="cancellation") cancelled.CancelAfter(100);
                if(mode=="cancellation")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"blocked"));
                else
                    await Assert.ThrowsAsync<TimeoutException>(()=>session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"blocked"));
            }
        }
        finally { await session.DisposeAsync(); await session.DisposeAsync(); }
        Assert.True(session.Cleanup,session.CleanupFailure); Assert.NotNull(native); Assert.NotNull(owned);
        Assert.True(owned.HasExited);
        if(mode=="crash") Assert.Equal(23,session.ExitCode);
        if(mode is "normal" or "eof" or "quit") Assert.Equal(0,session.ExitCode);
        if(mode=="force") Assert.Equal("owned target terminated after grace timeout",session.StopOutcome);
        await WindowClosed(native.WindowHandle);
        Assert.Equal(parent,ParentFacts());
        File.WriteAllText(Path.Combine(Results,"lifecycle-"+mode+".json"),
            JsonSerializer.Serialize(new{native,session.ExitCode,session.StopOutcome,session.Cleanup,parentUnchanged=true,windowClosed=true}));
        owned.Dispose();
    }

    [Fact]
    public async Task IdleWaitHasBoundedCpuAndLogicalInputWakesBothLoops()
    {
        var session=Session(ms:3000);
        var hello=await session.ConnectAsync();
        var native=hello.Target!.Console!;
        using var process=Process.GetProcessById(native.ProcessId);
        try
        {
            await session.SendAsync(Protocol.Operations.Start,Protocol.States.Started);
            process.Refresh(); var before=process.TotalProcessorTime;
            var watch=Stopwatch.StartNew(); await Task.Delay(1200);
            process.Refresh(); var cpu=process.TotalProcessorTime-before;
            Assert.True(cpu.TotalMilliseconds<300,$"Idle CPU {cpu.TotalMilliseconds} ms / {watch.ElapsedMilliseconds} ms");
            var wake=Stopwatch.StartNew();
            await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key","kbF2"));
            var cp=(await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"awake")).Capture!;
            Assert.Contains("Count: 1",Text(cp));
            long wakeMs=wake.ElapsedMilliseconds;
            Assert.True(wakeMs<3000);
            await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key","kbF3"));
            var modal=(await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"modal")).Capture!;
            Assert.Contains("Ordinary modal",Text(modal));
            await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("key","kbEsc"));
            Assert.Contains("Modal closed",Text((await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"closed")).Capture!));
            await session.SendAsync(Protocol.Operations.Shutdown,Protocol.States.ShutdownAccepted);
            await session.WaitForExitAsync();
            File.WriteAllText(Path.Combine(Results,"idle-wake.json"),JsonSerializer.Serialize(new{cpuMilliseconds=cpu.TotalMilliseconds,wallMilliseconds=watch.ElapsedMilliseconds,wakeMilliseconds=wakeMs,native}));
        }
        finally { await session.DisposeAsync(); }
        Assert.True(session.Cleanup); await WindowClosed(native.WindowHandle);
    }

    [Fact]
    public async Task InitializationGeometryFailureReleasesOwnedProcess()
    {
        var session=Session(ms:1000,grid:"32767x32767");
        try { await Assert.ThrowsAsync<IOException>(()=>session.ConnectAsync()); }
        finally { await session.DisposeAsync(); }
        Assert.True(session.Cleanup); Assert.NotEqual(0,session.ExitCode);
        Assert.NotNull(session.OwnedProcessId);
        Assert.Throws<ArgumentException>(()=>Process.GetProcessById(session.OwnedProcessId.Value));
    }

    [Fact]
    public void FailedCreateProcessReleasesJob()
    {
        Assert.ThrowsAny<Exception>(()=>OwnedConsoleProcess.Start(new ProcessStartInfo(Path.Combine(Results,"missing-owned-target.exe")){WorkingDirectory=Results}));
    }

    [Fact]
    public async Task ControllerTerminationClosesOnlyOwnedJob()
    {
        string parent=ParentFacts();
        string marker=Path.Combine(Results,"controller-child.json");
        string controller=Environment.GetEnvironmentVariable("TSWB_PROXY_CONTROLLER")!;
        var start=new ProcessStartInfo(controller){UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add(Target);start.ArgumentList.Add(marker);
        using var process=Process.Start(start)!;
        Process? target=null;
        try
        {
            using var bound=new CancellationTokenSource(10000);
            while(!File.Exists(marker))
            {
                Assert.False(process.HasExited,"Controller failed before native handshake.");
                await Task.Delay(25,bound.Token);
            }
            var native=JsonSerializer.Deserialize<ConsoleHostEvidence>(File.ReadAllText(marker))!;
            target=Process.GetProcessById(native.ProcessId);_=target.Handle;
            process.Kill(); await process.WaitForExitAsync(bound.Token);
            await target.WaitForExitAsync(bound.Token);
            await WindowClosed(native.WindowHandle);
            Assert.Equal(parent,ParentFacts());
        }
        finally
        {
            if(!process.HasExited){process.Kill();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
            target?.Dispose();
        }
    }

    [Fact]
    public async Task SeparateRealConsoleParentRetainsModesGeometryCursorPaletteTitleAndCodePages()
    {
        string marker=Path.Combine(Results,"real-parent-isolation.json");
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("TSWB_PROXY_CONTROLLER")!);
        start.Environment["TSHARPVISION_OWNED_CONSOLE"]="1";
        start.Environment["TSW_CONSOLE_TARGET"]=Target;
        start.Environment["TSW_CONSOLE_MARKER"]=marker;
        start.Environment["TSW_CONSOLE_PARENT_PROBE"]="1";
        using var owner=OwnedConsoleProcess.Start(start);
        using var parent=owner.Process;
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0,parent.ExitCode);
        using var facts=JsonDocument.Parse(File.ReadAllText(marker));
        Assert.Equal(facts.RootElement.GetProperty("before").GetString(),facts.RootElement.GetProperty("after").GetString());
        Assert.True(facts.RootElement.GetProperty("Cleanup").GetBoolean());
        Assert.True(facts.RootElement.GetProperty("inputPrefixPreserved").GetBoolean());
    }

    [Fact]
    public async Task ActualDriverRendersFragmentsOverlapsCursorClearAndRestoresModes()
    {
        string marker=Path.Combine(Results,"native-rendering.json");
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("TSWB_PROXY_CONTROLLER")!);
        start.Environment["TSHARPVISION_OWNED_CONSOLE"]="1";
        start.Environment["TSW_CONSOLE_TARGET"]=Target;
        start.Environment["TSW_CONSOLE_MARKER"]=marker;
        start.Environment["TSW_CONSOLE_RENDER_PROBE"]="1";
        using var owner=OwnedConsoleProcess.Start(start);
        using var process=owner.Process;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode==0,File.Exists(marker)?File.ReadAllText(marker):"No rendering diagnostics.");
        using var facts=JsonDocument.Parse(File.ReadAllText(marker));
        Assert.True(facts.RootElement.GetProperty("allPassed").GetBoolean());
    }

    [Fact]
    public async Task RedirectedConsoleWithoutOwnedHostIsRejectedBeforeInitialization()
    {
        var start=new ProcessStartInfo(Target) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string key in start.Environment.Keys.Where(k=>k.StartsWith("TSHARPVISION_",StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["TSHARPVISION_DRIVER"]="Win32ConsoleDriver";
        start.Environment["TSHARPVISION_DRIVER_DECORATOR"]="TSharpWorkbench.Proxy.WorkbenchProxyDriverFactory, TSharpWorkbench.Proxy";
        start.Environment["TSHARPVISION_PROXY_SESSION"]="1";
        start.Environment[TestTargetHost.SizeVariable]="80x25";
        using var process=Process.Start(start)!;
        var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(0,process.ExitCode);
        Assert.Contains("owned interactive console host",await error);
    }

    private static string Text(CheckpointCapture cp)
    {
        var cells=ScreenSnapshot.Deserialize(Convert.FromBase64String(cp.Cells));
        return string.Join("\n",Enumerable.Range(0,cells.Height).Select(y=>new string(Enumerable.Range(0,cells.Width).Select(x=>cells[x,y].Character).ToArray())));
    }

    private static async Task WindowClosed(long window)
    {
        using var limit=new CancellationTokenSource(5000);
        while(IsWindow((IntPtr)window)) await Task.Delay(20,limit.Token);
    }

    private static string ParentFacts()
    {
        var input=GetStdHandle(-10);var output=GetStdHandle(-11);
        bool inputOk=GetConsoleMode(input,out var im);bool outputOk=GetConsoleMode(output,out var om);
        return JsonSerializer.Serialize(new {input=input.ToInt64(),output=output.ToInt64(),inputOk,outputOk,im,om,window=GetConsoleWindow().ToInt64(),inputCodePage=GetConsoleCP(),outputCodePage=GetConsoleOutputCP()});
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle,out uint mode);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
}
