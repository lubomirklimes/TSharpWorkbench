using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// The structured internal scenario editor. The real WorkbenchApp runs on NullDriver; keys and
// commands go through the shared event queue. Documents and projects live in a disposable folder.
// Optional logical screenshots (NullDriver cells + diagnostic bitmap font, NOT native): TSWB_PRODUCT_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class EditorTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;

    // ── Visual baseline: the windows nearest to the editor, at the three Workbench grids ─────────────────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public async Task AnalogousWindowsCapture(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string a = Write(docs, "alpha.scenario.json", Proxy("ext/alpha", "Alpha"));
        string b = Write(docs, "beta.scenario.json", Proxy("ext/beta", "Beta"));
        string path = SaveProject(root, "capture.twproj", new WorkbenchProject { Name = "Capture", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [a, b] });
        await Drive(width, height, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 1);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
            Send(WorkbenchDialog.ManageScenarios);
            await Wait(bridge, () => main.ScenariosWindow);
            await Shot(bridge, "scenarios-window");
            Send(WorkbenchDialog.NewScenario);
            await Wait(bridge, () => Modal(app, WorkbenchNewScenarioDialog.Title));
            await Shot(bridge, "new-scenario-dialog");
            Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
        });
        Delete(root);
    }

    // ── Schema vocabulary: the editor's tables are exactly what the runtime parser accepts ─────────────────────

    [Fact]
    public void EditorVocabularyMatchesTheRuntimeParser()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        string Doc(string stepsJson, bool proxy = false) =>
            $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"v/x","title":"t","description":"d",{{(proxy ? "\"backend\":\"proxy-logical\"" : "\"fixture\":\"f/x\"")}},"expectedOutcome":"passed","steps":[{{stepsJson}},{"kind":"checkpoint","id":"end"}]}""";
        bool Accepts(string json) { try { ExternalScenarios.LoadFromBytes(Path.Combine(root, "v.json"), System.Text.Encoding.UTF8.GetBytes(json)); return true; } catch (ExternalScenarioException) { return false; } }
        var samples = new Dictionary<string, string>
        {
            ["key"] = "\"key\":\"kbF10\"", ["keyDown"] = "\"key\":\"kbF10\"", ["keyUp"] = "\"key\":\"kbF10\"", ["text"] = "\"text\":\"abc\"",
            ["modifiers"] = "\"modifier\":\"shift\",\"state\":\"down\"", ["mouse"] = "\"input\":\"click-left@1,1\"", ["release"] = "", ["resize"] = "\"width\":90,\"height\":30",
            ["action"] = "\"adapter\":\"go\"", ["checkpoint"] = "\"id\":\"c1\"",
        };
        Assert.Equal(samples.Keys.Order(), ScenarioSchemaV1.StepKinds.Order());
        foreach (var (kind, fields) in samples)
        {
            string head = "{\"kind\":\"" + kind + "\"" + (fields.Length > 0 ? "," + fields : "");
            string steps = kind switch { "keyUp" => "{\"kind\":\"keyDown\",\"key\":\"kbF10\"}," + head + "}", "modifiers" => head + "},{\"kind\":\"release\"}", _ => head + "}" };
            Assert.True(Accepts(Doc(steps)), kind);
            Assert.True(Accepts(Doc(steps.Replace("{\"kind\":\"" + kind + "\"", "{\"kind\":\"" + kind + "\",\"note\":\"n\""))), kind + " note");
            Assert.False(Accepts(Doc(steps.Replace("{\"kind\":\"" + kind + "\"", "{\"kind\":\"" + kind + "\",\"bogus\":1"))), kind + " unknown field");
            foreach (string field in ScenarioSchemaV1.StepFields(kind).Where(f => !samples[kind].Contains('"' + f + '"')))
                Assert.True(kind == "checkpoint", $"{kind}.{field} has no sample");
        }
        Assert.False(Accepts(Doc("{\"kind\":\"wait\",\"milliseconds\":100}")));   // no wait step in schema v1
        // Assertion kinds and their fields.
        var values = new Dictionary<string, string> { ["view"] = "{\"text\":\"a\"}", ["other"] = "{\"text\":\"b\"}", ["edge"] = "\"left\"", ["min"] = "0", ["max"] = "4", ["value"] = "1",
            ["text"] = "\"a\"", ["level"] = "0", ["state"] = "\"enabled\"", ["x"] = "1", ["y"] = "1", ["width"] = "2",
            // Layout-audit (schema v1): optional profile, rule filter and justified suppressions.
            ["profile"] = "\"geometry\"", ["rules"] = "[\"geom.overlap\"]", ["suppress"] = "[{\"rule\":\"geom.overlap\",\"reason\":\"r\"}]" };
        foreach (string kind in ScenarioSchemaV1.AssertionKinds)
        {
            var fields = ScenarioSchemaV1.AssertionFields(kind);
            string Body(IEnumerable<string> f) => "{\"kind\":\"checkpoint\",\"id\":\"c\",\"assertions\":[{\"id\":\"a\",\"kind\":\"" + kind + "\"" + string.Concat(f.Select(x => ",\"" + x + "\":" + (x == "edge" && kind == "spacing" ? "\"horizontal\"" : x == "state" && kind == "status-item" ? "\"visible\"" : values[x]))) + "}]}";
            Assert.True(Accepts(Doc(Body(fields)).Replace(",{\"kind\":\"checkpoint\",\"id\":\"end\"}", "")), kind);
            foreach (string f in fields.Where(f => ScenarioSchemaV1.AssertionFieldRequired(kind, f)))
                Assert.False(Accepts(Doc(Body(fields.Where(x => x != f))).Replace(",{\"kind\":\"checkpoint\",\"id\":\"end\"}", "")), $"{kind} without {f}");
            foreach (string f in fields.Where(f => !ScenarioSchemaV1.AssertionFieldRequired(kind, f)))
                Assert.True(Accepts(Doc(Body(fields.Where(x => x != f))).Replace(",{\"kind\":\"checkpoint\",\"id\":\"end\"}", "")), $"{kind} without optional {f}");
        }
        Delete(root);
    }

    // ── Factories (the recorder's API) emit runtime-valid schema-v1 steps ───────────────────────────────────

    [Fact]
    public void StepFactoriesEmitSchemaV1()
    {
        Assert.Equal("""{"kind":"key","key":"kbF10"}""", ScenarioDocumentStep.Key("kbF10").ToString());
        Assert.Equal("""{"kind":"text","text":"abc","note":"n"}""", ScenarioDocumentStep.Text("abc", "n").ToString());
        Assert.Equal("""{"kind":"mouse","input":"click-left@3,0"}""", ScenarioDocumentStep.Mouse("click-left", 3, 0).ToString());
        Assert.Equal("wheel-down@-1,-2*3", ScenarioDocumentStep.MouseInput("wheel-down", -1, -2, 3));
        Assert.Equal("""{"kind":"modifiers","modifier":"ctrl","state":"down"}""", ScenarioDocumentStep.Modifier("ctrl", true).ToString());
        Assert.Throws<ArgumentException>(() => ScenarioDocumentStep.Key("kbNope"));
        Assert.Throws<ArgumentException>(() => ScenarioDocumentStep.Text(new string('x', 49)));
        Assert.Throws<ArgumentException>(() => ScenarioDocumentStep.Text("a\tb"));
        Assert.Throws<ArgumentException>(() => ScenarioDocumentStep.Mouse("click-left", 1, 1, 2));   // count only for the wheel
        Assert.Throws<ArgumentException>(() => ScenarioDocumentStep.Mouse("teleport@1,1"));
        var d = ScenarioDocument.New("f/all", "All factories", "Every factory.", "f", checkpoint: null);
        d.Append([ScenarioDocumentStep.Key("kbAltH"), ScenarioDocumentStep.Text("hello"), ScenarioDocumentStep.Modifier("shift", true), ScenarioDocumentStep.Key("kbTab"),
            ScenarioDocumentStep.Modifier("shift", false), ScenarioDocumentStep.KeyDown("kbF2"), ScenarioDocumentStep.KeyUp("kbF2"), ScenarioDocumentStep.Mouse("double-right", 2, 3),
            ScenarioDocumentStep.Mouse("wheel-up", 0, 0, 4), ScenarioDocumentStep.Release(), ScenarioDocumentStep.Resize(100, 30), ScenarioDocumentStep.Checkpoint("done", "explicit")]);
        Assert.DoesNotContain(d.Validate(), p => p.Severity == ScenarioProblemSeverity.Error);
        var parsed = ExternalScenarios.LoadFromBytes(Path.Combine(Path.GetTempPath(), "f.json"), d.Serialize());
        Assert.Equal(d.Steps.Select(s => s.Name), parsed.Scenario.Steps.Select(s => s.Name));
        Assert.Equal(["key", "text", "modifier", "key", "modifier", "key-down", "key-up", "mouse", "mouse", "release", "resize", "checkpoint"], parsed.Scenario.Steps.Select(s => s.Kind));
    }

    // ── Create new -> edit steps -> save -> reopen identical ──────────────────────────────────────────────────

    [Fact]
    public void NewDocumentEditSaveReopenIsIdentical()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "made.scenario.json");
        var d = ScenarioDocument.New("made/one", "Made in the editor", "Created by the structured editor.", "made");
        Assert.True(d.Dirty); Assert.Null(d.Path);
        Assert.True(d.Save().NeedsPath);
        d.Insert(0, [ScenarioDocumentStep.Key("kbF10"), ScenarioDocumentStep.Key("kbEsc")]);
        d.Append(ScenarioDocumentStep.Checkpoint("after-menu"));
        d.Tags = ["smoke", "editor"];
        int copy = d.Duplicate(0, 2); Assert.Equal(2, copy);
        Assert.Equal(1, d.Move(2, 2, -1));
        d.Remove(1, 2);
        d.Replace(1, ScenarioDocumentStep.Text("abc"));
        Assert.Equal(["key", "text", "checkpoint", "checkpoint"], d.Steps.Select(s => s.Kind));
        var saved = d.Save(path);
        Assert.True(saved.Saved, saved.Message); Assert.True(saved.Written); Assert.False(d.Dirty);
        Assert.Equal(ExternalScenarios.Load(path).ContentHash, d.LoadedContentHash);
        byte[] bytes = File.ReadAllBytes(path);
        var again = ScenarioDocument.Load(path);
        Assert.False(again.ReadOnly); Assert.True(again.ByteExact); Assert.False(again.Dirty);
        Assert.Equal(bytes, again.Serialize());
        Assert.Equal(d.Steps.Select(s => s.ToString()), again.Steps.Select(s => s.ToString()));
        Assert.Equal(["smoke", "editor"], again.Tags);
        // Unchanged save writes nothing.
        var touched = File.GetLastWriteTimeUtc(path);
        var noop = again.Save(); Assert.True(noop.Saved); Assert.False(noop.Written); Assert.Equal(touched, File.GetLastWriteTimeUtc(path));
        // Property order of the new document follows the schema example; steps start with "kind".
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(["schema", "version", "id", "title", "description", "group", "tags", "backend", "expectedOutcome", "steps"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Concat(Directory.GetFiles(root, ".*")));
        Delete(root);
    }

    // ── Round trip of the repository's real external scenarios (copies; nothing is written in place) ────────────

    [Fact]
    public void RepositoryScenariosRoundTripWithoutSemanticLoss()
    {
        string repo = RepositoryRoot(), root = Temp(); Directory.CreateDirectory(root);
        var sources = new List<string>();
        sources.AddRange(Directory.GetFiles(Path.Combine(repo, "examples"), "*.scenario.json", SearchOption.AllDirectories));
        sources.AddRange(Directory.GetFiles(Path.Combine(repo, "diagnostics"), "*.scenario.json", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)));
        string commander = Environment.GetEnvironmentVariable("TSWB_FIXTURE_COMMANDER_SCENARIOS") is { Length: > 0 } c ? c : @"C:\TurboVision\GIT\TSharpCommander\diagnostics\CommanderTestTarget\ExternalScenarios";
        if (Directory.Exists(commander)) sources.AddRange(Directory.GetFiles(commander, "*.json").Where(f => !f.EndsWith("visual-baselines.json", StringComparison.OrdinalIgnoreCase)));
        Assert.True(sources.Count >= 13, "repository scenarios found: " + sources.Count);
        var report = new List<object>();
        int byteExact = 0, semantic = 0, readOnly = 0;
        foreach (string source in sources)
        {
            byte[] original = File.ReadAllBytes(source);
            string copy = Path.Combine(root, Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(source));
            File.WriteAllBytes(copy, original);
            ExternalScenarioFile? runtime = null;
            try { runtime = ExternalScenarios.Load(copy); } catch (ExternalScenarioException) { }
            var d = ScenarioDocument.Load(copy);
            if (d.ReadOnly) { readOnly++; report.Add(new { source, readOnly = true, d.ReadOnlyReason }); Assert.Null(runtime); continue; }
            if (runtime is not null) Assert.DoesNotContain(d.Validate(), p => p.Severity == ScenarioProblemSeverity.Error);
            Assert.Equal(runtime is null, d.Validate().Any(p => p.Severity == ScenarioProblemSeverity.Error));
            bool exactOnLoad = d.ByteExact;
            if (exactOnLoad) byteExact++; else semantic++;
            // Unedited: never rewritten.
            Assert.False(d.Save().Written);
            Assert.Equal(original, File.ReadAllBytes(copy));
            // Same tree as the original.
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Strip(original)), JsonNode.Parse(Strip(d.Serialize()))), source);
            if (runtime is null) { report.Add(new { source, byteExactOnLoad = exactOnLoad, runtimeValid = false, problems = d.Validate().Select(p => p.Display).ToArray() }); continue; }
            // An edit and its revert: the rewritten file is the same document (same runtime descriptor, same JSON tree).
            string title = d.Title;
            d.Title = title + " (edited)"; d.Title = title;
            Assert.True(d.Dirty);
            var result = d.Save();
            Assert.True(result.Saved && result.Written, result.Message);
            var reread = ExternalScenarios.Load(copy);
            Assert.Equal(JsonSerializer.Serialize(runtime.Scenario, ScenarioRunner.Json), JsonSerializer.Serialize(reread.Scenario, ScenarioRunner.Json));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Strip(original)), JsonNode.Parse(Strip(File.ReadAllBytes(copy)))), source);
            byte[] rewritten = File.ReadAllBytes(copy); int diff = original.AsSpan().CommonPrefixLength(rewritten);
            if (exactOnLoad) Assert.True(original.AsSpan().SequenceEqual(rewritten), "edit+revert of a byte-exact document must restore its bytes: " + source);
            report.Add(new { source, byteExactOnLoad = exactOnLoad, rewrittenBytesEqual = original.AsSpan().SequenceEqual(rewritten), runtimeValid = true,
                firstDifference = diff == original.Length && diff == rewritten.Length ? null : $"offset {diff}: original \"{System.Text.Encoding.UTF8.GetString(original, Math.Max(0, diff - 20), Math.Min(40, original.Length - Math.Max(0, diff - 20)))}\" rewritten \"{System.Text.Encoding.UTF8.GetString(rewritten, Math.Max(0, diff - 20), Math.Min(40, rewritten.Length - Math.Max(0, diff - 20)))}\"" });
        }
        if (Environment.GetEnvironmentVariable("TSWB_PRODUCT_EVIDENCE") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "p3b-roundtrip-report.json"), JsonSerializer.Serialize(new { total = sources.Count, byteExact, semanticOnly = semantic, readOnly, files = report }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(byteExact > 0);
        Delete(root);
    }
    private static byte[] Strip(byte[] bytes) => bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes[3..] : bytes;

    // ── Invalid files: diagnostics, read-only, never written ──────────────────────────────────────────────

    [Fact]
    public void InvalidFilesAreShownWithProblemsAndNeverWritten()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        string valid = Proxy("bad/x", "X");
        var readOnlyCases = new Dictionary<string, string>
        {
            ["malformed"] = "{ \"schema\": \"tsharpworkbench.scenario\", ",
            ["comments"] = valid.Replace("\"version\": 1,", "\"version\": 1, // comment"),
            ["trailing-comma"] = valid.Replace("\"passed\",", "\"passed\",,"),
            ["duplicate"] = valid.Replace("\"title\": \"X\",", "\"title\": \"X\", \"title\": \"Y\","),
            ["version2"] = valid.Replace("\"version\": 1", "\"version\": 2"),
            ["not-scenario"] = """{ "schema": "TSharpVision.TestWorkbench", "version": 1 }""",
            ["steps-object"] = valid.Replace("\"steps\": [ { \"kind\": \"key\", \"key\": \"kbF10\" }, { \"kind\": \"checkpoint\", \"id\": \"initial\" } ]", "\"steps\": {}"),
            ["step-string"] = valid.Replace("{ \"kind\": \"key\", \"key\": \"kbF10\" }", "\"kbF10\""),
            ["root-array"] = "[]",
        };
        foreach (var (name, text) in readOnlyCases)
        {
            string path = Write(root, name + ".json", text);
            byte[] before = File.ReadAllBytes(path);
            var d = ScenarioDocument.Load(path);
            Assert.True(d.ReadOnly, name);
            Assert.NotEmpty(d.LoadProblems); Assert.NotNull(d.ReadOnlyReason);
            Assert.NotEmpty(d.Validate());
            var save = d.Save(); Assert.False(save.Saved); Assert.False(save.Written);
            Assert.Throws<InvalidOperationException>(() => d.Append(ScenarioDocumentStep.Key("kbEnter")));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        Assert.Contains("line", ScenarioDocument.Load(Path.Combine(root, "malformed.json")).ReadOnlyReason);
        Assert.Contains("Duplicate JSON property 'title'", ScenarioDocument.Load(Path.Combine(root, "duplicate.json")).ReadOnlyReason);
        // Semantically invalid but representable: editable, located problems, save refused until fixed.
        string semantic = Write(root, "semantic.json", valid.Replace("kbF10", "kbNope").Replace("\"passed\"", "\"maybe\""));
        byte[] original = File.ReadAllBytes(semantic);
        var doc = ScenarioDocument.Load(semantic);
        Assert.False(doc.ReadOnly);
        var problems = doc.Validate();
        Assert.Contains(problems, p => p.Location == "$.steps[0].key" && p.Step == 0 && p.Severity == ScenarioProblemSeverity.Error && p.Message.Contains("kbNope"));
        Assert.Contains(problems, p => p.Location == "$.expectedOutcome");
        var refused = doc.Save();
        Assert.False(refused.Saved); Assert.Contains("problem", refused.Message);
        doc.Title = "X2";
        Assert.Contains("must be fixed", doc.Save().Message);
        Assert.Equal(original, File.ReadAllBytes(semantic));
        doc.Replace(0, doc.Steps[0].WithText("key", "kbF10")); doc.ExpectedOutcome = "passed";
        Assert.DoesNotContain(doc.Validate(), p => p.Severity == ScenarioProblemSeverity.Error);
        Assert.True(doc.Save().Written);
        Assert.Equal("kbF10", ExternalScenarios.Load(semantic).Scenario.Steps[0].Name);
        // Unknown properties (rejected by schema v1) are kept, shown and block the save; they are never silently dropped.
        string unknown = Write(root, "unknown.json", valid.Replace("\"group\":", "\"owner\": \"me\",\n  \"group\":"));
        var u = ScenarioDocument.Load(unknown);
        Assert.False(u.ReadOnly);
        Assert.Contains(u.Validate(), p => p.Location == "$.owner" && p.Severity == ScenarioProblemSeverity.Error);
        u.Title = "Y";
        Assert.False(u.Save().Saved);
        Assert.Contains("\"owner\": \"me\"", u.SerializeText());
        Delete(root);
    }

    // ── External modification since load is refused; Save As to an existing file needs confirmation ──────────

    [Fact]
    public void ExternalModificationIsNeverClobbered()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        string path = Write(root, "shared.scenario.json", Proxy("shared/x", "Shared"));
        var d = ScenarioDocument.Load(path);
        d.Append(ScenarioDocumentStep.Key("kbEnter"));
        string external = Proxy("shared/x", "Changed elsewhere");
        File.WriteAllText(path, external);
        Assert.True(d.ChangedOnDisk());
        var result = d.Save();
        Assert.False(result.Saved); Assert.True(result.ExternalChange); Assert.Contains("changed or deleted on disk", result.Message);
        Assert.Equal(external, File.ReadAllText(path));
        File.Delete(path);
        Assert.True(d.Save().ExternalChange); Assert.False(File.Exists(path));
        // Save As elsewhere works; to an existing other file only with explicit replacement.
        string other = Write(root, "other.scenario.json", Proxy("other/x", "Other"));
        var exists = d.Save(other);
        Assert.False(exists.Saved); Assert.True(exists.TargetExists);
        Assert.Contains("Other", File.ReadAllText(other));
        Assert.True(d.Save(other, replaceExisting: true).Written);
        Assert.Equal(other, d.Path);
        Assert.Equal(3, ExternalScenarios.Load(other).Scenario.Steps.Length);   // kbF10, initial, kbEnter
        Assert.False(d.Save(Path.Combine(root, "x.txt")).Saved);
        Delete(root);
    }

    // ── Freshness: a save after the catalog was loaded makes a run of the old snapshot stale (refused) ─────────

    [Fact]
    public void SavingMakesTheLoadedCatalogStaleUntilRefresh()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        string path = Write(root, "fresh.scenario.json", Proxy("fresh/x", "Fresh"));
        var stamps = ScenarioFiles.Stamp([path]);
        var loaded = ExternalScenarios.Merge([], [ExternalScenarios.Load(path)]);
        Assert.Null(ScenarioFiles.StaleRun(loaded, [path], stamps));
        var d = ScenarioDocument.Load(path);
        Assert.Equal(loaded[0].External!.ContentHash, d.LoadedContentHash);
        d.Append(ScenarioDocumentStep.Key("kbEnter"));
        Assert.True(d.Save().Written);
        string? stale = ScenarioFiles.StaleRun(loaded, [path], stamps);
        Assert.NotNull(stale); Assert.Contains("F2", stale);
        var refreshed = ExternalScenarios.Merge([], [ExternalScenarios.Load(path)]);
        Assert.Null(ScenarioFiles.StaleRun(refreshed, [path], ScenarioFiles.Stamp([path])));
        Delete(root);
    }

    // ── Keyboard-driven editor in the real Workbench (NullDriver) ────────────────────────────────────────────

    [Fact]
    public async Task KeyboardEditorOpensEditsSavesAndRefreshesTheCatalog()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string doc = Write(docs, "alpha.scenario.json", Proxy("ext/alpha", "Alpha"));
        string path = SaveProject(root, "editor.twproj", new WorkbenchProject { Name = "Editor", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [doc] });
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 1);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count == 1 ? p : null, 60);
            // Scenarios window (Alt+2), Enter on the selected file: the internal editor opens.
            Send(WorkbenchDialog.ManageScenarios);
            var window = await Wait(bridge, () => main.ScenariosWindow);
            Press(Keys.kbEnter);
            var editor = await Wait(bridge, () => app.DeskTop!.current as WorkbenchScenarioEditorWindow);
            await bridge.Invoke(() =>
            {
                Assert.Equal(doc, editor.Document.Path); Assert.False(editor.Document.ReadOnly);
                Assert.Equal(2, editor.Steps.range); Assert.Same(editor.Steps, editor.current);
                Assert.Contains("ext/alpha", editor.HeaderText); Assert.Contains("Black-Box", editor.HeaderText); Assert.Contains("Saved", editor.HeaderText);
                Assert.Equal(doc, main.SelectedScenarioFile());
                Assert.True(TView.CommandEnabled(WorkbenchDialog.OpenScenario));
            });
            await Shot(bridge, "editor-opened");
            // Ins: Add Step -> "text" (second kind) -> type -> Enter.
            Press(Keys.kbIns);
            await Wait(bridge, () => Modal(app, WorkbenchStepKindDialog.Title));
            await Shot(bridge, "add-step-kind");
            Press(Keys.kbDown); Press(Keys.kbEnter);
            var step = (WorkbenchScenarioStepDialog)await Wait(bridge, () => Modal(app, "New Type text"));
            Type("hello"); await Settle(bridge);
            await Shot(bridge, "step-dialog-text");
            Press(Keys.kbEnter);
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() =>
            {
                Assert.Equal(["key", "text", "checkpoint"], editor.Document.Steps.Select(s => s.Kind));
                Assert.Equal("hello", editor.Document.Steps[1].String("text"));
                Assert.Equal(1, editor.Steps.focused);
                Assert.True(editor.Document.Dirty); Assert.Contains(" *", editor.title);
            });
            // Shift+Down selects a range, Alt+D moves it down... here: move the text step down (Alt+D), then up (Alt+U).
            Press(Keys.kbAltD); await Settle(bridge);
            await bridge.Invoke(() => Assert.Equal(["key", "checkpoint", "text"], editor.Document.Steps.Select(s => s.Kind)));
            Press(Keys.kbAltU); await Settle(bridge);
            await bridge.Invoke(() => Assert.Equal(["key", "text", "checkpoint"], editor.Document.Steps.Select(s => s.Kind)));
            // Range: Shift+Down from the text step selects text+checkpoint; Duplicate (Alt+C) copies both -> duplicate checkpoint ID.
            PressShift(Keys.kbDown); await Settle(bridge);
            await bridge.Invoke(() => Assert.Equal((1, 2), editor.Selection));
            Press(Keys.kbAltC); await Settle(bridge);
            await bridge.Invoke(() =>
            {
                Assert.Equal(5, editor.Document.Steps.Count);
                Assert.Contains(editor.Problems, p => p.Severity == ScenarioProblemSeverity.Error && p.Location == "$.steps[4].id" && p.Message.Contains("Duplicate checkpoint"));
                Assert.Equal((3, 2), editor.Selection);
            });
            await Shot(bridge, "editor-problem");
            // Save is refused while invalid; the file is untouched.
            byte[] before = File.ReadAllBytes(doc);
            Press(Keys.kbCtrlS); await Settle(bridge);
            await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Message.StartsWith("Not saved")));
            Assert.Equal(before, File.ReadAllBytes(doc));
            // Del removes the selected range; Ctrl+S saves; the attached catalog refreshes (no run).
            Press(Keys.kbDel); await Settle(bridge);
            await bridge.Invoke(() => Assert.Equal(3, editor.Document.Steps.Count));
            Press(Keys.kbCtrlS);
            await Wait(bridge, () => main is { Busy: false } && main.AttachedScenarios.FirstOrDefault()?.Info.Document?.Scenario.Steps.Length == 3 ? main : null, 30);
            await bridge.Invoke(() =>
            {
                Assert.False(editor.Document.Dirty); Assert.DoesNotContain(" *", editor.title);
                Assert.Contains(app.Notifications.History, n => n.Message.Contains("refreshing the catalog"));
                Assert.Equal(["Alpha"], main.TestList.Strings.ToArray());
            });
            Assert.Equal(["key", "text", "checkpoint"], ExternalScenarios.Load(doc).Scenario.Steps.Select(s => s.Kind == "text" ? "text" : s.Kind));
            await Shot(bridge, "editor-saved");
            // Dirty + Esc: Unsaved prompt; Cancel keeps the editor; Discard closes and leaves the file as saved.
            Press(Keys.kbDel); await Settle(bridge);
            byte[] saved = File.ReadAllBytes(doc);
            Press(Keys.kbEsc);
            await Wait(bridge, () => Modal(app, "Unsaved Scenario"));
            await Shot(bridge, "editor-unsaved-prompt");
            Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() => Assert.Contains(editor, main.ScenarioEditors));
            Press(Keys.kbEsc);
            await Wait(bridge, () => Modal(app, "Unsaved Scenario"));
            Press(Keys.kbAltD);
            await Until(bridge, () => !main.ScenarioEditors.Contains(editor));
            Assert.Equal(saved, File.ReadAllBytes(doc));
            // Open in editor again from the Workspace selection (the menu command): same file, one editor.
            await bridge.Invoke(() => main.Select());
            Send(WorkbenchDialog.OpenScenario); Send(WorkbenchDialog.OpenScenario);
            await Wait(bridge, () => main.ScenarioEditors.Count == 1 ? main : null);
            await Settle(bridge);
            await bridge.Invoke(() => Assert.Single(Children(app.DeskTop!).OfType<WorkbenchScenarioEditorWindow>()));
        });
        Delete(root);
    }

    // Checkpoint with an assertion, metadata, and the recorder API (ApplyRecording) in the window.
    [Fact]
    public async Task CheckpointAssertionMetadataAndRecordingApi()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string doc = Write(docs, "beta.scenario.json", Proxy("ext/beta", "Beta"));
        string path = SaveProject(root, "assert.twproj", new WorkbenchProject { Name = "Assert", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [doc], ScreenSizes = ["80x25", "140x40"] });
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 1);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count == 1 ? p : null, 60);
            var editor = await Wait(bridge, () => main.OpenScenarioEditor(doc));
            // Edit the checkpoint (End, Enter), add an assertion: Alt+D (Add), first kind screen-contains, text, OK, OK.
            Press(Keys.kbEnd); Press(Keys.kbEnter);
            var step = (WorkbenchScenarioStepDialog)await Wait(bridge, () => Modal(app, "Edit Checkpoint (capture + assertions)"));
            await Shot(bridge, "step-dialog-checkpoint");
            Press(Keys.kbAltD);
            await Wait(bridge, () => Modal(app, "Add Assertion"));
            await Shot(bridge, "assertion-kind");
            Press(Keys.kbEnter);
            var assertion = (WorkbenchAssertionDialog)await Wait(bridge, () => Modal(app, "New assertion: screen-contains"));
            await bridge.Invoke(() => Assert.Equal("screen-contains", assertion.IdField.Data));
            Press(Keys.kbAltT); Type("Hello world"); await Settle(bridge);
            await Shot(bridge, "assertion-dialog");
            Press(Keys.kbEnter);
            await Wait(bridge, () => Modal(app, "Edit Checkpoint (capture + assertions)"));
            Press(Keys.kbEnter);
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() =>
            {
                var cp = editor.Document.Steps[1];
                Assert.Equal(1, cp.AssertionCount);
                Assert.Equal("""{"kind":"checkpoint","id":"initial","assertions":[{"id":"screen-contains","kind":"screen-contains","text":"Hello world"}]}""", cp.ToString());
            });
            // Metadata: Alt+M, title and tags.
            Press(Keys.kbAltM);
            var meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
            await Shot(bridge, "metadata-dialog");
            await bridge.Invoke(() => { meta.TitleField.SetData("Beta edited"); meta.TagsField.SetData("smoke, editor"); });
            Press(Keys.kbEnter);
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() => { Assert.Equal("Beta edited", editor.Document.Title); Assert.Equal(["smoke", "editor"], editor.Document.Tags); });
            // Recorder API: context, Append / InsertAtSelection / ReplaceSelection.
            await bridge.Invoke(() =>
            {
                var ctx = editor.RecordingContext();
                Assert.Equal(Target, ctx.TargetPath); Assert.True(ctx.Proxy); Assert.Equal(["80x25", "140x40"], ctx.ScreenSizes); Assert.Equal(path, ctx.ProjectPath);
                Assert.Equal(doc, ctx.DocumentPath); Assert.Equal(2, ctx.StepCount);
                Assert.Equal((2, 2), editor.ApplyRecording(RecordedInsertMode.Append, [ScenarioDocumentStep.Key("kbDown"), ScenarioDocumentStep.Mouse("click-left", 4, 2)]));
                Assert.Equal((2, 2), editor.Selection);
                editor.SelectSteps(1);
                Assert.Equal((1, 1), editor.ApplyRecording(RecordedInsertMode.InsertAtSelection, [ScenarioDocumentStep.Text("rec")]));
                editor.SelectSteps(3, 2);
                Assert.Equal((3, 1), editor.ApplyRecording(RecordedInsertMode.ReplaceSelection, [ScenarioDocumentStep.Key("kbEnter")]));
                Assert.Equal(["kbF10", "rec", "initial", "kbEnter"], editor.Document.Steps.Select(s => s.Name));
                Assert.Throws<ArgumentException>(() => editor.ApplyRecording(RecordedInsertMode.Append, []));
            });
            Press(Keys.kbCtrlS);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.Contains("Beta edited") ? main : null, 30);
            await Shot(bridge, "editor-after-recording-api");
        });
        var saved = ExternalScenarios.Load(doc).Scenario;
        Assert.Equal("Beta edited", saved.Name);
        Assert.Equal("Hello world", Assert.Single(saved.Steps[2].Assertions!).Text);
        Delete(root);
    }

    // Read-only invalid document in the window; New scenario opens the editor on the created file.
    [Fact]
    public async Task ReadOnlyDocumentAndNewScenarioRoute()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string good = Write(docs, "good.scenario.json", Proxy("ext/good", "Good"));
        string broken = Write(docs, "broken.scenario.json", Proxy("ext/broken", "Broken").Replace("\"version\": 1,", "\"version\": 1, // not JSON"));
        byte[] brokenBytes = File.ReadAllBytes(broken);
        string created = Path.Combine(docs, "fresh.scenario.json");
        string path = SaveProject(root, "ro.twproj", new WorkbenchProject { Name = "ReadOnly", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [good, broken] });
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 1);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
            var editor = await Wait(bridge, () => main.OpenScenarioEditor(broken));
            await bridge.Invoke(() =>
            {
                Assert.True(editor.Document.ReadOnly); Assert.Contains("[read-only]", editor.title);
                Assert.Contains("READ-ONLY", editor.HeaderText);
                Assert.False(TView.CommandEnabled(WorkbenchScenarioEditorWindow.SaveDocument)); Assert.False(TView.CommandEnabled(WorkbenchScenarioEditorWindow.NewStep));
                Assert.Contains("Not valid JSON", editor.ProblemList.GetText(0, 300));
            });
            await Shot(bridge, "editor-read-only");
            Press(Keys.kbIns); Press(Keys.kbCtrlS); await Settle(bridge);
            await bridge.Invoke(() => Assert.Null(Modal(app, WorkbenchStepKindDialog.Title)));
            Press(Keys.kbEsc); await Until(bridge, () => main.ScenarioEditors.Count == 0);
            Assert.Equal(brokenBytes, File.ReadAllBytes(broken));
            // New scenario: the existing dialog, then the editor opens on the created, attached file.
            Send(WorkbenchDialog.NewScenario);
            await Wait(bridge, () => Modal(app, WorkbenchNewScenarioDialog.Title));
            Type("ext/fresh"); Press(Keys.kbTab); Type("Fresh one"); Press(Keys.kbEnter);
            var save = (TFileDialog)await Wait(bridge, () => Modal(app, "Save new scenario as"));
            await bridge.Invoke(() => save.fileName!.SetData(created));
            Press(Keys.kbEnter);
            var opened = await Wait(bridge, () => app.DeskTop!.current as WorkbenchScenarioEditorWindow);
            await bridge.Invoke(() =>
            {
                Assert.Equal(created, opened.Document.Path); Assert.False(opened.Document.Dirty); Assert.False(opened.Document.ReadOnly);
                Assert.Contains(created, main.ScenarioFileList);
                Assert.Equal(["checkpoint"], opened.Document.Steps.Select(s => s.Kind));
            });
            await Shot(bridge, "editor-new-scenario");
        });
        Delete(root);
    }

    // Mouse and key steps through their dialogs (structured fields compose the runtime grammar), at the minimum grid.
    [Fact]
    public async Task MouseAndKeyStepDialogsComposeSchemaSyntax()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string doc = Write(docs, "mouse.scenario.json", Proxy("ext/mouse", "Mouse"));
        string path = SaveProject(root, "mouse.twproj", new WorkbenchProject { Name = "Mouse", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [doc] });
        await Drive(120, 36, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 1);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
            var editor = await Wait(bridge, () => main.OpenScenarioEditor(doc));
            Press(Keys.kbIns);
            await Wait(bridge, () => Modal(app, WorkbenchStepKindDialog.Title));
            Press(Keys.kbDown); Press(Keys.kbDown); Press(Keys.kbEnter);   // key, text, mouse
            var mouse = (WorkbenchScenarioStepDialog)await Wait(bridge, () => Modal(app, "New Mouse input"));
            await bridge.Invoke(() => { mouse.XField!.SetData("5"); mouse.YField!.SetData("-1"); Nudge(mouse); Assert.Equal("Input  click-left@5,-1", mouse.PreviewText); });
            await Shot(bridge, "step-dialog-mouse");
            Press(Keys.kbEnter);
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            // A key step: Enter edits the first step; an unknown name is refused in the dialog.
            Press(Keys.kbHome); Press(Keys.kbEnter);
            var key = (WorkbenchScenarioStepDialog)await Wait(bridge, () => Modal(app, "Edit Key press"));
            await Shot(bridge, "step-dialog-key");
            await bridge.Invoke(() => key.KeyField!.SetData("kbNope"));
            Press(Keys.kbEnter); await Settle(bridge);
            await bridge.Invoke(() => { Assert.Same(key, app.DeskTop!.TopView()); Assert.Contains("Unknown key 'kbNope'", key.NoticeText); key.KeyField!.SetData("kbAltX"); });
            Press(Keys.kbEnter);
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() =>
            {
                Assert.Equal(["kbAltX", "click-left@5,-1", "initial"], editor.Document.Steps.Select(s => s.Name));
                Assert.Equal("""{"kind":"mouse","input":"click-left@5,-1"}""", editor.Document.Steps[1].ToString());
                Assert.DoesNotContain(editor.Problems, p => p.Severity == ScenarioProblemSeverity.Error);
            });
            await Shot(bridge, "editor-mouse-key");
        });
        Delete(root);
    }

    // Final layout at the three grids: editor window fits, no overlapping buttons, readable rows.
    [Theory]
    [InlineData(140, 40)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public async Task EditorLayoutFitsTheWorkbenchGrids(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string doc = Path.Combine(docs, "about.scenario.json");
        File.Copy(Path.Combine(RepositoryRoot(), "examples", "TVDemo", "scenarios", "about.scenario.json"), doc);
        string path = SaveProject(root, "grid.twproj", new WorkbenchProject { Name = "Grid", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [doc] });
        await Drive(width, height, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 1);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
            var editor = await Wait(bridge, () => main.OpenScenarioEditor(doc));
            Press(Keys.kbDown); Press(Keys.kbDown);
            await Settle(bridge);
            await bridge.Invoke(() =>
            {
                var desk = app.DeskTop!.GetExtent(); var bounds = editor.GetBounds();
                Assert.True(bounds.a.x >= 0 && bounds.a.y >= 0 && bounds.b.x <= desk.b.x && bounds.b.y <= desk.b.y);
                var views = Children(editor).Where(v => v is not TFrame).ToArray();
                foreach (var v in views) { var r = v.GetBounds(); Assert.True(r.a.x >= 1 && r.a.y >= 1 && r.b.x <= editor.size.x - 1 && r.b.y <= editor.size.y, v.GetType().Name + " " + r.a.x + "," + r.a.y + "-" + r.b.x + "," + r.b.y); }
                foreach (var row in views.OfType<TButton>().GroupBy(b => b.GetBounds().a.y))
                {
                    var buttons = row.Select(b => b.GetBounds()).OrderBy(r => r.a.x).ToArray();
                    for (int i = 1; i < buttons.Length; i++) Assert.True(buttons[i].a.x >= buttons[i - 1].b.x, "buttons overlap");
                }
                Assert.True(editor.Steps.size.y >= 10, "step rows " + editor.Steps.size.y);
                Assert.False(editor.ProblemList.GetState(Views.sfVisible));
                Assert.True(editor.StepFields.size.x >= 36);
                Assert.Equal(5, editor.Steps.range);
                Assert.True(editor.Document.ByteExact || !editor.Document.ReadOnly);
            });
            await Shot(bridge, "editor-tvdemo-about");
            Press(Keys.kbEnter);
            await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchScenarioStepDialog);
            await Shot(bridge, "editor-tvdemo-checkpoint-dialog");
            Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() => Assert.False(editor.Document.Dirty));
        });
        Delete(root);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private static void PressShift(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); ev.keyDown.controlKeyState = Keys.kbShift; TEventQueue.Enqueue(ev); }

    private static string Proxy(string id, string title) => $$"""
        {
          "schema": "tsharpworkbench.scenario",
          "version": 1,
          "id": "{{id}}",
          "title": "{{title}}",
          "description": "Editor test document.",
          "group": "{{id.Split('/')[0]}}",
          "backend": "proxy-logical",
          "expectedOutcome": "passed",
          "steps": [ { "kind": "key", "key": "kbF10" }, { "kind": "checkpoint", "id": "initial" } ]
        }
        """;
    private static string Write(string folder, string name, string text) { string path = Path.Combine(folder, name); File.WriteAllText(path, text); return path; }
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "p3b-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body, bool showStart = false)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            using var app = new WorkbenchApp(showStart ? "" : Target, output, new(showStart ? "" : Target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") }, showStart: showStart);
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        if (app.Project is { Busy: true }) Command(app.Project, WorkbenchDialog.CancelRun);
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
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge, int count) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= count ? p : null, 60);
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    // Logical screenshots (NullDriver cell screen of the real WorkbenchApp, diagnostic bitmap font; not desktop-native).
    private static async Task Shot(Bridge bridge, string state)
    {
        await Settle(bridge);
        if (Environment.GetEnvironmentVariable("TSWB_PRODUCT_EVIDENCE") is not { Length: > 0 } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string name = $"p3b-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            string text = string.Join("\n", Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character).ToArray()).TrimEnd()));
            File.WriteAllText(Path.Combine(root, name + ".logical.txt"), text);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(EditorTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Press(char letter) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = letter; ev.keyDown.charScan = new CharScanType(letter); TEventQueue.Enqueue(ev); }
    private static void Type(string text) { foreach (char c in text) Press(c); }
    private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    private static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(60); await bridge.Invoke(() => { }); }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
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
