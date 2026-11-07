using System.Diagnostics;
using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TSharpWorkbench.Proxy.Tests;

// Workstream B: in-process boundary/transient-capture tests. Real TSharpVision menus and dialogs run in the real event
// loop (TApplication.Run) on a NullDriver behind the proxy; keys arrive through the backend's ReadKeyEvent and mouse/hover
// through the framework queue during the backend pump, exactly where real drivers deliver them. No process or pipe.
public sealed class BoundaryCaptureTests(ITestOutputHelper output) : IDisposable
{
    private const ushort cmProbeNew = 0x7a10, cmProbeOpen = 0x7a11;
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

    // ── (a) CRITICAL: transient states that open and close before anything polls ─────────────────────────────────────

    [Theory]
    [InlineData(false)] // every key queued at once: the menu opens and closes with no idle boundary in between
    [InlineData(true)]  // one key per idle pass: idle boundaries inside the menu loops
    public void MenuOpenedAndClosedBeforeAnyPollIsRetainedWithMatchingInspection(bool trickle)
    {
        var run = Run([Keys.kbF10, Keys.kbEnter, Keys.kbEsc, Keys.kbF4], trickle);
        Assert.DoesNotContain("New probe", Text(run.Final));                 // closed again before anything read the store
        var snapshots = run.Snapshots;
        var open = Assert.Single(snapshots, s => s.Reason == Protocol.SnapshotReasons.MenuOpen);
        var change = Assert.Single(snapshots, s => s.Reason == Protocol.SnapshotReasons.MenuChange);
        Assert.True(open.Id < change.Id);
        var bar = Matching(run, open, 1);
        Assert.Contains("TMenuBar", bar.ModalTop);
        Assert.True(open.Inspection!.Menu!.Active); Assert.Equal(0, open.Inspection.Menu.Depth);
        var box = Matching(run, change, 2);
        Assert.Contains("TMenuBox", box.ModalTop);
        var cells = ScreenSnapshot.Deserialize(Convert.FromBase64String(change.Cells));
        Assert.Contains("New probe", Text(cells));
        Assert.Contains("Open probe", Text(cells));
        var menu = change.Inspection!.Menu!;
        Assert.True(menu.Active); Assert.Equal(1, menu.Depth);
        Assert.Equal(["New probe", "Open probe"], menu.Open[0].Items.Select(i => i.Text));
        Assert.All(menu.Open[0].Items, i => Assert.Equal("match", i.Observed)); // inspection agrees with the retained cells
        Assert.NotNull(change.Inspection.ViewTree!.ModalPath);
        if (!trickle) Assert.All([bar, box], b => Assert.Equal(Protocol.BoundaryKinds.PreInput, b.Kind));
        Assert.Equal(0, run.Proxy.Boundaries.Counters(run.Proxy.Recorder.RecordingId).CaptureFailures);
        // The byte charge is an upper bound of the serialized snapshot.
        Assert.True(ProxyRetainedSnapshots.Estimate(cells, change.Inspection) >= JsonSerializer.SerializeToUtf8Bytes(change).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModalDialogOpenedAndClosedBeforeAnyPollIsRetainedWithMatchingInspection(bool trickle)
    {
        var run = Run([Keys.kbF3, Keys.kbEsc, Keys.kbF4], trickle);
        Assert.DoesNotContain("Probe dialog", Text(run.Final));
        var snapshot = Assert.Single(run.Snapshots);
        Assert.Equal(Protocol.SnapshotReasons.ModalOpen, snapshot.Reason);
        var boundary = Matching(run, snapshot, 0);
        Assert.Contains("TDialog", boundary.ModalTop);
        Assert.Equal(1, boundary.AfterSequence);                               // F3 delivered, Esc not yet
        Assert.Contains("Probe dialog", Text(ScreenSnapshot.Deserialize(Convert.FromBase64String(snapshot.Cells))));
        var tree = snapshot.Inspection!.ViewTree!;
        Assert.NotNull(tree.ModalPath);
        Assert.Contains("TDialog", Find(tree.Root, tree.ModalPath!)!.Type);
        Assert.Equal("Probe dialog", Find(tree.Root, tree.ModalPath!)!.Text);
        Assert.False(snapshot.Inspection.Menu!.Active);
    }

    [Fact]
    public void ScreenChangesInsideAnOpenMenuAreKeptAsTransientLast()
    {
        // Down moves the selection inside the open box (same signature): the last state is committed when the menu closes.
        var run = Run([Keys.kbF10, Keys.kbEnter, Keys.kbDown, Keys.kbEsc, Keys.kbF4], trickle: false);
        var snapshots = run.Snapshots;
        Assert.Equal([Protocol.SnapshotReasons.MenuOpen, Protocol.SnapshotReasons.MenuChange, Protocol.SnapshotReasons.TransientLast],
            snapshots.Select(s => s.Reason));
        var last = snapshots[2];
        Matching(run, last, 2);
        Assert.Equal("Open probe", last.Inspection!.Menu!.Open[0].SelectedText);
        Assert.Equal("New probe", snapshots[1].Inspection!.Menu!.Open[0].SelectedText);
    }

    [Fact]
    public void LimitationModalClosedByAPendingEventWithoutInputOrIdleIsNotObserved()
    {
        // F7 opens a dialog whose loop reads a Pending cmCancel first: no ReadKeyEvent and no idle pass happen while it is
        // open, so no boundary can observe it. Documented limitation; the marks stay truthful (no modal state invented).
        var run = Run([Keys.kbF7, Keys.kbF4], trickle: false);
        Assert.True(run.App.LimitationDialogRan);
        Assert.Empty(run.Snapshots);
        Assert.All(run.Marks, b => Assert.Null(b.ModalTop));
    }

    // ── (b) causal metadata ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AfterSequenceCauseKindAndPauseFollowDeliveredInput()
    {
        var native = new QueueBackend(new NullDriver(40, 12));
        var proxy = Install(native);
        using var app = new TApplication();
        var marks = Collect(proxy);
        proxy.Recorder.Start(40, 12, null);
        proxy.OnIdleBoundary(app);
        Assert.Equal((1, Protocol.BoundaryCauses.Start, 0L, Protocol.BoundaryKinds.Idle), Shape(marks[^1]));
        TEvent ev = default;
        native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF2)); TScreen.GetEvent(ref ev);
        Paint(proxy, 'a'); proxy.OnIdleBoundary(app);
        Assert.Equal((2, Protocol.BoundaryCauses.Input, 1L, Protocol.BoundaryKinds.Idle), Shape(marks[^1]));
        native.Pending.AddRange([Mouse(Events.evMouseDown, 3, 2, 1), Mouse(Events.evMouseUp, 3, 2, 0)]);
        TScreen.GetEvent(ref ev); TScreen.GetEvent(ref ev);
        Paint(proxy, 'b'); proxy.OnIdleBoundary(app);
        Assert.Equal((3, Protocol.BoundaryCauses.Input, 3L, Protocol.BoundaryKinds.Idle), Shape(marks[^1]));
        native.Pending.Add(Mouse(Events.evMouseMove, 5, 5, 0));                // hover: delivered, not recorded
        TScreen.GetEvent(ref ev);
        Paint(proxy, 'c'); proxy.OnIdleBoundary(app);
        Assert.Equal((4, Protocol.BoundaryCauses.Input, 3L, Protocol.BoundaryKinds.Idle), Shape(marks[^1]));
        Assert.Equal(4, marks[^1].Delivered);
        Paint(proxy, 'd'); proxy.OnIdleBoundary(app);                          // application redraw, no input
        Assert.Equal((5, Protocol.BoundaryCauses.Application, 3L, Protocol.BoundaryKinds.Idle), Shape(marks[^1]));
        native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF5)); native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF6));
        TScreen.GetEvent(ref ev); Assert.Equal(5, marks.Count);               // nothing new since the idle boundary
        Paint(proxy, 'e');
        TScreen.GetEvent(ref ev);                                             // F5 handled and drawn, F6 not yet handed out
        Assert.Equal((6, Protocol.BoundaryCauses.Input, 4L, Protocol.BoundaryKinds.PreInput), Shape(marks[^1]));
        Assert.Equal(Keys.kbF6, ev.keyDown.keyCode);
        Paint(proxy, 'f'); proxy.OnIdleBoundary(app);
        Assert.Equal((7, Protocol.BoundaryCauses.Input, 5L, Protocol.BoundaryKinds.Idle), Shape(marks[^1]));
        Assert.All(marks, b => Assert.False(b.AfterGap));
        proxy.Recorder.SetPaused(true);
        Paint(proxy, 'g'); proxy.OnIdleBoundary(app);
        native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF8)); TScreen.GetEvent(ref ev);
        Paint(proxy, 'h'); proxy.OnIdleBoundary(app);
        Assert.Equal(7, marks.Count);                                         // no marks while paused
        proxy.Recorder.SetPaused(false);
        Paint(proxy, 'i'); proxy.OnIdleBoundary(app);
        Assert.Equal((8, Protocol.BoundaryCauses.Input, 5L, Protocol.BoundaryKinds.Idle), Shape(marks[^1]));
        Assert.True(marks[^1].AfterGap);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), marks.Select(b => b.Id));
        Assert.Equal(proxy.Recorder.Poll(0).Events.Length, (int)marks[^1].AfterSequence);
        foreach (var b in marks) Assert.True(b.Hash != 0 && b.Width == 40 && b.Height == 12 && b.MenuDepth == 0 && b.ModalTop is null);
        app.ShutDown();
    }

    // ── (c) dedupe ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnchangedScreensProduceNoMarksAndRevertedScreensAreSuppressed()
    {
        var proxy = Install(new NullDriver(40, 12));
        using var app = new TApplication();
        var marks = Collect(proxy);
        proxy.Recorder.Start(40, 12, null);
        proxy.OnIdleBoundary(app);
        Paint(proxy, 'a'); proxy.OnIdleBoundary(app);
        long generation = proxy.ScreenGeneration;
        for (int i = 0; i < 5; i++) proxy.OnIdleBoundary(app);
        Paint(proxy, 'a');                                                    // identical write: no logical change
        proxy.OnIdleBoundary(app);
        Assert.Equal(generation, proxy.ScreenGeneration);
        Assert.Equal(2, marks.Count);
        var counters = proxy.Boundaries.Counters(proxy.Recorder.RecordingId);
        Assert.Equal((2L, 0L), (counters.Emitted, counters.Suppressed));
        Paint(proxy, 'z'); Paint(proxy, 'a');                                 // changed and changed back between boundaries
        proxy.OnIdleBoundary(app);
        Assert.Equal(generation + 2, proxy.ScreenGeneration);
        Assert.Equal(2, marks.Count);
        Assert.Equal(1, proxy.Boundaries.Counters(proxy.Recorder.RecordingId).Suppressed);
        app.ShutDown();
    }

    // ── (d) bounded store ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StoreRefusesNewSnapshotsWhenFullByCountOrBytesAndReleasesOnRead()
    {
        var store = new ProxyRetainedSnapshots();
        var small = Cells(40, 12);
        Assert.Null(store.TryAdd(1, "menu-open", 0, 0, 0, 0, 0, small, null));   // inactive before Reset
        store.Reset();
        for (int i = 1; i <= ProxyRetainedSnapshots.MaximumCount; i++) Assert.Equal(i, store.TryAdd(i, "menu-open", i, i, i, (ulong)i, i, small, null));
        Assert.Null(store.TryAdd(99, "menu-open", 0, 0, 0, 0, 0, small, null));
        Assert.Equal((32L, 1L, 0L, 32), Head(store.Counters()));
        var first = store.Read(0, 2, int.MaxValue);
        Assert.Equal([1L, 2L], first.Select(s => s.Id));
        var one = store.Read(2, 8, 1);                                        // byte cap below one snapshot: still exactly one
        Assert.Equal(3, Assert.Single(one).Id);
        Assert.Equal(2, store.Counters().Released);
        Assert.Equal(small.Serialize(), Convert.FromBase64String(one[0].Cells));
        long size = JsonSerializer.SerializeToUtf8Bytes(one[0]).Length;
        Assert.Equal(3, store.Read(3, 8, (int)(size * 3 + size / 2)).Length);  // 4,5,6 fit; the 4th would exceed the cap
        Assert.Equal(33, store.TryAdd(33, "menu-open", 0, 0, 0, 0, 0, small, null)); // room again after release
        Assert.Equal(33, store.Latest);
        store.Freeze();
        Assert.Null(store.TryAdd(34, "menu-open", 0, 0, 0, 0, 0, small, null));
        Assert.Equal(8, store.Read(3, 8, int.MaxValue).Length);                // frozen: still readable
        store.Clear();
        Assert.Empty(store.Read(0, 8, int.MaxValue));
        Assert.Equal((0, 0L), (store.Counters().Retained, store.Counters().Bytes));
        Assert.Equal(0, store.Latest);
        Assert.Null(store.TryAdd(35, "menu-open", 0, 0, 0, 0, 0, small, null));

        store.Reset();
        var large = Cells(200, 100);
        long cost = ProxyRetainedSnapshots.Estimate(large, null);
        int fit = (int)(ProxyRetainedSnapshots.MaximumBytes / cost);
        Assert.True(fit < ProxyRetainedSnapshots.MaximumCount);
        for (int i = 0; i < fit; i++) Assert.NotNull(store.TryAdd(i + 1, "modal-open", 0, 0, 0, 0, 0, large, null));
        Assert.Null(store.TryAdd(fit + 1, "modal-open", 0, 0, 0, 0, 0, large, null));
        var counters = store.Counters();
        Assert.Equal((fit, 1L), (counters.Retained, counters.Dropped));
        Assert.True(counters.Bytes <= ProxyRetainedSnapshots.MaximumBytes);
        Assert.True(store.Read(0, 8, 1 << 20).Sum(s => (long)JsonSerializer.SerializeToUtf8Bytes(s).Length) <= 1 << 20);
    }

    [Fact]
    public void BoundarySaysSnapshotDroppedWhenTheStoreIsFull()
    {
        var run = Run([Keys.kbF3, Keys.kbEsc, Keys.kbF4], trickle: false, beforeKeys: proxy =>
        {
            for (int i = 0; i < ProxyRetainedSnapshots.MaximumCount; i++)
                Assert.NotNull(proxy.Snapshots.TryAdd(1000 + i, "menu-open", 0, 0, 0, 0, 0, Cells(4, 2), null));
        });
        var modal = Assert.Single(run.Marks, b => b.ModalTop is not null);
        Assert.True(modal.SnapshotDropped); Assert.Null(modal.SnapshotId);
        Assert.Equal((32L, 1L), (run.Proxy.Snapshots.Counters().Added, run.Proxy.Snapshots.Counters().Dropped));
    }

    // ── (e) normal input non-regression; (f) no tracker work when not recording ──────────────────────────────────────

    [Fact]
    public void TrackingLeavesDeliveredEventsAndClassificationIdentical()
    {
        TEventQueue.DoubleDelay = 500;
        var control = Deliver(record: false, out _);
        var tracked = Deliver(record: true, out int marks);
        Assert.True(marks >= 3);
        Assert.Equal(control.Count, tracked.Count);
        Assert.Equal(control.Select(Describe), tracked.Select(Describe));
        Assert.Equal(Events.meDoubleClick, tracked.Single(e => e.What == Events.evMouseDown && (e.mouse.eventFlags & Events.meDoubleClick) != 0).mouse.eventFlags & Events.meDoubleClick);
    }

    [Fact]
    public void NoTrackerWorkWithoutARecording()
    {
        var native = new QueueBackend(new NullDriver(40, 12));
        var proxy = Install(native);
        using var app = new TApplication();
        TEvent ev = default;
        native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF2)); native.Pending.Add(Mouse(Events.evMouseDown, 1, 1, 1));
        for (int i = 0; i < 3; i++) { TScreen.GetEvent(ref ev); Paint(proxy, (char)('a' + i)); proxy.OnIdleBoundary(app); }
        // The tracker exists (created eagerly) but never observed anything: no counters, no marks, no snapshots.
        Assert.Equal(default, proxy.Boundaries.Counters(proxy.Recorder.RecordingId));
        Assert.Empty(proxy.Recorder.Poll(0, null, 0).Boundaries);
        Assert.Equal(0, proxy.Snapshots.Latest);
        app.ShutDown();
    }

    // ── (7) rough costs (Stopwatch; reported in the test output, not asserted) ───────────────────────────────────────

    [Theory]
    [InlineData(80, 25)]
    [InlineData(200, 100)]
    public void MeasureCosts(int w, int h)
    {
        var proxy = Install(new NullDriver((ushort)w, (ushort)h));
        using var app = new MenuApp();
        app.DrawView();
        proxy.Recorder.Start(w, h, null);
        var row = Enumerable.Repeat(new TScreenChar('x', 7), w * h).ToArray();
        proxy.WriteBuf(0, 0, w, h, row);
        const int n = 200;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) proxy.WriteBuf(0, 0, w, h, row);        // equal content: full compare, no change
        double write = clock.Elapsed.TotalMicroseconds / n;
        var root = ScreenSnapshot.CopyRoot();
        clock.Restart();
        for (int i = 0; i < n; i++) ProxyScreenHash.Compute(ScreenSnapshot.CopyRoot());
        double hash = clock.Elapsed.TotalMicroseconds / n;
        clock.Restart();
        for (int i = 0; i < n; i++) Inspectors.Capture(app, ScreenSnapshot.CopyRoot(), Protocol.EvidenceKinds.All);
        double capture = clock.Elapsed.TotalMicroseconds / n;
        var store = new ProxyRetainedSnapshots(); store.Reset();
        store.TryAdd(1, "menu-open", 0, 0, 0, 0, 0, root, Inspectors.Capture(app, root, Protocol.EvidenceKinds.All));
        clock.Restart();
        var read = store.Read(0, 8, 1 << 20);
        double serialize = clock.Elapsed.TotalMicroseconds;
        output.WriteLine($"{w}x{h}: WriteBuf full-screen compare+copy {write:F1} us; CopyRoot+hash {hash:F1} us; " +
            $"CopyRoot+inspection(all) {capture:F1} us; first Read serialize (transport thread) {serialize:F0} us, {JsonSerializer.SerializeToUtf8Bytes(read[0]).Length} bytes");
        app.ShutDown();
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record RunResult(WorkbenchProxyDriver Proxy, MenuApp App, List<RecordedBoundary> Marks, ScreenSnapshot Final,
        RetainedSnapshot[] Snapshots);

    /// <summary>Runs the real event loop. First idle pass: recording starts (as RecordStart does, from a UI pass) and the keys are
    /// handed to the backend - all at once, or one per later idle pass with <paramref name="trickle"/>.</summary>
    private RunResult Run(ushort[] keys, bool trickle, Action<WorkbenchProxyDriver>? beforeKeys = null)
    {
        var native = new NullDriver(60, 20);
        var proxy = Install(native);
        var app = new MenuApp();
        var marks = Collect(proxy);
        int next = 0;
        RetainedSnapshot[] retained = [];
        // Read on quit (F4): after every transient closed, before AppLifecycleGuard shuts the proxy down (which clears the store).
        app.Quitting = () => retained = proxy.Snapshots.Read(0, 32, int.MaxValue);
        app.IdlePass = pass =>
        {
            if (pass == 1) { proxy.Recorder.Start(60, 20, null); return; }
            if (pass == 2) beforeKeys?.Invoke(proxy);
            if (pass > 60) { app.TimedOut = true; app.EndModal(Views.cmQuit); return; }
            if (trickle) { if (next < keys.Length) native.EnqueueKey(TestKeys.KeyDown(keys[next++])); }
            else while (next < keys.Length) native.EnqueueKey(TestKeys.KeyDown(keys[next++]));
        };
        Assert.Equal(0, AppLifecycleGuard.Run(app));
        Assert.False(app.TimedOut);
        Assert.Equal(keys.Length, next);
        Assert.Equal(keys.Length, proxy.Recorder.Poll(0).Events.Length);
        Assert.Equal(Protocol.BoundaryCauses.Start, marks[0].Cause);
        Assert.Equal((0, 0L), (proxy.Snapshots.Counters().Retained, proxy.Snapshots.Counters().Bytes)); // shutdown cleared payloads
        Assert.Empty(proxy.Snapshots.Read(0, 32, int.MaxValue));
        return new(proxy, app, marks, app.Final!, retained);
    }

    private static RecordedBoundary Matching(RunResult run, RetainedSnapshot snapshot, int menuDepth)
    {
        var boundary = Assert.Single(run.Marks, b => b.Id == snapshot.BoundaryId);
        if (snapshot.Reason != Protocol.SnapshotReasons.TransientLast) Assert.Equal(snapshot.Id, boundary.SnapshotId);
        Assert.Equal((boundary.Generation, boundary.Hash, boundary.AfterSequence, boundary.Delivered, boundary.Width, boundary.Height),
            (snapshot.Generation, snapshot.Hash, snapshot.AfterSequence, snapshot.Delivered, snapshot.Width, snapshot.Height));
        Assert.Equal(menuDepth, boundary.MenuDepth);
        Assert.Equal(snapshot.Hash, ProxyScreenHash.Compute(ScreenSnapshot.Deserialize(Convert.FromBase64String(snapshot.Cells))));
        Assert.Empty(snapshot.Inspection!.Errors);
        return boundary;
    }

    private List<TEvent> Deliver(bool record, out int marks)
    {
        Drain();
        var native = new QueueBackend(new NullDriver(40, 12));
        var proxy = Install(native);
        var app = new TApplication();
        var collected = Collect(proxy);
        if (record) { proxy.Recorder.Start(40, 12, null); proxy.OnIdleBoundary(app); }
        native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF2));
        native.Pending.AddRange([Mouse(Events.evMouseDown, 5, 5, 1), Mouse(Events.evMouseUp, 5, 5, 0), Mouse(Events.evMouseDown, 5, 5, 1),
            Mouse(Events.evMouseUp, 5, 5, 0), Mouse(Events.evMouseMove, 6, 5, 0), Command(0x7a01), Resize()]);
        native.Inner.EnqueueKey(TestKeys.KeyDown(Keys.kbF3));
        var result = new List<TEvent>();
        int i = 0;
        for (TEvent ev = default; ; i++)
        {
            long generation = proxy.ScreenGeneration;
            TScreen.GetEvent(ref ev);
            Assert.Equal(generation, proxy.ScreenGeneration);                 // observing never writes the screen
            if (ev.What == Events.evNothing) break;
            result.Add(ev);
            Paint(proxy, (char)('a' + i % 26));
            if (i % 3 == 2) { proxy.OnIdleBoundary(app); proxy.Snapshots.Read(0, 8, 1 << 20); Assert.NotEqual(0, proxy.ScreenGeneration); }
        }
        marks = collected.Count;
        app.ShutDown(); proxy.Shutdown(); TDisplay.driver = null;
        return result;
    }

    private static string Describe(TEvent e) => (e.What & Events.evMouse) != 0
        ? $"{e.What}:{e.mouse.where.x},{e.mouse.where.y}:{e.mouse.buttons}:{e.mouse.eventFlags}"
        : e.What == Events.evKeyDown ? $"key:{e.keyDown.keyCode}" : $"{e.What}:{e.message.command}";

    private static List<RecordedBoundary> Collect(WorkbenchProxyDriver proxy)
    {
        var marks = new List<RecordedBoundary>();
        proxy.Boundaries.Emitted = marks.Add;
        return marks;
    }

    private static (long, string, long, string) Shape(RecordedBoundary b) => (b.Id, b.Cause, b.AfterSequence, b.Kind);
    private static (long, long, long, int) Head((long Added, long Dropped, long Released, int Retained, long Bytes) c) => (c.Added, c.Dropped, c.Released, c.Retained);

    // An application redraw: one root cell written through the ordinary root write path (root storage + driver WriteBuf).
    private static void Paint(WorkbenchProxyDriver proxy, char c)
    {
        var cell = new TScreenChar(c, 0x1f);
        TScreen.ScreenBuffer!.SetChar(2, 5, cell);
        proxy.WriteBuf(2, 5, 1, 1, new[] { cell });
    }

    private static ScreenSnapshot Cells(int w, int h) => new(w, h, Enumerable.Range(0, w * h).Select(i => new TScreenChar((char)('A' + i % 26), 7)).ToArray());

    private static string Text(ScreenSnapshot s)
    {
        var text = new System.Text.StringBuilder();
        for (int y = 0; y < s.Height; y++) { for (int x = 0; x < s.Width; x++) text.Append(s[x, y].Character is '\0' ? ' ' : s[x, y].Character); text.Append('\n'); }
        return text.ToString();
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
    private static TEvent Command(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; return ev; }
    private static TEvent Resize() => Command(Views.cmScreenResized);
    private static TEvent Mouse(ushort what, int x, int y, byte buttons)
    {
        var ev = new TEvent { What = what };
        ev.mouse.where = new TPoint(x, y); ev.mouse.buttons = buttons;
        return ev;
    }

    /// <summary>A real application: File menu (two items), F3 = modal dialog, F7 = dialog closed by a Pending event, F4 = quit.</summary>
    private sealed class MenuApp : TApplication
    {
        public Action<int>? IdlePass;
        public Action? Quitting;
        public bool TimedOut, LimitationDialogRan;
        public ScreenSnapshot? Final;
        private int passes;

        public override void Idle() { base.Idle(); IdlePass?.Invoke(++passes); }

        public override TMenuBar? InitMenuBar(TRect r)
        {
            r.b.y = r.a.y + 1;
            return new TMenuBar(r, new TSubMenu("~F~ile", Keys.kbAltF) +
                new TMenuItem("~N~ew probe", cmProbeNew, Keys.kbNoKey) +
                new TMenuItem("~O~pen probe", cmProbeOpen, Keys.kbNoKey));
        }

        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode is Keys.kbF3 or Keys.kbF7)
            {
                bool limitation = ev.keyDown.keyCode == Keys.kbF7;
                ClearEvent(ref ev);
                using var dialog = new TDialog(new TRect(10, 4, 40, 12), "Probe dialog");
                if (limitation)
                {
                    var cancel = new TEvent { What = Events.evCommand }; cancel.message.command = Views.cmCancel;
                    PutEvent(ref cancel);
                    LimitationDialogRan = true;
                }
                DeskTop!.ExecView(dialog);
                return;
            }
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF4)
            {
                Final = ScreenSnapshot.CopyRoot();
                Quitting?.Invoke();
                EndModal(Views.cmQuit); ClearEvent(ref ev); return;
            }
            base.HandleEvent(ref ev);
        }
    }

    /// <summary>Stand-in native backend: PumpMessages appends already translated events to the framework queue (as console
    /// and SDL drivers do for mouse/resize); keys come from the inner NullDriver's ReadKeyEvent.</summary>
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
