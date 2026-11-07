using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// External compatibility: explicit baseline identity mapping (manifest executionScenario) and fixture-aware native
// eligibility. Core tests use synthetic screens and descriptors; the Commander tests read the Commander repository
// read-only (TSWB_RESULTS_COMMANDER_ROOT, default C:\TurboVision\GIT\TSharpCommander) and work on scratch copies only; the
// Commander runtime test additionally needs TSWB_RESULTS_COMMANDER_TARGET (diagnostic Commander executable) and
// TSWB_COMMANDER_PROJECT (CommanderExternal.twproj). Optional evidence: TSWB_RESULTS_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class ExternalCompatTests
{
    private const string Fixture = "demo/fixture/panels", Execution = "external/demo/panels", Checkpoint = "desktop";
    private static readonly TargetIdentity Identity = new("Demo", "1.0", null, null, null, "NullDriver", 80, 25);

    // ── manifest validation ───────────────────────────────────────────────────────

    [Fact]
    public void ManifestMappingIsBackwardCompatibleAndRejectsConflictsAmbiguityAndRedundancy()
    {
        string root = Scratch();
        try
        {
            // No mapping: identical behaviour (the entry executes as its own scenario).
            var plain = VisualBaselineSet.Load(Manifest(root, Entry(Fixture)));
            Assert.False(plain.HasIdentityMappings);
            Assert.Equal(Fixture, plain.Manifest.Baselines[0].Execution);
            Assert.NotNull(plain.FindForExecution(Fixture, Checkpoint, "80x25"));
            Assert.Null(plain.FindForExecution(Execution, Checkpoint, "80x25"));   // never borrowed by name

            var mapped = VisualBaselineSet.Load(Manifest(root, Entry(Fixture, Execution), Entry(Fixture, Execution, "menu")));
            Assert.True(mapped.HasIdentityMappings);
            Assert.Same(mapped.Find(Fixture, Checkpoint, "80x25"), mapped.FindForExecution(Execution, Checkpoint, "80x25"));
            Assert.Null(mapped.FindForExecution(Fixture, Checkpoint, "80x25"));     // the baseline identity itself no longer executes

            static string Refused(string path) => Assert.Throws<VisualBaselineConfigurationException>(() => VisualBaselineSet.Load(path)).Message;
            Assert.Contains("conflicting executionScenario", Refused(Manifest(root, Entry(Fixture, Execution), Entry(Fixture, null, "menu"))));
            Assert.Contains("conflicting executionScenario", Refused(Manifest(root, Entry(Fixture, Execution), Entry(Fixture, "external/other", "menu"))));
            Assert.Contains("ambiguous mapping", Refused(Manifest(root, Entry(Fixture, Execution), Entry("demo/fixture/other", Execution, "menu"))));
            Assert.Contains("ambiguous mapping", Refused(Manifest(root, Entry(Execution), Entry(Fixture, Execution, "menu"))));
            Assert.Contains("omit it", Refused(Manifest(root, Entry(Fixture, Fixture))));
            Assert.Contains("must be a scenario ID", Refused(Manifest(root, Entry(Fixture, " external/x"))));
            Assert.Contains("must be a scenario ID", Refused(Manifest(root, Entry(Fixture, ""))));
            Assert.Contains("executionScenarios", Refused(Manifest(root, Entry(Fixture).Replace("\"scenario\"", "\"executionScenarios\": \"x\", \"scenario\""))));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void BindingValidationNeedsTheValidatedFixtureBindingTheMappingNames()
    {
        string root = Scratch();
        try
        {
            var set = VisualBaselineSet.Load(Manifest(root, Entry(Fixture, Execution)));
            Assert.Null(set.BindingProblem(Execution, [Bound(Execution, Fixture)]));
            Assert.Null(set.BindingProblem("unrelated/scenario", [Bound(Execution, Fixture)]));                   // unmapped: nothing to check
            Assert.Contains("not in the current catalog", set.BindingProblem(Execution, [])!);
            Assert.Contains("bound to fixture 'demo/fixture/other'", set.BindingProblem(Execution, [Bound(Execution, "demo/fixture/other")])!);
            Assert.Contains("no validated fixture binding", set.BindingProblem(Execution, [Compiled(Execution)])!);
            Assert.Contains("no validated fixture binding", set.BindingProblem(Execution, [Bound(Execution, Fixture) with { External = new(Fixture, "a.json", "h", "") }])!);
            Assert.Contains("no validated fixture binding", set.BindingProblem(Execution, [Proxy(Execution)])!);
            // An unmapped entry of a fixture identity is refused for the bound execution, never borrowed.
            var plain = VisualBaselineSet.Load(Manifest(root, Entry(Fixture)));
            Assert.Contains("never borrowed implicitly", plain.UnmappedFixtureIdentity(plain.Manifest.Baselines[0], Bound(Execution, Fixture))!);
            Assert.Null(plain.UnmappedFixtureIdentity(plain.Manifest.Baselines[0], Bound(Execution, "demo/fixture/other")));
            Assert.Null(plain.UnmappedFixtureIdentity(plain.Manifest.Baselines[0], Compiled(Fixture)));
        }
        finally { Delete(root); }
    }

    // ── gate, candidate and approval with a mapped identity ─────────────────────────

    [Fact]
    public async Task MappedEntryUsesTheBaselineIdentityForKeysCandidatesApprovalAndComparisonButKeepsTheExecutionId()
    {
        string root = Scratch();
        try
        {
            var set = VisualBaselineSet.Load(Manifest(Path.Combine(root, "store"), Entry(Fixture, Execution)));
            var scenario = Bound(Execution, Fixture);
            string key = VisualBaselineSet.Key(Fixture, Checkpoint, "80x25"), stem = ArtifactNames.Stem(Execution, Checkpoint, "80x25");
            Assert.NotEqual(key, stem);
            string run1 = Path.Combine(root, "run1");
            var missing = await Evaluate(set, run1, scenario, Screen('A'));
            Assert.Equal((VisualStatuses.BaselineMissing, key), (missing.Status, missing.Baseline));
            Assert.Equal(key + ".candidate.tvs1", missing.Candidate);
            Assert.True(File.Exists(Path.Combine(run1, stem + ".diff.json")));                      // capture artifacts keep the execution stem
            var candidate = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllBytes(Path.Combine(run1, missing.CandidateMetadata!)), VisualBaselineSet.Strict)!;
            Assert.Equal((Fixture, Execution, key), (candidate.ScenarioId, candidate.ExecutionScenarioId, candidate.Key));
            using (var diff = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run1, stem + ".diff.json"))))
            {
                Assert.Equal(Execution, diff.RootElement.GetProperty("scenarioId").GetString());
                Assert.Equal(Fixture, diff.RootElement.GetProperty("baselineScenarioId").GetString());
            }
            Assert.False(Directory.Exists(set.ApprovedDirectory));

            Assert.Equal([key], set.Approve(run1, [key], "approve-1", false));
            var approved = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllBytes(set.ApprovedMetadata(key)), VisualBaselineSet.Strict)!;
            Assert.Equal(("approved", Fixture, Execution), (approved.Status, approved.ScenarioId, approved.ExecutionScenarioId));
            Assert.Equal(("Approved", key), (set.Status().Single().Status, set.Status().Single().Key));

            var match = await Evaluate(set, Path.Combine(root, "run2"), scenario, Screen('A'));
            Assert.Equal(VisualStatuses.Match, match.Status);
            var mismatch = await Evaluate(set, Path.Combine(root, "run3"), scenario, Screen('B'));
            Assert.Equal(VisualStatuses.Mismatch, mismatch.Status);

            // The run summary keeps the execution ID and names the baseline identity separately.
            var gate = new VisualGate(set, Path.Combine(root, "run2"));
            await gate.EvaluateAsync(scenario, Checkpoint, "80x25", "80x25", Screen('A'), stem + "-again", new((string?)null), Identity);
            var summary = gate.Finish([new ScenarioResult(Execution, "80x25", ScenarioOutcomes.Passed, "", [], true, true, true)], [scenario]);
            var entry = Assert.Single(summary.Entries);
            Assert.Equal((Execution, Fixture, key, VisualStatuses.Match), (entry.ScenarioId, entry.BaselineScenarioId, entry.BaselineKey(), entry.Status));
            Assert.True(summary.Passed);
            string json = JsonSerializer.Serialize(summary, ScenarioRunner.Json);
            Assert.Contains("\"baselineScenarioId\"", json);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task WrongBindingUnmappedBorrowingAndForeignApprovalsAreRefused()
    {
        string root = Scratch();
        try
        {
            string key = VisualBaselineSet.Key(Fixture, Checkpoint, "80x25");
            // Approve a legacy (unmapped) baseline under the fixture identity, as Commander's approved store was made.
            var legacy = VisualBaselineSet.Load(Manifest(Path.Combine(root, "store"), Entry(Fixture)));
            string legacyRun = Path.Combine(root, "legacy");
            await Evaluate(legacy, legacyRun, Compiled(Fixture), Screen('A'));
            legacy.Approve(legacyRun, [key], "approve-1", false);
            var legacyFile = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllBytes(legacy.ApprovedMetadata(key)), VisualBaselineSet.Strict)!;
            Assert.Null(legacyFile.ExecutionScenarioId);
            Assert.DoesNotContain("executionScenarioId", File.ReadAllText(legacy.ApprovedMetadata(key)));   // unmapped format unchanged
            var snapshot = Snapshot(legacy.ApprovedDirectory);

            // Without a mapping, an external scenario bound to that fixture is not gated by it and is explicitly refused.
            var unmappedGate = new VisualGate(legacy, Directory.CreateDirectory(Path.Combine(root, "borrow")).FullName);
            Assert.Null(await unmappedGate.EvaluateAsync(Bound(Execution, Fixture), Checkpoint, "80x25", "80x25", Screen('A'), "x", new((string?)null), Identity));
            var refusal = Assert.Single(unmappedGate.Finish([new ScenarioResult(Execution, "80x25", ScenarioOutcomes.Passed, "", [], true, true, true)], [Bound(Execution, Fixture)]).Entries);
            Assert.Equal((VisualStatuses.BaselineInvalid, Execution, Fixture), (refusal.Status, refusal.ScenarioId, refusal.BaselineScenarioId));
            Assert.Contains("never borrowed implicitly", refusal.Reason);

            // The same store with an explicit mapping: the legacy approval is accepted as is (no migration, no rewrite).
            string mappedManifest = Path.Combine(root, "store", "visual-baselines.json");
            File.WriteAllText(mappedManifest, ManifestText(Entry(Fixture, Execution)));
            var mapped = VisualBaselineSet.Load(mappedManifest);
            Assert.Equal("Approved", mapped.Status().Single().Status);
            Assert.Equal(VisualStatuses.Match, (await Evaluate(mapped, Path.Combine(root, "mapped"), Bound(Execution, Fixture), Screen('A'))).Status);

            // Wrong binding: refused at the gate (no candidate) and before a run starts.
            var wrong = await Evaluate(mapped, Path.Combine(root, "wrong"), Bound(Execution, "demo/fixture/other"), Screen('B'));
            Assert.Equal(VisualStatuses.BaselineInvalid, wrong.Status);
            Assert.Contains("bound to fixture 'demo/fixture/other'", wrong.Reason);
            Assert.Null(wrong.Candidate);
            var options = new RunOptions(Path.Combine(root, "runs"), ["80x25"], "declared", null, null, null, mappedManifest);
            var refused = await Assert.ThrowsAsync<VisualBaselineConfigurationException>(() =>
                new ScenarioRunner().RunAsync(Path.Combine(root, "no-target.exe"), [Bound(Execution, "demo/fixture/other")], options, null, CancellationToken.None));
            Assert.Contains("Nothing was run", refused.Message);
            Assert.False(Directory.Exists(Path.Combine(root, "runs")) && Directory.EnumerateDirectories(Path.Combine(root, "runs")).Any());

            // An approval recorded for another execution is invalid for this entry; a candidate of the unmapped era is refused.
            string otherMeta = mapped.ApprovedMetadata(key), metaText = File.ReadAllText(otherMeta);
            File.WriteAllText(otherMeta, metaText.Replace("\"review\"", "\"executionScenarioId\": \"external/elsewhere\",\n  \"review\""));
            Assert.Contains("approved for execution 'external/elsewhere'", mapped.Status().Single().Reason);
            File.WriteAllText(otherMeta, metaText);
            string staleRun = Path.Combine(root, "stale");
            await Evaluate(legacy, staleRun, Compiled(Fixture), Screen('C'));                       // unmapped candidate (no execution id)
            Assert.Contains("was produced by execution", Assert.Throws<ArgumentException>(() => mapped.Approve(staleRun, [key], "approve-1", true)).Message);
            Assert.Equal(snapshot, Snapshot(mapped.ApprovedDirectory));
        }
        finally { Delete(root); }
    }

    // ── native eligibility ─────────────────────────────────────────────────────────

    [Fact]
    public void NativeEligibilityFollowsTheValidatedFixtureBindingAndTheAuditedStepSet()
    {
        ScenarioStep[] f10 = [new(ScenarioStep.Checkpoint, "desktop"), new(ScenarioStep.Key, "kbF10"), new(ScenarioStep.Checkpoint, "bar"), new(ScenarioStep.Key, "kbEsc")];
        const string audited = "commander/menu/f10-open-close";
        // Existing compiled native scenario: unchanged.
        Assert.True(NativeExecution.Supports(Compiled(audited, f10), "SDLDriver"));
        Assert.True(NativeExecution.Supports(Compiled(audited, f10), "Win32ConsoleDriver"));
        Assert.True(NativeExecution.Supports(Compiled(audited, f10), "SDLGpuDriver"));
        // External scenario bound to the audited fixture: eligible, and its own ID stays the run identity.
        var external = Bound("external/" + audited, audited, f10);
        Assert.True(NativeExecution.Supports(external, "SDLDriver"));
        Assert.Equal(audited, NativeExecution.NativeIdentity(external));
        Assert.Equal("external/" + audited, external.Id);
        // Refusals.
        Assert.Contains("without a validated fixture binding", NativeExecution.Refusal(external with { External = new(audited, "a.json", "h", "") }, "SDLDriver"));
        Assert.Contains("without a validated fixture binding", NativeExecution.Refusal(external with { External = new("", "a.json", "h", "") }, "SDLDriver"));
        Assert.Contains("ordinary Proxy scenario", NativeExecution.Refusal(Proxy("external/" + audited), "SDLDriver"));
        Assert.Contains("bound fixture 'commander/panels/tree' is not an audited", NativeExecution.Refusal(Bound("external/x", "commander/panels/tree", f10), "SDLDriver"));
        Assert.Contains("'external/commander/menu/f10-open-close' is not an audited", NativeExecution.Refusal(Compiled("external/" + audited, f10), "SDLDriver"));
        Assert.Contains("not a supported native driver", NativeExecution.Refusal(external, "NullDriver"));
        Assert.Contains("not a supported native driver", NativeExecution.Refusal(external, "SDLMetalDriver"));
        Assert.Contains("step action", NativeExecution.Refusal(Bound("external/x", audited, [.. f10, new(ScenarioStep.Action, "open-dialog")]), "SDLDriver"));
        Assert.Contains("[redacted]", NativeExecution.Refusal(Bound("external/x", audited, [.. f10, new(InputSteps.Text, "secret")]), "SDLDriver"));
        Assert.Contains("step key 'kbAltX'", NativeExecution.Refusal(Bound("external/x", audited, [.. f10, new(ScenarioStep.Key, "kbAltX")]), "SDLDriver"));
        var mouse = Bound("external/commander/input/menu-mouse", "commander/input/menu-mouse", [new(ScenarioStep.Checkpoint, "desktop"), new(InputSteps.Mouse, "click-left@20,0")]);
        Assert.True(NativeExecution.Supports(mouse, "SDLDriver"));
        Assert.Contains("no audited mouse input", NativeExecution.Refusal(mouse, "Win32ConsoleDriver"));
        var jobs = Bound("external/commander/menu-status/menu-opens-jobs", "commander/menu-status/menu-opens-jobs", f10);
        Assert.True(NativeExecution.Supports(jobs, "Win32ConsoleDriver"));
        Assert.False(NativeExecution.Supports(jobs, "SDLDriver"));
        // Inside the target, the launched fixture-only registration (no steps of its own) is accepted for audited IDs only.
        Assert.True(NativeExecution.Supports(Compiled(audited, []), "SDLDriver"));
        Assert.False(NativeExecution.Supports(Compiled("commander/panels/tree", []), "SDLDriver"));
    }

    [Fact]
    public async Task RunnerEnforcesNativeEligibilityWithoutFallback()
    {
        string root = Scratch();
        try
        {
            ScenarioStep[] steps = [new(ScenarioStep.Checkpoint, "desktop"), new(ScenarioStep.Action, "open-dialog"), new(ScenarioStep.Checkpoint, "open")];
            var options = new RunOptions(Path.Combine(root, "runs"), ["80x25"], "declared", null, null, null) { Driver = "SDLDriver" };
            var batch = await new ScenarioRunner().RunAsync(Path.Combine(root, "no-target.exe"),
                [Bound("external/commander/menu/f10-open-close", "commander/menu/f10-open-close", steps)], options, null, CancellationToken.None);
            var result = Assert.Single(batch.Results);
            Assert.Equal(ScenarioOutcomes.Skipped, result.Outcome);   // Driver prerequisite
            Assert.Equal("external/commander/menu/f10-open-close", result.ScenarioId);
            Assert.Contains("step action 'open-dialog' is not audited", result.Reason);
            Assert.Contains("no logical fallback performed", result.Reason);
            Assert.Empty(result.Checkpoints);
        }
        finally { Delete(root); }
    }

    // ── Commander (read-only) ─────────────────────────────────────────────────────

    [CommanderFact]
    public void CommanderOriginalAndExplicitlyMappedManifestsResolveTheSame22ApprovedKeys()
    {
        string root = Scratch();
        try
        {
            string source = Path.Combine(CommanderRoot, "diagnostics", "CommanderTestTarget", "VisualBaselines");
            var before = Snapshot(Path.Combine(source, VisualBaselineSet.ApprovedFolder));
            byte[] manifestBefore = File.ReadAllBytes(Path.Combine(source, "visual-baselines.json"));
            Assert.Equal(66, before.Count);

            // Commander's own migration may already have added executionScenario to its manifest. The
            // unmapped form is then derived on a scratch copy (fields removed), and Commander's mapping must equal the one
            // derived from the real bindings below; Commander's files are only read.
            var commander = VisualBaselineSet.Load(Path.Combine(source, "visual-baselines.json"));
            string unmappedCopy = CopyStore(source, Path.Combine(root, "unmapped"));
            var unmappedJson = JsonNode.Parse(File.ReadAllText(Path.Combine(unmappedCopy, "visual-baselines.json")))!;
            foreach (var e in unmappedJson["baselines"]!.AsArray()) e!.AsObject().Remove("executionScenario");
            File.WriteAllText(Path.Combine(unmappedCopy, "visual-baselines.json"), unmappedJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var original = VisualBaselineSet.Load(Path.Combine(unmappedCopy, "visual-baselines.json"));
            var originalStatus = original.Status().ToArray();
            Assert.Equal(11, original.Manifest.Baselines.Length);
            Assert.Equal(22, originalStatus.Length);
            Assert.All(originalStatus, s => Assert.Equal("Approved", s.Status));
            Assert.False(original.HasIdentityMappings);
            Assert.Equal(originalStatus.Select(s => (s.Key, s.Status)), commander.Status().Select(s => (s.Key, s.Status)));

            // The mapping comes from the real JSON fixture bindings (one external document per baseline identity).
            var bindings = CommanderBindings();
            if (commander.HasIdentityMappings)
                Assert.All(commander.Manifest.Baselines, e => Assert.Equal(Assert.Single(bindings, b => b.Fixture == e.Scenario).Id, e.ExecutionScenario));
            string copy = CopyStore(source, Path.Combine(root, "mapped"));
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, "visual-baselines.json")))!;
            foreach (var e in manifest["baselines"]!.AsArray())
            {
                string scenario = e!["scenario"]!.GetValue<string>();
                var external = Assert.Single(bindings, b => b.Fixture == scenario);
                e["executionScenario"] = external.Id;
            }
            File.WriteAllText(Path.Combine(copy, "visual-baselines.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var mapped = VisualBaselineSet.Load(Path.Combine(copy, "visual-baselines.json"));
            var mappedStatus = mapped.Status().ToArray();
            Assert.Equal(originalStatus.Select(s => (s.Key, s.Status)), mappedStatus.Select(s => (s.Key, s.Status)));
            // 11 entries over 8 baseline identities: one external execution per identity (one-to-one).
            Assert.Equal(8, mapped.Manifest.Baselines.Select(e => e.Scenario).Distinct().Count());
            Assert.Equal(8, mapped.Manifest.Baselines.Select(e => e.ExecutionScenario).Distinct().Count());
            Assert.All(mapped.Manifest.Baselines, e => Assert.StartsWith("external/", e.Execution));

            // Synthetic catalog with the real bindings: every mapping verifies; wrong or missing bindings are refused.
            var catalog = bindings.Select(b => Bound(b.Id, b.Fixture)).ToArray();
            Assert.All(mapped.Manifest.Baselines, e => Assert.Null(mapped.BindingProblem(e.Execution, catalog)));
            var first = mapped.Manifest.Baselines[0];
            Assert.Contains("bound to fixture", mapped.BindingProblem(first.Execution, [Bound(first.Execution, mapped.Manifest.Baselines[2].Scenario)])!);
            Assert.Contains("not in the current catalog", mapped.BindingProblem(first.Execution, [])!);
            // Unmapped original manifest + external executions: refused, never borrowed.
            Assert.All(original.Manifest.Baselines, e => Assert.Contains("never borrowed", original.UnmappedFixtureIdentity(e, catalog.Single(c => c.External!.FixtureId == e.Scenario))!));

            // A wrong approved key or wrong scenario metadata stays invalid (strict metadata validation is unchanged).
            string key = mappedStatus[0].Key, meta = mapped.ApprovedMetadata(key), text = File.ReadAllText(meta);
            File.WriteAllText(meta, text.Replace($"\"scenarioId\": \"{first.Scenario}\"", $"\"scenarioId\": \"{first.Execution}\""));
            Assert.Contains("Baseline identity", mapped.Status().First().Reason);
            File.Move(meta, meta + ".moved"); File.WriteAllText(meta, text.Replace($"\"key\": \"{key}\"", "\"key\": \"commander-other--x--80x25\""));
            Assert.Equal(VisualStatuses.BaselineInvalid, mapped.Status().First().Status);

            // Originals untouched.
            Assert.Equal(before, Snapshot(Path.Combine(source, VisualBaselineSet.ApprovedFolder)));
            Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(source, "visual-baselines.json")));
            Evidence("commander-mapping.json", new
            {
                original = originalStatus.Select(s => new { s.Key, s.Status }),
                mappings = mapped.Manifest.Baselines.Select(e => new { baseline = e.Scenario, execution = e.Execution, e.Checkpoint, e.Grids }),
                approvedFiles = before.Count, approvedUnchanged = true,
            });
        }
        finally { Delete(root); }
    }

    [CommanderFact]
    public void CommanderExternalDocumentsAreNativeEligibleOnlyThroughTheirAuditedFixtures()
    {
        string scenarios = Path.Combine(CommanderRoot, "diagnostics", "CommanderTestTarget", "ExternalScenarios");
        var rows = new List<object>();
        int eligible = 0;
        foreach (var (id, fixture, path) in CommanderBindings())
        {
            var loaded = ExternalScenarios.Load(path).Scenario;
            var bound = loaded with { External = new(fixture, path, "h", ExternalScenarios.ContractFingerprintPrefix + "test") };
            var modes = new[] { "SDLDriver", "SDLGpuDriver", "Win32ConsoleDriver" }.Where(m => NativeExecution.Supports(bound, m)).ToArray();
            Assert.False(NativeExecution.Supports(loaded, "SDLDriver"));                                // the document alone (no binding) never is
            if (modes.Length > 0) { eligible++; rows.Add(new { id, fixture, modes }); }
        }
        // The five audited workflows, now reached through their external documents.
        Assert.Equal(5, eligible);
        Assert.True(Directory.Exists(scenarios));
        Evidence("commander-native-eligibility.json", rows);
    }

    // Representative runtime (logical, NullDriver): the 11 mapped external scenarios against the explicitly mapped scratch store.
    [CommanderRuntimeFact]
    public async Task CommanderMappedExternalRunsCompareWithTheLegacyApprovedBaselines()
    {
        string root = Scratch();
        try
        {
            string source = Path.Combine(CommanderRoot, "diagnostics", "CommanderTestTarget", "VisualBaselines");
            var before = Snapshot(Path.Combine(source, VisualBaselineSet.ApprovedFolder));
            string copy = CopyStore(source, Path.Combine(root, "mapped"));
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, "visual-baselines.json")))!;
            var bindings = CommanderBindings();
            foreach (var e in manifest["baselines"]!.AsArray()) e!["executionScenario"] = bindings.Single(b => b.Fixture == e["scenario"]!.GetValue<string>()).Id;
            File.WriteAllText(Path.Combine(copy, "visual-baselines.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var copySnapshot = Snapshot(Path.Combine(copy, VisualBaselineSet.ApprovedFolder));
            string ids = string.Join(',', manifest["baselines"]!.AsArray().Select(e => e!["executionScenario"]!.GetValue<string>()).Distinct());
            string results = Path.Combine(root, "results.json");
            var stdout = new StringWriter(); var stderr = new StringWriter();
            int code = await Cli.RunAsync(["run", Environment.GetEnvironmentVariable("TSWB_COMMANDER_PROJECT")!, "--target", Environment.GetEnvironmentVariable("TSWB_RESULTS_COMMANDER_TARGET")!,
                "--scenario", ids, "--size", "80x25,140x40", "--driver", "NullDriver", "--output", Path.Combine(root, "runs"), "--baselines", Path.Combine(copy, "visual-baselines.json"), "--results", results], stdout, stderr);
            var batch = JsonSerializer.Deserialize<BatchResult>(File.ReadAllText(results), ScenarioRunner.Json)!;
            Evidence("commander-mapped-runtime.json", new
            {
                exitCode = code, stdout = stdout.ToString(), stderr = stderr.ToString(),
                executions = batch.Results.Select(r => new { r.ScenarioId, r.Size, r.Outcome }),
                visual = batch.Visual!.Entries.Select(e => new { e.ScenarioId, e.BaselineScenarioId, key = e.BaselineKey(), e.Grid, e.Status, e.Reason }),
            });
            Assert.All(batch.Results, r => Assert.StartsWith("external/", r.ScenarioId));             // execution IDs stay external
            Assert.Equal(22, batch.Visual!.Entries.Length);
            Assert.All(batch.Visual.Entries, e => Assert.NotNull(e.BaselineScenarioId));
            Assert.Equal(22, batch.Visual.Entries.Select(e => e.BaselineKey()).Distinct().Count());
            Assert.Equal(before.Keys.Where(k => k.EndsWith(".tvs1")).Select(k => k[..^5]).Order(), batch.Visual.Entries.Select(e => e.BaselineKey()).Order());
            Assert.All(batch.Visual.Entries, e => Assert.Equal(VisualStatuses.Match, e.Status));
            Assert.Equal(0, code);
            Assert.Equal(copySnapshot, Snapshot(Path.Combine(copy, VisualBaselineSet.ApprovedFolder)));
            Assert.Equal(before, Snapshot(Path.Combine(source, VisualBaselineSet.ApprovedFolder)));
        }
        finally { Delete(root); }
    }

    // ── TUI: mapped baselines, Results identity lines, candidate review and Show in Scenarios ──────────────

    [Fact]
    public async Task TuiRunOfAMappedFixtureBoundScenarioShowsTheIdentityReviewsAndNavigatesToItsDocument()
    {
        const string fixture = "sample/fixture-only/notes", execution = "ext/notes-idle";
        string root = Scratch(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs"), runs = Path.Combine(root, "runs");
        Directory.CreateDirectory(docs);
        string document = Path.Combine(docs, "notes-idle.scenario.json");
        File.WriteAllText(document, $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"{{execution}}","title":"Notes idle","description":"d","group":"external","fixture":"{{fixture}}","expectedOutcome":"passed","steps":[{"kind":"checkpoint","id":"idle"}]}""");
        byte[] documentBytes = File.ReadAllBytes(document);
        string store = Path.Combine(root, "store");
        string manifest = Manifest(store, Entry(fixture, execution, "idle"));
        string project = Path.Combine(root, "mapped.twproj");
        new WorkbenchProject { Name = "Mapped", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = runs, ScenarioFiles = [document],
            TestType = "visual", BaselineStore = manifest, ScreenSizes = ["80x25"], SelectedIds = [execution], RunScope = "selected" }.Save(project);
        string projectBytes = File.ReadAllText(project);
        await Tui.Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Tui.Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count > 0 ? p : null, 60);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
            var main = await Tui.Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Contains("Notes idle") ? p : null, 60);
            await bridge.Invoke(() => { main.TestList.ClearMarks(); main.TestList.Toggle(execution); });
            Tui.Press(Keys.kbF5);
            await Tui.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
            await Tui.Shot(bridge, "run-configuration-mapped-baselines");
            await bridge.Invoke(() => Assert.Contains("explicit baseline identity mapping", Tui.Text()));
            Tui.Press(Keys.kbEnter);
            await Tui.Until(bridge, () => main.Busy, 30);
            await Tui.Until(bridge, () => !main.Busy, 120);
            Tui.Press(Keys.kbF6);
            await Tui.Wait(bridge, () => app.DeskTop!.current as WorkbenchResultsDialog);
            await Tui.Shot(bridge, "results-mapped-missing-baseline");
            await bridge.Invoke(() => Assert.Contains("baseline identity sample/fixture-only/notes", Tui.Text()));
            // Window 6 navigation from Results: the attached document is located, nothing is written.
            Tui.Send(WorkbenchDialog.ShowResultScenario);
            var scenarios = await Tui.Wait(bridge, () => main.ScenariosWindow);
            await bridge.Invoke(() => Assert.Equal(document, scenarios.Selected?.Path));
            await Tui.Shot(bridge, "scenarios-from-results");
        });
        var batch = Tui.LastRun(runs);
        var result = Assert.Single(batch.Results);
        Assert.Equal((execution, ScenarioOutcomes.Passed), (result.ScenarioId, result.Outcome));
        var entry = Assert.Single(batch.Visual!.Entries);
        Assert.Equal((VisualStatuses.BaselineMissing, fixture), (entry.Status, entry.BaselineScenarioId));
        // TUI review (CandidateReview) and the CLI approval agree on the identity.
        var set = VisualBaselineSet.Load(manifest);
        string key = VisualBaselineSet.Key(fixture, "idle", "80x25");
        var review = CandidateReview.Load(batch, set, key);
        Assert.Equal((fixture, execution, false), (review.File.ScenarioId, review.File.ExecutionScenarioId, review.Replace));
        var stdout = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["baseline", "approve", "--baselines", manifest, "--run", batch.RunRoot, "--candidate", key, "--confirm", "approve-1"], stdout, new StringWriter()));
        stdout = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["baseline", "status", "--baselines", manifest, "--project", project], stdout, new StringWriter()));
        Assert.Contains($"Approved\t{key}", stdout.ToString());
        Assert.Contains($"Mapping\t{execution} -> {fixture}\t1 key(s)\tfixture binding verified", stdout.ToString());
        Evidence("cli-baseline-status-mapped.txt", stdout.ToString());
        // A second run through the CLI compares with the approved baseline under the mapped identity.
        string json = Path.Combine(root, "second.json");
        Assert.Equal(0, await Cli.RunAsync(["run", project, "--output", runs, "--results", json], new StringWriter(), new StringWriter()));
        var second = JsonSerializer.Deserialize<BatchResult>(File.ReadAllText(json), ScenarioRunner.Json)!;
        Assert.Equal((execution, VisualStatuses.Match), (second.Visual!.Entries.Single().ScenarioId, second.Visual.Entries.Single().Status));
        Assert.Equal(documentBytes, File.ReadAllBytes(document));                                       // the JSON document is never rewritten
        Assert.Equal(projectBytes, File.ReadAllText(project));                                           // session run: project unchanged
        Delete(root);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static string Entry(string scenario, string? execution = null, string checkpoint = Checkpoint) =>
        $$"""{ "scenario": "{{scenario}}", "checkpoint": "{{checkpoint}}", "grids": [ "80x25" ], "purpose": "test"{{(execution is null ? "" : $", \"executionScenario\": \"{execution}\"")}} }""";
    private static string ManifestText(params string[] entries) =>
        $$"""{ "format": "{{VisualBaselineSet.ManifestFormat}}", "normalizations": [], "baselines": [ {{string.Join(", ", entries)}} ] }""";
    private static string Manifest(string root, params string[] entries)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "visual-baselines.json");
        File.WriteAllText(path, ManifestText(entries));
        return path;
    }
    private static ScenarioDescriptor Compiled(string id, ScenarioStep[]? steps = null) =>
        new(id, id, "d", "demo", ScenarioGrid.Standard, steps ?? [new(ScenarioStep.Checkpoint, Checkpoint)]);
    private static ScenarioDescriptor Bound(string id, string fixture, ScenarioStep[]? steps = null) =>
        Compiled(id, steps) with { External = new(fixture, id + ".json", "content", ExternalScenarios.ContractFingerprintPrefix + "00") };
    private static ScenarioDescriptor Proxy(string id) => Compiled(id) with { Backend = "proxy-logical", External = new("", id + ".json", "content", "") };
    private static ScreenSnapshot Screen(char fill)
    {
        var cells = new TScreenChar[80 * 25];
        for (int i = 0; i < cells.Length; i++) cells[i] = new(i < 10 ? fill : ' ', 0x17);
        return new(80, 25, cells);
    }
    private static async Task<VisualCheckpointResult> Evaluate(VisualBaselineSet set, string run, ScenarioDescriptor scenario, ScreenSnapshot screen)
    {
        Directory.CreateDirectory(run);
        string stem = ArtifactNames.Stem(scenario.Id, Checkpoint, "80x25");
        File.WriteAllBytes(Path.Combine(run, stem + ".tvs1"), screen.Serialize());
        return (await new VisualGate(set, run).EvaluateAsync(scenario, Checkpoint, "80x25", "80x25", screen, stem, new((string?)null), Identity))!;
    }
    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).ToDictionary(f => Path.GetFileName(f), f => ScreenSnapshot.Hash(File.ReadAllBytes(f))) : [];
    private static string CopyStore(string source, string target)
    {
        Directory.CreateDirectory(Path.Combine(target, VisualBaselineSet.ApprovedFolder));
        File.Copy(Path.Combine(source, "visual-baselines.json"), Path.Combine(target, "visual-baselines.json"));
        foreach (string f in Directory.GetFiles(Path.Combine(source, VisualBaselineSet.ApprovedFolder)))
            File.Copy(f, Path.Combine(target, VisualBaselineSet.ApprovedFolder, Path.GetFileName(f)));
        return target;
    }
    private static (string Id, string Fixture, string Path)[] CommanderBindings() =>
        Directory.GetFiles(Path.Combine(CommanderRoot, "diagnostics", "CommanderTestTarget", "ExternalScenarios"), "*.json", SearchOption.AllDirectories).Select(f =>
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(f));
            return (json.RootElement.GetProperty("id").GetString()!, json.RootElement.TryGetProperty("fixture", out var x) ? x.GetString()! : "", f);
        }).ToArray();
    internal static string CommanderRoot => Environment.GetEnvironmentVariable("TSWB_RESULTS_COMMANDER_ROOT") is { Length: > 0 } custom ? custom : @"C:\TurboVision\GIT\TSharpCommander";
    private static void Evidence(string name, object value)
    {
        if (Environment.GetEnvironmentVariable("TSWB_RESULTS_EVIDENCE") is not { Length: > 0 } evidence) return;
        string root = Path.Combine(evidence, "ext-compat");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, name), value as string ?? JsonSerializer.Serialize(value, ScenarioRunner.Json));
    }
    private static string Scratch() => Path.Combine(Path.GetTempPath(), "ui-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public sealed class CommanderFactAttribute : FactAttribute
    {
        public CommanderFactAttribute()
        {
            if (!File.Exists(Path.Combine(CommanderRoot, "diagnostics", "CommanderTestTarget", "VisualBaselines", "visual-baselines.json")))
                Skip = "Commander repository not found (TSWB_RESULTS_COMMANDER_ROOT).";
        }
    }
    public sealed class CommanderRuntimeFactAttribute : FactAttribute
    {
        public CommanderRuntimeFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("TSWB_RESULTS_COMMANDER_TARGET") ?? "") || !File.Exists(Environment.GetEnvironmentVariable("TSWB_COMMANDER_PROJECT") ?? ""))
                Skip = "Set TSWB_RESULTS_COMMANDER_TARGET (diagnostic Commander) and TSWB_COMMANDER_PROJECT (CommanderExternal.twproj).";
        }
    }

    // The real WorkbenchApp on NullDriver (same harness as ScenarioManagementTests). Logical screenshots: TSWB_RESULTS_EVIDENCE.
    private static class Tui
    {
        internal static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body)
        {
            Directory.CreateDirectory(root);
            string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home, target = TestWorkbenchHostTests.SampleTarget;
            ProjectLocation.Home = home;
            try
            {
                using var driver = new DriverScope((ushort)width, (ushort)height);
                using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") }, showStart: false);
                var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
                Exception? failure = null;
                Task worker = Task.Run(async () =>
                {
                    try { await body(app, bridge); await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); }); }
                    catch (Exception ex)
                    {
                        failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                        await bridge.Invoke(() =>
                        {
                            for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                            if (app.Project is { Busy: true }) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = WorkbenchDialog.CancelRun; app.Project.HandleEvent(ref ev); }
                            app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                        }).WaitAsync(TimeSpan.FromSeconds(20));
                    }
                });
                AppLifecycleGuard.Run(app);
                Assert.True(worker.Wait(TimeSpan.FromSeconds(240)), "Worker did not stop.");
                Assert.Null(failure);
            }
            finally { ProjectLocation.Home = previousHome; }
        }
        internal static BatchResult LastRun(string runs) =>
            WorkbenchRunHistory.Load(Path.Combine(Directory.GetDirectories(runs, "run-*").OrderBy(Directory.GetCreationTimeUtc).First(), "run.json")).Batch;
        internal static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
        internal static async Task Shot(Bridge bridge, string state)
        {
            await Settle(bridge);
            if (Environment.GetEnvironmentVariable("TSWB_RESULTS_EVIDENCE") is not { Length: > 0 } root) return;
            await bridge.Invoke(() =>
            {
                string folder = Path.Combine(root, "screenshots"); Directory.CreateDirectory(folder);
                var screen = ScreenSnapshot.CopyRoot();
                string name = $"ext-{state}-{screen.Width}x{screen.Height}";
                byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
                File.WriteAllBytes(Path.Combine(folder, name + ".logical.png"), png);
                File.WriteAllBytes(Path.Combine(folder, name + ".logical.tvs1"), cells);
                File.WriteAllText(Path.Combine(folder, name + ".logical.json"), JsonSerializer.Serialize(new
                {
                    name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", origin = "logical", backend = "NullDriver (in-process WorkbenchApp)",
                    targetDriver = "NullDriver", test = nameof(ExternalCompatTests), renderer = LogicalPng.Renderer,
                    cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png), files = new[] { name + ".logical.png", name + ".logical.tvs1" }, captured = true
                }, new JsonSerializerOptions { WriteIndented = true }));
            });
        }
        internal static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
        internal static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
        internal static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(60); await bridge.Invoke(() => { }); }
        internal static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
        internal static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
        }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
