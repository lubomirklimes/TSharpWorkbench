using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Workstream A: the pure assertion proposer over hand-built evidence of one checkpoint state (cells + inspection).
// Every Propose call goes through Run(), which checks the invariants (no Path selector, valid unique ids, cap, origin/pick
// consistency, allowed kinds only, never the input line's value) — a property check over every test of this class.
[Collection("NonParallel")]
public sealed class AssertionProposerTests
{
    private const int W = 60, H = 16;
    private const ulong Hash = 0x5eed;
    private const long After = 7;
    private const string Secret = "hunter2x";   // the input line's value: must never reach a candidate

    private static readonly string[] Allowed =
    [
        AssertionKinds.ViewExists, AssertionKinds.ViewVisible, AssertionKinds.ViewFocused, AssertionKinds.Modal, AssertionKinds.NotClipped,
        AssertionKinds.WithinScreen, AssertionKinds.MenuDepth, AssertionKinds.MenuSelected, AssertionKinds.MenuItemState,
        AssertionKinds.SubmenuOpen, AssertionKinds.StatusItem, AssertionKinds.StatusHint, AssertionKinds.ScreenContains, AssertionKinds.TextAt,
    ];

    private static readonly string[] Semantic =
    [
        AssertionKinds.ViewExists, AssertionKinds.ViewVisible, AssertionKinds.ViewFocused, AssertionKinds.Modal, AssertionKinds.NotClipped,
        AssertionKinds.WithinScreen, AssertionKinds.MenuDepth, AssertionKinds.MenuSelected, AssertionKinds.MenuItemState,
        AssertionKinds.SubmenuOpen, AssertionKinds.StatusItem, AssertionKinds.StatusHint,
    ];

    // ── state rules ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModalOpenProposesTheExecutingDialogWithAUniqueSelectorAndTheFocusedControl()
    {
        var evidence = Evidence(DialogTree());
        var c = Run(Input(CandidateKinds.ModalOpen, evidence));
        var modal = Assert.Single(c, x => x.Spec.Kind == AssertionKinds.Modal);
        Assert.Equal(new ViewSelector(Type: "TDialog", Text: "Find"), modal.Spec.View);
        Assert.Equal(("find-dialog-modal", AssertionProposer.RuleNames.ModalOpen, Confidences.High, SuggestionOrigins.State),
            (modal.Spec.Id, modal.Rule, modal.Confidence, modal.Origin));
        var focus = Assert.Single(c, x => x.Spec.Kind == AssertionKinds.ViewFocused);
        Assert.Equal(ViewSelector.ById("find-input"), focus.Spec.View);   // the input line by id, never by its value
        Assert.Equal(AssertionProposer.RuleNames.ModalOpenFocus, focus.Rule);
        Assert.Equal(AssertionKinds.Modal, c[0].Spec.Kind);
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Fact]
    public void ModalWithoutTitleIsSelectedByTypeWithLowerConfidence()
    {
        var tree = DialogTree(dialogTitle: null);
        var c = Run(Input(CandidateKinds.ModalChange, Evidence(tree)));
        var modal = Assert.Single(c, x => x.Spec.Kind == AssertionKinds.Modal);
        Assert.Equal(("dialog-modal", Confidences.Medium), (modal.Spec.Id, modal.Confidence));
        Assert.Equal(ViewSelector.OfType("TDialog"), modal.Spec.View);
        Assert.Contains(AssertionProposer.ProposerFlags.SelectorTypeOnly, modal.Flags);
        AssertPassesOnOwnEvidence(c, Evidence(tree));
    }

    [Fact]
    public void FocusedInputLineWithoutIdIsSelectedByTypeNeverByItsValue()
    {
        var tree = DialogTree(inputId: null);
        var c = Run(Input(CandidateKinds.ModalOpen, Evidence(tree)));
        var focus = Assert.Single(c, x => x.Spec.Kind == AssertionKinds.ViewFocused);
        Assert.Equal(ViewSelector.OfType("TInputLine"), focus.Spec.View);
        Assert.Equal(Confidences.Low, focus.Confidence);
        AssertPassesOnOwnEvidence(c, Evidence(tree));
    }

    [Fact]
    public void ModalCloseAssertsNoModalOnlyWhenObserved()
    {
        var closed = Evidence(DialogTree() with { ModalPath = null, FocusedPath = null });
        var c = Run(Input(CandidateKinds.ModalClose, closed));
        var none = Assert.Single(c);
        Assert.Equal((AssertionKinds.Modal, "no-modal", Confidences.High), (none.Spec.Kind, none.Spec.Id, none.Confidence));
        Assert.Null(none.Spec.View);
        AssertPassesOnOwnEvidence(c, closed);

        var stillModal = Run(Input(CandidateKinds.ModalClose, Evidence(DialogTree())));       // a nested dialog closed back to the outer one
        Assert.DoesNotContain(stillModal, x => x.Spec.Kind == AssertionKinds.Modal && x.Spec.View is null);
        Assert.Equal(AssertionProposer.RuleNames.ModalCloseOuter, Assert.Single(stillModal).Rule);
    }

    [Fact]
    public void MenuOpenProposesObservedDepthDeepestSelectionAndSubmenu()
    {
        var evidence = Evidence(DialogTree() with { ModalPath = "0/3", FocusedPath = "0/3" }, OpenMenu(), MenuCells());
        var c = Run(Input(CandidateKinds.MenuOpen, evidence));
        Assert.Equal([AssertionKinds.MenuDepth, AssertionKinds.MenuSelected, AssertionKinds.SubmenuOpen], c.Select(x => x.Spec.Kind));
        Assert.Equal(1, c[0].Spec.Value);
        Assert.Equal(("New", 1), (c[1].Spec.Text, c[1].Spec.Level));
        Assert.Equal("new-menu-selected", c[1].Spec.Id);
        Assert.Equal("File", c[2].Spec.Text);
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Theory]
    [InlineData("mismatch", 0x20)]     // the cells do not show the entry
    [InlineData("match", 0x70)]        // drawn like an unselected entry: not visibly highlighted
    public void MenuSelectedNeedsObservedMatchAndHighlight(string observed, int attribute)
    {
        var menu = OpenMenu(selectedObserved: observed, selectedAttribute: attribute);
        var c = Run(Input(CandidateKinds.MenuChange, Evidence(DialogTree(), menu, MenuCells())));
        Assert.DoesNotContain(c, x => x.Spec.Kind == AssertionKinds.MenuSelected);
        Assert.Contains(c, x => x.Spec.Kind == AssertionKinds.MenuDepth);
    }

    [Fact]
    public void MenuCloseAssertsDepthZeroOnlyWhenObserved()
    {
        var evidence = Evidence(DialogTree());
        var c = Run(Input(CandidateKinds.MenuClose, evidence));
        var depth = Assert.Single(c);
        Assert.Equal((AssertionKinds.MenuDepth, 0, "menu-closed"), (depth.Spec.Kind, depth.Spec.Value, depth.Spec.Id));
        AssertPassesOnOwnEvidence(c, evidence);
        Assert.Empty(Run(Input(CandidateKinds.MenuClose, Evidence(DialogTree(), OpenMenu(), MenuCells()))));   // still open: nothing
    }

    [Theory]
    [InlineData(CandidateKinds.Content)]
    [InlineData(CandidateKinds.Async)]
    [InlineData(CandidateKinds.Final)]
    [InlineData(CandidateKinds.Resize)]
    public void ContentKindsProposeAtMostTwoHighValueViewAssertions(string kind)
    {
        var evidence = Evidence(DialogTree());
        var c = Run(Input(kind, evidence));
        Assert.InRange(c.Count, 1, 2);
        Assert.All(c, x => Assert.Contains(x.Spec.Kind, new[] { AssertionKinds.Modal, AssertionKinds.ViewFocused }));
        Assert.All(c, x => Assert.Equal(Confidences.Medium, x.Confidence));
        AssertPassesOnOwnEvidence(c, evidence);
    }

    // ── picks ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ControlPickWithUniqueSelectorGivesVisibleAndNotClipped()
    {
        var evidence = Evidence(DialogTree());
        var pick = ControlPick(1, ViewTarget("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", "Cancel", new(24, 9, 10, 1)),
            Selector(new ViewSelector(Type: "TButton", Text: "Cancel"), SelectorBases.TypeText));
        var c = Run(Input(null, evidence, pick));
        Assert.Equal([AssertionKinds.ViewVisible, AssertionKinds.NotClipped], c.Select(x => x.Spec.Kind));
        Assert.All(c, x => Assert.Equal((SuggestionOrigins.Pick, (long?)1), (x.Origin, x.PickId)));
        Assert.Equal(("cancel-button-visible", Confidences.High), (c[0].Spec.Id, c[0].Confidence));
        Assert.Contains(AssertionProposer.ProposerFlags.SelectorFromPick, c[0].Flags);
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Fact]
    public void ControlPickOnTheFocusedModalDialogAddsFocusAndModal()
    {
        var tree = DialogTree() with { FocusedPath = "0/2/0/3" };
        var evidence = Evidence(tree);
        var cancel = ControlPick(1, ViewTarget("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", "Cancel", new(24, 9, 10, 1)),
            Selector(new ViewSelector(Type: "TButton", Text: "Cancel"), SelectorBases.TypeText));
        var dialog = ControlPick(2, ViewTarget("0/2/0", "TSharpVision.Dialogs.TDialog", "TDialog", "Find", new(10, 3, 30, 9)),
            Selector(new ViewSelector(Type: "TDialog", Text: "Find"), SelectorBases.TypeText));
        var c = Run(Input(null, evidence, cancel, dialog), max: 20);
        Assert.Contains(c, x => x.PickId == 1 && x.Spec.Kind == AssertionKinds.ViewFocused);
        Assert.Contains(c, x => x.PickId == 2 && x.Spec.Kind == AssertionKinds.Modal && x.Rule == AssertionProposer.RuleNames.PickViewModal);
        Assert.Equal([1L, 2L], c.Take(2).Select(x => x.PickId!.Value));   // round-robin: each pick's best idea first
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Fact]
    public void UnsafePickSelectorOnAnInputLineIsRebuiltWithoutItsValue()
    {
        var evidence = Evidence(DialogTree(inputId: null));
        var pick = ControlPick(1, ViewTarget("0/2/0/1", "TSharpVision.Dialogs.TInputLine", "TInputLine", Secret, new(12, 6, 20, 1)),
            Selector(new ViewSelector(Type: "TInputLine", Text: Secret), SelectorBases.TypeText));
        var c = Run(Input(null, evidence, pick));
        Assert.NotEmpty(c);
        Assert.All(c, x => Assert.Equal(ViewSelector.OfType("TInputLine"), x.Spec.View));
        Assert.Contains(AssertionProposer.ProposerFlags.SelectorRebuilt, c[0].Flags);
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Fact]
    public void ControlPickWithAmbiguousSelectorFallsBackToRegionText()
    {
        var evidence = Evidence(DialogTree());               // "OK" exists in the dialog and in the editor window
        var pick = ControlPick(1, ViewTarget("0/2/0/2", "TSharpVision.Dialogs.TButton", "TButton", "OK", new(14, 9, 6, 1)));
        pick = pick with { Flags = [PickFlags.AmbiguousSelector] };
        var c = Run(Input(null, evidence, pick));
        Assert.NotEmpty(c);
        Assert.DoesNotContain(c, x => x.Spec.View is not null);
        var text = Assert.Single(c, x => x.Spec.Kind == AssertionKinds.ScreenContains);
        Assert.Equal(("OK", AssertionProposer.RuleNames.PickControlRegionText), (text.Spec.Text, text.Rule));
        Assert.Contains(AssertionProposer.ProposerFlags.RegionFallback, text.Flags);
        Assert.Contains(AssertionProposer.ProposerFlags.RegionNotUnique, text.Flags);   // "OK" is on screen twice
        var at = Assert.Single(c, x => x.Spec.Kind == AssertionKinds.TextAt);
        Assert.Equal((16, 9, "OK"), (at.Spec.X!.Value, at.Spec.Y!.Value, at.Spec.Text));
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Fact]
    public void ControlPickOnAnInputLineWithoutSelectorProposesNothing()
    {
        var tree = DialogTree(inputId: null, secondInput: true);   // two input lines: no unique selector
        var pick = ControlPick(1, ViewTarget("0/2/0/1", "TSharpVision.Dialogs.TInputLine", "TInputLine", Secret, new(12, 6, 20, 1)));
        Assert.Empty(Run(Input(null, Evidence(tree), pick)));
    }

    [Fact]
    public void RegionPickGivesScreenContainsAndTextAtOfStaticText()
    {
        var evidence = Evidence(DialogTree());
        var pick = RegionPick(1, new CellRect(12, 5, 14, 2));       // label row + input line row (masked)
        var c = Run(Input(null, evidence, pick));
        Assert.Equal([AssertionKinds.ScreenContains, AssertionKinds.TextAt], c.Select(x => x.Spec.Kind));
        Assert.Equal(("Text to find", Confidences.High), (c[0].Spec.Text, c[0].Confidence));
        Assert.Equal((12, 5), (c[1].Spec.X!.Value, c[1].Spec.Y!.Value));
        Assert.Equal(("picked-text", "picked-text-at"), (c[0].Spec.Id, c[1].Spec.Id));
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Fact]
    public void RegionCutInsideAWordDropsThePartialWordOrIsLowWithoutTextAt()
    {
        var evidence = Evidence(DialogTree());
        var trimmed = Run(Input(null, evidence, RegionPick(1, new CellRect(12, 5, 10, 1))));   // "Text to fi"
        Assert.Equal("Text to", trimmed[0].Spec.Text);
        Assert.Contains(trimmed, x => x.Spec.Kind == AssertionKinds.TextAt);
        var cut = Run(Input(null, evidence, RegionPick(1, new CellRect(13, 5, 3, 1))));        // "ext": no whole word
        var only = Assert.Single(cut);
        Assert.Equal((AssertionKinds.ScreenContains, Confidences.Low), (only.Spec.Kind, only.Confidence));
        Assert.Contains(AssertionProposer.ProposerFlags.RegionCut, only.Flags);
        AssertPassesOnOwnEvidence(trimmed, evidence);
    }

    [Fact]
    public void MultiRowRegionUsesTheMostDistinctiveRowsNeverTheBlock()
    {
        var evidence = Evidence(DialogTree());
        var c = Run(Input(null, evidence, RegionPick(1, new CellRect(11, 4, 28, 6))));   // label, input (masked), buttons
        var contains = c.Where(x => x.Spec.Kind == AssertionKinds.ScreenContains).ToArray();
        Assert.InRange(contains.Length, 1, 2);
        Assert.All(contains, x => Assert.DoesNotContain('\n', x.Spec.Text!));
        Assert.Equal("Text to find", contains[0].Spec.Text);
        Assert.Equal("Cancel", contains[1].Spec.Text);              // "OK" is not unique on screen
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Theory]
    [InlineData(42, 2, 10)]    // "Count: 0"
    [InlineData(42, 4, 12)]    // "Clock 12:30"
    [InlineData(42, 13, 16)]   // "C:\work\a.txt"
    [InlineData(12, 6, 20)]    // the input line's value
    public void DynamicOrTypedRegionTextYieldsNothing(int x, int y, int width)
    {
        Assert.Empty(Run(Input(null, Evidence(DialogTree()), RegionPick(1, new CellRect(x, y, width, 1)))));
    }

    [Fact]
    public void StatusItemPickGivesVisibleAndObservedState()
    {
        var evidence = Evidence(DialogTree());
        var exit = ControlPick(1, StatusTarget("Alt-X Exit", new(0, 15, 12, 1)));
        var help = ControlPick(2, StatusTarget("F1 Help", new(12, 15, 9, 1)));
        var c = Run(Input(null, evidence, exit, help));
        Assert.Contains(c, x => x.PickId == 1 && x.Spec is { Kind: AssertionKinds.StatusItem, State: "visible", Text: "Alt-X Exit" } && x.Confidence == Confidences.High);
        Assert.Contains(c, x => x.PickId == 1 && x.Spec is { State: "enabled" });
        Assert.Contains(c, x => x.PickId == 2 && x.Spec is { State: "disabled", Text: "F1 Help" });
        Assert.Equal("alt-x-exit-status-visible", c[0].Spec.Id);
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Fact]
    public void MenuItemPickGivesSelectedAndItemState()
    {
        var evidence = Evidence(DialogTree() with { ModalPath = "0/3", FocusedPath = "0/3" }, OpenMenu(), MenuCells());
        var selected = ControlPick(1, MenuTarget("New", new(1, 2, 12, 1)));
        var other = ControlPick(2, MenuTarget("Open", new(1, 3, 12, 1)));
        var c = Run(Input(null, evidence, selected, other), max: 20);
        Assert.Contains(c, x => x.PickId == 1 && x.Spec is { Kind: AssertionKinds.MenuSelected, Text: "New", Level: 1 });
        Assert.Contains(c, x => x.PickId == 2 && x.Spec is { Kind: AssertionKinds.MenuItemState, Text: "Open", State: "disabled", Level: 1 });
        Assert.DoesNotContain(c, x => x.PickId == 2 && x.Spec.Kind == AssertionKinds.MenuSelected);
        AssertPassesOnOwnEvidence(c, evidence);
    }

    // ── degraded evidence ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CellsOnlyEvidenceNeverGetsSemanticKinds()
    {
        var cellsOnly = Evidence(null) with { Inspection = null, Flags = [EvidenceFlags.NoInspection] };
        var pick = ControlPick(1, ViewTarget("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", "Cancel", new(24, 9, 10, 1)),
            Selector(new ViewSelector(Type: "TButton", Text: "Cancel"), SelectorBases.TypeText));
        foreach (string kind in new[] { CandidateKinds.ModalOpen, CandidateKinds.MenuOpen, CandidateKinds.MenuClose, CandidateKinds.ModalClose, CandidateKinds.Content })
        {
            var c = Run(Input(kind, cellsOnly, pick));
            Assert.All(c, x => Assert.DoesNotContain(x.Spec.Kind, Semantic));
            Assert.All(c, x => Assert.Equal(SuggestionOrigins.Pick, x.Origin));
            Assert.Contains(c, x => x.Spec is { Kind: AssertionKinds.ScreenContains, Text: "Cancel" });
            Assert.All(c, x => Assert.Contains(AssertionProposer.ProposerFlags.NoInputMask, x.Flags));
        }
    }

    [Fact]
    public void HashOnlyEvidenceGivesNothing()
    {
        var hashOnly = Evidence(null) with { Cells = null, Inspection = null, Source = EvidenceSources.None };
        Assert.Empty(Run(Input(CandidateKinds.ModalOpen, hashOnly, RegionPick(1, new CellRect(12, 5, 14, 1)))));
    }

    [Fact]
    public void OmittedInspectionIsNotSemantic()
    {
        var omitted = Evidence(DialogTree()) with { Omitted = true, Flags = [EvidenceFlags.InspectionOmitted] };
        Assert.Empty(Run(Input(CandidateKinds.ModalOpen, omitted)));
        Assert.Empty(Run(Input(CandidateKinds.MenuClose, omitted)));
        var c = Run(Input(CandidateKinds.ModalOpen, omitted, RegionPick(1, new CellRect(12, 5, 14, 1))));
        Assert.All(c, x => Assert.DoesNotContain(x.Spec.Kind, Semantic));
    }

    [Fact]
    public void TruncatedTreeGivesNoViewSelectorButKeepsMenuAndStatus()
    {
        var truncated = Evidence(DialogTree() with { Truncated = true });
        Assert.Empty(Run(Input(CandidateKinds.ModalOpen, truncated)));
        var closed = Run(Input(CandidateKinds.ModalClose, Evidence(DialogTree() with { Truncated = true, ModalPath = null })));
        Assert.Equal(Confidences.Medium, Assert.Single(closed).Confidence);
        var pick = ControlPick(1, ViewTarget("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", "Cancel", new(24, 9, 10, 1)),
            Selector(new ViewSelector(Type: "TButton", Text: "Cancel"), SelectorBases.TypeText));
        var c = Run(Input(CandidateKinds.MenuClose, truncated, pick, ControlPick(2, StatusTarget("Alt-X Exit", new(0, 15, 12, 1)))), max: 20);
        Assert.DoesNotContain(c, x => x.Spec.View is not null);
        Assert.Contains(c, x => x.Spec.Kind == AssertionKinds.ScreenContains && x.PickId == 1);
        Assert.Contains(c, x => x.Spec.Kind == AssertionKinds.StatusItem);
        Assert.Contains(c, x => x.Spec.Kind == AssertionKinds.MenuDepth);
    }

    [Fact]
    public void PickOfAnotherStateGetsNoRegionAssertion()
    {
        var pick = RegionPick(1, new CellRect(12, 5, 14, 1)) with { Hash = 1 };
        Assert.Empty(Run(Input(null, Evidence(DialogTree()), pick)));
    }

    // ── ranking, cap, determinism ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PickCandidatesRankBeforeStateCandidates()
    {
        var evidence = Evidence(DialogTree());
        var pick = ControlPick(1, ViewTarget("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", "Cancel", new(24, 9, 10, 1)),
            Selector(new ViewSelector(Type: "TButton", Text: "Cancel"), SelectorBases.TypeText));
        var c = Run(Input(CandidateKinds.ModalOpen, evidence, pick, RegionPick(2, new CellRect(12, 5, 14, 1))), max: 20);
        int lastPick = c.ToList().FindLastIndex(x => x.Origin == SuggestionOrigins.Pick);
        int firstState = c.ToList().FindIndex(x => x.Origin == SuggestionOrigins.State);
        Assert.True(lastPick >= 0 && firstState > lastPick);
        Assert.Equal([1L, 2L], c.Take(2).Select(x => x.PickId!.Value));
        AssertPassesOnOwnEvidence(c, evidence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void CapIsRespectedAndKeepsTheTopRankedCandidates(int max)
    {
        var evidence = Evidence(DialogTree() with { FocusedPath = "0/2/0/3" });
        var input = Input(CandidateKinds.ModalOpen, evidence,
            ControlPick(1, ViewTarget("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", "Cancel", new(24, 9, 10, 1)),
                Selector(new ViewSelector(Type: "TButton", Text: "Cancel"), SelectorBases.TypeText)),
            RegionPick(2, new CellRect(11, 4, 28, 6)), ControlPick(3, StatusTarget("Alt-X Exit", new(0, 15, 12, 1))));
        var all = Run(input, max: 50);
        var capped = Run(input, max: max);
        Assert.True(all.Count > 8);
        Assert.Equal(Math.Min(max, all.Count), capped.Count);
        Assert.Equal(all.Take(max).Select(x => x.Spec.Id), capped.Select(x => x.Spec.Id));
        Assert.Equal(8, AssertionProposer.Propose(input).Count);   // default options
    }

    [Fact]
    public void SameInputGivesTheSameOutput()
    {
        var input = Input(CandidateKinds.ModalOpen, Evidence(DialogTree()),
            ControlPick(1, ViewTarget("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", "Cancel", new(24, 9, 10, 1)),
                Selector(new ViewSelector(Type: "TButton", Text: "Cancel"), SelectorBases.TypeText)),
            RegionPick(2, new CellRect(11, 4, 28, 6)));
        var a = JsonSerializer.Serialize(Run(input, max: 50));
        var b = JsonSerializer.Serialize(Run(input with { }, max: 50));
        Assert.Equal(a, b);
        // Pick order in the proposal does not matter: picks are ranked by PickId.
        var reversed = input with { Proposal = input.Proposal with { Picks = input.Proposal.Picks.Reverse().ToArray() } };
        Assert.Equal(a, JsonSerializer.Serialize(Run(reversed, max: 50)));
    }

    [Fact]
    public void DuplicateIdeasCollapseAndIdsStayUnique()
    {
        var evidence = Evidence(DialogTree());
        var pick = RegionPick(1, new CellRect(12, 5, 14, 1));
        var c = Run(Input(null, evidence, pick, pick with { PickId = 2 }), max: 20);
        Assert.Equal(2, c.Count);                                   // the second pick's ideas are identical: dropped
    }

    [Fact]
    public void StaticTextHeuristic()
    {
        foreach (string ok in new[] { "OK", "Cancel", "Text to find", "Alt-X Exit", "F10 Menu", "Save as..." })
            Assert.True(AssertionProposer.IsStatic(ok), ok);
        foreach (string bad in new[] { "", " ", "x", "12:30", "2026-11-06", "Events: 0000", "Count: 0", "50%", "3/10", "3 of 10",
            "Items (3)", "C:\\work\\a.txt", "/home/u/a", "https://example.com", "a@b.cz", "deadbeefcafe0123",
            "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "1234", new string('a', 61) })
            Assert.False(AssertionProposer.IsStatic(bad), bad);
    }

    // ── realism: a real in-process TSharpVision capture (NullDriver: logical evidence only) ─────────────────────────────

    [Fact]
    public void RealInProcessCaptureCandidatesParseAndPassTheEvaluator()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var dialog = new TDialog(new TRect(10, 3, 50, 13), "Inspect me");
        var input = new TInputLine(new TRect(3, 2, 30, 3), 40); dialog.Insert(input);
        var ok = new TButton(new TRect(3, 6, 13, 8), "~O~K", Views.cmOK, ButtonConstants.bfDefault); dialog.Insert(ok);
        var cancel = new TButton(new TRect(15, 6, 25, 8), "Cancel", Views.cmCancel, ButtonConstants.bfNormal); dialog.Insert(cancel);
        input.SetData(Secret); input.Select();
        ViewTreeInspector.Identify(ok, "ok");
        app.DeskTop!.Insert(dialog);
        app.StatusLine!.Update(); app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var inspection = Inspectors.Capture(app, cells, Protocol.EvidenceKinds.All);
        Assert.Empty(inspection.Errors);
        var evidence = new CheckpointEvidence("p-3", 3, Hash, After, EvidenceSources.Snapshot, 11, cells, inspection, false, []);
        var tree = inspection.ViewTree!;
        string okPath = ViewTreeInspector.PathOf(ok, app)!, cancelPath = ViewTreeInspector.PathOf(cancel, app)!;
        var okNode = AssertionEvaluator.All(tree.Root).Single(n => n.Path == okPath);
        var cancelNode = AssertionEvaluator.All(tree.Root).Single(n => n.Path == cancelPath);
        var picks = new[]
        {
            ControlPick(1, ViewTarget(okPath, okNode.Type, okNode.Kind, okNode.Text, okNode.Screen), Selector(ViewSelector.ById("ok"), SelectorBases.Id)),
            ControlPick(2, ViewTarget(cancelPath, cancelNode.Type, cancelNode.Kind, cancelNode.Text, cancelNode.Screen)),   // no selector: rebuilt
            RegionPick(3, new CellRect(10, 3, 40, 3)),                                                                        // title + input row
        };
        foreach (string kind in new[] { CandidateKinds.Content, CandidateKinds.ModalClose, CandidateKinds.MenuClose, CandidateKinds.Final })
        {
            var c = Run(Input(kind, evidence, picks), max: 50);
            Assert.Contains(c, x => x.PickId == 1 && x.Spec.Kind == AssertionKinds.ViewVisible && x.Spec.View == ViewSelector.ById("ok"));
            Assert.Contains(c, x => x.PickId == 2 && x.Spec.View == new ViewSelector(Type: "TButton", Text: "Cancel"));
            Assert.Contains(c, x => x.PickId == 3 && x.Spec is { Kind: AssertionKinds.ScreenContains, Text: "Inspect me" });
            Assert.Contains(c, x => x.Origin == SuggestionOrigins.State);
            AssertPassesOnOwnEvidence(c, evidence);
        }
    }

    // ── invariants and evaluator sanity ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<AssertionCandidate> Run(ProposerInput input, int max = 8)
    {
        var result = AssertionProposer.Propose(input, new ProposerOptions(max));
        Assert.True(result.Count <= max);
        Assert.Equal(result.Count, result.Select(c => c.Spec.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var c in result)
        {
            Assert.True(Protocol.IsValidName(c.Spec.Id), c.Spec.Id);
            Assert.Contains(c.Spec.Kind, Allowed);
            Assert.Equal(input.Proposal.Id, c.ProposalId);
            Assert.Null(c.Spec.View?.Path);
            Assert.Null(c.Spec.Other);
            Assert.False(string.IsNullOrWhiteSpace(c.Rule));
            Assert.False(string.IsNullOrWhiteSpace(c.Rationale));
            Assert.Contains(c.Confidence, new[] { Confidences.High, Confidences.Medium, Confidences.Low });
            Assert.Equal(c.Origin == SuggestionOrigins.Pick, c.PickId is not null);
            Assert.Contains(c.Origin, new[] { SuggestionOrigins.Pick, SuggestionOrigins.State });
            foreach (string? text in new[] { c.Spec.Text, c.Spec.View?.Text, c.Spec.View?.Id, c.Spec.Id, c.Rationale })
                Assert.DoesNotContain(Secret, text ?? "", StringComparison.OrdinalIgnoreCase);
            if (!input.Evidence.Semantic) Assert.DoesNotContain(c.Spec.Kind, Semantic);
        }
        return result;
    }

    /// <summary>Sanity (B is the gate): every candidate parses with the runtime v1 parser and PASSes the real evaluator on the
    /// same evidence; view selectors match exactly one node.</summary>
    private static void AssertPassesOnOwnEvidence(IReadOnlyList<AssertionCandidate> candidates, CheckpointEvidence evidence)
    {
        Assert.NotEmpty(candidates);
        var (specs, error) = AssertionJson.Parse(candidates.Select(c => AssertionJson.ToJson(c.Spec)).ToList(), evidence.Width, evidence.Height);
        Assert.True(error is null, error);
        Assert.Equal(candidates.Count, specs!.Length);
        var results = AssertionEvaluator.Evaluate(specs, evidence.Cells!, evidence.Inspection, "validation", "check");
        Assert.All(results, r => Assert.True(r.Outcome == AssertionOutcomes.Pass, $"{r.Id} {r.Kind}: {r.Outcome} {r.Explanation} ({r.Actual})"));
        if (evidence.Tree is { } tree)
            foreach (var view in specs.Select(s => s.View).OfType<ViewSelector>())
                Assert.Equal(1, AssertionEvaluator.All(tree.Root).Count(view.Matches));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ProposerInput Input(string? kind, CheckpointEvidence evidence, params PickSelection[] picks)
    {
        var proposal = new CheckpointProposal("p-3", "rec-check-1", 0, Placements.AfterStep, ScenarioDocumentStep.Checkpoint("rec-check-1", null),
            kind ?? "pick", "test", Confidences.High, EvidenceLevels.Snapshot, After, 3, 11, kind is null ? [] : ["c-3"], picks, false, []);
        var candidate = kind is null ? null : new StableChangeCandidate("c-3", 3, 3, After, null, Protocol.BoundaryCauses.Input, kind,
            Stabilities.InputEnded, EvidenceLevels.Snapshot, Confidences.High, "test", Hash, 1, W, H, null, 0, 11, false, []);
        return new(proposal, evidence, candidate);
    }

    private static CheckpointEvidence Evidence(ViewTree? tree, MenuState? menu = null, ScreenSnapshot? cells = null) =>
        new("p-3", 3, Hash, After, EvidenceSources.Snapshot, 11, cells ?? Cells(),
            tree is null && menu is null ? null : new CheckpointInspection(tree, menu ?? ClosedMenu(), Status(), []), false, []);

    private static PickSelection ControlPick(long id, PickTarget target, params SelectorCandidate[] selectors) =>
        new(id, Protocol.PickKinds.Control, Protocol.PickTriggers.CtrlShift, After, 3, Hash, 1, W, H,
            new CellRect(target.Screen!.Value.X + 1, target.Screen.Value.Y, 1, 1), 20 + id, EvidenceLevels.Snapshot, target, selectors, null, []);

    private static PickSelection RegionPick(long id, CellRect region) =>
        new(id, Protocol.PickKinds.Region, Protocol.PickTriggers.PickMode, After, 3, Hash, 1, W, H, region, 20 + id, EvidenceLevels.Snapshot,
            new PickTarget(PickAreas.Region, null, null, null, null, null, region, null, null), [], null, [PickFlags.RegionOnly]);

    private static SelectorCandidate Selector(ViewSelector selector, string basis) => new(selector, basis, 1);

    private static PickTarget ViewTarget(string path, string type, string kind, string? text, CellRect screen) =>
        new(PickAreas.View, path, type, kind, null, text, screen, null, null);

    private static PickTarget StatusTarget(string item, CellRect screen) =>
        new(PickAreas.StatusItem, "0/1", "TSharpVision.Menus.TStatusLine", "TStatusLine", null, item, screen, null, item);

    private static PickTarget MenuTarget(string item, CellRect screen) =>
        new(PickAreas.MenuItem, "0/3", "TSharpVision.Menus.TMenuBox", "TMenuBox", null, item, screen, item, null);

    private static ScreenSnapshot Cells(Action<Action<int, int, string>>? extra = null)
    {
        var cells = Enumerable.Range(0, W * H).Select(_ => new TScreenChar(' ', 7)).ToArray();
        void Put(int x, int y, string text) { for (int i = 0; i < text.Length; i++) cells[y * W + x + i] = new TScreenChar(text[i], 7); }
        Put(1, 0, "File"); Put(7, 0, "Edit");
        Put(10, 3, "\u2554" + new string('\u2550', 10) + " Find " + new string('\u2550', 12) + "\u2557");
        Put(12, 5, "Text to find"); Put(12, 6, Secret); Put(16, 9, "OK"); Put(26, 9, "Cancel");
        Put(4, 10, "OK");
        Put(42, 2, "Count: 0"); Put(42, 4, "Clock 12:30"); Put(42, 13, "C:\\work\\a.txt");
        Put(1, 15, "Alt-X Exit"); Put(13, 15, "F1 Help");
        extra?.Invoke(Put);
        return new ScreenSnapshot(W, H, cells);
    }

    private static ScreenSnapshot MenuCells() => Cells(put => { put(2, 2, "New"); put(2, 3, "Open"); put(2, 4, "Save"); });

    private static MenuEntry Entry(int index, string text, CellRect screen, bool selected = false, string observed = "match", int attribute = 0x70,
        bool disabled = false) =>
        new(index, text, "~" + text, null, 100 + index, !disabled, disabled, false, false, null, 0, null, 0, selected, screen, observed, attribute);

    private static MenuViewState Bar(int? selected) =>
        new(0, "TSharpVision.Menus.TMenuBar", "0/0", new(0, 0, W, 1), selected is not null, true, selected, selected is null ? null : "File", null,
            [Entry(0, "File", new(0, 0, 6, 1), selected == 0, attribute: selected == 0 ? 0x20 : 0x70), Entry(1, "Edit", new(6, 0, 6, 1))]);

    private static MenuState ClosedMenu() => new(false, 0, Bar(null), []);

    private static MenuState OpenMenu(string selectedObserved = "match", int selectedAttribute = 0x20)
    {
        var box = new MenuViewState(1, "TSharpVision.Menus.TMenuBox", "0/3", new(0, 1, 14, 5), true, true, 0, "New", "File",
            [Entry(0, "New", new(1, 2, 12, 1), true, selectedObserved, selectedAttribute), Entry(1, "Open", new(1, 3, 12, 1), disabled: true),
             Entry(2, "Save", new(1, 4, 12, 1))]);
        return new(true, 1, Bar(0), [box]);
    }

    private static StatusState Status() =>
        new("TSharpVision.Menus.TStatusLine", "0/1", new(0, 15, W, 1), 0, 0, 0, 0xFFFF, null, null, "none",
            [new(0, "Alt-X Exit", "~Alt-X~ Exit", 0x2d00, "Alt+X", 1, true, true, new(0, 15, 12, 1), "match", 7),
             new(1, "F1 Help", "~F1~ Help", 0x3b00, "F1", 2, false, true, new(12, 15, 9, 1), "match", 7)], " Alt-X Exit  F1 Help");

    /// <summary>Find dialog (modal) over an editor window; the input line holds <see cref="Secret"/> and has the focus.</summary>
    private static ViewTree DialogTree(string? dialogTitle = "Find", string? inputId = "find-input", bool secondInput = false)
    {
        ViewNode N(string path, string type, string kind, CellRect screen, string? text = null, string? id = null, CellRect? visible = null,
            params ViewNode[] children) =>
            new(path, id, type, kind, screen, screen, visible ?? screen,
                new ViewFlags(true, true, false, false, false, false, false, false, true, false, 0, 0), 0, text, null, null, children, 0);
        var dialogChildren = new List<ViewNode>
        {
            N("0/2/0/0", "TSharpVision.Dialogs.TStaticText", "TStaticText", new(12, 5, 12, 1)),
            N("0/2/0/1", "TSharpVision.Dialogs.TInputLine", "TInputLine", new(12, 6, 20, 1), Secret, inputId),
            N("0/2/0/2", "TSharpVision.Dialogs.TButton", "TButton", new(14, 9, 6, 1), "OK"),
            N("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", new(24, 9, 10, 1), "Cancel"),
            N("0/2/0/4", "TSharpVision.Views.TFrame", "TFrame", new(10, 3, 30, 9)),
        };
        if (secondInput) dialogChildren.Add(N("0/2/0/5", "TSharpVision.Dialogs.TInputLine", "TInputLine", new(12, 7, 20, 1), "other"));
        var dialog = N("0/2/0", "TSharpVision.Dialogs.TDialog", "TDialog", new(10, 3, 30, 9), dialogTitle, null, null, dialogChildren.ToArray());
        var editor = N("0/2/1", "TSharpVision.Views.TWindow", "TWindow", new(0, 1, 40, 12), "Editor", null, new CellRect(0, 1, 40, 12),
            N("0/2/1/0", "TSharpVision.Dialogs.TButton", "TButton", new(2, 10, 6, 1), "OK"),
            N("0/2/1/1", "TSharpVision.Views.TFrame", "TFrame", new(0, 1, 40, 12)));
        var desk = N("0/2", "TSharpVision.App.TDeskTop", "TDeskTop", new(0, 1, W, 14), null, null, null,
            dialog, editor, N("0/2/2", "TSharpVision.App.TBackground", "TBackground", new(0, 1, W, 14)));
        var root = N("0", "TSharpVision.App.TApplication", "TApplication", new(0, 0, W, H), null, null, null,
            N("0/0", "TSharpVision.Menus.TMenuBar", "TMenuBar", new(0, 0, W, 1), "File"),
            N("0/1", "TSharpVision.Menus.TStatusLine", "TStatusLine", new(0, 15, W, 1)),
            desk,
            N("0/3", "TSharpVision.Menus.TMenuBox", "TMenuBox", new(0, 1, 14, 4), "New"));
        return new(root, 17, false, "front-to-back", new(0, 0, W, H), new(0, 1, W, 14), "0/2/0", "0/2/0/1");
    }
}
