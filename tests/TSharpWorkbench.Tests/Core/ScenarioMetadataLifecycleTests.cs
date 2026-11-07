using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class ScenarioMetadataLifecycleTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task EditorMetadataInitializesAndKeepsDraftTransactions(int width, int height)
    {
        string root = Temp();
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Loaded(app, bridge, 1);
                string path = Path.Combine(root, "metadata.scenario.json");
                var initial = ScenarioDocument.New("metadata/menu", "Menu scenario", "Inspect menu content", "smoke", checkpoint: null);
                initial.Tags = ["menu", "regression"];
                initial.Append([ScenarioDocumentStep.Key("kbF10"),
                    ScenarioDocumentStep.FromJson(JsonNode.Parse("""{"kind":"checkpoint","id":"menu","assertions":[{"id":"help","kind":"screen-contains","text":"Help"}]}""")!.AsObject()),
                    ScenarioDocumentStep.Key("kbEsc"), ScenarioDocumentStep.Checkpoint("desktop")]);
                Assert.True(initial.Save(path).Saved);
                var editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
                byte[] original = initial.Serialize();
                string[] steps = initial.Steps.Select(s => s.ToString()).ToArray();

                Press(Keys.kbAltM);
                var meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                await Capture(bridge, "metadata-black-box");
                await bridge.Invoke(() =>
                {
                    Assert.True(meta.GetState(Views.sfModal));
                    Assert.Equal("metadata/menu", meta.IdField.Data);
                    Assert.Equal("Menu scenario", meta.TitleField.Data);
                    Assert.Equal("menu, regression", meta.TagsField.Data);
                    Assert.Equal("Inspect menu content", Text(meta.DescriptionField));
                    Assert.Equal("smoke", meta.GroupField.Data);
                    Assert.Equal(0u, meta.Kind.value);
                    Assert.Equal(0u, Expected(meta).value);
                    Assert.True(meta.FixtureField.GetState(Views.sfDisabled));
                    Assert.All(meta.GridFields, field => Assert.True(field.GetState(Views.sfDisabled)));
                    foreach (var view in Descendants(meta).Where(v => v is TInputLine or TButton or TCluster or TListBox))
                    {
                        Assert.True(view.origin.x >= 1 && view.origin.y >= 1 && view.origin.x + view.size.x <= meta.size.x - 1 && view.origin.y + view.size.y <= meta.size.y - 1);
                    }
                });
                Press(Keys.kbTab); await Until(bridge, () => ReferenceEquals(meta.current, meta.TitleField));
                Press(Keys.kbShiftTab); await Until(bridge, () => ReferenceEquals(meta.current, meta.IdField));
                Press(Keys.kbAltT); await Until(bridge, () => ReferenceEquals(meta.current, meta.TitleField));
                await bridge.Invoke(() => meta.TitleField.SetData("Cancel this title"));
                Press(Keys.kbAltA); await Until(bridge, () => Expected(meta).value == 1);
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                await bridge.Invoke(() => { Assert.Equal(original, editor.Document.Serialize()); Assert.False(editor.Document.Dirty); });

                // Use the existing Metadata button's actual press action for the second route.
                await bridge.Invoke(() => Assert.Single(Descendants(editor).OfType<TButton>(), b => b.Command == WorkbenchScenarioEditorWindow.EditMetadata).Press());
                meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                Press(Keys.kbAltW); await Until(bridge, () => meta.Kind.value == 1 && !meta.FixtureField.GetState(Views.sfDisabled));
                await bridge.Invoke(() =>
                {
                    var picker = Assert.Single(Descendants(meta).OfType<TListBox>());
                    Assert.True(picker.range > 0);
                    picker.Select();
                });
                Press(Keys.kbEnter); await Until(bridge, () => meta.FixtureField.Data.Length > 0 && ReferenceEquals(meta.current, meta.FixtureField));
                Press(Keys.kbAltR); await Until(bridge, () => meta.GridFields.All(field => !field.GetState(Views.sfDisabled)));
                await bridge.Invoke(() =>
                {
                    meta.GridFields[0].SetData("80"); meta.GridFields[1].SetData("25");
                    meta.GridFields[2].SetData("160"); meta.GridFields[3].SetData("50");
                    meta.GridFields[4].SetData("80x25, 140x40");
                });
                await Capture(bridge, "metadata-white-box-explicit-grid");
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                string fixture = "";
                await bridge.Invoke(() =>
                {
                    Assert.False(editor.Document.Proxy); fixture = editor.Document.Fixture!;
                    Assert.True(editor.Document.Dirty);
                    Assert.Equal(steps, editor.Document.Steps.Select(s => s.ToString()));
                    Assert.Equal("metadata/menu", editor.Document.Id);
                    Assert.Null(editor.Document.HeaderNode("backend"));
                });
                Press(Keys.kbAltM);
                meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                await bridge.Invoke(() =>
                {
                    Assert.Equal(1u, meta.Kind.value); Assert.Equal(fixture, meta.FixtureField.Data);
                    Assert.Equal("80", meta.GridFields[0].Data); Assert.Equal("25", meta.GridFields[1].Data);
                    Assert.Equal("160", meta.GridFields[2].Data); Assert.Equal("50", meta.GridFields[3].Data);
                    Assert.All(meta.GridFields, field => Assert.False(field.GetState(Views.sfDisabled)));
                    meta.IdField.SetData(""); meta.TitleField.SetData("Must not commit yet");
                });
                Press(Keys.kbEnter); await Until(bridge, () => meta.NoticeText.Contains("scenario ID"));
                await Capture(bridge, "metadata-invalid");
                await bridge.Invoke(() =>
                {
                    Assert.Equal("metadata/menu", editor.Document.Id); Assert.Equal("Menu scenario", editor.Document.Title);
                    meta.IdField.SetData("metadata/menu"); meta.TitleField.SetData("Accepted title");
                });
                Press(Keys.kbAltB); await Until(bridge, () => meta.Kind.value == 0 && meta.FixtureField.GetState(Views.sfDisabled));
                Press(Keys.kbAltR); await Until(bridge, () => meta.GridFields.All(field => field.GetState(Views.sfDisabled)));
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                await bridge.Invoke(() =>
                {
                    Assert.True(editor.Document.Proxy); Assert.Null(editor.Document.Fixture);
                    Assert.False(editor.Document.HasHeader("grid")); Assert.Equal("Accepted title", editor.Document.Title);
                    Assert.Equal("proxy-logical", editor.Document.HeaderNode("backend")?.ToString());
                    Assert.Equal(steps, editor.Document.Steps.Select(s => s.ToString()));
                });
                await bridge.Invoke(() => app.Project!.SetScenarioFiles([path], "Added scenario.", path));
                await Until(bridge, () => !app.Project!.Busy);
                await bridge.Invoke(() => editor.Select());
                Press(Keys.kbCtrlS); await Until(bridge, () => !editor.Document.Dirty);
                await Until(bridge, () => !app.Project!.Busy);
                await bridge.Invoke(() => editor.Select());
                Press(Keys.kbAltM);
                meta = (WorkbenchScenarioMetadataDialog)await Wait(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title));
                await Capture(bridge, "metadata-reopened");
                byte[] accepted = [];
                await bridge.Invoke(() =>
                {
                    accepted = editor.Document.Serialize();
                    Assert.Equal("Accepted title", meta.TitleField.Data);
                    Assert.Equal("metadata/menu", meta.IdField.Data);
                    Assert.Equal(0u, meta.Kind.value); Assert.Equal(0u, Expected(meta).value);
                    meta.TitleField.SetData("Cancel button must discard this");
                    Assert.Single(Descendants(meta).OfType<TButton>(), b => b.Command == Views.cmCancel).Press();
                });
                await Until(bridge, () => Modal(app, WorkbenchScenarioMetadataDialog.Title) is null);
                await bridge.Invoke(() => { Assert.Equal(accepted, editor.Document.Serialize()); Assert.False(editor.Document.Dirty); });
                await bridge.Invoke(() => { editor.Select(); editor.CloseApproved(); });
                var saved = ScenarioDocument.Load(path);
                Assert.Equal("Accepted title", saved.Title); Assert.Equal(initial.Id, saved.Id);
                Assert.Equal(steps, saved.Steps.Select(s => s.ToString()));
                saved.Title = initial.Title;
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(saved.Serialize())));
            });
        }
        finally { Delete(root); }
    }

    private static string Text(TMemo memo)
    {
        var data = new TMemoData(memo.bufLen); memo.GetData(data);
        return new string(data.buffer, 0, (int)data.length);
    }
    private static TRadioButtons Expected(WorkbenchScenarioMetadataDialog dialog) => Assert.Single(Descendants(dialog).OfType<TRadioButtons>(), radio => !ReferenceEquals(radio, dialog.Kind));
}
