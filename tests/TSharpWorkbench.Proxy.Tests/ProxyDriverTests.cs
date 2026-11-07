using System.Diagnostics;
using TSharpVision;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Proxy;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace TSharpWorkbench.Proxy.Tests;

public sealed class ProxyDriverTests : IDisposable
{
    private readonly IDriver? oldDriver = TDisplay.driver;
    private readonly ScreenBuffer? oldBuffer = TScreen.ScreenBuffer;
    private readonly string? oldSelection = Environment.GetEnvironmentVariable("TSHARPVISION_DRIVER");
    private readonly string? oldDecorator = Environment.GetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR");
    private readonly TSharpVision.Config.TSharpVisionConfiguration? oldConfig = ScreenDriverFactory.Configuration;
    private const string Provider = "TSharpWorkbench.Proxy.WorkbenchProxyDriverFactory, TSharpWorkbench.Proxy";

    public ProxyDriverTests()
    {
        TDisplay.driver = null;
        TEventQueue.Resume(); Drain(); TEventQueue.ClearPosted();
        TEventQueue.ClaimUiThread();
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", "NullDriver");
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", null);
        ScreenDriverFactory.Configuration = null;
    }
    public void Dispose()
    {
        TDisplay.driver?.Shutdown();
        TDisplay.driver = oldDriver; TScreen.ScreenBuffer = oldBuffer;
        Drain(); TEventQueue.ClearPosted();
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", oldSelection);
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", oldDecorator);
        ScreenDriverFactory.Configuration = oldConfig;
    }

    [Fact]
    public void OrdinaryStartupAndOptInSelectSameBackendWithoutRecursion()
    {
        Assert.IsType<NullDriver>(ScreenDriverFactory.CreateScreenDriver());
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", Provider);
        var proxy = Assert.IsType<WorkbenchProxyDriver>(ScreenDriverFactory.CreateScreenDriver());
        Assert.Equal("NullDriver", proxy.RequestedBackend);
        Assert.Equal(typeof(NullDriver).FullName, proxy.ActualBackend);
        Assert.False(proxy is IIdleWaitDriver);
        Assert.Throws<ArgumentException>(() => WorkbenchProxyDriver.Wrap(proxy));
        using var app = new TApplication();
        Assert.IsType<WorkbenchProxyDriver>(TDisplay.driver);
        app.ShutDown();
    }

    [Fact]
    public void EnvironmentSelectionStillWinsOverConfigurationWithProxyEnabled()
    {
        ScreenDriverFactory.Configuration = new() { Driver = new() { Name = "MissingConfiguredBackend" } };
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", Provider);
        var proxy = Assert.IsType<WorkbenchProxyDriver>(ScreenDriverFactory.CreateScreenDriver());
        Assert.Equal("NullDriver", proxy.RequestedBackend);
        ScreenDriverFactory.Configuration = new() { Driver = new() { Name = "NullDriver" } };
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", " ");
        proxy = Assert.IsType<WorkbenchProxyDriver>(ScreenDriverFactory.CreateScreenDriver());
        Assert.Equal("NullDriver", proxy.RequestedBackend);
    }

    [Fact]
    public void NullBackendModalLifecyclePreservesCompleteCheckpointEquality()
    {
        var proxy = Install(new NullDriver(40, 12));
        using var app = new ModalApp(proxy);
        foreach (var key in new[] { Keys.kbF3, Keys.kbEsc, Keys.kbF4 }) proxy.Inject(TestKeys.KeyDown(key));
        Assert.Equal(0, AppLifecycleGuard.Run(app));
        Assert.True(app.Opened); Assert.True(app.Closed);
        Assert.NotNull(app.ModalCheckpoint);
        Assert.True(app.ModalCheckpoint!.WritesMatchRoot);
        Assert.True(app.Checkpoint!.WritesMatchRoot);
    }

    [Fact]
    public void UnknownRequestedBackendAndMissingProviderFailExplicitly()
    {
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", Provider);
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", "MissingBackend");
        Assert.Contains("no fallback", Assert.Throws<InvalidOperationException>(() => ScreenDriverFactory.CreateScreenDriver()).Message);
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", "NullDriver");
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", "Missing.Provider, Missing.Assembly");
        Assert.ThrowsAny<Exception>(() => ScreenDriverFactory.CreateScreenDriver());
    }

    [Fact]
    public void FullContractDelegatesAndLifecycleHasOneOwner()
    {
        var inner = new RecordingDriver();
        using var proxy = WorkbenchProxyDriver.Wrap(inner);
        proxy.Initialize(); proxy.Suspend(); proxy.Resume();
        Assert.Equal((ushort)8, proxy.GetCols()); Assert.Equal((ushort)4, proxy.GetRows());
        Assert.Equal(TDisplay.SM.CO80, proxy.GetScreenMode());
        proxy.SetScreenMode(TDisplay.SM.Mono);
        Assert.Equal(32, proxy.AllocateScreenBuffer().Size);
        proxy.ClearScreen(8, 4); proxy.SetCursorType(12); proxy.SetCaretPosition(2, 3);
        Assert.Equal((ushort)12, proxy.GetCursorType());
        Assert.Equal((2, 3), proxy.Caret); Assert.Equal((ushort)12, proxy.CursorType);
        proxy.MakeBeep(); proxy.PumpMessages();
        Assert.False(proxy.ReadKeyEvent(out _));
        Assert.Equal(inner.SupportsMouse, proxy.SupportsMouse);
        Assert.Equal(inner.SupportsGraphics, proxy.SupportsGraphics);
        Assert.Equal(inner.SupportsTrueColor, proxy.SupportsTrueColor);
        Assert.Equal(inner.KeyboardCapabilities, proxy.KeyboardCapabilities);
        proxy.Shutdown(); proxy.Shutdown(); proxy.Dispose();
        Assert.Equal(new[] { "init", "suspend", "resume", "mode", "allocate", "clear", "cursor", "caret", "beep", "pump", "read", "shutdown" }, inner.Calls);
        Assert.Throws<InvalidOperationException>(() => { _ = proxy.BeginCaptureNative(ProxyCaptureKinds.Renderer, default); });
        Assert.Throws<InvalidOperationException>(() => proxy.Initialize());
        Assert.Throws<InvalidOperationException>(() => proxy.Inject(TestKeys.KeyDown(Keys.kbEnter)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupFailuresCleanUpOnceAndPreserveBothErrors(bool cleanupFails)
    {
        var inner = new RecordingDriver { FailInit = true, FailShutdown = cleanupFails };
        var proxy = WorkbenchProxyDriver.Wrap(inner);
        var error = Assert.ThrowsAny<Exception>(() => proxy.Initialize());
        if (cleanupFails) Assert.Equal(2, Assert.IsType<AggregateException>(error).InnerExceptions.Count);
        else Assert.Equal("init failed", error.Message);
        proxy.Shutdown();
        Assert.Equal(new[] { "init", "shutdown" }, inner.Calls);
    }

    [Fact]
    public void ShutdownFailureIsPropagatedWithoutRetry()
    {
        var inner = new RecordingDriver { FailShutdown = true };
        var proxy = WorkbenchProxyDriver.Wrap(inner);
        proxy.Initialize();
        Assert.Throws<IOException>(() => proxy.Shutdown());
        proxy.Dispose();
        Assert.Equal(1, inner.Calls.Count(x => x == "shutdown"));
    }

    [Fact]
    public void KeyMouseModifierInjectionRetainsFifoAndClassification()
    {
        var proxy = Install(new NullDriver(8, 4));
        var plan = InputEvents();
        foreach (var e in plan) proxy.Inject(e);
        foreach (var expected in plan)
        {
            TEvent actual = default; TScreen.GetEvent(ref actual);
            Assert.Equal(expected.What, actual.What);
            if (actual.What == Events.evKeyDown) Assert.Equal(expected.keyDown.keyCode, actual.keyDown.keyCode);
            if (actual.What == Events.evModifierChanged) Assert.Equal(expected.Modifiers, actual.Modifiers);
            if ((actual.What & Events.evMouse) != 0)
            {
                Assert.Equal(expected.mouse.where, actual.mouse.where);
                Assert.Equal(expected.mouse.controlKeyState, actual.mouse.controlKeyState);
            }
        }
        TEvent empty = default; TScreen.GetEvent(ref empty); Assert.Equal(Events.evNothing, empty.What);
        Assert.Throws<NotSupportedException>(() => proxy.Inject(new TEvent { What = Events.evCommand }));
        Assert.False(proxy.SupportsMouse); // backend capability stays truthful, simulation is separate
    }
    private static TEvent[] InputEvents()
    {
        var mouse = new TEvent { What = Events.evMouseDown };
        mouse.mouse.where = new TPoint(2, 1); mouse.mouse.buttons = 1; mouse.mouse.controlKeyState = Keys.kbShift;
        var release = mouse; release.What = Events.evMouseUp; release.mouse.buttons = 0;
        return [TestKeys.KeyDown(Keys.kbF3), mouse, new TEvent { What = Events.evModifierChanged, Modifiers = Keys.kbShift }, release, TestKeys.KeyDown(Keys.kbEsc)];
    }

    [Fact]
    public void IncrementalOverlappingAndClippedRectanglesAreCopied()
    {
        var proxy = Install(new NullDriver(8, 4));
        using var app = new TApplication();
        proxy.CaptureCheckpoint(app); // explicit initial oracle synchronization
        var data = Enumerable.Repeat(new TScreenChar('A', 7), 6).ToArray();
        proxy.WriteBuf(1, 1, 3, 2, data);
        for (int y = 1; y <= 2; y++) for (int x = 1; x <= 3; x++) TScreen.ScreenBuffer!.SetChar((uint)x, (uint)y, data[0]);
        data.AsSpan().Fill(new TScreenChar('X', 3)); // mutate caller storage after return
        var overlap = new[] { new TScreenChar('B', 4), new TScreenChar('C', 5), new TScreenChar('D', 6) };
        proxy.WriteBuf(-1, 2, 3, 1, overlap);
        TScreen.ScreenBuffer!.SetChar(0, 2, overlap[1]); TScreen.ScreenBuffer.SetChar(1, 2, overlap[2]);
        var cp = proxy.CaptureCheckpoint(app);
        Assert.True(cp.WritesMatchRoot); Assert.False(cp.Resynchronized);
        Assert.Equal('A', cp.Observed[2, 1].Character);
        Assert.Equal(cp.Root.Serialize(), cp.Observed.Serialize());
        TScreen.ScreenBuffer.SetChar(2, 1, new('Z', 1));
        Assert.Equal('A', cp.Root[2, 1].Character);
        var recovery = proxy.CaptureCheckpoint(app);
        Assert.False(recovery.WritesMatchRoot); Assert.True(recovery.Resynchronized);
        Assert.True(proxy.CaptureCheckpoint(app).WritesMatchRoot);
        app.ShutDown();
    }

    [Fact]
    public void ClearResizeAndRootReplacementRecoverAtCheckpoint()
    {
        var inner = new NullDriver(8, 4);
        var proxy = Install(inner);
        using var app = new TApplication();
        proxy.CaptureCheckpoint(app);
        proxy.ClearScreen(8, 4);
        Assert.False(proxy.CaptureCheckpoint(app).WritesMatchRoot);
        inner.SimulateResize(12, 6); // bypasses proxy allocation, just like native resize
        TEvent resize = default; app.GetEvent(ref resize); app.HandleEvent(ref resize);
        var cp = proxy.CaptureCheckpoint(app);
        Assert.Equal((12, 6), (cp.Root.Width, cp.Root.Height));
        Assert.True(cp.BufferReplaced); Assert.True(cp.Resynchronized);
        Assert.True(proxy.CaptureCheckpoint(app).WritesMatchRoot);
        TScreen.ScreenBuffer = new ScreenBuffer(12, 6); app.buffer = TScreen.ScreenBuffer;
        Assert.True(proxy.CaptureCheckpoint(app).BufferReplaced);
        app.lockFlag = 1;
        Assert.Throws<InvalidOperationException>(() => proxy.CaptureCheckpoint(app));
        app.lockFlag = 0;
        app.ShutDown();
    }

    [Fact]
    public void WriteAndPumpFailuresRemainVisible()
    {
        var inner = new RecordingDriver();
        var proxy = Install(inner);
        using var app = new TApplication();
        var before = proxy.CaptureCheckpoint(app);
        inner.FailWrite = true;
        Assert.Throws<IOException>(() => proxy.WriteBuf(0, 0, 1, 1, new[] { new TScreenChar('X', 7) }));
        var failedWrite = proxy.CaptureCheckpoint(app);
        Assert.False(failedWrite.WritesMatchRoot);
        Assert.True(failedWrite.Resynchronized);
        Assert.Equal(before.Root.Serialize(), failedWrite.Observed.Serialize());
        inner.FailPump = true; Assert.Throws<IOException>(() => proxy.PumpMessages());
        inner.FailWrite = false; inner.FailPump = false;
        app.ShutDown();
    }

    // The UI event loop must stay on its owning thread while a producer injects concurrently.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1031", Justification = "Synchronous UI thread affinity; producer is independently bounded.")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleWakeAndModalOpenCloseUseRealEventLoop(bool wrapped)
    {
        var inner = new WaitingDriver();
        WorkbenchProxyDriver? proxy = wrapped ? WorkbenchProxyDriver.Wrap(inner) : null;
        IDriver driver = proxy is null ? inner : proxy;
        driver.Initialize(); TDisplay.driver = driver;
        using var app = new ModalApp(proxy);
        var producer = Task.Run(() =>
        {
            if (!inner.Entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("UI never entered idle wait.");
            // Give an idle interval: an unbounded wait must stay asleep, with no timed polling.
            Thread.Sleep(150);
            Assert.Equal(1, Volatile.Read(ref inner.Waits));
            foreach (var ev in new[] { TestKeys.KeyDown(Keys.kbF3), TestKeys.KeyDown(Keys.kbEsc), TestKeys.KeyDown(Keys.kbF4) })
            {
                if (proxy is not null) proxy.Inject(ev); else TEventQueue.Enqueue(ev);
            }
        });
        // Independent bounded watchdog prevents a broken wake from hanging qualification.
        using var watchdog = new Timer(_ => TEventQueue.Enqueue(Quit()), null, 6000, Timeout.Infinite);
        var clock = Stopwatch.StartNew();
        Assert.Equal(0, AppLifecycleGuard.Run(app));
        Assert.True(producer.Wait(TimeSpan.FromSeconds(5)));
        if (producer.Exception is not null) throw producer.Exception;
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(app.Opened); Assert.True(app.Closed);
        Assert.Equal(1, inner.Waits);
        Assert.Equal(1, inner.Shutdowns);
        if (wrapped) { Assert.True(app.Checkpoint!.WritesMatchRoot); Assert.True(driver is IIdleWaitDriver); }
    }

    [Fact]
    public void CheckpointRefusesBackgroundThread()
    {
        var proxy = Install(new NullDriver(8, 4));
        using var app = new TApplication();
        Exception? failure = null;
        var worker = new Thread(() => { try { Assert.Throws<InvalidOperationException>(() => proxy.CaptureCheckpoint(app)); } catch (Exception ex) { failure = ex; } });
        worker.Start(); Assert.True(worker.Join(5000)); Assert.Null(failure);
        app.ShutDown();
    }

    // Explicitly executed separately; successful execution means actual Windows SDL resources, not native-pixel parity.
    [Fact]
    [Trait("Category", "SDL")]
    public void SdlRendererLimitedLifecycleAndLogicalCheckpoint() => SdlLifecycle("SDLDriver");

    [Fact]
    [Trait("Category", "SDL")]
    public void SdlGpuLifecycleAndLogicalCheckpoint() => SdlLifecycle("SDLGpuDriver");

    private void SdlLifecycle(string backend)
    {
        if (Environment.GetEnvironmentVariable("TSW_PROXY_SDL_TEST") != "1")
            throw new InvalidOperationException("Run this SDL qualification explicitly with TSW_PROXY_SDL_TEST=1, or exclude Category=SDL.");
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", backend);
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", Provider);
        var proxy = Assert.IsAssignableFrom<WorkbenchProxyDriver>(ScreenDriverFactory.CreateScreenDriver());
        Assert.EndsWith("." + backend, proxy.ActualBackend);
        Assert.IsAssignableFrom<IIdleWaitDriver>(proxy);
        proxy.Initialize(); TDisplay.driver = proxy;
        Assert.NotEqual((SDL3.SDL.InitFlags)0, SDL3.SDL.WasInit(SDL3.SDL.InitFlags.Video));
        using var app = new TApplication();
        app.DrawView(); proxy.CaptureCheckpoint(app); app.DrawView();
        Assert.True(proxy.CaptureCheckpoint(app).WritesMatchRoot);
        proxy.SetCursorType(0x2000); proxy.SetCaretPosition(4, 3);
        Assert.Equal((4, 3), proxy.Caret); Assert.Equal((ushort)0x2000, proxy.CursorType);
        proxy.PumpMessages(); proxy.Suspend(); proxy.Resume();
        proxy.Inject(TestKeys.KeyDown(Keys.kbF3));
        TEvent ev = default;
        for (int i = 0; i < 20; i++) { TScreen.GetEvent(ref ev); if (ev.What == Events.evKeyDown) break; }
        Assert.Equal(Keys.kbF3, ev.keyDown.keyCode);
        app.ShutDown(); proxy.Shutdown(); proxy.Dispose();
        Assert.Equal((SDL3.SDL.InitFlags)0, SDL3.SDL.WasInit(0));
        Assert.Equal(0, SDL3.TTF.WasInit());
    }

    private static WorkbenchProxyDriver Install(IDriver backend)
    {
        var proxy = WorkbenchProxyDriver.Wrap(backend);
        proxy.Initialize(); TDisplay.driver = proxy;
        return proxy;
    }
    private static void Drain() { TEventQueue.Resume(); TEvent ev = default; do { TEventQueue.GetNextEvent(ref ev); } while (ev.What != Events.evNothing); }
    private static TEvent Quit()
    {
        var ev = new TEvent { What = Events.evCommand }; ev.message.command = Views.cmQuit; return ev;
    }
    private sealed class ModalApp(WorkbenchProxyDriver? proxy) : TApplication
    {
        public bool Opened, Closed;
        public ProxyCheckpoint? Checkpoint, ModalCheckpoint;
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF3)
            {
                proxy?.CaptureCheckpoint(this);
                using var dialog = new CapturingDialog(this, proxy);
                Opened = true;
                DeskTop!.ExecView(dialog);
                Closed = true;
                Checkpoint = proxy?.CaptureCheckpoint(this);
                ClearEvent(ref ev); return;
            }
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF4)
            {
                EndModal(Views.cmQuit); ClearEvent(ref ev); return;
            }
            base.HandleEvent(ref ev);
        }
    }
    private sealed class CapturingDialog(ModalApp root, WorkbenchProxyDriver? proxy)
        : TDialog(new TRect(1, 1, 7, 4), "Modal")
    {
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEsc)
                root.ModalCheckpoint = proxy?.CaptureCheckpoint(root);
            base.HandleEvent(ref ev);
        }
    }
    private class RecordingDriver : IDriver
    {
        public List<string> Calls = [];
        public bool FailInit, FailShutdown, FailWrite, FailPump;
        public void Initialize() { Calls.Add("init"); if (FailInit) throw new IOException("init failed"); }
        public void Suspend() => Calls.Add("suspend");
        public void Resume() => Calls.Add("resume");
        public virtual void Shutdown() { Calls.Add("shutdown"); if (FailShutdown) throw new IOException("shutdown failed"); }
        public ushort GetCols() => 8;
        public ushort GetRows() => 4;
        public TDisplay.SM GetScreenMode() => TDisplay.SM.CO80;
        public void SetScreenMode(TDisplay.SM mode) => Calls.Add("mode");
        public ScreenBuffer AllocateScreenBuffer() { Calls.Add("allocate"); return new(8, 4); }
        public void ClearScreen(ushort cols, ushort rows) => Calls.Add("clear");
        private ushort cursor;
        public ushort GetCursorType() => cursor;
        public void SetCursorType(ushort value) { Calls.Add("cursor"); cursor = value; }
        public void SetCaretPosition(int x, int y) => Calls.Add("caret");
        public void WriteBuf(int x, int y, int w, int h, Span<TScreenChar> buf) { if (FailWrite) throw new IOException("write failed"); }
        public void MakeBeep() => Calls.Add("beep");
        public void PumpMessages() { Calls.Add("pump"); if (FailPump) throw new IOException("pump failed"); }
        public bool ReadKeyEvent(out TEvent ev) { Calls.Add("read"); ev = default; return false; }
        public bool SupportsMouse => true;
        public bool SupportsTrueColor => true;
        public bool SupportsGraphics => false;
        public KeyboardCapabilities KeyboardCapabilities => KeyboardCapabilities.KeyReleaseEvents;
    }
    private sealed class WaitingDriver : RecordingDriver, IIdleWaitDriver
    {
        public readonly ManualResetEventSlim Entered = new();
        public int Waits, Shutdowns;
        public void WaitForEvent(int timeoutMilliseconds, WaitHandle wakeSignal)
        {
            Assert.Equal(-1, timeoutMilliseconds);
            Interlocked.Increment(ref Waits); Entered.Set();
            Assert.True(wakeSignal.WaitOne(5000), "Injected input failed to wake UI.");
        }
        public override void Shutdown() { Shutdowns++; Entered.Dispose(); base.Shutdown(); }
    }
}

