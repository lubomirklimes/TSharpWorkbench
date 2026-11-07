using TSharpVision;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Workstream B: the pure assertion suggestion validator over hand-built evidence (cells + inspection), no process.
public sealed class AssertionValidatorTests
{
    private const int W = 40, H = 12;
    private const string Secret = "hunter2";

    // ── positive: valid PASS on the state's own evidence ─────────────────────────────────────────────────────────────

    public static TheoryData<string, AssertionSpec> ValidSpecs() => new()
    {
        { "modal", UiAssert.Modal("dialog-modal", ViewSelector.ById("find-dialog")) },
        { "modal by type and text", UiAssert.Modal("dialog-modal", new ViewSelector(Type: "TDialog", Text: "Find")) },
        { "focused control", UiAssert.Focused("ok-focused", ViewSelector.ById("ok-button")) },
        { "visible input line by id (no text)", UiAssert.Visible("input-visible", ViewSelector.ById("search-input")) },
        { "not clipped", UiAssert.NotClipped("dialog-not-clipped", ViewSelector.ById("find-dialog")) },
        { "within screen", UiAssert.WithinScreen("dialog-within-screen", ViewSelector.ById("find-dialog")) },
        { "view exists", UiAssert.Exists("cancel-exists", new ViewSelector(Type: "TButton", Text: "Cancel")) },
        { "menu depth", UiAssert.MenuDepth("menu-depth", 1) },
        { "menu selected", UiAssert.MenuSelected("menu-new", "New") },
        { "menu item state", UiAssert.MenuItem("menu-open-enabled", "Open", "enabled") },
        { "submenu open", UiAssert.SubmenuOpen("file-open", "File") },
        { "status item", UiAssert.StatusItem("status-exit", "Alt-X Exit") },
        { "screen contains static text", UiAssert.ScreenContains("title-shown", "Find") },
        { "text-at on the same grid", UiAssert.TextAt("title-at", 15, 3, "Find") },
    };

    [Theory]
    [MemberData(nameof(ValidSpecs))]
    public void StaticObservedAssertionsAreValidAndPass(string name, AssertionSpec spec)
    {
        var s = Single(spec);
        Assert.True(s.Status == SuggestionStatuses.Valid, name + ": " + s.Reason);
        Assert.Equal(AssertionOutcomes.Pass, s.Outcome);
        Assert.Null(s.Reason);
        Assert.True(s.Selectable);
        Assert.True(s.DefaultSelected);
        Assert.Equal(AssertionSeverity.Warning, s.Spec.Severity);
        Assert.Equal(spec.Id, s.Id);
        Assert.Null(AssertionJson.Parse([AssertionJson.ToJson(s.Spec)], W, H).Error);
    }

    [Fact]
    public void CellsOnlyEvidenceStillAllowsScreenContainsAndTextAt()
    {
        foreach (var evidence in new[] { Evidence(noInspection: true), Evidence(omitted: true) })
        {
            var result = Validate(evidence, Ctx(), UiAssert.ScreenContains("title", "Find"), UiAssert.TextAt("title-at", 15, 3, "Find"),
                UiAssert.Modal("dialog-modal", ViewSelector.ById("find-dialog")), UiAssert.MenuDepth("menu-depth", 1));
            Assert.Equal([SuggestionStatuses.Valid, SuggestionStatuses.Valid, SuggestionStatuses.Rejected, SuggestionStatuses.Rejected],
                result.Select(r => r.Status));
            Assert.Contains("Only the cells", result[2].Reason);
        }
    }

    // ── gate 1: kinds ─────────────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<AssertionSpec> ForbiddenKinds() => new()
    {
        UiAssert.AttributeAt("attr", 0, 0, 7),
        UiAssert.Shadow("shadow", ViewSelector.ById("find-dialog"), 8),
        UiAssert.Aligned("aligned", new ViewSelector(Type: "TButton"), "top"),
        UiAssert.Spacing("spacing", ViewSelector.ById("ok-button"), new ViewSelector(Type: "TButton", Text: "Cancel"), "horizontal", 1, 4),
        UiAssert.Disjoint("disjoint", ViewSelector.ById("ok-button"), new ViewSelector(Type: "TButton", Text: "Cancel")),
        UiAssert.Within("within", ViewSelector.ById("ok-button")),
        UiAssert.StatusContext("context", 0),
        UiAssert.Absent("absent", ViewSelector.ById("gone")),
        new AssertionSpec("strange", "made-up-kind"),
        UiAssert.StatusItem("status-absent", "Alt-F3 Close", "absent"),
    };

    [Theory]
    [MemberData(nameof(ForbiddenKinds))]
    public void KindsOutsideSr4AreRejected(AssertionSpec spec)
    {
        var s = Single(spec);
        Assert.Equal(SuggestionStatuses.Rejected, s.Status);
        Assert.NotNull(s.Reason);
        Assert.Null(s.Outcome);
        Assert.False(s.DefaultSelected);
    }

    // ── gate 2: evidence ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoCellsMeansCaptureOnlyAndEverythingIsRejected()
    {
        var evidence = new CheckpointEvidence("p1", 1, 0xabc, 10, EvidenceSources.None, null, null, null, false, [EvidenceFlags.NoInspection]);
        var result = Validate(evidence, Ctx(), UiAssert.ScreenContains("title", "Find"), UiAssert.MenuDepth("menu-depth", 1));
        Assert.All(result, r => Assert.Equal(SuggestionStatuses.Rejected, r.Status));
        Assert.All(result, r => Assert.Contains("capture only", r.Reason));
    }

    [Fact]
    public void TruncatedTreeCannotProveSelectorUniqueness()
    {
        var evidence = Evidence(tree: Tree() with { Truncated = true }, flags: [EvidenceFlags.TreeTruncated]);
        var result = Validate(evidence, Ctx(), UiAssert.Visible("ok-visible", ViewSelector.ById("ok-button")),
            UiAssert.Modal("dialog-modal", ViewSelector.ById("find-dialog")), UiAssert.MenuDepth("menu-depth", 1));
        Assert.Equal([SuggestionStatuses.Unsupported, SuggestionStatuses.Unsupported, SuggestionStatuses.Valid], result.Select(r => r.Status));
        Assert.Contains("truncated", result[0].Reason);
        Assert.Contains(EvidenceFlags.TreeTruncated, result[2].Flags);
        Assert.Equal(Confidences.Medium, result[2].Confidence);  // downgraded once by the truncated tree
    }

    [Fact]
    public void SemanticEvidenceWithoutViewTreeMakesViewKindsUnsupported()
    {
        var evidence = Evidence(inspection: new CheckpointInspection(null, Menu(), Status(), ["view-tree: inspector failed"]));
        var s = Assert.Single(Validate(evidence, Ctx(), UiAssert.Visible("ok-visible", ViewSelector.ById("ok-button"))));
        Assert.Equal(SuggestionStatuses.Unsupported, s.Status);
        Assert.Contains("No view tree", s.Reason);
    }

    // ── gate 3: safety ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PathSelectorIsRejected()
    {
        var s = Single(UiAssert.Visible("ok-visible", new ViewSelector(Path: "0/2/0/1")));
        Assert.Equal(SuggestionStatuses.Rejected, s.Status);
        Assert.Contains("capture path", s.Reason);
    }

    public static TheoryData<string, AssertionSpec> TypedSecretSpecs() => new()
    {
        { "screen-contains of the typed secret", UiAssert.ScreenContains("hunter2-shown", Secret) },
        { "text containing the typed run", UiAssert.ScreenContains("hunter2-shown", "pw: " + Secret) },
        { "text contained in the typed run", UiAssert.ScreenContains("hunter2-shown", "HUNT") },
        { "selector text", UiAssert.Visible("hunter2-visible", new ViewSelector(Type: "TButton", Text: Secret)) },
        { "text-at", UiAssert.TextAt("hunter2-at", 12, 4, Secret) },
        { "menu-selected", UiAssert.MenuSelected("hunter2-menu", Secret) },
    };

    [Theory]
    [MemberData(nameof(TypedSecretSpecs))]
    public void TypedTextIsRejectedAndRedactedEverywhere(string name, AssertionSpec spec)
    {
        var candidate = Candidate(spec, rationale: "The screen shows " + Secret + " after typing.");
        var s = Assert.Single(AssertionSuggestionValidator.Validate([candidate], Evidence(), Ctx(typed: [Secret, "x"])));
        Assert.True(s.Status == SuggestionStatuses.Rejected, name);
        Assert.Contains("typed", s.Reason);
        AssertNoSecret(s, Secret);
        AssertNoSecret(s, "hunt");
        Assert.Equal(spec.Kind, s.Id);                           // the readable id may quote the text: kind only
        Assert.True(Protocol.IsValidName(s.Id));
    }

    [Fact]
    public void ShortTypedRunsUnderTwoCharactersAreIgnored()
    {
        var s = Assert.Single(AssertionSuggestionValidator.Validate([Candidate(UiAssert.ScreenContains("title", "Find"))], Evidence(), Ctx(typed: ["F", "i"])));
        Assert.Equal(SuggestionStatuses.Valid, s.Status);
        var two = Assert.Single(AssertionSuggestionValidator.Validate([Candidate(UiAssert.ScreenContains("title", "Find"))], Evidence(), Ctx(typed: ["in"])));
        Assert.Equal(SuggestionStatuses.Rejected, two.Status);   // "Find" contains the typed run "in" (length 2)
    }

    public static TheoryData<string, AssertionSpec> InputLineSpecs() => new()
    {
        { "screen-contains of the value", UiAssert.ScreenContains("value-shown", Secret) },
        { "text-at inside the input line", UiAssert.TextAt("value-at", 12, 4, "hun") },
        { "selector by the input line's text", UiAssert.Visible("input-visible", new ViewSelector(Type: "TInputLine", Text: Secret)) },
        { "derived input line selected by text", UiAssert.Visible("pin-visible", new ViewSelector(Text: "4711pin")) },
    };

    [Theory]
    [MemberData(nameof(InputLineSpecs))]
    public void InputLineValuesAreRejectedEvenWhenNotTyped(string name, AssertionSpec spec)
    {
        var s = Single(spec);
        Assert.True(s.Status == SuggestionStatuses.Rejected, name + ": " + s.Status);
        Assert.Contains("input line", s.Reason);
        AssertNoSecret(s, Secret);
        AssertNoSecret(s, "4711pin");
    }

    public static TheoryData<string, string> DynamicTexts() => new()
    {
        { "clock time", "Saved at 12:30" },
        { "clock time with seconds", "Elapsed 1:02:03" },
        { "ISO date", "Build 2026-11-06" },
        { "dotted date", "Modified 06.11.26" },
        { "month date", "Due Nov 6" },
        { "long number", "Order 12345" },
        { "percentage", "Done 45 %" },
        { "counter", "Item 3/10" },
        { "counter words", "Page 3 of 10" },
        { "hex token", "Id deadbeefcafe" },
        { "base64 token", "Key aB3dE5fG7hJ9kL" },
        { "GUID", "3f2504e0-4f89-11d3-9a0c-0305e82c3301" },
        { "windows path", @"Open C:\Users\me\notes.txt" },
        { "UNC path", @"\\server\share\x" },
        { "unix path", "Saved /home/me/notes" },
        { "URL", "See https://example.com/a" },
        { "www", "Visit www.example.com" },
        { "e-mail", "Mail me@example.com" },
    };

    [Theory]
    [MemberData(nameof(DynamicTexts))]
    public void DynamicLookingTextIsRejectedAndRedacted(string category, string text)
    {
        var candidate = Candidate(UiAssert.ScreenContains("dynamic", text), rationale: "Observed \"" + text + "\".");
        var s = Assert.Single(AssertionSuggestionValidator.Validate([candidate], Evidence(), Ctx()));
        Assert.True(s.Status == SuggestionStatuses.Rejected, category);
        Assert.Contains("looks dynamic", s.Reason);
        Assert.Equal(AssertionSuggestionValidator.Redacted, s.Spec.Text);
        AssertNoSecret(s, text);
        Assert.NotEmpty(AssertionSuggestionValidator.DynamicCategories(text));
    }

    [Theory]
    [InlineData("Alt-X Exit")]
    [InlineData("Ctrl+Shift+F12")]
    [InlineData("F10 Menu")]
    [InlineData("Configuration")]
    [InlineData("TSharpWorkbench")]
    [InlineData("Save as...")]
    [InlineData("drop-down/popup")]
    [InlineData("Find")]
    public void StaticTextIsNotDynamic(string text) => Assert.Empty(AssertionSuggestionValidator.DynamicCategories(text));

    [Fact]
    public void DynamicSelectorTextIsRejectedToo()
    {
        var s = Single(UiAssert.Visible("clock", new ViewSelector(Type: "TStaticText", Text: "12:30")));
        Assert.Equal(SuggestionStatuses.Rejected, s.Status);
        Assert.Equal(AssertionSuggestionValidator.Redacted, s.Spec.View!.Text);
        Assert.Equal("TStaticText", s.Spec.View.Type);
    }

    [Fact]
    public void TrivialCellTextIsRejected()
    {
        var s = Single(UiAssert.ScreenContains("blank", " F "));
        Assert.Equal(SuggestionStatuses.Rejected, s.Status);
        Assert.Contains("too short", s.Reason);
    }

    // ── gate 4: v1 validity and grid ──────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, AssertionSpec> MalformedSpecs() => new()
    {
        { "screen-contains without text", new AssertionSpec("missing-text", AssertionKinds.ScreenContains) },
        { "menu-selected without text", new AssertionSpec("missing-text", AssertionKinds.MenuSelected) },
        { "invalid menu item state", UiAssert.MenuItem("bad-state", "Open", "bogus") },
        { "invalid status state", UiAssert.StatusItem("bad-state", "Alt-X Exit", "bogus") },
        { "menu depth out of range", UiAssert.MenuDepth("deep", 99) },
        { "empty selector", UiAssert.Visible("empty", new ViewSelector()) },
        { "negative x", UiAssert.TextAt("neg", -1, 3, "Find") },
    };

    [Theory]
    [MemberData(nameof(MalformedSpecs))]
    public void MalformedSpecsAreRejectedByTheRealParser(string name, AssertionSpec spec)
    {
        var s = Single(spec);
        Assert.True(s.Status == SuggestionStatuses.Rejected, name + ": " + s.Status + " " + s.Reason);
        Assert.StartsWith("Not a valid schema-v1 assertion", s.Reason);
    }

    [Fact]
    public void TextAtAfterAResizeIsUnsupportedButScreenContainsStaysValid()
    {
        var result = Validate(Evidence(), Ctx(width: 50, height: 12), UiAssert.TextAt("title-at", 15, 3, "Find"), UiAssert.ScreenContains("title", "Find"));
        Assert.Equal([SuggestionStatuses.Unsupported, SuggestionStatuses.Valid], result.Select(r => r.Status));
        Assert.Contains("40x12", result[0].Reason);
        Assert.Contains("50x12", result[0].Reason);
    }

    // ── gate 5: uniqueness ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AmbiguousAndMissingSelectorsAreUnsupported()
    {
        var result = Validate(Evidence(), Ctx(),
            UiAssert.Visible("ok-visible", new ViewSelector(Type: "TButton", Text: "OK")),   // two OK buttons
            UiAssert.Exists("ok-exists", new ViewSelector(Type: "TButton", Text: "OK")),      // the evaluator would accept many
            UiAssert.Visible("nothing", ViewSelector.ById("nope")));
        Assert.All(result, r => Assert.Equal(SuggestionStatuses.Unsupported, r.Status));
        Assert.Contains("ambiguous", result[0].Reason);
        Assert.Contains("2 views", result[0].Reason);
        Assert.Contains("ambiguous", result[1].Reason);
        Assert.Contains("no match", result[2].Reason);
        Assert.All(result, r => Assert.Null(r.Outcome));
    }

    // ── gate 6: evaluator ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvaluatorFailIsUnsupportedWithItsExplanation()
    {
        var result = Validate(Evidence(), Ctx(),
            UiAssert.Focused("cancel-focused", new ViewSelector(Type: "TButton", Text: "Cancel")),
            UiAssert.Modal("no-modal", null),
            UiAssert.MenuSelected("menu-open", "Open"),
            UiAssert.ScreenContains("absent-text", "Replace"));
        Assert.All(result, r => Assert.Equal((SuggestionStatuses.Unsupported, AssertionOutcomes.Fail), (r.Status, r.Outcome)));
        Assert.Contains("evaluator reports FAIL", result[0].Reason);
        Assert.Contains("Focus is on a different view", result[0].Reason);
        Assert.All(result, r => Assert.False(r.DefaultSelected));
    }

    [Fact]
    public void EvaluatorUnsupportedIsReportedWhenEvidenceIsMissing()
    {
        var evidence = Evidence(inspection: new CheckpointInspection(Tree(), null, Status(), ["menu: inspector failed (x)"]));
        var s = Assert.Single(Validate(evidence, Ctx(), UiAssert.MenuDepth("menu-depth", 1)));
        Assert.Equal((SuggestionStatuses.Unsupported, AssertionOutcomes.Unsupported), (s.Status, s.Outcome));
        Assert.Contains("menu: inspector failed", s.Reason);
    }

    // ── ids, severity, duplicates, selection, confidence ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExactDuplicatesCollapseIntoTheFirst()
    {
        var a = Candidate(UiAssert.ScreenContains("first", "Find"));
        var b = Candidate(UiAssert.ScreenContains("second", "Find") with { Severity = AssertionSeverity.Error }, rule: "other");
        var c = Candidate(UiAssert.ScreenContains("third", "Cancel"));
        var result = AssertionSuggestionValidator.Validate([a, b, c], Evidence(), Ctx());
        Assert.Equal(["first", "third"], result.Select(r => r.Id));
    }

    [Theory]
    [InlineData("Dialog Modal!", "dialog-modal")]
    [InlineData("  --Find__dialog--  ", "find-dialog")]
    [InlineData("ÄÖÜ", "screen-contains")]
    [InlineData("", "screen-contains")]
    [InlineData("UPPER-case-42", "upper-case-42")]
    public void IdsAreNormalizedToValidNames(string readable, string expected)
    {
        var s = Single(UiAssert.ScreenContains(readable, "Find"));
        Assert.Equal(expected, s.Id);
        Assert.True(Protocol.IsValidName(s.Id));
    }

    [Fact]
    public void LongIdsAreTruncatedAndStillValid()
    {
        var s = Single(UiAssert.ScreenContains(new string('a', 30) + "-" + new string('b', 30), "Find"));
        Assert.True(Protocol.IsValidName(s.Id));
        Assert.True(s.Id.Length <= 40);
    }

    [Fact]
    public void IdCollisionsGetDeterministicSuffixesInCandidateOrder()
    {
        var result = Validate(Evidence(), Ctx(), UiAssert.ScreenContains("x-2", "Find"), UiAssert.ScreenContains("x", "Cancel"),
            UiAssert.MenuDepth("x", 1), UiAssert.StatusItem("x", "Alt-X Exit"), UiAssert.MenuSelected("", "New"), UiAssert.MenuItem("", "Open", "enabled"));
        Assert.Equal(["x-2", "x", "x-3", "x-4", "menu-selected", "menu-item-state"], result.Select(r => r.Id));
        Assert.Equal(result.Count, result.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void SeverityIsAlwaysWarning()
    {
        var result = Validate(Evidence(), Ctx(), UiAssert.ScreenContains("a", "Find") with { Severity = AssertionSeverity.Error },
            UiAssert.AttributeAt("b", 0, 0, 7) with { Severity = "error" }, UiAssert.ScreenContains("c", Secret));
        Assert.All(result, r => Assert.Equal(AssertionSeverity.Warning, r.Spec.Severity));
    }

    [Fact]
    public void DefaultSelectionTakesTheFirstValidHighOrMediumUpToTheCap()
    {
        var candidates = new[]
        {
            Candidate(UiAssert.Visible("bad", ViewSelector.ById("nope"))),                       // unsupported
            Candidate(UiAssert.ScreenContains("low", "Find"), confidence: Confidences.Low),      // valid, low
            Candidate(UiAssert.MenuDepth("v1", 1)),
            Candidate(UiAssert.MenuSelected("v2", "New"), confidence: Confidences.Medium),
            Candidate(UiAssert.StatusItem("v3", "Alt-X Exit")),
            Candidate(UiAssert.Modal("v4", ViewSelector.ById("find-dialog"))),
            Candidate(UiAssert.Focused("v5", ViewSelector.ById("ok-button"))),
            Candidate(UiAssert.SubmenuOpen("v6", "File")),
        };
        var result = AssertionSuggestionValidator.Validate(candidates, Evidence(), Ctx());
        Assert.Equal(["v1", "v2", "v3", "v4"], result.Where(r => r.DefaultSelected).Select(r => r.Id));
        Assert.Equal(7, result.Count(r => r.Status == SuggestionStatuses.Valid));
        var two = AssertionSuggestionValidator.Validate(candidates, Evidence(), Ctx() with { MaximumSelected = 2 });
        Assert.Equal(["v1", "v2"], two.Where(r => r.DefaultSelected).Select(r => r.Id));
        Assert.DoesNotContain(AssertionSuggestionValidator.Validate(candidates, Evidence(), Ctx() with { MaximumSelected = 0 }), r => r.DefaultSelected);
    }

    [Fact]
    public void ConfidenceIsLoweredOneLevelPerDegradation()
    {
        string D(string confidence, string source, params string[] flags) =>
            AssertionSuggestionValidator.Downgrade(confidence, Evidence(source: source, flags: flags));
        Assert.Equal(Confidences.High, D(Confidences.High, EvidenceSources.Snapshot));
        Assert.Equal(Confidences.Medium, D(Confidences.High, EvidenceSources.Inspection));
        Assert.Equal(Confidences.Medium, D(Confidences.High, EvidenceSources.PickSnapshot));
        Assert.Equal(Confidences.Medium, D(Confidences.High, EvidenceSources.Snapshot, EvidenceFlags.CoverageIncomplete));
        Assert.Equal(Confidences.Low, D(Confidences.High, EvidenceSources.Inspection, EvidenceFlags.CoverageIncomplete));
        Assert.Equal(Confidences.Low, D(Confidences.Medium, EvidenceSources.Snapshot, EvidenceFlags.TreeTruncated));
        Assert.Equal(Confidences.Low, D(Confidences.Low, EvidenceSources.Snapshot));
        Assert.Equal(Confidences.Low, D("unknown", EvidenceSources.Snapshot));

        var s = Assert.Single(AssertionSuggestionValidator.Validate([Candidate(UiAssert.MenuDepth("menu-depth", 1))],
            Evidence(source: EvidenceSources.Inspection, flags: [EvidenceFlags.CoverageIncomplete]), Ctx()));
        Assert.Equal((SuggestionStatuses.Valid, Confidences.Low, false), (s.Status, s.Confidence, s.DefaultSelected));
        Assert.Contains(EvidenceFlags.CoverageIncomplete, s.Flags);
    }

    [Fact]
    public void ProvenanceIsCarriedThrough()
    {
        var candidate = new AssertionCandidate("p1", UiAssert.MenuDepth("menu-depth", 1), SuggestionOrigins.Pick, 42, "pick-menu", "Menu open.",
            Confidences.High, ["custom-flag"]);
        var s = Assert.Single(AssertionSuggestionValidator.Validate([candidate], Evidence(), Ctx()));
        Assert.Equal(("p1", SuggestionOrigins.Pick, (long?)42, "pick-menu", "Menu open."), (s.ProposalId, s.Origin, s.PickId, s.Rule, s.Rationale));
        Assert.Contains("custom-flag", s.Flags);
    }

    // ── determinism ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SameInputGivesSameOutputAndShuffledInputGivesTheSamePerCandidateVerdicts()
    {
        var specs = new[]
        {
            UiAssert.Modal("a-modal", ViewSelector.ById("find-dialog")), UiAssert.ScreenContains("b-secret", Secret),
            UiAssert.Visible("c-ambiguous", new ViewSelector(Type: "TButton", Text: "OK")), UiAssert.TextAt("d-title", 15, 3, "Find"),
            UiAssert.Shadow("e-shadow", ViewSelector.ById("find-dialog"), 8), UiAssert.ScreenContains("f-clock", "12:30"),
            UiAssert.Focused("g-cancel", new ViewSelector(Type: "TButton", Text: "Cancel")), UiAssert.MenuDepth("h-depth", 1),
        };
        var candidates = specs.Select(s => Candidate(s)).ToArray();
        string Show(AssertionSuggestion s) => $"{s.Spec with { Id = "" }} {s.Status} {s.Outcome} {s.Reason} {s.Confidence} {s.Rationale}";
        var first = AssertionSuggestionValidator.Validate(candidates, Evidence(), Ctx(typed: [Secret])).Select(Show).ToArray();
        var again = AssertionSuggestionValidator.Validate(candidates, Evidence(), Ctx(typed: [Secret])).Select(Show).ToArray();
        Assert.Equal(first, again);

        var shuffled = new[] { 5, 2, 7, 0, 3, 6, 1, 4 }.Select(i => candidates[i]).ToArray();
        var other = AssertionSuggestionValidator.Validate(shuffled, Evidence(), Ctx(typed: [Secret]));
        // Redacted candidates share the kind as id root, so their suffix follows candidate order; everything else is order-free.
        Assert.Equal(first.Order(StringComparer.Ordinal), other.Select(Show).Order(StringComparer.Ordinal));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertNoSecret(AssertionSuggestion s, string secret)
    {
        foreach (string text in new[] { s.ToString(), s.Spec.ToString(), s.Reason ?? "", s.Rationale, s.Id, AssertionJson.ToJson(s.Spec).ToJsonString() })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    private static AssertionSuggestion Single(AssertionSpec spec) => Assert.Single(Validate(Evidence(), Ctx(), spec));

    private static IReadOnlyList<AssertionSuggestion> Validate(CheckpointEvidence evidence, ValidationContext context, params AssertionSpec[] specs) =>
        AssertionSuggestionValidator.Validate(specs.Select(s => Candidate(s)).ToArray(), evidence, context);

    private static AssertionCandidate Candidate(AssertionSpec spec, string confidence = Confidences.High, string rule = "test-rule", string rationale = "Observed in this state.") =>
        new("p1", spec, SuggestionOrigins.State, null, rule, rationale, confidence, []);

    private static ValidationContext Ctx(int width = W, int height = H, string[]? typed = null) =>
        new("p1", "checkpoint-1", width, height, typed ?? []);

    private static CheckpointEvidence Evidence(CheckpointInspection? inspection = null, ViewTree? tree = null, bool omitted = false,
        bool noInspection = false, string source = EvidenceSources.Snapshot, string[]? flags = null) =>
        new("p1", 1, 0xabc, 10, source, 5, Cells(), noInspection ? null : inspection ?? Inspection(tree ?? Tree()), omitted, flags ?? []);

    private static ScreenSnapshot Cells()
    {
        var cells = Enumerable.Range(0, W * H).Select(_ => new TScreenChar(' ', 7)).ToArray();
        void Put(int x, int y, string text, byte attr = 7) { for (int i = 0; i < text.Length; i++) cells[y * W + x + i] = new TScreenChar(text[i], attr); }
        Put(1, 0, "File"); Put(7, 0, "Edit");
        Put(2, 2, "New", 0x20); Put(2, 3, "Open");
        Put(15, 3, "Find"); Put(12, 4, Secret); Put(12, 5, "4711pin");
        Put(7, 6, "OK"); Put(15, 6, "Cancel"); Put(27, 6, "OK");
        Put(1, 11, "Alt-X Exit");
        return new ScreenSnapshot(W, H, cells);
    }

    private static CheckpointInspection Inspection(ViewTree? tree) => new(tree, Menu(), Status(), []);

    private static MenuState Menu()
    {
        MenuEntry Entry(int i, string text, CellRect screen, bool selected = false, int attr = 7) =>
            new(i, text, "~" + text, null, 100 + i, true, false, false, false, null, 0, null, 0, selected, screen, "match", attr);
        var bar = new MenuViewState(0, "TSharpVision.Menus.TMenuBar", "0/0", new(0, 0, W, 1), true, true, 0, "File", null,
            [Entry(0, "File", new(0, 0, 6, 1), true), Entry(1, "Edit", new(6, 0, 6, 1))]);
        var box = new MenuViewState(1, "TSharpVision.Menus.TMenuBox", "0/3", new(0, 1, 12, 4), true, true, 0, "New", "File",
            [Entry(0, "New", new(1, 2, 10, 1), true, 0x20), Entry(1, "Open", new(1, 3, 10, 1))]);
        return new(true, 1, bar, [box]);
    }

    private static StatusState Status() =>
        new("TSharpVision.Menus.TStatusLine", "0/1", new(0, 11, W, 1), 0, 0, 0, 0xFFFF, null, null, "none",
            [new(0, "Alt-X Exit", "~Alt-X~ Exit", 0x2d00, "Alt+X", 1, true, true, new(0, 11, 12, 1), "match", 7)], " Alt-X Exit");

    private static ViewTree Tree()
    {
        ViewNode N(string path, string type, string kind, CellRect screen, string? text = null, string? id = null, params ViewNode[] children) =>
            new(path, id, type, kind, screen, screen, screen,
                new ViewFlags(true, true, false, false, false, false, false, false, true, false, 0, 0), 0, text, null, null, children, 0);
        var dialog = N("0/2/0", "TSharpVision.Dialogs.TDialog", "TDialog", new(10, 2, 20, 6), "Find", "find-dialog",
            N("0/2/0/0", "TSharpVision.Dialogs.TInputLine", "TInputLine", new(12, 4, 10, 1), Secret, "search-input"),
            N("0/2/0/1", "Sample.PinLine", "TInputLine", new(12, 5, 10, 1), "4711pin"),
            N("0/2/0/2", "TSharpVision.Dialogs.TButton", "TButton", new(6, 6, 6, 1), "OK", "ok-button"),
            N("0/2/0/3", "TSharpVision.Dialogs.TButton", "TButton", new(14, 6, 8, 1), "Cancel"));
        var other = N("0/2/1", "TSharpVision.Views.TWindow", "TWindow", new(24, 2, 14, 8), "Other", null,
            N("0/2/1/0", "TSharpVision.Dialogs.TButton", "TButton", new(26, 6, 6, 1), "OK"));
        var desk = N("0/2", "TSharpVision.App.TDeskTop", "TDeskTop", new(0, 1, W, 10), null, null, dialog, other);
        var root = N("0", "TSharpVision.App.TApplication", "TApplication", new(0, 0, W, H), null, null,
            N("0/0", "TSharpVision.Menus.TMenuBar", "TMenuBar", new(0, 0, W, 1)),
            N("0/1", "TSharpVision.Menus.TStatusLine", "TStatusLine", new(0, 11, W, 1)),
            desk,
            N("0/3", "TSharpVision.Menus.TMenuBox", "TMenuBox", new(0, 1, 12, 4)));
        return new(root, 13, false, "front-to-back", new(0, 0, W, H), new(0, 1, W, 10), "0/2/0", "0/2/0/2");
    }
}
