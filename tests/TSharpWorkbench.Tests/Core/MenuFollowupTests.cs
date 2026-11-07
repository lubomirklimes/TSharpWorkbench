using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class MenuFollowupTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task NativeProjectMenuAndWindowActions(int width, int height)
    {
        string root = Temp();
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.CreateNativeProject(Path.Combine(root, "Menu.tsw"))));
                var project = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);

                Press(Keys.kbAltP);
                await Wait(bridge, () => Descendants(app).OfType<TMenuBox>().FirstOrDefault());
                await Capture(bridge, "project-menu");
                Press('p');
                var overview = await Wait(bridge, () => app.Overview);
                await bridge.Invoke(() => Assert.True(overview.GetState(Views.sfModal)));
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Press(Keys.kbF3);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);

                Press(Keys.kbAltW);
                await Wait(bridge, () => Descendants(app).OfType<TMenuBox>().FirstOrDefault());
                await Capture(bridge, "window-menu");
                Press(Keys.kbEsc);
                await Until(bridge, () => !Descendants(app).OfType<TMenuBox>().Any());

                Press(Keys.kbAltG);
                await Wait(bridge, () => Descendants(app).OfType<TMenuBox>().FirstOrDefault());
                await Capture(bridge, "settings-menu");
                Press('p');
                await Wait(bridge, () => Modal(app, "Project Properties"));
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Project Properties") is null);

                Press(Keys.kbF4);
                var properties = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(() => properties.ExecutableField.SetData(TestWorkbenchHostTests.SampleTarget));
                Press(Keys.kbEnter);
                await Until(bridge, () => Modal(app, "Project Properties") is null);
                await Until(bridge, () => !project.Busy);

                string scenarioPath = "";
                string contents = ScenarioDocument.New("menu-proxy", "Menu proxy", "Validation fixture").SerializeText();
                await bridge.Invoke(() =>
                {
                    var native = project.Native!;
                    scenarioPath = native.CreateScenario(contents);
                    project.SetScenarioFiles([scenarioPath], "Scenario added.", scenarioPath);
                    string environment = Assert.Single(native.Session.Environments).Id;
                    native.Session.PutTest(new("alpha", "Alpha test", "", "", "menu-proxy", [environment]));
                    native.Session.PutTest(new("beta", "Beta test", "", "", "menu-proxy", [environment]));

                    string id = Guid.NewGuid().ToString("N");
                    var now = DateTimeOffset.UtcNow;
                    var entry = new WorkbenchMatrix.Entry("alpha", "Historical Alpha", "menu-proxy", Assert.Single(native.Session.Environments));
                    var run = new WorkbenchMatrix.Run(id, now, now, [new(entry, "Passed", "")]);
                    using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(run, ScenarioRunner.Json));
                    native.Session.PutArtifact($"runs/{id}/matrix.json", input);
                });

                Press(Keys.kbAlt2);
                var manager = await Wait(bridge, () => project.ScenariosWindow);
                await Capture(bridge, "scenario-manager");
                Press(Keys.kbAltV);
                await Until(bridge, () => app.Notifications.History.Any(n => n.Message.StartsWith("Validation passed")));
                Assert.Equal(contents, File.ReadAllText(scenarioPath));
                int validationCount = 0;
                await bridge.Invoke(() => validationCount = app.Notifications.History.Count(n => n.Message.StartsWith("Validation passed")));
                Press(Keys.kbF2);
                await Settle(bridge);
                await Until(bridge, () => !project.Busy);
                await bridge.Invoke(() => Assert.Equal(validationCount, app.Notifications.History.Count(n => n.Message.StartsWith("Validation passed"))));

                Send(WorkbenchTestExplorer.Command);
                var explorer = await Wait(bridge, () => project.TestExplorer);
                var list = Descendants(explorer).OfType<TListBox>().Single();
                await bridge.Invoke(() => Descendants(explorer).OfType<TInputLine>().Single().SetData("Alpha"));
                Press(Keys.kbAltA);
                await Until(bridge, () => list.range == 1);
                Press(Keys.kbAltM);
                await Until(bridge, () => project.MarkedTests.SetEquals(["alpha", "beta"]));
                await Capture(bridge, "test-explorer");
                Press(Keys.kbF5);
                var configuration = await Wait(bridge, () => Modal(app, "Configure Test Run"));
                await bridge.Invoke(() => Assert.Equal(2, Descendants(configuration).OfType<TListBox>().Single().range));
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Configure Test Run") is null);
                Press(Keys.kbAltL);
                await Until(bridge, () => project.MarkedTests.Count == 0);
                await bridge.Invoke(() => list.Select());
                Press(Keys.kbSpace);
                await Until(bridge, () => project.MarkedTests.SetEquals(["alpha"]));
                await bridge.Invoke(() => Assert.Equal(1, list.range));

                Press(Keys.kbAltR);
                await Wait(bridge, () => Descendants(app).OfType<TMenuBox>().FirstOrDefault());
                await Capture(bridge, "runs-menu");
                Press('h');
                var runs = await Wait(bridge, () => app.DeskTop!.current as WorkbenchRunExplorer);
                await bridge.Invoke(() =>
                {
                    Assert.False(runs.GetState(Views.sfModal));
                    var entries = Descendants(runs).OfType<TListBox>().Single();
                    Assert.Contains("Historical Alpha", entries.GetText(entries.focused, 160));
                });
                Send(WorkbenchDialog.RunHistory);
                await Settle(bridge);
                await bridge.Invoke(() => Assert.Same(runs, app.DeskTop!.current));
            }, showStart: true);
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Delete(root);
        }
    }
}
