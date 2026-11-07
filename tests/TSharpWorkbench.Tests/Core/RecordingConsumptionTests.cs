using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Workbench-side capture consumption. No process: RecordingSession.ForTesting with a scripted transport
// that answers like the target's Proxy, plus the pure RecordingCaptureTimeline.
public sealed class RecordingConsumptionTests
{
    private static long seq;
    private static RecordedInput Key(ushort code, string? text = null) =>
        new(++seq, "backend-key", 0, "key", Events.evKeyDown, code, text, 0, 0, 0, 0, 0, 80, 25);
    private static RecordedInput Mouse(ushort what, int x, int y, byte buttons = 0) =>
        new(++seq, "backend-queue", 0, "mouse", what, 0, null, 0, x, y, buttons, 0, 80, 25);
    private static RecordedInput Input(long sequence) => new(sequence, "backend-key", 0, "key", Events.evKeyDown, Keys.kbF2, null, 0, 0, 0, 0, 0, 80, 25);
    private static RecordedBoundary Mark(long id, long after = 0, long? snapshot = null) =>
        new(id, Protocol.BoundaryKinds.Idle, Protocol.BoundaryCauses.Input, after, after, id, (ulong)id, id, 80, 25, null, 0, SnapshotId: snapshot);
    private static CaptureCoverage Covered(long boundaries = 0, long snapshotsDropped = 0) => new(boundaries, 0, 0, 0, snapshotsDropped, 0, 0, 0, 0, 0);
    private static RetainedSnapshot Snap(long id, int cellChars = 16) =>
        new(id, id, Protocol.SnapshotReasons.MenuOpen, id, id, id, (ulong)id, id, new string('A', cellChars), 80, 25, null);
    private static RecordingBatch Batch(RecordedInput[]? events = null, RecordedBoundary[]? marks = null, long marksAfter = 0, long latest = 0,
        CaptureCoverage? coverage = null)
    {
        events ??= []; marks ??= [];
        return new(events, events.Length == 0 ? 0 : events[^1].Sequence, true, false, 0, 0, 0, 0, 80, 25, "Win32ConsoleDriver")
            { Boundaries = marks, BoundaryNext = marks.Length == 0 ? marksAfter : marks[^1].Id, SnapshotLatest = latest, Coverage = coverage };
    }
    private static Reply Answer(string state, RecordingBatch? recording = null, SnapshotBatch? snapshots = null, RecordInspection? inspect = null,
        string? reason = null) =>
        new(RecordingSession.TestId, state, Protocol.Schema, RecordingSession.TestId, reason, Recording: recording, Snapshots: snapshots, Inspect: inspect);

    /// <summary>Scripted target: records every call (operation, after, marksAfter) and the peak number of concurrent calls.</summary>
    private sealed class Transport(Func<string, long, long?, Reply> answer)
    {
        internal readonly List<(string Operation, long After, long? Marks)> Calls = [];
        private int inside;
        internal int Peak;
        internal Func<Task>? Gate;
        internal async Task<Reply> Send(string operation, long after, long? marks)
        {
            int now = Interlocked.Increment(ref inside);
            Peak = Math.Max(Peak, now);
            try
            {
                lock (Calls) Calls.Add((operation, after, marks));
                if (Gate is not null && operation == Protocol.Operations.RecordPoll) await Gate();
                await Task.Yield();
                return answer(operation, after, marks);
            }
            finally { Interlocked.Decrement(ref inside); }
        }
    }

    private static RecordingSession Session(Transport transport, params string[] features)
    {
        var s = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Recording, [], transport.Send);
        s.Capture.Negotiate(features);
        return s;
    }

    // ── capability negotiation ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FullEmptyAndPartialFeatureSetsNegotiateCaptureWithAClearDiagnostic()
    {
        var full = new RecordingCaptureTimeline();
        full.Negotiate([.. Protocol.RecordFeatures.All, "future-feature/9"]);
        Assert.Equal(Protocol.RecordFeatures.All, full.Features);
        Assert.Null(full.Diagnostic);
        Assert.Equal(0, full.MarksAfter);

        var legacy = new RecordingCaptureTimeline();
        legacy.Negotiate([]);
        Assert.Empty(legacy.Features);
        Assert.Contains("predates Smart Recorder capture", legacy.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Fix Issues", legacy.Diagnostic, StringComparison.Ordinal);
        Assert.Null(legacy.MarksAfter); // the legacy poll: no MarksAfter on the wire
        legacy.Merge(Batch(marks: [Mark(1)], latest: 4, coverage: Covered(1)));
        Assert.Empty(legacy.Boundaries);
        Assert.False(legacy.CoverageComplete);
        new RecordingCaptureTimeline().Negotiate(null); // a null list is legacy too, never an exception

        var partial = new RecordingCaptureTimeline();
        partial.Negotiate([Protocol.RecordFeatures.Boundaries]);
        Assert.True(partial.BoundariesEnabled);
        Assert.False(partial.SnapshotsEnabled); Assert.False(partial.InspectEnabled);
        Assert.Contains("missing", partial.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(Protocol.RecordFeatures.Snapshots, partial.Diagnostic, StringComparison.Ordinal);
        partial.Merge(Batch(marks: [Mark(1)], latest: 4, coverage: Covered(1)));
        Assert.Single(partial.Boundaries);
        Assert.False(partial.SnapshotsPending); // snapshots not advertised: never fetched
        Assert.False(partial.CoverageComplete); // partial capture is never full coverage
    }

    [Fact]
    public async Task ALegacyTargetKeepsRecordingExactlyAsBeforeWithoutSr1Requests()
    {
        var transport = new Transport((op, after, marks) => Answer(Protocol.States.Recording, new RecordingBatch([Input(after + 1)], after + 1, true,
            false, 0, 0, 0, 0, 80, 25, "Win32ConsoleDriver")));
        await using var s = Session(transport);
        await s.PollOnceAsync();
        await s.PollOnceAsync();
        Assert.Equal([1L, 2L], s.Events.Select(e => e.Sequence));
        Assert.All(transport.Calls, c => Assert.Null(c.Marks));
        Assert.DoesNotContain(transport.Calls, c => c.Operation == Protocol.Operations.RecordSnapshots);
        Assert.Null(await s.InspectAsync()); // not advertised: no request at all
        Assert.Equal(2, transport.Calls.Count);
        Assert.NotNull(s.CaptureDiagnostic);
    }

    // ── consumption ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollMergesBoundaryBatchesThenFetchesAnnouncedSnapshotsInBoundedBatches()
    {
        var marks = Enumerable.Range(1, 612).Select(i => Mark(i, after: i / 4, snapshot: i is 100 or 200 or 300 ? i / 100 : null)).ToArray();
        var transport = new Transport((op, after, cursor) => op switch
        {
            Protocol.Operations.RecordPoll => Answer(Protocol.States.Recording,
                Batch(marks: marks.Where(m => m.Id > cursor).Take(512).ToArray(), marksAfter: cursor ?? 0, latest: 3, coverage: Covered(612))),
            Protocol.Operations.RecordSnapshots => Answer(Protocol.States.Recording, snapshots: after == 0
                ? new SnapshotBatch([Snap(1), Snap(2)], 2, 3, Covered(612)) : new SnapshotBatch([Snap(3)], 3, 3, Covered(612))),
            _ => throw new InvalidOperationException(op),
        });
        await using var s = Session(transport, Protocol.RecordFeatures.All);
        await s.PollOnceAsync();
        Assert.Equal([(Protocol.Operations.RecordPoll, 0L, (long?)0L), (Protocol.Operations.RecordPoll, 0L, 512L),
            (Protocol.Operations.RecordSnapshots, 0L, 612L), (Protocol.Operations.RecordSnapshots, 2L, 612L)], transport.Calls);
        Assert.Equal(Enumerable.Range(1, 612).Select(i => (long)i), s.Capture.Boundaries.Select(b => b.Id));
        Assert.Equal([1L, 2L, 3L], s.Capture.Snapshots.Select(x => x.Id));
        Assert.False(s.Capture.SnapshotsPending);
        Assert.True(s.Capture.CoverageComplete);
        Assert.Equal(612, s.Capture.BoundaryCursor);
    }

    [Fact]
    public async Task SnapshotFetchingStopsWhenTheTargetMakesNoProgressAndCoverageStaysIncomplete()
    {
        var transport = new Transport((op, after, cursor) => op == Protocol.Operations.RecordPoll
            ? Answer(Protocol.States.Recording, Batch(marksAfter: cursor ?? 0, latest: 5, coverage: Covered()))
            : Answer(Protocol.States.Recording, snapshots: new SnapshotBatch([], after, 5, Covered())));
        await using var s = Session(transport, Protocol.RecordFeatures.All);
        await s.PollOnceAsync();
        Assert.Single(transport.Calls, c => c.Operation == Protocol.Operations.RecordSnapshots); // no busy loop
        Assert.True(s.Capture.SnapshotsPending);
        Assert.False(s.Capture.CoverageComplete);
    }

    [Fact]
    public void BoundariesAreDeduplicatedByIdAndKeptInOrder()
    {
        var t = new RecordingCaptureTimeline();
        t.Negotiate(Protocol.RecordFeatures.All);
        t.Merge(Batch(marks: [Mark(1), Mark(2), Mark(5)]));
        t.Merge(Batch(marks: [Mark(2), Mark(5), Mark(6)]));
        t.Merge(Batch(marks: [Mark(3)], marksAfter: 6));
        Assert.Equal([1L, 2L, 3L, 5L, 6L], t.Boundaries.Select(b => b.Id));
        Assert.Equal(6, t.BoundaryCursor); // the cursor never moves backwards
    }

    [Fact]
    public void StepIndexAfterMapsBoundariesOntoConverterStepRanges()
    {
        seq = 0;
        var events = new[] { Key((ushort)(0x1e00 | 'h'), "h"), Key((ushort)(0x1e00 | 'i'), "i"), Key(Keys.kbEnter, "\r"),
            Mouse(Events.evMouseDown, 5, 3, 1), Mouse(Events.evMouseUp, 5, 3) };
        var steps = RecordingConverter.Convert(events, 80, 25).Steps;
        Assert.Equal([(1L, 2L), (3L, 3L), (4L, 5L)], steps.Select(s => (s.First, s.Last)));
        Assert.Equal([-1, 0, 0, 1, 2, 2, 2], new long[] { 0, 1, 2, 3, 4, 5, 9 }.Select(a => RecordingCaptureTimeline.StepIndexAfter(Mark(1, a), steps)));
        Assert.Equal(-1, RecordingCaptureTimeline.StepIndexAfter(Mark(1, 3), []));
    }

    [Fact]
    public void OrderedPlacesEachBoundaryAfterItsEventAndBeforeTheNext()
    {
        var ordered = RecordingCaptureTimeline.Ordered([Input(3), Input(1), Input(2)], [Mark(4, 3), Mark(3, 2), Mark(1, 0), Mark(2, 2)]);
        Assert.Equal(["b1", "e1", "e2", "b2", "b3", "e3", "b4"],
            ordered.Select(o => o.Input is { } e ? "e" + e.Sequence : "b" + o.Boundary!.Id));
    }

    [Fact]
    public void TheWorkbenchStoreIsBoundedByCountAndBytesWithExplicitLoss()
    {
        var t = new RecordingCaptureTimeline();
        t.Negotiate(Protocol.RecordFeatures.All);
        long id = 0;
        while (id < RecordingCaptureTimeline.MaximumSnapshots + 10)
        {
            var batch = Enumerable.Range(0, 8).Select(_ => Snap(++id)).ToArray();
            t.AddSnapshots(new SnapshotBatch(batch, id, id, Covered()));
        }
        Assert.Equal(RecordingCaptureTimeline.MaximumSnapshots, t.Snapshots.Count);
        Assert.Equal(id - RecordingCaptureTimeline.MaximumSnapshots, t.SnapshotsLost);
        Assert.False(t.SnapshotsPending);
        Assert.False(t.CoverageComplete);

        var bytes = new RecordingCaptureTimeline();
        bytes.Negotiate(Protocol.RecordFeatures.All);
        int large = (int)(RecordingCaptureTimeline.MaximumSnapshotBytes / 3) + 1024;
        for (int i = 1; i <= 3; i++) bytes.AddSnapshots(new SnapshotBatch([Snap(i, large)], i, 3, Covered()));
        Assert.Equal(2, bytes.Snapshots.Count);
        Assert.Equal(1, bytes.SnapshotsLost);
        Assert.True(bytes.SnapshotBytes <= RecordingCaptureTimeline.MaximumSnapshotBytes);
        bytes.Clear();
        Assert.Equal((0, 0L), (bytes.Snapshots.Count, bytes.SnapshotBytes));
    }

    [Fact]
    public void CrashRecoveryMergesJournaledBoundariesAndNeverClaimsFullCoverage()
    {
        var t = new RecordingCaptureTimeline();
        t.Negotiate(Protocol.RecordFeatures.All);
        t.Merge(Batch(marks: [Mark(1), Mark(2)], coverage: Covered(2)));
        Assert.True(t.CoverageComplete);
        t.Recover(new BoundaryJournalContent([Mark(1), Mark(2), Mark(3, 4, snapshot: 1), Mark(4, 5)], 0, 0, Stopped: false, Torn: true,
            RecordingBoundaryJournal.Format, RecordingBoundaryJournal.PayloadsNotPersisted));
        Assert.Equal([1L, 2L, 3L, 4L], t.Boundaries.Select(b => b.Id));
        Assert.True(t.Recovered);
        Assert.Equal(1, t.SnapshotsLost); // payloads are never journaled: the unfetched snapshot is gone
        Assert.False(t.CoverageComplete);

        var lossy = new RecordingCaptureTimeline();
        lossy.Negotiate(Protocol.RecordFeatures.All);
        lossy.Merge(Batch(coverage: Covered()));
        lossy.Recover(new BoundaryJournalContent([], 2, 0, Stopped: true, Torn: false, RecordingBoundaryJournal.Format, RecordingBoundaryJournal.PayloadsNotPersisted));
        Assert.False(lossy.CoverageComplete);
    }

    // ── lifecycle and inspection ───────────────────────────────────────────────────────────────────────────────────────

    private static Transport Target(Func<string, Reply?>? special = null) => new((op, after, cursor) => special?.Invoke(op) ?? op switch
    {
        Protocol.Operations.RecordPoll or Protocol.Operations.RecordStop => Answer(Protocol.States.Recording,
            Batch(marks: cursor == 0 ? [Mark(1, snapshot: 1)] : [], marksAfter: cursor ?? 0, latest: 1, coverage: Covered(1))),
        Protocol.Operations.RecordSnapshots => Answer(Protocol.States.Recording, snapshots: new SnapshotBatch(after == 0 ? [Snap(1)] : [], 1, 1, Covered(1))),
        Protocol.Operations.Shutdown => Answer(Protocol.States.ShutdownAccepted),
        _ => throw new InvalidOperationException(op),
    });

    [Fact]
    public async Task CancelDiscardsHeldSnapshotsWhileStopKeepsThemForReview()
    {
        await using (var cancelled = Session(Target(), Protocol.RecordFeatures.All))
        {
            await cancelled.PollOnceAsync();
            Assert.Single(cancelled.Capture.Snapshots);
            await cancelled.CancelAsync();
            Assert.Empty(cancelled.Capture.Snapshots);
            Assert.Empty(cancelled.Capture.Boundaries);
        }
        var stopped = Session(Target(), Protocol.RecordFeatures.All);
        await stopped.StopAsync(); // RecordStop, then the frozen store is drained before Shutdown
        Assert.Equal(RecordingState.Stopped, stopped.State);
        Assert.Single(stopped.Capture.Snapshots);
        Assert.Single(stopped.Capture.Boundaries);
        await stopped.DisposeAsync();
        Assert.Empty(stopped.Capture.Snapshots); // Dispose drops payloads too
    }

    [Fact]
    public async Task BusyInspectionIsNonfatalAndSharesThePipeWithPolling()
    {
        bool busy = true;
        var inspection = new RecordInspection(7, 42, 3, 4, 100, "VFZTMQ==", 80, 25, null, 1);
        var transport = Target(op => op != Protocol.Operations.RecordInspect ? null : busy
            ? Answer(Protocol.States.Rejected, reason: Protocol.BusyPrefix + "UI boundary not reached")
            : Answer(Protocol.States.Inspected, inspect: inspection));
        var release = new TaskCompletionSource();
        transport.Gate = () => release.Task;
        await using var s = Session(transport, Protocol.RecordFeatures.All);
        var poll = s.PollOnceAsync();                 // holds the pipe until released
        var inspect = s.InspectAsync();
        await Task.Delay(50);
        Assert.False(inspect.IsCompleted);            // waits for the single pipe
        release.SetResult();
        await poll;
        Assert.Null(await inspect);
        Assert.Equal("UI boundary not reached", s.InspectBusy);
        Assert.Equal(RecordingState.Recording, s.State); // the session continues
        busy = false;
        Assert.Equal(inspection, await s.InspectAsync());
        Assert.Null(s.InspectBusy);
        Assert.Equal(1, transport.Peak);
    }
}
