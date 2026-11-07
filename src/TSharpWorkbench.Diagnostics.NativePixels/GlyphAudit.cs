using System.Text.Json;
using TSharpVision.Drivers.SDL;
using TSharpVision.Drivers.SDL.Rendering.Fonts;
using TSharpVision.Drivers.SDL.Rendering.GlyphComposition;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>One glyph: the product's cell mask against the independent oracle and the unplaced glyph.</summary>
public sealed class GlyphAuditEntry
{
    public required string Char { get; init; }
    public required string Codepoint { get; init; }
    public required string Group { get; init; }
    public required string Mode { get; init; }
    public string Source { get; set; } = "";
    /// <summary>Product mask byte-identical to the oracle's mask.</summary>
    public bool MatchesOracle { get; set; }
    public int DifferingPixels { get; set; }
    /// <summary>Ink box of the product mask in cell pixels (right/bottom exclusive); all zero when blank.</summary>
    public int[] CellInk { get; set; } = [0, 0, 0, 0];
    /// <summary>Ink box of the unplaced glyph: pen-relative x, baseline-relative y (right/bottom exclusive).</summary>
    public int[] NaturalInk { get; set; } = [0, 0, 0, 0];
    public int NaturalSurfaceWidth { get; set; }
    public int NaturalSurfaceHeight { get; set; }
    /// <summary>How the glyph was re-rasterised to fit (oracle's description), or null when it fit as is.</summary>
    public string? Fit { get; set; }
    public bool Clipped { get; set; }
    /// <summary>Unplaced ink exceeds the cell at the font's own baseline (the case that used to clip or squash).</summary>
    public bool NaturalExceedsCell { get; set; }
    public int NaturalParts { get; set; }
    public int PlacedParts { get; set; }
    /// <summary>Anti-clipping verdict of the product mask (null = every ink part kept).</summary>
    public string? PartsVerdict { get; set; }
    /// <summary>Anti-clipping verdict of the pre-fix rule's mask (null = it would have been fine).</summary>
    public string? LegacyPartsVerdict { get; set; }
}

public sealed class GlyphAuditResult
{
    public string Font { get; set; } = "";
    public int PointSize { get; set; }
    public string ProductCell { get; set; } = "";
    public string OracleCell { get; set; } = "";
    public int Ascent { get; set; }
    public int ProductTextBaseline { get; set; }
    public int OracleTextBaseline { get; set; }
    public int DescenderDepth { get; set; }
    public int AccentOverflow { get; set; }
    public int ProductFittedGlyphs { get; set; }
    public int ProductClippedGlyphs { get; set; }
    public double MillisecondsPerGlyph { get; set; }
    /// <summary>One-time cost of measuring the descender/accent probes (first glyph of a renderer).</summary>
    public double FontMeasureMilliseconds { get; set; }
    public List<GlyphAuditEntry> Entries { get; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// <c>glyph-audit</c>: rasterises a fixed character set through the product's shared
/// <see cref="SdlCellGlyphRasterizer"/> (no window, no renderer) and compares every mask with the
/// independent <see cref="GlyphMaskOracle"/> and with the unplaced SDL_ttf glyph. Runs in its own
/// process, like the renderer paths, so no SDL state enters a test host.
/// </summary>
public static class GlyphAudit
{
    public static readonly (string Group, string Chars)[] Groups =
    [
        ("czech-upper", "ŘÍŠŽŤČĎŮÁÉÓÚÝĚŇÜ"),
        ("czech-lower", "říšžťčďůáéóúýěňü"),
        ("ascii", new string(Enumerable.Range(0x21, 0x5E).Select(i => (char)i).ToArray())),
        ("symbols", "►◄▲▼■·€±≥≤÷≈°√²☺♥♪"),
        ("box-font", "╭╮╯╰╱╲╳"),
    ];

    public static GlyphAuditResult Run(FontSpec font, string? symbolPath)
    {
        var r = new GlyphAuditResult { Font = font.Path, PointSize = font.PointSize };
        using var oracle = new GlyphMaskOracle(font.Path, font.PointSize, symbolPath);
        IntPtr productFont = SDL3.TTF.OpenFont(font.Path, font.PointSize);
        IntPtr productSymbol = symbolPath == null ? IntPtr.Zero : SDL3.TTF.OpenFont(symbolPath, font.PointSize);
        if (productSymbol != IntPtr.Zero) SDL3.TTF.AddFallbackFont(productFont, productSymbol);
        try
        {
            SdlFontMetrics.ComputeMetrics(productFont, out int cw, out int ch);
            using var product = new SdlCellGlyphRasterizer(productFont, font.Path, font.PointSize, productSymbol == IntPtr.Zero ? null : symbolPath, cw, ch);
            r.ProductCell = $"{cw}x{ch}";
            r.OracleCell = $"{oracle.CellWidth}x{oracle.CellHeight}";
            r.Ascent = oracle.Ascent;
            var measure = System.Diagnostics.Stopwatch.StartNew();
            r.ProductTextBaseline = product.TextBaseline; // first use measures the font's probes
            r.FontMeasureMilliseconds = Math.Round(measure.Elapsed.TotalMilliseconds, 2);
            r.OracleTextBaseline = oracle.TextBaseline;
            r.DescenderDepth = oracle.DescenderDepth;
            r.AccentOverflow = oracle.AccentOverflow;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int rasterised = 0;
            foreach (var (group, chars) in Groups)
            {
                foreach (char c in chars)
                {
                    var e = new GlyphAuditEntry
                    {
                        Char = c.ToString(), Codepoint = $"U+{(int)c:X4}", Group = group,
                        Mode = SdlCellGlyphRasterizer.ModeFor(c).ToString(),
                    };
                    r.Entries.Add(e);
                    if (Cp437GlyphGenerator.IsGenerated(c)) { e.Source = "generated"; e.MatchesOracle = true; continue; }

                    byte[]? mine = product.Rasterize(c);
                    rasterised++;
                    GlyphMask expected = oracle.Mask(c, 0, 0, FontGlyphPlacement.CellPlaced);
                    GlyphMask legacy = oracle.Mask(c, 0, 0, FontGlyphPlacement.LegacyClipAboveAscent);
                    NaturalGlyph? natural = oracle.Natural(c);
                    e.Source = oracle.FontCoverage(c);
                    e.Fit = expected.Fit;
                    e.Clipped = expected.Clipped;
                    byte[] a = mine ?? new byte[cw * ch], b = expected.Alpha ?? new byte[cw * ch];
                    e.DifferingPixels = a.Length == b.Length ? a.Zip(b).Count(p => p.First != p.Second) : -1;
                    e.MatchesOracle = e.DifferingPixels == 0;
                    e.CellInk = InkBox(a, cw, ch);
                    if (natural is { HasInk: true } n)
                    {
                        e.NaturalInk = [n.InkLeft, n.InkTop, n.InkRight, n.InkBottom];
                        e.NaturalSurfaceWidth = n.Width;
                        e.NaturalSurfaceHeight = n.Height;
                        e.NaturalExceedsCell = n.InkLeft < 0 || n.InkRight > cw || oracle.Ascent + n.InkTop < 0 || oracle.Ascent + n.InkBottom > ch;
                        var parts = DiacriticChecks.Parts(n.Alpha, n.Width, n.Height);
                        var placed = DiacriticChecks.Parts(a, cw, ch);
                        e.NaturalParts = parts.Count;
                        e.PlacedParts = placed.Count;
                        e.PartsVerdict = DiacriticChecks.Compare(parts, placed);
                        e.LegacyPartsVerdict = DiacriticChecks.Compare(parts, DiacriticChecks.Parts(legacy.Alpha ?? new byte[cw * ch], cw, ch));
                    }
                }
            }
            timer.Stop();
            r.MillisecondsPerGlyph = rasterised == 0 ? 0 : Math.Round(timer.Elapsed.TotalMilliseconds / rasterised, 3);
            r.ProductFittedGlyphs = product.FittedGlyphs;
            r.ProductClippedGlyphs = product.ClippedGlyphs;
        }
        catch (Exception ex)
        {
            r.Error = ex.ToString();
        }
        finally
        {
            if (productFont != IntPtr.Zero) { SDL3.TTF.ClearFallbackFonts(productFont); SDL3.TTF.CloseFont(productFont); }
            if (productSymbol != IntPtr.Zero) SDL3.TTF.CloseFont(productSymbol);
        }
        return r;
    }

    private static int[] InkBox(byte[] a, int w, int h)
    {
        int l = int.MaxValue, t = int.MaxValue, rr = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (a[y * w + x] != 0) { l = Math.Min(l, x); t = Math.Min(t, y); rr = Math.Max(rr, x + 1); b = Math.Max(b, y + 1); }
        return rr < 0 ? [0, 0, 0, 0] : [l, t, rr, b];
    }

    public static int Execute(string output)
    {
        GlyphAuditResult r = Run(FontSpec.Default, SdlFontLocator.ProbeSymbolFontPath());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(r, ChildRunner.Json));
        int mismatches = r.Entries.Count(e => !e.MatchesOracle);
        Console.WriteLine($"glyph-audit: {r.Entries.Count} glyphs, {mismatches} differ from the oracle, text baseline product {r.ProductTextBaseline} / oracle {r.OracleTextBaseline}, fitted {r.ProductFittedGlyphs}, clipped {r.ProductClippedGlyphs}{(r.Error is { } err ? " — " + err.Split('\n')[0] : "")}");
        return r.Error != null ? 2 : mismatches > 0 ? 1 : 0;
    }
}
