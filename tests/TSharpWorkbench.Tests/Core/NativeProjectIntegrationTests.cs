using TSharpVision.Constants;
using TSharpWorkbench.Storage;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class NativeProjectIntegrationTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task NativeProjectSaveCloseAndReopen(int width, int height)
    {
        string root = Temp(), path = Path.Combine(root, "Native.tsw");
        string? original = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await Capture(bridge, "native-start");
                await bridge.Invoke(() => Assert.True(app.CreateNativeProject(path)));
                var project = await Wait(bridge, () => app.Project);
                var overview = await Wait(bridge, () => app.Overview);
                Assert.True(overview.GetState(Views.sfModal));
                Assert.Equal(HelpTopic.Workspace, WorkbenchHelpContext.Resolve(overview));
                await Capture(bridge, "project-overview");
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Press(Keys.kbF3);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);

                WorkbenchScenarioEditorWindow? editor = null;
                await bridge.Invoke(() => editor = project.ShowEditor(ScenarioDocument.New("draft", "Draft", "")));
                Send(WorkbenchApp.CloseProject);
                await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                await bridge.Invoke(() =>
                {
                    Assert.Same(project, app.Project);
                    Assert.Same(editor, Assert.Single(project.ScenarioEditors));
                    Assert.True(editor!.Document.Dirty);
                    Assert.False(File.Exists(path));
                    editor.CloseApproved();
                });
                Assert.Equal("Local headless", Assert.Single(project.Native!.Session.Environments).Name);
                Press(Keys.kbF3);
                overview = await Wait(bridge, () => app.Overview);
                await bridge.Invoke(() => Descendants(overview).OfType<TButton>()
                    .Single(button => button.Command == WorkbenchApp.Environments).Press());
                var manager = await Wait(bridge, () => project.EnvironmentManager);
                await bridge.Invoke(() =>
                {
                    Assert.Null(app.Overview);
                    Assert.True(manager.GetState(Views.sfModal));
                });
                await Capture(bridge, "environment-manager");
                Press(Keys.kbAltE);
                await Wait(bridge, () => Modal(app, "Environment Properties"));
                var environment = await Wait(bridge, () => Modal(app, "Environment Properties"));
                await Capture(bridge, "environment-properties");
                await bridge.Invoke(() =>
                {
                    var combos = Descendants(environment).OfType<TComboBox>().ToArray();
                    Assert.Equal(4, combos.Length);
                    Assert.Single(Descendants(environment).OfType<TCheckBoxes>());
                    combos.Single(c => c.Items.Contains("Linux")).Text = "Linux";
                    combos.Single(c => c.Items.Contains("160x50")).Text = "140x40";
                });
                Press(Keys.kbAltS);
                await Until(bridge, () => Modal(app, "Environment Properties") is null);
                await Wait(bridge, () => project.EnvironmentManager);
                Press(Keys.kbEsc);
                await Until(bridge, () => project.EnvironmentManager is null);
                var repository = new DirectoryInfo(AppContext.BaseDirectory);
                while (!File.Exists(Path.Combine(repository.FullName, "TSharpWorkbench.slnx"))) repository = repository.Parent!;
                var sources = Directory.GetFiles(Path.Combine(repository.FullName, "examples", "TSharpCommander", "scenarios"), "*.json").Order().ToArray();
                var bytes = sources.Select(File.ReadAllBytes).ToArray();
                string[] imported = [];
                await bridge.Invoke(() =>
                {
                    Assert.Equal("linux", project.Native!.Session.Environments.Single().OperatingSystem);
                    Assert.Equal(140, project.Native.Session.Environments.Single().Width);
                    imported = project.Native.Import(sources);
                    Assert.Throws<ArgumentException>(() => project.Native.Import(sources));
                    project.SetScenarioFiles(imported, "Imported four scenarios", imported[0]);
                    project.Native.Session.PutEnvironment(new("second", "Another profile", "windows", "NullDriver", 80, 25, "none"));
                });
                Send(WorkbenchDialog.ManageScenarios);
                await Wait(bridge, () => project.ScenariosWindow);
                await Capture(bridge, "scenario-manager");
                Press(Keys.kbEsc);
                await Until(bridge, () => project.ScenariosWindow is null);
                Send(WorkbenchTestExplorer.Command);
                await Wait(bridge, () => project.TestExplorer);
                await Capture(bridge, "test-explorer-empty");
                for (int index = 0; index < 2; index++)
                {
                    Press(Keys.kbAltN);
                    var newTest = await Wait(bridge, () => Modal(app, "New Test"));
                    int selectedEnvironment = index;
                    await bridge.Invoke(() =>
                    {
                        Descendants(newTest).OfType<TInputLine>().OrderBy(x => x.origin.y).First().SetData("Shared test " + selectedEnvironment);
                        var list = Descendants(newTest).OfType<TListBox>().Single();
                        list.FocusItemNum(selectedEnvironment);
                        list.Select();
                    });
                    await Capture(bridge, "new-test-" + index);
                    Press(Keys.kbSpace);
                    Press(Keys.kbAltS);
                    await Until(bridge, () => Modal(app, "New Test") is null);
                    await Wait(bridge, () => project.TestExplorer);
                }
                await bridge.Invoke(() => Descendants(project.TestExplorer!).OfType<TListBox>().Single().FocusItemNum(1));
                await Capture(bridge, "test-explorer-populated");
                Press(Keys.kbAltI);
                await Wait(bridge, () => Modal(app, "Test Details"));
                await Capture(bridge, "test-details");
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Test Details") is null);
                await bridge.Invoke(() => Assert.Equal(1, Descendants(project.TestExplorer!).OfType<TListBox>().Single().focused));
                Press(Keys.kbEsc);
                await Until(bridge, () => project.TestExplorer is null);
                await bridge.Invoke(() =>
                {
                    var definitions = project.Native!.Session.Tests;
                    Assert.Equal(2, definitions.Count);
                    Assert.Single(definitions.Select(t => t.ScenarioId).Distinct());
                    Assert.Equal(2, definitions.SelectMany(t => t.EnvironmentIds).Distinct().Count());
                });
                await bridge.Invoke(() => Assert.True(project.SaveProject(false)));
                await Capture(bridge, "native-project");
                var originalBytes = File.ReadAllBytes(path);
                string copyPath = Path.Combine(root, "Copy.tsw");
                await bridge.Invoke(() => Assert.True(project.SaveProject(true, copyPath)));
                Assert.Equal(copyPath, project.ProjectPath);
                Assert.Equal(originalBytes, File.ReadAllBytes(path));
                Assert.Equal(2, project.Native!.Session.Tests.Count);
                string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
                await bridge.Invoke(() => Assert.True(project.SaveProject(false)));
                Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
                Send(WorkbenchApp.CloseProject);
                await Until(bridge, () => app.Project is null);
                using (var archive = ProjectSession.Open(path, workspaceRoot: NativeProject.WorkspaceRoot))
                {
                    Assert.Equal("Native", archive.Project.Name);
                    Assert.NotEmpty(archive.Settings);
                    archive.Close(CloseChoice.Save);
                }
                await bridge.Invoke(() => Assert.True(app.OpenNativeProject(copyPath)));
                var reopened = await Wait(bridge, () => app.Project);
                Assert.Equal(copyPath, reopened.ProjectPath);
                Assert.Equal(2, reopened.Native!.Session.Tests.Count);
                for (int j = 0; j < sources.Length; j++)
                {
                    Assert.Equal(bytes[j], File.ReadAllBytes(sources[j]));
                    var scenario = ScenarioFiles.Inspect(sources[j]);
                    var reference = reopened.Native.Session.Scenarios.Single(r => r.Id == scenario.Id);
                    using var payload = new MemoryStream();
                    reopened.Native.Session.CopyArtifactTo(reference.ArtifactPath!, payload);
                    Assert.Equal(bytes[j], payload.ToArray());
                }
                Send(WorkbenchApp.CloseProject);
                await Until(bridge, () => app.Project is null);
            }, showStart: true);
        }
        finally { NativeProject.WorkspaceRoot = original; Delete(root); }
    }
}
