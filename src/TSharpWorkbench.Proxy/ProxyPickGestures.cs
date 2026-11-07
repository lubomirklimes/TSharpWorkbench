using TSharpVision.Constants;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

// Pick gesture engine and live hit test.

internal enum GestureAction { Deliver, Swallow }

/// <summary>A completed gesture (the release was swallowed): what to emit as a pick.</summary>
internal sealed record GestureOutcome(string Kind, string Trigger, int X, int Y, int EndX, int EndY, CellRect Region, int Swallowed);

/// <summary>Result of filtering one event. <see cref="Began"/>: this event is the press that started a gesture (capture
/// the frozen state now, before anything else). <see cref="Completed"/>: the release that ended a pick. <see cref="Aborted"/>:
/// the gesture ended without a pick (no snapshot must be emitted).</summary>
internal readonly record struct GestureDecision(GestureAction Action, bool Began = false, GestureOutcome? Completed = null, bool Aborted = false);

/// <summary>UI-thread pick gesture state machine of one proxy (Ctrl+Shift+left and one-shot Pick Mode). Never injects, never
/// changes application state. Arm/Cancel/State are thread-safe (transport thread); Filter/Reset run on the UI thread.</summary>
/// <remarks>
/// Invariants: never deliver a release whose press was swallowed; never swallow a release whose press was delivered.
/// A gesture is press..release; every mouse event in between (moves, auto, wheel, other buttons) is swallowed. A gesture
/// that cannot complete (a second button or a new press, a button-less move = capture/focus loss, Reset, recording no longer
/// active) aborts: <see cref="GestureDecision.Aborted"/> is reported exactly once, and swallowing continues until the release
/// when one may still come. After any gesture the engine stays guarded until the next delivered press: a stray release is
/// swallowed (orphan) and a false <c>meDoubleClick</c> on that press — classified against the swallowed press by
/// TEventQueue (at drain time) or by the native driver — is cleared.
/// </remarks>
internal sealed class ProxyPickGestures
{
    /// <summary>Release within this many cells (Chebyshev) of the press cell = <c>control</c>; farther = <c>region</c>.</summary>
    internal const int DragThreshold = 0;

    private volatile bool modifierGestures;
    private volatile int armed;          // 0/1, any thread
    private volatile bool capturing;     // press..release of a gesture (UI thread writes)
    private volatile bool guarded;       // the last press seen was swallowed; no delivered press since (UI thread writes)
    private bool aborted, abortReported; // UI thread only
    private string trigger = Protocol.PickTriggers.CtrlShift;
    private byte gestureButtons, guardButtons;
    private int x, y, endX, endY, swallowedThis;
    private long picks, abortedCount, swallowed, doubleClicksCleared;

    /// <summary>Per recording (RecordStart option <see cref="Protocol.RecordOptions.ModifierPicks"/>): intercept Ctrl+Shift.</summary>
    internal bool ModifierGestures { get => modifierGestures; set => modifierGestures = value; }

    /// <summary>Every mouse event must reach <see cref="Filter"/>: a gesture is in progress (even after the recording stopped
    /// or paused: its release is still swallowed), or the last press the engine saw was swallowed and no delivered press has
    /// followed yet (orphan-release and false-double-click guard).</summary>
    internal bool Engaged => capturing || guarded;

    /// <summary><see cref="Protocol.PickStates"/>: <c>capturing</c> only for the press..release of a gesture.</summary>
    internal string State => capturing ? Protocol.PickStates.Capturing : armed != 0 ? Protocol.PickStates.Armed : Protocol.PickStates.Off;

    /// <summary>Any thread: arm the one-shot Pick Mode (next left press, no modifiers needed). False when refused (a gesture
    /// is being captured). Arming twice is idempotent.</summary>
    internal bool Arm()
    {
        if (capturing) return false;
        armed = 1;
        return true;
    }

    /// <summary>Any thread: disarm Pick Mode (an engaged gesture still swallows until its release).</summary>
    internal void Cancel() => armed = 0;

    /// <summary>UI thread: classify one event about to be handed to the application. May clear a false meDoubleClick in
    /// <paramref name="ev"/> on a delivered press that follows a swallowed one.</summary>
    /// <param name="width">Current grid width (clipping).</param>
    /// <param name="recording">Recording active and not paused (a gesture may only start then; an in-flight gesture aborts
    /// when it is false).</param>
    internal GestureDecision Filter(ref TEvent ev, int width, int height, bool recording)
    {
        ushort what = ev.What;
        if (what is not (Events.evMouseDown or Events.evMouseUp or Events.evMouseMove or Events.evMouseAuto or Events.evMouseWheel))
            return new(GestureAction.Deliver);
        if (capturing) return Continue(ref ev, width, height, recording);

        if (what == Events.evMouseDown)
        {
            if (recording && ev.mouse.buttons == Events.mbLeftButton && Trigger(ev.mouse.controlKeyState) is { } t)
            {
                trigger = t;
                gestureButtons = guardButtons = ev.mouse.buttons;
                (x, y) = Clip(ev.mouse.where.x, ev.mouse.where.y, width, height);
                (endX, endY) = (x, y);
                aborted = abortReported = false;
                swallowedThis = 1;
                Interlocked.Increment(ref swallowed);
                guarded = true;
                capturing = true;
                return new(GestureAction.Swallow, Began: true);
            }
            if (guarded)
            {
                // TEventQueue (drain time) / the native driver classified this press against the swallowed one.
                if ((ev.mouse.eventFlags & Events.meDoubleClick) != 0 && ev.mouse.buttons == guardButtons)
                {
                    ev.mouse.eventFlags &= ~Events.meDoubleClick;
                    Interlocked.Increment(ref doubleClicksCleared);
                }
                guarded = false;
            }
            return new(GestureAction.Deliver);
        }
        if (what == Events.evMouseUp && guarded)
        {
            Interlocked.Increment(ref swallowed); // orphan release: its press was swallowed (or no press was delivered since)
            return new(GestureAction.Swallow);
        }
        return new(GestureAction.Deliver);
    }

    private string? Trigger(uint controlKeyState)
    {
        if (armed != 0 && Interlocked.CompareExchange(ref armed, 0, 1) == 1) return Protocol.PickTriggers.PickMode;
        bool ctrlShift = (controlKeyState & Keys.kbShift) != 0 && (controlKeyState & Keys.kbCtrlShift) != 0
            && (controlKeyState & Keys.kbAltShift) == 0;
        return modifierGestures && ctrlShift ? Protocol.PickTriggers.CtrlShift : null;
    }

    private GestureDecision Continue(ref TEvent ev, int width, int height, bool recording)
    {
        swallowedThis++;
        Interlocked.Increment(ref swallowed);
        if (!recording) Abort();
        byte buttons = ev.mouse.buttons;
        switch (ev.What)
        {
            case Events.evMouseUp:
                capturing = false;
                if (aborted) return Report();
                (endX, endY) = Clip(ev.mouse.where.x, ev.mouse.where.y, width, height);
                Interlocked.Increment(ref picks);
                return new(GestureAction.Swallow, Completed: Outcome());
            case Events.evMouseDown: // a new button transition: the queue saw the buttons go up without us
                Abort();
                break;
            case Events.evMouseMove:
                if (buttons == 0) { Abort(); capturing = false; } // capture/focus lost: no release will come for this press
                else if ((buttons & ~gestureButtons) != 0) Abort(); // a second button
                else (endX, endY) = Clip(ev.mouse.where.x, ev.mouse.where.y, width, height);
                break;
            case Events.evMouseAuto:
                if ((buttons & ~gestureButtons) != 0) Abort();
                break;
            // evMouseWheel: swallowed, no state change
        }
        return Report();
    }

    private GestureDecision Report()
    {
        bool report = aborted && !abortReported;
        if (report) abortReported = true;
        return new(GestureAction.Swallow, Aborted: report);
    }

    private void Abort()
    {
        if (aborted) return;
        aborted = true;
        Interlocked.Increment(ref abortedCount);
    }

    private GestureOutcome Outcome()
    {
        bool click = Math.Abs(endX - x) <= DragThreshold && Math.Abs(endY - y) <= DragThreshold;
        CellRect region = click ? new(x, y, 1, 1)
            : new(Math.Min(x, endX), Math.Min(y, endY), Math.Abs(endX - x) + 1, Math.Abs(endY - y) + 1);
        return new(click ? Protocol.PickKinds.Control : Protocol.PickKinds.Region, trigger, x, y, endX, endY, region, swallowedThis);
    }

    private static (int, int) Clip(int px, int py, int width, int height) =>
        (Math.Clamp(px, 0, Math.Max(0, width - 1)), Math.Clamp(py, 0, Math.Max(0, height - 1)));

    /// <summary>UI thread: new recording / stop / pause. Disarms; an engaged gesture is aborted (reported on its next Filter
    /// call) and keeps swallowing until its release. The post-gesture guard is kept (it protects the application).</summary>
    internal void Reset()
    {
        armed = 0;
        if (capturing) Abort();
    }

    internal (long Picks, long Aborted, long Swallowed, long DoubleClicksCleared) Counters() =>
        (Interlocked.Read(ref picks), Interlocked.Read(ref abortedCount), Interlocked.Read(ref swallowed), Interlocked.Read(ref doubleClicksCleared));
}

/// <summary>UI thread: live front-to-back hit test at a press, from the modal top view (menus and status line included),
/// using the framework's own visibility/z-order. Returns the hit view's path in ViewTreeInspector format (valid for a view
/// tree captured in the same pass), or null when nothing selectable was hit.</summary>
/// <remarks>Mirrors TGroup.HandleEvent positional routing: at each level the first child in front-to-back order
/// (<c>FirstThat</c> = <c>ForEachView</c> order) whose <c>ContainsMouse</c> holds (sfVisible + MouseInView), descending
/// into groups. Open menu boxes and the menu bar/status line are children of the application, so they are found in their
/// real z-order. The test is visual (what the user pointed at): it does not stop at the modal view, and it ignores
/// sfDisabled. Read-only: no event is dispatched, nothing is selected or focused.</remarks>
internal static class ProxyPickHitTest
{
    internal static string? PathAt(TProgram application, int x, int y)
    {
        if (x < 0 || y < 0 || x >= TScreen.ScreenWidth || y >= TScreen.ScreenHeight) return null;
        var probe = new TEvent { What = Events.evMouseDown };
        probe.mouse.where = new TPoint(x, y);
        if (!application.ContainsMouse(probe)) return null;
        TView? hit = null;
        TGroup group = application;
        for (int depth = 0; depth < 64; depth++)
        {
            TView? child = null;
            foreach (TView c in ViewTreeInspector.Children(group))
                if (c.ContainsMouse(probe)) { child = c; break; }
            if (child is null) break;
            hit = child;
            if (child is TGroup inner) group = inner; else break;
        }
        return hit is null ? null : ViewTreeInspector.PathOf(hit, application);
    }
}
