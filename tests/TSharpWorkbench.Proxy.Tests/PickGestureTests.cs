using TSharpVision;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

// Workstream C: the pick gesture engine as a pure UI-thread state machine over TEvent sequences, the real TEventQueue
// double-click classification, and the live hit test on real in-process views (NullDriver; logical evidence only).
public sealed class PickGestureTests : IDisposable
{
    private const uint CtrlShift = Keys.kbLeftShift | Keys.kbCtrlShift;
    private const int W = 40, H = 12;
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

    // ── state machine ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CtrlShiftClickIsSwallowedAndCompletesAControlPick()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        var down = Filter(g, Down(5, 4, CtrlShift));
        Assert.Equal((GestureAction.Swallow, true), (down.Action, down.Began));
        Assert.Equal(Protocol.PickStates.Capturing, g.State);
        Assert.True(g.Engaged);
        var up = Filter(g, Up(5, 4));
        Assert.Equal(GestureAction.Swallow, up.Action);
        var o = Assert.IsType<GestureOutcome>(up.Completed);
        Assert.Equal((Protocol.PickKinds.Control, Protocol.PickTriggers.CtrlShift, 5, 4, 5, 4, new CellRect(5, 4, 1, 1), 2),
            (o.Kind, o.Trigger, o.X, o.Y, o.EndX, o.EndY, o.Region, o.Swallowed));
        Assert.False(up.Aborted);
        Assert.Equal(Protocol.PickStates.Off, g.State);
        Assert.Equal((1L, 0L, 2L, 0L), g.Counters());
    }

    [Theory]
    [InlineData(false, CtrlShift, true)]                       // option off
    [InlineData(true, CtrlShift, false)]                       // not recording / paused
    [InlineData(true, Keys.kbCtrlShift, true)]                 // Ctrl only
    [InlineData(true, Keys.kbLeftShift, true)]                 // Shift only
    [InlineData(true, CtrlShift | Keys.kbAltShift, true)]      // Ctrl+Shift+Alt is not the gesture
    public void OrdinaryPressesAreDeliveredUntouched(bool modifier, uint keys, bool recording)
    {
        var g = new ProxyPickGestures { ModifierGestures = modifier };
        foreach (var ev in new[] { Down(3, 3, keys), Move(4, 3, 1), Up(4, 3) })
        {
            var e = ev;
            var d = g.Filter(ref e, W, H, recording);
            Assert.Equal(new GestureDecision(GestureAction.Deliver), d);
            Assert.Equal(ev.mouse.eventFlags, e.mouse.eventFlags);
        }
        Assert.False(g.Engaged);
        Assert.Equal(default, g.Counters());
    }

    [Fact]
    public void RightButtonWithCtrlShiftIsNotAGesture()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Assert.Equal(GestureAction.Deliver, Filter(g, Down(3, 3, CtrlShift, buttons: Events.mbRightButton)).Action);
        Assert.Equal(GestureAction.Deliver, Filter(g, Up(3, 3)).Action);
    }

    [Theory]
    [InlineData(10, 3, 2, 9, 2, 3, 9, 7)]       // drag up-left: region normalized
    [InlineData(2, 2, 6, 2, 2, 2, 5, 1)]        // horizontal
    [InlineData(5, 5, 100, 50, 5, 5, 35, 7)]    // released outside the grid: clipped to 39,11
    public void DragBecomesANormalizedClippedRegion(int x, int y, int ex, int ey, int rx, int ry, int rw, int rh)
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Assert.True(Filter(g, Down(x, y, CtrlShift)).Began);
        Assert.Equal(GestureAction.Swallow, Filter(g, Move((x + ex) / 2, (y + ey) / 2, 1)).Action);
        Assert.Equal(GestureAction.Swallow, Filter(g, Auto((x + ex) / 2, (y + ey) / 2, 1)).Action);
        Assert.Equal(GestureAction.Swallow, Filter(g, Wheel((x + ex) / 2, (y + ey) / 2)).Action);
        var o = Filter(g, Up(ex, ey)).Completed!;
        Assert.Equal(Protocol.PickKinds.Region, o.Kind);
        Assert.Equal(new CellRect(rx, ry, rw, rh), o.Region);
        Assert.Equal(5, o.Swallowed);
        Assert.True(o.EndX < W && o.EndY < H);
    }

    [Fact]
    public void DragBackToThePressCellIsAControlPick()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(7, 7, CtrlShift));
        Filter(g, Move(12, 9, 1));
        Assert.Equal(Protocol.PickKinds.Control, Filter(g, Up(7, 7)).Completed!.Kind);
    }

    [Fact]
    public void PickModeIsOneShotAndAcceptsAnyModifiers()
    {
        var g = new ProxyPickGestures();                       // no modifier gestures
        Assert.Equal(Protocol.PickStates.Off, g.State);
        Assert.True(g.Arm()); Assert.True(g.Arm());           // idempotent
        Assert.Equal(Protocol.PickStates.Armed, g.State);
        Assert.Equal(GestureAction.Deliver, Filter(g, Down(1, 1, 0, buttons: Events.mbRightButton)).Action); // not left: stays armed
        Assert.Equal(GestureAction.Deliver, Filter(g, Up(1, 1)).Action);
        Assert.Equal(Protocol.PickStates.Armed, g.State);
        var d = Filter(g, Down(2, 2, Keys.kbAltShift));
        Assert.True(d.Began);
        Assert.False(g.Arm());                                 // refused while capturing
        Assert.Equal(Protocol.PickTriggers.PickMode, Filter(g, Up(2, 2)).Completed!.Trigger);
        Assert.Equal(Protocol.PickStates.Off, g.State);
        Assert.Equal(GestureAction.Deliver, Filter(g, Down(2, 3)).Action); // one shot
        Assert.Equal(GestureAction.Deliver, Filter(g, Up(2, 3)).Action);
    }

    [Fact]
    public void PickModeWaitsWhileNotRecordingAndCancelDisarms()
    {
        var g = new ProxyPickGestures();
        g.Arm();
        var e = Down(2, 2);
        Assert.Equal(GestureAction.Deliver, g.Filter(ref e, W, H, recording: false).Action);
        Assert.Equal(Protocol.PickStates.Armed, g.State);
        g.Cancel();
        Assert.Equal(Protocol.PickStates.Off, g.State);
        Filter(g, Up(2, 2));
        Assert.Equal(GestureAction.Deliver, Filter(g, Down(2, 2)).Action);
        g.Arm(); g.Reset();
        Assert.Equal(Protocol.PickStates.Off, g.State);
    }

    [Fact]
    public void ModifierReleasedMidDragKeepsTheGesture()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(1, 1, CtrlShift));
        Assert.Equal(GestureAction.Deliver, Filter(g, Modifiers(0)).Action);
        Assert.Equal(GestureAction.Swallow, Filter(g, Move(4, 2, 1, 0)).Action);
        var o = Filter(g, Up(4, 2, 0)).Completed!;
        Assert.Equal(new CellRect(1, 1, 4, 2), o.Region);
    }

    [Fact]
    public void InterleavedKeyboardInputIsDeliveredDuringAGesture()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(1, 1, CtrlShift));
        var key = Key(Keys.kbEnter);
        var d = g.Filter(ref key, W, H, true);
        Assert.Equal(new GestureDecision(GestureAction.Deliver), d);
        Assert.Equal(Keys.kbEnter, key.keyDown.keyCode);
        Assert.Equal(GestureAction.Deliver, Filter(g, Modifiers(CtrlShift)).Action);
        var cmd = new TEvent { What = Events.evCommand };
        Assert.Equal(GestureAction.Deliver, g.Filter(ref cmd, W, H, true).Action);
        Assert.NotNull(Filter(g, Up(1, 1)).Completed);
        Assert.Equal(2L, g.Counters().Swallowed);
    }

    [Theory]
    [InlineData("second-button-move")]
    [InlineData("second-button-auto")]
    [InlineData("new-press")]
    public void SecondPressAbortsOnceAndKeepsSwallowingUntilTheRelease(string how)
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(3, 3, CtrlShift));
        var second = how switch
        {
            "second-button-move" => Move(4, 3, 3),
            "second-button-auto" => Auto(3, 3, 3),
            _ => Down(3, 3),
        };
        var abort = Filter(g, second);
        Assert.Equal((GestureAction.Swallow, true), (abort.Action, abort.Aborted));
        Assert.Equal(Protocol.PickStates.Capturing, g.State);   // a release may still come
        var later = Filter(g, Auto(3, 3, 1));
        Assert.Equal((GestureAction.Swallow, false), (later.Action, later.Aborted)); // reported once
        var up = Filter(g, Up(3, 3));
        Assert.Equal(new GestureDecision(GestureAction.Swallow), up);              // no pick
        // SDL reports each button's release: the second release is an orphan and must not reach the application.
        Assert.Equal(GestureAction.Swallow, Filter(g, Up(3, 3)).Action);
        Assert.True(g.Engaged);
        Assert.Equal(GestureAction.Deliver, Filter(g, Down(8, 8)).Action);
        Assert.False(g.Engaged);
        Assert.Equal(GestureAction.Deliver, Filter(g, Up(8, 8)).Action);
        Assert.Equal((0L, 1L), (g.Counters().Picks, g.Counters().Aborted));
    }

    [Fact]
    public void ButtonlessMoveAbortsAndALateReleaseIsNeverDeliveredAsAnOrphan()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(3, 3, CtrlShift));
        Filter(g, Move(5, 5, 1));
        var lost = Filter(g, Move(6, 5, 0));
        Assert.Equal((GestureAction.Swallow, true), (lost.Action, lost.Aborted));
        Assert.Equal(Protocol.PickStates.Off, g.State);
        Assert.True(g.Engaged);                                 // guard until the next delivered press
        Assert.Equal(GestureAction.Deliver, Filter(g, Move(7, 5, 0)).Action); // hover after the abort reaches the app
        Assert.Equal(GestureAction.Swallow, Filter(g, Up(7, 5)).Action);      // late release: orphan
        Assert.Equal(GestureAction.Deliver, Filter(g, Down(7, 5)).Action);
        Assert.Equal(GestureAction.Deliver, Filter(g, Up(7, 5)).Action);      // its press was delivered
    }

    [Fact]
    public void ResetMidGestureAbortsButTheReleaseAfterStopIsStillSwallowed()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        g.Arm();
        Filter(g, Down(3, 3));
        g.Reset();                                              // RecordStop
        Assert.True(g.Engaged);
        var e = Move(4, 4, 1);
        var moved = g.Filter(ref e, W, H, recording: false);
        Assert.Equal((GestureAction.Swallow, true), (moved.Action, moved.Aborted));
        e = Up(4, 4);
        Assert.Equal(new GestureDecision(GestureAction.Swallow), g.Filter(ref e, W, H, recording: false));
        Assert.Equal((0L, 1L, 3L), (g.Counters().Picks, g.Counters().Aborted, g.Counters().Swallowed));
        Assert.Equal(Protocol.PickStates.Off, g.State);
    }

    [Fact]
    public void RecordingEndingWithoutResetAbortsTheGestureAtItsRelease()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(3, 3, CtrlShift));
        var e = Up(3, 3);
        var up = g.Filter(ref e, W, H, recording: false);       // paused/stopped: no pick may be emitted
        Assert.Equal((GestureAction.Swallow, true, (GestureOutcome?)null), (up.Action, up.Aborted, up.Completed));
    }

    [Fact]
    public void FirstDeliveredPressAfterASwallowedPressHasAFalseDoubleClickCleared()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(5, 5, CtrlShift)); Filter(g, Up(5, 5));
        var e = Down(5, 5, flags: Events.meDoubleClick);
        Assert.Equal(GestureAction.Deliver, g.Filter(ref e, W, H, true).Action);
        Assert.Equal(0u, e.mouse.eventFlags & Events.meDoubleClick);
        Assert.Equal(1L, g.Counters().DoubleClicksCleared);
        Filter(g, Up(5, 5));
        e = Down(5, 5, flags: Events.meDoubleClick);            // a genuine double click on the delivered press: kept
        g.Filter(ref e, W, H, true);
        Assert.NotEqual(0u, e.mouse.eventFlags & Events.meDoubleClick);
        Assert.Equal(1L, g.Counters().DoubleClicksCleared);
    }

    [Fact]
    public void DoubleClickOfAnotherButtonAfterAPickIsNotTouched()
    {
        var g = new ProxyPickGestures { ModifierGestures = true };
        Filter(g, Down(5, 5, CtrlShift)); Filter(g, Up(5, 5));
        var e = Down(5, 5, buttons: Events.mbRightButton, flags: Events.meDoubleClick);
        g.Filter(ref e, W, H, true);
        Assert.NotEqual(0u, e.mouse.eventFlags & Events.meDoubleClick);
        Assert.Equal(0L, g.Counters().DoubleClicksCleared);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealEventQueueClassificationAfterASwallowedPressIsNotAFalseDoubleClick(bool pick)
    {
        // TEventQueue classifies at drain time (WorkbenchProxyDriver.DrainQueue) against the last press IT saw, the swallowed
        // gesture press included; the engine then runs on the drained events in the same order, before the application.
        Drain();
        var g = new ProxyPickGestures { ModifierGestures = true };
        foreach (var ev in new[] { Down(5, 5, pick ? CtrlShift : 0), Up(5, 5), Down(5, 5), Up(5, 5) }) TEventQueue.Enqueue(ev);
        var drained = new List<TEvent>();
        for (TEvent ev = default; ;)
        {
            TEventQueue.GetNextEvent(ref ev);
            if (ev.What == Events.evNothing) break;
            drained.Add(ev);
        }
        Assert.Equal(4, drained.Count);
        Assert.NotEqual(0u, drained[2].mouse.eventFlags & Events.meDoubleClick); // the queue flags it either way
        var delivered = new List<TEvent>();
        foreach (var ev in drained)
        {
            var e = ev;
            if (g.Filter(ref e, W, H, true).Action == GestureAction.Deliver) delivered.Add(e);
        }
        if (pick)
        {
            var press = Assert.Single(delivered, e => e.What == Events.evMouseDown);
            Assert.Equal(0u, press.mouse.eventFlags & Events.meDoubleClick);
            Assert.Equal(2, delivered.Count);
            Assert.Equal(1L, g.Counters().DoubleClicksCleared);
        }
        else
        {
            Assert.Equal(4, delivered.Count);                    // a real double click stays one
            Assert.NotEqual(0u, delivered[2].mouse.eventFlags & Events.meDoubleClick);
        }
    }

    [Fact]
    public void RealEventQueueProducesNoAutoAfterASwallowedPressIsReleased()
    {
        // evMouseAuto comes only from TEventQueue.HandleClickDetection for a held, unmoved down event; after the swallowed
        // release the queue's last mouse has no buttons, so the next held press is a fresh evMouseDown (not auto).
        Drain();
        int repeat = TEventQueue.RepeatDelay;
        TEventQueue.RepeatDelay = 0;
        try
        {
            var g = new ProxyPickGestures { ModifierGestures = true };
            foreach (var ev in new[] { Down(5, 5, CtrlShift), Down(5, 5, CtrlShift), Up(5, 5), Down(5, 5), Down(5, 5), Up(5, 5) })
                TEventQueue.Enqueue(ev);
            var seen = new List<(ushort What, GestureAction Action)>();
            for (TEvent ev = default; ;)
            {
                TEventQueue.GetNextEvent(ref ev);
                if (ev.What == Events.evNothing) break;
                var e = ev;
                seen.Add((ev.What, g.Filter(ref e, W, H, true).Action));
            }
            Assert.Equal([(Events.evMouseDown, GestureAction.Swallow), (Events.evMouseAuto, GestureAction.Swallow), (Events.evMouseUp, GestureAction.Swallow),
                (Events.evMouseDown, GestureAction.Deliver), (Events.evMouseAuto, GestureAction.Deliver), (Events.evMouseUp, GestureAction.Deliver)], seen);
        }
        finally { TEventQueue.RepeatDelay = repeat; }
    }

    [Fact]
    public void RandomSequencesKeepThePressReleaseInvariants()
    {
        // Never deliver a release whose press was swallowed; never swallow a release whose press was delivered.
        for (int seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var g = new ProxyPickGestures { ModifierGestures = random.Next(2) == 0 };
            bool recording = true, held = false;
            bool? lastPressDelivered = null;
            for (int step = 0; step < 60; step++)
            {
                int r = random.Next(100);
                if (r < 4) { g.Reset(); continue; }
                if (r < 8) { recording = !recording; continue; }
                if (r < 12) { g.Arm(); continue; }
                if (r < 14) { g.Cancel(); continue; }
                TEvent ev;
                int x = random.Next(W), y = random.Next(H);
                if (!held && r < 50) ev = Down(x, y, random.Next(3) == 0 ? CtrlShift : 0, buttons: random.Next(5) == 0 ? Events.mbRightButton : Events.mbLeftButton);
                else if (held && r < 45) ev = Up(x, y);
                else if (r < 70) ev = Move(x, y, held ? (byte)(random.Next(6) == 0 ? 3 : 1) : (byte)0);
                else if (r < 80) ev = Wheel(x, y);
                else if (r < 85) ev = Up(x, y);                         // stray/second release (SDL two-button, late release)
                else ev = Key(Keys.kbEnter);
                var e = ev;
                var d = g.Filter(ref e, W, H, recording);
                bool delivered = d.Action == GestureAction.Deliver;
                if (ev.What == Events.evMouseDown) { held = true; lastPressDelivered = delivered; } // the latest press owns the next release
                if (ev.What == Events.evMouseUp)
                {
                    Assert.True(!delivered || lastPressDelivered != false, $"seed {seed} step {step}: release of a swallowed press delivered");
                    Assert.True(delivered || lastPressDelivered != true, $"seed {seed} step {step}: release of a delivered press swallowed");
                    held = false;
                }
                if (ev.What == Events.evMouseMove && ev.mouse.buttons == 0) held = false;
                if (ev.What is Events.evKeyDown) Assert.True(delivered);
            }
        }
    }

    [Fact]
    public void ArmCancelAndStateAreThreadSafe()
    {
        var g = new ProxyPickGestures();
        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (i % 2 == 0) g.Arm(); else g.Cancel();
                Assert.Contains(g.State, new[] { Protocol.PickStates.Off, Protocol.PickStates.Armed, Protocol.PickStates.Capturing });
                _ = g.Counters();
            }
        })).ToArray();
        int began = 0, completed = 0;
        for (int i = 0; i < 2000; i++)
        {
            var d = Filter(g, Down(i % W, i % H));
            if (d.Began) began++;
            if (Filter(g, Up(i % W, i % H)).Completed is not null) completed++;
        }
        stop.Cancel();
        Task.WaitAll(workers);
        Assert.Equal(began, completed);
        Assert.Equal(began, (int)g.Counters().Picks);
        g.Cancel(); g.Arm();
        Assert.True(Filter(g, Down(1, 1)).Began);
        Filter(g, Up(1, 1));
        Assert.False(Filter(g, Down(1, 1)).Began);              // exactly one consumer per arm
    }

    // ── hit test (real views, NullDriver) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HitTestFollowsFrameworkZOrderAndMatchesTheCapturedTree()
    {
        Install(new NullDriver(60, 20));
        using var app = new HitApp();
        var back = new TWindow(new TRect(2, 2, 30, 12), "Back", 1);
        var backButton = new TButton(new TRect(2, 2, 14, 4), "~B~ack", 0x7a20, 0);
        back.Insert(backButton);
        var front = new TWindow(new TRect(20, 4, 50, 14), "Front", 2);
        var ok = new TButton(new TRect(3, 3, 13, 5), "~O~K", 0x7a21, 0);
        front.Insert(ok);
        app.DeskTop!.Insert(back);
        app.DeskTop.Insert(front);
        var tree = ViewTreeInspector.Inspect(app);
        string? Hit(int x, int y) => ProxyPickHitTest.PathAt(app, x, y);
        ViewNode Node(string path) => Find(tree.Root, path)!;

        var before = Fingerprint(app);
        long generation = ((WorkbenchProxyDriver)TDisplay.driver!).ScreenGeneration;

        Assert.Equal(ViewTreeInspector.PathOf(ok, app), Hit(25, 8));                  // button in the front window
        Assert.Equal("OK", Node(Hit(25, 8)!).Text);
        Assert.Equal(ViewTreeInspector.PathOf(backButton, app), Hit(6, 5));
        Assert.Contains("TFrame", Node(Hit(20, 9)!).Kind);                            // front frame covers the back window
        Assert.Equal(ViewTreeInspector.PathOf(front, app), Parent(Hit(20, 9)!));
        Assert.Equal("TMenuBar", Node(Hit(1, 0)!).Kind);
        Assert.Equal("TStatusLine", Node(Hit(1, 19)!).Kind);
        Assert.Equal("TBackground", Node(Hit(55, 16)!).Kind);                         // empty desktop
        Assert.Null(Hit(-1, 3)); Assert.Null(Hit(60, 3)); Assert.Null(Hit(3, 20));
        Assert.Equal(before, Fingerprint(app));                                       // read-only: no select/focus change
        Assert.Equal(generation, ((WorkbenchProxyDriver)TDisplay.driver!).ScreenGeneration); // and nothing redrawn
        front.Hide();
        Assert.Equal(ViewTreeInspector.PathOf(back, app), Parent(Hit(20, 9)!));       // hidden: the back window shows
        front.Show();
        app.ShutDown();
    }

    [Fact]
    public void HitTestInsideAModalDialogAndAnOpenMenuBox()
    {
        var native = new NullDriver(60, 20);
        Install(native);
        var app = new HitApp();
        var results = new Dictionary<string, (string? Path, ViewTree Tree, MenuState Menu)>();
        var steps = new Queue<Action>();
        void Observe(string name, int x, int y)
        {
            var tree = ViewTreeInspector.Inspect(app);
            var before = Fingerprint(app);
            string? path = ProxyPickHitTest.PathAt(app, x, y);
            Assert.Equal(before, Fingerprint(app));
            results[name] = (path, tree, MenuInspector.Inspect(app));
        }
        steps.Enqueue(() => native.EnqueueKey(TestKeys.KeyDown(Keys.kbF3)));
        steps.Enqueue(() => { Observe("dialog-button", 18, 9); Observe("behind-dialog", 50, 15); });
        steps.Enqueue(() => native.EnqueueKey(TestKeys.KeyDown(Keys.kbEsc)));
        steps.Enqueue(() => native.EnqueueKey(TestKeys.KeyDown(Keys.kbF10)));
        steps.Enqueue(() => native.EnqueueKey(TestKeys.KeyDown(Keys.kbEnter)));
        steps.Enqueue(() =>
        {
            var item = MenuInspector.Inspect(app).Open[0].Items[1].Screen!.Value;
            Observe("menu-item", item.X + 2, item.Y);
        });
        steps.Enqueue(() => native.EnqueueKey(TestKeys.KeyDown(Keys.kbEsc)));
        steps.Enqueue(() => native.EnqueueKey(TestKeys.KeyDown(Keys.kbEsc)));
        steps.Enqueue(() => native.EnqueueKey(TestKeys.KeyDown(Keys.kbF4)));
        int passes = 0;
        app.IdlePass = () =>
        {
            if (++passes > 200) { app.EndModal(Views.cmQuit); return; }
            if (steps.Count > 0) steps.Dequeue()();
        };
        Assert.Equal(0, AppLifecycleGuard.Run(app));
        Assert.Empty(steps);

        var (dialogPath, dialogTree, _) = results["dialog-button"];
        Assert.NotNull(dialogTree.ModalPath);
        Assert.StartsWith(dialogTree.ModalPath + "/", dialogPath);
        Assert.Equal("Yes", Find(dialogTree.Root, dialogPath!)!.Text);
        var (behind, behindTree, _) = results["behind-dialog"];
        Assert.Equal("TBackground", Find(behindTree.Root, behind!)!.Kind);             // visual pick outside the modal view
        var (menuPath, menuTree, menu) = results["menu-item"];
        Assert.Equal("TMenuBox", Find(menuTree.Root, menuPath!)!.Kind);
        Assert.Equal(menu.Open[0].Path, menuPath);
        Assert.True(menu.Active);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static GestureDecision Filter(ProxyPickGestures g, TEvent ev) => g.Filter(ref ev, W, H, true);

    private static TEvent Mouse(ushort what, int x, int y, byte buttons, uint keys = 0, uint flags = 0)
    {
        var ev = new TEvent { What = what };
        ev.mouse.where = new TPoint(x, y); ev.mouse.buttons = buttons; ev.mouse.controlKeyState = keys; ev.mouse.eventFlags = flags;
        return ev;
    }
    private static TEvent Down(int x, int y, uint keys = 0, ushort buttons = Events.mbLeftButton, uint flags = 0) =>
        Mouse(Events.evMouseDown, x, y, (byte)buttons, keys, flags);
    private static TEvent Up(int x, int y, uint keys = 0) => Mouse(Events.evMouseUp, x, y, 0, keys);
    private static TEvent Move(int x, int y, byte buttons, uint keys = 0) => Mouse(Events.evMouseMove, x, y, buttons, keys, Events.meMouseMoved);
    private static TEvent Auto(int x, int y, byte buttons) => Mouse(Events.evMouseAuto, x, y, buttons);
    private static TEvent Wheel(int x, int y) => Mouse(Events.evMouseWheel, x, y, 0, 0, Events.meWheelDown);
    private static TEvent Key(ushort code) { var ev = TestKeys.KeyDown(code); return ev; }
    private static TEvent Modifiers(uint keys) { var ev = new TEvent { What = Events.evModifierChanged }; ev.keyDown.controlKeyState = keys; return ev; }

    private static string Parent(string path) => path[..path.LastIndexOf('/')];

    private static ViewNode? Find(ViewNode node, string path) =>
        node.Path == path ? node : node.Children.Select(c => Find(c, path)).FirstOrDefault(n => n is not null);

    // Selection, focus and state of every view: a hit test must leave all of it untouched.
    private static string Fingerprint(TProgram app)
    {
        var text = new System.Text.StringBuilder();
        void Walk(TView v, int depth)
        {
            text.Append(depth).Append(v.GetType().Name).Append(':').Append(v.state).Append(':').Append(v.owner?.current == v).Append(';');
            if (v is TGroup g) foreach (var c in ViewTreeInspector.Children(g)) Walk(c, depth + 1);
        }
        Walk(app, 0);
        return text.ToString();
    }

    private static void Install(IDriver backend)
    {
        var proxy = WorkbenchProxyDriver.Wrap(backend);
        proxy.Initialize(); TDisplay.driver = proxy;
        TScreen.ScreenBuffer = proxy.AllocateScreenBuffer();
    }
    private static void Drain() { TEventQueue.Resume(); TEvent ev = default; do { TEventQueue.GetNextEvent(ref ev); } while (ev.What != Events.evNothing); }

    /// <summary>File menu (two items), default status line, F3 = modal dialog with a Yes button, F4 = quit.</summary>
    private sealed class HitApp : TApplication
    {
        public Action? IdlePass;

        public override void Idle() { base.Idle(); IdlePass?.Invoke(); }

        public override TMenuBar? InitMenuBar(TRect r)
        {
            r.b.y = r.a.y + 1;
            return new TMenuBar(r, new TSubMenu("~F~ile", Keys.kbAltF) +
                new TMenuItem("~N~ew probe", 0x7a10, Keys.kbNoKey) +
                new TMenuItem("~O~pen probe", 0x7a11, Keys.kbNoKey));
        }

        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF3)
            {
                ClearEvent(ref ev);
                using var dialog = new TDialog(new TRect(10, 4, 40, 14), "Probe dialog");
                dialog.Insert(new TButton(new TRect(4, 4, 14, 6), "~Y~es", Views.cmOK, 0));
                DeskTop!.ExecView(dialog);
                return;
            }
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF4) { EndModal(Views.cmQuit); ClearEvent(ref ev); return; }
            base.HandleEvent(ref ev);
        }
    }
}
