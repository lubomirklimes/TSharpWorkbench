using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>
/// Glyph fixture: Czech upper and lower case (the accents the SDL GPU paths used to clip),
/// next to ASCII, digits, punctuation, box drawing, CP437 blocks, hotkey-highlighted menu text,
/// selected and focused list rows and a status line; then a block and an underline caret over
/// accented letters, and a resize with a full redraw. Every grid is clipped by the canvas, so the
/// 20x6 case keeps the first lines only.
/// </summary>
public static class CzechFixture
{
    public const string Id = "czech";

    public const string Pangram = "Příliš žluťoučký kůň úpěl ďábelské ódy";
    public const string PangramUpper = "PŘÍLIŠ ŽLUŤOUČKÝ KŮŇ ÚPĚL ĎÁBELSKÉ ÓDY";
    public const string Short = "Žluťoučký kůň";
    public const string Capitals = "Ř Í Š Ž Ť Č Ď Ů Á É Ó Ú Ý Ě Ň Ü";
    public const string Lower = "ť ď ř í š ž č ů á é ó ú ý ě ň ü";

    /// <summary>Caret positions: a block over the Ž and an underline over the ů of <see cref="Short"/> (row 1).</summary>
    public static readonly (int X, int Y) BlockCaret = (1, 1);
    public static readonly (int X, int Y) UnderlineCaret = (1 + Short.IndexOf('ů'), 1);

    public static Canvas Screen(GridSize grid)
    {
        int w = grid.Width, h = grid.Height;
        var c = new Canvas(grid, ' ', 0x17);

        // Menu bar with hotkeys on accented letters; one selected item.
        c.Fill(0, 0, w, 1, ' ', 0x70);
        c.Put(1, 0, " Soubor ", 0x70); c.Put(2, 0, 'S', 0x74);
        c.Put(9, 0, " Úpravy ", 0x70); c.Put(10, 0, 'Ú', 0x74);
        c.Put(17, 0, " Žádosti ", 0x70); c.Put(18, 0, 'Ž', 0x74);
        c.Put(27, 0, " Řazení ", 0x20); c.Put(28, 0, 'Ř', 0x24);

        c.Put(1, 1, Short, 0x1E);
        c.Put(1, 2, PangramUpper, 0x1F);
        c.Put(1, 3, Pangram, 0x1E);
        c.Put(1, 4, Capitals, 0x1F);
        c.Put(1, 5, Lower, 0x1E);
        c.Put(1, 6, "The quick brown fox jumps over the lazy dog 0123456789", 0x07);
        c.Put(1, 7, "MixEd CaSe ŽlUťOuČkÝ kŮň: gjpqy_ |$@Q (){}[] ~!#%^&*-+=;'\",.<>/?\\", 0x0F);

        // Single frame with Czech content and a divider; double frame beside it; shadow.
        const int bx = 1, by = 9, bw = 32, bh = 5;
        c.Put(bx, by, "┌" + new string('─', bw - 2) + "┐", 0x1B);
        c.Put(bx, by + 1, "│" + " Žluťoučký kůň ┼ ŘÍŠŽ ť ď".PadRight(bw - 2) + "│", 0x1B);
        c.Put(bx, by + 2, "├" + new string('─', bw - 2) + "┤", 0x1B);
        c.Put(bx, by + 3, "│" + " ÚPĚL ĎÁBELSKÉ ÓDY".PadRight(bw - 2) + "│", 0x1B);
        c.Put(bx, by + 4, "└" + new string('─', bw - 2) + "┘", 0x1B);
        c.Shadow(bx + bw, by + 1, 2, bh);
        c.Shadow(bx + 2, by + bh, bw, 1);
        const int dx = 37, dw = 30;
        c.Put(dx, by, "╔" + new string('═', dw - 2) + "╗", 0x1F);
        c.Put(dx, by + 1, "║" + " Ďábelské ódy úpěl kůň".PadRight(dw - 2) + "║", 0x1F);
        c.Put(dx, by + 2, "╟" + new string('─', dw - 2) + "╢", 0x1F);
        c.Put(dx, by + 3, "║" + " ŤŽŮŇ ťďůň ░▒▓█▀▄▌▐■".PadRight(dw - 2) + "║", 0x1F);
        c.Put(dx, by + 4, "╚" + new string('═', dw - 2) + "╝", 0x1F);

        // Blocks and shades on their own line.
        c.Put(1, 15, "░▒▓█▀▄▌▐■ ░░▒▒▓▓██ ▀▀▄▄", 0x1E);

        // List rows: normal, selected, focused+selected.
        c.Put(1, 17, " Normální položka Žluťoučký  ", 0x30);
        c.Put(1, 18, " Vybraná položka ŘÍŠŽŤČĎŮ    ", 0x2F);
        c.Put(1, 19, " Zaměřená+vybraná ďábelské   ", 0x1F);

        // Status line with hotkeys.
        c.Fill(0, h - 1, w, 1, ' ', 0x70);
        c.Put(1, h - 1, "F1 Nápověda  F10 Nabídka  Alt+X Konec", 0x70);
        c.Put(1, h - 1, "F1", 0x74); c.Put(13, h - 1, "F10", 0x74); c.Put(27, h - 1, "Alt+X", 0x74);
        return c;
    }

    public static IReadOnlyList<FixtureStep> Steps(GridSize grid)
    {
        var hidden = new CaretRequest(0, 0, 0);
        GridSize target = Fixtures.ResizeTarget(grid);
        return
        [
            new("full", Screen(grid).Rows(), hidden),
            new("cursor-block", [], new CaretRequest(BlockCaret.X, BlockCaret.Y, 100)),
            new("cursor-underline", [], new CaretRequest(UnderlineCaret.X, UnderlineCaret.Y, 25)),
            new("resized", Screen(target).Rows(), hidden, target),
        ];
    }
}
