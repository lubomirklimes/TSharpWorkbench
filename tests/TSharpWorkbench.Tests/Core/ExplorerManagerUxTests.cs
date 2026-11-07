using TSharpVision;
using TSharpVision.Constants;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class ExplorerManagerUxTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task ListsKeepTheirActionsAndState(int width, int height)
    {
        string root = Temp();
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.CreateNativeProject(Path.Combine(root, "Explorer.tsw"))));
                var project = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Press(Keys.kbF4);
                var properties = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(() => properties.ExecutableField.SetData(TestWorkbenchHostTests.SampleTarget));
                Press(Keys.kbEnter);
                await Until(bridge, () => Modal(app, "Project Properties") is null);
                await Loaded(app, bridge, 1);

                Send(WorkbenchTestExplorer.Command);
                var explorer = await Wait(bridge, () => project.TestExplorer);
                var list = Assert.Single(Descendants(explorer).OfType<TListBox>());
                await bridge.Invoke(() =>
                {
                    Assert.False(explorer.GetState(Views.sfModal));
                    Assert.Equal(0, list.range);
                    var buttons = Descendants(explorer).OfType<TButton>().ToArray();
                    Assert.DoesNotContain(buttons, b => b.Command == 0x7bb9 || b.Title.Replace("~", "") == "Details");
                    Assert.Contains(buttons, b => b.Title.Replace("~", "") == "Apply filter" && b.origin.y < list.origin.y);
                });
                await Capture(bridge, "test-explorer-empty");

                Press(Keys.kbAlt2);
                var manager = await Wait(bridge, () => project.ScenariosWindow);
                await bridge.Invoke(() =>
                {
                    Assert.False(manager.GetState(Views.sfModal));
                    Assert.Single(Descendants(manager).OfType<TListBox>());
                    var compiled = Assert.IsType<TSharpWorkbench.Testing.ScenarioDescriptor>(manager.Selected!.Info.Compiled);
                    Assert.StartsWith(compiled.Name, manager.Entries.GetText(manager.Entries.focused, width));
                });
                await Capture(bridge, "scenario-manager-ready");
                Press(Keys.kbEnter);
                await Wait(bridge, () => Modal(app, "Compiled Scenario"));
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Compiled Scenario") is null);

                string alphaPath = "";
                string alphaContents = ScenarioDocument.New("alpha-scenario", "Human-readable alpha scenario", "Alpha description", "First").SerializeText();
                await bridge.Invoke(() =>
                {
                    var native = project.Native!;
                    alphaPath = native.CreateScenario(alphaContents);
                    string betaPath = native.CreateScenario(ScenarioDocument.New("beta-scenario", "Human-readable beta scenario", "Beta description", "Second").SerializeText());
                    project.SetScenarioFiles([alphaPath, betaPath], "Scenarios added.", alphaPath);
                    string environment = Assert.Single(native.Session.Environments).Id;
                    native.Session.PutTest(new("alpha", "Alpha test", "", "", "alpha-scenario", [environment]));
                    native.Session.PutTest(new("beta", "Beta test", "", "", "beta-scenario", [environment]));
                    string row = manager.Entries.GetText(manager.Entries.focused, width);
                    Assert.StartsWith("Human-readable alpha scenario", row);
                    Assert.Contains("alpha-scenario", row);
                    Assert.DoesNotContain(Path.GetFileName(alphaPath), row);
                });

                Send(WorkbenchTestExplorer.Command);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, explorer) && list.range == 2);
                var query = Assert.Single(Descendants(explorer).OfType<TInputLine>());
                var combos = Descendants(explorer).OfType<TComboBox>().OrderBy(c => c.origin.x).ToArray();
                async Task Click(int x, int row, bool twice = false)
                {
                    await bridge.Invoke(() =>
                    {
                        var down = new TEvent { What = Events.evMouseDown };
                        down.mouse.where = list.MakeGlobal(new TPoint(x, row));
                        down.mouse.buttons = (byte)Events.mbLeftButton;
                        down.mouse.doubleClick = twice;
                        var up = down; up.What = Events.evMouseUp; up.mouse.buttons = 0;
                        TEventQueue.Enqueue(down); TEventQueue.Enqueue(up);
                    });
                    await Settle(bridge);
                }
                await bridge.Invoke(() => Assert.Equal(query.origin.x + query.size.x, combos[1].origin.x + combos[1].size.x));
                // Text clicks focus a row; only the three checkbox cells toggle selection.
                await Click(12, 1);
                await bridge.Invoke(() => { Assert.Equal(1, list.focused); Assert.Empty(project.MarkedTests); });
                await Click(1, 1);
                await Until(bridge, () => project.MarkedTests.SetEquals(["beta"]));
                await Click(3, 1);
                await Until(bridge, () => project.MarkedTests.Count == 0);
                await Click(2, 1);
                await Click(2, 1, true);
                await bridge.Invoke(() => { Assert.True(project.MarkedTests.SetEquals(["beta"])); Assert.Null(Modal(app, "Edit Test")); });
                await Click(2, 1);
                await Until(bridge, () => project.MarkedTests.Count == 0);
                // A double-click opens the clicked test even when another row was focused.
                await Click(12, 0, true);
                var mouseDetail = await Wait(bridge, () => Modal(app, "Edit Test"));
                await bridge.Invoke(() => Assert.Equal("Alpha test", Assert.Single(Descendants(mouseDetail).OfType<TInputLine>()).Data));
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Edit Test") is null);
                await bridge.Invoke(() =>
                {
                    query.SetData("Alpha");
                    combos[0].Text = "First";
                    combos[1].Text = "White-Box";
                    Assert.Equal(2, list.range);
                });
                Press(Keys.kbAltA);
                await Until(bridge, () => list.range == 0);
                await bridge.Invoke(() => combos[1].Text = "Black-Box");
                Press(Keys.kbAltA);
                await Until(bridge, () => list.range == 1);
                await bridge.Invoke(() =>
                {
                    var header = Rows().Single(row => row.Contains("Selected") && row.Contains("Environments"));
                    var data = Rows().Single(row => row.Contains("Alpha test") && row.Contains("Black-Box"));
                    Assert.Equal(header.IndexOf("Test", StringComparison.Ordinal), data.IndexOf("Alpha test", StringComparison.Ordinal));
                    Assert.Equal(header.IndexOf("Mode", StringComparison.Ordinal), data.IndexOf("Black-Box", StringComparison.Ordinal));
                });
                await bridge.Invoke(() => list.Select());
                Press(Keys.kbSpace);
                await Until(bridge, () => project.MarkedTests.SetEquals(["alpha"]));
                Press(Keys.kbAltQ);
                await Until(bridge, () => ReferenceEquals(explorer.current, query));
                await Capture(bridge, "test-explorer-populated-filter");
                Press(Keys.kbAltE);
                var editor = await Wait(bridge, () => Modal(app, "Edit Test"));
                await bridge.Invoke(() => Descendants(editor).OfType<TInputLine>().First().SetData("Discard me"));
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Edit Test") is null);
                await bridge.Invoke(() =>
                {
                    Assert.Equal("Alpha test", project.Native!.Session.Tests.Single(t => t.Id == "alpha").Title);
                    Assert.Equal("Alpha", query.Data);
                    Assert.Equal("First", combos[0].Text);
                    Assert.Equal("Black-Box", combos[1].Text);
                    Assert.Contains("Alpha test", list.GetText(list.focused, width));
                    Assert.Equal(0, list.topItem);
                    Assert.True(project.MarkedTests.SetEquals(["alpha"]));
                });

                Press(Keys.kbAlt2);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, manager));
                await bridge.Invoke(() => File.WriteAllText(alphaPath, "{"));
                Press(Keys.kbAltV);
                await Until(bridge, () => app.Notifications.History.Any(n => n.Message.StartsWith("Validation:")));
                await bridge.Invoke(() =>
                {
                    Assert.Equal("JSON error", manager.Selected!.Status);
                    Assert.StartsWith("alpha-scenario", manager.Entries.GetText(manager.Entries.focused, width));
                    Assert.Contains(Path.GetFileName(manager.Selected.Path), app.Notifications.History.Last(n => n.Message.StartsWith("Validation:")).Details);
                });
                Assert.Equal("{", File.ReadAllText(alphaPath));
                await Capture(bridge, "scenario-manager-problem");
                int validationNotices = 0;
                await bridge.Invoke(() => validationNotices = app.Notifications.History.Count(n => n.Message.StartsWith("Validation:")));
                Press(Keys.kbF2);
                await Settle(bridge);
                await Until(bridge, () => !project.Busy);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(validationNotices, app.Notifications.History.Count(n => n.Message.StartsWith("Validation:")));
                    File.WriteAllText(alphaPath, alphaContents);
                });
                Press(Keys.kbF2);
                await Until(bridge, () => !project.Busy && manager.Items.Any(item => item.Info.Id == "alpha-scenario"));
                Send(WorkbenchTestExplorer.Command);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, explorer));
                await bridge.Invoke(() =>
                {
                    Assert.Equal("Alpha", query.Data);
                    Assert.Contains("Alpha test", list.GetText(list.focused, width));
                    Assert.True(project.MarkedTests.SetEquals(["alpha"]));
                });
                await bridge.Invoke(() =>
                {
                    manager.Select();
                    int index = Array.FindIndex(manager.Items, item => item.Info.Id == "alpha-scenario");
                    Assert.True(index >= 0);
                    manager.Entries.FocusItemNum(index + 1);
                    var down = new TEvent { What = Events.evMouseDown };
                    down.mouse.where = manager.Entries.MakeGlobal(new(12, index - manager.Entries.topItem));
                    down.mouse.buttons = (byte)Events.mbLeftButton; down.mouse.doubleClick = true;
                    var up = down; up.What = Events.evMouseUp; up.mouse.buttons = 0;
                    TEventQueue.Enqueue(down); TEventQueue.Enqueue(up);
                });
                var scenarioEditor = await Wait(bridge, () => project.ScenarioEditors.FirstOrDefault(e => e.Document.Id == "alpha-scenario"));
                await bridge.Invoke(() => { Assert.Single(project.ScenarioEditors); scenarioEditor.CloseApproved(); });
            }, showStart: true);
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Delete(root);
        }
    }
}
