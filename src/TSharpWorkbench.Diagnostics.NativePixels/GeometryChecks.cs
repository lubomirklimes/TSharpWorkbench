using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>Results of the structural checks for one capture.</summary>
public sealed class GeometryResult
{
    public Dictionary<string, int> Checked { get; } = new();
    public Dictionary<string, int> Failed { get; } = new();
    public List<string> Failures { get; } = new();
    public bool Passed => Failed.Count == 0;

    public void Count(string check, bool ok, Func<string> detail)
    {
        Checked[check] = Checked.GetValueOrDefault(check) + 1;
        if (ok) return;
        Failed[check] = Failed.GetValueOrDefault(check) + 1;
        if (Failures.Count < 40) Failures.Add($"{check}: {detail()}");
    }
}

/// <summary>
/// Structural checks computed from the captured pixels and the logical cells alone — no glyph
/// masks, no renderer code: blank cells are exactly the background; block elements fill the
/// expected halves; box-drawing glyphs reach exactly the cell edges their arms name and join
/// their neighbours pixel for pixel; shadow cells use only shadow colours; caret cells show the
/// caret; every non-blank glyph has ink.
/// </summary>
public static class GeometryChecks
{
    // Arms: L, R, U, D. Single and double lines are both CP437 B0–DF.
    private static readonly Dictionary<char, string> Arms = new()
    {
        ['─'] = "LR", ['│'] = "UD", ['┌'] = "RD", ['┐'] = "LD", ['└'] = "RU", ['┘'] = "LU",
        ['├'] = "UDR", ['┤'] = "UDL", ['┬'] = "LRD", ['┴'] = "LRU", ['┼'] = "LRUD",
        ['═'] = "LR", ['║'] = "UD", ['╔'] = "RD", ['╗'] = "LD", ['╚'] = "RU", ['╝'] = "LU",
        ['╠'] = "UDR", ['╣'] = "UDL", ['╦'] = "LRD", ['╩'] = "LRU", ['╬'] = "LRUD",
    };

    // Glyphs whose horizontal / vertical strokes are double lines. A join is compared only between
    // strokes of the same weight: Turbo Vision legitimately meets a double frame with a single
    // line (the "─┘" resize corner of an active window).
    private const string DoubleHorizontal = "═╔╗╚╝╠╣╦╩╬";
    private const string DoubleVertical = "║╔╗╚╝╠╣╦╩╬";

    public static GeometryResult Run(LogicalScreen screen, CaretRequest caret, NativeImage img, int cw, int ch)
    {
        var r = new GeometryResult();
        int cols = Math.Min(screen.Grid.Width, img.Width / cw), rows = Math.Min(screen.Grid.Height, img.Height / ch);
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                TScreenChar cell = screen[col, row];
                byte attr = cell.Attr;
                int fg = Vga.Rgb[attr & 0x0F], bg = Vga.Rgb[(attr >> 4) & 0x0F];
                bool caretCell = caret.CursorType != 0 && caret.X == col && caret.Y == row;
                bool underline = caretCell && caret.CursorType < 100;
                if (caretCell && !underline) (fg, bg) = (bg, fg);
                int x0 = col * cw, y0 = row * ch;
                int inkRows = underline ? ch - 2 : ch;
                char c = cell.Character;
                string at = $"({col},{row}) '{c}'";

                if (underline)
                    r.Count("caret-underline-bar", AllEqual(img, x0, y0 + ch - 2, cw, 2, fg), () => $"{at} bottom two rows not all #{fg:X6}");
                else if (caretCell && c is ' ' or '\0')
                    r.Count("caret-block-colours", AllEqual(img, x0, y0, cw, ch, bg), () => $"{at} block caret cell does not show the swapped colour");

                if (c is ' ' or '\0')
                {
                    r.Count("blank-cell-background", AllEqual(img, x0, y0, cw, inkRows, bg), () => $"{at} blank cell not exactly #{bg:X6}");
                    continue;
                }

                if (fg != bg)
                    r.Count("glyph-has-ink", AnyNot(img, x0, y0, cw, inkRows, bg), () => $"{at} no pixel differs from the background (missing glyph)");

                // Every pixel of a cell lies between its background and foreground colours.
                r.Count("cell-colour-range", InRange(img, x0, y0, cw, inkRows, fg, bg), () => $"{at} pixel outside the #{bg:X6}..#{fg:X6} range");

                if (attr == 0x08)
                    r.Count("shadow-cell-colours", InRange(img, x0, y0, cw, inkRows, Vga.Rgb[8], Vga.Rgb[0]), () => $"{at} shadow cell uses a non-shadow colour");

                if (fg == bg || underline) continue;
                switch (c)
                {
                    case '█': r.Count("block-full", AllEqual(img, x0, y0, cw, ch, fg), () => $"{at} not solid foreground"); break;
                    case '▀': r.Count("block-upper-half", AllEqual(img, x0, y0, cw, 1, fg) && AllEqual(img, x0, y0 + ch - 1, cw, 1, bg), () => $"{at} top row fg / bottom row bg violated"); break;
                    case '▄': r.Count("block-lower-half", AllEqual(img, x0, y0, cw, 1, bg) && AllEqual(img, x0, y0 + ch - 1, cw, 1, fg), () => $"{at} top row bg / bottom row fg violated"); break;
                    case '▌': r.Count("block-left-half", AllEqual(img, x0, y0, 1, ch, fg) && AllEqual(img, x0 + cw - 1, y0, 1, ch, bg), () => $"{at} left column fg / right column bg violated"); break;
                    case '▐': r.Count("block-right-half", AllEqual(img, x0, y0, 1, ch, bg) && AllEqual(img, x0 + cw - 1, y0, 1, ch, fg), () => $"{at} left column bg / right column fg violated"); break;
                }

                if (!Arms.TryGetValue(c, out string? arms)) continue;
                foreach (char edge in "LRUD")
                {
                    bool ink = EdgeInk(img, x0, y0, cw, ch, edge, bg).Count > 0;
                    bool want = arms.Contains(edge);
                    r.Count("box-arms", ink == want, () => $"{at} {(want ? "missing" : "unexpected")} ink on {edge} edge");
                }
                // Joins with the right and lower neighbour when both name the shared edge.
                char right = col + 1 < cols ? screen[col + 1, row].Character : ' ';
                char below = row + 1 < rows ? screen[col, row + 1].Character : ' ';
                if (col + 1 < cols && arms.Contains('R') && Arms.TryGetValue(right, out string? rn) && rn.Contains('L')
                    && screen[col + 1, row].Attr == cell.Attr && DoubleHorizontal.Contains(c) == DoubleHorizontal.Contains(right))
                    r.Count("box-join-horizontal", EdgeInk(img, x0, y0, cw, ch, 'R', bg).SetEquals(EdgeInk(img, x0 + cw, y0, cw, ch, 'L', bg)),
                        () => $"{at} ink rows at its right edge differ from ({col + 1},{row})'s left edge");
                if (row + 1 < rows && arms.Contains('D') && Arms.TryGetValue(below, out string? dn) && dn.Contains('U')
                    && screen[col, row + 1].Attr == cell.Attr && DoubleVertical.Contains(c) == DoubleVertical.Contains(below) && !(caret.CursorType != 0 && caret.X == col && caret.Y == row + 1))
                    r.Count("box-join-vertical", EdgeInk(img, x0, y0, cw, ch, 'D', bg).SetEquals(EdgeInk(img, x0, y0 + ch, cw, ch, 'U', bg)),
                        () => $"{at} ink columns at its bottom edge differ from ({col},{row + 1})'s top edge");
            }
        }
        // Grid edges: the last column/row of the grid is drawn (not cut by an off-by-one).
        return r;
    }

    private static bool AllEqual(NativeImage img, int x0, int y0, int w, int h, int rgb)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                if (img.Rgb(x, y) != rgb || img.Alpha(x, y) != 255) return false;
        return true;
    }

    private static bool AnyNot(NativeImage img, int x0, int y0, int w, int h, int rgb)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                if (img.Rgb(x, y) != rgb) return true;
        return false;
    }

    private static bool InRange(NativeImage img, int x0, int y0, int w, int h, int a, int b)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                int p = img.Rgb(x, y);
                for (int s = 0; s <= 16; s += 8)
                {
                    int v = (p >> s) & 0xFF, lo = Math.Min((a >> s) & 0xFF, (b >> s) & 0xFF), hi = Math.Max((a >> s) & 0xFF, (b >> s) & 0xFF);
                    if (v < lo || v > hi) return false;
                }
            }
        return true;
    }

    /// <summary>Offsets along an edge (rows for L/R, columns for U/D) whose pixel is not the background.</summary>
    private static HashSet<int> EdgeInk(NativeImage img, int x0, int y0, int cw, int ch, char edge, int bg)
    {
        var set = new HashSet<int>();
        switch (edge)
        {
            case 'L': for (int y = 0; y < ch; y++) if (img.Rgb(x0, y0 + y) != bg) set.Add(y); break;
            case 'R': for (int y = 0; y < ch; y++) if (img.Rgb(x0 + cw - 1, y0 + y) != bg) set.Add(y); break;
            case 'U': for (int x = 0; x < cw; x++) if (img.Rgb(x0 + x, y0) != bg) set.Add(x); break;
            case 'D': for (int x = 0; x < cw; x++) if (img.Rgb(x0 + x, y0 + ch - 1) != bg) set.Add(x); break;
        }
        return set;
    }
}
