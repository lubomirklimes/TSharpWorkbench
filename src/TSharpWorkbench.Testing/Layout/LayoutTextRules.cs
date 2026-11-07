namespace TSharpWorkbench.Testing;

// Text fit / overflow rules (text.*), using the exact TSharpVision v1.1.0 drawing rules
// (LayoutTextMetrics). Source text comes from the inspection (button "markup", label/static "markup" of an Testing
// payload, cluster "items", window title = ViewNode.Text); captured cells only corroborate or, for a window title whose
// budget depends on unexposed window flags, decide. Without source text a view is a coverage gap (sink.Unavailable), and
// at most a low-confidence text.cells-heuristic warning — never an error and never a silent pass.
//
// Not clipping by design (no rule examines them): input line content longer than the field (it scrolls), list items
// beyond the visible rows, editor/scroller content, menu and status texts (other assertions cover those).
internal static class LayoutTextRules
{
    private const string NoMarkup = "The target's Testing payload does not expose this view's source text (older inspection); its fit cannot be judged.";

    internal static void Evaluate(LayoutContext context, LayoutRuleSink sink)
    {
        var noSource = new List<LayoutNode>();
        Captions(context, sink);
        Titles(context, sink);
        Labels(context, sink, noSource);
        Statics(context, sink, noSource);
        Clusters(context, sink);
        CellsHeuristic(context, sink, noSource);
    }

    private static bool Drawable(LayoutNode n) => n.VisibleFlag && n.Bounds.Width > 0 && n.Bounds.Height > 0;

    private static void Report(LayoutRuleSink sink, string rule, bool any)
    {
        if (any) sink.Evaluated(rule); else sink.NotApplicable(rule);
    }

    private static string Q(string s) => "\"" + Inspectors.Bound(s, 60) + "\"";

    private static string Observed(LayoutContext ctx, CellRect r) =>
        r.Y >= 0 && r.Y < ctx.Cells.Height ? Q(Inspectors.Row(ctx.Cells, r.X, r.Y, Math.Max(0, r.Width)).Replace('\0', ' ').TrimEnd()) : "(off screen)";

    /// <summary>Whether owner clipping of <paramref name="n"/> is intentional scrolling (a plain group with its own scroll bar).</summary>
    private static bool ScrolledOwner(LayoutNode n)
    {
        for (var o = n.Parent; o is not null && o.Role is not (LayoutRole.Window or LayoutRole.Dialog or LayoutRole.Desktop or LayoutRole.Application); o = o.Parent)
            if (o.Role is LayoutRole.Group or LayoutRole.Other or LayoutRole.Scroller && o.Children.Any(c => c.Role == LayoutRole.ScrollBar)) return true;
        return false;
    }

    /// <summary>Cells of an intrinsically fitting text segment that its owners or the screen cut off (0 when not shown).</summary>
    private static int OwnerCut(LayoutNode n, CellRect segment)
    {
        if (segment.IsEmpty || n.Visible is not { IsEmpty: false } v || ScrolledOwner(n)) return 0;
        return Geometry.ClippedCells(segment, v);
    }

    private static void AddOwnerCut(LayoutContext ctx, LayoutRuleSink sink, string rule, LayoutNode n, CellRect segment, string what, string text)
    {
        int cut = OwnerCut(n, segment);
        if (cut == 0) return;
        sink.Add(rule, LayoutFindingClasses.Error, "high", n, segment,
            $"The {what} {Q(text)} fits its {n.Kind} but {cut} of its {segment.Width * segment.Height} cell(s) are cut by the owner or the screen (visible part {n.Visible}). Move the control fully inside its owner.",
            $"text={segment}; visible={n.Visible}; cutCells={cut}; observed={Observed(ctx, segment)}");
    }

    // ── text.caption-clipped (TButton) ──────────────────────────────────────

    private static void Captions(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.CaptionClipped;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var n in ctx.Nodes.Where(n => n.Role == LayoutRole.Button && Drawable(n)))
        {
            if (n.Markup is not { } markup) { sink.Unavailable(rule, "The inspection does not expose this button's caption markup.", n.Path); continue; }
            any = true;
            int width = n.Bounds.Width, height = n.Bounds.Height;
            var cap = LayoutTextMetrics.ButtonCaption(width, height, markup);
            string shown = LayoutTextMetrics.Shown(markup);
            if (cap.Width == 0) continue;
            if (!cap.RowDrawn)
            {
                sink.Add(rule, LayoutFindingClasses.Error, "high", n, n.Screen,
                    $"Button caption {Q(shown)} is never drawn: the button is {height} row(s) tall and TButton draws its caption on row size.y/2-1 above the shadow row, so it needs at least 2 rows. Make the button 2 rows tall.",
                    $"caption={Q(markup)}; cells={cap.Width}; size={width}x{height}; captionRow={cap.Row}");
                continue;
            }
            var rect = new CellRect(n.Screen.X + cap.Start, n.Screen.Y + cap.Row, cap.Width, 1);
            if (cap.Start + cap.Width > cap.FaceEnd)
            {
                int lost = cap.Start + cap.Width - cap.FaceEnd;
                sink.Add(rule, LayoutFindingClasses.Error, "high", n, rect,
                    $"Button caption {Q(shown)} ({cap.Width} cell(s), mnemonic markers excluded) does not fit the {width}-column button: the face holds {Math.Max(0, width - 3)} caption cell(s) between the left margin and the shadow column, so {lost} cell(s) overwrite the shadow or are cut. Make the button at least {cap.Width + 3} columns wide or shorten the caption.",
                    $"caption={Q(markup)}; cells={cap.Width}; size={width}x{height}; captionStart={cap.Start}; faceEnd={cap.FaceEnd}; needWidth={cap.Width + 3}; observed={Observed(ctx, new CellRect(n.Screen.X, rect.Y, width, 1))}");
                continue;
            }
            AddOwnerCut(ctx, sink, rule, n, rect, "button caption", shown);
            int wide = LayoutTextMetrics.WideUnits(shown);
            if (wide > 0 && cap.Start + cap.Width + wide > cap.FaceEnd)
                sink.Add(rule, LayoutFindingClasses.Warning, "medium", n, rect,
                    $"Button caption {Q(shown)} fits as TSharpVision 1.1 draws it (one cell per character) but contains {wide} wide character(s) a terminal may render two columns wide; then it would run past the face. Allow {cap.Width + wide + 3} columns.",
                    $"cells={cap.Width}; wide={wide}; size={width}x{height}");
        }
        Report(sink, rule, any);
    }

    // ── text.title-clipped (TFrame) ─────────────────────────────────────────

    private static void Titles(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.TitleClipped;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var n in ctx.Nodes.Where(n => n.Role is LayoutRole.Window or LayoutRole.Dialog && Drawable(n) && !string.IsNullOrEmpty(n.Node.Text)))
        {
            var frame = n.Children.FirstOrDefault(c => c.Role == LayoutRole.Frame && c.VisibleFlag);
            if (frame is null || frame.Screen.Width <= 0 || frame.Screen.Height <= 0) continue;
            any = true;
            string title = n.Node.Text!;
            bool cut = title.Length == InspectionLimits.Default.MaxText + 1 && title.EndsWith('…');
            if (cut) title = title[..^1];
            int w = frame.Screen.Width, ls = title.Length;
            int[] budgets = LayoutTextMetrics.TitleBudgets(w);
            int max = budgets[0], min = budgets[^1];
            var top = new CellRect(frame.Screen.X, frame.Screen.Y, w, 1);
            // TEditWindow overrides GetTitle (the drawn text is not window.title): only the cells can decide.
            bool cellsOnly = n.Kind == "TEditWindow";
            bool fullShown = Shows(ctx, frame, LayoutTextMetrics.FrameTitle(w, int.MaxValue, title));
            int? truncatedBudget = budgets.Where(l => l < ls).Cast<int?>().FirstOrDefault(l => Shows(ctx, frame, LayoutTextMetrics.FrameTitle(w, l!.Value, title)));
            string budgetText = $"frame width {w}: budget {max} (width-10), {w - 16} with close/zoom icons, {w - 14} with a window number, {min} with both";
            string evidence = $"title={Q(title)}{(cut ? "(+more)" : "")}; length={ls}; frameWidth={w}; budgets={string.Join("/", budgets)}; observed={Observed(ctx, top)}";
            if (!cellsOnly && !cut && ls <= min || fullShown && !cut) { WideTitle(sink, n, top, title, min); continue; }
            if (!cellsOnly && ls > max || truncatedBudget is not null)
            {
                string how = truncatedBudget is { } b ? $"the captured frame row shows it truncated to \" ..\" + the last {b - 2} character(s) (budget {b})"
                    : max <= 0 ? "the frame is too narrow to draw any title" : $"it is longer than the largest budget ({max})";
                string need = truncatedBudget is { } tb ? $"{ls + w - tb}" : $"{ls + 10} ({ls + 20} with close/zoom icons and a window number)";
                sink.Add(rule, LayoutFindingClasses.Error, "high", n, top,
                    $"Window title {Q(title)} ({ls}{(cut ? "+" : "")} characters) does not fit its frame: {how} ({budgetText}). Widen the window to at least {need} columns or shorten the title.",
                    evidence, frame);
                continue;
            }
            sink.Unavailable(rule, cellsOnly
                ? "TEditWindow draws GetTitle(), not the inspected title, and the frame row cells do not show it."
                : $"The fit of this {ls}-character title depends on the window's close/zoom flags and number ({budgetText}), which the inspection does not expose, and the frame row cells do not show the title (occluded or off screen).", n.Path);
        }
        Report(sink, rule, any);
    }

    private static bool Shows(LayoutContext ctx, LayoutNode frame, (int X, string Text, bool Truncated)? drawn)
    {
        if (drawn is not { } d || d.Text.Length == 0) return false;
        int x = frame.Screen.X + d.X, y = frame.Screen.Y;
        if (y < 0 || y >= ctx.Cells.Height || x < 0 || x + d.Text.Length > ctx.Cells.Width) return false;
        return Inspectors.Row(ctx.Cells, x, y, d.Text.Length) == d.Text;
    }

    private static void WideTitle(LayoutRuleSink sink, LayoutNode n, CellRect top, string title, int minBudget)
    {
        int wide = LayoutTextMetrics.WideUnits(title);
        if (wide > 0 && title.Length + wide > minBudget)
            sink.Add(LayoutRules.TitleClipped, LayoutFindingClasses.Warning, "medium", n, top,
                $"Window title {Q(title)} fits as TSharpVision 1.1 draws it (one cell per character) but contains {wide} wide character(s) a terminal may render two columns wide, which can push it into the frame icons.",
                $"length={title.Length}; wide={wide}; smallestBudget={minBudget}");
    }

    // ── text.label-clipped (TLabel) ─────────────────────────────────────────

    private static void Labels(LayoutContext ctx, LayoutRuleSink sink, List<LayoutNode> noSource)
    {
        const string rule = LayoutRules.LabelClipped;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var n in ctx.Nodes.Where(n => n.Role == LayoutRole.Label && Drawable(n)))
        {
            any = true;
            if (n.Markup is not { } markup) { sink.Unavailable(rule, NoMarkup, n.Path); noSource.Add(n); continue; }
            int width = n.Bounds.Width, w = LayoutTextMetrics.CStrLen(n.MeasuredMarkup!);
            string shown = LayoutTextMetrics.Shown(markup);
            if (w == 0) continue;
            // TLabel.Draw: moveCStr(1, Text) on row 0 of a size.x buffer.
            var rect = new CellRect(n.Screen.X + 1, n.Screen.Y, w, 1);
            if (1 + w > width)
            {
                int lost = 1 + w - width;
                sink.Add(rule, LayoutFindingClasses.Error, "high", n, rect,
                    $"Label {Q(shown)} ({w}{(n.MarkupTruncated ? "+" : "")} cell(s), mnemonic markers excluded) does not fit its {width}-column view: TLabel draws from column 1, so {lost} cell(s) are cut. Make the label at least {w + 1} columns wide.",
                    $"markup={Q(markup)}; cells={w}; width={width}; needWidth={w + 1}; observed={Observed(ctx, n.Screen with { Height = 1 })}");
                continue;
            }
            if (n.MarkupTruncated) { sink.Unavailable(rule, "The label text exceeds the inspection limit; its full length is unknown.", n.Path); continue; }
            AddOwnerCut(ctx, sink, rule, n, rect, "label", shown);
            int wide = LayoutTextMetrics.WideUnits(shown);
            if (wide > 0 && 1 + w + wide > width)
                sink.Add(rule, LayoutFindingClasses.Warning, "medium", n, rect,
                    $"Label {Q(shown)} fits as TSharpVision 1.1 draws it but contains {wide} wide character(s) a terminal may render two columns wide. Allow {w + wide + 1} columns.",
                    $"cells={w}; wide={wide}; width={width}");
        }
        Report(sink, rule, any);
    }

    // ── text.static-clipped (TStaticText / TParamText) ─────────────────────

    private static void Statics(LayoutContext ctx, LayoutRuleSink sink, List<LayoutNode> noSource)
    {
        const string rule = LayoutRules.StaticClipped;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var n in ctx.Nodes.Where(n => n.Role == LayoutRole.StaticText && Drawable(n)))
        {
            any = true;
            if (n.Markup is not { } text) { sink.Unavailable(rule, NoMarkup, n.Path); noSource.Add(n); continue; }
            int width = n.Bounds.Width, height = n.Bounds.Height;
            var rows = LayoutTextMetrics.StaticRows(n.MeasuredMarkup!, width, height + 64);
            if (rows.Count > height)
            {
                var hidden = rows[height];
                sink.Add(rule, LayoutFindingClasses.Error, "high", n, n.Screen,
                    $"Static text needs {(rows.Count > height + 63 ? "more than " + (height + 63) : rows.Count.ToString())}{(n.MarkupTruncated ? "+" : "")} row(s) at width {width} (TStaticText word wrap) but the view has {height}; the text from row {height + 1} on ({Q(hidden.Text.TrimStart())}…) is not shown. Make the view taller or wider, or shorten the text.",
                    $"rowsNeeded={rows.Count}{(n.MarkupTruncated ? "+" : "")}; height={height}; width={width}; firstHiddenRow={Q(hidden.Text)}; textLength={text.Length}{(n.MarkupTruncated ? "+" : "")}");
                continue;
            }
            if (n.MarkupTruncated) { sink.Unavailable(rule, "The static text exceeds the inspection limit; its full length is unknown.", n.Path); continue; }
            for (int r = 0; r < rows.Count; r++)
            {
                var (x, rowText) = rows[r];
                string trimmed = rowText.TrimEnd();
                if (trimmed.Length == 0) continue;
                var seg = new CellRect(n.Screen.X + Math.Max(0, x), n.Screen.Y + r, Math.Min(trimmed.Length, width), 1);
                if (OwnerCut(n, seg) > 0) { AddOwnerCut(ctx, sink, rule, n, seg, "static text row", trimmed); break; }
                int wide = LayoutTextMetrics.WideUnits(trimmed);
                if (wide > 0 && x + trimmed.Length + wide > width)
                {
                    sink.Add(rule, LayoutFindingClasses.Warning, "medium", n, seg,
                        $"Static text row {r + 1} {Q(trimmed)} fits as TSharpVision 1.1 draws it but contains {wide} wide character(s) a terminal may render two columns wide.",
                        $"row={r + 1}; cells={trimmed.Length}; wide={wide}; width={width}");
                    break;
                }
            }
        }
        Report(sink, rule, any);
    }

    // ── text.cluster-clipped (TCheckBoxes / TRadioButtons / TMultiCheckBoxes) ─

    private static void Clusters(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.ClusterClipped;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var n in ctx.Nodes.Where(n => n.Role == LayoutRole.Cluster && Drawable(n)))
        {
            if (n.Property("items") is not { } joined) { sink.Unavailable(rule, "The inspection does not expose this cluster's items.", n.Path); continue; }
            any = true;
            if (joined.Length == 0) continue;
            bool cut = joined.Length == InspectionLimits.Default.MaxText + 1 && joined.EndsWith('…');
            var items = (cut ? joined[..^1] : joined).Split(" | ");
            if (cut)
            {
                items = items[..^1];   // the last one is partial
                sink.Unavailable(rule, $"The item list exceeds the inspection limit; items after #{items.Length} were not checked.", n.Path);
            }
            int width = n.Bounds.Width, height = n.Bounds.Height;
            var widths = items.Select(i => i.Length).ToArray();
            for (int k = 0; k < items.Length; k++)
            {
                int col = LayoutTextMetrics.ClusterColumn(widths, height, k), row = k % height, w = widths[k];
                var rect = new CellRect(n.Screen.X + col + 5, n.Screen.Y + row, w, 1);
                if (col >= width || col + w + 5 >= TDrawBufferMax)
                {
                    sink.Add(rule, LayoutFindingClasses.Error, "high", n, new CellRect(n.Screen.X + col, n.Screen.Y + row, w + 5, 1),
                        $"Cluster item {k + 1} {Q(items[k])} is not drawn: with {height} row(s) it falls into a column starting at {col}, beyond the {width}-column view. Make the cluster taller or wider.",
                        $"item={k + 1}/{items.Length}; column={col}; row={row}; width={width}; height={height}");
                    continue;
                }
                if (col + 5 + w > width)
                {
                    sink.Add(rule, LayoutFindingClasses.Error, "high", n, rect,
                        $"Cluster item {k + 1} {Q(items[k])} ({w} cell(s)) does not fit: it starts at column {col + 5} after the box, so it needs {col + 5 + w} columns but the view has {width}; {col + 5 + w - width} cell(s) are cut. Widen the cluster to {col + 5 + w} columns or shorten the item.",
                        $"item={k + 1}/{items.Length}; column={col}; textStart={col + 5}; cells={w}; width={width}; height={height}; observed={Observed(ctx, rect with { X = n.Screen.X + col, Width = Math.Max(0, width - col) })}");
                    continue;
                }
                AddOwnerCut(ctx, sink, rule, n, rect, $"cluster item {k + 1}", items[k]);
            }
        }
        Report(sink, rule, any);
    }

    private const int TDrawBufferMax = 256;   // TDrawBuffer.MaxViewWidth: DrawMultiBox skips items with col + len + 5 ≥ 256

    // ── text.cells-heuristic ────────────────────────────────────────────────

    private static void CellsHeuristic(LayoutContext ctx, LayoutRuleSink sink, List<LayoutNode> noSource)
    {
        const string rule = LayoutRules.CellsHeuristic;
        if (!sink.Enabled(rule)) return;
        // Views whose source text is missing (independent of whether the label/static rules are enabled).
        var targets = ctx.Nodes.Where(n => n.Role is LayoutRole.Label or LayoutRole.StaticText && Drawable(n) && n.Markup is null).ToList();
        if (targets.Count == 0) { sink.NotApplicable(rule); return; }
        sink.Evaluated(rule);
        foreach (var n in targets)
        {
            if (n.Visible is not { IsEmpty: false } v) continue;
            var r = n.Screen;
            int lastRow = n.Role == LayoutRole.Label ? r.Y : r.Bottom - 1, x = r.Right - 1;
            if (!Geometry.Contains(v, new CellRect(x - 1, lastRow, 2, 1)) || r.Width < 2) continue;
            char a = ctx.Cells[x - 1, lastRow].Character, b = ctx.Cells[x, lastRow].Character;
            if (!char.IsLetterOrDigit(a) || !char.IsLetterOrDigit(b)) continue;
            if (n.Role == LayoutRole.StaticText)
            {
                bool full = true;
                for (int y = r.Y; y < r.Bottom && full; y++) full = Inspectors.Row(ctx.Cells, r.X, y, r.Width).Any(c => c is not (' ' or '\0'));
                if (!full) continue;
            }
            sink.Add(rule, LayoutFindingClasses.Warning, "low", n, new CellRect(x - 1, lastRow, 2, 1),
                $"{n.Kind} text runs into its last column ({Q(Inspectors.Row(ctx.Cells, r.X, lastRow, r.Width).TrimEnd())}) and may be cut off. The source text is not exposed by the target's Testing payload, so this is a cell-only suspicion: update the target's Testing payload for an exact check.",
                $"lastCells='{a}{b}' at ({x - 1},{lastRow}); rect={r}; source=unavailable");
        }
    }
}
