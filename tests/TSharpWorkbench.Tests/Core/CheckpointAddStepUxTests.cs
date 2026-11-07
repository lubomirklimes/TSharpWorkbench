using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class CheckpointAddStepUxTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task CheckpointAndSelector(int width, int height)
    {
        string root = Temp();
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Loaded(app, bridge, 1);
                string path = Path.Combine(root, "checkpoint.scenario.json");
                var doc = ScenarioDocument.New("checkpoint/ux", "Checkpoint UX", "Capture and assert", "smoke", checkpoint: null);
                doc.Append([ScenarioDocumentStep.Checkpoint("empty", ""), ScenarioDocumentStep.FromJson(new JsonObject
                {
                    ["kind"] = "checkpoint", ["id"] = "populated", ["note"] = LongNote,
                    ["evidence"] = new JsonArray("view-tree", "menu", "status"),
                    ["assertions"] = new JsonArray(Enumerable.Range(1, 15).Select(i => (JsonNode)new JsonObject
                    { ["kind"] = "screen-contains", ["id"] = "text-" + i, ["text"] = "Text " + i }).ToArray())
                })]);
                Assert.True(doc.Save(path).Saved);
                var editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
                byte[] original = editor.Document.Serialize();
                async Task<WorkbenchScenarioStepDialog> Open(int index)
                {
                    await Settle(bridge);
                    await bridge.Invoke(() => { editor.Select(); editor.SelectSteps(index); editor.Steps.Select(); });
                    Press(Keys.kbEnter);
                    return (WorkbenchScenarioStepDialog)await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchScenarioStepDialog);
                }
                var dialog = await Open(0);
                await Capture(bridge, "checkpoint-empty");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                dialog = await Open(1);
                await Capture(bridge, "checkpoint-populated");
                await bridge.Invoke(() => dialog.AssertionList!.Select());
                Press(Keys.kbCtrlPgDn); await Until(bridge, () => dialog.AssertionList!.focused == 14 && dialog.AssertionList.topItem > 0);
                await Capture(bridge, "checkpoint-scrolled");
                if (Environment.GetEnvironmentVariable("TSWC1_PHASE") != "before")
                    await bridge.Invoke(() => Assert.Contains(Rows(), row => row.Contains("text-15") && row.Contains("Text 15")));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                await bridge.Invoke(() => { Assert.False(editor.Document.Dirty); Assert.Equal(original, editor.Document.Serialize()); });
                Press(Keys.kbIns);
                var selector = (WorkbenchStepKindDialog)await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchStepKindDialog);
                await Capture(bridge, "add-step");
                await bridge.Invoke(() => Assert.Equal(ScenarioSchemaV1.StepKinds.Where(k => k != "action"), Enumerable.Range(0, selector.Kinds.range).Select(i => selector.Kinds.GetText(i, 256).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                if (Environment.GetEnvironmentVariable("TSWC1_PHASE") == "before") return;
                await VerifyEditing(app, bridge, editor, Open, path, original);
                // Exercise the same modal entry point with a fixture advertising more expectations than fit.
                var fixture = new ScenarioDescriptor("fixture", "Fixture", "Fixture", "smoke", new(80, 25, 160, 50, ["120x36"]), [])
                { Contract = new(FixtureContract.FixtureOnlyRole, 1, "1", Enumerable.Range(1, 12).Select(i => "screen-" + i).ToArray()) };
                var edit = bridge.Invoke(() => WorkbenchScenarioStepDialog.Edit(app.DeskTop!, ScenarioDocumentStep.Checkpoint("screen-1", LongNote), false, fixture, false));
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchScenarioStepDialog);
                await bridge.Invoke(() => Assert.Single(Descendants(dialog).OfType<TListBox>(), b => b != dialog.AssertionList).Select());
                Press(Keys.kbCtrlPgDn); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains(Rows(), r => r.Contains("screen-12")));
                await Capture(bridge, "checkpoint-fixture-expectations");
                Press(Keys.kbEnter); await Until(bridge, () => dialog.IdField!.Data == "screen-12");
                Press(Keys.kbEsc); await edit;
            });
        }
        finally { Delete(root); }
    }

    private static async Task VerifyEditing(WorkbenchApp app, Bridge bridge, WorkbenchScenarioEditorWindow editor,
        Func<int, Task<WorkbenchScenarioStepDialog>> open, string path, byte[] original)
    {
        var dialog = await open(1);
        TMemo memo = null!;
        TInputLine attempts = null!, interval = null!;
        await bridge.Invoke(() =>
        {
            memo = Assert.Single(Descendants(dialog).OfType<TMemo>());
            Assert.Equal(LongNote, Text(memo)); Assert.Equal(3, memo.size.y);
            Assert.InRange(dialog.AssertionList!.size.y, 5, 7);
            Assert.Equal(dialog.AssertionList.origin.x, memo.origin.x);
            Assert.Equal(dialog.IdField!.origin.x + dialog.IdField.size.x, memo.origin.x + memo.size.x + 1);
            var footer = Descendants(dialog).OfType<TButton>().Where(b => b.Command is Views.cmYes or Views.cmOK or Views.cmCancel).ToArray();
            Assert.Equal(3, footer.Length);
            Assert.Single(footer.Select(b => b.origin.y).Distinct());
            Assert.Equal(memo.origin.y + memo.size.y + 2, footer[0].origin.y);
            Assert.Equal(dialog.AssertionList.origin.y + dialog.AssertionList.size.y,
                Descendants(dialog).OfType<TButton>().Single(b => b.Command == WorkbenchScenarioStepDialog.AssertionAdd).origin.y);
            Assert.DoesNotContain(Rows(), row => row.Contains("(any stable screen)"));
            Assert.Equal((app.DeskTop!.size.x - dialog.size.x) / 2, dialog.origin.x);
            Assert.Equal((app.DeskTop.size.y - dialog.size.y) / 2, dialog.origin.y);
            var controls = Descendants(dialog).Where(v => v is TInputLine or TMemo or TCluster or TListBox or TButton or TScrollBar).ToArray();
            foreach (var view in controls)
            {
                Assert.InRange(view.origin.y, 2, dialog.size.y - view.size.y - 1);
                Assert.InRange(view.origin.x, 1, dialog.size.x - view.size.x - 1);
                Assert.DoesNotContain(controls, other => other != view
                    && view.origin.x < other.origin.x + other.size.x && other.origin.x < view.origin.x + view.size.x
                    && view.origin.y < other.origin.y + other.size.y && other.origin.y < view.origin.y + view.size.y);
            }
            var fields = Descendants(dialog).OfType<TInputLine>().OrderBy(v => v.origin.y).ThenBy(v => v.origin.x).ToArray();
            attempts = fields[1]; interval = fields[2];
            Assert.Equal("", attempts.Data); Assert.Equal("", interval.Data);
            Assert.Equal(7u, Assert.Single(Descendants(dialog).OfType<TCheckBoxes>()).value);
            var mnemonics = Descendants(dialog).SelectMany(v => v switch
            {
                TButton b => new[] { b.Title }, TLabel l => new[] { LabelText(l) },
                TCluster c => c.Strings, _ => Enumerable.Empty<string>()
            }).Where(t => t.Contains('~')).Select(t => char.ToUpperInvariant(t[t.IndexOf('~') + 1])).ToArray();
            Assert.Equal(mnemonics.Length, mnemonics.Distinct().Count());
            memo.Select(); memo.SetSelect(memo.bufLen, memo.bufLen, false);
        });
        Press(Keys.kbEnter); Press('x'); await Settle(bridge);
        await bridge.Invoke(() => { Assert.Equal(LongNote + "\nx", Text(memo)); Assert.Same(dialog, app.DeskTop!.TopView()); });
        await Capture(bridge, "checkpoint-note-edited");
        Press(Keys.kbAltV); Press(Keys.kbAltM); Press(Keys.kbAltS);
        await bridge.Invoke(() => { attempts.SetData("0"); interval.SetData("250"); });
        Press(Keys.kbAltK); await Until(bridge, () => dialog.NoticeText.Contains("1..20"));
        await bridge.Invoke(() => { Assert.Equal(LongNote + "\nx", Text(memo)); Assert.Equal(original, editor.Document.Serialize()); attempts.SetData("5"); interval.SetData(""); });
        Press(Keys.kbAltK); await Until(bridge, () => dialog.NoticeText.Contains("both"));
        await Capture(bridge, "checkpoint-invalid-retry");
        await bridge.Invoke(() => { attempts.SetData("bad"); });
        Press(Keys.kbAltK); await Until(bridge, () => dialog.NoticeText.Contains("integers"));
        Press(Keys.kbAltC); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
        await bridge.Invoke(() => { Assert.Equal(original, editor.Document.Serialize()); Assert.False(editor.Document.Dirty); });
        dialog = await open(1);
        Press(Keys.kbAltG);
        await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchStepKindDialog);
        Press(Keys.kbEsc);
        dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchScenarioStepDialog);
        Press(Keys.kbF1); await Wait(bridge, () => app.HelpWindow);
        await bridge.Invoke(() => Assert.Contains(Rows(), r => r.Contains("Scenario Editor")));
        Press(Keys.kbEsc); await Until(bridge, () => app.HelpWindow is null);
        // Edit a nested assertion, then remove another, using actual modal commands.
        await bridge.Invoke(() => { dialog.AssertionList!.FocusItemNum(1); dialog.AssertionList.Select(); });
        Press(Keys.kbEnter);
        var assertion = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchAssertionDialog);
        await bridge.Invoke(() => assertion.Fields["text"].SetData("Changed second"));
        Press(Keys.kbAltK); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), dialog));
        await bridge.Invoke(() => { dialog.AssertionList!.FocusItemNum(3); dialog.AssertionList.Select(); });
        Press(Keys.kbDel); await Settle(bridge);
        Press(Keys.kbAltD); await Wait(bridge, () => Modal(app, "Add Assertion"));
        Press(Keys.kbEnter); assertion = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchAssertionDialog);
        await bridge.Invoke(() => assertion.Fields["text"].SetData("New final"));
        Press(Keys.kbAltK); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), dialog));
        await bridge.Invoke(() =>
        {
            memo = Assert.Single(Descendants(dialog).OfType<TMemo>());
            Assert.Equal(LongNote, Text(memo)); memo.Select(); memo.SetSelect(memo.bufLen, memo.bufLen, false);
            var retry = Descendants(dialog).OfType<TInputLine>().Where(v => v != dialog.IdField).OrderBy(v => v.origin.x).ToArray();
            retry[0].SetData("5"); retry[1].SetData("250");
        });
        Press(Keys.kbEnter); Press('x');
        Press(Keys.kbAltM); // Toggle just menu evidence off.
        Press(Keys.kbAltK); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
        await bridge.Invoke(() =>
        {
            var expected = JsonNode.Parse(original)!;
            var cp = expected["steps"]![1]!;
            cp["note"] = LongNote + "\nx";
            cp["evidence"] = new JsonArray("view-tree", "status");
            cp["retry"] = new JsonObject { ["attempts"] = 5, ["intervalMilliseconds"] = 250 };
            var assertions = cp["assertions"]!.AsArray();
            assertions[1]!["text"] = "Changed second"; assertions.RemoveAt(3);
            var added = editor.Document.Steps[1].Node("assertions")!.AsArray()[^1]!;
            Assert.Equal("New final", added["text"]!.GetValue<string>());
            assertions.Add(added.DeepClone());
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(editor.Document.Serialize())));
            Assert.Null(editor.Document.Steps[0].Node("retry"));
        });
        Press(Keys.kbCtrlS); await Wait(bridge, () => Modal(app, "Attach Scenario?"));
        Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Attach Scenario?") is null);
        await bridge.Invoke(() => { Assert.False(editor.Document.Dirty); editor.CloseApproved(); });
        editor = await Wait(bridge, () => app.Project!.OpenScenarioEditor(path));
        await bridge.Invoke(() => { editor.SelectSteps(1); editor.Steps.Select(); });
        Press(Keys.kbEnter); dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchScenarioStepDialog);
        await bridge.Invoke(() => Assert.Equal(LongNote + "\nx", Text(Assert.Single(Descendants(dialog).OfType<TMemo>()))));
        Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
        // Enter still chooses a step kind and opens the existing New Checkpoint editor.
        await bridge.Invoke(() => editor.Steps.Select());
        Press(Keys.kbIns); var selector = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchStepKindDialog);
        await bridge.Invoke(() =>
        {
            selector.Kinds.FocusItemNum(0);
            int row = Array.IndexOf(ScenarioSchemaV1.StepKinds.Where(k => k != "action").ToArray(), "checkpoint") - selector.Kinds.topItem;
            var click = new TEvent { What = Events.evMouseDown };
            click.mouse.where = selector.Kinds.MakeGlobal(new(12, row)); click.mouse.buttons = (byte)Events.mbLeftButton; click.mouse.doubleClick = true;
            var up = click; up.What = Events.evMouseUp; up.mouse.buttons = 0;
            TEventQueue.Enqueue(click); TEventQueue.Enqueue(up);
        });
        dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchScenarioStepDialog);
        await bridge.Invoke(() => { Assert.StartsWith("New Checkpoint", dialog.title); Assert.Single(Descendants(dialog).OfType<TMemo>()); });
        Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
        await bridge.Invoke(() => { Assert.Equal(2, editor.Document.Steps.Count); Assert.False(editor.Document.Dirty); });
    }

    private static string Text(TMemo memo)
    {
        var data = new TMemoData(memo.bufLen); memo.GetData(data);
        return new string(data.buffer, 0, (int)data.length);
    }

    private static string LabelText(TLabel label) { label.GetText(out string text); return text; }

    private static string LongNote => "  First line\r\nSecond line\nThird line  \r\n" + string.Join("\n", Enumerable.Range(4, 25).Select(i => $"Line {i} with preserved spaces  ")) + "\r\n  End  ";
}

