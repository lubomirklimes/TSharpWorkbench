using TSharpWorkbench.Testing;

// The layout-audit suggestion of a checkpoint proposal. ONE composite candidate per applicable state
// (never one assertion per rule, never relation assertions from picks). Applicability is decided here and explained through
// flags the validator turns into an "unsupported" reason, so the review says why a state has no layout audit.

internal static class LayoutAuditProposer
{
    internal const string Rule = "layout-audit";
    /// <summary>Flags: a menu is open in this state; the same screen was already audited by an earlier proposal.</summary>
    internal const string MenuFlag = "layout-menu-open", DuplicatePrefix = "layout-duplicate:";
    /// <summary>Suggested profile: geometry and text fit, plus UX heuristics as warnings.</summary>
    internal const string Profile = LayoutProfiles.Ux;

    /// <summary>The candidate for <paramref name="input"/>, or null when the state has no screen at all. <paramref name="auditedHashes"/>
    /// maps screen hashes already audited to their checkpoint ID (duplicate suppression; updated here).</summary>
    internal static AssertionCandidate? Propose(ProposerInput input, IDictionary<ulong, string> auditedHashes)
    {
        var evidence = input.Evidence;
        var proposal = input.Proposal;
        if (evidence.Cells is null) return null;
        var flags = new List<string>();
        if (proposal.Kind is CandidateKinds.MenuOpen or CandidateKinds.MenuChange) flags.Add(MenuFlag);
        else if (evidence.Hash != 0 && auditedHashes.TryGetValue(evidence.Hash, out string? earlier)) flags.Add(DuplicatePrefix + earlier);
        else if (evidence.Hash != 0 && evidence.Semantic) auditedHashes[evidence.Hash] = proposal.CheckpointId;

        // Scope: the executing modal view by its diagnostic id when it has one; otherwise none (the audit then takes the
        // modal view that executes at replay, or the whole screen). Titles are not used: they may be dynamic or sensitive.
        ViewSelector? scope = null;
        string where = "the screen";
        if (evidence.Tree is { ModalPath: { } modalPath } tree && AssertionEvaluator.All(tree.Root).FirstOrDefault(n => n.Path == modalPath) is { } modal
            && modal.Kind is not ("TMenuBar" or "TMenuBox" or "TMenuPopup" or "TMenuView"))
        {
            where = $"the {modal.Kind} in front";
            if (modal.Id is { } id && AssertionEvaluator.All(tree.Root).Count(n => n.Id == id) == 1) scope = ViewSelector.ById(id);
        }
        var spec = new AssertionSpec("layout", AssertionKinds.LayoutAudit, AssertionSeverity.Warning, View: scope) { Profile = Profile };
        string rationale = $"Audits the layout of {where} in this state: geometry, text fit and UX heuristics (profile {Profile}); "
            + "UX findings are warnings, hard geometry findings fail the assertion (severity warning: the scenario does not fail).";
        return new AssertionCandidate(proposal.Id, spec, SuggestionOrigins.State, null, Rule, rationale, Confidences.High, flags);
    }
}
