using System.Buffers.Binary;
using System.IO.Compression;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>An RGBA8 image (R = byte 0, top row first, no padding).</summary>
public sealed class NativeImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public NativeImage(int width, int height, byte[]? rgba = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height}");
        Width = width;
        Height = height;
        Rgba = rgba ?? new byte[width * height * 4];
        if (Rgba.Length != width * height * 4)
            throw new CorruptCaptureException($"pixel buffer has {Rgba.Length} bytes, {width}x{height} RGBA8 needs {width * height * 4}");
    }

    /// <summary>0xRRGGBB at (x, y).</summary>
    public int Rgb(int x, int y)
    {
        int o = (y * Width + x) * 4;
        return (Rgba[o] << 16) | (Rgba[o + 1] << 8) | Rgba[o + 2];
    }

    public byte Alpha(int x, int y) => Rgba[(y * Width + x) * 4 + 3];

    public void Set(int x, int y, int rgb, byte alpha = 255)
    {
        int o = (y * Width + x) * 4;
        Rgba[o] = (byte)(rgb >> 16); Rgba[o + 1] = (byte)(rgb >> 8); Rgba[o + 2] = (byte)rgb; Rgba[o + 3] = alpha;
    }

    public NativeImage Clone() => new(Width, Height, (byte[])Rgba.Clone());

    /// <summary>Copies a <paramref name="w"/> x <paramref name="h"/> block from <paramref name="src"/> to (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public void Blit(NativeImage src, int sx, int sy, int w, int h, int dx, int dy)
    {
        for (int y = 0; y < h; y++)
            Array.Copy(src.Rgba, ((sy + y) * src.Width + sx) * 4, Rgba, ((dy + y) * Width + dx) * 4, w * 4);
    }

    // ── PNG (8-bit RGBA, non-interlaced, filter 0) ─────────────────────────────

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Lossless PNG of exactly these pixels.</summary>
    public byte[] EncodePng()
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], Height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = Width * 4;
            for (int y = 0; y < Height; y++)
            {
                z.WriteByte(0);
                z.Write(Rgba, y * stride, stride);
            }
        }
        WriteChunk(ms, "IDAT", raw.ToArray());
        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc32.Append(Crc32.Append(0xFFFFFFFFu, head[4..]), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    /// <summary>
    /// Decodes a PNG written by <see cref="EncodePng"/> (8-bit RGBA, non-interlaced; all five
    /// scanline filters accepted). Any structural damage — bad signature, chunk CRC, size,
    /// truncated data — throws <see cref="CorruptCaptureException"/>.
    /// </summary>
    public static NativeImage DecodePng(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new CorruptCaptureException("not a PNG (signature)");
        int pos = 8, width = 0, height = 0;
        using var idat = new MemoryStream();
        bool end = false;
        while (!end)
        {
            if (pos + 12 > png.Length) throw new CorruptCaptureException("truncated PNG chunk");
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            if (len < 0 || pos + 12 + len > png.Length) throw new CorruptCaptureException("PNG chunk length out of range");
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            uint crc = Crc32.Append(0xFFFFFFFFu, png.AsSpan(pos + 4, 4 + len)) ^ 0xFFFFFFFFu;
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len)))
                throw new CorruptCaptureException($"PNG chunk {type} CRC mismatch");
            ReadOnlySpan<byte> data = png.AsSpan(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[9] != 6 || data[12] != 0) throw new CorruptCaptureException("PNG is not 8-bit RGBA non-interlaced");
                    break;
                case "IDAT": idat.Write(data); break;
                case "IEND": end = true; break;
            }
            pos += 12 + len;
        }
        if (width <= 0 || height <= 0) throw new CorruptCaptureException("PNG without IHDR");
        int stride = width * 4;
        byte[] raw = new byte[(stride + 1) * height];
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            z.ReadExactly(raw);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new CorruptCaptureException("PNG image data truncated or damaged: " + ex.Message);
        }
        var rgba = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int f = raw[y * (stride + 1)];
            int src = y * (stride + 1) + 1, dst = y * stride;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 4 ? rgba[dst + i - 4] : 0;
                int b = y > 0 ? rgba[dst - stride + i] : 0;
                int c = i >= 4 && y > 0 ? rgba[dst - stride + i - 4] : 0;
                int v = raw[src + i];
                rgba[dst + i] = (byte)(f switch
                {
                    0 => v,
                    1 => v + a,
                    2 => v + b,
                    3 => v + (a + b) / 2,
                    4 => v + Paeth(a, b, c),
                    _ => throw new CorruptCaptureException($"PNG filter {f}"),
                });
            }
        }
        return new NativeImage(width, height, rgba);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

/// <summary>CRC-32 (IEEE 802.3) as PNG uses it.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}

/// <summary>Captured pixel data is structurally unusable (wrong size, damaged encoding).</summary>
public sealed class CorruptCaptureException(string message) : Exception(message);
