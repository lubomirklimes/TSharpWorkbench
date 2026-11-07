using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Internal, catalog-derived hierarchy. TListBox provides focus, mouse hit testing and scrolling.
internal sealed class GroupTree(TRect bounds, TScrollBar scroll) : TListBox(bounds, 1, scroll)
{
    private string[] paths = [];
    private readonly List<string?> visible = [null];
    private ScenarioDescriptor[] catalog = [];
    internal HashSet<string> Collapsed { get; } = new(StringComparer.Ordinal);
    internal string? SelectedGroup => visible.ElementAtOrDefault(focused);
    internal void Load(ScenarioDescriptor[] entries, string? selected = null)
    {
        catalog = entries;
        paths = entries.SelectMany(s => s.Group.Split('/').Select((_, i) => string.Join('/', s.Group.Split('/').Take(i + 1))))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Rebuild(selected);
    }
    private bool HasChildren(string path) => paths.Any(p => p.StartsWith(path + "/", StringComparison.Ordinal));
    private void Rebuild(string? selected)
    {
        visible.Clear(); visible.Add(null);
        visible.AddRange(paths.Where(p => !Collapsed.Any(c => p.StartsWith(c + "/", StringComparison.Ordinal))));
        SetRange(visible.Count); FocusItemNum(Math.Max(0, visible.IndexOf(selected))); DrawView();
    }
    public override string GetText(int item, int maxChars)
    {
        string? p = visible.ElementAtOrDefault(item);
        string text = p is null ? $"All scenarios ({catalog.Length})" : new string(' ', p.Count(c => c == '/') * 2)
            + (HasChildren(p) ? Collapsed.Contains(p) ? "+ " : "- " : "  ") + p.Split('/')[^1]
            + $" ({catalog.Count(s => ScenarioSelection.InGroup(s.Group, p))})";
        return text[..Math.Min(text.Length, maxChars)];
    }
    internal void Toggle()
    {
        if (SelectedGroup is not { } p || !HasChildren(p)) return;
        if (!Collapsed.Remove(p)) Collapsed.Add(p);
        Rebuild(p);
    }
    public override void SelectItem(int item) { FocusItemNum(item); Toggle(); }
    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode is Keys.kbLeft or Keys.kbRight or Keys.kbEnter)
        {
            string? p = SelectedGroup;
            if (ev.keyDown.keyCode == Keys.kbEnter) Toggle();
            else if (ev.keyDown.keyCode == Keys.kbRight)
            {
                if (p is null) { if (range > 1) FocusItemNum(1); }
                else if (HasChildren(p)) { if (Collapsed.Remove(p)) Rebuild(p); else FocusItemNum(Math.Min(range - 1, focused + 1)); }
            }
            else if (p is not null)
            {
                if (HasChildren(p) && !Collapsed.Contains(p)) { Collapsed.Add(p); Rebuild(p); }
                else { int slash = p.LastIndexOf('/'); FocusItemNum(Math.Max(0, visible.IndexOf(slash < 0 ? null : p[..slash]))); }
            }
            ClearEvent(ref ev); return;
        }
        base.HandleEvent(ref ev);
    }
}
