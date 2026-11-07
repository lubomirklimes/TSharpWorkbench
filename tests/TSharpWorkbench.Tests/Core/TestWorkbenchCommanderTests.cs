using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Opt-in consumer integration: build the real diagnostic target with the Commander launcher first.
// No dependency on the sibling checkout is introduced into ordinary test/build execution.
// Since the 213 product scenarios are external JSON documents (CommanderExternal.twproj) bound to
// fixture-only registrations; the target alone lists only the two compiled self-tests. The workflow therefore opens a copy of
// that project (TSWB_COMMANDER_PROJECT) and uses the external IDs; catalog 215 = 213 external + 2 self-tests.
[Collection("NonParallel")]
public sealed class TestWorkbenchCommanderTests
{
    [Fact]
    public void TargetBuildIdentityIncludesManagedAssemblyAndRefusesMissingTargets()
    {
        string root = Path.Combine(Path.GetTempPath(), "wb-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "owned.exe"), dll = Path.Combine(root, "owned.dll");
            Assert.Null(WorkbenchDialog.BuildIdentity(exe));
            File.WriteAllText(exe, "apphost"); File.WriteAllText(dll, "first build");
            string first = WorkbenchDialog.BuildIdentity(exe)!;
            Assert.Equal(first, WorkbenchDialog.BuildIdentity(exe));
            File.WriteAllText(dll, "second build"); Assert.NotEqual(first, WorkbenchDialog.BuildIdentity(exe));
            string second = WorkbenchDialog.BuildIdentity(exe)!;
            File.WriteAllText(exe, "new apphost"); Assert.NotEqual(second, WorkbenchDialog.BuildIdentity(exe));
        }
        finally { Directory.Delete(root, true); }
    }
    [CommanderTheory]
    [InlineData(80, 25)]
    [InlineData(140, 40)]
    public void RealCommanderCatalogExecutionFailureCancellationAndResults(int width, int height)
    {
        string? target = Environment.GetEnvironmentVariable("TSWB_COMMANDER_TARGET");
        if (string.IsNullOrEmpty(target)) throw new InvalidOperationException("Set TSWB_COMMANDER_TARGET to a freshly built Commander diagnostic apphost.");
        Assert.True(File.Exists(target), target);
        string root = Environment.GetEnvironmentVariable("TSWB_COMMANDER_OUTPUT") ?? throw new InvalidOperationException("TSWB_COMMANDER_OUTPUT is required.");
        string evidence = Environment.GetEnvironmentVariable("TSWB_COMMANDER_EVIDENCE") ?? throw new InvalidOperationException("TSWB_COMMANDER_EVIDENCE is required.");
        Directory.CreateDirectory(evidence);
        string settings = Path.Combine(evidence, $"preferences-{width}x{height}.json");
        var (project, projectPath) = CommanderProject(Path.Combine(evidence, $"project-{width}x{height}"), target, root);
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(target, root, new(target, root, "D1", "standard") { StoragePath = settings }, project, projectPath);
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        var runs = new List<string>();
        Task worker = Task.Run(async () =>
        {
            try
            {
                var main = await Wait(bridge, () => app.Project);
                var list = await Wait(bridge, () => Children(main).OfType<ScenarioList>().SingleOrDefault(l => l.Strings.Count > 0));
                // Search is the Workspace search box; target screen sizes are chosen in Run Configuration (the
                // workspace grid radios were removed); details are rows of the Results inspector.
                var groups = main.Tree;
                var search = main.SearchBox;
                string[] sizes = ["80x25", "140x40"];
                async Task Run()
                {
                    Task configuring = bridge.Invoke(() => Command(main, WorkbenchDialog.SelectMode));
                    var dialog = await Wait(bridge, () => app.DeskTop.TopView() as WorkbenchRunDialog);
                    await bridge.Invoke(() =>
                    {
                        dialog.Sizes.value = (sizes.Contains("80x25") ? 1u : 0u) | (sizes.Contains("140x40") ? 2u : 0u); dialog.Sizes.DrawView();
                        Key(dialog, Keys.kbTab); Assert.Empty(dialog.Problems); Command(dialog, Views.cmOK);
                    });
                    await configuring;
                }
                void Group(string name) => groups.FocusItemNum(Enumerable.Range(0, groups.range).Single(i => GroupName(groups.GetText(i, 200)) == name));
                void Capture(string name)
                {
                    var screen = ScreenSnapshot.CopyRoot();
                    string stem = Path.Combine(evidence, $"{name}-{width}x{height}");
                    File.WriteAllBytes(stem + ".png", LogicalPng.Encode(screen));
                    File.WriteAllBytes(stem + ".tvs1", screen.Serialize());
                    File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new { name, width, height, captureLayer = "Logical UI", driver = "NullDriver", target, renderer = LogicalPng.Renderer }, ScenarioRunner.Json));
                }
                string[] Marks() => list.MarkedIds.Order().ToArray();
                string[] before = Directory.GetDirectories(root, "run-*");
                async Task<string> Finished()
                {
                    await Wait(bridge, () => main.Busy ? "started" : null);
                    await Wait(bridge, () => !main.Busy ? "done" : null);
                    string run = Directory.GetDirectories(root, "run-*").Except(before).Single();
                    before = Directory.GetDirectories(root, "run-*"); runs.Add(run); return run;
                }
                async Task Results(string name, bool failed, bool locations)
                {
                    await bridge.Invoke(() => Command(main, WorkbenchDialog.Results));
                    var results = await Wait(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                    await bridge.Invoke(() => { if (failed) Command(results, WorkbenchResultsDialog.ToggleFailed); Capture(name + "-results"); });
                    if (locations)
                    {
                        // Run folder opens through the system shell; the test seam records it instead of launching Explorer.
                        var opened = new List<string>(); var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, _) => opened.Add(p);
                        try { await bridge.Invoke(() => { Command(results, WorkbenchResultsDialog.Locations); Assert.StartsWith(root, Assert.Single(opened)); Capture("output-locations"); }); }
                        finally { WorkbenchDialog.Opener = previous; }
                    }
                    await bridge.Invoke(() =>
                    {
                        Assert.Contains(results.InspectorRows, l => l.Contains("cleanup succeeded"));
                        if (failed)
                        {
                            // Enter on the failed execution selects its failed checkpoint: assertions with expected/actual.
                            results.ExecutionsList.Select(); Key(results, Keys.kbEnter);
                            string[] lines = results.InspectorRows;
                            Assert.Contains(lines, l => l.TrimStart().StartsWith("expected", StringComparison.Ordinal));
                            Assert.Contains(lines, l => l.TrimStart().StartsWith("actual", StringComparison.Ordinal));
                            results.Inspector.Select(); results.Inspector.FocusItemNum(Math.Max(0, Array.FindIndex(lines, l => l.Contains("expected"))));
                        }
                        Capture(name + "-details"); Key(results, Keys.kbEsc);
                    });
                    await Wait(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any() ? null : "closed");
                }
                await bridge.Invoke(() =>
                {
                    Assert.Equal(CatalogCount, list.Strings.Count);
                    Assert.Equal(list.Strings.Count, list.Strings.Distinct().Count());   // each external product scenario once, no fixture rows
                    for (int i = 1; i <= 10; i++) Assert.Contains(Enumerable.Range(0, groups.range), n => GroupName(groups.GetText(n, 200)) == "D" + i);
                    foreach (string name in new[] { "keyboard", "input-extended", "menu-status" }) Assert.Contains(Enumerable.Range(0, groups.range), n => GroupName(groups.GetText(n, 200)) == name);
                    Capture("loaded"); Group("D1"); Assert.Equal(22, list.range); Capture("group-filtered");
                    search.SetData("New File"); Key(main, Keys.kbTab); Assert.Equal(1, list.range);
                    list.Select(); Key(main, Keys.kbSpace); Assert.Contains("external/commander/dialogs/new-file/default", list.MarkedIds);
                    search.SetData("external/commander/dialogs/new-file/default"); Key(main, Keys.kbTab); Assert.Equal(1, list.range);
                    // The fixture-only registration behind it is not a runnable row: its ID finds only the external row and cannot be marked.
                    search.SetData("commander/dialogs/new-file/default"); Key(main, Keys.kbTab); Assert.Equal(1, list.range);
                    Assert.Throws<ArgumentException>(() => list.Toggle("commander/dialogs/new-file/default"));
                    Group("D2"); Assert.Single(list.MarkedIds); search.SetData(""); Key(main, Keys.kbTab);
                    Command(main, WorkbenchDialog.SelectGroup); Assert.Equal(10, list.MarkedIds.Count);
                    Command(main, WorkbenchDialog.ClearSelection); Group("All scenarios");
                    // Mark all: the 213 external product scenarios only; the expected-failure self-tests stay unmarked.
                    Command(main, WorkbenchDialog.MarkAll);
                    Assert.Equal(213, list.MarkedIds.Count); Assert.All(list.MarkedIds, id => Assert.StartsWith("external/", id));
                    Command(main, WorkbenchDialog.ClearSelection);
                    list.Toggle("external/commander/dialogs/new-file/default"); list.Toggle("external/commander/input/hidden-indicators");
                    Group("D1"); Capture("marked");
                });
                sizes = ["80x25", "140x40"]; await Run();
                await Wait(bridge, () => main.Busy && Children(app.DeskTop).OfType<WorkbenchRunMonitor>().Any() ? "running" : null);
                await bridge.Invoke(() => Capture("progress"));
                string success = await Finished();
                using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(success, "run.json"))))
                {
                    Assert.Equal(4, json.RootElement.GetProperty("passed").GetInt32());
                    foreach (var r in json.RootElement.GetProperty("results").EnumerateArray()) { Assert.True(r.GetProperty("fixtureIntegrityVerified").GetBoolean()); Assert.True(r.GetProperty("cleanupSucceeded").GetBoolean()); }
                }
                string[] marked = []; await bridge.Invoke(() => marked = Marks());
                await Results("success", false, true);
                await bridge.Invoke(() => { Assert.Equal(marked, Marks()); Command(main, WorkbenchDialog.ClearSelection); list.Toggle("selftest/commander/wrong-dialog-expectations"); });
                sizes = ["80x25"]; await Run();
                string negative = await Finished();
                using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(negative, "run.json")))) Assert.Equal("Failed", json.RootElement.GetProperty("results")[0].GetProperty("outcome").GetString());
                await Results("failed", true, false);
                await bridge.Invoke(() => { Assert.Contains("selftest/commander/wrong-dialog-expectations", list.MarkedIds); Command(main, WorkbenchDialog.ClearSelection); list.Toggle("selftest/commander/unmet-deferred-readiness"); });
                await Run();
                var monitor = await Wait(bridge, () => Children(app.DeskTop).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await Wait(bridge, () => monitor.Text.Contains("checkpoint results") ? "waiting" : null);
                await bridge.Invoke(() => { Capture("blocked-progress"); Command(main, WorkbenchDialog.CancelRun); Capture("cancelling"); });
                string cancelled = await Finished();
                using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(cancelled, "run.json"))))
                {
                    var r = json.RootElement.GetProperty("results")[0];
                    Assert.Equal("Aborted", r.GetProperty("outcome").GetString()); Assert.True(r.GetProperty("cleanupSucceeded").GetBoolean()); Assert.True(r.GetProperty("fixtureIntegrityVerified").GetBoolean());
                }
                await bridge.Invoke(() => { Assert.Contains("selftest/commander/unmet-deferred-readiness", list.MarkedIds); Command(main, WorkbenchDialog.Load); });
                await Wait(bridge, () => Text().Contains($"Loaded {CatalogCount} scenarios") ? "refreshed" : null);
                await bridge.Invoke(() => { Assert.Contains("selftest/commander/unmet-deferred-readiness", list.MarkedIds); Capture("refreshed"); Command(main, WorkbenchDialog.ClearSelection); list.Toggle("external/commander/menu/f10-open-close"); });
                await Run();
                string recovery = await Finished();
                using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(recovery, "run.json")))) Assert.Equal(1, json.RootElement.GetProperty("passed").GetInt32());
                // Help > Test modes and drivers (replaces the former "Test mode capabilities" list).
                Task modes = bridge.Invoke(() => Command(app, WorkbenchApp.TestModes));
                var modeDialog = await Wait(bridge, () => app.DeskTop.TopView() is TDialog d && !ReferenceEquals(d, main) ? d : null);
                await bridge.Invoke(() => { Capture("capabilities"); Key(modeDialog, Keys.kbEsc); }); await modes;
                // Project Properties: a changed executable shows the inline consequence (replaces "Change project target?").
                Task advanced = bridge.Invoke(() => Command(main, WorkbenchDialog.Advanced));
                var settingsDialog = (WorkbenchPropertiesDialog)await Wait(bridge, () => app.DeskTop.TopView() as WorkbenchPropertiesDialog);
                string missing = Path.Combine(root, "missing-target.exe");
                await bridge.Invoke(() =>
                {
                    Capture("advanced"); settingsDialog.ExecutableField.SetData(missing); settingsDialog.HandleEvent(ref Nothing);
                    Assert.Contains("clears the 1 marked scenario", settingsDialog.NoticeText);
                    Assert.False(settingsDialog.Valid(Views.cmOK)); Assert.Contains("no file exists", settingsDialog.NoticeText);   // a missing executable is refused
                    Key(settingsDialog, Keys.kbEsc);
                });
                await advanced;
                await bridge.Invoke(() => { Assert.Contains("external/commander/menu/f10-open-close", list.MarkedIds); Assert.NotEmpty(list.Strings); Capture("invalidated-target"); main.CloseApproved(); Command(app, Views.cmQuit); });
                File.WriteAllText(Path.Combine(evidence, $"workflow-{width}x{height}.json"), JsonSerializer.Serialize(new { target, project = projectPath, width, height, runs, captureLayer = "Logical UI", catalogCount = CatalogCount }, ScenarioRunner.Json));
            }
            catch (Exception ex)
            {
                failure = ex;
                await bridge.Invoke(() => { app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); });
            }
        });
        int code = AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(10)), "Commander TUI worker did not finish.");
        Assert.Null(failure); Assert.Equal(0, code);
    }

    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break;
        TView first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    // 213 external product scenarios (one per fixture-only registration) + the 2 compiled self-tests.
    private const int CatalogCount = 215;
    private static string GroupName(string row) => System.Text.RegularExpressions.Regex.Replace(row.Trim(), @"^[+-] | \(\d+\)$", "");
    // A portable copy of Commander's external project beside the evidence, with the run's target and output and no marks, so
    // marks, refreshes and saves never touch the Commander checkout.
    private static (WorkbenchProject Project, string Path) CommanderProject(string folder, string target, string output)
    {
        string source = Environment.GetEnvironmentVariable("TSWB_COMMANDER_PROJECT")
            ?? throw new InvalidOperationException("Set TSWB_COMMANDER_PROJECT to Commander's diagnostics/CommanderTestTarget/ExternalScenarios/CommanderExternal.twproj.");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "commander-external.twproj");
        (WorkbenchProject.Load(source) with { Target = target, OutputRoot = output, SelectedIds = [] }).Save(path);
        var project = WorkbenchProject.Load(path);
        Assert.Equal(213, project.ScenarioFiles.Length);
        return (project, path);
    }
    private static TEvent Nothing = new() { What = Events.evNothing };
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command;
        if(command==WorkbenchDialog.Run && (view is WorkbenchDialog w && w.TestList.MarkedIds.Count>0 || view is WorkbenchApp a && a.Project?.TestList.MarkedIds.Count>0))
        {var accept=new TEvent{What=Events.evCommand};accept.message.command=Views.cmOK;view.PutEvent(ref accept);}
        view.HandleEvent(ref ev); }
    private static void Key(TView view, ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); view.HandleEvent(ref ev); }
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action) { var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } })); return done.Task; }
        public override void HandleEvent(ref TEvent ev) { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
    public sealed class CommanderTheoryAttribute : TheoryAttribute
    {
        public CommanderTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSWB_COMMANDER_TARGET")))
                Skip = "Set TSWB_COMMANDER_TARGET to a freshly built Commander diagnostic apphost (and TSWB_COMMANDER_PROJECT to its CommanderExternal.twproj).";
        }
    }

    [CommanderTheory]
    [InlineData(true)]
    public void RebuiltCommanderAtSamePathInvalidatesSelectionBeforeLaunch(bool _)
    {
        string target = Environment.GetEnvironmentVariable("TSWB_COMMANDER_TARGET")!;
        string output = Environment.GetEnvironmentVariable("TSWB_COMMANDER_OUTPUT")!;
        string clone = Path.Combine(output, "identity-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(clone);
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(target)!)) File.Copy(file, Path.Combine(clone, Path.GetFileName(file)));
        string ownedTarget = Path.Combine(clone, Path.GetFileName(target));
        var (project, projectPath) = CommanderProject(Path.Combine(clone, "project"), ownedTarget, output);
        using var driver = new DriverScope(80, 25);
        using var app = new WorkbenchApp(ownedTarget, output, new(ownedTarget, output, null, "80x25") { StoragePath = Path.Combine(clone, "settings.json") }, project, projectPath);
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                var main = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => Text().Contains($"Loaded {CatalogCount} scenarios") ? "loaded" : null);
                await bridge.Invoke(() =>
                {
                    var list = Children(main).OfType<ScenarioList>().Single(); list.Toggle("external/commander/menu/f10-open-close");
                    // Change only an owned copy, after its discovery process has exited. This copy must never execute again.
                    using (var changed = new FileStream(Path.ChangeExtension(ownedTarget, ".dll"), FileMode.Append)) changed.WriteByte(0);
                    Command(main, WorkbenchDialog.Run);
                    Assert.NotEmpty(list.Strings); Assert.Contains("external/commander/menu/f10-open-close",list.MarkedIds);
                    Assert.False(main.Busy); Assert.Contains("target build changed", Text(), StringComparison.OrdinalIgnoreCase); // refresh required; marks remain reviewable (wording: "The target build changed.")
                    main.CloseApproved(); Command(app, Views.cmQuit);
                });
            }
            catch (Exception ex) { failure = ex; await bridge.Invoke(() => { app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); }); }
        });
        AppLifecycleGuard.Run(app); Assert.True(worker.Wait(TimeSpan.FromSeconds(10))); Assert.Null(failure);
    }

    [CommanderTheory]
    [InlineData(80, 25, "Mismatch")]
    [InlineData(140, 40, "BaselineMissing")]
    [InlineData(80, 25, "NotRun")]
    public void RealCommanderVisualFailuresAreNavigable(int width, int height, string status)
    {
        string target = Environment.GetEnvironmentVariable("TSWB_COMMANDER_TARGET")!;
        string root = Environment.GetEnvironmentVariable("TSWB_COMMANDER_OUTPUT")!;
        string evidence = Environment.GetEnvironmentVariable("TSWB_COMMANDER_EVIDENCE")!;
        Directory.CreateDirectory(evidence);
        string original = Environment.GetEnvironmentVariable("TSWB_COMMANDER_BASELINES") ?? throw new InvalidOperationException("TSWB_COMMANDER_BASELINES is required.");
        string store = Path.Combine(evidence, "..", "control-" + status + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(store);
        string manifest = Path.Combine(store, "visual-baselines.json");
        File.Copy(original, manifest);
        string key = $"commander-input-dialog-mouse--typed--{width}x{height}";
        if (status == VisualStatuses.Mismatch)
        {
            string approved = Path.Combine(store, "approved"); Directory.CreateDirectory(approved);
            string source = Path.Combine(Path.GetDirectoryName(original)!, "approved", key);
            var snapshot = ScreenSnapshot.Deserialize(File.ReadAllBytes(source + ".tvs1"));
            var cells = Enumerable.Range(0, width * height).Select(i => snapshot[i % width, i / width]).ToArray();
            cells[0] = new(cells[0].Character == 'X' ? 'Y' : 'X', cells[0].Attr);
            byte[] changed = new ScreenSnapshot(width, height, cells).Serialize();
            File.WriteAllBytes(Path.Combine(approved, key + ".tvs1"), changed);
            var metadata = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllText(source + ".baseline.json"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            File.WriteAllText(Path.Combine(approved, key + ".baseline.json"), JsonSerializer.Serialize(metadata with { CellsSha256 = ScreenSnapshot.Hash(changed) }, ScenarioRunner.Json));
        }
        using var driver = new DriverScope((ushort)width, (ushort)height);
        string scenario = status == VisualStatuses.NotRun ? "commander/dialogs/new-file/default" : "commander/input/dialog-mouse";
        var options = Cli.Parse(["--target", target, "--output", root, "--size", $"{width}x{height}", "--scenario", scenario, "--baselines", manifest]);
        using var app = new WorkbenchApp(target, root, WorkbenchPreferences.Resolve(options, Path.Combine(store, "settings.json")));
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                var main = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => Text().Contains("Loaded 196 scenarios") ? "loaded" : null);
                await bridge.Invoke(() => Command(main, WorkbenchDialog.Run));
                await Wait(bridge, () => main.Busy ? "started" : null);
                await Wait(bridge, () => !main.Busy ? "done" : null);
                await bridge.Invoke(() => Command(main, WorkbenchDialog.Results));
                var results = await Wait(bridge, () => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                void Capture(string name)
                {
                    var screen = ScreenSnapshot.CopyRoot(); string stem = Path.Combine(evidence, $"{name}-{width}x{height}");
                    File.WriteAllBytes(stem + ".png", LogicalPng.Encode(screen)); File.WriteAllBytes(stem + ".tvs1", screen.Serialize());
                    File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new { name, width, height, captureLayer = "Logical UI", driver = "NullDriver", target, controlledBaselineStore = store }, ScenarioRunner.Json));
                }
                // Separate Visual column (MISMATCH / MISSING / -) and Overview rows instead of the legacy
                // "Passed/VisualFail" token and the modal Result details.
                await bridge.Invoke(() =>
                {
                    string column = status == VisualStatuses.Mismatch ? "MISMATCH" : "MISSING";
                    if (status != VisualStatuses.NotRun) { Command(results, WorkbenchResultsDialog.ToggleFailed); Assert.Contains(results.ExecutionRows, r => r.Contains(column)); }
                    else Assert.DoesNotContain(results.ExecutionRows, r => r.Contains("MISMATCH") || r.Contains("MISSING"));
                    Assert.Contains("Visual", results.HeaderText); Capture(status + "-results");
                    string[] lines = results.InspectorRows;
                    if (status != VisualStatuses.NotRun)
                    {
                        Assert.Contains(lines, l => l.StartsWith("Visual", StringComparison.Ordinal) && l.Contains(column));
                        results.Inspector.Select(); results.Inspector.FocusItemNum(Array.FindIndex(lines, l => l.StartsWith("Visual", StringComparison.Ordinal)));
                    }
                    Capture(status + "-details"); Key(results, Keys.kbEsc);
                });
                await bridge.Invoke(() => { Assert.Contains(scenario, Children(main).OfType<ScenarioList>().Single().MarkedIds); main.CloseApproved(); Command(app, Views.cmQuit); });
            }
            catch (Exception ex) { failure = ex; await bridge.Invoke(() => { app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); }); }
        });
        AppLifecycleGuard.Run(app); Assert.True(worker.Wait(TimeSpan.FromSeconds(10))); Assert.Null(failure);
    }
}
