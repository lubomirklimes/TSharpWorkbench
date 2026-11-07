using System.Text.Json.Nodes;
using TSharpWorkbench.Testing;

// The review model of a stopped Smart recording and its materialization into schema-v1 steps.
// Safe high-confidence items are PRESELECTED (an assertion that is valid, high confidence and in the
// validator's default selection; a checkpoint that is representable, high confidence, needs no review and has at least one
// preselected assertion). Nothing is inserted or saved automatically: a checkpoint is inserted only when accepted at Append/
// Insert/Replace, and its assertions only when selected. With nothing accepted, the steps are exactly the converter's (the
// legacy input-only result). The model is UI-thread state; Save/Restore support Cancel.

/// <summary>One suggested assertion under review.</summary>
internal sealed class ReviewAssertion
{
    internal ReviewAssertion(AssertionSuggestion suggestion) { Suggestion = suggestion; Selected = Preselected = Safe(suggestion); }
    /// <summary>Preselection follows the validated status and confidence only (unsafe, ambiguous, unsupported, withheld,
    /// low or medium confidence are never preselected).</summary>
    internal static bool Safe(AssertionSuggestion s) => s.Selectable && s.DefaultSelected && s.Confidence == Confidences.High;
    /// <summary>The review preselected it; the user may deselect it.</summary>
    internal bool Preselected { get; }
    internal AssertionSuggestion Suggestion { get; }
    /// <summary>The user's edited JSON (validated by the runtime parser), else null.</summary>
    internal JsonObject? Edited { get; set; }
    internal bool Selected { get; set; }
    /// <summary>Valid suggestions and edited ones can be selected; unsupported and withheld ones cannot.</summary>
    internal bool Selectable => Edited is not null || Suggestion.Selectable;
    /// <summary>Withheld (rejected) suggestions are never editable: their values may be redacted.</summary>
    internal bool Editable => Suggestion.Status != SuggestionStatuses.Rejected;
    internal JsonObject Json => (JsonObject)(Edited ?? AssertionJson.ToJson(Suggestion.Spec)).DeepClone();
    internal string Id => Edited?["id"]?.GetValue<string>() ?? Suggestion.Id;
    internal string Kind => Edited?["kind"]?.GetValue<string>() ?? Suggestion.Spec.Kind;
}

/// <summary>One checkpoint proposal under review.</summary>
internal sealed class ReviewCheckpoint
{
    internal ReviewCheckpoint(CheckpointSuggestions suggestions)
    {
        Suggestions = suggestions;
        CheckpointId = suggestions.Proposal.CheckpointId;
        Note = suggestions.Proposal.Step?.Note;
        Assertions = suggestions.Suggestions.Select(s => new ReviewAssertion(s)).ToArray();
        Accepted = Preselected = Representable && suggestions.Proposal.Confidence == Confidences.High && !suggestions.Proposal.NeedsReview
            && !suggestions.CaptureOnly && Assertions.Any(a => a.Selected);
    }
    /// <summary>The review preselected (accepted) it; the user may reject it.</summary>
    internal bool Preselected { get; }
    internal CheckpointSuggestions Suggestions { get; }
    internal CheckpointProposal Proposal => Suggestions.Proposal;
    internal CheckpointEvidence Evidence => Suggestions.Evidence;
    internal IReadOnlyList<ReviewAssertion> Assertions { get; }
    /// <summary>Only representable proposals can be accepted (unrepresentable ones are never inserted or relocated).</summary>
    internal bool Representable => Proposal.Step is not null && Proposal.Placement != Placements.Unrepresentable;
    internal bool Accepted { get; set; }
    internal string CheckpointId { get; set; }
    internal string? Note { get; set; }
    /// <summary>True once the user changed the checkpoint ID (an edited ID is never renamed automatically on collision).</summary>
    internal bool IdEdited { get; set; }
    /// <summary>The whole checkpoint as edited in the step editor; overrides the ID, note and assertion selection.</summary>
    internal ScenarioDocumentStep? EditedStep { get; set; }
    internal int SelectedAssertions => EditedStep?.AssertionCount ?? Assertions.Count(a => a.Selected);

    /// <summary>The schema-v1 checkpoint step this proposal becomes when accepted.</summary>
    internal ScenarioDocumentStep BuildStep(string? id = null)
    {
        if (EditedStep is { } edited) return id is null ? edited : edited.WithText("id", id);
        return AssertionJson.Checkpoint(id ?? CheckpointId, Note, Assertions.Where(a => a.Selected).Select(a => a.Json));
    }
}

/// <summary>Review state of a stopped Smart recording (see file header).</summary>
internal sealed class RecordingReviewModel
{
    internal RecordingReviewModel(SmartRecordingAnalysis analysis, IReadOnlyList<CheckpointSuggestions> suggestions, RecordingConversion conversion,
        IReadOnlyList<string> status)
    {
        Analysis = analysis; Conversion = conversion; Status = status;
        Checkpoints = suggestions.OrderBy(s => s.Proposal.InsertAfterStep).ThenBy(s => s.Proposal.BoundaryId).Select(s => new ReviewCheckpoint(s)).ToArray();
    }

    internal SmartRecordingAnalysis Analysis { get; }
    internal RecordingConversion Conversion { get; }
    /// <summary>Honest capture status lines (Smart capture, Proxy features, coverage, backend qualification).</summary>
    internal IReadOnlyList<string> Status { get; }
    internal IReadOnlyList<ReviewCheckpoint> Checkpoints { get; }
    internal bool AnyAccepted => Checkpoints.Any(c => c.Accepted);
    internal int AcceptedCount => Checkpoints.Count(c => c.Accepted);
    internal int SelectedAssertionCount => Checkpoints.Where(c => c.Accepted).Sum(c => c.SelectedAssertions);

    // Decision 4: what Append/Insert/Replace would leave out. "Pending" = the user could still accept it; "not insertable"
    // = it can never be inserted (unrepresentable checkpoint, unsupported or withheld assertion), whatever the user decides.
    /// <summary>Representable checkpoint proposals that are not accepted.</summary>
    internal int PendingCheckpoints => Checkpoints.Count(c => c.Representable && !c.Accepted);
    /// <summary>Selectable assertions of accepted checkpoints that are not selected (edited checkpoints decide themselves).</summary>
    internal int PendingAssertions => Checkpoints.Where(c => c.Accepted && c.EditedStep is null).Sum(c => c.Assertions.Count(a => a.Selectable && !a.Selected));
    internal int UnrepresentableCheckpoints => Checkpoints.Count(c => !c.Representable);
    internal int NotInsertableAssertions => Checkpoints.Sum(c => c.Assertions.Count(a => !a.Selectable));
    internal bool AnyPending => PendingCheckpoints + PendingAssertions > 0;

    /// <summary>Summary lines: counts, confidence, evidence, placement and the honest status.</summary>
    internal IReadOnlyList<string> Summary()
    {
        var lines = new List<string>();
        int n = Checkpoints.Count, unrep = Checkpoints.Count(c => !c.Representable), review = Checkpoints.Count(c => c.Proposal.NeedsReview);
        int captureOnly = Checkpoints.Count(c => c.Suggestions.CaptureOnly || c.Assertions.Count == 0);
        var all = Checkpoints.SelectMany(c => c.Assertions).ToArray();
        int valid = all.Count(a => a.Suggestion.Selectable), unsupported = all.Count(a => a.Suggestion.Status == SuggestionStatuses.Unsupported),
            withheld = all.Count(a => a.Suggestion.Status == SuggestionStatuses.Rejected);
        string Conf(string c) => $"{Checkpoints.Count(x => x.Proposal.Confidence == c)} {c}";
        int preselected = Checkpoints.Count(c => c.Preselected);
        lines.Add($"{n} checkpoint proposal{(n == 1 ? "" : "s")} ({Conf(Confidences.High)}, {Conf(Confidences.Medium)}, {Conf(Confidences.Low)} confidence); "
            + $"{AcceptedCount} accepted" + (preselected > 0 ? $" ({preselected} preselected: high confidence)" : "") + ".");
        lines.Add($"{valid} suggested assertion{(valid == 1 ? "" : "s")} ({SelectedAssertionCount} selected in accepted checkpoints)"
            + (unsupported > 0 ? $", {unsupported} not checkable" : "") + (withheld > 0 ? $", {withheld} withheld" : "") + ".");
        if (captureOnly > 0) lines.Add($"{captureOnly} checkpoint{(captureOnly == 1 ? " has" : "s have")} no assertion (capture only: missing or partial evidence).");
        if (unrep > 0) lines.Add($"{unrep} proposal{(unrep == 1 ? "" : "s")} cannot be placed without changing the replay; insert inputs only or add the checkpoint by hand.");
        if (review > 0) lines.Add($"{review} proposal{(review == 1 ? " needs" : "s need")} manual review.");
        lines.AddRange(Status);
        return lines;
    }

    // ── decisions ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Accepts or rejects a checkpoint. Returns why it cannot be accepted, else null.</summary>
    internal string? SetAccepted(ReviewCheckpoint checkpoint, bool accepted)
    {
        if (accepted && !checkpoint.Representable)
            return "This proposal cannot be inserted: " + (checkpoint.Proposal.Reason is { Length: > 0 } r ? r : "its position cannot be represented by a step boundary.")
                + " Insert the inputs only and add a checkpoint by hand if you need one.";
        checkpoint.Accepted = accepted;
        return null;
    }

    /// <summary>Selects or deselects one assertion. Returns why it cannot be selected, else null.</summary>
    internal static string? SetSelected(ReviewAssertion assertion, bool selected)
    {
        if (selected && !assertion.Selectable)
            return "This assertion cannot be added: " + (assertion.Suggestion.Reason ?? "it could not be checked on the captured evidence.");
        assertion.Selected = selected;
        return null;
    }

    /// <summary>Renames a checkpoint (valid name, unique among this review). Returns the problem, else null.</summary>
    internal string? Rename(ReviewCheckpoint checkpoint, string id)
    {
        if (!Protocol.IsValidName(id)) return "Use lowercase letters, digits and single hyphens (at most 48 characters), e.g. dialog-open.";
        if (Checkpoints.Any(c => !ReferenceEquals(c, checkpoint) && string.Equals(c.EditedStep?.String("id") ?? c.CheckpointId, id, StringComparison.OrdinalIgnoreCase)))
            return $"Another proposal already uses the checkpoint ID '{id}'.";
        if (checkpoint.EditedStep is { } edited) checkpoint.EditedStep = edited.WithText("id", id);
        checkpoint.CheckpointId = id; checkpoint.IdEdited = true;
        return null;
    }

    /// <summary>Stores a user-edited assertion after checking it with the runtime parser at the checkpoint's replay grid.</summary>
    internal static string? EditAssertion(ReviewCheckpoint checkpoint, ReviewAssertion assertion, JsonObject edited)
    {
        if (!assertion.Editable) return "A withheld suggestion cannot be edited; add your own assertion in the scenario editor instead.";
        string? id = edited["id"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        if (id is null || !Protocol.IsValidName(id)) return "The assertion ID must be lowercase words joined by hyphens.";
        if (checkpoint.Assertions.Any(a => !ReferenceEquals(a, assertion) && a.Selected && a.Id == id)) return $"Another selected assertion uses the ID '{id}'.";
        var (_, error) = AssertionJson.Parse([edited], checkpoint.Suggestions.ReplayWidth, checkpoint.Suggestions.ReplayHeight);
        if (error is not null) return "Not valid schema v1: " + error;
        assertion.Edited = (JsonObject)edited.DeepClone(); assertion.Selected = true;
        return null;
    }

    /// <summary>Stores a checkpoint edited in the step editor (must stay a checkpoint with a valid ID and v1 assertions).</summary>
    internal string? EditCheckpoint(ReviewCheckpoint checkpoint, ScenarioDocumentStep edited)
    {
        if (edited.Kind != "checkpoint" || edited.String("id") is not { } id) return "The edited step must stay a checkpoint with an ID.";
        if (id != checkpoint.CheckpointId && Rename(checkpoint, id) is { } problem) return problem;
        var assertions = edited.Node("assertions") is JsonArray a ? a.OfType<JsonObject>().ToArray() : [];
        if (assertions.Length > 0 && AssertionJson.Parse(assertions, checkpoint.Suggestions.ReplayWidth, checkpoint.Suggestions.ReplayHeight).Error is { } error)
            return "Not valid schema v1: " + error;
        checkpoint.EditedStep = edited; checkpoint.CheckpointId = id;
        return null;
    }

    /// <summary>Accepts every representable proposal (an explicit user action; the default assertion selection applies).</summary>
    internal void AcceptAll() { foreach (var c in Checkpoints) if (c.Representable) c.Accepted = true; }
    internal void RejectAll() { foreach (var c in Checkpoints) c.Accepted = false; }

    private sealed record State(bool Accepted, string Id, string? Note, bool IdEdited, ScenarioDocumentStep? Step, (bool Selected, JsonObject? Edited)[] Assertions);

    /// <summary>A copy of every decision (Cancel restores it).</summary>
    internal object Save() => Checkpoints.Select(c => new State(c.Accepted, c.CheckpointId, c.Note, c.IdEdited, c.EditedStep,
        c.Assertions.Select(a => (a.Selected, (JsonObject?)a.Edited?.DeepClone())).ToArray())).ToArray();

    internal void Restore(object saved)
    {
        var states = (State[])saved;
        for (int i = 0; i < Checkpoints.Count; i++)
        {
            var c = Checkpoints[i]; var s = states[i];
            c.Accepted = s.Accepted; c.CheckpointId = s.Id; c.Note = s.Note; c.IdEdited = s.IdEdited; c.EditedStep = s.Step;
            for (int j = 0; j < c.Assertions.Count; j++) { c.Assertions[j].Selected = s.Assertions[j].Selected; c.Assertions[j].Edited = s.Assertions[j].Edited; }
        }
    }
}

/// <summary>Result of <see cref="RecordingReviewMaterializer.Materialize"/>. When <see cref="Problems"/> is not empty
/// nothing may be inserted (the user fixes the review or inserts inputs only).</summary>
internal sealed record ReviewMaterialization(IReadOnlyList<ScenarioDocumentStep> Steps, int Checkpoints, int Assertions, IReadOnlyList<string> Renamed,
    IReadOnlyList<string> Problems)
{
    internal bool Ok => Problems.Count == 0;
}

/// <summary>Pure: the steps to insert for a review (inputs in recorded order, accepted checkpoints at their planned places).</summary>
internal static class RecordingReviewMaterializer
{
    /// <param name="existingCheckpointIds">Checkpoint IDs that stay in the document after the insertion (for Replace
    /// selection: the IDs outside the replaced range).</param>
    /// <param name="inputsOnly">Ignore every decision: the converter's steps exactly (legacy behaviour).</param>
    internal static ReviewMaterialization Materialize(RecordingReviewModel model, IReadOnlyCollection<string> existingCheckpointIds, bool inputsOnly = false)
    {
        var conversion = model.Conversion.Steps;
        var accepted = inputsOnly ? [] : model.Checkpoints.Where(c => c.Accepted).ToArray();
        if (accepted.Length == 0) return new(model.Conversion.DocumentSteps, 0, 0, [], []);
        var problems = new List<string>();
        foreach (var c in accepted.Where(c => !c.Representable)) problems.Add($"Proposal {c.CheckpointId} cannot be placed and was accepted; reject it.");

        // Checkpoint IDs: unique among themselves and against the document; generated IDs get a numbered suffix, edited ones are refused.
        var used = new HashSet<string>(existingCheckpointIds, StringComparer.OrdinalIgnoreCase);
        var renamed = new List<string>();
        var built = new Dictionary<ReviewCheckpoint, ScenarioDocumentStep>();
        foreach (var c in accepted.Where(c => c.Representable).OrderBy(c => c.Proposal.InsertAfterStep).ThenBy(c => c.Proposal.BoundaryId))
        {
            string id = c.EditedStep?.String("id") ?? c.CheckpointId;
            if (used.Contains(id))
            {
                if (c.IdEdited) { problems.Add($"The checkpoint ID '{id}' already exists in the scenario; rename it in the review."); continue; }
                string stem = id.Length > 44 ? id[..44].TrimEnd('-') : id, next = id;
                for (int n = 2; used.Contains(next); n++) next = stem + "-" + n;
                renamed.Add($"{id} -> {next}"); id = next;
            }
            used.Add(id);
            var step = c.BuildStep(id);
            var assertions = step.Node("assertions") is JsonArray a ? a.OfType<JsonObject>().ToArray() : [];
            if (assertions.Length > 0 && AssertionJson.Parse(assertions, c.Suggestions.ReplayWidth, c.Suggestions.ReplayHeight, id).Error is { } error)
            { problems.Add($"Checkpoint {id}: not valid schema v1 ({error})."); continue; }
            built[c] = step;
        }
        if (problems.Count > 0) return new([], 0, 0, renamed, problems);

        // Map plan steps (with splits) back to converter steps; a converter step is kept whole unless an accepted
        // checkpoint sits between two of its split parts.
        var plan = model.Analysis.Plan.Steps;
        var after = built.GroupBy(b => b.Key.Proposal.InsertAfterStep).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Key.Proposal.BoundaryId).Select(b => b.Value).ToArray());
        var output = new List<ScenarioDocumentStep>();
        if (after.TryGetValue(-1, out var first)) output.AddRange(first);
        int p = 0;
        foreach (var c in conversion)
        {
            int start = p;
            if (p < plan.Count && plan[p].First == c.First && plan[p].Last == c.Last && plan[p].Step.SameJson(c.Step)) p++;
            else
            {
                while (p < plan.Count) { var piece = plan[p++]; if (piece.Last == c.Last) break; }
                if (p - start < 2 || plan[p - 1].Last != c.Last || plan[start].First != c.First)
                    return new([], 0, 0, renamed, ["The checkpoint plan does not match the recorded steps; insert the inputs only."]);
            }
            bool inner = Enumerable.Range(start, p - start - 1).Any(after.ContainsKey);
            if (inner)
                for (int i = start; i < p; i++) { output.Add(plan[i].Step); if (after.TryGetValue(i, out var cps)) output.AddRange(cps); }
            else { output.Add(c.Step); if (after.TryGetValue(p - 1, out var cps)) output.AddRange(cps); }
        }
        if (p != plan.Count) return new([], 0, 0, renamed, ["The checkpoint plan does not match the recorded steps; insert the inputs only."]);
        return new(output, built.Count, built.Values.Sum(s => s.AssertionCount), renamed, []);
    }
}
