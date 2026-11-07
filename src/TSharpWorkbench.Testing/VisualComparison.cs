using System.Globalization;

namespace TSharpWorkbench.Testing;

/// <summary>One logical cell as the comparison reports it. The unit of comparison is exactly one TVS1 cell: one UTF-16 code
/// unit (<see cref="TScreenChar.Character"/>, compared ordinally, never normalized) plus the raw attribute byte (low nibble
/// foreground palette index, high nibble background index including its top bit). TSharpVision has no wide-cell model, so a
/// surrogate or combining code unit is simply its own cell.</summary>
/// <param name="Code"><c>U+XXXX</c> of the code unit.</param>
/// <param name="Text">The code unit as text; control characters and lone surrogates are shown as their code instead.</param>
/// <param name="Attribute">Raw attribute byte, <c>0xBF</c>.</param>
public sealed record CellInfo(string Code, string Text, string Attribute, int Foreground, int Background)
{
    public static CellInfo Of(TScreenChar cell)
    {
        char ch = cell.Character;
        byte attr = cell.Attr;
        string text = char.IsControl(ch) || char.IsSurrogate(ch) ? CodeOf(ch) : ch.ToString();
        return new(CodeOf(ch), text, "0x" + attr.ToString("X2", CultureInfo.InvariantCulture), attr & 0x0F, attr >> 4);
    }

    internal static string CodeOf(char ch) => "U+" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture);
}

/// <summary>One differing cell. <paramref name="Hints"/> are descriptive classifications (<c>border</c>, <c>shadow</c>,
/// <c>blank-to-content</c>, <c>content-to-blank</c>, <c>placeholder</c>); they never change the verdict.</summary>
public sealed record CellDifference(int X, int Y, bool Glyph, bool Foreground, bool Background, CellInfo Expected, CellInfo Actual,
    string[] Hints);

/// <summary>A connected group of differing cells (8-neighbourhood) and its bounding box, in screen coordinates.</summary>
public sealed record DifferenceRegion(int X, int Y, int Width, int Height, int Cells, int GlyphCells, int AttributeCells);

/// <summary>Row-level observation that explains a group of glyph differences, e.g. a label drawn <c>Shift</c> columns away.</summary>
public sealed record RowObservation(int Y, int FromX, int ToX, string Kind, int Shift, string Expected, string Actual);

/// <summary>Result of <see cref="ScreenComparer.Compare"/>. Cells are compared over the common area; any difference in
/// dimensions alone makes the screens unequal. <see cref="Differences"/> is bounded by the caller's limit; the counts are not.</summary>
public sealed record ScreenComparison(int ExpectedWidth, int ExpectedHeight, int ActualWidth, int ActualHeight, int ComparedCells,
    int DifferingCells, int GlyphDifferences, int AttributeDifferences, int ForegroundDifferences, int BackgroundDifferences,
    CellDifference? FirstDifference, CellDifference[] Differences, bool DifferencesTruncated, DifferenceRegion[] Regions,
    RowObservation[] Observations)
{
    public bool DimensionsMatch => ExpectedWidth == ActualWidth && ExpectedHeight == ActualHeight;
    public bool Equal => DimensionsMatch && DifferingCells == 0;
}

/// <summary>Exact logical-screen comparison: no tolerance, no string normalization. Normalization (masking of explicitly
/// declared dynamic cells) happens before comparison, in <see cref="VisualNormalizer"/>, and is reported separately.</summary>
public static class ScreenComparer
{
    /// <summary>The attribute TSharpVision draws shadows with (dark grey on black); used only for the <c>shadow</c> hint.</summary>
    public const byte ShadowAttribute = 0x08;
    public const int DefaultDifferenceLimit = 200;
    private const int MaximumRegions = 64, MaximumObservations = 64, MaximumShift = 8;

    public static ScreenComparison Compare(ScreenSnapshot expected, ScreenSnapshot actual, int limit = DefaultDifferenceLimit)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(actual);
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        int width = Math.Min(expected.Width, actual.Width), height = Math.Min(expected.Height, actual.Height);
        var differs = new bool[width, height];
        var list = new List<CellDifference>();
        int count = 0, glyphs = 0, attributes = 0, foregrounds = 0, backgrounds = 0;
        CellDifference? first = null, firstAttribute = null;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            TScreenChar e = expected[x, y], a = actual[x, y];
            byte ea = e.Attr, aa = a.Attr;
            bool glyph = e.Character != a.Character, fg = (ea & 0x0F) != (aa & 0x0F), bg = (ea & 0xF0) != (aa & 0xF0);
            if (!glyph && !fg && !bg) continue;
            differs[x, y] = true;
            count++;
            if (glyph) glyphs++;
            if (fg || bg) attributes++;
            if (fg) foregrounds++;
            if (bg) backgrounds++;
            if (list.Count >= limit && (first is not null || !glyph)) continue;
            var difference = new CellDifference(x, y, glyph, fg, bg, CellInfo.Of(e), CellInfo.Of(a), Hints(e, a, glyph));
            if (glyph) first ??= difference;
            firstAttribute ??= difference;
            if (list.Count < limit) list.Add(difference);
        }
        return new(expected.Width, expected.Height, actual.Width, actual.Height, width * height, count, glyphs, attributes, foregrounds,
            backgrounds, first ?? firstAttribute, list.ToArray(), count > list.Count, Regions(expected, actual, differs, width, height),
            Observations(expected, actual, differs, width, height));
    }

    internal static bool IsBorder(char ch) => ch is >= '─' and <= '╿';
    internal static bool IsBlank(char ch) => ch is ' ' or '\0';

    private static string[] Hints(TScreenChar e, TScreenChar a, bool glyph)
    {
        var hints = new List<string>(2);
        if (IsBorder(e.Character) || IsBorder(a.Character)) hints.Add("border");
        if (!glyph && ((byte)e.Attr == ShadowAttribute) != ((byte)a.Attr == ShadowAttribute)) hints.Add("shadow");
        if (glyph && IsBlank(e.Character) && !IsBlank(a.Character)) hints.Add("blank-to-content");
        if (glyph && !IsBlank(e.Character) && IsBlank(a.Character)) hints.Add("content-to-blank");
        if (e.Character == VisualNormalizer.Placeholder || a.Character == VisualNormalizer.Placeholder) hints.Add("placeholder");
        return hints.ToArray();
    }

    private static DifferenceRegion[] Regions(ScreenSnapshot expected, ScreenSnapshot actual, bool[,] differs, int width, int height)
    {
        var seen = new bool[width, height];
        var regions = new List<DifferenceRegion>();
        var stack = new Stack<(int X, int Y)>();
        for (int y = 0; y < height && regions.Count < MaximumRegions; y++)
        for (int x = 0; x < width && regions.Count < MaximumRegions; x++)
        {
            if (!differs[x, y] || seen[x, y]) continue;
            int x0 = x, y0 = y, x1 = x, y1 = y, cells = 0, glyphCells = 0, attributeCells = 0;
            stack.Push((x, y)); seen[x, y] = true;
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                cells++;
                if (expected[cx, cy].Character != actual[cx, cy].Character) glyphCells++;
                if ((byte)expected[cx, cy].Attr != (byte)actual[cx, cy].Attr) attributeCells++;
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || seen[nx, ny] || !differs[nx, ny]) continue;
                    seen[nx, ny] = true; stack.Push((nx, ny));
                }
            }
            regions.Add(new(x0, y0, x1 - x0 + 1, y1 - y0 + 1, cells, glyphCells, attributeCells));
        }
        return regions.ToArray();
    }

    // Per row, the span of glyph differences: is it the expected text drawn a few columns away (moved), or expected
    // content replaced by blanks / borders / an ellipsis at its end (clipped or truncated)?
    private static RowObservation[] Observations(ScreenSnapshot expected, ScreenSnapshot actual, bool[,] differs, int width, int height)
    {
        var observations = new List<RowObservation>();
        for (int y = 0; y < height && observations.Count < MaximumObservations; y++)
        {
            int from = -1, to = -1;
            for (int x = 0; x < width; x++)
                if (differs[x, y] && expected[x, y].Character != actual[x, y].Character) { if (from < 0) from = x; to = x; }
            if (from < 0) continue;
            string e = Row(expected, y, from, to), a = Row(actual, y, from, to);
            int shift = 0;
            for (int s = 1; s <= MaximumShift && shift == 0; s++)
                foreach (int candidate in new[] { s, -s })
                    if (Shifted(expected, actual, y, from, to, candidate, width)) { shift = candidate; break; }
            if (shift != 0) { observations.Add(new(y, from, to, "moved", shift, e, a)); continue; }
            bool truncated = true;
            for (int x = from; x <= to && truncated; x++)
            {
                char ec = expected[x, y].Character, ac = actual[x, y].Character;
                if (ec == ac) continue;
                truncated = !IsBlank(ec) && !IsBorder(ec) && (IsBlank(ac) || IsBorder(ac) || ac == '…' || ac == '~' || ac == '►');
            }
            if (truncated) observations.Add(new(y, from, to, "content-missing", 0, e, a));
        }
        return observations.ToArray();
    }

    // The expected glyphs of the span appear in the actual row at x + shift, and the span is not just blanks.
    private static bool Shifted(ScreenSnapshot expected, ScreenSnapshot actual, int y, int from, int to, int shift, int width)
    {
        bool content = false;
        for (int x = from; x <= to; x++)
        {
            int ax = x + shift;
            char ec = expected[x, y].Character;
            if (ax < 0 || ax >= width) { if (!IsBlank(ec)) return false; continue; }
            if (actual[ax, y].Character != ec) return false;
            content |= !IsBlank(ec);
        }
        return content;
    }

    internal static string Row(ScreenSnapshot screen, int y, int from, int to)
    {
        var chars = new char[to - from + 1];
        for (int x = from; x <= to; x++) chars[x - from] = screen[x, y].Character is var c && (char.IsControl(c) || char.IsSurrogate(c)) ? '�' : c;
        return new string(chars);
    }
}
