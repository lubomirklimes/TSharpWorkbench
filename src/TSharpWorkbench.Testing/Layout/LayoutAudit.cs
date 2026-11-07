namespace TSharpWorkbench.Testing;

// Engine entry (coordinator). Runs the selected rule families over one capture, applies justified suppressions and
// builds the coverage. Pure and deterministic; never throws for missing evidence (that is a gap or an UNSUPPORTED outcome).

public static class LayoutAudit
{
    /// <summary>Caps that keep a report bounded on very large trees (the coverage notes say when they applied).</summary>
    public const int MaxFindings = 200, MaxGaps = 50;

    /// <summary>Audits one capture. Returns the report, or the scope problem (no/ambiguous scope match) instead.</summary>
    public static (LayoutAuditReport? Report, string? Problem) Run(LayoutAuditRequest request, ScreenSnapshot cells, ViewTree tree, MenuState? menu = null)
    {
        string profile = LayoutProfiles.All.Contains(request.Profile) ? request.Profile : throw new ArgumentException("Unknown layout-audit profile '" + request.Profile + "'.");
        foreach (string rule in request.Rules ?? [])
            if (!LayoutRules.IsKnown(rule) || !LayoutRules.InProfile(rule, profile)) throw new ArgumentException($"Rule '{rule}' is not a {profile} rule.");
        var (context, problem) = LayoutModel.Build(cells, tree, menu, request.Scope, profile);
        if (context is null) return (null, problem);

        var rules = LayoutRules.Selected(profile, request.Rules);
        var sink = new LayoutRuleSink(rules);
        LayoutGeometryRules.Evaluate(context, sink);
        LayoutTextRules.Evaluate(context, sink);
        if (profile is LayoutProfiles.Ux or LayoutProfiles.Strict) LayoutUxRules.Evaluate(context, sink);

        // A selected rule no family reported on was not implemented for this evidence: an honest gap, never a silent pass.
        foreach (string rule in rules.Where(r => !sink.Ran(r) && !sink.Gaps.Any(g => g.Rule == r)))
            sink.Unavailable(rule, "The rule did not run on this capture.");

        var kept = new List<LayoutFinding>();
        var suppressed = new List<LayoutSuppressedFinding>();
        foreach (var f in sink.Findings.Distinct())
        {
            var match = (request.Suppress ?? []).FirstOrDefault(s => s.Rule == f.Rule && (s.View is null || context.Find(f.Path) is { } n && s.View.Matches(n.Node)));
            if (match is not null) suppressed.Add(new(f.Rule, f.Path, match.Reason));
            else kept.Add(f);
        }
        var notes = new List<string>();
        var ordered = kept.OrderBy(f => LayoutFindingClasses.Rank(f.Class)).ThenBy(f => f.Rule, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Rect.Y).ThenBy(f => f.Rect.X).ThenBy(f => f.OtherPath, StringComparer.Ordinal).ToArray();
        if (ordered.Length > MaxFindings) { notes.Add($"{ordered.Length - MaxFindings} more finding(s) not listed."); ordered = ordered[..MaxFindings]; }
        var gaps = sink.Gaps.Distinct().OrderBy(g => g.Rule, StringComparer.Ordinal).ThenBy(g => g.Path, StringComparer.Ordinal).ToArray();
        if (gaps.Length > MaxGaps) { notes.Add($"{gaps.Length - MaxGaps} more coverage gap(s) not listed."); gaps = gaps[..MaxGaps]; }

        int omitted = context.Nodes.Sum(n => n.Node.OmittedChildren);
        var coverage = new LayoutCoverage(profile, context.Scope.Path, sink.EvaluatedRules.ToArray(), sink.NotApplicableRules.ToArray(), gaps,
            suppressed.OrderBy(s => s.Rule, StringComparer.Ordinal).ThenBy(s => s.Path, StringComparer.Ordinal).ToArray(),
            context.Nodes.Count, context.Nodes.Count(n => n.Shown), tree.Truncated && omitted > 0, omitted, notes.ToArray());
        return (new LayoutAuditReport(ordered, coverage), null);
    }
}
