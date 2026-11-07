using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Fixture-only registration, strict external checkpoint binding, raw versus runnable discovery and the
// versioned fixture-only contract fingerprint. Process-level cases launch the real sample target, whose fixture-only
// registration is sample/fixture-only/notes (tests/TSharpWorkbench.SampleTarget/FixtureOnlyScenarios.cs).
[Collection("NonParallel")]
public sealed class FixtureOnlyRegistrationTests
{
    private const string FixtureId = "sample/fixture-only/notes", ExternalId = "external/notes-flow", Revision = "tsw-fixture-1.sample.1";
    private static string Target => TestWorkbenchHostTests.SampleTarget;

    private static string Folder()
    {
        string root = Environment.GetEnvironmentVariable("TSWB_FIXTURE_RESULTS") ?? Path.Combine(Path.GetTempPath(), "tsw-fixture-1");
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private const string Flow = """
        [{"kind":"checkpoint","id":"idle","assertions":[{"id":"pending","kind":"screen-contains","text":"Notes fixture pending"}]},
         {"kind":"action","adapter":"open-note"},
         {"kind":"checkpoint","id":"note-open"},
         {"kind":"key","key":"kbAltF3"},
         {"kind":"mouse","input":"click-left@:label"},
         {"kind":"action","adapter":"begin-deferred-label"},
         {"kind":"checkpoint","id":"label-ready","assertions":[{"id":"ready","kind":"screen-contains","text":"Notes fixture ready"}]}]
        """;

    private static string Document(string steps = Flow, string id = ExternalId, string fixture = FixtureId) =>
        $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"{{id}}","title":"External notes flow","description":"Authored externally; bound to a fixture-only registration","group":"external","fixture":"{{fixture}}","expectedOutcome":"passed","steps":{{steps}}}""";

    private static string FileIn(string root, string content, string name = "notes.json")
    {
        string path = Path.Combine(root, name); File.WriteAllText(path, content); return path;
    }

    private static TestScenario FixtureOnly(string revision = "r1", string checkpoint = "ready", string action = "act") =>
        new TestScenario("fixture/only", "Fixture only", "fixture", "Fixture-only registration").Grid(60, 20)
            .FixtureOnly(revision).RegisterAction(action, _ => { }).RegisterPoint("cell", _ => new TPoint(1, 1))
            .RegisterCheckpointExpectation(checkpoint, _ => true).RegisterCheckpointExpectation("done", _ => true);

    private static ScenarioDescriptor Bound(ScenarioDescriptor fixture, string steps, string[]? legacyFixtures = null)
    {
        string path = FileIn(Folder(), Document(steps, "external/flow", fixture.Id));
        return ExternalScenarios.Merge([fixture], [ExternalScenarios.Load(path)], legacyFixtures).Single(s => s.Id == "external/flow");
    }

    // ── A. registration ─────────────────────────────────────────────────────

    [Fact]
    public void BodylessFixtureOnlyRegistrationAdvertisesItsContractWithoutSteps()
    {
        var scenario = FixtureOnly();
        var d = scenario.Descriptor;
        Assert.True(scenario.IsFixtureOnly); Assert.True(d.IsFixtureOnly);
        Assert.Empty(d.Steps);
        Assert.Equal(("fixture-only", 1, "r1"), (d.Contract!.Role, d.Contract.ContractVersion, d.Contract.Revision));
        Assert.Equal(new[] { "done", "ready" }, d.Contract.Checkpoints);
        Assert.Equal(new[] { "act" }, d.Adapters!.Actions); Assert.Equal(new[] { "cell" }, d.Adapters.Points);
        ScenarioRunner.ValidateCatalog([d]);
        // The contract survives the wire (default serializer) and artifact serialization.
        var wire = JsonSerializer.Deserialize<ScenarioDescriptor>(JsonSerializer.SerializeToUtf8Bytes(d))!;
        Assert.Equal(d.Contract.Checkpoints, wire.Contract!.Checkpoints); Assert.True(wire.IsFixtureOnly);
        ScenarioRunner.ValidateCatalog([wire]);
        Assert.DoesNotContain("isFixtureOnly", JsonSerializer.Serialize(d, ScenarioRunner.Json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndependentRegistrationNeverCreatesStepsAndKeepsCompiledBuildersUnchanged()
    {
        var scenario = new TestScenario("x/legacy", "Legacy", "x", "d")
            .RegisterAction("registered", _ => { }).RegisterCheckpointExpectation("expected", _ => true, "note").RegisterPoint("cell", _ => null);
        Assert.Empty(scenario.Descriptor.Steps);
        Assert.Null(scenario.Descriptor.Contract);
        // An ordinary scenario with only registrations is still an empty runnable scenario: rejected as before.
        Assert.Throws<IOException>(() => ScenarioRunner.ValidateCatalog([scenario.Descriptor]));
        scenario.Checkpoint("expected").Action("scripted", _ => { });
        Assert.Equal(new[] { "checkpoint", "action" }, scenario.Descriptor.Steps.Select(s => s.Kind));
        Assert.Equal(new[] { "registered", "scripted" }, scenario.Descriptor.Adapters!.Actions);
        ScenarioRunner.ValidateCatalog([scenario.Descriptor]);
        Assert.Throws<ArgumentException>(() => scenario.RegisterAction("scripted", _ => { }));
        Assert.Throws<ArgumentException>(() => scenario.Action("registered", _ => { }));
        Assert.Throws<ArgumentException>(() => scenario.RegisterCheckpointExpectation("expected", _ => true));
        Assert.Throws<ArgumentException>(() => scenario.RegisterCheckpointExpectation("Bad Name", _ => true));
        Assert.Throws<ArgumentException>(() => scenario.RegisterCheckpointExpectation("no-predicate", null!));
        Assert.Throws<ArgumentException>(() => scenario.RegisterAction("no-callback", null!));
        Assert.Throws<ArgumentException>(() => scenario.RegisterPoint("cell", _ => null));
        Assert.Throws<ArgumentException>(() => scenario.RegisterPoint("no-resolver", null!));
        // Ordinary empty scenarios stay invalid.
        Assert.Throws<IOException>(() => ScenarioRunner.ValidateCatalog([new TestScenario("x/empty", "Empty", "x", "d").Descriptor]));
    }

    [Fact]
    public void InvalidFixtureOnlyRegistrationsAndDescriptorsAreRejected()
    {
        foreach (string revision in new[] { "", "   ", new string('r', 129), "bad\nrevision" })
            Assert.Throws<ArgumentException>(() => new TestScenario("f/x", "F", "f", "d").FixtureOnly(revision));
        Assert.Throws<InvalidOperationException>(() => new TestScenario("f/x", "F", "f", "d").Key("kbF3").FixtureOnly("r1"));
        Assert.Throws<InvalidOperationException>(() => new TestScenario("f/x", "F", "f", "d").ExpectFailure().FixtureOnly("r1"));
        Assert.Throws<InvalidOperationException>(() => FixtureOnly().Key("kbF3"));
        Assert.Throws<InvalidOperationException>(() => FixtureOnly().Text("x"));
        Assert.Throws<InvalidOperationException>(() => FixtureOnly().Resize(80, 25));
        Assert.Throws<InvalidOperationException>(() => FixtureOnly().Action("scripted", _ => { }));
        Assert.Throws<InvalidOperationException>(() => FixtureOnly().Checkpoint("scripted"));
        Assert.Throws<InvalidOperationException>(() => FixtureOnly().ExpectFailure());
        // A fixture-only registration without a single checkpoint expectation can never be bound.
        Assert.Throws<IOException>(() => ScenarioRunner.ValidateCatalog([new TestScenario("f/x", "F", "f", "d").FixtureOnly("r1").Descriptor]));
        var valid = FixtureOnly().Descriptor;
        foreach (var bad in new[]
        {
            valid with { Contract = valid.Contract! with { Role = "runnable" } },
            valid with { Contract = valid.Contract! with { ContractVersion = 2 } },
            valid with { Contract = valid.Contract! with { Revision = " " } },
            valid with { Contract = valid.Contract! with { Checkpoints = ["ready", "done"] } },
            valid with { Contract = valid.Contract! with { Checkpoints = ["done", "done"] } },
            valid with { Contract = valid.Contract! with { Checkpoints = [] } },
            valid with { Steps = [new(ScenarioStep.Checkpoint, "ready")] },
            valid with { Adapters = null },
            valid with { ExpectedResult = ScenarioOutcomes.Failed },
            valid with { External = new("x", "x.json", "h", "f") },
        })
            Assert.Throws<IOException>(() => ScenarioRunner.ValidateCatalog([bad]));
    }

    // ── B. binding ──────────────────────────────────────────────────────────

    [Fact]
    public void StrictBindingAcceptsAdvertisedNamesAndRejectsUnknownCheckpointsActionsAndPoints()
    {
        var fixture = FixtureOnly().Descriptor;
        var bound = Bound(fixture, """[{"kind":"action","adapter":"act"},{"kind":"mouse","input":"click-left@:cell"},{"kind":"checkpoint","id":"ready"},{"kind":"checkpoint","id":"done"}]""");
        Assert.False(bound.IsFixtureOnly); Assert.Equal(fixture.Id, bound.External!.FixtureId);
        Assert.Equal(ExternalScenarios.ContractFingerprint(fixture), bound.External.FixtureHash);
        Assert.StartsWith(ExternalScenarios.ContractFingerprintPrefix, bound.External.FixtureHash);

        var unknown = Assert.Throws<ExternalScenarioException>(() => Bound(fixture, """[{"kind":"checkpoint","id":"ready"},{"kind":"checkpoint","id":"made-up"}]"""));
        Assert.Contains("'external/flow'", unknown.Message); Assert.Contains("'made-up'", unknown.Message);
        Assert.Contains("'fixture/only'", unknown.Message); Assert.Contains("advertised: done, ready", unknown.Message);
        Assert.Contains("Unresolved action adapter 'missing'", Assert.Throws<ExternalScenarioException>(() =>
            Bound(fixture, """[{"kind":"action","adapter":"missing"},{"kind":"checkpoint","id":"ready"}]""")).Message);
        Assert.Contains("Unresolved point adapter 'nowhere'", Assert.Throws<ExternalScenarioException>(() =>
            Bound(fixture, """[{"kind":"mouse","input":"click-left@:nowhere"},{"kind":"checkpoint","id":"ready"}]""")).Message);
        Assert.Equal(3, Bound(fixture, """[{"kind":"checkpoint","id":"ready","retry":{"attempts":3,"intervalMilliseconds":10}}]""").Steps[0].Retry!.Attempts);
    }

    [Fact]
    public void StrictCheckpointRuleIsLimitedToFixtureOnlyRegistrations()
    {
        // Legacy compiled fixture: refused by default; with the explicit legacy allowance unadvertised external
        // checkpoint names still bind (existing behaviour), with the legacy fingerprint.
        var legacy = new TestScenario("fixture/only", "Legacy", "fixture", "d").Grid(60, 20).Action("act", _ => { }).Checkpoint("compiled", _ => true).Descriptor;
        Assert.Contains("not a fixture-only registration", Assert.Throws<ExternalScenarioException>(() =>
            Bound(legacy, """[{"kind":"action","adapter":"act"},{"kind":"checkpoint","id":"anything-authored"}]""")).Message);
        var bound = Bound(legacy, """[{"kind":"action","adapter":"act"},{"kind":"checkpoint","id":"anything-authored"}]""", [legacy.Id]);
        Assert.Equal(ExternalScenarios.Fingerprint(legacy), bound.External!.FixtureHash);
        Assert.Equal(ExternalScenarios.Fingerprint(legacy), ExternalScenarios.BindingFingerprint(legacy));
        Assert.DoesNotContain(ExternalScenarios.ContractFingerprintPrefix, bound.External.FixtureHash);
        Assert.Null(ExternalScenarios.UnboundCheckpoint(bound, FixtureOnly(checkpoint: "anything-authored").Descriptor));
    }

    // ── C. fingerprints ─────────────────────────────────────────────────────

    // Pre-change Testing build (src/TSharpWorkbench.Testing/bin/Debug, 2026-10-26 09:59) computed these for Legacy().
    private const string LegacyFingerprint = "9dc0c0eb679de87536105386999963a7a79fdf5e8c04b0948ba5cb649dd5bac8",
        LegacyWireSha256 = "e5711d4632c34349a8bfd0baf48377f442d1c6bda28bcf26bb1d8c37d602c206";

    private static ScenarioDescriptor Legacy() => new TestScenario("golden/legacy", "Legacy fixture", "golden", "Legacy descriptor golden", "80x25")
        .Grid(60, 20).Kind(ScenarioKinds.Dialog).Metadata("area", "golden").Conditions("Idle desktop.")
        .Safety(new ScenarioSafety(FixtureFiles: true, Notes: "golden"))
        .Point("button", _ => null).Checkpoint("initial", _ => true, "idle", ["view-tree"], UiAssert.Modal("no-modal", null))
        .Key("kbF3").Action("act", _ => { }, "note").Resize(100, 30).Checkpoint("after").Descriptor;

    [Fact]
    public void LegacyDescriptorSerializationAndFingerprintAreByteIdentical()
    {
        var d = Legacy();
        Assert.Equal(LegacyFingerprint, ExternalScenarios.Fingerprint(d));
        Assert.Equal(LegacyFingerprint, ExternalScenarios.BindingFingerprint(d));
        Assert.Equal(LegacyWireSha256, Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(d))));
        Assert.DoesNotContain("ontract", JsonSerializer.Serialize(d)); Assert.DoesNotContain("ontract", JsonSerializer.Serialize(d, ScenarioRunner.Json));
        Assert.Throws<ArgumentException>(() => ExternalScenarios.ContractFingerprint(d));
    }

    [Fact]
    public void ContractFingerprintIsDeterministicAndCoversTheAdvertisedContract()
    {
        string a = ExternalScenarios.ContractFingerprint(FixtureOnly().Descriptor);
        Assert.Equal(a, ExternalScenarios.ContractFingerprint(FixtureOnly().Descriptor));
        Assert.Matches("^fixture-contract/1:[0-9a-f]{64}$", a);
        // Registration order does not matter; names are ordinal-sorted.
        var reordered = new TestScenario("fixture/only", "Other display name", "other-group", "Other description").Grid(60, 20)
            .FixtureOnly("r1").RegisterCheckpointExpectation("done", _ => false).RegisterPoint("cell", _ => null)
            .RegisterCheckpointExpectation("ready", _ => false).RegisterAction("act", _ => throw new InvalidOperationException());
        Assert.Equal(a, ExternalScenarios.ContractFingerprint(reordered.Descriptor)); // display text and callback bodies are not covered
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly(revision: "r2").Descriptor));
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly(checkpoint: "ready-now").Descriptor));
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly(action: "act-two").Descriptor));
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly().Safety(new(Network: true)).Descriptor));
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly().Grid(80, 25).Descriptor));
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly().Metadata("area", "x").Descriptor));
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly().Conditions("Idle desktop.").Descriptor));
        Assert.NotEqual(a, ExternalScenarios.ContractFingerprint(FixtureOnly().Descriptor with { Adapters = FixtureOnly().Descriptor.Adapters! with { Recapture = false } }));
        Assert.NotEqual(ExternalScenarios.Fingerprint(FixtureOnly().Descriptor), a);
    }

    [Fact]
    public void JsonContentHashIsTheRawDocumentHashAndSchemaV1DocumentsStayByteIdentical()
    {
        string path = FileIn(Folder(), Document());
        byte[] before = File.ReadAllBytes(path);
        var file = ExternalScenarios.Load(path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)), file.ContentHash);
        var bound = ExternalScenarios.Merge([FixtureOnlyFromSample()], [file]).Single(s => s.Id == ExternalId);
        Assert.Equal(file.ContentHash, bound.External!.ContentHash);
        Assert.Equal(before, File.ReadAllBytes(path));

        // The Commander schema-v1 documents (read-only) still load unchanged and carry no fixtureHash.
        string commander = Environment.GetEnvironmentVariable("TSWB_FIXTURE_COMMANDER_SCENARIOS")
            ?? throw new InvalidOperationException("Set TSWB_FIXTURE_COMMANDER_SCENARIOS to Commander's ExternalScenarios directory.");
        var documents = Directory.GetFiles(commander, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(213, documents.Length);
        foreach (string document in documents)
        {
            byte[] bytes = File.ReadAllBytes(document);
            Assert.DoesNotContain("fixtureHash", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), ExternalScenarios.Load(document).ContentHash);
            Assert.Equal(bytes, File.ReadAllBytes(document));
        }
    }

    // The sample registration as advertised, without launching the target (same builder contract).
    private static ScenarioDescriptor FixtureOnlyFromSample() =>
        new TestScenario(FixtureId, "Notes fixture (fixture-only)", "sample/fixture-only", "d").Kind(ScenarioKinds.Window).Grid(60, 20)
            .FixtureOnly(Revision).RegisterAction("open-note", _ => { }).RegisterAction("begin-deferred-label", _ => { }).RegisterPoint("label", _ => null)
            .RegisterCheckpointExpectation("idle", _ => true).RegisterCheckpointExpectation("note-open", _ => true)
            .RegisterCheckpointExpectation("label-ready", _ => true).Descriptor;

    // ── D/E. real sample target: discovery, execution, host boundary ─────────

    [Fact]
    public async Task RawAdvertisementKeepsFixtureOnlyContractsAndRunnableDiscoveryOmitsThem()
    {
        string path = FileIn(Folder(), Document());
        var runner = new ScenarioRunner();
        var raw = await runner.DiscoverRawAsync(Target);
        var fixture = raw.Single(s => s.Id == FixtureId);
        Assert.True(fixture.IsFixtureOnly); Assert.Empty(fixture.Steps);
        Assert.Equal(("fixture-only", 1, Revision), (fixture.Contract!.Role, fixture.Contract.ContractVersion, fixture.Contract.Revision));
        Assert.Equal(new[] { "idle", "label-ready", "never-true", "note-open" }, fixture.Contract.Checkpoints);
        Assert.Equal(new[] { "begin-deferred-label", "open-note" }, fixture.Adapters!.Actions); Assert.Equal(new[] { "label" }, fixture.Adapters.Points);

        var runnable = await runner.DiscoverAsync(Target);
        Assert.Equal(raw.Where(s => !s.IsFixtureOnly).Select(s => s.Id), runnable.Select(s => s.Id));
        Assert.DoesNotContain(runnable, s => s.Id == FixtureId);
        // Negative host self-tests stay runnable entries, excluded from positive bulk selection only.
        var negatives = runnable.Where(s => s.ExpectedResult == ScenarioOutcomes.Failed).Select(s => s.Id).ToArray();
        Assert.Contains("selftest/failure/wrong-expectations", negatives); Assert.Contains("selftest/failure/target-crash", negatives);

        var merged = await runner.DiscoverAsync(Target, [path]);
        var external = merged.Single(s => s.Id == ExternalId);
        Assert.Equal(ExternalScenarios.ContractFingerprint(fixture), external.External!.FixtureHash);
        Assert.Contains(merged, s => s.Id == "sample/dialog/basic"); // legacy compiled scenarios are not hidden
        Assert.DoesNotContain(merged, s => s.IsFixtureOnly);
        var rawMerged = await runner.DiscoverRawAsync(Target, [path]);
        Assert.Contains(rawMerged, s => s.Id == FixtureId); Assert.Contains(rawMerged, s => s.Id == ExternalId);

        // Selection over either catalog: fixture-only never bulk-selected, refused when named.
        foreach (var catalog in new[] { merged, rawMerged })
        {
            var all = ScenarioSelection.Resolve(catalog, [], null, true);
            Assert.Contains(all, s => s.Id == ExternalId);
            Assert.DoesNotContain(all, s => s.IsFixtureOnly || s.ExpectedResult == ScenarioOutcomes.Failed);
            Assert.DoesNotContain(ScenarioSelection.Resolve(catalog, [], "sample", false), s => s.IsFixtureOnly);
            Assert.Equal(new[] { ExternalId }, ScenarioSelection.Resolve(catalog, [], "external", false).Select(s => s.Id));
        }
        Assert.Contains("fixture-only registration", Assert.Throws<ArgumentException>(() => ScenarioSelection.Resolve(rawMerged, [FixtureId], null, false)).Message);
        Assert.Throws<ArgumentException>(() => ScenarioSelection.Resolve(rawMerged, [], "sample/fixture-only", false));
        Assert.Contains("fixture-only registration", (await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(Target, [fixture], new RunOptions(Folder(), ["80x25"])))).Message);
    }

    [Fact]
    public async Task ExternalScenarioExecutesRegisteredActionsPointsAndTypedPredicatesWithLifecycle()
    {
        string root = Folder(), marker = Path.Combine(root, "cleanup.log");
        var runner = new ScenarioRunner();
        var scenario = (await runner.DiscoverAsync(Target, [FileIn(root, Document())])).Single(s => s.Id == ExternalId);
        var batch = await runner.RunAsync(Target, [scenario], new RunOptions(root, ["80x25"]) { TargetEnvironment = new() { ["TSWB_FIXTURE_CLEANUP_MARKER"] = marker } });
        var result = Assert.Single(batch.Results);
        Assert.True(result.Outcome == ScenarioOutcomes.Passed, result.Reason);
        Assert.Equal(new[] { "idle", "note-open", "label-ready" }, result.Checkpoints.Select(c => c.Checkpoint));
        Assert.All(result.Checkpoints.SelectMany(c => c.Assertions), a => Assert.Equal(AssertionOutcomes.Pass, a.Outcome));
        Assert.True(result.TargetExited && result.FixtureIntegrityVerified && result.CleanupSucceeded);
        Assert.Equal("cleanup\n", File.ReadAllText(marker));
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, "run.json")));
        var recorded = run.RootElement.GetProperty("scenarios")[0];
        Assert.StartsWith(ExternalScenarios.ContractFingerprintPrefix, recorded.GetProperty("external").GetProperty("fixtureHash").GetString());
        Assert.False(recorded.TryGetProperty("contract", out _));
        using var label = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, result.Checkpoints[2].Metadata)));
        var interactions = label.RootElement.GetProperty("interactionsSincePreviousCheckpoint").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "key", "mouse", "action" }, interactions);
    }

    [Fact]
    public async Task FailedCancelledAndDriftedExecutionsAreBoundedAndCleanedUp()
    {
        string root = Folder(), marker = Path.Combine(root, "cleanup.log");
        var runner = new ScenarioRunner();
        string path = FileIn(root, Document("""[{"kind":"checkpoint","id":"idle"},{"kind":"checkpoint","id":"never-true"}]"""));
        var scenario = (await runner.DiscoverAsync(Target, [path])).Single(s => s.Id == ExternalId);
        var environment = new Dictionary<string, string> { ["TSWB_FIXTURE_CLEANUP_MARKER"] = marker };

        var clock = Stopwatch.StartNew();
        var failed = Assert.Single((await runner.RunAsync(Target, [scenario], new RunOptions(root, ["80x25"], OperationMilliseconds: 1000) { TargetEnvironment = environment })).Results);
        Assert.True(failed.Outcome == ScenarioOutcomes.TimedOut, failed.Reason); Assert.Contains("checkpoint never-true", failed.Reason); // the unsatisfiable predicate held the capture until the bound
        Assert.Equal(new[] { "idle" }, failed.Checkpoints.Select(c => c.Checkpoint));
        Assert.True(failed.CleanupSucceeded && failed.FixtureIntegrityVerified);
        Assert.Equal("cleanup\n", File.ReadAllText(marker)); // the scenario's own cleanup ran on the failure path
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());

        clock.Restart();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var cancelled = Assert.Single((await runner.RunAsync(Target, [scenario], new RunOptions(root, ["80x25"], OperationMilliseconds: 2500) { TargetEnvironment = environment }, null, cancel.Token)).Results);
        Assert.Equal(ScenarioOutcomes.Aborted, cancelled.Outcome); Assert.True(cancelled.CleanupSucceeded, cancelled.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), clock.Elapsed.ToString());

        // Contract drift between binding and execution (another reviewed revision) is refused before Start.
        var drifted = Assert.Single((await runner.RunAsync(Target, [scenario], new RunOptions(root, ["80x25"])
            { TargetEnvironment = new() { ["TSWB_FIXTURE_REVISION"] = "tsw-fixture-1.sample.2" } })).Results);
        Assert.Equal(ScenarioOutcomes.Error, drifted.Outcome); Assert.Contains($"Fixture-only contract of '{FixtureId}' changed since scenario '{ExternalId}' was bound", drifted.Reason); Assert.Contains("revision tsw-fixture-1.sample.2", drifted.Reason);
        Assert.Empty(drifted.Checkpoints); Assert.True(drifted.CleanupSucceeded);
    }

    [Fact]
    public async Task UnboundCheckpointsAndDirectExecutionAreRefusedAtControllerAndHostBoundaries()
    {
        string root = Folder();
        var runner = new ScenarioRunner();
        var scenario = (await runner.DiscoverAsync(Target, [FileIn(root, Document())])).Single(s => s.Id == ExternalId);

        // Controller execution boundary: a descriptor that bypassed merge validation.
        var forged = scenario with { Steps = [.. scenario.Steps.Take(2), new(ScenarioStep.Checkpoint, "made-up")] };
        var refused = Assert.Single((await runner.RunAsync(Target, [forged], new RunOptions(root, ["80x25"]))).Results);
        Assert.Equal(ScenarioOutcomes.Error, refused.Outcome);
        Assert.Contains($"Scenario '{ExternalId}': checkpoint 'made-up' does not bind", refused.Reason); Assert.Contains($"'{FixtureId}'", refused.Reason);
        Assert.Empty(refused.Checkpoints); Assert.True(refused.CleanupSucceeded);

        // Host boundary: a session driven without the controller's validation.
        await using (var session = new TargetSession(Target, FixtureId, "80x25", default))
        {
            await session.ConnectAsync();
            await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started); // waits for the fixture's deferred readiness
            var unknown = await Assert.ThrowsAsync<FixtureBindingException>(() =>
                session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, checkpoint: "made-up"));
            Assert.Contains("'made-up'", unknown.Message); Assert.Contains($"'{FixtureId}'", unknown.Message);
            Assert.Contains("advertised: idle, label-ready, never-true, note-open", unknown.Message);
            Assert.Equal("idle", (await session.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, checkpoint: "idle")).Capture!.Checkpoint);
            await session.SendAsync(Protocol.Operations.Complete, Protocol.States.Completed);
        }
        await using (var direct = new TargetSession(Target, FixtureId, "80x25", default))
        {
            await direct.ConnectAsync();
            await direct.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
            var bare = await Assert.ThrowsAsync<FixtureBindingException>(() => direct.SendAsync(Protocol.Operations.Complete, Protocol.States.Completed));
            Assert.Contains("fixture-only registration, not a runnable scenario", bare.Message);
        }
        // An ordinary compiled scenario keeps unadvertised checkpoint names (legacy behaviour unchanged at the host).
        await using (var legacy = new TargetSession(Target, "sample/dialog/basic", "80x25", default))
        {
            await legacy.ConnectAsync();
            await legacy.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
            Assert.NotNull((await legacy.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, checkpoint: "anything-authored")).Capture);
        }
    }

    // ── CLI ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CliListDescribeSelectedGroupAndAllUseRunnableDiscovery()
    {
        string root = Folder(), source = FileIn(root, Document()), project = Path.Combine(root, "notes.twproj");
        new WorkbenchProject { Target = Target, OutputRoot = root, ScenarioFiles = [source], ScreenSizes = ["80x25"] }.Save(project);
        var output = new StringWriter(); var error = new StringWriter();

        Assert.Equal(0, await Cli.RunAsync(["list", project], output, error));
        Assert.Contains(ExternalId, output.ToString()); Assert.Contains("sample/dialog/basic", output.ToString());
        Assert.DoesNotContain(FixtureId, output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.RunAsync(["groups", project], output, error));
        Assert.DoesNotContain("sample/fixture-only", output.ToString()); Assert.Contains("external\t1", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.RunAsync(["describe", project, "--scenario", FixtureId + "," + ExternalId], output, error));
        string described = output.ToString();
        Assert.Contains($"Role: fixture-only registration (not directly runnable); contract v1, revision {Revision}", described);
        Assert.Contains("Checkpoint expectations: idle, label-ready, never-true, note-open", described);
        Assert.Contains("Actions: begin-deferred-label, open-note", described); Assert.Contains("Points: label", described);
        Assert.Contains("Contract fingerprint: fixture-contract/1:", described);
        Assert.Contains("Fixture-only contract: fixture-contract/1:", described);

        error.GetStringBuilder().Clear();
        Assert.Equal(2, await Cli.RunAsync(["run", project, "--scenario", FixtureId], output, error));
        Assert.Contains("fixture-only registration, not a runnable scenario", error.ToString());

        string selected = Path.Combine(root, "selected.json"), group = Path.Combine(root, "group.json"), all = Path.Combine(root, "all.json");
        Assert.Equal(0, await Cli.RunAsync(["run", project, "--scenario", ExternalId, "--results", selected], output, error));
        Assert.Equal(new[] { ExternalId }, Ids(selected));
        Assert.Equal(0, await Cli.RunAsync(["run", project, "--group", "external", "--results", group], output, error));
        Assert.Equal(new[] { ExternalId }, Ids(group));
        Assert.True(await Cli.RunAsync(["run", project, "--all", "--results", all], output, error) == 0, error.ToString());
        var runnable = ScenarioSelection.Resolve(await new ScenarioRunner().DiscoverAsync(Target, [source]), [], null, true).Select(s => s.Id).ToArray();
        Assert.Equal(runnable, Ids(all));
        Assert.Contains(ExternalId, Ids(all)); Assert.DoesNotContain(FixtureId, Ids(all));
        Assert.DoesNotContain("selftest/failure/wrong-expectations", Ids(all));

        static string[] Ids(string results)
        {
            var batch = JsonSerializer.Deserialize<BatchResult>(File.ReadAllText(results), ScenarioRunner.Json)!;
            Assert.All(batch.Results, r => Assert.True(r.Outcome == ScenarioOutcomes.Passed, $"{r.ScenarioId}: {r.Reason}"));
            return batch.Results.Select(r => r.ScenarioId).Distinct().ToArray();
        }
    }

    // ── TUI (NullDriver) ────────────────────────────────────────────────────

    [Fact]
    public async Task TuiListsMarksFiltersAndRunsOnlyRunnableEntries()
    {
        string root = Folder(), source = FileIn(root, Document()), path = Path.Combine(root, "ui.twproj");
        new WorkbenchProject { Target = Target, OutputRoot = root, ScenarioFiles = [source], ScreenSizes = ["80x25"], SelectedIds = [ExternalId] }.Save(path);
        using var driver = new TSharpWorkbench.Tests.Infrastructure.DriverScope(80, 25);
        using var app = new WorkbenchApp("", root, new("", root, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
        var bridge = new UiBridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                async Task Until(Func<bool> condition)
                {
                    while (true)
                    {
                        bool met = false;
                        await bridge.Invoke(() => met = condition()).WaitAsync(deadline.Token);
                        if (met) return;
                        await Task.Delay(20, deadline.Token);
                    }
                }
                await Until(() => app.Project is not null);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
                await Until(() => app.Project is { Busy: false } p && p.TestList.Strings.Contains("External notes flow"));
                await bridge.Invoke(() =>
                {
                    var workspace = app.Project!;
                    var list = workspace.TestList;
                    Assert.DoesNotContain("Notes fixture (fixture-only)", list.Strings);
                    Assert.Contains("Basic dialog", list.Strings);
                    Assert.Equal(new[] { ExternalId }, list.MarkedIds);
                    Assert.Throws<ArgumentException>(() => list.Toggle(FixtureId)); // not in the catalog, so it cannot be marked
                    list.Filter(null, "notes");
                    Assert.Equal(1, list.range); Assert.Equal(ExternalId, list.Current!.Id);
                    list.Filter("external", "");
                    Assert.Equal(1, list.range);
                    list.Filter(null, "");
                    var markAll = new TEvent { What = Constants.Events.evCommand }; markAll.message.command = WorkbenchDialog.MarkAll;
                    workspace.HandleEvent(ref markAll);
                    Assert.Contains(ExternalId, list.MarkedIds); Assert.DoesNotContain(FixtureId, list.MarkedIds);
                    Assert.DoesNotContain("selftest/failure/wrong-expectations", list.MarkedIds);
                    list.ClearMarks(); list.RestoreMarks([ExternalId, FixtureId]);
                    Assert.Equal(new[] { ExternalId }, list.MarkedIds);
                    var accept = new TEvent { What = Constants.Events.evCommand }; accept.message.command = Constants.Views.cmOK;
                    workspace.PutEvent(ref accept);
                    var run = new TEvent { What = Constants.Events.evCommand }; run.message.command = WorkbenchDialog.Run;
                    workspace.HandleEvent(ref run);
                });
                await Until(() => Directory.GetFiles(root, "run.json", SearchOption.AllDirectories).Length > 0 && app.Project is { Busy: false });
                using var runJson = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(root, "run.json", SearchOption.AllDirectories))));
                var results = runJson.RootElement.GetProperty("results");
                Assert.Equal(1, results.GetArrayLength());
                Assert.Equal(ExternalId, results[0].GetProperty("scenarioId").GetString());
                Assert.Equal("Passed", results[0].GetProperty("outcome").GetString());
            }
            catch (Exception ex) { failure = ex; }
            finally { await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Constants.Views.cmQuit); }); }
        });
        AppLifecycleGuard.Run(app); await worker; Assert.Null(failure);
    }

    private sealed class UiBridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal UiBridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Constants.Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7dda, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Constants.Events.evCommand && ev.message.command == 0x7dda && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); }
            else base.HandleEvent(ref ev);
        }
    }
}
