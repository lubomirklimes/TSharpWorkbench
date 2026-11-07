using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Integration (coordinator): the review model and its materialization into schema-v1 steps (inputs-only identity,
// safe splits, ID collisions, validation by the runtime parser), the Smart capture setting precedence, and the Record
// Interaction window's Review / Append / Replace path with Smart capture (logical NullDriver screenshots when TSWB_RECORDER_INTEGRATION_EVIDENCE
// is set; NOT native).
[Collection("NonParallel")]
public sealed class RecorderIntegrationTests
{
    private static string? Evidence => Environment.GetEnvironmentVariable("TSWB_RECORDER_INTEGRATION_EVIDENCE") is { Length: > 0 } e ? e : null;

    private static RecordedInput KeyAt(long sequence, ushort code, string? text = null) =>
        new(sequence, "backend-key", sequence * 100, "key", Events.evKeyDown, code, text, 0, 0, 0, 0, 0, 80, 25);

    // "abcd" then Enter: converter steps text "abcd" [1..4], key kbEnter [5..5]. The plan splits the text run after "ab".
    private static readonly RecordedInput[] Typed =
        [KeyAt(1, 0x1e61, "a"), KeyAt(2, 0x3062, "b"), KeyAt(3, 0x2e63, "c"), KeyAt(4, 0x2064, "d"), KeyAt(5, Keys.kbEnter)];

    private static CheckpointProposal Proposal(string id, int after, string placement, long boundary, long sequence, string kind = "content") =>
        new("p-" + boundary, id, after, placement, placement == Placements.Unrepresentable ? null : ScenarioDocumentStep.Checkpoint(id),
            kind, "state after input " + sequence, Confidences.High, EvidenceLevels.HashOnly, sequence, boundary, null, ["c-" + boundary], [],
            placement == Placements.Unrepresentable, []);

    private static AssertionSuggestion Suggestion(string proposal, AssertionSpec spec, string status = SuggestionStatuses.Valid, bool selected = true) =>
        new(proposal, spec with { Severity = AssertionSeverity.Warning }, SuggestionOrigins.State, null, "modal-open", "A dialog was executing.",
            Confidences.High, status, selected && status == SuggestionStatuses.Valid, status == SuggestionStatuses.Valid ? AssertionOutcomes.Pass : AssertionOutcomes.Fail,
            status == SuggestionStatuses.Valid ? null : "The evaluator did not pass on the captured evidence.", []);

    private static RecordingReviewModel Model(out RecordingConversion conversion)
    {
        conversion = RecordingConverter.Convert(Typed, 80, 25);
        Assert.Equal(new[] { "text", "key" }, conversion.Steps.Select(s => s.Step.Kind));
        var steps = new List<RecordedStep>
        {
            new(ScenarioDocumentStep.Text("ab"), 1, 2), new(ScenarioDocumentStep.Text("cd"), 3, 4), conversion.Steps[1],
        };
        var split = Proposal("rec-content-1", 0, Placements.Split, 2, 2);
        var modal = Proposal("rec-modal-open-2", 2, Placements.AfterStep, 5, 5, "modal-open");
        var lost = Proposal("rec-content-3", 1, Placements.Unrepresentable, 7, 3);
        var plan = new CheckpointPlan(steps, true, [split, modal, lost], []);
        var analysis = new SmartRecordingAnalysis(new DetectionResult([], [], new DetectorCounters(0, 0, 0, 0, 0, 0, 0, 0, 0)), [], plan, []);
        CheckpointEvidence None(CheckpointProposal p) => new(p.Id, p.BoundaryId, 1, p.AfterSequence, EvidenceSources.None, null, null, null, false, []);
        var suggestions = new[]
        {
            new CheckpointSuggestions(split, None(split), [], true, "capture only", 80, 25),
            new CheckpointSuggestions(modal, None(modal),
            [
                Suggestion(modal.Id, UiAssert.Modal("dialog-modal", new ViewSelector(Type: "TDialog"))),
                Suggestion(modal.Id, UiAssert.Focused("ok-focused", new ViewSelector(Type: "TButton", Text: "OK")), SuggestionStatuses.Unsupported),
            ], false, null, 80, 25),
            new CheckpointSuggestions(lost, None(lost), [], true, "cannot be placed", 80, 25),
        };
        return new RecordingReviewModel(analysis, suggestions, conversion, ["Console window: test."]);
    }

    private static ReviewCheckpoint Cp(RecordingReviewModel m, string id) => m.Checkpoints.Single(c => c.Proposal.CheckpointId == id);
    private static string[] Json(IEnumerable<ScenarioDocumentStep> steps) => steps.Select(s => s.ToString()).ToArray();

    // ── materialization ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NothingAcceptedInsertsExactlyTheConverterSteps()
    {
        var model = Model(out var conversion);
        // Decision 2: the high-confidence modal proposal with a high-confidence valid assertion is preselected.
        Assert.True(Cp(model, "rec-modal-open-2").Preselected && Cp(model, "rec-modal-open-2").Accepted);
        Assert.Equal(1, model.AcceptedCount);
        model.RejectAll();
        Assert.False(model.AnyAccepted);
        var m = RecordingReviewMaterializer.Materialize(model, ["initial"]);
        Assert.True(m.Ok);
        Assert.Equal(Json(conversion.DocumentSteps), Json(m.Steps));
        Assert.Equal(0, m.Checkpoints);
        // inputsOnly ignores every decision.
        Assert.Null(model.SetAccepted(Cp(model, "rec-modal-open-2"), true));
        Assert.Equal(Json(conversion.DocumentSteps), Json(RecordingReviewMaterializer.Materialize(model, [], inputsOnly: true).Steps));
    }

    [Fact]
    public void AcceptedSplitProposalSplitsOnlyItsStepAndReplaysIdentically()
    {
        var model = Model(out _);
        model.RejectAll();   // Drop the preselected modal proposal; only the split proposal is under test
        Assert.Null(model.SetAccepted(Cp(model, "rec-content-1"), true));
        var m = RecordingReviewMaterializer.Materialize(model, []);
        Assert.True(m.Ok, string.Join("; ", m.Problems));
        Assert.Equal(new[] { "text:ab", "checkpoint:rec-content-1", "text:cd", "key:kbEnter" }, m.Steps.Select(s => s.Kind + ":" + s.Name));
        var inputs = m.Steps.Where(s => s.Kind != "checkpoint").ToArray();
        Assert.Empty(RecordingConverter.SelfCheck(inputs, Typed, 80, 25));
    }

    [Fact]
    public void AcceptedAfterStepProposalKeepsTheCoalescedStepWholeWithSelectedAssertionsOnly()
    {
        var model = Model(out _);
        Assert.Null(model.SetAccepted(Cp(model, "rec-modal-open-2"), true));
        var m = RecordingReviewMaterializer.Materialize(model, []);
        Assert.True(m.Ok);
        Assert.Equal(new[] { "text:abcd", "key:kbEnter", "checkpoint:rec-modal-open-2" }, m.Steps.Select(s => s.Kind + ":" + s.Name));
        var assertions = (JsonArray)m.Steps[2].Node("assertions")!;
        var only = Assert.Single(assertions);
        Assert.Equal("dialog-modal", only!["id"]!.GetValue<string>());
        Assert.Equal("warning", only["severity"]!.GetValue<string>());
        Assert.Equal(1, m.Assertions);
        // Deselecting leaves a capture-only checkpoint.
        Assert.Null(RecordingReviewModel.SetSelected(Cp(model, "rec-modal-open-2").Assertions[0], false));
        Assert.False(RecordingReviewMaterializer.Materialize(model, []).Steps[2].Has("assertions"));
    }

    [Fact]
    public void UnrepresentableProposalsAndUnsupportedAssertionsCannotBeAccepted()
    {
        var model = Model(out _);
        var lost = Cp(model, "rec-content-3");
        Assert.False(lost.Representable);
        Assert.Contains("cannot be inserted", model.SetAccepted(lost, true));
        Assert.False(lost.Accepted);
        model.AcceptAll();
        Assert.False(lost.Accepted);
        Assert.Equal(2, model.AcceptedCount);
        var unsupported = Cp(model, "rec-modal-open-2").Assertions[1];
        Assert.False(unsupported.Selected);
        Assert.Contains("cannot be added", RecordingReviewModel.SetSelected(unsupported, true));
        Assert.False(unsupported.Selected);
    }

    [Fact]
    public void CheckpointIdCollisionsAreRenamedOrRefusedNeverDuplicated()
    {
        var model = Model(out _);
        model.AcceptAll();
        var m = RecordingReviewMaterializer.Materialize(model, ["initial", "rec-modal-open-2"]);
        Assert.True(m.Ok);
        Assert.Contains("rec-modal-open-2 -> rec-modal-open-2-2", m.Renamed);
        var ids = m.Steps.Where(s => s.Kind == "checkpoint").Select(s => s.Name).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("rec-modal-open-2", ids);

        // An ID the user chose is never renamed silently.
        Assert.Null(model.Rename(Cp(model, "rec-content-1"), "initial"));
        var refused = RecordingReviewMaterializer.Materialize(model, ["initial"]);
        Assert.False(refused.Ok);
        Assert.Empty(refused.Steps);
        Assert.Contains("already exists", refused.Problems[0]);
        Assert.NotNull(model.Rename(Cp(model, "rec-modal-open-2"), "initial"));     // unique within the review
        Assert.NotNull(model.Rename(Cp(model, "rec-modal-open-2"), "Bad Name"));
    }

    [Fact]
    public void SaveAndRestoreUndoEveryDecision()
    {
        var model = Model(out _);
        var saved = model.Save();
        model.AcceptAll();
        Assert.Null(model.Rename(Cp(model, "rec-content-1"), "typed-ab"));
        Assert.Null(RecordingReviewModel.SetSelected(Cp(model, "rec-modal-open-2").Assertions[0], false));
        model.Restore(saved);
        // Back to the preselection (only the high-confidence modal proposal accepted).
        Assert.False(Cp(model, "rec-content-1").Accepted);
        Assert.True(Cp(model, "rec-modal-open-2").Accepted);
        Assert.Equal("rec-content-1", model.Checkpoints[0].CheckpointId);
        Assert.False(model.Checkpoints[0].IdEdited);
        Assert.True(Cp(model, "rec-modal-open-2").Assertions[0].Selected);
    }

    [Fact]
    public void EditedAssertionsAreCheckedByTheRuntimeParser()
    {
        var model = Model(out _);
        var cp = Cp(model, "rec-modal-open-2");
        var broken = new JsonObject { ["id"] = "ok-visible", ["kind"] = "view-visible", ["severity"] = "warning" }; // no view
        Assert.Contains("schema v1", RecordingReviewModel.EditAssertion(cp, cp.Assertions[1], broken));
        var fixedJson = new JsonObject { ["id"] = "ok-visible", ["kind"] = "view-visible", ["severity"] = "error", ["view"] = new JsonObject { ["type"] = "TButton", ["text"] = "OK" } };
        Assert.Null(RecordingReviewModel.EditAssertion(cp, cp.Assertions[1], fixedJson));
        Assert.True(cp.Assertions[1].Selected);
        Assert.Null(model.SetAccepted(cp, true));
        var step = RecordingReviewMaterializer.Materialize(model, []).Steps.Single(s => s.Kind == "checkpoint");
        Assert.Equal(2, step.AssertionCount);
    }

    [Fact]
    public void MaterializedStepsSaveAsAValidV1DocumentTheRuntimeLoads()
    {
        var model = Model(out _);
        model.AcceptAll();
        var m = RecordingReviewMaterializer.Materialize(model, ["initial"]);
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            var doc = ScenarioDocument.New("recorded/review", "Review", "Materialized review.", checkpoint: "initial");
            doc.Append(m.Steps);
            Assert.DoesNotContain(doc.Validate(), p => p.Severity == ScenarioProblemSeverity.Error);
            var saved = doc.Save(Path.Combine(root, "review.scenario.json"));
            Assert.True(saved.Saved, saved.Message);
            var loaded = ExternalScenarios.Load(saved.Path!);
            var checkpoint = loaded.Scenario.Steps.Single(s => s.Name == "rec-modal-open-2");
            var spec = Assert.Single(checkpoint.Assertions!);
            Assert.Equal((AssertionKinds.Modal, AssertionSeverity.Warning, "TDialog"), (spec.Kind, spec.Severity, spec.View!.Type));
            Assert.Contains("\"version\": 1", File.ReadAllText(saved.Path!));
            // Review metadata never reaches the document: only v1 assertion fields.
            Assert.DoesNotContain("rationale", File.ReadAllText(saved.Path!), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("confidence", File.ReadAllText(saved.Path!), StringComparison.OrdinalIgnoreCase);
            if (Evidence is { } evidence)
            {
                Directory.CreateDirectory(Path.Combine(evidence, "docs"));
                File.Copy(saved.Path!, Path.Combine(evidence, "docs", "review-materialized.scenario.json"), true);
            }
        }
        finally { Delete(root); }
    }

    // ── Smart capture setting ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, null, true)]       // Decision 1: missing setting = ON
    [InlineData(null, null, true, true)]
    [InlineData(null, "0", true, false)]
    [InlineData(null, "1", false, true)]
    [InlineData(null, "yes", true, true)]      // unknown env values are ignored
    [InlineData(false, "1", true, false)]      // the test seam wins
    [InlineData(true, "0", null, true)]
    public void SmartCapturePrecedenceIsSeamThenEnvironmentThenSetting(bool? seam, string? env, bool? setting, bool expected) =>
        Assert.Equal(expected, WorkbenchRecorder.ResolveSmartCapture(seam, env, setting));

    [Fact]
    public void SmartCaptureSettingPersistsInTheUserSettingsFile()
    {
        string root = Temp(), previous = WorkbenchRecorder.PreferencesPath ?? "";
        try
        {
            Directory.CreateDirectory(root);
            WorkbenchRecorder.PreferencesPath = Path.Combine(root, "settings.json");
            Assert.Null(WorkbenchPreferences.Load(WorkbenchRecorder.PreferencesPath).SmartRecorder);   // missing = ON
            Assert.True(WorkbenchRecorder.SaveSmartCapture(true));
            Assert.True(WorkbenchPreferences.Load(WorkbenchRecorder.PreferencesPath).SmartRecorder);
            Assert.True(WorkbenchRecorder.SaveSmartCapture(false));
            Assert.False(WorkbenchPreferences.Load(WorkbenchRecorder.PreferencesPath).SmartRecorder);
        }
        finally { WorkbenchRecorder.PreferencesPath = previous.Length == 0 ? null : previous; Delete(root); }
    }

    // ── window: Smart capture check box, Review, Append with accepted proposals, Replace confirmation ────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task RecordingWindowInsertsAcceptedProposalsAndKeepsInputsOnlyByDefault(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);

                // Decision 1: no override, settings file without the field = Smart capture ON (Pick + Review buttons);
                // the check box turns it off and the saved OFF stays OFF.
                WorkbenchRecorder.SmartCaptureOverride = null;
                WorkbenchRecordingWindow.Chooser = (_, _, _, _) => 1;   // Decision 4: proceed with accepted only
                if (WorkbenchRecorder.SmartCaptureForced is null)
                {
                    await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                    var off = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                    await bridge.Invoke(() =>
                    {
                        Assert.Null(WorkbenchPreferences.Load(Path.Combine(root, "settings.json")).SmartRecorder);
                        Assert.True(off.SmartCaptureOn);
                        Assert.Equal(11, Buttons(off).Count);
                        off.SetSmart(false, persist: true);               // the check box: Pick + Review disappear, setting saved
                        Assert.Equal(9, Buttons(off).Count);
                        Assert.False(WorkbenchPreferences.Load(Path.Combine(root, "settings.json")).SmartRecorder);
                        off.SetSmart(true, persist: true);
                        Assert.Equal(11, Buttons(off).Count);
                        Assert.True(WorkbenchPreferences.Load(Path.Combine(root, "settings.json")).SmartRecorder);
                        off.SetSmart(false, persist: true);
                        Assert.Equal(9, Buttons(off).Count);
                    });
                    await Shot(bridge, "smart-off-ready");
                    await bridge.Invoke(() => off.Execute(WorkbenchRecordingWindow.DiscardCommand));
                    await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                }

                // Smart ON, stopped recording with a modal state: proposals exist, nothing is accepted.
                WorkbenchRecorder.SmartCaptureOverride = true;
                var window = await OpenStopped(bridge, editor);
                var review = await Invoke(bridge, () => window.Review!);
                Assert.NotEmpty(review.Checkpoints);
                Assert.False(review.AnyAccepted);
                string text = await Text(bridge, window);
                Assert.Contains("Review:", text);
                Assert.Contains("Console window: Smart capture and picking are qualified", text);
                await bridge.Invoke(() => Assert.True(TView.CommandEnabled(WorkbenchRecordingWindow.ReviewCommand)));
                await Shot(bridge, "smart-on-stopped");

                // Append with nothing accepted = the legacy input-only insertion.
                int before = await Invoke(bridge, () => editor.Document.Steps.Count);
                var legacy = await Invoke(bridge, () => window.Conversion!.DocumentSteps.Select(s => s.ToString()).ToArray());
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.AppendCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Equal(legacy, await Invoke(bridge, () => editor.Document.Steps.Skip(before).Select(s => s.ToString()).ToArray()));

                // Accept the first representable proposal: Append inserts it at its planned place.
                window = await OpenStopped(bridge, editor);
                review = await Invoke(bridge, () => window.Review!);
                var accepted = review.Checkpoints.First(c => c.Representable);
                Assert.Null(review.SetAccepted(accepted, true));
                await Shot(bridge, "smart-on-accepted");
                before = await Invoke(bridge, () => editor.Document.Steps.Count);
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.AppendCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                var inserted = await Invoke(bridge, () => editor.Document.Steps.Skip(before).ToArray());
                var checkpoints = inserted.Where(s => s.Kind == "checkpoint").ToArray();
                Assert.Single(checkpoints);
                Assert.Equal(legacy, inserted.Where(s => s.Kind != "checkpoint").Select(s => s.ToString()).ToArray());
                Assert.Equal(accepted.Proposal.InsertAfterStep + 1, Array.IndexOf(inserted, checkpoints[0]));

                // Discard after accepting everything inserts nothing and leaves the document as it was.
                window = await OpenStopped(bridge, editor);
                var unchanged = await Invoke(bridge, () => editor.Document.Steps.Select(s => s.ToString()).ToArray());
                int revision = await Invoke(bridge, () => editor.Document.Revision);
                await bridge.Invoke(() => { window.Review!.AcceptAll(); window.Execute(WorkbenchRecordingWindow.DiscardCommand); });
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Equal(unchanged, await Invoke(bridge, () => editor.Document.Steps.Select(s => s.ToString()).ToArray()));
                Assert.Equal(revision, await Invoke(bridge, () => editor.Document.Revision));

                // Replace selection asks first (with pending proposals the decision-4 dialog is the confirmation);
                // Cancel changes nothing.
                window = await OpenStopped(bridge, editor);
                await bridge.Invoke(() => editor.SelectSteps(0, 1));
                var snapshot = await Invoke(bridge, () => editor.Document.Steps.Select(s => s.ToString()).ToArray());
                string? asked = null;
                WorkbenchRecordingWindow.Chooser = (_, question, _, _) => { asked = question; return -1; };
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.ReplaceCommand));
                Assert.Contains("Replace removes the 1 selected step", asked);
                Assert.Equal(snapshot, await Invoke(bridge, () => editor.Document.Steps.Select(s => s.ToString()).ToArray()));
                WorkbenchRecordingWindow.Chooser = (_, _, _, _) => 1;
                WorkbenchRecordingWindow.ConfirmReplace = _ => throw new InvalidOperationException("Replace was already confirmed in the pending dialog.");
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.ReplaceCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Equal(legacy, await Invoke(bridge, () => editor.Document.Steps.Take(legacy.Length).Select(s => s.ToString()).ToArray()));
            });
        }
        finally
        {
            WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchRecordingWindow.ConfirmReplace = null; WorkbenchRecordingWindow.Chooser = null;
            WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root);
        }
    }

    // ── manual-recording regression: why no checkpoint was inserted, and Ctrl+Shift clicks that were not picks ─────

    private static RecordedInput MouseAt(long sequence, ushort what, int x, int y, uint keys, byte buttons) =>
        new(sequence, "backend-queue", sequence * 100, "mouse", what, 0, null, keys, x, y, buttons, 0, 80, 25);

    // The owner's sequence as the target delivered it without pick interception: Ctrl+Shift+left click on a control, then F2.
    private static readonly RecordedInput[] CtrlShiftClick =
    [
        MouseAt(1, Events.evMouseDown, 6, 5, Keys.kbLeftShift | Keys.kbCtrlShift, (byte)Events.mbLeftButton),
        MouseAt(2, Events.evMouseUp, 6, 5, Keys.kbLeftShift | Keys.kbCtrlShift, 0),
        KeyAt(3, Keys.kbF2),
    ];

    [Fact]
    public async Task StoppedRecordingSaysWhyNoCheckpointWasInsertedAndFlagsCtrlShiftClicks()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                string LastNotice() => app.Notifications.History[^1].Message;
                async Task<ScenarioDocumentStep[]> Append(WorkbenchRecordingWindow w)
                {
                    int before = await Invoke(bridge, () => editor.Document.Steps.Count);
                    await bridge.Invoke(() => w.Execute(WorkbenchRecordingWindow.AppendCommand));
                    await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                    return await Invoke(bridge, () => editor.Document.Steps.Skip(before).ToArray());
                }

                WorkbenchRecordingWindow.Chooser = (_, _, _, _) => 1;   // Decision 4: proceed with accepted only
                // (1) Smart capture OFF: the Ctrl+Shift click stays ordinary input (modifier steps kept for replay), and the
                // window and the insert notice say that Smart capture was off and why the click was not a pick.
                WorkbenchRecorder.SmartCaptureOverride = false;
                var window = await OpenWith(bridge, editor, CtrlShiftClick, null);
                Assert.Equal(1, await Invoke(bridge, () => window.CtrlShiftClicks));
                Assert.Equal(["modifiers:shift-down", "modifiers:ctrl-down", "mouse:click-left@6,5", "modifiers:ctrl-up", "modifiers:shift-up", "key:kbF2"],
                    await Invoke(bridge, () => window.Conversion!.DocumentSteps.Select(s => s.Kind + ":" + s.Name).ToArray()));
                string text = await Text(bridge, window);
                Assert.Contains("Smart capture was off: no checkpoints proposed.", text);
                Assert.Contains("1 Ctrl+Shift click was recorded as input", text);
                var inserted = await Append(window);
                Assert.DoesNotContain(inserted, s => s.Kind == "checkpoint");
                Assert.Contains("Smart capture was off, so no checkpoints were proposed.", LastNotice());
                Assert.Contains("picking needs Smart capture on before Start", LastNotice());

                // (2) Smart capture ON, but the target's Proxy negotiated no Smart Recorder capture (deployed before Smart Recording).
                WorkbenchRecorder.SmartCaptureOverride = true;
                window = await OpenWith(bridge, editor, CtrlShiftClick, []);
                text = await Text(bridge, window);
                Assert.Contains("! Smart capture was unavailable (old target Proxy): nothing proposed.", text);
                Assert.Contains("Not a Smart recording", text);
                Assert.DoesNotContain("qualified", text);
                Assert.Equal(1, await Invoke(bridge, () => window.CtrlShiftClicks));
                Assert.Contains("1 Ctrl+Shift click was recorded as input", text);
                inserted = await Append(window);
                Assert.DoesNotContain(inserted, s => s.Kind == "checkpoint");
                Assert.Contains("Smart capture was unavailable: the target's Workbench Proxy is older than this Workbench", LastNotice());
                Assert.Contains("picking needs a target Proxy from this Workbench", LastNotice());

                // (3) Smart capture ON with a current Proxy: proposals exist, none accepted; hint and notice name the count.
                window = await OpenStopped(bridge, editor);
                int proposed = await Invoke(bridge, () => window.Review!.Checkpoints.Count);
                Assert.True(proposed > 0);
                Assert.Equal(0, await Invoke(bridge, () => window.CtrlShiftClicks));
                Assert.Contains($"{proposed} proposed checkpoint(s) NOT accepted: Review... to accept", await Text(bridge, window));
                inserted = await Append(window);
                Assert.DoesNotContain(inserted, s => s.Kind == "checkpoint");
                Assert.Contains($"{proposed} proposed checkpoint{(proposed == 1 ? " was" : "s were")} not accepted (Review...) and not inserted.", LastNotice());

                // (4) With picks available a Ctrl+Shift press would have been a pick, so no recorded click is flagged.
                window = await OpenWith(bridge, editor, CtrlShiftClick, [.. Protocol.RecordFeatures.All, Protocol.RecordFeatures.Picks]);
                Assert.Equal(0, await Invoke(bridge, () => window.CtrlShiftClicks));
                Assert.DoesNotContain("Ctrl+Shift click", await Text(bridge, window));
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.DiscardCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
            });
        }
        finally
        {
            WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchRecordingWindow.Chooser = null;
            WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root);
        }
    }

    // A stopped recording of the given events; features = what the target's Proxy advertised (null = not negotiated).
    private static async Task<WorkbenchRecordingWindow> OpenWith(Bridge bridge, WorkbenchScenarioEditorWindow editor, RecordedInput[] events, string[]? features)
    {
        await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
        var window = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
        var stopped = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Stopped, events);
        if (features is not null) stopped.Capture.Negotiate(features);
        await bridge.Invoke(() => window.Attach(stopped));
        return window;
    }

    private static async Task<WorkbenchRecordingWindow> OpenStopped(Bridge bridge, WorkbenchScenarioEditorWindow editor)
    {
        await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
        var window = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
        RecordedInput[] events = [KeyAt(1, Keys.kbF3), KeyAt(2, 0x1e61, "a"), KeyAt(3, Keys.kbEnter)];
        var stopped = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Stopped, events);
        stopped.Capture.Negotiate([.. Protocol.RecordFeatures.All, Protocol.RecordFeatures.Picks]);
        stopped.Capture.Merge(new RecordingBatch([], 3, false, false, 0, 0, 0, 0, 80, 25, "Win32ConsoleDriver")
        {
            Boundaries = [Mark(1, 0, 1), Mark(2, 1, 2, modal: "TDialog"), Mark(3, 2, 3, modal: "TDialog"), Mark(4, 3, 4)], BoundaryNext = 4,
            Coverage = new(4, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        });
        await bridge.Invoke(() => window.Attach(stopped));
        await Wait(bridge, () => window.Review);
        return window;
    }

    private static RecordedBoundary Mark(long id, long after, ulong hash, string? modal = null) =>
        new(id, Protocol.BoundaryKinds.Idle, id == 1 ? Protocol.BoundaryCauses.Start : Protocol.BoundaryCauses.Input, after, after, id, hash, id * 100, 80, 25, modal, 0);

    // ── physical: Win32ConsoleDriver in its own conhost (needs TSWB_PRODUCT_TARGET = a ProxyOrdinary prepared with this build) ──

    private static string? Target => Environment.GetEnvironmentVariable("TSWB_PRODUCT_TARGET") is { Length: > 0 } t && File.Exists(t) ? t : null;
    public sealed class ConsoleTargetFactAttribute : FactAttribute
    {
        public ConsoleTargetFactAttribute() { if (Target is null) Skip = "Set TSWB_PRODUCT_TARGET to a ProxyOrdinary.exe prepared with this build's Workbench Proxy."; }
    }

    [ConsoleTargetFact]
    public async Task ConsoleRecordingReviewedProposalsReplayWithRealAssertionPassAndForcedFail()
    {
        var log = new List<string>();
        await using var session = new RecordingSession(Target!, "Win32ConsoleDriver", "80x25") { ModifierPicks = true, SampleInspections = true };
        await session.StartAsync();
        Assert.True(session.State == RecordingState.Recording, session.Failure);
        Assert.True(session.PicksAvailable);
        int pid = session.ProcessId!.Value;
        // Ctrl+Shift picks: the status item "Alt-X Exit" (control) and the counter text (region drag); then F3 opens the
        // modal, Ctrl+Shift pick on its OK button, an ordinary click on OK closes it, F2 counts.
        // The counter region ("Count: 0") is dynamic and must yield no assertion; the window title region is static.
        SmartRecordingTests.ConsoleInput(pid, "x,3,24,24;d,4,5,13,5,24;d,12,3,33,3,24");
        await UntilAsync(() => Picks(session) >= 3, 20);
        SmartRecordingTests.ConsoleInput(pid, "k,114,61;x,20,9,24");
        await UntilAsync(() => Picks(session) >= 4, 20);
        SmartRecordingTests.ConsoleInput(pid, "c,20,9");
        await UntilAsync(() => session.Events.Any(e => e.What == Events.evMouseUp), 20);
        SmartRecordingTests.ConsoleInput(pid, "k,113,60");
        await UntilAsync(() => session.Events.Count(e => e.KeyCode == Keys.kbF2) >= 1, 20);
        await Task.Delay(600);
        await session.StopAsync();
        Assert.Equal(RecordingState.Stopped, session.State);

        var conversion = RecordingConverter.Convert(session.Events, session.Width, session.Height);
        Assert.Empty(conversion.Mismatches);
        var analysis = session.Analyze(conversion, ["initial"]);
        var suggestions = AssertionSuggester.Suggest(analysis, AssertionSuggester.TypedTexts(session.Events, conversion), session.Width, session.Height);
        var model = new RecordingReviewModel(analysis, suggestions, conversion, ["physical Win32 Console"]);
        log.Add("steps: " + string.Join(" | ", conversion.Steps.Select(s => $"{s.Step.Kind} {s.Step.Parameters} [{s.First}..{s.Last}]")));
        log.Add("coverage complete: " + session.Capture.CoverageComplete + "; picks: " + string.Join(" | ", analysis.Picks.Select(p =>
            $"{p.PickId} {p.Kind} {p.Region} {p.Evidence} {p.Target?.Area}:{p.Target?.Type}:{p.Target?.Text ?? p.Target?.StatusItem} sel={string.Join(",", p.Selectors.Select(c => c.Selector + "/" + c.Matches))}")));
        foreach (var c in model.Checkpoints)
        {
            log.Add($"proposal {c.CheckpointId} after={c.Proposal.InsertAfterStep} {c.Proposal.Placement} {c.Proposal.Kind} {c.Proposal.Confidence} evidence={c.Evidence.Source} "
                + $"flags={string.Join(",", c.Evidence.Flags)} picks={c.Proposal.Picks.Count} captureOnly={c.Suggestions.CaptureOnly} {c.Suggestions.CaptureOnlyReason}");
            foreach (var a in c.Assertions)
                log.Add($"   {a.Suggestion.Status,-11} {(a.Selected ? "[x]" : "[ ]")} {a.Suggestion.Origin,-5} {a.Suggestion.Confidence,-6} {a.Suggestion.Rule}: "
                    + $"{AssertionJson.ToJson(a.Suggestion.Spec).ToJsonString()} outcome={a.Suggestion.Outcome} {a.Suggestion.Reason}");
        }
        WriteLog(log);

        // Gate on real evidence: a modal state, the picked OK button, the status item and the static region text all
        // produced valid (evaluator PASS) suggestions; nothing uses a Path selector; the telemetry counter never appears.
        var valid = model.Checkpoints.SelectMany(c => c.Assertions).Where(a => a.Suggestion.Selectable).Select(a => a.Suggestion).ToArray();
        Assert.Contains(valid, s => s.Spec.Kind == AssertionKinds.Modal && s.Spec.View is not null);
        Assert.Contains(valid, s => s.Origin == SuggestionOrigins.Pick && s.Spec.View is { Type: not null, Text: "OK" });
        Assert.Contains(valid, s => s.Origin == SuggestionOrigins.Pick && s.Spec.Kind == AssertionKinds.StatusItem);
        Assert.Contains(valid, s => s.Origin == SuggestionOrigins.Pick && s.Spec.Kind is AssertionKinds.ScreenContains or AssertionKinds.TextAt && s.Spec.Text!.Contains("Ordinary counter"));
        Assert.DoesNotContain(valid, s => s.Spec.Text?.Contains("Count:", StringComparison.Ordinal) == true);
        Assert.All(model.Checkpoints.SelectMany(c => c.Assertions), a => Assert.True(a.Suggestion.Spec.View?.Path is null && a.Suggestion.Spec.Other?.Path is null));
        Assert.DoesNotContain(valid, s => s.Spec.Text?.Contains("Events", StringComparison.Ordinal) == true);

        // Review: accept every representable checkpoint, promote every selected suggestion to error (the user's choice).
        model.AcceptAll();
        foreach (var c in model.Checkpoints.Where(c => c.Accepted))
            foreach (var a in c.Assertions.Where(a => a.Selected).ToArray())
            {
                var json = a.Json; json["severity"] = AssertionSeverity.Error;
                Assert.Null(RecordingReviewModel.EditAssertion(c, a, json));
            }
        var m = RecordingReviewMaterializer.Materialize(model, ["initial"]);
        Assert.True(m.Ok, string.Join("; ", m.Problems));
        Assert.Equal(conversion.DocumentSteps.Select(s => s.ToString()), m.Steps.Where(s => s.Kind != "checkpoint").Select(s => s.ToString()));
        Assert.True(m.Assertions >= 4, $"{m.Assertions} assertions materialized.");

        var passed = await ReplayAsync(Target!, m.Steps, "pass", null);
        log.Add($"replay pass: {passed.Outcome} {passed.Reason}");
        var all = passed.Checkpoints.SelectMany(c => c.Assertions).ToArray();
        log.AddRange(all.Select(a => $"   {a.Checkpoint} {a.Id} {a.Kind} {a.Severity} {a.Outcome} expected={a.Expected} actual={a.Actual}"));
        WriteLog(log);
        Assert.Equal(ScenarioOutcomes.Passed, passed.Outcome);
        Assert.Equal(m.Assertions, all.Length);
        Assert.All(all, a => Assert.Equal(AssertionOutcomes.Pass, a.Outcome));

        // A deliberately wrong expectation at the modal checkpoint must FAIL (no false PASS).
        string modalId = model.Checkpoints.First(c => c.Accepted && c.Assertions.Any(a => a.Selected && a.Kind == AssertionKinds.Modal)).CheckpointId;
        var wrong = UiAssert.ScreenContains("count-is-ninety-nine", "Count: 99");
        var failing = await ReplayAsync(Target!, m.Steps, "fail", (modalId, wrong));
        var bad = failing.Checkpoints.SelectMany(c => c.Assertions).Single(a => a.Id == "count-is-ninety-nine");
        log.Add($"replay fail: {failing.Outcome} {failing.Reason}; forced assertion {bad.Outcome}: {bad.Explanation}");
        WriteLog(log);
        Assert.Equal(AssertionOutcomes.Fail, bad.Outcome);
        Assert.NotEqual(ScenarioOutcomes.Passed, failing.Outcome);
        Assert.Single(failing.FailedAssertions);

        // The same physical recording through the real Record Interaction window and scenario editor (Workbench UI logical):
        // Append, Insert at selection and Replace selection of reviewed proposals into one document, Save, then replay it.
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        string? savedPath = null;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            WorkbenchRecorder.SmartCaptureOverride = true;
            WorkbenchRecordingWindow.ConfirmReplace = _ => true;
            WorkbenchRecordingWindow.Chooser = (_, _, _, _) => 1;   // Decision 4: insert the accepted proposals
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                foreach (var (command, select) in new[] { (WorkbenchRecordingWindow.AppendCommand, -1), (WorkbenchRecordingWindow.InsertCommand, 0), (WorkbenchRecordingWindow.ReplaceCommand, -2) })
                {
                    await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                    var window = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                    await bridge.Invoke(() => window.Attach(session));
                    var review = await Wait(bridge, () => window.Review);
                    await bridge.Invoke(() =>
                    {
                        review.AcceptAll();
                        foreach (var c in review.Checkpoints.Where(c => c.Accepted))
                            foreach (var a in c.Assertions.Where(a => a.Selected).ToArray())
                            { var json = a.Json; json["severity"] = AssertionSeverity.Error; Assert.Null(RecordingReviewModel.EditAssertion(c, a, json)); }
                        // Insert before step 1; Replace the original "initial" checkpoint (wherever it is now).
                        if (select == 0) editor.SelectSteps(0, 1);
                        if (select == -2) editor.SelectSteps(editor.Document.Steps.ToList().FindIndex(s => s.Kind == "checkpoint" && s.Name == "initial"), 1);
                        window.Execute(command);
                    });
                    await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                }
                await bridge.Invoke(() =>
                {
                    Assert.True(editor.Document.Dirty);
                    var ids = editor.Document.Steps.Where(s => s.Kind == "checkpoint").Select(s => s.Name).ToArray();
                    Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                    Assert.DoesNotContain("initial", ids);
                    var saved = editor.Document.Save();
                    Assert.True(saved.Saved, saved.Message);
                    savedPath = saved.Path;
                });
            });
            Assert.NotNull(savedPath);
            if (Evidence is { } evidence) { Directory.CreateDirectory(Path.Combine(evidence, "docs")); File.Copy(savedPath!, Path.Combine(evidence, "docs", "review-physical-window.scenario.json"), true); }
            var scenario = ExternalScenarios.Merge([], [ExternalScenarios.Load(savedPath!)]).Single();
            var batch = await new ScenarioRunner().RunAsync(Target!, [scenario], new RunOptions(Path.Combine(root, "replay"), ["80x25"]) { ProxyBackend = "Win32ConsoleDriver" });
            var result = batch.Results.Single();
            var checks = result.Checkpoints.SelectMany(c => c.Assertions).ToArray();
            log.Add($"window round trip (Append + Insert at selection + Replace selection, saved): {result.Outcome}; {result.Checkpoints.Length} checkpoints, "
                + $"{checks.Length} assertions, {checks.Count(a => a.Outcome == AssertionOutcomes.Pass)} PASS; {result.Reason}");
            log.AddRange(checks.Where(a => a.Outcome != AssertionOutcomes.Pass).Select(a => $"   NOT PASS {a.Checkpoint} {a.Id} {a.Outcome} {a.Explanation}"));
            WriteLog(log);
            Assert.Equal(ScenarioOutcomes.Passed, result.Outcome);
            Assert.Equal(3 * m.Assertions, checks.Length);
            Assert.All(checks, a => Assert.Equal(AssertionOutcomes.Pass, a.Outcome));
        }
        finally
        {
            WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchRecordingWindow.ConfirmReplace = null; WorkbenchRecordingWindow.Chooser = null;
            WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root);
        }
    }

    private static int Picks(RecordingSession session) => session.Capture.Boundaries.Count(b => b.Kind == Protocol.BoundaryKinds.Pick);

    private static async Task<ScenarioResult> ReplayAsync(string target, IReadOnlyList<ScenarioDocumentStep> steps, string name, (string Checkpoint, AssertionSpec Spec)? extra)
    {
        string folder = Temp(); Directory.CreateDirectory(folder);
        try
        {
            var doc = ScenarioDocument.New("recorded/review-" + name, "Review " + name, "Reviewed Smart recording replayed on the real console.", checkpoint: null);
            foreach (var step in steps)
            {
                if (extra is { } x && step.Kind == "checkpoint" && step.Name == x.Checkpoint)
                {
                    var list = step.Node("assertions") as JsonArray ?? [];
                    list.Add(AssertionJson.ToJson(x.Spec));
                    doc.Append(step.With("assertions", list));
                }
                else doc.Append(step);
            }
            string path = Path.Combine(folder, $"review-physical-{name}.scenario.json");
            var saved = doc.Save(path);
            Assert.True(saved.Saved, saved.Message);
            if (Evidence is { } evidence) { Directory.CreateDirectory(Path.Combine(evidence, "docs")); File.Copy(path, Path.Combine(evidence, "docs", Path.GetFileName(path)), true); }
            var scenario = ExternalScenarios.Merge([], [ExternalScenarios.Load(path)]).Single();
            var batch = await new ScenarioRunner().RunAsync(target, [scenario], new RunOptions(Path.Combine(folder, "runs"), ["80x25"]) { ProxyBackend = "Win32ConsoleDriver" });
            return batch.Results.Single();
        }
        finally { Delete(folder); }
    }

    private static void WriteLog(List<string> lines)
    {
        if (Evidence is not { } root) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "review-console-physical.txt"), string.Join("\n", lines));
    }
    private static async Task UntilAsync(Func<bool> condition, int seconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Condition not reached."); await Task.Delay(50); }
    }

    // ── harness (pattern of SmartRecordingTests) ─────────────────────────────────────────────────────────────────

    private static List<TButton> Buttons(TGroup group)
    {
        var result = new List<TButton>();
        group.ForEachView(v => { if (v is TButton b) result.Add(b); });
        return result;
    }
    private static string SavedDocument(string root)
    {
        string path = Path.Combine(root, "docs", "recorded-" + Guid.NewGuid().ToString("N")[..6] + ".scenario.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saved = ScenarioDocument.New("recorded/demo", "Recorded demo", "Recorder window capture document.", checkpoint: "initial").Save(path);
        Assert.True(saved.Saved, saved.Message);
        return path;
    }
    private static string Row(ScreenSnapshot s, int y) => new(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character == 0 ? ' ' : s[x, y].Character).ToArray());
    private static string Screen(ScreenSnapshot s) => string.Join("\n", Enumerable.Range(0, s.Height).Select(y => Row(s, y).TrimEnd()));
    private static string SaveProject(string folder, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, "recorder.twproj"); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "review-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static async Task<string> Text(Bridge bridge, WorkbenchRecordingWindow window) { string text = ""; await bridge.Invoke(() => { window.UpdateView(); text = window.Text; }); return text; }
    private static async Task<T> Invoke<T>(Bridge bridge, Func<T> func) { T result = default!; await bridge.Invoke(() => result = func()); return result; }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            string target = TestWorkbenchHostTests.SampleTarget;
            using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { WorkbenchRecordingWindow.Active?.Close(); app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                    }).WaitAsync(TimeSpan.FromSeconds(30));
                }
            });
            AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(300)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }

    // Logical screenshots (NullDriver cell screen of the real WorkbenchApp, diagnostic bitmap font; not desktop-native).
    private static async Task Shot(Bridge bridge, string state)
    {
        await bridge.Invoke(() => { }); await Task.Delay(80); await bridge.Invoke(() => { });
        if (Evidence is not { } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root);
            var screen = ScreenSnapshot.CopyRoot();
            string name = $"review-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllText(Path.Combine(root, name + ".logical.txt"), Screen(screen));
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(RecorderIntegrationTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
