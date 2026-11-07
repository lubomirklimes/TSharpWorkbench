using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class ScenarioEditorUxTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task MixedStepsKeepTheirOrderAndEditorState(int width, int height)
    {
        string root = Temp();
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Loaded(app, bridge, 1);
                string path = Path.Combine(root, "scenario-892f93a7e56d4dc4b9ce283c7ac0ec2a.json");
                var document = ScenarioDocument.New("menu/about", "Open About dialog", "Check menu and About content", "smoke", checkpoint: null);
                document.Append([
                    ScenarioDocumentStep.Key("kbF10", "Open the application menu"),
                    ScenarioDocumentStep.FromJson(JsonNode.Parse("""{"kind":"checkpoint","id":"menu-open","note":"Menu is visible","assertions":[{"id":"menu-text","kind":"screen-contains","text":"Help"},{"id":"menu-level","kind":"menu-depth","value":1}]}""")!.AsObject()),
                    ScenarioDocumentStep.Key("kbEsc", "Return to the desktop"),
                    ScenarioDocumentStep.Checkpoint("desktop", "Capture only; no assertions"),
                    ScenarioDocumentStep.Text("hello", "A final input action")]);
                Assert.True(document.Save(path).Saved);
                await bridge.Invoke(() => app.Project!.SetScenarioFiles([path], "Added sample.", path));
                await Until(bridge, () => !app.Project!.Busy);
                var editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
                string[] ordered = document.Steps.Select(s => s.ToString()).ToArray();
                await Capture(bridge, "scenario-editor-action-valid");
                await bridge.Invoke(() => editor.SelectSteps(1));
                await Capture(bridge, "scenario-editor-checkpoint");
                await bridge.Invoke(() => { editor.Document.Replace(4, editor.Document.Steps[4].With("note", JsonValue.Create("Changed final action"))); editor.Refresh(); });
                await Capture(bridge, "scenario-editor-dirty");
                await bridge.Invoke(() => { editor.Document.Id = ""; editor.Refresh(); });
                await Capture(bridge, "scenario-editor-invalid");
                if (Environment.GetEnvironmentVariable("TSWC1_PHASE") != "before")
                {
                    byte[] unchanged = File.ReadAllBytes(path);
                    await bridge.Invoke(() => { Assert.True(editor.ProblemList.GetState(Views.sfVisible)); editor.Steps.Select(); });
                    Press(Keys.kbAltV); await Settle(bridge);
                    await bridge.Invoke(() => { Assert.Same(editor.Steps, editor.current); Assert.Equal((1, 1), editor.Selection); Assert.True(editor.Document.Dirty); });
                    Press(Keys.kbCtrlS); await Settle(bridge);
                    Assert.Equal(unchanged, File.ReadAllBytes(path));
                }
                await bridge.Invoke(() => { editor.Document.Id = "menu/about"; editor.Refresh(); editor.CloseApproved(); });
                if (Environment.GetEnvironmentVariable("TSWC1_PHASE") == "before") return;
                Assert.Equal(ordered, ScenarioDocument.Load(path).Steps.Select(s => s.ToString()));
                editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
                await bridge.Invoke(() =>
                {
                    Assert.Same(editor, app.Project!.OpenScenarioEditor(path));
                    Assert.False(editor.GetState(Views.sfModal));
                    Assert.Contains("Open About dialog [menu/about]", editor.title);
                    Assert.StartsWith("Scenario ", editor.title);
                    Assert.DoesNotContain('|', editor.HeaderText);
                    Assert.Equal(3u, Assert.Single(Descendants(editor).OfType<WorkbenchReadOnlyChecks>()).value);
                    Assert.All(Descendants(editor).OfType<WorkbenchReadOnlyField>(), field => Assert.False((field.options & Views.ofSelectable) != 0));
                    Assert.False(editor.ProblemList.GetState(Views.sfVisible));
                    var buttons = Descendants(editor).OfType<TButton>().ToArray();
                    Assert.Equal(RecordCount(), buttons.Length);
                    ushort[] expected = [WorkbenchScenarioEditorWindow.NewStep, WorkbenchScenarioEditorWindow.EditStep,
                        WorkbenchScenarioEditorWindow.DuplicateStep, WorkbenchScenarioEditorWindow.DeleteStep,
                        WorkbenchScenarioEditorWindow.MoveUp, WorkbenchScenarioEditorWindow.MoveDown,
                        WorkbenchScenarioEditorWindow.EditMetadata, WorkbenchScenarioEditorWindow.ValidateDocument,
                        WorkbenchScenarioEditorWindow.SaveDocument, WorkbenchScenarioEditorWindow.SaveDocumentAs,
                        WorkbenchDialog.OpenScenarioExternal, WorkbenchScenarioEditorWindow.CloseEditor];
                    Assert.Equal(expected.Order(), buttons.Where(b => b.Command != WorkbenchScenarioEditorWindow.RecordCommand).Select(b => b.Command).Order());
                    var keys = buttons.Select(b => char.ToUpperInvariant(b.Title[b.Title.IndexOf('~') + 1])).ToArray();
                    Assert.Equal(keys.Length, keys.Distinct().Count());
                    Assert.DoesNotContain(keys, k => "PTRBGWHX".Contains(k));
                    foreach (var b in buttons)
                    {
                        Assert.InRange(b.origin.x, 1, editor.size.x - b.size.x - 1);
                        Assert.InRange(b.origin.y, 2, editor.size.y - b.size.y - 1);
                        Assert.DoesNotContain(buttons, other => other != b && b.origin.y == other.origin.y && b.origin.x < other.origin.x + other.size.x && other.origin.x < b.origin.x + b.size.x);
                        Assert.Equal(b.Command == WorkbenchDialog.OpenScenarioExternal ? 20 : 14, b.size.x);
                    }
                    Assert.Equal(new[] { editor.size.y - 6, editor.size.y - 3 }, buttons.Select(b => b.origin.y).Distinct().Order());
                    int left = app.DeskTop!.origin.x + editor.origin.x, top = app.DeskTop.origin.y + editor.origin.y;
                    Assert.True(string.IsNullOrWhiteSpace(Rows()[top + 1][(left + 1)..(left + editor.size.x - 1)]));
                    Assert.True(editor.Steps.size.x > editor.StepFields.size.x);
                    editor.SelectSteps(1); editor.Steps.Select();
                });
                Press(Keys.kbTab); await Until(bridge, () => ReferenceEquals(editor.current, editor.StepFields));
                Press(Keys.kbShiftTab); await Until(bridge, () => ReferenceEquals(editor.current, editor.Steps));
                await bridge.Invoke(() =>
                {
                    var status = Assert.Single(Descendants(editor).OfType<WorkbenchReadOnlyChecks>());
                    Click(status, 2, 0);
                });
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(3u, Assert.Single(Descendants(editor).OfType<WorkbenchReadOnlyChecks>()).value); Assert.False(editor.Document.Dirty); });
                await bridge.Invoke(() => { editor.SelectSteps(0); Click(editor.Steps, 12, 1 - editor.Steps.topItem, true); });
                await Wait(bridge, () => Modal(app, "Edit Checkpoint (capture + assertions)"));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                foreach (ushort key in new[] { Keys.kbAltI, Keys.kbAltN })
                {
                    Press(key); await Wait(bridge, () => app.DeskTop!.TopView() as TDialog);
                    Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                }
                await bridge.Invoke(() =>
                {
                    Assert.False(editor.Document.Dirty);
                    editor.SelectSteps(1); editor.Steps.Select();
                    var text = ListText(editor.StepFields);
                    Assert.Contains("menu-open", text); Assert.Matches(@"assertions\s+2", text);
                    Assert.DoesNotContain(path, text.Replace("\n", ""));
                    Assert.DoesNotContain(path, editor.HeaderText);
                    editor.SelectSteps(3);
                    Assert.Contains("0 (capture only)", ListText(editor.StepFields));
                    editor.SelectSteps(0);
                });
                Press(Keys.kbCtrlP);
                var info = await Wait(bridge, () => Modal(app, "Scenario Information"));
                await bridge.Invoke(() =>
                {
                    Assert.Contains(path, ListText(Assert.Single(Descendants(info).OfType<TListBox>())).Replace("\n", ""));
                    Assert.False(editor.Document.Dirty);
                });
                await Capture(bridge, "scenario-editor-information");
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Scenario Information") is null);
                Press(Keys.kbAltC); await Until(bridge, () => editor.Document.Steps.Count == 6);
                Press(Keys.kbAltD); await Until(bridge, () => editor.Selection.Start == 2);
                Press(Keys.kbAltU); await Until(bridge, () => editor.Selection.Start == 1);
                Press(Keys.kbAltY); await Until(bridge, () => editor.Document.Steps.Count == 5);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(ordered, editor.Document.Steps.Select(s => s.ToString()));
                    editor.Document.Replace(4, editor.Document.Steps[4].With("note", JsonValue.Create("Saved final action")));
                    editor.Refresh(); editor.SelectSteps(1); editor.StepFields.Select();
                });
                Press(Keys.kbAltV); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(editor.StepFields, editor.current); Assert.Equal((1, 1), editor.Selection); Assert.True(editor.Document.Dirty); });
                Send(WorkbenchDialog.ManageScenarios);
                var manager = await Wait(bridge, () => app.Project!.ScenariosWindow);
                await bridge.Invoke(() => { Assert.True(editor.Document.Dirty); editor.Select(); });
                Press(Keys.kbCtrlS); await Until(bridge, () => !editor.Document.Dirty);
                await bridge.Invoke(() => { editor.Document.Replace(4, editor.Document.Steps[4].With("note", JsonValue.Create("Discard this"))); editor.Refresh(); });
                Press(Keys.kbEsc); await Wait(bridge, () => Modal(app, "Unsaved Scenario"));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                await bridge.Invoke(() => { Assert.True(editor.Document.Dirty); Assert.Contains(editor, app.Project!.ScenarioEditors); });
                Send(Views.cmClose); await Wait(bridge, () => Modal(app, "Unsaved Scenario"));
                Press(Keys.kbAltD); await Until(bridge, () => !app.Project!.ScenarioEditors.Contains(editor));
                var saved = ScenarioDocument.Load(path);
                Assert.Equal(new[] { "key", "checkpoint", "key", "checkpoint", "text" }, saved.Steps.Select(s => s.Kind));
                Assert.Equal(2, saved.Steps[1].AssertionCount);
                Assert.Equal(ordered[1], saved.Steps[1].ToString());
                Assert.Equal("Saved final action", saved.Steps[4].Note);
                editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
                await bridge.Invoke(() =>
                {
                    Assert.False(editor.Document.Dirty);
                    editor.Document.Append(Enumerable.Range(0, 25).Select(i => ScenarioDocumentStep.Text(new string('x', 48), $"Long note {i}\r\nwith a second line and trailing spaces  ")).ToArray());
                    editor.Document.Replace(1, editor.Document.Steps[1].With("note", JsonValue.Create("First line\r\nSecond line with enough content to wrap under the value column.\n  Final line  ")));
                    editor.Document.Id = new string('i', 80); editor.Document.Group = new string('g', 80);
                    editor.Refresh(); editor.SelectSteps(1);
                    var header = Rows()[editor.Steps.MakeGlobal(new(0, 0)).y - 1];
                    var firstRow = Rows()[editor.Steps.MakeGlobal(new(0, 0)).y];
                    Assert.Equal(header.IndexOf("Kind", StringComparison.Ordinal), firstRow.IndexOf("key", StringComparison.Ordinal));
                    Assert.Equal(header.IndexOf("Parameters", StringComparison.Ordinal), firstRow.IndexOf("kbF10", StringComparison.Ordinal));
                    Assert.Contains("Ctrl+P Info", editor.HeaderText);
                    Assert.DoesNotContain(path, ListText(editor.StepFields));
                    for (int i = 0; i < editor.Steps.range; i++) Assert.DoesNotContain('\n', editor.Steps.GetText(i, 2000));
                });
                await Capture(bridge, "scenario-editor-long-values");
                TRect originalBounds = default;
                await bridge.Invoke(() =>
                {
                    originalBounds = editor.GetBounds();
                    editor.Locate(new(originalBounds.a.x, originalBounds.a.y,
                        originalBounds.a.x + WorkbenchScenarioEditorWindow.MinWidth, originalBounds.a.y + WorkbenchScenarioEditorWindow.MinHeight));
                    Assert.True(editor.Steps.size.y >= 10);
                    Assert.Equal(editor.Steps.size.y, editor.StepFields.size.y);
                    Assert.True(editor.StepFields.size.x >= 36);
                    Assert.Equal((1, 1), editor.Selection);
                });
                await Capture(bridge, "scenario-editor-resized");
                await bridge.Invoke(() => editor.Locate(originalBounds));
                await bridge.Invoke(() => { editor.SelectSteps(29); editor.Steps.Select(); });
                await Capture(bridge, "scenario-editor-long-list-scrolled");
                await bridge.Invoke(() => { editor.Document.Remove(0, editor.Document.Steps.Count); editor.Refresh(); });
                await Capture(bridge, "scenario-editor-empty");
                await bridge.Invoke(() => editor.CloseApproved());
                Assert.Equal("Saved final action", ScenarioDocument.Load(path).Steps[4].Note);
            });
        }
        finally { Delete(root); }
    }

    private static int RecordCount() => WorkbenchScenarioEditorWindow.RecordRequested is null ? 12 : 13;
    private static void Click(TView view, int x, int row, bool twice = false)
    {
        var down = new TEvent { What = Events.evMouseDown };
        down.mouse.where = view.MakeGlobal(new(x, row)); down.mouse.buttons = (byte)Events.mbLeftButton; down.mouse.doubleClick = twice;
        var up = down; up.What = Events.evMouseUp; up.mouse.buttons = 0;
        TEventQueue.Enqueue(down); TEventQueue.Enqueue(up);
    }
    private static string ListText(TListBox list) => string.Join("\n", Enumerable.Range(0, list.range).Select(i => list.GetText(i, 2000)));
}
