using TSharpVision;
using TSharpVision.Constants;

internal enum NoticeLevel { Info, Warning, Error }

internal sealed record WorkbenchNotice(DateTimeOffset Time, NoticeLevel Level, string Message, string Details);

// One application-level feedback channel. The status-line hint shows the latest notice whichever window is active,
// the Messages window keeps the session history, and blocking failures also open the standard error dialog.
// Clearing the history only forgets these in-memory notices; project files and run artifacts are never touched.
internal sealed class WorkbenchNotifications
{
    internal static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarningGrace = TimeSpan.FromSeconds(1);
    private readonly List<WorkbenchNotice> history = [];
    internal IReadOnlyList<WorkbenchNotice> History => history;
    internal WorkbenchNotice? Current { get; private set; }
    internal Action? Changed { get; set; }
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    internal WorkbenchNotice Post(NoticeLevel level, string message, string? details = null)
    {
        var notice = new WorkbenchNotice(Clock(), level, message.ReplaceLineEndings(" ").Trim(), details ?? message);
        history.Add(notice); Current = notice; Changed?.Invoke();
        return notice;
    }
    internal WorkbenchNotice Info(string message, string? details = null) => Post(NoticeLevel.Info, message, details);
    internal WorkbenchNotice Warning(string message, string? details = null) => Post(NoticeLevel.Warning, message, details);
    internal WorkbenchNotice Error(string message, string? details = null) => Post(NoticeLevel.Error, message, details);

    // Live progress (each finished execution of a run) updates the hint without flooding the Messages history.
    internal void Progress(string message) { Current = new WorkbenchNotice(Clock(), NoticeLevel.Info, message, message); Changed?.Invoke(); }
    internal void Clear() { history.Clear(); Current = null; Changed?.Invoke(); }

    // A warning stays until the next user input; an info hint fades after InfoLifetime. Errors stay until replaced.
    internal void NoteInput()
    {
        if (Current is { Level: NoticeLevel.Warning } warning && Clock() - warning.Time > WarningGrace) { Current = null; Changed?.Invoke(); }
    }
    internal string HintText()
    {
        if (Current is not { } notice) return "";
        if (notice.Level == NoticeLevel.Info && Clock() - notice.Time > InfoLifetime) return "";
        return (notice.Level == NoticeLevel.Info ? "" : "! ") + notice.Message;
    }
}

// The status line's hint area carries the latest notification after the context keys.
internal sealed class WorkbenchStatusLine(TRect bounds, TStatusDef defs, WorkbenchNotifications? notifications) : TStatusLine(bounds, defs)
{
    // 38 columns for a notice beside the keys. With F3 Info the Workspace keys up to
    // F10 Menu still show beside a notice at 120 columns; Space Mark and Ins Mark next are hidden first.
    private const int MinimumHint = 38;
    private string drawn = "";
    public override string Hint(ushort aHelpCtx) => drawn = notifications?.HintText() ?? "";
    internal void Refresh() { if ((notifications?.HintText() ?? "") != drawn) DrawView(); }
    // On narrow grids a notice keeps at least MinimumHint columns: trailing key labels are hidden while it shows.
    // Hidden labels only stop being drawn; their keys still work because key matching uses the full item chain.
    public override void Draw()
    {
        string hint = notifications?.HintText() ?? "";
        var all = Items;
        static int Width(TStatusItem? t) { int n = 0; for (; t is not null; t = t.Next) if (t.Text is not null) n += t.Text.Replace("~", "").Length + 2; return n; }
        if (hint.Length == 0 || all is null || Width(all) + 4 + MinimumHint <= size.x) { base.Draw(); return; }
        var shown = new List<TStatusItem>();
        for (var t = all; t is not null; t = t.Next) if (t.Text is not null) shown.Add(t);
        while (shown.Count > 1 && shown.Sum(t => t.Text!.Replace("~", "").Length + 2) + 4 + MinimumHint > size.x) shown.RemoveAt(shown.Count - 1);
        TStatusItem? chain = null;
        for (int i = shown.Count - 1; i >= 0; i--) chain = new TStatusItem(shown[i].Text, shown[i].KeyCode, shown[i].Command, chain);
        try { Items = chain; base.Draw(); }
        finally { Items = all; }
    }
}

// Messages (Alt+6): time, level and message of every session notice; details of the selected one below.
internal sealed class WorkbenchMessagesWindow : TWindow
{
    internal const ushort Context = 0x7d94, CopyDetails = 0x7d95, ClearHistory = 0x7d96, CloseMessages = 0x7d97;
    private readonly WorkbenchNotifications notifications;
    private readonly MessageList list;
    private readonly TListBox details;
    private WorkbenchNotice? shown;
    internal Action? Closed { get; set; }
    internal TListBox Entries => list;
    internal TListBox DetailLines => details;

    private sealed class MessageList(TRect bounds, TScrollBar scroll) : TListBox(bounds, 1, scroll)
    {
        internal WorkbenchNotice[] Items = [];
        internal WorkbenchNotice? Selected => Items.ElementAtOrDefault(focused);
        internal void Load(WorkbenchNotice[] items)
        {
            var previous = Selected; Items = items; SetRange(items.Length);
            FocusItemNum(Math.Max(0, previous is null ? 0 : Array.IndexOf(items, previous))); DrawView();
        }
        public override string GetText(int item, int maxChars)
        {
            if (Items.ElementAtOrDefault(item) is not { } n) return item == 0 ? "No messages in this session." : "";
            string level = n.Level switch { NoticeLevel.Error => "ERROR", NoticeLevel.Warning => "Warning", _ => "Info" };
            return WorkbenchText.Fit($"{n.Time.ToLocalTime():HH:mm:ss}  {level,-8}  {n.Message}", maxChars);
        }
    }

    internal WorkbenchMessagesWindow(TDeskTop desktop, WorkbenchNotifications notifications, TRect? remembered = null)
        : base(DefaultBounds(desktop, remembered), "Messages", 6)
    {
        this.notifications = notifications; helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        int w = size.x, h = size.y, split = Math.Max(3, h - 9);
        Insert(new TStaticText(new(2, 1, w - 2, 2), "Time      Level     Message") { growMode = Views.gfGrowHiX });
        var scroll = new TScrollBar(new(w - 3, 2, w - 2, split)) { growMode = Views.gfGrowLoX | Views.gfGrowHiX | Views.gfGrowHiY };
        Insert(scroll);
        list = new MessageList(new(2, 2, w - 3, split), scroll) { growMode = Views.gfGrowHiX | Views.gfGrowHiY }; Insert(list);
        Insert(new TStaticText(new(2, split, w - 2, split + 1), "Details") { growMode = Views.gfGrowHiX | Views.gfGrowLoY | Views.gfGrowHiY });
        var detailScroll = new TScrollBar(new(w - 3, split + 1, w - 2, h - 3)) { growMode = Views.gfGrowLoX | Views.gfGrowHiX | Views.gfGrowLoY | Views.gfGrowHiY };
        Insert(detailScroll);
        details = new TListBox(new(2, split + 1, w - 3, h - 3), 1, detailScroll) { growMode = Views.gfGrowHiX | Views.gfGrowLoY | Views.gfGrowHiY };
        Insert(details);
        byte bottom = (byte)(Views.gfGrowLoY | Views.gfGrowHiY);
        Insert(new TButton(new(2, h - 3, 20, h - 1), "~C~opy details", CopyDetails, ButtonConstants.bfNormal) { growMode = bottom });
        Insert(new TButton(new(21, h - 3, 33, h - 1), "C~l~ear", ClearHistory, ButtonConstants.bfNormal) { growMode = bottom });
        Insert(new TButton(new(w - 15, h - 3, w - 2, h - 1), "Close", CloseMessages, ButtonConstants.bfNormal)
            { growMode = (byte)(Views.gfGrowLoX | Views.gfGrowHiX | bottom) });
        Reload(); list.Select();
    }

    private static TRect DefaultBounds(TDeskTop desktop, TRect? remembered)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        int w = Math.Min(dw, Math.Max(60, remembered is { } bounds ? bounds.b.x - bounds.a.x : dw - 8));
        int h = Math.Min(dh, Math.Max(10, remembered is { } saved ? saved.b.y - saved.a.y : 18));
        int x = (dw - w) / 2, y = (dh - h) / 2;
        return new(x, y, x + w, y + h);
    }

    internal void Reload()
    {
        list.Load(notifications.History.Reverse().ToArray());
        ShowDetails();
    }
    // Focus a specific notice, used by the error dialog's Show messages button.
    internal void Focus(WorkbenchNotice notice)
    {
        int index = Array.IndexOf(list.Items, notice);
        if (index >= 0) list.FocusItemNum(index);
        ShowDetails();
    }
    private void ShowDetails()
    {
        if (ReferenceEquals(shown, list.Selected) && details.range > 0) return;
        shown = list.Selected;
        var lines = new TStringCollection();
        foreach (string line in WorkbenchText.Wrap(shown is null ? "Select a message to see its details." : shown.Details, Math.Max(1, details.size.x - 1)))
            lines.Insert(line);
        details.NewList(lines);
    }
    public override void SizeLimits(ref TPoint min, ref TPoint max) { base.SizeLimits(ref min, ref max); min = new(Math.Min(60, max.x), Math.Min(10, max.y)); }
    public override void ShutDown() { base.ShutDown(); Closed?.Invoke(); }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEsc) { ClearEvent(ref ev); Close(); return; }
        if (ev.What == Events.evCommand && ev.message.command is CopyDetails or ClearHistory or CloseMessages)
        {
            ushort command = ev.message.command; ClearEvent(ref ev);
            if (command == CloseMessages) { Close(); return; }
            if (command == ClearHistory) { notifications.Clear(); return; }
            if (list.Selected is not { } notice) { notifications.Warning("Select a message first; there are no details to copy."); return; }
            bool copied = ClipboardService.Current.IsAvailable && ClipboardService.Current.SetText(notice.Details);
            if (copied) notifications.Info("Message details copied to the clipboard.");
            else notifications.Warning("The clipboard is not available; the details remain visible in Messages.");
            return;
        }
        base.HandleEvent(ref ev);
        ShowDetails();
    }
}

// Standard error dialog for blocking failures: static text, OK as default, and Show messages for the technical details.
internal static class WorkbenchErrorDialog
{
    internal const ushort Context = 0x7dc4;
    internal static void Show(TDeskTop desktop, WorkbenchNotifications notifications, string title, string message, string? details,
        Action<WorkbenchNotice>? showMessages)
    {
        var notice = notifications.Error(title + ": " + message, details ?? message);
        int w = Math.Min(84, desktop.size.x - 2);
        var lines = WorkbenchText.Wrap(message + "\n\nFull technical details are in the Messages window (Alt+6).", w - 6).ToArray();
        int h = Math.Min(Math.Min(20, desktop.size.y), lines.Length + 6);
        using var dialog = new TDialog(new(0, 0, w, h), title); dialog.options |= Views.ofCentered; dialog.helpCtx = Context;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 3), string.Join('\n', lines)));
        var ok = new TButton(new(3, h - 3, 15, h - 1), "OK", Views.cmOK, ButtonConstants.bfDefault); dialog.Insert(ok);
        dialog.Insert(new TButton(new(16, h - 3, 35, h - 1), "Show ~m~essages", Views.cmYes, ButtonConstants.bfNormal));
        ok.Select();
        if (desktop.ExecView(dialog) == Views.cmYes) showMessages?.Invoke(notice);
    }
}

// A label for modeless blue windows. TLabel's palette targets gray dialogs; in a TWindow its focused-link colour maps
// to black on black. Normal: white on blue, focused link: bright white, mnemonic: yellow.
internal sealed class WindowLabel(TRect bounds, string text, TView? link) : TLabel(bounds, text, link)
{
    private static readonly TPalette Palette = new("\x01\x02\x06\x06\x01\x01", 6);
    public override TPalette GetPalette() => Palette;
}

internal static class WorkbenchText
{
    // Trailing ellipsis for list columns and single-line captions; full text stays in the Details pane or Messages.
    internal static string Fit(string text, int width)
    {
        if (width <= 0) return "";
        return text.Length <= width ? text : width == 1 ? "…" : text[..(width - 1)] + "…";
    }
    internal static string Column(string text, int width) => Fit(text, width).PadRight(Math.Max(0, width));
    // Middle ellipsis keeping the root and the last two segments of a path.
    internal static string Path(string path, int width)
    {
        if (path.Length <= width || width < 12) return Fit(path, width);
        char separator = path.Contains('\\') ? '\\' : '/';
        string[] parts = path.Split(separator);
        if (parts.Length > 3)
        {
            string shortened = parts[0] + separator + "..." + separator + string.Join(separator, parts[^2..]);
            if (shortened.Length <= width) return shortened;
        }
        return "..." + path[^(width - 3)..];
    }
    internal static IEnumerable<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw;
            if (line.Length == 0) { yield return ""; continue; }
            while (line.Length > width)
            {
                int cut = line.LastIndexOf(' ', width);
                if (cut <= 0) cut = width;
                yield return line[..cut].TrimEnd();
                line = line[cut..].TrimStart();
            }
            yield return line;
        }
    }
}
