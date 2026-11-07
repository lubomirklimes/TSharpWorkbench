using TSharpVision;
using TSharpVision.Constants;

internal sealed class WorkbenchStartWindow : TDialog
{
    internal const ushort Context = 0x7d88, OpenRecent = 0x7d98, OpenOther = 0x7d99, CreateNew = 0x7d9a;
    internal sealed record RecentEntry(string Path, string Name, bool Available);
    internal sealed class RecentProject(string path) : IInfo { internal string Path { get; } = path; }
    private readonly RecentList list;
    private readonly TButton open;
    internal string? RecentPath { get; private set; }
    internal RecentEntry[] Entries => list.Items;
    internal TListBox Recent => list;

    private sealed class RecentList(TRect bounds, TScrollBar scroll) : TListBox(bounds, 1, scroll)
    {
        internal RecentEntry[] Items = [];
        internal void Load(RecentEntry[] items) { Items = items; SetRange(Math.Max(1, items.Length)); FocusItemNum(0); DrawView(); }
        public override string GetText(int item, int maxChars)
        {
            if (Items.Length == 0) return item == 0 ? "No recent projects" : "";
            if (Items.ElementAtOrDefault(item) is not { } entry) return "";
            int nameWidth = Math.Min(34, Math.Max(10, maxChars / 2 - 2));
            string name = entry.Available ? entry.Name : "(missing) " + System.IO.Path.GetFileName(entry.Path);
            return WorkbenchText.Fit(WorkbenchText.Column(name, nameWidth) + " " + WorkbenchText.Path(entry.Path, maxChars - nameWidth - 2), maxChars);
        }
    }

    internal const string Caption = "Start Page";
    // The list is as tall as the recent list (WorkbenchPreferences keeps at most eight), at least four rows.
    private static int ListRows(IEnumerable<string> recent) => Math.Clamp(recent.Count(), 4, 8);
    // The window is taller instead of the list shorter. Rows: frame, one empty row, the three identity rows,
    // Gap empty rows (at least one after "Version ..."), "Recent projects", the list, one empty row, the buttons (two rows
    // with their shadow), frame. On a desktop too short for that the extra rows go first (top margin, then the second gap
    // row), then list rows (never fewer than two); the row after "Version ..." always stays empty.
    internal const int IdentityTop = 2, Gap = 2;
    internal static (int Margin, int Gap, int List) Rows(int desktopHeight, int recentRows)
    {
        int margin = 1, gap = Gap, list = recentRows;
        int Height() => margin + 3 + gap + 1 + list + 1 + 2 + 2;
        if (Height() > desktopHeight) margin = 0;
        if (Height() > desktopHeight) gap = 1;
        while (Height() > desktopHeight && list > 2) list--;
        return (margin, gap, list);
    }
    internal static int Height(int desktopHeight, int recentRows) { var (m, g, l) = Rows(desktopHeight, recentRows); return m + 3 + g + 1 + l + 1 + 2 + 2; }
    internal WorkbenchStartWindow(TDeskTop desktop, IEnumerable<string> recent)
        : base(Centered(desktop, Math.Clamp(desktop.size.x * 2 / 3, 80, 100), Height(desktop.size.y, ListRows(recent))), Caption)
    {
        helpCtx = Context;
        int w = size.x, h = size.y;
        var (margin, gap, _) = Rows(desktop.size.y, ListRows(recent));
        int identity = 1 + margin;
        Insert(new TStaticText(new(3, identity, w - 3, identity + 3), string.Join('\n', Identity.Select(line => Centre(line, w - 6)))));
        int top = identity + 3 + gap + 1, bottom = Math.Max(top + 1, h - 4);
        var scroll = new TScrollBar(new(w - 4, top, w - 3, bottom)); Insert(scroll);
        list = new RecentList(new(3, top, w - 4, bottom), scroll); Insert(list);
        Insert(new TLabel(new(2, top - 1, 20, top), "~R~ecent projects", list));
        open = new TButton(new(3, h - 3, 15, h - 1), "~O~pen", OpenRecent, ButtonConstants.bfDefault); Insert(open);
        Insert(new TButton(new(16, h - 3, 37, h - 1), "Open pro~j~ect...", OpenOther, ButtonConstants.bfNormal));
        Insert(new TButton(new(38, h - 3, 57, h - 1), "~N~ew project...", CreateNew, ButtonConstants.bfNormal));
        Reload(recent);
    }
    // Identity: product, purpose and the Workbench assembly version; author and copyright are in About.
    internal static string[] Identity => [WorkbenchHelp.ProductName, WorkbenchHelp.Tagline, "Version " + WorkbenchHelp.WorkbenchVersion];
    private static string Centre(string text, int width) => new string(' ', Math.Max(0, (width - text.Length) / 2)) + text;

    private static TRect Centered(TDeskTop desktop, int width, int height)
    {
        int w = Math.Min(width, desktop.size.x), h = Math.Min(height, desktop.size.y);
        int x = Math.Max(0, (desktop.size.x - w) / 2), y = Math.Max(0, (desktop.size.y - h) / 2);
        return new(x, y, x + w, y + h);
    }

    private void Reload(IEnumerable<string> recent)
    {
        list.Load(recent.Where(p => Path.GetExtension(p).Equals(".tsw", StringComparison.OrdinalIgnoreCase)).Select(Describe).ToArray());
        // TButton contract: availability is the command's, so Open stays disabled after cmCommandSetChanged.
        if (list.Items.Length == 0) DisableCommand(OpenRecent); else EnableCommand(OpenRecent);
        open.SetState(Views.sfDisabled, list.Items.Length == 0); open.DrawView();
        (list.Items.Length == 0 ? (TView)FirstButton(OpenOther) : list).Select();
    }
    private TView FirstButton(ushort command)
    {
        TView? found = null;
        ForEachView(view => { if (found is null && view is TButton button && button.Command == command) found = view; });
        return found ?? list;
    }

    // Reads only the project's display name; a missing or unreadable file is shown and reported when chosen.
    private static RecentEntry Describe(string path)
    {
        if (!File.Exists(path)) return new(path, System.IO.Path.GetFileNameWithoutExtension(path), false);
        return new(path, System.IO.Path.GetFileNameWithoutExtension(path), true);
    }

    public override void ShutDown() { EnableCommand(OpenRecent); base.ShutDown(); }

    public override void HandleEvent(ref TEvent ev)
    {
        ushort command = ev.What switch
        {
            Events.evKeyDown => ev.keyDown.keyCode switch
            {
                Keys.kbEnter when current == list => OpenRecent,
                Keys.kbEsc => Views.cmCancel,
                Keys.kbCtrlO => WorkbenchApp.OpenProject,
                Keys.kbCtrlN => WorkbenchApp.NewProject,
                _ => (ushort)0
            },
            Events.evCommand => ev.message.command,
            Events.evBroadcast when ev.message.command == Views.cmListItemSelected &&
                ReferenceEquals(ev.message.infoPtr, list) => OpenRecent,
            _ => (ushort)0
        };
        switch (command)
        {
            case OpenRecent:
                if (list.Items.ElementAtOrDefault(list.focused) is { } entry)
                {
                    RecentPath = entry.Path;
                    EndModal(OpenRecent);
                }
                break;
            case OpenOther:
            case WorkbenchApp.OpenProject:
                EndModal(WorkbenchApp.OpenProject);
                break;
            case CreateNew:
            case WorkbenchApp.NewProject:
                EndModal(WorkbenchApp.NewProject);
                break;
            case Views.cmCancel:
            case Views.cmQuit:
                EndModal(command);
                break;
            default:
                base.HandleEvent(ref ev);
                return;
        }
        ClearEvent(ref ev);
    }
}
