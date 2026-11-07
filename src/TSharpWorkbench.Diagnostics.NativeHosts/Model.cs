using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSharpWorkbench.Diagnostics.NativeHosts;

/// <summary>Host facts the child reads from its own console at startup (font, palette, window, grid).</summary>
public sealed class ConsoleProbe
{
    public int Pid { get; set; }
    public string Mode { get; set; } = "";
    public string Driver { get; set; } = "";
    public long ConsoleWindow { get; set; }
    public string ConsoleWindowClass { get; set; } = "";
    public long OwnerWindow { get; set; }
    public string OwnerWindowClass { get; set; } = "";
    public string FontFace { get; set; } = "";
    public int FontWidth { get; set; }
    public int FontHeight { get; set; }
    public int FontWeight { get; set; }
    public string[] ColorTable { get; set; } = [];
    public int BufferWidth { get; set; }
    public int BufferHeight { get; set; }
    public int WindowColumns { get; set; }
    public int WindowRows { get; set; }
    public uint OutputCodePage { get; set; }
    public string? SizeRequest { get; set; }
    public string? SizeResult { get; set; }
    public string? FontRequest { get; set; }
    public string? FontResult { get; set; }
}

/// <summary>
/// The cells a host was asked to show: either read back from the console buffer the host renders
/// (<see cref="Source"/> = "console-readback") or the fixture's logical screen ("logical").
/// </summary>
public sealed class HostScreen
{
    public string Source { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Chars { get; set; } = "";
    public byte[] Attrs { get; set; } = [];
    public int CursorX { get; set; }
    public int CursorY { get; set; }
    public bool CursorVisible { get; set; }
    public int CursorSize { get; set; }

    public char Char(int x, int y) => Chars[y * Width + x];
    public byte Attr(int x, int y) => Attrs[y * Width + x];

    public bool SameCells(HostScreen other) =>
        Width == other.Width && Height == other.Height && Chars == other.Chars && Attrs.AsSpan().SequenceEqual(other.Attrs);

    public bool SameAll(HostScreen other) => SameCells(other) && CursorX == other.CursorX && CursorY == other.CursorY
                                             && CursorVisible == other.CursorVisible && CursorSize == other.CursorSize;

    /// <summary>The whole screen as text rows (for reports).</summary>
    public IEnumerable<string> Rows() => Enumerable.Range(0, Height).Select(y => Chars.Substring(y * Width, Width));
}

/// <summary>The child's answer to one control command.</summary>
public sealed class ControlAck
{
    public int Seq { get; set; }
    public string Command { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Settled { get; set; }
    public HostScreen? Readback { get; set; }
    public HostScreen? Logical { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    public static void Write<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)!;
}
