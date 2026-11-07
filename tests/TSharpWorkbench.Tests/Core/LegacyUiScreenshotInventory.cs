using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Opt-in UX screenshot inventory of the Workbench's own UI. The real WorkbenchApp runs in-process on
// NullDriver at the Workbench grid (not the target grid); every capture is the logical cell screen written as
// TVS1 + logical PNG + JSON. Input goes through the shared event queue (keys and menu commands), the same path the
// menu bar and status line use. Fixtures are disposable copies of the maintained example projects; their runs and
// the only baseline store live in a temporary workspace. Approved production stores are never read for writing.
[Collection("NonParallel")]
public sealed class LegacyUiScreenshotInventory
{
    private const string Variable = "TSWB_LEGACY_UI_SCREENSHOTS";

    // Retired in This harness drives the pre-redesign windows (Result details, Locations, capture pickers,
    // Run Options, ONE-candidate prompts) that removed or merged. Its evidence is kept unchanged in
    // artifacts/ui-inventory/; the current UI inventory is ProductQualificationTests.InventoryOfEveryCurrentWindowType
    // (TSWB_QUALIFICATION_EVIDENCE). The code stays as the record of how the baseline was produced.
    public sealed class InventoryFactAttribute : FactAttribute
    {
        public InventoryFactAttribute()
        {
            Skip = "Retired: pre-redesign UI baseline; see ProductQualificationTests.InventoryOfEveryCurrentWindowType. "
                + (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)) ? "" : Variable + " is ignored.");
        }
    }

    [InventoryFact] public void PrimaryGrid140x40() => new Session(140, 40, full: true).Run();
    [InventoryFact] public void SecondaryGrid160x50() => new Session(160, 50, full: false).Run();

    private sealed record Entry(string File, string Cells, string Metadata, string Window, string State, string Source,
        string Grid, string Fixture, string Mechanism, string Classification, bool Reached, string? Limitation);

    private sealed class Session(int width, int height, bool full)
    {
        private readonly string output = Path.GetFullPath(Environment.GetEnvironmentVariable(Variable)!);
        private readonly string repo = RepositoryRoot();
        private readonly string workspace = Path.Combine(Path.GetTempPath(), $"ui-{width}x{height}-{Guid.NewGuid():N}"[..28]);
        private readonly List<Entry> entries = [];
        private readonly List<string> notes = [];
        private WorkbenchApp app = null!;
        private Bridge bridge = null!;
        private DriverScope driver = null!;
        private string fixture = "untitled";

        internal void Run()
        {
            Directory.CreateDirectory(output); Directory.CreateDirectory(workspace);
            string tvdemo = Prepare("examples/TVDemo/tvdemo.twproj", "tvdemo");
            string commander = Prepare("examples/TSharpCommander/commander.twproj", "commander");
            string previousDirectory = Environment.CurrentDirectory;
            try
            {
                using (driver = new DriverScope((ushort)width, (ushort)height))
                {
                    string runs = Path.Combine(workspace, "untitled-runs");
                    var preferences = new WorkbenchPreferences("", runs, null, "80x25") { StoragePath = Path.Combine(workspace, "preferences.json") };
                    app = full ? new WorkbenchApp("", runs, preferences)
                        : new WorkbenchApp("", runs, preferences, WorkbenchProject.Load(tvdemo), tvdemo);
                    using (app)
                    {
                        bridge = new Bridge(); app.DeskTop!.Insert(bridge);
                        Exception? failure = null;
                        Task worker = Task.Run(async () =>
                        {
                            try { if (full) await Primary(tvdemo, commander); else await Secondary(); }
                            catch (Exception ex)
                            {
                                failure = ex;
                                await bridge.Invoke(() => { app.DeskTop.TopView()?.EndModal(Views.cmCancel); app.EndModal(Views.cmQuit); });
                            }
                        });
                        int code = AppLifecycleGuard.Run(app);
                        Assert.True(worker.Wait(TimeSpan.FromSeconds(30)), "Inventory worker did not stop.");
                        WriteManifest();
                        Assert.Null(failure); Assert.Equal(0, code);
                    }
                }
            }
            finally
            {
                Environment.CurrentDirectory = previousDirectory;
                if (Environment.GetEnvironmentVariable("TSWB_LEGACY_UI_KEEP_WORKSPACE") is not { Length: > 0 }) TryDelete(workspace);
            }
        }

        // ── primary 140x40 walk-through ─────────────────────────────────────────────

        private async Task Primary(string tvdemo, string commander)
        {
            var main = await Until(() => app.Project);
            await Stage("startup", async () =>
            {
                await Shot("workspace-untitled-startup", "Workspace (TestWorkbench main window)", "startup without project; target none", "tui (no --project)");
                await Menus("untitled");
                Press(Keys.kbF5); await UntilText("Load the catalog");
                await Shot("workspace-run-without-catalog", "Workspace", "F5 refused: catalog not loaded (status text)", "F5");
                Press(Keys.kbF2); await UntilText("Invalid target executable path");
                await Shot("workspace-load-missing-target", "Workspace", "F2 refused: no target (status text)", "F2 / Test > Refresh catalog");
            });
            await Stage("help", async () =>
            {
                await ModalShot(WorkbenchApp.Shortcuts, "Keyboard shortcuts", "help-keyboard-shortcuts", "Keyboard shortcuts (message)", "default", "Help > Keyboard shortcuts");
                await ModalShot(WorkbenchDialog.Capabilities, "Test mode capabilities", "help-test-modes-project", "Test mode capabilities (browse list)", "with project window", "Help > Test modes");
                await ModalShot(WorkbenchApp.About, "About TestWorkbench", "help-about", "About TestWorkbench (message)", "default", "Help > About");
                await ModalShot(WorkbenchDialog.OperationError, "Last operation error", "results-last-operation-error", "Last operation error (browse list)", "after F2 refusal", "Results > Last operation error");
            });
            await Stage("properties", async () =>
            {
                Send(WorkbenchDialog.Advanced); var dialog = await Modal("Project Properties");
                await Shot("project-properties-default", "Project Properties", "untitled defaults", "Project > Project Properties... / Properties button");
                await bridge.Invoke(() => Inputs(dialog).Single(i => i.origin.y == 10).SetData(""));
                Send(Views.cmOK); await UntilText("Project name is required");
                await Shot("project-properties-invalid-name", "Workspace", "Apply refused: empty project name (status text, dialog already closed)", "Project Properties > Apply");
            });
            await Stage("browse", async () =>
            {
                Press(Keys.kbF3); await Modal("Select target executable");
                await Shot("browse-target-executable", "Select target executable (file dialog)", "default", "F3 / Project > Browse Target executable...");
                await Dismiss();
                Press(Keys.kbF4); await Modal("Select output directory");
                await Shot("browse-output-directory", "Select output directory (directory dialog)", "default", "F4 / Project > Browse output directory...");
                await Dismiss();
            });
            await Stage("no-project", async () =>
            {
                Send(WorkbenchApp.CloseProject);
                await Until(() => app.Project is null || Top("Unsaved project changes") is not null ? "" : null);
                if (Top("Unsaved project changes") is not null)
                {
                    await Shot("prompt-unsaved-close-untitled", "Unsaved project changes (prompt)", "close untitled project", "Project > Close");
                    Send(Views.cmNo);
                }
                await Until(() => app.Project is null ? "" : null);
                await Shot("desktop-no-project", "Empty desktop", "no project open (Ctrl+N / Ctrl+O status line)", "Project > Close");
                await Menu("menu-project-no-project", Keys.kbAltP, "Save As", "Project menu", "no project");
                Press(Keys.kbF5); await Modal("No project");
                await Shot("message-no-project", "No project (message)", "project command without project", "F5 without project");
                await Dismiss();
                await ModalShot(WorkbenchDialog.Capabilities, "Test modes", "help-test-modes-no-project", "Test modes (message)", "without project window", "Help > Test modes");
            });
            await Stage("open-tvdemo", async () =>
            {
                Environment.CurrentDirectory = Path.GetDirectoryName(tvdemo)!;
                Press(Keys.kbCtrlO); var picker = await Modal("Open project");
                await Settle(300);
                await Shot("project-open-dialog", "Open project (file dialog)", "TVDemo project folder; tvdemo.twproj NOT listed", "Ctrl+O / Project > Open...",
                    @"File list shows only '..\': TFileDialog.Valid reads '<dir>\*.twproj' + '*.twproj' (framework defect).");
                await bridge.Invoke(() => ((TFileDialog)picker).fileName!.SetData(tvdemo));
                Send(Views.cmFileOpen);
                main = await Until(() => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 6 ? p : null);
                fixture = "examples/TVDemo/tvdemo.twproj (disposable copy)";
                await Shot("workspace-tvdemo-loaded", "Workspace", "TVDemo catalog loaded; first scenario details", "Open project + automatic F2 load");
            });
            await Stage("catalog", async () =>
            {
                await bridge.Invoke(() => { main.Tree.Select(); main.Tree.FocusItemNum(1); });
                await UntilText("Filter: tvdemo");
                await Shot("workspace-group-selected", "Workspace", "group tree: tvdemo focused (filter)", "Group tree arrows");
                await bridge.Invoke(() => { Inputs(main).Single(i => i.origin.y == 4).SetData("menu"); Nudge(main); });
                await Until(() => main.TestList.range == 1 ? "" : null);
                await Shot("workspace-search-filter", "Workspace", "search 'menu' filters scenarios", "Search input");
                await bridge.Invoke(() => { Inputs(main).Single(i => i.origin.y == 4).SetData(""); Nudge(main); main.TestList.Select(); main.TestList.FocusItemNum(0); });
                await Until(() => main.TestList.range >= 6 ? "" : null);
                await bridge.Invoke(() => main.TestList.ClearMarks());
                Press(Keys.kbSpace); await Until(() => main.TestList.MarkedIds.Count == 1 ? "" : null);
                await Shot("workspace-scenario-marked", "Workspace", "one scenario marked with Space", "Space / Enter / double-click");
                Send(WorkbenchDialog.Details); await Modal("Scenario details");
                await Shot("scenario-details", "Scenario details (browse list)", "focused scenario", "command 0x7de5 only (no menu item or key)", "Command has no menu item, key or button; reached by sending its command id.");
                await Dismiss();
                Send(WorkbenchDialog.MarkAll); await Until(() => main.TestList.MarkedIds.Count >= 6 ? "" : null);
                await Shot("workspace-mark-all", "Workspace", "all expected-pass scenarios marked", "Test > Mark All");
                await Menu("menu-test-loaded", Keys.kbAltT, "Refresh catalog", "Test menu", "catalog loaded");
                Send(WorkbenchDialog.ClearSelection); await Until(() => main.TestList.MarkedIds.Count == 0 ? "" : null);
                await bridge.Invoke(() => main.TestList.Toggle("tvdemo/menu"));
            });
            await Stage("run-configuration", async () =>
            {
                Press(Keys.kbF5); var dialog = await Modal("Run Configuration");
                await Shot("run-configuration-default", "Run Configuration", "Functional, Marked tests, project grids", "F5 / Test > Run... / Run... button");
                await bridge.Invoke(() => Radios(dialog)[0].Select());
                Press(Keys.kbDown); await UntilText("needs a baseline manifest");
                await Shot("run-configuration-visual-no-store", "Run Configuration", "Visual Regression without baseline manifest (Cannot run)", "Test type radio");
                Press(Keys.kbDown); await UntilText("unavailable for this target");
                await Shot("run-configuration-rendering-unavailable", "Run Configuration", "Rendering & Drivers: no native driver for proxy scenarios", "Test type radio");
                Send(Views.cmOK); await Modal("Cannot run");
                await Shot("run-cannot-run-backend", "Cannot run (message)", "Run refused: unsupported application backend", "Run Configuration > Run");
                await Dismiss("Run Configuration");
                Press(Keys.kbUp); Press(Keys.kbUp); await UntilText("Functional / Interaction");
                await bridge.Invoke(() => Radios(dialog)[1].Select());
                Press(Keys.kbDown); await UntilText("Selected: 6");
                await Shot("run-configuration-scope-group", "Run Configuration", "Run scope: Current group", "Run scope radio");
                Press(Keys.kbDown); await Settle();
                await Shot("run-configuration-scope-all", "Run Configuration", "Run scope: All eligible tests", "Run scope radio");
                Press(Keys.kbUp); Press(Keys.kbUp); await UntilText("Selected: 1");
                await bridge.Invoke(() => { var grids = Checks(dialog); grids.value |= 4; grids.DrawView(); var edit = Inputs(dialog).OrderBy(i => i.origin.x).First(); edit.SetData("abc"); edit.DrawView(); Nudge(dialog); });
                await UntilText("Custom width and height must be integers");
                await Shot("run-configuration-custom-invalid", "Run Configuration", "Custom grid checked with invalid width", "Screen sizes > Custom");
                await bridge.Invoke(() => { var grids = Checks(dialog); grids.value &= 3; grids.DrawView(); var edit = Inputs(dialog).OrderBy(i => i.origin.x).First(); edit.SetData("100"); edit.DrawView(); Nudge(dialog); });
                await UntilText("Selected: 1");
                // Removed the nested Proxy backend and Run Options dialogs; these states no longer exist.
                notes.Add("run-proxy-backend-* and run-options-*: nested dialogs replaced by a single Run Configuration.");
                await Dismiss("Run Configuration");
                await Dismiss();
            });
            await Stage("run-functional", async () =>
            {
                Press(Keys.kbF5); await Modal("Run Configuration");
                Send(Views.cmOK);
                await Until(() => Children(app.DeskTop).OfType<WorkbenchRunMonitor>().Any() && Text().Contains("Running ") ? "" : null, 60);
                await Shot("running-tests-progress", "Running Tests (modeless)", "functional run in progress", "Run Configuration > Run");
                Press(Keys.kbF6); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any() ? "" : null);
                await Shot("results-live-running", "Results (modeless)", "opened while the run is active", "F6 during run");
                Send(WorkbenchApp.NewProject);
                var busy = await Until(() => Top("Work in progress"), 20, optional: true);
                if (busy is not null) { await Shot("message-work-in-progress", "Work in progress (message)", "project change refused during run", "Ctrl+N during run"); await Dismiss(); }
                else notes.Add("message-work-in-progress: run finished before Ctrl+N; not reached.");
                await Until(() => !main.Busy ? "" : null, 180);
                Send(WorkbenchDialog.ProgressWindow); await UntilText("Finished:");
                await Shot("running-tests-finished", "Running Tests (modeless)", "finished, cleanup done", "Window > Running tests");
            });
            await Stage("results", async () =>
            {
                Press(Keys.kbF6); var results = await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await Until(() => Text().Contains("PASS") ? "" : null);
                await Shot("results-pass", "Results (modeless)", "functional run passed", "F6 / Results > Show results");
                Send(WorkbenchResultsDialog.ToggleFailed); await UntilText("Failed only");
                await Shot("results-failed-only-empty", "Results", "Failed only filter, nothing failed", "Results > Failed / All");
                Send(WorkbenchResultsDialog.ToggleFailed); await UntilText("All results");
                Press(Keys.kbEnter); await Modal("Result details");
                await Shot("result-details-pass", "Result details (browse list)", "passed proxy execution, top", "Results > Enter / Details");
                Press(Keys.kbPgDn); await Settle();
                await Shot("result-details-pass-scrolled", "Result details", "scrolled to checkpoint/assertion rows", "PgDn");
                await Dismiss();
                Send(WorkbenchResultsDialog.Locations); await Modal("Result locations");
                await Shot("result-locations", "Result locations (browse list)", "run / report / metadata paths", "Results > Locations");
                await Dismiss();
                Send(WorkbenchDialog.OpenCaptures); await Modal("Open capture - logical and native evidence");
                await Shot("results-captures-picker", "Open capture (captures picker)", "logical PNG checkpoints of selected execution", "Results > Open captures...", "Open hands the PNG to the OS shell; not invoked.");
                await Dismiss();
                Send(WorkbenchDialog.CompareResult); await Settle(300);
                await Shot("results-compare-refused-hidden", "Results", "Compare refused; message written to the hidden workspace status line", "Baselines > Compare selected result",
                    "No visible feedback while Results covers the workspace.");
                Send(WorkbenchApp.ProjectWindow); await UntilText("no logical baseline comparison");
                await Shot("results-compare-functional-refused", "Workspace/Results", "Compare refused for functional run (status text)", "Baselines > Compare selected result");
                Send(WorkbenchDialog.ReviewCandidates); await UntilText("Only logical baseline candidates");
                await Shot("results-review-candidates-refused", "Workspace/Results", "Review candidates refused (status text)", "Baselines > Review candidates");
                Send(WorkbenchDialog.BaselineBrowse); await UntilText("No baseline store");
                await Shot("baselines-browse-no-store", "Workspace/Results", "Browse baseline store refused: no store (status text)", "Baselines > Browse baseline store");
                await Menu("menu-results", Keys.kbAltR, "Open output location", "Results menu", "results available");
                await Menu("menu-baselines", Keys.kbAltB, "Browse baseline store", "Baselines menu", "results available");
                await Menu("menu-window-multiple", Keys.kbAltW, "Running tests", "Window menu", "workspace + Running Tests + Results open");
                await bridge.Invoke(() => results.Select());
                Send(Views.cmZoom); await Settle();
                await Shot("results-zoomed", "Results", "zoomed via frame / cmZoom", "Results frame zoom icon");
                Send(Views.cmZoom); await Settle();
                Send(Views.cmNext); await Settle();
                await Shot("window-next", "Window switching", "after Window > Next window", "Window > Next window");
                await bridge.Invoke(() => results.Select());
                Press(Keys.kbEsc); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any() ? null : "");
            });
            await Stage("run-cancel", async () =>
            {
                await bridge.Invoke(() => { main.TestList.ClearMarks(); main.TestList.Toggle("tvdemo/window"); main.TestList.Toggle("tvdemo/controls"); });
                Press(Keys.kbF5); await Modal("Run Configuration"); Send(Views.cmOK);
                await Until(() => main.Busy && Text().Contains("Running ") ? "" : null, 60);
                Send(WorkbenchDialog.CancelRun); await UntilText("Cancelling");
                await Shot("running-tests-cancelling", "Running Tests", "cancellation requested, waiting for owned cleanup", "Test > Cancel running test / Cancel button");
                await Until(() => !main.Busy ? "" : null, 120);
                await Shot("running-tests-cancelled", "Running Tests / Workspace", "cancelled; cleanup finished", "after cancellation");
                Press(Keys.kbF6); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await Settle();
                await Shot("results-aborted", "Results", "cancelled run (Aborted rows)", "F6");
                Send(WorkbenchResultsDialog.ToggleFailed); await UntilText("Failed only");
                await Shot("results-failed-only", "Results", "Failed only after cancellation", "Results > Failed / All");
                Press(Keys.kbEnter);
                if (await Until(() => Top("Result details"), 5, optional: true) is not null)
                { await Shot("result-details-aborted", "Result details", "aborted execution", "Results > Enter"); await Dismiss(); }
                else notes.Add("result-details-aborted: no visible failed row after cancellation.");
                Send(WorkbenchResultsDialog.ToggleFailed); await UntilText("All results");
                Send(WorkbenchResultsDialog.PreviousRun); await Settle();
                await Shot("results-previous-run", "Results", "Prev run cycles to earlier batch", "Results > Prev run");
                Press(Keys.kbEsc); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any() ? null : "");
            });
            await Stage("visual", async () => await Visual(main));
            await Stage("historical-run", async () =>
            {
                string? existing = Directory.Exists(Path.Combine(repo, "artifacts", "tvdemo", "runs"))
                    ? Directory.GetDirectories(Path.Combine(repo, "artifacts", "tvdemo", "runs"), "run-*").Order().LastOrDefault(d => File.Exists(Path.Combine(d, "run.json"))) : null;
                Send(WorkbenchDialog.OpenRun); var picker = await Modal("Open completed run");
                await Shot("results-open-run-dialog", "Open completed run (file dialog)", "default output location", "Results > Open Run...");
                if (existing is null) { notes.Add("historical-run: no existing artifacts/tvdemo/runs run.json."); await Dismiss(); return; }
                await bridge.Invoke(() => ((TFileDialog)picker).fileName!.SetData(Path.Combine(existing, "run.json")));
                Send(Views.cmFileOpen);
                await Until(() => Top("Open completed run") is null ? "" : null);
                await Settle(300);
                await Shot("results-historical-run", "Results", "existing completed run loaded read-only: " + Path.GetFileName(existing), "Results > Open Run...");
                if (Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any()) { Press(Keys.kbEsc); await Settle(); }
                await ModalShot(WorkbenchDialog.OperationError, "Last operation error", "results-last-operation-error-populated", "Last operation error", "after session errors", "Results > Last operation error");
            });
            await Stage("resize", async () =>
            {
                await bridge.Invoke(() => driver.Driver.SimulateResize(100, 30));
                await Until(() => ScreenSnapshot.CopyRoot().Width == 100 ? "" : null); await Settle(300);
                await Shot("workspace-resized-100x30", "Workspace", "after host resize to 100x30", "console resize", grid: "100x30");
                await bridge.Invoke(() => driver.Driver.SimulateResize((ushort)width, (ushort)height));
                await Until(() => ScreenSnapshot.CopyRoot().Width == width ? "" : null); await Settle(300);
            });
            await Stage("project-files", async () =>
            {
                await Until(() => main.Dirty ? "" : null, 5);
                await Shot("workspace-dirty-title", "Workspace", "unsaved changes (* in title)", "after marks/grid changes");
                Press(Keys.kbCtrlS); await UntilText("Saved project");
                await Shot("project-saved", "Workspace", "Ctrl+S saved project (status text)", "Ctrl+S / Project > Save");
                Send(WorkbenchApp.SaveAsProject); var picker = await Modal("Save project as");
                await Shot("project-save-as-dialog", "Save project as (file dialog)", "default", "Project > Save As...");
                await bridge.Invoke(() => ((TFileDialog)picker).fileName!.SetData(tvdemo));
                Send(Views.cmOK); await Modal("Replace project?");
                await Shot("prompt-replace-project", "Replace project? (prompt)", "Save As onto existing file", "Project > Save As...");
                await Dismiss();
                await bridge.Invoke(() => main.TestList.Toggle("tvdemo/about"));
                Send(WorkbenchApp.CloseProject); await Modal("Unsaved project changes");
                await Shot("prompt-unsaved-changes", "Unsaved project changes (prompt)", "Close with unsaved changes", "Project > Close");
                await Dismiss();
                Send(WorkbenchDialog.Advanced); var dialog = await Modal("Project Properties");
                await Shot("project-properties-tvdemo", "Project Properties", "TVDemo project values", "Project > Project Properties...");
                await bridge.Invoke(() => Inputs(dialog).Single(i => i.origin.y == 3).SetData(Path.Combine(workspace, "other-target.exe")));
                Send(Views.cmOK); await Modal("Change project target?");
                await Shot("prompt-change-target", "Change project target? (prompt)", "target edit with marked scenarios", "Project Properties > Apply");
                await Dismiss();
                string bad = Path.Combine(workspace, "invalid.twproj"); File.WriteAllText(bad, "{ \"schema\": \"something-else\" }");
                Environment.CurrentDirectory = workspace;
                Press(Keys.kbCtrlO); picker = await Modal("Open project");
                await bridge.Invoke(() => ((TFileDialog)picker).fileName!.SetData(bad));
                Send(Views.cmFileOpen); await Modal("Project operation failed");
                await Shot("message-project-open-failed", "Project operation failed (message)", "invalid .twproj schema", "Project > Open...");
                await Dismiss();
                Send(WorkbenchApp.SaveAsProject); picker = await Modal("Save project as");
                await bridge.Invoke(() => ((TFileDialog)picker).fileName!.SetData(Path.Combine(workspace, "missing-folder", "copy.twproj")));
                Send(Views.cmOK);
                var shown = await Until(() => app.DeskTop.TopView() is TDialog d && d.title != "Save project as" ? d : null, 10, optional: true);
                if (shown is not null)
                { await Shot("message-project-save-failed", shown.title + " (message)", "Save As into a missing folder", "Project > Save As..."); }
                else notes.Add("message-project-save-failed: Save As into a missing folder showed no message.");
                await Recover();
            });
            await Stage("commander", async () =>
            {
                Environment.CurrentDirectory = Path.GetDirectoryName(commander)!;
                Press(Keys.kbCtrlO); var picker = await Modal("Open project");
                await bridge.Invoke(() => ((TFileDialog)picker).fileName!.SetData(commander));
                Send(Views.cmFileOpen);
                if (await Until(() => Top("Unsaved project changes"), 10, optional: true) is not null) Send(Views.cmNo);
                main = await Until(() => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 4 && p.ProjectPath == commander ? p : null, 60);
                fixture = "examples/TSharpCommander/commander.twproj (disposable copy)";
                await Shot("workspace-commander-loaded", "Workspace", "Commander catalog loaded", "Project > Open...");
                await Menu("menu-project", Keys.kbAltP, "Save As", "Project menu", "project loaded");
                await Menu("menu-help", Keys.kbAltH, "Keyboard shortcuts", "Help menu", "project loaded");
                Press(Keys.kbF5); await Modal("Run Configuration");
                await Shot("run-configuration-commander", "Run Configuration", "Commander project, f10 marked", "F5");
                Send(Views.cmOK);
                await Until(() => main.Busy ? "" : null, 30);
                await Until(() => !main.Busy ? "" : null, 240);
                Press(Keys.kbF6); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await Settle(200);
                await Shot("results-commander", "Results", "Commander proxy run", "F6");
                Press(Keys.kbEnter); await Modal("Result details"); Press(Keys.kbPgDn); await Settle();
                await Shot("result-details-commander-proxy", "Result details", "Commander proxy execution (proxy rows)", "Results > Enter");
                await Dismiss();
                Press(Keys.kbEsc); await Settle();
            });
            await Stage("quit", async () =>
            {
                await bridge.Invoke(() => main.TestList.Toggle("commander/menu/f10-open-close"));
                Press(Keys.kbAltX); await Modal("Unsaved project changes");
                await Shot("prompt-unsaved-quit", "Unsaved project changes (prompt)", "Alt+X with unsaved changes", "Alt+X / Project > Exit");
            });
            if (Top("Unsaved project changes") is not null) Send(Views.cmNo);
            else await bridge.Invoke(() => { app.Project?.CloseApproved(); Command(app, Views.cmQuit); });
        }

        private async Task Visual(WorkbenchDialog main)
        {
            string store = Path.Combine(workspace, "tvdemo", "store", "visual-baselines.json");
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
                [new("tvdemo/menu", "menu", ["80x25"], "Disposable store")]), ScenarioRunner.Json));
            string key = VisualBaselineSet.Key("tvdemo/menu", "menu", "80x25");
            await bridge.Invoke(() => { main.TestList.ClearMarks(); main.TestList.Toggle("tvdemo/menu"); });
            Press(Keys.kbF5); var dialog = await Modal("Run Configuration");
            await bridge.Invoke(() => { Radios(dialog)[0].value = 1; Radios(dialog)[0].DrawView(); var grids = Checks(dialog); grids.value = 1; grids.DrawView(); Nudge(dialog); });
            Send(0x7d70); var options = await Modal("Run Options");
            await bridge.Invoke(() => { var edit = Inputs(options).Single(i => i.origin.y == 3); edit.SetData(store); edit.DrawView(); });
            await Shot("run-options-baseline-store", "Run Options", "baseline manifest entered", "Run Configuration > Options...");
            Send(Views.cmOK); await Until(() => Top("Run Options") is null ? "" : null); await UntilList(dialog, "executions gated");
            await Shot("run-configuration-visual-ready", "Run Configuration", "Visual Regression with store, 1 gated", "F5 > Visual Regression");
            Send(Views.cmOK); await Until(() => main.Busy ? "" : null, 30); await Until(() => !main.Busy ? "" : null, 180);
            Press(Keys.kbF6); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault()); await Settle(200);
            await Shot("results-visual-baseline-missing", "Results", "visual run: baseline missing", "F6");
            Press(Keys.kbEnter); await Modal("Result details"); Press(Keys.kbEnd); await Settle();
            await Shot("result-details-visual", "Result details", "visual statuses at the end of the list", "Results > Enter, End");
            await Dismiss();
            Send(WorkbenchDialog.BaselineBrowse); await Modal("Baseline store");
            await Shot("baselines-browse-missing", "Baseline store (browse list)", "entries missing", "Baselines > Browse baseline store");
            await Dismiss();
            Send(WorkbenchDialog.ApproveCandidate); await Modal("Choose ONE candidate");
            await Shot("baselines-choose-candidate", "Choose ONE candidate (picker)", "candidate of missing baseline", "Baselines > Approve selected candidate...");
            Send(Views.cmOK); await Modal("Candidate review");
            await Shot("baselines-candidate-review", "Candidate review (browse list)", "review details", "Choose ONE candidate > Details");
            await Dismiss(); await Modal("Approve ONE candidate?");
            await Shot("baselines-approve-confirmation", "Approve ONE candidate? (confirmation)", "default focus Cancel", "Candidate review > Close");
            await Dismiss();
            var set = VisualBaselineSet.Load(store);
            Assert.False(Directory.Exists(set.ApprovedDirectory));
            // Test-owner setup of the disposable store only (same API the existing visual tests use), so that
            // Match/Mismatch comparison states are reachable. No real baseline store is touched.
            string missingRun = Directory.GetDirectories(Path.Combine(workspace, "tvdemo", "runs"), "run-*").OrderByDescending(Directory.GetCreationTimeUtc).First();
            set.Approve(missingRun, [key], "approve-1", false);
            Send(WorkbenchDialog.BaselineBrowse); await Modal("Baseline store");
            await Shot("baselines-browse-approved", "Baseline store", "entry approved in disposable store", "Baselines > Browse baseline store");
            await Dismiss();
            await RunVisualAgain(main);
            await Shot("results-visual-match", "Results", "visual run: Match", "F6");
            Send(WorkbenchDialog.CompareResult); await Modal("Choose logical checkpoint");
            await Shot("comparison-choose-checkpoint", "Choose logical checkpoint (picker)", "Match checkpoint", "Baselines > Compare selected result");
            Send(Views.cmOK);
            var comparison = await Until(() => Children(app.DeskTop).OfType<WorkbenchComparisonWindow>().SingleOrDefault()); await Settle();
            await Shot("comparison-diff-match", "Logical Baseline Comparison (modeless)", "Diff view, no differences", "Choose logical checkpoint > Details");
            Send(0x7d71); await UntilText("Expected |");
            await Shot("comparison-expected", "Logical Baseline Comparison", "Expected view", "Expected button");
            Send(0x7d72); await UntilText("Actual |");
            await Shot("comparison-actual", "Logical Baseline Comparison", "Actual view", "Actual button");
            await bridge.Invoke(() => comparison.Zoom()); await Settle();
            await Shot("comparison-zoomed", "Logical Baseline Comparison", "zoomed", "frame zoom");
            Press(Keys.kbEsc); await Until(() => Children(app.DeskTop).OfType<WorkbenchComparisonWindow>().Any() ? null : "");
            // Deliberate mismatch in the disposable store; metadata hash updated so it is a cell difference.
            string approved = set.ApprovedCells(key); var screen = ScreenSnapshot.Deserialize(File.ReadAllBytes(approved));
            var cells = Enumerable.Range(0, screen.Width * screen.Height).Select(i => screen[i % screen.Width, i / screen.Width]).ToArray();
            for (int i = 0; i < 6; i++) cells[screen.Width * 2 + 4 + i] = new('#', cells[screen.Width * 2 + 4 + i].Attr);
            byte[] changed = new ScreenSnapshot(screen.Width, screen.Height, cells).Serialize(); File.WriteAllBytes(approved, changed);
            string meta = set.ApprovedMetadata(key); var file = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllText(meta), ScenarioRunner.Json)!;
            File.WriteAllText(meta, JsonSerializer.Serialize(file with { CellsSha256 = ScreenSnapshot.Hash(changed) }, ScenarioRunner.Json));
            await RunVisualAgain(main);
            await Shot("results-visual-mismatch", "Results", "visual run: Mismatch (FAIL)", "F6");
            Send(WorkbenchDialog.CompareResult); await Modal("Choose logical checkpoint"); Send(Views.cmOK);
            await Until(() => Children(app.DeskTop).OfType<WorkbenchComparisonWindow>().SingleOrDefault()); await Settle();
            await Shot("comparison-diff-mismatch", "Logical Baseline Comparison", "Diff view with differences highlighted", "Baselines > Compare selected result");
            Send(0x7d74); await Settle();
            await Shot("comparison-next-difference", "Logical Baseline Comparison", "Next difference focused", "Next difference button");
            Press(Keys.kbEsc); await Until(() => Children(app.DeskTop).OfType<WorkbenchComparisonWindow>().Any() ? null : "");
            string before = ScreenSnapshot.Hash(File.ReadAllBytes(approved));
            Send(WorkbenchDialog.ReplaceBaseline); await Modal("Choose ONE candidate"); Send(Views.cmOK);
            await Modal("Candidate review"); await Dismiss(); await Modal("Replace ONE approved baseline?");
            await Shot("baselines-replace-confirmation", "Replace ONE approved baseline? (confirmation)", "mismatch candidate; cancelled", "Baselines > Replace approved baseline...");
            await Dismiss();
            Assert.Equal(before, ScreenSnapshot.Hash(File.ReadAllBytes(approved)));
            if (Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any()) { await bridge.Invoke(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Single().Select()); Press(Keys.kbEsc); await Settle(); }
        }

        private async Task RunVisualAgain(WorkbenchDialog main)
        {
            if (Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Any()) { await bridge.Invoke(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().Single().Select()); Press(Keys.kbEsc); await Settle(); }
            Press(Keys.kbF5); var again = await Modal("Run Configuration"); await UntilList(again, "executions gated"); Send(Views.cmOK);
            await Until(() => main.Busy ? "" : null, 30); await Until(() => !main.Busy ? "" : null, 180);
            Press(Keys.kbF6); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault()); await Settle(200);
        }

        // ── secondary grid: the main surfaces only ──────────────────────────────────

        private async Task Secondary()
        {
            fixture = "examples/TVDemo/tvdemo.twproj (disposable copy, --project startup)";
            var main = await Until(() => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 6 ? p : null, 60);
            await Stage("secondary", async () =>
            {
                await Shot("workspace-tvdemo-loaded", "Workspace", "started with --project, catalog loaded", "tui --project tvdemo.twproj");
                await Menu("menu-project", Keys.kbAltP, "Save As", "Project menu", "project loaded");
                Press(Keys.kbF5); await Modal("Run Configuration");
                await Shot("run-configuration-default", "Run Configuration", "default", "F5");
                Send(Views.cmOK);
                await Until(() => main.Busy ? "" : null, 30);
                await Shot("running-tests-progress", "Running Tests", "run in progress", "Run");
                await Until(() => !main.Busy ? "" : null, 180);
                Press(Keys.kbF6); await Until(() => Children(app.DeskTop).OfType<WorkbenchResultsDialog>().SingleOrDefault()); await Settle(200);
                await Shot("results-pass", "Results", "functional run passed", "F6");
                Press(Keys.kbEnter); await Modal("Result details");
                await Shot("result-details-pass", "Result details", "passed execution", "Results > Enter");
                await Dismiss(); Press(Keys.kbEsc); await Settle();
                Send(WorkbenchDialog.Advanced); await Modal("Project Properties");
                await Shot("project-properties-tvdemo", "Project Properties", "TVDemo values", "Project > Project Properties...");
                await Dismiss();
                await ModalShot(WorkbenchApp.About, "About TestWorkbench", "help-about", "About TestWorkbench", "default", "Help > About");
            });
            await bridge.Invoke(() => { app.Project?.CloseApproved(); Command(app, Views.cmQuit); });
        }

        // ── capture and navigation helpers ──────────────────────────────────────────

        private async Task Stage(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex)
            {
                notes.Add($"stage {name} stopped: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("LegacyUiScreenshotInventory"))?.Trim()}");
                // Diagnostic evidence for the stop, kept outside the flat screenshot directory.
                string debug = Path.Combine(Path.GetDirectoryName(output)!, "debug"); Directory.CreateDirectory(debug);
                await bridge.Invoke(() => File.WriteAllBytes(Path.Combine(debug, $"{name}-{width}x{height}.png"), LogicalPng.Encode(ScreenSnapshot.CopyRoot())));
                await Recover();
            }
        }

        private async Task Recover()
        {
            for (int i = 0; i < 8 && app.DeskTop.TopView() is TDialog; i++) { Press(Keys.kbEsc); await Settle(150); }
        }

        private async Task Menus(string state)
        {
            await Menu("menu-project-" + state, Keys.kbAltP, "Save As", "Project menu", state);
            await Menu("menu-test-" + state, Keys.kbAltT, "Refresh catalog", "Test menu", state);
            await Menu("menu-baselines-" + state, Keys.kbAltB, "Browse baseline store", "Baselines menu", state);
            await Menu("menu-results-" + state, Keys.kbAltR, "Open output location", "Results menu", state);
            await Menu("menu-window-" + state, Keys.kbAltW, "Running tests", "Window menu", state);
            await Menu("menu-help-" + state, Keys.kbAltH, "Keyboard shortcuts", "Help menu", state);
        }

        private async Task Menu(string name, ushort hotKey, string probe, string window, string state)
        {
            Press(hotKey); await UntilText(probe); await Settle();
            await Shot(name, window + " (drop-down)", state, "Alt+" + window[0]);
            Press(Keys.kbEsc); await Until(() => Text().Contains(probe) ? null : ""); Press(Keys.kbEsc); await Settle();
        }

        private async Task ModalShot(ushort command, string title, string name, string window, string state, string source)
        {
            Send(command); await Modal(title);
            await Shot(name, window, state, source);
            await Dismiss();
        }

        private async Task Shot(string name, string window, string state, string source, string? limitation = null, string? grid = null)
        {
            await Settle();
            await bridge.Invoke(() =>
            {
                app.StatusLine?.Update();
                var screen = ScreenSnapshot.CopyRoot();
                string size = grid ?? $"{width}x{height}";
                Assert.Equal(size, $"{screen.Width}x{screen.Height}");
                string stem = $"{name}-{size}";
                byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
                File.WriteAllBytes(Path.Combine(output, stem + ".png"), png);
                File.WriteAllBytes(Path.Combine(output, stem + ".tvs1"), cells);
                File.WriteAllText(Path.Combine(output, stem + ".json"), JsonSerializer.Serialize(new
                {
                    name = stem, window, state, source, width = screen.Width, height = screen.Height, captureLayer = "logical",
                    workbenchDriver = "NullDriver", renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png), fixture
                }, new JsonSerializerOptions { WriteIndented = true }));
                entries.Add(new(stem + ".png", stem + ".tvs1", stem + ".json", window, state, source, size, fixture,
                    "in-process WorkbenchApp on NullDriver; ScreenSnapshot.CopyRoot -> TVS1 + LogicalPng", "logical", true, limitation));
            });
        }

        private void WriteManifest()
        {
            string path = Path.Combine(Path.GetDirectoryName(output)!, $"logical-{width}x{height}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { grid = $"{width}x{height}", entries, notes },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }

        private async Task<TDialog> Modal(string title) => await Until(() => Top(title));
        private TDialog? Top(string title) => app.DeskTop.TopView() is TDialog d && d.title == title ? d : null;

        private async Task Dismiss(string? next = null)
        {
            var top = app.DeskTop.TopView();
            Press(Keys.kbEsc);
            await Until(() => !ReferenceEquals(app.DeskTop.TopView(), top) && (next is null || Top(next) is not null) ? "" : null);
            await Settle();
        }

        private async Task UntilList(TGroup dialog, string text) => await Until(() =>
            Children(dialog).OfType<TListBox>().Any(l => Enumerable.Range(0, l.range).Any(i => l.GetText(i, 300).Contains(text))) ? "" : null);

        private async Task UntilText(string text, int seconds = 30) => await Until(() => Text().Contains(text) ? "" : null, seconds);

        private async Task<T> Until<T>(Func<T?> predicate, int seconds = 30, bool optional = false) where T : class
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                while (true)
                {
                    T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token);
                    if (value is not null) return value;
                    await Task.Delay(25, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (optional) { return null!; }
        }

        private async Task Settle(int milliseconds = 80) { await bridge.Invoke(() => { }); await Task.Delay(milliseconds); await bridge.Invoke(() => { }); }

        private static void Press(ushort key)
        {
            var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key);
            TEventQueue.Enqueue(ev);
        }

        private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }

        // Lets a view observe a value edited through SetData, the way its next event would.
        private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }

        private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }

        private string Prepare(string example, string folder)
        {
            var project = WorkbenchProject.Load(Path.Combine(repo, example));
            string directory = Path.Combine(workspace, folder); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Path.GetFileName(example));
            (project with { OutputRoot = Path.Combine(directory, "runs") }).Save(path);
            return path;
        }

        private static string RepositoryRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found above the test output.");
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static TRadioButtons[] Radios(TGroup group) => Children(group).OfType<TRadioButtons>().OrderBy(r => r.origin.y).ThenBy(r => r.origin.x).ToArray();
    private static TCheckBoxes Checks(TGroup group) => Children(group).OfType<TCheckBoxes>().Single();
    private static TInputLine[] Inputs(TGroup group) => Children(group).OfType<TInputLine>().ToArray();

    private static string Text()
    {
        var s = ScreenSnapshot.CopyRoot();
        return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray());
    }

    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break;
        var first = group.last.Next!;
        for (var v = first; ; v = v.Next!) { yield return v; if (v.Next == first) break; }
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
        {
            if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); }
            else base.HandleEvent(ref ev);
        }
    }
}
