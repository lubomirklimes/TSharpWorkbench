using TSharpVision;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

// In-process origin tests of the recording seam. A fake "native" backend appends events to the
// framework queue inside its own PumpMessages and returns keys from its own ReadKeyEvent, exactly where real drivers do;
// scripted input goes through WorkbenchProxyDriver.Inject. No process, window or pipe.
public sealed class RecorderProxyTests : IDisposable
{
    private readonly IDriver? oldDriver = TDisplay.driver;
    private readonly ScreenBuffer? oldBuffer = TScreen.ScreenBuffer;

    public RecorderProxyTests()
    {
        TDisplay.driver = null;
        TEventQueue.Resume(); Drain(); TEventQueue.ClearPosted(); TEventQueue.ClaimUiThread();
    }

    public void Dispose()
    {
        TDisplay.driver?.Shutdown();
        TDisplay.driver = oldDriver; TScreen.ScreenBuffer = oldBuffer;
        Drain(); TEventQueue.ClearPosted();
    }

    [Fact]
    public void BackendKeysAreRecordedAndScriptedInjectionIsRefusedWhileRecording()
    {
        var native = new NullDriver(40, 12);
        var proxy = Install(native);
        proxy.Recorder.Start(40, 12, null);
        native.EnqueueKey(TestKeys.KeyDown(Keys.kbF2));
        TEvent ev = default; TScreen.GetEvent(ref ev);
        Assert.Equal(Events.evKeyDown, ev.What); Assert.Equal(Keys.kbF2, ev.keyDown.keyCode);
        Assert.Throws<InvalidOperationException>(() => proxy.Inject(TestKeys.KeyDown(Keys.kbF3)));
        TScreen.GetEvent(ref ev);
        Assert.Equal(Events.evNothing, ev.What); // the refused injection never reached the queue
        var batch = proxy.Recorder.Poll(0);
        var recorded = Assert.Single(batch.Events);
        Assert.Equal(("key", Keys.kbF2, ProxyInputRecorder.BackendKey), (recorded.Kind, recorded.KeyCode, recorded.Origin));
        Assert.Equal(1, batch.RefusedInjections);
        proxy.Recorder.Stop();
        proxy.Inject(TestKeys.KeyDown(Keys.kbF3)); // after the recording, ordinary scripted input works again
        TScreen.GetEvent(ref ev);
        Assert.Equal(Keys.kbF3, ev.keyDown.keyCode);
        Assert.Single(proxy.Recorder.Poll(0).Events);
    }

    [Fact]
    public void QueueEventsFromTheBackendPumpAreTaggedDeliveredOnceAndKeepTheirOrder()
    {
        var native = new QueueBackend(new NullDriver(40, 12));
        var proxy = Install(native);
        proxy.Recorder.Start(40, 12, null);
        TEventQueue.Enqueue(Command(0x7a01));                    // queued by the application, before the backend runs
        native.Pending.AddRange([Mouse(Events.evMouseDown, 3, 2, 1), Mouse(Events.evMouseUp, 3, 2, 0), Resize()]);
        native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF3));    // a key the backend translated in the same pump
        var delivered = new List<TEvent>();
        for (TEvent ev = default; ; ) { TScreen.GetEvent(ref ev); if (ev.What == Events.evNothing) break; delivered.Add(ev); }
        // Same order TScreen.GetEvent gives without the proxy: queued events (application first, then backend), then keys.
        Assert.Equal([Events.evCommand, Events.evMouseDown, Events.evMouseUp, Events.evCommand, Events.evKeyDown], delivered.Select(e => e.What));
        Assert.Equal(0x7a01, delivered[0].message.command);
        var batch = proxy.Recorder.Poll(0);
        Assert.Equal(["mouse", "mouse", "resize", "key"], batch.Events.Select(e => e.Kind));
        Assert.Equal([ProxyInputRecorder.BackendQueue, ProxyInputRecorder.BackendQueue, ProxyInputRecorder.BackendQueue, ProxyInputRecorder.BackendKey], batch.Events.Select(e => e.Origin));
        Assert.Equal((3, 2), (batch.Events[0].X, batch.Events[0].Y));
        Assert.Equal(1, batch.OtherQueue);       // forwarded, never recorded
        Assert.Equal(0, batch.RefusedInjections);
    }

    [Fact]
    public void ClickClassificationIsAppliedExactlyOnceAsWithoutTheProxy()
    {
        // Control: the framework queue alone classifies two quick presses on one cell as a double click.
        TEventQueue.DoubleDelay = 500;
        var control = Classify(record: false);
        var recorded = Classify(record: true);
        Assert.Equal(control.Select(e => (e.What, e.mouse.eventFlags & Events.meDoubleClick)), recorded.Select(e => (e.What, e.mouse.eventFlags & Events.meDoubleClick)));
        Assert.Equal(Events.meDoubleClick, recorded[2].mouse.eventFlags & Events.meDoubleClick);
        Assert.Equal(4, recorded.Count); // no event lost or duplicated
    }

    [Fact]
    public void InactiveRecorderLeavesTheQueueRoutingUntouched()
    {
        var native = new QueueBackend(new NullDriver(40, 12));
        var proxy = Install(native);
        native.Pending.Add(Mouse(Events.evMouseDown, 1, 1, 1));
        proxy.PumpMessages();
        Assert.False(proxy.ReadKeyEvent(out _));                  // nothing is forwarded through the key path
        TEvent ev = default; TEventQueue.GetNextEvent(ref ev);     // the backend's event is still in the framework queue
        Assert.Equal(Events.evMouseDown, ev.What);
        Assert.Empty(proxy.Recorder.Poll(0).Events);
    }

    [Fact]
    public void PauseCountsButDoesNotRecordAndMarksTheGap()
    {
        var native = new NullDriver(40, 12);
        var proxy = Install(native);
        proxy.Recorder.Start(40, 12, null);
        TEvent ev = default;
        native.EnqueueKey(TestKeys.KeyDown(Keys.kbF2)); TScreen.GetEvent(ref ev);
        proxy.Recorder.SetPaused(true);
        native.EnqueueKey(TestKeys.KeyDown(Keys.kbF5)); TScreen.GetEvent(ref ev);
        Assert.Equal(Keys.kbF5, ev.keyDown.keyCode);               // still delivered to the application
        proxy.Recorder.SetPaused(false);
        native.EnqueueKey(TestKeys.KeyDown(Keys.kbF3)); TScreen.GetEvent(ref ev);
        var batch = proxy.Recorder.Poll(0);
        Assert.Equal([Keys.kbF2, Keys.kbF3], batch.Events.Select(e => e.KeyCode));
        Assert.True(batch.Events[1].AfterGap);
        Assert.Equal(1, batch.Unrecorded);
    }

    [Fact]
    public void JournalCopiesEveryRecordedEvent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "p3c-journal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var native = new NullDriver(40, 12);
            var proxy = Install(native);
            proxy.Recorder.Start(40, 12, folder);
            TEvent ev = default;
            foreach (var key in new[] { Keys.kbF2, Keys.kbF3, Keys.kbEnter }) { native.EnqueueKey(TestKeys.KeyDown(key)); TScreen.GetEvent(ref ev); }
            proxy.Recorder.Stop();
            var journal = RecordingJournal.Read(Path.Combine(folder, RecordingJournal.FileName));
            Assert.Equal(proxy.Recorder.Poll(0).Events, journal);
        }
        finally { Directory.Delete(folder, true); }
    }

    private List<TEvent> Classify(bool record)
    {
        Drain();
        var native = new QueueBackend(new NullDriver(40, 12));
        var proxy = Install(native);
        if (record) proxy.Recorder.Start(40, 12, null);
        native.Pending.AddRange([Mouse(Events.evMouseDown, 5, 5, 1), Mouse(Events.evMouseUp, 5, 5, 0), Mouse(Events.evMouseDown, 5, 5, 1), Mouse(Events.evMouseUp, 5, 5, 0)]);
        var result = new List<TEvent>();
        for (TEvent ev = default; ; ) { TScreen.GetEvent(ref ev); if (ev.What == Events.evNothing) break; result.Add(ev); }
        if (record) Assert.Equal(4, proxy.Recorder.Poll(0).Events.Length);
        proxy.Shutdown(); TDisplay.driver = null;
        return result;
    }

    private static WorkbenchProxyDriver Install(IDriver backend)
    {
        var proxy = WorkbenchProxyDriver.Wrap(backend);
        proxy.Initialize(); TDisplay.driver = proxy;
        TScreen.ScreenBuffer = proxy.AllocateScreenBuffer();
        return proxy;
    }
    private static void Drain() { TEventQueue.Resume(); TEvent ev = default; do { TEventQueue.GetNextEvent(ref ev); } while (ev.What != Events.evNothing); }
    private static TEvent Command(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; return ev; }
    private static TEvent Resize() => Command(Views.cmScreenResized);
    private static TEvent Mouse(ushort what, int x, int y, byte buttons)
    {
        var ev = new TEvent { What = what };
        ev.mouse.where = new TPoint(x, y); ev.mouse.buttons = buttons;
        return ev;
    }

    /// <summary>A stand-in native backend: PumpMessages appends its pending (already translated) events to the framework queue,
    /// as Win32ConsoleDriver/SDLDriver do for mouse and resize; keys come from the inner NullDriver's ReadKeyEvent.</summary>
    private sealed class QueueBackend(NullDriver inner) : IDriver
    {
        internal NullDriver Inner { get; } = inner;
        internal List<TEvent> Pending { get; } = [];
        public void PumpMessages() { Inner.PumpMessages(); foreach (var ev in Pending) TEventQueue.Enqueue(ev); Pending.Clear(); }
        public bool ReadKeyEvent(out TEvent ev) => Inner.ReadKeyEvent(out ev);
        public void Initialize() => Inner.Initialize();
        public void Suspend() => Inner.Suspend();
        public void Resume() => Inner.Resume();
        public void Shutdown() => Inner.Shutdown();
        public ushort GetCols() => Inner.GetCols();
        public ushort GetRows() => Inner.GetRows();
        public TDisplay.SM GetScreenMode() => Inner.GetScreenMode();
        public void SetScreenMode(TDisplay.SM mode) => Inner.SetScreenMode(mode);
        public ScreenBuffer AllocateScreenBuffer() => Inner.AllocateScreenBuffer();
        public void ClearScreen(ushort cols, ushort rows) => Inner.ClearScreen(cols, rows);
        public ushort GetCursorType() => Inner.GetCursorType();
        public void SetCursorType(ushort cursorType) => Inner.SetCursorType(cursorType);
        public void SetCaretPosition(int x, int y) => Inner.SetCaretPosition(x, y);
        public void WriteBuf(int x, int y, int w, int h, Span<TScreenChar> buf) => Inner.WriteBuf(x, y, w, h, buf);
        public void MakeBeep() => Inner.MakeBeep();
        public bool SupportsMouse => true;
        public bool SupportsTrueColor => Inner.SupportsTrueColor;
        public bool SupportsGraphics => false;
        public KeyboardCapabilities KeyboardCapabilities => ((IDriver)Inner).KeyboardCapabilities;
    }
}
