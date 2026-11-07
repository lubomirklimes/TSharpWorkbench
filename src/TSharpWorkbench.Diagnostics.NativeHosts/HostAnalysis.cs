using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativeHosts;

/// <summary>The located cell grid inside a host capture (fractional pitch allowed).</summary>
public sealed record Geometry(double X0, double Y0, double CellW, double CellH, int Cols, int Rows)
{
    public (int X0, int Y0, int X1, int Y1) Cell(int c, int r) =>
        ((int)Math.Round(X0 + c * CellW), (int)Math.Round(Y0 + r * CellH),
         (int)Math.Round(X0 + (c + 1) * CellW), (int)Math.Round(Y0 + (r + 1) * CellH));

    public int Right => (int)Math.Round(X0 + Cols * CellW);
    public int Bottom => (int)Math.Round(Y0 + Rows * CellH);
}

public sealed class CalibrationResult
{
    public Geometry? Geometry { get; set; }
    public int MarkerWidth { get; set; }
    public int MarkerHeight { get; set; }
    public int ColsFromPixels { get; set; }
    public int RowsFromPixels { get; set; }
    public string[] Background { get; set; } = [];
    public string[] Foreground { get; set; } = [];
    public int DistinctColours { get; set; }
    public List<string> Checks { get; } = new();
    public List<string> Failures { get; } = new();
    public string? Unavailable { get; set; }
    public int[] BackgroundRgb { get; set; } = [];
}

public sealed class CellRuleStats
{
    public int Checked { get; set; }
    public int Failed { get; set; }
}

public sealed class CellCheck
{
    public int CellsChecked { get; set; }
    public int CellsFailed { get; set; }
    public SortedDictionary<string, CellRuleStats> Rules { get; } = new();
    public List<string> Samples { get; } = new();
    public List<string> Observations { get; } = new();
    public DiacriticSummary Diacritics { get; } = new();
    public bool Passed => CellsFailed == 0;

    internal void Count(string rule, bool ok, string? sample)
    {
        if (!Rules.TryGetValue(rule, out var s)) Rules[rule] = s = new CellRuleStats();
        s.Checked++;
        if (ok) return;
        s.Failed++;
        if (sample != null && Samples.Count < 40) Samples.Add(sample);
    }
}

public sealed class DiacriticSummary
{
    public int Pairs { get; set; }
    public int Failed { get; set; }
    public int TouchingCellTop { get; set; }
    public List<string> Details { get; } = new();
}

public sealed class UpdateCheck
{
    public int LogicalChanged { get; set; }
    public int PixelChanged { get; set; }
    public int Stale { get; set; }
    public int Spurious { get; set; }
    /// <summary>Pixel changes in an unchanged cell next to a changed one: glyph ink overhanging into the neighbour (host font metrics); reported, not a failure.</summary>
    public int NeighbourOverhang { get; set; }
    public List<string> Samples { get; } = new();
    public bool Passed => Stale == 0 && Spurious == 0;
}

public sealed class CursorCheck
{
    public string Expected { get; set; } = "";
    public int Captures { get; set; }
    public List<string> VaryingCells { get; } = new();
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// Independent checks of a real host capture against the cells the host was asked to show.
/// Colours are the host's own rendered palette, measured from the calibration swatches, so the
/// checks hold for any host palette, font and antialiasing; they are geometry/colour checks, not a
/// bit comparison with a reference image.
/// </summary>
public static class HostAnalysis
{
    public const int BgTolerance = 10, InkThreshold = 40;

    public static int Dist(int a, int b) => Math.Max(Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)),
        Math.Max(Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)), Math.Abs((a & 0xFF) - (b & 0xFF))));

    public static string Hex(int rgb) => $"#{rgb & 0xFFFFFF:X6}";

    private static bool IsMarker(int p)
    {
        int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
        return r >= 140 && b >= 110 && g <= 110 && Math.Abs(r - b) <= 110;
    }

    /// <summary>Locates the grid from the two marker cells and measures the host's 16 colours.</summary>
    public static CalibrationResult Calibrate(NativeImage img, int cols, int rows)
    {
        var result = new CalibrationResult();
        var comps = Components(img, IsMarker, minArea: 12);
        if (comps.Count < 2)
        {
            result.Unavailable = comps.Count == 0 && IsUniform(img)
                ? "capture is uniform (blank surface)"
                : $"calibration markers not found ({comps.Count} marker regions)";
            return result;
        }
        var tl = comps.MinBy(c => c.X0 + c.Y0)!;
        var br = comps.MaxBy(c => c.X1 + c.Y1)!;
        result.MarkerWidth = tl.X1 - tl.X0;
        result.MarkerHeight = tl.Y1 - tl.Y0;
        double cw = cols > 1 ? (br.X0 - tl.X0) / (double)(cols - 1) : result.MarkerWidth;
        double ch = rows > 1 ? (br.Y0 - tl.Y0) / (double)(rows - 1) : result.MarkerHeight;
        // Independent of the host's claimed grid: the horizontal pitch from the colour swatches (15 cells
        // between the left edge of swatch 1 and the right edge of swatch 15), else the marker width.
        double pitchX = SwatchPitch(img, tl, result.MarkerHeight) ?? result.MarkerWidth;
        result.ColsFromPixels = (int)Math.Round((br.X0 - tl.X0) / pitchX) + 1;
        result.RowsFromPixels = (int)Math.Round((br.Y0 - tl.Y0) / (double)result.MarkerHeight) + 1;
        var g = new Geometry(tl.X0, tl.Y0, cw, ch, cols, rows);
        result.Geometry = g;

        Check(result, Math.Abs(result.MarkerWidth - cw) <= 1.5 && Math.Abs(result.MarkerHeight - ch) <= 1.5,
            $"marker cell {result.MarkerWidth}x{result.MarkerHeight}px matches pitch {cw:0.##}x{ch:0.##}px");
        Check(result, result.ColsFromPixels == cols && result.RowsFromPixels == rows,
            $"grid measured from pixels {result.ColsFromPixels}x{result.RowsFromPixels} equals host grid {cols}x{rows}");
        Check(result, br.X1 - br.X0 == result.MarkerWidth || Math.Abs(br.X1 - br.X0 - result.MarkerWidth) <= 1,
            "bottom-right marker has the same cell size (uniform grid)");
        Check(result, g.Right <= img.Width && g.Bottom <= img.Height, $"grid {g.Right}x{g.Bottom}px lies inside the capture {img.Width}x{img.Height}");

        if (cols >= Calibration.SwatchX + 16 && rows >= 4)
        {
            var bg = new int[16];
            var fg = new int[16];
            for (int i = 0; i < 16; i++)
            {
                bg[i] = Mode(img, Inset(g.Cell(Calibration.SwatchX + i, 1), 1));
                fg[i] = Mode(img, Inset(g.Cell(Calibration.SwatchX + i, 2), Math.Max(1, (int)(cw / 4))));
            }
            result.BackgroundRgb = bg;
            result.Background = bg.Select(Hex).ToArray();
            result.Foreground = fg.Select(Hex).ToArray();
            result.DistinctColours = bg.Distinct().Count();
            Check(result, result.DistinctColours == 16, $"16 distinct background colours rendered ({result.DistinctColours})");
            int fgSame = Enumerable.Range(1, 15).Count(i => Dist(fg[i], bg[i]) <= 8);
            Check(result, fgSame >= 13, $"foreground full blocks render the same palette as backgrounds ({fgSame}/15 within 8)");
            Check(result, Dist(bg[0], bg[15]) > 150, "black and bright white are far apart (contrast)");
        }
        else result.Checks.Add("palette swatches do not fit this grid; palette taken from a previous calibration");
        return result;
    }

    private static double? SwatchPitch(NativeImage img, Comp tl, int markerHeight)
    {
        int y = tl.Y0 + markerHeight + markerHeight / 2;   // middle of row 1
        if (y >= img.Height) return null;
        int bg = img.Rgb(Math.Min(img.Width - 1, tl.X1 + 1), y);
        int x = tl.X1;
        while (x < img.Width && Dist(img.Rgb(x, y), bg) <= 4) x++;          // left edge of swatch 1
        if (x >= img.Width) return null;
        int start = x, edges = 0, last = img.Rgb(x, y), end = x;
        for (; x < img.Width; x++)
        {
            int p = img.Rgb(x, y);
            if (Dist(p, last) <= 4) continue;
            if (Dist(p, bg) <= 4) { end = x; break; }                        // back to the background after swatch 15
            edges++; last = p;
        }
        return edges >= 13 && end > start ? (end - start) / 15.0 : null;
    }

    private static void Check(CalibrationResult r, bool ok, string text)
    {
        r.Checks.Add((ok ? "PASS " : "FAIL ") + text);
        if (!ok) r.Failures.Add(text);
    }

    // ── per-cell checks ─────────────────────────────────────────────────────

    private const int L = 1, R = 2, U = 4, D = 8;
    private static readonly Dictionary<char, int> Box = BuildBox();
    private const string SingleLines = "─│┌┐└┘├┤┬┴┼", DoubleLines = "═║╔╗╚╝╠╣╦╩╬";

    // Strokes of different line styles (e.g. ║ over ┘ at a window's resize corner) sit at different
    // offsets in every font; only same-style neighbours are expected to join.
    private static bool SameStyle(char a, char b) =>
        (SingleLines.Contains(a) && SingleLines.Contains(b)) || (DoubleLines.Contains(a) && DoubleLines.Contains(b));

    private static Dictionary<char, int> BuildBox()
    {
        var m = new Dictionary<char, int>();
        void Add(string chars, int dirs) { foreach (char c in chars) m[c] = dirs; }
        Add("─═", L | R); Add("│║", U | D);
        Add("┌╔╒╓", R | D); Add("┐╗╕╖", L | D); Add("└╚╘╙", R | U); Add("┘╝╛╜", L | U);
        Add("├╠╞╟", U | D | R); Add("┤╣╡╢", U | D | L); Add("┬╦╤╥", L | R | D); Add("┴╩╧╨", L | R | U);
        Add("┼╬╪╫", L | R | U | D);
        return m;
    }

    private static readonly Dictionary<char, char> DiacriticBase = new()
    {
        ['á'] = 'a', ['č'] = 'c', ['é'] = 'e', ['ě'] = 'e', ['ň'] = 'n', ['ó'] = 'o', ['ř'] = 'r', ['š'] = 's',
        ['ú'] = 'u', ['ů'] = 'u', ['ý'] = 'y', ['ž'] = 'z',
        ['Á'] = 'A', ['Č'] = 'C', ['É'] = 'E', ['Ě'] = 'E', ['Í'] = 'I', ['Ň'] = 'N', ['Ó'] = 'O', ['Ř'] = 'R',
        ['Š'] = 'S', ['Ú'] = 'U', ['Ů'] = 'U', ['Ý'] = 'Y', ['Ž'] = 'Z',
    };

    public static bool IsBlank(char c) => c is ' ' or '\0' or ' ';

    public static CellCheck CheckCells(NativeImage img, Geometry g, int[] palette, HostScreen e, ISet<(int, int)> exempt)
    {
        var check = new CellCheck();
        var ink = new Dictionary<(int, int), bool[]>(); // per box cell ink mask for continuity
        for (int r = 0; r < Math.Min(e.Height, g.Rows); r++)
            for (int c = 0; c < Math.Min(e.Width, g.Cols); c++)
            {
                if (exempt.Contains((c, r))) continue;
                char ch = e.Char(c, r);
                byte attr = e.Attr(c, r);
                int pf = palette[attr & 0x0F], pb = palette[attr >> 4];
                var rect = g.Cell(c, r);
                int w = rect.X1 - rect.X0, h = rect.Y1 - rect.Y0;
                if (w <= 0 || h <= 0) continue;
                if (rect.X0 < 0 || rect.Y0 < 0 || rect.X1 > img.Width || rect.Y1 > img.Height)
                {
                    check.CellsChecked++; check.CellsFailed++;
                    check.Count("inside-capture", false, $"({c},{r}) lies outside the capture");
                    continue;
                }
                string ctx = attr == 0x08 ? "shadow:" : "";
                int contrast = Dist(pf, pb);
                // Ink is anything clearly away from the background; for low-contrast attributes
                // (e.g. bright white on light grey) the threshold scales with the cell's own contrast.
                int inkThreshold = Math.Max(BgTolerance + 2, Math.Min(InkThreshold, contrast / 2));
                int n = w * h, bgCount = 0, inkCount = 0, nearFg = 0, fgSide = 0;
                var mask = new bool[n];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int p = img.Rgb(rect.X0 + x, rect.Y0 + y);
                        int db = Dist(p, pb);
                        if (db <= BgTolerance) bgCount++;
                        if (db > inkThreshold)
                        {
                            inkCount++; mask[y * w + x] = true;
                            int df = Dist(p, pf);
                            if (df < db) fgSide++;
                            if (df <= Math.Max(40, contrast / 4)) nearFg++;
                        }
                    }
                double bgFrac = bgCount / (double)n, inkFrac = inkCount / (double)n;
                bool distinct = contrast >= 2 * BgTolerance + 4;
                string where = $"({c},{r}) '{ch}' attr {attr:X2}";
                check.CellsChecked++;
                int failsBefore = check.Rules.Values.Sum(v => v.Failed);

                if (IsBlank(ch) || !distinct)
                {
                    double min = IsBlank(ch) ? 0.90 : 0.60;
                    check.Count(ctx + "background", bgFrac >= min, $"{where}: background {bgFrac:P0} of {Hex(pb)} (< {min:P0})");
                }
                else if (ch == '█')
                    check.Count(ctx + "full-block", nearFg / (double)n >= 0.55, $"{where}: full block covers {nearFg / (double)n:P0} in {Hex(pf)}");
                else if (ch is '▀' or '▄' or '▌' or '▐')
                {
                    (double a, double b) = Halves(mask, w, h, ch is '▀' or '▄');
                    bool firstHalf = ch is '▀' or '▌';
                    double inside = firstHalf ? a : b, outside = firstHalf ? b : a;
                    check.Count(ctx + "half-block", inside >= 0.6 && outside <= 0.35, $"{where}: half block ink {inside:P0} inside, {outside:P0} outside");
                }
                else if (ch is '░' or '▒' or '▓')
                    check.Count(ctx + "shade", inkFrac >= 0.05, $"{where}: shade ink {inkFrac:P0}");
                else if (Box.TryGetValue(ch, out int dirs))
                {
                    bool ok = true;
                    var missing = new List<string>();
                    if ((dirs & L) != 0 && !EdgeInk(mask, w, h, 'L')) { ok = false; missing.Add("left"); }
                    if ((dirs & R) != 0 && !EdgeInk(mask, w, h, 'R')) { ok = false; missing.Add("right"); }
                    if ((dirs & U) != 0 && !EdgeInk(mask, w, h, 'U')) { ok = false; missing.Add("top"); }
                    if ((dirs & D) != 0 && !EdgeInk(mask, w, h, 'D')) { ok = false; missing.Add("bottom"); }
                    check.Count(ctx + "box-edges", ok, $"{where}: line does not reach {string.Join("/", missing)} edge");
                    check.Count(ctx + "ink-colour", fgSide > 0, $"{where}: no ink pixel is closer to {Hex(pf)} than to the background");
                    ink[(c, r)] = mask;
                }
                else
                {
                    check.Count(ctx + "background", bgFrac >= 0.25, $"{where}: background {bgFrac:P0} of {Hex(pb)}");
                    check.Count(ctx + "ink", inkCount >= 2, $"{where}: no glyph ink");
                    if (inkCount >= 2) check.Count(ctx + "ink-colour", fgSide > 0, $"{where}: no ink pixel is closer to {Hex(pf)} than to the background");
                }
                if (check.Rules.Values.Sum(v => v.Failed) > failsBefore) check.CellsFailed++;
            }

        // Continuity: adjacent connected box cells share ink rows (horizontal) or columns (vertical).
        foreach (var ((c, r), mask) in ink)
        {
            char ch = e.Char(c, r);
            int dirs = Box[ch];
            var rect = g.Cell(c, r);
            int w = rect.X1 - rect.X0, h = rect.Y1 - rect.Y0;
            if ((dirs & R) != 0 && ink.TryGetValue((c + 1, r), out var right) && (Box[e.Char(c + 1, r)] & L) != 0 && SameStyle(ch, e.Char(c + 1, r)))
            {
                var rr = g.Cell(c + 1, r);
                int w2 = rr.X1 - rr.X0, h2 = rr.Y1 - rr.Y0;
                var a = Enumerable.Range(0, h).Where(y => mask[y * w + w - 1]).ToHashSet();
                var b = Enumerable.Range(0, h2).Where(y => right[y * w2]).ToHashSet();
                check.Count("box-continuity", a.Overlaps(b), $"({c},{r})→({c + 1},{r}) '{ch}{e.Char(c + 1, r)}': horizontal line breaks at the cell boundary");
            }
            if ((dirs & D) != 0 && ink.TryGetValue((c, r + 1), out var below) && (Box[e.Char(c, r + 1)] & U) != 0 && SameStyle(ch, e.Char(c, r + 1)))
            {
                var rb = g.Cell(c, r + 1);
                int w2 = rb.X1 - rb.X0;
                var a = Enumerable.Range(0, w).Where(x => mask[(h - 1) * w + x]).ToHashSet();
                var b = Enumerable.Range(0, w2).Where(x => below[x]).ToHashSet();
                check.Count("box-continuity", a.Overlaps(b), $"({c},{r})↓({c},{r + 1}) '{ch}{e.Char(c, r + 1)}': vertical line breaks at the cell boundary");
            }
        }

        CheckShadeOrder(img, g, palette, e, check);
        CheckDiacritics(img, g, palette, e, exempt, check.Diacritics);
        if (check.Diacritics.Failed > 0) check.Count("diacritics", false, $"{check.Diacritics.Failed}/{check.Diacritics.Pairs} accented letters have no ink above their base letter");
        else if (check.Diacritics.Pairs > 0) check.Count("diacritics", true, null);
        return check;
    }

    private static void CheckShadeOrder(NativeImage img, Geometry g, int[] palette, HostScreen e, CellCheck check)
    {
        for (int r = 0; r < Math.Min(e.Height, g.Rows); r++)
            for (int c = 0; c + 2 < Math.Min(e.Width, g.Cols); c++)
            {
                if (e.Char(c, r) != '░' || e.Char(c + 1, r) != '▒' || e.Char(c + 2, r) != '▓') continue;
                byte attr = e.Attr(c, r);
                if (e.Attr(c + 1, r) != attr || e.Attr(c + 2, r) != attr) continue;
                double[] d = [InkFraction(img, g.Cell(c, r), palette[attr >> 4]), InkFraction(img, g.Cell(c + 1, r), palette[attr >> 4]), InkFraction(img, g.Cell(c + 2, r), palette[attr >> 4])];
                // Coverage is not comparable once a host blends shades into one solid colour; compare mean distance instead.
                double[] m = [MeanDistance(img, g.Cell(c, r), palette[attr >> 4]), MeanDistance(img, g.Cell(c + 1, r), palette[attr >> 4]), MeanDistance(img, g.Cell(c + 2, r), palette[attr >> 4])];
                check.Count("shade-order", m[0] < m[1] && m[1] < m[2], $"({c},{r}) ░▒▓ darkness {m[0]:0}/{m[1]:0}/{m[2]:0} not increasing");
                check.Observations.Add($"shades at ({c},{r}): ink coverage {d[0]:P0}/{d[1]:P0}/{d[2]:P0}, mean distance from background {m[0]:0}/{m[1]:0}/{m[2]:0}");
            }
    }

    private static void CheckDiacritics(NativeImage img, Geometry g, int[] palette, HostScreen e, ISet<(int, int)> exempt, DiacriticSummary s)
    {
        var baseTop = new Dictionary<(char, byte), int>();
        for (int r = 0; r < Math.Min(e.Height, g.Rows); r++)
            for (int c = 0; c < Math.Min(e.Width, g.Cols); c++)
            {
                char ch = e.Char(c, r);
                if (DiacriticBase.ContainsValue(ch) && !baseTop.ContainsKey((ch, e.Attr(c, r))) && !exempt.Contains((c, r)))
                {
                    int t = TopInk(img, g.Cell(c, r), palette[e.Attr(c, r) >> 4]);
                    if (t >= 0) baseTop[(ch, e.Attr(c, r))] = t;
                }
            }
        var seen = new HashSet<(char, byte)>();
        for (int r = 0; r < Math.Min(e.Height, g.Rows); r++)
            for (int c = 0; c < Math.Min(e.Width, g.Cols); c++)
            {
                char ch = e.Char(c, r);
                byte attr = e.Attr(c, r);
                if (!DiacriticBase.TryGetValue(ch, out char b) || exempt.Contains((c, r)) || !seen.Add((ch, attr))) continue;
                if (!baseTop.TryGetValue((b, attr), out int bt)) continue;
                int at = TopInk(img, g.Cell(c, r), palette[attr >> 4]);
                s.Pairs++;
                bool ok = at >= 0 && at < bt - 1;
                if (!ok) s.Failed++;
                if (at == 0) s.TouchingCellTop++;
                s.Details.Add($"{ch}/{b} attr {attr:X2}: top ink row {at} vs {bt} {(ok ? "ok" : "FAIL")}{(at == 0 ? " (touches cell top)" : "")}");
            }
    }

    // ── comparisons between captures ────────────────────────────────────────

    // The outermost pixel columns are left out: antialiased glyphs of a neighbouring cell may overhang into them.
    // Caret detection uses the full cell (a bar caret is one pixel column at the cell's left edge).
    public static bool CellDiffers(NativeImage a, NativeImage b, (int X0, int Y0, int X1, int Y1) rect, bool fullCell = false)
    {
        int n = 0, diff = 0;
        bool inset = !fullCell && rect.X1 - rect.X0 > 4;
        int x0 = inset ? rect.X0 + 1 : rect.X0, x1 = inset ? rect.X1 - 1 : rect.X1;
        for (int y = rect.Y0; y < rect.Y1; y++)
            for (int x = x0; x < x1; x++) { n++; if (Dist(a.Rgb(x, y), b.Rgb(x, y)) > 24) diff++; }
        return diff >= (fullCell ? Math.Max(3, (rect.Y1 - rect.Y0) / 2) : Math.Max(3, n / 50));
    }

    private static bool VisiblyDifferent(HostScreen a, HostScreen b, int c, int r, int[] palette)
    {
        char ca = a.Char(c, r), cb = b.Char(c, r);
        byte aa = a.Attr(c, r), ab = b.Attr(c, r);
        if (ca == cb && aa == ab) return false;
        bool blankA = IsBlank(ca) || Dist(palette[aa & 15], palette[aa >> 4]) <= InkThreshold;
        bool blankB = IsBlank(cb) || Dist(palette[ab & 15], palette[ab >> 4]) <= InkThreshold;
        if (blankA && blankB) return Dist(palette[aa >> 4], palette[ab >> 4]) > BgTolerance;
        return true;
    }

    /// <summary>Cells whose pixels changed between two captures versus cells the host buffer changed.</summary>
    public static UpdateCheck CompareUpdate(NativeImage a, NativeImage b, Geometry g, int[] palette, HostScreen ea, HostScreen eb, ISet<(int, int)> exempt)
    {
        var u = new UpdateCheck();
        int cols = Math.Min(ea.Width, g.Cols), rows = Math.Min(ea.Height, g.Rows);
        var changed = new bool[cols, rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                changed[c, r] = VisiblyDifferent(ea, eb, c, r, palette);
        bool NextToChange(int c, int r) =>
            (c > 0 && changed[c - 1, r]) || (c + 1 < cols && changed[c + 1, r]) || (r > 0 && changed[c, r - 1]) || (r + 1 < rows && changed[c, r + 1]);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                if (exempt.Contains((c, r))) continue;
                bool logical = changed[c, r];
                bool pixels = CellDiffers(a, b, g.Cell(c, r));
                if (logical) u.LogicalChanged++;
                if (pixels) u.PixelChanged++;
                if (logical && !pixels) { u.Stale++; if (u.Samples.Count < 20) u.Samples.Add($"stale ({c},{r}) '{ea.Char(c, r)}'→'{eb.Char(c, r)}'"); }
                if (!logical && pixels)
                {
                    if (NextToChange(c, r)) { u.NeighbourOverhang++; if (u.Samples.Count < 20) u.Samples.Add($"overhang from a changed neighbour into ({c},{r}) '{eb.Char(c, r)}'"); }
                    else { u.Spurious++; if (u.Samples.Count < 20) u.Samples.Add($"unexpected change ({c},{r}) '{eb.Char(c, r)}'"); }
                }
            }
        return u;
    }

    /// <summary>Finds cells that vary across a timed capture series (a blinking caret) and checks them against the expected caret.</summary>
    public static CursorCheck CheckCursor(IReadOnlyList<NativeImage> series, Geometry g, int[] palette, HostScreen e,
        NativeImage? reference = null, HostScreen? referenceExpected = null)
    {
        var cc = new CursorCheck { Captures = series.Count, Expected = e.CursorVisible ? $"visible at ({e.CursorX},{e.CursorY}) size {e.CursorSize}" : "hidden" };
        var varying = new HashSet<(int, int)>();
        for (int i = 1; i < series.Count; i++)
            for (int r = 0; r < g.Rows; r++)
                for (int c = 0; c < g.Cols; c++)
                    if (CellDiffers(series[0], series[i], g.Cell(c, r), fullCell: true)) varying.Add((c, r));
        foreach (var v in varying.OrderBy(v => v.Item2).ThenBy(v => v.Item1)) cc.VaryingCells.Add($"({v.Item1},{v.Item2})");
        var at = (e.CursorX, e.CursorY);
        if (!e.CursorVisible)
        {
            cc.Outcome = varying.Count == 0 ? "Passed" : "Failed";
            cc.Detail = varying.Count == 0 ? "no cell changes over the series (no caret drawn)" : "cells change although the caret is hidden";
            return cc;
        }
        if (varying.Count == 1 && varying.Contains(at))
        {
            cc.Outcome = "Passed";
            cc.Detail = "the caret blinks exactly in the expected cell, shape " + BlinkShape(series, g.Cell(at.CursorX, at.CursorY));
            return cc;
        }
        if (varying.Count > 0 && !varying.Contains(at)) { cc.Outcome = "Failed"; cc.Detail = "cells blink outside the expected caret cell"; return cc; }
        if (varying.Count > 1) { cc.Outcome = "Failed"; cc.Detail = "more cells than the caret cell change over the series"; return cc; }
        // No blinking observed (an unfocused host may draw a steady caret): compare the caret cell with
        // the same cell in an earlier capture that shows the same cell content without the caret.
        if (reference != null && referenceExpected != null && reference.Width == series[0].Width && reference.Height == series[0].Height
            && referenceExpected.Width == e.Width && referenceExpected.Height == e.Height
            && referenceExpected.Char(e.CursorX, e.CursorY) == e.Char(e.CursorX, e.CursorY) && referenceExpected.Attr(e.CursorX, e.CursorY) == e.Attr(e.CursorX, e.CursorY)
            && !(referenceExpected.CursorVisible && referenceExpected.CursorX == e.CursorX && referenceExpected.CursorY == e.CursorY))
        {
            var rect = g.Cell(e.CursorX, e.CursorY);
            int w = rect.X1 - rect.X0, h = rect.Y1 - rect.Y0, diff = 0;
            var rows = new HashSet<int>(); var cols = new HashSet<int>();
            foreach (NativeImage img in series)  // the caret may be in its off phase in some captures
            {
                int d = 0; var rs = new HashSet<int>(); var cs = new HashSet<int>();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (Dist(img.Rgb(rect.X0 + x, rect.Y0 + y), reference.Rgb(rect.X0 + x, rect.Y0 + y)) > 24) { d++; rs.Add(y); cs.Add(x); }
                if (d > diff) { diff = d; rows = rs; cols = cs; }
            }
            if (diff >= Math.Max(3, h / 2))
            {
                string shape = rows.Count >= h * 0.7 && cols.Count >= w * 0.7 ? "block"
                    : rows.Count > 0 && rows.Min() >= h * 0.55 ? "underline"
                    : cols.Count > 0 && cols.Max() <= w * 0.4 ? "bar" : "other";
                cc.Outcome = "Passed";
                cc.Detail = $"steady caret visible in the expected cell: {diff} px differ from the caret-less capture, shape {shape} (rows {rows.Min()}–{rows.Max()}, columns {cols.Min()}–{cols.Max()})";
                return cc;
            }
            cc.Outcome = "Failed";
            cc.Detail = "the caret cell is identical to the caret-less capture (no caret drawn)";
            return cc;
        }
        byte attr = e.Attr(e.CursorX, e.CursorY);
        double bgFrac = 1 - InkFraction(series[0], g.Cell(e.CursorX, e.CursorY), palette[attr >> 4]);
        if (IsBlank(e.Char(e.CursorX, e.CursorY)) && bgFrac < 0.9) { cc.Outcome = "Passed"; cc.Detail = $"steady caret visible in the expected blank cell ({1 - bgFrac:P0} covered)"; }
        else { cc.Outcome = "ManualReviewRequired"; cc.Detail = "no blinking observed and the caret cell is not blank; caret visibility not decidable automatically"; }
        return cc;
    }

    private static string BlinkShape(IReadOnlyList<NativeImage> series, (int X0, int Y0, int X1, int Y1) rect)
    {
        var rows = new HashSet<int>(); var cols = new HashSet<int>();
        int w = rect.X1 - rect.X0, h = rect.Y1 - rect.Y0;
        for (int i = 1; i < series.Count; i++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (Dist(series[0].Rgb(rect.X0 + x, rect.Y0 + y), series[i].Rgb(rect.X0 + x, rect.Y0 + y)) > 24) { rows.Add(y); cols.Add(x); }
        if (rows.Count == 0) return "unknown";
        string shape = rows.Count >= h * 0.7 && cols.Count >= w * 0.7 ? "block" : rows.Min() >= h * 0.55 ? "underline" : cols.Max() <= w * 0.4 ? "bar" : "other";
        return $"{shape} (rows {rows.Min()}–{rows.Max()}, columns {cols.Min()}–{cols.Max()} of {w}x{h})";
    }

    /// <summary>Whether the client area outside the grid is one uniform colour (no stale cells beyond a resized grid).</summary>
    public static (bool Ok, string Detail) OutsideGridUniform(NativeImage img, Geometry g, int marginLimit)
    {
        int right = Math.Min(img.Width, g.Right + marginLimit), bottom = Math.Min(img.Height, g.Bottom + marginLimit);
        var hist = new Dictionary<int, int>();
        int n = 0;
        for (int y = (int)g.Y0; y < bottom; y++)
            for (int x = (int)g.X0; x < right; x++)
            {
                if (x < g.Right && y < g.Bottom) continue;
                int p = img.Rgb(x, y);
                hist[p] = hist.GetValueOrDefault(p) + 1; n++;
            }
        if (n == 0) return (true, "no client area outside the grid");
        var top = hist.MaxBy(kv => kv.Value);
        double frac = top.Value / (double)n;
        return (frac >= 0.98, $"{n} px outside the grid, {frac:P1} are {Hex(top.Key)}");
    }

    public static int MeasuredBackground(NativeImage img, Geometry g, int c, int r) => Mode(img, g.Cell(c, r));

    // ── pixel helpers ───────────────────────────────────────────────────────

    private sealed record Comp(int X0, int Y0, int X1, int Y1, int Area);

    private static List<Comp> Components(NativeImage img, Func<int, bool> pred, int minArea)
    {
        var seen = new bool[img.Width * img.Height];
        var list = new List<Comp>();
        var stack = new Stack<int>();
        for (int i = 0; i < seen.Length; i++)
        {
            if (seen[i] || !pred(img.Rgb(i % img.Width, i / img.Width))) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0, area = 0;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop(), x = k % img.Width, y = k / img.Width;
                area++; x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1);
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= img.Width || ny >= img.Height) continue;
                    int j = ny * img.Width + nx;
                    if (!seen[j] && pred(img.Rgb(nx, ny))) { seen[j] = true; stack.Push(j); }
                }
            }
            if (area >= minArea) list.Add(new Comp(x0, y0, x1, y1, area));
        }
        return list;
    }

    private static bool IsUniform(NativeImage img)
    {
        int first = img.Rgb(0, 0), same = 0, n = 0;
        for (int y = 0; y < img.Height; y += 3)
            for (int x = 0; x < img.Width; x += 3) { n++; if (Dist(img.Rgb(x, y), first) <= 4) same++; }
        return same >= n * 0.995;
    }

    private static (int, int, int, int) Inset((int X0, int Y0, int X1, int Y1) r, int k) =>
        (r.X0 + k, r.Y0 + k, Math.Max(r.X0 + k + 1, r.X1 - k), Math.Max(r.Y0 + k + 1, r.Y1 - k));

    public static int Mode(NativeImage img, (int X0, int Y0, int X1, int Y1) r)
    {
        var hist = new Dictionary<int, int>();
        for (int y = r.Y0; y < r.Y1; y++)
            for (int x = r.X0; x < r.X1; x++) { int p = img.Rgb(x, y); hist[p] = hist.GetValueOrDefault(p) + 1; }
        return hist.Count == 0 ? 0 : hist.MaxBy(kv => kv.Value).Key;
    }

    private static double InkFraction(NativeImage img, (int X0, int Y0, int X1, int Y1) r, int bg)
    {
        int n = 0, ink = 0;
        for (int y = r.Y0; y < r.Y1; y++)
            for (int x = r.X0; x < r.X1; x++) { n++; if (Dist(img.Rgb(x, y), bg) > InkThreshold) ink++; }
        return n == 0 ? 0 : ink / (double)n;
    }

    private static double MeanDistance(NativeImage img, (int X0, int Y0, int X1, int Y1) r, int bg)
    {
        long sum = 0; int n = 0;
        for (int y = r.Y0; y < r.Y1; y++)
            for (int x = r.X0; x < r.X1; x++) { n++; sum += Dist(img.Rgb(x, y), bg); }
        return n == 0 ? 0 : sum / (double)n;
    }

    private static int TopInk(NativeImage img, (int X0, int Y0, int X1, int Y1) r, int bg)
    {
        for (int y = r.Y0; y < r.Y1; y++)
            for (int x = r.X0; x < r.X1; x++)
                if (Dist(img.Rgb(x, y), bg) > InkThreshold) return y - r.Y0;
        return -1;
    }

    private static (double First, double Second) Halves(bool[] mask, int w, int h, bool vertical)
    {
        int a = 0, b = 0, na = 0, nb = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool first = vertical ? y < h / 2 : x < w / 2;
                bool mid = vertical ? (h % 2 == 1 && y == h / 2) : (w % 2 == 1 && x == w / 2);
                if (mid) continue;
                if (first) { na++; if (mask[y * w + x]) a++; } else { nb++; if (mask[y * w + x]) b++; }
            }
        return (na == 0 ? 0 : a / (double)na, nb == 0 ? 0 : b / (double)nb);
    }

    // Ink within one pixel of an edge, in the middle band (so a corner stroke counts only for its own edges).
    private static bool EdgeInk(bool[] mask, int w, int h, char edge)
    {
        switch (edge)
        {
            case 'L': case 'R':
            {
                int x0 = edge == 'L' ? 0 : w - 2, x1 = edge == 'L' ? 2 : w;
                for (int y = h / 4; y < h - h / 4; y++) for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++) if (mask[y * w + x]) return true;
                return false;
            }
            default:
            {
                int y0 = edge == 'U' ? 0 : h - 2, y1 = edge == 'U' ? 2 : h;
                for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++) for (int x = w / 4; x < w - w / 4; x++) if (mask[y * w + x]) return true;
                return false;
            }
        }
    }
}
