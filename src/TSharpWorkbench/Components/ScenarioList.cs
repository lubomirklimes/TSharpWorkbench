using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Marks belong to stable IDs, independently of focus, scrolling and catalog order.
// Rows are columns: mark, scenario name, ID, supported grids, last result, note. Cut text ends with an ellipsis;
// the Details pane shows the full text.
internal sealed class ScenarioList : TListBox
{
    private ScenarioDescriptor[] catalog = [];
    private ScenarioDescriptor[] visible = [];
    private readonly Dictionary<string, string> focusByFilter = new(StringComparer.Ordinal);
    private string filterKey = "";
    internal Action? Changed { get; set; }
    internal Func<ScenarioDescriptor, string>? LastResult { get; set; }
    internal Func<ScenarioDescriptor, string>? Note { get; set; }
    internal ScenarioDescriptor? Current => visible.ElementAtOrDefault(focused);
    private readonly HashSet<string> marked = new(StringComparer.Ordinal);
    internal IReadOnlyList<string> Strings => catalog.Select(s => s.Name).ToArray();
    internal IReadOnlyCollection<string> MarkedIds => marked;
    internal int sel { get => focused; set => FocusItemNum(value); }
    internal ScenarioList(TRect bounds, TScrollBar scroll) : base(bounds, 1, scroll) { }
    internal void LoadCatalog(ScenarioDescriptor[] entries)
    {
        // Loading is an explicit identity boundary, including a different target
        // whose catalog happens to reuse some IDs. A raw catalog supplied by mistake never makes a bare fixture-only
        // registration listable or markable.
        catalog = ScenarioSelection.Runnable(entries); visible = catalog; marked.Clear(); focusByFilter.Clear(); filterKey = ""; SetRange(visible.Length);
        if (catalog.Length > 0) FocusItemNum(0);
        DrawView();
    }
    internal void Filter(string? group, string search)
    {
        string? id = Current?.Id;
        if (id is not null) focusByFilter[filterKey] = id;
        filterKey = (group ?? "") + "\n" + search;
        if (focusByFilter.TryGetValue(filterKey, out string? remembered)) id = remembered;
        visible = catalog.Where(s => (group is null || ScenarioSelection.InGroup(s.Group, group))
            && (s.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        SetRange(visible.Length);
        FocusItemNum(Math.Max(0, Array.FindIndex(visible, s => s.Id == id)));
        DrawView();
    }
    internal void ClearMarks() { marked.Clear(); DrawView(); Changed?.Invoke(); }
    internal void MarkGroup(string group)
    {
        foreach (var s in catalog.Where(s => ScenarioSelection.InGroup(s.Group, group) && s.ExpectedResult == ScenarioOutcomes.Passed)) marked.Add(s.Id);
        DrawView(); Changed?.Invoke();
    }
    internal void RestoreMarks(IEnumerable<string> ids)
    { foreach (string id in ids.Where(id => catalog.Any(s => s.Id == id))) marked.Add(id); DrawView(); Changed?.Invoke(); }
    internal void Toggle(string id)
    {
        if (!catalog.Any(s => s.Id == id)) throw new ArgumentException("Unknown scenario ID.");
        if (!marked.Remove(id)) marked.Add(id);
        // Draw only the visible list; never rebuild the collection or reset scroll/focus.
        DrawView();
        Changed?.Invoke();
    }

    private const int Mark = 4, Grids = 15, Last = 10;
    private static (int Name, int Id, int Note) Widths(int width)
    {
        int note = Math.Clamp(width / 8, 13, 24);
        int rest = Math.Max(10, width - Mark - Grids - Last - note - 3);
        int name = rest * 55 / 100;
        return (name, rest - name, note);
    }
    internal string Header(int width)
    {
        var (name, id, note) = Widths(width);
        return WorkbenchText.Fit(new string(' ', Mark) + WorkbenchText.Column("Scenario", name) + " " + WorkbenchText.Column("ID", id) + " "
            + WorkbenchText.Column("Grids", Grids) + WorkbenchText.Column("Last", Last) + WorkbenchText.Column("Note", note), width);
    }
    public override string GetText(int item, int maxChars)
    {
        if (item < 0 || item >= visible.Length) return string.Empty;
        var s = visible[item];
        var (name, id, note) = Widths(maxChars);
        string text = (marked.Contains(s.Id) ? "[x] " : "[ ] ") + WorkbenchText.Column(s.Name, name) + " " + WorkbenchText.Column(s.Id, id) + " "
            + WorkbenchText.Column(s.Grid.Describe(), Grids) + WorkbenchText.Column(LastResult?.Invoke(s) ?? "-", Last) + WorkbenchText.Fit(Note?.Invoke(s) ?? "", note);
        return WorkbenchText.Fit(text.TrimEnd(), maxChars);
    }
    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && (state & Views.sfFocused) != 0 && Current is { } current)
        {
            if (ev.keyDown.keyCode == Keys.kbSpace || ev.keyDown.text == " ") { Toggle(current.Id); ClearEvent(ref ev); return; }
            // Ins marks (or unmarks) and moves to the next row, for quick selection of consecutive scenarios.
            if (ev.keyDown.keyCode == Keys.kbIns) { Toggle(current.Id); if (focused < range - 1) FocusItemNum(focused + 1); ClearEvent(ref ev); return; }
        }
        base.HandleEvent(ref ev);
    }
    public override void SelectItem(int item) { if (visible.ElementAtOrDefault(item) is { } s) Toggle(s.Id); }
}
