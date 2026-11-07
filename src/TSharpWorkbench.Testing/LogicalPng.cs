using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TSharpWorkbench.Testing;

public static class LogicalPng
{
    public const int CellWidth = 8, CellHeight = 12;
    // v2: adds the standard menu/status/control symbols (see Symbol). Hashes of PNGs containing those
    // characters differ from v1; TVS1 cells are unaffected. Never compare PNGs across renderer identities.
    public const string Renderer = "diagnostics-original-bitmap-v2";
    // VGA order, 0..15. Fixed RGB; cursor intentionally omitted.
    private static readonly uint[] Palette = [0x000000,0x0000aa,0x00aa00,0x00aaaa,0xaa0000,0xaa00aa,0xaa5500,0xaaaaaa,
        0x555555,0x5555ff,0x55ff55,0x55ffff,0xff5555,0xff55ff,0xffff55,0xffffff];
    public static uint Color(int index) => Palette[index];

    /// <summary>The uncompressed PNG raster (filter byte 0 + RGB per scanline). Its hash identifies the rendered pixels on
    /// every platform; the compressed PNG bytes depend on the runtime's deflate implementation (Windows and Linux differ).</summary>
    public static byte[] Raster(ScreenSnapshot screen)
    {
        int width = screen.Width * CellWidth, height = screen.Height * CellHeight;
        using var raw = new MemoryStream(height * (width * 3 + 1));
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // fixed PNG filter
            for (int x = 0; x < width; x++)
            {
                var cell = screen[x / CellWidth, y / CellHeight];
                uint rgb = Color(Glyph(cell.Character, x % CellWidth, y % CellHeight)
                    ? cell.Attr.Foreground : cell.Attr.Background >> 4);
                raw.WriteByte((byte)(rgb >> 16)); raw.WriteByte((byte)(rgb >> 8)); raw.WriteByte((byte)rgb);
            }
        }
        return raw.ToArray();
    }

    public static byte[] Encode(ScreenSnapshot screen) => Encode(screen, Raster(screen));

    internal static byte[] Encode(ScreenSnapshot screen, byte[] raster) => EncodeRaster(screen.Width * CellWidth, screen.Height * CellHeight, raster);

    /// <summary>PNG (RGB, 8 bit) of an uncompressed raster in <see cref="Raster"/>'s layout; also used for composed images (diffs).</summary>
    internal static byte[] EncodeRaster(int width, int height, byte[] raster, int channels = 3)
    {
        if (channels is not (3 or 4) || raster.Length != height * (width * channels + 1)) throw new ArgumentException("Raster size does not match the image.");
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zip.Write(raster);
        using var png = new MemoryStream(); png.Write([137,80,78,71,13,10,26,10]);
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = channels == 4 ? (byte)6 : (byte)2;
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string name, byte[] data)
    {
        Span<byte> value = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(value, data.Length); stream.Write(value);
        byte[] type = Encoding.ASCII.GetBytes(name); stream.Write(type); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (byte b in type.Concat(data)) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc); stream.Write(value);
    }

    // Original MIT-licensed bitmap glyphs. Unicode Latin marks are decomposed, drawn above
    // their base glyph; unsupported UTF-16 cells use a visible replacement box.
    public static bool Glyph(char ch, int x, int y)
    {
        if (ch is ' ' or '\0') return false;
        const string boxes = "─│┌┐└┘├┤┬┴┼═║╔╗╚╝╠╣╦╩╬";
        int box = boxes.IndexOf(ch);
        if (box >= 0)
        {
            int shape = box % 11;
            bool left = shape is 0 or 3 or 5 or 7 or 8 or 9 or 10;
            bool right = shape is 0 or 2 or 4 or 6 or 8 or 9 or 10;
            bool up = shape is 1 or 4 or 5 or 6 or 7 or 9 or 10;
            bool down = shape is 1 or 2 or 3 or 6 or 7 or 8 or 10;
            bool horizontal = box < 11 ? y == 5 : y is 4 or 6;
            bool vertical = box < 11 ? x == 3 : x is 2 or 4;
            return horizontal && ((left && x <= 4) || (right && x >= 2)) || vertical && ((up && y <= 6) || (down && y >= 4));
        }
        if (ch is >= '░' and <= '▓') return (x + y * 3) % 4 < ch - '░' + 1;
        if (ch == '█') return true;
        if (ch == '▀') return y < 6;
        if (ch == '▄') return y >= 6;
        if (ch == '■') return x is >= 1 and <= 6 && y is >= 3 and <= 8;
        if (ch == '·') return x is 3 or 4 && y is 5 or 6;
        if (ch == '…') return y == 8 && x is 1 or 3 or 5;
        if (ch == '≡') return y is 3 or 5 or 7 && x is >= 1 and <= 6;
        if (ch is '↑' or '↕') return x == 3 && y is >= 2 and <= 9 || y is >= 2 and <= 4 && Math.Abs(x-3) == y-2
            || ch == '↕' && y is >= 7 and <= 9 && Math.Abs(x-3) == 9-y;
        if (ch is '▲' or '▼') { int triangleRow = ch == '▲' ? y-3 : 8-y; return triangleRow is >= 0 and <= 3 && Math.Abs(x-3) <= triangleRow; }
        if (Symbol(ch, x, y) is { } symbol) return symbol;
        string decomposed = char.IsSurrogate(ch) ? "\ufffd" : ch.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.Length > 1 && y < 2)
            return decomposed[1] switch { '\u0301' => x == 4 - y, '\u030c' => y == 0 ? x is 2 or 4 : x == 3,
                '\u030a' => x is 2 or 4, '\u0308' => x is 2 or 4, _ => x == 3 };
        char basic = decomposed[0];
        int index = Characters.IndexOf(basic);
        if (index < 0) return (x is 1 or 6 && y is >= 2 and <= 9) || (y is 2 or 9 && x is >= 1 and <= 6);
        int row = y - 3, col = x - 1;
        return row is >= 0 and < 7 && col is >= 0 and < 5 && (Rows[index * 7 + row] & (1 << (4 - col))) != 0;
    }

    /// <summary>Characters drawn by <see cref="Symbol"/>; everything else outside the Latin set falls back to the visible
    /// replacement box.</summary>
    public const string Symbols = "►◄▶◀●○•◘□▪√✓→←↓↔";

    // Standard TSharpVision symbols (TSharpVisionGlyphs: submenu marker, scroll arrows, radio/indicator dot) and a few
    // common status/control marks. Original drawings on the 8x12 cell; null = not a symbol.
    private static bool? Symbol(char ch, int x, int y)
    {
        int dx = 2 * x - 7, dy = 2 * y - 11; // doubled offsets from the cell centre (3.5, 5.5)
        switch (ch)
        {
            case '►' or '▶': return y is >= 2 and <= 9 && x >= 2 && (x - 2) * 2 <= 7 - Math.Abs(dy);
            case '◄' or '◀': return y is >= 2 and <= 9 && x <= 5 && (5 - x) * 2 <= 7 - Math.Abs(dy);
            case '●': return dx * dx + dy * dy <= 30;
            case '○': return dx * dx + dy * dy is >= 14 and <= 34;
            case '•': return dx * dx + dy * dy <= 6;
            case '◘': return !(dx * dx + dy * dy <= 10) && x is >= 0 and <= 7 && y is >= 1 and <= 10;
            case '□': return (x is 1 or 6 && y is >= 3 and <= 8) || (y is 3 or 8 && x is >= 1 and <= 6);
            case '▪': return x is >= 2 and <= 5 && y is >= 4 and <= 7;
            case '√' or '✓': return (x, y) is (0, 6) or (1, 7) or (2, 8) or (3, 7) or (4, 6) or (5, 5) or (6, 4) or (7, 3) || ch == '√' && y == 2 && x is 6 or 7;
            case '→': return y == 5 && x is >= 1 and <= 6 || Math.Abs(y - 5) is >= 1 and <= 2 && x == 6 - Math.Abs(y - 5);
            case '←': return y == 5 && x is >= 1 and <= 6 || Math.Abs(y - 5) is >= 1 and <= 2 && x == 1 + Math.Abs(y - 5);
            case '↓': return x == 3 && y is >= 2 and <= 9 || y is >= 7 and <= 9 && Math.Abs(x - 3) == 9 - y;
            case '↔': return y == 5 && x is >= 1 and <= 6 || Math.Abs(y - 5) == 1 && x is 2 or 5;
            default: return null;
        }
    }

    private const string Characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,:;!?-_=+*/\\()[]{}<>|\"'`~@#$%^&";
    // Seven 5-bit rows per glyph; original drawings, bundled in source.
    private static readonly byte[] Rows = Parse([
        "0E11111F111111","1E11111E11111E","0F10101010100F","1E11111111111E","1F10101E10101F","1F10101E101010","0F10101711110F","1111111F111111","0E04040404040E","0101010111110E","11121418141211","1010101010101F","111B1515111111","11191513111111","0E11111111110E","1E11111E101010","0E11111115120D","1E11111E141211","0F10100E01011E","1F040404040404","1111111111110E","11111111110A04","11111115151B11","11110A040A1111","11110A04040404","1F01020408101F",
        "000E010F11110F","10101E1111111E","00000F1010100F","01010F1111110F","00000E111F100E","0608081E080808","00000F11110F01","10101E11111111","04000C0404040E","0200060202120C","10101214181412","0C04040404040E","00001A15151515","00001E11111111","00000E1111110E","00001E11111E10","00000F11110F01","00001619101010","00000F100E011E","08081E08080906","0000111111130D","00001111110A04","0000111115150A","0000110A040A11","00001111110F01","00001F0204081F",
        "0E11131519110E","040C040404040E","0E11010204081F","1E01010E01011E","02060A121F0202","1F10101E01011E","0E10101E11110E","1F010204080808","0E11110E11110E","0E11110F01010E",
        "00000000000C0C","00000000000408","000C0C000C0C00","000C0C000C0408","04040404040004","0E110102040004","0000001F000000","0000000000001F","00001F001F0000","0004041F040400","00150E1F0E1500","01020404081000","10080404020100","02040808080402","08040202020408","0E08080808080E","0E02020202020E","06080810080806","0C02020102020C","02040810080402","08040201020408","04040404040404","0A0A0000000000","04040800000000","08040000000000","00000D12000000","0E11171517100F","0A0A1F0A1F0A0A","040F140E051E04","19190204081313","040A1100000000","0C12140C15120D"]);
    private static byte[] Parse(string[] glyphs) => glyphs.SelectMany(g => Enumerable.Range(0,7).Select(i => Convert.ToByte(g.Substring(i*2,2),16))).ToArray();
}
