using TSharpVision;
using TSharpVision.Constants;

// Project > Information...: a read-only summary of the open project that replaces the Workspace's
// three header rows. Values come from the Workspace (already computed; nothing is diagnosed while drawing) and are
// reloaded while the dialog is open, e.g. during a run. Long values wrap under their label (paths at a separator); the
// list scrolls on small grids. Project > Properties... stays the only place that changes the configuration. Only Close
//: Properties (F4) and Target diagnostics are on the Project menu, not repeated here.
internal sealed class WorkbenchProjectInfoDialog : TDialog
{
    internal const string DialogTitle = "Project Information";
    private const int LabelWidth = 15;
    private readonly Func<(string Label, string Value)[]> rows;
    private readonly TListBox list;
    private string[] lines = [];
    internal string[] Lines => lines;
    internal TListBox List => list;

    internal WorkbenchProjectInfoDialog(TDeskTop desktop, Func<(string Label, string Value)[]> rows) : base(Bounds(desktop, rows), DialogTitle)
    {
        this.rows = rows;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y;
        var scroll = new TScrollBar(new(w - 3, 2, w - 2, h - 4)); Insert(scroll);
        list = new TListBox(new(2, 2, w - 3, h - 4), 1, scroll); Insert(list);
        var close = new TButton(new(w - 15, h - 3, w - 3, h - 1), "Close", Views.cmCancel, ButtonConstants.bfDefault); Insert(close);
        Reload();
        list.Select();
    }

    // As tall as the content (one free row below the list), never taller than the desktop; the list scrolls beyond that.
    private static TRect Bounds(TDeskTop desktop, Func<(string Label, string Value)[]> rows)
    {
        int w = Math.Min(Math.Min(100, Math.Max(60, desktop.size.x - 8)), desktop.size.x);
        int content = Format(rows(), w - 6).Length;
        int h = Math.Min(Math.Max(10, content + 7), desktop.size.y);
        return new(0, 0, w, h);
    }

    private static string[] Format((string Label, string Value)[] rows, int width)
    {
        var lines = new List<string>();
        foreach (var (label, value) in rows)
        {
            string[] wrapped = Wrap(value, Math.Max(10, width - LabelWidth)).ToArray();
            for (int i = 0; i < wrapped.Length; i++) lines.Add((i == 0 ? label : "").PadRight(LabelWidth) + wrapped[i]);
        }
        return lines.ToArray();
    }
    // Wraps at the last space that fits, else after the last path separator that fits (paths), else at the width.
    private static IEnumerable<string> Wrap(string text, int width)
    {
        string rest = text.Replace("\r", "").Replace('\n', ' ');
        while (rest.Length > width)
        {
            int space = rest.LastIndexOf(' ', width);
            int separator = rest.LastIndexOfAny(['\\', '/'], width - 1) + 1;
            int cut = space > width / 3 ? space : separator > width / 3 ? separator : width;
            yield return rest[..cut].TrimEnd();
            rest = rest[cut..].TrimStart();
        }
        yield return rest;
    }

    // Keeps the focused row, so a live update during a run does not move the reader.
    internal void Reload()
    {
        string[] next = Format(rows(), Math.Max(10, list.size.x - 1));
        if (next.SequenceEqual(lines)) return;
        lines = next;
        int focused = list.focused;
        var collection = new TStringCollection(); foreach (string line in lines) collection.Insert(line);
        list.NewList(collection);
        if (focused > 0) list.FocusItemNum(Math.Min(focused, lines.Length - 1));
        list.DrawView();
    }
}
