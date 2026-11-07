using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Generic TestTargetHost, Protocol v1 checkpoints, input acknowledgement, semantic evidence and
// assertions against the TSharpVision-owned sample target. Process-level tests launch the real compiled sample target;
// nothing here uses Commander.
[Collection("NonParallel")]
public sealed class TestWorkbenchHostTests
{
    private const string DialogId = "sample/dialog/basic", MenuId = "sample/menu/navigation", SelectionId = "sample/menu/selection",
        StatusId = "sample/status/context", ResizeId = "sample/screen/resize", FixtureId = "sample/fixture/window", BurstId = "selftest/input/queued-burst",
        FailureId = "selftest/failure/wrong-expectations", BlockedId = "selftest/failure/blocked-ui", CrashId = "selftest/failure/target-crash";

    internal static string SampleTarget
    {
        get
        {
            // The owning test build stages the current target deployment beside this assembly.
            string path = Path.Combine(AppContext.BaseDirectory, "SampleTarget",
                "TSharpWorkbench.SampleTarget" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            Assert.True(File.Exists(path), "Sample target not built: " + path);
            return path;
        }
    }

    // ── contracts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtendedV1MessagesRoundTripWithCheckpointsInputEvidenceAndCapture()
    {
        var cells = Convert.ToBase64String(new ScreenSnapshot(1, 1, [new('A', 0x1f)]).Serialize());
        var inspection = new CheckpointInspection(null, new MenuState(true, 1, null, []), null, ["view-tree: inspector failed (X: y)"]);
        var reply = new Reply("id", Protocol.States.Captured, Protocol.Schema, "instance",
            Target: new("Target", "1.0", Guid.Empty.ToString(), "fw", "testing", "NullDriver", 1, 1),
            Capture: new("menu-open", cells, 1, 1, 3, DateTimeOffset.UnixEpoch, new("TSharpVision.TMenuBox", false, 1, 2, 3, 4, null, 0), inspection),
            Interaction: new("key", "kbAltF", "routing", 7, Protocol.Acknowledgements.Settled));
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, reply, default); stream.Position = 0;
        var back = await Protocol.ReadAsync<Reply>(stream, default);
        Assert.Equal(reply.Capture! with { Inspection = null }, back.Capture! with { Inspection = null });
        Assert.Equal(inspection.Errors, back.Capture!.Inspection!.Errors); Assert.Equal(1, back.Capture.Inspection.Menu!.Depth);
        Assert.Equal(reply.Target, back.Target); Assert.Equal(reply.Interaction, back.Interaction);
        Assert.Equal(Protocol.Schema, back.Schema);
        Assert.Same(back, TargetSession.Validate(back, "id", "instance", Protocol.States.Captured));
        var request = new Request(Protocol.Version, Protocol.Schema, "i", "r", "s", Protocol.Operations.Checkpoint, "x", "a", Evidence: ["menu", "status"]);
        Assert.True(Protocol.Authenticate(request, "i", "s"));
        Assert.False(Protocol.Authenticate(request with { Evidence = ["pixels"] }, "i", "s"));
        Assert.True(Protocol.Authenticate(request with { Input = new("resize", "100x30") }, "i", "s"));
        Assert.False(Protocol.Authenticate(request with { Input = new("mouse", "x") }, "i", "s"));
        // A target-side bounded wait is reported as a timeout, not as a generic protocol error.
        var timeout = new Reply("id", Protocol.States.Rejected, Protocol.Schema, "instance", Reason: Protocol.TimeoutPrefix + "UI thread did not respond.");
        Assert.Equal("UI thread did not respond.", Assert.Throws<TimeoutException>(() => TargetSession.Validate(timeout, "id", "instance", Protocol.States.Routed)).Message);
    }

    [Fact]
    public void EarlierWireContractsAreRefusedInBothDirections()
    {
        // Exactly what the older binaries put on the wire: no Schema field.
        var oldRequest = JsonSerializer.Deserialize<Request>("""{"Version":1,"Instance":"i","Id":"r","Secret":"s","Operation":"Handshake","Scenario":null}""")!;
        Assert.Null(oldRequest.Schema);
        Assert.False(Protocol.Authenticate(oldRequest, "i", "s"));
        // Binaries: same v1, previous schema identity.
        Assert.False(Protocol.Authenticate(oldRequest with { Schema = "tsharpvision.testing/v1+checkpoints.1" }, "i", "s"));
        var oldReply = JsonSerializer.Deserialize<Reply>("""{"Id":"r","State":"Ready","ProtocolVersion":1,"Instance":"i","Driver":"NullDriver","Width":80,"Height":25}""")!;
        Assert.Equal(1, oldReply.ProtocolVersion);
        Assert.Equal("Target protocol schema mismatch.", Assert.Throws<IOException>(() => TargetSession.Validate(oldReply, "r", "i", "Ready")).Message);
        Assert.Throws<IOException>(() => TargetSession.Validate(oldReply with { Schema = "tsharpvision.testing/v1+checkpoints.1" }, "r", "i", "Ready"));
        Assert.Throws<IOException>(() => TargetSession.Validate(oldReply with { Schema = Protocol.Schema }, "other", "i", "Ready"));
    }

    [Fact]
    public void ScenarioRegistrationValidatesKeysNamesGridsEvidenceAndAssertions()
    {
        var scenario = new TestScenario("x/y", "X", "x", "d").Checkpoint("initial").Key("kbAltF").Action("open-note", _ => { })
            .Resize(100, 30).Checkpoint("after", null, null, ["view-tree"], UiAssert.MenuDepth("depth", 1), UiAssert.ScreenContains("text", "File"));
        Assert.Equal(new[] { "checkpoint", "key", "action", "resize", "checkpoint" }, scenario.Descriptor.Steps.Select(s => s.Kind));
        // Evidence needed by assertions is requested automatically.
        Assert.Equal(new[] { "view-tree", "menu" }, scenario.Descriptor.Steps[^1].Evidence);
        Assert.Equal(ScenarioSelection.Sizes, scenario.Descriptor.Grid.Defaults);
        Assert.Throws<ArgumentException>(() => scenario.Key("kbNoSuchKey"));
        Assert.Throws<ArgumentException>(() => scenario.Checkpoint("initial"));
        Assert.Throws<ArgumentException>(() => scenario.Checkpoint("Menu--Open"));
        Assert.Throws<ArgumentException>(() => scenario.Action("open-note", _ => { }));
        Assert.Throws<ArgumentException>(() => scenario.Resize(300, 30));
        Assert.Throws<ArgumentException>(() => scenario.Checkpoint("dup", null, null, null, UiAssert.MenuDepth("a", 0), UiAssert.MenuDepth("a", 1)));
        Assert.Throws<ArgumentException>(() => scenario.Checkpoint("bad-evidence", null, null, ["pixels"]));
        Assert.Throws<ArgumentException>(() => new TestScenario("a", "a", "a", "a").Grid(100, 30).Grid(10, 5));
        Assert.Throws<ArgumentException>(() => new TestScenario("a", "a", "a", "a", "80x25").Grid(100, 30)); // default outside range
        Assert.Throws<ArgumentException>(() => new TestScenario("a", "a", "a", "a").Kind("gallery"));
        Assert.Throws<ArgumentException>(() => new TestScenario("a", "a", "a", "a").Metadata("Bad Key", "v"));
        Assert.True(TestKeys.TryResolve("kbEsc", out ushort esc)); Assert.Equal(Constants.Keys.kbEsc, esc);
        Assert.Equal("kbEsc", TestKeys.NameOf(esc));
        Assert.Equal(Constants.Events.evKeyDown, TestKeys.KeyDown(esc).What);
    }

    // ── real sample target, end to end ─────────────────────────────────────

    [Fact]
    public async Task SampleTargetCapturesEverySuccessfulScenarioAtStandardGridsWithEvidenceAndAssertions()
    {
        string root = Scratch();
        int tempRootsBefore = TempRootCount();
        try
        {
            var runner = new ScenarioRunner();
            var catalog = await runner.DiscoverAsync(SampleTarget);
            Assert.Equal(new[] { DialogId, MenuId, SelectionId, StatusId, ResizeId, FixtureId, "sample/fixture/path", BurstId, FailureId, BlockedId, CrashId, "selftest/fixture/deferred-label",
                "selftest/input/keyboard-state", "selftest/input/mouse-lifecycle", "selftest/input/mixed-dialog", "selftest/failure/input-off-screen" }, catalog.Select(s => s.Id));
            Assert.Equal(new[] { "sample", "sample/dialog", "sample/menu", "sample/status", "sample/screen", "sample/fixture", "selftest", "selftest/input", "selftest/failure", "selftest/fixture" }, Cli.Groups(catalog));
            Assert.True(catalog.Single(s => s.Id == FixtureId).Safety!.FixtureFiles);
            var selected = ScenarioSelection.Resolve(catalog, [], null, true);
            // Deliberate failure demonstrations are excluded from bulk selection.
            Assert.Equal(catalog.Where(s => s.ExpectedResult == ScenarioOutcomes.Passed).Select(s => s.Id), selected.Select(s => s.Id));
            var batch = await runner.RunAsync(SampleTarget, selected, new RunOptions(root));
            Assert.True(batch.ExitCode == 0, string.Join("\n", batch.Results.Select(r => $"{r.ScenarioId} {r.Size}: {r.Outcome} {r.Reason}")));
            Assert.Equal(selected.Length * 2, batch.Results.Length); // scenario defaults = the standard grids
            Assert.Empty(Directory.GetDirectories(batch.RunRoot));
            int checkpoints = batch.Results.Sum(r => r.Checkpoints.Length);
            Assert.Equal(selected.Sum(s => s.Steps.Count(step => step.Kind == ScenarioStep.Checkpoint)) * 2, checkpoints);
            Assert.Equal(checkpoints * 3 + 2, Directory.GetFiles(batch.RunRoot).Length);
            using (var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, "run.json"))))
            {
                var r = run.RootElement;
                Assert.Equal(batch.Results.Length, r.GetProperty("passed").GetInt32());
                Assert.Equal(0, r.GetProperty("assertions").GetProperty("fail").GetInt32());
                Assert.True(r.GetProperty("assertions").GetProperty("pass").GetInt32() > 100);
                Assert.Contains("Layer B", r.GetProperty("layers").GetProperty("driverOutput").GetString());
                Assert.False(string.IsNullOrEmpty(r.GetProperty("controller").GetProperty("environment").GetProperty("os").GetString()));
            }
            string report = File.ReadAllText(Path.Combine(batch.RunRoot, "report.md"));
            Assert.Contains("Result: **PASS**", report); Assert.Contains(LogicalPng.Renderer, report); Assert.Contains("Layer B", report);

            var screens = new Dictionary<string, ScreenSnapshot>();
            var metadata = new Dictionary<string, JsonElement>();
            foreach (var result in batch.Results)
            {
                Assert.True(result.TargetExited && result.FixtureIntegrityVerified && result.CleanupSucceeded);
                var expected = catalog.Single(s => s.Id == result.ScenarioId).Steps.Where(s => s.Kind == ScenarioStep.Checkpoint).Select(s => s.Name);
                Assert.Equal(expected, result.Checkpoints.Select(c => c.Checkpoint));
                foreach (var c in result.Checkpoints)
                {
                    Assert.All(c.Assertions, a => Assert.True(a.Outcome == AssertionOutcomes.Pass, $"{result.ScenarioId}/{c.Checkpoint}/{a.Id}: {a.Actual} {a.Explanation}"));
                    Assert.Empty(c.InspectionErrors);
                    Assert.Equal(ArtifactNames.Stem(result.ScenarioId, c.Checkpoint, c.Size, result.Size) + ".png", c.Png);
                    byte[] cells = File.ReadAllBytes(Path.Combine(batch.RunRoot, c.Cells)), png = File.ReadAllBytes(Path.Combine(batch.RunRoot, c.Png));
                    Assert.Equal(c.CellsHash, ScreenSnapshot.Hash(cells)); Assert.Equal(c.PngHash, ScreenSnapshot.Hash(png));
                    var snapshot = ScreenSnapshot.Deserialize(cells);
                    var (w, h) = ScenarioSelection.ParseSize(c.Size);
                    Assert.Equal((w, h), (snapshot.Width, snapshot.Height));
                    Assert.Equal(png, LogicalPng.Encode(snapshot)); // PNG is exactly the logical rendering of the TVS1 cells
                    Assert.Equal(w * LogicalPng.CellWidth, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
                    var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, c.Metadata))).RootElement.Clone();
                    Assert.Equal(result.ScenarioId, json.GetProperty("scenarioId").GetString());
                    Assert.Equal(c.Checkpoint, json.GetProperty("checkpointId").GetString());
                    Assert.Equal((c.Size, result.Size), (json.GetProperty("size").GetString(), json.GetProperty("initialSize").GetString()));
                    Assert.Equal("logical", json.GetProperty("captureLayer").GetString());
                    Assert.Equal("NullDriver", json.GetProperty("driver").GetString());
                    Assert.Equal(Protocol.Schema, json.GetProperty("schema").GetString());
                    Assert.Equal(LogicalPng.Renderer, json.GetProperty("renderer").GetString());
                    Assert.Equal("TSharpWorkbench.SampleTarget", json.GetProperty("target").GetProperty("assembly").GetString());
                    Assert.False(string.IsNullOrEmpty(json.GetProperty("tsharpVisionVersion").GetString()));
                    Assert.False(string.IsNullOrEmpty(json.GetProperty("testingVersion").GetString()));
                    Assert.Equal(c.CellsHash, json.GetProperty("cellsSha256").GetString());
                    Assert.Equal(ScreenSnapshot.Hash(LogicalPng.Raster(snapshot)), json.GetProperty("pixelsSha256").GetString());
                    foreach (var interaction in json.GetProperty("interactionsSincePreviousCheckpoint").EnumerateArray())
                        Assert.Equal(Protocol.Acknowledgements.Settled, interaction.GetProperty("acknowledgement").GetString());
                    screens[$"{result.ScenarioId}|{c.Checkpoint}|{result.Size}"] = snapshot;
                    metadata[$"{result.ScenarioId}|{c.Checkpoint}|{result.Size}"] = json;
                }
            }

            foreach (string size in ScenarioSelection.Sizes)
            {
                ScreenSnapshot S(string id, string checkpoint) => screens[$"{id}|{checkpoint}|{size}"];
                JsonElement M(string id, string checkpoint) => metadata[$"{id}|{checkpoint}|{size}"];
                JsonElement Surface(string id, string checkpoint) => M(id, checkpoint).GetProperty("surface");
                int last = S(MenuId, "initial").Height - 1;

                // Whole-screen capture with no dialog or window at all.
                Assert.Equal("TSharpWorkbench.SampleTarget.SampleApp", Surface(MenuId, "initial").GetProperty("topView").GetString());
                Assert.Contains("File", Row(S(MenuId, "initial"), 0));
                Assert.Contains("F4 New note", Row(S(MenuId, "initial"), last));

                // Menu captured while TMenuView.Execute's modal loop is running; closing restores the exact screen.
                Assert.Equal("TSharpVision.TMenuBox", Surface(MenuId, "menu-open").GetProperty("topView").GetString());
                Assert.Contains("Dialog...", Text(S(MenuId, "menu-open"))); Assert.DoesNotContain("Dialog...", Text(S(MenuId, "initial")));
                Assert.Contains("Cascade", Text(S(MenuId, "submenu-open"))); Assert.DoesNotContain("Cascade", Text(S(MenuId, "next-menu")));
                Assert.Equal(Bytes(S(MenuId, "next-menu")), Bytes(S(MenuId, "submenu-closed")));
                Assert.Equal(Bytes(S(MenuId, "initial")), Bytes(S(MenuId, "menu-closed")));
                Assert.Equal(Bytes(S(MenuId, "initial")), Bytes(S(SelectionId, "restored")));

                // Menu metadata agrees with the cells: the selected entry's text is at its rectangle and highlighted.
                var menu = M(SelectionId, "submenu").GetProperty("inspection").GetProperty("menu");
                Assert.Equal(2, menu.GetProperty("depth").GetInt32());
                var open = menu.GetProperty("open");
                Assert.Equal("Arrange", open[1].GetProperty("parentItem").GetString());
                var tile = open[1].GetProperty("items").EnumerateArray().Single(i => i.GetProperty("selected").GetBoolean());
                Assert.Equal("Tile", tile.GetProperty("text").GetString());
                var rect = tile.GetProperty("screen");
                Assert.Equal("Tile", Row(S(SelectionId, "submenu"), rect.GetProperty("y").GetInt32()).Substring(rect.GetProperty("x").GetInt32() + 1, 4));
                var plain = open[1].GetProperty("items").EnumerateArray().Single(i => i.TryGetProperty("text", out var x) && x.GetString() == "Cascade");
                Assert.NotEqual(tile.GetProperty("attribute").GetInt32(), plain.GetProperty("attribute").GetInt32());
                var print = M(SelectionId, "repeated-down").GetProperty("inspection").GetProperty("menu").GetProperty("open")[0].GetProperty("items")
                    .EnumerateArray().Single(i => i.TryGetProperty("text", out var x) && x.GetString() == "Print"); // separators have no text
                Assert.True(print.GetProperty("disabled").GetBoolean()); Assert.False(print.GetProperty("commandEnabled").GetBoolean());
                Assert.Equal("P", print.GetProperty("hotChar").GetString());
                // The same moment is also captured as a view tree when requested: the menu box is the modal view.
                var tree = M(SelectionId, "submenu").GetProperty("inspection").GetProperty("viewTree");
                Assert.False(string.IsNullOrEmpty(tree.GetProperty("modalPath").GetString()));

                // Real modal dialog, Tab/Shift+Tab focus changes, Esc restores the desktop; focus metadata = assertion evidence.
                Assert.Equal("TSharpVision.TDialog", Surface(DialogId, "dialog-open").GetProperty("topView").GetString());
                Assert.Equal("TSharpVision.TInputLine", Surface(DialogId, "dialog-open").GetProperty("focused").GetString());
                Assert.Equal("TSharpVision.TButton", Surface(DialogId, "focus-cancel").GetProperty("focused").GetString());
                Assert.Equal(Bytes(S(DialogId, "dialog-open")), Bytes(S(DialogId, "focus-back")));
                Assert.Equal(Bytes(S(DialogId, "initial")), Bytes(S(DialogId, "dialog-closed")));

                // Status line follows the application help context; model, hint and cells agree.
                var status = M(StatusId, "note-status").GetProperty("inspection").GetProperty("status");
                Assert.Equal(1000, status.GetProperty("helpContext").GetInt32());
                Assert.Equal("Note context", status.GetProperty("hint").GetString());
                Assert.Equal(Row(S(StatusId, "note-status"), last).TrimEnd(), status.GetProperty("observedRow").GetString());
                Assert.All(status.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("drawn").GetBoolean()),
                    i => Assert.Equal("match", i.GetProperty("observed").GetString()));
                Assert.Contains("Desktop context", Row(S(StatusId, "desktop-status"), last));
                Assert.Equal(Bytes(S(StatusId, "desktop-status")), Bytes(S(StatusId, "desktop-restored")));

                // Resize: later checkpoints carry the current grid in cells, metadata and names.
                Assert.Equal((100, 30), (S(ResizeId, "resized").Width, S(ResizeId, "resized").Height));
                Assert.Equal((60, 20), (S(ResizeId, "minimum").Width, S(ResizeId, "minimum").Height));
                Assert.Contains("Close note", Row(S(ResizeId, "resized"), 29));
                var resize = M(ResizeId, "resized").GetProperty("interactionsSincePreviousCheckpoint")[0];
                Assert.Equal(("resize", "100x30"), (resize.GetProperty("kind").GetString(), resize.GetProperty("name").GetString()));
            }
            // Deterministic across processes and scenarios: the same idle desktop is byte-identical.
            Assert.Equal(Bytes(screens[$"{MenuId}|initial|80x25"]), Bytes(screens[$"{DialogId}|initial|80x25"]));
            Assert.Equal(tempRootsBefore, TempRootCount()); // every per-execution temp root was removed
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task CustomGridsAndResizeProduceCurrentGridNamesAndRejectUnsupportedSizes()
    {
        string root = Scratch();
        try
        {
            var stdout = new StringWriter();
            Assert.Equal(0, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", $"{ResizeId},{StatusId}", "--size", "100x30,64x20",
                "--output", root, "--results", Path.Combine(root, "results.json")], stdout));
            string run = Directory.GetDirectories(root, "run-*").Single();
            var names = Directory.GetFiles(run).Select(Path.GetFileName).ToArray();
            Assert.Contains("sample-screen-resize--resized--100x30.png", names);              // started at 100x30: no suffix
            Assert.Contains("sample-screen-resize--minimum--60x20-from-100x30.png", names);
            Assert.Contains("sample-screen-resize--resized--100x30-from-64x20.png", names);
            Assert.Contains("sample-screen-resize--minimum--60x20-from-64x20.png", names);
            Assert.Contains("sample-status-context--note-status--64x20.json", names);
            using (var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "results.json"))))
                Assert.Equal(4, results.RootElement.GetProperty("results").GetArrayLength());
            // 64x20 status line (the scenario's declared minimum): items and hint just fit; model and cells agree.
            using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "sample-status-context--desktop-status--64x20.json"))))
            {
                var status = json.RootElement.GetProperty("inspection").GetProperty("status");
                Assert.Equal(64, status.GetProperty("screen").GetProperty("width").GetInt32());
                Assert.Equal("match", status.GetProperty("hintObserved").GetString());
                Assert.All(status.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("drawn").GetBoolean()),
                    i => Assert.Equal("match", i.GetProperty("observed").GetString()));
            }
            // Scenario-specific minimum (dialog needs 60x20): blocked, not silently resized; out-of-range grids are refused.
            var stderr = new StringWriter();
            Assert.Equal(1, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", DialogId, "--size", "40x12", "--output", root], new StringWriter(), stderr));
            Assert.Contains("Blocked", stderr.ToString()); Assert.Contains("60x20", stderr.ToString());
            Assert.Equal(2, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", DialogId, "--size", "300x30", "--output", root], new StringWriter(), new StringWriter()));
            Assert.Equal(2, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", DialogId, "--size", "80x25,x", "--output", root], new StringWriter(), new StringWriter()));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task DeliberatelyWrongAssertionsFailWithCheckpointExpectedAndActual()
    {
        string root = Scratch();
        try
        {
            var stdout = new StringWriter(); var stderr = new StringWriter();
            Assert.Equal(1, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", FailureId, "--size", "80x25", "--output", root, "--verbose",
                "--results", Path.Combine(root, "results.json")], stdout, stderr));
            string text = stderr.ToString();
            Assert.Contains("Failed: " + FailureId, text);
            Assert.Contains("wrong-focus", text); Assert.Contains("dialog-open", text); Assert.Contains("TInputLine", text);
            var batch = JsonSerializer.Deserialize<BatchResult>(File.ReadAllText(Path.Combine(root, "results.json")), ScenarioRunner.Json)!;
            var result = batch.Results.Single();
            Assert.Equal((ScenarioOutcomes.Failed, ScenarioOutcomes.Failed), (result.Outcome, result.ExpectedResult));
            // The UI itself was fine: the run completed, captured both checkpoints and cleaned up.
            Assert.True(result.TargetExited && result.CleanupSucceeded && result.Checkpoints.Length == 2);
            Assert.Equal(new[] { "wrong-focus", "wrong-title", "wrong-menu-depth" }, result.FailedAssertions.Select(a => a.Id));
            Assert.All(result.FailedAssertions, a => Assert.Equal(("dialog-open", "80x25"), (a.Checkpoint, a.Grid)));
            var warning = result.Checkpoints[0].Assertions.Single(a => a.Id == "shadow-without-attribute");
            Assert.Equal((AssertionOutcomes.Unsupported, AssertionSeverity.Warning), (warning.Outcome, warning.Severity));
            Assert.Equal(AssertionOutcomes.Pass, result.Checkpoints[0].Assertions.Single(a => a.Id == "dialog-is-modal").Outcome);
            string report = File.ReadAllText(batch.Report!);
            Assert.Contains("Result: **FAIL**", report); Assert.Contains("`wrong-title`", report); Assert.Contains("`dialog-open`", report);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ControllerChoosesCheckpointOrderAndCliListsDescribesAndRunsAGroup()
    {
        string root = Scratch();
        try
        {
            var runner = new ScenarioRunner();
            var menu = (await runner.DiscoverAsync(SampleTarget)).Single(s => s.Id == MenuId);
            // A different, controller-chosen sequence: names are free (unique), counts and order are not fixed. The controller
            // requests the evidence an assertion needs even when the step does not declare it.
            var custom = menu with { Steps = [new("key", "kbAltW"), new("checkpoint", "window-menu"), new("key", "kbEnter"),
                new("checkpoint", "arrange", Assertions: [UiAssert.SubmenuOpen("arrange-open", "Arrange")]), new("key", "kbEsc"), new("key", "kbEsc"), new("checkpoint", "done")] };
            var batch = await runner.RunAsync(SampleTarget, [custom], ["140x40"], root);
            var result = batch.Results.Single();
            Assert.True(result.Outcome == "Passed", result.Reason);
            Assert.Equal(new[] { "window-menu", "arrange", "done" }, result.Checkpoints.Select(c => c.Checkpoint));
            Assert.Equal(AssertionOutcomes.Pass, result.Checkpoints[1].Assertions.Single().Outcome);
            Assert.Contains("Cascade", Text(ScreenSnapshot.Deserialize(File.ReadAllBytes(Path.Combine(batch.RunRoot, result.Checkpoints[1].Cells)))));

            var stdout = new StringWriter();
            Assert.Equal(0, await Cli.RunAsync(["list", "--target", SampleTarget, "--verbose"], stdout));
            Assert.Contains("sample/screen/resize", stdout.ToString()); Assert.Contains("assert input-focused", stdout.ToString());
            stdout = new StringWriter();
            Assert.Equal(0, await Cli.RunAsync(["groups", "--target", SampleTarget], stdout));
            Assert.Contains("sample/menu\t2", stdout.ToString());
            stdout = new StringWriter();
            Assert.Equal(0, await Cli.RunAsync(["describe", "--target", SampleTarget, "--scenario", ResizeId], stdout));
            Assert.Contains("resize 100x30", stdout.ToString()); Assert.Contains("Grid: 60x20..200x100", stdout.ToString());
            Assert.Equal(0, await Cli.RunAsync(["run", "--target", SampleTarget, "--group", "sample/status", "--size", "standard", "--output", root, "--inspect", "all"], new StringWriter()));
            Assert.Equal(1, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", DialogId, "--size", "80x25", "--output", root, "--inspect", "none"], new StringWriter(), new StringWriter()));
            Assert.Equal(2, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", DialogId, "--output", root, "--inspect", "pixels"], new StringWriter(), new StringWriter()));
            Assert.Equal(2, await Cli.RunAsync(["run", "--target", SampleTarget, "--scenario", DialogId, "--output", root, "--bogus"], new StringWriter(), new StringWriter()));
            Assert.Equal(2, await Cli.RunAsync(["run", "--target", SampleTarget, "--group", "nope", "--output", root], new StringWriter(), new StringWriter()));
            Assert.Equal(3, Directory.GetDirectories(root, "run-*").Length);
            // --inspect none: semantic assertions become UNSUPPORTED and fail closed (never a guessed PASS).
            string noneRun = Directory.GetDirectories(root, "run-*").Single(d => File.Exists(Path.Combine(d, "sample-dialog-basic--dialog-open--80x25.json")));
            using var none = JsonDocument.Parse(File.ReadAllText(Path.Combine(noneRun, "sample-dialog-basic--dialog-open--80x25.json")));
            Assert.Contains(none.RootElement.GetProperty("assertions").EnumerateArray(), a => a.GetProperty("outcome").GetString() == AssertionOutcomes.Unsupported);
            Assert.Contains(none.RootElement.GetProperty("assertions").EnumerateArray(), a => a.GetProperty("outcome").GetString() == AssertionOutcomes.Pass); // cell assertions still decidable
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task SafetyDeclarationsGateExecutionButNeverElevate()
    {
        string root = Scratch();
        try
        {
            static ScenarioDescriptor Declared(string id, ScenarioSafety safety) =>
                new(id, id, "declares effects", "App", ScenarioGrid.Standard, [new(ScenarioStep.Checkpoint, "initial")], Safety: safety);
            var network = Declared("app/network", new(Network: true));
            var elevated = Declared("app/elevated", new(Elevation: true));
            var batch = await new ScenarioRunner().RunAsync("missing-target.exe", [network, elevated], new RunOptions(root, ["80x25"]));
            Assert.All(batch.Results, r => Assert.Equal(ScenarioOutcomes.Blocked, r.Outcome));
            Assert.Contains("network", batch.Results[0].Reason); Assert.Contains("--allow", batch.Results[0].Reason);
            Assert.Contains("never elevates", batch.Results[1].Reason);
            // Allowing the capability lets the run proceed to launch (which then fails: the declaration is not a sandbox).
            var allowed = await new ScenarioRunner().RunAsync("missing-target.exe", [network, elevated], new RunOptions(root, ["80x25"], Allow: ["network"]));
            Assert.Equal((ScenarioOutcomes.Error, ScenarioOutcomes.Blocked), (allowed.Results[0].Outcome, allowed.Results[1].Outcome));
            await Assert.ThrowsAsync<ArgumentException>(() => new ScenarioRunner().RunAsync("x", [network], new RunOptions(root, ["80x25"], Allow: ["elevation"])));
        }
        finally { Delete(root); }
    }

    // ── protocol behaviour against the real host ───────────────────────────

    [Fact]
    public async Task WrongSecretAndForeignSchemaAreRejectedAndTheTargetExits()
    {
        foreach (var tamper in new Func<Request, Request>[] { r => r with { Secret = new string('0', 64) }, r => r with { Schema = "tsharpvision.testing/v1+checkpoints.1" } })
        {
            await using var target = await RawTarget.StartAsync(DialogId);
            var reply = await target.SendRawAsync(tamper(target.Request(Protocol.Operations.Handshake)));
            Assert.Equal(Protocol.States.Rejected, reply.State);
            Assert.NotEqual(0, await target.WaitForExitAsync());
        }
    }

    [Fact]
    public async Task HostEnforcesScenarioStateRegisteredNamesAndUniqueCheckpoints()
    {
        await using var target = await RawTarget.StartAsync(MenuId, operationMs: 500);
        Reply hello = await target.SendAsync(Protocol.Operations.Handshake);
        Assert.Equal(Protocol.States.Ready, hello.State);
        Assert.Equal(("NullDriver", 80, 25), (hello.Target!.Driver, hello.Target.Width, hello.Target.Height));
        Assert.Equal(Protocol.States.Rejected, (await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "early")).State);
        Assert.Equal(Protocol.States.Rejected, (await target.SendRawAsync(target.Request(Protocol.Operations.Start) with { Scenario = DialogId })).State);
        Assert.Equal(Protocol.States.Started, (await target.SendAsync(Protocol.Operations.Start)).State);
        Assert.Equal(Protocol.States.Captured, (await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "initial")).State);
        Assert.Contains("Duplicate", (await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "initial")).Reason);
        Assert.Contains("Unknown key", (await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbBogus"))).Reason);
        Assert.Contains("Unregistered", (await target.SendAsync(Protocol.Operations.Input, input: new("action", "open-note"))).Reason);
        Assert.Contains("Invalid resize grid", (await target.SendAsync(Protocol.Operations.Input, input: new("resize", "10x5"))).Reason);
        // Bounded expectation: menu-open requires an executing TMenuBox; nothing opened it yet.
        var clock = Stopwatch.StartNew();
        var unmet = await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "menu-open");
        Assert.Equal(Protocol.States.Rejected, unmet.State); Assert.Contains("expectation not met", unmet.Reason);
        Assert.StartsWith(Protocol.TimeoutPrefix, unmet.Reason);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
        // The failed name was not consumed; real input makes it succeed.
        var routed = await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbAltF"));
        Assert.Equal((Protocol.States.Routed, Protocol.Acknowledgements.Settled), (routed.State, routed.Interaction!.Acknowledgement));
        var open = await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "menu-open", evidence: ["menu"]);
        Assert.Equal(Protocol.States.Captured, open.State);
        Assert.Equal("TSharpVision.TMenuBox", open.Capture!.Surface!.TopView);
        Assert.Equal(1, open.Capture.Inspection!.Menu!.Depth);
        Assert.True(open.Capture.Observations >= 3);
        Assert.Equal(Protocol.States.Routed, (await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbEsc"))).State);
        Assert.Equal(Protocol.States.Completed, (await target.SendAsync(Protocol.Operations.Complete)).State);
        Assert.Equal(Protocol.States.ShutdownAccepted, (await target.SendAsync(Protocol.Operations.Shutdown)).State);
        Assert.Equal(0, await target.WaitForExitAsync());
    }

    [Fact]
    public async Task InputIsAcknowledgedForRepeatedKeysNestedMenusAndImmediateCheckpoints()
    {
        await using var target = await RawTarget.StartAsync(SelectionId);
        await target.SendAsync(Protocol.Operations.Handshake);
        await target.SendAsync(Protocol.Operations.Start);
        long previous = 0;
        async Task<MenuState> Key(string key)
        {
            var reply = await target.SendAsync(Protocol.Operations.Input, input: new("key", key));
            Assert.Equal(Protocol.States.Routed, reply.State);
            Assert.Equal(Protocol.Acknowledgements.Settled, reply.Interaction!.Acknowledgement);
            Assert.Equal(previous + 1, reply.Interaction.Sequence); previous = reply.Interaction.Sequence;
            // A checkpoint immediately after the input, without any expectation predicate.
            var capture = await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "after-" + previous, evidence: ["menu"]);
            Assert.Equal(Protocol.States.Captured, capture.State);
            return capture.Capture!.Inspection!.Menu!;
        }
        Assert.Equal((true, 0, "File"), Selected(await Key("kbF10"), 0));
        Assert.Equal((true, 1, "New note"), Selected(await Key("kbDown")));
        // Repeated identical keys: each one is consumed exactly once and in order.
        Assert.Equal("Dialog...", Selected(await Key("kbDown")).Text);
        Assert.Equal("Print", Selected(await Key("kbDown")).Text);
        Assert.Equal("Exit", Selected(await Key("kbDown")).Text);
        Assert.Equal("New note", Selected(await Key("kbDown")).Text); // wraps
        Assert.Equal("Exit", Selected(await Key("kbUp")).Text);
        // Mnemonic of a bar entry from inside the File drop-down, then a nested submenu.
        Assert.Equal((true, 1, "Arrange"), Selected(await Key("kbAltW")));
        var nested = await Key("kbEnter");
        Assert.Equal((2, "Arrange", "Tile"), (nested.Depth, nested.Open[1].ParentItem, nested.Open[1].SelectedText));
        Assert.Equal(1, (await Key("kbEsc")).Depth);
        var closed = await Key("kbEsc");
        Assert.Equal((false, 0), (closed.Active, closed.Depth));
        Assert.Equal(Protocol.States.Completed, (await target.SendAsync(Protocol.Operations.Complete)).State);
        Assert.Equal(Protocol.States.ShutdownAccepted, (await target.SendAsync(Protocol.Operations.Shutdown)).State);
        Assert.Equal(0, await target.WaitForExitAsync());

        static (bool Active, int Depth, string? Text) Selected(MenuState menu, int? level = null)
        {
            var view = level == 0 ? menu.Bar! : menu.Open[^1];
            return (menu.Active, menu.Depth, view.SelectedText);
        }
    }

    [Fact]
    public async Task KeysQueuedBehindABackgroundBurstAreStillAcknowledgedAndProcessedOnce()
    {
        for (int round = 0; round < 3; round++)
        {
            await using var target = await RawTarget.StartAsync(BurstId);
            await target.SendAsync(Protocol.Operations.Handshake);
            await target.SendAsync(Protocol.Operations.Start);
            var action = await target.SendAsync(Protocol.Operations.Input, input: new("action", "start-burst"));
            Assert.Equal(("action", Protocol.Acknowledgements.Settled), (action.Interaction!.Kind, action.Interaction.Acknowledgement));
            var key = await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbF3"));
            Assert.Equal(Protocol.Acknowledgements.Settled, key.Interaction!.Acknowledgement);
            var capture = await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "dialog-after-burst", evidence: ["view-tree"]);
            var tree = capture.Capture!.Inspection!.ViewTree!;
            Assert.Equal("sample-dialog", AssertionEvaluator.All(tree.Root).Single(n => n.Path == tree.ModalPath).Id);
            Assert.Equal("name-input", AssertionEvaluator.All(tree.Root).Single(n => n.Path == tree.FocusedPath).Id);
            await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbEsc"));
            Assert.Equal(Protocol.States.Captured, (await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "all-ticks")).State);
            Assert.Equal(Protocol.States.Completed, (await target.SendAsync(Protocol.Operations.Complete)).State);
            await target.SendAsync(Protocol.Operations.Shutdown);
            Assert.Equal(0, await target.WaitForExitAsync());
        }
    }

    [Fact]
    public async Task ResizeIsRealRejectedWhenAModalViewSwallowsItAndBlockedUiTimesOutBounded()
    {
        await using (var target = await RawTarget.StartAsync(DialogId))
        {
            await target.SendAsync(Protocol.Operations.Handshake);
            await target.SendAsync(Protocol.Operations.Start);
            var resized = await target.SendAsync(Protocol.Operations.Input, input: new("resize", "100x30"));
            Assert.Equal((Protocol.States.Routed, "resize"), (resized.State, resized.Interaction!.Kind));
            var at = await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "initial", evidence: ["view-tree"]);
            Assert.Equal((100, 30), (at.Capture!.Width, at.Capture.Height));
            Assert.Equal(new CellRect(0, 0, 100, 30), at.Capture.Inspection!.ViewTree!.Root.Screen);
            await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbF3"));
            // TGroup.ExecView hands cmScreenResized to the modal dialog, which drops it: reported, never assumed.
            var swallowed = await target.SendAsync(Protocol.Operations.Input, input: new("resize", "80x25"));
            Assert.Equal(Protocol.States.Rejected, swallowed.State);
            Assert.Contains("did not apply cmScreenResized", swallowed.Reason);
        }
        await using (var target = await RawTarget.StartAsync(BlockedId, operationMs: 500))
        {
            await target.SendAsync(Protocol.Operations.Handshake);
            await target.SendAsync(Protocol.Operations.Start);
            var clock = Stopwatch.StartNew();
            var blocked = await target.SendAsync(Protocol.Operations.Input, input: new("action", "block-ui"));
            Assert.Equal(Protocol.States.Rejected, blocked.State);
            Assert.StartsWith(Protocol.TimeoutPrefix, blocked.Reason);
            Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2.5));
            target.Disconnect(); // controller gives up while the UI thread is still blocked
            Assert.Equal(1, await target.WaitForExitAsync());
            Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public async Task TargetCrashIsReportedWithItsExceptionAndCleanedUp()
    {
        string root = Scratch();
        int before = TempRootCount();
        try
        {
            var runner = new ScenarioRunner();
            var crash = (await runner.DiscoverAsync(SampleTarget)).Single(s => s.Id == CrashId);
            var clock = Stopwatch.StartNew();
            var result = (await runner.RunAsync(SampleTarget, [crash], ["80x25"], root)).Results.Single();
            Assert.Equal(ScenarioOutcomes.Error, result.Outcome);
            Assert.Contains("Deliberate sample crash.", result.Reason);       // the target's own stderr
            Assert.Contains("step 2 (action crash)", result.Reason);           // died while the action was being acknowledged
            Assert.Equal(new[] { "initial" }, result.Checkpoints.Select(c => c.Checkpoint));
            Assert.True(result.CleanupSucceeded && result.FixtureIntegrityVerified);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
            Assert.Equal(before, TempRootCount());
            Assert.Empty(Process.GetProcessesByName("TSharpWorkbench.SampleTarget"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ControllerLossWhileAMenuLoopRunsUnwindsAndExitsBounded()
    {
        await using var target = await RawTarget.StartAsync(MenuId, operationMs: 500);
        await target.SendAsync(Protocol.Operations.Handshake);
        await target.SendAsync(Protocol.Operations.Start);
        await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbAltW"));
        await target.SendAsync(Protocol.Operations.Input, input: new("key", "kbEnter")); // Arrange submenu: nested menu loops
        Assert.Equal("TSharpVision.TMenuBox", (await target.SendAsync(Protocol.Operations.Checkpoint, checkpoint: "nested")).Capture!.Surface!.TopView);
        var incomplete = await target.SendAsync(Protocol.Operations.Complete);
        Assert.Equal(Protocol.States.Rejected, incomplete.State); Assert.Contains("modal view is still executing", incomplete.Reason);
        var clock = Stopwatch.StartNew();
        target.Disconnect(); // EOF: no Complete, no Shutdown
        Assert.Equal(1, await target.WaitForExitAsync());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Target did not unwind its menu loops promptly.");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string Scratch() => Path.Combine(Path.GetTempPath(), "wb-test-" + Guid.NewGuid().ToString("N"));
    private static void Delete(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static int TempRootCount() => Directory.GetDirectories(Path.GetTempPath(), TempRoots.Prefix + "*").Length;
    private static byte[] Bytes(ScreenSnapshot s) => s.Serialize();
    private static string Row(ScreenSnapshot s, int y) => new(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character).ToArray());
    private static string Text(ScreenSnapshot s) => string.Join("\n", Enumerable.Range(0, s.Height).Select(y => Row(s, y)));

    /// <summary>Launches the sample target with a hand-built authenticated session to exercise raw protocol behaviour.</summary>
    private sealed class RawTarget : IAsyncDisposable
    {
        private readonly string instance = Guid.NewGuid().ToString("N"), secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly string root = Scratch(), scenario;
        private Process process = null!;
        private NamedPipeClientStream pipe = null!;
        private RawTarget(string scenario) => this.scenario = scenario;

        internal static async Task<RawTarget> StartAsync(string scenario, int? operationMs = null)
        {
            var target = new RawTarget(scenario);
            string fixture = Path.Combine(target.root, "fixture"), state = Path.Combine(target.root, "state");
            Directory.CreateDirectory(fixture); Directory.CreateDirectory(state);
            var start = new ProcessStartInfo(SampleTarget) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(TestTargetHost.ActivationArgument);
            foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("TSHARPVISION_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.Environment[TestTargetHost.InstanceVariable] = target.instance;
            start.Environment[TestTargetHost.SecretVariable] = target.secret;
            start.Environment[TestTargetHost.StateVariable] = state;
            start.Environment[TestTargetHost.ScenarioVariable] = scenario;
            start.Environment[TestTargetHost.SizeVariable] = "80x25";
            if (operationMs is not null) start.Environment[TestTargetHost.OperationLimitVariable] = operationMs.Value.ToString();
            target.process = Process.Start(start)!;
            _ = target.process.StandardOutput.ReadToEndAsync(); _ = target.process.StandardError.ReadToEndAsync();
            target.pipe = new NamedPipeClientStream(".", "tsharp-diagnostics-" + target.instance, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await target.pipe.ConnectAsync(timeout.Token);
            return target;
        }

        internal Request Request(string operation, string? checkpoint = null, InputStep? input = null, string[]? evidence = null) =>
            new(Protocol.Version, Protocol.Schema, instance, Guid.NewGuid().ToString("N"), secret, operation, scenario, checkpoint, input, evidence);

        internal Task<Reply> SendAsync(string operation, string? checkpoint = null, InputStep? input = null, string[]? evidence = null) =>
            SendRawAsync(Request(operation, checkpoint, input, evidence));

        internal async Task<Reply> SendRawAsync(Request request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Protocol.WriteAsync(pipe, request, timeout.Token);
            var reply = await Protocol.ReadAsync<Reply>(pipe, timeout.Token);
            Assert.Equal(Protocol.Schema, reply.Schema); Assert.Equal(request.Id, reply.Id);
            return reply;
        }

        internal void Disconnect() => pipe.Dispose();

        internal async Task<int> WaitForExitAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            pipe.Dispose();
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            process.Dispose();
            Delete(root);
        }
    }
}
