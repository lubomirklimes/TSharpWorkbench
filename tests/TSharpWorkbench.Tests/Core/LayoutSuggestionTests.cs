using System.Text.Json.Nodes;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// The layout-audit suggestion of Smart recording (one composite suggestion per applicable state, duplicate
// and menu-state handling, validation through the v1 parser and the real evaluator) and the physical qualification on the Win32
// Console (TSWB_PRODUCT_TARGET = a ProxyOrdinary prepared with this build; TSWB_TVDEMO_TARGET = a prepared TVDemo copy; TSWB_STALE_PROXY_TARGET = a
// target whose Proxy predates Smart capture). Evidence logs go to TSWB_LAYOUT_EVIDENCE when set.
[Collection("NonParallel")]
public sealed class LayoutSuggestionTests
{
    private static string? Evidence => Environment.GetEnvironmentVariable("TSWB_LAYOUT_EVIDENCE") is { Length: > 0 } e ? e : null;

    private static CheckpointProposal Proposal(string id, long boundary, string kind) =>
        new("p-" + boundary, id, 0, Placements.AfterStep, ScenarioDocumentStep.Checkpoint(id), kind, "state", Confidences.High, EvidenceLevels.Snapshot,
            boundary, boundary, boundary, ["c-" + boundary], [], false, []);

    private static CheckpointEvidence Capture(string proposal, long boundary, ulong hash, Action<TApplication> build, bool cellsOnly = false)
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        build(app);
        app.StatusLine!.Update(); app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var inspection = cellsOnly ? null : Inspectors.Capture(app, cells, Protocol.EvidenceKinds.All);
        return new CheckpointEvidence(proposal, boundary, hash, boundary, EvidenceSources.Snapshot, boundary, cells, inspection, false, []);
    }

    private static void GoodDialog(TApplication app)
    {
        var dialog = new TDialog(new TRect(15, 5, 65, 16), "Find");
        var input = new TInputLine(new TRect(14, 2, 46, 3), 80);
        dialog.Insert(input);
        dialog.Insert(new TLabel(new TRect(2, 2, 13, 3), "~T~ext", input));
        dialog.Insert(new TButton(new TRect(14, 8, 26, 10), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
        dialog.Insert(new TButton(new TRect(27, 8, 39, 10), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        input.Select();
        app.DeskTop!.Insert(dialog);
    }

    private static void OverlappingDialog(TApplication app)
    {
        var dialog = new TDialog(new TRect(15, 5, 65, 16), "Broken");
        dialog.Insert(new TButton(new TRect(14, 8, 26, 10), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
        dialog.Insert(new TButton(new TRect(20, 8, 32, 10), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));   // overlaps OK
        app.DeskTop!.Insert(dialog);
    }

    private static IReadOnlyList<AssertionSuggestion> Suggest(CheckpointProposal proposal, CheckpointEvidence evidence, Dictionary<ulong, string>? audited = null)
    {
        var candidate = LayoutAuditProposer.Propose(new ProposerInput(proposal, evidence, null), audited ?? []);
        Assert.NotNull(candidate);
        Assert.Equal(AssertionKinds.LayoutAudit, candidate!.Spec.Kind);
        Assert.Equal(LayoutProfiles.Ux, candidate.Spec.Profile);
        Assert.Null(candidate.Spec.View?.Path);
        return AssertionSuggestionValidator.Validate([candidate], evidence, new ValidationContext(proposal.Id, proposal.CheckpointId, 80, 25, []));
    }

    [Fact]
    public void OneLayoutAuditPerStateValidatedByTheRuntimeParserAndEvaluator()
    {
        var p = Proposal("find-open", 2, CandidateKinds.ModalOpen);
        var evidence = Capture(p.Id, 2, 0x22, GoodDialog);
        var s = Assert.Single(Suggest(p, evidence));
        Assert.Equal(SuggestionStatuses.Valid, s.Status);
        Assert.Equal(AssertionSeverity.Warning, s.Spec.Severity);
        Assert.NotNull(s.Layout);
        Assert.Equal(0, s.Layout!.Errors);
        // The suggestion is exact schema v1 and round-trips through the runtime parser.
        var json = AssertionJson.ToJson(s.Spec);
        Assert.Equal("layout-audit", json["kind"]!.GetValue<string>());
        Assert.Equal("ux", json["profile"]!.GetValue<string>());
        var (specs, error) = AssertionJson.Parse([json], 80, 25);
        Assert.Null(error);
        Assert.Equal(LayoutProfiles.Ux, Assert.Single(specs!).Profile);
        // Preselected only with complete coverage (high confidence); with gaps it is offered but not preselected.
        Assert.Equal(s.Layout.Coverage.Complete ? Confidences.High : Confidences.Medium, s.Confidence);
        Assert.Equal(s.Layout.Coverage.Complete, s.DefaultSelected);
        Assert.Equal(s.DefaultSelected, ReviewAssertion.Safe(s));
        Assert.DoesNotContain("rationale", json.ToJsonString());
    }

    [Fact]
    public void HardFindingsInTheRecordedStateAreShownButNeverPreselected()
    {
        var p = Proposal("broken-open", 3, CandidateKinds.ModalOpen);
        var s = Assert.Single(Suggest(p, Capture(p.Id, 3, 0x33, OverlappingDialog)));
        Assert.Equal(SuggestionStatuses.Valid, s.Status);
        Assert.Equal(AssertionOutcomes.Fail, s.Outcome);
        Assert.Equal(Confidences.Low, s.Confidence);
        Assert.False(s.DefaultSelected);
        Assert.Contains(s.Layout!.Findings, f => f.Rule == LayoutRules.Overlap && f.Class == LayoutFindingClasses.Error);
        Assert.Contains("would FAIL as recorded", s.Rationale);
    }

    [Fact]
    public void LayoutAuditIsNotSuggestedForMenusDuplicatesOrCellsOnlyAndSaysWhy()
    {
        var audited = new Dictionary<ulong, string>();
        var first = Proposal("find-open", 2, CandidateKinds.ModalOpen);
        var evidence = Capture(first.Id, 2, 0x44, GoodDialog);
        Assert.Equal(SuggestionStatuses.Valid, Assert.Single(Suggest(first, evidence, audited)).Status);
        // The same screen (same hash) again: one layout audit per state.
        var again = Proposal("find-again", 4, CandidateKinds.Content);
        var duplicate = Assert.Single(Suggest(again, evidence with { ProposalId = again.Id }, audited));
        Assert.Equal(SuggestionStatuses.Unsupported, duplicate.Status);
        Assert.Contains("already audited at checkpoint find-open", duplicate.Reason);
        // A menu state.
        var menu = Proposal("menu-open", 5, CandidateKinds.MenuOpen);
        var m = Assert.Single(Suggest(menu, Capture(menu.Id, 5, 0x55, GoodDialog), audited));
        Assert.Equal(SuggestionStatuses.Unsupported, m.Status);
        Assert.Contains("menu is open", m.Reason);
        // Cells only: the audit needs the view tree.
        var cells = Proposal("cells-only", 6, CandidateKinds.Content);
        var c = Assert.Single(Suggest(cells, Capture(cells.Id, 6, 0x66, GoodDialog, cellsOnly: true), audited));
        Assert.Equal(SuggestionStatuses.Unsupported, c.Status);
        Assert.Contains("needs the view tree", c.Reason);
        Assert.All(new[] { duplicate, m, c }, x => Assert.False(ReviewAssertion.Safe(x)));
    }

    [Fact]
    public void PicksNeverProduceRelationAssertionsAndSuggestionsCanBeTurnedOff()
    {
        foreach (string kind in new[] { AssertionKinds.Aligned, AssertionKinds.Spacing, AssertionKinds.Disjoint, AssertionKinds.WithinView })
            Assert.DoesNotContain(kind, AssertionSuggestionValidator.AllowedKinds);
        Assert.True(AssertionSuggester.LayoutSuggestions);
    }

    // ── physical qualification (Win32 Console, injected input; NOT a physical mouse) ─────────────────────────────────

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } t && File.Exists(t) ? t : null;
    public sealed class EnvTargetFactAttribute : FactAttribute
    {
        public EnvTargetFactAttribute(string variable) { if (Env(variable) is null) Skip = $"Set {variable} to a prepared target executable."; }
    }

    private static readonly List<string> Log = [];
    private static void Write(string name)
    {
        if (Evidence is not { } root) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, name), string.Join("\n", Log));
    }

    private static async Task<RecordingSession> Record(string target, Action<int> drive, bool requireSmart = true)
    {
        var session = new RecordingSession(target, "Win32ConsoleDriver", "80x25") { ModifierPicks = requireSmart, SampleInspections = requireSmart, RequireSmartCapture = requireSmart };
        await session.StartAsync();
        if (session.State != RecordingState.Recording) return session;
        drive(session.ProcessId!.Value);
        await Task.Delay(800);
        await session.StopAsync();
        return session;
    }

    private static void Step(int pid, string plan, int settleMs = 700) { SmartRecordingTests.ConsoleInput(pid, plan); Thread.Sleep(settleMs); }

    // The whole Smart flow on a real target: one layout audit per applicable checkpoint, review, materialize, save, load, replay.
    private static async Task<(RecordingReviewModel Model, ScenarioResult Result)> RecordReviewReplay(string target, Action<int> drive, string name)
    {
        await using var session = await Record(target, drive);
        Assert.True(session.State == RecordingState.Stopped, session.Failure);
        Assert.True(session.Capture.BoundariesEnabled);
        var conversion = RecordingConverter.Convert(session.Events, session.Width, session.Height);
        var analysis = session.Analyze(conversion, ["initial"]);
        var suggestions = AssertionSuggester.Suggest(analysis, AssertionSuggester.TypedTexts(session.Events, conversion), session.Width, session.Height);
        var model = new RecordingReviewModel(analysis, suggestions, conversion, ["physical"]);
        Log.Add($"== {name}: steps " + string.Join(" | ", conversion.Steps.Select(s => s.Step.Kind + " " + s.Step.Parameters)));
        foreach (var c in model.Checkpoints)
        {
            Log.Add($"proposal {c.CheckpointId} {c.Proposal.Kind} {c.Proposal.Confidence} evidence={c.Evidence.Source} hash={c.Evidence.Hash:x} accepted={c.Accepted} preselected={c.Preselected}");
            foreach (var a in c.Assertions)
            {
                Log.Add($"   {a.Suggestion.Status,-11} {(a.Selected ? "[x]" : "[ ]")} {a.Suggestion.Confidence,-6} {a.Suggestion.Rule}: {AssertionJson.ToJson(a.Suggestion.Spec).ToJsonString()} outcome={a.Suggestion.Outcome} {a.Suggestion.Reason}");
                if (a.Suggestion.Layout is { } r)
                {
                    Log.Add($"      layout: {r.Errors} error, {r.Warnings} warning; evaluated {r.Coverage.Evaluated.Length}, n/a {r.Coverage.NotApplicable.Length}, gaps {r.Coverage.Unavailable.Length}, nodes {r.Coverage.AuditedNodes}/{r.Coverage.ScopeNodes}");
                    Log.AddRange(r.Findings.Take(12).Select(f => $"      {f.Class} {f.Rule} {f.Rect} {f.View}: {f.Message} [{f.Evidence}]"));
                    Log.AddRange(r.Coverage.Unavailable.Take(8).Select(g => $"      gap {g.Rule} {g.Path}: {g.Reason}"));
                }
            }
        }
        // Exactly one composite layout-audit suggestion per checkpoint at most; never relation assertions from picks.
        foreach (var c in model.Checkpoints)
            Assert.True(c.Assertions.Count(a => a.Kind == AssertionKinds.LayoutAudit) <= 1, c.CheckpointId);
        Assert.DoesNotContain(model.Checkpoints.SelectMany(c => c.Assertions), a => a.Kind is AssertionKinds.Aligned or AssertionKinds.Spacing or AssertionKinds.Disjoint);

        // Review: accept every representable checkpoint and select every valid layout audit (user decision).
        model.AcceptAll();
        foreach (var c in model.Checkpoints.Where(c => c.Accepted))
            foreach (var a in c.Assertions.Where(a => a.Kind == AssertionKinds.LayoutAudit && a.Selectable)) RecordingReviewModel.SetSelected(a, true);
        var m = RecordingReviewMaterializer.Materialize(model, ["initial"]);
        Assert.True(m.Ok, string.Join("; ", m.Problems));
        string folder = Path.Combine(Path.GetTempPath(), "layout-phys-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var doc = ScenarioDocument.New("recorded/layout-" + name, "Layout " + name, "Smart recording with layout audits, replayed on the real console.", checkpoint: null);
            foreach (var step in m.Steps) doc.Append(step);
            string path = Path.Combine(folder, $"layout-{name}.scenario.json");
            var saved = doc.Save(path);
            Assert.True(saved.Saved, saved.Message);
            Assert.Contains("\"version\": 1", File.ReadAllText(path));
            if (Evidence is { } ev) { Directory.CreateDirectory(Path.Combine(ev, "docs")); File.Copy(path, Path.Combine(ev, "docs", Path.GetFileName(path)), true); }
            var scenario = ExternalScenarios.Merge([], [ExternalScenarios.Load(path)]).Single();
            var batch = await new ScenarioRunner().RunAsync(target, [scenario], new RunOptions(Path.Combine(folder, "runs"), ["80x25"]) { ProxyBackend = "Win32ConsoleDriver" });
            var result = batch.Results.Single();
            Log.Add($"replay {name}: {result.Outcome} {result.Reason}");
            foreach (var a in result.Checkpoints.SelectMany(c => c.Assertions))
            {
                Log.Add($"   {a.Checkpoint} {a.Id} {a.Kind} {a.Severity} {a.Outcome} expected={a.Expected} actual={a.Actual}");
                if (a.Layout is { } r) Log.AddRange(r.Findings.Take(6).Select(f => $"      {f.Class} {f.Rule} {f.Rect} {f.View}: {f.Message}"));
            }
            return (model, result);
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [EnvTargetFact("TSWB_PRODUCT_TARGET")]
    public async Task OrdinaryConsoleRecordingAuditsLayoutAndReplays()
    {
        Log.Clear();
        try
        {
            // F3 opens the modal; the OK button (20,9) closes it; F2 counts.
            var (model, result) = await RecordReviewReplay(Env("TSWB_PRODUCT_TARGET")!, pid =>
            {
                Step(pid, "k,114,61");
                Step(pid, "c,20,9");
                Step(pid, "k,113,60");
            }, "ordinary");
            var audits = model.Checkpoints.SelectMany(c => c.Assertions).Where(a => a.Kind == AssertionKinds.LayoutAudit).ToArray();
            Assert.NotEmpty(audits);
            Assert.Contains(audits, a => a.Suggestion.Status == SuggestionStatuses.Valid);
            Assert.Equal(ScenarioOutcomes.Passed, result.Outcome);
            var replayed = result.Checkpoints.SelectMany(c => c.Assertions).Where(a => a.Kind == AssertionKinds.LayoutAudit).ToArray();
            Assert.NotEmpty(replayed);
            Assert.All(replayed, a => Assert.NotNull(a.Layout));
        }
        finally { Write("layout-console-ordinary.txt"); }
    }

    [EnvTargetFact("TSWB_TVDEMO_TARGET")]
    public async Task TvDemoMenuAndDialogRecordingAuditsLayoutAndReplays()
    {
        Log.Clear();
        try
        {
            // The owner's TVDemo sequence (80x25): Demo menu, Controls submenu, the form dialog, then the Information dialog.
            var (model, result) = await RecordReviewReplay(Env("TSWB_TVDEMO_TARGET")!, pid =>
            {
                Step(pid, "c,11,0", 900);
                Step(pid, "c,14,3", 900);
                Step(pid, "c,18,5", 1200);
                Step(pid, "c,15,8", 900);
                Step(pid, "c,31,18", 1200);
            }, "tvdemo");
            var all = model.Checkpoints.SelectMany(c => c.Assertions).ToArray();
            Assert.Contains(all, a => a.Kind == AssertionKinds.LayoutAudit && a.Suggestion.Status == SuggestionStatuses.Valid);
            Assert.Contains(all, a => a.Kind == AssertionKinds.LayoutAudit && a.Suggestion.Reason?.Contains("menu is open") == true);
            Assert.Equal(ScenarioOutcomes.Passed, result.Outcome);
        }
        finally { Write("layout-console-tvdemo.txt"); }
    }

    [EnvTargetFact("TSWB_STALE_PROXY_TARGET")]
    public async Task StaleProxyStopsASmartRecordingAndInputsOnlyStillWorks()
    {
        Log.Clear();
        try
        {
            string target = Env("TSWB_STALE_PROXY_TARGET")!;
            Assert.Equal(ProxyCapability.Stale, RecordingProxyProbe.Probe(target).Capability);
            // Runtime guard: Smart required, the old Proxy negotiates nothing -> Failed before recording, target cleaned up.
            await using (var smart = await Record(target, _ => { }))
            {
                Log.Add($"smart on stale: {smart.State} smartUnavailable={smart.SmartUnavailable} failure={smart.Failure} events={smart.Events.Count} cleanup={smart.Cleanup}");
                Assert.Equal(RecordingState.Failed, smart.State);
                Assert.True(smart.SmartUnavailable);
                Assert.Empty(smart.Events);
            }
            // A deliberate inputs-only recording on the same old target keeps working (legacy).
            await using var legacy = await Record(target, pid => Step(pid, "k,113,60"), requireSmart: false);
            Log.Add($"inputs only on stale: {legacy.State} events={legacy.Events.Count} boundaries={legacy.Capture.BoundariesEnabled}");
            Assert.Equal(RecordingState.Stopped, legacy.State);
            Assert.False(legacy.Capture.BoundariesEnabled);
            Assert.Contains(legacy.Events, e => e.KeyCode == Keys.kbF2);
        }
        finally { Write("layout-console-stale.txt"); }
    }
}
