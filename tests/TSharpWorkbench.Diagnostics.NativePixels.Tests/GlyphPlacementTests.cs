using System.Text.Json;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativePixels.Tests;

/// <summary>
/// Glyph placement. The product's shared rasteriser is compared glyph by glyph with the
/// independent oracle and with the unplaced SDL_ttf glyph (<c>glyph-audit</c>, own process), and
/// the real renderers' Czech fixture frames are checked by the placement-independent anti-clipping
/// check and against each other.
/// </summary>
[Collection(RealRendererCollection.Name)]
public sealed class GlyphPlacementTests(RealRendererFixture fixture)
{
    private const string CzechCapitals = "ŘÍŠŽŤČĎŮÁÉÓÚÝĚŇ";
    private static readonly Lazy<(GlyphAuditResult? Result, string Log)> Audit = new(RunAudit);

    private static (GlyphAuditResult?, string) RunAudit()
    {
        string file = Path.Combine(RealRendererFixture.OutputRoot, "glyph-audit.json");
        Directory.CreateDirectory(RealRendererFixture.OutputRoot);
        string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "TSharpWorkbench.Diagnostics.NativePixels.exe" : "TSharpWorkbench.Diagnostics.NativePixels");
        var (code, stdout, stderr) = Parent.Exec(exe, ["glyph-audit", file], TimeSpan.FromSeconds(120));
        GlyphAuditResult? r = File.Exists(file) ? JsonSerializer.Deserialize<GlyphAuditResult>(File.ReadAllText(file), ChildRunner.Json) : null;
        return (r, $"exit {code}\n{stdout}\n{stderr}");
    }

    private static GlyphAuditResult RequireAudit()
    {
        Skip.IfNot(OperatingSystem.IsWindows() && File.Exists(FontSpec.Default.Path), "the pinned font (Consolas) is Windows-only");
        var (r, log) = Audit.Value;
        Assert.True(r != null, "glyph-audit produced no result\n" + log);
        Assert.True(r!.Error == null, r.Error);
        return r;
    }

    private static GlyphAuditEntry Entry(GlyphAuditResult r, char c) => r.Entries.Single(e => e.Char == c.ToString());

    private static int Baseline(GlyphAuditResult r) => r.ProductTextBaseline;

    // ── Font metrics, cell geometry, shared policy ─────────────────────────────

    [SkippableFact]
    public void Cell_geometry_is_unchanged_and_the_text_baseline_uses_the_spare_descent_row()
    {
        GlyphAuditResult r = RequireAudit();
        Assert.Equal("11x20", r.ProductCell);
        Assert.Equal(r.ProductCell, r.OracleCell);
        Assert.Equal(15, r.Ascent);
        Assert.Equal(4, r.DescenderDepth);   // g j p q y reach 4 rows below the baseline
        Assert.Equal(3, r.AccentOverflow);   // Ů rises 3 rows above the ascent
        Assert.Equal(16, r.ProductTextBaseline); // one spare row: 15 + min(3, 20 - 15 - 4)
        Assert.Equal(r.OracleTextBaseline, r.ProductTextBaseline);
    }

    [SkippableFact]
    public void Every_product_glyph_mask_equals_the_independent_oracle()
    {
        GlyphAuditResult r = RequireAudit();
        Assert.All(r.Entries, e => Assert.True(e.MatchesOracle, $"{e.Char} {e.Codepoint}: {e.DifferingPixels} px differ"));
        Assert.Equal(0, r.ProductClippedGlyphs);
        Assert.All(r.Entries, e => Assert.False(e.Clipped, e.Char));
    }

    // ── Czech capitals: taller than the cell, accents kept ──────────────────────

    [SkippableFact]
    public void Czech_capitals_exceed_the_cell_unplaced_and_keep_every_accent_pixel_part_when_placed()
    {
        GlyphAuditResult r = RequireAudit();
        foreach (char c in CzechCapitals)
        {
            GlyphAuditEntry e = Entry(r, c);
            Assert.True(e.NaturalExceedsCell, $"{c}: expected the SDL_ttf glyph to rise above the cell");
            Assert.True(e.NaturalSurfaceHeight > 20, $"{c}: surface {e.NaturalSurfaceHeight} rows");
            Assert.Equal(2, e.NaturalParts);
            Assert.Equal(2, e.PlacedParts);
            Assert.Null(e.PartsVerdict);
            // Inside the cell, sitting on the shared baseline.
            Assert.True(e.CellInk[1] >= 0 && e.CellInk[3] == Baseline(r), $"{c}: ink rows {e.CellInk[1]}..{e.CellInk[3]}");
            Assert.True(e.CellInk[0] >= 0 && e.CellInk[2] <= 11, $"{c}: ink columns {e.CellInk[0]}..{e.CellInk[2]}");
        }
    }

    [SkippableFact]
    public void The_original_clip_above_ascent_rule_is_detected_on_every_czech_capital()
    {
        // Regression evidence for the native-pixel defect: the pre-fix rule reduces the accent to a dot
        // (or removes it); the placement-independent check must reject it.
        GlyphAuditResult r = RequireAudit();
        foreach (char c in CzechCapitals)
            Assert.NotNull(Entry(r, c).LegacyPartsVerdict);
        Assert.NotNull(Entry(r, 'Ü').LegacyPartsVerdict);
    }

    [SkippableFact]
    public void Oversized_capitals_are_condensed_vertically_only_a_little_and_keep_their_width()
    {
        GlyphAuditResult r = RequireAudit();
        foreach (char c in CzechCapitals)
        {
            GlyphAuditEntry e = Entry(r, c);
            Assert.NotNull(e.Fit);
            Assert.Matches(@"^20pt 72x(6[2-9]|7[01])dpi$", e.Fit!); // only the vertical axis, at most 15 %
            int naturalWidth = e.NaturalInk[2] - e.NaturalInk[0], placedWidth = e.CellInk[2] - e.CellInk[0];
            Assert.InRange(placedWidth, naturalWidth - 1, naturalWidth);
            int placedHeight = e.CellInk[3] - e.CellInk[1], naturalHeight = e.NaturalInk[3] - e.NaturalInk[1];
            Assert.True(placedHeight >= naturalHeight * 85 / 100, $"{c}: {placedHeight} of {naturalHeight} rows");
        }
        // Ü fits once the baseline uses the spare row: not re-rasterised at all.
        Assert.Null(Entry(r, 'Ü').Fit);
    }

    // ── Czech lower case: negative bearings, wide ď ─────────────────────────────

    [SkippableFact]
    public void Lowercase_accents_fit_and_negative_bearings_land_inside_the_cell()
    {
        GlyphAuditResult r = RequireAudit();
        foreach (char c in "říšžťčůáéóúýěňü")
        {
            GlyphAuditEntry e = Entry(r, c);
            Assert.Null(e.Fit);
            Assert.Null(e.PartsVerdict);
            Assert.Equal(e.NaturalParts, e.PlacedParts);
            // Unscaled: the placed ink is exactly the unplaced ink moved onto the baseline.
            Assert.Equal(e.NaturalInk[2] - e.NaturalInk[0], e.CellInk[2] - e.CellInk[0]);
            Assert.Equal(e.NaturalInk[3] - e.NaturalInk[1], e.CellInk[3] - e.CellInk[1]);
            Assert.Equal(Baseline(r) + e.NaturalInk[1], e.CellInk[1]);
        }
        // ť has a -2 left bearing (13-px surface): its ink starts at column 0, not cut.
        GlyphAuditEntry t = Entry(r, 'ť');
        Assert.Equal(13, t.NaturalSurfaceWidth);
        Assert.Equal(0, t.CellInk[0]);
    }

    [SkippableFact]
    public void Wide_d_caron_is_condensed_horizontally_and_keeps_the_ascender_height_of_d()
    {
        GlyphAuditResult r = RequireAudit();
        GlyphAuditEntry dc = Entry(r, 'ď'), d = Entry(r, 'd');
        Assert.Equal(12, dc.NaturalInk[2] - dc.NaturalInk[0]); // one column wider than the cell
        Assert.Matches(@"^20pt (6[2-9]|7[01])x72dpi$", dc.Fit!);
        Assert.InRange(dc.CellInk[2] - dc.CellInk[0], 1, 11);
        Assert.Equal(dc.NaturalInk[3] - dc.NaturalInk[1], dc.CellInk[3] - dc.CellInk[1]); // height untouched
        Assert.Equal(d.CellInk[3], dc.CellInk[3]);
        Assert.True(dc.CellInk[1] <= d.CellInk[1], "ď must reach at least as high as d");
        Assert.Null(dc.PartsVerdict);
    }

    // ── ASCII, symbols, box drawing ─────────────────────────────────────────────

    [SkippableFact]
    public void Ascii_glyphs_are_never_resized_and_keep_their_metrics_on_the_shared_baseline()
    {
        GlyphAuditResult r = RequireAudit();
        foreach (GlyphAuditEntry e in r.Entries.Where(e => e.Group == "ascii"))
        {
            Assert.Null(e.Fit);
            Assert.Null(e.PartsVerdict);
            Assert.Equal(e.NaturalInk[2] - e.NaturalInk[0], e.CellInk[2] - e.CellInk[0]);
            Assert.Equal(e.NaturalInk[3] - e.NaturalInk[1], e.CellInk[3] - e.CellInk[1]);
            Assert.Equal(Baseline(r) + e.NaturalInk[1], e.CellInk[1]);
        }
        // Capitals without accents sit on the same baseline as the accented ones.
        Assert.Equal(Baseline(r), Entry(r, 'H').CellInk[3]);
        Assert.Equal(Entry(r, 'Z').CellInk[3], Entry(r, 'Ž').CellInk[3]);
        // Deepest descender ends on the last cell row; '|' (21-row surface) now fits whole.
        Assert.Equal(20, Entry(r, 'g').CellInk[3]);
        Assert.Equal(new[] { 0, 20 }, new[] { Entry(r, '|').CellInk[1], Entry(r, '|').CellInk[3] });
    }

    [SkippableFact]
    public void Box_drawing_from_the_font_keeps_the_font_baseline_and_fills_the_cell()
    {
        GlyphAuditResult r = RequireAudit();
        foreach (GlyphAuditEntry e in r.Entries.Where(e => e.Group == "box-font"))
        {
            Assert.Equal("CellFill", e.Mode);
            Assert.Null(e.Fit);
            Assert.True(e.MatchesOracle);
        }
        // ╳ spans the full cell: no gap between neighbouring cells.
        Assert.Equal(new[] { 0, 0, 11, 20 }, Entry(r, '╳').CellInk);
    }

    [SkippableFact]
    public void Rasterisation_cost_stays_small()
    {
        GlyphAuditResult r = RequireAudit();
        Assert.True(r.MillisecondsPerGlyph < 20, $"{r.MillisecondsPerGlyph} ms per glyph");
    }

    // ── Real renderers: Czech fixture ───────────────────────────────────────────

    public static TheoryData<string> RequiredPaths => RealRendererTests.RequiredPaths;

    private PathResult Require(string path)
    {
        var (result, dir, log) = fixture.Run(path);
        Assert.True(result != null, $"{path}: child produced no result ({dir})\n{log}");
        Skip.If(result!.Outcome is "PlatformUnavailable" or "BackendUnavailable", $"{path}: {result.Unavailable}");
        Assert.True(result.Error == null, $"{path}: {result.Error}");
        return result;
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Czech_frames_match_the_oracle_and_keep_every_accent(string path)
    {
        PathResult r = Require(path);
        List<StepResult> czech = r.Steps.Where(s => s.Fixture == CzechFixture.Id).ToList();
        Assert.Equal(12, czech.Count); // 3 grids × (full, block caret, underline caret, resize)
        foreach (StepResult s in czech)
        {
            Assert.True(s.Outcome == "Match", $"{path} {s.Step} {s.Grid}: {s.Reason}");
            Assert.Equal(0, s.Oracle!.Differing);
            Assert.Equal(0, s.UnverifiedCells);
            Assert.True(s.Diacritics!.Passed, string.Join("; ", s.Diacritics.Failures));
            Assert.True(s.Diacritics.MultiPartCells > 0, $"{path} {s.Step} {s.Grid}: no multi-part glyph checked");
        }
        Assert.True(czech.Single(s => s.Step == "full" && s.Grid == "80x25").Diacritics!.MultiPartCells >= 100);
        foreach (string grid in new[] { "80x25-to-140x40", "140x40-to-80x25", "20x6-to-80x25" })
            Assert.Equal("Match", czech.Single(s => s.Step == "resized" && s.Grid == grid).Outcome);
        Assert.All(r.Steps, s => Assert.True(s.Diacritics!.Passed, $"{s.Fixture} {s.Step} {s.Grid}"));
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Simulated_clipped_accents_are_detected(string path)
    {
        PathResult r = Require(path);
        foreach (string name in new[] { "clipped-accents-legacy-rule", "clipped-accents-cut-capture" })
        {
            NegativeControl n = r.NegativeControls.Single(c => c.Name == name);
            Assert.True(n.Detected, $"{path} {name}: {n.Observed}");
            Assert.Contains("anti-clipping", n.Observed);
        }
    }

    [SkippableFact]
    public void Sdl2d_and_gpu_draw_the_same_glyphs_and_shader_equals_fallback()
    {
        string Png(string path) => Path.Combine(fixture.Run(path).Dir, $"{path}--{CzechFixture.Id}-full--80x25.native.png");
        PathResult sw = Require("sdl2d-software"), shader = Require("sdl-gpu-shader"), fallback = Require("sdl-gpu-fallback");
        NativeImage a = NativeImage.DecodePng(File.ReadAllBytes(Png(sw.Path)));
        NativeImage b = NativeImage.DecodePng(File.ReadAllBytes(Png(shader.Path)));
        NativeImage c = NativeImage.DecodePng(File.ReadAllBytes(Png(fallback.Path)));
        // Same placement: only blending rounding (≤ 1 per channel) may differ between SDL 2D and the GPU.
        PixelComparison twoD = PixelComparer.CompareExact(a, b, 11, 20, ExpectedSource.RendererCapture);
        Assert.True(twoD.MaxChannelDeviation <= 1, $"SDL 2D vs GPU: max deviation {twoD.MaxChannelDeviation}");
        PixelComparison gpu = PixelComparer.CompareExact(b, c, 11, 20, ExpectedSource.RendererCapture);
        Assert.Equal(0, gpu.Differing);
    }
}
