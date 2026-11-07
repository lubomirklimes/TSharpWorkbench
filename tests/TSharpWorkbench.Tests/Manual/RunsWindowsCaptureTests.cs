using System.Diagnostics;
using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;
using Host = TSharpWorkbench.Tests.Core.TestWorkbenchHostTests;

namespace TSharpWorkbench.Tests.Manual;

// Manual: drives the real WorkbenchApp (NullDriver, 120x36) through the windows of the chapter
// "Běhy, výsledky a baselines" and writes logical PNGs to <repo>/artifacts/runs-windows/*.png.
// Skipped in normal runs; runs under "Debug Test" or with RUNS_WINDOWS_CAPTURE set.
[Collection("NonParallel")]
public sealed class RunsWindowsCaptureTests
{
    public sealed class ManualFactAttribute : FactAttribute
    {
        public ManualFactAttribute()
        {
            if (!Debugger.IsAttached && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUNS_WINDOWS_CAPTURE")))
                Skip = "Manual capture: run with Debug Test (or set RUNS_WINDOWS_CAPTURE).";
        }
    }

    private const int Width = 120, Height = 36;
    private const string Scenario = "sample/dialog/basic", Status = "sample/status/context", Gated = "desktop-status";

    [ManualFact, Trait("Category", "Manual")]
    public async Task CaptureRunsResultsAndBaselines()
    {
        string output = Path.Combine(RepositoryRoot(), "artifacts", "runs-windows");
        Directory.CreateDirectory(output);
        var errors = new List<string>();
        await NativeSession(output, errors);
        await LegacySession(output, errors);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static Func<string, Func<Task>, Func<Task>?, Task> Shooter(Bridge bridge, string output, List<string> errors) => async (name, open, close) =>
    {
        try
        {
            await open();
            await Settle(bridge);
            await bridge.Invoke(() => File.WriteAllBytes(Path.Combine(output, name + ".png"), LogicalPng.Encode(ScreenSnapshot.CopyRoot())));
        }
        catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        try { if (close is not null) await close(); }
        catch (Exception ex) { errors.Add($"{name} (close): {ex.Message}"); }
    };

    // Native .tsw project: Configure Test Run, Run Explorer, Execution Details, Results, Baseline Explorer, candidate approval.
    private static async Task NativeSession(string output, List<string> errors)
    {
        string root = Temp(), archive = Path.Combine(root, "Runs.tsw");
        Directory.CreateDirectory(root);
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            using (var native = NativeProject.Create(archive, "Runs"))
            {
                string manifest = Path.Combine(root, "visual-baselines.json");
                File.WriteAllText(manifest, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat,
                    [], [new(Scenario, "initial", ["80x25"], "Sample dialog baseline")]), ScenarioRunner.Json));
                native.Session.AdoptBaselineStore(manifest, WorkbenchDialog.ValidateAdoptedStore);
                native.Store(new() { Name = "Runs", Target = Host.SampleTarget, OutputRoot = Path.Combine(root, "external"),
                    TestType = "visual", BaselineStore = native.Session.ResolveArtifact(NativeProject.BaselineArtifact) });
                native.Session.PutScenario(new(Scenario, null, null, "diagnostic-logical"));
                native.Session.PutEnvironment(new("a-local", "Local", WorkbenchMatrix.LocalOs, "NullDriver", 80, 25, "none"));
                native.Session.PutEnvironment(new("b-foreign", "Foreign", WorkbenchMatrix.LocalOs == "linux" ? "windows" : "linux", "NullDriver", 80, 25, "none"));
                native.Session.PutTest(new("test-1", "Dialog opens and closes", "", "", Scenario, ["b-foreign", "a-local"]));
                native.Session.Close(CloseChoice.Save);
            }
            await Drive(Width, Height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                var shot = Shooter(bridge, output, errors);
                Task CloseModal(string title) { Press(Keys.kbEsc); return Until(bridge, () => Modal(app, title) is null, 10); }

                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.OpenNativeProject(archive)));
                var project = await Loaded(app, bridge, 1);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Send(WorkbenchTestExplorer.Command);
                await Wait(bridge, () => project.TestExplorer);
                Press(Keys.kbSpace);

                await shot("configure-test-run", async () => { Press(Keys.kbF5); await Wait(bridge, () => Modal(app, "Configure Test Run"), 10); }, null);
                await shot("run-explorer", async () =>
                {
                    Press(Keys.kbDown); Press(Keys.kbSpace); Press(Keys.kbAltS);
                    await Until(bridge, () => !project.Busy && project.Native!.Session.ListArtifacts("runs/").Any(p => p.EndsWith("/matrix.json")), 90);
                    Send(WorkbenchDialog.ProgressWindow);
                    await Wait(bridge, () => app.DeskTop!.current as WorkbenchRunExplorer, 10);
                }, null);
                await shot("execution-details", async () => { Press(Keys.kbEnter); await Wait(bridge, () => Modal(app, "Execution Details"), 10); }, null);
                await shot("results", async () => { Press(Keys.kbAltO); await Wait(bridge, () => app.DeskTop!.current as WorkbenchResultsDialog, 10); }, null);
                await shot("rerun-configure-test-run", async () => { Send(WorkbenchDialog.Rerun); await Wait(bridge, () => Modal(app, "Configure Test Run"), 10); },
                    () => CloseModal("Configure Test Run"));

                WorkbenchBaselineExplorer? explorer = null;
                await shot("baseline-explorer", async () => { Send(WorkbenchDialog.BaselineBrowse); explorer = await Wait(bridge, () => app.DeskTop!.current as WorkbenchBaselineExplorer, 10); }, null);
                await shot("approve-baseline-candidate", async () =>
                {
                    await bridge.Invoke(() => Descendants(explorer!).OfType<TInputLine>().Single().SetData("Candidate"));
                    Press(Keys.kbAltL); Press(Keys.kbEnter);
                    await Wait(bridge, () => Modal(app, WorkbenchCandidateDialog.ApproveTitle), 10);
                }, null);
                await shot("approve-baseline-confirmation", async () =>
                {
                    var review = (WorkbenchCandidateDialog)Modal(app, WorkbenchCandidateDialog.ApproveTitle)!;
                    await bridge.Invoke(() => review.Confirm.Select());
                    Press(Keys.kbSpace); Press(Keys.kbAltA);
                    await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)), 10);
                }, async () =>
                {
                    await CloseModal(WorkbenchBaselineDialogs.ConfirmTitle(false));
                    if (Modal(app, WorkbenchCandidateDialog.ApproveTitle) is not null) await CloseModal(WorkbenchCandidateDialog.ApproveTitle);
                });
                await bridge.Invoke(() => project.SaveProject(false));
            }, showStart: true);
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Delete(root);
        }
    }

    // Legacy .twproj project: Run Tests / Run Configuration, Run Monitor, Results, comparison, candidate review and
    // replacement, Baseline Store Status, Record Interaction.
    private static async Task LegacySession(string output, List<string> errors)
    {
        string root = Temp();
        Directory.CreateDirectory(root);
        var previousRecord = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string runs = Path.Combine(root, "runs"), store = Path.Combine(root, "store", "visual-baselines.json");
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
                [new(Status, Gated, ["80x25"], "Capture store")]), ScenarioRunner.Json));
            string projectPath = SaveProject(root, "visual.twproj", new WorkbenchProject
            {
                Name = "Visual sample", Target = Host.SampleTarget, OutputRoot = runs, SelectedIds = [Status], BaselineStore = store,
                TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"]
            });
            var set = VisualBaselineSet.Load(store);
            string key = VisualBaselineSet.Key(Status, Gated, "80x25"), approved = set.ApprovedCells(key);

            await Drive(Width, Height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                var shot = Shooter(bridge, output, errors);
                Task CloseModal(string title) { Press(Keys.kbEsc); return Until(bridge, () => Modal(app, title) is null, 10); }

                await Loaded(app, bridge, 1);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);

                // Run configuration dialogs (either may open first depending on the stored view).
                Press(Keys.kbF5);
                var first = await Wait(bridge, () => app.DeskTop!.TopView() as TDialog is { } d && (d is WorkbenchRunDialog || d is WorkbenchRunBasicDialog) ? d : null, 10);
                if (first is WorkbenchRunBasicDialog)
                {
                    await shot("run-tests-basic", () => Task.CompletedTask, null);
                    await shot("run-configuration-advanced", async () => { Press(Keys.kbAltE); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog, 10); }, null);
                }
                else
                {
                    await shot("run-configuration-advanced", () => Task.CompletedTask, null);
                    await shot("run-tests-basic", async () => { Press(Keys.kbAltB); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunBasicDialog, 10); }, null);
                }
                Press(Keys.kbEsc);
                await Until(bridge, () => app.DeskTop!.TopView() is not (WorkbenchRunDialog or WorkbenchRunBasicDialog), 10);

                async Task<string> RunVisual(string? monitorShot)
                {
                    var earlier = Directory.Exists(runs) ? Directory.GetDirectories(runs, "run-*") : [];
                    Press(Keys.kbF5);
                    await Wait(bridge, () => app.DeskTop!.TopView() as TDialog is WorkbenchRunDialog or WorkbenchRunBasicDialog ? app.DeskTop!.TopView() : null, 10);
                    Press(Keys.kbEnter);
                    if (monitorShot is not null)
                        await shot(monitorShot, () => Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().FirstOrDefault(), 30), null);
                    else await Until(bridge, () => main.Busy, 30);
                    await Until(bridge, () => !main.Busy, 120);
                    return Directory.GetDirectories(runs, "run-*").Except(earlier).Single();
                }

                await RunVisual("run-monitor");
                await shot("run-monitor-completed", () => Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().FirstOrDefault(), 5), null);

                WorkbenchResultsDialog? results = null;
                await shot("results-legacy", async () => { Press(Keys.kbF6); results = await Wait(bridge, () => main.ResultsWindow, 10); }, null);
                await shot("baseline-comparison-missing", async () =>
                {
                    await bridge.Invoke(() => Command(results!, WorkbenchResultsDialog.CompareCheckpoint));
                    await Wait(bridge, () => main.Comparison, 10);
                }, async () => { Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null, 10); });

                await shot("approve-baseline-candidate-legacy", async () =>
                {
                    await bridge.Invoke(() => Send(WorkbenchDialog.ReviewCandidates));
                    await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog, 10);
                }, null);
                await shot("approve-baseline-confirmation-legacy", async () =>
                {
                    var dialog = (WorkbenchCandidateDialog)app.DeskTop!.TopView()!;
                    await bridge.Invoke(() => { dialog.Confirm.Press(0); Nudge(dialog); Command(dialog, Views.cmYes); });
                    await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)), 10);
                }, async () =>
                {
                    var confirm = Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false))!;
                    await bridge.Invoke(() => Command(confirm, Views.cmYes));
                    await Until(bridge, () => File.Exists(approved), 20);
                });

                // A deliberate mismatch in the disposable store gives the replacement path.
                try
                {
                    var screen = ScreenSnapshot.Deserialize(File.ReadAllBytes(approved));
                    var cells = Enumerable.Range(0, screen.Width * screen.Height).Select(i => screen[i % screen.Width, i / screen.Width]).ToArray();
                    cells[0] = new('!', cells[0].Attr); cells[1] = new(cells[1].Character, (byte)(cells[1].Attr ^ 0x0f));
                    byte[] changed = new ScreenSnapshot(screen.Width, screen.Height, cells).Serialize(); File.WriteAllBytes(approved, changed);
                    string meta = set.ApprovedMetadata(key); var file = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllText(meta), ScenarioRunner.Json)!;
                    File.WriteAllText(meta, JsonSerializer.Serialize(file with { CellsSha256 = ScreenSnapshot.Hash(changed) }, ScenarioRunner.Json));
                    await RunVisual(null);
                }
                catch (Exception ex) { errors.Add($"mismatch-run: {ex.Message}"); }

                WorkbenchComparisonWindow? comparison = null;
                await shot("baseline-comparison-mismatch", async () =>
                {
                    await bridge.Invoke(() => Command(main.ResultsWindow!, WorkbenchResultsDialog.CompareCheckpoint));
                    comparison = await Wait(bridge, () => main.Comparison, 10);
                }, null);
                await shot("baseline-comparison-expected", async () => await bridge.Invoke(() => Command(comparison!, WorkbenchComparisonWindow.Expected)), null);
                await shot("replace-approved-baseline", async () =>
                {
                    _ = bridge.Invoke(() => Command(comparison!, WorkbenchComparisonWindow.Review));
                    await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog, 10);
                }, null);
                await shot("replace-approved-baseline-confirmation", async () =>
                {
                    var dialog = (WorkbenchCandidateDialog)app.DeskTop!.TopView()!;
                    await bridge.Invoke(() => { dialog.Confirm.Press(0); Nudge(dialog); Command(dialog, Views.cmYes); });
                    await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(true)), 10);
                }, async () =>
                {
                    await CloseModal(WorkbenchBaselineDialogs.ConfirmTitle(true));
                    await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchCandidateDialog, 10);
                    Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null, 10);
                });

                await shot("baseline-store-status", async () =>
                {
                    _ = bridge.Invoke(() => WorkbenchBaselineDialogs.StoreStatus(app.DeskTop!, store, "store/visual-baselines.json", 1, app.Notifications));
                    await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.StoreStatusTitle), 10);
                }, () => CloseModal(WorkbenchBaselineDialogs.StoreStatusTitle));

                WorkbenchScenarioEditorWindow? editor = null;
                await shot("record-interaction", async () =>
                {
                    string doc = Path.Combine(root, "docs", "recorded.scenario.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(doc)!);
                    var saved = ScenarioDocument.New("recorded/demo", "Recorded demo", "Recorder window capture document.", checkpoint: "initial").Save(doc);
                    Assert.True(saved.Saved, saved.Message);
                    await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                    await bridge.Invoke(() => editor = main.OpenScenarioEditor(doc));
                    await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor!));
                    await Wait(bridge, () => WorkbenchRecordingWindow.Active, 10);
                }, async () =>
                {
                    await bridge.Invoke(() => WorkbenchRecordingWindow.Active?.Close());
                    if (editor is not null) await bridge.Invoke(() => editor.CloseApproved());
                });
            });
        }
        finally
        {
            WorkbenchScenarioEditorWindow.RecordRequested = previousRecord;
            Delete(root);
        }
    }

    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found.");
    }
}
