using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class ScenarioMetadataUxTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task DescriptionAndMetadataKeepTheirExactValues(int width, int height)
    {
        string root = Temp();
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Loaded(app, bridge, 1);
                string path = Path.Combine(root, "multiline.scenario.json");
                var document = ScenarioDocument.New("metadata/multiline", "Metadata text", LongDescription, "smoke");
                Assert.True(document.Save(path).Saved);
                var editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
                string[] steps = document.Steps.Select(s => s.ToString()).ToArray();
                byte[] original = document.Serialize();
                Press(Keys.kbAltM);
                var meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                await bridge.Invoke(() =>
                {
                    Assert.Equal(LongDescription, Text(meta.DescriptionField));
                    Assert.Equal(3, meta.DescriptionField.size.y);
                    int left = app.DeskTop!.origin.x + meta.origin.x, top = app.DeskTop.origin.y + meta.origin.y;
                    foreach (int y in new[] { 1, 3, 5, 10, 12, 18, 20, 22, 24 })
                        Assert.True(string.IsNullOrWhiteSpace(Rows()[top + y][(left + 1)..(left + meta.size.x - 1)]));
                    Assert.Equal(94, meta.size.x); Assert.Equal(32, meta.size.y);
                    Assert.Equal((app.DeskTop.size.x - meta.size.x) / 2, meta.origin.x);
                    Assert.Equal((app.DeskTop.size.y - meta.size.y) / 2, meta.origin.y);
                    var controls = Descendants(meta).Where(v => v is TInputLine or TMemo or TCluster or TListBox or TButton).ToArray();
                    foreach (var view in controls)
                    {
                        Assert.True(view.origin.x >= 1 && view.origin.y >= 2 && view.origin.x + view.size.x <= meta.size.x - 1 && view.origin.y + view.size.y <= meta.size.y - 1);
                        Assert.DoesNotContain(controls, other => other != view
                            && view.origin.x < other.origin.x + other.size.x && other.origin.x < view.origin.x + view.size.x
                            && view.origin.y < other.origin.y + other.size.y && other.origin.y < view.origin.y + view.size.y);
                    }
                    Assert.Equal(meta.GridFields[0].origin.y, meta.GridFields[1].origin.y);
                    Assert.Equal(meta.GridFields[2].origin.y, meta.GridFields[3].origin.y);
                    Assert.True(meta.GridFields[2].origin.y > meta.GridFields[0].origin.y);
                    Assert.DoesNotContain(Rows(), row => row.Contains("Schema tsharpworkbench.scenario version"));
                    Assert.Contains("Cancel", Rows()[^1]);
                    Assert.DoesNotContain("New step", Rows()[^1]);
                });
                await Capture(bridge, "metadata-long-description");
                Press(Keys.kbAltT); await Until(bridge, () => ReferenceEquals(meta.current, meta.TitleField));
                Press(Keys.kbTab); await Until(bridge, () => ReferenceEquals(meta.current, meta.DescriptionField));
                Press(Keys.kbTab); await Until(bridge, () => ReferenceEquals(meta.current, meta.GroupField));
                Press(Keys.kbShiftTab); await Until(bridge, () => ReferenceEquals(meta.current, meta.DescriptionField));
                Press(Keys.kbF1); var help = await Wait(bridge, () => app.HelpWindow);
                await bridge.Invoke(() => Assert.Contains(Rows(), row => row.Contains("Scenario Editor")));
                Press(Keys.kbEsc); await Until(bridge, () => app.HelpWindow is null);
                await bridge.Invoke(() => { meta.DescriptionField.SetSelect(meta.DescriptionField.bufLen, meta.DescriptionField.bufLen, false); meta.DescriptionField.Select(); });
                Press(Keys.kbEnter); Press('x'); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Equal(LongDescription + "\nx", Text(meta.DescriptionField)); Assert.Same(meta, app.DeskTop!.TopView()); });
                await Capture(bridge, "metadata-long-description-scrolled");
                Press(Keys.kbAltC); await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                await bridge.Invoke(() => { Assert.Equal(original, editor.Document.Serialize()); Assert.False(editor.Document.Dirty); });
                await bridge.Invoke(() => Assert.Single(Descendants(editor).OfType<TButton>(), b => b.Command == WorkbenchScenarioEditorWindow.EditMetadata).Press());
                meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                await bridge.Invoke(() => { Assert.Equal(LongDescription, Text(meta.DescriptionField)); SetText(meta.DescriptionField, " \r\n "); });
                Press(Keys.kbAltK); await Until(bridge, () => meta.NoticeText.Contains("Enter a description"));
                await bridge.Invoke(() =>
                {
                    Assert.Equal(original, editor.Document.Serialize());
                    SetText(meta.DescriptionField, LongDescription + "\nx");
                });
                Press(Keys.kbAltK); await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                await bridge.Invoke(() =>
                {
                    var expected = JsonNode.Parse(original)!; expected["description"] = LongDescription + "\nx";
                    Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(editor.Document.Serialize())));
                    Assert.True(editor.Document.Dirty);
                });
                Press(Keys.kbCtrlS); await Wait(bridge, () => Modal(app, "Attach Scenario?"));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Attach Scenario?") is null);
                await bridge.Invoke(() => { Assert.False(editor.Document.Dirty); editor.CloseApproved(); });
                editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
                Press(Keys.kbAltM);
                meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                await bridge.Invoke(() => { Assert.Equal(LongDescription + "\nx", Text(meta.DescriptionField)); Assert.Equal("", meta.FixtureField.Data); });
                foreach (var (key, field) in new[] { (Keys.kbAltI, meta.IdField), (Keys.kbAltT, meta.TitleField), (Keys.kbAltU, meta.GroupField), (Keys.kbAltG, meta.TagsField) })
                { Press(key); await Until(bridge, () => ReferenceEquals(meta.current, field)); }
                Press(Keys.kbAltD); await Until(bridge, () => ReferenceEquals(meta.current, meta.DescriptionField));
                Press(Keys.kbAltM); await Until(bridge, () => ReferenceEquals(meta.current, meta.Kind));
                Press(Keys.kbAltB); await Until(bridge, () => meta.Kind.value == 0);
                Press(Keys.kbAltW); await Until(bridge, () => meta.Kind.value == 1 && !meta.FixtureField.GetState(Views.sfDisabled));
                await bridge.Invoke(() => Assert.Equal("", meta.FixtureField.Data));
                Press(Keys.kbAltK); await Until(bridge, () => meta.NoticeText.Contains("names its fixture"));
                string fixture = "";
                await bridge.Invoke(() =>
                {
                    var picker = Assert.Single(Descendants(meta).OfType<TListBox>());
                    fixture = picker.GetText(picker.focused, 256); picker.Select();
                });
                Press(Keys.kbEnter); await Until(bridge, () => meta.FixtureField.Data == fixture);
                Press(Keys.kbAltF); await Until(bridge, () => ReferenceEquals(meta.current, meta.FixtureField));
                Press(Keys.kbAltE); await Until(bridge, () => meta.current is TRadioButtons radio && !ReferenceEquals(radio, meta.Kind));
                Press(Keys.kbAltS); await Settle(bridge);
                Press(Keys.kbAltA); await Settle(bridge);
                Press(Keys.kbAltR); await Until(bridge, () => meta.GridFields.All(f => !f.GetState(Views.sfDisabled)));
                Press(Keys.kbAltK); await Until(bridge, () => meta.NoticeText.Contains("positive min"));
                await bridge.Invoke(() =>
                {
                    meta.GridFields[0].SetData("80"); meta.GridFields[1].SetData("25");
                    meta.GridFields[2].SetData("160"); meta.GridFields[3].SetData("50");
                    meta.GridFields[4].SetData("80x25, 140x40");
                });
                Press(Keys.kbAltL); await Until(bridge, () => ReferenceEquals(meta.current, meta.GridFields[4]));
                Press(Keys.kbAltK); await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                Press(Keys.kbAltM);
                meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                await bridge.Invoke(() =>
                {
                    Assert.Equal("failed", editor.Document.ExpectedOutcome);
                    Assert.Equal(fixture, meta.FixtureField.Data);
                    var picker = Assert.Single(Descendants(meta).OfType<TListBox>());
                    Assert.Equal(fixture, picker.GetText(picker.focused, 256));
                    Assert.Equal(LongDescription + "\nx", Text(meta.DescriptionField));
                });
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                Press(Keys.kbCtrlS); await Wait(bridge, () => Modal(app, "Attach Scenario?"));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Attach Scenario?") is null);
                await bridge.Invoke(() => { Assert.False(editor.Document.Dirty); editor.CloseApproved(); });
                var saved = ScenarioDocument.Load(path);
                Assert.Equal(LongDescription + "\nx", saved.Description); Assert.Equal(fixture, saved.Fixture);
                Assert.Equal("failed", saved.ExpectedOutcome); Assert.Equal("metadata/multiline", saved.Id);
                Assert.Equal("Metadata text", saved.Title); Assert.Equal("smoke", saved.Group);
                Assert.Equal(steps, saved.Steps.Select(s => s.ToString()));
                Assert.Equal(160, saved.HeaderNode("grid")!["maxWidth"]!.GetValue<int>());

            });
        }
        finally { Delete(root); }
    }

    private static string Text(TMemo memo)
    {
        var data = new TMemoData(memo.bufLen); memo.GetData(data);
        return new string(data.buffer, 0, (int)data.length);
    }
    private static void SetText(TMemo memo, string value) => memo.SetData(new TMemoData((uint)value.Length) { length = (uint)value.Length, buffer = value.ToCharArray() });

    private static string LongDescription => "  First line with leading spaces\r\nSecond line uses CRLF\nThird line uses LF  \r\n"
        + string.Join("\n", Enumerable.Range(4, 20).Select(i => $"Line {i}: inspect the application menu and preserve whitespace.  ")) + "\r\n  Last line  ";
}
