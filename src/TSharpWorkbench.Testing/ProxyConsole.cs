using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TSharpWorkbench.Testing;

/// <summary>Child-side inspection/sizing of this process's own native console only. Never attaches a console.</summary>
internal static class ProxyConsole
{
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Info
    {
        public Coord Size, Cursor; public ushort Attributes; public Rect Window; public Coord Maximum;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Cursor { public uint Size; public int Visible; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Cell { public char Character; public ushort Attribute; }

    internal static void Resize(int width, int height)
    {
        RequireOwned();
        IntPtr output = GetStdHandle(-11);
        Check(GetConsoleScreenBufferInfo(output, out var before), "GetConsoleScreenBufferInfo");
        var maximum = GetLargestConsoleWindowSize(output);
        if (width > maximum.X || height > maximum.Y)
            throw new NotSupportedException($"Owned console requested {width}x{height}; font/display limit {maximum.X}x{maximum.Y}.");
        try
        {
            // Shrink only the necessary dimensions before changing buffer size.
            var intermediate = new Rect { Right = (short)(Math.Min(width, before.Window.Right - before.Window.Left + 1) - 1),
                Bottom = (short)(Math.Min(height, before.Window.Bottom - before.Window.Top + 1) - 1) };
            Check(SetConsoleWindowInfo(output, true, ref intermediate), "Shrink owned console window");
            Check(SetConsoleScreenBufferSize(output, new Coord { X = checked((short)width), Y = checked((short)height) }), "Size owned console buffer");
            var desired = new Rect { Right = (short)(width - 1), Bottom = (short)(height - 1) };
            Check(SetConsoleWindowInfo(output, true, ref desired), "Size owned console window");
            Check(GetConsoleScreenBufferInfo(output, out var actual), "Read owned geometry");
            if (actual.Size.X != width || actual.Size.Y != height || actual.Window.Left != 0 || actual.Window.Top != 0
                || actual.Window.Right + 1 != width || actual.Window.Bottom + 1 != height)
                throw new NotSupportedException($"Console host did not represent requested {width}x{height}; buffer {actual.Size.X}x{actual.Size.Y}, window {actual.Window.Right-actual.Window.Left+1}x{actual.Window.Bottom-actual.Window.Top+1}.");
        }
        catch
        {
            // Roll back best-effort; only this owned console is affected, and session failure closes it.
            var small = new Rect { Right = 0, Bottom = 0 };
            SetConsoleWindowInfo(output, true, ref small);
            SetConsoleScreenBufferSize(output, before.Size);
            SetConsoleWindowInfo(output, true, ref before.Window);
            throw;
        }
    }

    internal static ConsoleHostEvidence Inspect()
    {
        RequireOwned();
        Check(GetConsoleMode(GetStdHandle(-10), out uint inputMode), "Console input mode");
        var output = GetStdHandle(-11);
        Check(GetConsoleMode(output, out uint outputMode), "Console output mode");
        Check(GetConsoleScreenBufferInfo(output, out var info), "Console buffer geometry");
        Check(GetConsoleCursorInfo(output, out var cursor), "Console cursor");
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero) throw new NotSupportedException("Owned console window missing.");
        var maximum = GetLargestConsoleWindowSize(output);
        return new("CREATE_NEW_CONSOLE/job-kill-on-close", Environment.ProcessId, window.ToInt64(), inputMode, outputMode,
            info.Size.X, info.Size.Y, info.Window.Right-info.Window.Left+1, info.Window.Bottom-info.Window.Top+1,
            info.Cursor.X, info.Cursor.Y, (int)cursor.Size, cursor.Visible != 0)
        { WindowVisible=IsWindowVisible(window), MaximumWidth=maximum.X, MaximumHeight=maximum.Y };
    }

    internal static ProxyNativeCapture CaptureNativeCells()
    {
        var evidence = Inspect(); // Enforces this session's owned console, never attaches elsewhere.
        int width = evidence.WindowWidth, height = evidence.WindowHeight;
        var data = new Cell[checked(width * height)];
        var region = new Rect { Right = (short)(width - 1), Bottom = (short)(height - 1) };
        Check(ReadConsoleOutputW(GetStdHandle(-11), data, new Coord { X = (short)width, Y = (short)height }, default, ref region), "Read native console cells");
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "tsharpworkbench.native-console-cells.1", width, height,
            characters = data.Select(c => (int)c.Character).ToArray(),
            attributes = data.Select(c => c.Attribute).ToArray(), evidence.CursorX, evidence.CursorY, evidence.CursorVisible
        });
        return new(ProxyCaptureKinds.Console, "console-cells-json", Convert.ToBase64String(bytes), 0, 0,
            ScreenSnapshot.Hash(bytes), new() { ["provider"] = "ReadConsoleOutputW", ["completion"] = "synchronous native console read",
                ["captureLayer"] = "native console text cells and attributes; not window pixels", ["payloadBytes"] = bytes.Length.ToString() });
    }

    internal static bool Matches(ScreenSnapshot root)
    {
        RequireOwned();
        var data = new Cell[root.Width * root.Height];
        var region = new Rect { Right = (short)(root.Width-1), Bottom = (short)(root.Height-1) };
        Check(ReadConsoleOutputW(GetStdHandle(-11), data, new Coord { X=(short)root.Width,Y=(short)root.Height }, default, ref region), "Read driver output");
        return Enumerable.Range(0, data.Length).All(i =>
            data[i].Character == (root[i % root.Width,i / root.Width].Character == 0 ? ' ' : root[i % root.Width,i / root.Width].Character)
            && data[i].Attribute == (ushort)root[i % root.Width,i / root.Width].Attr);
    }

    private static void RequireOwned()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("TSHARPVISION_OWNED_CONSOLE") != "1")
            throw new NotSupportedException("Console proxy requires an owned interactive console host.");
    }
    private static void Check(bool ok, string operation) { if (!ok) { int code=Marshal.GetLastWin32Error(); throw new Win32Exception(code,$"{operation}: {new Win32Exception(code).Message} (Win32 {code})."); } }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetConsoleScreenBufferInfo(IntPtr h, out Info info);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetConsoleCursorInfo(IntPtr h, out Cursor cursor);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetConsoleScreenBufferSize(IntPtr h, Coord size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetConsoleWindowInfo(IntPtr h, bool absolute, ref Rect window);
    [DllImport("kernel32.dll")] private static extern Coord GetLargestConsoleWindowSize(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern bool ReadConsoleOutputW(IntPtr h, [Out] Cell[] data, Coord size, Coord origin, ref Rect region);
}
