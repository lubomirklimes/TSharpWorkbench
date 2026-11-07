using System.Text.Json;
using TSharpWorkbench.Testing;

// Workbench-side capture timeline of one recording session. Holds what the target's Proxy advertised and
// delivered beyond raw input: causal UI boundaries, retained transient snapshots and loss/coverage counters. Read-only API for
// (proposals/review); nothing here proposes checkpoints, touches UI or the scenario schema.

/// <summary>One entry of <see cref="RecordingCaptureTimeline.Ordered"/>: exactly one of Input/Boundary is set.</summary>
internal sealed record CaptureTimelineEntry(RecordedInput? Input, RecordedBoundary? Boundary);

/// <summary>
/// Bounded, thread-safe capture timeline. Written by the session's poll task (never the UI thread), read by anyone.
/// Boundaries are deduplicated by Id and kept ordered; snapshots are kept until <see cref="Clear"/> (Cancel/Dispose) - a
/// stopped recording keeps them for review. Every refusal or loss is counted; <see cref="CoverageComplete"/> is false
/// after any discard anywhere (target, Workbench store, journal recovery).
/// </summary>
internal sealed class RecordingCaptureTimeline
{
    internal const int MaximumBoundaries = 10000, MaximumSnapshots = 256, SnapshotBatchesPerPoll = 4;
    internal const long MaximumSnapshotBytes = 64L * 1024 * 1024;
    /// <summary>Live inspection samples kept (count and serialized bytes); optional evidence, outside the snapshot budget.</summary>
    internal const int MaximumInspections = 64;
    internal const long MaximumInspectionBytes = 16L * 1024 * 1024;

    private readonly object gate = new();
    private readonly List<RecordedBoundary> boundaries = [];
    private readonly List<RetainedSnapshot> snapshots = [];
    private string[] features = [];
    private long boundaryCursor, snapshotCursor, snapshotLatest, snapshotBytes, boundariesLost, snapshotsLost, recoveryLost;
    private CaptureCoverage? coverage;
    private bool recovered, recoveryIncomplete;
    private bool picks;
    private readonly List<RecordInspection> inspections = [];
    private long inspectionBytes, inspectionsLost;
    private string? pickState;

    /// <summary>Features the target advertised and this Workbench uses (subset of <see cref="Protocol.RecordFeatures.All"/>).</summary>
    internal IReadOnlyList<string> Features { get { lock (gate) return features; } }
    internal bool BoundariesEnabled => Has(Protocol.RecordFeatures.Boundaries);
    internal bool SnapshotsEnabled => Has(Protocol.RecordFeatures.Snapshots);
    internal bool InspectEnabled => Has(Protocol.RecordFeatures.Inspect);
    /// <summary>The target advertised <see cref="Protocol.RecordFeatures.Picks"/> (optional; never part of coverage).</summary>
    internal bool PicksEnabled { get { lock (gate) return picks; } }
    /// <summary>Last pick state the target reported (<see cref="Protocol.PickStates"/>), null when unknown.</summary>
    internal string? PickState { get { lock (gate) return pickState; } }
    /// <summary>Live RecordInspect samples (bounded; optional evidence for the detector).</summary>
    internal IReadOnlyList<RecordInspection> Inspections { get { lock (gate) return inspections.ToArray(); } }
    internal long InspectionsLost { get { lock (gate) return inspectionsLost; } }
    /// <summary>Why capture is missing or partial (null when all features are available).</summary>
    internal string? Diagnostic { get; private set; }

    internal long BoundaryCursor { get { lock (gate) return boundaryCursor; } }
    internal long SnapshotCursor { get { lock (gate) return snapshotCursor; } }
    internal long SnapshotLatest { get { lock (gate) return snapshotLatest; } }
    /// <summary>A target-held snapshot is newer than what was fetched.</summary>
    internal bool SnapshotsPending { get { lock (gate) return SnapshotsEnabled && snapshotLatest > snapshotCursor; } }
    internal IReadOnlyList<RecordedBoundary> Boundaries { get { lock (gate) return boundaries.ToArray(); } }
    internal IReadOnlyList<RetainedSnapshot> Snapshots { get { lock (gate) return snapshots.ToArray(); } }
    /// <summary>Last coverage counters reported by the target (null: legacy target or nothing polled yet).</summary>
    internal CaptureCoverage? Coverage { get { lock (gate) return coverage; } }
    internal long BoundariesLost { get { lock (gate) return boundariesLost; } }
    internal long SnapshotsLost { get { lock (gate) return snapshotsLost; } }
    internal long SnapshotBytes { get { lock (gate) return snapshotBytes; } }
    /// <summary>Boundaries were merged from the crash journal after the target ended.</summary>
    internal bool Recovered { get { lock (gate) return recovered; } }

    /// <summary>True only when all features were negotiated, the target reported complete coverage, every announced snapshot
    /// was fetched and nothing was lost in Workbench or in crash recovery. Never true for a legacy or partial target.</summary>
    internal bool CoverageComplete
    {
        get
        {
            lock (gate) return features.Length == Protocol.RecordFeatures.All.Length && coverage is { Complete: true }
                && boundariesLost == 0 && snapshotsLost == 0 && recoveryLost == 0 && !recoveryIncomplete
                && snapshotLatest <= snapshotCursor; // an announced snapshot not yet fetched is not covered
        }
    }

    private bool Has(string feature) { lock (gate) return features.Contains(feature); }

    /// <summary>Capability negotiation from the handshake's RecordFeatures: unknown names are ignored; empty = legacy.</summary>
    internal void Negotiate(string[]? advertised)
    {
        var known = Protocol.RecordFeatures.All.Where(f => advertised?.Contains(f) == true).ToArray();
        lock (gate) { features = known; picks = known.Length > 0 && advertised?.Contains(Protocol.RecordFeatures.Picks) == true; }
        var missing = Protocol.RecordFeatures.All.Except(known).ToArray();
        Diagnostic = known.Length == 0
            ? "The target's Workbench Proxy predates Smart Recorder capture (no UI boundaries, transient snapshots or live "
              + "inspection); input recording works as before. Re-prepare the target with Settings > Target Diagnostics... "
              + "(Fix Issues) or Prepare target to enable it."
            : missing.Length == 0 ? null
            : "The target's Workbench Proxy supports only part of Smart Recorder capture (" + string.Join(", ", known)
              + "; missing " + string.Join(", ", missing) + "); only the advertised parts are used. Re-prepare the target "
              + "with Fix Issues or Prepare target for full capture.";
    }

    /// <summary>The poll's MarksAfter: the opt-in, sent whenever any capture feature was negotiated (null = legacy poll).</summary>
    internal long? MarksAfter { get { lock (gate) return features.Length == 0 ? null : boundaryCursor; } }

    /// <summary>Merges one recorder batch: boundaries (dedupe by Id, ordered; bounded), cursors and coverage.</summary>
    internal void Merge(RecordingBatch? batch)
    {
        if (batch is null) return;
        lock (gate)
        {
            if (features.Length == 0) return;
            if (BoundariesEnabled) foreach (var b in batch.Boundaries) AddBoundary(b);
            boundaryCursor = Math.Max(boundaryCursor, batch.BoundaryNext);
            snapshotLatest = Math.Max(snapshotLatest, batch.SnapshotLatest);
            if (batch.Coverage is not null) coverage = batch.Coverage;
            if (picks && batch.PickState is not null) pickState = batch.PickState;
        }
    }

    /// <summary>Stores one live inspection sample (bounded by count and serialized bytes; refusals counted, never
    /// evicting). Samples are optional evidence: a lost sample never affects <see cref="CoverageComplete"/>.</summary>
    internal bool AddInspection(RecordInspection? sample)
    {
        if (sample is null) return false;
        long bytes = JsonSerializer.SerializeToUtf8Bytes(sample).LongLength;
        lock (gate)
        {
            if (features.Length == 0) return false;
            if (inspections.Count >= MaximumInspections || inspectionBytes + bytes > MaximumInspectionBytes) { inspectionsLost++; return false; }
            inspections.Add(sample); inspectionBytes += bytes;
            return true;
        }
    }

    private void AddBoundary(RecordedBoundary b)
    {
        int index = Find(b.Id);
        if (index >= 0) return; // duplicate
        if (boundaries.Count >= MaximumBoundaries) { boundariesLost++; return; }
        boundaries.Insert(~index, b);
    }

    private int Find(long id)
    {
        if (boundaries.Count == 0 || boundaries[^1].Id < id) return ~boundaries.Count;
        int lo = 0, hi = boundaries.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (boundaries[mid].Id == id) return mid;
            if (boundaries[mid].Id < id) lo = mid + 1; else hi = mid - 1;
        }
        return ~lo;
    }

    /// <summary>Stores one fetched snapshot batch (bounded by count and serialized bytes; refusals counted, never evicting
    /// what is held). Returns whether the cursor advanced.</summary>
    internal bool AddSnapshots(SnapshotBatch? batch)
    {
        if (batch is null || !SnapshotsEnabled) return false;
        var sized = batch.Snapshots.Select(s => (Snapshot: s, Bytes: JsonSerializer.SerializeToUtf8Bytes(s).LongLength)).ToArray();
        lock (gate)
        {
            foreach (var (s, bytes) in sized)
            {
                if (s.Id <= snapshotCursor || snapshots.Any(h => h.Id == s.Id)) continue;
                if (snapshots.Count >= MaximumSnapshots || snapshotBytes + bytes > MaximumSnapshotBytes) { snapshotsLost++; continue; }
                snapshots.Add(s); snapshotBytes += bytes;
            }
            bool advanced = batch.Next > snapshotCursor;
            snapshotCursor = Math.Max(snapshotCursor, batch.Next);
            snapshotLatest = batch.Latest > 0 ? Math.Max(batch.Latest, snapshotCursor) : snapshotCursor;
            coverage = batch.Coverage;
            return advanced;
        }
    }

    /// <summary>After a target crash: merges journaled boundaries (dedupe by Id). Snapshot payloads are never persisted, so
    /// every journaled boundary whose snapshot was not fetched counts as a lost snapshot. A journal without a clean stop
    /// marks coverage incomplete (its queued tail may be lost).</summary>
    internal void Recover(BoundaryJournalContent journal)
    {
        if (!BoundariesEnabled) return;
        lock (gate)
        {
            int before = boundaries.Count;
            foreach (var b in journal.Boundaries) AddBoundary(b);
            recovered = boundaries.Count > before;
            recoveryLost += journal.CaptureLost + journal.JournalLost;
            recoveryIncomplete = !journal.Clean;
            var held = snapshots.Select(s => s.Id).ToHashSet();
            snapshotsLost += boundaries.Count(b => b.SnapshotId is { } id && id > snapshotCursor && !held.Contains(id));
            if (boundaries.Count > 0) boundaryCursor = Math.Max(boundaryCursor, boundaries[^1].Id);
        }
    }

    /// <summary>Discard (Cancel/Dispose): drops every held snapshot payload and boundary now (privacy).</summary>
    internal void Clear()
    {
        lock (gate) { snapshots.Clear(); snapshotBytes = 0; boundaries.Clear(); inspections.Clear(); inspectionBytes = 0; }
    }

    // ── pure helpers for Smart Recording ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Index of the converter step a boundary follows: the last step whose First &lt;= AfterSequence (it contains
    /// AfterSequence, or precedes it); -1 when the boundary precedes every step (AfterSequence 0 or before the first step).
    /// A boundary with First &lt; AfterSequence &lt; Last sits inside a coalesced step (text run, wheel, click).</summary>
    internal static int StepIndexAfter(RecordedBoundary boundary, IReadOnlyList<RecordedStep> steps)
    {
        int result = -1;
        for (int i = 0; i < steps.Count; i++) if (steps[i].First <= boundary.AfterSequence && steps[i].First > 0) result = i;
        return result;
    }

    /// <summary>Events and boundaries in causal order: a boundary with AfterSequence s sits after event s and before s+1;
    /// boundaries with the same AfterSequence keep Id order.</summary>
    internal static IReadOnlyList<CaptureTimelineEntry> Ordered(IReadOnlyList<RecordedInput> events, IReadOnlyList<RecordedBoundary> boundaries)
    {
        var inputs = events.OrderBy(e => e.Sequence).ToArray();
        var marks = boundaries.OrderBy(b => b.AfterSequence).ThenBy(b => b.Id).ToArray();
        var result = new List<CaptureTimelineEntry>(inputs.Length + marks.Length);
        int m = 0;
        foreach (var e in inputs)
        {
            while (m < marks.Length && marks[m].AfterSequence < e.Sequence) result.Add(new(null, marks[m++]));
            result.Add(new(e, null));
        }
        while (m < marks.Length) result.Add(new(null, marks[m++]));
        return result;
    }
}
