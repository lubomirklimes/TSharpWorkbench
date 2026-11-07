using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// What one Baseline Comparison shows. Logical comparisons carry the approved (Expected) and normalized actual cells
// produced by the existing gate; native evidence comparisons carry only artifact rows, because native captures have no
// approved baselines and the Workbench has no embedded image viewer: they are opened in the system viewer.
internal sealed record ComparisonModel(string Scenario, string Checkpoint, string Grid, string Key, string Store, string? ApprovedHash,
    string RunName, string Status, string Reason, ScreenSnapshot? Expected, ScreenSnapshot? Actual, string ActualSource, string Normalization,
    EvidenceRow[] Evidence, string RunRoot, bool Native);

// Baseline Comparison (window 5): modeless, one at a time (a new comparison replaces the content).
// Header with provenance, the cell viewport (Diff / Expected / Actual), a side panel with the focused cell and the list
// of differences, and actions. Native evidence comparisons list the native and logical artifacts with open actions.
internal sealed class WorkbenchComparisonWindow : TWindow
{
    internal const ushort Expected = 0x7d71, Actual = 0x7d72, Diff = 0x7d73, NextDiff = 0x7d74, PreviousDiff = 0x7d75, Review = 0x7d76,
        CloseComparison = 0x7d77, OpenFile = 0x7d78, Context = 0x7d79, OpenNative = 0x7d7b, OpenLogical = 0x7d7c;
    internal const string LogicalTitle = "Baseline Comparison", NativeTitle = "Native Evidence Comparison";
    internal Action? Closed { get; set; }
    // The Comparison-only commands return to the enabled default when the window closes.
    public override void ShutDown() { SetAvailable(true, true); base.ShutDown(); Closed?.Invoke(); }
    private static void SetAvailable(bool expected, bool review)
    {
        foreach (ushort command in new[] { Diff, Expected }) { if (expected) EnableCommand(command); else DisableCommand(command); }
        if (review) EnableCommand(Review); else DisableCommand(Review);
    }

    private readonly ComparisonModel model;
    private readonly Action<string> open;
    private readonly Action? review;
    private readonly Cells? viewport;
    private readonly TListBox? differences;
    private readonly EvidenceList? evidence;
    private readonly Caption statusLine, cellLine, expectedLine, actualLine;
    private readonly TButton? diffButton, expectedButton, reviewButton;

    private sealed class Caption(TRect r, string text = "") : TStaticText(r, text)
    {
        internal string Current => Text;
        internal void Set(string value) { if (Text == value) return; Text = value; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }
    private sealed class DifferenceList(TRect bounds, TScrollBar scroll, (int X, int Y, string Kind)[] rows) : TListBox(bounds, 1, scroll)
    {
        public override string GetText(int item, int maxChars) => item < rows.Length
            ? WorkbenchText.Fit($"{item + 1,4}  ({rows[item].X},{rows[item].Y})".PadRight(16) + rows[item].Kind, maxChars)
            : item == 0 ? "No differences." : "";
    }
    private sealed class EvidenceList(TRect bounds, TScrollBar scroll, EvidenceRow[] rows, string root) : TListBox(bounds, 1, scroll)
    {
        internal EvidenceRow? Current => rows.ElementAtOrDefault(focused);
        public override string GetText(int item, int maxChars) => item < rows.Length
            ? WorkbenchText.Fit($"{rows[item].Label,-17}{rows[item].File,-44}  {rows[item].Layer}{(ResultText.Exists(root, rows[item].File) ? "" : "   ! missing")}", maxChars)
            : "";
    }

    internal static TRect DefaultBounds(TDeskTop desktop, TRect? remembered)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        if (remembered is { } r && r.a.x >= 0 && r.a.y >= 0 && r.b.x <= dw && r.b.y <= dh && r.b.x - r.a.x >= Math.Min(100, dw) && r.b.y - r.a.y >= Math.Min(30, dh)) return r;
        return new(0, 0, dw, dh);
    }

    internal WorkbenchComparisonWindow(TDeskTop desktop, ComparisonModel model, Action<string> open, Action? review, TRect? remembered = null)
        : base(DefaultBounds(desktop, remembered), model.Native ? NativeTitle : LogicalTitle, 5)
    {
        this.model = model; this.open = open; this.review = review;
        helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        int w = size.x, h = size.y;
        byte stretch = Views.gfGrowHiX, bottom = (byte)(Views.gfGrowLoY | Views.gfGrowHiY), right = (byte)(Views.gfGrowLoX | Views.gfGrowHiX);
        Caption Row(int y, string text, int x0 = 2, int x1 = -2, byte grow = Views.gfGrowHiX)
        { var c = new Caption(new(x0, y, x1 < 0 ? w + x1 : x1, y + 1), text) { growMode = grow }; Insert(c); return c; }
        Row(2, $"Checkpoint  {model.Scenario} > {model.Checkpoint} at {model.Grid}" + (model.Key.Length > 0 ? $"     key {model.Key}" : ""));
        Row(3, $"Store       {(model.Store.Length == 0 ? "not used (native evidence has no approved baselines)" : model.Store)}"
            + (model.ApprovedHash is { } hash ? $"   approved cells {Short(hash)}" : "") + $"   run {model.RunName}");
        statusLine = Row(4, "");
        Row(5, "Capture     " + model.ActualSource);
        int side = Math.Clamp(w * 2 / 5, 40, 58), vx = w - side - 3;
        if (!model.Native)
        {
            viewport = new(new(2, 6, vx, h - 7), model.Expected, model.Actual, ShowCell) { growMode = (byte)(Views.gfGrowHiX | Views.gfGrowHiY) };
            Insert(viewport);
            Row(6, "Viewport   arrows pan; PgUp/PgDn page", vx + 2, -2, right);
            cellLine = Row(8, "", vx + 2, -2, right);
            expectedLine = Row(9, "", vx + 2, -2, right);
            actualLine = Row(10, "", vx + 2, -2, right);
            var items = viewport.Differences;
            var scroll = new TScrollBar(new(w - 3, 13, w - 2, h - 10)) { growMode = (byte)(right | Views.gfGrowHiY) }; Insert(scroll);
            differences = new DifferenceList(new(vx + 2, 13, w - 3, h - 10), scroll, items) { growMode = (byte)(right | Views.gfGrowHiY) };
            differences.SetRange(Math.Max(1, items.Length)); Insert(differences);
            Insert(new WindowLabel(new(vx + 1, 12, w - 2, 13), $"Di~f~ferences ({items.Length})", differences) { growMode = right });
            Row(h - 9, "Legend: differing cells are red in Diff;", vx + 2, -2, (byte)(right | bottom));
            Row(h - 8, "the focused cell is shown reversed.", vx + 2, -2, (byte)(right | bottom));
            Row(h - 6, "Normalization  " + model.Normalization, 2, -2, (byte)(stretch | bottom));
            // Availability is set on the Comparison-only commands (TButton follows the command set, also after every
            // cmCommandSetChanged broadcast); the buttons read it when they are created.
            SetAvailable(model.Expected is not null, review is not null);
            diffButton = Button(2, 8, "~D~iff", Diff); expectedButton = Button(11, 12, "~E~xpected", Expected); Button(24, 10, "~A~ctual", Actual);
            Button(35, 8, "~N~ext", NextDiff); Button(44, 12, "~P~revious", PreviousDiff);
            reviewButton = Button(57, 23, "Re~v~iew candidate...", Review);
            if (model.Expected is null) viewport.Mode = 1;
        }
        else
        {
            cellLine = Row(6, "Artifacts of this checkpoint (Enter opens the file in the system viewer)");
            expectedLine = Row(h - 7, "Native captures have no approved baselines. Compare the native capture with the logical PNG side by side;", 2, -2, (byte)(stretch | bottom));
            actualLine = Row(h - 6, "logical PNGs are rendered by the Workbench from cells and are never native pixels.", 2, -2, (byte)(stretch | bottom));
            var scroll = new TScrollBar(new(w - 3, 7, w - 2, h - 8)) { growMode = (byte)(right | Views.gfGrowHiY) }; Insert(scroll);
            evidence = new EvidenceList(new(2, 7, w - 3, h - 8), scroll, model.Evidence, model.RunRoot) { growMode = (byte)(stretch | Views.gfGrowHiY) };
            evidence.SetRange(model.Evidence.Length); Insert(evidence);
            Button(2, 14, "~O~pen file", OpenFile); Button(17, 14, "~N~ative", OpenNative); Button(32, 21, "Open ~l~ogical PNG", OpenLogical);
        }
        Insert(new TButton(new(w - 13, h - 3, w - 2, h - 1), "~C~lose", CloseComparison, ButtonConstants.bfNormal) { growMode = (byte)(right | bottom) });
        statusLine.Set(StatusText());
        if (viewport is not null)
        {
            viewport.Select();
            // Opening Comparison focuses the viewport on the first difference ().
            if (viewport.Differences.Length > 0) viewport.Go(0); else viewport.Move(0, 0);
        }
        else evidence!.Select();
    }

    private TButton Button(int x, int width, string title, ushort command)
    {
        var button = new TButton(new(x, size.y - 3, x + width, size.y - 1), title, command, ButtonConstants.bfNormal) { growMode = (byte)(Views.gfGrowLoY | Views.gfGrowHiY) };
        Insert(button); return button;
    }
    private static string Short(string hash) => hash.Length > 16 ? hash[..8] + ".." + hash[^4..] : hash;
    internal string HeaderText => string.Join("\n", Children().OfType<Caption>().Select(c => c.Current));
    internal int DifferenceCount => viewport?.Differences.Length ?? 0;
    internal int CurrentDifference => viewport?.Current ?? -1;
    internal int ViewMode => viewport?.Mode ?? -1;
    internal ComparisonModel Model => model;
    internal TButton[] ActionButtons => Children().OfType<TButton>().ToArray();
    private IEnumerable<TView> Children()
    {
        if (last is null) yield break; var first = last.Next!;
        for (TView? v = first; v is not null; v = v.Next) { yield return v; if (v.Next == first) break; }
    }

    private string StatusText()
    {
        // Native evidence is never compared with approved (logical-cell) baselines; the status says so plainly.
        if (model.Native) return "Status      Native evidence only: native captures are not compared with approved baselines; open them to inspect side by side.";
        string status = $"Status      {ResultText.VisualPhrase(model.Status)}: {model.Reason}";
        if (viewport is null) return status;
        string view = new[] { "Expected", "Actual", "Diff" }[viewport.Mode];
        return status + $"   View {view}" + (viewport.Differences.Length > 0 && viewport.Current >= 0 ? $"   difference {viewport.Current + 1} of {viewport.Differences.Length}" : "");
    }
    private void ShowCell(string cell, string expected, string actual)
    {
        cellLine.Set(cell); expectedLine.Set(expected); actualLine.Set(actual);
        statusLine?.Set(StatusText());
        if (differences is not null && viewport is not null && viewport.Current >= 0 && differences.focused != viewport.Current) differences.FocusItemNum(viewport.Current);
    }

    private void OpenRow(EvidenceRow? row)
    {
        if (row is null || !ResultText.Exists(model.RunRoot, row.File))
        { WorkbenchNotificationsHook?.Invoke($"{row?.File ?? "The artifact"} is missing from the run folder; it may have been deleted after the run was loaded."); return; }
        open(Path.Combine(model.RunRoot, row.File));
    }
    // Missing-artifact warnings go to the application notification channel (set by the workspace).
    internal Action<string>? WorkbenchNotificationsHook { get; set; }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEsc) { ClearEvent(ref ev); Close(); return; }
        if (viewport is not null && ev.What == Events.evKeyDown && !ReferenceEquals(current, differences) && char.ToLowerInvariant((char)ev.keyDown.charScan.charCode) is 'n' or 'p'
            && (ev.keyDown.controlKeyState & (Keys.kbAltShift | Keys.kbCtrlShift)) == 0)
        { bool next = char.ToLowerInvariant((char)ev.keyDown.charScan.charCode) == 'n'; ClearEvent(ref ev); if (next) viewport.NextDifference(); else viewport.PreviousDifference(); return; }
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter)
        {
            if (current is TButton button) { ClearEvent(ref ev); if (!button.GetState(Views.sfDisabled)) button.Press(); return; }
            if (differences is not null && ReferenceEquals(current, differences)) { ClearEvent(ref ev); viewport!.Go(differences.focused); return; }
            if (evidence is not null && ReferenceEquals(current, evidence)) { ClearEvent(ref ev); OpenRow(evidence.Current); return; }
            if (viewport is not null) { ClearEvent(ref ev); viewport.NextDifference(); return; }
        }
        if (ev.What == Events.evBroadcast && ev.message.command == Views.cmListItemSelected)
        {
            if (differences is not null && ReferenceEquals(ev.message.infoPtr, differences)) { ClearEvent(ref ev); viewport!.Go(differences.focused); return; }
            if (evidence is not null && ReferenceEquals(ev.message.infoPtr, evidence)) { ClearEvent(ref ev); OpenRow(evidence.Current); return; }
        }
        if (ev.What == Events.evCommand && ev.message.command is (>= Expected and <= OpenFile) or OpenNative or OpenLogical)
        {
            ushort c = ev.message.command; ClearEvent(ref ev);
            switch (c)
            {
                case CloseComparison: Close(); return;
                case Review: if (review is null) WorkbenchNotificationsHook?.Invoke("No candidate was kept for this checkpoint."); else review(); return;
                case OpenFile: OpenRow(evidence?.Current); return;
                case OpenNative: OpenRow(model.Evidence.FirstOrDefault(e => e.Native && ResultText.Exists(model.RunRoot, e.File)) ?? model.Evidence.FirstOrDefault(e => e.Native)); return;
                case OpenLogical: OpenRow(model.Evidence.FirstOrDefault(e => e.Label == "Logical PNG")); return;
            }
            if (viewport is null) return;
            if (c == NextDiff) viewport.NextDifference();
            else if (c == PreviousDiff) viewport.PreviousDifference();
            else if (!(model.Expected is null && c is Expected or Diff)) { viewport.Mode = c - Expected; viewport.Move(0, 0); }
            return;
        }
        base.HandleEvent(ref ev);
    }
    public override void SizeLimits(ref TPoint min, ref TPoint max) { base.SizeLimits(ref min, ref max); min = new(Math.Min(100, max.x), Math.Min(30, max.y)); }

    private sealed class Cells : TView
    {
        private readonly ScreenSnapshot? expected, actual;
        private readonly Action<string, string, string> details;
        internal readonly (int X, int Y, string Kind)[] Differences;
        private int x, y, difference = -1;
        internal int Mode = 2;
        internal int Current => difference;
        internal Cells(TRect r, ScreenSnapshot? expected, ScreenSnapshot? actual, Action<string, string, string> details) : base(r)
        {
            this.expected = expected; this.actual = actual; this.details = details; options |= Views.ofSelectable;
            int width = Math.Max(expected?.Width ?? 0, actual?.Width ?? 0), height = Math.Max(expected?.Height ?? 0, actual?.Height ?? 0);
            Differences = expected is null || actual is null ? []
                : Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (X: x, Y: y)))
                    .Select(p => (p.X, p.Y, Kind: Kind(p.X, p.Y))).Where(p => p.Kind.Length > 0).ToArray();
        }
        private int Width => Math.Max(1, Math.Max(expected?.Width ?? 0, actual?.Width ?? 0));
        private int Height => Math.Max(1, Math.Max(expected?.Height ?? 0, actual?.Height ?? 0));
        private static TScreenChar? Cell(ScreenSnapshot? s, int x, int y) => s is not null && x < s.Width && y < s.Height ? s[x, y] : null;
        private string Kind(int x, int y)
        {
            var e = Cell(expected, x, y); var a = Cell(actual, x, y);
            bool glyph = e?.Character != a?.Character, attribute = e?.Attr != a?.Attr;
            return glyph && attribute ? "character and attribute" : glyph ? "character" : attribute ? "attribute" : "";
        }
        private bool Different(int x, int y) => expected is not null && actual is not null && Kind(x, y).Length > 0;
        internal void Go(int index)
        {
            if (Differences.Length == 0) { Move(0, 0); return; }
            difference = Math.Clamp(index, 0, Differences.Length - 1); (x, y, _) = Differences[difference]; Move(0, 0);
        }
        internal void NextDifference() { if (Differences.Length > 0) Go((difference + 1) % Differences.Length); }
        internal void PreviousDifference() { if (Differences.Length > 0) Go(difference <= 0 ? Differences.Length - 1 : difference - 1); }
        internal void Move(int dx, int dy)
        {
            x = Math.Clamp(x + dx, 0, Width - 1); y = Math.Clamp(y + dy, 0, Height - 1);
            static string Describe(TScreenChar? c) => c is { } v ? $"'{v.Character}' U+{(int)v.Character:X4}  fg {v.Attr & 15:X1} bg {v.Attr >> 4:X1}" : "outside the grid";
            details($"Focused cell ({x},{y})" + (Differences.Length == 0 ? "" : $"   {Differences.Length} differences"),
                "  Expected  " + (expected is null ? "no approved baseline" : Describe(Cell(expected, x, y))),
                "  Actual    " + (actual is null ? "no actual capture" : Describe(Cell(actual, x, y))));
            DrawView();
        }
        public override void Draw()
        {
            int left = Math.Clamp(x - size.x / 2, 0, Math.Max(0, Width - size.x)), top = Math.Clamp(y - size.y / 2, 0, Math.Max(0, Height - size.y));
            var row = new TScreenChar[size.x];
            for (int sy = 0; sy < size.y; sy++)
            {
                for (int sx = 0; sx < size.x; sx++)
                {
                    int cx = left + sx, cy = top + sy; var cell = Cell(Mode == 0 ? expected : actual, cx, cy);
                    row[sx] = cell ?? new TScreenChar(' ', 0x17);
                    if (Mode == 2 && Different(cx, cy)) row[sx] = new(row[sx].Character, 0x4f);
                    if (cx == x && cy == y) row[sx] = new(row[sx].Character, 0x70);
                }
                WriteLine(0, sy, size.x, 1, row.AsSpan());
            }
        }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown)
            {
                switch (ev.keyDown.keyCode)
                {
                    case Keys.kbLeft: Move(-1, 0); break; case Keys.kbRight: Move(1, 0); break; case Keys.kbUp: Move(0, -1); break;
                    case Keys.kbDown: Move(0, 1); break; case Keys.kbPgDn: Move(0, size.y); break; case Keys.kbPgUp: Move(0, -size.y); break;
                    default: base.HandleEvent(ref ev); return;
                }
                ClearEvent(ref ev); return;
            }
            base.HandleEvent(ref ev);
        }
    }
}
