using System.Runtime.InteropServices;
using static TSharpWorkbench.Diagnostics.NativeHosts.Win32;

namespace TSharpWorkbench.Diagnostics.NativeHosts;

/// <summary>Child-side access to the child's own console: sizing, font, facts and buffer readback.</summary>
internal static class ConsoleSide
{
    /// <summary>
    /// Sizes this process's own console window and buffer to the requested grid and pins its font
    /// (classic conhost only; ConPTY refuses buffer resizes). Affects only this test-owned console.
    /// </summary>
    public static void Configure(ConsoleProbe probe, string? grid, string? font)
    {
        IntPtr h = OpenConsoleOutput();
        try
        {
            if (font is { } f)
            {
                string[] parts = f.Split(':');
                var fi = new CONSOLE_FONT_INFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFOEX>(),
                    dwFontSize = new COORD { X = 0, Y = short.Parse(parts[1]) },
                    FontFamily = 54, // FF_MODERN | TMPF_TRUETYPE | TMPF_VECTOR
                    FontWeight = 400,
                    FaceName = parts[0],
                };
                probe.FontRequest = f;
                probe.FontResult = SetCurrentConsoleFontEx(h, false, ref fi) ? "ok" : $"error {Marshal.GetLastWin32Error()}";
            }
            if (grid is { } g)
            {
                string[] wh = g.Split('x');
                short w = short.Parse(wh[0]), hgt = short.Parse(wh[1]);
                probe.SizeRequest = g;
                // Shrink the window first so the buffer can be set to any size, then grow the window.
                var tiny = new SMALL_RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
                SetConsoleWindowInfo(h, true, ref tiny);
                bool ok = SetConsoleScreenBufferSize(h, new COORD { X = w, Y = hgt });
                int e1 = ok ? 0 : Marshal.GetLastWin32Error();
                var full = new SMALL_RECT { Left = 0, Top = 0, Right = (short)(w - 1), Bottom = (short)(hgt - 1) };
                bool ok2 = SetConsoleWindowInfo(h, true, ref full);
                int e2 = ok2 ? 0 : Marshal.GetLastWin32Error();
                probe.SizeResult = ok && ok2 ? "ok" : $"buffer {(ok ? "ok" : "error " + e1)}, window {(ok2 ? "ok" : "error " + e2)}";
            }
        }
        finally { CloseHandle(h); }
    }

    public static void Describe(ConsoleProbe probe)
    {
        probe.Pid = Environment.ProcessId;
        IntPtr cw = GetConsoleWindow();
        probe.ConsoleWindow = cw.ToInt64();
        probe.ConsoleWindowClass = cw == IntPtr.Zero ? "" : ClassName(cw);
        IntPtr owner = cw == IntPtr.Zero ? IntPtr.Zero : GetWindow(cw, GW_OWNER);
        if (owner == IntPtr.Zero && cw != IntPtr.Zero) owner = GetAncestor(cw, GA_ROOTOWNER);
        probe.OwnerWindow = owner.ToInt64();
        probe.OwnerWindowClass = owner == IntPtr.Zero ? "" : ClassName(owner);
        probe.OutputCodePage = GetConsoleOutputCP();
        IntPtr h = OpenConsoleOutput();
        try
        {
            var fi = new CONSOLE_FONT_INFOEX { cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFOEX>() };
            if (GetCurrentConsoleFontEx(h, false, ref fi))
            {
                probe.FontFace = fi.FaceName; probe.FontWidth = fi.dwFontSize.X; probe.FontHeight = fi.dwFontSize.Y; probe.FontWeight = fi.FontWeight;
            }
            var info = new CONSOLE_SCREEN_BUFFER_INFOEX { cbSize = (uint)Marshal.SizeOf<CONSOLE_SCREEN_BUFFER_INFOEX>(), ColorTable = new uint[16] };
            if (GetConsoleScreenBufferInfoEx(h, ref info))
            {
                probe.BufferWidth = info.dwSize.X; probe.BufferHeight = info.dwSize.Y;
                probe.WindowColumns = info.srWindow.Right - info.srWindow.Left + 1;
                probe.WindowRows = info.srWindow.Bottom - info.srWindow.Top + 1;
                // COLORREF is 0x00BBGGRR.
                probe.ColorTable = info.ColorTable.Select(c => $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}").ToArray();
            }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Reads the visible window of the active console buffer: the cells the host renders.</summary>
    public static HostScreen? Readback()
    {
        IntPtr h = OpenConsoleOutput();
        if (h == new IntPtr(-1)) return null;
        try
        {
            var info = new CONSOLE_SCREEN_BUFFER_INFOEX { cbSize = (uint)Marshal.SizeOf<CONSOLE_SCREEN_BUFFER_INFOEX>(), ColorTable = new uint[16] };
            if (!GetConsoleScreenBufferInfoEx(h, ref info)) return null;
            int w = info.srWindow.Right - info.srWindow.Left + 1, hgt = info.srWindow.Bottom - info.srWindow.Top + 1;
            var cells = new CHAR_INFO[w * hgt];
            var region = info.srWindow;
            if (!ReadConsoleOutputW(h, cells, new COORD { X = (short)w, Y = (short)hgt }, new COORD(), ref region)) return null;
            GetConsoleCursorInfo(h, out var ci);
            return new HostScreen
            {
                Source = "console-readback",
                Width = w, Height = hgt,
                Chars = new string(cells.Select(c => c.UnicodeChar == '\0' ? ' ' : c.UnicodeChar).ToArray()),
                Attrs = cells.Select(c => (byte)(c.Attributes & 0xFF)).ToArray(),
                CursorX = info.dwCursorPosition.X - info.srWindow.Left,
                CursorY = info.dwCursorPosition.Y - info.srWindow.Top,
                CursorVisible = ci.bVisible != 0,
                CursorSize = (int)ci.dwSize,
            };
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Polls the readback until two consecutive reads agree (the host buffer is quiet).</summary>
    public static (HostScreen? Screen, bool Settled) Settle(HostScreen? previous, bool expectChange, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HostScreen? last = Readback();
        while (sw.Elapsed < timeout)
        {
            Thread.Sleep(120);
            HostScreen? now = Readback();
            bool quiet = now != null && last != null && now.SameAll(last);
            bool changed = !expectChange || previous == null || (now != null && !now.SameAll(previous));
            if (quiet && changed) return (now, true);
            last = now;
        }
        return (last, false);
    }

    /// <summary>Enables VT output on this console (for the Terminal driver's escape sequences).</summary>
    public static void EnableVirtualTerminalOutput()
    {
        IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(h, out uint mode)) SetConsoleMode(h, mode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }
}
