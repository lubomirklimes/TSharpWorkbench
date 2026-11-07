using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

/// <summary>Edits one step: only the fields of its kind, plus its note. Unknown (non-v1) properties are kept as they are.</summary>
/// <remarks>
/// The fields of each kind are a <see cref="StepPanel"/> (Dialogs/Editors/Steps) that reports the size it needs. The dialog
/// is sized around it, top to bottom: panel, blank row, note, (unknown properties), blank row, notice (2 rows), buttons.
/// </remarks>
internal sealed class WorkbenchScenarioStepDialog : EditorDialog
{
    internal const ushort AssertionAdd = CheckpointStepPanel.AssertionAdd, AssertionEdit = CheckpointStepPanel.AssertionEdit, AssertionDelete = CheckpointStepPanel.AssertionDelete;
    private const int PanelX = 2, PanelY = 2;
    private readonly ScenarioDocumentStep original;
    private readonly StepPanel panel;
    private readonly TInputLine? noteLine;
    private readonly TMemo? noteMemo;

    internal ScenarioDocumentStep? Result { get; private set; }
    internal StepPanel Panel => panel;
    internal TInputLine? KeyField => (panel as KeyStepPanel)?.Key;
    internal TInputLine? TextField => (panel as TextStepPanel)?.TextField;
    internal TInputLine? IdField => (panel as CheckpointStepPanel)?.Id;
    internal TInputLine NoteField => noteLine!;
    internal TMemo? CheckpointNoteField => noteMemo;
    internal TInputLine? XField => (panel as MouseStepPanel)?.X;
    internal TInputLine? YField => (panel as MouseStepPanel)?.Y;
    internal TRadioButtons? Verb => (panel as MouseStepPanel)?.Verb;
    internal TListBox? AssertionList => (panel as CheckpointStepPanel)?.AssertionList;
    internal string NoticeText => notice.Current;
    internal string PreviewText => (panel as MouseStepPanel)?.PreviewText ?? "";

    private WorkbenchScenarioStepDialog(TDeskTop desktop, ScenarioDocumentStep step, bool proxy, ScenarioDescriptor? fixture, bool isNew)
        : this(desktop, step, StepPanel.Create(step, proxy, fixture, desktop.size.x - 2 - 2 * PanelX), isNew) { }

    private WorkbenchScenarioStepDialog(TDeskTop desktop, ScenarioDocumentStep step, StepPanel panel, bool isNew)
        : base(Math.Min(desktop.size.x - 2, panel.size.x + 2 * PanelX), Math.Min(HeightFor(panel, Unknown(step)), desktop.size.y),
            (isNew ? "New " : "Edit ") + ScenarioSchemaV1.StepTitle(step.Kind))
    {
        original = step;
        this.panel = panel;
        int w = size.x;
        panel.ChangeBounds(new(PanelX, PanelY, PanelX + panel.size.x, PanelY + panel.size.y));
        panel.Notify = notice.Set;
        Insert(panel);

        int y = PanelY + panel.size.y + 1;
        if (panel.NoteLines > 1)
        {
            int lines = panel.NoteLines;
            string value = step.Note ?? "";
            var vertical = new TScrollBar(new(w - 4, y + 1, w - 3, y + 1 + lines)) { growMode = 0 };
            var horizontal = new TScrollBar(new(LabelX, y + 1 + lines, w - 4, y + 2 + lines)) { growMode = 0 };
            Insert(vertical); Insert(horizontal);
            noteMemo = new TMemo(new(LabelX, y + 1, w - 4, y + 1 + lines), horizontal, vertical, null, (uint)Math.Max(65536, value.Length + 16384)) { growMode = 0, canUndo = true };
            noteMemo.SetData(new TMemoData((uint)value.Length) { length = (uint)value.Length, buffer = value.ToCharArray() });
            Insert(noteMemo);
            Insert(new TLabel(new(LabelX, y, w - 3, y + 1), "N~o~te", noteMemo));
            y += lines + 2;
        }
        else
        {
            noteLine = Input(y, "N~o~te", step.Note, 200);
            y += 1;
        }
        if (Unknown(step) is { Length: > 0 } unknown)
            Insert(new TStaticText(new(LabelX, y + 1, w - 3, y + 2), "Other properties kept as they are (not schema v1): " + string.Join(", ", unknown)));

        Insert(notice);
        Buttons("O~K~", "Chan~g~e kind...", Views.cmYes);
        if (panel.FirstField is { } first) { panel.Select(); first.Select(); }
        else ((TView?)noteMemo ?? noteLine!).Select();
        panel.Refresh();
    }

    private static string[] Unknown(ScenarioDocumentStep step) =>
        step.PropertyNames.Where(p => p is not ("kind" or "note") && !ScenarioSchemaV1.StepFields(step.Kind).Contains(p)).ToArray();

    // Frame row, blank row, panel, blank row, note, (blank + unknown row), then blank, notice (2), buttons (2), frame.
    private static int HeightFor(StepPanel panel, string[] unknown)
    {
        int note = panel.NoteLines > 1 ? panel.NoteLines + 2 : 1;
        return PanelY + panel.size.y + 1 + note + (unknown.Length > 0 ? 2 : 0) + 6;
    }

    public override void HandleEvent(ref TEvent ev)
    {
        // The panel's buttons post commands to the dialog; they belong to the panel whatever is focused.
        if (ev.What == Events.evCommand && panel.OwnsCommand(ev.message.command)) { panel.HandleEvent(ref ev); panel.Refresh(); return; }
        bool tab = ev.What == Events.evKeyDown && (ev.keyDown.keyCode == Keys.kbTab || ev.keyDown.keyCode == Keys.kbShiftTab);
        bool forward = tab && ev.keyDown.keyCode == Keys.kbTab;
        var before = current;
        base.HandleEvent(ref ev);
        // Tab into the panel starts at its first control (Shift+Tab at its last), not where focus was left.
        if (tab && !ReferenceEquals(before, panel) && ReferenceEquals(current, panel)) panel.EnterFrom(forward);
        panel.Refresh();
    }

    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return base.Valid(command);
        var o = original.ToJson();
        if (panel.Apply(o) is { } error) return Refuse(error);
        SetText(o, "note", noteMemo is null ? noteLine!.Data : MemoText(noteMemo));
        Result = ScenarioDocumentStep.FromJson(o);
        return base.Valid(command);
    }

    private static string MemoText(TMemo memo)
    {
        var data = new TMemoData(memo.bufLen);
        memo.GetData(data);
        return new string(data.buffer, 0, (int)data.length);
    }

    /// <summary>Edits <paramref name="step"/>; returns the changed step or null (cancelled). "Change kind" keeps only the note.</summary>
    internal static ScenarioDocumentStep? Edit(TDeskTop desktop, ScenarioDocumentStep step, bool proxy, ScenarioDescriptor? fixture, bool isNew)
    {
        while (true)
        {
            using var dialog = new WorkbenchScenarioStepDialog(desktop, step, proxy, fixture, isNew);
            ushort result = desktop.ExecView(dialog);
            if (result == Views.cmOK) return dialog.Result;
            if (result != Views.cmYes) return null;
            string? kind = WorkbenchStepKindDialog.Choose(desktop, proxy, step.Kind);
            if (kind is null || kind == step.Kind) continue;
            var fresh = new JsonObject { ["kind"] = kind };
            if (step.Note is { Length: > 0 } n) fresh["note"] = n;
            step = ScenarioDocumentStep.FromJson(fresh);
        }
    }
}
