using TSharpWorkbench.Diagnostics.DriverOutput;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativePixels.Tests;

/// <summary>
/// The comparison machinery on synthetic images (no SDL): exactness rules, the narrow partial
/// tolerance, unverified accounting, dimension and corruption handling, PNG fidelity, geometry
/// and update checks.
/// </summary>
public sealed class ComparisonTests
{
    private const int Cw = 4, Ch = 6;
    private const int Blue = 0x0000AA, White = 0xFFFFFF, Yellow = 0xFFFF55;

    private static (NativeImage Image, PixelClass[] Classes) Solid(int w, int h, int rgb, PixelClass cls = PixelClass.Background)
    {
        var img = new NativeImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) img.Set(x, y, rgb);
        var classes = new PixelClass[w * h];
        Array.Fill(classes, cls);
        return (img, classes);
    }

    private static ExpectedRaster Raster(NativeImage img, PixelClass[] classes) =>
        new() { Image = img, Classes = classes, CellWidth = Cw, CellHeight = Ch, Grid = new GridSize(img.Width / Cw, img.Height / Ch) };

    [Fact]
    public void Identical_images_match_exactly()
    {
        var (img, classes) = Solid(8, 12, Blue);
        PixelComparison c = PixelComparer.Compare(Raster(img, classes), img.Clone(), 1);
        Assert.True(c.Passed);
        Assert.Equal(96, c.ExactMatches);
        Assert.Equal(0, c.Differing);
    }

    [Fact]
    public void One_channel_step_on_a_background_pixel_is_a_mismatch()
    {
        var (img, classes) = Solid(8, 12, Blue);
        NativeImage actual = img.Clone();
        actual.Set(5, 7, Blue + 1);
        PixelComparison c = PixelComparer.Compare(Raster(img, classes), actual, 1);
        Assert.Equal("Mismatch", c.Outcome);
        Assert.Equal(1, c.Differing);
        Assert.Equal(1, c.DifferingCellCount);
        Assert.Equal(new CellDifference(1, 1, 1, 1, ""), c.DifferingCells.Single());
        Assert.Equal(new PixelBox(4, 6, 4, 6, 1), c.DifferenceBoxes.Single());
    }

    [Fact]
    public void Partial_tolerance_applies_only_to_partial_coverage_pixels()
    {
        var (img, classes) = Solid(4, 6, 0x808080, PixelClass.Partial);
        classes[0] = PixelClass.Foreground;
        NativeImage within = img.Clone();
        for (int x = 1; x < 4; x++) within.Set(x, 0, 0x818080);           // partial, deviation 1
        PixelComparison ok = PixelComparer.Compare(Raster(img, classes), within, 1);
        Assert.True(ok.Passed);
        Assert.Equal(3, ok.WithinPartialTolerance);

        NativeImage beyond = within.Clone();
        beyond.Set(1, 1, 0x828080);                                          // partial, deviation 2
        Assert.False(PixelComparer.Compare(Raster(img, classes), beyond, 1).Passed);

        NativeImage fgOff = img.Clone();
        fgOff.Set(0, 0, 0x818080);                                           // foreground, deviation 1
        PixelComparison fg = PixelComparer.Compare(Raster(img, classes), fgOff, 1);
        Assert.False(fg.Passed);
        Assert.Equal(1, fg.DifferingByClass["Foreground"]);
    }

    [Fact]
    public void Unverified_pixels_are_counted_never_compared_as_passed()
    {
        var (img, classes) = Solid(4, 6, Blue, PixelClass.Unverified);
        NativeImage actual = img.Clone();
        actual.Set(0, 0, White);
        PixelComparison c = PixelComparer.Compare(Raster(img, classes), actual, 1);
        Assert.Equal(24, c.UnverifiedPixels);
        Assert.Equal(0, c.ExactMatches);
        Assert.Equal(0, c.Differing);
    }

    [Fact]
    public void Alpha_and_dimension_differences_fail()
    {
        var (img, classes) = Solid(8, 12, Blue);
        NativeImage alpha = img.Clone();
        alpha.Set(0, 0, Blue, alpha: 254);
        PixelComparison a = PixelComparer.Compare(Raster(img, classes), alpha, 1);
        Assert.False(a.Passed);
        Assert.Equal(1, a.AlphaDifferences);

        PixelComparison d = PixelComparer.Compare(Raster(img, classes), new NativeImage(12, 12), 1);
        Assert.Equal("DimensionMismatch", d.Outcome);
    }

    [Fact]
    public void Png_round_trip_is_lossless_and_damage_is_detected()
    {
        var img = new NativeImage(13, 7);
        var rnd = new Random(5);
        rnd.NextBytes(img.Rgba);
        byte[] png = img.EncodePng();
        Assert.Equal(img.Rgba, NativeImage.DecodePng(png).Rgba);

        byte[] damaged = (byte[])png.Clone();
        damaged[damaged.Length / 2] ^= 0x40;
        Assert.Throws<CorruptCaptureException>(() => NativeImage.DecodePng(damaged));
        Assert.Throws<CorruptCaptureException>(() => NativeImage.DecodePng(png[..^20]));
        Assert.Throws<CorruptCaptureException>(() => new NativeImage(13, 7, new byte[13 * 7 * 4 - 1]));
    }

    // ── Geometry checks on hand-drawn cells ─────────────────────────────────

    private static LogicalScreen Screen(string text, byte attr)
    {
        var s = new LogicalScreen(new GridSize(text.Length, 1), new TScreenChar(' ', attr));
        s.Apply(new CellWrite(0, 0, text.Select(c => new TScreenChar(c, attr)).ToArray()));
        return s;
    }

    /// <summary>Draws '═' as two full-width rows in every cell (rows 2 and 4).</summary>
    private static NativeImage DoubleLines(int cells)
    {
        var (img, _) = Solid(cells * Cw, Ch, Blue);
        for (int x = 0; x < img.Width; x++) { img.Set(x, 2, White); img.Set(x, 4, White); }
        return img;
    }

    [Fact]
    public void Joined_double_lines_pass_and_a_shifted_line_breaks_the_join()
    {
        LogicalScreen screen = Screen("══", 0x1F);
        GeometryResult ok = GeometryChecks.Run(screen, default, DoubleLines(2), Cw, Ch);
        Assert.True(ok.Passed, string.Join("; ", ok.Failures));
        Assert.Equal(1, ok.Checked["box-join-horizontal"]);

        NativeImage shifted = DoubleLines(2);
        for (int x = Cw; x < 2 * Cw; x++) { shifted.Set(x, 4, Blue); shifted.Set(x, 5, White); }
        GeometryResult broken = GeometryChecks.Run(screen, default, shifted, Cw, Ch);
        Assert.Contains("box-join-horizontal", broken.Failed.Keys);
    }

    [Fact]
    public void A_missing_arm_and_a_missing_glyph_are_reported()
    {
        NativeImage img = DoubleLines(1);
        img.Set(Cw - 1, 2, Blue); img.Set(Cw - 1, 4, Blue);                // right edge erased
        Assert.Contains("box-arms", GeometryChecks.Run(Screen("═", 0x1F), default, img, Cw, Ch).Failed.Keys);

        var (blank, _) = Solid(Cw, Ch, Blue);
        Assert.Contains("glyph-has-ink", GeometryChecks.Run(Screen("A", 0x1F), default, blank, Cw, Ch).Failed.Keys);
    }

    [Fact]
    public void Shadow_cells_must_use_shadow_colours()
    {
        var (img, _) = Solid(Cw, Ch, 0x000000);
        img.Set(1, 1, 0x555555);
        Assert.True(GeometryChecks.Run(Screen("░", 0x08), default, img, Cw, Ch).Checked.ContainsKey("shadow-cell-colours"));
        img.Set(2, 2, Yellow);
        Assert.Contains("shadow-cell-colours", GeometryChecks.Run(Screen("░", 0x08), default, img, Cw, Ch).Failed.Keys);
    }

    [Fact]
    public void Underline_caret_needs_two_foreground_rows()
    {
        var (img, _) = Solid(Cw, Ch, Blue);
        for (int x = 0; x < Cw; x++) { img.Set(x, Ch - 1, White); img.Set(x, Ch - 2, White); }
        var caret = new CaretRequest(0, 0, 25);
        Assert.True(GeometryChecks.Run(Screen(" ", 0x1F), caret, img, Cw, Ch).Passed);
        img.Set(0, Ch - 2, Blue);
        Assert.Contains("caret-underline-bar", GeometryChecks.Run(Screen(" ", 0x1F), caret, img, Cw, Ch).Failed.Keys);
    }

    [Fact]
    public void Update_check_detects_stale_and_collateral_cells()
    {
        var (before, cls) = Solid(3 * Cw, Ch, Blue);
        NativeImage expectedAfter = before.Clone();
        for (int y = 0; y < Ch; y++) for (int x = Cw; x < 2 * Cw; x++) expectedAfter.Set(x, y, Yellow);  // middle cell changes
        ExpectedRaster rb = Raster(before, cls), ra = Raster(expectedAfter, cls);

        Assert.True(ChildRunner.CheckUpdate(before, expectedAfter, rb, ra, Cw, Ch).Passed);

        UpdateCheck stale = ChildRunner.CheckUpdate(before, before.Clone(), rb, ra, Cw, Ch);
        Assert.Equal(1, stale.MissingChangedCells);

        NativeImage collateral = expectedAfter.Clone();
        collateral.Set(0, 0, White);
        Assert.Equal(1, ChildRunner.CheckUpdate(before, collateral, rb, ra, Cw, Ch).UnexpectedChangedCells);
    }

    [Fact]
    public void Two_captures_compare_exactly_and_are_labelled_as_renderer_captures()
    {
        var (a, _) = Solid(8, 12, Blue);
        NativeImage b = a.Clone();
        b.Set(7, 11, Blue ^ 1);
        PixelComparison c = PixelComparer.CompareExact(a, b, Cw, Ch, ExpectedSource.RendererCapture);
        Assert.Equal(ExpectedSource.RendererCapture, c.ExpectedSource);
        Assert.Equal(1, c.Differing);
        Assert.Equal(1, c.DifferingCellsWithinOne);
    }

    [Fact]
    public void Oracle_blend_rounds_to_nearest_and_is_exact_at_the_ends()
    {
        Assert.Equal(0xAA, RasterOracle.Blend(0xAA, 0x00, 255));
        Assert.Equal(0x00, RasterOracle.Blend(0xAA, 0x00, 0));
        Assert.Equal(85, RasterOracle.Blend(0xAA, 0x00, 128)); // 170*128/255 = 85.33
        Assert.Equal(128, RasterOracle.Blend(255, 0, 128));    // 128.0
    }

    // ── anti-clipping check on synthetic masks ─────────────────────────

    /// <summary>A 9×18 "Ž": a 3-row caron, a blank row, a 13-row body.</summary>
    private static byte[] ZCaron(int caronRows = 3, int w = 9, int h = 18)
    {
        var a = new byte[w * h];
        for (int y = 3 - caronRows; y < 3; y++) for (int x = 3; x < 6; x++) a[y * w + x] = 255;
        for (int y = 4; y < 17; y++) for (int x = 1; x < 8; x++) if (y is 4 or 16 || x == 8 - (y - 3) * 7 / 13) a[y * w + x] = 255;
        return a;
    }

    [Fact]
    public void Anti_clipping_accepts_the_same_parts_and_a_uniformly_smaller_copy()
    {
        var natural = DiacriticChecks.Parts(ZCaron(), 9, 18);
        Assert.Equal(2, natural.Count);
        Assert.Null(DiacriticChecks.Compare(natural, DiacriticChecks.Parts(ZCaron(), 9, 18)));
        // Shares survive a slightly smaller caron (2 of 3 rows, as after a small condensation).
        Assert.Null(DiacriticChecks.Compare(natural, DiacriticChecks.Parts(ZCaron(caronRows: 2), 9, 18)));
    }

    [Fact]
    public void Anti_clipping_rejects_an_accent_cut_to_one_row_or_removed()
    {
        var natural = DiacriticChecks.Parts(ZCaron(), 9, 18);
        string? dot = DiacriticChecks.Compare(natural, DiacriticChecks.Parts(ZCaron(caronRows: 1), 9, 18));
        Assert.NotNull(dot);
        Assert.Contains("cut off", dot);
        string? gone = DiacriticChecks.Compare(natural, DiacriticChecks.Parts(ZCaron(caronRows: 0), 9, 18));
        Assert.Contains("ink part(s)", gone);
    }

    [Fact]
    public void Captured_coverage_is_recovered_from_pixel_colours()
    {
        var img = new NativeImage(2, 1);
        img.Set(0, 0, Blue);
        img.Set(1, 0, White);
        byte[] a = DiacriticChecks.CapturedCoverage(img, 0, 0, 2, 1, White, Blue);
        Assert.Equal(new byte[] { 0, 255 }, a);
    }
}
