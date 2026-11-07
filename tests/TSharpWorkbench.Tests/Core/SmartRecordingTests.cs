using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Integration (coordinator): RecordingSession pick/sampler wiring through the scripted transport seam, the analysis
// facade over the real detector/resolver/planner, and the Record Interaction window with Smart capture on/off (logical
// NullDriver screenshots when TSWB_SMART_RECORDING_EVIDENCE is set; NOT native).
[Collection("NonParallel")]
public sealed class SmartRecordingTests
{
    private static string? Evidence => Environment.GetEnvironmentVariable("TSWB_SMART_RECORDING_EVIDENCE") is { Length: > 0 } e ? e : null;

    private static long seq;
    private static RecordedInput Key(ushort code, string? text = null, long ms = 0) =>
        new(++seq, "backend-key", ms, "key", Events.evKeyDown, code, text, 0, 0, 0, 0, 0, 80, 25);
    private static RecordedInput KeyAt(long sequence, ushort code, string? text = null, long ms = 0) =>
        new(sequence, "backend-key", ms, "key", Events.evKeyDown, code, text, 0, 0, 0, 0, 0, 80, 25);
    private static RecordedBoundary Mark(long id, long after, ulong hash, string cause = Protocol.BoundaryCauses.Input, string? modal = null,
        int depth = 0, long ms = 0, long? snapshot = null, string kind = Protocol.BoundaryKinds.Idle) =>
        new(id, kind, id == 1 ? Protocol.BoundaryCauses.Start : cause, after, after, id, hash, ms, 80, 25, modal, depth, SnapshotId: snapshot);
    private static CaptureCoverage Covered(long boundaries = 0) => new(boundaries, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static RecordingBatch Batch(RecordedInput[]? events = null, RecordedBoundary[]? marks = null, string? pickState = null)
    {
        events ??= []; marks ??= [];
        return new(events, events.Length == 0 ? 0 : events[^1].Sequence, true, false, 0, 0, 0, 0, 80, 25, "Win32ConsoleDriver")
            { Boundaries = marks, BoundaryNext = marks.Length == 0 ? 0 : marks[^1].Id, Coverage = Covered(marks.Length), PickState = pickState };
    }
    private static Reply Answer(string state, RecordingBatch? recording = null, RecordInspection? inspect = null, string? reason = null) =>
        new(RecordingSession.TestId, state, Protocol.Schema, RecordingSession.TestId, reason, Recording: recording, Inspect: inspect);
    private static RecordedBoundary State(List<RecordedBoundary> marks) => marks.Last(b => b.Kind != Protocol.BoundaryKinds.Pick);
    private static readonly string[] Full = [.. Protocol.RecordFeatures.All, Protocol.RecordFeatures.Picks];

    private sealed class Transport(Func<string, long, long?, Reply> answer)
    {
        internal readonly List<string> Calls = [];
        internal Task<Reply> Send(string operation, long after, long? marks) { lock (Calls) Calls.Add(operation); return Task.FromResult(answer(operation, after, marks)); }
    }

    private static RecordingSession Session(Transport transport, string[] features, RecordingState state = RecordingState.Recording)
    {
        var s = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", state, [], transport.Send);
        s.Capture.Negotiate(features);
        return s;
    }

    // ── negotiation: picks are optional and never part of coverage ──────────────────────────────────────────────

    [Fact]
    public void PicksAreNegotiatedSeparatelyAndNeverChangeSr1Coverage()
    {
        var withPicks = new RecordingCaptureTimeline(); withPicks.Negotiate(Full);
        Assert.True(withPicks.PicksEnabled);
        Assert.Equal(Protocol.RecordFeatures.All, withPicks.Features);
        Assert.Null(withPicks.Diagnostic);
        var poll = new RecordingCaptureTimeline(); poll.Negotiate(Protocol.RecordFeatures.All);
        Assert.False(poll.PicksEnabled);
        var legacy = new RecordingCaptureTimeline(); legacy.Negotiate([Protocol.RecordFeatures.Picks]); // picks alone mean nothing
        Assert.False(legacy.PicksEnabled);
        Assert.Empty(legacy.Features);
        withPicks.Merge(Batch(pickState: Protocol.PickStates.Armed));
        Assert.Equal(Protocol.PickStates.Armed, withPicks.PickState);
        poll.Merge(Batch(pickState: Protocol.PickStates.Armed));
        Assert.Null(poll.PickState);
    }

    // ── Pick Mode arm/cancel over the transport ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PickModeArmsAndCancelsOverTheTransportAndRefusalsAreNonfatal()
    {
        string pickState = Protocol.PickStates.Off;
        bool refuse = false;
        var transport = new Transport((op, _, _) => op switch
        {
            Protocol.Operations.RecordPickArm when refuse => Answer(Protocol.States.Rejected, reason: "Unsupported operation or invalid state: Pick Mode needs an active, unpaused recording."),
            Protocol.Operations.RecordPickArm => Answer(Protocol.States.Recording, Batch(pickState: pickState = Protocol.PickStates.Armed)),
            Protocol.Operations.RecordPickCancel => Answer(Protocol.States.Recording, Batch(pickState: pickState = Protocol.PickStates.Off)),
            _ => Answer(Protocol.States.Recording, Batch(pickState: pickState)),
        });
        await using var session = Session(transport, Full);
        Assert.True(session.PicksAvailable);
        Assert.True(await session.ArmPickAsync());
        Assert.Equal(Protocol.PickStates.Armed, session.Capture.PickState);
        Assert.True(await session.CancelPickAsync());
        Assert.Equal(Protocol.PickStates.Off, session.Capture.PickState);
        refuse = true;
        Assert.False(await session.ArmPickAsync());
        Assert.Contains("Pick Mode needs", session.PickRefused);
        Assert.Equal(RecordingState.Recording, session.State); // the refusal did not end the session
        Assert.Equal([Protocol.Operations.RecordPickArm, Protocol.Operations.RecordPickCancel, Protocol.Operations.RecordPickArm], transport.Calls);

        // No picks feature (boundaries-only or legacy Proxy): nothing is sent, the reason says how to fix it.
        var old = new Transport((_, _, _) => throw new InvalidOperationException("must not be called"));
        await using var poll = Session(old, Protocol.RecordFeatures.All);
        Assert.False(await poll.ArmPickAsync());
        Assert.Contains("re-prepare", poll.PickRefused);
        Assert.Empty(old.Calls);
        // Paused: refused locally.
        await using var paused = Session(new Transport((_, _, _) => throw new InvalidOperationException()), Full, RecordingState.Paused);
        Assert.False(await paused.ArmPickAsync());
    }

    // ── live inspection sampler ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SamplerInspectsOnlyStatesThatStayedLatestForAPollAndOnlyOnce()
    {
        var marks = new List<RecordedBoundary>();
        int inspects = 0;
        bool refuseInspect = false;
        var transport = new Transport((op, _, _) => op switch
        {
            Protocol.Operations.RecordPoll => Answer(Protocol.States.Recording, Batch(marks: [.. marks])),
            Protocol.Operations.RecordInspect when refuseInspect => Answer(Protocol.States.Rejected, reason: "Unsupported operation or invalid state: Inspection requires an active recording."),
            Protocol.Operations.RecordInspect => Answer(Protocol.States.Inspected, inspect: State(marks) is var m ? new(++inspects, m.Hash, m.AfterSequence, 0, 0, "", 80, 25, null, 1) : null),
            _ => Answer(Protocol.States.Recording, Batch()),
        });
        await using var session = Session(transport, Full);
        session.SampleInspections = true;
        marks.Add(Mark(1, 0, 11));
        await session.PollOnceAsync();                       // first seen: candidate, not yet sampled
        Assert.Equal(0, inspects);
        await session.PollOnceAsync();                       // still latest after a whole poll: one sample
        Assert.Equal(1, inspects);
        await session.PollOnceAsync();                       // already sampled: never again
        Assert.Equal(1, inspects);
        for (int i = 2; i < 6; i++) { marks.Add(Mark(i, i, (ulong)(10 + i))); await session.PollOnceAsync(); } // a burst: changes every poll
        Assert.Equal(1, inspects);
        marks.Add(Mark(6, 6, 99, kind: Protocol.BoundaryKinds.Pick) with { Cause = Protocol.BoundaryCauses.Pick }); // a pick anchor is no state
        await session.PollOnceAsync();
        Assert.Equal(2, inspects);                           // boundary 5 stayed the latest STATE for a poll
        var sample = Assert.Single(session.Capture.Inspections, s => s.Hash == 15);
        Assert.Equal(5, sample.AfterSequence);
        marks.Add(Mark(7, 7, 77, modal: "TDialog", snapshot: 1));  // transient with its own snapshot: never re-sampled
        await session.PollOnceAsync(); await session.PollOnceAsync();
        Assert.Equal(2, inspects);
        marks.Add(Mark(8, 8, 88)); refuseInspect = true;
        await session.PollOnceAsync(); await session.PollOnceAsync();  // a refusal is not a target failure
        Assert.Equal(RecordingState.Recording, session.State);
        Assert.Equal(2, session.Capture.Inspections.Count);

        // Off by default: no RecordInspect at all.
        var quiet = new Transport((op, _, _) => op == Protocol.Operations.RecordPoll ? Answer(Protocol.States.Recording, Batch(marks: [Mark(1, 0, 1)])) : throw new InvalidOperationException(op));
        await using var off = Session(quiet, Full);
        await off.PollOnceAsync(); await off.PollOnceAsync(); await off.PollOnceAsync();
        Assert.DoesNotContain(Protocol.Operations.RecordInspect, quiet.Calls);
    }

    [Fact]
    public void InspectionSamplesAreBoundedAndClearedAndNeverAffectCoverage()
    {
        var timeline = new RecordingCaptureTimeline(); timeline.Negotiate(Full);
        timeline.Merge(Batch(marks: [Mark(1, 0, 1)]));
        bool complete = timeline.CoverageComplete;
        for (int i = 0; i < RecordingCaptureTimeline.MaximumInspections + 3; i++) timeline.AddInspection(new(i, (ulong)i, 0, 0, 0, "", 80, 25, null, 1));
        Assert.Equal(RecordingCaptureTimeline.MaximumInspections, timeline.Inspections.Count);
        Assert.Equal(3, timeline.InspectionsLost);
        Assert.Equal(complete, timeline.CoverageComplete);
        timeline.Clear();
        Assert.Empty(timeline.Inspections);
        var legacy = new RecordingCaptureTimeline(); legacy.Negotiate([]);
        Assert.False(legacy.AddInspection(new(1, 1, 0, 0, 0, "", 80, 25, null, 1)));
    }

    // ── analysis facade ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnalysisOfALegacyRecordingIsInputOnlyWithAClearSummary()
    {
        RecordedInput[] events = [KeyAt(1, Keys.kbF3), KeyAt(2, Keys.kbEsc)];
        var conversion = RecordingConverter.Convert(events, 80, 25);
        var timeline = new RecordingCaptureTimeline(); timeline.Negotiate([]);
        var analysis = SmartRecordingAnalyzer.Analyze(events, timeline, conversion, 80, 25, final: true, []);
        Assert.Empty(analysis.Plan.Proposals);
        Assert.Empty(analysis.Picks);
        Assert.Contains("unavailable", Assert.Single(analysis.Summary));
        Assert.Equal(conversion.Steps.Select(s => s.Step.ToString()), analysis.Plan.Steps.Select(s => s.Step.ToString()));
    }

    [Fact]
    public void AnalysisNeverChangesTheConversionUsedByAppendInsertReplace()
    {
        RecordedInput[] events = [KeyAt(1, Keys.kbF3), KeyAt(2, 0x1e61, "a"), KeyAt(3, 0x1e62, "b"), KeyAt(4, Keys.kbEnter)];
        var conversion = RecordingConverter.Convert(events, 80, 25);
        string before = string.Join("|", conversion.Steps.Select(s => s.Step + "@" + s.First + ".." + s.Last));
        var timeline = new RecordingCaptureTimeline(); timeline.Negotiate(Full);
        timeline.Merge(Batch(marks: [Mark(1, 0, 1), Mark(2, 1, 2, modal: "TDialog"), Mark(3, 2, 3, modal: "TDialog"), Mark(4, 4, 4)]));
        var analysis = SmartRecordingAnalyzer.Analyze(events, timeline, conversion, 80, 25, final: true, ["initial"]);
        Assert.Equal(before, string.Join("|", conversion.Steps.Select(s => s.Step + "@" + s.First + ".." + s.Last)));
        Assert.NotEmpty(analysis.Summary);
        Assert.All(analysis.Plan.Proposals, p => Assert.NotEqual("initial", p.CheckpointId));
        Assert.All(analysis.Plan.Proposals, p => Assert.True(p.Step is null || p.Step.Kind == "checkpoint"));
    }

    // ── window (logical captures): Smart capture OFF keeps the legacy layout; ON adds only the Pick button ────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task RecordingWindowSmartCaptureCaptures(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);

                // OFF (default): no Pick button.
                WorkbenchRecorder.SmartCaptureOverride = false;
                await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                var legacy = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                Assert.Equal(9, await Invoke(bridge, () => Buttons(legacy).Count));
                await Shot(bridge, "smart-off-ready");
                await bridge.Invoke(() => legacy.Execute(WorkbenchRecordingWindow.DiscardCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);

                // ON: the Pick button sits in the session row after Cancel, fully inside the window.
                WorkbenchRecorder.SmartCaptureOverride = true;
                await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                var window = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                var buttons = await Invoke(bridge, () => Buttons(window));
                Assert.Equal(11, buttons.Count); // Pick (session row) and Review... (review row)
                var pick = buttons.Single(b => b.Title?.Contains("Pic~k~", StringComparison.Ordinal) == true);
                var cancel = buttons.Single(b => b.Title?.Contains("~C~ancel", StringComparison.Ordinal) == true);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(cancel.origin.y, pick.origin.y);
                    Assert.True(pick.origin.x >= cancel.origin.x + cancel.size.x);
                    Assert.True(pick.origin.x + pick.size.x <= window.size.x - 1);
                    Assert.False(TView.CommandEnabled(WorkbenchRecordingWindow.PickCommand)); // Ready: nothing to pick yet
                });
                await Shot(bridge, "smart-on-ready");

                var transport = new Transport((op, _, _) => Answer(Protocol.States.Recording, Batch(pickState: op == Protocol.Operations.RecordPickArm ? Protocol.PickStates.Armed : null)));
                var live = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Recording, [Key(Keys.kbF3)], transport.Send);
                live.Capture.Negotiate(Full);
                await bridge.Invoke(() => window.Attach(live));
                await bridge.Invoke(() => Assert.True(TView.CommandEnabled(WorkbenchRecordingWindow.PickCommand)));
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.PickCommand));
                await Wait(bridge, () => live.Capture.PickState == Protocol.PickStates.Armed ? "" : null);
                Assert.Contains("PICK MODE", await Text(bridge, window));
                await Shot(bridge, "smart-on-pick-armed");

                RecordedInput[] events = [KeyAt(1, Keys.kbF3), KeyAt(2, 0x1e61, "a"), KeyAt(3, Keys.kbEnter)];
                var stopped = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Stopped, events);
                stopped.Capture.Negotiate(Full);
                stopped.Capture.Merge(Batch(marks: [Mark(1, 0, 1), Mark(2, 1, 2, modal: "TDialog"), Mark(3, 2, 3, modal: "TDialog"), Mark(4, 3, 4)]));
                await bridge.Invoke(() => window.Attach(stopped));
                string review = await Text(bridge, window);
                Assert.Contains("Smart capture:", review);
                Assert.NotNull(window.Analysis);
                await Shot(bridge, "smart-on-review");
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.DiscardCommand));
            });
        }
        finally { WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root); }
    }

    // ── physical: Win32ConsoleDriver in its own conhost (needs TSWB_PRODUCT_TARGET = a ProxyOrdinary prepared with this build) ──

    private static string? Target => Environment.GetEnvironmentVariable("TSWB_PRODUCT_TARGET") is { Length: > 0 } t && File.Exists(t) ? t : null;
    public sealed class ConsoleTargetFactAttribute : FactAttribute
    {
        public ConsoleTargetFactAttribute() { if (Target is null) Skip = "Set TSWB_PRODUCT_TARGET to a ProxyOrdinary.exe prepared with this build's Workbench Proxy."; }
    }

    [ConsoleTargetFact]
    public async Task ConsoleCtrlShiftAndPickModeGesturesAreSwallowedAndAnalyzedOnTheRealConsole()
    {
        await using var session = new RecordingSession(Target!, "Win32ConsoleDriver", "80x25") { ModifierPicks = true, SampleInspections = true };
        await session.StartAsync();
        Assert.True(session.State == RecordingState.Recording, session.Failure);
        Assert.True(session.PicksAvailable);
        int pid = session.ProcessId!.Value;
        // Ctrl+Shift click on the counter label, Ctrl+Shift drag over the window: the application must see no mouse event at all.
        ConsoleInput(pid, "x,6,5,24;d,3,4,20,7,24");
        await UntilPicks(session, 2);
        var after = await session.InspectAsync();
        Assert.NotNull(after);
        string screen = Screen(ScreenSnapshot.Deserialize(Convert.FromBase64String(after.Cells)));
        Assert.Contains("Count: 0", screen);
        int seen = int.Parse(screen[(screen.IndexOf("Events: ", StringComparison.Ordinal) + 8)..][..4], System.Globalization.NumberStyles.HexNumber);
        Assert.True((seen & 0x0F) == 0, $"The application observed mouse events (Events: {seen:X4}).");
        // F3 opens the modal; a Ctrl+Shift click on its OK button must NOT press it (the dialog stays open).
        ConsoleInput(pid, "k,114,61;x,20,9,24");
        await UntilPicks(session, 3);
        var modal = await session.InspectAsync();
        Assert.Contains("Ordinary modal", Screen(ScreenSnapshot.Deserialize(Convert.FromBase64String(modal!.Cells))));
        // An ordinary click on the same cell right after the swallowed one closes it, and is not a double click.
        ConsoleInput(pid, "c,20,9");
        await UntilAsync(() => session.Events.Any(e => e.What == Events.evMouseUp), 20);
        // Pick Mode: a plain drag (no modifiers) is a region pick; then F2 still counts.
        Assert.True(await session.ArmPickAsync(), session.PickRefused);
        ConsoleInput(pid, "d,4,5,12,5,0;k,113,60");
        await UntilPicks(session, 4);
        await UntilAsync(() => session.Events.Count(e => e.KeyCode == Keys.kbF2) >= 1, 20);
        await session.StopAsync();
        Assert.Equal(RecordingState.Stopped, session.State);
        string end = Screen(session.EndScreen!);
        Assert.True(end.Contains("Count: 1", StringComparison.Ordinal), end + " | " + string.Join(" ", session.Events.Select(RecordingConverter.Describe)));
        var mouse = session.Events.Where(e => e.Kind == "mouse").ToArray();
        Assert.Equal([(Events.evMouseDown, 20, 9), (Events.evMouseUp, 20, 9)], mouse.Select(e => (e.What, e.X, e.Y)));
        Assert.Equal(0u, mouse[0].EventFlags & Events.meDoubleClick);
        var picks = session.Capture.Boundaries.Where(b => b.Kind == Protocol.BoundaryKinds.Pick).ToArray();
        Assert.Equal([(Protocol.PickKinds.Control, Protocol.PickTriggers.CtrlShift), (Protocol.PickKinds.Region, Protocol.PickTriggers.CtrlShift),
            (Protocol.PickKinds.Control, Protocol.PickTriggers.CtrlShift), (Protocol.PickKinds.Region, Protocol.PickTriggers.PickMode)],
            picks.Select(b => (b.Pick!.Kind, b.Pick.Trigger)));
        Assert.Contains("TDialog", picks[2].ModalTop);
        var conversion = RecordingConverter.Convert(session.Events, session.Width, session.Height);
        Assert.Empty(conversion.Mismatches);
        Assert.Equal(["key", "mouse", "key"], conversion.Steps.Select(s => s.Step.Kind));
        var analysis = session.Analyze(conversion, ["initial"]);
        Log(session, conversion, analysis);
        Assert.Equal(4, analysis.Picks.Count);
        var ok = analysis.Picks[2];
        Assert.Equal(EvidenceLevels.Snapshot, ok.Evidence);
        Assert.Contains(ok.Selectors, c => c.Unique && c.Selector.Path is null);
        Assert.All(analysis.Picks.SelectMany(p => p.Selectors), c => Assert.Null(c.Selector.Path));
        Assert.Equal(analysis.Plan.Proposals.Select(p => p.CheckpointId).Distinct().Count(), analysis.Plan.Proposals.Count);
        Assert.True(session.TargetExited);
    }

    private static void Log(RecordingSession session, RecordingConversion conversion, SmartRecordingAnalysis analysis)
    {
        if (Evidence is not { } root) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "smart-console-physical.txt"), string.Join("\n", new[]
        {
            "steps: " + string.Join(" | ", conversion.Steps.Select(s => $"{s.Step.Kind} {s.Step.Parameters} [{s.First}..{s.Last}]")),
            "boundaries: " + string.Join(" | ", session.Capture.Boundaries.Select(b => $"#{b.Id} {b.Kind}/{b.Cause} after={b.AfterSequence} modal={b.ModalTop} menu={b.MenuDepth} snap={b.SnapshotId}"
                + (b.Pick is { } p ? $" pick={p.Kind}/{p.Trigger} {p.Region} anchor={p.AnchorBoundaryId} hit={p.HitPath}" : ""))),
            "inspections: " + session.Capture.Inspections.Count + ", coverage complete: " + session.Capture.CoverageComplete + ", coverage: " + session.Capture.Coverage,
            "picks: " + string.Join(" | ", analysis.Picks.Select(p => $"{p.PickId} {p.Kind} {p.Region} {p.Evidence} target={p.Target?.Area}:{p.Target?.Type}:{p.Target?.Text} "
                + "selectors=" + string.Join(",", p.Selectors.Select(c => $"{c.Selector}({c.Basis},{c.Matches})")) + " flags=" + string.Join(",", p.Flags))),
            "proposals: " + string.Join(" | ", analysis.Plan.Proposals.Select(p => $"{p.CheckpointId} after={p.InsertAfterStep} {p.Placement} {p.Kind} {p.Confidence} {p.Evidence} picks={p.Picks.Count} review={p.NeedsReview} flags={string.Join(",", p.Flags)}")),
            "summary: " + string.Join(" / ", analysis.Summary),
        }));
    }

    private static Task UntilPicks(RecordingSession session, int count) =>
        UntilAsync(() => session.Capture.Boundaries.Count(b => b.Kind == Protocol.BoundaryKinds.Pick) >= count, 20);
    private static async Task UntilAsync(Func<bool> condition, int seconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Condition not reached."); await Task.Delay(50); }
    }

    // Separate process: attaches to the TARGET's console and writes ordinary INPUT_RECORDs (mouse with dwControlKeyState).
    // Tokens: k,vk,scan = key press; c,x,y = hover move + click; x,x,y,state = click (no hover first) with control key state;
    // d,x1,y1,x2,y2,state = press, two button-held moves, release (no hover first): every gesture record is part of the gesture.
    private const string InjectorScript = """
        param([int]$ProcessId, [string]$Plan)
        $ErrorActionPreference = 'Stop'
        Add-Type -TypeDefinition @'
        using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Threading;
        public static class SmartRecordingConsoleInput {
            [StructLayout(LayoutKind.Explicit)] public struct Rec {
                [FieldOffset(0)] public ushort Type;
                [FieldOffset(4)] public int Down; [FieldOffset(8)] public ushort Repeat; [FieldOffset(10)] public ushort Vk; [FieldOffset(12)] public ushort Scan; [FieldOffset(14)] public ushort Ch; [FieldOffset(16)] public uint KeyState;
                [FieldOffset(4)] public short X; [FieldOffset(6)] public short Y; [FieldOffset(8)] public uint Buttons; [FieldOffset(12)] public uint MouseState; [FieldOffset(16)] public uint Flags; }
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteConsoleInputW(IntPtr h, Rec[] r, uint n, out uint w);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
            static Rec Key(bool down, ushort vk, ushort scan) { var r = new Rec(); r.Type = 1; r.Down = down ? 1 : 0; r.Repeat = 1; r.Vk = vk; r.Scan = scan; return r; }
            static Rec Mouse(int x, int y, uint buttons, uint flags, uint state) { var r = new Rec(); r.Type = 2; r.X = (short)x; r.Y = (short)y; r.Buttons = buttons; r.Flags = flags; r.MouseState = state; return r; }
            public static int Run(uint pid, string plan) {
                FreeConsole();
                if (!AttachConsole(pid)) return 10;
                IntPtr h = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) return 11;
                try {
                    foreach (string token in plan.Split(';')) {
                        string[] p = token.Split(',');
                        int[] n = Array.ConvertAll(p, s => { int v; return int.TryParse(s, out v) ? v : 0; });
                        var recs = new List<Rec>();
                        if (p[0] == "k") { recs.Add(Key(true, (ushort)n[1], (ushort)n[2])); recs.Add(Key(false, (ushort)n[1], (ushort)n[2])); }
                        else if (p[0] == "c") { recs.Add(Mouse(n[1], n[2], 0, 1, 0)); recs.Add(Mouse(n[1], n[2], 1, 0, 0)); recs.Add(Mouse(n[1], n[2], 0, 0, 0)); }
                        else if (p[0] == "x") { uint s = (uint)n[3]; recs.Add(Mouse(n[1], n[2], 1, 0, s)); recs.Add(Mouse(n[1], n[2], 0, 0, s)); }
                        else if (p[0] == "d") { uint s = (uint)n[5]; recs.Add(Mouse(n[1], n[2], 1, 0, s));
                            recs.Add(Mouse((n[1] + n[3]) / 2, (n[2] + n[4]) / 2, 1, 1, s)); recs.Add(Mouse(n[3], n[4], 1, 1, s)); recs.Add(Mouse(n[3], n[4], 0, 0, s)); }
                        else return 12;
                        uint written;
                        if (!WriteConsoleInputW(h, recs.ToArray(), (uint)recs.Count, out written) || written != recs.Count) return 13;
                        Thread.Sleep(250);
                    }
                    return 0;
                } finally { CloseHandle(h); FreeConsole(); }
            }
        }
        '@
        exit [SmartRecordingConsoleInput]::Run([uint32]$ProcessId, $Plan)
        """;

    internal static void ConsoleInput(int pid, string plan)
    {
        string script = Path.Combine(Path.GetTempPath(), "smart-console-input.ps1");
        File.WriteAllText(script, InjectorScript);
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-ProcessId", pid.ToString(), "-Plan", plan }) start.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(start)!;
        string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60000), "Console input helper did not finish.");
        Assert.True(p.ExitCode == 0, $"Console input helper failed ({p.ExitCode}): {err}");
    }

    private static List<TButton> Buttons(TGroup group)
    {
        var result = new List<TButton>();
        group.ForEachView(v => { if (v is TButton b) result.Add(b); });
        return result;
    }

    // ── harness (copied pattern of RecorderTests) ─────────────────────────────────────────────────────────

    private static string SavedDocument(string root)
    {
        string path = Path.Combine(root, "docs", "recorded-" + Guid.NewGuid().ToString("N")[..6] + ".scenario.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saved = ScenarioDocument.New("recorded/demo", "Recorded demo", "Recorder window capture document.", checkpoint: "initial").Save(path);
        Assert.True(saved.Saved, saved.Message);
        return path;
    }
    private static string Row(ScreenSnapshot s, int y) => new(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character == 0 ? ' ' : s[x, y].Character).ToArray());
    private static string Screen(ScreenSnapshot s) => string.Join("\n", Enumerable.Range(0, s.Height).Select(y => Row(s, y).TrimEnd()));
    private static string SaveProject(string folder, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, "recorder.twproj"); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "smart-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static async Task<string> Text(Bridge bridge, WorkbenchRecordingWindow window) { string text = ""; await bridge.Invoke(() => { window.UpdateView(); text = window.Text; }); return text; }
    private static async Task<T> Invoke<T>(Bridge bridge, Func<T> func) { T result = default!; await bridge.Invoke(() => result = func()); return result; }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            string target = TestWorkbenchHostTests.SampleTarget;
            using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { WorkbenchRecordingWindow.Active?.Close(); app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                    }).WaitAsync(TimeSpan.FromSeconds(30));
                }
            });
            AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(300)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }

    // Logical screenshots (NullDriver cell screen of the real WorkbenchApp, diagnostic bitmap font; not desktop-native).
    private static async Task Shot(Bridge bridge, string state)
    {
        await bridge.Invoke(() => { }); await Task.Delay(80); await bridge.Invoke(() => { });
        if (Evidence is not { } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root);
            var screen = ScreenSnapshot.CopyRoot();
            string name = $"smart-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllText(Path.Combine(root, name + ".logical.txt"), Screen(screen));
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(SmartRecordingTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
