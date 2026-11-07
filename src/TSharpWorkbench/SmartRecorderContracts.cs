using TSharpWorkbench.Testing;

// Shared contracts. Data only: the detector
// (CheckpointDetector.cs), planner (CheckpointPlanner.cs) and pick resolver (RecordingPickResolver.cs) are pure functions
// over these records. Nothing here persists, inserts steps, adds assertions or changes the scenario schema (still v1).

// ── detector (Workstream A) ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>Everything the detector may read: one immutable copy of a recording's capture timeline.</summary>
/// <param name="Events">Recorded inputs ordered by Sequence (converter numbering).</param>
/// <param name="Boundaries">boundaries ordered by Id, pick anchors included (Kind <c>pick</c>: never a state change).</param>
/// <param name="Snapshots">Retained transient/pick snapshots fetched so far (immutable; never replaced by later evidence).</param>
/// <param name="Inspections">Live RecordInspect samples taken by the Workbench while recording (bounded; may be empty).</param>
/// <param name="Final">True after Stop/target exit: the last state is final. False for a live (partial) analysis.</param>
internal sealed record DetectorInput(IReadOnlyList<RecordedInput> Events, IReadOnlyList<RecordedBoundary> Boundaries,
    IReadOnlyList<RetainedSnapshot> Snapshots, IReadOnlyList<RecordInspection> Inspections, CaptureState Capture, bool Final);

/// <summary>Loss/coverage facts the detector must honour (from RecordingCaptureTimeline). Never upgrade evidence past them.</summary>
internal sealed record CaptureState(bool BoundariesEnabled, bool SnapshotsEnabled, bool CoverageComplete, long BoundariesLost,
    long SnapshotsLost, bool Recovered, CaptureCoverage? Coverage, string? Diagnostic)
{
    internal static CaptureState Legacy { get; } = new(false, false, false, 0, 0, false, null, null);
}

/// <summary>Detector thresholds (all deterministic; Milliseconds are the target recording clock, never poll time).</summary>
/// <param name="SettleMilliseconds">An application-caused (async) state counts as settled when no further boundary changes
/// it for this long, or when the next input arrives.</param>
/// <param name="PeriodicChanges">This many application-caused changes inside <paramref name="PeriodicWindowMilliseconds"/>
/// with no input between them mark the stretch as never-quiescent (no async candidates from it).</param>
/// <param name="MaximumCandidates">Hard cap; overflow is reported in diagnostics, never silently dropped.</param>
internal sealed record DetectorOptions(int SettleMilliseconds = 300, int PeriodicChanges = 3, int PeriodicWindowMilliseconds = 2000,
    int MaximumCandidates = 200)
{
    internal static DetectorOptions Default { get; } = new();
}

/// <summary>One significant, stable screen/UI state the detector proposes to checkpoint. Deterministic for equal input.</summary>
/// <param name="Id">Stable within the recording: <c>c-{BoundaryId}</c>.</param>
/// <param name="BoundaryId">First boundary showing the state (the causal anchor).</param>
/// <param name="LastBoundaryId">Last boundary still showing the same state (end of the stability window).</param>
/// <param name="AfterSequence">Last recorded input before the state (the boundary's AfterSequence).</param>
/// <param name="EndSequence">The recorded input that ended the state; null when it lasted to the end (or is live).</param>
/// <param name="Kind"><see cref="CandidateKinds"/>.</param>
/// <param name="Stability"><see cref="Stabilities"/>.</param>
/// <param name="Evidence"><see cref="EvidenceLevels"/>: what backs the candidate.</param>
/// <param name="Confidence"><see cref="Confidences"/>.</param>
/// <param name="SnapshotId">retained snapshot taken AT this state's own boundary (never a later one), else null.</param>
/// <param name="Flags">Degradations and context (<see cref="CandidateFlags"/>); never empty when Evidence is not full.</param>
internal sealed record StableChangeCandidate(string Id, long BoundaryId, long LastBoundaryId, long AfterSequence, long? EndSequence,
    string Cause, string Kind, string Stability, string Evidence, string Confidence, string Reason, ulong Hash, long Generation,
    int Width, int Height, string? ModalTop, int MenuDepth, long? SnapshotId, bool AfterGap, IReadOnlyList<string> Flags);

internal sealed record DetectionResult(IReadOnlyList<StableChangeCandidate> Candidates, IReadOnlyList<string> Diagnostics,
    DetectorCounters Counters);

/// <summary>Why boundaries did not become candidates (diagnostics; every boundary lands in exactly one bucket).</summary>
internal sealed record DetectorCounters(int StateBoundaries, int PickBoundaries, int Candidates, int Typing, int Unchanged,
    int Unsettled, int Periodic, int Insignificant, int Overflow);

internal static class CandidateKinds
{
    internal const string ModalOpen = "modal-open", ModalChange = "modal-change", ModalClose = "modal-close",
        MenuOpen = "menu-open", MenuChange = "menu-change", MenuClose = "menu-close",
        Content = "content", Async = "async", Resize = "resize", Final = "final";
}

internal static class Stabilities
{
    /// <summary>The next recorded input ended the state (the user saw it and reacted).</summary>
    internal const string InputEnded = "input-ended";
    /// <summary>No change for SettleMilliseconds (or until the next input) after an application-caused change.</summary>
    internal const string Settled = "settled";
    /// <summary>The state lasted until the recording ended.</summary>
    internal const string Final = "final";
    /// <summary>A menu/modal state observed by Smart Recording (possibly closed before any poll); stable for its own boundary span.</summary>
    internal const string Transient = "transient";
}

internal static class EvidenceLevels
{
    /// <summary>Retained snapshot (cells + inspection) of this exact boundary.</summary>
    internal const string Snapshot = "snapshot";
    /// <summary>A live RecordInspect sample whose Hash and AfterSequence match the state.</summary>
    internal const string Inspection = "inspection";
    /// <summary>Only the boundary's hash/signature (no cells): the state is known to differ, its content is not.</summary>
    internal const string HashOnly = "hash-only";
    /// <summary>A snapshot was due but dropped/omitted/not fetched, or the boundary came from crash recovery.</summary>
    internal const string Missing = "missing";
}

internal static class Confidences
{
    internal const string High = "high", Medium = "medium", Low = "low";
}

internal static class CandidateFlags
{
    internal const string SnapshotDropped = "snapshot-dropped", SnapshotNotFetched = "snapshot-not-fetched",
        InspectionOmitted = "inspection-omitted", CoverageIncomplete = "coverage-incomplete", Recovered = "recovered",
        AfterGap = "after-gap", AfterResize = "after-resize", Async = "async", Legacy = "legacy-target",
        HashOnly = "hash-only", NeverQuiescent = "never-quiescent";
}

// ── planner (Workstream B) ──────────────────────────────────────────────────────────────────────────────────────

/// <param name="Conversion">RecordingConverter.Convert output for <paramref name="Events"/> (never mutated).</param>
/// <param name="ExistingCheckpointIds">Checkpoint ids already used by the target document (generated ids avoid them).</param>
internal sealed record PlannerInput(IReadOnlyList<RecordedInput> Events, RecordingConversion Conversion, int Width, int Height,
    IReadOnlyList<StableChangeCandidate> Candidates, IReadOnlyList<PickSelection> Picks, IReadOnlyCollection<string> ExistingCheckpointIds);

internal sealed record PlannerOptions(string IdPrefix = "rec", int MaximumProposals = 100)
{
    internal static PlannerOptions Default { get; } = new();
}

/// <summary>Planner output. <see cref="Steps"/> is the input-only step list the proposals refer to: the converter's steps,
/// with coalesced steps split ONLY where a proposal needs a boundary inside them and the split replays identically
/// (RecordingConverter.SelfCheck stays empty). Nothing is persisted; accepting/rejecting happens in the review.</summary>
internal sealed record CheckpointPlan(IReadOnlyList<RecordedStep> Steps, bool StepsSplit, IReadOnlyList<CheckpointProposal> Proposals,
    IReadOnlyList<string> Diagnostics);

/// <summary>One proposed schema-v1 checkpoint step (capture only, no assertions).</summary>
/// <param name="Id">Proposal identity, stable for equal input: <c>p-{BoundaryId}</c>.</param>
/// <param name="CheckpointId">Valid checkpoint name (Protocol.IsValidName), unique in the plan and vs existing ids.</param>
/// <param name="InsertAfterStep">Index into <see cref="CheckpointPlan.Steps"/> the checkpoint follows; -1 = before the
/// first step. Meaningless when <see cref="Placement"/> is <c>unrepresentable</c>.</param>
/// <param name="Placement"><see cref="Placements"/>.</param>
/// <param name="Step">The schema-v1 checkpoint step (ScenarioDocumentStep.Checkpoint), null when unrepresentable.</param>
/// <param name="CandidateIds">Detector candidates merged into this proposal (deduplicated state).</param>
/// <param name="Picks">picks attached to this state (they never create a second proposal for it).</param>
internal sealed record CheckpointProposal(string Id, string CheckpointId, int InsertAfterStep, string Placement, ScenarioDocumentStep? Step,
    string Kind, string Reason, string Confidence, string Evidence, long AfterSequence, long BoundaryId, long? SnapshotId,
    IReadOnlyList<string> CandidateIds, IReadOnlyList<PickSelection> Picks, bool NeedsReview, IReadOnlyList<string> Flags);

internal static class Placements
{
    internal const string AfterStep = "after-step", BeforeFirst = "before-first", Split = "split",
        Unrepresentable = "unrepresentable";
}

// ── picks (Workstream C, Workbench side) ────────────────────────────────────────────────────────────────────────

/// <summary>A pick resolved against its own frozen snapshot: region data always; a view/menu/status target and selector
/// candidates only when the captured evidence supports them. Never an assertion.</summary>
/// <param name="PickId">The pick boundary's Id.</param>
/// <param name="AfterSequence">Causal anchor (state at the press).</param>
/// <param name="Evidence"><see cref="EvidenceLevels"/> (<c>snapshot</c>, <c>missing</c>; <c>hash-only</c> when the snapshot
/// was fetched without inspection).</param>
/// <param name="RegionText">Cell text inside the region from the snapshot (null when no cells).</param>
internal sealed record PickSelection(long PickId, string Kind, string Trigger, long AfterSequence, long AnchorBoundaryId, ulong Hash,
    long Generation, int Width, int Height, CellRect Region, long? SnapshotId, string Evidence, PickTarget? Target,
    IReadOnlyList<SelectorCandidate> Selectors, string? RegionText, IReadOnlyList<string> Flags);

/// <param name="Area"><see cref="PickAreas"/>.</param>
/// <param name="Path">Snapshot-local view path (diagnostic only; NEVER emitted as a selector).</param>
internal sealed record PickTarget(string Area, string? Path, string? Type, string? Kind, string? ViewId, string? Text, CellRect? Screen,
    string? MenuItem, string? StatusItem);

/// <param name="Basis"><see cref="SelectorBases"/>.</param>
/// <param name="Matches">Matches of the selector on the pick's captured view tree (Assertions' ViewSelector.Matches).</param>
internal sealed record SelectorCandidate(ViewSelector Selector, string Basis, int Matches)
{
    internal bool Unique => Matches == 1;
}

internal static class PickAreas
{
    internal const string View = "view", MenuItem = "menu-item", Menu = "menu", StatusItem = "status-item", Status = "status",
        Region = "region";
}

internal static class SelectorBases
{
    internal const string Id = "id", TypeText = "type-text", Type = "type";
}

// ── analysis facade (coordinator) ────────────────────────────────────────────────────────────────────────────────────

/// <summary>Detector + resolver + planner over one recording, with a short diagnostic summary. Internal model only.</summary>
internal sealed record SmartRecordingAnalysis(DetectionResult Detection, IReadOnlyList<PickSelection> Picks, CheckpointPlan Plan,
    IReadOnlyList<string> Summary)
{
    /// <summary>The immutable input the analysis was computed from (evidence for assertion suggestions).</summary>
    internal DetectorInput? Input { get; init; }
}
