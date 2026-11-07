using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// CheckpointDetector is a pure function over a capture timeline. Every test builds the timeline from
// RecordedInput/RecordedBoundary/RetainedSnapshot/RecordInspection records directly (no process, no UI) and checks, through
// Run(), that every state boundary lands in exactly one counter bucket and that a second run gives the identical result.
public sealed class CheckpointDetectorTests
{
    private const int W = 20, H = 5;
    private const string Dialog = "TSharpVision.TDialog", FileDialog = "TSharpVision.TFileDialog", MenuBar = "TSharpVision.TMenuBar";

    // ── builders ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static RecordedInput Key(long seq, ushort code, string? text = null, long ms = 0, uint state = 0, bool gap = false) =>
        new(seq, "backend-key", ms, "key", Events.evKeyDown, code, text, state, 0, 0, 0, 0, W, H, gap);
    private static RecordedInput Typed(long seq, char c, long ms = 0) => Key(seq, c, c.ToString(), ms);
    private static RecordedInput Mouse(long seq, ushort what, int x = 3, int y = 2, long ms = 0) =>
        new(seq, "backend-queue", ms, "mouse", what, 0, null, 0, x, y, what == Events.evMouseUp ? (byte)0 : (byte)1, 0, W, H);
    private static RecordedInput Resize(long seq, int w, int h, long ms = 0) =>
        new(seq, "backend-queue", ms, "resize", 0, 0, null, 0, 0, 0, 0, 0, w, h);

    private static RecordedBoundary B(long id, long after, ulong hash, string cause = Protocol.BoundaryCauses.Input, long ms = 0,
        string? modal = null, int menu = 0, long? snap = null, bool dropped = false, bool gap = false, string kind = Protocol.BoundaryKinds.Idle,
        int w = W, int h = H) =>
        new(id, kind, cause, after, after, id, hash, ms, w, h, modal, menu, gap, snap, dropped);
    private static RecordedBoundary Start(ulong hash = 1) => B(1, 0, hash, Protocol.BoundaryCauses.Start);
    private static RecordedBoundary App(long id, long after, ulong hash, long ms, string? modal = null, int menu = 0) =>
        B(id, after, hash, Protocol.BoundaryCauses.Application, ms, modal, menu);
    private static RecordedBoundary PickMark(long id, long after, ulong hash, long anchor) =>
        new(id, Protocol.BoundaryKinds.Pick, Protocol.BoundaryCauses.Pick, after, after, id, hash, 0, W, H, null, 0, SnapshotId: null)
        { Pick = new(Protocol.PickKinds.Control, Protocol.PickTriggers.PickMode, 1, 1, 1, 1, new(1, 1, 1, 1), anchor, null, 2) };

    private static RetainedSnapshot Snap(long id, long boundary, string reason, ulong hash, long after = 0, string? cells = null,
        CheckpointInspection? inspection = null, string? omitted = null) =>
        new(id, boundary, reason, after, after, boundary, hash, 0, cells ?? Cells(), W, H, inspection) { Omitted = omitted };

    private static RecordInspection Live(ulong hash, long after, string cells, CheckpointInspection? inspection = null) =>
        new(0, hash, after, after, 0, cells, W, H, inspection, 1);

    /// <summary>TVS1 base64 of a W x H screen; <paramref name="rows"/> fill rows from the top, <paramref name="attr"/> every cell.</summary>
    private static string Cells(byte attr = 0x07, params string[] rows)
    {
        var cells = new TScreenChar[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                cells[y * W + x] = new(y < rows.Length && x < rows[y].Length ? rows[y][x] : ' ', attr);
        return Convert.ToBase64String(new ScreenSnapshot(W, H, cells).Serialize());
    }

    private static ViewFlags Flags(bool focused = false) => new(true, true, focused, false, true, focused, false, false, true, false, 0, 0);
    private static ViewNode Node(string path, string type, string kind, string? text = null, params ViewNode[] children) =>
        new(path, null, type, kind, new(0, 0, W, H), new(0, 0, W, H), new(0, 0, W, H), Flags(), 0, text, null, null, children, 0);

    /// <summary>Inspection with an application root, a desktop, optionally a dialog, and a status line on the last row.</summary>
    private static CheckpointInspection Inspect(bool dialog = false, string focused = "0/0", string? hint = null)
    {
        var desktop = Node("0/0", "TSharpVision.TDesktop", "TDesktop", null,
            dialog ? [Node("0/0/0", Dialog, "TDialog", "Open")] : []);
        var root = Node("0", "Demo.App", "TApplication", null, desktop);
        var tree = new ViewTree(root, dialog ? 3 : 2, false, "front-to-back", new(0, 0, W, H), new(0, 0, W, H - 1),
            dialog ? "0/0/0" : null, focused);
        var status = new StatusState("TSharpVision.TStatusLine", "0/1", new(0, H - 1, W, 1), 0, 0, 0, 0xFFFF, hint, null, "none", [], null);
        return new(tree, null, status, []);
    }

    private static CaptureState Full => new(true, true, true, 0, 0, false, new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), null);

    private static DetectorInput In(RecordedInput[] events, RecordedBoundary[] marks, RetainedSnapshot[]? snaps = null,
        RecordInspection[]? samples = null, bool final = true, CaptureState? capture = null) =>
        new(events, marks, snaps ?? [], samples ?? [], capture ?? Full, final);

    /// <summary>Detect twice (determinism) and check the counter invariant.</summary>
    private static DetectionResult Run(DetectorInput input, DetectorOptions? options = null)
    {
        var first = CheckpointDetector.Detect(input, options);
        var second = CheckpointDetector.Detect(input, options);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        var c = first.Counters;
        Assert.Equal(c.StateBoundaries, c.Candidates + c.Typing + c.Unchanged + c.Unsettled + c.Periodic + c.Insignificant + c.Overflow);
        Assert.Equal(input.Boundaries.Count, c.StateBoundaries + c.PickBoundaries);
        Assert.Equal(first.Candidates.Count, c.Candidates);
        foreach (var candidate in first.Candidates)
        {
            Assert.Equal($"c-{candidate.BoundaryId}", candidate.Id);
            if (candidate.Evidence is not EvidenceLevels.Snapshot and not EvidenceLevels.Inspection) Assert.NotEmpty(candidate.Flags);
            if (candidate.Flags.Contains(CandidateFlags.CoverageIncomplete)) Assert.NotEqual(Confidences.High, candidate.Confidence);
        }
        return first;
    }

    // ── transients ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MenuOpenedAndCancelledKeepsItsOwnSnapshotAndTheCloseIsNotASecondState()
    {
        var r = Run(In([Key(1, Keys.kbF10), Key(2, Keys.kbEsc)],
            [Start(), B(2, 1, 2, menu: 1, modal: MenuBar, snap: 1), B(3, 2, 1)],
            [Snap(1, 2, Protocol.SnapshotReasons.MenuOpen, 2, 1)]));
        var c = Assert.Single(r.Candidates);
        Assert.Equal(("c-2", CandidateKinds.MenuOpen, Stabilities.InputEnded, 2L), (c.Id, c.Kind, c.Stability, c.EndSequence!.Value));
        Assert.Equal((1L, EvidenceLevels.Snapshot, Confidences.High), (c.SnapshotId!.Value, c.Evidence, c.Confidence));
        Assert.Equal(1, r.Counters.Unchanged); // the cancelled close returns to the pre-menu screen
    }

    [Fact]
    public void MenuWalkKeepsOpenAndTransientLastSnapshotsOnTheBoundariesTheyName()
    {
        // Alt+F opens the menu (snapshot 1 at boundary 2); Down, Down move the highlight (boundaries 3, 4); Enter opens a file
        // dialog (snapshot 3 at boundary 5) and commits transient-last snapshot 2 for boundary 4 at that moment. The dialog
        // is cancelled (Esc) back to the original screen. Snapshot 3 is never replaced by the later screen.
        var r = Run(In([Key(1, Keys.kbAltF), Key(2, Keys.kbDown), Key(3, Keys.kbDown), Key(4, Keys.kbEnter), Key(5, Keys.kbEsc)],
            [Start(), B(2, 1, 2, modal: MenuBar, menu: 2, snap: 1), B(3, 2, 3, modal: MenuBar, menu: 2), B(4, 3, 4, modal: MenuBar, menu: 2),
             B(5, 4, 5, modal: FileDialog, snap: 3), B(6, 5, 1)],
            [Snap(1, 2, Protocol.SnapshotReasons.MenuOpen, 2, 1), Snap(2, 4, Protocol.SnapshotReasons.TransientLast, 4, 3),
             Snap(3, 5, Protocol.SnapshotReasons.ModalOpen, 5, 4)]));
        Assert.Equal(["c-2", "c-4", "c-5"], r.Candidates.Select(c => c.Id));
        Assert.Equal([1L, 2L, 3L], r.Candidates.Select(c => c.SnapshotId!.Value));
        Assert.Equal([CandidateKinds.MenuOpen, CandidateKinds.MenuChange, CandidateKinds.ModalOpen], r.Candidates.Select(c => c.Kind));
        Assert.All(r.Candidates, c => Assert.Equal(EvidenceLevels.Snapshot, c.Evidence));
        Assert.Equal([2L, 4L, 5L], r.Candidates.Select(c => c.EndSequence!.Value));
        Assert.Equal((1, 1), (r.Counters.Insignificant - 1, r.Counters.Unchanged)); // highlight move; cancelled dialog (+ baseline)
    }

    [Fact]
    public void ModalClosedByTheApplicationBeforeSettleStaysATransientWithItsOwnSnapshot()
    {
        // F3 opens a dialog; the application closes it 40 ms later (posted event) and draws a new screen: the recorder's snapshot of
        // the dialog is the only evidence of it and stays attached to boundary 2.
        var r = Run(In([Key(1, Keys.kbF3, ms: 0), Key(2, Keys.kbF4, ms: 2000)],
            [Start(), B(2, 1, 2, ms: 10, modal: Dialog, snap: 7), App(3, 1, 3, 50)],
            [Snap(7, 2, Protocol.SnapshotReasons.ModalOpen, 2, 1)]));
        Assert.Equal(["c-2", "c-3"], r.Candidates.Select(c => c.Id));
        var modal = r.Candidates[0];
        Assert.Equal((CandidateKinds.ModalOpen, Stabilities.Transient, 7L, (long?)null),
            (modal.Kind, modal.Stability, modal.SnapshotId!.Value, modal.EndSequence));
        var closed = r.Candidates[1];
        Assert.Equal((CandidateKinds.ModalClose, Stabilities.Settled, Protocol.BoundaryCauses.Application, 2L),
            (closed.Kind, closed.Stability, closed.Cause, closed.EndSequence!.Value));
        Assert.Contains(CandidateFlags.Async, closed.Flags);
        Assert.Null(closed.SnapshotId);
    }

    [Fact]
    public void ModalSubmittedToANewScreenYieldsOpenAndClose()
    {
        var r = Run(In([Key(1, Keys.kbF3), Key(2, Keys.kbEnter)],
            [Start(), B(2, 1, 2, modal: Dialog, snap: 1), B(3, 2, 9)], [Snap(1, 2, Protocol.SnapshotReasons.ModalOpen, 2, 1)]));
        Assert.Equal([CandidateKinds.ModalOpen, CandidateKinds.ModalClose], r.Candidates.Select(c => c.Kind));
        Assert.Equal(Stabilities.Final, r.Candidates[1].Stability);
        Assert.Equal(Confidences.Medium, r.Candidates[1].Confidence); // hash-only
        Assert.Contains(CheckpointDetector.HashOnlyFlag, r.Candidates[1].Flags);
    }

    [Fact]
    public void ADialogFilledByTheApplicationRightAfterOpeningCarriesTheOpenKind()
    {
        var r = Run(In([Key(1, Keys.kbF3, ms: 0), Key(2, Keys.kbEsc, ms: 3000)],
            [Start(), B(2, 1, 2, ms: 5, modal: Dialog), App(3, 1, 3, 40, modal: Dialog), B(4, 2, 1)]));
        var c = Assert.Single(r.Candidates);
        Assert.Equal(("c-3", CandidateKinds.ModalOpen, Stabilities.Settled), (c.Id, c.Kind, c.Stability));
        Assert.Equal(1, r.Counters.Unsettled);
    }

    [Fact]
    public void APressedButtonLookInsideOneClickIsNotAState()
    {
        // Physical smoke: F3 opens a dialog; a click on OK (down #2, up #3) closes it. The pre-input boundary between press and
        // release shows the pressed button (same dialog signature; even committed a transient-last for it) - not a state.
        var r = Run(In([Key(1, Keys.kbF3), Mouse(2, Events.evMouseDown), Mouse(3, Events.evMouseUp), Key(4, Keys.kbF2)],
            [Start(), B(2, 1, 2, modal: Dialog, snap: 1), B(3, 2, 3, modal: Dialog, kind: Protocol.BoundaryKinds.PreInput), B(4, 3, 4), B(5, 4, 5)],
            [Snap(1, 2, Protocol.SnapshotReasons.ModalOpen, 2, 1), Snap(2, 3, Protocol.SnapshotReasons.TransientLast, 3, 2)]));
        Assert.Equal(["c-2", "c-4", "c-5"], r.Candidates.Select(c => c.Id));
        Assert.Equal([CandidateKinds.ModalOpen, CandidateKinds.ModalClose, CandidateKinds.Content], r.Candidates.Select(c => c.Kind));
        Assert.DoesNotContain(r.Candidates, c => c.AfterSequence == 2);
        Assert.Equal(2, r.Counters.Insignificant); // baseline + pressed look
    }

    [Theory]
    [InlineData(MenuBar, 1, CandidateKinds.MenuOpen)]
    [InlineData(Dialog, 0, CandidateKinds.ModalOpen)]
    public void ASignatureChangeAtTheMouseDownStaysACandidate(string modal, int menu, string kind)
    {
        var r = Run(In([Mouse(1, Events.evMouseDown, 2, 0), Mouse(2, Events.evMouseUp, 2, 0), Key(3, Keys.kbEsc)],
            [Start(), B(2, 1, 2, modal: modal, menu: menu, snap: 1, kind: Protocol.BoundaryKinds.PreInput), B(3, 2, 2, modal: modal, menu: menu),
             B(4, 3, 1)], [Snap(1, 2, menu > 0 ? Protocol.SnapshotReasons.MenuOpen : Protocol.SnapshotReasons.ModalOpen, 2, 1)]));
        var c = Assert.Single(r.Candidates);
        Assert.Equal(("c-2", kind, 2L, 1L, EvidenceLevels.Snapshot), (c.Id, c.Kind, c.EndSequence!.Value, c.SnapshotId!.Value, c.Evidence));
    }

    // ── typing, navigation, unrecorded input ───────────────────────────────────────────────────────────────────────────

    public static TheoryData<bool, bool, int, string?> TypingCases => new()
    {
        // final, enterAfter, candidates, kind of the single candidate
        { false, false, 0, null },
        { true, false, 1, CandidateKinds.Final },
        { false, true, 0, null }, // Enter's state is still showing in a live analysis: pending
        { true, true, 1, CandidateKinds.Content },
    };

    [Theory, MemberData(nameof(TypingCases))]
    public void ATypingBurstNeverFloodsCandidates(bool final, bool enterAfter, int expected, string? kind)
    {
        var events = new List<RecordedInput>();
        var marks = new List<RecordedBoundary> { Start() };
        for (int i = 1; i <= 10; i++)
        {
            events.Add(Typed(i, (char)('a' + i - 1), i * 30));
            marks.Add(B(i + 1, i, (ulong)(100 + i), ms: i * 30 + 5, kind: i % 2 == 0 ? Protocol.BoundaryKinds.PreInput : Protocol.BoundaryKinds.Idle));
        }
        if (enterAfter) { events.Add(Key(11, Keys.kbEnter, ms: 1000)); marks.Add(B(12, 11, 999, ms: 1005)); }
        var r = Run(In([.. events], [.. marks], final: final));
        Assert.Equal(expected, r.Candidates.Count);
        if (kind is not null) Assert.Equal(kind, r.Candidates[0].Kind);
        Assert.Equal(final && !enterAfter ? 9 : 10, r.Counters.Typing); // the end of the run became the final candidate
    }

    [Fact]
    public void TypingThatOpensAPopupWindowIsWarrantedByEvidenceAfterTheRun()
    {
        // Typing in an input line; the view-tree evidence of the last state shows a new dialog (autocomplete popup).
        var before = Cells(0x07, "name:");
        var after = Cells(0x07, "name: ab", "  [popup]");
        var r = Run(In([Typed(1, 'a'), Typed(2, 'b'), Key(3, Keys.kbF10)],
            [Start(), B(2, 1, 2), B(3, 2, 3)], samples: [Live(1, 0, before, Inspect()), Live(3, 2, after, Inspect(dialog: true))], final: false));
        var c = Assert.Single(r.Candidates);
        Assert.Equal(("c-3", CandidateKinds.Content, EvidenceLevels.Inspection, Confidences.High), (c.Id, c.Kind, c.Evidence, c.Confidence));
        Assert.Equal(1, r.Counters.Typing);
    }

    [Fact]
    public void NavigationAndUnrecordedHoverAreNotCheckpoints()
    {
        var r = Run(In([Key(1, Keys.kbTab), Key(2, Keys.kbDown), Key(3, Keys.kbF2)],
            [Start(), B(2, 1, 2), B(3, 2, 3), B(4, 2, 4 /* hover highlight: Delivered advanced, nothing recorded */), B(5, 3, 5)], final: false));
        Assert.Empty(r.Candidates);
        Assert.Equal((4, 1), (r.Counters.Insignificant, r.Counters.Unsettled)); // baseline, Tab, Down, hover; F2 pending
    }

    // ── evidence: noise vs meaning ─────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, bool, bool, string?> EvidenceCases => new()
    {
        // case, second state has a dialog, text changed outside status row, expected evidence (null = no candidate)
        { "status-hint-only", false, false, null },
        { "focus-attributes-only", false, false, null },
        { "dialog-appeared", true, true, EvidenceLevels.Inspection },
        { "text-changed", false, true, EvidenceLevels.Inspection },
    };

    [Theory, MemberData(nameof(EvidenceCases))]
    public void EvidenceTellsNoiseFromMeaningAfterACommandInput(string name, bool dialog, bool text, string? expected)
    {
        string before = Cells(0x07, "File Edit", "", "", "", "F1 Help");
        string after = name switch
        {
            "status-hint-only" => Cells(0x07, "File Edit", "", "", "", "F1 Help | Saves file"),
            "focus-attributes-only" => Cells(0x1F, "File Edit", "", "", "", "F1 Help"),
            _ => Cells(0x07, "File Edit", "Document 2", "", "", "F1 Help"),
        };
        var r = Run(In([Mouse(1, Events.evMouseDown), Mouse(2, Events.evMouseUp)], [Start(), B(2, 2, 2)],
            samples: [Live(1, 0, before, Inspect()), Live(2, 2, after, Inspect(dialog, focused: name == "focus-attributes-only" ? "0/0/1" : "0/0"))]));
        if (expected is null)
        {
            Assert.Empty(r.Candidates);
            Assert.Equal(2, r.Counters.Insignificant);
            Assert.False(text);
        }
        else
        {
            var c = Assert.Single(r.Candidates);
            Assert.Equal((CandidateKinds.Content, expected, Confidences.High), (c.Kind, c.Evidence, c.Confidence));
            Assert.Contains("evidence", c.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutEvidenceACommandChangeIsInferredAndSaysSo()
    {
        var r = Run(In([Key(1, Keys.kbF5)], [Start(), B(2, 1, 2)]));
        var c = Assert.Single(r.Candidates);
        Assert.Equal((EvidenceLevels.HashOnly, Confidences.Medium), (c.Evidence, c.Confidence));
        Assert.Contains("inferred from signature and input kind", c.Reason, StringComparison.Ordinal);
        Assert.Contains(r.Diagnostics, d => d.Contains("hash/signature only", StringComparison.Ordinal));
    }

    // ── async, settle, periodic ────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<long, string, string> AsyncCases => new()
    {
        // application change at ms, first (input) state outcome, async state stability
        { 500, "candidate:" + Stabilities.Settled, Stabilities.Settled },
        { 100, "unsettled", Stabilities.Settled },
    };

    [Theory, MemberData(nameof(AsyncCases))]
    public void InputAndApplicationCausesSettleDifferently(long asyncMs, string inputOutcome, string asyncStability)
    {
        var r = Run(In([Key(1, Keys.kbEnter, ms: 0), Key(2, Keys.kbF2, ms: 5000)],
            [Start(), B(2, 1, 2, ms: 10), App(3, 1, 3, asyncMs), B(4, 2, 4, ms: 5010)], final: false));
        var async = r.Candidates.Single(c => c.BoundaryId == 3);
        Assert.Equal((CandidateKinds.Async, asyncStability, 2L, Confidences.Low), (async.Kind, async.Stability, async.EndSequence!.Value, async.Confidence));
        Assert.Contains(CandidateFlags.Async, async.Flags);
        if (inputOutcome == "unsettled") { Assert.DoesNotContain(r.Candidates, c => c.BoundaryId == 2); Assert.Equal(2, r.Counters.Unsettled); }
        else Assert.Equal(Stabilities.Settled, r.Candidates.Single(c => c.BoundaryId == 2).Stability);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PeriodicTimerRedrawsYieldNoAsyncCandidates(bool final)
    {
        // F5 starts a clock/animation: 6 application redraws, 150 ms apart, never quiescent.
        var marks = new List<RecordedBoundary> { Start(), B(2, 1, 2, ms: 10) };
        for (int i = 0; i < 6; i++) marks.Add(App(3 + i, 1, (ulong)(50 + i), 160 + i * 150));
        var r = Run(In([Key(1, Keys.kbF5, ms: 0)], [.. marks], final: final));
        Assert.DoesNotContain(r.Candidates, c => c.Kind == CandidateKinds.Async);
        Assert.Equal(6, r.Counters.Periodic);
        Assert.Contains(r.Diagnostics, d => d.Contains("never-quiescent", StringComparison.Ordinal));
        if (final)
        {
            var c = Assert.Single(r.Candidates); // the F5 state, anchored at its input, low confidence
            Assert.Equal(("c-2", Stabilities.Final, Confidences.Low), (c.Id, c.Stability, c.Confidence));
            Assert.Contains(CheckpointDetector.NeverQuiescentFlag, c.Flags);
        }
        else Assert.Empty(r.Candidates);
    }

    [Fact]
    public void APeriodicStretchThatGoesQuietForAWindowBeforeInputKeepsItsLastState()
    {
        var marks = new List<RecordedBoundary> { Start(), B(2, 1, 2, ms: 10) };
        for (int i = 0; i < 4; i++) marks.Add(App(3 + i, 1, (ulong)(50 + i), 100 + i * 100)); // spinner, last frame = result at 400
        var r = Run(In([Key(1, Keys.kbF5, ms: 0), Key(2, Keys.kbEsc, ms: 3000)], [.. marks, B(7, 2, 1, ms: 3010)], final: false));
        var async = Assert.Single(r.Candidates, c => c.Kind == CandidateKinds.Async);
        Assert.Equal((6L, Stabilities.Settled), (async.BoundaryId, async.Stability));
        Assert.Equal(3, r.Counters.Periodic);
    }

    // ── resize, gap ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResizeIsOneCandidateAfterTheLastGridSizeAndIsFlagged()
    {
        var r = Run(In([Resize(1, 24, 6), Resize(2, 30, 8), Key(3, Keys.kbF2)],
            [Start(), B(2, 1, 2, w: 24, h: 6), B(3, 2, 3, w: 30, h: 8), B(4, 3, 4, w: 30, h: 8)], final: false));
        var c = Assert.Single(r.Candidates);
        Assert.Equal(("c-3", CandidateKinds.Resize, 30, 8), (c.Id, c.Kind, c.Width, c.Height));
        Assert.Contains(CandidateFlags.AfterResize, c.Flags);
        Assert.Equal(Confidences.Low, c.Confidence); // hash-only, lowered for the resize
    }

    [Fact]
    public void AfterGapIsFlaggedAndPausedInputStatesAreNotProposed()
    {
        var r = Run(In([Key(1, Keys.kbF2), Key(2, Keys.kbF3, gap: true)],
            [Start(), B(2, 1, 2, snap: null), B(3, 1, 3, gap: true /* changed by input made while paused */), B(4, 2, 4)]));
        Assert.DoesNotContain(r.Candidates, c => c.BoundaryId == 3);
        var c = r.Candidates.Single(c => c.BoundaryId == 4);
        Assert.True(c.AfterGap);
        Assert.Contains(CandidateFlags.AfterGap, c.Flags);
        Assert.Equal(Confidences.Low, c.Confidence);
    }

    // ── duplicates and picks ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreInputAndIdleViewsOfTheSameStateAreOneCandidate()
    {
        var r = Run(In([Key(1, Keys.kbF2), Key(2, Keys.kbF3)],
            [Start(), B(2, 1, 2), B(3, 1, 2, kind: Protocol.BoundaryKinds.PreInput), B(4, 2, 4)], final: false));
        var c = Assert.Single(r.Candidates);
        Assert.Equal((2L, 3L, 2L), (c.BoundaryId, c.LastBoundaryId, c.EndSequence!.Value));
        Assert.Equal(1, r.Counters.Unchanged);
    }

    [Fact]
    public void PickBoundariesAreAnchorsThatNeverEndOrSplitAState()
    {
        var r = Run(In([Key(1, Keys.kbF2), Key(2, Keys.kbF3)],
            [Start(), B(2, 1, 2), PickMark(3, 1, 77, anchor: 2), B(4, 1, 2, kind: Protocol.BoundaryKinds.PreInput), PickMark(5, 1, 2, anchor: 4),
             B(6, 2, 6)], [Snap(9, 3, Protocol.SnapshotReasons.Pick, 77, 1)]));
        Assert.Equal(["c-2", "c-6"], r.Candidates.Select(c => c.Id));
        Assert.Equal((4L, 2L), (r.Candidates[0].LastBoundaryId, r.Candidates[0].EndSequence!.Value));
        Assert.Null(r.Candidates[0].SnapshotId); // the pick's snapshot belongs to the pick boundary, never to the state
        Assert.Equal((2, 4), (r.Counters.PickBoundaries, r.Counters.StateBoundaries));
    }

    // ── degraded evidence and coverage ─────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string, string> DegradedCases => new()
    {
        { "dropped", EvidenceLevels.Missing, CandidateFlags.SnapshotDropped },
        { "not-fetched", EvidenceLevels.Missing, CandidateFlags.SnapshotNotFetched },
        { "omitted", EvidenceLevels.Missing, CandidateFlags.InspectionOmitted },
        { "incomplete", EvidenceLevels.Snapshot, CandidateFlags.CoverageIncomplete },
        { "recovered", EvidenceLevels.Missing, CandidateFlags.Recovered },
        { "partial-target", EvidenceLevels.HashOnly, CandidateFlags.CoverageIncomplete },
    };

    [Theory, MemberData(nameof(DegradedCases))]
    public void MissingDroppedOrOmittedSnapshotsDegradeEvidenceAndNeverRateHigh(string name, string evidence, string flag)
    {
        var capture = name switch
        {
            "incomplete" => Full with { CoverageComplete = false, Coverage = new(4, 0, 0, 1, 0, 0, 0, 0, 1, 0) },
            "recovered" => Full with { CoverageComplete = false, Recovered = true },
            "partial-target" => Full with { SnapshotsEnabled = false, CoverageComplete = false },
            _ => Full,
        };
        var mark = B(2, 1, 2, modal: Dialog, menu: 0, snap: name is "not-fetched" or "omitted" or "incomplete" ? 1 : null,
            dropped: name == "dropped");
        RetainedSnapshot[] snaps = name switch
        {
            "omitted" => [Snap(1, 2, Protocol.SnapshotReasons.ModalOpen, 2, 1, omitted: Protocol.SnapshotOmissions.InspectionSize)],
            "incomplete" => [Snap(1, 2, Protocol.SnapshotReasons.ModalOpen, 2, 1, inspection: Inspect(dialog: true))],
            _ => [],
        };
        var r = Run(In([Key(1, Keys.kbF3)], [Start(), mark], snaps, capture: capture));
        var c = Assert.Single(r.Candidates);
        Assert.Equal(evidence, c.Evidence);
        Assert.Contains(flag, c.Flags);
        Assert.NotEqual(Confidences.High, c.Confidence);
        if (name == "omitted") Assert.Equal(1L, c.SnapshotId); // still referenced: cells were fetched
        if (name is "incomplete" or "recovered" or "partial-target") Assert.Contains(r.Diagnostics, d => d.Contains("incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void ALegacyTargetYieldsNoCandidatesAndOneDiagnostic()
    {
        var r = Run(In([Key(1, Keys.kbF2)], [], capture: CaptureState.Legacy with { Diagnostic = "predates Smart Recorder capture" }));
        Assert.Empty(r.Candidates);
        Assert.Contains("predates", Assert.Single(r.Diagnostics), StringComparison.Ordinal);
    }

    // ── final vs live, empty, overflow, determinism ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, Stabilities.Final, 1)]
    [InlineData(false, null, 0)]
    public void TheStateOpenAtStopIsFinalOnlyAfterStop(bool final, string? stability, int count)
    {
        var r = Run(In([Key(1, Keys.kbF2)], [Start(), B(2, 1, 2)], final: final));
        Assert.Equal(count, r.Candidates.Count);
        if (stability is not null) Assert.Equal((stability, (long?)null), (r.Candidates[0].Stability, r.Candidates[0].EndSequence));
        else { Assert.Equal(1, r.Counters.Unsettled); Assert.Contains(r.Diagnostics, d => d.Contains("live analysis", StringComparison.Ordinal)); }
    }

    [Fact]
    public void EmptyInputYieldsNothing()
    {
        var r = Run(In([], []));
        Assert.Empty(r.Candidates);
        Assert.Equal(new DetectorCounters(0, 0, 0, 0, 0, 0, 0, 0, 0), r.Counters);
        Assert.Single(r.Diagnostics);
    }

    [Fact]
    public void OverflowIsCountedAndReportedNeverSilentlyDropped()
    {
        var events = Enumerable.Range(1, 5).Select(i => Key(i, Keys.kbF2)).ToArray();
        var marks = new[] { Start() }.Concat(Enumerable.Range(1, 5).Select(i => B(i + 1, i, (ulong)(10 + i)))).ToArray();
        var r = Run(In(events, marks), new DetectorOptions(MaximumCandidates: 2));
        Assert.Equal(["c-2", "c-3"], r.Candidates.Select(c => c.Id));
        Assert.Equal(3, r.Counters.Overflow);
        Assert.Contains(r.Diagnostics, d => d.Contains("cap of 2", StringComparison.Ordinal) && d.Contains("boundary 4", StringComparison.Ordinal));
    }

    [Fact]
    public void ResultIsIndependentOfInputListOrder()
    {
        RecordedInput[] events = [Key(1, Keys.kbF10), Key(2, Keys.kbEnter), Key(3, Keys.kbF5)];
        RecordedBoundary[] marks = [Start(), B(2, 1, 2, modal: MenuBar, menu: 1, snap: 1), B(3, 2, 3, modal: Dialog, snap: 2), B(4, 3, 4)];
        RetainedSnapshot[] snaps = [Snap(1, 2, Protocol.SnapshotReasons.MenuOpen, 2, 1), Snap(2, 3, Protocol.SnapshotReasons.ModalOpen, 3, 2)];
        var a = Run(In(events, marks, snaps));
        var b = Run(In([.. events.Reverse()], [.. marks.Reverse()], [.. snaps.Reverse()]));
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        Assert.Equal([CandidateKinds.MenuOpen, CandidateKinds.ModalOpen, CandidateKinds.ModalClose], a.Candidates.Select(c => c.Kind));
    }
}
