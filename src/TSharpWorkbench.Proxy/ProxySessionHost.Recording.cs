using System.Text.Json;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

// Transport-thread recorder operations (Workstream C owns this file; ProxySessionHost.cs owns the UI-thread path).
internal sealed partial class ProxySessionHost
{
    // Transport thread: recorder operations that never touch the UI. RecordSnapshots is allowed from RecordStart until the
    // session ends (recording, paused, and after RecordStop: the store is frozen, still readable); refused before RecordStart.
    private Reply RespondRecording(Request r)
    {
        try
        {
            if (!record || !recordingStarted) throw new InvalidOperationException("No recording was started.");
            switch (r.Operation)
            {
                case Protocol.Operations.RecordPause: driver.Recorder.SetPaused(true); driver.Gestures.Cancel(); break;
                case Protocol.Operations.RecordResume: driver.Recorder.SetPaused(false); break;
                // One-shot Pick Mode (transport thread; the UI thread consumes it at the next left press).
                case Protocol.Operations.RecordPickArm:
                {
                    var c = driver.Recorder.Cursor();
                    if (!c.Active || c.Paused) throw new InvalidOperationException("Pick Mode needs an active, unpaused recording.");
                    if (!driver.Gestures.Arm()) throw new InvalidOperationException("Pick Mode could not be armed.");
                    break;
                }
                case Protocol.Operations.RecordPickCancel: driver.Gestures.Cancel(); break;
                case Protocol.Operations.RecordSnapshots:
                    return Response(r, Protocol.States.Recording) with { Snapshots = RecordingTransport.Snapshots(driver, r.After ?? 0) };
            }
            return Response(r, Protocol.States.Recording) with { Recording = PollRecording(r.After ?? 0, r.MarksAfter) };
        }
        catch (InvalidOperationException ex) { return Response(r, Protocol.States.Rejected, "Unsupported operation or invalid state: " + ex.Message); }
    }

    /// <summary>Any thread: the recorder batch with the capture sources (RecordStart/RecordStop use it too).</summary>
    private RecordingBatch PollRecording(long after, long? marksAfter, string? snapshot = null) =>
        RecordingTransport.Poll(driver, after, marksAfter, snapshot);
}

/// <summary>Transport-side composition of recorder batches and bounded retained-snapshot replies.</summary>
internal static class RecordingTransport
{
    internal const int SnapshotCount = 8, SnapshotBytes = 1024 * 1024;
    /// <summary>Reply envelope reserve (ids, schema, coverage, JSON framing); a lone snapshot may use the rest of a frame.</summary>
    internal const int EnvelopeReserve = 64 * 1024;
    internal const int SingleSnapshotBytes = Protocol.MaximumBytes - EnvelopeReserve;

    internal static CaptureSources Sources(WorkbenchProxyDriver driver) => new(driver.Snapshots, TrackerCounters(driver), PickCounters(driver));

    internal static RecordingBatch Poll(WorkbenchProxyDriver driver, long after, long? marksAfter, string? snapshot = null) =>
        driver.Recorder.Poll(after, snapshot, marksAfter, Sources(driver)) with { PickState = driver.Gestures.State };

    // Tracker-emitted/refused picks of the current recording + the gesture engine's own counters (per proxy session).
    private static Func<(long, long, long, long)> PickCounters(WorkbenchProxyDriver driver) => () =>
    {
        var t = driver.Boundaries.PickCounters(driver.Recorder.RecordingId);
        var g = driver.Gestures.Counters();
        return (t.Picks, t.Refused + g.Aborted, g.Swallowed, g.DoubleClicksCleared);
    };

    // Tracker-held counters of the recorder's current recording (zeros until the tracker observed it). Tracker "Refused"
    // (AddBoundary refusals) is not added: the recorder already counts its own refusals as BoundariesDropped.
    // NOTE(coordinator): WorkbenchProxyDriver.Boundaries is created lazily (`??=`); it must be created before the transport
    // can poll (eagerly, or on the UI thread at RecordStart) so two threads never race to create two trackers.
    private static Func<(long Suppressed, long CaptureFailures, long SkippedUnsafe)> TrackerCounters(WorkbenchProxyDriver driver) => () =>
    {
        var c = driver.Boundaries.Counters(driver.Recorder.RecordingId);
        return (c.Suppressed, c.CaptureFailures, c.SkippedUnsafe);
    };

    /// <summary>Transport thread: acknowledges (releases) snapshots up to <paramref name="after"/> and returns the next ones,
    /// at most <see cref="SnapshotCount"/> and <see cref="SnapshotBytes"/> serialized (a lone larger snapshot is allowed up to
    /// <see cref="SingleSnapshotBytes"/>; beyond that its inspection is omitted with an explicit marker).</summary>
    internal static SnapshotBatch Snapshots(WorkbenchProxyDriver driver, long after)
    {
        var read = driver.Snapshots.Read(after, SnapshotCount, SnapshotBytes);
        var sources = Sources(driver);
        return Fit(read, after, driver.Snapshots.Latest, driver.Recorder.Coverage(sources));
    }

    /// <summary>Pure: bounds a store read to the reply budget. Snapshots left out stay retained (Next = last returned id, so the
    /// target releases them only after a later read). The result always serializes within one protocol frame.</summary>
    internal static SnapshotBatch Fit(IReadOnlyList<RetainedSnapshot> read, long after, long latest, CaptureCoverage coverage,
        int maximumCount = SnapshotCount, int maximumBytes = SnapshotBytes)
    {
        var kept = new List<RetainedSnapshot>();
        long total = 0;
        foreach (var snapshot in read.Take(maximumCount))
        {
            var item = snapshot;
            long size = Size(item);
            if (size > SingleSnapshotBytes && item.Inspection is { ViewTree: not null } inspection)
            {
                // The view tree is the bulky part; menu/status state (what transient snapshots are for) is kept if it fits.
                item = item with { Inspection = inspection with { ViewTree = null }, Omitted = Protocol.SnapshotOmissions.ViewTreeSize };
                size = Size(item);
            }
            if (size > SingleSnapshotBytes)
            {
                item = item with { Inspection = null, Omitted = Protocol.SnapshotOmissions.InspectionSize };
                size = Size(item);
            }
            if (kept.Count > 0 && total + size > maximumBytes) break;
            kept.Add(item); total += size;
        }
        var batch = new SnapshotBatch(kept.ToArray(), kept.Count == 0 ? Math.Max(after, 0) : kept[^1].Id, latest, coverage);
        // Defensive: never hand the transport a frame it must refuse (WriteAsync would end the session).
        if (Size(batch) > SingleSnapshotBytes)
            batch = batch with { Snapshots = [.. kept.Select(s => s.Inspection is null ? s : s with { Inspection = null, Omitted = Protocol.SnapshotOmissions.InspectionSize })] };
        return batch;
    }

    // The same serializer and options as Protocol.WriteAsync.
    internal static long Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value).LongLength;
}
