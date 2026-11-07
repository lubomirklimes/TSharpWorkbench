using TSharpWorkbench.Testing;

// The proposer's rules. Every rule reads only the proposal's own evidence (Context) and mirrors the real
// AssertionEvaluator's PASS conditions, so an idea it emits passes on that evidence unless the validator's safety rules say no.

internal static partial class AssertionProposer
{
    private static readonly string[] NoFlags = [];

    // ── state rules (origin "state") ─────────────────────────────────────────────────────────────────────────────────

    private static List<Draft> StateRules(Context context)
    {
        var drafts = new List<Draft>();
        string kind = context.Input.Candidate?.Kind ?? context.Input.Proposal.Kind;
        switch (kind)
        {
            case CandidateKinds.ModalOpen or CandidateKinds.ModalChange:
                ExecutingModal(context, drafts, RuleNames.ModalOpen, Confidences.High);
                FocusedControl(context, drafts, RuleNames.ModalOpenFocus);
                break;
            case CandidateKinds.ModalClose:
                if (context.Tree is { } tree)
                {
                    if (tree.ModalPath is null)
                        drafts.Add(State(new AssertionSpec("no-modal", AssertionKinds.Modal), RuleNames.ModalClose,
                            "No modal view executes in this state: only the application loop runs.", tree.Truncated ? Confidences.Medium : Confidences.High, NoFlags));
                    else ExecutingModal(context, drafts, RuleNames.ModalCloseOuter, Confidences.Medium);
                }
                break;
            case CandidateKinds.MenuOpen or CandidateKinds.MenuChange:
                OpenMenu(context, drafts);
                break;
            case CandidateKinds.MenuClose:
                if (context.Menu is { Depth: 0 } closed)
                    drafts.Add(State(new AssertionSpec("menu-closed", AssertionKinds.MenuDepth, Value: 0), RuleNames.MenuClose,
                        closed.Active ? "No drop-down or popup level is open in this state (only the menu bar is active)."
                            : "No menu level is open in this state.", Confidences.High, NoFlags));
                break;
            case CandidateKinds.Content or CandidateKinds.Async or CandidateKinds.Final or CandidateKinds.Resize:
                ExecutingModal(context, drafts, RuleNames.ContentModal, Confidences.Medium);
                FocusedControl(context, drafts, RuleNames.ContentFocus);
                break;
        }
        return drafts;
    }

    private static Draft State(AssertionSpec spec, string rule, string rationale, string confidence, IReadOnlyList<string> flags) =>
        new(spec with { Severity = AssertionSeverity.Warning }, SuggestionOrigins.State, null, rule, rationale, confidence, flags);

    /// <summary>`modal` with the executing modal's unique selector (dialogs/windows; menus are covered by the menu rules).</summary>
    private static void ExecutingModal(Context context, List<Draft> drafts, string rule, string confidence)
    {
        if (context.Tree?.ModalPath is not { } path || context.NodeAt(path) is not { } modal || IsMenuView(modal)) return;
        if (UniqueSelector(context, modal) is not { } found) return;
        var (selector, basis) = found;
        drafts.Add(State(new AssertionSpec(Stem(modal, selector) + "-modal", AssertionKinds.Modal, View: selector), rule,
            $"The executing modal view in this state is a {modal.Kind}, uniquely selected by {Describe(basis)}.",
            basis == Basis.Type ? Lower(confidence) : confidence, basis == Basis.Type ? [ProposerFlags.SelectorTypeOnly] : NoFlags));
    }

    /// <summary>`view-focused` on the node at tree.FocusedPath (the evaluator's definition of focus), when it is a visible control.</summary>
    private static void FocusedControl(Context context, List<Draft> drafts, string rule)
    {
        var tree = context.Tree;
        if (tree?.FocusedPath is not { } path || context.NodeAt(path) is not { } node) return;
        if (path == tree.ModalPath || IsChrome(node) || node.Visible is not { IsEmpty: false }) return;
        if (UniqueSelector(context, node) is not { } found) return;
        var (selector, basis) = found;
        drafts.Add(State(new AssertionSpec(Stem(node, selector) + "-focused", AssertionKinds.ViewFocused, View: selector), rule,
            $"The focused view in this state is a {node.Kind}, uniquely selected by {Describe(basis)}.",
            basis == Basis.Type ? Confidences.Low : Confidences.Medium, basis == Basis.Type ? [ProposerFlags.SelectorTypeOnly] : NoFlags));
    }

    /// <summary>`menu-depth` (observed), `menu-selected` of the deepest level (drawn and highlighted), `submenu-open`.</summary>
    private static void OpenMenu(Context context, List<Draft> drafts)
    {
        if (context.Menu is not { Active: true } menu) return;
        drafts.Add(State(new AssertionSpec($"menu-depth-{menu.Depth}", AssertionKinds.MenuDepth, Value: menu.Depth), RuleNames.MenuOpenDepth,
            menu.Depth == 0 ? "The menu bar is active with no drop-down open in this state."
                : $"{menu.Depth} menu level(s) below the bar are open in this state.", Confidences.High, NoFlags));
        var level = menu.Open.Length > 0 ? menu.Open[^1] : menu.Bar;
        if (level is not null && SelectedEntry(menu, level) is { } entry)
            drafts.Add(State(new AssertionSpec(Slug(entry.Text!, 24) + "-menu-selected", AssertionKinds.MenuSelected, Text: entry.Text, Level: level.Level),
                RuleNames.MenuOpenSelected, $"The selected entry of menu level {level.Level} is drawn at its rectangle and highlighted in this state.",
                Confidences.High, NoFlags));
        if (menu.Open.Length > 0 && menu.Open[^1].ParentItem is { } parent && IsStatic(parent)
            && menu.Open.FirstOrDefault(o => o.ParentItem == parent) is { } first && ReferenceEquals(first, menu.Open[^1]))
            drafts.Add(State(new AssertionSpec(Slug(parent, 24) + "-submenu-open", AssertionKinds.SubmenuOpen, Text: parent), RuleNames.MenuOpenSubmenu,
                $"The deepest open menu level ({menu.Open[^1].Level}) was opened from its parent entry in this state.", Confidences.Medium, NoFlags));
    }

    /// <summary>The level's selected entry when menu-selected would PASS: model text static, observed <c>match</c>, and not
    /// drawn with the attribute of the evaluator's reference unselected entry.</summary>
    private static MenuEntry? SelectedEntry(MenuState menu, MenuViewState level)
    {
        if (!ReferenceEquals(Level(menu, level.Level), level)) return null;
        if (level.Selected is not { } i || i < 0 || i >= level.Items.Length) return null;
        var selected = level.Items[i];
        if (selected.Separator || !IsStatic(selected.Text) || selected.Observed != "match") return null;
        var plain = level.Items.FirstOrDefault(e => !e.Selected && !e.Separator && !e.Disabled && e.Attribute is not null);
        if (plain is not null && plain.Attribute == selected.Attribute) return null;
        return selected;
    }

    /// <summary>The evaluator's explicit level lookup (0 = bar, n = first open level with Level n).</summary>
    private static MenuViewState? Level(MenuState menu, int level) => level == 0 ? menu.Bar : menu.Open.FirstOrDefault(o => o.Level == level);

    // ── pick rules (origin "pick") ───────────────────────────────────────────────────────────────────────────────────

    private static List<Draft> PickRules(Context context, PickSelection pick)
    {
        var drafts = new List<Draft>();
        bool sameState = pick.Hash == context.Evidence.Hash && pick.AfterSequence == context.Evidence.AfterSequence;
        if (pick.Kind == Protocol.PickKinds.Region)
        {
            if (sameState) RegionRules(context, pick, pick.Region, drafts, RuleNames.PickRegionText, RuleNames.PickRegionTextAt, []);
            return drafts;
        }
        var target = pick.Target;
        switch (target?.Area)
        {
            case PickAreas.View:
                if (ViewPick(context, pick, target, drafts)) return drafts;
                break;
            case PickAreas.MenuItem:
                MenuItemPick(context, pick, target, drafts);
                return drafts;
            case PickAreas.Menu:
                if (context.Menu is { } menu)
                    drafts.Add(Pick(pick, new AssertionSpec($"menu-depth-{menu.Depth}", AssertionKinds.MenuDepth, Value: menu.Depth), RuleNames.PickMenuDepth,
                        $"The picked menu shows {menu.Depth} open level(s) below the bar in this state.", Confidences.Medium, NoFlags));
                return drafts;
            case PickAreas.StatusItem:
                StatusItemPick(context, pick, target, drafts);
                return drafts;
            case PickAreas.Status:
                if (context.Status is { Hint: { } hint, HintObserved: "match" } && IsStatic(hint))
                    drafts.Add(Pick(pick, new AssertionSpec("status-hint", AssertionKinds.StatusHint, Text: hint), RuleNames.PickStatusHint,
                        "The picked status line shows the hint of its active context in full in this state.", Confidences.Medium, NoFlags));
                return drafts;
        }
        // Control pick without a usable unique selector (or a region target): region rules over the picked view's cells.
        if (!sameState) return drafts;
        if (target?.Area == PickAreas.View && target.Path is { } p && context.NodeAt(p) is { } hit && IsTextEntry(hit)) return drafts;
        if (target is not null && (target.Kind is { } k && Has(k) || target.Type is { } t && Has(t))) return drafts;
        var area = target?.Screen is { IsEmpty: false } screen && target.Area is PickAreas.View or PickAreas.Region ? screen : pick.Region;
        RegionRules(context, pick, area, drafts, RuleNames.PickControlRegionText, RuleNames.PickControlRegionTextAt, [ProposerFlags.RegionFallback]);
        return drafts;
    }

    private static Draft Pick(PickSelection pick, AssertionSpec spec, string rule, string rationale, string confidence, IReadOnlyList<string> flags) =>
        new(spec with { Severity = AssertionSeverity.Warning }, SuggestionOrigins.Pick, pick.PickId, rule, rationale, confidence, flags);

    /// <summary>Control pick on a view: the pick's verified selector re-checked on this evidence (else one rebuilt from the
    /// node the pick names), then visible / focused / modal / not-clipped as observed. False when no unique selector exists.</summary>
    private static bool ViewPick(Context context, PickSelection pick, PickTarget target, List<Draft> drafts)
    {
        if (!context.TreeUsable) return false;
        ViewNode? node = null; ViewSelector? selector = null; string origin = ProposerFlags.SelectorFromPick;
        foreach (var candidate in pick.Selectors.Where(c => c.Unique))
        {
            var matches = context.Nodes.Where(candidate.Selector.Matches).Take(2).ToArray();
            if (matches.Length != 1 || !SafeSelector(candidate.Selector, matches[0])) continue;
            if (target.Type is not null && matches[0].Type != target.Type) continue;
            (node, selector) = (matches[0], candidate.Selector);
            break;
        }
        if (selector is null)
        {
            var named = context.NodeAt(target.Path);
            if (named is null || (target.Type is not null && named.Type != target.Type)) return false;
            if (UniqueSelector(context, named) is not { } rebuiltFound) return false;
            var rebuilt = rebuiltFound.Selector;
            (node, selector, origin) = (named, rebuilt, ProposerFlags.SelectorRebuilt);
        }
        var basis = BasisOf(selector);
        bool typeOnly = basis == Basis.Type;
        string stem = Stem(node!, selector);
        string by = Describe(basis);
        IReadOnlyList<string> flags = typeOnly ? [origin, ProposerFlags.SelectorTypeOnly] : [origin];
        string Conf(string c) => typeOnly ? Lower(c) : c;
        var tree = context.Tree!;
        int before = drafts.Count;
        if (node!.Visible is { IsEmpty: false })
            drafts.Add(Pick(pick, new AssertionSpec(stem + "-visible", AssertionKinds.ViewVisible, View: selector), RuleNames.PickViewVisible,
                $"The picked {node.Kind} is visible on screen in this state (selected by {by}).", Conf(Confidences.High), flags));
        if (tree.FocusedPath == node.Path)
            drafts.Add(Pick(pick, new AssertionSpec(stem + "-focused", AssertionKinds.ViewFocused, View: selector), RuleNames.PickViewFocused,
                $"The picked {node.Kind} has the focus in this state (selected by {by}).", Conf(Confidences.High), flags));
        if (tree.ModalPath == node.Path && !IsMenuView(node))
            drafts.Add(Pick(pick, new AssertionSpec(stem + "-modal", AssertionKinds.Modal, View: selector), RuleNames.PickViewModal,
                $"The picked {node.Kind} is the executing modal view in this state (selected by {by}).", Conf(Confidences.High), flags));
        if (node.Visible is { IsEmpty: false } visible && visible == node.Screen)
            drafts.Add(Pick(pick, new AssertionSpec(stem + "-not-clipped", AssertionKinds.NotClipped, View: selector), RuleNames.PickViewNotClipped,
                $"No part of the picked {node.Kind} is clipped by an owner or the screen in this state.", Conf(Confidences.Medium), flags));
        return drafts.Count > before;
    }

    /// <summary>Menu entry pick: menu-selected when it is the drawn, highlighted selection; menu-item-state as observed.</summary>
    private static void MenuItemPick(Context context, PickSelection pick, PickTarget target, List<Draft> drafts)
    {
        if (context.Menu is not { } menu || target.MenuItem is not { } text || !IsStatic(text)) return;
        var levels = (menu.Bar is { } bar ? new[] { bar } : []).Concat(menu.Open).ToArray();
        var level = levels.FirstOrDefault(l => l.Path is not null && l.Path == target.Path && l.Items.Any(e => e.Text == text))
            ?? levels.Reverse().FirstOrDefault(l => l.Items.Any(e => e.Text == text));
        if (level is null || !ReferenceEquals(Level(menu, level.Level), level)) return;
        var entry = level.Items.First(e => e.Text == text);                  // the evaluator also takes the first entry with this text
        string stem = Slug(text, 24);
        if (stem.Length == 0) stem = "entry";
        if (entry.Selected && SelectedEntry(menu, level) is { } selected && ReferenceEquals(selected, entry))
            drafts.Add(Pick(pick, new AssertionSpec(stem + "-menu-selected", AssertionKinds.MenuSelected, Text: text, Level: level.Level), RuleNames.PickMenuSelected,
                $"The picked menu entry is the drawn, highlighted selection of menu level {level.Level} in this state.", Confidences.High, NoFlags));
        string state = entry.Disabled ? "disabled" : "enabled";
        drafts.Add(Pick(pick, new AssertionSpec($"{stem}-menu-{state}", AssertionKinds.MenuItemState, Text: text, State: state, Level: level.Level),
            RuleNames.PickMenuItemState, $"The picked menu entry of level {level.Level} is {state} in this state.", Confidences.Medium, NoFlags));
    }

    /// <summary>Status item pick: status-item visible when drawn and matching the cells; enabled/disabled as observed.</summary>
    private static void StatusItemPick(Context context, PickSelection pick, PickTarget target, List<Draft> drafts)
    {
        if (context.Status is not { } status || target.StatusItem is not { } text || !IsStatic(text)) return;
        var item = status.Items.FirstOrDefault(i => i.Text == text);
        if (item is null) return;
        string stem = Slug(text, 24);
        if (stem.Length == 0) stem = "item";
        if (item.Drawn && item.Observed == "match")
            drafts.Add(Pick(pick, new AssertionSpec(stem + "-status-visible", AssertionKinds.StatusItem, Text: text, State: "visible"), RuleNames.PickStatusVisible,
                "The picked status item is laid out on the status line and shown in the cells in this state.", Confidences.High, NoFlags));
        string state = item.Enabled ? "enabled" : "disabled";
        drafts.Add(Pick(pick, new AssertionSpec($"{stem}-status-{state}", AssertionKinds.StatusItem, Text: text, State: state), RuleNames.PickStatusState,
            $"The command of the picked status item is {state} in this state.", Confidences.Medium, NoFlags));
    }

    /// <summary>Region rules: screen-contains of the best static segment, text-at of it when not cut at the region edge, and
    /// for multi-row regions a second segment from another row. Never the whole block; never input-line/editor cells.</summary>
    private static void RegionRules(Context context, PickSelection pick, CellRect region, List<Draft> drafts, string textRule, string atRule,
        IReadOnlyList<string> extraFlags)
    {
        var segments = Segments(context, region);
        if (segments.Count == 0) return;
        var flags = new List<string>(extraFlags);
        if (!context.InputMaskKnown) flags.Add(ProposerFlags.NoInputMask);
        var best = segments[0];
        string Conf(Segment s) => s.Cut ? Confidences.Low : s.Occurrences == 1 ? (context.InputMaskKnown ? Confidences.High : Confidences.Medium) : Confidences.Low;
        IReadOnlyList<string> FlagsOf(Segment s) =>
            [.. flags, .. s.Cut ? new[] { ProposerFlags.RegionCut } : [], .. s.Occurrences != 1 ? new[] { ProposerFlags.RegionNotUnique } : []];
        drafts.Add(Pick(pick, new AssertionSpec("picked-text", AssertionKinds.ScreenContains, Text: best.Text), textRule,
            $"Row {best.Y} of the picked area shows a static {best.Text.Length}-character text in this state"
            + (best.Occurrences == 1 ? ", found nowhere else on screen." : $", which appears {best.Occurrences} times on screen."),
            Conf(best), FlagsOf(best)));
        if (!best.Cut)
            drafts.Add(Pick(pick, new AssertionSpec("picked-text-at", AssertionKinds.TextAt, X: best.X, Y: best.Y, Text: best.Text), atRule,
                $"The same static text starts at cell ({best.X},{best.Y}) of this {context.Cells.Width}x{context.Cells.Height} screen.",
                context.InputMaskKnown ? Confidences.Medium : Confidences.Low, FlagsOf(best)));
        if (segments.FirstOrDefault(s => s.Y != best.Y && !s.Cut && s.Occurrences == 1) is { } second)
            drafts.Add(Pick(pick, new AssertionSpec("picked-text-row", AssertionKinds.ScreenContains, Text: second.Text), RuleNames.PickRegionSecondRow,
                $"Row {second.Y} of the picked area shows another static {second.Text.Length}-character text found nowhere else on screen.",
                Confidences.Low, FlagsOf(second)));
    }
}
