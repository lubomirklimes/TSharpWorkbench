using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

// Recorder timeline, wire contracts, boundary journal and bounded snapshot replies. In-process
// (NullDriver, no target process); the pipe test talks to a real ProxySessionHost transport over its named pipe.
public sealed class RecordingProtocolTests : IDisposable
{
    private readonly IDriver? oldDriver = TDisplay.driver;
    private readonly ScreenBuffer? oldBuffer = TScreen.ScreenBuffer;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "recording-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public RecordingProtocolTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
        TDisplay.driver = null;
        TEventQueue.Resume(); Drain(); TEventQueue.ClearPosted(); TEventQueue.ClaimUiThread();
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        TDisplay.driver?.Shutdown();
        TDisplay.driver = oldDriver; TScreen.ScreenBuffer = oldBuffer;
        Drain(); TEventQueue.ClearPosted();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static RecordedBoundary Mark(long id, long after = 0, long? snapshot = null) =>
        new(id, Protocol.BoundaryKinds.Idle, id == 1 ? Protocol.BoundaryCauses.Start : Protocol.BoundaryCauses.Input, after, after, id,
            0xfeed_0000_0000_0000UL + (ulong)id, id * 10, 80, 25, id % 2 == 0 ? "TDialog" : null, (int)(id % 3), SnapshotId: snapshot);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;

    // ── wire contracts ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllSr1RecordsRoundTripThroughTheProtocolSerializer()
    {
        var coverage = new CaptureCoverage(5, 1, 2, 3, 1, 2, 1, 4096, 1, 2) { JournalDropped = 7 };
        var snapshot = new RetainedSnapshot(3, 2, Protocol.SnapshotReasons.MenuOpen, 9, 11, 4, ulong.MaxValue, 120, "VFZTMQ==", 80, 25,
            new CheckpointInspection(null, new MenuState(true, 1, null, []), null, ["status: inspector failed"]))
            { Omitted = Protocol.SnapshotOmissions.InspectionSize };
        var batch = new RecordingBatch([new RecordedInput(1, "backend-key", 5, "key", 0x10, 0x3c00, null, 0, 0, 0, 0, 0, 80, 25)], 1, true,
            false, 0, 1, 0, 0, 80, 25, "NullDriver")
            { Boundaries = [Mark(1), Mark(2, 1, snapshot: 3) with { AfterGap = true, SnapshotDropped = true }], BoundaryNext = 2, SnapshotLatest = 3, Coverage = coverage };
        var reply = new Reply("id", Protocol.States.Recording, Protocol.Schema, "inst", Recording: batch,
            Inspect: new RecordInspection(4, 99, 9, 11, 120, "VFZTMQ==", 80, 25, null, 7),
            Snapshots: new SnapshotBatch([snapshot], 3, 3, coverage));
        var request = new Request(1, Protocol.Schema, "inst", "id", "s", Protocol.Operations.RecordSnapshots, After: 2, MarksAfter: 5);
        var line = new BoundaryJournalLine(RecordingBoundaryJournal.Loss, Mark(4), 3, RecordingBoundaryJournal.LossJournal);

        Assert.Equal(Json(reply), Json(RoundTrip(reply)));
        Assert.Equal(Json(request), Json(RoundTrip(request)));
        Assert.Equal(Json(line), Json(RoundTrip(line)));
        Assert.Equal(5, RoundTrip(request).MarksAfter);
        var back = RoundTrip(reply);
        Assert.Equal(ulong.MaxValue, back.Snapshots!.Snapshots[0].Hash);
        Assert.Equal(Protocol.SnapshotOmissions.InspectionSize, back.Snapshots.Snapshots[0].Omitted);
        Assert.Equal(7, back.Recording!.Coverage!.JournalDropped);
        Assert.True(back.Recording.Boundaries[1].AfterGap);
        Assert.False(back.Recording.Coverage.Complete);
        Assert.DoesNotContain("\"Complete\"", Json(coverage), StringComparison.Ordinal); // derived, never on the wire
        // An older request (no MarksAfter) still deserializes with a null cursor.
        var legacy = JsonSerializer.Deserialize<Request>("""{"Version":1,"Schema":"x","Instance":"i","Id":"a","Secret":"s","Operation":"RecordPoll","After":3}""")!;
        Assert.Null(legacy.MarksAfter);
    }

    [Fact]
    public void WithoutMarksAfterThePollIsTheLegacyPayload()
    {
        var recorder = new ProxyInputRecorder("NullDriver");
        recorder.Start(80, 25, null);
        long id = recorder.RecordingId;
        Assert.True(recorder.AddBoundary(Mark(1), id));
        var batch = recorder.Poll(0);
        Assert.Empty(batch.Boundaries);
        Assert.Equal((0L, 0L), (batch.BoundaryNext, batch.SnapshotLatest));
        Assert.Null(batch.Coverage);
        var poll = recorder.Poll(0, marksAfter: 0);
        Assert.Single(poll.Boundaries);
        Assert.NotNull(poll.Coverage);
    }

    [Fact]
    public void BoundariesAreBatchedInIdOrderWithACursorAcrossManyBatches()
    {
        var recorder = new ProxyInputRecorder("NullDriver");
        recorder.Start(80, 25, null);
        long id = recorder.RecordingId;
        for (int i = 1; i <= 1300; i++) Assert.True(recorder.AddBoundary(Mark(i, after: i / 3), id));
        var seen = new List<RecordedBoundary>();
        long cursor = 0;
        int polls = 0;
        while (true)
        {
            var batch = recorder.Poll(0, marksAfter: cursor);
            Assert.True(batch.Boundaries.Length <= ProxyInputRecorder.MaximumBatch);
            polls++;
            if (batch.Boundaries.Length == 0) { Assert.Equal(cursor, batch.BoundaryNext); break; }
            Assert.Equal(batch.Boundaries[^1].Id, batch.BoundaryNext);
            seen.AddRange(batch.Boundaries); cursor = batch.BoundaryNext;
        }
        Assert.Equal(4, polls); // 512 + 512 + 276 + empty
        Assert.Equal(Enumerable.Range(1, 1300).Select(i => (long)i), seen.Select(b => b.Id));
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.First.AfterSequence <= p.Second.AfterSequence));
        Assert.Equal(1300, recorder.Poll(0, marksAfter: 0).Coverage!.Boundaries);
    }

    [Fact]
    public void FirstBoundaryAfterAResumeIsMarkedAfterGapAndPausedOrStaleBoundariesAreRefused()
    {
        var recorder = new ProxyInputRecorder("NullDriver");
        recorder.Start(80, 25, null);
        long id = recorder.RecordingId;
        Assert.True(recorder.AddBoundary(Mark(1), id));
        recorder.SetPaused(true);
        Assert.False(recorder.AddBoundary(Mark(2), id));   // no marks while paused (not counted as a loss)
        recorder.SetPaused(false);
        Assert.False(recorder.AddBoundary(Mark(3), id - 1)); // a stale recording id
        Assert.True(recorder.AddBoundary(Mark(3), id));
        Assert.True(recorder.AddBoundary(Mark(4), id));
        var batch = recorder.Poll(0, marksAfter: 0);
        Assert.Equal([false, true, false], batch.Boundaries.Select(b => b.AfterGap));
        Assert.Equal(0, batch.Coverage!.BoundariesDropped);
        recorder.Stop();
        Assert.False(recorder.AddBoundary(Mark(5), id));
        recorder.Start(80, 25, null); // a new recording starts empty
        Assert.Empty(recorder.Poll(0, marksAfter: 0).Boundaries);
    }

    [Fact]
    public void OverflowAndNonIncreasingIdsAreCountedNeverBlockingAndCoverageIsIncomplete()
    {
        var recorder = new ProxyInputRecorder("NullDriver");
        recorder.Start(80, 25, null);
        long id = recorder.RecordingId;
        Assert.True(recorder.AddBoundary(Mark(1), id));
        Assert.False(recorder.AddBoundary(Mark(1), id)); // duplicate id: refused and counted
        for (int i = 2; i <= ProxyInputRecorder.MaximumBoundaries + 5; i++) recorder.AddBoundary(Mark(i), id);
        var coverage = recorder.Coverage();
        Assert.Equal(ProxyInputRecorder.MaximumBoundaries, coverage.Boundaries);
        Assert.Equal(6, coverage.BoundariesDropped);
        Assert.False(coverage.Complete);
    }

    [Fact]
    public void CoverageComposesRecorderSnapshotStoreAndTrackerCounters()
    {
        var recorder = new ProxyInputRecorder("NullDriver");
        var store = new ProxyRetainedSnapshots();
        recorder.Start(80, 25, null);
        long id = recorder.RecordingId;
        recorder.AddBoundary(Mark(1), id);
        var sources = new CaptureSources(store, () => (10, 20, 30));
        var batch = recorder.Poll(0, marksAfter: 0, sources: sources);
        var c = batch.Coverage!;
        var s = store.Counters();
        Assert.Equal((1L, 0L, 10L, 20L, 30L), (c.Boundaries, c.BoundariesDropped, c.BoundariesSuppressed, c.CaptureFailures, c.SkippedUnsafe));
        Assert.Equal((s.Added, s.Dropped, s.Released, s.Retained, s.Bytes), (c.Snapshots, c.SnapshotsDropped, c.SnapshotsReleased, c.SnapshotsRetained, c.SnapshotBytesRetained));
        Assert.Equal(store.Latest, batch.SnapshotLatest);
        Assert.False(c.Complete);
        Assert.True(recorder.Poll(0, marksAfter: 0, sources: new(store)).Coverage!.Complete); // no tracker counters, nothing lost
        // The production composition: the driver's own store and boundary tracker (nothing observed yet: zeros).
        var proxy = Install(new NullDriver(40, 12));
        proxy.Recorder.Start(40, 12, null);
        var live = RecordingTransport.Poll(proxy, 0, 0).Coverage!;
        Assert.Equal((0L, 0L, 0L), (live.BoundariesSuppressed, live.CaptureFailures, live.SkippedUnsafe));
    }

    // ── boundary journal ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BoundaryJournalIsWrittenOffTheCallingThreadAndIsReadableBeforeStopLikeAfterACrash()
    {
        var native = new NullDriver(40, 12);
        var proxy = Install(native);
        proxy.Recorder.Start(40, 12, folder);
        long id = proxy.Recorder.RecordingId;
        TEvent ev = default;
        foreach (var key in new[] { Keys.kbF2, Keys.kbF3 }) { native.EnqueueKey(TestKeys.KeyDown(key)); TScreen.GetEvent(ref ev); }
        for (int i = 1; i <= 50; i++) Assert.True(proxy.Recorder.AddBoundary(Mark(i, after: Math.Min(i, 2)), id));
        var writer = proxy.Recorder.BoundaryJournal!;
        // Synchronous spin: this thread stays busy, so any progress was made by the background writer.
        Assert.True(SpinWait.SpinUntil(() => writer.Written >= 50, 10000));
        Assert.NotEqual(Environment.CurrentManagedThreadId, writer.WriterThread);
        string path = Path.Combine(folder, RecordingBoundaryJournal.FileName);
        var crashed = RecordingBoundaryJournal.Read(path); // the writer still holds the file: what a crash would leave
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), crashed.Boundaries.Select(b => b.Id));
        Assert.Equal((RecordingBoundaryJournal.Format, RecordingBoundaryJournal.PayloadsNotPersisted), (crashed.Format, crashed.SnapshotPayloads));
        Assert.False(crashed.Stopped); Assert.False(crashed.Clean);
        proxy.Recorder.Stop();
        Assert.True(SpinWait.SpinUntil(() => writer.Completion.IsCompleted, 10000));
        var clean = RecordingBoundaryJournal.Read(path);
        Assert.True(clean.Clean);
        Assert.Equal(50, clean.Boundaries.Length);
        // The input journal is unchanged: RecordedInput lines only, as old Workbench builds read it.
        var inputs = RecordingJournal.Read(Path.Combine(folder, RecordingJournal.FileName));
        Assert.Equal(proxy.Recorder.Poll(0).Events, inputs);
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(folder, RecordingJournal.FileName)), l => l.Contains("\"Type\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JournalQueueOverflowAndCaptureLossesBecomeExplicitLossLines()
    {
        string path = Path.Combine(folder, RecordingBoundaryJournal.FileName);
        var blocked = new TaskCompletionSource();
        var writer = new BoundaryJournalWriter(path, blocked.Task); // waits for a "previous" writer: the queue fills up
        for (int i = 1; i <= BoundaryJournalWriter.Capacity + 10; i++) writer.Post(new(RecordingBoundaryJournal.Boundary, Mark(i)));
        Assert.Equal(10, writer.Dropped);
        writer.CaptureLost(); writer.CaptureLost(); writer.CaptureLost();
        blocked.SetResult();
        writer.Complete();
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var journal = RecordingBoundaryJournal.Read(path);
        Assert.Equal(BoundaryJournalWriter.Capacity, journal.Boundaries.Length);
        Assert.Equal((3L, 10L, true), (journal.CaptureLost, journal.JournalLost, journal.Stopped));
        Assert.False(journal.Clean);
        Assert.Contains(File.ReadAllLines(path), l => l.Contains("\"loss\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ATornFinalJournalLineEndsTheReadableJournalAndAMissingFileIsEmpty()
    {
        string path = Path.Combine(folder, RecordingBoundaryJournal.FileName);
        File.WriteAllLines(path, [Json(new BoundaryJournalLine(RecordingBoundaryJournal.Header, Detail: RecordingBoundaryJournal.Format)),
            Json(new BoundaryJournalLine(RecordingBoundaryJournal.Boundary, Mark(1))), "{\"Type\":\"boundary\",\"Boun"]);
        var journal = RecordingBoundaryJournal.Read(path);
        Assert.Single(journal.Boundaries);
        Assert.True(journal.Torn); Assert.False(journal.Clean);
        Assert.Empty(RecordingBoundaryJournal.Read(Path.Combine(folder, "absent.jsonl")).Boundaries);
    }

    // ── retained-snapshot replies ──────────────────────────────────────────────────────────────────────────────────────

    private static string Cells(int w, int h) => Convert.ToBase64String(new ScreenSnapshot(w, h, new TScreenChar[w * h]).Serialize());

    private static RetainedSnapshot Snap(long id, string cells, CheckpointInspection? inspection = null) =>
        new(id, id, Protocol.SnapshotReasons.ModalOpen, id, id, id, (ulong)id, id, cells, 200, 100, inspection);

    // Worst realistic semantic payload: 1000 view nodes with maximal non-ASCII texts (escaped by the serializer).
    private static CheckpointInspection WorstInspection(int nodes = 1000, int text = 256, bool ascii = false)
    {
        string t = new(ascii ? 'x' : 'č', text);
        var flags = new ViewFlags(true, true, true, true, true, true, false, true, true, true, 0x7fff, 0x7fff);
        var rect = new CellRect(199, 99, 200, 100);
        var props = new Dictionary<string, string> { ["value"] = t, ["range"] = "0..2147483647", ["state"] = new string('š', 64) };
        ViewNode Node(int i) => new(string.Join('/', Enumerable.Repeat(i % 1000, 24)), "id-" + new string('x', 60), "TSharpVision.Views.TInputLineWithLongTypeName",
            "input", rect, rect, rect, flags, 65535, t, t, props, [], 0);
        var root = new ViewNode("", null, "TApplication", "application", rect, rect, rect, flags, 0, null, null, null,
            Enumerable.Range(1, nodes - 1).Select(Node).ToArray(), 0);
        var items = Enumerable.Range(0, 40).Select(i => new MenuEntry(i, t, t, "x", 1000 + i, true, false, false, true, "Alt+F10", 0x7100, "kbAltF10", 1, false, rect, "mismatch", 0x70)).ToArray();
        var level = new MenuViewState(1, "TMenuBox", "0/1", rect, true, true, 3, t, t, items);
        var status = new StatusState("TStatusLine", "0/2", rect, 1, 0, 0, 65535, t, rect, "truncated",
            Enumerable.Range(0, 40).Select(i => new StatusEntry(i, t, t, 0x3b00, "kbF1", 1, true, true, rect, "match", 0x30)).ToArray(), t);
        return new(new ViewTree(root, nodes, true, "front-to-back", rect, rect, "0/1", "0/1/0"),
            new MenuState(true, 3, level, [level, level, level]), status, []);
    }

    private static async Task<long> FrameBytes(Reply reply)
    {
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, reply, CancellationToken.None); // throws when the frame exceeds Protocol.MaximumBytes
        return stream.Length;
    }

    [Fact]
    public async Task SnapshotRepliesAreBoundedByCountAndBytesAndKeepTheRestRetained()
    {
        string cells = Cells(200, 100);
        var read = Enumerable.Range(1, 20).Select(i => Snap(i, cells)).ToArray();
        var coverage = new CaptureCoverage(20, 0, 0, 20, 0, 0, 20, 0, 0, 0);
        var first = RecordingTransport.Fit(read, 0, 20, coverage);
        Assert.InRange(first.Snapshots.Length, 1, RecordingTransport.SnapshotCount);
        Assert.True(first.Snapshots.Sum(s => RecordingTransport.Size(s)) <= RecordingTransport.SnapshotBytes);
        Assert.Equal(first.Snapshots[^1].Id, first.Next);
        Assert.Equal(20, first.Latest);
        // Large enough that the byte cap, not the count cap, decides (80 KB cells each: 8 fit in 1 MiB, so pad to ~280 KB).
        var padded = read.Select(s => s with { Inspection = new CheckpointInspection(null, null, null, [new string('x', 200_000)]) }).ToArray();
        var capped = RecordingTransport.Fit(padded, 0, 20, coverage);
        Assert.InRange(capped.Snapshots.Length, 1, RecordingTransport.SnapshotCount - 1);
        Assert.True(capped.Snapshots.Sum(s => RecordingTransport.Size(s)) <= RecordingTransport.SnapshotBytes);
        Assert.All(capped.Snapshots, s => Assert.Null(s.Omitted));
        var empty = RecordingTransport.Fit([], 7, 0, coverage);
        Assert.Equal((0, 7L), (empty.Snapshots.Length, empty.Next));
        Assert.True(await FrameBytes(new Reply("x", Protocol.States.Recording, Protocol.Schema, Snapshots: capped)) <= Protocol.MaximumBytes);
    }

    [Fact]
    public async Task WorstCaseSnapshotsFitOneFrameAndOversizedPartsAreOmittedExplicitly()
    {
        var coverage = new CaptureCoverage(1, 0, 0, 8, 0, 0, 8, 0, 0, 0);
        string cells = Cells(200, 100);
        // 200x100 cells + 1000-node view tree + 4 menu levels + status, every text maximal and non-ASCII (escaped x6).
        var worst = Enumerable.Range(1, 8).Select(i => Snap(i, cells, WorstInspection())).ToArray();
        long single = RecordingTransport.Size(worst[0]);
        Assert.True(single > RecordingTransport.SingleSnapshotBytes);
        var batch = RecordingTransport.Fit(worst, 0, 8, coverage);
        var first = batch.Snapshots[0];
        Assert.Equal(Protocol.SnapshotOmissions.ViewTreeSize, first.Omitted); // view tree left out, menu/status kept
        Assert.Null(first.Inspection!.ViewTree);
        Assert.NotNull(first.Inspection.Menu);
        Assert.True(batch.Snapshots.Sum(s => RecordingTransport.Size(s)) <= Math.Max(RecordingTransport.SnapshotBytes, RecordingTransport.Size(first)));
        var reply = new Reply(Guid.NewGuid().ToString("N"), Protocol.States.Recording, Protocol.Schema, new string('f', 32), Snapshots: batch);
        long frame = await FrameBytes(reply);
        output.WriteLine($"worst snapshot {single} bytes serialized; reply {batch.Snapshots.Length} snapshot(s), frame {frame} bytes");
        Assert.True(frame <= Protocol.MaximumBytes);
        // Cells + the 1000-node tree alone (no menu/status): measured and bounded the same way.
        var tree = Snap(1, cells, WorstInspection() with { Menu = null, Status = null });
        output.WriteLine($"cells + 1000-node view tree: {RecordingTransport.Size(tree)} bytes");
        var lone = RecordingTransport.Fit([tree, tree with { Id = 2 }], 0, 2, coverage);
        Assert.True(await FrameBytes(new Reply("x", Protocol.States.Recording, Protocol.Schema, Snapshots: lone)) <= Protocol.MaximumBytes);
        // A 1000-node ASCII tree with short texts (a large but ordinary dialog) fits whole.
        var ordinary = Snap(1, cells, WorstInspection(1000, 24, ascii: true) with { Menu = null, Status = null });
        var whole = Assert.Single(RecordingTransport.Fit([ordinary], 0, 1, coverage).Snapshots);
        output.WriteLine($"cells + ordinary 1000-node view tree: {RecordingTransport.Size(ordinary)} bytes");
        Assert.Null(whole.Omitted);
        // Nothing but non-tree inspection far beyond a frame: the whole inspection is left out; cells and metadata stay.
        var giant = Snap(1, cells, new CheckpointInspection(null, null, null, [new string('\u010D', 1_000_000)]));
        Assert.True(RecordingTransport.Size(giant) > Protocol.MaximumBytes);
        var fitted = RecordingTransport.Fit([giant], 0, 1, coverage);
        var kept = Assert.Single(fitted.Snapshots);
        Assert.Equal((Protocol.SnapshotOmissions.InspectionSize, cells), (kept.Omitted, kept.Cells));
        Assert.Null(kept.Inspection);
        Assert.Equal(1, fitted.Next);
        Assert.True(await FrameBytes(new Reply("x", Protocol.States.Recording, Protocol.Schema, Snapshots: fitted)) <= Protocol.MaximumBytes);
    }

    // ── transport (real ProxySessionHost pipe; no UI thread involved) ─────────────────────────────────────────────────

    [Fact]
    public async Task TransportPassesMarksAfterAndServesSnapshotsOnlyAfterRecordStart()
    {
        var proxy = Install(new NullDriver(40, 12));
        string instance = Guid.NewGuid().ToString("N"), secret = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        Environment.SetEnvironmentVariable(TestTargetHost.InstanceVariable, instance);
        Environment.SetEnvironmentVariable(TestTargetHost.SecretVariable, secret);
        Environment.SetEnvironmentVariable(TestTargetHost.RecordVariable, "1");
        using var host = new ProxySessionHost(proxy);
        Environment.SetEnvironmentVariable(TestTargetHost.InstanceVariable, null);
        await using var pipe = new NamedPipeClientStream(".", "tsharp-diagnostics-" + instance, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10000);
        async Task<Reply> Send(string operation, long? after = null, long? marks = null)
        {
            using var limit = new CancellationTokenSource(10000);
            await Protocol.WriteAsync(pipe, new Request(Protocol.Version, Protocol.Schema, instance, Guid.NewGuid().ToString("N"), secret, operation,
                After: after, MarksAfter: marks), limit.Token);
            return await Protocol.ReadAsync<Reply>(pipe, limit.Token);
        }
        var refused = await Send(Protocol.Operations.RecordSnapshots, 0);
        Assert.Equal(Protocol.States.Rejected, refused.State); // before RecordStart

        // RecordStart runs on the UI path (not driven here): start the recorder and mark the session as recording.
        proxy.Recorder.Start(40, 12, null);
        typeof(ProxySessionHost).GetField("recordingStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(host, true);
        long id = proxy.Recorder.RecordingId;
        for (int i = 1; i <= 600; i++) proxy.Recorder.AddBoundary(Mark(i), id);

        var legacy = await Send(Protocol.Operations.RecordPoll, 0);
        Assert.Empty(legacy.Recording!.Boundaries); Assert.Null(legacy.Recording.Coverage);
        var poll = await Send(Protocol.Operations.RecordPoll, 0, 0);
        Assert.Equal(ProxyInputRecorder.MaximumBatch, poll.Recording!.Boundaries.Length);
        var paused = await Send(Protocol.Operations.RecordPause, 0, poll.Recording.BoundaryNext);
        Assert.Equal(88, paused.Recording!.Boundaries.Length);
        Assert.True(paused.Recording.Paused);
        var resumed = await Send(Protocol.Operations.RecordResume, 0, paused.Recording.BoundaryNext);
        Assert.Empty(resumed.Recording!.Boundaries);
        Assert.Equal(600, resumed.Recording.BoundaryNext);

        var snapshots = await Send(Protocol.Operations.RecordSnapshots, 0);
        Assert.Equal(Protocol.States.Recording, snapshots.State);
        Assert.NotNull(snapshots.Snapshots!.Coverage);
        Assert.True(snapshots.Snapshots.Snapshots.Length <= RecordingTransport.SnapshotCount);
        proxy.Recorder.Stop();
        var afterStop = await Send(Protocol.Operations.RecordSnapshots, snapshots.Snapshots.Next);
        Assert.Equal(Protocol.States.Recording, afterStop.State); // still readable after RecordStop until the session ends
        Assert.Equal(600, afterStop.Snapshots!.Coverage.Boundaries);
    }

    private static WorkbenchProxyDriver Install(IDriver backend)
    {
        var proxy = WorkbenchProxyDriver.Wrap(backend);
        proxy.Initialize(); TDisplay.driver = proxy;
        TScreen.ScreenBuffer = proxy.AllocateScreenBuffer();
        return proxy;
    }

    private static void Drain() { TEventQueue.Resume(); TEvent ev = default; do { TEventQueue.GetNextEvent(ref ev); } while (ev.What != Events.evNothing); }
}
