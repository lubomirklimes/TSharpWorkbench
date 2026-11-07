using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

/// <summary>Where a recorder batch reads the capture state it does not own. Read OUTSIDE the recorder lock.</summary>
/// <param name="Tracker">Boundary-tracker counters (suppressed duplicate marks, capture failures, unsafe skips); null = none.</param>
/// <param name="Picks">pick counters (emitted, aborted or refused, events swallowed, double clicks cleared); null = none.</param>
internal readonly record struct CaptureSources(ProxyRetainedSnapshots? Snapshots,
    Func<(long Suppressed, long CaptureFailures, long SkippedUnsafe)>? Tracker = null,
    Func<(long Picks, long Aborted, long Swallowed, long DoubleClicksCleared)>? Picks = null);

/// <summary>
/// Physical-input recorder of one Proxy session. Written on the UI thread (the decorator's input path),
/// read by the session's transport thread (RecordPoll); everything shared is guarded by <see cref="gate"/>.
/// It never creates, alters or injects input: it copies what the real backend delivered to the application.
/// </summary>
/// <remarks>Origins: <c>backend-key</c> = returned by the real backend's ReadKeyEvent; <c>backend-queue</c> = appended to the
/// framework queue while the real backend's PumpMessages/WaitForEvent ran on the UI thread. Queue events that existed before
/// the backend call are <c>other-queue</c> (application enqueues) and are forwarded but never recorded. Key-ups, standalone
/// modifier transitions, hover moves (no button held) and mouse auto-repeat are delivered but not recorded (counted).
/// The recorder also holds the boundary timeline (bounded, ordered by Id; a boundary with AfterSequence s sits after
/// event s and before s+1) and mirrors it to <see cref="RecordingBoundaryJournal"/> through a background writer.</remarks>
internal sealed class ProxyInputRecorder
{
    internal const string BackendKey = "backend-key", BackendQueue = "backend-queue", OtherQueue = "other-queue";
    internal const int MaximumEvents = 10000, MaximumBatch = 512, MaximumBoundaries = 10000;
    internal static readonly string[] Kinds = ["key", "text", "mouse", "resize"];

    private readonly object gate = new();
    private readonly List<RecordedInput> events = [];
    private readonly Stopwatch clock = new();
    private readonly string backend;
    private volatile bool active;
    private bool paused, gap;
    private long sequence, dropped, unrecorded, other, refused;
    private int width, height;
    private StreamWriter? journal;

    internal ProxyInputRecorder(string backend) => this.backend = backend;

    /// <summary>Volatile view for the UI thread hot path.</summary>
    internal bool Active => active;

    internal void Start(int cols, int rows, string? journalDirectory)
    {
        lock (gate)
        {
            if (active) throw new InvalidOperationException("A recording is already active.");
            events.Clear(); sequence = dropped = unrecorded = other = refused = 0; paused = gap = false;
            boundaries.Clear(); delivered = boundariesDropped = lastBoundaryId = 0; boundaryGap = false;
            recordingId++;
            width = cols; height = rows; active = true; clock.Restart();
            journal?.Dispose(); journal = null;
            // The boundary journal opens its file on its own background task (no file I/O added to this thread).
            var previous = boundaryJournal?.Completion;
            boundaryJournal = journalDirectory is { Length: > 0 }
                ? new BoundaryJournalWriter(Path.Combine(journalDirectory, RecordingBoundaryJournal.FileName), previous) : null;
            // Pre-existing: the input journal is opened and written synchronously on the UI thread.
            if (journalDirectory is { Length: > 0 } && Directory.Exists(journalDirectory))
            {
                try { journal = new StreamWriter(Path.Combine(journalDirectory, RecordingJournal.FileName), append: false) { AutoFlush = true }; }
                catch (IOException) { journal = null; }
                catch (UnauthorizedAccessException) { journal = null; }
            }
        }
    }

    internal void Stop()
    {
        lock (gate)
        {
            if (active) boundaryJournal?.Complete();
            active = false; paused = false; journal?.Dispose(); journal = null;
        }
    }

    internal void SetPaused(bool value)
    {
        lock (gate)
        {
            if (!active) throw new InvalidOperationException("No recording is active.");
            if (paused && !value) { gap = true; boundaryGap = true; boundaryJournal?.Post(new(RecordingBoundaryJournal.Resume)); }
            if (!paused && value) boundaryJournal?.Post(new(RecordingBoundaryJournal.Pause));
            paused = value;
        }
    }

    // ── boundary timeline ──────────────────────────────────────────────────────────────────────────────────────────
    private readonly List<RecordedBoundary> boundaries = [];
    private long delivered, recordingId, boundariesDropped, lastBoundaryId;
    private bool boundaryGap;
    private BoundaryJournalWriter? boundaryJournal;

    /// <summary>Incremented by every <see cref="Start"/>; lets UI-thread observers detect a new recording.</summary>
    internal long RecordingId { get { lock (gate) return recordingId; } }

    /// <summary>Test seam: the boundary journal writer of the current/last recording.</summary>
    internal BoundaryJournalWriter? BoundaryJournal { get { lock (gate) return boundaryJournal; } }

    /// <summary>Consistent cursor for a boundary: last recorded sequence, physical inputs delivered (recorded or not),
    /// recording clock, pause state and recording id - read together under the recorder lock.</summary>
    internal (long Sequence, long Delivered, long Milliseconds, bool Active, bool Paused, long RecordingId) Cursor()
    {
        lock (gate) return (sequence, delivered, clock.ElapsedMilliseconds, active, paused, recordingId);
    }

    /// <summary>UI thread: append one boundary of the recording <paramref name="recording"/>; false when it is not the
    /// active, unpaused recording (not counted) or when it is refused - at the bound or with a non-increasing Id (counted
    /// as BoundariesDropped, journaled as a capture loss). The first stored boundary after a resume gets AfterGap.
    /// Never waits on I/O (the journal line is queued to a bounded background writer).</summary>
    internal bool AddBoundary(RecordedBoundary boundary, long recording)
    {
        lock (gate)
        {
            if (!active || paused || recording != recordingId) return false;
            if (boundaries.Count >= MaximumBoundaries || boundary.Id <= lastBoundaryId)
            {
                boundariesDropped++; boundaryJournal?.CaptureLost();
                return false;
            }
            // A pick anchor is not a state boundary; the gap belongs to the next state boundary.
            bool pick = boundary.Kind == Protocol.BoundaryKinds.Pick;
            if (boundaryGap && !boundary.AfterGap && !pick) boundary = boundary with { AfterGap = true };
            if (!pick) boundaryGap = false;
            boundaries.Add(boundary); lastBoundaryId = boundary.Id;
            boundaryJournal?.Post(new(RecordingBoundaryJournal.Boundary, boundary));
            return true;
        }
    }

    internal void RefusedInjection() { lock (gate) refused++; }
    internal void Other() { lock (gate) if (active) other++; }

    /// <summary>UI thread: one event the application is about to receive, with where the proxy observed it.</summary>
    internal void Deliver(in TEvent ev, string origin, int cols, int rows)
    {
        lock (gate)
        {
            if (!active) return;
            if (origin == OtherQueue) { other++; return; }
            delivered++;
            string? kind = ev.What switch
            {
                Events.evKeyDown => "key",
                Events.evMouseDown or Events.evMouseUp or Events.evMouseWheel => "mouse",
                Events.evMouseMove when ev.mouse.buttons != 0 => "mouse",
                Events.evCommand when ev.message.command == Views.cmScreenResized => "resize",
                _ => null,
            };
            if (kind is null || paused)
            {
                if (paused) gap = true;
                if (ev.What is Events.evKeyDown or Events.evKeyUp or Events.evModifierChanged or Events.evMouseDown or Events.evMouseUp
                    or Events.evMouseMove or Events.evMouseAuto or Events.evMouseWheel) unrecorded++;
                return;
            }
            if (events.Count >= MaximumEvents) { dropped++; return; }
            bool mouse = kind == "mouse";
            var record = new RecordedInput(++sequence, origin, clock.ElapsedMilliseconds, kind, ev.What,
                kind == "key" ? ev.keyDown.keyCode : (ushort)0, kind == "key" && !string.IsNullOrEmpty(ev.keyDown.text) ? ev.keyDown.text : null,
                kind == "key" ? ev.keyDown.controlKeyState : mouse ? ev.mouse.controlKeyState : 0,
                mouse ? ev.mouse.where.x : 0, mouse ? ev.mouse.where.y : 0, mouse ? ev.mouse.buttons : (byte)0,
                mouse ? ev.mouse.eventFlags : 0, cols, rows, gap);
            gap = false;
            width = cols; height = rows;
            events.Add(record);
            if (journal is not null)
            {
                try { journal.WriteLine(JsonSerializer.Serialize(record)); }
                catch (IOException) { journal.Dispose(); journal = null; }
            }
        }
    }

    /// <summary>Any thread: events after <paramref name="after"/> (sequence), at most <see cref="MaximumBatch"/>. With
    /// <paramref name="marksAfter"/> also the boundaries after that id (at most one batch), the snapshot store's latest id and
    /// the coverage counters; without it (an older controller) the legacy payload: no boundaries, no coverage.</summary>
    internal RecordingBatch Poll(long after, string? snapshot = null, long? marksAfter = null, CaptureSources sources = default)
    {
        RecordingBatch batch;
        (long Stored, long Dropped, long Journal) own;
        lock (gate)
        {
            var list = events.Where(e => e.Sequence > after).Take(MaximumBatch).ToArray();
            long next = list.Length == 0 ? Math.Max(after, 0) : list[^1].Sequence;
            batch = new(list, next, active, paused, dropped, unrecorded, other, refused, width, height, backend, snapshot);
            if (marksAfter is not { } marks) return batch;
            int first = FirstAfter(marks);
            var marked = boundaries.GetRange(first, Math.Min(MaximumBatch, boundaries.Count - first)).ToArray();
            batch = batch with { Boundaries = marked, BoundaryNext = marked.Length == 0 ? Math.Max(marks, 0) : marked[^1].Id };
            own = OwnCounters();
        }
        // Outside the recorder lock (the store and tracker have their own); read after the boundaries, so SnapshotLatest
        // covers every SnapshotId a returned boundary names.
        return batch with { SnapshotLatest = sources.Snapshots?.Latest ?? 0, Coverage = Compose(own, sources) };
    }

    /// <summary>Any thread: the coverage counters of the current/last recording.</summary>
    internal CaptureCoverage Coverage(CaptureSources sources = default)
    {
        (long, long, long) own;
        lock (gate) own = OwnCounters();
        return Compose(own, sources);
    }

    private (long, long, long) OwnCounters() => (boundaries.Count, boundariesDropped, boundaryJournal?.Dropped ?? 0);

    // Recorder (stored/dropped boundaries, journal loss) + snapshot store + tracker (suppressed, failures, unsafe skips).
    private static CaptureCoverage Compose((long Stored, long Dropped, long Journal) own, CaptureSources sources)
    {
        var s = sources.Snapshots?.Counters() ?? default;
        var t = sources.Tracker?.Invoke() ?? default;
        var p = sources.Picks?.Invoke() ?? default;
        return new(own.Stored, own.Dropped, t.Suppressed, s.Added, s.Dropped, s.Released, s.Retained, s.Bytes, t.CaptureFailures,
            t.SkippedUnsafe)
        { JournalDropped = own.Journal, Picks = p.Picks, PicksAborted = p.Aborted, GestureEventsSwallowed = p.Swallowed, DoubleClicksCleared = p.DoubleClicksCleared };
    }

    // boundaries are strictly increasing by Id: first index with Id > marks.
    private int FirstAfter(long marks)
    {
        int lo = 0, hi = boundaries.Count;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (boundaries[mid].Id <= marks) lo = mid + 1; else hi = mid; }
        return lo;
    }
}

/// <summary>Background writer of <see cref="RecordingBoundaryJournal"/>. Callers (UI thread, transport thread) only
/// enqueue into a bounded channel and never wait; when it is full the line is lost and counted, and the writer emits an
/// explicit <c>loss</c> line before the next line it writes. The file is opened, written and flushed only on the writer task
/// (flushed after every drained burst, so a target crash loses at most what was still queued). Snapshot payloads are never
/// written (privacy); boundaries carry only snapshot ids.</summary>
internal sealed class BoundaryJournalWriter
{
    internal const int Capacity = 1024;
    private readonly Channel<BoundaryJournalLine> queue = Channel.CreateBounded<BoundaryJournalLine>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long captureLost, journalLost, dropped, written, unflushed; // unflushed: writer task only
    private volatile bool closed;

    internal BoundaryJournalWriter(string path, Task? previous) => Completion = Task.Run(() => RunAsync(path, previous));

    /// <summary>Completes after the stop line was written and the file closed (or the writer failed).</summary>
    internal Task Completion { get; }
    /// <summary>Lines lost by the journal itself (queue full, file unavailable or failed).</summary>
    internal long Dropped => Interlocked.Read(ref dropped);
    /// <summary>Boundary/pause/resume lines written and flushed to the OS (test seam).</summary>
    internal long Written => Interlocked.Read(ref written);
    /// <summary>Managed thread of the last write (test seam: never the UI thread).</summary>
    internal int WriterThread { get; private set; }

    internal void Post(BoundaryJournalLine line)
    {
        if (closed) return;
        if (!queue.Writer.TryWrite(line)) { Interlocked.Increment(ref journalLost); Interlocked.Increment(ref dropped); }
    }

    internal void CaptureLost() => Interlocked.Increment(ref captureLost);

    /// <summary>Clean end: the writer drains what is queued, writes pending loss lines and a <c>stop</c> line, and closes.</summary>
    internal void Complete() { closed = true; queue.Writer.TryComplete(); }

    private async Task RunAsync(string path, Task? previous)
    {
        if (previous is not null) { try { await previous.ConfigureAwait(false); } catch (Exception) { } }
        StreamWriter? file = null;
        try
        {
            file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            Write(ref file, new(RecordingBoundaryJournal.Header, Detail: RecordingBoundaryJournal.Format), count: false);
            file?.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { file?.Dispose(); file = null; }
        var reader = queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line)) { Losses(ref file); Write(ref file, line); }
                Flush(ref file);
            }
            Losses(ref file);
            Write(ref file, new(RecordingBoundaryJournal.Stop), count: false);
            Flush(ref file);
        }
        finally { file?.Dispose(); }
    }

    private void Losses(ref StreamWriter? file)
    {
        if (Interlocked.Exchange(ref captureLost, 0) is > 0 and var c)
            Write(ref file, new(RecordingBoundaryJournal.Loss, Lost: c, Detail: RecordingBoundaryJournal.LossCapture), count: false);
        if (Interlocked.Exchange(ref journalLost, 0) is > 0 and var j)
            Write(ref file, new(RecordingBoundaryJournal.Loss, Lost: j, Detail: RecordingBoundaryJournal.LossJournal), count: false);
    }

    private void Write(ref StreamWriter? file, BoundaryJournalLine line, bool count = true)
    {
        if (file is null) { if (count) Interlocked.Increment(ref dropped); return; }
        try
        {
            file.WriteLine(JsonSerializer.Serialize(line));
            WriterThread = Environment.CurrentManagedThreadId;
            if (count) unflushed++;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { file.Dispose(); file = null; if (count) Interlocked.Increment(ref dropped); }
    }

    private void Flush(ref StreamWriter? file)
    {
        try { file?.Flush(); Interlocked.Add(ref written, unflushed); }
        catch (IOException) { file?.Dispose(); file = null; Interlocked.Add(ref dropped, unflushed); }
        unflushed = 0;
    }
}
