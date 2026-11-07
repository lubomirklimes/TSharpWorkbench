using TSharpWorkbench.Diagnostics.NativeHosts;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativeHosts.Tests;

/// <summary>
/// The Layer C analysis on synthetic host images: a tiny renderer draws cells the way a host would
/// (background fill, stroke glyphs, box lines to the cell edges), at an offset origin with a fractional
/// pitch, so each check is shown to pass on a correct image and to fail on the defect it guards.
/// </summary>
public sealed class HostAnalysisTests
{
    private static readonly int[] Palette =
    [
        0x0C0C0C, 0x0037DA, 0x13A10E, 0x3A96DD, 0xC50F1F, 0x881798, 0xC19C00, 0xCCCCCC,
        0x767676, 0x3B78FF, 0x16C60C, 0x61D6D6, 0xE74856, 0xB4009E, 0xF9F1A5, 0xF2F2F2,
    ];

    private const double X0 = 7, Y0 = 41, CellW = 9.5, CellH = 19;

    private static HostScreen Screen(int w, int h, Func<int, int, (char, byte)> cell, int cx = 0, int cy = 0, bool cursor = false)
    {
        var chars = new char[w * h];
        var attrs = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) (chars[y * w + x], attrs[y * w + x]) = cell(x, y);
        return new HostScreen { Source = "synthetic", Width = w, Height = h, Chars = new string(chars), Attrs = attrs, CursorX = cx, CursorY = cy, CursorVisible = cursor, CursorSize = 25 };
    }

    private static Geometry Grid(HostScreen s) => new(X0, Y0, CellW, CellH, s.Width, s.Height);

    /// <summary>Draws a host-like image of <paramref name="s"/> (window chrome above and around the grid).</summary>
    private static NativeImage Render(HostScreen s, Action<NativeImage, Geometry>? after = null, (int X, int Y, string Shape)? caret = null)
    {
        var g = Grid(s);
        var img = new NativeImage(g.Right + 20, g.Bottom + 15);
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++) img.Set(x, y, 0x202020);
        for (int r = 0; r < s.Height; r++)
            for (int c = 0; c < s.Width; c++)
            {
                var (x0, y0, x1, y1) = g.Cell(c, r);
                int w = x1 - x0, h = y1 - y0;
                byte a = s.Attr(c, r);
                int fg = Palette[a & 15], bg = Palette[a >> 4];
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) img.Set(x, y, bg);
                char ch = s.Char(c, r);
                void V(int from, int to) { for (int y = y0 + from; y < y0 + to; y++) img.Set(x0 + w / 2, y, fg); }
                void H(int from, int to) { for (int x = x0 + from; x < x0 + to; x++) img.Set(x, y0 + h / 2, fg); }
                switch (ch)
                {
                    case ' ': break;
                    case '█': for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) img.Set(x, y, fg); break;
                    case '─': H(0, w); break;
                    case '│': V(0, h); break;
                    case '┌': H(w / 2, w); V(h / 2, h); break;
                    case '┐': H(0, w / 2 + 1); V(h / 2, h); break;
                    case '└': H(w / 2, w); V(0, h / 2 + 1); break;
                    case '┘': H(0, w / 2 + 1); V(0, h / 2 + 1); break;
                    case 'c': V(8, h - 3); break;
                    case 'B': V(4, h - 3); H(1, w - 1); break;
                    case 'č': V(8, h - 3); img.Set(x0 + w / 2, y0 + 3, fg); img.Set(x0 + w / 2, y0 + 4, fg); break;
                    default: V(4, h - 3); img.Set(x0 + w / 2 - 1, y0 + 4, fg); break;
                }
            }
        if (caret is { } k)
        {
            var (x0, y0, x1, y1) = g.Cell(k.X, k.Y);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (k.Shape == "bar" ? x == x0 : y >= y1 - 3) img.Set(x, y, 0xFFFFFF);
        }
        after?.Invoke(img, g);
        return img;
    }

    private static HostScreen Calibration(int w, int h) => Screen(w, h, (x, y) =>
        (x == 0 && y == 0) || (x == w - 1 && y == h - 1) ? (' ', (byte)0xD0)
        : y == 1 && x >= 2 && x < 18 ? (' ', (byte)((x - 2) << 4))
        : y == 2 && x >= 2 && x < 18 ? ('█', (byte)(x - 2)) : (' ', (byte)0x00));

    [Fact]
    public void Calibration_locates_an_offset_fractional_grid_and_measures_the_palette()
    {
        var s = Calibration(30, 8);
        var cal = HostAnalysis.Calibrate(Render(s), 30, 8);
        Assert.Null(cal.Unavailable);
        Assert.Empty(cal.Failures);
        Assert.Equal(X0, cal.Geometry!.X0);
        Assert.Equal(Y0, cal.Geometry.Y0);
        Assert.Equal(CellW, cal.Geometry.CellW, 1); // within half a pixel over the grid
        Assert.Equal(CellH, cal.Geometry.CellH, 2);
        Assert.Equal(Palette, cal.BackgroundRgb);
        Assert.Equal(30, cal.ColsFromPixels);
    }

    [Fact]
    public void Calibration_reports_a_grid_that_differs_from_the_host_grid()
    {
        var s = Calibration(30, 8);
        var cal = HostAnalysis.Calibrate(Render(s), 24, 8); // host claims fewer columns than the pixels show
        Assert.Contains(cal.Failures, f => f.Contains("equals host grid"));
    }

    [Fact]
    public void Calibration_of_a_blank_capture_is_unavailable()
    {
        var img = new NativeImage(200, 100);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 200; x++) img.Set(x, y, 0x0C0C0C);
        Assert.Equal("capture is uniform (blank surface)", HostAnalysis.Calibrate(img, 20, 5).Unavailable);
    }

    private static HostScreen Window() => Screen(12, 5, (x, y) => (x, y) switch
    {
        (1, 1) => ('┌', 0x1F), (10, 1) => ('┐', 0x1F), (1, 3) => ('└', 0x1F), (10, 3) => ('┘', 0x1F),
        (_, 1 or 3) when x > 1 && x < 10 => ('─', 0x1F),
        (1 or 10, 2) => ('│', 0x1F),
        (3, 2) => ('c', 0x1E), (4, 2) => ('č', 0x1E), (6, 2) => ('A', 0x1E), (8, 2) => ('█', 0x1E),
        _ when x > 1 && x < 10 && y == 2 => (' ', 0x1E),
        _ => (' ', 0x70),
    });

    [Fact]
    public void A_correct_render_passes_every_cell_rule_including_continuity_and_diacritics()
    {
        var s = Window();
        var check = HostAnalysis.CheckCells(Render(s), Grid(s), Palette, s, new HashSet<(int, int)>());
        Assert.True(check.Passed, string.Join("\n", check.Samples));
        Assert.True(check.Rules["box-continuity"].Checked >= 10);
        Assert.Equal(1, check.Diacritics.Pairs);
        Assert.Equal(0, check.Diacritics.Failed);
    }

    [Fact]
    public void A_wrong_background_and_a_missing_glyph_are_detected()
    {
        var s = Window();
        var img = Render(s, (img, g) =>
        {
            var (x0, y0, x1, y1) = g.Cell(6, 2); // erase 'A'
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) img.Set(x, y, Palette[1]);
            var c = g.Cell(0, 0);                // repaint a desktop cell red
            for (int y = c.Y0; y < c.Y1; y++) for (int x = c.X0; x < c.X1; x++) img.Set(x, y, Palette[4]);
        });
        var check = HostAnalysis.CheckCells(img, Grid(s), Palette, s, new HashSet<(int, int)>());
        Assert.Equal(1, check.Rules["ink"].Failed);
        Assert.Equal(1, check.Rules["background"].Failed);
    }

    [Fact]
    public void A_broken_box_line_is_detected_at_the_cell_boundary()
    {
        var s = Window();
        var img = Render(s, (img, g) =>
        {
            var (x0, y0, x1, y1) = g.Cell(5, 1); // cut the top line at the right edge of one cell
            for (int y = y0; y < y1; y++) { img.Set(x1 - 1, y, Palette[1]); img.Set(x1 - 2, y, Palette[1]); }
        });
        var check = HostAnalysis.CheckCells(img, Grid(s), Palette, s, new HashSet<(int, int)>());
        Assert.True(check.Rules["box-edges"].Failed + check.Rules["box-continuity"].Failed >= 1);
    }

    [Fact]
    public void A_missing_diacritic_is_detected()
    {
        var s = Window();
        var img = Render(s, (img, g) =>
        {
            var (x0, y0, x1, y1) = g.Cell(4, 2); // remove the caron of 'č'
            for (int y = y0; y < y0 + 6; y++) for (int x = x0; x < x1; x++) img.Set(x, y, Palette[1]);
        });
        var check = HostAnalysis.CheckCells(img, Grid(s), Palette, s, new HashSet<(int, int)>());
        Assert.Equal(1, check.Diacritics.Failed);
    }

    [Fact]
    public void Update_comparison_finds_stale_cells_and_separates_neighbour_overhang()
    {
        var a = Window();
        var b = Screen(a.Width, a.Height, (x, y) => (x, y) == (6, 2) ? ('B', (byte)0x1E) : (x, y) == (3, 2) ? (' ', (byte)0x1E) : (a.Char(x, y), a.Attr(x, y)));
        var ia = Render(a);
        var stale = HostAnalysis.CompareUpdate(ia, Render(a), Grid(a), Palette, a, b, new HashSet<(int, int)>());
        Assert.Equal(2, stale.Stale);                      // 'c' erased and 'A'→'B' in the buffer, pixels unchanged
        var ib = Render(b, (img, g) =>
        {
            var (x0, y0, x1, y1) = g.Cell(7, 2);            // ink spilling into the unchanged right neighbour of (6,2)
            for (int y = y0 + 3; y < y1 - 3; y++) for (int x = x0 + 1; x < x0 + 4; x++) img.Set(x, y, Palette[14]);
        });
        var u = HostAnalysis.CompareUpdate(ia, ib, Grid(a), Palette, a, b, new HashSet<(int, int)>());
        Assert.Equal(0, u.Stale);
        Assert.Equal(1, u.NeighbourOverhang);
        Assert.Equal(0, u.Spurious);
        var far = Render(b, (img, g) => { var c = g.Cell(0, 4); for (int y = c.Y0; y < c.Y1; y++) for (int x = c.X0; x < c.X1; x++) img.Set(x, y, Palette[4]); });
        Assert.Equal(1, HostAnalysis.CompareUpdate(ia, far, Grid(a), Palette, a, b, new HashSet<(int, int)>()).Spurious);
    }

    [Fact]
    public void A_blinking_bar_caret_in_the_expected_cell_passes_and_elsewhere_fails()
    {
        var s = Screen(12, 5, (x, y) => (' ', 0x1E), cx: 4, cy: 2, cursor: true);
        var on = Render(s, caret: (4, 2, "bar"));
        var off = Render(s);
        var ok = HostAnalysis.CheckCursor([on, off, on, off], Grid(s), Palette, s);
        Assert.Equal("Passed", ok.Outcome);
        Assert.Contains("bar", ok.Detail);
        var wrong = HostAnalysis.CheckCursor([Render(s, caret: (6, 2, "bar")), off], Grid(s), Palette, s);
        Assert.Equal("Failed", wrong.Outcome);
    }

    [Fact]
    public void A_steady_underline_caret_is_found_against_a_caret_less_reference()
    {
        var s = Screen(12, 5, (x, y) => (x, y) == (4, 2) ? ('A', (byte)0x1E) : (' ', (byte)0x1E), cx: 4, cy: 2, cursor: true);
        var reference = Render(s);
        var steady = Render(s, caret: (4, 2, "underline"));
        var hiddenRef = Screen(12, 5, (x, y) => (s.Char(x, y), s.Attr(x, y)));
        var cc = HostAnalysis.CheckCursor([steady, steady, steady], Grid(s), Palette, s, reference, hiddenRef);
        Assert.Equal("Passed", cc.Outcome);
        Assert.Contains("underline", cc.Detail);
        var none = HostAnalysis.CheckCursor([reference, reference], Grid(s), Palette, s, reference, hiddenRef);
        Assert.Equal("Failed", none.Outcome);
    }
}
