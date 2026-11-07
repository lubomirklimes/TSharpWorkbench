using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class ProjectInformationPopupTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task ProjectInformationIsCompactReadOnlyAndDistinctFromSettings(int width, int height)
    {
        string root = Temp();
        string folder = Path.Combine(root, new string('a', 44), new string('b', 42));
        Directory.CreateDirectory(folder);
        string archive = Path.Combine(folder, "Information.tsw");
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() =>
                {
                    Assert.True(app.CreateNativeProject(archive));
                    Assert.False(app.Project!.GetState(Views.sfVisible));
                });
                var project = await Wait(bridge, () => app.Project);
                var overview = await Wait(bridge, () => app.Overview);
                await bridge.Invoke(() =>
                {
                    Assert.Equal("Project Information", overview.title);
                    Assert.True(overview.GetState(Views.sfModal));
                    Assert.InRange(overview.size.x, 1, 84);
                    Assert.InRange(overview.size.y, 1, 22);
                    Assert.Equal((app.DeskTop!.size.x - overview.size.x) / 2, overview.origin.x);
                    Assert.Equal((app.DeskTop.size.y - overview.size.y) / 2, overview.origin.y);
                    Assert.Equal("~C~lose", Assert.Single(Descendants(overview).OfType<TButton>()).Title);
                    Assert.IsType<TButton>(overview.current);
                    Assert.Empty(Descendants(overview).OfType<TInputLine>());
                    string screen = string.Join("\n", Rows());
                    Assert.Contains("No archived project runs.", screen);
                    Assert.Contains("Information.tsw", screen);
                    Assert.DoesNotContain(archive, screen);
                });
                await Capture(bridge, "project-information-empty");

                Press(Keys.kbCtrlP);
                var paths = await Wait(bridge, () => Modal(app, "Project Paths"));
                await bridge.Invoke(() =>
                {
                    var list = Assert.Single(Descendants(paths).OfType<TListBox>());
                    var rows = Enumerable.Range(0, list.range).Select(i => list.GetText(i, 200));
                    Assert.Contains(archive, string.Concat(rows));
                    Assert.Contains(project.CaptureProject().OutputRoot, string.Concat(rows));
                });
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Project Paths") is null);
                Press(Keys.kbF1);
                await Wait(bridge, () => app.HelpWindow);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.HelpWindow is null);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                await bridge.Invoke(() => Assert.Same(project, app.Project));

                Press(Keys.kbAltG);
                await Wait(bridge, () => Descendants(app).OfType<TMenuBox>().FirstOrDefault());
                Press('p');
                var settings = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(() => Assert.NotEmpty(Descendants(settings).OfType<TInputLine>()));
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Project Properties") is null);
                Press(Keys.kbF4);
                settings = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(() => settings.ExecutableField.SetData(TestWorkbenchHostTests.SampleTarget));
                Press(Keys.kbEnter);
                await Until(bridge, () => Modal(app, "Project Properties") is null);
                await Until(bridge, () => !project.Busy);

                await bridge.Invoke(() =>
                {
                    var native = project.Native!;
                    var local = Assert.Single(native.Session.Environments);
                    native.Session.PutEnvironment(local with { Id = "disabled", Name = "Disabled", Enabled = false });
                    const string scenarioId = "popup-scenario";
                    string path = native.CreateScenario(ScenarioDocument.New(scenarioId, "Popup scenario", "Example").SerializeText());
                    project.SetScenarioFiles([path], "Scenario added.", path);
                    native.Session.PutTest(new("test", "Popup test", "", "", scenarioId, [local.Id]));
                    var entry = new WorkbenchMatrix.Entry("test", "Popup test", scenarioId, local);
                    var now = DateTimeOffset.UtcNow;
                    for (int index = 0; index < 4; index++)
                    {
                        string id = Guid.NewGuid().ToString("N");
                        var began = now.AddMinutes(index - 5);
                        WorkbenchMatrix.Result result = index switch
                        {
                            0 => new(entry, "Aborted", "Old run"),
                            1 => new(entry, "Passed", "Missing reference", Functional: "Passed", Visual: "Missing baseline"),
                            2 => new(entry, "Failed", "Failed check"),
                            _ => new(entry, "Skipped", "Unsupported environment")
                        };
                        var run = new WorkbenchMatrix.Run(id, began, began.AddSeconds(1), [result]);
                        using var data = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(run, ScenarioRunner.Json));
                        native.Session.PutArtifact($"runs/{id}/matrix.json", data);
                    }
                });
                Press(Keys.kbF3);
                overview = await Wait(bridge, () => app.Overview);
                await bridge.Invoke(() =>
                {
                    string screen = string.Join("\n", Rows());
                    Assert.Contains("1 Visual failed", screen);
                    Assert.Contains("1 Failed", screen);
                    Assert.Contains("1 Skipped", screen);
                    Assert.DoesNotContain("1 Aborted", screen);
                    Assert.Contains("2 total  |  1 enabled  |  1 disabled", screen);
                    Assert.Contains("Scenario references", screen);
                    Assert.Contains("Attachment issues", screen);
                    Assert.Contains(Path.GetFileName(TestWorkbenchHostTests.SampleTarget), screen);
                });
                await Capture(bridge, "project-information-populated");
                Press(Keys.kbTab);
                Press(Keys.kbShiftTab);
                await bridge.Invoke(() => Assert.IsType<TButton>(overview.current));
                Press(Keys.kbAltC);
                await Until(bridge, () => app.Overview is null);
                Press(Keys.kbF3);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEnter);
                await Until(bridge, () => app.Overview is null);
                await bridge.Invoke(() => Assert.Same(project, app.Project));
            }, showStart: true);
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Delete(root);
        }
    }
}
