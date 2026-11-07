using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

// The Review Proposals dialog over a RecordingReviewModel built directly from records (independent of
// the proposer/validator). Driven through the real WorkbenchApp on NullDriver; optional logical captures with
// TSWB_REVIEW_DIALOG_EVIDENCE=<directory> (c-review-{state}-{WxH}.logical.{png,txt,json}; logical only, NOT native).
[Collection("NonParallel")]
public sealed class ReviewDialogTests
{
    private static string? Evidence => Environment.GetEnvironmentVariable("TSWB_REVIEW_DIALOG_EVIDENCE") is { Length: > 0 } e ? e : null;

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static RecordedInput KeyAt(long sequence, ushort code, string? text = null) =>
        new(sequence, "backend-key", sequence * 100, "key", Events.evKeyDown, code, text, 0, 0, 0, 0, 0, 80, 25);

    private static CheckpointProposal Proposal(string id, int after, string kind = CandidateKinds.ModalOpen, string confidence = Confidences.High,
        bool unrepresentable = false, bool review = false, string evidence = EvidenceLevels.Snapshot, int boundary = 0) =>
        new("p-" + (boundary == 0 ? after + 2 : boundary), id, unrepresentable ? 0 : after, unrepresentable ? Placements.Unrepresentable : after < 0 ? Placements.BeforeFirst : Placements.AfterStep,
            unrepresentable ? null : ScenarioDocumentStep.Checkpoint(id), kind,
            unrepresentable ? "The state lies inside a coalesced step that cannot be split." : "A dialog opened after the key press.",
            confidence, evidence, after + 1, boundary == 0 ? after + 2 : boundary, evidence == EvidenceLevels.Snapshot ? 5 : null, ["c-" + (after + 2)], [], review,
            evidence == EvidenceLevels.Snapshot ? [] : [CandidateFlags.SnapshotDropped]);

    private static CheckpointEvidence EvidenceOf(CheckpointProposal p, bool cells = true) =>
        new(p.Id, p.BoundaryId, 0x40 + (ulong)p.BoundaryId, p.AfterSequence, cells ? EvidenceSources.Snapshot : EvidenceSources.None, cells ? 5 : null, null, null, false,
            cells ? [] : [EvidenceFlags.NoInspection]);

    private static AssertionSuggestion Valid(string proposal, string id, string kind, ViewSelector? view = null, string? text = null, bool selected = true, string origin = SuggestionOrigins.State) =>
        new(proposal, new AssertionSpec(id, kind, AssertionSeverity.Warning, View: view, Text: text), origin, origin == SuggestionOrigins.Pick ? 7 : null, "modal-open",
            "The dialog executes modally in this state.", Confidences.High, SuggestionStatuses.Valid, selected, "PASS", null, []);
    private static AssertionSuggestion Unsupported(string proposal, string id) =>
        new(proposal, new AssertionSpec(id, "view-focused", AssertionSeverity.Warning, View: new ViewSelector(Type: "TButton")), SuggestionOrigins.State, null, "focused-control",
            "The focused control in this state.", Confidences.Medium, SuggestionStatuses.Unsupported, false, "FAIL", "The selector matches 3 views; it is not unique.", []);
    private static AssertionSuggestion Withheld(string proposal, string id) =>
        new(proposal, new AssertionSpec(id, "screen-contains", AssertionSeverity.Warning, Text: "(redacted)"), SuggestionOrigins.Pick, 9, "pick-region",
            "Text of the picked region.", Confidences.Low, SuggestionStatuses.Rejected, false, null, "Contains text you typed; withheld.", ["typed-text"]);

    private static RecordingReviewModel Model(IReadOnlyList<CheckpointSuggestions> suggestions, RecordingConversion? conversion = null)
    {
        conversion ??= RecordingConverter.Convert([KeyAt(1, Keys.kbF3), KeyAt(2, Keys.kbEnter), KeyAt(3, Keys.kbEsc)], 80, 25);
        var analysis = new SmartRecordingAnalysis(new DetectionResult([], [], new DetectorCounters(0, 0, 0, 0, 0, 0, 0, 0, 0)), [],
            new CheckpointPlan(conversion.Steps, false, suggestions.Select(s => s.Proposal).ToArray(), []), []);
        return new RecordingReviewModel(analysis, suggestions, conversion, ["Smart capture on; Proxy features complete (logical test fixture)."]);
    }

    /// <summary>Four proposals: a full one (valid ×2, unsupported, withheld), an unrepresentable one, a NeedsReview capture-only
    /// one (missing evidence) and a pick-only medium one.</summary>
    private static RecordingReviewModel Populated()
    {
        var open = Proposal("rec-dialog-open", 0);
        var inside = Proposal("rec-inside-text", 1, CandidateKinds.Content, Confidences.Medium, unrepresentable: true);
        var gone = Proposal("rec-dialog-closed", 2, CandidateKinds.ModalClose, Confidences.Low, review: true, evidence: EvidenceLevels.Missing);
        var first = Proposal("rec-initial", -1, CandidateKinds.Content, Confidences.Medium, boundary: 1);
        return Model([
            new(open, EvidenceOf(open), [
                Valid(open.Id, "dialog-modal", "modal", new ViewSelector(Type: "TDialog", Text: "Open File")),
                Valid(open.Id, "file-name-visible", "view-visible", new ViewSelector(Type: "TLabel", Text: "~N~ame"), origin: SuggestionOrigins.Pick),
                Unsupported(open.Id, "focused-button"), Withheld(open.Id, "typed-region")], false, null, 80, 25),
            new(inside, EvidenceOf(inside), [], true, "The checkpoint cannot be placed in the step list.", 80, 25),
            new(gone, EvidenceOf(gone, cells: false), [], true, "No screen of this state was captured (no-inspection); the checkpoint is a capture only.", 80, 25),
            new(first, EvidenceOf(first), [Valid(first.Id, "title-shown", "screen-contains", text: "Workbench Sample", selected: false)], false, null, 80, 25)]);
    }

    private static RecordingReviewModel Many(int count)
    {
        var list = new List<CheckpointSuggestions>();
        for (int i = 0; i < count; i++)
        {
            var p = Proposal($"rec-state-{i + 1}", 0, CandidateKinds.Content, i % 3 == 0 ? Confidences.High : Confidences.Medium, boundary: 10 + i);
            list.Add(new(p, EvidenceOf(p), [Valid(p.Id, "status-" + (i + 1), "status-hint", text: "Ready " + (i + 1))], false, null, 80, 25));
        }
        return Model(list);
    }

    private static string Decisions(RecordingReviewModel model) => string.Join("|", model.Checkpoints.Select(c =>
        $"{c.CheckpointId}:{c.Accepted}:{c.IdEdited}:{c.EditedStep?.ToJson().ToJsonString()}:" + string.Join(",", c.Assertions.Select(a => $"{a.Selected}/{a.Edited?.ToJsonString()}"))));

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Session(WorkbenchApp app, Bridge bridge, Task running, Func<bool?> result)
    {
        internal WorkbenchApp App => app;
        internal Bridge Bridge => bridge;
        internal WorkbenchRecordingReviewDialog Dialog = null!;
        internal async Task<bool> Closed()
        {
            await running.WaitAsync(TimeSpan.FromSeconds(30));
            return result() ?? throw new InvalidOperationException("Review returned no result.");
        }
        internal async Task<T> Get<T>(Func<WorkbenchRecordingReviewDialog, T> f) { T value = default!; await bridge.Invoke(() => value = f(Dialog)); return value; }
        internal async Task Key(ushort key) { Press(key); await Settle(bridge); }
        internal Task FocusRow(Func<ReviewRow, bool> where) => bridge.Invoke(() => Dialog.List.FocusItemNum(Dialog.Rows.ToList().FindIndex(r => where(r))));
    }

    private static async Task<Session> Open(WorkbenchApp app, Bridge bridge, RecordingReviewModel model)
    {
        bool? result = null;
        var running = bridge.Invoke(() => result = WorkbenchRecordingReview.Review(app.DeskTop!, model));
        var session = new Session(app, bridge, running, () => result);
        session.Dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRecordingReviewDialog);
        await Settle(bridge);
        return session;
    }

    private static async Task Run(int width, int height, Func<WorkbenchApp, Bridge, Task> body)
    {
        string root = Temp();
        var assertion = WorkbenchRecordingReview.AssertionEditor; var step = WorkbenchRecordingReview.StepEditor;
        try { await Drive(width, height, root, Path.Combine(root, "home"), body); }
        finally { WorkbenchRecordingReview.AssertionEditor = assertion; WorkbenchRecordingReview.StepEditor = step; Delete(root); }
    }

    // ── keyboard decisions ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SpaceTogglesThroughTheModelAndDoneKeepsTheDecisions()
    {
        var model = Populated();
        await Run(120, 36, async (app, bridge) =>
        {
            var s = await Open(app, bridge, model);
            var open = model.Checkpoints.Single(c => c.CheckpointId == "rec-dialog-open");
            Assert.Equal("rec-initial", (await s.Get(d => d.Focused))!.Checkpoint!.CheckpointId); // first checkpoint row focused (before step 1)
            // Only the safe high-confidence proposal is preselected; the others start not accepted.
            Assert.All(model.Checkpoints, c => Assert.Equal(c == open, c.Accepted));
            Assert.True(open.Preselected);

            await s.FocusRow(r => r.Kind == ReviewRowKind.Checkpoint && r.Checkpoint == open);
            Assert.Contains("rec-dialog-open (proposal p-2): ACCEPTED (preselected", await s.Get(d => d.DetailText));
            await s.Key(Keys.kbSpace);                                                                   // reject: children inactive
            Assert.False(open.Accepted);
            Assert.Contains("rec-dialog-open (proposal p-2): not accepted", await s.Get(d => d.DetailText));
            Assert.Contains("inactive(sel)", await s.Get(d => d.RowText(d.List.focused + 1)));
            await s.Key(Keys.kbSpace);
            Assert.True(open.Accepted);
            Assert.StartsWith("Accepted rec-dialog-open", await s.Get(d => d.NoticeText));
            Assert.StartsWith("[x] rec-dialog-open", await s.Get(d => d.RowText(d.List.focused)));

            await s.Key(Keys.kbDown);                                                                    // dialog-modal (default selected)
            Assert.Equal("dialog-modal", (await s.Get(d => d.Focused))!.Assertion!.Id);
            Assert.True(open.Assertions[0].Selected);
            await s.Key(Keys.kbSpace);
            Assert.False(open.Assertions[0].Selected);
            Assert.Contains("[ ] modal", await s.Get(d => d.RowText(d.List.focused)));

            await s.Key(Keys.kbDown); await s.Key(Keys.kbDown);                                           // focused-button: unsupported
            Assert.Equal("focused-button", (await s.Get(d => d.Focused))!.Assertion!.Id);
            await s.Key(Keys.kbSpace);
            Assert.False(open.Assertions[2].Selected);
            Assert.StartsWith("! This assertion cannot be added: The selector matches 3 views", await s.Get(d => d.NoticeText));
            Assert.Contains("Reason: The selector matches 3 views", await s.Get(d => d.DetailText));    // fits the notice: not repeated
            Assert.Contains("not checkable", await s.Get(d => d.RowText(d.List.focused)));

            await s.Key(Keys.kbDown);                                                                    // typed-region: withheld
            await s.Key(Keys.kbSpace);
            Assert.False(open.Assertions[3].Selected);
            Assert.StartsWith("! This assertion cannot be added: Contains text you typed", await s.Get(d => d.NoticeText));
            string withheld = await s.Get(d => d.DetailText);
            Assert.DoesNotContain("(redacted)", withheld); Assert.Contains("Values are not shown", withheld);
            Assert.Contains("(values withheld)", await s.Get(d => d.RowText(d.List.focused)));

            await s.Key(Keys.kbDown);                                                                    // rec-inside-text: unrepresentable
            Assert.Equal("rec-inside-text", (await s.Get(d => d.Focused))!.Checkpoint!.CheckpointId);
            await s.Key(Keys.kbSpace);
            Assert.False(model.Checkpoints.Single(c => c.CheckpointId == "rec-inside-text").Accepted);
            Assert.StartsWith("! This proposal cannot be inserted: The state lies inside a coalesced step", await s.Get(d => d.NoticeText));
            Assert.StartsWith("! This proposal cannot be inserted", await s.Get(d => d.DetailText));    // too long for the notice: full text leads Details
            Assert.Contains("Insert the inputs only", (await s.Get(d => d.DetailText)).Replace('\n', ' '));
            Assert.StartsWith("[-] rec-inside-text", await s.Get(d => d.RowText(d.List.focused)));

            await s.FocusRow(r => r.Kind == ReviewRowKind.Summary);
            await s.Key(Keys.kbSpace);
            Assert.StartsWith("! Choose a proposal", await s.Get(d => d.NoticeText));

            string before = Decisions(model);
            await s.Key(Keys.kbAltD);
            Assert.True(await s.Closed());
            Assert.Equal(before, Decisions(model));
            Assert.True(open.Accepted); Assert.False(open.Assertions[0].Selected); Assert.True(open.Assertions[1].Selected);
        });
    }

    [Fact]
    public async Task AcceptAllAndRejectAllUseTheModelAndSkipUnrepresentable()
    {
        var model = Populated();
        await Run(120, 36, async (app, bridge) =>
        {
            var s = await Open(app, bridge, model);
            await s.Key(Keys.kbAltA);
            Assert.Equal(3, model.AcceptedCount);
            Assert.False(model.Checkpoints.Single(c => !c.Representable).Accepted);
            Assert.Equal("Accepted 3 proposal(s); 1 cannot be placed and stay rejected.", await s.Get(d => d.NoticeText));
            Assert.Contains("3 accepted", await s.Get(d => d.RowText(0)));
            await s.Key(Keys.kbAltJ);
            Assert.Equal(0, model.AcceptedCount);
            Assert.StartsWith("Rejected every proposal", await s.Get(d => d.NoticeText));
            await s.Key(Keys.kbAltD);
            Assert.True(await s.Closed());
        });
    }

    // ── editing via seams ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnterEditsAssertionsAndCheckpointsThroughTheSeams()
    {
        var model = Populated();
        var open = model.Checkpoints.Single(c => c.CheckpointId == "rec-dialog-open");
        JsonObject? seen = null; ScenarioDocumentStep? seenStep = null;
        await Run(120, 36, async (app, bridge) =>
        {
            WorkbenchRecordingReview.AssertionEditor = (_, json) =>
            {
                seen = json;
                return new JsonObject { ["id"] = "dialog-title", ["kind"] = "screen-contains", ["severity"] = "error", ["text"] = "Open File" };
            };
            var s = await Open(app, bridge, model);

            // Valid assertion → seam → model.EditAssertion (selected + "edited").
            await s.FocusRow(r => r.Assertion?.Id == "file-name-visible");
            await s.Key(Keys.kbEnter);
            Assert.Equal("file-name-visible", seen!["id"]!.GetValue<string>());
            var edited = open.Assertions[1];
            Assert.NotNull(edited.Edited); Assert.True(edited.Selected); Assert.Equal("dialog-title", edited.Id);
            Assert.Contains("edited", await s.Get(d => d.RowText(d.List.focused)));
            Assert.Contains("\"text\":\"Open File\"", await s.Get(d => d.DetailText));

            // Invalid edit → the model's refusal, nothing stored.
            WorkbenchRecordingReview.AssertionEditor = (_, json) => new JsonObject { ["id"] = "bad", ["kind"] = "text-at", ["severity"] = "warning", ["text"] = "x" };
            await s.FocusRow(r => r.Assertion?.Id == "dialog-modal");
            await s.Key(Keys.kbAltE);
            Assert.StartsWith("! Not valid schema v1", await s.Get(d => d.NoticeText));
            Assert.Null(open.Assertions[0].Edited);

            // Cancelled editor → nothing changes.
            WorkbenchRecordingReview.AssertionEditor = (_, _) => null;
            await s.Key(Keys.kbEnter);
            Assert.Equal("Edit cancelled; nothing changed.", await s.Get(d => d.NoticeText));

            // Withheld → the model's message; the editor is never opened.
            int calls = 0; WorkbenchRecordingReview.AssertionEditor = (_, j) => { calls++; return j; };
            await s.FocusRow(r => r.Assertion?.Id == "typed-region");
            await s.Key(Keys.kbEnter);
            Assert.Equal(0, calls);
            Assert.StartsWith("! A withheld suggestion cannot be edited", await s.Get(d => d.NoticeText));

            // Unrepresentable checkpoint → refused before the step editor.
            WorkbenchRecordingReview.StepEditor = (_, st) => { calls++; return st; };
            await s.FocusRow(r => r.Kind == ReviewRowKind.Checkpoint && !r.Checkpoint!.Representable);
            await s.Key(Keys.kbEnter);
            Assert.Equal(0, calls);
            Assert.StartsWith("! This proposal cannot be placed", await s.Get(d => d.NoticeText));

            // Checkpoint → step seam → model.EditCheckpoint (renamed, edited assertions replace the suggestions).
            WorkbenchRecordingReview.StepEditor = (_, st) =>
            {
                seenStep = st;
                return AssertionJson.Checkpoint("rec-file-dialog", "Edited by hand",
                    [new JsonObject { ["id"] = "dialog-modal", ["kind"] = "modal", ["severity"] = "error", ["view"] = new JsonObject { ["type"] = "TDialog" } }]);
            };
            await s.FocusRow(r => r.Kind == ReviewRowKind.Checkpoint && r.Checkpoint == open);
            await s.Key(Keys.kbEnter);
            Assert.Equal("checkpoint", seenStep!.Kind);
            Assert.Equal("rec-dialog-open", seenStep.String("id"));
            Assert.Equal(2, seenStep.AssertionCount);                                                  // the selected suggestions went in
            Assert.Equal("rec-file-dialog", open.CheckpointId); Assert.NotNull(open.EditedStep); Assert.True(open.IdEdited);
            Assert.Contains("edited", await s.Get(d => d.RowText(d.List.focused)));
            var rows = await s.Get(d => d.Rows.Where(r => r.Checkpoint == open).Select(r => r.Kind).ToArray());
            Assert.Equal([ReviewRowKind.Checkpoint, ReviewRowKind.EditedStepAssertion], rows);
            await s.Key(Keys.kbDown); await s.Key(Keys.kbSpace);
            Assert.StartsWith("! This checkpoint was edited in the step editor", await s.Get(d => d.NoticeText));

            // An edited step that collides with another proposal's ID → the model's refusal.
            WorkbenchRecordingReview.StepEditor = (_, _) => ScenarioDocumentStep.Checkpoint("rec-initial");
            await s.FocusRow(r => r.Kind == ReviewRowKind.Checkpoint && r.Checkpoint == open);
            await s.Key(Keys.kbEnter);
            Assert.StartsWith("! Another proposal already uses the checkpoint ID 'rec-initial'", await s.Get(d => d.NoticeText));
            Assert.Equal("rec-file-dialog", open.CheckpointId);

            await s.Key(Keys.kbAltD);
            Assert.True(await s.Closed());
            Assert.Equal("rec-file-dialog", open.CheckpointId);
        });
    }

    // ── cancel semantics ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("button")]
    [InlineData("esc")]
    [InlineData("close-box")]
    public async Task CancelEscAndCloseBoxRestoreEveryDecision(string how)
    {
        var model = Populated();
        // Decisions before the dialog opens are kept (only this session's changes are undone).
        model.SetAccepted(model.Checkpoints.Single(c => c.CheckpointId == "rec-initial"), true);
        string initial = Decisions(model);
        await Run(120, 36, async (app, bridge) =>
        {
            WorkbenchRecordingReview.AssertionEditor = (_, _) => new JsonObject { ["id"] = "dialog-title", ["kind"] = "screen-contains", ["severity"] = "warning", ["text"] = "Open File" };
            WorkbenchRecordingReview.StepEditor = (_, _) => ScenarioDocumentStep.Checkpoint("rec-renamed");
            var s = await Open(app, bridge, model);
            await s.Key(Keys.kbAltA);
            await s.FocusRow(r => r.Assertion?.Id == "dialog-modal"); await s.Key(Keys.kbSpace);
            await s.FocusRow(r => r.Assertion?.Id == "file-name-visible"); await s.Key(Keys.kbEnter);
            await s.FocusRow(r => r.Kind == ReviewRowKind.Checkpoint && r.Checkpoint!.CheckpointId == "rec-initial"); await s.Key(Keys.kbEnter);
            Assert.NotEqual(initial, Decisions(model));
            switch (how)
            {
                case "button": await s.Key(Keys.kbAltC); break;
                case "esc": await s.Key(Keys.kbEsc); break;
                default: Send(Views.cmClose); break;
            }
            Assert.False(await s.Closed());
            Assert.Equal(initial, Decisions(model));
            Assert.Null(app.DeskTop!.TopView() as WorkbenchRecordingReviewDialog);
        });
    }

    // ── states ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyReviewExplainsAndDisablesTheDecisionButtons()
    {
        var model = Model([]);
        await Run(120, 36, async (app, bridge) =>
        {
            var s = await Open(app, bridge, model);
            Assert.Equal([ReviewRowKind.Summary, ReviewRowKind.Empty], await s.Get(d => d.Rows.Select(r => r.Kind).ToArray()));
            Assert.Contains("No checkpoint was proposed", await s.Get(d => d.NoticeText));
            Assert.Contains("0 checkpoint proposals", await s.Get(d => d.DetailText));
            await bridge.Invoke(() =>
            {
                foreach (ushort c in new[] { WorkbenchRecordingReviewDialog.ToggleCommand, WorkbenchRecordingReviewDialog.EditCommand,
                    WorkbenchRecordingReviewDialog.AcceptAllCommand, WorkbenchRecordingReviewDialog.RejectAllCommand })
                    Assert.False(TView.CommandEnabled(c));
            });
            await s.Key(Keys.kbSpace);
            Assert.StartsWith("! Choose a proposal", await s.Get(d => d.NoticeText));
            await s.Key(Keys.kbAltD);
            Assert.True(await s.Closed());
            await bridge.Invoke(() => Assert.True(TView.CommandEnabled(WorkbenchRecordingReviewDialog.ToggleCommand)));
        });
    }

    [Fact]
    public async Task HundredsOfRowsScrollAndStayAddressable()
    {
        var model = Many(120);
        await Run(120, 36, async (app, bridge) =>
        {
            var s = await Open(app, bridge, model);
            Assert.Equal(1 + 120 * 2, await s.Get(d => d.Rows.Count));
            await s.Key(Keys.kbCtrlPgDn);
            Assert.Equal(240, await s.Get(d => d.List.focused));
            Assert.True(await s.Get(d => d.List.topItem) > 0);
            Assert.Equal("status-120", (await s.Get(d => d.Focused))!.Assertion!.Id);
            await s.Key(Keys.kbUp); await s.Key(Keys.kbSpace);
            Assert.True(model.Checkpoints[^1].Accepted);
            Assert.Contains(Rows(), r => r.Contains("[x] rec-state-120", StringComparison.Ordinal));
            await s.Key(Keys.kbCtrlPgUp);
            Assert.Equal(0, await s.Get(d => d.List.topItem));
            await s.Key(Keys.kbEsc);
            Assert.False(await s.Closed());
            Assert.False(model.Checkpoints[^1].Accepted);
        });
    }

    // ── geometry + logical captures ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(80, 25)]
    public async Task DialogFitsTheDesktopWithoutOverlaps(int width, int height)
    {
        await Run(width, height, async (app, bridge) =>
        {
            var s = await Open(app, bridge, Model([]));
            await bridge.Invoke(() => Geometry(app.DeskTop!, s.Dialog));
            await Shot(bridge, "empty");
            await s.Key(Keys.kbAltD); Assert.True(await s.Closed());

            var model = Populated();
            s = await Open(app, bridge, model);
            await bridge.Invoke(() => Geometry(app.DeskTop!, s.Dialog));
            await s.FocusRow(r => r.Kind == ReviewRowKind.Checkpoint && r.Checkpoint!.CheckpointId == "rec-dialog-open");
            await s.Key(Keys.kbSpace);
            await Shot(bridge, "populated");
            await s.FocusRow(r => r.Assertion?.Id == "focused-button");
            await s.Key(Keys.kbSpace);
            await Shot(bridge, "detail-unsupported");
            string[] screen = Rows();
            Assert.Contains(screen, r => r.Contains("Review Proposals", StringComparison.Ordinal));
            Assert.Contains(screen, r => r.Contains("Done", StringComparison.Ordinal) && r.Contains("Cancel", StringComparison.Ordinal));
            Assert.Contains(screen, r => r.Contains("! This assertion cannot be added", StringComparison.Ordinal));
            await s.Key(Keys.kbEsc); Assert.False(await s.Closed());
        });
    }

    private static void Geometry(TDeskTop desktop, WorkbenchRecordingReviewDialog dialog)
    {
        Assert.True(dialog.origin.x >= 0 && dialog.origin.y >= 0, $"dialog origin {dialog.origin.x},{dialog.origin.y}");
        Assert.True(dialog.origin.x + dialog.size.x <= desktop.size.x && dialog.origin.y + dialog.size.y <= desktop.size.y,
            $"dialog {dialog.size.x}x{dialog.size.y} at {dialog.origin.x},{dialog.origin.y} exceeds desktop {desktop.size.x}x{desktop.size.y}");
        Assert.True(dialog.size.x <= 110);
        var views = Children(dialog).Where(v => v is not TFrame && v.size.x > 0 && v.size.y > 0).ToArray();
        foreach (var v in views)
            Assert.True(v.origin.x >= 1 && v.origin.y >= 1 && v.origin.x + v.size.x <= dialog.size.x - 1 && v.origin.y + v.size.y <= dialog.size.y - 1,
                $"{v.GetType().Name} at {v.origin.x},{v.origin.y} size {v.size.x}x{v.size.y} leaves the {dialog.size.x}x{dialog.size.y} dialog interior");
        for (int i = 0; i < views.Length; i++)
            for (int j = i + 1; j < views.Length; j++)
            {
                TView a = views[i], b = views[j];
                bool overlap = a.origin.x < b.origin.x + b.size.x && b.origin.x < a.origin.x + a.size.x && a.origin.y < b.origin.y + b.size.y && b.origin.y < a.origin.y + a.size.y;
                Assert.False(overlap, $"{a.GetType().Name} ({a.origin.x},{a.origin.y} {a.size.x}x{a.size.y}) overlaps {b.GetType().Name} ({b.origin.x},{b.origin.y} {b.size.x}x{b.size.y})");
            }
        Assert.Equal(6, views.Count(v => v is TButton));
        Assert.True(dialog.List.size.y >= 3 && dialog.Detail.size.y >= 4, $"list {dialog.List.size.y} rows, detail {dialog.Detail.size.y} rows");
        Assert.Equal(WorkbenchRecordingReview.Context, dialog.helpCtx);
    }

    private static async Task Shot(Bridge bridge, string state)
    {
        await Settle(bridge);
        if (Evidence is not { } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root);
            var screen = ScreenSnapshot.CopyRoot();
            string name = $"c-review-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllLines(Path.Combine(root, name + ".logical.txt"),
                Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character == 0 ? ' ' : screen[x, y].Character).ToArray()).TrimEnd()));
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(ReviewDialogTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
}
