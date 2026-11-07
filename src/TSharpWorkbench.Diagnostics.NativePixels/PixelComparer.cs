namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>What the expected image of a comparison is. These are different proof levels.</summary>
public static class ExpectedSource
{
    /// <summary>Computed by <see cref="RasterOracle"/>, independent of the renderer under test.</summary>
    public const string IndependentOracle = "independent-raster-oracle";
    /// <summary>Another renderer's actual capture: consistency only, not correctness.</summary>
    public const string RendererCapture = "other-renderer-capture";
    /// <summary>An owner-approved native baseline (none exists yet).</summary>
    public const string ApprovedBaseline = "owner-approved-native-baseline";
}

public sealed record PixelBox(int X, int Y, int Width, int Height, int Pixels);

public sealed record CellDifference(int Col, int Row, int Pixels, int MaxDeviation, string Character);

/// <summary>Pixel-level comparison of a capture against an expected image.</summary>
public sealed class PixelComparison
{
    public required string ExpectedSource { get; init; }
    public string Outcome { get; set; } = "Match";
    public string? Reason { get; set; }
    public int ExpectedWidth { get; set; }
    public int ExpectedHeight { get; set; }
    public int ActualWidth { get; set; }
    public int ActualHeight { get; set; }
    public string PixelFormat { get; set; } = "RGBA8";
    public long PixelsCompared { get; set; }
    public long ExactMatches { get; set; }
    /// <summary>Pixels that violate the comparison rule (the failure count).</summary>
    public long Differing { get; set; }
    /// <summary>Partial-coverage pixels that differ but lie within the stated partial tolerance.</summary>
    public long WithinPartialTolerance { get; set; }
    public int PartialTolerance { get; set; }
    public long UnverifiedPixels { get; set; }
    public long AlphaDifferences { get; set; }
    public int MaxChannelDeviation { get; set; }
    public int MaxPartialDeviation { get; set; }
    public double MeanAbsChannelDifference { get; set; }
    public Dictionary<string, long> DifferingByClass { get; } = new();
    public Dictionary<string, long> PixelsByClass { get; } = new();
    public int DifferingCellCount { get; set; }
    /// <summary>Differing cells whose largest channel deviation is 1 (rounding-level blend differences).</summary>
    public int DifferingCellsWithinOne { get; set; }
    public List<CellDifference> DifferingCells { get; } = new();
    public List<PixelBox> DifferenceBoxes { get; } = new();

    public bool Passed => Outcome == "Match";
}

/// <summary>
/// Strict comparison. Exact equality is required for background, foreground and letterbox pixels;
/// partial-coverage pixels may deviate by at most <c>partialTolerance</c> per channel (stated per
/// renderer, measured, never grown after a failure). Unverified pixels are counted, not passed.
/// </summary>
public static class PixelComparer
{
    public const int MaxListedCells = 40;

    public static PixelComparison Compare(ExpectedRaster expected, NativeImage actual, int partialTolerance, Func<int, int, string>? cellText = null) =>
        Compare(expected.Image, expected.Classes, actual, expected.CellWidth, expected.CellHeight, partialTolerance, ExpectedSource.IndependentOracle, cellText);

    /// <summary>Exact comparison of two images (e.g. two renderers' captures); every pixel must match.</summary>
    public static PixelComparison CompareExact(NativeImage expected, NativeImage actual, int cellWidth, int cellHeight, string source) =>
        Compare(expected, null, actual, cellWidth, cellHeight, 0, source, null);

    public static PixelComparison Compare(NativeImage expected, PixelClass[]? classes, NativeImage actual,
        int cellWidth, int cellHeight, int partialTolerance, string source, Func<int, int, string>? cellText)
    {
        var r = new PixelComparison
        {
            ExpectedSource = source, PartialTolerance = partialTolerance,
            ExpectedWidth = expected.Width, ExpectedHeight = expected.Height,
            ActualWidth = actual.Width, ActualHeight = actual.Height,
        };
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            r.Outcome = "DimensionMismatch";
            r.Reason = $"expected {expected.Width}x{expected.Height} px, captured {actual.Width}x{actual.Height} px";
            return r;
        }
        if (actual.Rgba.Length != actual.Width * actual.Height * 4)
        {
            r.Outcome = "Corrupt";
            r.Reason = "capture buffer size does not match its dimensions";
            return r;
        }

        int w = expected.Width, h = expected.Height;
        int cols = (w + cellWidth - 1) / cellWidth, rows = (h + cellHeight - 1) / cellHeight;
        var cellPixels = new int[cols * rows];
        var cellMax = new int[cols * rows];
        long sumAbs = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, o = p * 4;
                PixelClass cls = classes?[p] ?? PixelClass.Background;
                string clsName = classes == null ? "All" : cls.ToString();
                r.PixelsByClass[clsName] = r.PixelsByClass.GetValueOrDefault(clsName) + 1;
                int dev = 0;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(expected.Rgba[o + c] - actual.Rgba[o + c]);
                    sumAbs += d;
                    dev = Math.Max(dev, d);
                }
                if (expected.Rgba[o + 3] != actual.Rgba[o + 3]) r.AlphaDifferences++;
                r.PixelsCompared++;
                if (cls == PixelClass.Unverified) { r.UnverifiedPixels++; continue; }
                r.MaxChannelDeviation = Math.Max(r.MaxChannelDeviation, dev);
                bool alphaOk = expected.Rgba[o + 3] == actual.Rgba[o + 3];
                if (dev == 0 && alphaOk) { r.ExactMatches++; continue; }
                if (cls == PixelClass.Partial)
                {
                    r.MaxPartialDeviation = Math.Max(r.MaxPartialDeviation, dev);
                    if (dev <= partialTolerance && alphaOk) { r.WithinPartialTolerance++; continue; }
                }
                r.Differing++;
                r.DifferingByClass[clsName] = r.DifferingByClass.GetValueOrDefault(clsName) + 1;
                int ci = y / cellHeight * cols + x / cellWidth;
                cellPixels[ci]++;
                cellMax[ci] = Math.Max(cellMax[ci], Math.Max(dev, alphaOk ? 0 : 255));
            }
        }
        r.MeanAbsChannelDifference = r.PixelsCompared == 0 ? 0 : Math.Round(sumAbs / (3.0 * r.PixelsCompared), 6);

        for (int i = 0; i < cellPixels.Length; i++)
        {
            if (cellPixels[i] == 0) continue;
            r.DifferingCellCount++;
            if (cellMax[i] <= 1) r.DifferingCellsWithinOne++;
            if (r.DifferingCells.Count < MaxListedCells)
                r.DifferingCells.Add(new CellDifference(i % cols, i / cols, cellPixels[i], cellMax[i], cellText?.Invoke(i % cols, i / cols) ?? ""));
        }
        r.DifferenceBoxes.AddRange(Boxes(cellPixels, cols, rows, cellWidth, cellHeight, w, h).Take(20));
        if (r.Differing > 0)
        {
            r.Outcome = "Mismatch";
            r.Reason = $"{r.Differing} pixel(s) in {r.DifferingCellCount} cell(s) violate the rule (max channel deviation {r.MaxChannelDeviation})";
        }
        return r;
    }

    /// <summary>Bounding boxes of 8-connected groups of differing cells, in pixels.</summary>
    private static IEnumerable<PixelBox> Boxes(int[] cellPixels, int cols, int rows, int cw, int ch, int w, int h)
    {
        var seen = new bool[cellPixels.Length];
        var stack = new Stack<int>();
        var boxes = new List<PixelBox>();
        for (int i = 0; i < cellPixels.Length; i++)
        {
            if (cellPixels[i] == 0 || seen[i]) continue;
            int minC = int.MaxValue, minR = int.MaxValue, maxC = -1, maxR = -1, px = 0;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int j = stack.Pop(), c = j % cols, rr = j / cols;
                minC = Math.Min(minC, c); maxC = Math.Max(maxC, c); minR = Math.Min(minR, rr); maxR = Math.Max(maxR, rr);
                px += cellPixels[j];
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int nc = c + dc, nr = rr + dr;
                        if (nc < 0 || nr < 0 || nc >= cols || nr >= rows) continue;
                        int k = nr * cols + nc;
                        if (cellPixels[k] != 0 && !seen[k]) { seen[k] = true; stack.Push(k); }
                    }
            }
            int x0 = minC * cw, y0 = minR * ch;
            boxes.Add(new PixelBox(x0, y0, Math.Min(w, (maxC + 1) * cw) - x0, Math.Min(h, (maxR + 1) * ch) - y0, px));
        }
        return boxes.OrderByDescending(b => b.Pixels);
    }

    /// <summary>
    /// EXPECTED | ACTUAL | DIFFERENCE. In the difference panel matching pixels are the actual image
    /// dimmed, rule violations red, tolerated partial-coverage deviations yellow and unverified
    /// pixels blue-tinted.
    /// </summary>
    public static NativeImage DiffImage(NativeImage expected, PixelClass[]? classes, NativeImage actual, int partialTolerance)
    {
        int w = Math.Max(expected.Width, actual.Width), h = Math.Max(expected.Height, actual.Height), gap = 8;
        var img = new NativeImage(w * 3 + gap * 2, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < img.Width; x++) img.Set(x, y, 0x808080);
        img.Blit(expected, 0, 0, expected.Width, expected.Height, 0, 0);
        img.Blit(actual, 0, 0, actual.Width, actual.Height, w + gap, 0);
        int ox = 2 * (w + gap);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool inE = x < expected.Width && y < expected.Height, inA = x < actual.Width && y < actual.Height;
                if (!inE || !inA) { img.Set(ox + x, y, 0xFF00FF); continue; }
                int a = actual.Rgb(x, y), e = expected.Rgb(x, y);
                int dev = Math.Max(Math.Abs((a >> 16) - (e >> 16)), Math.Max(Math.Abs(((a >> 8) & 0xFF) - ((e >> 8) & 0xFF)), Math.Abs((a & 0xFF) - (e & 0xFF))));
                bool alphaOk = actual.Alpha(x, y) == expected.Alpha(x, y);
                PixelClass cls = classes != null && expected.Width == actual.Width ? classes[y * expected.Width + x] : PixelClass.Background;
                int dim = (((a >> 16) / 4) << 16) | ((((a >> 8) & 0xFF) / 4) << 8) | ((a & 0xFF) / 4);
                int rgb = cls == PixelClass.Unverified ? (dim | 0x000080)
                    : dev == 0 && alphaOk ? dim
                    : cls == PixelClass.Partial && dev <= partialTolerance && alphaOk ? 0xC0C000
                    : 0xFF0000;
                img.Set(ox + x, y, rgb);
            }
        }
        return img;
    }
}
