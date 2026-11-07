using System.Text;
using System.Text.Json.Nodes;
using TSharpWorkbench.Testing;

// Row and detail text of the Review Proposals dialog. Pure formatting over RecordingReviewModel; never
// shows the values of a withheld suggestion (only its ID, kind and reason), even though the validator already redacts them.

internal enum ReviewRowKind { Summary, Empty, Checkpoint, Assertion, EditedStepAssertion }

/// <summary>One list row: the summary, the empty-state line, a checkpoint proposal, one of its suggested assertions, or an
/// assertion of a checkpoint edited in the step editor (<paramref name="EditedJson"/>).</summary>
internal sealed record ReviewRow(ReviewRowKind Kind, ReviewCheckpoint? Checkpoint, ReviewAssertion? Assertion, JsonObject? EditedJson);

internal static class ReviewRowText
{
    internal static string Placement(CheckpointProposal p) => p.Step is null || p.Placement == Placements.Unrepresentable ? "cannot place"
        : p.InsertAfterStep < 0 ? "before step 1" : $"after step {p.InsertAfterStep + 1}";

    internal static string Status(ReviewAssertion a) => a.Edited is not null ? "edited"
        : a.Suggestion.Status switch { SuggestionStatuses.Valid => "", SuggestionStatuses.Unsupported => "not checkable", SuggestionStatuses.Rejected => "withheld", var s => s };

    /// <summary>A child of a checkpoint that is not accepted is inactive. It never looks selected (mark
    /// <c>[-]</c>, status <c>inactive</c>); the model keeps its selection, which applies once the checkpoint is accepted.</summary>
    internal static bool Inactive(ReviewRow row) => row.Kind is ReviewRowKind.Assertion or ReviewRowKind.EditedStepAssertion
        && row.Checkpoint is { Accepted: false } && (row.Assertion?.Selectable ?? true);

    /// <summary>The status column of an assertion row: inactive children say so (and whether they stay selected).</summary>
    internal static string RowStatus(ReviewRow row) => Inactive(row)
        ? row.Assertion is { Selected: true } ? "inactive(sel)" : "inactive"
        : row.Assertion is { } b ? Status(b) : "edited step";

    internal static string Mark(ReviewRow row) => row.Kind switch
    {
        ReviewRowKind.Checkpoint => !row.Checkpoint!.Representable ? "[-]" : row.Checkpoint.Accepted ? "[x]" : "[ ]",
        ReviewRowKind.Assertion => !row.Assertion!.Selectable || Inactive(row) ? "[-]" : row.Assertion.Selected ? "[x]" : "[ ]",
        ReviewRowKind.EditedStepAssertion => Inactive(row) ? "[-]" : "[=]",
        _ => "   ",
    };

    /// <summary>Column header aligned with checkpoint rows (with the list label's mnemonic).</summary>
    internal static string Header(int width) => "     " + WorkbenchText.Column("Pr~o~posal", 24) + " " + WorkbenchText.Column("Placement", 14) + " "
        + WorkbenchText.Column("Kind", 12) + " " + WorkbenchText.Column("Conf.", 6) + " " + (width >= 90 ? WorkbenchText.Column("Evidence", 10) + " " : "") + "Assertions, flags";

    internal static string Format(ReviewRow row, RecordingReviewModel model, int width)
    {
        switch (row.Kind)
        {
            case ReviewRowKind.Summary:
                return $"    Summary: {model.Checkpoints.Count} proposal(s), {model.AcceptedCount} accepted, {model.SelectedAssertionCount} assertion(s) to insert"
                    + $" (all {model.Summary().Count} summary lines in Details)";
            case ReviewRowKind.Empty:
                return "    (no checkpoint proposals: the recording is inserted as inputs only)";
            case ReviewRowKind.Checkpoint:
            {
                var c = row.Checkpoint!; var p = c.Proposal;
                bool wide = width >= 90;
                var flags = new List<string>();
                int total = c.EditedStep?.AssertionCount ?? c.Assertions.Count(a => a.Selectable);
                if (c.Suggestions.CaptureOnly) flags.Add("capture only");
                else if (c.EditedStep is null && c.Assertions.Count == 0) flags.Add("no assertion");
                else flags.Add($"{c.SelectedAssertions}/{total} sel" + (c.Accepted || !c.Representable ? "" : " if accepted"));
                if (p.NeedsReview) flags.Add("NEEDS REVIEW");
                if (c.EditedStep is not null || c.IdEdited) flags.Add("edited");
                if (p.Picks.Count > 0) flags.Add(p.Picks.Count == 1 ? "1 pick" : $"{p.Picks.Count} picks");
                return Mark(row) + " " + WorkbenchText.Column(c.CheckpointId, 22) + " " + WorkbenchText.Column(Placement(p), 14) + " "
                    + WorkbenchText.Column(p.Kind, 12) + " " + WorkbenchText.Column(p.Confidence, 6) + " "
                    + (wide ? WorkbenchText.Column(p.Evidence, 10) + " " : "") + string.Join(", ", flags);
            }
            case ReviewRowKind.Assertion:
            {
                var a = row.Assertion!; var s = a.Suggestion;
                string target = s.Status == SuggestionStatuses.Rejected && a.Edited is null ? "(values withheld)" : Target(a.Json);
                return "    " + Mark(row) + " " + WorkbenchText.Column(a.Kind, 16) + " " + WorkbenchText.Column(s.Origin, 5) + " "
                    + WorkbenchText.Column(s.Confidence, 6) + " " + WorkbenchText.Column(RowStatus(row), 13) + " " + target;
            }
            case ReviewRowKind.EditedStepAssertion:
            {
                var o = row.EditedJson!;
                return "    " + Mark(row) + " " + WorkbenchText.Column(Str(o, "kind") ?? "?", 16) + " " + WorkbenchText.Column("step", 5) + " "
                    + WorkbenchText.Column("", 6) + " " + WorkbenchText.Column(RowStatus(row), 13) + " " + Target(o);
            }
        }
        return "";
    }

    /// <summary>A short target of an assertion: selectors, text, position and values (never the ID or severity).</summary>
    internal static string Target(JsonObject o)
    {
        var parts = new List<string>();
        foreach (string field in new[] { "view", "other" })
            if (o[field] is JsonObject s)
            {
                string sel = (Str(s, "type") ?? "*") + (Str(s, "id") is { } id ? "#" + id : "") + (Str(s, "text") is { } t ? " \"" + t + "\"" : "")
                    + (Str(s, "path") is { } path ? " path " + path : "");
                parts.Add(field == "other" ? "vs " + sel : sel);
            }
        if (Str(o, "text") is { } text) parts.Add("\"" + text + "\"");
        if (o["x"] is not null || o["y"] is not null) parts.Add($"at {o["x"]?.ToJsonString() ?? "?"},{o["y"]?.ToJsonString() ?? "?"}");
        foreach (string field in new[] { "state", "edge", "value", "level", "min", "max", "width", "profile" })
            if (o[field] is JsonValue v) parts.Add($"{field} {(v.TryGetValue(out string? str) ? str : v.ToJsonString())}");
        // Layout-audit: no scope = the modal view (or the screen); rule filter and suppressions in short.
        if (Str(o, "kind") == AssertionKinds.LayoutAudit)
        {
            if (o["view"] is null) parts.Insert(0, "scope: modal view or screen");
            if (o["rules"] is JsonArray rules) parts.Add(rules.Count <= 2 ? "rules " + string.Join(",", rules.Select(r => r?.ToString())) : $"{rules.Count} rules");
            if (o["suppress"] is JsonArray suppress && suppress.Count > 0) parts.Add($"{suppress.Count} suppressed");
        }
        return parts.Count == 0 ? "(no fields)" : string.Join(" ", parts);
    }

    /// <summary>Layout findings shown per suggestion in the detail pane (the rest is counted).</summary>
    internal const int LayoutDetailFindings = 5, LayoutDetailGaps = 4;

    // The audit of the recorded state behind a layout-audit suggestion: profile, counts, top findings, coverage gaps.
    internal static void LayoutDetail(StringBuilder b, LayoutAuditReport report)
    {
        var c = report.Coverage;
        int info = report.Findings.Length - report.Errors - report.Warnings;
        b.AppendLine($"Layout audit of the recorded state: profile {c.Profile}, scope {c.Scope ?? "screen"} ({c.AuditedNodes} of {c.ScopeNodes} view(s) audited); "
            + $"{report.Errors} error, {report.Warnings} warning, {info} info finding(s)" + (c.Suppressed.Length > 0 ? $", {c.Suppressed.Length} suppressed" : "") + ".");
        if (report.Findings.Length == 0) b.AppendLine("Findings: none.");
        else
        {
            b.AppendLine(report.Findings.Length > LayoutDetailFindings ? $"Top {LayoutDetailFindings} findings:" : "Findings:");
            foreach (var f in report.Findings.Take(LayoutDetailFindings))
                b.AppendLine($"  [{f.Class}] {f.Rule} at {f.Rect} on {f.View}: {f.Message}");
            if (report.Findings.Length > LayoutDetailFindings) b.AppendLine($"  ... {report.Findings.Length - LayoutDetailFindings} more.");
        }
        if (c.Complete) b.AppendLine($"Coverage: complete ({c.Evaluated.Length} rule(s) evaluated, {c.NotApplicable.Length} not applicable).");
        else
        {
            b.AppendLine($"Coverage: PARTIAL ({c.Evaluated.Length} evaluated, {c.NotApplicable.Length} not applicable, {c.Unavailable.Length} gap(s)"
                + (c.Truncated ? $", tree truncated: {c.OmittedNodes} view(s) not captured" : "") + "):");
            foreach (var g in c.Unavailable.Take(LayoutDetailGaps)) b.AppendLine($"  {g.Rule}{(g.Path is { } p ? " on " + p : "")}: {g.Reason}");
            if (c.Unavailable.Length > LayoutDetailGaps) b.AppendLine($"  ... {c.Unavailable.Length - LayoutDetailGaps} more gap(s).");
        }
        b.AppendLine("Enter (Edit) changes the scope, profile, rules or suppressions; the edited assertion is checked by the schema v1 parser.");
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // ── detail pane ──────────────────────────────────────────────────────────────────────────────────────────

    internal static string Detail(ReviewRow row, RecordingReviewModel model)
    {
        var b = new StringBuilder();
        switch (row.Kind)
        {
            case ReviewRowKind.Summary:
            case ReviewRowKind.Empty:
                foreach (string line in model.Summary()) b.AppendLine(line);
                if (model.Checkpoints.Count == 0)
                    b.AppendLine().AppendLine("Smart capture proposed no checkpoint for this recording. Done keeps the recording input-only; "
                        + "add checkpoints by hand in the scenario editor if you need them.");
                else
                    b.AppendLine().AppendLine("Safe high-confidence proposals are preselected with their high-confidence assertions; everything else "
                        + "starts not accepted. Space accepts or rejects a proposal (its assertions are inactive while it is not accepted) and "
                        + "selects an assertion; Done keeps the decisions, Cancel restores them. Nothing is inserted until you choose Append, "
                        + "Insert at selection or Replace selection in the recording window.");
                break;
            case ReviewRowKind.Checkpoint:
            {
                var c = row.Checkpoint!; var p = c.Proposal; var e = c.Evidence;
                b.AppendLine($"Checkpoint {c.CheckpointId} (proposal {p.Id}): " + (!c.Representable ? "cannot be accepted." : c.Accepted
                    ? "ACCEPTED" + (c.Preselected ? " (preselected: high confidence; Space rejects it)." : ".") : "not accepted (Space accepts it; its assertions are inactive until then)."));
                b.AppendLine("Placement: " + (c.Representable ? Placement(p) + (p.Placement == Placements.Split ? " (a recorded step is split at this point; the replay is unchanged)" : "")
                    : "cannot be placed without changing the replay. " + p.Reason));
                b.AppendLine($"Kind: {p.Kind}. Confidence: {p.Confidence}." + (p.NeedsReview ? " NEEDS REVIEW: check that this state is what the test should expect." : ""));
                if (p.Reason.Length > 0 && c.Representable) b.AppendLine("Why: " + p.Reason);
                b.AppendLine($"Evidence: {p.Evidence}; source {e.Source}" + (e.SnapshotId is { } sid ? $" (snapshot {sid})" : "")
                    + (e.Semantic ? ", cells and inspection" : e.Cells is not null ? ", cells only" : ", no cells") + ".");
                var flags = p.Flags.Concat(e.Flags).Distinct().ToArray();
                if (flags.Length > 0) b.AppendLine("Flags: " + string.Join(", ", flags) + ".");
                if (c.Suggestions.CaptureOnlyReason is { } why) b.AppendLine("Capture only: " + why);
                if (p.Picks.Count > 0) b.AppendLine($"Picks: {string.Join(", ", p.Picks.Select(k => $"#{k.PickId} {k.Kind}{(k.Target is { } t ? " " + t.Area : "")}"))}.");
                var all = c.Assertions;
                b.AppendLine($"Assertions: {all.Count(a => a.Suggestion.Selectable)} checkable, {all.Count(a => a.Suggestion.Status == SuggestionStatuses.Unsupported)} not checkable, "
                    + $"{all.Count(a => a.Suggestion.Status == SuggestionStatuses.Rejected)} withheld; {c.SelectedAssertions} selected.");
                if (c.EditedStep is { } step) b.AppendLine("Edited in the step editor; the edited step replaces the suggestions:").AppendLine(step.ToJson().ToJsonString());
                else if (c.IdEdited) b.AppendLine("Checkpoint ID edited.");
                break;
            }
            case ReviewRowKind.Assertion:
            {
                var c = row.Checkpoint!; var a = row.Assertion!; var s = a.Suggestion;
                b.AppendLine($"Assertion {a.Id} ({a.Kind}) of {c.CheckpointId}: " + (!a.Selectable ? "cannot be selected."
                    : Inactive(row) ? $"INACTIVE: checkpoint {c.CheckpointId} is not accepted, so nothing of it is inserted. Your choice ("
                        + (a.Selected ? "selected" : "not selected") + ") is kept and applies when you accept the checkpoint."
                    : a.Selected ? "SELECTED." : "not selected (Space selects it)."));
                b.AppendLine("Status: " + s.Status switch
                {
                    SuggestionStatuses.Valid => "checkable",
                    SuggestionStatuses.Unsupported => "not checkable",
                    SuggestionStatuses.Rejected => "withheld for safety",
                    var x => x,
                } + (s.Outcome is { } outcome ? $"; evaluator on the captured state: {outcome}" : "; not evaluated") + "."
                  + (a.Edited is not null ? " Edited by you (checked by the schema v1 parser)." : ""));
                if (s.Reason is { Length: > 0 } reason) b.AppendLine("Reason: " + reason);
                b.AppendLine($"Origin: {(s.Origin == SuggestionOrigins.Pick ? $"pick #{s.PickId}" : "state change")}, rule {s.Rule}. Confidence: {s.Confidence}.");
                if (s.Rationale.Length > 0) b.AppendLine("Rationale: " + s.Rationale);
                if (s.Flags.Count > 0) b.AppendLine("Flags: " + string.Join(", ", s.Flags) + ".");
                if (s.Layout is { } layout && a.Edited is null) LayoutDetail(b, layout);
                if (s.Status == SuggestionStatuses.Rejected && a.Edited is null)
                    b.AppendLine("Values are not shown for a withheld suggestion. Add your own assertion in the scenario editor if you need one.");
                else b.AppendLine("Schema v1: " + a.Json.ToJsonString());
                break;
            }
            case ReviewRowKind.EditedStepAssertion:
                b.AppendLine($"Assertion of the edited checkpoint {row.Checkpoint!.CheckpointId}. Edit the checkpoint to change it."
                    + (Inactive(row) ? " INACTIVE: the checkpoint is not accepted, so it is not inserted." : ""));
                b.AppendLine("Schema v1: " + row.EditedJson!.ToJsonString());
                break;
        }
        return b.ToString().TrimEnd().Replace("\r", "");
    }
}
