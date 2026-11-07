using System.Diagnostics;
using System.Reflection;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Manual;

// Manual: opens the confirmation and information windows of the chapter "Potvrzení a informační okna ze sdílených
// továren" through their shared factories (real titles, representative texts) and writes logical PNGs named
// <defining class>-<window title>.png to <repo>/artifacts/shared-dialogs.
// Skipped in normal runs; runs under "Debug Test" or with SHARED_DIALOGS_CAPTURE set.
[Collection("NonParallel")]
public sealed class SharedDialogsCaptureTests
{
    public sealed class ManualFactAttribute : FactAttribute
    {
        public ManualFactAttribute()
        {
            if (!Debugger.IsAttached && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHARED_DIALOGS_CAPTURE")))
                Skip = "Manual capture: run with Debug Test (or set SHARED_DIALOGS_CAPTURE).";
        }
    }

    private const int Width = 120, Height = 36;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [ManualFact, Trait("Category", "Manual")]
    public async Task CaptureSharedDialogs()
    {
        string output = Path.Combine(RepositoryRoot(), "artifacts", "shared-dialogs");
        Directory.CreateDirectory(output);
        var errors = new List<string>();
        string root = Temp();
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(Width, Height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.CreateNativeProject(Path.Combine(root, "Capture.tsw"))));
                var project = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null, 10);
                var desktop = app.DeskTop!;

                async Task Shot(string owner, string title, Action open)
                {
                    string name = owner + "-" + new string(title.Where(char.IsLetterOrDigit).ToArray());
                    try
                    {
                        _ = bridge.Invoke(open);
                        await Wait(bridge, () => Modal(app, title), 10);
                        await Settle(bridge);
                        await bridge.Invoke(() => File.WriteAllBytes(Path.Combine(output, name + ".png"), LogicalPng.Encode(ScreenSnapshot.CopyRoot())));
                    }
                    catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                    try
                    {
                        if (Modal(app, title) is { } dialog) await bridge.Invoke(() => ((TDialog)dialog).EndModal(Views.cmCancel));
                        await Until(bridge, () => Modal(app, title) is null, 10);
                    }
                    catch (Exception ex) { errors.Add($"{name} (close): {ex.Message}"); }
                }
                static object? Call(Type type, object? target, string method, params object?[] args)
                {
                    try { return type.GetMethod(method, Any)!.Invoke(target, args); }
                    catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
                }

                string scenarioPath = Path.Combine(root, "scenarios", "dialog-basic.scenario.json");
                const string D = nameof(WorkbenchDialog);

                await Shot(D, WorkbenchDialog.UnsavedTitle, () => WorkbenchDialog.UnsavedChanges(desktop, "Capture", [
                    "project properties (name, target, runs folder, baseline store or capabilities)",
                    "attached scenario files (now 3 attached)", "marked scenarios (now 2 marked)", "run defaults (from Run Configuration)"],
                    Path.Combine(root, "Capture.tsw")));
                await Shot(D, "Unsaved Scenario", () => WorkbenchDialog.Prompt(desktop, "Unsaved Scenario",
                    "dialog-basic.scenario.json has unsaved changes.\n\nSave them before closing?", true));
                await Shot(nameof(WorkbenchScenarioEditorWindow), "File Changed on Disk", () => Call(typeof(WorkbenchScenarioEditorWindow), null, "ExternalChangeChoice", desktop,
                    "dialog-basic.scenario.json was changed on disk after it was opened here. Reload discards the edits in this editor; Save as... keeps them in another file."));
                await Shot(D, "Replace File?", () => WorkbenchDialog.Confirm(desktop, "Replace File?",
                    $"dialog-basic.scenario.json already exists. Replace it with the new scenario?\n\n{scenarioPath}", "~R~eplace"));
                await Shot(D, "Attach Scenario?", () => WorkbenchDialog.Confirm(desktop, "Attach Scenario?",
                    $"Saved Dialog basic.\n\nAttach it to this project so it is listed in the Workspace catalog?\n\n{scenarioPath}", "~A~ttach"));
                await Shot(D, "Import External Scenario", () => WorkbenchDialog.Confirm(desktop, "Import External Scenario",
                    "Save and close the external editor before importing. The checkout is kept at:\n" + Path.Combine(root, "work", "checkout", "dialog-basic.scenario.json"), "~I~mport"));
                await Shot(D, WorkbenchDialog.RemoveTitle, () => WorkbenchDialog.Confirm(desktop, WorkbenchDialog.RemoveTitle,
                    "Remove dialog-basic.scenario.json from the project? The file is not deleted.\n\n" + scenarioPath, "~R~emove"));
                await Shot(D, "Remove Test", () => WorkbenchDialog.Confirm(desktop, "Remove Test",
                    "Remove test Dialog opens and closes? Its scenario remains available.", "~R~emove"));
                await Shot(D, "Remove Environment", () => WorkbenchDialog.Confirm(desktop, "Remove Environment",
                    "Remove Local? Tests using it must be updated first.", "~R~emove"));
                await Shot(D, "Adopt Baseline Store", () => WorkbenchDialog.Confirm(desktop, "Adopt Baseline Store",
                    "Copy this manifest and its approved artifacts into the project? Future test runs will compare with the project store. The source remains intact.", "~A~dopt"));
                await Shot(D, "Discard Recording?", () => WorkbenchDialog.Confirm(desktop, "Discard Recording?",
                    "4 recorded steps have not been inserted into the editor.\n\nDiscard them?", "~D~iscard"));
                await Shot(D, WorkbenchDialog.RunningTitle, () => Call(typeof(WorkbenchDialog), project, "ConfirmLeavingRun", "exit", (Action)(() => { })));
                await Shot(D, WorkbenchDialog.EndedTitle, () => Call(typeof(WorkbenchDialog), project, "RunEndedUnexpectedly",
                    new InvalidOperationException("The target process exited before the first checkpoint")));

                await Shot(D, "Project Paths", () => WorkbenchDialog.ShowMessage(desktop, "Project Paths",
                    $"Project file\n{Path.Combine(root, "Capture.tsw")}\n\nTarget\n{Host.SampleTarget}\n\nRuns folder\n{Path.Combine(root, "runs")}"));
                await Shot(D, "Compiled Scenario", () => WorkbenchDialog.ShowMessage(desktop, "Compiled Scenario",
                    "sample/dialog/basic\nDialog basic\nOpens a dialog and closes it.\nCompiled scenarios are read-only. Use Link Test to assign this scenario."));
                await Shot(D, "Scenario Editor", () => WorkbenchDialog.ShowMessage(desktop, "Scenario Editor", "This compiled scenario is read-only."));
                await Shot(D, "Test Needs Assignments", () => WorkbenchDialog.ShowMessage(desktop, "Test Needs Assignments",
                    "Create a Test from Scenario Manager, or import a scenario and add at least one environment first."));
                await Shot(D, "Test Not Changed", () => WorkbenchDialog.ShowMessage(desktop, "Test Not Changed", "A test with the title 'Dialog opens and closes' already exists."));
                await Shot(D, "Environment Not Changed", () => WorkbenchDialog.ShowMessage(desktop, "Environment Not Changed", "An environment named 'Local' already exists."));
                await Shot(D, "Baseline Details", () => WorkbenchDialog.ShowMessage(desktop, "Baseline Details",
                    "sample/status/context / desktop-status / 80x25   MISSING\n\nNo approved baseline exists for this checkpoint and grid. Run the test with candidates kept, then approve a candidate."));
                await Shot(D, WorkbenchResultsDialog.TechnicalTitle, () => WorkbenchDialog.ShowMessage(desktop, WorkbenchResultsDialog.TechnicalTitle,
                    "sample/dialog/basic at 80x25: FAILED\n\n" + string.Join("\n", Enumerable.Range(1, 12).Select(i => $"   at TSharpWorkbench.Testing.ScenarioRunner.Step{i}() in ScenarioRunner.cs:line {100 + i}"))));
                await Shot(nameof(WorkbenchErrorDialog), "Cannot Open Project", () => WorkbenchErrorDialog.Show(desktop, app.Notifications, "Cannot Open Project",
                    $"{Path.Combine(root, "Broken.tsw")} is not a valid TSharpWorkbench project: the archive manifest is missing.", null, null));
                await Shot(nameof(WorkbenchApp), "About TSharpWorkbench", () => Call(typeof(WorkbenchApp), app, "ShowInformation", "About TSharpWorkbench", WorkbenchHelp.About()));
                await Shot(nameof(WorkbenchApp), "Help unavailable", () => Call(typeof(WorkbenchApp), app, "ShowInformation", "Help unavailable", new[] {
                    "The installed help resource is missing, damaged or incompatible.", "Reinstall or redeploy the complete Workbench distribution,",
                    "including Help/TSharpWorkbench.hlp and its .sha256 file.", "Project operations remain available.", "", "Could not find file 'Help/TSharpWorkbench.hlp'." }));
                await Shot(D, "Test mode capabilities", () => Call(typeof(WorkbenchDialog), project, "Browse", "Test mode capabilities",
                    WorkbenchCapabilities.Describe(), true, null));
            }, showStart: true);
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Delete(root);
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static class Host { internal static string SampleTarget => TSharpWorkbench.Tests.Core.TestWorkbenchHostTests.SampleTarget; }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found.");
    }
}
