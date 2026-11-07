using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

/// <summary>UI-thread boundary tracker of one proxy. Observes causal UI boundaries while a recording is active,
/// computes generation/hash marks, and retains transient (menu/modal) snapshots immediately into
/// <see cref="WorkbenchProxyDriver.Snapshots"/>. Never injects, never changes focus or UI state, never waits.</summary>
/// <remarks>
/// <para><b>Pass.</b> Recorder cursor (one lock) → new recording? reset → paused: nothing → safety (UI thread, active proxy,
/// <c>lockFlag == 0</c>, root bound to <c>TScreen.ScreenBuffer</c>; else SkippedUnsafe) → signature (modal top identity from
/// <c>TopView()</c>, executing menu chain length). Only when the generation or signature changed (or first mark): copy the
/// root, hash it, dedupe against the last mark (Suppressed), emit. Unchanged boundaries cost the cursor lock, TopView() and a
/// short ParentMenu walk.</para>
/// <para><b>Transients</b> (menu loop executing or modal top ≠ application). Entering/changing one (depth or top changed):
/// inspection of the SAME root copy now, retained (menu-open/menu-change/modal-open/modal-change). Screen changes inside an
/// unchanged transient: the mark's root copy + inspection become the single rolling candidate (replaced each time), committed
/// as transient-last when the signature changes, unless its hash equals the last retained one. So every changed boundary while
/// a transient is open costs one inspection; non-transient boundaries never inspect.</para>
/// <para><b>Pre-input</b> boundaries observe only when input was delivered or the generation advanced since the last pass.
/// The application comes from the last idle boundary (the framework has no public accessor for the running program); before
/// the first idle of a recording, a pre-input pass is skipped as unsafe.</para>
/// <para>Failures are counted (CaptureFailures) and never propagate into the input path or event loop.</para>
/// </remarks>
internal sealed class ProxyBoundaryTracker
{
    private readonly WorkbenchProxyDriver driver;
    private TProgram? application;
    private long recording; // Recorder.RecordingId this state belongs to (0 = none yet); written on the UI thread only
    private bool started, sawPause;
    private long lastDelivered, observedGeneration, nextId;
    private TView? observedTop;
    private int observedDepth;
    private ulong markHash; private TView? markTop; private int markDepth;
    private ulong? retainedHash;
    private Candidate? candidate;
    private long emitted, suppressed, skippedUnsafe, captureFailures, refused;
    private long lastState; // Id of the last emitted state boundary (a pick's anchor)
    private PendingPick? pendingPick;
    private long picks, picksRefused;

    // State frozen at a gesture's press (same UI pass), emitted as a pick boundary at its release.
    private sealed record PendingPick(long Recording, long Anchor, long Sequence, long Delivered, long Milliseconds, long Generation,
        ulong Hash, int Width, int Height, string? ModalTop, int MenuDepth, ScreenSnapshot? Cells, CheckpointInspection? Inspection,
        string? HitPath);

    private sealed record Candidate(long BoundaryId, long AfterSequence, long Delivered, long Generation, ulong Hash,
        long Milliseconds, ScreenSnapshot Cells, CheckpointInspection Inspection);

    internal ProxyBoundaryTracker(WorkbenchProxyDriver driver) => this.driver = driver;

    /// <summary>Diagnostics/tests: every boundary the recorder accepted, on the UI thread, after it was added.</summary>
    internal Action<RecordedBoundary>? Emitted;

    /// <summary>Any thread: counters of recording <paramref name="recordingId"/>; zeros when the tracker has not observed that
    /// recording yet (e.g. a RecordStart reply composed before the first boundary of the new recording).</summary>
    internal (long Emitted, long Suppressed, long SkippedUnsafe, long CaptureFailures, long Refused) Counters(long recordingId)
    {
        if (Interlocked.Read(ref recording) != recordingId) return default;
        var result = (Interlocked.Read(ref emitted), Interlocked.Read(ref suppressed), Interlocked.Read(ref skippedUnsafe),
            Interlocked.Read(ref captureFailures), Interlocked.Read(ref refused));
        return Interlocked.Read(ref recording) == recordingId ? result : default;
    }

    /// <summary>UI thread, from IEventLoopObserver.OnIdleBoundary (before the session host serves a queued command).</summary>
    internal void OnIdle(TProgram application)
    {
        this.application = application;
        Observe(application, Protocol.BoundaryKinds.Idle);
    }

    /// <summary>UI thread, from the proxy's ReadKeyEvent right before a forwarded input is handed out.</summary>
    internal void BeforeInput()
    {
        if (application is not { } app)
        {
            var c = driver.Recorder.Cursor();
            if (c.Active && !c.Paused) Interlocked.Increment(ref skippedUnsafe);
            return;
        }
        Observe(app, Protocol.BoundaryKinds.PreInput);
    }

    private void Observe(TProgram app, string kind)
    {
        try
        {
            var c = driver.Recorder.Cursor();
            if (!c.Active) return;
            if (c.RecordingId != recording) Begin(c.RecordingId);
            if (c.Paused) { sawPause = true; return; }
            if (kind == Protocol.BoundaryKinds.PreInput && started && c.Delivered == lastDelivered
                && driver.ScreenGeneration == observedGeneration) return;
            if (!TEventQueue.IsUiThread || !ReferenceEquals(TDisplay.driver, driver) || app.lockFlag != 0
                || TScreen.ScreenBuffer is null || !ReferenceEquals(app.buffer, TScreen.ScreenBuffer))
            { Interlocked.Increment(ref skippedUnsafe); return; }
            Pass(app, kind, c.Sequence, c.Delivered, c.Milliseconds);
        }
        catch (Exception) { Interlocked.Increment(ref captureFailures); }
    }

    private void Begin(long id)
    {
        driver.Snapshots.Reset();
        started = sawPause = false; lastDelivered = observedGeneration = nextId = 0;
        observedTop = markTop = null; observedDepth = markDepth = 0; markHash = 0; retainedHash = null; candidate = null;
        Interlocked.Exchange(ref emitted, 0); Interlocked.Exchange(ref suppressed, 0); Interlocked.Exchange(ref skippedUnsafe, 0);
        Interlocked.Exchange(ref captureFailures, 0); Interlocked.Exchange(ref refused, 0);
        lastState = 0; pendingPick = null; Interlocked.Exchange(ref picks, 0); Interlocked.Exchange(ref picksRefused, 0);
        Interlocked.Exchange(ref recording, id);
    }

    private void Pass(TProgram app, string kind, long sequence, long delivered, long milliseconds)
    {
        TView? top = app.TopView();
        int depth = MenuDepth(top);
        long generation = driver.ScreenGeneration;
        bool signatureChanged = !started || !ReferenceEquals(top, observedTop) || depth != observedDepth;
        bool wasTransient = started && IsTransient(app, observedTop, observedDepth);
        int priorDepth = observedDepth;
        string cause = !started ? Protocol.BoundaryCauses.Start
            : delivered != lastDelivered ? Protocol.BoundaryCauses.Input : Protocol.BoundaryCauses.Application;
        bool changed = signatureChanged || generation != observedGeneration;
        observedGeneration = generation; observedTop = top; observedDepth = depth; lastDelivered = delivered;
        if (!changed) return;

        // The transient that just ended/changed: keep its last observed state if it differs from what was retained.
        if (signatureChanged && candidate is { } last)
        {
            candidate = null;
            if (last.Hash != retainedHash && driver.Snapshots.TryAdd(last.BoundaryId, Protocol.SnapshotReasons.TransientLast,
                last.AfterSequence, last.Delivered, last.Generation, last.Hash, last.Milliseconds, last.Cells, last.Inspection) is not null)
                retainedHash = last.Hash;
        }

        ScreenSnapshot root;
        ulong hash;
        try { root = ScreenSnapshot.CopyRoot(); hash = ProxyScreenHash.Compute(root); }
        catch (Exception) { Interlocked.Increment(ref captureFailures); return; }
        if (started && hash == markHash && ReferenceEquals(top, markTop) && depth == markDepth)
        { Interlocked.Increment(ref suppressed); return; }

        long id = ++nextId;
        bool transient = IsTransient(app, top, depth);
        long? snapshotId = null; bool dropped = false;
        if (transient)
        {
            CheckpointInspection? inspection = null;
            try { inspection = Inspectors.Capture(app, root, Protocol.EvidenceKinds.All); }
            catch (Exception) { Interlocked.Increment(ref captureFailures); }
            if (inspection is not null && (signatureChanged || !wasTransient))
            {
                string reason = depth > 0 ? (priorDepth == 0 || !wasTransient ? Protocol.SnapshotReasons.MenuOpen : Protocol.SnapshotReasons.MenuChange)
                    : !wasTransient ? Protocol.SnapshotReasons.ModalOpen : Protocol.SnapshotReasons.ModalChange;
                snapshotId = driver.Snapshots.TryAdd(id, reason, sequence, delivered, generation, hash, milliseconds, root, inspection);
                dropped = snapshotId is null;
                if (!dropped) retainedHash = hash;
            }
            else if (inspection is not null)
                candidate = new(id, sequence, delivered, generation, hash, milliseconds, root, inspection);
        }
        var boundary = new RecordedBoundary(id, kind, cause, sequence, delivered, generation, hash, milliseconds, root.Width, root.Height,
            top is null || ReferenceEquals(top, app) ? null : top.GetType().FullName ?? top.GetType().Name, depth, sawPause, snapshotId, dropped);
        started = true; markHash = hash; markTop = top; markDepth = depth;
        if (driver.Recorder.AddBoundary(boundary, recording))
        {
            sawPause = false; lastState = id;
            Interlocked.Increment(ref emitted);
            Emitted?.Invoke(boundary);
        }
        else Interlocked.Increment(ref refused);
    }

    // ── pick anchors ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Any thread: picks emitted and picks refused/dropped at emission (no capture, paused, stopped, new recording)
    /// of recording <paramref name="recordingId"/>.</summary>
    internal (long Picks, long Refused) PickCounters(long recordingId) =>
        Interlocked.Read(ref recording) == recordingId ? (Interlocked.Read(ref picks), Interlocked.Read(ref picksRefused)) : default;

    /// <summary>UI thread, at the press that began a gesture (after this event's pre-input boundary): freezes cells,
    /// inspection, live hit path and the causal anchor. Never mutates the application; failures leave a pick without cells.</summary>
    internal void BeginPick(int x, int y)
    {
        pendingPick = null;
        try
        {
            var c = driver.Recorder.Cursor();
            if (!c.Active || c.Paused) return;
            if (c.RecordingId != recording) Begin(c.RecordingId);
            ScreenSnapshot? cells = null; CheckpointInspection? inspection = null; string? hit = null;
            ulong hash = markHash; string? top = null; int depth = 0;
            if (application is { } app && TEventQueue.IsUiThread && ReferenceEquals(TDisplay.driver, driver) && app.lockFlag == 0
                && TScreen.ScreenBuffer is not null && ReferenceEquals(app.buffer, TScreen.ScreenBuffer))
            {
                try
                {
                    TView? view = app.TopView();
                    depth = MenuDepth(view);
                    top = view is null || ReferenceEquals(view, app) ? null : view.GetType().FullName ?? view.GetType().Name;
                    cells = ScreenSnapshot.CopyRoot(); hash = ProxyScreenHash.Compute(cells);
                    inspection = Inspectors.Capture(app, cells, Protocol.EvidenceKinds.All);
                    hit = ProxyPickHitTest.PathAt(app, x, y);
                }
                catch (Exception) { Interlocked.Increment(ref captureFailures); cells = null; inspection = null; }
            }
            else Interlocked.Increment(ref skippedUnsafe);
            pendingPick = new(recording, lastState, c.Sequence, c.Delivered, c.Milliseconds, driver.ScreenGeneration, hash,
                cells?.Width ?? TScreen.ScreenWidth, cells?.Height ?? TScreen.ScreenHeight, top, depth, cells, inspection, hit);
        }
        catch (Exception) { Interlocked.Increment(ref captureFailures); }
    }

    /// <summary>UI thread, at the swallowed release of a completed gesture: next boundary Id, the frozen snapshot (reason
    /// <c>pick</c>; refused → SnapshotDropped) and the pick boundary. Refused (counted) when no press was frozen or the
    /// recording is paused, stopped or replaced.</summary>
    internal void CompletePick(GestureOutcome outcome)
    {
        var pending = pendingPick; pendingPick = null;
        try
        {
            var c = driver.Recorder.Cursor();
            if (pending is null || !c.Active || c.Paused || c.RecordingId != pending.Recording || pending.Recording != recording)
            { Interlocked.Increment(ref picksRefused); return; }
            long id = ++nextId;
            long? snapshotId = pending is { Cells: { } cells, Inspection: { } inspection }
                ? driver.Snapshots.TryAdd(id, Protocol.SnapshotReasons.Pick, pending.Sequence, pending.Delivered, pending.Generation,
                    pending.Hash, pending.Milliseconds, cells, inspection) : null;
            var boundary = new RecordedBoundary(id, Protocol.BoundaryKinds.Pick, Protocol.BoundaryCauses.Pick, pending.Sequence,
                pending.Delivered, pending.Generation, pending.Hash, pending.Milliseconds, pending.Width, pending.Height,
                pending.ModalTop, pending.MenuDepth, false, snapshotId, snapshotId is null)
            {
                Pick = new(outcome.Kind, outcome.Trigger, outcome.X, outcome.Y, outcome.EndX, outcome.EndY, outcome.Region,
                    pending.Anchor, pending.HitPath, outcome.Swallowed),
            };
            if (driver.Recorder.AddBoundary(boundary, recording)) { Interlocked.Increment(ref picks); Emitted?.Invoke(boundary); }
            else Interlocked.Increment(ref picksRefused);
        }
        catch (Exception) { Interlocked.Increment(ref captureFailures); Interlocked.Increment(ref picksRefused); }
    }

    /// <summary>UI thread: the gesture ended without a pick; the frozen state is discarded.</summary>
    internal void AbortPick() => pendingPick = null;

    private static bool IsTransient(TProgram app, TView? top, int depth) => depth > 0 || (top is not null && !ReferenceEquals(top, app));

    // Executing menu levels: the modal top menu and its ParentMenu chain (bar included when it executes), as MenuInspector.
    private static int MenuDepth(TView? top)
    {
        int depth = 0;
        for (TMenuView? m = top as TMenuView; m is not null && depth < 64; m = m.ParentMenu) depth++;
        return depth;
    }
}
