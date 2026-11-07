using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

// The composite layout-audit assertion in scenario schema v1: parser, schema, outcome mapping (the layout assertion contract),
// evaluator on real in-process captures, editor panel round-trip, Results/RunReport rows and the Review dialog (inactive
// children, layout suggestion detail). Outcome tests that must not depend on the rule families (geometry and text rules) use synthetic
// reports through LayoutAuditAssertion.Outcome; engine tests use rule filters and rule-independent properties.
// Optional logical captures with TSWB_LAYOUT_EVIDENCE=<directory> (c-{state}-{WxH}.logical.{png,txt,json}; logical only, NOT native).
[Collection("NonParallel")]
public sealed class LayoutAssertionTests
{
    private static string? Evidence => Environment.GetEnvironmentVariable("TSWB_LAYOUT_EVIDENCE") is { Length: > 0 } e ? e : null;

    // ── documents ────────────────────────────────────────────────────────────────────────────────────────────────────

    private const string Full = """
        { "id": "find-layout", "kind": "layout-audit", "view": { "type": "TDialog", "text": "Find" }, "profile": "ux",
          "rules": ["geom.overlap", "text.caption-clipped", "ux.button-row"],
          "suppress": [ { "rule": "ux.button-row", "view": { "id": "help-button" }, "reason": "Help sits apart by design." } ] }
        """;

    private static string Document(params string[] assertions) =>
        $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"layout/audit","title":"Layout","description":"Layout-audit document.","backend":"proxy-logical","expectedOutcome":"passed","steps":[{"kind":"key","key":"kbF10"},{"kind":"checkpoint","id":"check","assertions":[{{string.Join(",", assertions)}}]}]}""";

    private static AssertionSpec[] Load(string document) =>
        ExternalScenarios.LoadFromBytes(Path.Combine(Path.GetTempPath(), "layout.scenario.json"), Encoding.UTF8.GetBytes(document))
            .Scenario.Steps.Single(s => s.Kind == ScenarioStep.Checkpoint).Assertions!;

    private static string? Refusal(string assertion)
    {
        try { Load(Document(assertion)); return null; }
        catch (ExternalScenarioException ex) { return ex.Message; }
    }

    [Fact]
    public void SchemaV1DocumentWithLayoutAuditLoadsThroughTheRuntimeParser()
    {
        var specs = Load(Document(Full, """{"id":"plain","kind":"layout-audit"}""", """{"id":"strict","kind":"layout-audit","severity":"error","profile":"strict","rules":["ux.balance"]}"""));
        Assert.Equal(3, specs.Length);
        var full = specs[0];
        Assert.Equal((AssertionKinds.LayoutAudit, AssertionSeverity.Warning, "ux"), (full.Kind, full.Severity, full.Profile));
        Assert.Equal(new ViewSelector(Type: "TDialog", Text: "Find"), full.View);
        Assert.Equal(["geom.overlap", "text.caption-clipped", "ux.button-row"], full.Rules);
        var s = Assert.Single(full.Suppress!);
        Assert.Equal(("ux.button-row", ViewSelector.ById("help-button"), "Help sits apart by design."), (s.Rule, s.View, s.Reason));
        // Defaults: severity warning for layout-audit only, profile/rules/suppress absent (engine default geometry).
        Assert.Equal((AssertionSeverity.Warning, (string?)null, (string[]?)null, (LayoutSuppression[]?)null, (ViewSelector?)null),
            (specs[1].Severity, specs[1].Profile, specs[1].Rules, specs[1].Suppress, specs[1].View));
        Assert.Equal(LayoutProfiles.Geometry, LayoutAuditRequest.Of(specs[1]).Profile);
        Assert.Equal(AssertionSeverity.Error, specs[2].Severity);
        Assert.Equal([Protocol.EvidenceKinds.ViewTree], full.RequiredEvidence());
        // Other kinds keep severity error by default.
        Assert.Equal(AssertionSeverity.Error, Load(Document("""{"id":"v","kind":"view-exists","view":{"type":"TDialog"}}"""))[0].Severity);
        WriteSchemaSample("layout-audit.scenario.json", Document(Full, """{"id":"plain","kind":"layout-audit"}""",
            """{"id":"strict","kind":"layout-audit","severity":"error","profile":"strict","rules":["ux.balance"]}"""));
    }

    [Theory]
    [InlineData("""{"id":"a","kind":"layout-audit","bogus":1}""", "unknown property 'bogus'")]
    [InlineData("""{"id":"a","kind":"layout-audit","profile":"fancy"}""", "profile must be")]
    [InlineData("""{"id":"a","kind":"layout-audit","profile":null}""", "cannot be null")]
    [InlineData("""{"id":"a","kind":"layout-audit","view":null}""", "cannot be null")]
    [InlineData("""{"id":"a","kind":"layout-audit","view":{}}""", "Selector must contain")]
    [InlineData("""{"id":"a","kind":"layout-audit","rules":[]}""", "1 to 32")]
    [InlineData("""{"id":"a","kind":"layout-audit","rules":["geom.nope"]}""", "unknown layout rule")]
    [InlineData("""{"id":"a","kind":"layout-audit","rules":["ux.balance"]}""", "not part of profile geometry")]
    [InlineData("""{"id":"a","kind":"layout-audit","rules":["geom.overlap","geom.overlap"]}""", "unique")]
    [InlineData("""{"id":"a","kind":"layout-audit","rules":"geom.overlap"}""", "")]
    [InlineData("""{"id":"a","kind":"layout-audit","suppress":[{"rule":"geom.overlap"}]}""", "'reason' is required")]
    [InlineData("""{"id":"a","kind":"layout-audit","suppress":[{"rule":"geom.overlap","reason":"   "}]}""", "'reason' is required")]
    [InlineData("""{"id":"a","kind":"layout-audit","suppress":[{"rule":"geom.overlap","reason":"r","extra":1}]}""", "unknown property 'extra'")]
    [InlineData("""{"id":"a","kind":"layout-audit","suppress":[{"rule":"geom.overlap","reason":"r","view":{}}]}""", "selector must contain")]
    [InlineData("""{"id":"a","kind":"layout-audit","suppress":[{"rule":"geom.overlap","reason":"r","view":null}]}""", "cannot be null")]
    [InlineData("""{"id":"a","kind":"layout-audit","suppress":[{"rule":"ux.balance","reason":"r"}]}""", "not part of profile geometry")]
    [InlineData("""{"id":"a","kind":"layout-audit","suppress":[{"rule":"text.nope","reason":"r"}]}""", "unknown layout rule")]
    [InlineData("""{"id":"a","kind":"layout-audit","severity":"info"}""", "")]
    [InlineData("""{"id":"a","kind":"view-exists","view":{"type":"T"},"profile":"ux"}""", "unknown property 'profile'")]
    [InlineData("""{"id":"a","kind":"screen-contains","text":"x","rules":["geom.overlap"]}""", "unknown property 'rules'")]
    [InlineData("""{"id":"a","kind":"modal","suppress":[]}""", "unknown property 'suppress'")]
    [InlineData("""{"id":"a","kind":"layout-check"}""", "Unknown assertion kind")]
    public void InvalidLayoutFieldsAreRefused(string assertion, string expected)
    {
        string? refusal = Refusal(assertion);
        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal);
    }

    [Fact]
    public void LimitsOfRulesSuppressionsAndReasonsAreEnforced()
    {
        string Suppress(int n, int reason = 10) => "[" + string.Join(",", Enumerable.Range(0, n).Select(_ => $$"""{"rule":"geom.overlap","reason":"{{new string('r', reason)}}"}""")) + "]";
        Assert.Null(Refusal($$"""{"id":"a","kind":"layout-audit","suppress":{{Suppress(32)}}}"""));
        Assert.Contains("at most 32", Refusal($$"""{"id":"a","kind":"layout-audit","suppress":{{Suppress(33)}}}"""));
        Assert.Null(Refusal($$"""{"id":"a","kind":"layout-audit","suppress":{{Suppress(1, 200)}}}"""));
        Assert.Contains("at most 200", Refusal($$"""{"id":"a","kind":"layout-audit","suppress":{{Suppress(1, 201)}}}"""));
        var all = LayoutRules.All.Select(r => '"' + r.Id + '"').ToArray();
        Assert.Null(Refusal($$"""{"id":"a","kind":"layout-audit","profile":"strict","rules":[{{string.Join(",", all)}}]}"""));
        Assert.Null(Refusal("""{"id":"a","kind":"layout-audit","suppress":[]}"""));
    }

    [Fact]
    public void AssertionJsonRoundTripPreservesTheLayoutFields()
    {
        var spec = UiAssert.LayoutAudit("dialog-layout", new ViewSelector(Type: "TDialog", Text: "Find"), LayoutProfiles.Ux, ["geom.overlap", "ux.button-row"],
            [new("ux.button-row", ViewSelector.ById("help"), "Help sits apart."), new("geom.overlap", null, "Known overlap.")]);
        var json = AssertionJson.ToJson(spec);
        Assert.Equal("""{"id":"dialog-layout","kind":"layout-audit","severity":"warning","view":{"type":"TDialog","text":"Find"},"profile":"ux","rules":["geom.overlap","ux.button-row"],"suppress":[{"rule":"ux.button-row","view":{"id":"help"},"reason":"Help sits apart."},{"rule":"geom.overlap","reason":"Known overlap."}]}""",
            json.ToJsonString());
        var (specs, error) = AssertionJson.Parse([json], 80, 25);
        Assert.Null(error);
        Assert.Equal(json.ToJsonString(), AssertionJson.ToJson(Assert.Single(specs!)).ToJsonString());
        // Minimal: no profile written back when the document had none.
        var minimal = AssertionJson.Parse([JsonNode.Parse("""{"id":"x","kind":"layout-audit"}""")!.AsObject()], 80, 25).Specs!.Single();
        Assert.Equal("""{"id":"x","kind":"layout-audit","severity":"warning"}""", AssertionJson.ToJson(minimal).ToJsonString());
    }

    // The repository schema has exactly one layout-audit branch whose vocabulary matches the runtime registry (structural check;
    // full JSON Schema validation of the sample document is done separately).
    [Fact]
    public void RepositorySchemaDescribesLayoutAuditInVersionOne()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "scenarios", "scenario-v1.schema.json")))!;
        Assert.Equal(1, (int)schema["properties"]!["version"]!["const"]!);
        var branches = schema["$defs"]!["assertion"]!["oneOf"]!.AsArray().Select(b => b!).ToArray();
        Assert.Equal(ScenarioSchemaV1.AssertionKinds.Order(), branches.Select(b => (string)b["properties"]!["kind"]!["const"]!).Order());
        var layout = Assert.Single(branches, b => (string)b["properties"]!["kind"]!["const"]! == "layout-audit");
        Assert.Equal(new[] { "id", "kind", "severity" }.Concat(ScenarioSchemaV1.AssertionFields("layout-audit")).Order(), layout["properties"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["id", "kind"], layout["required"]!.AsArray().Select(x => (string)x!));
        Assert.False((bool)layout["additionalProperties"]!);
        Assert.Equal(LayoutProfiles.All, layout["properties"]!["profile"]!["enum"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(LayoutRules.All.Select(r => r.Id), schema["$defs"]!["layoutRule"]!["enum"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(LayoutRules.All.Where(r => r.Family != LayoutFamilies.Ux).Select(r => r.Id), schema["$defs"]!["layoutGeometryRule"]!["enum"]!.AsArray().Select(x => (string)x!));
        Assert.All(LayoutRules.All, r => Assert.Matches((string)schema["$defs"]!["layoutRule"]!["pattern"]!, r.Id));
        Assert.Equal(200, (int)schema["$defs"]!["layoutSuppression"]!["properties"]!["reason"]!["maxLength"]!);
        Assert.Equal(ExternalScenarios.MaxLayoutRules, (int)layout["properties"]!["rules"]!["maxItems"]!);
        Assert.Equal(ExternalScenarios.MaxLayoutSuppressions, (int)layout["properties"]!["suppress"]!["maxItems"]!);
    }

    // ── outcome mapping (the layout assertion contract), independent of the rule families ────────────────────────────────────────────

    private static readonly ViewFlags Shown = new(true, true, false, false, false, false, false, false, true, false, 0, 0);
    private static ViewNode Node(string path, string kind, CellRect rect, CellRect? visible, params ViewNode[] children) =>
        new(path, null, kind, kind, rect, rect, visible, Shown, 0, kind == "TDialog" ? "Find" : null, null, null, children, 0);
    private static ViewTree Tree(bool hiddenDialog = false)
    {
        var dialog = Node("0/1/0", "TDialog", new(10, 3, 40, 10), hiddenDialog ? null : new CellRect(10, 3, 40, 10));
        var desk = Node("0/1", "TDeskTop", new(0, 1, 80, 23), new CellRect(0, 1, 80, 23), dialog);
        return new(Node("0", "TApplication", new(0, 0, 80, 25), new CellRect(0, 0, 80, 25), desk), 3, false, "front-to-back", new(0, 0, 80, 25), new(0, 1, 80, 23), null, null);
    }
    private static LayoutFinding Finding(string rule, string @class, string path = "0/1/0", string message = "Fix it.") =>
        new(rule, @class, "high", path, $"TDialog \"Find\" ({path})", null, new CellRect(12, 4, 6, 2), null, null, message, "observed 6x2");
    private static LayoutCoverage Coverage(string profile = "geometry", string[]? evaluated = null, string[]? notApplicable = null, LayoutRuleGap[]? gaps = null,
        bool truncated = false, int omitted = 0, LayoutSuppressedFinding[]? suppressed = null) =>
        new(profile, "0/1/0", evaluated ?? ["geom.overlap"], notApplicable ?? [], gaps ?? [], suppressed ?? [], 5, 5, truncated, omitted, []);
    private static AssertionResult Map(LayoutAuditReport report, string profile = "geometry", ViewTree? tree = null) =>
        LayoutAuditAssertion.Outcome(profile, report, tree ?? Tree(), (o, e, a, x, l) => new AssertionResult("layout", AssertionKinds.LayoutAudit, o, AssertionSeverity.Warning, e, a, x, "s", "c", "80x25", l));

    [Fact]
    public void ErrorFindingsFailAndTheExplanationListsAtMostThree()
    {
        var findings = new[] { Finding("geom.overlap", "error", "0/1/0/2"), Finding("geom.overlap", "error", "0/1/0/3"), Finding("text.caption-clipped", "error", "0/1/0/4"),
            Finding("text.label-clipped", "error", "0/1/0/5"), Finding("ux.balance", "warning") };
        var r = Map(new(findings, Coverage()));
        Assert.Equal(AssertionOutcomes.Fail, r.Outcome);
        Assert.Equal("no error-class layout findings (profile geometry)", r.Expected);
        Assert.Equal("4 error, 1 warning finding(s)", r.Actual);
        Assert.StartsWith("4 failing finding(s): geom.overlap at (12,4 6x2) on TDialog \"Find\" (0/1/0/2): Fix it.; ", r.Explanation);
        Assert.Contains("(+1 more failing finding(s)", r.Explanation);
        Assert.DoesNotContain("text.label-clipped at", r.Explanation);
        Assert.Equal("0/1/0/2", r.Location);
        Assert.Same(findings, r.Layout!.Findings);
    }

    [Fact]
    public void WarningsFailOnlyInStrictAndPassOtherwiseWithTheirList()
    {
        var report = new LayoutAuditReport([Finding("ux.button-row", "warning", message: "Uneven gaps."), Finding("ux.balance", "info")], Coverage("ux", ["ux.button-row", "ux.balance"]));
        var ux = Map(report, "ux");
        Assert.Equal(AssertionOutcomes.Pass, ux.Outcome);
        Assert.Equal("0 error, 1 warning finding(s)", ux.Actual);
        Assert.Contains("1 warning finding(s) (do not fail profile ux): ux.button-row at (12,4 6x2)", ux.Explanation);
        Assert.Equal("0/1/0", ux.Location);
        var strict = Map(report with { Coverage = report.Coverage with { Profile = "strict" } }, "strict");
        Assert.Equal(AssertionOutcomes.Fail, strict.Outcome);
        Assert.Equal("no error or warning layout findings (profile strict)", strict.Expected);
        Assert.StartsWith("1 failing finding(s): ux.button-row", strict.Explanation);   // info never fails
    }

    [Fact]
    public void TruncatedTreeIsUnsupportedUnlessErrorsExistAndNothingRunIsUnsupported()
    {
        var truncated = Map(new([], Coverage(truncated: true, omitted: 7)));
        Assert.Equal(AssertionOutcomes.Unsupported, truncated.Outcome);
        Assert.StartsWith("The captured tree was truncated: 7 view(s) not inspected; the audit cannot pass.", truncated.Explanation);
        Assert.NotNull(truncated.Layout);
        Assert.Equal(AssertionOutcomes.Fail, Map(new([Finding("geom.offscreen", "error")], Coverage(truncated: true, omitted: 7))).Outcome);
        Assert.Equal(AssertionOutcomes.Unsupported, Map(new([Finding("ux.balance", "warning")], Coverage("ux", truncated: true, omitted: 7)), "ux").Outcome);

        var none = Map(new([], Coverage(evaluated: [], gaps: [new("geom.overlap", "The rule did not run on this capture.", null)])));
        Assert.Equal(AssertionOutcomes.Unsupported, none.Outcome);
        Assert.Contains("No selected rule could run on this capture (1 rule gap(s), e.g. geom.overlap: The rule did not run on this capture.)", none.Explanation);
    }

    [Fact]
    public void PartialCoverageIsExplainedOnPass()
    {
        var gaps = new LayoutRuleGap[]
        {
            new("text.label-clipped", "The target does not expose label markup.", "0/1/0/1"), new("text.label-clipped", "The target does not expose label markup.", "0/1/0/2"),
            new("text.static-clipped", "The target does not expose static text markup.", "0/1/0/3"),
        };
        var r = Map(new([], Coverage(evaluated: ["geom.overlap", "geom.offscreen"], notApplicable: ["text.cluster-clipped"], gaps: gaps,
            suppressed: [new("geom.overlap", "0/1/0/4", "Intended.")])));
        Assert.Equal(AssertionOutcomes.Pass, r.Outcome);
        Assert.Equal("Audited 5 of 5 view(s) (profile geometry): 2 rule(s) evaluated, 1 not applicable. Partial coverage: 3 rule gap(s) in 2 rule(s) "
            + "(text.label-clipped: The target does not expose label markup. (2 views); text.static-clipped: The target does not expose static text markup.). "
            + "1 finding(s) suppressed with a reason.", r.Explanation);
        var complete = Map(new([], Coverage()));
        Assert.Equal("Audited 5 of 5 view(s) (profile geometry): 1 rule(s) evaluated, 0 not applicable.", complete.Explanation);
    }

    [Fact]
    public void HiddenScopeFails()
    {
        var r = Map(new([], Coverage()), tree: Tree(hiddenDialog: true));
        Assert.Equal(AssertionOutcomes.Fail, r.Outcome);
        Assert.Equal("hidden", r.Actual);
        Assert.Contains("not shown on screen", r.Explanation);
        Assert.NotNull(r.Layout);
    }

    // ── evaluator on real in-process captures ────────────────────────────────────────────────────────────────────────

    private sealed record Captured(ScreenSnapshot Cells, CheckpointInspection Inspection, string OkPath);

    private static Captured Take(TApplication app, Action<TDialog>? extra = null, InspectionLimits? limits = null, bool overlap = false)
    {
        var dialog = new TDialog(new TRect(10, 3, 60, 15), "Inspect me");
        var input = new TInputLine(new TRect(14, 2, 40, 3), 40); dialog.Insert(input);
        dialog.Insert(new TLabel(new TRect(2, 2, 13, 3), "~N~ame", input));
        var ok = new TButton(new TRect(14, 8, 24, 10), "~O~K", Views.cmOK, ButtonConstants.bfDefault); dialog.Insert(ok);
        var cancel = new TButton(new TRect(overlap ? 18 : 26, 8, overlap ? 30 : 38, 10), "Cancel", Views.cmCancel, ButtonConstants.bfNormal); dialog.Insert(cancel);
        ViewTreeInspector.Identify(ok, "ok"); ViewTreeInspector.Identify(cancel, "cancel"); ViewTreeInspector.Identify(dialog, "inspect");
        extra?.Invoke(dialog);
        input.Select();
        app.DeskTop!.Insert(dialog);
        app.StatusLine!.Update(); app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var inspection = Inspectors.Capture(app, cells, Protocol.EvidenceKinds.All, limits);
        return new(cells, inspection, ViewTreeInspector.PathOf(ok, app)!);
    }

    private static AssertionResult Eval(AssertionSpec spec, Captured c) => Assert.Single(AssertionEvaluator.Evaluate([spec], c.Cells, c.Inspection, "layout", "check"));
    private static string[] Covered(LayoutCoverage c) => c.Evaluated.Concat(c.NotApplicable).Concat(c.Unavailable.Select(g => g.Rule)).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void RealCaptureEvaluatesDeterministicallyWithProfileAndRuleFilter()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var c = Take(app);
        Assert.Empty(c.Inspection.Errors);
        var dialogScope = ViewSelector.ById("inspect");

        // Loaded through the runtime parser, evaluated by the real evaluator; twice → identical results.
        var spec = Load(Document("""{"id":"layout","kind":"layout-audit","view":{"id":"inspect"}}"""))[0];
        var first = Eval(spec, c); var second = Eval(spec, c);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.NotNull(first.Layout);
        Assert.Equal(AssertionSeverity.Warning, first.Severity);
        Assert.Equal(LayoutProfiles.Geometry, first.Layout!.Coverage.Profile);
        Assert.Equal(AssertionEvaluator.All(c.Inspection.ViewTree!.Root).Single(n => n.Id == "inspect").Path, first.Layout.Coverage.Scope);
        Assert.Contains(first.Outcome, new[] { AssertionOutcomes.Pass, AssertionOutcomes.Fail, AssertionOutcomes.Unsupported });

        // geometry: every geom/text rule is accounted for (evaluated, not applicable or an honest gap); no ux rule runs.
        Assert.Equal(LayoutRules.Selected(LayoutProfiles.Geometry, null).Order(StringComparer.Ordinal), Covered(first.Layout.Coverage));
        Assert.DoesNotContain(first.Layout.Findings, f => f.Rule.StartsWith("ux.", StringComparison.Ordinal));
        // ux: the ux rules are accounted for too.
        var ux = Eval(UiAssert.LayoutAudit("ux", dialogScope, LayoutProfiles.Ux), c);
        Assert.Equal(LayoutRules.All.Select(r => r.Id).Order(StringComparer.Ordinal), Covered(ux.Layout!.Coverage));
        // rules filter: only those rules appear anywhere.
        string[] only = [LayoutRules.Overlap, LayoutRules.CaptionClipped];
        var filtered = Eval(UiAssert.LayoutAudit("only", dialogScope, rules: only), c);
        Assert.Equal(only.Order(StringComparer.Ordinal), Covered(filtered.Layout!.Coverage));
        Assert.All(filtered.Layout.Findings, f => Assert.Contains(f.Rule, only));
        // Default scope with no executing modal: the application root.
        Assert.Equal("0", Eval(UiAssert.LayoutAudit("screen"), c).Layout!.Coverage.Scope);
    }

    [Fact]
    public void MissingViewTreeAndInvalidRequestsAreUnsupported()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var c = Take(app);
        var none = Eval(UiAssert.LayoutAudit("layout"), c with { Inspection = c.Inspection with { ViewTree = null } });
        Assert.Equal(AssertionOutcomes.Unsupported, none.Outcome);
        Assert.Null(none.Layout);
        Assert.Contains("'view-tree' evidence was not captured", none.Explanation);
        var noInspection = Assert.Single(AssertionEvaluator.Evaluate([UiAssert.LayoutAudit("layout")], c.Cells, null, "layout", "check"));
        Assert.Equal(AssertionOutcomes.Unsupported, noInspection.Outcome);
        // A spec built in code (not through the parser) with an unknown profile/rule: UNSUPPORTED, never a pass.
        var bad = Eval(UiAssert.LayoutAudit("bad", profile: "fancy"), c);
        Assert.Equal(AssertionOutcomes.Unsupported, bad.Outcome);
        Assert.Contains("invalid", bad.Explanation);
        Assert.Equal(AssertionOutcomes.Unsupported, Eval(UiAssert.LayoutAudit("bad", rules: ["ux.balance"]), c).Outcome);
    }

    [Fact]
    public void ScopeNotFoundAmbiguousOrHiddenFails()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        TButton? hidden = null;
        var c = Take(app, d => { hidden = new TButton(new TRect(2, 4, 12, 6), "Hidden", Views.cmNo, ButtonConstants.bfNormal); d.Insert(hidden); ViewTreeInspector.Identify(hidden, "hidden-one"); hidden.Hide(); });
        var missing = Eval(UiAssert.LayoutAudit("layout", ViewSelector.ById("nope")), c);
        Assert.Equal((AssertionOutcomes.Fail, "no match"), (missing.Outcome, missing.Actual));
        Assert.Null(missing.Layout);
        Assert.Contains("No view matched the audit scope", missing.Explanation);
        var ambiguous = Eval(UiAssert.LayoutAudit("layout", ViewSelector.OfType("TButton")), c);
        Assert.Equal(AssertionOutcomes.Fail, ambiguous.Outcome);
        Assert.Contains("ambiguous", ambiguous.Explanation);
        var invisible = Eval(UiAssert.LayoutAudit("layout", ViewSelector.ById("hidden-one")), c);
        Assert.Equal((AssertionOutcomes.Fail, "hidden"), (invisible.Outcome, invisible.Actual));
        Assert.NotNull(invisible.Layout);
    }

    [Fact]
    public void TruncatedCaptureIsUnsupportedWithoutErrorFindings()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var c = Take(app, limits: new InspectionLimits(MaxNodes: 8));
        Assert.True(c.Inspection.ViewTree!.Truncated);
        var r = Eval(UiAssert.LayoutAudit("layout", rules: [LayoutRules.Degenerate]), c);
        Assert.True(r.Layout!.Coverage.Truncated);
        Assert.True(r.Layout.Coverage.OmittedNodes > 0);
        if (r.Layout.Errors == 0)
        {
            Assert.Equal(AssertionOutcomes.Unsupported, r.Outcome);
            Assert.Contains("truncated", r.Explanation);
        }
        else Assert.Equal(AssertionOutcomes.Fail, r.Outcome);
    }

    [Fact]
    public void MissingSourceMarkupIsAGapNeverASilentPass()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var c = Take(app);
        // An older Testing payload: no label/static markup property.
        ViewNode Strip(ViewNode n) => n with
        {
            Properties = n.Kind is "TLabel" or "TStaticText" && n.Properties is { } p ? p.Where(kv => kv.Key is not ("markup" or "markupTruncated")).ToDictionary(kv => kv.Key, kv => kv.Value) : n.Properties,
            Children = n.Children.Select(Strip).ToArray()
        };
        var tree = c.Inspection.ViewTree!;
        var old = c with { Inspection = c.Inspection with { ViewTree = tree with { Root = Strip(tree.Root) } } };
        var r = Eval(UiAssert.LayoutAudit("layout", ViewSelector.ById("inspect"), rules: [LayoutRules.LabelClipped]), old);
        Assert.False(r.Layout!.Coverage.Complete);                          // never a complete (silent) pass
        if (r.Outcome == AssertionOutcomes.Pass) Assert.Contains("Partial coverage", r.Explanation);
    }

    [Fact]
    public void SuppressionRemovesAFindingAndReportsItWithTheReason()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var c = Take(app, overlap: true);                       // OK (14..24) and Cancel (18..30) overlap on rows 8-9
        var plain = Eval(UiAssert.LayoutAudit("layout", ViewSelector.ById("inspect"), rules: [LayoutRules.Overlap]), c);
        var overlap = plain.Layout!.Findings.Where(f => f.Rule == LayoutRules.Overlap).ToArray();
        Assert.NotEmpty(overlap);
        Assert.Equal(AssertionOutcomes.Fail, plain.Outcome);
        Assert.Contains("geom.overlap at ", plain.Explanation);

        var bySelector = Eval(UiAssert.LayoutAudit("layout", ViewSelector.ById("inspect"), rules: [LayoutRules.Overlap],
            suppress: [new(LayoutRules.Overlap, null, "The overlap is the point of this test.")]), c);
        Assert.DoesNotContain(bySelector.Layout!.Findings, f => f.Rule == LayoutRules.Overlap);
        Assert.Equal(overlap.Length, bySelector.Layout.Coverage.Suppressed.Length);
        Assert.All(bySelector.Layout.Coverage.Suppressed, s => Assert.Equal("The overlap is the point of this test.", s.Reason));
        Assert.Equal(AssertionOutcomes.Pass, bySelector.Outcome);
        Assert.Contains("suppressed with a reason", bySelector.Explanation);
        // A suppression whose selector matches no finding's view keeps the findings.
        var other = Eval(UiAssert.LayoutAudit("layout", ViewSelector.ById("inspect"), rules: [LayoutRules.Overlap],
            suppress: [new(LayoutRules.Overlap, ViewSelector.ById("nothing-here"), "Not this one.")]), c);
        Assert.Equal(overlap.Length, other.Layout!.Findings.Count(f => f.Rule == LayoutRules.Overlap));
    }

    // ── editor ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void EditorPanelRoundTripsAndValidatesWithTheRuntimeParser()
    {
        using var scope = new DriverScope(120, 36);
        using var app = new TApplication();
        var original = Obj(Full);
        var panel = Assert.IsType<LayoutAuditAssertionPanel>(AssertionPanel.Create(original));
        // Untouched: the same JSON (field order and spelling kept).
        var saved = (JsonObject)original.DeepClone();
        Assert.Null(panel.Save(saved));
        Assert.Equal(original.ToJsonString(), saved.ToJsonString());
        Assert.Equal(1u, panel.Profile.value);                                   // ux
        Assert.Equal("geom.overlap, text.caption-clipped, ux.button-row", panel.Fields["rules"].Data);
        Assert.Single(panel.Suppressions);

        // Minimal document: severity shows warning (the kind's default) and nothing is added on save.
        var minimal = Obj("""{"id":"layout","kind":"layout-audit"}""");
        var mp = (LayoutAuditAssertionPanel)AssertionPanel.Create(minimal);
        var m = (JsonObject)minimal.DeepClone();
        Assert.Null(mp.Save(m));
        Assert.Equal("""{"id":"layout","kind":"layout-audit"}""", m.ToJsonString());

        // Changes: profile strict, a rule filter, a suppression added through the seam, the scope removed.
        var previous = LayoutAuditAssertionPanel.SuppressionEditor;
        try
        {
            string? seenProfile = null;
            LayoutAuditAssertionPanel.SuppressionEditor = (existing, profile) => { seenProfile = profile; return Obj("""{"rule":"ux.balance","reason":"Wizard page."}"""); };
            mp.Profile.value = 2;
            mp.Fields["rules"].SetData("geom.overlap ux.balance");
            mp.Suppression(add: true);
            Assert.Equal("strict", seenProfile);
            mp.Selectors["view"][1].SetData("main-dialog");
            var changed = (JsonObject)minimal.DeepClone();
            Assert.Null(mp.Save(changed));
            Assert.Equal("""{"id":"layout","kind":"layout-audit","view":{"id":"main-dialog"},"profile":"strict","rules":["geom.overlap","ux.balance"],"suppress":[{"rule":"ux.balance","reason":"Wizard page."}]}""",
                changed.ToJsonString());
            Assert.Null(AssertionJson.Parse([changed], 80, 25).Error);

            // Back to geometry: the ux rule and suppression are refused by the runtime parser (not silently dropped).
            mp.Profile.value = 0;
            string? error = mp.Save((JsonObject)minimal.DeepClone());
            Assert.NotNull(error);
            Assert.Contains("not part of profile geometry", error);
            mp.Fields["rules"].SetData("geom.nothing");
            Assert.Contains("unknown layout rule", mp.Save((JsonObject)minimal.DeepClone()));
            mp.Fields["rules"].SetData("");
            mp.RemoveSuppression();
            var back = (JsonObject)minimal.DeepClone();
            Assert.Null(mp.Save(back));
            Assert.Equal("""{"id":"layout","kind":"layout-audit","view":{"id":"main-dialog"}}""", back.ToJsonString());
        }
        finally { LayoutAuditAssertionPanel.SuppressionEditor = previous; }

        // Severity error is written explicitly (the default is warning).
        var sp = AssertionPanel.Create(minimal);
        sp.SeverityField.value = 0;
        var withError = (JsonObject)minimal.DeepClone();
        Assert.Null(sp.Save(withError));
        Assert.Equal("error", (string)withError["severity"]!);
        // Other kinds are unchanged: an omitted severity stays omitted with the error default.
        var exists = Obj("""{"id":"v","kind":"view-exists","view":{"type":"TDialog"}}""");
        var ep = AssertionPanel.Create(exists);
        var es = (JsonObject)exists.DeepClone();
        Assert.Null(ep.Save(es));
        Assert.Equal(exists.ToJsonString(), es.ToJsonString());
    }

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(80, 25)]
    public void EditorDialogFitsTheScreen(int width, int height)
    {
        using var scope = new DriverScope((ushort)width, (ushort)height);
        using var app = new TApplication();
        var a = Obj(Full);
        var panel = AssertionPanel.Create(a);
        var dialog = (TDialog)Activator.CreateInstance(typeof(WorkbenchAssertionDialog), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            [app.DeskTop!, a, panel, false], null)!;
        var desktop = app.DeskTop!;
        dialog.MoveTo(Math.Max(0, (desktop.size.x - dialog.size.x) / 2), Math.Max(0, (desktop.size.y - dialog.size.y) / 2));
        desktop.Insert(dialog);
        app.Redraw();
        // Every control of the panel lies inside the panel and the dialog interior; the dialog fits the desktop.
        Assert.True(dialog.size.x <= desktop.size.x && dialog.size.y <= desktop.size.y);
        Assert.True(panel.size.y + 2 + 7 <= desktop.size.y, $"panel {panel.size.y} rows do not fit {desktop.size.y}");
        foreach (var v in Children(panel)) Assert.True(v.origin.x >= 0 && v.origin.y >= 0 && v.origin.x + v.size.x <= panel.size.x && v.origin.y + v.size.y <= panel.size.y, $"{v.GetType().Name} at {v.origin.x},{v.origin.y}");
        var screen = ScreenSnapshot.CopyRoot();
        string[] rows = Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character).ToArray())).ToArray();
        Assert.Contains(rows, r => r.Contains("Profile", StringComparison.Ordinal) && r.Contains("geometry", StringComparison.Ordinal) && r.Contains("strict", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("ux.button-row #help-button", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("Add", StringComparison.Ordinal) && r.Contains("Edit", StringComparison.Ordinal) && r.Contains("Remove", StringComparison.Ordinal));
        Save(screen, "editor-layout-audit");
    }

    // ── Results, run report ─────────────────────────────────────────────────────────────────────────────────────────

    private static LayoutAuditReport SampleReport() => new(
        [
            new("geom.overlap", "error", "high", "0/1/0/3", "TButton #cancel \"Cancel\" (0/1/0/3)", ViewSelector.ById("cancel"), new(28, 11, 6, 2), "0/1/0/2", new(24, 11, 10, 2),
                "Cancel overlaps OK by 6x2 cells; move it right of OK.", "OK (24,11 10x2), Cancel (28,11 12x2), overlap 6x2"),
            new("ux.button-row", "warning", "medium", "0/1/0/2", "TButton #ok \"OK\" (0/1/0/2)", ViewSelector.ById("ok"), new(24, 11, 16, 2), null, null,
                "Buttons are not evenly spaced.", "gaps -6"),
        ],
        new("ux", "0/1/0", ["geom.overlap", "ux.button-row"], ["text.cluster-clipped"], [new("text.label-clipped", "The target's Testing payload does not expose label markup.", "0/1/0/1")],
            [new("ux.balance", "0/1/0", "Wizard page.")], 9, 8, false, 0, []));

    private static AssertionResult SampleResult() => new("layout", AssertionKinds.LayoutAudit, AssertionOutcomes.Fail, AssertionSeverity.Warning,
        "no error-class layout findings (profile ux)", "1 error, 1 warning finding(s)",
        "1 failing finding(s): geom.overlap at (28,11 6x2) on TButton #cancel \"Cancel\" (0/1/0/3): Cancel overlaps OK by 6x2 cells; move it right of OK.",
        "layout/audit", "dialog-open", "120x36", "0/1/0/3") { Layout = SampleReport() };

    private static ScenarioResult SampleExecution() => new("layout/audit", "120x36", ScenarioOutcomes.Passed, "",
        [new CheckpointResult("dialog-open", "120x36", "a.png", "a.tvs1", "a.json", new string('0', 64), new string('0', 64), "", [SampleResult()], [])], true, true, true);

    [Fact]
    public void ResultsRowsShowProfileFindingsAndCoverage()
    {
        var rows = ResultText.Layout(SampleResult(), SampleReport()).Select(r => r.Text).ToArray();
        Assert.Equal("         layout    profile ux, scope 0/1/0: 8 of 9 view(s) audited, PARTIAL coverage", rows[0]);
        Assert.Equal("         findings  1 error, 1 warning, 0 info; 1 suppressed (only error-class findings fail)", rows[1]);
        Assert.Contains("           ERROR geom.overlap at (28,11 6x2) on TButton #cancel \"Cancel\" (0/1/0/3) vs 0/1/0/2 (24,11 10x2)", rows);
        Assert.Contains("                   Cancel overlaps OK by 6x2 cells; move it right of OK.", rows);
        Assert.Contains("                   evidence: OK (24,11 10x2), Cancel (28,11 12x2), overlap 6x2", rows);
        Assert.Contains(rows, r => r.Contains("warn  ux.button-row at (24,11 16x2)", StringComparison.Ordinal) && r.Contains("(confidence medium)", StringComparison.Ordinal));
        Assert.Contains("         coverage  evaluated 2: geom.overlap, ux.button-row", rows);
        Assert.Contains("                   not applicable 1: text.cluster-clipped", rows);
        Assert.Contains("                     text.label-clipped on 0/1/0/1: The target's Testing payload does not expose label markup.", rows);
        Assert.Contains("                     ux.balance on 0/1/0: Wizard page.", rows);
        // In the checkpoint view, under the assertion (message also shown for PASS layout results).
        var checkpoint = ResultText.Checkpoint(new BatchResult("", [SampleExecution()]), SampleExecution(), SampleExecution().Checkpoints[0]).Select(r => r.Text).ToList();
        int head = checkpoint.FindIndex(r => r.Contains("layout  layout-audit", StringComparison.Ordinal));
        Assert.True(head > 0);
        Assert.StartsWith("         layout    profile ux", checkpoint[head + 4]);
        var pass = SampleResult() with { Outcome = AssertionOutcomes.Pass, Explanation = "Audited 8 of 9 view(s). Partial coverage: 1 rule gap(s)." };
        var passRows = ResultText.Checkpoint(new BatchResult("", []), SampleExecution(), SampleExecution().Checkpoints[0] with { Assertions = [pass] }).Select(r => r.Text).ToArray();
        Assert.Contains("         message   Audited 8 of 9 view(s). Partial coverage: 1 rule gap(s).", passRows);
        // Bounded: 25 findings listed, the rest counted.
        var many = SampleReport() with { Findings = Enumerable.Range(0, 40).Select(i => SampleReport().Findings[0] with { Path = "0/1/0/" + i }).ToArray() };
        var manyRows = ResultText.Layout(SampleResult(), many).Select(r => r.Text).ToArray();
        Assert.Equal(ResultText.LayoutFindingRows, manyRows.Count(r => r.Contains("ERROR geom.overlap", StringComparison.Ordinal)));
        Assert.Contains("           ... 15 more finding(s): see the run report (Markdown) and run.json", manyRows);
    }

    [Fact]
    public void RunReportHasALayoutFindingsSection()
    {
        var md = new StringBuilder();
        RunReport.LayoutFindings(md, [SampleExecution()]);
        string text = md.ToString();
        Assert.Contains("## Layout findings", text);
        Assert.Contains("### `layout/audit` / `dialog-open` / `layout` (120x36) — FAIL (warning)", text);
        Assert.Contains("- Profile `ux`, scope `0/1/0`: 8 of 9 view(s) audited; 1 error, 1 warning, 0 info finding(s); 1 suppressed.", text);
        Assert.Contains("- Coverage gaps (1): `text.label-clipped` on 0/1/0/1: The target's Testing payload does not expose label markup..", text);
        Assert.Contains("| error | `geom.overlap` | TButton #cancel \"Cancel\" (0/1/0/3) vs 0/1/0/2 | (28,11 6x2) | Cancel overlaps OK by 6x2 cells; move it right of OK. | OK (24,11 10x2), Cancel (28,11 12x2), overlap 6x2 |", text);
        var empty = new StringBuilder();
        RunReport.LayoutFindings(empty, [SampleExecution() with { Checkpoints = [] }]);
        Assert.Equal("", empty.ToString());
        // The report survives the run.json round trip (results are read back from disk for historical runs).
        var json = JsonSerializer.Serialize(SampleResult(), ScenarioRunner.Json);
        Assert.Contains("\"layout\"", json, StringComparison.OrdinalIgnoreCase);
        var back = JsonSerializer.Deserialize<AssertionResult>(json, ScenarioRunner.Json)!;
        Assert.Equal(JsonSerializer.Serialize(SampleResult().Layout, ScenarioRunner.Json), JsonSerializer.Serialize(back.Layout, ScenarioRunner.Json));
        Assert.Null(JsonSerializer.Deserialize<AssertionResult>(JsonSerializer.Serialize(SampleResult() with { Layout = null }, ScenarioRunner.Json), ScenarioRunner.Json)!.Layout);
    }

    private sealed class Host(BatchResult batch) : IResultsHost
    {
        public ResultsView View() => new(batch, ["Run layout", "", "", ""], false, "", _ => null, null);
        public WorkbenchNotifications Notifications { get; } = new();
        public ScenarioDescriptor? Descriptor(string id) => null;
        public void Navigate(int direction) { }
        public void OpenRun() { }
        public void Rerun(BatchResult batch, ScenarioResult result) { }
        public void Compare(BatchResult batch, ScenarioResult result, CheckpointResult? checkpoint) { }
        public void ReviewCandidate(BatchResult batch, ScenarioResult result, CheckpointResult? checkpoint) { }
        public void OpenPath(string path, bool folder) { }
    }

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public void ResultsDialogShowsTheLayoutDrilldown(int width, int height)
    {
        using var scope = new DriverScope((ushort)width, (ushort)height);
        using var app = new TApplication();
        var dialog = new WorkbenchResultsDialog(app.DeskTop!, new Host(new BatchResult("", [SampleExecution()])));
        app.DeskTop!.Insert(dialog);
        dialog.CheckpointList.FocusItemNum(1);                 // the checkpoint (row 0 is Overview)
        dialog.Reload();
        app.Redraw();
        var rows = dialog.InspectorRows;
        Assert.Contains(rows, r => r.Contains("layout    profile ux, scope 0/1/0", StringComparison.Ordinal));
        int finding = Array.FindIndex(rows, r => r.Contains("ERROR geom.overlap", StringComparison.Ordinal));
        Assert.True(finding >= 0);
        Assert.All(rows, r => Assert.True(r.Length <= dialog.Inspector.size.x, $"row wider than the inspector ({dialog.Inspector.size.x}): [{r}]"));
        Save(ScreenSnapshot.CopyRoot(), "results-layout-top");
        dialog.Inspector.FocusItemNum(finding); app.Redraw();
        var screen = ScreenSnapshot.CopyRoot();
        string text = string.Join("\n", Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character).ToArray())));
        Assert.Contains("ERROR geom.overlap", text);
        Save(screen, "results-layout-findings");
        dialog.Inspector.FocusItemNum(Array.FindIndex(rows, r => r.Contains("coverage  evaluated", StringComparison.Ordinal)) + 6); app.Redraw();
        Save(ScreenSnapshot.CopyRoot(), "results-layout-coverage");
    }

    // ── Recorder review: inactive children, layout suggestion detail ───────────────────────────────────────────────────

    private static RecordedInput KeyAt(long sequence, ushort code) => new(sequence, "backend-key", sequence * 100, "key", Events.evKeyDown, code, null, 0, 0, 0, 0, 0, 80, 25);

    private static (RecordingReviewModel Model, ReviewCheckpoint Checkpoint) ReviewModel()
    {
        var conversion = RecordingConverter.Convert([KeyAt(1, Keys.kbF3), KeyAt(2, Keys.kbEnter)], 80, 25);
        var p = new CheckpointProposal("p-2", "rec-dialog-open", 0, Placements.AfterStep, ScenarioDocumentStep.Checkpoint("rec-dialog-open"), CandidateKinds.ModalOpen,
            "A dialog opened after the key press.", Confidences.High, EvidenceLevels.Snapshot, 1, 2, 5, ["c-2"], [], false, []);
        var evidence = new CheckpointEvidence(p.Id, p.BoundaryId, 0x42, p.AfterSequence, EvidenceSources.Snapshot, 5, null, null, false, []);
        var modal = new AssertionSuggestion(p.Id, new AssertionSpec("dialog-modal", "modal", AssertionSeverity.Warning, View: new ViewSelector(Type: "TDialog", Text: "Find")),
            SuggestionOrigins.State, null, "modal-open", "The dialog executes modally.", Confidences.High, SuggestionStatuses.Valid, true, "PASS", null, []);
        var layout = new AssertionSuggestion(p.Id, UiAssert.LayoutAudit("dialog-layout", new ViewSelector(Type: "TDialog", Text: "Find"), LayoutProfiles.Ux),
            SuggestionOrigins.State, null, "layout-audit", "The layout of the modal dialog in this state.", Confidences.Medium, SuggestionStatuses.Valid, true, "PASS", null, [])
            { Layout = SampleReport() };
        var analysis = new SmartRecordingAnalysis(new DetectionResult([], [], new DetectorCounters(0, 0, 0, 0, 0, 0, 0, 0, 0)), [],
            new CheckpointPlan(conversion.Steps, false, [p], []), []);
        var model = new RecordingReviewModel(analysis, [new(p, evidence, [modal, layout], false, null, 80, 25)], conversion, ["Smart capture on (test fixture)."]);
        return (model, model.Checkpoints[0]);
    }

    private static ReviewRow Row(ReviewCheckpoint c, int assertion) => new(ReviewRowKind.Assertion, c, c.Assertions[assertion], null);

    [Fact]
    public void ChildrenOfANotAcceptedCheckpointAreInactiveButKeepTheirSelection()
    {
        var (model, c) = ReviewModel();
        model.SetAccepted(c, false);
        c.Assertions[0].Selected = true; c.Assertions[1].Selected = false;
        string selectedRow = ReviewRowText.Format(Row(c, 0), model, 200), plainRow = ReviewRowText.Format(Row(c, 1), model, 200);
        Assert.StartsWith("    [-] modal", selectedRow);
        Assert.Contains("inactive(sel)", selectedRow);
        Assert.StartsWith("    [-] layout-audit", plainRow);
        Assert.Contains("inactive ", plainRow);
        Assert.DoesNotContain("[x]", selectedRow);
        Assert.True(c.Assertions[0].Selected);                                  // the model keeps the selection
        Assert.Contains("INACTIVE: checkpoint rec-dialog-open is not accepted, so nothing of it is inserted. Your choice (selected) is kept",
            ReviewRowText.Detail(Row(c, 0), model));
        Assert.Contains("1/2 sel if accepted", ReviewRowText.Format(new(ReviewRowKind.Checkpoint, c, null, null), model, 200));

        Assert.Null(model.SetAccepted(c, true));
        Assert.StartsWith("    [x] modal", ReviewRowText.Format(Row(c, 0), model, 200));
        Assert.StartsWith("    [ ] layout-audit", ReviewRowText.Format(Row(c, 1), model, 200));
        Assert.DoesNotContain("inactive", ReviewRowText.Format(Row(c, 0), model, 200));
        Assert.Contains("SELECTED.", ReviewRowText.Detail(Row(c, 0), model));
        Assert.Contains("1/2 sel", ReviewRowText.Format(new(ReviewRowKind.Checkpoint, c, null, null), model, 200));
    }

    [Fact]
    public void LayoutSuggestionDetailShowsProfileCountsFindingsAndCoverage()
    {
        var (model, c) = ReviewModel();
        string row = ReviewRowText.Format(Row(c, 1), model, 200);
        Assert.Contains("TDialog \"Find\" profile ux", row);
        string detail = ReviewRowText.Detail(Row(c, 1), model);
        Assert.Contains("Layout audit of the recorded state: profile ux, scope 0/1/0 (8 of 9 view(s) audited); 1 error, 1 warning, 0 info finding(s), 1 suppressed.", detail);
        Assert.Contains("  [error] geom.overlap at (28,11 6x2) on TButton #cancel \"Cancel\" (0/1/0/3): Cancel overlaps OK by 6x2 cells; move it right of OK.", detail);
        Assert.Contains("Coverage: PARTIAL (2 evaluated, 1 not applicable, 1 gap(s)):", detail);
        Assert.Contains("  text.label-clipped on 0/1/0/1: The target's Testing payload does not expose label markup.", detail);
        Assert.Contains("Enter (Edit) changes the scope, profile", detail);
        Assert.Contains("\"kind\":\"layout-audit\"", detail);
    }

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task ReviewDialogEditsTheLayoutSuggestionAndRendersInactiveChildren(int width, int height)
    {
        var (model, c) = ReviewModel();
        string root = Temp();
        var previous = WorkbenchRecordingReview.AssertionEditor;
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                bool? result = null;
                var running = bridge.Invoke(() => result = WorkbenchRecordingReview.Review(app.DeskTop!, model));
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRecordingReviewDialog);
                await Settle(bridge);
                Assert.True(c.Accepted && c.Preselected);                         // high confidence + a preselected assertion
                await bridge.Invoke(() => dialog.List.FocusItemNum(dialog.Rows.ToList().FindIndex(r => r.Kind == ReviewRowKind.Checkpoint)));
                Press(Keys.kbSpace); await Settle(bridge);                        // reject the checkpoint: its children become inactive
                Assert.False(c.Accepted);
                await bridge.Invoke(() => dialog.List.FocusItemNum(dialog.Rows.ToList().FindIndex(r => r.Assertion == c.Assertions[1])));
                await Settle(bridge);
                var rows = Rows();
                Assert.Contains(rows, r => r.Contains("[-] modal", StringComparison.Ordinal) && r.Contains("inactive", StringComparison.Ordinal));
                Assert.Contains("Layout audit of the recorded state: profile ux", await Get(bridge, () => dialog.DetailText));
                await Shot(bridge, "review-layout-inactive");

                // Edit opens the assertion editor (here the seam) with the layout JSON; the user changes the profile to strict.
                JsonObject? seen = null;
                WorkbenchRecordingReview.AssertionEditor = (_, json) => { seen = json; var e = (JsonObject)json.DeepClone(); e["profile"] = "strict"; return e; };
                Press(Keys.kbEnter); await Settle(bridge);
                Assert.Equal("ux", (string)seen!["profile"]!);
                Assert.Equal("strict", (string)c.Assertions[1].Json["profile"]!);
                Assert.True(c.Assertions[1].Selected);
                await Shot(bridge, "review-layout-edited");
                Press(Keys.kbEsc);
                await running.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(result);
            });
        }
        finally { WorkbenchRecordingReview.AssertionEditor = previous; Delete(root); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<T> Get<T>(Bridge bridge, Func<T> f) { T value = default!; await bridge.Invoke(() => value = f()); return value; }
    private static async Task Shot(Bridge bridge, string state) { await Settle(bridge); await bridge.Invoke(() => Save(ScreenSnapshot.CopyRoot(), state)); }

    private static void Save(ScreenSnapshot screen, string state)
    {
        if (Evidence is not { } root) return;
        Directory.CreateDirectory(root);
        string name = $"c-{state}-{screen.Width}x{screen.Height}";
        byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
        File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
        File.WriteAllLines(Path.Combine(root, name + ".logical.txt"),
            Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character == 0 ? ' ' : screen[x, y].Character).ToArray()).TrimEnd()));
        File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
        {
            name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(LayoutAssertionTests),
            renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSchemaSample(string name, string json)
    {
        if (Evidence is not { } root) return;
        string folder = Path.Combine(root, "..", "schema");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found.");
    }
}
