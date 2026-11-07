using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// The Review Proposals dialog of a stopped Smart recording. A classic
// modal dialog over RecordingReviewModel: summary lines on top, one list of checkpoint proposals with their suggested
// assertions indented below each, a scrollable detail pane for the focused row, a one-line notice and the buttons. Every
// decision goes through the model (refusals are shown in the notice line, never ignored); Cancel, Esc and the close box
// restore the decisions saved when the dialog opened. The dialog never inserts or saves anything.

internal static class WorkbenchRecordingReview
{
    /// <summary>Help context of the review dialog (F1 → Recorder topic).</summary>
    internal const ushort Context = 0x7d1c;

    /// <summary>Test seam: edits one assertion's v1 JSON (default: the scenario editor's assertion dialog). Null = cancelled.</summary>
    internal static Func<TDeskTop, JsonObject, JsonObject?>? AssertionEditor { get; set; }
    /// <summary>Test seam: edits a checkpoint step incl. its assertions (default: the scenario editor's step dialog). Null = cancelled.</summary>
    internal static Func<TDeskTop, ScenarioDocumentStep, ScenarioDocumentStep?>? StepEditor { get; set; }

    internal static JsonObject? EditAssertionJson(TDeskTop desktop, JsonObject assertion) =>
        AssertionEditor is { } seam ? seam(desktop, assertion) : WorkbenchAssertionDialog.Edit(desktop, assertion, isNew: false);
    internal static ScenarioDocumentStep? EditStep(TDeskTop desktop, ScenarioDocumentStep step) =>
        StepEditor is { } seam ? seam(desktop, step) : WorkbenchScenarioStepDialog.Edit(desktop, step, proxy: true, fixture: null, isNew: false);

    /// <summary>Shows the review of <paramref name="model"/> modally. OK keeps the decisions and returns true; Cancel, Esc and
    /// the close box restore every decision made in this session of the dialog and return false.</summary>
    internal static bool Review(TDeskTop desktop, RecordingReviewModel model)
    {
        object saved = model.Save();
        ushort result;
        using (var dialog = new WorkbenchRecordingReviewDialog(desktop, model)) result = desktop.ExecView(dialog);
        foreach (ushort command in WorkbenchRecordingReviewDialog.Commands) TView.EnableCommand(command);
        if (result == Views.cmOK) return true;
        model.Restore(saved);
        return false;
    }
}

/// <summary>The modal review dialog (see <see cref="WorkbenchRecordingReview"/>).</summary>
internal sealed class WorkbenchRecordingReviewDialog : TDialog
{
    internal const string DialogTitle = "Review Proposals";
    // Dialog-local commands (free in the Workbench range; enabled while the dialog lives).
    internal const ushort ToggleCommand = 0x7d38, EditCommand = 0x7d39, AcceptAllCommand = 0x7d3a, RejectAllCommand = 0x7d3b;
    internal static readonly ushort[] Commands = [ToggleCommand, EditCommand, AcceptAllCommand, RejectAllCommand];
    internal const string Hint = "Space accepts a proposal or selects an assertion; Enter edits. Done keeps the decisions, Cancel restores them.";

    private readonly TDeskTop desktop;
    private readonly RecordingReviewModel model;
    private readonly IReadOnlyList<string> summary;
    private readonly SummaryLine[] summaryLines;
    private readonly ReviewList list;
    private readonly DetailList detail;
    private readonly EditorNotice notice;
    private readonly List<ReviewRow> rows = [];

    internal TListViewer List => list;
    internal TListViewer Detail => detail;
    internal IReadOnlyList<ReviewRow> Rows => rows;
    internal string NoticeText => notice.Current;
    internal string DetailText => string.Join("\n", detail.Lines);
    internal ReviewRow? Focused => rows.ElementAtOrDefault(list.focused);
    internal string RowText(int index) => ReviewRowText.Format(rows[index], model, 512);

    /// <summary>Width ≤ 110 and height ≤ 38 inside the desktop (one cell of margin when it fits); a short review (few rows)
    /// gets a shorter dialog so the empty state is not a tall blank box.</summary>
    internal static TRect DefaultBounds(TDeskTop desktop, RecordingReviewModel model)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        int w = Math.Min(110, dw - 2 >= 76 ? dw - 2 : dw), h = Math.Min(38, dh - 2 >= 20 ? dh - 2 : dh);
        int rowCount = 1 + Math.Max(1, model.Checkpoints.Sum(c => 1 + (c.EditedStep?.AssertionCount ?? c.Assertions.Count)));
        int s = SummaryRows(h, model.Summary().Count);
        int wanted = s + 3 + Math.Max(6, rowCount) + 1 + 10 + 4;
        return new(0, 0, w, Math.Min(h, Math.Max(Math.Min(h, 20), wanted)));
    }

    private static int SummaryRows(int height, int lines) => Math.Max(1, Math.Min(height >= 28 ? 3 : 2, lines));

    internal WorkbenchRecordingReviewDialog(TDeskTop desktop, RecordingReviewModel model) : base(DefaultBounds(desktop, model), DialogTitle)
    {
        this.desktop = desktop; this.model = model;
        options |= Views.ofCentered; helpCtx = WorkbenchRecordingReview.Context;
        foreach (ushort command in Commands) EnableCommand(command);
        int w = size.x, h = size.y;
        summary = model.Summary();

        // Summary: the first 2-3 lines; the Summary row (first list row) shows every line in Details.
        int s = SummaryRows(h, summary.Count);
        summaryLines = new SummaryLine[s];
        for (int i = 0; i < s; i++) Insert(summaryLines[i] = new SummaryLine(new(2, 1 + i, w - 2, 2 + i), summary.ElementAtOrDefault(i) ?? ""));

        int listTop = s + 2, available = h - 4 - (s + 3);
        int detailRows = Math.Clamp(available * 2 / 5, 4, 10), listRows = Math.Max(3, available - detailRows);
        int rowCount = 1 + Math.Max(1, model.Checkpoints.Sum(c => 1 + (c.EditedStep?.AssertionCount ?? c.Assertions.Count)));
        if (rowCount < listRows) { listRows = Math.Max(Math.Min(listRows, 6), rowCount); detailRows = available - listRows; } // few rows: Details gets the rest
        var listScroll = new TScrollBar(new(w - 3, listTop, w - 2, listTop + listRows)); Insert(listScroll);
        list = new ReviewList(this, new(2, listTop, w - 3, listTop + listRows), listScroll); Insert(list);
        // The list label doubles as the column header of the checkpoint rows (TLabel text starts one cell right of its origin).
        Insert(new TLabel(new(1, listTop - 1, w - 2, listTop), ReviewRowText.Header(list.size.x), list));
        int detailTop = listTop + listRows + 1;
        var detailScroll = new TScrollBar(new(w - 3, detailTop, w - 2, h - 4)); Insert(detailScroll);
        detail = new DetailList(new(2, detailTop, w - 3, h - 4), detailScroll); Insert(detail);
        Insert(new TLabel(new(1, detailTop - 1, w - 2, detailTop), "Deta~i~ls", detail));
        notice = new EditorNotice(new(2, h - 4, w - 2, h - 3)); Insert(notice);

        // Mnemonics avoid the menu bar's P S T R B W H (and the recording window's); Space/Enter work in the list.
        int x = 2;
        void Button(string text, int width, ushort command, byte flags = ButtonConstants.bfNormal)
        { Insert(new TButton(new(x, h - 3, x + width, h - 1), text, command, flags)); x += width + 1; }
        Button("Togg~l~e", 10, ToggleCommand);
        Button("~E~dit", 8, EditCommand);
        Button("Accept ~a~ll", 14, AcceptAllCommand);
        Button("Re~j~ect all", 14, RejectAllCommand);
        x = Math.Max(x, w - 2 - 10 - 1 - 10);
        Button("~D~one", 10, Views.cmOK, ButtonConstants.bfDefault);
        Button("~C~ancel", 10, Views.cmCancel);

        Rebuild(null);
        list.FocusItemNum(rows.FindIndex(r => r.Checkpoint is not null) is >= 0 and var first ? first : 0);
        Notify(model.Checkpoints.Count == 0 ? "No checkpoint was proposed. Done keeps the recording input-only." : Hint);
        list.Select();
    }

    public override void ShutDown()
    {
        foreach (ushort command in Commands) EnableCommand(command);
        base.ShutDown();
    }

    // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild(object? keep)
    {
        rows.Clear();
        rows.Add(new ReviewRow(ReviewRowKind.Summary, null, null, null));
        if (model.Checkpoints.Count == 0) rows.Add(new ReviewRow(ReviewRowKind.Empty, null, null, null));
        foreach (var c in model.Checkpoints)
        {
            rows.Add(new ReviewRow(ReviewRowKind.Checkpoint, c, null, null));
            if (c.EditedStep is { } step)
                foreach (var a in step.Node("assertions") is JsonArray array ? array.OfType<JsonObject>() : [])
                    rows.Add(new ReviewRow(ReviewRowKind.EditedStepAssertion, c, null, a));
            else
                foreach (var a in c.Assertions) rows.Add(new ReviewRow(ReviewRowKind.Assertion, c, a, null));
        }
        int focus = keep is null ? -1 : rows.FindIndex(r => ReferenceEquals(r.Assertion, keep) || (r.Assertion is null && r.Kind == ReviewRowKind.Checkpoint && ReferenceEquals(r.Checkpoint, keep)));
        int previous = list.focused;
        list.SetRange(rows.Count);
        bool any = model.Checkpoints.Count > 0;
        foreach (ushort command in Commands) if (any) EnableCommand(command); else DisableCommand(command);
        if (focus >= 0) list.FocusItemNum(focus);
        else if (previous < rows.Count) list.FocusItemNum(previous);
        var now = model.Summary();
        for (int i = 0; i < summaryLines.Length && i < now.Count; i++) summaryLines[i].Set(now[i]);
        list.DrawView();
        ShowDetail();
    }

    internal void ShowDetail()
    {
        if (detail is null || list is null) return;
        var row = Focused;
        string text = row is null ? "" : ReviewRowText.Detail(row, model);
        detail.SetLines(text.Split('\n').SelectMany(l => WorkbenchText.Wrap(l, Math.Max(10, detail.size.x - 1))).ToArray());
    }

    private void Notify(string message) => notice.Set(WorkbenchText.Fit(message, notice.size.x));

    // ── decisions (only through the model) ───────────────────────────────────────────────────────────────────

    internal void Toggle()
    {
        if (Focused is not { } row) return;
        switch (row.Kind)
        {
            case ReviewRowKind.Checkpoint:
            {
                var c = row.Checkpoint!;
                bool accept = !c.Accepted;
                if (model.SetAccepted(c, accept) is { } refused) { Refuse(refused); return; }
                Notify(accept ? $"Accepted {c.CheckpointId}: it is inserted with {c.SelectedAssertions} assertion(s) when you insert the recording."
                    : $"Rejected {c.CheckpointId}: it is not inserted.");
                break;
            }
            case ReviewRowKind.Assertion:
            {
                var c = row.Checkpoint!; var a = row.Assertion!;
                bool select = !a.Selected;
                if (RecordingReviewModel.SetSelected(a, select) is { } refused) { Refuse(refused); return; }
                Notify(select ? $"Selected {a.Id}" + (c.Accepted ? "." : $"; it is inserted only if checkpoint {c.CheckpointId} is accepted.") : $"Deselected {a.Id}.");
                break;
            }
            case ReviewRowKind.EditedStepAssertion:
                Refuse("This checkpoint was edited in the step editor; its assertions are the edited step's. Edit the checkpoint to change them.");
                return;
            default:
                Refuse("Choose a proposal or an assertion row to accept or select it.");
                return;
        }
        Rebuild(row.Assertion ?? (object?)row.Checkpoint);
    }

    internal void Edit()
    {
        if (Focused is not { } row) return;
        switch (row.Kind)
        {
            case ReviewRowKind.Checkpoint:
            {
                var c = row.Checkpoint!;
                if (!c.Representable)
                {
                    Refuse("This proposal cannot be placed, so it cannot be edited or inserted: "
                        + (c.Proposal.Reason is { Length: > 0 } r ? r : "its position cannot be represented by a step boundary."));
                    return;
                }
                if (WorkbenchRecordingReview.EditStep(desktop, c.BuildStep()) is not { } edited) { Notify("Edit cancelled; nothing changed."); return; }
                if (model.EditCheckpoint(c, edited) is { } refused) { Refuse(refused); return; }
                Notify($"Checkpoint {c.CheckpointId} edited" + (c.Accepted ? "." : "; press Space to accept it."));
                break;
            }
            case ReviewRowKind.Assertion:
            {
                var c = row.Checkpoint!; var a = row.Assertion!;
                if (!a.Editable) { Refuse(RecordingReviewModel.EditAssertion(c, a, new JsonObject()) ?? "This suggestion cannot be edited."); return; }
                if (WorkbenchRecordingReview.EditAssertionJson(desktop, a.Json) is not { } edited) { Notify("Edit cancelled; nothing changed."); return; }
                if (RecordingReviewModel.EditAssertion(c, a, edited) is { } refused) { Refuse(refused); return; }
                Notify($"Assertion {a.Id} edited and selected" + (c.Accepted ? "." : $"; it is inserted only if checkpoint {c.CheckpointId} is accepted."));
                break;
            }
            case ReviewRowKind.EditedStepAssertion:
                Refuse("This assertion belongs to the edited checkpoint step; select the checkpoint row and Edit it.");
                return;
            default:
                Refuse("Choose a proposal or an assertion row to edit it.");
                return;
        }
        Rebuild(row.Assertion ?? (object?)row.Checkpoint);
    }

    internal void AcceptAll()
    {
        if (model.Checkpoints.Count == 0) return;
        model.AcceptAll();
        int blocked = model.Checkpoints.Count(c => !c.Representable);
        Notify($"Accepted {model.AcceptedCount} proposal(s)" + (blocked > 0 ? $"; {blocked} cannot be placed and stay rejected." : "."));
        Rebuild(Focused?.Assertion ?? (object?)Focused?.Checkpoint);
    }

    internal void RejectAll()
    {
        if (model.Checkpoints.Count == 0) return;
        model.RejectAll();
        Notify("Rejected every proposal: only the recorded inputs are inserted.");
        Rebuild(Focused?.Assertion ?? (object?)Focused?.Checkpoint);
    }

    private void Refuse(string message)
    {
        Notify("! " + message);
        // A refusal longer than the notice line also leads the detail pane, in full.
        if (message.Length + 2 <= notice.size.x) return;
        ShowDetail();
        detail.SetLines(WorkbenchText.Wrap("! " + message, Math.Max(10, detail.size.x - 1)).Append("").Concat(detail.Lines).ToArray());
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && Commands.Contains(ev.message.command))
        {
            ushort command = ev.message.command; ClearEvent(ref ev);
            switch (command)
            {
                case ToggleCommand: Toggle(); break;
                case EditCommand: Edit(); break;
                case AcceptAllCommand: AcceptAll(); break;
                case RejectAllCommand: RejectAll(); break;
            }
            list.Select();
            return;
        }
        base.HandleEvent(ref ev);
    }

    // ── views ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SummaryLine(TRect bounds, string text) : TStaticText(bounds, text)
    {
        internal void Set(string value) { if (Text == value) return; Text = value; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }

    /// <summary>The proposal list: Space toggles, Enter/double click edits, a click on the [x] mark toggles.</summary>
    private sealed class ReviewList(WorkbenchRecordingReviewDialog dialog, TRect bounds, TScrollBar scroll) : WorkbenchListBox(bounds, 1, scroll)
    {
        public override string GetText(int item, int maxChars) =>
            item < dialog.rows.Count ? WorkbenchText.Fit(ReviewRowText.Format(dialog.rows[item], dialog.model, maxChars), maxChars) : "";
        public override void FocusItem(int item) { base.FocusItem(item); dialog.ShowDetail(); }
        protected override bool IsToggleCell(int x) => x is >= 0 and <= 7;
        protected override void ToggleItem(int index) { FocusItemNum(index); dialog.Toggle(); }
        public override void SelectItem(int item) { FocusItemNum(item); dialog.Edit(); }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && (ev.keyDown.keyCode == Keys.kbSpace || ev.keyDown.charScan.charCode == ' ') && range > 0)
            { ClearEvent(ref ev); dialog.Toggle(); return; }
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter && range > 0)
            { ClearEvent(ref ev); dialog.Edit(); return; }
            base.HandleEvent(ref ev);
        }
    }

    /// <summary>Read-only wrapped text of the focused row (Tab or Alt+I to scroll it).</summary>
    private sealed class DetailList(TRect bounds, TScrollBar scroll) : TListViewer(bounds, 1, null, scroll)
    {
        internal string[] Lines { get; private set; } = [];
        internal void SetLines(string[] lines) { Lines = lines; SetRange(lines.Length); FocusItemNum(0); topItem = 0; DrawView(); }
        public override string GetText(int item, int maxLen) => item < Lines.Length ? WorkbenchText.Fit(Lines[item], maxLen) : "";
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.charScan.charCode == ' ') return; // nothing to select here
            base.HandleEvent(ref ev);
        }
    }
}
