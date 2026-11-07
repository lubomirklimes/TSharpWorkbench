using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

internal sealed class WorkbenchScenariosWindow : TWindow
{
    internal const ushort Context = 0x7d47, CloseScenarios = 0x7d48;
    private readonly AttachedList list;
    private readonly TComboBox groupFilter;
    private AttachedScenario[] library = [];
    private bool loadingGroups;
    internal TComboBox GroupFilter => groupFilter;
    private static string GroupOf(AttachedScenario item) => item.Info.Compiled?.Group ?? item.Info.Document?.Scenario.Group ?? "Ungrouped";
    private readonly Line summary, columns;
    internal Action? Closed { get; set; }
    internal AttachedScenario? Selected => list.Selected;
    internal AttachedScenario[] Items => list.Items;
    internal TListBox Entries => list;
    internal string SummaryText => summary.Current;

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }

    private sealed class AttachedList(TRect bounds, TScrollBar scroll) : WorkbenchListBox(bounds, 1, scroll)
    {
        internal AttachedScenario[] Items = [];
        internal IReadOnlyDictionary<string, string>? StoredIds;
        internal AttachedScenario? Selected => Items.ElementAtOrDefault(focused);
        internal void Load(AttachedScenario[] items, string? keep)
        {
            Items = items; SetRange(items.Length);
            int index = keep is null ? -1 : Array.FindIndex(items, a => WorkbenchPreferences.SamePath(a.Path, keep));
            FocusItemNum(index >= 0 ? index : Math.Min(focused, Math.Max(0, items.Length - 1))); DrawView();
        }
        internal static string Columns(int width, string title, string id, string kind, string status)
        {
            int idWidth = Math.Clamp(width / 4, 13, 32);
            int titleWidth = Math.Max(8, width - idWidth - 37);
            return WorkbenchText.Column(title, titleWidth) + " " + WorkbenchText.Column(id, idWidth) + " " +
                WorkbenchText.Column(kind, 19) + " " + status;
        }
        public override string GetText(int item, int maxChars)
        {
            if (Items.ElementAtOrDefault(item) is not { } scenario) return "";
            string? id = scenario.Info.Id ?? StoredIds?.GetValueOrDefault(scenario.Path);
            string? name = scenario.Info.Document?.Scenario.Name ?? scenario.Info.Compiled?.Name;
            string title = !string.IsNullOrWhiteSpace(name) ? name : id ?? System.IO.Path.GetFileName(scenario.Path);
            string kind = scenario.Info.Compiled is not null ? "White-Box compiled" :
                scenario.Info.Document is null ? "-" : scenario.Info.Proxy ? "Black-Box" : "White-Box";
            return WorkbenchText.Fit(Columns(size.x, title, id ?? "-", kind, scenario.Note is null ? scenario.Status : "Not ready"), maxChars);
        }
    }

    internal WorkbenchScenariosWindow(TDeskTop desktop, TRect? remembered = null) : base(DefaultBounds(desktop, remembered), "Scenario Manager", 2)
    {
        helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        int w = size.x, h = size.y;
        const byte right = Views.gfGrowHiX, bottom = Views.gfGrowLoY | Views.gfGrowHiY;
        summary = new Line(new(3, 2, 48, 3)); Insert(summary);
        groupFilter = new TComboBox(new(58, 2, w - 3, 3), ["All"], false, 100) { Text = "All", growMode = right };
        Insert(groupFilter); Insert(new WindowLabel(new(49, 2, 58, 3), "Gro~u~p", groupFilter));
        groupFilter.SelectionChanged += (_, _) => { if (!loadingGroups) ApplyGroup(); };
        columns = new Line(new(4, 4, w - 3, 5)) { growMode = right }; Insert(columns);
        var scroll = new TScrollBar(new(w - 3, 5, w - 2, h - 6)) { growMode = Views.gfGrowLoX | Views.gfGrowHiX | Views.gfGrowHiY }; Insert(scroll);
        list = new AttachedList(new(3, 5, w - 3, h - 6), scroll) { growMode = Views.gfGrowHiX | Views.gfGrowHiY, ActivationCommand = WorkbenchDialog.OpenScenario }; Insert(list);
        UpdateColumns();

        int x = 3;
        void Button(string text, int width, ushort command, int row)
        {
            Insert(new TButton(new(x, h - row, x + width, h - row + 2), text, command, ButtonConstants.bfNormal) { growMode = bottom });
            x += width + 1;
        }
        Button("~N~ew...", 9, WorkbenchDialog.NewScenario, 5);
        Button("~E~dit...", 10, WorkbenchDialog.OpenScenario, 5);
        Button("Re~m~ove", 10, WorkbenchDialog.RemoveScenario, 5);
        Button("Lin~k~ Test", 13, WorkbenchDialog.CreateScenarioTest, 5);
        Button("~V~alidate", 12, WorkbenchDialog.ValidateScenarios, 5);
        x = 3;
        Button("~A~dd files...", 15, WorkbenchDialog.AddScenarioFiles, 3);
        Button("Add f~o~lder...", 16, WorkbenchDialog.AddScenarioFolder, 3);

        Insert(new TButton(new(w - 11, h - 3, w - 2, h - 1), "~C~lose", CloseScenarios, ButtonConstants.bfNormal)
            { growMode = (byte)(Views.gfGrowLoX | Views.gfGrowHiX | bottom) });
        list.Select();
    }

    // Remembered geometry (user settings) when it still fits the desktop; otherwise centered with a margin, large
    // enough for the button row at the minimum 120x36 grid (desktop 120x34).
    internal static TRect DefaultBounds(TDeskTop desktop, TRect? remembered = null)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        if (remembered is { } r && r.a.x >= 0 && r.a.y >= 0 && r.b.x <= dw && r.b.y <= dh && r.b.x - r.a.x >= Math.Min(114, dw) && r.b.y - r.a.y >= Math.Min(22, dh)) return r;
        int w = Math.Min(dw, Math.Max(114, dw - 8)), h = Math.Min(dh, Math.Max(24, dh - 4));
        int x = (dw - w) / 2, y = (dh - h) / 2;
        return new(x, y, x + w, y + h);
    }
    public override void SizeLimits(ref TPoint min, ref TPoint max) { base.SizeLimits(ref min, ref max); min = new(Math.Min(114, max.x), Math.Min(22, max.y)); }
    public override void ShutDown() { base.ShutDown(); Closed?.Invoke(); }

    public override void ChangeBounds(TRect bounds)
    {
        base.ChangeBounds(bounds);
        if (columns is not null && list is not null) UpdateColumns();
    }

    private void UpdateColumns() => columns.Set(AttachedList.Columns(list.size.x, "Scenario", "Scenario ID", "Type", "Status"));

    internal void Load(AttachedScenario[] items, string? focus = null, IReadOnlyDictionary<string, string>? storedIds = null)
    {
        library = items;
        list.StoredIds = storedIds;
        int ready = items.Count(item => item.Usable && item.Note is null);
        int problems = items.Count(item => item.Problem is not null || item.Note is not null);
        summary.Set($"Scenarios: {items.Length} | Ready: {ready} | Problems: {problems}");
        string previousGroup = groupFilter.Text;
        loadingGroups = true;
        groupFilter.SetItems(new[] { "All" }.Concat(items.Select(GroupOf).Distinct().Order()));
        groupFilter.Text = previousGroup;
        loadingGroups = false;
        ApplyGroup(focus ?? Selected?.Path);
    }

    private void ApplyGroup(string? keep = null)
    {
        int top = list.topItem;
        list.Load(library.Where(item => groupFilter.Text == "All" || GroupOf(item) == groupFilter.Text).ToArray(), keep ?? Selected?.Path);
        list.topItem = Math.Clamp(top, Math.Max(0, list.focused - list.size.y + 1), list.focused);
        list.DrawView();
    }

    internal void Focus(string path)
    {
        int index = Array.FindIndex(list.Items, item => WorkbenchPreferences.SamePath(item.Path, path));
        if (index >= 0) list.FocusItemNum(index);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown)
        {
            ushort command = ev.keyDown.keyCode switch
            {
                Keys.kbEsc => CloseScenarios, Keys.kbIns => WorkbenchDialog.AddScenarioFiles, Keys.kbDel => WorkbenchDialog.RemoveScenario,
                Keys.kbEnter when ReferenceEquals(current, list) && list.Selected is not null => WorkbenchDialog.OpenScenario, _ => (ushort)0
            };
            if (command == CloseScenarios) { ClearEvent(ref ev); Close(); return; }
            if (command != 0)
            {
                ClearEvent(ref ev);
                var post = new TEvent { What = Events.evCommand }; post.message.command = command; PutEvent(ref post);
                return;
            }
        }
        if (ev.What == Events.evCommand && ev.message.command == CloseScenarios) { ClearEvent(ref ev); Close(); return; }
        base.HandleEvent(ref ev);
    }
}

// Static text a dialog replaces while it is open (multi-line).
internal sealed class ScenarioText(TRect bounds) : TStaticText(bounds, "")
{
    internal string Current => Text;
    internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
}

// Scenarios > Add Folder: shows what the chosen folder contains before anything changes. Only documents that declare
// the scenario schema are candidates; other JSON is listed as ignored. Cancel (or Esc) changes nothing.
internal sealed class WorkbenchAddFolderDialog : TDialog
{
    internal const string Title = "Add Scenario Folder";
    private readonly IReadOnlyList<string> attached;
    private readonly TCheckBoxes recursive;
    private readonly ScenarioText summary;
    private readonly ScenarioText? entry;
    private readonly TListBox entries;
    private readonly List<string> full = [];
    private readonly string folder;
    private bool scannedRecursive;
    internal ScenarioFiles.FolderScan Scan { get; private set; }
    internal TCheckBoxes Recursive => recursive;
    internal TListBox Entries => entries;
    internal string SummaryText => summary.Current;

    internal WorkbenchAddFolderDialog(TDeskTop desktop, string folder, IReadOnlyList<string> attached, bool recursiveDefault = false)
        : base(new(0, 0, Math.Min(110, desktop.size.x - 2), Math.Min(28, desktop.size.y)), Title)
    {
        this.folder = folder; this.attached = attached;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y;
        Insert(new TStaticText(new(3, 2, 12, 3), "Folder"));
        Insert(new TStaticText(new(13, 2, w - 3, 3), WorkbenchText.Path(folder, w - 16)));
        recursive = new TCheckBoxes(new(12, 3, 60, 4), new TSItem("~I~nclude subfolders (recursive)", null!)) { value = recursiveDefault ? 1u : 0u };
        Insert(recursive);
        summary = new ScenarioText(new(3, 5, w - 3, 7));
        Insert(summary);
        var scroll = new TScrollBar(new(w - 4, 8, w - 3, h - 7)); Insert(scroll);
        entries = new TListBox(new(3, 8, w - 4, h - 7), 1, scroll); Insert(entries);
        Insert(new TLabel(new(2, 7, 30, 8), "~F~ound JSON files", entries));
        entry = new ScenarioText(new(3, h - 7, w - 3, h - 5)); Insert(entry);
        Insert(new TStaticText(new(3, h - 4, w - 3, h - 3), "Files are attached by reference; nothing is copied, moved or changed."));
        Insert(new TButton(new(w - 34, h - 3, w - 18, h - 1), "~A~ttach", Views.cmOK, ButtonConstants.bfDefault));
        Insert(new TButton(new(w - 17, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        Scan = Rescan();
        recursive.Select();
    }

    private ScenarioFiles.FolderScan Rescan()
    {
        scannedRecursive = recursive.value == 1;
        var scan = ScenarioFiles.ScanFolder(folder, scannedRecursive, attached);
        Scan = scan;
        int total = scan.Add.Length + scan.AlreadyAttached.Length + scan.Rejected.Length + scan.Ignored.Length;
        summary.Set($"{total} JSON file{(total == 1 ? "" : "s")} {(scan.Recursive ? "in this folder and its subfolders" : "in this folder (subfolders not included)")}"
            + (scan.Truncated ? $"; only the first {ScenarioFiles.FolderLimit} are listed" : "") + ".\n"
            + $"{scan.Add.Length} valid to attach   {scan.AlreadyAttached.Length} already attached   {scan.Rejected.Length} rejected   {scan.Ignored.Length} ignored (not scenario documents)");
        // One row per file, cut with an ellipsis; the focused row's full text is shown below the list.
        full.Clear();
        string Name(ScenarioFileInfo i) => System.IO.Path.GetRelativePath(folder, i.Path);
        foreach (var i in scan.Add) full.Add($"Attach    {i.Id,-36} {Name(i)}");
        foreach (var i in scan.AlreadyAttached) full.Add($"Attached  {i.Id ?? "-",-36} {Name(i)}");
        foreach (var (i, reason) in scan.Rejected) full.Add($"Rejected  {Name(i)}: {reason}");
        foreach (var i in scan.Ignored) full.Add($"Ignored   {Name(i)}: {i.Message}");
        if (total == 0) full.Add("No .json files found." + (scan.Recursive ? "" : " Include subfolders to search deeper."));
        var lines = new TStringCollection();
        foreach (string line in full) lines.Insert(WorkbenchText.Fit(line, entries.size.x - 1));
        entries.NewList(lines);
        shownEntry = -1; ShowEntry();
        return scan;
    }
    private int shownEntry = -1;
    private void ShowEntry()
    {
        if (entry is null || entries.focused == shownEntry) return;
        shownEntry = entries.focused;
        entry.Set(string.Join('\n', WorkbenchText.Wrap(full.ElementAtOrDefault(entries.focused) ?? "", entry.size.x).Take(2)));
    }

    public override void HandleEvent(ref TEvent ev)
    {
        base.HandleEvent(ref ev);
        if (entry is null) return;   // broadcasts arrive while the constructor inserts controls
        if ((recursive.value == 1) != scannedRecursive) Rescan();
        ShowEntry();
    }

    public override bool Valid(ushort command)
    {
        if (command == Views.cmOK && Scan.Add.Length == 0)
        {
            summary.Set("Nothing to attach: no new valid scenario documents in this selection."
                + (Scan.Recursive ? "" : " Include subfolders (Alt+I) to search deeper.") + "\nChoose Cancel to close.");
            return false;
        }
        return base.Valid(command);
    }
}

// Scenarios > New Scenario: the minimal schema-v1 document of one kind. Fixture-bound documents can name only fixtures
// the current target advertises (known after Refresh Catalog), so no fixture, action or expectation name is invented.
internal sealed class WorkbenchNewScenarioDialog : TDialog
{
    internal const string Title = "New Scenario";
    private readonly TRadioButtons kind;
    private readonly TInputLine id, name, group;
    private readonly TListBox fixtureList;
    private readonly ScenarioText fixtureNote, notice;
    private readonly ScenarioDescriptor[] fixtures;
    private readonly bool? projectProxy;
    private readonly HashSet<string> usedIds;
    private uint shownKind = uint.MaxValue;
    private int shownFixture = -1;
    internal TRadioButtons Kind => kind;
    internal TInputLine IdField => id;
    internal TInputLine TitleField => name;
    internal TListBox Fixtures => fixtureList;
    internal string NoticeText => notice.Current;
    internal bool Proxy => kind.value == 0;
    internal string ScenarioId => id.Data.Trim();
    internal string ScenarioTitle => name.Data.Trim();
    internal string Group => group.Data.Trim().Length == 0 ? "external" : group.Data.Trim();
    internal ScenarioDescriptor? Fixture => Proxy ? null : fixtures.ElementAtOrDefault(fixtureList.focused);

    internal WorkbenchNewScenarioDialog(TDeskTop desktop, ScenarioDescriptor[] fixtures, bool? projectProxy, IEnumerable<string> usedIds)
        : base(new(0, 0, Math.Min(96, desktop.size.x - 2), Math.Min(24, desktop.size.y)), Title)
    {
        this.fixtures = fixtures; this.projectProxy = projectProxy; this.usedIds = new(usedIds, StringComparer.Ordinal);
        options |= Views.ofCentered; helpCtx = WorkbenchScenariosWindow.Context;
        int w = size.x, h = size.y;
        kind = new TRadioButtons(new(17, 2, w - 3, 4), new TSItem("~B~lack-Box Mode: an ordinary application (no fixture, no recompilation)",
            new TSItem("~W~hite-Box Mode: a diagnostic fixture advertised by the target", null!)));
        kind.value = projectProxy == false ? 1u : 0u;
        Insert(kind); Insert(new TLabel(new(2, 2, 16, 3), "~K~ind", kind));
        id = new TInputLine(new(17, 5, w - 3, 6), 128); Insert(id); Insert(new TLabel(new(2, 5, 16, 6), "Scenario ~I~D", id));
        name = new TInputLine(new(17, 6, w - 3, 7), 200); Insert(name); Insert(new TLabel(new(2, 6, 16, 7), "~T~itle", name));
        group = new TInputLine(new(17, 7, 60, 8), 128); group.SetData("external"); Insert(group); Insert(new TLabel(new(2, 7, 16, 8), "~G~roup", group));
        var scroll = new TScrollBar(new(w - 4, 9, w - 3, h - 9)); Insert(scroll);
        fixtureList = new TListBox(new(17, 9, w - 4, h - 9), 1, scroll); Insert(fixtureList);
        Insert(new TLabel(new(2, 9, 16, 10), "Fi~x~ture", fixtureList));
        var items = new TStringCollection();
        foreach (var f in fixtures) items.Insert(f.Id + (f.IsFixtureOnly ? "   (fixture-only, revision " + f.Contract!.Revision + ")" : ""));
        if (fixtures.Length == 0) items.Insert("No fixtures known: refresh Scenario Manager (F2) with the diagnostic target selected.");
        fixtureList.NewList(items);
        fixtureNote = new ScenarioText(new(17, h - 9, w - 3, h - 6)); Insert(fixtureNote);
        notice = new ScenarioText(new(2, h - 5, w - 2, h - 4)); Insert(notice);
        Insert(new TButton(new(w - 34, h - 3, w - 18, h - 1), "~C~reate...", Views.cmOK, ButtonConstants.bfDefault));
        Insert(new TButton(new(w - 17, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        id.Select();
        Refresh();
    }

    private void Refresh()
    {
        if (fixtureNote is null || fixtureList is null) return;   // broadcasts arrive while the constructor inserts controls
        if (kind.value == shownKind && fixtureList.focused == shownFixture) return;
        shownKind = kind.value; shownFixture = fixtureList.focused;
        fixtureList.SetState(Views.sfDisabled, Proxy || fixtures.Length == 0);
        string text = Proxy ? "Black-Box Mode: steps are keys, text, mouse input and checkpoint assertions against the real application."
            : Fixture is { } f ? $"Checkpoint expectations: {(f.Contract is { } c ? string.Join(", ", c.Checkpoints) : "any (legacy fixture, unbound checkpoints)")}"
                + $"\nActions: {(f.Adapters!.Actions.Length == 0 ? "none" : string.Join(", ", f.Adapters.Actions))}   Points: {(f.Adapters.Points.Length == 0 ? "none" : string.Join(", ", f.Adapters.Points))}"
            : "No fixture is known for this target. A fixture-bound document must name a fixture the target advertises.";
        fixtureNote.Set(text);
    }

    public override void HandleEvent(ref TEvent ev) { base.HandleEvent(ref ev); Refresh(); }

    private bool Refuse(string message) { notice.Set(message); return false; }
    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return base.Valid(command);
        string value = ScenarioId;
        if (value.Length == 0) return Refuse("Enter a scenario ID, for example myapp/smoke.");
        if (value.Length > 128 || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) return Refuse("The scenario ID must be at most 128 characters, without spaces or control characters.");
        if (usedIds.Contains(value)) return Refuse($"Scenario ID '{value}' is already used in this project; choose another.");
        if (ScenarioTitle.Length == 0) return Refuse("Enter a title.");
        if (projectProxy is { } p && p != Proxy)
            return Refuse(p ? "This project uses Black-Box Mode scenarios; fixture-bound documents need a separate project." : "This project uses fixture-bound scenarios; Black-Box Mode documents need a separate project.");
        if (!Proxy && Fixture is null) return Refuse("No fixture is known: refresh Scenario Manager (F2) with the diagnostic target, then choose its fixture.");
        if (!Proxy && Fixture!.Id == value) return Refuse("The scenario ID must differ from the fixture ID.");
        return base.Valid(command);
    }
}
