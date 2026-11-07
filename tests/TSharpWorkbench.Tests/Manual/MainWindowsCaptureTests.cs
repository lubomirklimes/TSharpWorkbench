using System.Diagnostics;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;
using Host = TSharpWorkbench.Tests.Core.TestWorkbenchHostTests;

namespace TSharpWorkbench.Tests.Manual;

// Manual: drives the real WorkbenchApp (NullDriver, 120x36) through the windows of the chapter
// "Hlavní okna, projekty a správa" and writes logical PNGs to <repo>/artifacts/main-windows/*.png.
// Skipped in normal runs; runs under "Debug Test" or with MAIN_WINDOWS_CAPTURE set.
[Collection("NonParallel")]
public sealed class MainWindowsCaptureTests
{
    public sealed class ManualFactAttribute : FactAttribute
    {
        public ManualFactAttribute()
        {
            if (!Debugger.IsAttached && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAIN_WINDOWS_CAPTURE")))
                Skip = "Manual capture: run with Debug Test (or set MAIN_WINDOWS_CAPTURE).";
        }
    }

    private const int Width = 120, Height = 36;

    [ManualFact, Trait("Category", "Manual")]
    public async Task CaptureMainWindows()
    {
        string output = Path.Combine(RepositoryRoot(), "artifacts", "main-windows");
        Directory.CreateDirectory(output);
        var errors = new List<string>();
        string root = Temp();
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(Width, Height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                async Task Shot(string name, Func<Task> open, Func<Task>? close = null)
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
                }
                Task CloseModal(string title) { Press(Keys.kbEsc); return Until(bridge, () => Modal(app, title) is null, 10); }

                await Shot("start-page", () => Wait(bridge, () => app.Start));

                await bridge.Invoke(() => Assert.True(app.CreateNativeProject(Path.Combine(root, "Capture.tsw"))));
                var project = await Wait(bridge, () => app.Project);
                await Shot("project-information", () => Wait(bridge, () => app.Overview),
                    async () => { Press(Keys.kbEsc); await Until(bridge, () => app.Overview is null, 10); });

                await Shot("project-properties", async () =>
                {
                    Press(Keys.kbF4);
                    var properties = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog, 10);
                    await bridge.Invoke(() => properties.ExecutableField.SetData(Host.SampleTarget));
                }, async () => { Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null, 10); });
                await Loaded(app, bridge, 1);

                await Shot("test-explorer", async () => { Send(WorkbenchTestExplorer.Command); await Wait(bridge, () => project.TestExplorer, 10); });

                await Shot("scenario-manager", async () => { Send(WorkbenchDialog.ManageScenarios); await Wait(bridge, () => project.ScenariosWindow, 10); });
                await Shot("new-scenario", async () => { Send(WorkbenchDialog.NewScenario); await Wait(bridge, () => Modal(app, WorkbenchNewScenarioDialog.Title), 10); },
                    () => CloseModal(WorkbenchNewScenarioDialog.Title));

                string folder = Path.Combine(root, "scenario-folder");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "alpha.scenario.json"), ScenarioDocument.New("alpha-scenario", "Alpha scenario", "Alpha", "First").SerializeText());
                File.WriteAllText(Path.Combine(folder, "broken.scenario.json"), "{ not json");
                File.WriteAllText(Path.Combine(folder, "notes.txt"), "ignored");
                await Shot("add-scenario-folder", async () =>
                {
                    _ = bridge.Invoke(() => { using var d = new WorkbenchAddFolderDialog(app.DeskTop!, folder, []); app.DeskTop!.ExecView(d); });
                    await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchAddFolderDialog, 10);
                }, async () => { Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchAddFolderDialog, 10); });

                WorkbenchScenarioEditorWindow? editor = null;
                await Shot("scenario-editor", async () =>
                {
                    await bridge.Invoke(() =>
                    {
                        var doc = ScenarioDocument.New("beta-scenario", "Beta scenario", "Beta description", "Second").SerializeText();
                        string path = project.Native!.CreateScenario(doc);
                        project.SetScenarioFiles([path], "Scenario added.", path);
                        editor = project.OpenScenarioEditor(path);
                    });
                    Assert.NotNull(editor);
                });
                await Shot("scenario-metadata", async () => { Press(Keys.kbAltM); await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title), 10); },
                    () => CloseModal(WorkbenchScenarioMetadataDialog.Title));
                if (editor is not null) await bridge.Invoke(() => editor.CloseApproved());

                await Shot("new-test", async () =>
                {
                    _ = bridge.Invoke(() => WorkbenchTestExplorer.EditTest(app.DeskTop!, project.Native!.Session, null, metadata: project.TestScenarioMetadata));
                    await Wait(bridge, () => Modal(app, "New Test"), 10);
                }, () => CloseModal("New Test"));

                await Shot("environment-manager", async () => { Send(WorkbenchApp.Environments); await Wait(bridge, () => Modal(app, "Environment Manager"), 10); });
                await Shot("environment-properties", async () => { Press(Keys.kbAltE); await Wait(bridge, () => Modal(app, "Environment Properties"), 10); },
                    async () => { await CloseModal("Environment Properties"); await CloseModal("Environment Manager"); });

                await Shot("messages", async () => { Send(WorkbenchApp.MessagesWindow); await Wait(bridge, () => app.Messages, 10); });

                await Shot("help", async () => { Press(Keys.kbF1); await Wait(bridge, () => app.HelpWindow, 10); },
                    async () => { Press(Keys.kbEsc); await Until(bridge, () => app.HelpWindow is null, 10); });
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
