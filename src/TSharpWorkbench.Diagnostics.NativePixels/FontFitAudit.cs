using TSharpVision.Config;
using System.Text.Json;
using TSharpVision.Drivers.SDL;
using TSharpVision.Drivers.SDL.Rendering.Fonts;
using TSharpVision.Drivers.SDL.Rendering.GlyphComposition;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>One glyph under both fit modes: the product's masks against the oracle's.</summary>
public sealed class FontFitGlyph
{
    public required string Char { get; init; }
    public required string Codepoint { get; init; }
    public required string Group { get; init; }
    /// <summary>In the Uniform sizing repertoire (ordinary text).</summary>
    public bool InRepertoire { get; set; }
    public string Source { get; set; } = "";
    /// <summary>Ink box in cell pixels [left, top, right, bottom) of the product mask, per mode.</summary>
    public int[] CondenseInk { get; set; } = [0, 0, 0, 0];
    public int[] UniformInk { get; set; } = [0, 0, 0, 0];
    /// <summary>How the oracle re-rasterised the glyph to fit, per mode (null = drawn as rasterised).</summary>
    public string? CondenseFit { get; set; }
    public string? UniformFit { get; set; }
    public int CondenseDifferingPixels { get; set; }
    public int UniformDifferingPixels { get; set; }
    /// <summary>Anti-clipping verdict of the Uniform mask against the unplaced glyph (null = every ink part kept).</summary>
    public string? UniformPartsVerdict { get; set; }
}

/// <summary>Sizing results of one mode for one font, from the product rasteriser and the oracle.</summary>
public sealed class FontFitModeMetrics
{
    public string Mode { get; set; } = "";
    public string TextPointSize { get; set; } = "";
    public int TextBaseline { get; set; }
    public int Pen { get; set; }
    public string Extremes { get; set; } = "";
    public List<string> RejectedSizes { get; set; } = new();
    public int RepertoireGlyphs { get; set; }
    public double MeasureMilliseconds { get; set; }
    public double MillisecondsPerGlyph { get; set; }
    public int OracleTextPointSize { get; set; }
    public int OracleTextBaseline { get; set; }
    public List<string> OracleTrace { get; set; } = new();
    public int FittedGlyphs { get; set; }
    public int OutlierGlyphs { get; set; }
    public int ClippedGlyphs { get; set; }
    public int AuxiliaryFontsOpen { get; set; }
    public int AuxiliaryFontsAfterDispose { get; set; }
}

public sealed class FontFitFontResult
{
    public string FontId { get; set; } = "";
    public string Path { get; set; } = "";
    public int PointSize { get; set; }
    public string Cell { get; set; } = "";
    public int Ascent { get; set; }
    public int Descent { get; set; }
    public int LineSkip { get; set; }
    public FontFitModeMetrics Condense { get; set; } = new();
    public FontFitModeMetrics Uniform { get; set; } = new();
    /// <summary>Named measurements (ink heights and widths per mode) for the report.</summary>
    public Dictionary<string, string> Measurements { get; set; } = new();
    /// <summary>Named pass/fail checks; every value must be true.</summary>
    public Dictionary<string, bool> Checks { get; set; } = new();
    public List<FontFitGlyph> Glyphs { get; set; } = new();
}

public sealed class FontFitAuditResult
{
    public string Schema { get; set; } = "tsharpvision.font-fit-metrics/v1";
    public List<FontFitFontResult> Fonts { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// <c>font-fit-audit</c>: for each font, rasterises a fixed character set through the
/// product's <see cref="SdlCellGlyphRasterizer"/> in both fit modes (no window) and compares every
/// mask with the independent <see cref="GlyphMaskOracle"/> of the same mode. Records the common
/// Uniform size, baseline and extremes, and checks that ordinary text has one size, that modes do
/// not leak between rasteriser instances and that auxiliary font handles are released.
/// </summary>
public static class FontFitAudit
{
    public static readonly (string Group, string Chars)[] Groups =
    [
        ("czech-upper", "ŘÍŠŽŤČĎŮÁÉÓÚÝĚŇÜ"),
        ("czech-lower", "říšžťčďůáéóúýěňü"),
        ("ascii", new string(Enumerable.Range(0x21, 0x5E).Select(i => (char)i).ToArray())),
        ("latin", new string(SdlFontCellPlacement.UniformRepertoire.Where(c => c > '~' && !"ŘÍŠŽŤČĎŮÁÉÓÚÝĚŇÜříšžťčďůáéóúýěňü".Contains(c)).ToArray())),
        ("compatibility", SdlFontCellPlacement.UniformExcluded),
        ("symbols", "►◄▲▼■·€±≥≤÷≈°√²☺♥♪"),
        ("box", "─│┌┐└┘├┤┬┴┼═║╔╗╚╝╠╣╦╩╬╭╮╯╰╳▀▄▌▐█"),
    ];

    public static FontFitAuditResult Run(IEnumerable<string> fontIds)
    {
        var result = new FontFitAuditResult();
        try
        {
            foreach (string id in fontIds)
            {
                var (name, size) = FontSpec.Known[id];
                string path = SdlFontLocator.ProbeFontPathByName(name) ?? throw new InvalidOperationException($"font '{name}' is not installed");
                result.Fonts.Add(RunFont(id, path, size, SdlFontLocator.ProbeSymbolFontPath()));
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
        }
        return result;
    }

    private static FontFitFontResult RunFont(string id, string path, int size, string? symbolPath)
    {
        var r = new FontFitFontResult { FontId = id, Path = path, PointSize = size };
        using var condenseOracle = new GlyphMaskOracle(path, size, symbolPath, uniform: false);
        using var uniformOracle = new GlyphMaskOracle(path, size, symbolPath, uniform: true);

        IntPtr font = SDL3.TTF.OpenFont(path, size);
        IntPtr symbol = symbolPath == null ? IntPtr.Zero : SDL3.TTF.OpenFont(symbolPath, size);
        if (symbol != IntPtr.Zero) SDL3.TTF.AddFallbackFont(font, symbol);
        try
        {
            SdlFontMetrics.ComputeMetrics(font, out int cw, out int ch);
            r.Cell = $"{cw}x{ch}";
            r.Ascent = SDL3.TTF.GetFontAscent(font);
            r.Descent = SDL3.TTF.GetFontDescent(font);
            r.LineSkip = SDL3.TTF.GetFontLineSkip(font);
            string? sym = symbol == IntPtr.Zero ? null : symbolPath;

            var condense = new SdlCellGlyphRasterizer(font, path, size, sym, cw, ch, FontFitMode.Condense);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var uniform = new SdlCellGlyphRasterizer(font, path, size, sym, cw, ch, FontFitMode.Uniform);
            double uniformCreate = timer.Elapsed.TotalMilliseconds;
            var chars = Groups.SelectMany(g => g.Chars.Select(c => (g.Group, Char: c))).ToList();

            var masks = new Dictionary<(char, FontFitMode), byte[]>();
            foreach (FontFitMode mode in new[] { FontFitMode.Condense, FontFitMode.Uniform })
            {
                SdlCellGlyphRasterizer product = mode == FontFitMode.Uniform ? uniform : condense;
                timer.Restart();
                foreach (var (_, c) in chars) masks[(c, mode)] = product.Rasterize(c) ?? new byte[cw * ch];
                double perGlyph = timer.Elapsed.TotalMilliseconds / chars.Count;
                FontFitModeMetrics m = mode == FontFitMode.Uniform ? r.Uniform : r.Condense;
                GlyphMaskOracle oracle = mode == FontFitMode.Uniform ? uniformOracle : condenseOracle;
                m.Mode = mode.ToString();
                m.TextPointSize = product.UniformPointSize.ToString("0.#");
                m.TextBaseline = product.TextBaseline;
                m.MillisecondsPerGlyph = Math.Round(perGlyph, 3);
                m.OracleTextPointSize = oracle.UniformPointSize;
                m.OracleTextBaseline = oracle.TextBaseline;
                m.FittedGlyphs = product.FittedGlyphs;
                m.ClippedGlyphs = product.ClippedGlyphs;
                if (mode == FontFitMode.Uniform)
                {
                    m.Pen = product.UniformPen;
                    m.Extremes = product.UniformExtremes;
                    m.RejectedSizes = product.UniformRejectedSizes.ToList();
                    m.RepertoireGlyphs = product.UniformMeasuredGlyphs;
                    m.MeasureMilliseconds = Math.Round(uniformCreate, 1);
                    m.OracleTrace = oracle.UniformTrace;
                    m.OutlierGlyphs = product.OutlierGlyphs;
                }
                m.AuxiliaryFontsOpen = product.OpenAuxiliaryFonts;
            }

            foreach (var (group, c) in chars)
            {
                var g = new FontFitGlyph
                {
                    Char = c.ToString(), Codepoint = $"U+{(int)c:X4}", Group = group,
                    InRepertoire = SdlFontCellPlacement.UniformRepertoire.Contains(c),
                    Source = Cp437GlyphGenerator.IsGenerated(c) ? "generated" : condenseOracle.FontCoverage(c),
                };
                r.Glyphs.Add(g);
                if (Cp437GlyphGenerator.IsGenerated(c)) continue;
                GlyphMask ce = condenseOracle.Mask(c, 0, 0, FontGlyphPlacement.CellPlaced);
                GlyphMask ue = uniformOracle.Mask(c, 0, 0, FontGlyphPlacement.CellPlaced);
                byte[] cm = masks[(c, FontFitMode.Condense)], um = masks[(c, FontFitMode.Uniform)];
                g.CondenseFit = ce.Fit;
                g.UniformFit = ue.Fit;
                g.CondenseDifferingPixels = Differing(cm, ce.Alpha, cw * ch);
                g.UniformDifferingPixels = Differing(um, ue.Alpha, cw * ch);
                g.CondenseInk = InkBox(cm, cw, ch);
                g.UniformInk = InkBox(um, cw, ch);
                if (uniformOracle.NaturalText(c) is { HasInk: true } n)
                    g.UniformPartsVerdict = DiacriticChecks.Compare(DiacriticChecks.Parts(n.Alpha, n.Width, n.Height), DiacriticChecks.Parts(um, cw, ch));
            }

            // Leakage: a Condense rasteriser created after (and used interleaved with) a Uniform one
            // over the same font produces exactly the first Condense rasteriser's masks.
            var condenseAgain = new SdlCellGlyphRasterizer(font, path, size, sym, cw, ch, FontFitMode.Condense);
            var uniformAgain = new SdlCellGlyphRasterizer(font, path, size, sym, cw, ch, FontFitMode.Uniform);
            bool noLeak = true;
            foreach (var (_, c) in chars)
            {
                byte[] u2 = uniformAgain.Rasterize(c) ?? new byte[cw * ch];
                byte[] c2 = condenseAgain.Rasterize(c) ?? new byte[cw * ch];
                noLeak &= c2.AsSpan().SequenceEqual(masks[(c, FontFitMode.Condense)]) && u2.AsSpan().SequenceEqual(masks[(c, FontFitMode.Uniform)]);
            }

            foreach (SdlCellGlyphRasterizer x in new[] { condense, uniform, condenseAgain, uniformAgain }) x.Dispose();
            r.Condense.AuxiliaryFontsAfterDispose = condense.OpenAuxiliaryFonts + condenseAgain.OpenAuxiliaryFonts;
            r.Uniform.AuxiliaryFontsAfterDispose = uniform.OpenAuxiliaryFonts + uniformAgain.OpenAuxiliaryFonts;

            Measure(r, cw, ch);
            bool reduced = float.Parse(r.Uniform.TextPointSize) < size;
            var text = r.Glyphs.Where(g => g.InRepertoire).ToList();
            r.Checks["product-equals-oracle-condense"] = r.Glyphs.All(g => g.CondenseDifferingPixels == 0);
            r.Checks["product-equals-oracle-uniform"] = r.Glyphs.All(g => g.UniformDifferingPixels == 0);
            r.Checks["uniform-size-equals-oracle"] = r.Uniform.TextPointSize == r.Uniform.OracleTextPointSize.ToString() && r.Uniform.TextBaseline == r.Uniform.OracleTextBaseline;
            r.Checks["condense-keeps-configured-size"] = r.Condense.TextPointSize == size.ToString();
            r.Checks["uniform-never-larger-than-configured"] = float.Parse(r.Uniform.TextPointSize) <= size;
            r.Checks["uniform-repertoire-one-size-no-refit"] = text.All(g => g.UniformFit == null);
            r.Checks["uniform-repertoire-inside-cell"] = text.All(g => g.UniformInk[2] <= cw && g.UniformInk[3] <= ch);
            r.Checks["uniform-keeps-every-ink-part"] = text.All(g => g.UniformPartsVerdict == null);
            r.Checks["box-drawing-identical-in-both-modes"] = r.Glyphs.Where(g => g.Group == "box").All(g => g.CondenseInk.SequenceEqual(g.UniformInk));
            r.Checks["uniform-ascii-reduced-when-size-reduced"] = !reduced || Height(r, 'H', uniform: true) < Height(r, 'H', uniform: false);
            r.Checks["modes-do-not-leak-between-rasterisers"] = noLeak;
            r.Checks["auxiliary-fonts-released"] = r.Condense.AuxiliaryFontsAfterDispose == 0 && r.Uniform.AuxiliaryFontsAfterDispose == 0;
        }
        finally
        {
            if (font != IntPtr.Zero) { SDL3.TTF.ClearFallbackFonts(font); SDL3.TTF.CloseFont(font); }
            if (symbol != IntPtr.Zero) SDL3.TTF.CloseFont(symbol);
        }
        return r;
    }

    private static int Height(FontFitFontResult r, char c, bool uniform)
    {
        int[] ink = (uniform ? r.Glyphs.Single(g => g.Char == c.ToString()).UniformInk : r.Glyphs.Single(g => g.Char == c.ToString()).CondenseInk);
        return ink[3] - ink[1];
    }

    private static void Measure(FontFitFontResult r, int cw, int ch)
    {
        foreach (char c in "HMxgŽŮďť")
        {
            FontFitGlyph? g = r.Glyphs.FirstOrDefault(x => x.Char == c.ToString());
            if (g == null) continue;
            r.Measurements[$"{c} condense ink w×h @rows"] = $"{g.CondenseInk[2] - g.CondenseInk[0]}×{g.CondenseInk[3] - g.CondenseInk[1]} @{g.CondenseInk[1]}..{g.CondenseInk[3] - 1}{(g.CondenseFit is { } f ? " (" + f + ")" : "")}";
            r.Measurements[$"{c} uniform ink w×h @rows"] = $"{g.UniformInk[2] - g.UniformInk[0]}×{g.UniformInk[3] - g.UniformInk[1]} @{g.UniformInk[1]}..{g.UniformInk[3] - 1}{(g.UniformFit is { } f2 ? " (" + f2 + ")" : "")}";
        }
        var text = r.Glyphs.Where(g => g.InRepertoire && g.CondenseInk[2] > 0).ToList();
        r.Measurements["repertoire glyphs re-rasterised (condense)"] = text.Count(g => g.CondenseFit != null) + ": " + string.Concat(text.Where(g => g.CondenseFit != null).Select(g => g.Char));
        r.Measurements["repertoire glyphs re-rasterised (uniform)"] = text.Count(g => g.UniformFit != null).ToString();
        r.Measurements["glyphs outside the repertoire fitted (uniform)"] = string.Concat(r.Glyphs.Where(g => !g.InRepertoire && g.UniformFit != null).Select(g => g.Char));
        r.Measurements["mean ink height condense / uniform (repertoire)"] = $"{text.Average(g => g.CondenseInk[3] - g.CondenseInk[1]):F2} / {text.Average(g => g.UniformInk[3] - g.UniformInk[1]):F2}";
        r.Measurements["mean ink width condense / uniform (repertoire)"] = $"{text.Average(g => g.CondenseInk[2] - g.CondenseInk[0]):F2} / {text.Average(g => g.UniformInk[2] - g.UniformInk[0]):F2}";
        r.Measurements["max ink rows above baseline / below (uniform)"] = r.Uniform.Extremes;
    }

    private static int Differing(byte[] a, byte[]? b, int n)
    {
        b ??= new byte[n];
        return a.Length != b.Length ? -1 : a.Zip(b).Count(p => p.First != p.Second);
    }

    private static int[] InkBox(byte[] a, int w, int h)
    {
        int l = int.MaxValue, t = int.MaxValue, rr = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (a[y * w + x] != 0) { l = Math.Min(l, x); t = Math.Min(t, y); rr = Math.Max(rr, x + 1); b = Math.Max(b, y + 1); }
        return rr < 0 ? [0, 0, 0, 0] : [l, t, rr, b];
    }

    public static int Execute(string output, IEnumerable<string> fontIds)
    {
        if (!SDL3.TTF.Init()) { Console.Error.WriteLine("TTF_Init failed"); return 2; }
        FontFitAuditResult r;
        try { r = Run(fontIds); }
        finally { SDL3.TTF.Quit(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(r, ChildRunner.Json));
        foreach (FontFitFontResult f in r.Fonts)
            Console.WriteLine($"{f.FontId}: cell {f.Cell}, condense {f.Condense.TextPointSize}pt baseline {f.Condense.TextBaseline}, uniform {f.Uniform.TextPointSize}pt baseline {f.Uniform.TextBaseline} pen {f.Uniform.Pen} ({f.Uniform.Extremes}); failed checks: {string.Join(", ", f.Checks.Where(c => !c.Value).Select(c => c.Key).DefaultIfEmpty("none"))}");
        bool failed = r.Error != null || r.Fonts.Any(f => f.Checks.Values.Any(v => !v));
        return r.Error != null ? 2 : failed ? 1 : 0;
    }
}
