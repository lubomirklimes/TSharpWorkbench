using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Pure checkpoint planner. Inputs are realistic: RecordedInput sequences converted with the product's
// RecordingConverter.Convert; candidates and picks are built by hand. No process, no driver.
public sealed class CheckpointPlannerTests
{
    private const ushort CharCode = 0x1e00;

    /// <summary>Numbered recording (Sequence 1..n), converted exactly as the product does.</summary>
    private sealed class Rec
    {
        internal readonly List<RecordedInput> Inputs = [];
        internal long Seq => Inputs.Count;
        private RecordedInput Add(Func<long, RecordedInput> make) { var e = make(Inputs.Count + 1); Inputs.Add(e); return e; }
        internal Rec Key(ushort code, uint state = 0, bool gap = false)
        { Add(n => new(n, "backend-key", n * 10, "key", Events.evKeyDown, code, null, state, 0, 0, 0, 0, 80, 25, gap)); return this; }
        internal Rec Text(string text, bool gap = false)
        {
            foreach (char c in text) { bool g = gap; gap = false; Add(n => new(n, "backend-key", n * 10, "key", Events.evKeyDown, (ushort)(CharCode | c), c.ToString(), 0, 0, 0, 0, 0, 80, 25, g)); }
            return this;
        }
        internal Rec Mouse(ushort what, int x, int y, byte buttons = 0, uint flags = 0)
        { Add(n => new(n, "backend-queue", n * 10, "mouse", what, 0, null, 0, x, y, buttons, flags, 80, 25)); return this; }
        internal Rec Down(int x, int y, uint flags = 0) => Mouse(Events.evMouseDown, x, y, 1, flags);
        internal Rec Up(int x, int y) => Mouse(Events.evMouseUp, x, y);
        internal Rec Move(int x, int y, byte buttons = 0) => Mouse(Events.evMouseMove, x, y, buttons);
        internal Rec Click(int x, int y) => Down(x, y).Up(x, y);
        internal Rec Wheel(int x, int y, int count) { for (int i = 0; i < count; i++) Mouse(Events.evMouseWheel, x, y, 0, Events.meWheelDown); return this; }
        internal Rec Grid(int w, int h) { Add(n => new(n, "backend-queue", n * 10, "resize", Events.evCommand, 0, null, 0, 0, 0, 0, 0, w, h)); return this; }
        internal RecordingConversion Convert() => RecordingConverter.Convert(Inputs, 80, 25);
    }

    private static StableChangeCandidate Cand(long boundary, long after, ulong hash, string kind = CandidateKinds.Content, string evidence = EvidenceLevels.Snapshot,
        string confidence = Confidences.High, long? last = null, bool gap = false, string[]? flags = null, string? id = null, string stability = Stabilities.InputEnded) =>
        new(id ?? "c-" + boundary, boundary, last ?? boundary, after, null, Protocol.BoundaryCauses.Input, kind, stability, evidence, confidence,
            kind + " changed", hash, boundary, 80, 25, null, 0, evidence == EvidenceLevels.Snapshot ? boundary : null, gap,
            flags ?? (evidence == EvidenceLevels.Snapshot ? [] : [CandidateFlags.SnapshotNotFetched]));

    private static PickSelection Pick(long id, long anchor, long after, ulong hash, string evidence = EvidenceLevels.Snapshot) =>
        new(id, Protocol.PickKinds.Control, Protocol.PickTriggers.CtrlShift, after, anchor, hash, id, 80, 25, new CellRect(2, 3, 1, 1),
            evidence == EvidenceLevels.Missing ? null : id, evidence, null, [], null, evidence == EvidenceLevels.Missing ? [CandidateFlags.SnapshotDropped] : []);

    private static CheckpointPlan Plan(Rec rec, RecordingConversion conversion, IEnumerable<StableChangeCandidate>? candidates = null,
        IEnumerable<PickSelection>? picks = null, IEnumerable<string>? existing = null, PlannerOptions? options = null)
    {
        var plan = CheckpointPlanner.Plan(new(rec.Inputs, conversion, 80, 25, (candidates ?? []).ToArray(), (picks ?? []).ToArray(),
            (existing ?? []).ToArray()), options);
        AssertSound(rec, conversion, plan);
        return plan;
    }

    private static ScenarioDocumentStep[] Docs(IEnumerable<RecordedStep> steps) => steps.Select(s => s.Step).ToArray();

    /// <summary>Invariants of every plan: identical replay of Steps, causal placement, valid unique ids, v1 checkpoint steps.</summary>
    private static void AssertSound(Rec rec, RecordingConversion conversion, CheckpointPlan plan)
    {
        Assert.Equal(CheckpointPlanner.Stream(Docs(conversion.Steps), 80, 25), CheckpointPlanner.Stream(Docs(plan.Steps), 80, 25));
        Assert.Equal(conversion.Mismatches, RecordingConverter.SelfCheck(Docs(plan.Steps), rec.Inputs, 80, 25));
        Assert.Equal(plan.StepsSplit, plan.Steps.Count != conversion.Steps.Count);
        int trailing = CheckpointPlanner.TrailingSynthetic(plan.Steps);
        foreach (var p in plan.Proposals)
        {
            Assert.True(Protocol.IsValidName(p.CheckpointId), p.CheckpointId);
            if (p.Placement == Placements.Unrepresentable) { Assert.Null(p.Step); Assert.True(p.NeedsReview); continue; }
            Assert.NotNull(p.Step);
            Assert.Equal("checkpoint", p.Step!.Kind);
            Assert.Equal(p.CheckpointId, p.Step.String("id"));
            Assert.Equal(0, p.Step.AssertionCount);
            Assert.Equal(p.InsertAfterStep < 0 ? Placements.BeforeFirst : p.Placement, p.Placement);
            for (int i = 0; i < trailing; i++)
            {
                if (plan.Steps[i].First <= 0) continue;
                if (i <= p.InsertAfterStep) Assert.True(plan.Steps[i].Last <= p.AfterSequence, $"step {i} replays input after the state of {p.Id}");
                else Assert.True(plan.Steps[i].First > p.AfterSequence, $"step {i} replays input before the state of {p.Id}");
            }
        }
        Assert.Equal(plan.Proposals.Count, plan.Proposals.Select(p => p.CheckpointId).Distinct().Count());
        Assert.Equal(plan.Proposals.Count, plan.Proposals.Select(p => p.Id).Distinct().Count());
    }

    private static string Describe(CheckpointPlan plan) => string.Join("\n", Docs(plan.Steps).Select(s => s.ToString()))
        + "\n" + string.Join("\n", plan.Proposals.Select(p => $"{p.Id} {p.CheckpointId} {p.InsertAfterStep} {p.Placement} {p.Kind} {p.Confidence} {p.Evidence} "
            + $"{p.AfterSequence} {p.BoundaryId} {p.SnapshotId} [{string.Join(',', p.CandidateIds)}] [{string.Join(',', p.Picks.Select(x => x.PickId))}] {p.NeedsReview} "
            + $"[{string.Join(',', p.Flags)}] {p.Step}")) + "\n" + string.Join("\n", plan.Diagnostics);

    // ── basics ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyInputYieldsNoProposalsAndTheConversionUnchanged()
    {
        var rec = new Rec();
        var c = rec.Convert();
        var plan = Plan(rec, c);
        Assert.Empty(plan.Proposals);
        Assert.Same(c.Steps, plan.Steps);
        Assert.False(plan.StepsSplit);

        var only = Plan(rec, c, [Cand(1, 0, 7)]);
        var p = Assert.Single(only.Proposals);
        Assert.Equal((-1, Placements.BeforeFirst), (p.InsertAfterStep, p.Placement));
    }

    [Theory]
    [InlineData(0, -1, Placements.BeforeFirst)]  // initial state: before the first step
    [InlineData(1, 0, Placements.AfterStep)]     // after kbF2
    [InlineData(3, 1, Placements.AfterStep)]     // after the whole text run "ab"
    [InlineData(4, 2, Placements.AfterStep)]     // after the last step
    [InlineData(9, 2, Placements.AfterStep)]     // anchor beyond every input: after the last step
    public void AnchorsMapToStepBoundaries(long after, int expected, string placement)
    {
        var rec = new Rec().Key(Keys.kbF2).Text("ab").Key(Keys.kbF3);
        var c = rec.Convert();
        Assert.Equal(3, c.Steps.Count);
        var plan = Plan(rec, c, [Cand(5, after, 42)]);
        var p = Assert.Single(plan.Proposals);
        Assert.Equal((expected, placement), (p.InsertAfterStep, p.Placement));
        var boundary = new RecordedBoundary(5, Protocol.BoundaryKinds.Idle, Protocol.BoundaryCauses.Input, after, after, 5, 42, 0, 80, 25, null, 0);
        Assert.Equal(RecordingCaptureTimeline.StepIndexAfter(boundary, c.Steps), CheckpointPlanner.StepIndex(after, c.Steps));
    }

    [Fact]
    public void AnchorAfterInputsThatMadeNoStepIsBeforeFirst()
    {
        var rec = new Rec().Move(4, 4).Key(Keys.kbF2); // a hover move is not a step
        var plan = Plan(rec, rec.Convert(), [Cand(2, 1, 9)]);
        Assert.Equal(Placements.BeforeFirst, Assert.Single(plan.Proposals).Placement);
    }

    [Fact]
    public void ProposalsAreOrderedCausallyRegardlessOfCandidateOrder()
    {
        var rec = new Rec().Key(Keys.kbF2).Text("ab").Key(Keys.kbF3);
        var c = rec.Convert();
        var candidates = new[] { Cand(9, 4, 4), Cand(2, 0, 1, CandidateKinds.Final), Cand(7, 3, 3, CandidateKinds.MenuOpen), Cand(4, 1, 2, CandidateKinds.ModalOpen) };
        var plan = Plan(rec, c, candidates);
        Assert.Equal(["p-2", "p-4", "p-7", "p-9"], plan.Proposals.Select(p => p.Id));
        Assert.Equal(["rec-final-1", "rec-modal-open-2", "rec-menu-open-3", "rec-content-4"], plan.Proposals.Select(p => p.CheckpointId));
        Assert.Equal([-1, 0, 1, 2], plan.Proposals.Select(p => p.InsertAfterStep));
        var reversed = Plan(rec, c, candidates.Reverse());
        Assert.Equal(Describe(plan), Describe(reversed));
    }

    // ── aggregated steps ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TextRunIsSplitIdenticallyAtTheAnchorAndLaterIndicesShift()
    {
        var rec = new Rec().Text("hello").Key(Keys.kbF2);
        var c = rec.Convert();
        string before = string.Join("|", Docs(c.Steps).Select(s => s.ToString()));
        var plan = Plan(rec, c, [Cand(3, 2, 30), Cand(8, 6, 80)]);
        Assert.True(plan.StepsSplit);
        Assert.Equal(["he", "llo"], plan.Steps.Where(s => s.Step.Kind == "text").Select(s => s.Step.Name));
        Assert.Equal([(1L, 2L), (3L, 5L), (6L, 6L)], plan.Steps.Select(s => (s.First, s.Last)));
        Assert.Equal((0, Placements.Split), (plan.Proposals[0].InsertAfterStep, plan.Proposals[0].Placement));
        Assert.Contains(CheckpointPlanner.Flags.Split, plan.Proposals[0].Flags);
        Assert.Equal((2, Placements.AfterStep), (plan.Proposals[1].InsertAfterStep, plan.Proposals[1].Placement)); // was 1 before the split
        Assert.Empty(RecordingConverter.SelfCheck(Docs(plan.Steps), rec.Inputs, 80, 25));
        Assert.Equal(before, string.Join("|", Docs(c.Steps).Select(s => s.ToString()))); // conversion never mutated
        Assert.Equal(2, c.Steps.Count);
    }

    [Fact]
    public void SeveralAnchorsInOneTextRunSplitItOnceIntoSeveralParts()
    {
        var rec = new Rec().Text("abcd");
        var plan = Plan(rec, rec.Convert(), [Cand(2, 1, 1), Cand(4, 3, 3)]);
        Assert.Equal(["a", "bc", "d"], plan.Steps.Select(s => s.Step.Name));
        Assert.Equal([0, 1], plan.Proposals.Select(p => p.InsertAfterStep));
        Assert.All(plan.Proposals, p => Assert.Equal(Placements.Split, p.Placement));
    }

    [Fact]
    public void WheelRunIsSplitIdentically()
    {
        var rec = new Rec().Wheel(5, 6, 5);
        var c = rec.Convert();
        Assert.Equal("wheel-down@5,6*5", Assert.Single(c.Steps).Step.Name);
        var plan = Plan(rec, c, [Cand(3, 2, 2)]);
        Assert.Equal(["wheel-down@5,6*2", "wheel-down@5,6*3"], plan.Steps.Select(s => s.Step.Name));
        Assert.Equal((0, Placements.Split), (plan.Proposals[0].InsertAfterStep, plan.Proposals[0].Placement));
    }

    [Fact]
    public void MenuOpenedOnMouseDownSplitsTheClickIntoDownAndUp()
    {
        // A menu bar opens on the press: the transient state lies between the down and the up of one click step.
        var rec = new Rec().Key(Keys.kbF2).Click(3, 0);
        var c = rec.Convert();
        Assert.Equal("click-left@3,0", c.Steps[1].Step.Name);
        var plan = Plan(rec, c, [Cand(2, 2, 20, CandidateKinds.MenuOpen, stability: Stabilities.Transient), Cand(3, 3, 30, CandidateKinds.MenuChange)]);
        Assert.Equal(["kbF2", "down-left@3,0", "up-left@3,0"], plan.Steps.Select(s => s.Step.Name));
        Assert.Equal([(1, Placements.Split), (2, Placements.AfterStep)], plan.Proposals.Select(p => (p.InsertAfterStep, p.Placement)));
        Assert.Equal("rec-menu-open-1", plan.Proposals[0].CheckpointId);
        // InputPlanner: click = [move], down, up; down + up = [move], down, up (no second move).
        Assert.Equal(CheckpointPlanner.Stream([ScenarioDocumentStep.Mouse("click-left@3,0")], 80, 25),
            CheckpointPlanner.Stream([ScenarioDocumentStep.Mouse("down-left@3,0"), ScenarioDocumentStep.Mouse("up-left@3,0")], 80, 25));
    }

    [Fact]
    public void AnchorInsideADoubleClickIsUnrepresentable()
    {
        var rec = new Rec().Click(9, 4).Down(9, 4, Events.meDoubleClick).Up(9, 4);
        var c = rec.Convert();
        Assert.Equal("double-left@9,4", Assert.Single(c.Steps).Step.Name);
        foreach (long after in new long[] { 1, 2, 3 })
        {
            var plan = Plan(rec, c, [Cand(5, after, 50)]);
            var p = Assert.Single(plan.Proposals);
            Assert.Equal(Placements.Unrepresentable, p.Placement);
            Assert.Null(p.Step);
            Assert.True(p.NeedsReview);
            Assert.Contains(CheckpointPlanner.Flags.Unrepresentable, p.Flags);
            Assert.Contains(plan.Diagnostics, d => d.Contains("double", StringComparison.Ordinal) && d.Contains("step 1", StringComparison.Ordinal));
            Assert.False(plan.StepsSplit);
            Assert.Same(c.Steps, plan.Steps);
        }
        // Why: two click steps replay the second press without meDoubleClick (the self-check notices).
        Assert.NotEmpty(RecordingConverter.SelfCheck([ScenarioDocumentStep.Mouse("click-left@9,4"), ScenarioDocumentStep.Mouse("click-left@9,4")], rec.Inputs, 80, 25));
        // After the whole double click is fine.
        Assert.Equal(Placements.AfterStep, Assert.Single(Plan(rec, c, [Cand(6, 4, 60)]).Proposals).Placement);
    }

    [Fact]
    public void AnchorInsideAMergedResizeIsUnrepresentable()
    {
        var rec = new Rec().Key(Keys.kbF2).Grid(100, 30).Grid(110, 32).Key(Keys.kbF3);
        var c = rec.Convert();
        Assert.Equal((2L, 3L), (c.Steps[1].First, c.Steps[1].Last));
        var plan = Plan(rec, c, [Cand(4, 2, 40, CandidateKinds.Resize), Cand(5, 3, 41, CandidateKinds.Resize)]);
        Assert.Equal(Placements.Unrepresentable, plan.Proposals[0].Placement);
        Assert.Contains(plan.Diagnostics, d => d.Contains("resize", StringComparison.Ordinal));
        Assert.Equal((1, Placements.AfterStep), (plan.Proposals[1].InsertAfterStep, plan.Proposals[1].Placement));
    }

    [Fact]
    public void ModifierWrappedKeyPlacesBeforeTheReleaseAndEndCleanupIsNeverPassed()
    {
        var rec = new Rec().Key(Keys.kbUp, Keys.kbShift).Key(Keys.kbDown).Key(Keys.kbUp, Keys.kbShift);
        var c = rec.Convert();
        string[] kinds = Docs(c.Steps).Select(s => s.Kind + ":" + s.Name).ToArray();
        Assert.Equal(["modifiers:shift-down", "key:kbUp", "modifiers:shift-up", "key:kbDown", "modifiers:shift-down", "key:kbUp", "modifiers:shift-up"], kinds);
        var plan = Plan(rec, c, [Cand(2, 1, 1), Cand(3, 2, 2), Cand(4, 3, 3)]);
        // After event 1 (Shift+Up): after the key, before the shift release that the next event caused.
        Assert.Equal(1, plan.Proposals[0].InsertAfterStep);
        Assert.Equal(3, plan.Proposals[1].InsertAfterStep);
        // After the last event: before the converter's synthetic end-of-recording shift-up.
        Assert.Equal(5, plan.Proposals[2].InsertAfterStep);
        Assert.Contains(CheckpointPlanner.Flags.BeforeFinalRelease, plan.Proposals[2].Flags);
        Assert.False(plan.StepsSplit);
    }

    [Fact]
    public void ButtonHeldAtTheEndPlacesBeforeTheSyntheticRelease()
    {
        var rec = new Rec().Down(2, 2).Move(5, 2, 1);
        var c = rec.Convert();
        Assert.Equal(["mouse", "release"], Docs(c.Steps).Select(s => s.Kind));
        var plan = Plan(rec, c, [Cand(3, 2, 3, CandidateKinds.Final, stability: Stabilities.Final)]);
        var p = Assert.Single(plan.Proposals);
        Assert.Equal((0, Placements.AfterStep), (p.InsertAfterStep, p.Placement));
        Assert.Contains(CheckpointPlanner.Flags.BeforeFinalRelease, p.Flags);
    }

    [Fact]
    public void StateAfterAGapIsFlaggedAndNeedsReview()
    {
        var rec = new Rec().Text("a").Text("b", gap: true);
        var c = rec.Convert();
        Assert.Equal(2, c.Steps.Count); // the gap breaks the text run
        var plan = Plan(rec, c, [Cand(3, 2, 2, gap: true, flags: [CandidateFlags.AfterGap], confidence: Confidences.Medium)]);
        var p = Assert.Single(plan.Proposals);
        Assert.Equal((1, Placements.AfterStep), (p.InsertAfterStep, p.Placement));
        Assert.Contains(CandidateFlags.AfterGap, p.Flags);
        Assert.True(p.NeedsReview);
    }

    [Fact]
    public void ConversionThatDoesNotReplayExactlyFlagsEveryProposal()
    {
        // A key typed while the button was held: the converter emits the text step before the click (self-check mismatch).
        var rec = new Rec().Down(4, 4).Text("a").Up(4, 4);
        var c = rec.Convert();
        Assert.NotEmpty(c.Mismatches);
        var plan = Plan(rec, c, [Cand(4, 2, 4)]);
        var p = Assert.Single(plan.Proposals);
        Assert.Contains(CheckpointPlanner.Flags.ConversionMismatch, p.Flags);
        Assert.True(p.NeedsReview);
        Assert.Contains(plan.Diagnostics, d => d.Contains("self-check", StringComparison.Ordinal));
    }

    // ── ids ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckpointIdsAvoidExistingIdsAndStayValid()
    {
        var rec = new Rec().Key(Keys.kbF2).Key(Keys.kbF3).Key(Keys.kbF4);
        var c = rec.Convert();
        var plan = Plan(rec, c, [Cand(1, 1, 1), Cand(2, 2, 2, CandidateKinds.MenuOpen), Cand(3, 3, 3, "A Very Long Kind Name That Goes On And On Forever!")],
            existing: ["rec-content-1", "REC-MENU-OPEN-2", "rec-menu-open-2-2", "initial"]);
        Assert.Equal("rec-content-1-2", plan.Proposals[0].CheckpointId);
        Assert.Equal("rec-menu-open-2-3", plan.Proposals[1].CheckpointId);
        Assert.StartsWith("rec-a-very-long-kind", plan.Proposals[2].CheckpointId, StringComparison.Ordinal);
        Assert.True(plan.Proposals[2].CheckpointId.Length <= 48);

        var custom = Plan(rec, c, [Cand(1, 1, 1)], options: new("My Prefix!"));
        Assert.Equal("my-prefix-content-1", custom.Proposals[0].CheckpointId);
        var bad = Plan(rec, c, [Cand(1, 1, 1)], options: new("!!!"));
        Assert.Equal("rec-content-1", bad.Proposals[0].CheckpointId);
        Assert.Contains(bad.Diagnostics, d => d.Contains("prefix", StringComparison.Ordinal));
    }

    // ── dedupe and picks ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CandidatesOfOneStateBecomeOneProposalWithTheLowestConfidence()
    {
        var rec = new Rec().Key(Keys.kbF2).Move(6, 6).Key(Keys.kbF3);
        var c = rec.Convert();
        var plan = Plan(rec, c,
        [
            Cand(2, 1, 77),                                                                        // the state
            Cand(2, 1, 77, id: "c-2-dup", confidence: Confidences.Medium),                         // same BoundaryId
            Cand(3, 1, 77, evidence: EvidenceLevels.HashOnly, confidence: Confidences.Low),         // same Hash + AfterSequence
            Cand(4, 2, 77, confidence: Confidences.Medium),                                         // same hash at the same step boundary (hover move)
            Cand(5, 3, 99),                                                                         // a different state
        ]);
        Assert.Equal(2, plan.Proposals.Count);
        var p = plan.Proposals[0];
        Assert.Equal(["c-2", "c-2-dup", "c-3", "c-4"], p.CandidateIds);
        Assert.Equal((Confidences.Low, EvidenceLevels.Snapshot, 2L, (long?)2), (p.Confidence, p.Evidence, p.BoundaryId, p.SnapshotId));
        Assert.Contains(CheckpointPlanner.Flags.Merged, p.Flags);
        Assert.Contains(CandidateFlags.SnapshotNotFetched, p.Flags); // degradation of a merged candidate stays visible
        Assert.True(p.NeedsReview);
        Assert.Equal(["c-5"], plan.Proposals[1].CandidateIds);
    }

    [Fact]
    public void DifferentStatesAtOneStepBoundaryAreKeptAndFlagged()
    {
        var rec = new Rec().Key(Keys.kbF2).Key(Keys.kbF3);
        var plan = Plan(rec, rec.Convert(), [Cand(2, 1, 10), Cand(3, 1, 11, CandidateKinds.Async, stability: Stabilities.Settled)]);
        Assert.Equal(2, plan.Proposals.Count);
        Assert.All(plan.Proposals, p => { Assert.Equal(0, p.InsertAfterStep); Assert.Contains(CheckpointPlanner.Flags.SharedPosition, p.Flags); Assert.True(p.NeedsReview); });
        Assert.Contains(plan.Diagnostics, d => d.Contains("share one step boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void PicksAttachToTheirStateOrCreateExactlyOneProposal()
    {
        var rec = new Rec().Key(Keys.kbF2).Key(Keys.kbF3).Key(Keys.kbF4);
        var c = rec.Convert();
        var plan = Plan(rec, c, [Cand(3, 1, 30, CandidateKinds.ModalOpen, last: 6)],
        [
            Pick(7, 5, 1, 30),                                   // anchor inside the candidate's window 3..6: attach
            Pick(9, 8, 2, 80),                                   // no candidate: creates a pick proposal
            Pick(10, 8, 2, 80),                                  // same state picked again: same proposal
            Pick(10, 8, 2, 80),                                  // the same pick delivered twice: attached once
            Pick(12, 11, 3, 31, EvidenceLevels.Missing),         // pick-only, snapshot dropped
        ]);
        Assert.Equal(3, plan.Proposals.Count);
        Assert.Equal([7L], plan.Proposals[0].Picks.Select(p => p.PickId));
        Assert.Equal(["c-3"], plan.Proposals[0].CandidateIds);
        Assert.Equal(Confidences.High, plan.Proposals[0].Confidence); // an attached pick never changes the state's confidence

        var picked = plan.Proposals[1];
        Assert.Equal((CheckpointPlanner.PickKind, "p-8", "rec-pick-2", 1, Confidences.Medium), (picked.Kind, picked.Id, picked.CheckpointId, picked.InsertAfterStep, picked.Confidence));
        Assert.Equal([9L, 10L], picked.Picks.Select(p => p.PickId));
        Assert.Empty(picked.CandidateIds);
        Assert.Contains(CheckpointPlanner.Flags.PickOnly, picked.Flags);
        Assert.Contains(plan.Diagnostics, d => d.Contains("Pick 10", StringComparison.Ordinal));

        var dropped = plan.Proposals[2];
        Assert.Equal((Confidences.Low, EvidenceLevels.Missing, true), (dropped.Confidence, dropped.Evidence, dropped.NeedsReview));
        Assert.Contains(CandidateFlags.SnapshotDropped, dropped.Flags);
    }

    [Fact]
    public void PickMatchingHashAndSequenceAttachesWithoutAWindow()
    {
        var rec = new Rec().Key(Keys.kbF2);
        var plan = Plan(rec, rec.Convert(), [Cand(2, 1, 5)], [Pick(4, 99, 1, 5)]);
        var p = Assert.Single(plan.Proposals);
        Assert.Equal([4L], p.Picks.Select(x => x.PickId));
    }

    // ── cap and determinism ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MaximumProposalsCapIsReported()
    {
        var rec = new Rec().Key(Keys.kbF2).Key(Keys.kbF3).Key(Keys.kbF4).Key(Keys.kbF5);
        var plan = Plan(rec, rec.Convert(), Enumerable.Range(1, 4).Select(i => Cand(i, i, (ulong)i)), options: new(MaximumProposals: 2));
        Assert.Equal(2, plan.Proposals.Count);
        Assert.Contains(plan.Diagnostics, d => d.Contains("cap of 2", StringComparison.Ordinal) && d.Contains("boundary 3", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanningIsDeterministic()
    {
        var rec = new Rec().Text("abc").Click(3, 0).Wheel(1, 1, 3).Key(Keys.kbF2);
        var c = rec.Convert();
        StableChangeCandidate[] candidates = [Cand(2, 1, 1), Cand(5, 4, 4, CandidateKinds.MenuOpen), Cand(7, 6, 6), Cand(9, 9, 9), Cand(1, 0, 0)];
        PickSelection[] picks = [Pick(20, 9, 9, 9), Pick(21, 30, 2, 2)];
        string first = Describe(Plan(rec, c, candidates, picks, ["rec-content-1"]));
        for (int i = 0; i < 5; i++)
        {
            var shuffled = candidates.OrderBy(x => (x.BoundaryId * 7 + i) % 5).ToArray();
            Assert.Equal(first, Describe(Plan(rec, c, shuffled, picks.Reverse(), ["rec-content-1"])));
        }
        Assert.Contains("down-left@3,0", first, StringComparison.Ordinal);
        Assert.Contains("wheel-down@1,1*2", first, StringComparison.Ordinal);
    }
}
