using System.Globalization;

namespace TSharpWorkbench.Testing;

// The layout-audit assertion's outcome mapping. AssertionEvaluator delegates
// here after its generic required-evidence gate (no view tree → UNSUPPORTED). Pure and deterministic: the same capture and
// request give the same outcome and texts. Whenever the audit ran, the result carries the report (findings + coverage).
internal static class LayoutAuditAssertion
{
    /// <summary>Failing findings listed in the explanation (the full list is in <see cref="AssertionResult.Layout"/>).</summary>
    internal const int ExplainedFindings = 3;

    /// <param name="make">The evaluator's result factory: (outcome, expected, actual, explanation, location).</param>
    internal static AssertionResult Evaluate(AssertionSpec spec, ScreenSnapshot cells, CheckpointInspection? inspection,
        Func<string, string, string?, string, string?, AssertionResult> make)
    {
        const string Fail = AssertionOutcomes.Fail, Unsupported = AssertionOutcomes.Unsupported;
        var request = LayoutAuditRequest.Of(spec);
        string profile = request.Profile;
        if (inspection?.ViewTree is not { } tree)
            return make(Unsupported, "layout audit (profile " + profile + ")", null, "'view-tree' evidence was not captured at this checkpoint.", null);

        LayoutAuditReport? report; string? problem;
        try { (report, problem) = LayoutAudit.Run(request, cells, tree, inspection.Menu); }
        catch (ArgumentException ex)
        { return make(Unsupported, "layout audit (profile " + profile + ")", null, "The layout audit request is invalid: " + ex.Message, null); }

        if (report is null)
        {
            // Scope selector matched no view or several: FAIL, like every single-view selector.
            string expected = $"audit scope [{spec.View}]";
            int matches = spec.View is null ? 0 : AssertionEvaluator.All(tree.Root).Count(spec.View.Matches);
            return make(Fail, expected, matches == 0 ? "no match" : $"{matches} matches", problem ?? "The audit scope could not be resolved.", null);
        }

        return Outcome(profile, report, tree, make);
    }

    /// <summary>Outcome of an audit that ran (contracts §5 items 2-7): pure over the report and the captured tree, so the
    /// mapping is testable independent of the rule families. The result always carries the report.</summary>
    internal static AssertionResult Outcome(string profile, LayoutAuditReport report, ViewTree tree,
        Func<string, string, string?, string, string?, AssertionResult> make)
    {
        const string Pass = AssertionOutcomes.Pass, Fail = AssertionOutcomes.Fail, Unsupported = AssertionOutcomes.Unsupported;
        AssertionResult R(string outcome, string expected, string? actual, string explanation, string? location) =>
            make(outcome, expected, actual, explanation, location) with { Layout = report };

        var coverage = report.Coverage;
        bool strict = profile == LayoutProfiles.Strict;
        string expectedText = strict ? "no error or warning layout findings (profile strict)" : $"no error-class layout findings (profile {profile})";
        string actualText = $"{report.Errors} error, {report.Warnings} warning finding(s)";
        string? scopePath = coverage.Scope;

        // A scope that is hidden or entirely clipped cannot be audited as shown: FAIL.
        var scopeNode = scopePath is null ? null : AssertionEvaluator.All(tree.Root).FirstOrDefault(n => n.Path == scopePath);
        if (scopeNode is not null && scopeNode.Visible is not { IsEmpty: false })
            return R(Fail, $"audit scope {Describe(scopeNode)} shown", scopeNode.Visible is null ? "hidden" : "fully clipped",
                "The audit scope is not shown on screen (hidden or entirely clipped by its owners or the screen); nothing of it can be judged as displayed.", scopePath);

        var failing = report.Findings.Where(f => f.Class == LayoutFindingClasses.Error || (strict && f.Class == LayoutFindingClasses.Warning)).ToArray();
        if (failing.Length > 0)
        {
            string list = string.Join("; ", failing.Take(ExplainedFindings).Select(Line));
            string more = failing.Length > ExplainedFindings ? $" (+{failing.Length - ExplainedFindings} more failing finding(s) in the layout details)" : "";
            return R(Fail, expectedText, actualText, $"{failing.Length} failing finding(s): {list}{more}." + Suffix(coverage), failing[0].Path);
        }

        if (coverage.Truncated)
            return R(Unsupported, expectedText, actualText,
                $"The captured tree was truncated: {coverage.OmittedNodes.ToString(CultureInfo.InvariantCulture)} view(s) not inspected; the audit cannot pass."
                + Suffix(coverage with { Truncated = false }), scopePath);

        if (coverage.Evaluated.Length == 0 && coverage.NotApplicable.Length == 0)
            return R(Unsupported, expectedText, actualText,
                $"No selected rule could run on this capture ({coverage.Unavailable.Length.ToString(CultureInfo.InvariantCulture)} rule gap(s)"
                + (coverage.Unavailable.FirstOrDefault() is { } g ? $", e.g. {g.Rule}: {g.Reason}" : "") + "); the audit cannot pass.", scopePath);

        var text = new List<string>
        {
            $"Audited {coverage.AuditedNodes.ToString(CultureInfo.InvariantCulture)} of {coverage.ScopeNodes.ToString(CultureInfo.InvariantCulture)} view(s) "
            + $"(profile {profile}): {coverage.Evaluated.Length} rule(s) evaluated, {coverage.NotApplicable.Length} not applicable."
        };
        if (!coverage.Complete) text.Add(Partial(coverage));
        var warnings = report.Findings.Where(f => f.Class == LayoutFindingClasses.Warning).ToArray();
        if (warnings.Length > 0)
            text.Add($"{warnings.Length} warning finding(s) (do not fail profile {profile}): " + string.Join("; ", warnings.Take(ExplainedFindings).Select(Line))
                + (warnings.Length > ExplainedFindings ? $" (+{warnings.Length - ExplainedFindings} more)" : "") + ".");
        if (coverage.Suppressed.Length > 0) text.Add($"{coverage.Suppressed.Length} finding(s) suppressed with a reason.");
        return R(Pass, expectedText, actualText, string.Join(" ", text), scopePath);
    }

    /// <summary>One finding for an explanation: <c>rule at (x,y WxH) on View: message</c>.</summary>
    internal static string Line(LayoutFinding f) => $"{f.Rule} at {f.Rect} on {f.View}: {f.Message}";

    /// <summary>The partial-coverage sentence (gaps by rule, bounded).</summary>
    internal static string Partial(LayoutCoverage c)
    {
        var parts = new List<string>();
        if (c.Unavailable.Length > 0)
        {
            var byRule = c.Unavailable.GroupBy(g => g.Rule, StringComparer.Ordinal).ToArray();
            parts.Add($"{c.Unavailable.Length} rule gap(s) in {byRule.Length} rule(s) (" + string.Join("; ", byRule.Take(3).Select(g => $"{g.Key}: {g.First().Reason}"
                + (g.Count() > 1 ? $" ({g.Count()} views)" : ""))) + (byRule.Length > 3 ? "; ..." : "") + ")");
        }
        if (c.Truncated) parts.Add($"tree truncated ({c.OmittedNodes} view(s) not inspected)");
        return "Partial coverage: " + string.Join(", ", parts) + ".";
    }

    private static string Suffix(LayoutCoverage c) => c.Complete ? "" : " " + Partial(c);

    private static string Describe(ViewNode n) => $"{n.Kind}{(n.Id is { } id ? " #" + id : "")}{(n.Text is { Length: > 0 } t ? " \"" + Inspectors.Bound(t, 40) + "\"" : "")} ({n.Path})";
}
