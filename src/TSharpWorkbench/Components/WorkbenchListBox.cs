using TSharpVision.Constants;

// Shared pointer routing for Workbench lists: hit-test the clicked row before activation.
internal class WorkbenchListBox(TRect bounds, int columns, TScrollBar? scroll) : TListBox(bounds, (ushort)columns, scroll!)
{
    internal ushort ActivationCommand { get; set; }
    internal bool ActivateOnEnter { get; set; }
    protected virtual bool IsToggleCell(int x) => false;
    protected virtual void ToggleItem(int index) { }

    public override void SelectItem(int item)
    {
        if (ActivationCommand == 0) { base.SelectItem(item); return; }
        PutEvent(Events.evCommand, ActivationCommand, null);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evMouseDown && (ev.mouse.buttons & Events.mbLeftButton) != 0)
        {
            var at = MakeLocal(ev.mouse.where);
            int index = topItem + at.y + size.y * (at.x / Math.Max(1, size.x / numCols + 1));
            bool hit = at.x >= 0 && at.x < size.x && at.y >= 0 && at.y < size.y && index >= 0 && index < range;
            if (hit && (IsToggleCell(at.x) || ev.mouse.doubleClick))
            {
                Select(); FocusItemNum(index);
                bool toggle = IsToggleCell(at.x), twice = ev.mouse.doubleClick;
                ClearEvent(ref ev);
                if (toggle) { if (!twice) ToggleItem(index); }
                else SelectItem(index);
                DrawView();
                return;
            }
            if (!hit && ev.mouse.doubleClick) { ClearEvent(ref ev); return; }
        }
        if (ActivateOnEnter && ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter && range > 0)
        { ClearEvent(ref ev); SelectItem(focused); return; }
        base.HandleEvent(ref ev);
    }
}

internal sealed class WorkbenchChoiceList<T>(TRect bounds, TScrollBar scroll, T[] choices, HashSet<string> marked,
    Func<T, string> id, Func<T, string> description, Action? marksChanged = null, Action? activate = null)
    : WorkbenchListBox(bounds, 1, scroll)
{
    internal void Reload(T[] items) { choices = items; SetRange(items.Length); DrawView(); }
    public override string GetText(int item, int maxChars) => choices.ElementAtOrDefault(item) is { } value
        ? WorkbenchText.Fit((marked.Contains(id(value)) ? "[X] " : "[ ] ") + description(value), maxChars) : "";
    protected override bool IsToggleCell(int x) => activate is null || x is >= 1 and <= 3;
    protected override void ToggleItem(int index)
    {
        if (choices.ElementAtOrDefault(index) is not { } item) return;
        string key = id(item);
        if (!marked.Add(key)) marked.Remove(key);
        marksChanged?.Invoke();
    }
    public override void SelectItem(int item)
    {
        if (activate is not null) activate(); else base.SelectItem(item);
    }
    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbSpace && focused < range)
        { ToggleItem(focused); DrawView(); ClearEvent(ref ev); return; }
        base.HandleEvent(ref ev);
    }
}

internal sealed class WorkbenchReadOnlyField : TInputLine
{
    internal WorkbenchReadOnlyField(TRect bounds) : base(bounds, 1024)
    { options &= unchecked((ushort)~(Views.ofSelectable | Views.ofFirstClick)); eventMask = 0; }
    public override void HandleEvent(ref TEvent ev) { }
}

internal sealed class WorkbenchReadOnlyChecks : TCheckBoxes
{
    internal WorkbenchReadOnlyChecks(TRect bounds, TSItem items) : base(bounds, items)
    { options &= unchecked((ushort)~(Views.ofSelectable | Views.ofFirstClick)); eventMask = 0; }
    public override void Press(int item) { }
    public override void HandleEvent(ref TEvent ev) { }
}
