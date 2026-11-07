using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpWorkbench.Testing;

// Shared contracts. Data and small pure helpers only. The
// proposer (AssertionProposer.cs, Workstream A) and validator (AssertionSuggestionValidator.cs, Workstream B) are pure
// functions over these records. Scenario documents stay schema v1: a suggestion's review metadata (origin, rule,
// rationale, confidence, status) lives in memory only and is never written into a v1 field.

/// <summary>Where the evidence of one checkpoint state came from (<see cref="CheckpointEvidence.Source"/>).</summary>
internal static class EvidenceSources
{
    /// <summary>Retained snapshot of the proposal's own boundary (or the snapshot the planner named for it).</summary>
    internal const string Snapshot = "snapshot";
    /// <summary>Live RecordInspect sample with the same Hash and AfterSequence as the proposal's boundary.</summary>
    internal const string Inspection = "inspection";
    /// <summary>The frozen snapshot of a pick attached to the proposal, taken in the same state (same Hash and AfterSequence).</summary>
    internal const string PickSnapshot = "pick-snapshot";
    /// <summary>No cells of this state are available (hash-only, dropped, not fetched, recovered, legacy).</summary>
    internal const string None = "none";
}

/// <summary>Flags of <see cref="CheckpointEvidence"/> in addition to CandidateFlags/PickFlags.</summary>
internal static class EvidenceFlags
{
    internal const string NoBoundary = "no-boundary", CellsUnreadable = "cells-unreadable", NoInspection = "no-inspection",
        InspectionOmitted = "inspection-omitted", TreeTruncated = "view-tree-truncated", NoViewTree = "no-view-tree",
        NoMenu = "no-menu", NoStatus = "no-status", CoverageIncomplete = "coverage-incomplete";
}

/// <summary>The evidence of exactly ONE proposal's own historical state. Never a later or earlier screen: every source must
/// have the boundary's Hash AND AfterSequence. <see cref="Cells"/> and <see cref="Inspection"/> come from the same capture.</summary>
/// <param name="Hash">The proposal boundary's screen hash (the state's identity).</param>
/// <param name="Omitted">The snapshot left part of its inspection out (SnapshotOmissions): never semantic evidence.</param>
internal sealed record CheckpointEvidence(string ProposalId, long BoundaryId, ulong Hash, long AfterSequence, string Source, long? SnapshotId,
    ScreenSnapshot? Cells, CheckpointInspection? Inspection, bool Omitted, IReadOnlyList<string> Flags)
{
    internal int Width => Cells?.Width ?? 0;
    internal int Height => Cells?.Height ?? 0;
    internal ViewTree? Tree => Omitted ? null : Inspection?.ViewTree;
    internal bool TreeTruncated => Inspection?.ViewTree?.Truncated == true;
    /// <summary>Cells plus a complete (non-omitted) inspection: semantic assertions may be proposed.</summary>
    internal bool Semantic => Cells is not null && Inspection is not null && !Omitted;
    /// <summary>No semantic assertion may be proposed: the checkpoint is a capture only.</summary>
    internal bool CaptureOnly => !Semantic && Cells is null;
}

/// <summary>Pure: finds the evidence of a proposal's own state (see <see cref="CheckpointEvidence"/>).</summary>
internal static class CheckpointEvidenceResolver
{
    /// <remarks>Order: the proposal's SnapshotId; any snapshot of the proposal's boundary; the last live inspection of the
    /// state; the snapshot of an attached pick. Each candidate must match the boundary's Hash and AfterSequence. Among the
    /// matches a complete (inspection present, not omitted) source wins over cells-only.</remarks>
    internal static CheckpointEvidence Resolve(CheckpointProposal proposal, IReadOnlyList<RecordedBoundary> boundaries,
        IReadOnlyList<RetainedSnapshot> snapshots, IReadOnlyList<RecordInspection> inspections, bool coverageComplete = true)
    {
        var flags = new List<string>();
        if (!coverageComplete) flags.Add(EvidenceFlags.CoverageIncomplete);
        var boundary = boundaries.FirstOrDefault(b => b.Id == proposal.BoundaryId);
        if (boundary is null)
        {
            flags.Add(EvidenceFlags.NoBoundary);
            return new(proposal.Id, proposal.BoundaryId, 0, proposal.AfterSequence, EvidenceSources.None, null, null, null, false, flags);
        }
        bool Same(ulong hash, long after) => hash == boundary.Hash && after == boundary.AfterSequence;
        var options = new List<(string Source, long? Id, string Cells, CheckpointInspection? Inspection, bool Omitted)>();
        if (proposal.SnapshotId is { } sid && snapshots.FirstOrDefault(s => s.Id == sid) is { } own && Same(own.Hash, own.AfterSequence))
            options.Add((EvidenceSources.Snapshot, own.Id, own.Cells, own.Inspection, own.Omitted is not null));
        foreach (var s in snapshots.Where(s => s.BoundaryId == boundary.Id && s.Id != proposal.SnapshotId && Same(s.Hash, s.AfterSequence)))
            options.Add((EvidenceSources.Snapshot, s.Id, s.Cells, s.Inspection, s.Omitted is not null));
        if (inspections.LastOrDefault(i => Same(i.Hash, i.AfterSequence)) is { } live)
            options.Add((EvidenceSources.Inspection, null, live.Cells, live.Inspection, false));
        foreach (var pick in proposal.Picks.Where(p => p.SnapshotId is not null).OrderBy(p => p.PickId))
            if (snapshots.FirstOrDefault(s => s.Id == pick.SnapshotId) is { } ps && Same(ps.Hash, ps.AfterSequence))
                options.Add((EvidenceSources.PickSnapshot, ps.Id, ps.Cells, ps.Inspection, ps.Omitted is not null));
        if (options.Count == 0)
            return new(proposal.Id, boundary.Id, boundary.Hash, boundary.AfterSequence, EvidenceSources.None, null, null, null, false, flags);
        var best = options.FirstOrDefault(o => o.Inspection is not null && !o.Omitted);
        if (best.Source is null) best = options[0];
        ScreenSnapshot? cells = null;
        try { cells = ScreenSnapshot.Deserialize(Convert.FromBase64String(best.Cells)); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { flags.Add(EvidenceFlags.CellsUnreadable); }
        var inspection = cells is null ? null : best.Inspection;
        if (best.Omitted) flags.Add(EvidenceFlags.InspectionOmitted);
        if (inspection is null) flags.Add(EvidenceFlags.NoInspection);
        else
        {
            if (inspection.ViewTree is null) flags.Add(EvidenceFlags.NoViewTree);
            else if (inspection.ViewTree.Truncated) flags.Add(EvidenceFlags.TreeTruncated);
            if (inspection.Menu is null) flags.Add(EvidenceFlags.NoMenu);
            if (inspection.Status is null) flags.Add(EvidenceFlags.NoStatus);
        }
        return new(proposal.Id, boundary.Id, boundary.Hash, boundary.AfterSequence, best.Source, best.Id, cells, inspection, best.Omitted, flags);
    }
}

// ── A: proposer ──────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Who caused a suggestion (review-only provenance; never written to the scenario).</summary>
internal static class SuggestionOrigins
{
    /// <summary>Derived automatically from the state change the checkpoint captures.</summary>
    internal const string State = "state";
    /// <summary>Derived from a control or region the user picked in this state.</summary>
    internal const string Pick = "pick";
}

/// <param name="Proposal">The checkpoint proposal with its attached picks.</param>
/// <param name="Evidence">The proposal's own evidence (<see cref="CheckpointEvidenceResolver"/>).</param>
/// <param name="Candidate">The proposal's first detector candidate (kind/stability/flags), null for a pick-only proposal.</param>
internal sealed record ProposerInput(CheckpointProposal Proposal, CheckpointEvidence Evidence, StableChangeCandidate? Candidate);

/// <param name="MaximumPerCheckpoint">Ranked cap of candidates per checkpoint (picks first, then state rules).</param>
internal sealed record ProposerOptions(int MaximumPerCheckpoint = 8)
{
    internal static ProposerOptions Default { get; } = new();
}

/// <summary>A raw assertion idea from a rule (A). Not yet validated: <see cref="Spec"/>'s Id is a readable proposal
/// (e.g. <c>dialog-modal</c>) that the validator turns into a final unique id; Severity is ignored (the validator sets it).</summary>
/// <param name="Rule">Stable rule name (e.g. <c>modal-open</c>, <c>pick-control-visible</c>).</param>
/// <param name="Rationale">One plain sentence: what was observed in this state that supports the assertion.</param>
/// <param name="Confidence"><see cref="Confidences"/>.</param>
internal sealed record AssertionCandidate(string ProposalId, AssertionSpec Spec, string Origin, long? PickId, string Rule, string Rationale,
    string Confidence, IReadOnlyList<string> Flags);

// ── B: validation ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <param name="ReplayWidth">Grid width at the checkpoint during replay (after any recorded resize step before it).</param>
/// <param name="TypedTexts">Text the user typed during the recording (text runs). Never allowed into an assertion.</param>
/// <param name="MaximumSelected">At most this many suggestions are selected by default per checkpoint.</param>
internal sealed record ValidationContext(string ProposalId, string CheckpointId, int ReplayWidth, int ReplayHeight, IReadOnlyList<string> TypedTexts,
    int MaximumSelected = 4);

/// <summary>Validator status of a suggestion.</summary>
internal static class SuggestionStatuses
{
    /// <summary>Exact v1 assertion, unique selector, PASS through the real evaluator on the same evidence: selectable.</summary>
    internal const string Valid = "valid";
    /// <summary>Well-formed but not decidable or not passing on its own evidence (UNSUPPORTED/FAIL, ambiguous, truncated): shown, never selectable.</summary>
    internal const string Unsupported = "unsupported";
    /// <summary>Withheld for safety (sensitive/dynamic text, volatile selector, invalid v1, duplicate): shown with the reason, never selectable.
    /// Its <see cref="AssertionSuggestion.Spec"/> has sensitive values redacted.</summary>
    internal const string Rejected = "rejected";
}

/// <summary>A validated suggestion. <see cref="Spec"/> is final: valid unique deterministic Id, Severity <c>warning</c>,
/// v1-valid fields only (redacted when <see cref="Status"/> is <c>rejected</c> for sensitivity).</summary>
/// <param name="Outcome">The evaluator's outcome on the captured evidence (PASS / FAIL / UNSUPPORTED), null when not evaluated.</param>
/// <param name="Reason">Why it is not valid (unsupported/rejected), else null. Never contains the sensitive text.</param>
internal sealed record AssertionSuggestion(string ProposalId, AssertionSpec Spec, string Origin, long? PickId, string Rule, string Rationale,
    string Confidence, string Status, bool DefaultSelected, string? Outcome, string? Reason, IReadOnlyList<string> Flags)
{
    internal string Id => Spec.Id;
    internal bool Selectable => Status == SuggestionStatuses.Valid;
    /// <summary>For a layout-audit suggestion, the audit of the captured state (findings and coverage), else null.</summary>
    internal LayoutAuditReport? Layout { get; init; }
}

// ── facade output (coordinator) ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>Suggestions for one checkpoint proposal. <see cref="CaptureOnly"/>: no semantic assertion is possible (the
/// checkpoint is still a valid capture), <see cref="CaptureOnlyReason"/> says why.</summary>
internal sealed record CheckpointSuggestions(CheckpointProposal Proposal, CheckpointEvidence Evidence, IReadOnlyList<AssertionSuggestion> Suggestions,
    bool CaptureOnly, string? CaptureOnlyReason, int ReplayWidth, int ReplayHeight);

/// <summary>Coordinator facade: evidence → proposer (A) → validator (B) for every proposal of an analysis. Pure.</summary>
internal static class AssertionSuggester
{
    internal static IReadOnlyList<CheckpointSuggestions> Suggest(SmartRecordingAnalysis analysis, IReadOnlyList<string> typedTexts,
        int initialWidth, int initialHeight, ProposerOptions? options = null)
    {
        if (analysis.Input is not { } input) return [];
        var result = new List<CheckpointSuggestions>();
        var audited = new Dictionary<ulong, string>();   // Screen hash -> checkpoint whose layout audit covers it
        foreach (var proposal in analysis.Plan.Proposals)
        {
            var evidence = CheckpointEvidenceResolver.Resolve(proposal, input.Boundaries, input.Snapshots, input.Inspections, input.Capture.CoverageComplete);
            var (w, h) = GridAfter(analysis.Plan.Steps, proposal.InsertAfterStep, initialWidth, initialHeight);
            var candidate = proposal.CandidateIds.Count > 0 ? analysis.Detection.Candidates.FirstOrDefault(c => c.Id == proposal.CandidateIds[0]) : null;
            string? captureOnly = proposal.Step is null ? "The checkpoint cannot be placed in the step list."
                : evidence.Cells is null ? "No screen of this state was captured (" + (evidence.Flags.Count > 0 ? string.Join(", ", evidence.Flags) : proposal.Evidence) + "); the checkpoint is a capture only."
                : !evidence.Semantic ? "Only the cells of this state were captured (no complete inspection); no view, menu or status assertion can be checked."
                : null;
            IReadOnlyList<AssertionSuggestion> suggestions = [];
            if (proposal.Step is not null && evidence.Cells is not null)
            {
                var input2 = new ProposerInput(proposal, evidence, candidate);
                var candidates = AssertionProposer.Propose(input2, options).ToList();
                // One composite layout-audit suggestion per applicable state (ON in Smart recording).
                if (LayoutSuggestions && LayoutAuditProposer.Propose(input2, audited) is { } layout) candidates.Add(layout);
                suggestions = AssertionSuggestionValidator.Validate(candidates, evidence,
                    new ValidationContext(proposal.Id, proposal.CheckpointId, w, h, typedTexts));
            }
            result.Add(new(proposal, evidence, suggestions, evidence.Cells is null || proposal.Step is null, captureOnly, w, h));
        }
        return result;
    }

    /// <summary>Layout-audit suggestions are part of Smart recording (default ON; test seam).</summary>
    internal static bool LayoutSuggestions { get; set; } = true;

    /// <summary>Grid in effect after step <paramref name="index"/> (-1 = before the first) of <paramref name="steps"/>.</summary>
    internal static (int Width, int Height) GridAfter(IReadOnlyList<RecordedStep> steps, int index, int width, int height)
    {
        for (int i = 0; i <= index && i < steps.Count; i++)
            if (steps[i].Step.Kind == "resize" && int.TryParse(steps[i].Step.String("width"), out int w) && int.TryParse(steps[i].Step.String("height"), out int h))
                (width, height) = (w, h);
        return (width, height);
    }

    /// <summary>Text the user typed in a recording (the converter's text runs plus every printable key text), for redaction.</summary>
    internal static IReadOnlyList<string> TypedTexts(IReadOnlyList<RecordedInput> events, RecordingConversion conversion)
    {
        var texts = new List<string>();
        foreach (var s in conversion.Steps) if (s.Step.Kind == "text" && s.Step.String("text") is { Length: > 0 } t) texts.Add(t);
        var run = new StringBuilder();
        foreach (var e in events)
        {
            if (e.Kind == "key" && e.Text is { Length: > 0 } c && InputSteps.IsValidText(c)) { run.Append(c); continue; }
            if (run.Length > 0) { texts.Add(run.ToString()); run.Clear(); }
        }
        if (run.Length > 0) texts.Add(run.ToString());
        return texts.Distinct(StringComparer.Ordinal).ToArray();
    }
}

/// <summary>Schema-v1 JSON of an <see cref="AssertionSpec"/> ("id", "kind", "severity", then the kind's fields in
/// <see cref="ScenarioSchemaV1.AssertionFields"/> order; selectors as objects with their non-null members) and back
/// through the runtime's own parser. Never writes a field the kind does not allow.</summary>
internal static class AssertionJson
{
    internal static JsonObject ToJson(AssertionSpec spec)
    {
        var o = new JsonObject { ["id"] = spec.Id, ["kind"] = spec.Kind, ["severity"] = spec.Severity };
        foreach (string field in ScenarioSchemaV1.AssertionFields(spec.Kind))
        {
            JsonNode? value = field switch
            {
                "view" => Selector(spec.View), "other" => Selector(spec.Other), "text" => Str(spec.Text), "state" => Str(spec.State),
                "edge" => Str(spec.Edge), "x" => Num(spec.X), "y" => Num(spec.Y), "width" => Num(spec.Width), "value" => Num(spec.Value),
                "min" => Num(spec.Min), "max" => Num(spec.Max), "level" => Num(spec.Level), "profile" => Str(spec.Profile),
                "rules" => spec.Rules is { } rules ? new JsonArray(rules.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()) : null,
                "suppress" => spec.Suppress is { } suppress ? new JsonArray(suppress.Select(x => (JsonNode?)Suppression(x)).ToArray()) : null, _ => null,
            };
            if (value is not null) o[field] = value;
        }
        return o;
    }

    private static JsonValue? Str(string? s) => s is null ? null : JsonValue.Create(s);
    // {"rule", ["view"], "reason"} (schema v1 layout-audit suppression).
    private static JsonObject Suppression(LayoutSuppression s)
    {
        var o = new JsonObject { ["rule"] = s.Rule };
        if (Selector(s.View) is { } view) o["view"] = view;
        o["reason"] = s.Reason;
        return o;
    }
    private static JsonValue? Num(int? n) => n is null ? null : JsonValue.Create(n.Value);
    private static JsonObject? Selector(ViewSelector? s)
    {
        if (s is null) return null;
        var o = new JsonObject();
        if (s.Type is not null) o["type"] = s.Type;
        if (s.Id is not null) o["id"] = s.Id;
        if (s.Text is not null) o["text"] = s.Text;
        if (s.Path is not null) o["path"] = s.Path;
        return o;
    }

    /// <summary>A checkpoint step with these assertions (schema v1: kind, id, [note], assertions).</summary>
    internal static ScenarioDocumentStep Checkpoint(string id, string? note, IEnumerable<JsonObject> assertions)
    {
        var step = ScenarioDocumentStep.Checkpoint(id, note);
        var list = new JsonArray(assertions.Select(a => (JsonNode?)a.DeepClone()).ToArray());
        return list.Count == 0 ? step : step.With("assertions", list);
    }

    /// <summary>Parses assertion JSON objects with the RUNTIME parser (ExternalScenarios, schema v1) inside one checkpoint of a
    /// minimal proxy-logical document of the given grid. Returns the specs, or the parser's error message.</summary>
    internal static (AssertionSpec[]? Specs, string? Error) Parse(IReadOnlyList<JsonObject> assertions, int width, int height, string checkpointId = "check")
    {
        var steps = new JsonArray(Checkpoint(checkpointId, null, assertions).ToJson());
        var doc = new JsonObject
        {
            ["schema"] = ExternalScenarios.Schema, ["version"] = 1, ["id"] = "validation", ["title"] = "Validation",
            ["description"] = "In-memory document used to validate suggested assertions.", ["backend"] = ScenarioSchemaV1.ProxyBackend,
            ["grid"] = new JsonObject
            {
                ["minWidth"] = width, ["minHeight"] = height, ["maxWidth"] = width, ["maxHeight"] = height,
                ["defaults"] = new JsonArray(ScenarioSelection.Format(width, height)),
            },
            ["expectedOutcome"] = "passed", ["steps"] = steps,
        };
        try
        {
            var file = ExternalScenarios.LoadFromBytes(Path.GetFullPath("validation.scenario.json"), Encoding.UTF8.GetBytes(doc.ToJsonString()));
            var step = file.Scenario.Steps.FirstOrDefault(s => s.Kind == ScenarioStep.Checkpoint);
            return (step?.Assertions ?? [], null);
        }
        catch (Exception ex) when (ex is ExternalScenarioException or ArgumentException or JsonException or IOException) { return (null, ex.Message); }
    }
}
