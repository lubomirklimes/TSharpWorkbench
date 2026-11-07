using System.Diagnostics;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// B. a run never executes an old in-memory snapshot of a modified, deleted or detached external JSON
// document (run-boundary freshness); C. fixture-bound documents bind only to fixture-only registrations unless a compiled
// scenario is explicitly allowed as a legacy fixture. Documents, projects and runs live in disposable temp folders.
[Collection("NonParallel")]
public sealed class ExternalDocumentFreshnessTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;
    private const string FixtureId = "sample/fixture-only/notes", LegacyId = "sample/dialog/basic", NegativeId = "selftest/failure/wrong-expectations";

    // ── B. run-boundary freshness (logic) ─────────────────────────────────────────

    private static string ProxyDocument(string id, string title = "Title") =>
        $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"{{id}}","title":"{{title}}","description":"d","group":"ext","backend":"proxy-logical","expectedOutcome":"passed","steps":[{"kind":"checkpoint","id":"initial"}]}""";

    // Two attached documents, loaded as a catalog would load them: stamps first, then the documents.
    private static (string Root, string A, string B, Dictionary<string, ScenarioFiles.FileStamp?> Stamps, ScenarioDescriptor[] Catalog) Loaded()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        string a = Write(root, "a.scenario.json", ProxyDocument("ext/a")), b = Write(root, "b.scenario.json", ProxyDocument("ext/b"));
        var stamps = ScenarioFiles.Stamp([a, b]);
        return (root, a, b, stamps, ExternalScenarios.Merge([], [ExternalScenarios.Load(a), ExternalScenarios.Load(b)]));
    }
    private static ScenarioDescriptor Id(ScenarioDescriptor[] catalog, string id) => catalog.Single(s => s.Id == id);
    // A later write within the file system's timestamp resolution must still be a change for these tests.
    private static void Touch(string path) => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

    [Fact]
    public void UnchangedDocumentsAndCompiledScenariosStayRunnable()
    {
        var (root, a, b, stamps, catalog) = Loaded();
        try
        {
            Assert.Null(ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a, b], stamps));
            Assert.Null(ScenarioFiles.StaleRun(catalog, [a, b], stamps));
            // Compiled scenarios have no document; even a changed attachment set does not concern them.
            var compiled = new TestScenario("compiled/x", "X", "compiled", "d").Checkpoint("c").Descriptor;
            Assert.Null(ScenarioFiles.StaleRun([compiled], [a], stamps));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void EditedDocumentWithTheSameIdIsRefused()
    {
        var (root, a, b, stamps, catalog) = Loaded();
        try
        {
            File.WriteAllText(a, ProxyDocument("ext/a", "Edited title")); Touch(a);
            string? stale = ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a, b], stamps);
            Assert.NotNull(stale);
            Assert.StartsWith("Nothing was run", stale);
            Assert.Contains("'ext/a': a.scenario.json was edited on disk after the catalog was loaded", stale);
            Assert.Contains("Refresh the catalog with F2", stale);
            // Same bytes again but a newer write time: content matches, the attachment stamps do not (refresh required).
            File.WriteAllText(a, ProxyDocument("ext/a")); Touch(a);
            Assert.Contains("the attached scenario files changed after the catalog was loaded", ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a, b], stamps));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DeletedReplacedAndInvalidDocumentsAreRefused()
    {
        var (root, a, b, stamps, catalog) = Loaded();
        try
        {
            File.Delete(a);
            Assert.Contains("'ext/a': a.scenario.json no longer exists on disk", ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a, b], stamps));
            File.WriteAllText(a, ProxyDocument("ext/other"));
            Assert.Contains("'ext/a': a.scenario.json was changed on disk and now defines 'ext/other'", ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a, b], stamps));
            File.WriteAllText(a, "{ not json");
            Assert.Contains("'ext/a': a.scenario.json was changed on disk and is now not usable (JSON error)", ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a, b], stamps));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DetachedDocumentIsRefusedEvenWhenItsFileIsUnchanged()
    {
        var (root, a, b, stamps, catalog) = Loaded();
        try
        {
            // Remove from project, then the reload was cancelled or failed: the old catalog still holds ext/b.
            string? stale = ScenarioFiles.StaleRun([Id(catalog, "ext/b")], [a], stamps);
            Assert.Contains("'ext/b': b.scenario.json is no longer attached to the project", stale);
            // The remaining document is still the loaded one, but the attachment set changed: refresh first.
            Assert.Contains("the attached scenario files changed", ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a], stamps));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ChangesInOtherAttachedFilesRequireARefresh()
    {
        var (root, a, b, stamps, catalog) = Loaded();
        try
        {
            // b now duplicates a's ID: a itself is unchanged, but the loaded catalog no longer describes the project.
            File.WriteAllText(b, ProxyDocument("ext/a")); Touch(b);
            Assert.Contains("the attached scenario files changed after the catalog was loaded", ScenarioFiles.StaleRun([Id(catalog, "ext/a")], [a, b], stamps));
            // An added, not yet loaded file is a change as well.
            var (root2, a2, b2, stamps2, catalog2) = Loaded();
            try
            {
                string c = Write(root2, "c.scenario.json", ProxyDocument("ext/c"));
                Assert.Contains("the attached scenario files changed", ScenarioFiles.StaleRun([Id(catalog2, "ext/a")], [a2, b2, c], stamps2));
            }
            finally { Delete(root2); }
        }
        finally { Delete(root); }
    }

    // ── B. run-boundary freshness (real Workbench TUI, real SampleTarget) ──────────

    private static string FixtureDocument(string id, string title = "Notes idle") =>
        $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"{{id}}","title":"{{title}}","description":"d","group":"external","fixture":"{{FixtureId}}","expectedOutcome":"passed","steps":[{"kind":"checkpoint","id":"idle"}]}""";

    [Fact]
    public async Task StaleOrDetachedDocumentNeverLaunchesTheTargetAndRefreshRecovers()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs"), runs = Path.Combine(root, "runs");
        Directory.CreateDirectory(docs);
        string a = Write(docs, "a.scenario.json", FixtureDocument("ext/a", "Notes idle A")), b = Write(docs, "b.scenario.json", FixtureDocument("ext/b", "Notes idle B"));
        // A private copy of the (small) sample target deployment, so this test can make a reload fail without touching the shared one.
        string copy = Path.Combine(root, "target");
        Directory.CreateDirectory(copy);
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(Target)!)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        string exe = Path.Combine(copy, Path.GetFileName(Target)), hidden = exe + ".hidden";
        string path = SaveProject(root, "fresh.twproj", new WorkbenchProject { Name = "Fresh", Target = exe, OutputRoot = runs, ScenarioFiles = [a, b], ScreenSizes = ["80x25"] });
        string targetName = Path.GetFileNameWithoutExtension(Target);
        var lines = new List<string>();
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count > 0 ? p : null, 60);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Contains("Notes idle A")
                && p.AttachedScenarios.Length == 2 && p.AttachedScenarios.All(s => s.Status == "Bound") ? p : null, 60);

            // 1. External edit after the catalog loaded (same ID): Run is refused, nothing is launched, no run folder.
            File.WriteAllText(a, FixtureDocument("ext/a", "Notes idle A (edited)")); File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddSeconds(5));
            int before = Running(targetName);
            string refusal = await Refused(app, bridge, main, "ext/a");
            Assert.Contains("'ext/a': a.scenario.json was edited on disk after the catalog was loaded", refusal);
            Assert.Equal(before, Running(targetName)); Assert.False(Directory.Exists(runs) && Directory.GetDirectories(runs, "run-*").Length > 0);
            lines.Add("edited: " + refusal);

            // 2. Refresh (F2) loads the current document; the unchanged catalog runs.
            await Refresh(bridge, main);
            await bridge.Invoke(() => Assert.Contains("Notes idle A (edited)", main.TestList.Strings));
            await RunPasses(app, bridge, main, "ext/a");
            lines.Add("after refresh: ext/a passed");

            // 3. Save while discovery is loading still works.
            await bridge.Invoke(() =>
            {
                Command(main, WorkbenchDialog.Load);
                Assert.True(main.Busy);
                Assert.True(main.SaveProject(false, path));
                Assert.DoesNotContain(app.Notifications.History, n => n.Message.Contains("Wait for the run"));
            });
            await Until(bridge, () => !main.Busy, 60);
            lines.Add("save while loading: saved");

            // 4. Remove ext/b while its reload cannot happen (the executable is temporarily unavailable, as after a failed
            //    reload): the old catalog still lists ext/b, but it is no longer eligible to run.
            await bridge.Invoke(() => { main.TestList.ClearMarks(); FocusScenario(main, "ext/b"); });
            Send(WorkbenchDialog.RemoveScenario);
            var confirm = await Wait(bridge, () => Modal(app, "Remove Scenario from Project"));
            File.Move(exe, hidden);
            await bridge.Invoke(() => confirm.EndModal(Views.cmYes));
            await Settle(bridge);
            await bridge.Invoke(() =>
            {
                Assert.False(main.Busy);
                Assert.Equal([a], main.ScenarioFileList);
                Assert.Contains("Notes idle B", main.TestList.Strings);   // stale catalog entry, not reloaded
            });
            File.Move(hidden, exe);
            before = Running(targetName);
            string detached = await Refused(app, bridge, main, "ext/b");
            Assert.Contains("'ext/b': b.scenario.json is no longer attached to the project", detached);
            Assert.Equal(before, Running(targetName));
            Assert.Single(Directory.GetDirectories(runs, "run-*"));   // only the passing run of step 2
            lines.Add("removed, reload not done: " + detached);

            // 5. Refresh: ext/b leaves the catalog and its stale mark is reported, not remapped; ext/a still runs.
            await bridge.Invoke(() => Assert.Contains("ext/b", main.TestList.MarkedIds));   // still marked from the refused run
            await Refresh(bridge, main);
            await bridge.Invoke(() =>
            {
                Assert.DoesNotContain("Notes idle B", main.TestList.Strings);
                Assert.DoesNotContain("ext/b", main.TestList.MarkedIds);
                Assert.Contains(app.Notifications.History, n => n.Message.Contains("Missing/stale scenario IDs were removed from the marks: ext/b"));
            });
            await RunPasses(app, bridge, main, "ext/a");
            lines.Add("after refresh: ext/b gone (stale mark reported), ext/a passed");
        });
        Evidence("tui-freshness.txt", lines);
        Assert.True(File.Exists(b));   // Remove never deletes the document
        Delete(root);
    }

    private static async Task<string> Refused(WorkbenchApp app, Bridge bridge, WorkbenchDialog main, string id)
    {
        int notices = 0;
        await bridge.Invoke(() => { notices = app.Notifications.History.Count; main.TestList.ClearMarks(); main.TestList.Toggle(id); });
        Press(Keys.kbF5);
        await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog ?? (object?)(app.DeskTop!.TopView() as WorkbenchRunBasicDialog));
        Press(Keys.kbEnter);
        var notice = await Wait(bridge, () => app.Notifications.History.Skip(notices).FirstOrDefault(n => n.Message.StartsWith("Nothing was run")), 30);
        await Settle(bridge);
        await bridge.Invoke(() => Assert.False(main.Busy));
        return notice.Message;
    }

    private static async Task RunPasses(WorkbenchApp app, Bridge bridge, WorkbenchDialog main, string id)
    {
        int notices = 0;
        await bridge.Invoke(() => { notices = app.Notifications.History.Count; main.TestList.ClearMarks(); main.TestList.Toggle(id); });
        Press(Keys.kbF5);
        await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog ?? (object?)(app.DeskTop!.TopView() as WorkbenchRunBasicDialog));
        Press(Keys.kbEnter);
        var notice = await Wait(bridge, () => app.Notifications.History.Skip(notices).FirstOrDefault(n => n.Message.StartsWith("Run finished")), 120);
        Assert.StartsWith("Run finished: PASSED", notice.Message);
        await Until(bridge, () => !main.Busy, 60);
    }

    private static async Task Refresh(Bridge bridge, WorkbenchDialog main)
    {
        await bridge.Invoke(() => Command(main, WorkbenchDialog.Load));
        await Until(bridge, () => !main.Busy, 60);
    }

    // ── C. strict fixture-only binding ────────────────────────────────────────────

    private static string BoundDocument(string id, string fixture, string checkpoint) =>
        $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"{{id}}","title":"{{id}}","description":"d","group":"external","fixture":"{{fixture}}","expectedOutcome":"passed","steps":[{"kind":"checkpoint","id":"{{checkpoint}}"}]}""";

    [Fact]
    public void LibraryBindingIsStrictAndTheLegacyAllowanceIsExplicitPerId()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        try
        {
            var legacy = new TestScenario(LegacyId, "Legacy", "sample", "d").Grid(60, 20).Action("act", _ => { }).Checkpoint("compiled").Descriptor;
            var fixtureOnly = new TestScenario(FixtureId, "Fixture", "sample", "d").Grid(60, 20).RegisterCheckpointExpectation("idle", _ => true, "idle").FixtureOnly("r1").Descriptor;
            var onLegacy = ExternalScenarios.Load(Write(root, "legacy.json", BoundDocument("ext/legacy", LegacyId, "authored")));
            var onFixture = ExternalScenarios.Load(Write(root, "fixture.json", BoundDocument("ext/fixture", FixtureId, "idle")));

            // Default: the fixture-only registration binds with its contract fingerprint; the compiled scenario is refused.
            var bound = ExternalScenarios.Merge([legacy, fixtureOnly], [onFixture]).Single(s => s.Id == "ext/fixture");
            Assert.StartsWith(ExternalScenarios.ContractFingerprintPrefix, bound.External!.FixtureHash);
            var refused = Assert.Throws<ExternalScenarioException>(() => ExternalScenarios.Merge([legacy, fixtureOnly], [onLegacy]));
            Assert.Contains("is an ordinary compiled scenario, not a fixture-only registration", refused.Message);
            Assert.Contains("\"legacyFixtures\"", refused.Message);
            // An allowance for another ID (or a wildcard-looking text) does not open the door.
            Assert.Throws<ExternalScenarioException>(() => ExternalScenarios.Merge([legacy], [onLegacy], ["sample/dialog/other"]));
            Assert.Throws<ExternalScenarioException>(() => ExternalScenarios.Merge([legacy], [onLegacy], ["*"]));
            Assert.Throws<ExternalScenarioException>(() => ExternalScenarios.Merge([legacy], [onLegacy], []));
            // Explicit allowance: the legacy binding keeps its unchanged legacy descriptor fingerprint and unbound checkpoints.
            var allowed = ExternalScenarios.Merge([legacy], [onLegacy], [LegacyId]).Single(s => s.Id == "ext/legacy");
            Assert.Equal(ExternalScenarios.Fingerprint(legacy), allowed.External!.FixtureHash);
            Assert.Equal(ExternalScenarios.Fingerprint(legacy), ExternalScenarios.BindingFingerprint(legacy));
            Assert.Equal(onLegacy.ContentHash, allowed.External.ContentHash);
            // The fixture-only rules are unchanged by the allowance.
            var unknownCheckpoint = ExternalScenarios.Load(Write(root, "unknown.json", BoundDocument("ext/unknown", FixtureId, "made-up")));
            Assert.Throws<ExternalScenarioException>(() => ExternalScenarios.Merge([fixtureOnly], [unknownCheckpoint], [FixtureId]));

            // Workbench: Check and New Scenario follow the same rule.
            var check = ScenarioFiles.Check([Path.Combine(root, "legacy.json")], Target, [legacy, fixtureOnly], false);
            Assert.Equal("Unbound", check[0].Status); Assert.Contains("not a fixture-only registration", check[0].Problem);
            Assert.Equal("Bound", ScenarioFiles.Check([Path.Combine(root, "legacy.json")], Target, [legacy, fixtureOnly], false, null, [LegacyId])[0].Status);
            Assert.Equal([FixtureId], ScenarioFiles.BindableFixtures([legacy, fixtureOnly]).Select(s => s.Id));
            Assert.Equal([FixtureId, LegacyId], ScenarioFiles.BindableFixtures([legacy, fixtureOnly], [LegacyId]).Select(s => s.Id));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ProjectLegacyFixturesRoundTripAndAbsentFieldIsNotWritten()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        try
        {
            string plain = SaveProject(root, "plain.twproj", new WorkbenchProject { Target = Target, OutputRoot = root });
            Assert.DoesNotContain("legacyFixtures", File.ReadAllText(plain));
            Assert.Null(WorkbenchProject.Load(plain).LegacyFixtures);
            string legacy = SaveProject(root, "legacy.twproj", new WorkbenchProject { Target = Target, OutputRoot = root, LegacyFixtures = [LegacyId] });
            Assert.Equal([LegacyId], WorkbenchProject.Load(legacy).LegacyFixtures);
            foreach (string[] bad in new[] { new[] { "" }, new[] { "*" }, new[] { "sample/*" }, new[] { LegacyId, LegacyId } })
                Assert.Throws<ArgumentException>(() => (new WorkbenchProject { Target = Target, OutputRoot = root, LegacyFixtures = bad }).Validate());
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task CliRefusesLegacyCompiledBindingByDefaultAndAcceptsTheExplicitAllowance()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        try
        {
            string legacyDoc = Write(root, "legacy.json", BoundDocument("ext/legacy", LegacyId, "authored"));
            string fixtureDoc = Write(root, "fixture.json", BoundDocument("ext/fixture", FixtureId, "idle"));
            string legacyProject = SaveProject(root, "legacy.twproj", new WorkbenchProject { Target = Target, OutputRoot = root, ScenarioFiles = [legacyDoc] });
            string fixtureProject = SaveProject(root, "fixture.twproj", new WorkbenchProject { Target = Target, OutputRoot = root, ScenarioFiles = [fixtureDoc] });
            var lines = new List<string>();
            async Task<(int Code, string Out, string Err)> Cli(params string[] args)
            {
                var output = new StringWriter(); var error = new StringWriter();
                int code = await global::Cli.RunAsync(args, output, error);
                lines.Add($"$ {string.Join(' ', args.Select(a => a.Replace(root, "<tmp>")))} -> exit {code}\n{error.ToString().Replace(root, "<tmp>").Trim()}");
                return (code, output.ToString(), error.ToString());
            }
            var strict = await Cli("list", legacyProject);
            Assert.Equal(5, strict.Code);
            Assert.Contains("not a fixture-only registration", strict.Err);
            var allowed = await Cli("list", legacyProject, "--legacy-fixture", LegacyId);
            Assert.Equal(0, allowed.Code); Assert.Contains("ext/legacy", allowed.Out);
            string allowedProject = SaveProject(root, "legacy-allowed.twproj", WorkbenchProject.Load(legacyProject) with { LegacyFixtures = [LegacyId] });
            var projectAllowed = await Cli("list", allowedProject);
            Assert.Equal(0, projectAllowed.Code); Assert.Contains("ext/legacy", projectAllowed.Out);
            var fixture = await Cli("list", fixtureProject);
            Assert.Equal(0, fixture.Code); Assert.Contains("ext/fixture", fixture.Out);
            Evidence("cli-binding.txt", lines);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task CompiledNegativeSelfTestStaysExecutableThroughItsDeliberateSelection()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        try
        {
            var runner = new ScenarioRunner();
            var catalog = await runner.DiscoverAsync(Target);
            var negative = catalog.Single(s => s.Id == NegativeId);
            Assert.Equal(ScenarioOutcomes.Failed, negative.ExpectedResult);
            // Never in bulk selection; explicit selection runs it and it fails exactly as intended.
            Assert.DoesNotContain(ScenarioSelection.Resolve(catalog, [], null, true, false), s => s.Id == NegativeId);
            var batch = await runner.RunAsync(Target, [negative], new RunOptions(Path.Combine(root, "runs"), ["80x25"]));
            var result = Assert.Single(batch.Results);
            Assert.Equal(ScenarioOutcomes.Failed, result.Outcome);
            Assert.NotEmpty(result.Checkpoints);
            Assert.True(result.CleanupSucceeded);
        }
        finally { Delete(root); }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static void FocusScenario(WorkbenchDialog main, string id)
    {
        main.Select();
        for (int i = 0; i < main.TestList.range; i++) { main.TestList.sel = i; if (main.TestList.Current?.Id == id) return; }
        throw new InvalidOperationException(id + " is not listed.");
    }
    private static int Running(string name) => Process.GetProcessesByName(name).Length;
    private static void Evidence(string name, IEnumerable<string> lines)
    {
        if (Environment.GetEnvironmentVariable("TSWB_EXTERNAL_DOCUMENT_EVIDENCE") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root); File.WriteAllLines(Path.Combine(root, name), lines);
    }
    private static string Write(string folder, string name, string text) { string path = Path.Combine(folder, name); File.WriteAllText(path, text); return path; }
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "fu1-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            using var app = new WorkbenchApp(Target, output, new(Target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") }, showStart: false);
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
            Assert.True(worker.Wait(TimeSpan.FromSeconds(300)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
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
