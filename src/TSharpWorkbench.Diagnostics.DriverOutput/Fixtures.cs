using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>The standard 16-colour VGA text palette (0xRRGGBB), defined here from the VGA specification.</summary>
public static class Vga
{
    public static readonly int[] Rgb =
    [
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
    ];

    /// <summary>Palette index of an RGB value, or -1 when it is not a VGA colour.</summary>
    public static int IndexOf(int rgb) => Array.IndexOf(Rgb, rgb & 0xFFFFFF);

    /// <summary>Marks a colour the observation cannot see (e.g. the background of a fully inked cell).</summary>
    public const int Unobservable = -2;

    public static string Describe(int index) => index is >= 0 and < 16 ? index.ToString("X") : index == Unobservable ? "-" : "?";
}

/// <summary>
/// The expected logical screen: the cells the application wrote, with the framework's
/// clipping (writes outside the grid are dropped). This is the Layer A reference a Layer B
/// capture is compared against.
/// </summary>
public sealed class LogicalScreen
{
    public GridSize Grid { get; }
    public TScreenChar[] Cells { get; }

    public LogicalScreen(GridSize grid, TScreenChar fill)
    {
        Grid = grid;
        Cells = new TScreenChar[grid.Width * grid.Height];
        Array.Fill(Cells, fill);
    }

    public TScreenChar this[int x, int y] => Cells[y * Grid.Width + x];

    public void Apply(CellWrite write)
    {
        if (write.Y < 0 || write.Y >= Grid.Height) return;
        for (int i = 0; i < write.Cells.Length; i++)
        {
            int x = write.X + i;
            if (x >= 0 && x < Grid.Width) Cells[write.Y * Grid.Width + x] = write.Cells[i];
        }
    }

    public SemanticCell Semantic(int x, int y)
    {
        TScreenChar c = this[x, y];
        byte attr = c.Attr;
        return new SemanticCell(c.Character is '\0' ? " " : c.Character.ToString(), attr & 0x0F, attr >> 4);
    }
}

/// <summary>A small clipping canvas used to compose fixture screens.</summary>
public sealed class Canvas
{
    private readonly TScreenChar[] _cells;
    public GridSize Grid { get; }

    public Canvas(GridSize grid, char fill, byte attr)
    {
        Grid = grid;
        _cells = new TScreenChar[grid.Width * grid.Height];
        Array.Fill(_cells, new TScreenChar(fill, attr));
    }

    public void Put(int x, int y, string text, byte attr)
    {
        if (y < 0 || y >= Grid.Height) return;
        for (int i = 0; i < text.Length; i++)
            if (x + i >= 0 && x + i < Grid.Width)
                _cells[y * Grid.Width + x + i] = new TScreenChar(text[i], attr);
    }

    public void Fill(int x, int y, int w, int h, char ch, byte attr)
    {
        for (int row = y; row < y + h; row++) Put(x, row, new string(ch, Math.Max(0, w)), attr);
    }

    /// <summary>Darkens existing cells as Turbo Vision shadows do: same character, attribute 0x08.</summary>
    public void Shadow(int x, int y, int w, int h)
    {
        for (int row = Math.Max(0, y); row < Math.Min(Grid.Height, y + h); row++)
            for (int col = Math.Max(0, x); col < Math.Min(Grid.Width, x + w); col++)
                _cells[row * Grid.Width + col].Attr = 0x08;
    }

    public void Put(int x, int y, char ch, byte attr) => Put(x, y, ch.ToString(), attr);

    /// <summary>The whole screen as one full-width write per row: a full-screen update.</summary>
    public List<CellWrite> Rows()
    {
        var rows = new List<CellWrite>(Grid.Height);
        for (int y = 0; y < Grid.Height; y++)
            rows.Add(new CellWrite(0, y, _cells.AsSpan(y * Grid.Width, Grid.Width).ToArray()));
        return rows;
    }
}

/// <summary>
/// The representative fixtures. One composite screen covers plain text, Czech text, double and
/// single box drawing, CP437 shades and blocks, all 16 foreground and background colours,
/// mixed colours on one line, selected/highlighted items, a window shadow and near-edge text;
/// later steps exercise single-cell, rectangular and edge updates and the caret; a second
/// fixture resizes and redraws.
/// </summary>
public static class Fixtures
{
    public const string Screen = "screen";
    public const string Resize = "resize";

    public static readonly GridSize[] DefaultGrids = [new(80, 25), new(140, 40), new(20, 6)];

    public const string Czech = "Příliš žluťoučký kůň úpěl ďábelské ódy";
    public const string CzechUpper = "PŘÍLIŠ ŽLUŤOUČKÝ KŮŇ ÚPĚL ĎÁBELSKÉ ÓDY";
    public const string SingleBox = "┌─┬─┐│├┼┤└┴┘";
    public const string Shades = "░▒▓█▀▄▌▐■";

    /// <summary>The resize target for a starting grid.</summary>
    public static GridSize ResizeTarget(GridSize grid) => grid switch
    {
        { Width: 80, Height: 25 } => new GridSize(140, 40),
        { Width: 140, Height: 40 } => new GridSize(80, 25),
        _ => new GridSize(80, 25),
    };

    /// <summary>The composite screen, clipped to <paramref name="grid"/>.</summary>
    public static Canvas Composite(GridSize grid)
    {
        int w = grid.Width, h = grid.Height;
        var c = new Canvas(grid, '░', 0x71);

        // Menu bar: mixed colours on one line, one selected (highlighted) item.
        c.Fill(0, 0, w, 1, ' ', 0x70);
        c.Put(1, 0, " File ", 0x70); c.Put(2, 0, 'F', 0x74);
        c.Put(7, 0, " Edit ", 0x20); c.Put(8, 0, 'E', 0x24);
        c.Put(13, 0, " Search ", 0x70); c.Put(14, 0, 'S', 0x74);
        c.Put(w - 1, 0, '►', 0x4F);

        // Window with a double frame, title, content and a shadow.
        int wx = 2, wy = 2, ww = Math.Min(w - 6, 50), wh = Math.Min(h - 5, 14);
        if (ww >= 2 && wh >= 2)
        {
            c.Fill(wx, wy, ww, wh, ' ', 0x1E);
            c.Put(wx, wy, "╔" + new string('═', ww - 2) + "╗", 0x1F);
            for (int y = wy + 1; y < wy + wh - 1; y++) { c.Put(wx, y, '║', 0x1F); c.Put(wx + ww - 1, y, '║', 0x1F); }
            c.Put(wx, wy + wh - 1, "╚" + new string('═', ww - 2) + "╝", 0x1F);
            c.Put(wx + 2, wy, " Ünïcode ", 0x1E);
            c.Shadow(wx + ww, wy + 1, 2, wh);
            c.Shadow(wx + 2, wy + wh, ww, 1);

            string[] lines =
            [
                "Plain ASCII 0123456789 ~!@#$%^&*()_+",
                Czech,
                CzechUpper,
                SingleBox + " " + Shades,
            ];
            for (int i = 0; i < lines.Length; i++) c.Put(wx + 2, wy + 1 + i, lines[i], (byte)(i == 1 ? 0x1F : 0x1E));
            // All 16 foregrounds on black, then all 16 backgrounds under white.
            for (int i = 0; i < 16; i++)
            {
                c.Put(wx + 2 + i, wy + 5, "0123456789ABCDEF"[i], (byte)i);
                c.Put(wx + 2 + i, wy + 6, '·', (byte)((i << 4) | 0x0F));
            }
            // A list with a selected row and a highlighted (focused) row.
            c.Put(wx + 2, wy + 7, " Normal item       ", 0x30);
            c.Put(wx + 2, wy + 8, " Selected item     ", 0x2F);
            c.Put(wx + 2, wy + 9, " Focused+selected  ", 0x1F);
        }

        // Status line with mixed colours; edge cells in the last column and bottom-right corner.
        c.Fill(0, h - 1, w, 1, ' ', 0x70);
        c.Put(1, h - 1, "F1 Help  F10 Menu", 0x70);
        c.Put(1, h - 1, "F1", 0x74); c.Put(10, h - 1, "F10", 0x74);
        c.Put(w - 1, h - 1, '┘', 0x4E);
        // Near-edge text that the grid clips.
        if (h >= 3) c.Put(w - 6, h - 2, "EDGE-CLIPPED", 0x5F);
        return c;
    }

    private static TScreenChar[] Text(string s, byte attr) => s.Select(ch => new TScreenChar(ch, attr)).ToArray();

    /// <summary>The steps of a fixture at a grid.</summary>
    public static IReadOnlyList<FixtureStep> Steps(string fixture, GridSize grid)
    {
        int w = grid.Width, h = grid.Height;
        var hidden = new CaretRequest(0, 0, 0);
        switch (fixture)
        {
            case Screen:
            {
                var steps = new List<FixtureStep> { new("full", Composite(grid).Rows(), hidden) };
                steps.Add(new("single-cell", [new CellWrite(w / 2, h / 2, Text("X", 0x4F))], hidden));
                var rect = new List<CellWrite>();
                for (int y = 2; y < Math.Min(h, 6); y++)
                    rect.Add(new CellWrite(Math.Min(4, w - 1), y, Text("RECT-ŽŮ" + y + "##", 0x2E)[..Math.Min(10, w - Math.Min(4, w - 1))]));
                steps.Add(new("rect", rect, hidden));
                steps.Add(new("edge",
                [
                    new CellWrite(w - 1, 0, Text("▲", 0x4F)),
                    new CellWrite(w - 1, h - 1, Text("▼", 0x4F)),
                    new CellWrite(Math.Max(0, w - 8), h / 2, Text("EDGE>>>>", 0x5F)[..Math.Min(8, w)]),
                ], hidden));
                steps.Add(new("cursor-block", [], new CaretRequest(Math.Min(3, w - 1), Math.Max(0, h - 2), 100)));
                steps.Add(new("cursor-underline", [], new CaretRequest(Math.Min(4, w - 1), Math.Min(1, h - 1), 25)));
                steps.Add(new("cursor-hidden", [], hidden));
                return steps;
            }
            case Resize:
            {
                GridSize target = ResizeTarget(grid);
                return
                [
                    new("before", Composite(grid).Rows(), hidden),
                    new("after", Composite(target).Rows(), hidden, target),
                ];
            }
            case not null when External.ContainsKey(fixture):
                return ExternalSteps(fixture, grid);
            default:
                throw new ArgumentException($"Unknown fixture '{fixture}'.", nameof(fixture));
        }
    }

    public static readonly string[] All = [Screen, Resize];

    private static readonly Dictionary<string, (string File, ScreenSnapshot Screen)> External = new();

    /// <summary>
    /// Adds a fixture from a TVS1 logical screen (for example an approved logical baseline, read only):
    /// one full-screen write of its cells. Returns the fixture id.
    /// </summary>
    public static string AddTvs1(string file)
    {
        ScreenSnapshot screen = ScreenSnapshot.Deserialize(File.ReadAllBytes(file));
        string id = "tvs1-" + Path.GetFileNameWithoutExtension(file).Replace("--", "_");
        External[id] = (Path.GetFullPath(file), screen);
        return id;
    }

    public static IEnumerable<(string Id, string File, GridSize Grid)> ExternalFixtures =>
        External.Select(e => (e.Key, e.Value.File, new GridSize(e.Value.Screen.Width, e.Value.Screen.Height)));

    private static IReadOnlyList<FixtureStep> ExternalSteps(string id, GridSize grid)
    {
        ScreenSnapshot s = External[id].Screen;
        if (s.Width != grid.Width || s.Height != grid.Height)
            throw new ArgumentException($"Fixture '{id}' is {s.Width}x{s.Height}, not {grid}.");
        var rows = new List<CellWrite>(s.Height);
        for (int y = 0; y < s.Height; y++)
            rows.Add(new CellWrite(0, y, Enumerable.Range(0, s.Width).Select(x => s[x, y]).ToArray()));
        return [new FixtureStep("full", rows, new CaretRequest(0, 0, 0))];
    }
}
