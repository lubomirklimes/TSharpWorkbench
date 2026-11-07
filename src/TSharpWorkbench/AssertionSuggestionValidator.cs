using TSharpWorkbench.Testing;

// Pure validation, safety, dedup and default selection.
//
// Gates, applied to every distinct candidate in order (the first failing gate decides the status and the reason):
//   1. kind      - only the kinds (contracts §2); status-item "absent" is not observed evidence      -> rejected
//   2. evidence  - no cells (capture-only)                                                                -> rejected
//                  cells only (no inspection / Omitted): only screen-contains and text-at                 -> rejected
//   3. safety    - ViewSelector.Path, typed text, input-line values, dynamic-looking text               -> rejected (+ redacted)
//                  screen-contains/text-at with < 2 non-blank characters (not distinctive)              -> rejected
//   2b. truncated view tree: a view selector cannot be proven unique                                     -> unsupported
//      (evaluated after the safety gate so that sensitive content is always rejected and redacted, fail closed)
//   4. v1        - the real parser (AssertionJson.Parse) on the replay grid                               -> rejected
//                  text-at only when the evidence grid equals the replay grid                             -> unsupported
//   5. unique    - every view selector matches exactly one node of Evidence.Tree                         -> unsupported
//   6. evaluator - AssertionEvaluator.Evaluate on the same evidence must be PASS                         -> unsupported
// Independently of the deciding gate, a candidate whose text is sensitive (typed, input-line value, dynamic) has every text
// field of its Spec replaced by "(redacted)", its id derived from the kind only, and its rationale scrubbed.
// Confidence: the candidate's confidence is lowered one level (high -> medium -> low) for EACH of: the evidence flag
// coverage-incomplete, the evidence flag view-tree-truncated (or a truncated tree), an evidence source other than snapshot.
// Default selection: status valid AND confidence high/medium, the first ValidationContext.MaximumSelected in input order.
// Layout-audit (coordinator): the same gates (kind, evidence, safety, truncation, v1, uniqueness), then the real evaluator:
// PASS with complete coverage -> valid/high; PASS with coverage gaps -> valid/medium at most; FAIL (hard findings in the
// recorded state) -> valid/low (shown with its findings, never preselected); UNSUPPORTED -> unsupported. A menu state or a
// screen already audited by an earlier proposal -> unsupported with the reason. It is outside MaximumSelected (one per state).

internal static partial class AssertionSuggestionValidator
{
    internal const string Redacted = "(redacted)";

    /// <summary>Kinds that may be suggested (contracts §2). view-absent is not suggested.</summary>
    internal static readonly string[] AllowedKinds =
    [
        AssertionKinds.ViewExists, AssertionKinds.ViewVisible, AssertionKinds.ViewFocused, AssertionKinds.Modal, AssertionKinds.NotClipped,
        AssertionKinds.WithinScreen, AssertionKinds.MenuDepth, AssertionKinds.MenuSelected, AssertionKinds.MenuItemState,
        AssertionKinds.SubmenuOpen, AssertionKinds.StatusItem, AssertionKinds.StatusHint, AssertionKinds.ScreenContains, AssertionKinds.TextAt,
        AssertionKinds.LayoutAudit,
    ];

    /// <summary>Kinds decidable on cells alone (allowed when the evidence is not semantic).</summary>
    internal static readonly string[] CellKinds = [AssertionKinds.ScreenContains, AssertionKinds.TextAt];

    /// <summary>Turns raw candidates into final suggestions (valid / unsupported / rejected), deterministic ids, severity
    /// warning, default selection. Same input → same output. Returns one suggestion per distinct candidate (duplicates dropped).</summary>
    internal static IReadOnlyList<AssertionSuggestion> Validate(IReadOnlyList<AssertionCandidate> candidates, CheckpointEvidence evidence,
        ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(context);

        // Exact duplicates (same kind and fields, ignoring id and severity) collapse into the first.
        var distinct = new List<AssertionCandidate>();
        var seen = new List<AssertionSpec>();
        foreach (var candidate in candidates)
        {
            if (candidate?.Spec is null) continue;
            var key = candidate.Spec with { Id = "", Severity = "" };
            if (seen.Contains(key)) continue;
            seen.Add(key);
            distinct.Add(candidate);
        }

        var sensitive = new Sensitivity(evidence, context);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AssertionSuggestion>(distinct.Count);
        int selected = 0;
        foreach (var candidate in distinct)
        {
            var findings = sensitive.Inspect(candidate.Spec);
            string id = UniqueId(findings.Count > 0 ? null : candidate.Spec.Id, candidate.Spec.Kind, used);
            var spec = candidate.Spec with { Id = id, Severity = AssertionSeverity.Warning };
            if (findings.Count > 0) spec = Redact(spec);

            var (status, reason, outcome, final) = Decide(spec, findings, evidence, context, sensitive);
            spec = final ?? spec;
            string confidence = Downgrade(candidate.Confidence, evidence);
            bool layout = spec.Kind == AssertionKinds.LayoutAudit;
            LayoutAuditReport? report = null;
            if (layout && status == SuggestionStatuses.Valid)
            {
                report = AssertionEvaluator.Evaluate([spec], evidence.Cells!, evidence.Inspection, "layout", context.CheckpointId).Single().Layout;
                if (outcome == AssertionOutcomes.Fail) confidence = Confidences.Low;
                else if (report is { Coverage.Complete: false } && confidence == Confidences.High) confidence = Confidences.Medium;
            }
            if (layout && LayoutApplicability(candidate.Flags) is { } skip) (status, reason, outcome) = (SuggestionStatuses.Unsupported, skip, null);
            bool select = status == SuggestionStatuses.Valid && (layout ? confidence == Confidences.High
                : confidence is Confidences.High or Confidences.Medium && selected < context.MaximumSelected);
            if (select && !layout) selected++;
            string rationale = sensitive.Scrub(candidate.Rationale ?? "", findings.Count > 0 ? TextsOf(candidate.Spec) : [], findings.Count > 0);
            if (report is not null) rationale += " " + LayoutSummary(report, outcome);
            var flags = (candidate.Flags ?? []).Concat(evidence.Flags.Where(f => f is EvidenceFlags.CoverageIncomplete or EvidenceFlags.TreeTruncated))
                .Distinct(StringComparer.Ordinal).ToArray();
            result.Add(new AssertionSuggestion(candidate.ProposalId, spec, candidate.Origin, candidate.PickId, candidate.Rule, rationale,
                confidence, status, select, outcome, reason, flags) { Layout = report });
        }
        return result;
    }

    private static (string Status, string? Reason, string? Outcome, AssertionSpec? Final) Decide(AssertionSpec spec, IReadOnlyList<string> findings,
        CheckpointEvidence evidence, ValidationContext context, Sensitivity sensitive)
    {
        const string Rejected = SuggestionStatuses.Rejected, Unsupported = SuggestionStatuses.Unsupported;

        // 1. kind
        if (!AllowedKinds.Contains(spec.Kind))
            return (Rejected, KindReason(spec.Kind), null, null);
        if (spec.Kind == AssertionKinds.StatusItem && spec.State == "absent")
            return (Rejected, "A status item's absence is not observed evidence of this state; only visible, enabled or disabled is suggested.", null, null);

        // 2. evidence
        if (evidence.Cells is null)
            return (Rejected, "No screen of this state was captured; the checkpoint is a capture only.", null, null);
        bool cellKind = CellKinds.Contains(spec.Kind);
        if (spec.Kind == AssertionKinds.LayoutAudit && (!evidence.Semantic || evidence.Tree is null))
            return (Unsupported, "The layout audit needs the view tree of this state, but only " + (evidence.Semantic ? "an inspection without a view tree" : "the cells")
                + " were captured; it cannot be suggested here.", null, null);
        if (!evidence.Semantic && !cellKind)
            return (Rejected, "Only the cells of this state were captured (no complete inspection); a " + spec.Kind
                + " assertion cannot be checked. Only screen-contains and text-at are possible.", null, null);

        // 3. safety
        if (spec.View?.Path is not null || spec.Other?.Path is not null)
            return (Rejected, "The selector uses a capture path, which is volatile and only valid for one capture; use a type, id or text selector.", null, null);
        if (findings.Count > 0)
            return (Rejected, "Withheld for safety: " + string.Join("; ", findings) + ". The text was redacted.", null, null);
        if (cellKind && spec.Text is { } cellText && cellText.Trim().Length < 2)
            return (Rejected, "The text is too short to identify this state (at least two non-blank characters).", null, null);

        // 2b. truncated tree: no uniqueness proof
        bool usesView = spec.View is not null || spec.Other is not null;
        if (usesView && evidence.TreeTruncated)
            return (Unsupported, "The captured view tree is truncated, so the selector cannot be proven to match exactly one view.", null, null);

        // 4. v1 validity on the replay grid
        var (specs, error) = AssertionJson.Parse([AssertionJson.ToJson(spec)], context.ReplayWidth, context.ReplayHeight);
        if (specs is not { Length: 1 })
            return (Rejected, "Not a valid schema-v1 assertion: " + sensitive.Scrub(error ?? "the parser returned no assertion", [], dynamic: true), null, null);
        var final = specs[0];
        if (final.Kind == AssertionKinds.TextAt && (evidence.Width != context.ReplayWidth || evidence.Height != context.ReplayHeight))
            return (Unsupported, $"The state was captured on a {evidence.Width}x{evidence.Height} grid but replays on "
                + $"{context.ReplayWidth}x{context.ReplayHeight}; an absolute text position is not suggested after a resize.", null, final);

        // 5. uniqueness of every view selector on the state's own tree
        foreach (var (selector, role) in new[] { (final.View, "view"), (final.Other, "other") })
        {
            if (selector is null) continue;
            if (evidence.Tree is not { } tree)
                return (Unsupported, "No view tree of this state was captured, so the " + role + " selector cannot be checked.", null, final);
            int matches = AssertionEvaluator.All(tree.Root).Count(selector.Matches);
            if (matches == 0) return (Unsupported, "The " + role + " selector matches no view of this state (no match).", null, final);
            if (matches > 1) return (Unsupported, $"The {role} selector is ambiguous: it matches {matches} views of this state.", null, final);
        }

        // 6. the real evaluator on the same evidence
        var result = AssertionEvaluator.Evaluate([final], evidence.Cells, evidence.Inspection, "validation", context.CheckpointId).Single();
        if (result.Outcome == AssertionOutcomes.Pass) return (SuggestionStatuses.Valid, null, result.Outcome, final);
        // A layout audit that finds hard problems in the recorded state is still worth reviewing (never preselected).
        if (final.Kind == AssertionKinds.LayoutAudit && result.Outcome == AssertionOutcomes.Fail && result.Layout is not null)
            return (SuggestionStatuses.Valid, null, result.Outcome, final);
        return (Unsupported, $"The evaluator reports {result.Outcome} on this state: {sensitive.Scrub(result.Explanation, [], dynamic: true)}", result.Outcome, final);
    }

    // Why a state gets no layout audit (flags from LayoutAuditProposer), else null.
    private static string? LayoutApplicability(IReadOnlyList<string>? flags)
    {
        if (flags is null) return null;
        if (flags.Contains(LayoutAuditProposer.MenuFlag))
            return "A menu is open in this state; the layout audit is suggested for the states before and after the menu, not for the menu itself.";
        if (flags.FirstOrDefault(f => f.StartsWith(LayoutAuditProposer.DuplicatePrefix, StringComparison.Ordinal)) is { } duplicate)
            return $"The same screen is already audited at checkpoint {duplicate[LayoutAuditProposer.DuplicatePrefix.Length..]}; one layout audit per state.";
        return null;
    }

    // One sentence on what the audit found in the recorded state.
    private static string LayoutSummary(LayoutAuditReport report, string? outcome)
    {
        string found = report.Findings.Length == 0 ? "No layout finding in the recorded state"
            : $"In the recorded state: {report.Errors} error and {report.Warnings} warning finding(s)"
              + (report.Findings.FirstOrDefault() is { } first ? $", e.g. {first.Rule} on {first.View}" : "");
        string coverage = report.Coverage.Complete ? "" : $"; partial coverage ({report.Coverage.Unavailable.Length} gap(s){(report.Coverage.Truncated ? ", tree truncated" : "")})";
        return found + coverage + (outcome == AssertionOutcomes.Fail ? ". It would FAIL as recorded (fix the layout or add suppressions); not preselected." : ".");
    }

    private static string KindReason(string kind) => kind switch
    {
        AssertionKinds.AttributeAt or AssertionKinds.Shadow => "A " + kind + " assertion depends on palette attributes and is never suggested.",
        AssertionKinds.Aligned or AssertionKinds.Spacing or AssertionKinds.Disjoint or AssertionKinds.WithinView =>
            "A " + kind + " assertion is a layout check reserved for the layout audit and is not suggested.",
        AssertionKinds.StatusContext => "A status-context assertion checks a numeric help context, which is not meaningful to review; not suggested.",
        AssertionKinds.ViewAbsent => "A view-absent assertion would need a selector from an earlier screen; not suggested.",
        _ => "The assertion kind is not one that can be suggested.",
    };

    /// <summary>Lowers the candidate's confidence one level per degradation (see the header).</summary>
    internal static string Downgrade(string? confidence, CheckpointEvidence evidence)
    {
        int level = confidence switch { Confidences.High => 2, Confidences.Medium => 1, _ => 0 };
        if (evidence.Flags.Contains(EvidenceFlags.CoverageIncomplete)) level--;
        if (evidence.Flags.Contains(EvidenceFlags.TreeTruncated) || evidence.TreeTruncated) level--;
        if (evidence.Source != EvidenceSources.Snapshot) level--;
        return level >= 2 ? Confidences.High : level == 1 ? Confidences.Medium : Confidences.Low;
    }

    /// <summary>Normalizes a readable id to <see cref="Protocol.IsValidName"/> (lowercase words joined by hyphens), falls back to
    /// the kind, and adds -2, -3, ... on collision in candidate order.</summary>
    internal static string UniqueId(string? readable, string kind, HashSet<string> used)
    {
        string root = Normalize(readable) ?? Normalize(kind) ?? "assertion";
        string id = root;
        for (int n = 2; !used.Add(id); n++) id = root + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return id;
    }

    internal static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (char c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string id = sb.ToString().Trim('-');
        if (id.Length > 40) id = id[..40].TrimEnd('-');   // room for a -NNN suffix within 48
        return Protocol.IsValidName(id) ? id : null;
    }

    private static AssertionSpec Redact(AssertionSpec spec) => spec with
    {
        Text = spec.Text is null ? null : Redacted,
        View = spec.View is { Text: not null } v ? v with { Text = Redacted } : spec.View,
        Other = spec.Other is { Text: not null } o ? o with { Text = Redacted } : spec.Other,
    };

    private static string[] TextsOf(AssertionSpec spec) =>
        new[] { spec.Text, spec.View?.Text, spec.Other?.Text }.OfType<string>().Where(t => t.Length > 0).ToArray();
}
