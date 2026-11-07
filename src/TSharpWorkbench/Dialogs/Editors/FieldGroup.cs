using TSharpVision;
using TSharpVision.Constants;

/// <summary>
/// A plain Turbo Vision group of edit controls inserted into an editor dialog. The only addition to <see cref="TGroup"/>
/// is Tab handling: Tab / Shift+Tab walk the group's controls in reading order (top to bottom, left to right),
/// nested groups included, and leave the group to its owner at either end.
/// </summary>
internal abstract class FieldGroup : TGroupBox
{
    // A borderless TGroupBox (not a bare TGroup): it paints its background in the dialog's colors,
    // so cells between the controls are not left black.
    protected FieldGroup(TRect bounds) : base(bounds, "")
    {
        ShowBorder = false;
        options |= Views.ofSelectable;
    }

    private List<TView> Tabs()
    {
        var list = new List<TView>();
        for (TView? v = last?.Next; v is not null; v = v.Next)
        {
            if ((v.options & Views.ofSelectable) != 0 && (v.state & (Views.sfVisible | Views.sfDisabled)) == Views.sfVisible) list.Add(v);
            if (ReferenceEquals(v, last)) break;
        }
        return list.OrderBy(v => v.origin.y).ThenBy(v => v.origin.x).ToList();
    }

    /// <summary>Moves focus to the next/previous control; false when the group's end is reached.</summary>
    internal bool Move(bool forward)
    {
        if (current is FieldGroup inner && inner.Move(forward)) return true;
        var tabs = Tabs();
        int i = current is null ? -1 : tabs.IndexOf(current);
        int j = i + (forward ? 1 : -1);
        if (i < 0 || j < 0 || j >= tabs.Count) return false;
        tabs[j].Select();
        if (tabs[j] is FieldGroup group) group.EnterFrom(forward);
        return true;
    }

    /// <summary>Focus arrived from outside: select the first (or, backwards, the last) control.</summary>
    internal void EnterFrom(bool forward)
    {
        var tabs = Tabs();
        if (tabs.Count == 0) return;
        var view = forward ? tabs[0] : tabs[^1];
        view.Select();
        if (view is FieldGroup group) group.EnterFrom(forward);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && (ev.keyDown.keyCode == Keys.kbTab || ev.keyDown.keyCode == Keys.kbShiftTab) && GetState(Views.sfFocused))
        {
            if (Move(ev.keyDown.keyCode == Keys.kbTab)) ClearEvent(ref ev);
            return; // at the group's end the owner moves focus on
        }
        base.HandleEvent(ref ev);
    }
}
