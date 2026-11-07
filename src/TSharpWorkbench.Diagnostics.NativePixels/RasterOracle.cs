using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>What the oracle claims about one expected pixel, which decides how it is compared.</summary>
public enum PixelClass : byte
{
    /// <summary>Background colour exactly (no glyph coverage).</summary>
    Background,
    /// <summary>Foreground colour exactly (full coverage, underline caret bar).</summary>
    Foreground,
    /// <summary>Partial glyph coverage: a blend of foreground over background.</summary>
    Partial,
    /// <summary>Outside the grid: the renderer's black clear (letterbox).</summary>
    Letterbox,
    /// <summary>The oracle cannot state this pixel exactly (counted and reported, never passed silently).</summary>
    Unverified,
}

/// <summary>An expected frame plus the class of every pixel.</summary>
public sealed class ExpectedRaster
{
    public required NativeImage Image { get; init; }
    public required PixelClass[] Classes { get; init; }
    public required int CellWidth { get; init; }
    public required int CellHeight { get; init; }
    public required GridSize Grid { get; init; }
    /// <summary>Cells whose glyph shape is unverified, with the reason (first few).</summary>
    public List<string> UnverifiedCells { get; } = new();
    public int UnverifiedCellCount { get; set; }
    /// <summary>Characters the fonts do not contain.</summary>
    public SortedSet<string> MissingFromFont { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Independent raster oracle: the expected frame computed from the logical cells, the VGA palette
/// (<see cref="Vga"/>, defined from the VGA specification, not the driver's table), the caret model
/// and <see cref="GlyphMaskOracle"/> coverage. It shares no rendering code with the drivers.
/// </summary>
public static class RasterOracle
{
    /// <summary>Source-over blend of 8-bit coverage, rounded to nearest.</summary>
    public static byte Blend(int fg, int bg, int alpha) => (byte)((fg * alpha + bg * (255 - alpha) + 127) / 255);

    public static ExpectedRaster Build(LogicalScreen screen, CaretRequest caret, GlyphMaskOracle masks,
        FontGlyphPlacement placement, int targetWidth, int targetHeight)
    {
        int cw = masks.CellWidth, ch = masks.CellHeight;
        var img = new NativeImage(targetWidth, targetHeight);
        var classes = new PixelClass[targetWidth * targetHeight];
        Array.Fill(classes, PixelClass.Letterbox);
        for (int i = 3; i < img.Rgba.Length; i += 4) img.Rgba[i] = 255; // letterbox: opaque black
        var raster = new ExpectedRaster { Image = img, Classes = classes, CellWidth = cw, CellHeight = ch, Grid = screen.Grid };

        for (int row = 0; row < screen.Grid.Height; row++)
        {
            for (int col = 0; col < screen.Grid.Width; col++)
            {
                int x0 = col * cw, y0 = row * ch;
                if (x0 >= targetWidth || y0 >= targetHeight) continue;
                TScreenChar cell = screen[col, row];
                byte attr = cell.Attr;
                int fg = Vga.Rgb[attr & 0x0F], bg = Vga.Rgb[(attr >> 4) & 0x0F];
                bool caretCell = caret.CursorType != 0 && caret.X == col && caret.Y == row;
                bool underline = caretCell && caret.CursorType < 100;
                if (caretCell && !underline) (fg, bg) = (bg, fg);

                GlyphMask mask = masks.Mask(cell.Character, col, row, placement);
                if (cell.Character is not (' ' or '\0') && masks.FontCoverage(cell.Character) == "missing" && mask.Source != "generated")
                    raster.MissingFromFont.Add($"U+{(int)cell.Character:X4}");
                if (mask.Unverified)
                {
                    raster.UnverifiedCellCount++;
                    if (raster.UnverifiedCells.Count < 20) raster.UnverifiedCells.Add($"({col},{row}) '{cell.Character}': {mask.Reason}");
                }

                for (int dy = 0; dy < ch && y0 + dy < targetHeight; dy++)
                {
                    for (int dx = 0; dx < cw && x0 + dx < targetWidth; dx++)
                    {
                        int x = x0 + dx, y = y0 + dy, p = y * targetWidth + x;
                        int a = mask.Alpha?[dy * cw + dx] ?? 0;
                        bool bar = underline && dy >= ch - 2;
                        if (mask.Unverified && !bar)
                        {
                            img.Set(x, y, bg);
                            classes[p] = PixelClass.Unverified;
                        }
                        else if (bar || a == 255)
                        {
                            img.Set(x, y, fg);
                            classes[p] = PixelClass.Foreground;
                        }
                        else if (a == 0)
                        {
                            img.Set(x, y, bg);
                            classes[p] = PixelClass.Background;
                        }
                        else
                        {
                            int rgb = (Blend(fg >> 16, bg >> 16, a) << 16) | (Blend((fg >> 8) & 0xFF, (bg >> 8) & 0xFF, a) << 8) | Blend(fg & 0xFF, bg & 0xFF, a);
                            img.Set(x, y, rgb);
                            classes[p] = PixelClass.Partial;
                        }
                    }
                }
            }
        }
        return raster;
    }
}
