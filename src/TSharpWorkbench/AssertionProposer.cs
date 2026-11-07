using TSharpWorkbench.Testing;

// Pure assertion proposer. Rules live in AssertionProposer.Rules.cs,
// text/selector helpers in AssertionProposer.Text.cs. Everything is derived from ProposerInput only (the proposal, its own
// CheckpointEvidence, its detector candidate and its attached picks): no clock, no randomness, no hash-set ordering.

internal static partial class AssertionProposer
{
    /// <summary>Stable rule names (<see cref="AssertionCandidate.Rule"/>).</summary>
    internal static class RuleNames
    {
        internal const string ModalOpen = "modal-open", ModalOpenFocus = "modal-open-focus", ModalClose = "modal-close",
            ModalCloseOuter = "modal-close-outer", MenuOpenDepth = "menu-open-depth", MenuOpenSelected = "menu-open-selected",
            MenuOpenSubmenu = "menu-open-submenu", MenuClose = "menu-close", ContentModal = "content-modal", ContentFocus = "content-focus",
            PickViewVisible = "pick-control-visible", PickViewFocused = "pick-control-focused", PickViewModal = "pick-control-modal",
            PickViewNotClipped = "pick-control-not-clipped", PickMenuSelected = "pick-menu-selected", PickMenuItemState = "pick-menu-item-state",
            PickMenuDepth = "pick-menu-depth", PickStatusVisible = "pick-status-visible", PickStatusState = "pick-status-state",
            PickStatusHint = "pick-status-hint", PickRegionText = "pick-region-text", PickRegionTextAt = "pick-region-text-at",
            PickRegionSecondRow = "pick-region-second-row", PickControlRegionText = "pick-control-region-text",
            PickControlRegionTextAt = "pick-control-region-text-at";
    }

    /// <summary>Flags the proposer adds to a candidate (besides nothing else: evidence flags stay on the evidence).</summary>
    internal static class ProposerFlags
    {
        internal const string SelectorFromPick = "selector-from-pick", SelectorRebuilt = "selector-rebuilt", SelectorTypeOnly = "selector-type-only",
            RegionCut = "region-cut", RegionNotUnique = "region-text-not-unique", NoInputMask = "no-input-mask",
            PickOtherState = "pick-other-state", RegionFallback = "region-fallback";
    }

    /// <summary>Rule-based assertion ideas for one checkpoint state, ranked (picks first), at most
    /// <see cref="ProposerOptions.MaximumPerCheckpoint"/>. Deterministic; never reads anything but <paramref name="input"/>.</summary>
    /// <remarks>Ranking: pick candidates first, interleaved round-robin over the picks in PickId order (each pick's best idea
    /// before any pick's second); then the state rules of the candidate kind in rule order. Duplicates (same kind and fields)
    /// keep the first. Readable ids are made unique with -2, -3 suffixes in rank order.</remarks>
    internal static IReadOnlyList<AssertionCandidate> Propose(ProposerInput input, ProposerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= ProposerOptions.Default;
        int max = Math.Max(0, options.MaximumPerCheckpoint);
        if (max == 0 || input.Evidence.Cells is null) return [];
        var context = new Context(input);

        var perPick = input.Proposal.Picks.OrderBy(p => p.PickId).Select(p => PickRules(context, p)).ToList();
        var ranked = new List<Draft>();
        for (int rank = 0; perPick.Any(list => rank < list.Count); rank++)
            foreach (var list in perPick) if (rank < list.Count) ranked.Add(list[rank]);
        ranked.AddRange(StateRules(context));

        var result = new List<AssertionCandidate>();
        var seen = new List<AssertionSpec>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var draft in ranked)
        {
            if (result.Count >= max) break;
            var key = draft.Spec with { Id = "", Severity = "" };
            if (seen.Contains(key)) continue;
            seen.Add(key);
            string id = UniqueId(draft.Spec.Id, ids);
            result.Add(new(input.Proposal.Id, draft.Spec with { Id = id, Severity = AssertionSeverity.Warning }, draft.Origin, draft.PickId,
                draft.Rule, draft.Rationale, draft.Confidence, draft.Flags));
        }
        return result;
    }

    private sealed record Draft(AssertionSpec Spec, string Origin, long? PickId, string Rule, string Rationale, string Confidence, IReadOnlyList<string> Flags);

    private static string UniqueId(string readable, HashSet<string> used)
    {
        string id = Protocol.IsValidName(readable) ? readable : "assertion";
        if (used.Add(id)) return id;
        for (int n = 2; ; n++)
        {
            string suffix = "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string candidate = (id.Length + suffix.Length > 48 ? id[..(48 - suffix.Length)].TrimEnd('-') : id) + suffix;
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string Lower(string confidence) => confidence switch
    {
        Confidences.High => Confidences.Medium,
        _ => Confidences.Low,
    };

    /// <summary>Read-only view of one proposal's evidence. Semantic parts are null unless the evidence is semantic.</summary>
    private sealed class Context
    {
        internal Context(ProposerInput input)
        {
            Input = input;
            Evidence = input.Evidence;
            Cells = input.Evidence.Cells!;
            bool semantic = Evidence.Semantic;
            Tree = semantic ? Evidence.Tree : null;
            Menu = semantic ? Evidence.Inspection!.Menu : null;
            Status = semantic ? Evidence.Inspection!.Status : null;
            Nodes = Tree is { Truncated: false } tree ? AssertionEvaluator.All(tree.Root).ToArray() : [];
            Mask = new bool[Cells.Width, Cells.Height];
            if (Tree is not null)
                foreach (var node in AssertionEvaluator.All(Tree.Root).Where(n => IsTextEntry(n) && n.Visible is { IsEmpty: false }))
                {
                    var v = node.Visible!.Value;
                    for (int y = Math.Max(0, v.Y); y < Math.Min(Cells.Height, v.Bottom); y++)
                        for (int x = Math.Max(0, v.X); x < Math.Min(Cells.Width, v.Right); x++) Mask[x, y] = true;
                }
        }

        internal ProposerInput Input { get; }
        internal CheckpointEvidence Evidence { get; }
        internal ScreenSnapshot Cells { get; }
        /// <summary>Complete (semantic) view tree, possibly truncated; null when not semantic or not captured.</summary>
        internal ViewTree? Tree { get; }
        internal MenuState? Menu { get; }
        internal StatusState? Status { get; }
        /// <summary>All nodes of a usable (semantic, not truncated) tree; empty otherwise: no selector can be proven unique.</summary>
        internal ViewNode[] Nodes { get; }
        /// <summary>Cells covered by input lines/editors (their text may be typed by the user): never used for region text.</summary>
        internal bool[,] Mask { get; }
        internal bool TreeUsable => Nodes.Length > 0;
        internal bool InputMaskKnown => Tree is { Truncated: false };

        internal ViewNode? NodeAt(string? path) => path is null ? null : Nodes.FirstOrDefault(n => n.Path == path);
        internal int Count(ViewSelector selector) => Nodes.Count(selector.Matches);
    }
}
