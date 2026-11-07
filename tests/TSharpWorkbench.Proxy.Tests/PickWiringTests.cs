using TSharpVision;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

// Integration (coordinator): the gesture engine wired into WorkbenchProxyDriver.ReadKeyEvent and the boundary
// tracker, in the real TSharpVision event loop (TApplication.Run) on a NullDriver behind the proxy. Mouse events arrive in
// the framework queue during the backend pump, as the Console and SDL drivers deliver them. Logical evidence only.
public sealed class PickWiringTests : IDisposable
{
    private const uint CtrlShift = Keys.kbCtrlShift | Keys.kbLeftShift;
    private readonly IDriver? oldDriver = TDisplay.driver;
    private readonly ScreenBuffer? oldBuffer = TScreen.ScreenBuffer;
    private readonly bool init = Init();

    private static bool Init()
    {
        TDisplay.driver = null;
        Drain(); TEventQueue.ClearPosted(); TEventQueue.ClaimUiThread();
        return true;
    }

    public void Dispose()
    {
        _ = init;
        TDisplay.driver?.Shutdown();
        TDisplay.driver = oldDriver; TScreen.ScreenBuffer = oldBuffer;
        Drain(); TEventQueue.ClearPosted();
    }

    [Fact]
    public void CtrlShiftClickAndDragAreSwallowedAndEmitPickBoundariesWithFrozenSnapshots()
    {
        var run = Run(modifierGestures: true, script:
        [
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 12, 6, 1, CtrlShift), Mouse(Events.evMouseUp, 12, 6, 0, CtrlShift)]),
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 2, 3, 1, CtrlShift), Mouse(Events.evMouseMove, 5, 4, 1, CtrlShift),
                Mouse(Events.evMouseMove, 9, 8, 1, 0), Mouse(Events.evMouseUp, 9, 8, 0, 0)]), // modifiers released mid-drag
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 30, 10, 1), Mouse(Events.evMouseUp, 30, 10, 0)]), // ordinary click
        ]);
        // The application saw only the ordinary click; the recorder recorded only it.
        Assert.Equal([(30, 10, false)], run.App.Presses);
        var recorded = run.Proxy.Recorder.Poll(0).Events.Where(e => e.Kind == "mouse").ToArray(); // F4 (quit) is the only key
        Assert.True(recorded.Length == 2, string.Join("; ", recorded.Select(e => $"{e.What:x}@{e.X},{e.Y} b{e.Buttons} {e.Origin}")));
        Assert.All(recorded, e => Assert.Equal((30, 10), (e.X, e.Y)));
        var picks = run.Marks.Where(b => b.Kind == Protocol.BoundaryKinds.Pick).ToArray();
        Assert.Equal(2, picks.Length);
        var click = picks[0].Pick!; var drag = picks[1].Pick!;
        Assert.Equal((Protocol.PickKinds.Control, Protocol.PickTriggers.CtrlShift, new CellRect(12, 6, 1, 1)), (click.Kind, click.Trigger, click.Region));
        Assert.Equal((Protocol.PickKinds.Region, new CellRect(2, 3, 8, 6)), (drag.Kind, drag.Region));
        Assert.Equal(2, click.Swallowed); Assert.Equal(4, drag.Swallowed);
        Assert.All(picks, b => Assert.Equal(Protocol.BoundaryCauses.Pick, b.Cause));
        Assert.All(picks, b => Assert.Equal(0, b.AfterSequence));                    // nothing recorded before either pick
        // Anchors: the last state boundary before the press; pick boundaries never move the recorded Delivered count.
        Assert.All(picks, b => Assert.True(b.Pick!.AnchorBoundaryId > 0 && run.Marks.Single(m => m.Id == b.Pick.AnchorBoundaryId).Kind != Protocol.BoundaryKinds.Pick));
        Assert.Equal(0, picks[^1].Delivered);
        // Frozen snapshots of the press state, reason pick, same hash as the boundary; the hit path names the probe view.
        foreach (var b in picks)
        {
            var snapshot = Assert.Single(run.Snapshots, s => s.Id == b.SnapshotId);
            Assert.Equal((Protocol.SnapshotReasons.Pick, b.Id, b.Hash), (snapshot.Reason, snapshot.BoundaryId, snapshot.Hash));
            Assert.Equal(b.Hash, ProxyScreenHash.Compute(ScreenSnapshot.Deserialize(Convert.FromBase64String(snapshot.Cells))));
        }
        var tree = run.Snapshots.Single(s => s.Id == picks[0].SnapshotId).Inspection!.ViewTree!;
        Assert.NotNull(click.HitPath);
        Assert.Equal("probe", Find(tree.Root, click.HitPath!)!.Id);
        var coverage = run.Proxy.Recorder.Coverage(RecordingTransport.Sources(run.Proxy));
        Assert.Equal((2L, 0L, 6L), (coverage.Picks, coverage.PicksAborted, coverage.GestureEventsSwallowed));
    }

    [Fact]
    public void PickModeIsOneShotAndTheNextOrdinaryClickOnTheSameCellIsNotAFalseDoubleClick()
    {
        var run = Run(modifierGestures: false, script:
        [
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 12, 6, 1, CtrlShift), Mouse(Events.evMouseUp, 12, 6, 0, CtrlShift)]), // not opted in: delivered
            p => Assert.True(p.Proxy.Gestures.Arm()),
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 20, 6, 1), Mouse(Events.evMouseUp, 20, 6, 0)]),   // the pick
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 20, 6, 1), Mouse(Events.evMouseUp, 20, 6, 0)]),   // ordinary, < DoubleDelay
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 20, 6, 1), Mouse(Events.evMouseUp, 20, 6, 0)]),   // a real double click
        ]);
        // Known limitation (documented): the framework classified the cleared press as the first half of a double click, so
        // a real double click that STARTS with the first ordinary press after a pick reads as single (never a false double).
        Assert.Equal([(12, 6, false), (20, 6, false), (20, 6, false)], run.App.Presses);
        var pick = Assert.Single(run.Marks, b => b.Kind == Protocol.BoundaryKinds.Pick);
        Assert.Equal(Protocol.PickTriggers.PickMode, pick.Pick!.Trigger);
        Assert.Equal(2, pick.AfterSequence);                                         // after the delivered Ctrl+Shift click
        Assert.Equal(Protocol.PickStates.Off, run.Proxy.Gestures.State);
        Assert.Equal(1, run.Proxy.Recorder.Coverage(RecordingTransport.Sources(run.Proxy)).DoubleClicksCleared);
        Assert.Equal(6, run.Proxy.Recorder.Poll(0).Events.Count(e => e.Kind == "mouse"));
    }

    [Fact]
    public void PicksInsideAModalDialogAndAnOpenMenuCarryTheirTransientSignature()
    {
        var run = Run(modifierGestures: true, script:
        [
            p => p.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF3)),                                                 // modal dialog
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 14, 7, 1, CtrlShift), Mouse(Events.evMouseUp, 14, 7, 0, CtrlShift)]),
            p => p.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbEsc)),
            p => { p.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF10)); p.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbEnter)); }, // File box
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 3, 2, 1, CtrlShift), Mouse(Events.evMouseUp, 3, 2, 0, CtrlShift)]),
            p => p.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbEsc)),
            p => p.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbEsc)),
        ]);
        Assert.Empty(run.App.Presses);
        var picks = run.Marks.Where(b => b.Kind == Protocol.BoundaryKinds.Pick).ToArray();
        Assert.Equal(2, picks.Length);
        Assert.Contains("TDialog", picks[0].ModalTop);
        Assert.Contains("TMenuBox", picks[1].ModalTop);
        Assert.Equal(2, picks[1].MenuDepth);
        var menu = run.Snapshots.Single(s => s.Id == picks[1].SnapshotId).Inspection!.Menu!;
        Assert.True(menu.Active);
        // The dialog and the menu were not changed by the picks: same keys closed them, nothing else was recorded.
        Assert.All(run.Proxy.Recorder.Poll(0).Events, e => Assert.Equal("key", e.Kind));
    }

    [Fact]
    public void AReleaseAfterStopIsStillSwallowedAndPauseCancelsPickMode()
    {
        var run = Run(modifierGestures: true, script:
        [
            p => p.Pending.Add(Mouse(Events.evMouseDown, 12, 6, 1, CtrlShift)),
            p => { p.Proxy.Recorder.Stop(); p.Proxy.Gestures.Reset(); },
            p => p.Pending.AddRange([Mouse(Events.evMouseMove, 13, 6, 1), Mouse(Events.evMouseUp, 13, 6, 0)]),
            p => p.Pending.AddRange([Mouse(Events.evMouseDown, 40, 2, 1), Mouse(Events.evMouseUp, 40, 2, 0)]),
        ], expectStopped: true);
        Assert.Equal([(40, 2, false)], run.App.Presses);                              // no orphan release, later click intact
        Assert.Equal(0, run.App.Releases.Count(r => r == (13, 6)));
        Assert.DoesNotContain(run.Marks, b => b.Kind == Protocol.BoundaryKinds.Pick);
        Assert.False(run.Proxy.Gestures.Engaged);

        Drain();
        var native = new QueueBackend(new NullDriver(60, 20));
        var proxy = Install(native);
        proxy.Recorder.Start(60, 20, null);
        Assert.True(proxy.Gestures.Arm());
        proxy.Recorder.SetPaused(true); proxy.Gestures.Cancel();                     // what RecordPause does
        Assert.Equal(Protocol.PickStates.Off, proxy.Gestures.State);
        proxy.Shutdown(); TDisplay.driver = null;
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record RunResult(WorkbenchProxyDriver Proxy, ProbeApp App, List<RecordedBoundary> Marks, RetainedSnapshot[] Snapshots);

    private sealed class Pass(WorkbenchProxyDriver proxy, QueueBackend native)
    {
        internal WorkbenchProxyDriver Proxy { get; } = proxy;
        internal NullDriver Inner => native.Inner;
        internal List<TEvent> Pending => native.Pending;
    }

    /// <summary>Real event loop. Pass 1 starts the recording (with or without modifier gestures, as RecordStart does); each
    /// later idle pass runs one script step; then F4 quits (snapshots are read before shutdown clears them).</summary>
    private static RunResult Run(bool modifierGestures, Action<Pass>[] script, bool expectStopped = false)
    {
        var native = new QueueBackend(new NullDriver(60, 20));
        var proxy = Install(native);
        var app = new ProbeApp();
        var marks = new List<RecordedBoundary>();
        proxy.Boundaries.Emitted = marks.Add;
        var pass = new Pass(proxy, native);
        RetainedSnapshot[] retained = [];
        app.Quitting = () => retained = proxy.Snapshots.Read(0, 32, int.MaxValue);
        int step = 0;
        app.IdlePass = n =>
        {
            if (n == 1)
            {
                proxy.Gestures.Reset(); proxy.Gestures.ModifierGestures = modifierGestures;
                proxy.Recorder.Start(60, 20, null);
                return;
            }
            if (n > 80) { app.TimedOut = true; app.EndModal(Views.cmQuit); return; }
            if (step < script.Length) script[step++](pass);
            else if (step++ == script.Length) native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF4));
        };
        Assert.Equal(0, AppLifecycleGuard.Run(app));
        Assert.False(app.TimedOut);
        Assert.Equal(expectStopped, !proxy.Recorder.Poll(0).Active && expectStopped);
        return new(proxy, app, marks, retained);
    }

    private static ViewNode? Find(ViewNode node, string path) =>
        node.Path == path ? node : node.Children.Select(c => Find(c, path)).FirstOrDefault(n => n is not null);

    private static WorkbenchProxyDriver Install(IDriver backend)
    {
        var proxy = WorkbenchProxyDriver.Wrap(backend);
        proxy.Initialize(); TDisplay.driver = proxy;
        TScreen.ScreenBuffer = proxy.AllocateScreenBuffer();
        return proxy;
    }
    private static void Drain() { TEventQueue.Resume(); TEvent ev = default; do { TEventQueue.GetNextEvent(ref ev); } while (ev.What != Events.evNothing); }
    private static TEvent Mouse(ushort what, int x, int y, byte buttons, uint keys = 0)
    {
        var ev = new TEvent { What = what };
        ev.mouse.where = new TPoint(x, y); ev.mouse.buttons = buttons; ev.mouse.controlKeyState = keys;
        return ev;
    }

    /// <summary>Records every mouse press/release that reached the desktop (a full-desktop probe view, id "probe"), F3 =
    /// modal dialog, F4 = quit, File menu with two items.</summary>
    private sealed class ProbeApp : TApplication
    {
        public Action<int>? IdlePass;
        public Action? Quitting;
        public bool TimedOut;
        public readonly List<(int X, int Y, bool Double)> Presses = [];
        public readonly List<(int X, int Y)> Releases = [];
        private int passes;

        public ProbeApp()
        {
            var probe = new Probe(DeskTop!.GetExtent(), this);
            ViewTreeInspector.Identify(probe, "probe");
            DeskTop.Insert(probe);
        }

        public override void Idle() { base.Idle(); IdlePass?.Invoke(++passes); }

        public override TMenuBar? InitMenuBar(TRect r)
        {
            r.b.y = r.a.y + 1;
            return new TMenuBar(r, new TSubMenu("~F~ile", Keys.kbAltF) + new TMenuItem("~N~ew probe", 0x7a10, Keys.kbNoKey)
                + new TMenuItem("~O~pen probe", 0x7a11, Keys.kbNoKey));
        }

        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF3)
            {
                ClearEvent(ref ev);
                using var dialog = new TDialog(new TRect(10, 4, 40, 12), "Probe dialog");
                DeskTop!.ExecView(dialog);
                return;
            }
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF4) { Quitting?.Invoke(); EndModal(Views.cmQuit); ClearEvent(ref ev); return; }
            base.HandleEvent(ref ev);
        }

        private sealed class Probe(TRect bounds, ProbeApp app) : TView(bounds)
        {
            public override void HandleEvent(ref TEvent ev)
            {
                if (ev.What == Events.evMouseDown) { app.Presses.Add((ev.mouse.where.x, ev.mouse.where.y, (ev.mouse.eventFlags & Events.meDoubleClick) != 0)); ClearEvent(ref ev); return; }
                if (ev.What == Events.evMouseUp) { app.Releases.Add((ev.mouse.where.x, ev.mouse.where.y)); ClearEvent(ref ev); return; }
                base.HandleEvent(ref ev);
            }
        }
    }

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
