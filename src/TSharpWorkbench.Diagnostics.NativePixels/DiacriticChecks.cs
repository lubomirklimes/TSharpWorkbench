using TSharpWorkbench.Diagnostics.DriverOutput;
using TSharpVision.Drivers.SDL.Rendering.GlyphComposition;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>Outcome of <see cref="DiacriticChecks"/> on one frame.</summary>
public sealed class DiacriticResult
{
    /// <summary>Cells whose glyph has more than one ink part (accented letters, i, j, ;, …) and were checked.</summary>
    public int MultiPartCells { get; set; }
    public int FailedCells { get; set; }
    public List<string> Failures { get; } = new();
    public bool Passed => FailedCells == 0;
}

/// <summary>
/// Independent anti-clipping check. It does not use the placement rule at all: for every cell
/// whose glyph has several separate ink parts when SDL_ttf rasterises it unplaced (an accent above
/// a letter, the dot of an i), the ink actually captured in that cell must still show the same
/// parts, each with a comparable share of the glyph's ink. A clipped accent (Ž drawn as Ż) keeps
/// one thin row of its caron: the part survives but its share collapses, which this detects. A
/// uniformly smaller glyph keeps its shares.
/// </summary>
public static class DiacriticChecks
{
    /// <summary>Coverage at or above this counts as ink for finding parts (faint antialiasing does not join parts).</summary>
    public const int PartThreshold = 64;

    /// <summary>Parts holding less than this share of the glyph's ink are specks, not parts.</summary>
    public const double MinPartShare = 0.03;

    /// <summary>A part's captured share must be at least this fraction of its unplaced share.</summary>
    public const double MinShareRatio = 0.6;

    /// <summary>A connected ink part: rows/columns span and summed coverage.</summary>
    public sealed record Part(int Top, int Bottom, int Left, int Right, long Mass);

    /// <summary>8-connected parts of coverage ≥ <see cref="PartThreshold"/>, mass including the faint pixels nearest to each, ordered top to bottom.</summary>
    public static List<Part> Parts(byte[] alpha, int w, int h)
    {
        var label = new int[w * h];
        var parts = new List<(int T, int B, int L, int R, long M)>();
        var stack = new Stack<int>();
        for (int i = 0; i < alpha.Length; i++)
        {
            if (alpha[i] < PartThreshold || label[i] != 0) continue;
            int id = parts.Count + 1;
            int t = int.MaxValue, b = -1, l = int.MaxValue, r = -1;
            long m = 0;
            stack.Push(i);
            label[i] = id;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                t = Math.Min(t, y); b = Math.Max(b, y); l = Math.Min(l, x); r = Math.Max(r, x);
                m += alpha[p];
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int q = ny * w + nx;
                        if (label[q] == 0 && alpha[q] >= PartThreshold) { label[q] = id; stack.Push(q); }
                    }
            }
            parts.Add((t, b, l, r, m));
        }
        long total = parts.Sum(p => p.M);
        return parts.Where(p => total > 0 && p.M >= MinPartShare * total)
            .OrderBy(p => p.T).ThenBy(p => p.L)
            .Select(p => new Part(p.T, p.B, p.L, p.R, p.M)).ToList();
    }

    /// <summary>Coverage of a captured cell, recovered from its pixels and the cell's foreground/background.</summary>
    public static byte[] CapturedCoverage(NativeImage img, int x0, int y0, int cw, int ch, int fg, int bg)
    {
        // The channel with the largest fg/bg contrast gives the most precise coverage.
        int best = 0, bestContrast = -1;
        for (int c = 0; c < 3; c++)
        {
            int contrast = Math.Abs(((fg >> (16 - 8 * c)) & 0xFF) - ((bg >> (16 - 8 * c)) & 0xFF));
            if (contrast > bestContrast) (best, bestContrast) = (c, contrast);
        }
        int shift = 16 - 8 * best, f = (fg >> shift) & 0xFF, b = (bg >> shift) & 0xFF;
        var a = new byte[cw * ch];
        if (f == b) return a;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int v = (img.Rgb(x0 + x, y0 + y) >> shift) & 0xFF;
                a[y * cw + x] = (byte)Math.Clamp((int)Math.Round((v - b) * 255.0 / (f - b)), 0, 255);
            }
        return a;
    }

    /// <summary>Runs the check on every eligible cell of <paramref name="img"/>.</summary>
    public static DiacriticResult Run(LogicalScreen screen, CaretRequest caret, NativeImage img, GlyphMaskOracle fonts)
    {
        int cw = fonts.CellWidth, chh = fonts.CellHeight;
        var result = new DiacriticResult();
        var natural = new Dictionary<char, List<Part>?>();
        for (int row = 0; row < screen.Grid.Height; row++)
        {
            for (int col = 0; col < screen.Grid.Width; col++)
            {
                if ((col + 1) * cw > img.Width || (row + 1) * chh > img.Height) continue;
                TScreenChar cell = screen[col, row];
                char ch = cell.Character;
                if (ch is ' ' or '\0' || Cp437GlyphGenerator.IsGenerated(ch) || GlyphMaskOracle.IsCellFill(ch)) continue;
                bool caretHere = caret.CursorType != 0 && caret.X == col && caret.Y == row;
                if (caretHere && caret.CursorType < 100) continue; // the underline bar is extra ink

                if (!natural.TryGetValue(ch, out List<Part>? expected))
                {
                    NaturalGlyph? g = fonts.NaturalText(ch);
                    expected = g is { HasInk: true } ? Parts(g.Alpha, g.Width, g.Height) : null;
                    natural[ch] = expected;
                }
                if (expected is not { Count: > 1 }) continue;

                int fg = Vga.Rgb[cell.Attr & 0x0F], bg = Vga.Rgb[(cell.Attr >> 4) & 0x0F];
                if (caretHere) (fg, bg) = (bg, fg);
                if (fg == bg) continue;
                result.MultiPartCells++;

                List<Part> actual = Parts(CapturedCoverage(img, col * cw, row * chh, cw, chh, fg, bg), cw, chh);
                string? why = Compare(expected, actual);
                if (why == null) continue;
                result.FailedCells++;
                if (result.Failures.Count < 12) result.Failures.Add($"({col},{row}) '{ch}': {why}");
            }
        }
        return result;
    }

    /// <summary>Null when <paramref name="actual"/> keeps every part of <paramref name="expected"/>; otherwise why not.</summary>
    public static string? Compare(List<Part> expected, List<Part> actual)
    {
        if (actual.Count != expected.Count)
            return $"{actual.Count} ink part(s) captured, the unplaced glyph has {expected.Count}";
        double te = expected.Sum(p => (double)p.Mass), ta = actual.Sum(p => (double)p.Mass);
        for (int i = 0; i < expected.Count; i++)
        {
            double se = expected[i].Mass / te, sa = actual[i].Mass / ta;
            if (sa < MinShareRatio * se)
                return $"part {i + 1} (top to bottom) holds {sa:P1} of the ink, unplaced {se:P1}: cut off";
        }
        return null;
    }
}
