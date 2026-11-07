using TSharpVision;
using TSharpVision.Constants;

/// <summary>
/// A list of names with a scroll bar; Enter (or double-click) copies the focused name into a target input line
/// and moves focus there. Inserted into step panels the classic way: <c>Insert(new NamePicker(...))</c>.
/// </summary>
internal sealed class NamePicker : FieldGroup
{
    private readonly WorkbenchListBox list;
    private readonly string[] names;
    private readonly TInputLine target;

    internal TListBox List => list;

    /// <param name="bounds">Includes the caption row (row 0); the list fills the rows below it.</param>
    internal NamePicker(TRect bounds, string caption, string[] names, TInputLine target, string? selected) : base(bounds)
    {
        this.names = names;
        this.target = target;
        int w = bounds.b.x - bounds.a.x, h = bounds.b.y - bounds.a.y;

        var scroll = new TScrollBar(new TRect(w - 1, 1, w, h));
        Insert(scroll);
        list = new WorkbenchListBox(new TRect(0, 1, w - 1, h), 1, scroll);
        var items = new TStringCollection();
        foreach (string name in names) items.Insert(name);
        list.NewList(items);
        if (selected is not null && Array.IndexOf(names, selected) is >= 0 and var i) list.FocusItemNum(i);
        Insert(list);
        Insert(new TLabel(new TRect(0, 0, w, 1), caption, list));
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter && GetState(Views.sfFocused))
        {
            target.SetData(names.ElementAtOrDefault(list.focused) ?? "");
            target.Select();
            ClearEvent(ref ev);
            return;
        }
        base.HandleEvent(ref ev);
    }
}
