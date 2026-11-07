using System.Runtime.InteropServices;
using TSharpVision.Drivers.SDL.Rendering.GlyphComposition;
using TSharpVision.Drivers.SDL.Rendering.PixelAnalysis;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>How a renderer puts a font glyph into its cell, which decides the expected coverage.</summary>
public enum FontGlyphPlacement
{
    /// <summary>
    /// The shared rule both SDL back-ends follow. Text sits on the text baseline (the
    /// font baseline moved down into rows the descenders never use, only as far as the accents
    /// need). Text that still does not fit is re-rasterised by FreeType: only the overflowing axis
    /// condensed (DPI 71 → 62), else shrunk uniformly in half points. Box drawing and
    /// block elements (U+2500–U+259F) sit on the font baseline and are clipped.
    /// </summary>
    CellPlaced,

    /// <summary>
    /// The older GPU rule, kept only to simulate the defect in negative controls: every font
    /// glyph on the font baseline, rows above the ascent trimmed (Ž drawn as Ż).
    /// </summary>
    LegacyClipAboveAscent,

    // ── negative controls for fontFit=Uniform (simulated defects, never a product rule) ──

    /// <summary>The Condense rule whatever the oracle's mode: ordinary text at the configured size.</summary>
    CondenseRule,

    /// <summary>
    /// The rejected strategy: text at the configured size on the Condense baseline, and only the
    /// glyphs that do not fit shrunk proportionally (accented letters smaller than plain ones).
    /// </summary>
    PerGlyphShrink,

    /// <summary>All text squeezed horizontally to the common width but kept at full height.</summary>
    HorizontalOnly,

    /// <summary>Uniform text one row above its baseline.</summary>
    BaselineOneRowUp,

    /// <summary>
    /// Box drawing and block elements, generated CP437 ones included, drawn like Uniform text from
    /// the font at the common size (smaller, gaps between cells).
    /// </summary>
    TextSizedBoxes,
}

/// <summary>Expected coverage of one cell's glyph.</summary>
/// <param name="Alpha">Coverage, cellWidth × cellHeight bytes, or null for no ink.</param>
/// <param name="Unverified">True when the oracle cannot state exact coverage.</param>
/// <param name="Source">generated | font | font-fallback | none.</param>
/// <param name="Reason">Why the shape is unverified or clipped, if it is.</param>
/// <param name="Fit">How the glyph was re-rasterised to fit ("20pt 72x66dpi"), if it was.</param>
/// <param name="Clipped">True when the glyph did not fit even at the smallest size tried.</param>
public sealed record GlyphMask(byte[]? Alpha, bool Unverified, string Source, string? Reason = null, string? Fit = null, bool Clipped = false);

/// <summary>A glyph as SDL_ttf rasterises it, before any cell placement.</summary>
/// <param name="Alpha">Surface coverage, Width × Height.</param>
/// <param name="OriginX">Pen-relative x of surface column 0.</param>
/// <param name="BaselineRow">Surface row of the baseline.</param>
/// <param name="InkLeft">Pen-relative first inked column (InkRight exclusive); InkTop/InkBottom are baseline-relative, bottom exclusive.</param>
public sealed record NaturalGlyph(byte[] Alpha, int Width, int Height, int OriginX, int BaselineRow, int Advance,
    int InkLeft, int InkTop, int InkRight, int InkBottom)
{
    public bool HasInk => InkRight > InkLeft;
}

/// <summary>
/// Glyph coverage computed without the renderers under test. Shapes come from the same glyph
/// <em>sources</em> the product uses — the font file rasterised by SDL_ttf (FreeType), and the
/// cell-geometry CP437 B0–DF generator — but baseline choice, fitting, placement, clipping and
/// colour are this class's own implementation of the documented rules, not a call into
/// SDLRenderer / SDLGpuRenderer / SdlCellGlyphRasterizer.
/// </summary>
public sealed class GlyphMaskOracle : IDisposable
{
    // Restated from the documented rule (SdlFontCellPlacement), deliberately not referenced.
    private const string Descenders = "gjpqy,;_()[]{}|$@Q";
    private const string AccentedCapitals =
        "ÀÁÂÃÄÅÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝ" +
        "ĀĂĄĆĈĊČĎĒĖĚĜĞĠĤĨĪĮİĴĹĽŃŇŌŐŔŘŚŜŞŠŢŤŨŪŬŮŰŲŴŶŸŹŻŽ";

    // fontFit=Uniform sizing repertoire, restated from the documented rule: printable ASCII,
    // Latin-1 letters U+00C0–U+00FF without × ÷, Latin Extended-A without the compatibility
    // characters Ĳ ĳ Ŀ ŀ ŉ ſ.
    private const string UniformExcluded = "ĲĳĿŀŉſ";

    private readonly IntPtr _font;
    private readonly IntPtr _symbolFont;
    private IntPtr _fitFont;
    private IntPtr _fitSymbolFont;
    private IntPtr _uniformFont;
    private IntPtr _uniformSymbolFont;
    private readonly CellGlyphBitmapCache _generated;
    private readonly Dictionary<(char, FontGlyphPlacement), GlyphMask> _cache = new();
    private bool _ttfInitialized;

    public int CellWidth { get; }
    public int CellHeight { get; }
    public int Ascent { get; }
    /// <summary>Row text glyphs sit on under <see cref="FontGlyphPlacement.CellPlaced"/> (the oracle's fit mode).</summary>
    public int TextBaseline { get; }
    /// <summary>Row text glyphs sit on under the Condense rule (equal to <see cref="TextBaseline"/> unless Uniform).</summary>
    public int CondenseTextBaseline { get; }
    public int DescenderDepth { get; }
    public int AccentOverflow { get; }
    public string FontPath { get; }
    public string? SymbolFontPath { get; }
    public int PointSize { get; }

    /// <summary>True when <see cref="FontGlyphPlacement.CellPlaced"/> follows fontFit=Uniform, else Condense.</summary>
    public bool Uniform { get; }
    /// <summary>Uniform: the common text size (the configured size under Condense).</summary>
    public int UniformPointSize { get; }
    public int UniformPen { get; }
    public int UniformMaxAbove { get; }
    public int UniformMaxBelow { get; }
    public int UniformMaxWidth { get; }
    public int UniformRepertoireSize { get; }
    /// <summary>Uniform: per size tried, the repertoire extremes ("20pt above 18 below 4 width 12 → no").</summary>
    public List<string> UniformTrace { get; } = new();

    public GlyphMaskOracle(string fontPath, int pointSize, string? symbolFontPath, bool uniform = false)
    {
        FontPath = fontPath;
        PointSize = pointSize;
        Uniform = uniform;
        if (!SDL3.TTF.Init()) throw new InvalidOperationException("TTF_Init failed: " + SDL3.SDL.GetError());
        _ttfInitialized = true;
        _font = SDL3.TTF.OpenFont(fontPath, pointSize);
        if (_font == IntPtr.Zero) throw new InvalidOperationException($"TTF_OpenFont({fontPath}) failed: {SDL3.SDL.GetError()}");
        if (symbolFontPath != null)
        {
            _symbolFont = SDL3.TTF.OpenFont(symbolFontPath, pointSize);
            if (_symbolFont != IntPtr.Zero)
            {
                SDL3.TTF.AddFallbackFont(_font, _symbolFont);
                SymbolFontPath = symbolFontPath;
            }
        }
        Ascent = SDL3.TTF.GetFontAscent(_font);

        // Cell size by the documented rule (font height; widest advance of M W 0 X).
        int height = SDL3.TTF.GetFontHeight(_font);
        int width = 0;
        foreach (char c in "MW0X")
            if (SDL3.TTF.GetGlyphMetrics(_font, c, out _, out _, out _, out _, out int adv)) width = Math.Max(width, adv);
        CellWidth = width;
        CellHeight = height;
        _generated = new CellGlyphBitmapCache(new Cp437GlyphGenerator(CellWidth, CellHeight));

        // Text baseline by the documented rule: move down by the accents' overflow, but never
        // into rows the deepest descender needs.
        DescenderDepth = Math.Max(0, Probe(Descenders).Select(g => g.InkBottom).DefaultIfEmpty(0).Max());
        AccentOverflow = Math.Max(0, Probe(AccentedCapitals).Select(g => -g.InkTop - Ascent).DefaultIfEmpty(0).Max());
        int spare = Math.Max(0, CellHeight - Ascent - DescenderDepth);
        CondenseTextBaseline = Ascent + Math.Min(AccentOverflow, spare);
        TextBaseline = CondenseTextBaseline;
        UniformPointSize = pointSize;
        if (!uniform) return;

        // Uniform by the documented rule: the largest whole point size, from the configured one down
        // to half of it, at which every repertoire glyph's ink fits the cell on one common baseline.
        var repertoire = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range(0xC0, 0x40).Where(c => c is not (0xD7 or 0xF7)))
            .Concat(Enumerable.Range(0x100, 0x80))
            .Select(c => (char)c).Where(c => !UniformExcluded.Contains(c) && SDL3.TTF.FontHasGlyph(_font, c)).ToList();
        UniformRepertoireSize = repertoire.Count;
        int chosen = -1;
        (int above, int below, int width) chosenExtremes = default;
        int lastTried = pointSize;
        for (int size = pointSize; size * 2 >= pointSize; size--)
        {
            lastTried = size;
            IntPtr f = SDL3.TTF.OpenFont(fontPath, size);
            if (f == IntPtr.Zero) throw new InvalidOperationException($"TTF_OpenFont({fontPath}, {size}) failed: {SDL3.SDL.GetError()}");
            int above = 0, below = 0, inkWidth = 0;
            foreach (char c in repertoire)
            {
                if (Rasterise(f, c) is not { HasInk: true } g) continue;
                above = Math.Max(above, -g.InkTop);
                below = Math.Max(below, g.InkBottom);
                inkWidth = Math.Max(inkWidth, g.InkRight - g.InkLeft);
            }
            SDL3.TTF.CloseFont(f);
            bool fits = inkWidth <= CellWidth && above + below <= CellHeight;
            UniformTrace.Add($"{size}pt above {above} below {below} width {inkWidth} → {(fits ? "fits" : "no")}");
            chosenExtremes = (above, below, inkWidth);
            if (fits) { chosen = size; break; }
        }
        if (chosen < 0) chosen = lastTried;
        (UniformMaxAbove, UniformMaxBelow, UniformMaxWidth) = chosenExtremes;
        UniformPointSize = chosen;

        _uniformFont = SDL3.TTF.OpenFont(fontPath, chosen);
        if (_uniformFont == IntPtr.Zero) throw new InvalidOperationException($"TTF_OpenFont({fontPath}, {chosen}) failed: {SDL3.SDL.GetError()}");
        if (SymbolFontPath != null)
        {
            _uniformSymbolFont = SDL3.TTF.OpenFont(SymbolFontPath, chosen);
            if (_uniformSymbolFont != IntPtr.Zero) SDL3.TTF.AddFallbackFont(_uniformFont, _uniformSymbolFont);
        }
        // Baseline: the font's line box at the common size centred in the cell, kept inside the
        // band where the repertoire's ink fits. Pen: the common advance centred in the cell.
        int lineBoxTop = SDL3.TTF.GetFontAscent(_uniformFont) + (CellHeight - SDL3.TTF.GetFontHeight(_uniformFont)) / 2;
        int lo = UniformMaxAbove, hi = CellHeight - UniformMaxBelow;
        TextBaseline = hi < lo ? lo : Math.Clamp(lineBoxTop, lo, hi);
        int advM = SDL3.TTF.GetGlyphMetrics(_uniformFont, 'M', out _, out _, out _, out _, out int a) ? a : CellWidth;
        UniformPen = Math.Max(0, (CellWidth - advM) / 2);
    }

    private IEnumerable<NaturalGlyph> Probe(string chars) =>
        chars.Where(c => SDL3.TTF.FontHasGlyph(_font, c)).Select(Natural).OfType<NaturalGlyph>().Where(g => g.HasInk);

    /// <summary>Whether the primary font, or else the symbol fallback, has a glyph for <paramref name="ch"/>.</summary>
    public string FontCoverage(char ch) =>
        SDL3.TTF.FontHasGlyph(_font, ch) ? "font" : _symbolFont != IntPtr.Zero && SDL3.TTF.FontHasGlyph(_symbolFont, ch) ? "font-fallback" : "missing";

    /// <summary>Box drawing and block elements: cell-filling designs, clipped rather than fitted.</summary>
    public static bool IsCellFill(char ch) => ch is >= '─' and <= '▟';

    public GlyphMask Mask(char ch, int col, int row, FontGlyphPlacement placement)
    {
        if (ch is ' ' or '\0') return new GlyphMask(null, false, "none");
        GlyphPhase phase = GlyphPhase.ForCell(col, row, CellWidth, CellHeight);
        if (Cp437GlyphGenerator.IsGenerated(ch) && !(placement == FontGlyphPlacement.TextSizedBoxes && Uniform))
        {
            if (!_generated.TryGet(ch, phase, out AlphaBitmap bmp)) return new GlyphMask(null, false, "none");
            return new GlyphMask(bmp.Alpha, false, "generated");
        }
        // Font glyphs do not depend on the cell phase.
        var key = (ch, placement);
        if (_cache.TryGetValue(key, out GlyphMask? cached)) return cached;
        GlyphMask mask = PlaceFontGlyph(ch, placement);
        _cache[key] = mask;
        return mask;
    }

    private GlyphMask PlaceFontGlyph(char ch, FontGlyphPlacement placement)
    {
        string source = FontCoverage(ch) == "font-fallback" ? "font-fallback" : "font";
        NaturalGlyph? g = Natural(ch);
        if (g == null) return new GlyphMask(null, false, "none", "SDL_ttf produced no surface");
        var canvas = new byte[CellWidth * CellHeight];
        bool uniformText = Uniform && placement is FontGlyphPlacement.CellPlaced or FontGlyphPlacement.BaselineOneRowUp;

        if (placement == FontGlyphPlacement.LegacyClipAboveAscent || (IsCellFill(ch) && placement != FontGlyphPlacement.TextSizedBoxes))
        {
            Draw(g, canvas, 0, Ascent);
            return new GlyphMask(canvas, false, source);
        }

        switch (placement)
        {
            case FontGlyphPlacement.TextSizedBoxes when Uniform:
            case FontGlyphPlacement.CellPlaced or FontGlyphPlacement.BaselineOneRowUp when uniformText:
            {
                // Uniform: every text glyph at the common size, common pen and baseline. A glyph
                // outside the repertoire that still does not fit takes the Condense fit from there.
                int baseline = TextBaseline - (placement == FontGlyphPlacement.BaselineOneRowUp ? 1 : 0);
                NaturalGlyph? u = Rasterise(_uniformFont, ch);
                if (u == null) return new GlyphMask(null, false, "none", "SDL_ttf produced no surface");
                if (placement == FontGlyphPlacement.BaselineOneRowUp)
                {
                    Draw(u, canvas, UniformPen, baseline);
                    return new GlyphMask(canvas, false, source);
                }
                return FitByCondense(ch, u, UniformPointSize, baseline, UniformPen, condenseFirst: true, canvas, source, outlier: true);
            }
            case FontGlyphPlacement.HorizontalOnly:
            {
                int hdpi = (int)Math.Round(72.0 * UniformPointSize / PointSize);
                NaturalGlyph? h = Smaller(ch, PointSize, hdpi, 72);
                if (h == null) return new GlyphMask(null, false, "none");
                Draw(h, canvas, Math.Max(0, (CellWidth - h.Advance) / 2), CondenseTextBaseline);
                return new GlyphMask(canvas, false, source, Fit: $"{PointSize}pt {hdpi}x72dpi");
            }
            case FontGlyphPlacement.PerGlyphShrink:
                return FitByCondense(ch, g, PointSize, CondenseTextBaseline, 0, condenseFirst: false, canvas, source, outlier: false);
            default: // CellPlaced under Condense, CondenseRule
                return FitByCondense(ch, g, PointSize, CondenseTextBaseline, 0, condenseFirst: true, canvas, source, outlier: false);
        }
    }

    /// <summary>
    /// The Condense procedure for <paramref name="g"/> rasterised at <paramref name="pt"/>: as is when
    /// it fits; otherwise the overflowing axis condensed (DPI 71 → 62, when only one overflows and
    /// <paramref name="condenseFirst"/>), then shrunk in half points down to half the size.
    /// </summary>
    private GlyphMask FitByCondense(char ch, NaturalGlyph g, int pt0, int baseline, int pen0, bool condenseFirst, byte[] canvas, string source, bool outlier)
    {
        if (Fit(g, pen0, baseline) is int pen)
        {
            Draw(g, canvas, pen, baseline);
            return new GlyphMask(canvas, false, source);
        }

        bool wide = g.InkRight - g.InkLeft > CellWidth;
        bool tall = baseline + g.InkTop < 0 || baseline + g.InkBottom > CellHeight;
        var attempts = new List<(float Pt, int H, int V)>();
        if (condenseFirst && wide != tall)
            for (int dpi = 71; dpi >= 62; dpi--) attempts.Add(wide ? (pt0, dpi, 72) : (pt0, 72, dpi));
        for (float size = pt0 - 0.5f; size >= pt0 / 2f; size -= 0.5f) attempts.Add((size, 72, 72));

        NaturalGlyph? last = null;
        int lastPen = 0;
        string? lastFit = null;
        string prefix = outlier ? "outlier " : "";
        foreach (var (pt, h, v) in attempts)
        {
            NaturalGlyph? s = Smaller(ch, pt, h, v);
            if (s == null) continue;
            int centred = Math.Max(0, (CellWidth - s.Advance) / 2);
            (last, lastPen, lastFit) = (s, centred, $"{prefix}{pt:0.#}pt {h}x{v}dpi");
            if (Fit(s, centred, baseline) is int p)
            {
                Draw(s, canvas, p, baseline);
                return new GlyphMask(canvas, false, source, Fit: lastFit);
            }
        }
        Draw(last ?? g, canvas, last == null ? 0 : lastPen, baseline);
        return new GlyphMask(canvas, false, source, "does not fit the cell at any size tried; clipped", lastFit, Clipped: true);
    }

    /// <summary>Pen column that keeps the ink inside the cell on <paramref name="baseline"/>, or null.</summary>
    private int? Fit(NaturalGlyph g, int pen, int baseline)
    {
        if (!g.HasInk) return pen;
        if (g.InkRight - g.InkLeft > CellWidth) return null;
        if (baseline + g.InkTop < 0 || baseline + g.InkBottom > CellHeight) return null;
        int shift = 0;
        if (pen + g.InkLeft < 0) shift = -(pen + g.InkLeft);
        else if (pen + g.InkRight > CellWidth) shift = CellWidth - (pen + g.InkRight);
        return pen + shift;
    }

    private void Draw(NaturalGlyph g, byte[] canvas, int pen, int baseline)
    {
        for (int y = 0; y < g.Height; y++)
        {
            int cy = baseline - g.BaselineRow + y;
            if ((uint)cy >= (uint)CellHeight) continue;
            for (int x = 0; x < g.Width; x++)
            {
                int cx = pen + g.OriginX + x;
                if ((uint)cx < (uint)CellWidth) canvas[cy * CellWidth + cx] = g.Alpha[y * g.Width + x];
            }
        }
    }

    /// <summary>The glyph at the oracle font's size, unplaced.</summary>
    public NaturalGlyph? Natural(char ch) => Rasterise(_font, ch);

    /// <summary>
    /// The text glyph unplaced at the size the oracle's mode draws ordinary text at: the configured
    /// size (Condense), or the common size (Uniform). Reference for the anti-clipping check: a glyph
    /// rasterised smaller can legitimately join parts (Å's ring touching the A).
    /// </summary>
    public NaturalGlyph? NaturalText(char ch) =>
        Uniform && !IsCellFill(ch) && _uniformFont != IntPtr.Zero ? Rasterise(_uniformFont, ch) : Natural(ch);

    private NaturalGlyph? Smaller(char ch, float size, int hdpi, int vdpi)
    {
        if (_fitFont == IntPtr.Zero)
        {
            _fitFont = SDL3.TTF.OpenFont(FontPath, PointSize);
            if (_fitFont == IntPtr.Zero) throw new InvalidOperationException($"TTF_OpenFont({FontPath}) failed: {SDL3.SDL.GetError()}");
            if (SymbolFontPath != null)
            {
                _fitSymbolFont = SDL3.TTF.OpenFont(SymbolFontPath, PointSize);
                if (_fitSymbolFont != IntPtr.Zero) SDL3.TTF.AddFallbackFont(_fitFont, _fitSymbolFont);
            }
        }
        SDL3.TTF.SetFontSizeDPI(_fitFont, size, hdpi, vdpi);
        if (_fitSymbolFont != IntPtr.Zero) SDL3.TTF.SetFontSizeDPI(_fitSymbolFont, size, hdpi, vdpi);
        return Rasterise(_fitFont, ch);
    }

    private static NaturalGlyph? Rasterise(IntPtr font, char ch)
    {
        int ascent = SDL3.TTF.GetFontAscent(font);
        if (!SDL3.TTF.GetGlyphMetrics(font, ch, out int minX, out _, out _, out int maxY, out int adv)) (minX, maxY, adv) = (0, ascent, 0);
        SDL3.SDL.Color white = new() { R = 255, G = 255, B = 255, A = 255 };
        IntPtr surf = SDL3.TTF.RenderGlyphBlended(font, ch, white);
        if (surf == IntPtr.Zero) return null;
        try
        {
            (byte[] alpha, int w, int h) = ReadAlpha(surf);
            int originX = Math.Min(0, minX), baseRow = Math.Max(ascent, maxY);
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (alpha[y * w + x] != 0) { l = Math.Min(l, x); r = Math.Max(r, x + 1); t = Math.Min(t, y); b = Math.Max(b, y + 1); }
            return l == int.MaxValue
                ? new NaturalGlyph(alpha, w, h, originX, baseRow, adv, 0, 0, 0, 0)
                : new NaturalGlyph(alpha, w, h, originX, baseRow, adv, l + originX, t - baseRow, r + originX, b - baseRow);
        }
        finally { SDL3.SDL.DestroySurface(surf); }
    }

    private static (byte[] Alpha, int Width, int Height) ReadAlpha(IntPtr surf)
    {
        IntPtr conv = SDL3.SDL.ConvertSurface(surf, SDL3.SDL.PixelFormat.ABGR8888);
        if (conv == IntPtr.Zero) throw new InvalidOperationException("ConvertSurface failed: " + SDL3.SDL.GetError());
        try
        {
            SDL3.SDL.Surface s = Marshal.PtrToStructure<SDL3.SDL.Surface>(conv);
            var raw = new byte[s.Pitch * s.Height];
            Marshal.Copy(s.Pixels, raw, 0, raw.Length);
            var alpha = new byte[s.Width * s.Height];
            for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++)
                    alpha[y * s.Width + x] = raw[y * s.Pitch + x * 4 + 3];
            return (alpha, s.Width, s.Height);
        }
        finally { SDL3.SDL.DestroySurface(conv); }
    }

    public void Dispose()
    {
        if (_fitFont != IntPtr.Zero) { SDL3.TTF.ClearFallbackFonts(_fitFont); SDL3.TTF.CloseFont(_fitFont); _fitFont = IntPtr.Zero; }
        if (_fitSymbolFont != IntPtr.Zero) { SDL3.TTF.CloseFont(_fitSymbolFont); _fitSymbolFont = IntPtr.Zero; }
        if (_uniformFont != IntPtr.Zero) { SDL3.TTF.ClearFallbackFonts(_uniformFont); SDL3.TTF.CloseFont(_uniformFont); _uniformFont = IntPtr.Zero; }
        if (_uniformSymbolFont != IntPtr.Zero) { SDL3.TTF.CloseFont(_uniformSymbolFont); _uniformSymbolFont = IntPtr.Zero; }
        if (_symbolFont != IntPtr.Zero) SDL3.TTF.CloseFont(_symbolFont);
        if (_font != IntPtr.Zero) SDL3.TTF.CloseFont(_font);
        if (_ttfInitialized) { SDL3.TTF.Quit(); _ttfInitialized = false; }
    }
}
