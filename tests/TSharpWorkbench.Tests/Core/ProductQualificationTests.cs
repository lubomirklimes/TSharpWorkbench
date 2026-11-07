using System.Diagnostics;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Final product UX qualification. The real WorkbenchApp runs on NullDriver at the supported Workbench grids
// (140x40 primary, 160x50 secondary, 120x36 minimum) with the compiled SampleTarget, the ordinary TVDemo Proxy target
// (scripts/prepare-tvdemo.ps1) and, where deployed, the ordinary Commander pilot. Keys and commands go through the
// shared event queue as a user's would; test-only shortcuts are named where used. Every project, run folder and
// baseline store is a disposable temporary copy. Optional logical screenshots: TSWB_QUALIFICATION_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class ProductQualificationTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;
    private const string Status = "sample/status/context", Dialog = "sample/dialog/basic", Blocked = "selftest/failure/blocked-ui",
        Failure = "selftest/failure/wrong-expectations";
    private const string Gated = "desktop-status";

    // ── TButton contract (follow-up): Run Configuration Run button ───────

    // Defect (reproduced before the fix): Run used the shared cmOK with SetState(sfDisabled); any command-set
    // change (here F1 → Keyboard Reference → Esc, or a run state change) re-enabled the button although the dialog still
    // had problems. Run now has its own command, enabled only while no problem exists.
    [Fact]
    public async Task RunButtonStaysDisabledAcrossCommandSetChangesWhileProblemsExist()
    {
        string root = Temp();
        try
        {
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => main.TestList.Toggle(Status));
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.Sizes.value = 1 | 4; dialog.CustomSizes.SetData("300x10"); Nudge(dialog); });
                await bridge.Invoke(() => Assert.True(dialog.RunButton.GetState(Views.sfDisabled), "after the problem"));
                await Settle(bridge);
                await bridge.Invoke(() => Assert.True(dialog.RunButton.GetState(Views.sfDisabled), "after idle"));
                // F1 opens the Keyboard Reference on top; closing it returns to the dialog with the button state intact.
                Press(Keys.kbF1); await Until(bridge, () => app.HelpWindow is not null);
                Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), dialog)); await Settle(bridge);
                await bridge.Invoke(() => Assert.True(dialog.RunButton.GetState(Views.sfDisabled), "after the nested Keyboard Reference"));
                await Shot(bridge, "run-configuration", "invalid-after-keyboard-reference", "contract");
                // A real command-set change processed by the application's Idle broadcast.
                await bridge.Invoke(() => { TView.DisableCommand(0x7dfe); TView.EnableCommand(0x7dfe); });
                await Settle(bridge);
                await bridge.Invoke(() => Assert.True(dialog.RunButton.GetState(Views.sfDisabled), "after a command-set change"));
                await bridge.Invoke(() => TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null));
                await bridge.Invoke(() =>
                {
                    Assert.True(dialog.RunButton.GetState(Views.sfDisabled), "after an explicit broadcast");
                    Assert.False(TView.CommandEnabled(WorkbenchRunDialog.RunCommand));
                    Assert.True(TView.CommandEnabled(Views.cmOK));           // the shared cmOK stays enabled for other dialogs
                    Assert.False(dialog.Valid(Views.cmOK));                   // defensive validation is unchanged
                });
                // Enter while invalid: nothing runs and the dialog stays open.
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(dialog, app.DeskTop!.TopView()); Assert.False(main.Busy); });
                // Correcting the problem enables Run (command and button); it survives a broadcast; Enter runs.
                await bridge.Invoke(() => { dialog.CustomSizes.SetData("64x20"); Nudge(dialog); });
                await bridge.Invoke(() => TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null));
                await bridge.Invoke(() =>
                {
                    Assert.Empty(dialog.Problems); Assert.False(dialog.RunButton.GetState(Views.sfDisabled));
                    Assert.True(TView.CommandEnabled(WorkbenchRunDialog.RunCommand)); dialog.RunButton.Select();
                });
                Press(Keys.kbEnter); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
                await Until(bridge, () => !main.Busy && Directory.Exists(Path.Combine(root, "runs")), 120);
                await bridge.Invoke(() => Assert.True(TView.CommandEnabled(WorkbenchRunDialog.RunCommand)));   // restored after close
                // Cancel path: invalid, Esc, the command is restored for the next dialog.
                Press(Keys.kbF5); dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.Sizes.value = 4; dialog.CustomSizes.SetData("1"); Nudge(dialog); });
                await bridge.Invoke(() => Assert.False(TView.CommandEnabled(WorkbenchRunDialog.RunCommand)));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
                await bridge.Invoke(() => Assert.True(TView.CommandEnabled(WorkbenchRunDialog.RunCommand)));
                Press(Keys.kbF5); dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { Assert.Empty(dialog.Problems); Assert.False(dialog.RunButton.GetState(Views.sfDisabled)); Assert.Same(dialog.RunButton, dialog.current); });
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunDialog);
            });
        }
        finally { Delete(root); }
    }

    // Start window: the Open button is unavailable while the recent list is empty, also after command-set changes.
    [Fact]
    public async Task StartOpenButtonFollowsTheRecentListAcrossCommandSetChanges()
    {
        string root = Temp();
        try
        {
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var start = await Wait(bridge, () => app.Start);
                TButton Open() => Children(start).OfType<TButton>().Single(b => b.Command == WorkbenchStartWindow.OpenRecent);
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.Empty(start.Entries); Assert.True(Open().GetState(Views.sfDisabled), "empty list, after idle"); });
                await bridge.Invoke(() => { TView.DisableCommand(0x7dfe); TView.EnableCommand(0x7dfe); });
                await Settle(bridge);
                await bridge.Invoke(() => Assert.True(Open().GetState(Views.sfDisabled), "empty list, after a command-set change"));
                await Shot(bridge, "start", "no-recent-projects", "contract");
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    // Defects (reproduced before the fix): the Project Properties notice line (validation errors and the
    // "clears the N marked scenarios" consequence) was never inserted, so it was invisible on screen; and Alt+W/Alt+E were
    // each shared by two controls (Browse runs / Network, Executable / Browse store).
    [Fact]
    public async Task ProjectPropertiesShowsValidationAndConsequenceOnScreenWithUniqueMnemonics()
    {
        string root = Temp();
        try
        {
            string project = SaveProject(root, "props.twproj", new WorkbenchProject { Name = "Props sample", Target = Target, OutputRoot = Path.Combine(root, "runs"), SelectedIds = [Status] });
            string before = File.ReadAllText(project);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                Press(Keys.kbAltO);
                var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
                await bridge.Invoke(() => Assert.Empty(DuplicateMnemonics(props)));
                // Alt+E focuses Executable; typing a different path shows the consequence line on screen.
                Press(Keys.kbAltE); await Settle(bridge);
                await bridge.Invoke(() => { Assert.True(props.ExecutableField.GetState(Views.sfFocused)); props.ExecutableField.SetData(""); });   // inside a group box since the redesign
                Type(Path.Combine(root, "other.exe")); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains("clears the 1 marked scenario", Text()));
                await Shot(bridge, "project-properties", "consequence-visible", "properties");
                // Enter = OK with a missing executable: the dialog stays open and says why, on screen.
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(props, app.DeskTop!.TopView()); Assert.Contains("! Executable: no file exists at", Text()); });
                await Shot(bridge, "project-properties", "validation-error-visible", "properties");
                // Alt+W toggles Network (its own mnemonic); Alt+S opens the runs folder picker; Alt+O the store picker.
                Press(Keys.kbAltW); await Settle(bridge);
                await bridge.Invoke(() => Assert.Equal(1u, props.AllowField.value & 1));
                Press(Keys.kbAltS); await Until(bridge, () => app.DeskTop!.TopView() is TDialog d && !ReferenceEquals(d, props));
                Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
                Press(Keys.kbAltO); await Until(bridge, () => app.DeskTop!.TopView() is TFileDialog { title: WorkbenchPropertiesDialog.ManifestPickerTitle });
                Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
                // Esc cancels everything: nothing applied, nothing written.
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Properties") is null);
                await bridge.Invoke(() => { Assert.False(main.Dirty); Assert.Equal([Status], main.TestList.MarkedIds.ToArray()); });
            });
            Assert.Equal(before, File.ReadAllText(project));
        }
        finally { Delete(root); }
    }

    // ── Workflow 1: new project, keyboard only ────────────────────────────────────────

    [Fact]
    public async Task Workflow1NewProjectPropertiesDiscoverMarkAndSave()
    {
        string root = Temp();
        try
        {
            string saved = Path.Combine(root, "wf1-new.twproj");
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await Shot(bridge, "start", "first-launch", "wf1");
                // Ctrl+N: an untitled project; Project Properties opens with focus on Executable.
                Press(Keys.kbCtrlN);
                var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
                await bridge.Invoke(() => { Assert.True(props.ExecutableField.GetState(Views.sfFocused)); Assert.Null(app.Start); });
                Type(Target);
                await Settle(bridge);
                await bridge.Invoke(() => Assert.Equal(Target, props.ExecutableField.Data));
                await Shot(bridge, "project-properties", "new-project-target-typed", "wf1");
                // Enter = OK (default): the target applies and discovery starts.
                Press(Keys.kbEnter);
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                await bridge.Invoke(() => { Assert.Null(main.ProjectPath); Assert.Contains("Scenarios  16 loaded", main.HeaderText); });
                // Alt+C focuses Scenarios; Space marks; Down + Space marks a second one.
                Press(Keys.kbAltC); await Until(bridge, () => ReferenceEquals(main.current, main.TestList));
                Press(Keys.kbHome); Press(' '); Press(Keys.kbDown); Press(' '); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(2, main.TestList.MarkedIds.Count); Assert.True(main.Dirty); Assert.Contains("*", main.title); });
                await Shot(bridge, "workspace", "new-project-marked", "wf1");
                // Ctrl+S on an untitled project asks for a file name (Save project as); Enter saves.
                Press(Keys.kbCtrlS);
                var picker = (TFileDialog)await Wait(bridge, () => Modal(app, "Save project as"));
                await bridge.Invoke(() => Assert.Same(picker.fileName, picker.current));
                await bridge.Invoke(() => picker.fileName!.SetData(""));    // the input starts with the folder wildcard; replace it
                Type(saved);
                await Shot(bridge, "save-project-as", "file-name-typed", "wf1");
                Press(Keys.kbEnter);
                await Until(bridge, () => File.Exists(saved) && !main.Dirty, 30);
                await bridge.Invoke(() => { Assert.Equal(saved, main.ProjectPath); Assert.DoesNotContain("*", main.title); });
                await Shot(bridge, "workspace", "new-project-saved", "wf1");
            }, showStart: true);
            var project = WorkbenchProject.Load(saved);
            Assert.Equal(Target, project.Target); Assert.Equal(2, project.SelectedIds.Length); Assert.Equal(1, project.Version);
        }
        finally { Delete(root); }
    }

    // ── Workflow 2: functional run ───────────────────────────────────────────────────

    [Fact]
    public async Task Workflow2OpenProjectRunFunctionalAndInspectAssertions()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "wf2.twproj", new WorkbenchProject { Name = "Functional sample", Target = Target, OutputRoot = runs, ScreenSizes = ["80x25"] });
            string before = File.ReadAllText(project);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                // Ctrl+O: the framework file dialog; the full path is typed and Enter opens the project.
                Press(Keys.kbCtrlO);
                var picker = (TFileDialog)await Wait(bridge, () => Modal(app, "Open project"));
                await bridge.Invoke(() => picker.fileName!.SetData(""));
                Type(project); Press(Keys.kbEnter);
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                // Mark the passing dialog scenario and the expected-failure scenario (assertion inspection).
                Press(Keys.kbAltC); await Until(bridge, () => ReferenceEquals(main.current, main.TestList));
                await bridge.Invoke(() => main.TestList.FocusItemNum(Index(main.TestList, Dialog)));
                Press(' '); await Settle(bridge);
                await bridge.Invoke(() => main.TestList.FocusItemNum(Index(main.TestList, Failure)));
                Press(' '); await Settle(bridge);
                await bridge.Invoke(() => Assert.Equal([Dialog, Failure], main.TestList.MarkedIds.Order().ToArray()));
                // F5 → Run Configuration (Run focused) → Enter.
                Press(Keys.kbF5);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { Assert.Same(dialog.RunButton, dialog.current); Assert.Empty(dialog.Problems); });
                await Shot(bridge, "run-configuration", "functional-two-marked", "wf2");
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await bridge.Invoke(() => Assert.Same(main, app.DeskTop!.current));   // the monitor never steals focus
                await Until(bridge, () => !main.Busy, 120);
                // The finished Run Monitor (selected directly; idle Alt+3 is Run Configuration); Enter = Results (default button).
                await Front(app, bridge, monitor);
                await bridge.Invoke(() => { Assert.Equal(RunState.Finished, monitor.State); Assert.Contains("1 passed", monitor.Text); });
                await Shot(bridge, "run-monitor", "finished-one-failed", "wf2");
                Press(Keys.kbEnter);
                var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() =>
                {
                    Assert.Same(results, app.DeskTop!.current); Assert.Contains("Verdict   FAILED", results.HeaderText);
                    Assert.Contains(results.ExecutionRows, r => r.Contains("Failed (expected)") && r.Contains(Failure));
                    Assert.Contains(results.ExecutionRows, r => r.Contains("Passed") && r.Contains(Dialog));
                    // Default focus is the first problem execution.
                    Assert.Contains(Failure, results.ExecutionRows[results.ExecutionsList.focused]);
                });
                // Enter inspects: the failed checkpoint is selected; Tab moves to Details with expected/actual.
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => Assert.Same(results.CheckpointList, results.current));
                Press(Keys.kbTab); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Same(results.Inspector, results.current);
                    var rows = results.InspectorRows;
                    Assert.Contains(rows, r => r.Contains("FAIL"));
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("expected", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("actual", StringComparison.Ordinal));
                });
                await Shot(bridge, "results", "failed-assertion-inspected", "wf2");
                // The passing execution: its checkpoints and passed assertions are shown the same way.
                Press(Keys.kbShiftTab); Press(Keys.kbShiftTab); await Settle(bridge);
                await bridge.Invoke(() => Assert.Same(results.ExecutionsList, results.current));
                await bridge.Invoke(() => results.ExecutionsList.FocusItemNum(Array.FindIndex(results.ExecutionRows, r => r.Contains(Dialog))));
                Press(Keys.kbEnter); Press(Keys.kbTab); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(results.Inspector, results.current); Assert.Contains(results.InspectorRows, r => r.Contains("pass", StringComparison.OrdinalIgnoreCase)); });
                await Shot(bridge, "results", "passed-checkpoint-inspected", "wf2");
                Press(Keys.kbEsc); await Until(bridge, () => main.ResultsWindow is null);
                await bridge.Invoke(() => Assert.True(main.Dirty));   // marking is project data; running is not
            }, showStart: true);
            Assert.Equal(before, File.ReadAllText(project));       // nothing was written without Save
        }
        finally { Delete(root); }
    }

    // ── Workflow 3: visual regression, candidate approval and a matching re-run ────────

    [Fact]
    public async Task Workflow3VisualRegressionReviewApproveRerunMatch()
    {
        string root = Temp(); var opened = new List<(string Path, bool Folder)>();
        var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, f) => opened.Add((p, f));
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            string project = SaveProject(root, "wf3.twproj", new WorkbenchProject
                { Name = "Visual sample", Target = Target, OutputRoot = runs, SelectedIds = [Status], ScreenSizes = ["80x25"] });
            var set = VisualBaselineSet.Load(store);
            string key = VisualBaselineSet.Key(Status, Gated, "80x25"), approved = set.ApprovedCells(key);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                // Configure the baseline store in Project Properties (Alt+O): Tab to the store field, type, Enter.
                Press(Keys.kbAltO);
                var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
                for (int i = 0; i < 20; i++) { bool there = false; await bridge.Invoke(() => there = props.StoreField.GetState(Views.sfFocused)); if (there) break; Press(Keys.kbTab); await Settle(bridge); }
                await bridge.Invoke(() => Assert.True(props.StoreField.GetState(Views.sfFocused)));
                Type(store); await Shot(bridge, "project-properties", "baseline-store-typed", "wf3");
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
                await bridge.Invoke(() => { Assert.True(main.Dirty); Assert.Equal(store, main.CaptureProject().BaselineStore); });
                // F5: Visual regression (Alt+V in the Test mode group) and "Compare and keep candidates".
                Press(Keys.kbF5); var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbAltV); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(1u, dialog.Mode.value); dialog.VisualPolicy.Focus(); });
                Press(Keys.kbDown); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(1u, dialog.VisualPolicy.value); Assert.Empty(dialog.Problems); Assert.Contains("1 gated checkpoint", dialog.Inline); dialog.RunButton.Select(); });
                await Shot(bridge, "run-configuration", "visual-keep-candidates", "wf3");
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 120);
                string first = Directory.GetDirectories(runs, "run-*").Single();
                // The finish notice uses the Results verdict (formerly "FAILED (1 passed / 0 failed / 0 blocked)").
                await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Message.StartsWith("Run finished: PASSED - VISUAL FAILED (1 passed / 0 failed / 0 blocked)", StringComparison.Ordinal)));
                // Results → Compare (Alt+C): the candidate only (no approved baseline yet).
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => Assert.Contains(results.ExecutionRows, r => r.Contains("MISSING")));
                Press(Keys.kbAltC); var comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() => { Assert.Null(comparison.Model.Expected); Assert.Contains("MISSING baseline", comparison.HeaderText); });
                await Shot(bridge, "comparison", "missing-baseline-candidate-only", "wf3");
                // Review candidate (Alt+V in Comparison is the Review button? use Alt+4 back to Results, then Alt+V).
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                Press(Keys.kbAltV);
                var review = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog);
                await bridge.Invoke(() => { Assert.Same(review.Confirm, review.current); Assert.True(review.ActionButton.GetState(Views.sfDisabled)); });
                // TButton contract of Approve: stays disabled across a command-set change and the nested Keyboard Reference.
                Press(Keys.kbF1); await Until(bridge, () => app.HelpWindow is not null);
                Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), review)); await Settle(bridge);
                await bridge.Invoke(() => { TView.DisableCommand(0x7dfe); TView.EnableCommand(0x7dfe); });
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.True(review.ActionButton.GetState(Views.sfDisabled), "Approve after command-set change"); Assert.False(review.Valid(Views.cmYes)); });
                await bridge.Invoke(() => TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null));
                await bridge.Invoke(() => Assert.True(review.ActionButton.GetState(Views.sfDisabled), "Approve after explicit broadcast"));
                // Alt+O opens the candidate image (recorded, no viewer); Enter on the check box: Cancel default, nothing written.
                Press(Keys.kbAltO); await Settle(bridge);
                await bridge.Invoke(() => Assert.EndsWith(".png", opened[^1].Path));
                Press(Keys.kbEnter); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchCandidateDialog);
                Assert.False(Directory.Exists(set.ApprovedDirectory));
                // Explicit approval: Space ticks the confirmation, Alt+A, then Alt+A in the final confirmation.
                Press(Keys.kbAltV); review = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog);
                Press(' '); await Settle(bridge);
                await bridge.Invoke(() => Assert.False(review.ActionButton.GetState(Views.sfDisabled)));
                await bridge.Invoke(() => TView.Message(app.DeskTop, Events.evBroadcast, Views.cmCommandSetChanged, null));
                await bridge.Invoke(() => Assert.False(review.ActionButton.GetState(Views.sfDisabled), "Approve enabled after broadcast"));
                await Shot(bridge, "candidate-review", "confirmation-ticked", "wf3");
                Press(Keys.kbAltA);
                var confirm = await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)));
                await bridge.Invoke(() => Assert.Equal("Cancel", (confirm.current as TButton)?.Title));
                await Shot(bridge, "approval-confirmation", "cancel-default", "wf3");
                Press(Keys.kbAltA); await Until(bridge, () => File.Exists(approved));
                Assert.Equal(Hash(Path.Combine(first, key + ".candidate.tvs1")), Hash(approved));
                // Re-run (Alt+E): Run Configuration preselects the execution; Enter; the new run matches.
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, results));
                Press(Keys.kbAltE); dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { Assert.True(dialog.RerunSelected); Assert.Equal(1u, dialog.Mode.value); Assert.Same(dialog.RunButton, dialog.current); });
                await Shot(bridge, "run-configuration", "rerun-visual", "wf3");
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy && Directory.GetDirectories(runs, "run-*").Length == 2, 120);
                await bridge.Invoke(() => Assert.Contains(results.ExecutionRows, r => r.Contains("Match")));
                Press(Keys.kbAltC); comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() => { Assert.Equal(0, comparison.DifferenceCount); Assert.Contains("Match", comparison.HeaderText); });
                await Shot(bridge, "comparison", "match-after-approval", "wf3");
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                await bridge.Invoke(() => Assert.True(main.Dirty));   // only the Properties change (store) is pending
            });
            Assert.Equal([key + ".baseline.json", key + ".png", key + ".tvs1"], Directory.GetFiles(set.ApprovedDirectory).Select(f => Path.GetFileName(f)).Order().ToArray());
        }
        finally { WorkbenchDialog.Opener = previous; Delete(root); }
    }

    // ── Workflow 4: native evidence on the ordinary TVDemo target ─────────────────────

    public sealed class TvDemoFactAttribute : FactAttribute
    {
        public TvDemoFactAttribute() { if (!File.Exists(TvDemoProject)) Skip = "Run scripts/prepare-tvdemo.ps1 first."; }
    }
    public sealed class TvDemoTheoryAttribute : TheoryAttribute
    {
        public TvDemoTheoryAttribute() { if (!File.Exists(TvDemoProject)) Skip = "Run scripts/prepare-tvdemo.ps1 first."; }
    }
    // TSW_TVDEMO_PROJECT selects another prepared deployment (for example a package-mode OutputRoot).
    private static string TvDemoProject => Environment.GetEnvironmentVariable("TSW_TVDEMO_PROJECT") is { Length: > 0 } custom ? custom
        : Path.Combine(RepositoryRoot(), "artifacts", "tvdemo", "current", "tvdemo.twproj");

    [TvDemoFact]
    public async Task Workflow4NativeEvidenceProvenanceOnTvDemo()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Temp(); var opened = new List<(string Path, bool Folder)>();
        var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, f) => opened.Add((p, f));
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "wf4.twproj", WorkbenchProject.Load(TvDemoProject) with { OutputRoot = runs, SelectedIds = ["tvdemo/menu"], ScreenSizes = ["80x25"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 6 ? p : null, 90);
                // Target display driver: SDL window (radio item 2), Native evidence: renderer pixels (item 2), by keyboard.
                Press(Keys.kbF5); var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => dialog.ProxyDrivers.Focus());
                Press(Keys.kbDown); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(1u, dialog.ProxyDrivers.value); dialog.Evidence.Focus(); });
                Press(Keys.kbDown); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(1u, dialog.Evidence.value); Assert.Empty(dialog.Problems); dialog.RunButton.Select(); });
                await Shot(bridge, "run-configuration", "tvdemo-sdl-window-renderer-pixels", "wf4");
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 180);
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() =>
                {
                    Assert.Contains(results.ExecutionRows, r => r.StartsWith(" +  Passed", StringComparison.Ordinal));
                    Assert.Contains("SDL window (SDLDriver) + renderer pixels", results.HeaderText);
                });
                // Tab to Checkpoints, Down to the first checkpoint; the evidence rows keep their provenance labels.
                Press(Keys.kbTab); Press(Keys.kbDown); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Same(results.CheckpointList, results.current);
                    var rows = results.InspectorRows;
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("Logical PNG", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.Contains("cells rendered by Workbench"));
                    Assert.Contains(rows, r => r.TrimStart().StartsWith("Native pixels", StringComparison.Ordinal));
                    Assert.Contains(rows, r => r.Contains("SDL renderer pixels (native)"));
                    Assert.DoesNotContain(rows, r => r.Contains("Logical PNG") && r.Contains("(native)"));
                });
                await Shot(bridge, "results", "tvdemo-native-evidence-rows", "wf4");
                // Alt+N opens the native artifact; Alt+I opens the logical PNG; they are different files.
                Press(Keys.kbAltN); await Settle(bridge);
                string native = opened[^1].Path;
                Press(Keys.kbAltI); await Settle(bridge);
                string logical = opened[^1].Path;
                Assert.NotEqual(native, logical); Assert.True(File.Exists(native)); Assert.True(File.Exists(logical));
                // Compare opens the Native Evidence Comparison with both rows, opened externally.
                Press(Keys.kbAltC); var comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(WorkbenchComparisonWindow.NativeTitle, comparison.title);
                    Assert.Contains(comparison.Model.Evidence, e => e.Native && e.Layer.Contains("SDL renderer pixels"));
                    Assert.Contains(comparison.Model.Evidence, e => !e.Native && e.Label == "Logical PNG");
                });
                await Shot(bridge, "comparison", "tvdemo-native-evidence", "wf4");
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                WriteEvidence("real-target-tvdemo-wf4", new { workflow = 4, backend = "SDLDriver", capture = "renderer-pixels", native, logical, outcome = "Passed" });
            });
        }
        finally { WorkbenchDialog.Opener = previous; Delete(root); }
    }

    // ── Workflow 5: cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Workflow5CancelRunKeepsPartialResultsAndCleansUpTheTarget()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string name = Path.GetFileNameWithoutExtension(Target);
            int targetsBefore = Process.GetProcessesByName(name).Length;
            string project = SaveProject(root, "wf5.twproj", new WorkbenchProject
                { Name = "Cancel sample", Target = Target, OutputRoot = runs, SelectedIds = [Status, Blocked], ScreenSizes = ["80x25", "140x40"], CustomWidth = 100, CustomHeight = 30 });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => main.Busy ? Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault() : null);
                await Until(bridge, () => monitor.Text.Contains(Blocked), 60);
                // Alt+3 (run active) → Run Monitor; Alt+C = Cancel run.
                PressAlt('3'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, monitor));
                await Shot(bridge, "run-monitor", "running-before-cancel", "wf5", optional: true);
                Press(Keys.kbAltC); await Settle(bridge);
                await bridge.Invoke(() => { if (main.Busy) Assert.Equal(RunState.Cancelling, monitor.State); });
                await Shot(bridge, "run-monitor", "cancelling", "wf5", optional: true);
                await Until(bridge, () => !main.Busy, 60);
                await bridge.Invoke(() => { Assert.Equal(RunState.Cancelled, monitor.State); Assert.Contains("CANCELLED", monitor.Text); });
                await Shot(bridge, "run-monitor", "cancelled", "wf5");
                // Partial Results: Enter on the monitor; Problems only (Alt+L) shows the cancelled executions.
                Press(Keys.kbEnter); var results = await Wait(bridge, () => main.ResultsWindow);
                var batch = LastRun(runs);
                await bridge.Invoke(() =>
                {
                    Assert.Contains("CANCELLED", results.HeaderText);
                    Assert.Contains(results.ExecutionRows, r => r.Contains("CANCELLED"));
                    Assert.DoesNotContain(results.ExecutionRows, r => r.Contains("CANCELLED") && r.Contains("FAILED"));
                });
                Press(Keys.kbAltL); await Settle(bridge);
                await bridge.Invoke(() => Assert.All(results.ExecutionRows, r => Assert.DoesNotContain("Passed  ", r[..Math.Min(14, r.Length)])));
                await Shot(bridge, "results", "cancelled-run-problems-only", "wf5");
                Assert.Contains(batch.Results, r => r.Outcome == "Aborted");
                Assert.All(batch.Results.Where(r => r.Outcome != "Aborted" && (r.TargetExited || r.Checkpoints.Length > 0)), r => Assert.True(r.CleanupSucceeded));
                await bridge.Invoke(() => Assert.False(main.Dirty));
            });
            // No owned target process is left running after the cancellation.
            await Task.Delay(500);
            Assert.True(Process.GetProcessesByName(name).Length <= targetsBefore, "an owned SampleTarget process is still running");
        }
        finally { Delete(root); }
    }

    // ── Workflow 6: historical results ───────────────────────────────────────────────

    [Fact]
    public async Task Workflow6HistoricalRunNavigationEvidenceAndRerunNeverChangeOriginals()
    {
        string root = Temp(); var opened = new List<(string Path, bool Folder)>();
        var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, f) => opened.Add((p, f));
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            var older = await PassedRun(runs);
            var mixed = await MixedRun(runs, store);
            var hashesBefore = new[] { older.RunRoot, mixed.RunRoot }.ToDictionary(r => r, Hashes);
            string project = SaveProject(root, "wf6.twproj", new WorkbenchProject { Name = "History sample", Target = Target, OutputRoot = runs });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                // Results > Open run... (menu by keyboard: Alt+R, then O) opens the run.json picker; type the path, Enter.
                Press(Keys.kbAltR); await Settle(bridge); Press('r');
                var picker = (TFileDialog)await Wait(bridge, () => app.DeskTop!.TopView() is TFileDialog d ? d : null);
                await Shot(bridge, "open-completed-run", "picker", "wf6");
                await bridge.Invoke(() => picker.fileName!.SetData(""));
                Type(Path.Combine(older.RunRoot, "run.json")); Press(Keys.kbEnter);
                var results = await Wait(bridge, () => main.ResultsWindow);
                await Until(bridge, () => results.HeaderText.Contains(Path.GetFileName(older.RunRoot)));
                await bridge.Invoke(() => { Assert.Contains("(historical run)", results.HeaderText); Assert.Contains("run 1 of 2", results.HeaderText); });
                await Shot(bridge, "results", "historical-run-opened", "wf6");
                // Ctrl+Right → later run; Ctrl+Left → earlier; no earlier run is a hint, not a change.
                Press(Keys.kbCtrlRight); await Until(bridge, () => results.HeaderText.Contains(Path.GetFileName(mixed.RunRoot)));
                await Shot(bridge, "results", "historical-later-run", "wf6");
                Press(Keys.kbCtrlLeft); await Until(bridge, () => results.HeaderText.Contains(Path.GetFileName(older.RunRoot)));
                Press(Keys.kbCtrlLeft); await Until(bridge, () => app.Notifications.HintText().Contains("No earlier run"));
                Press(Keys.kbCtrlRight); await Until(bridge, () => results.HeaderText.Contains(Path.GetFileName(mixed.RunRoot)));
                // Inspect a checkpoint: Tab to Checkpoints, Down to the gated checkpoint; Tab to Details; Enter opens the focused artifact.
                await bridge.Invoke(() => results.ExecutionsList.FocusItemNum(0));
                Press(Keys.kbTab); await Settle(bridge);
                await bridge.Invoke(() => results.CheckpointList.FocusItemNum(Array.FindIndex(results.CheckpointRows, r => r.StartsWith(Gated, StringComparison.Ordinal))));
                Press(Keys.kbTab); await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Same(results.Inspector, results.current);
                    results.Inspector.FocusItemNum(Array.FindIndex(results.InspectorRows, r => r.TrimStart().StartsWith("Cells (TVS1)", StringComparison.Ordinal)));
                });
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => Assert.EndsWith(".tvs1", opened[^1].Path));
                await Shot(bridge, "results", "historical-checkpoint-evidence", "wf6");
                // Re-run (Alt+E): a new run folder; the historical folders are byte-identical afterwards.
                Press(Keys.kbAltE); var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                // The historical run was a Visual regression run whose store this project does not have: Re-run restores
                // the mode, reports the missing store and substitutes nothing; Run stays disabled.
                await bridge.Invoke(() =>
                {
                    Assert.True(dialog.RerunSelected); Assert.Equal(1u, dialog.Mode.value);
                    Assert.Contains(dialog.Problems, p => p.Area == RunArea.Baselines);
                    Assert.True(dialog.RunButton.GetState(Views.sfDisabled));
                });
                await Shot(bridge, "run-configuration", "rerun-historical-visual-without-store", "wf6");
                // The user chooses Functional (Alt+F): the problem disappears and Run (default) is available again.
                Press(Keys.kbAltF); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(0u, dialog.Mode.value); Assert.Empty(dialog.Problems); Assert.False(dialog.RunButton.GetState(Views.sfDisabled)); dialog.RunButton.Select(); });
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy && Directory.GetDirectories(runs, "run-*").Length == 3, 120);
                await bridge.Invoke(() => { Assert.Contains("this session", results.HeaderText); Assert.False(main.Dirty); });
                await Shot(bridge, "results", "after-historical-rerun", "wf6");
            });
            foreach (var (run, before) in hashesBefore) Assert.Equal(before, Hashes(run));
        }
        finally { WorkbenchDialog.Opener = previous; Delete(root); }
    }

    // ── Workflow 7: window management ────────────────────────────────────────────────

    [Fact]
    public async Task Workflow7WindowNumbersMoveZoomCloseReopenAndRestoredGeometry()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            string project = SaveProject(root, "wf7.twproj", new WorkbenchProject
                { Name = "Window sample", Target = Target, OutputRoot = runs, SelectedIds = [Status], BaselineStore = store, TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 120);
                var monitor = Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().Single();
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                Press(Keys.kbAltC); var comparison = await Wait(bridge, () => main.Comparison);
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                // Alt+1/4/5/6 activate the one instance of each window; the finished monitor (number 3) is selected directly
                // (idle Alt+3 opens Run Configuration); no duplicates appear.
                async Task Activate(char key, TView expected, int number)
                {
                    PressAlt(key); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, expected));
                    await bridge.Invoke(() => Assert.Equal(number, ((TWindow)expected).number));
                }
                await Activate('1', main, 1); await Front(app, bridge, monitor); await bridge.Invoke(() => Assert.Equal(3, monitor.number));
                await Activate('4', results, 4); await Activate('5', comparison, 5); await Activate('6', messages, 6);
                await bridge.Invoke(() =>
                {
                    Assert.Single(Children(app.DeskTop!).OfType<WorkbenchRunMonitor>()); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchResultsDialog>());
                    Assert.Single(Children(app.DeskTop!).OfType<WorkbenchComparisonWindow>()); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchMessagesWindow>());
                });
                await Shot(bridge, "desktop", "all-modeless-windows-messages-front", "wf7");
                // Results: shrink (no keyboard resize exists in the framework's Size/Move; see the report), then move it with
                // Ctrl+F5 and arrow keys, Enter accepts; Zoom fills the desktop and Zoom again restores.
                await Activate('4', results, 4);
                await bridge.Invoke(() => results.Locate(new(0, 0, 110, 32)));
                Press(Keys.kbCtrlF5); Press(Keys.kbRight); Press(Keys.kbRight); Press(Keys.kbRight); Press(Keys.kbDown); Press(Keys.kbEnter); await DragDone(bridge);
                await bridge.Invoke(() => Assert.Equal(new TRect(3, 1, 113, 33), results.GetBounds()));
                await Shot(bridge, "results", "moved-by-keyboard", "wf7");
                Send(Views.cmZoom); await Settle(bridge);
                await bridge.Invoke(() => Assert.Equal(app.DeskTop!.GetExtent(), results.GetBounds()));
                Send(Views.cmZoom); await Settle(bridge);
                await bridge.Invoke(() => Assert.Equal(new TRect(3, 1, 113, 33), results.GetBounds()));
                // Esc in Size/Move restores the previous position. Keys are spaced as a person types: the status line switches
                // to the dragging context on idle; a burst of queued keys would still see Results' "Esc Close" status item.
                await KeysSlowly(Keys.kbCtrlF5, Keys.kbLeft, Keys.kbEsc); await DragDone(bridge);
                await bridge.Invoke(() => { Assert.False(results.GetState(Views.sfDragging)); Assert.Equal(new TRect(3, 1, 113, 33), results.GetBounds()); });
                // Close (Alt+F3) and reopen (F6): geometry restored.
                Press(Keys.kbAltF3); await Until(bridge, () => main.ResultsWindow is null);
                Press(Keys.kbF6); results = await Wait(bridge, () => main.ResultsWindow);
                await bridge.Invoke(() => Assert.Equal(new TRect(3, 1, 113, 33), results.GetBounds()));
                // Comparison: move, close, reopen from Results (Alt+C): geometry restored.
                // Compare replaces the Comparison still open from the start of the workflow (one instance only).
                var replaced = comparison;
                Press(Keys.kbAltC); comparison = await Wait(bridge, () => main.Comparison is { } c && !ReferenceEquals(c, replaced) ? c : null);
                await bridge.Invoke(() => { Assert.Null(replaced.owner); Assert.Single(Children(app.DeskTop!).OfType<WorkbenchComparisonWindow>()); comparison.Locate(new(4, 2, 124, 36)); });
                await Settle(bridge);
                await bridge.Invoke(() => Assert.Equal(new TRect(4, 2, 124, 36), comparison.GetBounds()));
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                await Settle(bridge);
                Assert.Equal(new TRect(4, 2, 124, 36), WorkbenchPreferences.Load(Path.Combine(root, "settings.json")).Window("comparison"));
                Press(Keys.kbAltC); comparison = await Wait(bridge, () => main.Comparison);
                await bridge.Invoke(() => Assert.Equal(new TRect(4, 2, 124, 36), comparison.GetBounds()));
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                // Messages: move by keyboard, close (Esc), reopen (Alt+6): restored.
                PressAlt('6'); messages = await Wait(bridge, () => app.Messages);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, messages));
                TRect moved = default;
                Press(Keys.kbCtrlF5); Press(Keys.kbUp); Press(Keys.kbUp); Press(Keys.kbEnter); await DragDone(bridge);
                await bridge.Invoke(() => moved = messages.GetBounds());
                Press(Keys.kbEsc); await Until(bridge, () => app.Messages is null);
                PressAlt('6'); messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() => Assert.Equal(moved, messages.GetBounds()));
                Press(Keys.kbEsc); await Until(bridge, () => app.Messages is null);
                // Run Monitor: idle Close closes it; idle Alt+3 then opens Run Configuration (never a run); the next run restores its bounds.
                await Front(app, bridge, monitor);
                await bridge.Invoke(() => monitor.Locate(new(8, 4, 81, 20)));
                Press(Keys.kbAltF3); await Until(bridge, () => !Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().Any());
                int runsBefore = Directory.GetDirectories(runs, "run-*").Length;
                PressAlt('3'); var config = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEsc); await Until(bridge, () => !ReferenceEquals(app.DeskTop!.TopView(), config));
                await bridge.Invoke(() => { Assert.False(main.Busy); Assert.Empty(Children(app.DeskTop!).OfType<WorkbenchRunMonitor>()); });
                Assert.Equal(runsBefore, Directory.GetDirectories(runs, "run-*").Length);
                PressAlt('1'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter);
                monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await bridge.Invoke(() => Assert.Equal(new TRect(8, 4, 81, 20), monitor.GetBounds()));
                await Until(bridge, () => !main.Busy, 120);
                await bridge.Invoke(() => Assert.False(main.Dirty));
            });
            var saved = WorkbenchPreferences.Load(Path.Combine(root, "settings.json"));
            Assert.Equal(new TRect(3, 1, 113, 33), saved.Window("results"));
            Assert.Equal(new TRect(4, 2, 124, 36), saved.Window("comparison"));
        }
        finally { Delete(root); }
    }

    // ── Workflow 8: safe exit ────────────────────────────────────────────────────────

    [Fact]
    public async Task Workflow8ExitWithUnsavedChangesKeepsTheProjectOnEnter()
    {
        string root = Temp();
        try
        {
            string project = SaveProject(root, "wf8.twproj", new WorkbenchProject { Name = "Exit sample", Target = Target, OutputRoot = Path.Combine(root, "runs") });
            string before = File.ReadAllText(project);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                Press(Keys.kbAltC); await Until(bridge, () => ReferenceEquals(main.current, main.TestList));
                Press(' '); await Settle(bridge);
                await bridge.Invoke(() => Assert.True(main.Dirty));
                Press(Keys.kbAltX);
                var unsaved = await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(() => Assert.Equal("Cancel", (unsaved.current as TButton)?.Title));
                await Shot(bridge, "unsaved-changes", "exit-cancel-default", "wf8");
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(main, app.Project); Assert.True(main.Dirty); });
            });
            Assert.Equal(before, File.ReadAllText(project));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Workflow8ExitDuringRunKeepsRunningByDefaultThenCancelsWaitsAndAsks()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string name = Path.GetFileNameWithoutExtension(Target);
            int targetsBefore = Process.GetProcessesByName(name).Length;
            string project = SaveProject(root, "wf8run.twproj", new WorkbenchProject
                { Name = "Exit run sample", Target = Target, OutputRoot = runs, SelectedIds = [Blocked], ScreenSizes = ["80x25", "140x40"] });
            string before = File.ReadAllText(project);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy && Text().Contains("Running"), 30);
                Press(Keys.kbAltC); await Until(bridge, () => ReferenceEquals(main.current, main.TestList));
                Press(Keys.kbHome); Press(' '); await Settle(bridge);   // marking stays allowed during a run and dirties the project
                await bridge.Invoke(() => Assert.True(main.Dirty));
                // Alt+X: Tests Are Running, Keep running default; Enter keeps running.
                Press(Keys.kbAltX);
                var running = await Wait(bridge, () => Modal(app, WorkbenchDialog.RunningTitle));
                await bridge.Invoke(() => Assert.Equal("~K~eep running", (running.current as TButton)?.Title));
                await Shot(bridge, "tests-are-running", "exit-keep-running-default", "wf8");
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.RunningTitle) is null);
                await bridge.Invoke(() => Assert.True(main.Busy));
                // Alt+X again; Alt+X in the dialog is "Cancel run and exit": cleanup first, then Unsaved Changes (Cancel default).
                Press(Keys.kbAltX); running = await Wait(bridge, () => Modal(app, WorkbenchDialog.RunningTitle));
                Press(Keys.kbAltX);
                var unsaved = await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle), 60);
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Busy); Assert.Equal("Cancel", (unsaved.current as TButton)?.Title);
                    Assert.Contains(LastRun(runs).Results, r => r.Outcome == "Aborted");
                });
                Assert.True(Process.GetProcessesByName(name).Length <= targetsBefore, "the owned target is still running when Unsaved Changes asks");
                await Shot(bridge, "unsaved-changes", "after-cancelled-run-exit", "wf8");
                // Enter = Cancel: the Workbench stays open with the project; nothing is written.
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(main, app.Project); Assert.True(main.Dirty); Assert.False(main.Busy); });
                // Explicit Discard on the next Alt+X exits the application.
                Press(Keys.kbAltX); await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                Press(Keys.kbAltD);
            }, exits: true);
            Assert.Equal(before, File.ReadAllText(project));
        }
        finally { Delete(root); }
    }

    // ── Resize / zoom / focus of modeless windows at the minimum grid ─────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public async Task ModelessWindowsResizeToTheirMinimumAndBackKeepingControlsReachable(int width, int height)
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            string project = SaveProject(root, "resize.twproj", new WorkbenchProject
                { Name = "Resize sample", Target = Target, OutputRoot = runs, SelectedIds = [Status], BaselineStore = store, TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"] });
            await Drive(width, height, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 120);
                var monitor = Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().Single();
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                Press(Keys.kbAltC); var comparison = await Wait(bridge, () => main.Comparison);
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                TWindow[] windows = [main, monitor, results, comparison, messages];
                foreach (var window in windows)
                {
                    TRect original = default; TPoint min = default, max = default;
                    await bridge.Invoke(() => { window.Select(); original = window.GetBounds(); window.SizeLimits(ref min, ref max); });
                    // Smallest size the window allows: every child stays inside the frame; no selectable controls overlap.
                    await bridge.Invoke(() => window.Locate(new(0, 0, min.x, min.y)));
                    await Settle(bridge);
                    await bridge.Invoke(() =>
                    {
                        var views = Children(window).Where(v => v is not TSharpVision.TFrame && v.size.x > 0 && (v.state & Views.sfVisible) != 0).ToArray();
                        Assert.All(views, v => Assert.True(v.origin.x >= 0 && v.origin.y >= 0 && v.origin.x + v.size.x <= window.size.x && v.origin.y + v.size.y <= window.size.y,
                            $"{window.GetType().Name} at {min.x}x{min.y}: {v.GetType().Name} at {v.origin.x},{v.origin.y} {v.size.x}x{v.size.y}"));
                        var selectable = views.Where(v => (v.options & Views.ofSelectable) != 0 && v is not TScrollBar).ToArray();
                        foreach (var a in selectable) foreach (var b in selectable.Where(b => !ReferenceEquals(a, b)))
                            Assert.False(Intersect(a, b), $"{window.GetType().Name} at {min.x}x{min.y}: {a.GetType().Name} overlaps {b.GetType().Name}");
                    });
                    await Shot(bridge, Kebab(window), $"minimum-size-{min.x}x{min.y}", "resize");
                    // Tab cycles through the window's controls without a trap and returns to the start.
                    await bridge.Invoke(() =>
                    {
                        var first = window.current; var seen = new HashSet<TView>();
                        for (int i = 0; i < 30; i++) { window.SelectNext(false); if (window.current is { } c) seen.Add(c); if (ReferenceEquals(window.current, first)) break; }
                        Assert.Same(first, window.current);
                        Assert.All(seen, v => Assert.Equal(0, v.state & Views.sfDisabled));
                    });
                    // Zoom and unzoom; then restore the original bounds.
                    await bridge.Invoke(() => { window.Select(); });
                    Send(Views.cmZoom); await Settle(bridge);
                    await bridge.Invoke(() => Assert.Equal(app.DeskTop!.GetExtent(), window.GetBounds()));
                    Send(Views.cmZoom); await Settle(bridge);
                    await bridge.Invoke(() => window.Locate(original));
                    await bridge.Invoke(() => Assert.Equal(original, window.GetBounds()));
                }
                // No orphaned shadows or stray cells: the desktop redraws identically after closing and reopening Messages.
                await bridge.Invoke(() => { foreach (var w in windows) w.DrawView(); });
                await Shot(bridge, "desktop", "after-resize-cycle", "resize");
                await bridge.Invoke(() => Assert.Equal(5, Children(app.DeskTop!).OfType<TWindow>().Count(w => w is WorkbenchDialog or WorkbenchRunMonitor or WorkbenchResultsDialog or WorkbenchComparisonWindow or WorkbenchMessagesWindow)));
            });
        }
        finally { Delete(root); }
    }

    // ── Current UI inventory (logical screenshots of every window type) ───────────────

    [Theory]
    [InlineData(140, 40, true)]
    [InlineData(160, 50, false)]
    [InlineData(120, 36, false)]
    public async Task InventoryOfEveryCurrentWindowType(int width, int height, bool full)
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            string project = SaveProject(root, "inventory.twproj", new WorkbenchProject
                { Name = "Inventory sample", Target = Target, OutputRoot = runs, SelectedIds = [Status, Failure], BaselineStore = store, TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"] });
            string settings = Path.Combine(root, "settings.json");
            new WorkbenchPreferences("", "", null, "80x25") { RecentProjects = [project] }.Save(settings);
            string g = $"{width}x{height}";
            await Drive(width, height, root, async (app, bridge) =>
            {
                var start = await Wait(bridge, () => app.Start);
                await Shot(bridge, "start", "recent-projects", "inventory");
                Press(Keys.kbEsc); await Until(bridge, () => app.Start is null);
                await Shot(bridge, "desktop", "no-project", "inventory");
                if (full)
                {
                    foreach (var (key, menu) in new[] { (Keys.kbAltP, "project"), (Keys.kbAltT, "test"), (Keys.kbAltR, "results"), (Keys.kbAltB, "baselines"), (Keys.kbAltW, "window"), (Keys.kbAltH, "help") })
                    { Press(key); await Settle(bridge); await Shot(bridge, "menu-" + menu, "no-project", "inventory"); Press(Keys.kbEsc); await Settle(bridge); }
                }
                await bridge.Invoke(() => app.ShowStart()); await Wait(bridge, () => app.Start);
                Press(Keys.kbEnter);
                var main = await LoadedProject(app, bridge);
                await Shot(bridge, "workspace", "project-loaded", "inventory");
                if (full)
                {
                    foreach (var (key, menu) in new[] { (Keys.kbAltP, "project"), (Keys.kbAltT, "test"), (Keys.kbAltR, "results"), (Keys.kbAltB, "baselines"), (Keys.kbAltW, "window"), (Keys.kbAltH, "help") })
                    { Press(key); await Settle(bridge); await Shot(bridge, "menu-" + menu, "project-open", "inventory"); Press(Keys.kbEsc); await Settle(bridge); }
                    Send(WorkbenchDialog.Details); await Settle(bridge);
                    await Shot(bridge, "workspace", "details-focused", "inventory");
                }
                // Project Properties and its pickers.
                Press(Keys.kbAltO); var props = await Wait(bridge, () => Modal(app, "Project Properties"));
                await Shot(bridge, "project-properties", "store-configured", "inventory");
                Press(Keys.kbAltB); await Wait(bridge, () => Modal(app, "Select target executable"));
                await Shot(bridge, "select-target-executable", "picker", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
                Press(Keys.kbAltS); await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && !ReferenceEquals(d, props) ? d : null);
                await Shot(bridge, "select-runs-folder", "picker", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
                Press(Keys.kbAltO); await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && !ReferenceEquals(d, props) ? d : null);
                await Shot(bridge, "select-baseline-store", "picker", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Properties") is null);
                // Run Configuration, Run Monitor, Results, Comparison.
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await Shot(bridge, "run-configuration", "visual-keep-candidates", "inventory");
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await Until(bridge, () => !main.Busy, 120);
                await Front(app, bridge, monitor);
                await Shot(bridge, "run-monitor", "finished", "inventory");
                Press(Keys.kbEnter); var results = await Wait(bridge, () => main.ResultsWindow);
                await Shot(bridge, "results", "after-visual-run", "inventory");
                if (full)
                {
                    Press(Keys.kbAltL); await Settle(bridge); await Shot(bridge, "results", "problems-only", "inventory"); Press(Keys.kbAltA); await Settle(bridge);
                    Press(Keys.kbEnter); Press(Keys.kbTab); await Settle(bridge); await Shot(bridge, "results", "checkpoint-details", "inventory");
                    Press(Keys.kbShiftTab); Press(Keys.kbShiftTab); await Settle(bridge);
                }
                await bridge.Invoke(() => results.ExecutionsList.FocusItemNum(Array.FindIndex(results.ExecutionRows, r => r.Contains(Status))));
                Press(Keys.kbAltC); var comparison = await Wait(bridge, () => main.Comparison);
                await Shot(bridge, "comparison", "missing-baseline", "inventory");
                Press(Keys.kbEsc); await Until(bridge, () => main.Comparison is null);
                // Candidate review, approval confirmation (cancelled), Baseline Store Status.
                Press(Keys.kbAltV); var review = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchCandidateDialog);
                await Shot(bridge, "candidate-review", "new-approval", "inventory");
                Press(' '); Press(Keys.kbAltA); await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)));
                await Shot(bridge, "approval-confirmation", "cancel-default", "inventory");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                Assert.False(Directory.Exists(VisualBaselineSet.Load(store).ApprovedDirectory));
                Send(WorkbenchDialog.BaselineBrowse); await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.StoreStatusTitle));
                await Shot(bridge, "baseline-store-status", "valid-one-missing", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                // Messages and the help dialogs.
                PressAlt('6'); await Wait(bridge, () => app.Messages); await Shot(bridge, "messages", "session-history", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.Messages is null);
                Press(Keys.kbF1); await Wait(bridge, () => app.HelpWindow); await Shot(bridge, "keyboard-reference", "f1", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                Send(WorkbenchApp.TestModes); await Wait(bridge, () => app.HelpWindow); await Shot(bridge, "test-modes-and-drivers", "help", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                Send(WorkbenchApp.About); await Wait(bridge, () => Modal(app, "About TSharpWorkbench")); await Shot(bridge, "about", "help", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                // File pickers: Open project, Save project as, Open completed run (Results Alt+O).
                Press(Keys.kbCtrlO); await Wait(bridge, () => Modal(app, "Open project")); await Shot(bridge, "open-project", "picker", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                Send(WorkbenchApp.SaveAsProject); var saveAs = (TFileDialog)await Wait(bridge, () => Modal(app, "Save project as"));
                await Shot(bridge, "save-project-as", "picker", "inventory");
                await bridge.Invoke(() => saveAs.fileName!.SetData(project)); Send(Views.cmOK);
                await Wait(bridge, () => Modal(app, "Replace project?")); await Shot(bridge, "replace-project", "cancel-default", "inventory");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                PressAlt('4'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, results));
                Press(Keys.kbAltO); await Wait(bridge, () => app.DeskTop!.TopView() as TFileDialog);
                await Shot(bridge, "open-completed-run", "picker", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                // Standard error dialog (a real save failure) and Unsaved Project Changes.
                Task saving = bridge.Invoke(() => main.SaveProject(false, Path.Combine(root, "missing-folder", "copy.twproj")));
                await Wait(bridge, () => Modal(app, "Cannot Save Project")); await Shot(bridge, "error-dialog", "cannot-save-project", "inventory");
                Press(Keys.kbEsc); await saving.WaitAsync(TimeSpan.FromSeconds(20));
                await bridge.Invoke(() => main.TestList.Toggle(Dialog));
                Send(WorkbenchApp.CloseProject); await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                await Shot(bridge, "unsaved-changes", "close-cancel-default", "inventory"); Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                // Tests Are Running (active run) and the Workspace header during a run.
                await bridge.Invoke(() => { main.TestList.ClearMarks(); main.TestList.Toggle(Blocked); });
                Press(Keys.kbF5); var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.Mode.Press(0); Nudge(dialog); dialog.RunButton.Select(); });
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy && Text().Contains("Running"), 30);
                PressAlt('1'); await Settle(bridge); await Shot(bridge, "workspace", "run-active", "inventory", optional: true);
                Press(Keys.kbAltX); await Wait(bridge, () => Modal(app, WorkbenchDialog.RunningTitle));
                await Shot(bridge, "tests-are-running", "keep-running-default", "inventory");
                Press(Keys.kbEnter); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                Send(WorkbenchDialog.CancelRun); await Until(bridge, () => !main.Busy, 60);
                if (full)
                {
                    // Historical navigation from Results.
                    Press(Keys.kbF6); await Wait(bridge, () => main.ResultsWindow); Press(Keys.kbCtrlLeft); await Settle(bridge);
                    await Shot(bridge, "results", "earlier-run", "inventory");
                }
            }, showStart: true, settings: settings);
        }
        finally { Delete(root); }
    }

    // Run Ended Unexpectedly (a genuine runner-side failure) for the inventory at the primary grid.
    [Fact]
    public async Task InventoryRunEndedUnexpectedly()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs"), store = Store(root);
            string project = SaveProject(root, "ended.twproj", new WorkbenchProject
                { Name = "Ended sample", Target = Target, OutputRoot = runs, SelectedIds = [Status, Blocked], BaselineStore = store, TestType = "visual", VisualBehavior = "candidates", ScreenSizes = ["80x25"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                Press(Keys.kbF5); await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                Press(Keys.kbEnter);
                var monitor = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchRunMonitor>().SingleOrDefault());
                await Until(bridge, () => monitor.Text.Contains(Blocked), 40);
                File.AppendAllText(store, " ");
                var error = await Wait(bridge, () => Modal(app, WorkbenchDialog.EndedTitle), 60);
                await bridge.Invoke(() => Assert.Equal("OK", (error.current as TButton)?.Title));
                await Shot(bridge, "run-ended-unexpectedly", "baseline-policy-changed", "inventory");
                Press(Keys.kbEnter); await Until(bridge, () => !main.Busy && app.DeskTop!.TopView() is not TDialog, 60);
            });
        }
        finally { Delete(root); }
    }

    // ── Level C: real targets through the Workbench UI ───────────────────────────────

    // TVDemo with every target display driver and its native evidence kind. Environment-unavailable backends are
    // recorded (not converted to PASS); the provenance of every native artifact is checked against its layer.
    [TvDemoTheory]
    [InlineData("NullDriver", 0, 0)]
    [InlineData("SDLDriver", 1, 1)]
    [InlineData("SDLGpuDriver", 2, 2)]
    [InlineData("Win32ConsoleDriver", 3, 3)]
    public async Task RealTvDemoBackendsThroughWorkbench(string backend, int driverItem, int evidenceItem)
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Temp(); var opened = new List<(string Path, bool Folder)>();
        var previous = WorkbenchDialog.Opener; WorkbenchDialog.Opener = (p, f) => opened.Add((p, f));
        try
        {
            string runs = Path.Combine(root, "runs");
            string project = SaveProject(root, "tvdemo.twproj", WorkbenchProject.Load(TvDemoProject) with { OutputRoot = runs, SelectedIds = ["tvdemo/menu"], ScreenSizes = ["80x25"] });
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 6 ? p : null, 90);
                Press(Keys.kbF5); var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() => { dialog.ProxyDrivers.Press(driverItem); Nudge(dialog); dialog.Evidence.Press(evidenceItem); Nudge(dialog); });
                await bridge.Invoke(() => { Assert.Empty(dialog.Problems); dialog.RunButton.Select(); });
                Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 240);
                var batch = LastRun(runs); var result = Assert.Single(batch.Results);
                Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                string[] header = [], rows = [];
                await bridge.Invoke(() =>
                {
                    header = results.HeaderText.Split('\n');
                    // Row 0 is the execution Overview; row 1 is the first recorded checkpoint.
                    if (results.CheckpointRows.Length > 1) { results.CheckpointList.Select(); results.CheckpointList.FocusItemNum(1); Nudge(results); }
                    rows = results.InspectorRows;
                });
                await Shot(bridge, "results", "tvdemo-" + backend, "real-target");
                string[] nativeRows = rows.Where(r => r.Contains("(native")).Select(r => r.Trim()).ToArray();
                WriteEvidence("real-target-tvdemo-" + backend, new
                {
                    backend, evidence = ProxyCaptureKinds.All[evidenceItem], outcome = result.Outcome, result.CleanupSucceeded, result.FixtureIntegrityVerified,
                    batch.RunRoot, setup = header.FirstOrDefault(h => h.StartsWith("Setup", StringComparison.Ordinal)), nativeRows,
                    logicalRows = rows.Where(r => r.Contains("Logical PNG")).Select(r => r.Trim()).ToArray(), reason = result.Reason
                });
                // Every outcome is shown in Results; a recorded native artifact is never labelled logical and vice versa.
                Assert.DoesNotContain(rows, r => r.Contains("Logical PNG") && r.Contains("(native"));
                if (result.Outcome == "Passed")
                {
                    Assert.True(result.CleanupSucceeded);
                    if (evidenceItem > 0) Assert.NotEmpty(nativeRows);
                }
                else Assert.Contains(results.ExecutionRows, r => r.Contains("BLOCKED") || r.Contains("FAILED") || r.Contains("ERROR"));
            });
        }
        finally { WorkbenchDialog.Opener = previous; Delete(root); }
    }

    // Commander: one representative maintained external scenario on the existing ordinary pilot deployment (// artifacts/commander/current); nothing is rebuilt and the Commander repository is never touched.
    public sealed class CommanderFactAttribute : FactAttribute
    {
        public CommanderFactAttribute() { if (!File.Exists(CommanderExecutable)) Skip = "No ordinary Commander deployment at artifacts/commander/current (scripts/prepare-commander.ps1)."; }
    }
    private static string CommanderExecutable => Path.Combine(RepositoryRoot(), "artifacts", "commander", "current", "target", "TSharpCommander.exe");

    [CommanderFact]
    public async Task RealCommanderRepresentativeScenarioThroughWorkbench()
    {
        string root = Temp();
        try
        {
            string runs = Path.Combine(root, "runs");
            string source = Path.Combine(RepositoryRoot(), "examples", "TSharpCommander", "commander.twproj");
            var maintained = WorkbenchProject.Load(source);
            string project = Path.Combine(Path.GetDirectoryName(source)!, $"ui-{Guid.NewGuid():N}.twproj");
            try
            {
                // A temporary copy beside the maintained project keeps its relative scenario paths; output goes to the temp root.
                (maintained with { OutputRoot = runs, SelectedIds = ["commander/menu/f10-open-close"], ScreenSizes = ["80x25"] }).Save(project);
                await Drive(140, 40, root, async (app, bridge) =>
                {
                    await Loaded(app, bridge);
                    await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                    var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 4 ? p : null, 120);
                    await bridge.Invoke(() => Assert.Equal(["commander/menu/f10-open-close"], main.TestList.MarkedIds.ToArray()));
                    await Shot(bridge, "workspace", "commander-pilot", "real-target");
                    Press(Keys.kbF5); var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                    await bridge.Invoke(() => Assert.Empty(dialog.Problems));
                    Press(Keys.kbEnter); await Until(bridge, () => main.Busy, 30); await Until(bridge, () => !main.Busy, 240);
                    var batch = LastRun(runs); var result = Assert.Single(batch.Results);
                    Press(Keys.kbF6); var results = await Wait(bridge, () => main.ResultsWindow);
                    string[] rows = [];
                    await bridge.Invoke(() => { if (results.CheckpointRows.Length > 1) { results.CheckpointList.Select(); results.CheckpointList.FocusItemNum(1); Nudge(results); } rows = results.InspectorRows; });
                    await Shot(bridge, "results", "commander-f10-open-close", "real-target");
                    WriteEvidence("real-target-commander", new { scenario = result.ScenarioId, outcome = result.Outcome, result.CleanupSucceeded, result.FixtureIntegrityVerified, batch.RunRoot, rows });
                    Assert.Equal("Passed", result.Outcome); Assert.True(result.CleanupSucceeded);
                    Assert.Contains(results.ExecutionRows, r => r.StartsWith(" +  Passed", StringComparison.Ordinal));
                });
            }
            finally { File.Delete(project); }
        }
        finally { Delete(root); }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────

    private static async Task<BatchResult> MixedRun(string runs, string store)
    {
        var runner = new ScenarioRunner(); var catalog = await runner.DiscoverAsync(Target);
        using var cancel = new CancellationTokenSource();
        var selected = new[] { Status, Failure, Blocked }.Select(id => catalog.Single(s => s.Id == id)).ToArray();
        return await runner.RunAsync(Target, selected, new RunOptions(runs, ["80x25", "20x6"], Baselines: store),
            message => { if (message.StartsWith("Failed: " + Failure, StringComparison.Ordinal)) cancel.Cancel(); }, cancel.Token);
    }
    private static async Task<BatchResult> PassedRun(string runs)
    {
        var runner = new ScenarioRunner(); var catalog = await runner.DiscoverAsync(Target);
        return await runner.RunAsync(Target, [catalog.Single(s => s.Id == Dialog)], ["80x25"], runs);
    }
    private static string Store(string root)
    {
        string store = Path.Combine(root, "store", "visual-baselines.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
            [new(Status, Gated, ["80x25"], "Disposable store")]), ScenarioRunner.Json));
        return store;
    }
    private static string Hash(string path) => ScreenSnapshot.Hash(File.ReadAllBytes(path));
    private static Dictionary<string, string> Hashes(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(folder, f), Hash) : [];
    private static BatchResult LastRun(string runs) =>
        WorkbenchRunHistory.Load(Path.Combine(Directory.GetDirectories(runs, "run-*").OrderByDescending(Directory.GetCreationTimeUtc).First(), "run.json")).Batch;
    private static int Index(ScenarioList list, string id) =>
        Enumerable.Range(0, list.range).Single(i => list.GetText(i, 400).Contains(id));
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    private static string Kebab(TWindow window) => window switch
    {
        WorkbenchDialog => "workspace", WorkbenchRunMonitor => "run-monitor", WorkbenchResultsDialog => "results",
        WorkbenchComparisonWindow => "comparison", WorkbenchMessagesWindow => "messages", _ => window.GetType().Name.ToLowerInvariant()
    };

    // ── harness (shared shape with the other UX suites) ──────────────────────────────────────

    private static async Task Drive(int width, int height, string root, Func<WorkbenchApp, Bridge, Task> body, bool showStart = false, string? settings = null, bool exits = false)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs");
        settings ??= Path.Combine(root, "settings.json");
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(showStart ? "" : Target, output, new(showStart ? "" : Target, output, null, "80x25") { StoragePath = settings }, showStart: showStart);
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                await body(app, bridge);
                if (!exits) await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
            }
            catch (Exception ex)
            {
                failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                Press(Keys.kbEnter);   // ends a keyboard Size/Move loop left open by the failure
                await bridge.Invoke(() =>
                {
                    for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                    if (app.Project is { Busy: true }) Command(app.Project, WorkbenchDialog.CancelRun);
                    app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                }).WaitAsync(TimeSpan.FromSeconds(20));
            }
        });
        MnemonicProblems.Clear();
        AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(60)), "Worker did not stop.");
        Assert.Null(failure);
        Assert.Empty(MnemonicProblems.Distinct());
    }
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static async Task<WorkbenchDialog> LoadedProject(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "ui-" + Guid.NewGuid().ToString("N")[..12]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static bool Intersect(TView a, TView b) =>
        a.origin.x < b.origin.x + b.size.x && b.origin.x < a.origin.x + a.size.x && a.origin.y < b.origin.y + b.size.y && b.origin.y < a.origin.y + a.size.y;
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    // Optional logical screenshots for the inventory: TSWB_QUALIFICATION_EVIDENCE=<flat directory>. Each capture is the NullDriver
    // cell screen of the real WorkbenchApp (logical; never desktop-native): <window>-<state>-<grid>.logical.png + .tvs1 + .json.
    private static async Task Shot(Bridge bridge, string window, string state, string workflow, bool optional = false)
    {
        // Every captured state is also checked for duplicate Alt mnemonics in the active window or modal dialog.
        await Settle(bridge);
        await bridge.Invoke(() =>
        {
            foreach (var group in new[] { bridge.TopView() as TGroup, (bridge.owner as TGroup)?.current as TGroup }.Distinct())
                if (group is not null and not TFileDialog and not TChDirDialog) MnemonicProblems.AddRange(DuplicateMnemonics(group).Select(p => $"{window}/{state}: {p}"));
        });
        if (Environment.GetEnvironmentVariable("TSWB_QUALIFICATION_EVIDENCE") is not { Length: > 0 } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string grid = $"{screen.Width}x{screen.Height}", name = $"{window}-{state}-{grid}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, window, state, workflow, grid, captureType = "logical", driver = "NullDriver", test = nameof(ProductQualificationTests),
                provenance = "NullDriver cell screen of the real WorkbenchApp event loop, rendered by the diagnostic bitmap font (not desktop-native)",
                transient = optional, renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static readonly List<string> MnemonicProblems = [];
    // Alt mnemonics (~x~) of buttons, labels and cluster items, including those inside group boxes; a letter used twice
    // in one window is ambiguous (the later control wins in Turbo Vision's post-process order).
    internal static IEnumerable<string> DuplicateMnemonics(TGroup group)
    {
        var found = new List<(char Key, string Owner)>();
        void Walk(TGroup g)
        {
            foreach (var v in Children(g))
            {
                IEnumerable<string> texts = v switch
                {
                    TButton b => [b.Title ?? ""], TLabel l => [LabelText(l)], TCluster c => c.Strings.Where(s => s is not null), _ => []
                };
                foreach (string t in texts) { int i = t.IndexOf('~'); if (i >= 0 && i + 1 < t.Length) found.Add((char.ToLowerInvariant(t[i + 1]), t.Replace("~", ""))); }
                if (v is TGroup inner and not TWindow) Walk(inner);
            }
        }
        Walk(group);
        return found.GroupBy(f => f.Key).Where(k => k.Count() > 1).Select(k => $"{(group as TWindow)?.title}: Alt+{char.ToUpperInvariant(k.Key)} used by {string.Join(" / ", k.Select(f => f.Owner))}");
    }
    private static string LabelText(TLabel label) { label.GetText(out string text); return text; }
    private static void WriteEvidence(string name, object data)
    {
        if (Environment.GetEnvironmentVariable("TSWB_QUALIFICATION_EVIDENCE") is not { Length: > 0 } root) return;
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "qualification"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Press(char letter) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = letter; ev.keyDown.charScan = new CharScanType(letter); TEventQueue.Enqueue(ev); }
    private static void Type(string text) { foreach (char c in text) Press(c); }
    private static void PressAlt(char digit) => Press((ushort)(Keys.kbAlt1 + (digit - '1') * 0x100));
    // Alt+3 reaches the Run Monitor only while a run is active (idle Alt+3 opens Run Configuration);
    // a finished monitor is selected directly, as Window > Next window or a mouse click would.
    private static async Task Front(WorkbenchApp app, Bridge bridge, TView window)
    {
        await bridge.Invoke(() => { window.Select(); if (!ReferenceEquals(app.DeskTop!.current, window)) app.DeskTop.SetCurrent(window, TView.selectMode.normalSelect); });
        await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, window));
    }
    private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    // The framework's keyboard Size/Move loop reads only key events and drops anything else (including the bridge's posted work),
    // so the test waits for the queued keys to be consumed before it posts again.
    private static async Task KeysSlowly(params ushort[] keys) { foreach (ushort k in keys) { Press(k); await Task.Delay(300); } }
    private static async Task DragDone(Bridge bridge) { await Task.Delay(800); await Settle(bridge); }
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
