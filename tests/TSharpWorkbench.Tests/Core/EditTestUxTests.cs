using TSharpVision;
using TSharpVision.Constants;
using Xunit;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class EditTestUxTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task MultilineDraftsKeepStableAssignmentsAndRoundTrip(int width, int height)
    {
        string root = Temp();
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.CreateNativeProject(Path.Combine(root, "TestDialog.tsw"))));
                var project = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                await bridge.Invoke(() =>
                {
                    string path = project.Native!.CreateScenario(ScenarioDocument.New("1", "Open application menu", "Real scenario description").SerializeText());
                    project.SetScenarioFiles([path], "Added.", path);
                    project.Native.Session.PutTest(new("test", "Menu test", "First line\r\nSecond line\nThird line", "Start clean\nKeep existing text", "1",
                        [project.Native.Session.Environments[0].Id]));
                });
                Task pending = bridge.Invoke(() => WorkbenchTestExplorer.EditTest(app.DeskTop!, project.Native!.Session, null));
                var create = await Wait(bridge, () => Modal(app, "New Test"));
                await bridge.Invoke(() =>
                {
                    Assert.InRange(create.size.x, 85, 95);
                    Assert.True(create.GetState(Views.sfModal));
                    Assert.Equal(2, Descendants(create).OfType<TMemo>().Count());
                    var titleField = Assert.Single(Descendants(create).OfType<TInputLine>());
                    foreach (var memo in Descendants(create).OfType<TMemo>())
                    {
                        Assert.Equal(titleField.origin.x + titleField.size.x - 1, memo.origin.x + memo.size.x);
                        Assert.Contains(Descendants(create).OfType<TScrollBar>(), bar =>
                            bar.origin.x == memo.origin.x + memo.size.x && bar.origin.y == memo.origin.y && bar.size.x == 1);
                    }
                    Assert.Equal(6, Assert.Single(Descendants(create).OfType<TListBox>()).size.y);
                    int left = app.DeskTop!.origin.x + create.origin.x;
                    int top = app.DeskTop.origin.y + create.origin.y;
                    string blankRow = Rows()[top + 1][(left + 1)..(left + create.size.x - 1)];
                    Assert.True(string.IsNullOrWhiteSpace(blankRow));
                    var buttons = Descendants(create).OfType<TButton>().OrderBy(button => button.origin.x).ToArray();
                    Assert.Equal(["~S~ave", "~C~ancel"], buttons.Select(button => button.Title));
                    Assert.True(buttons[0].origin.x + buttons[0].size.x < buttons[1].origin.x);
                });
                await Capture(bridge, "new-test-empty");
                var newEnvironments = Assert.Single(Descendants(create).OfType<TListBox>());
                await bridge.Invoke(() => Click(newEnvironments, 2, 0)); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains("[X]", newEnvironments.GetText(0, 256)));
                await bridge.Invoke(() => Click(newEnvironments, 2, 0, true)); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains("[X]", newEnvironments.GetText(0, 256)));
                await bridge.Invoke(() => Click(newEnvironments, 12, 0)); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Contains("[ ]", newEnvironments.GetText(0, 256)); Descendants(create).OfType<TInputLine>().Single().Select(); });
                Press(Keys.kbTab);
                await Until(bridge, () => create.current is TMemo);
                Press(Keys.kbShiftTab);
                await Until(bridge, () => create.current is TInputLine);
                Press(Keys.kbAltS);
                await Wait(bridge, () => Modal(app, "Test Needs Assignments"));
                await Capture(bridge, "new-test-validation-error");
                Press(Keys.kbEsc);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), create));
                await Until(bridge, () => create.current is TInputLine);
                Press(Keys.kbF1);
                await Wait(bridge, () => app.HelpWindow);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.HelpWindow is null);
                Press(Keys.kbEsc);
                await pending;
                pending = bridge.Invoke(() => WorkbenchTestExplorer.EditTest(app.DeskTop!, project.Native!.Session, project.Native.Session.Tests.Single()));
                var edit = await Wait(bridge, () => Modal(app, "Edit Test"));
                await bridge.Invoke(() =>
                {
                    var fields = Descendants(edit).OfType<TMemo>().OrderBy(memo => memo.origin.y).ToArray();
                    Assert.Equal("First line\r\nSecond line\nThird line", Text(fields[0]));
                    Assert.Equal("Start clean\nKeep existing text", Text(fields[1]));
                    var selector = Assert.Single(Descendants(edit).OfType<TComboBox>());
                    Assert.Contains("Open application menu", selector.Text);
                    Assert.Contains("[1]", selector.Text);
                });
                await Capture(bridge, "edit-test-populated");
                Press(Keys.kbEsc);
                await pending;
                await bridge.Invoke(() => Assert.Equal("Menu test", project.Native!.Session.Tests.Single().Title));

                await bridge.Invoke(() =>
                {
                    var native = project.Native!;
                    string path = native.CreateScenario(ScenarioDocument.New("0", "Open application menu", "Same title, another ID").SerializeText());
                    project.SetScenarioFiles([.. project.CaptureProject().ScenarioFiles, path], "Added second scenario.", path);
                    native.Session.PutScenario(new("compiled", null, null, "diagnostic-logical"));
                    for (int index = 0; index < 10; index++)
                        native.Session.PutEnvironment(new($"env-{index:00}", $"Similar profile {index:00}", "host", "NullDriver", 80, 25, "none", index != 2));
                });
                ScenarioDescriptor? Metadata(string id) => id == "compiled"
                    ? new("compiled", "Compiled menu example", "Actual descriptor supplied by target catalog", "compiled", ScenarioGrid.Standard, [])
                    : null;
                pending = bridge.Invoke(() => WorkbenchTestExplorer.EditTest(app.DeskTop!, project.Native!.Session,
                    project.Native.Session.Tests.Single(), metadata: Metadata));
                edit = await Wait(bridge, () => Modal(app, "Edit Test"));
                var memos = Descendants(edit).OfType<TMemo>().OrderBy(memo => memo.origin.y).ToArray();
                var scenario = Assert.Single(Descendants(edit).OfType<TComboBox>());
                var environments = Assert.Single(Descendants(edit).OfType<TListBox>());
                await bridge.Invoke(() =>
                {
                    Assert.EndsWith("[1]", scenario.Text);
                    Assert.Equal(2, scenario.Items.Count(label => label.StartsWith("Open application menu")));
                    Assert.Contains(scenario.Items, label => label == "Compiled menu example [compiled]");
                    Assert.Contains("[env-00]", environments.GetText(0, 200));
                    Assert.Contains("(disabled)", environments.GetText(2, 200));
                    SetText(memos[0], "Draft discarded\r\nIncluding lines");
                    scenario.SelectedIndex = 0;
                    environments.Select();
                });
                Press(Keys.kbSpace);
                Press(Keys.kbAltC);
                await pending;
                await bridge.Invoke(() =>
                {
                    var original = project.Native!.Session.Tests.Single();
                    Assert.Equal("1", original.ScenarioId);
                    Assert.Equal("First line\r\nSecond line\nThird line", original.Description);
                    Assert.Single(original.EnvironmentIds);
                });

                pending = bridge.Invoke(() => WorkbenchTestExplorer.EditTest(app.DeskTop!, project.Native!.Session,
                    project.Native.Session.Tests.Single(), metadata: Metadata));
                edit = await Wait(bridge, () => Modal(app, "Edit Test"));
                memos = Descendants(edit).OfType<TMemo>().OrderBy(memo => memo.origin.y).ToArray();
                scenario = Assert.Single(Descendants(edit).OfType<TComboBox>());
                environments = Assert.Single(Descendants(edit).OfType<TListBox>());
                string description = "  Preserve mixed newlines\r\nLine two\n" + string.Join("\n", Enumerable.Range(0, 15).Select(i => $"Long descriptive line {i}")) + "  ";
                string preconditions = " Documentary only\r\nDo not execute\nKeep trailing spaces  ";
                await bridge.Invoke(() =>
                {
                    SetText(memos[0], description);
                    SetText(memos[1], preconditions);
                    memos[0].SetSelect(memos[0].bufLen, memos[0].bufLen, false);
                    memos[0].Select();
                });
                Press(Keys.kbEnter);
                Press('x');
                await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Same(edit, app.DeskTop!.TopView());
                    Assert.Equal(description + "\nx", Text(memos[0]));
                    scenario.SelectedIndex = scenario.Items.ToList().FindIndex(label => label.EndsWith("[0]"));
                    environments.FocusItemNum(0);
                    environments.Select();
                });
                Press(Keys.kbSpace);
                await Settle(bridge);
                await bridge.Invoke(() => { environments.FocusItemNum(9); Click(environments, 2, 9 - environments.topItem); });
                await Settle(bridge);
                await bridge.Invoke(() =>
                {
                    Assert.True(environments.topItem > 0);
                    Assert.Contains(Rows(), row => row.Contains("Environments (3 selected)"));
                    memos[0].Select();
                });
                Press(Keys.kbCtrlHome);
                await Capture(bridge, "edit-test-long-text-many-environments");
                Press(Keys.kbAltS);
                await pending;
                await bridge.Invoke(() =>
                {
                    var saved = project.Native!.Session.Tests.Single();
                    Assert.Equal(description + "\nx", saved.Description);
                    Assert.Equal(preconditions, saved.Preconditions);
                    Assert.Equal("0", saved.ScenarioId);
                    Assert.Equal(new[] { "env-00", "env-09", "local-headless" }, saved.EnvironmentIds.Order().ToArray());
                });

                TestDefinition? missing = null;
                await bridge.Invoke(() => missing = project.Native!.Session.Tests.Single() with { ScenarioId = "removed" });
                pending = bridge.Invoke(() => WorkbenchTestExplorer.EditTest(app.DeskTop!, project.Native!.Session, missing, metadata: Metadata));
                edit = await Wait(bridge, () => Modal(app, "Edit Test"));
                await bridge.Invoke(() => Assert.Equal("removed — Missing scenario", Descendants(edit).OfType<TComboBox>().Single().Text));
                Press(Keys.kbAltS);
                await Wait(bridge, () => Modal(app, "Test Needs Assignments"));
                await Capture(bridge, "edit-test-unavailable-scenario");
                Press(Keys.kbEsc);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), edit));
                Press(Keys.kbEsc);
                await pending;

                await bridge.Invoke(() =>
                {
                    Assert.Equal("0", project.Native!.Session.Tests.Single().ScenarioId);
                    Assert.True(project.SaveProject(false));
                    project.Close();
                    Assert.Null(app.Project);
                });
                using var archive = NativeProject.Open(Path.Combine(root, "TestDialog.tsw"));
                var reopened = archive.Session.Tests.Single();
                Assert.Equal("0", reopened.ScenarioId);
                Assert.Equal(description + "\nx", reopened.Description);
                Assert.Equal(preconditions, reopened.Preconditions);
                Assert.Equal(new[] { "env-00", "env-09", "local-headless" }, reopened.EnvironmentIds.Order().ToArray());
            }, showStart: true);
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Delete(root);
        }
    }

    private static void Click(TView view, int x, int row, bool twice = false)
    {
        var down = new TEvent { What = Events.evMouseDown };
        down.mouse.where = view.MakeGlobal(new(x, row)); down.mouse.buttons = (byte)Events.mbLeftButton; down.mouse.doubleClick = twice;
        var up = down; up.What = Events.evMouseUp; up.mouse.buttons = 0;
        TEventQueue.Enqueue(down); TEventQueue.Enqueue(up);
    }

    private static string Text(TMemo memo)
    {
        var data = new TMemoData(memo.bufLen);
        memo.GetData(data);
        return new string(data.buffer, 0, (int)data.length);
    }

    private static void SetText(TMemo memo, string value) =>
        memo.SetData(new TMemoData((uint)value.Length) { length = (uint)value.Length, buffer = value.ToCharArray() });

}
