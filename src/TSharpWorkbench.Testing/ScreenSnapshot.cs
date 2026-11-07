using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TSharpWorkbench.Testing;

// Public types here belong to optional tooling, not the production framework API.
public sealed class ScreenSnapshot
{
    private readonly TScreenChar[] cells;
    public int Width { get; }
    public int Height { get; }
    public TScreenChar this[int x, int y] => x >= 0 && x < Width && y >= 0 && y < Height
        ? cells[y * Width + x] : throw new ArgumentOutOfRangeException();

    public ScreenSnapshot(int width, int height, ReadOnlySpan<TScreenChar> source)
    {
        if (width is < 1 or > 200 || height is < 1 or > 100 || source.Length != checked(width * height))
            throw new ArgumentException("Invalid screen geometry or cell count.");
        Width = width; Height = height; cells = source.ToArray();
    }

    // Caller must own the UI thread. No mutable live buffer escapes this copy.
    public static ScreenSnapshot CopyRoot()
    {
        var buffer = TScreen.ScreenBuffer ?? throw new InvalidOperationException("No composed screen.");
        if (buffer.Width != TScreen.ScreenWidth || buffer.Height != TScreen.ScreenHeight)
            throw new InvalidOperationException("Screen geometry mismatch.");
        return new((int)buffer.Width, (int)buffer.Height, buffer.Data);
    }

    // TVS1, little-endian int32 width/height, row-major UTF-16LE char + raw attribute byte.
    public byte[] Serialize()
    {
        var bytes = new byte[12 + cells.Length * 3];
        "TVS1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Height);
        for (int i = 0; i < cells.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12 + i * 3), cells[i].Character);
            bytes[14 + i * 3] = cells[i].Attr;
        }
        return bytes;
    }

    public static ScreenSnapshot Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("TVS1"u8)) throw new InvalidDataException("Invalid snapshot.");
        int w = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]), h = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (w is < 1 or > 200 || h is < 1 or > 100 || bytes.Length != 12 + w * h * 3)
            throw new InvalidDataException("Invalid snapshot bounds.");
        var cells = new TScreenChar[w * h];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new((char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(12 + i * 3)..]), bytes[14 + i * 3]);
        return new(w, h, cells);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
