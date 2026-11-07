using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

/// <summary>
/// Chooses an assertion kind and edits one assertion. The fields of each kind are an <see cref="AssertionPanel"/>
/// (Dialogs/Editors/Assertions) that reports the size it needs; the dialog is sized around it:
/// panel, blank row, notice (2 rows), buttons.
/// </summary>
internal sealed class WorkbenchAssertionDialog : EditorDialog
{
    private const int PanelX = 2, PanelY = 2;
    private readonly JsonObject original;
    private readonly AssertionPanel panel;
    internal JsonObject? Result { get; private set; }
    internal AssertionPanel Panel => panel;
    internal TInputLine IdField => panel.IdField;
    internal IReadOnlyDictionary<string, TInputLine> Fields => panel.Fields;
    internal IReadOnlyDictionary<string, TInputLine[]> Selectors => panel.Selectors;
    internal string NoticeText => notice.Current;

    // Frame, blank row, panel, blank row, notice (2), buttons (2), frame.
    private WorkbenchAssertionDialog(TDeskTop desktop, JsonObject assertion, AssertionPanel panel, bool isNew)
        : base(Math.Min(desktop.size.x - 2, panel.size.x + 2 * PanelX), Math.Min(PanelY + panel.size.y + 1 + 6, desktop.size.y),
               (isNew ? "New assertion: " : "Edit assertion: ") + panel.Kind)
    {
        original = assertion;
        this.panel = panel;
        panel.ChangeBounds(new TRect(PanelX, PanelY, PanelX + panel.size.x, PanelY + panel.size.y));
        Insert(panel);
        Insert(notice);
        Buttons();
        panel.Select();
        panel.IdField.Select();
    }

    public override void HandleEvent(ref TEvent ev)
    {
        bool tab = ev.What == Events.evKeyDown && (ev.keyDown.keyCode == Keys.kbTab || ev.keyDown.keyCode == Keys.kbShiftTab);
        bool forward = tab && ev.keyDown.keyCode == Keys.kbTab;
        var before = current;
        base.HandleEvent(ref ev);
        // Tab into the panel starts at its first control (Shift+Tab at its last).
        if (tab && !ReferenceEquals(before, panel) && ReferenceEquals(current, panel)) panel.EnterFrom(forward);
    }

    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return base.Valid(command);
        var o = (JsonObject)original.DeepClone();
        if (panel.Save(o) is { } error) return Refuse(error);
        Result = o;
        return base.Valid(command);
    }

    internal static JsonObject? Edit(TDeskTop desktop, JsonObject assertion, bool isNew)
    {
        if (!ScenarioSchemaV1.AssertionKinds.Contains(Text(assertion["kind"]) ?? "")) return null;
        var panel = AssertionPanel.Create(assertion);
        using var dialog = new WorkbenchAssertionDialog(desktop, assertion, panel, isNew);
        return desktop.ExecView(dialog) == Views.cmOK ? dialog.Result : null;
    }

    private static string Describe(string kind) => kind switch
    {
        "screen-contains" => "The screen contains the text somewhere.", "text-at" => "The text appears at cell (x,y).", "attribute-at" => "Cells from (x,y) have the attribute value.",
        "modal" => "A modal view matches the selector (or, with no view, no modal is open).", "view-absent" => "No view matches the selector.",
        "menu-depth" => "Exactly this many menu levels are open.", "status-hint" => "The status line hint contains the text.",
        "layout-audit" => "Audits geometry and text fit of a scope (ux/strict: UX heuristics too).", _ => "Fields follow schema v1 for " + kind + "."
    };

    internal static string? ChooseKind(TDeskTop desktop, string? current)
    {
        using var dialog = new AssertionKindDialog(desktop, current);
        return desktop.ExecView(dialog) == Views.cmOK ? dialog.Selected : null;
    }

    private sealed class AssertionKindDialog : EditorDialog
    {
        private readonly TListBox box;
        internal string Selected => ScenarioSchemaV1.AssertionKinds[Math.Clamp(box.focused, 0, ScenarioSchemaV1.AssertionKinds.Length - 1)];
        internal AssertionKindDialog(TDeskTop desktop, string? current) : base(Math.Min(76, desktop.size.x - 2), Math.Min(30, desktop.size.y), "Add Assertion")
        {
            box = Picker(3, 2, size.x - 4, size.y - 6, "Assertion ~k~ind", ScenarioSchemaV1.AssertionKinds.Select(k => k.PadRight(18) + Describe(k)));
            // Double-click on a kind chooses it (same as Enter / Choose).
            if (box is WorkbenchListBox list) list.ActivationCommand = Views.cmOK;
            if (current is not null && Array.IndexOf(ScenarioSchemaV1.AssertionKinds, current) is >= 0 and var i) box.FocusItemNum(i);
            Insert(notice); notice.Set("Assertions are evaluated on the checkpoint's capture (logical cells, inspection).");
            Buttons("~C~hoose"); box.Select();
        }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter && ReferenceEquals(current, box)) { ClearEvent(ref ev); EndModal(Views.cmOK); return; }
            base.HandleEvent(ref ev);
        }
    }
}
