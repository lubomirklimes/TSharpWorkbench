using System.Diagnostics;
using System.Reflection;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Manual;

// Manual: opens the file and folder pickers of the chapter "Výběry souborů a adresářů" through the code that defines
// them and writes logical PNGs named <defining class>-<window title>.png to <repo>/artifacts/file-pickers.
// Skipped in normal runs; runs under "Debug Test" or with FILE_PICKERS_CAPTURE set.
[Collection("NonParallel")]
public sealed class FilePickersCaptureTests
{
    public sealed class ManualFactAttribute : FactAttribute
    {
        public ManualFactAttribute()
        {
            if (!Debugger.IsAttached && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FILE_PICKERS_CAPTURE")))
                Skip = "Manual capture: run with Debug Test (or set FILE_PICKERS_CAPTURE).";
        }
    }

    private const int Width = 120, Height = 36;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [ManualFact, Trait("Category", "Manual")]
    public async Task CaptureFilePickers()
    {
        string output = Path.Combine(RepositoryRoot(), "artifacts", "file-pickers");
        Directory.CreateDirectory(output);
        var errors = new List<string>();
        string root = Temp();
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            // Some content for the pickers to list.
            string scenarios = Path.Combine(root, "scenarios"), runs = Path.Combine(root, "runs", "run-20250101-120000");
            Directory.CreateDirectory(scenarios); Directory.CreateDirectory(runs); Directory.CreateDirectory(Path.Combine(root, "store"));
            foreach (string name in new[] { "alpha", "beta", "gamma" })
                File.WriteAllText(Path.Combine(scenarios, name + ".scenario.json"), ScenarioDocument.New(name, name, name, "initial").SerializeText());
            File.WriteAllText(Path.Combine(runs, "run.json"), "{}");
            File.WriteAllText(Path.Combine(root, "store", "visual-baselines.json"), "{}");
            File.WriteAllText(Path.Combine(root, "Other.tsw"), "");

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
                        if (Modal(app, title) is TDialog dialog) await bridge.Invoke(() => dialog.EndModal(Views.cmCancel));
                        await Until(bridge, () => Modal(app, title) is null, 10);
                    }
                    catch (Exception ex) { errors.Add($"{name} (close): {ex.Message}"); }
                }
                static void Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Any)!.Invoke(target, args);

                const string D = nameof(WorkbenchDialog);
                await Shot(D, "Open project", () => WorkbenchDialog.ChooseProjectPath(desktop, false, Path.Combine(root, "Capture.tsw")));
                await Shot(D, "Save project as", () => WorkbenchDialog.ChooseProjectPath(desktop, true, null, Path.Combine(root, "New project.tsw")));
                await Shot(D, "Select target executable", () => Call(project, "SelectTarget"));
                await Shot(D, "Open completed run", () => Call(project, "OpenHistoricalRun"));
                await Shot(D, "Adopt Baseline Store", () => Call(project, "AdoptBaselineStore"));
                await Shot(D, "Add scenario files", () => Call(project, "AddFiles"));
                await Shot(D, "Add scenario files (2 added)", () =>
                {
                    // The repeat-add title of AddFiles, shown after files were added (same construction as AddFiles).
                    using var picker = new TFileDialog("*.json", "Add scenario files (2 added)", "Scenario ~f~ile",
                        FileDialogOptions.fdAddButton | FileDialogOptions.fdDoneButton | FileDialogOptions.fdNoLoadDir, 0);
                    picker.fileName!.MaxLen = 32767; WorkbenchDialog.Widen(picker, desktop);
                    picker.SetData(Path.Combine(scenarios, "*.json"));
                    desktop.ExecView(picker);
                });
                await Shot(D, "Save new scenario as", () => Call(project, "ChooseScenarioPath", Path.Combine(scenarios, "new-scenario.scenario.json"), "Save new scenario as"));
                await Shot(D, "Save scenario as", () => Call(project, "ChooseScenarioPath", Path.Combine(scenarios, "alpha-copy.scenario.json"), "Save scenario as"));

                Press(Keys.kbF4);
                var properties = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog, 10);
                await bridge.Invoke(() => properties.ExecutableField.SetData(Path.Combine(root, "target.exe")));
                const string P = nameof(WorkbenchPropertiesDialog);
                await Shot(P, "Select target executable", () => Call(properties, "PickExecutable"));
                await Shot(P, WorkbenchPropertiesDialog.ManifestPickerTitle, () => Call(properties, "PickManifest"));
                await bridge.Invoke(() => properties.EndModal(Views.cmCancel));
                await Until(bridge, () => Modal(app, "Project Properties") is null, 10);
            }, showStart: true);
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Delete(root);
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found.");
    }
}
