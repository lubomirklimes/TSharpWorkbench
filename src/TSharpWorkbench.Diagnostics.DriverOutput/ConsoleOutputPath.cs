using System.Runtime.InteropServices;
using TSharpVision.Drivers.Console;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>
/// Win32 console output. The real <see cref="Win32ConsoleDriver"/> writes with its own kernel32
/// calls (WriteConsoleOutputW, SetConsoleCursorPosition, SetConsoleCursorInfo) into a private,
/// never-displayed console screen buffer; the result is read back from the console host with
/// ReadConsoleOutputW. The observation point is therefore the console host's cell buffer, after
/// the native call — not a copy of the logical screen.
/// </summary>
public sealed class ConsoleOutputPath : IOutputPath
{
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, CONSOLE_TEXTMODE_BUFFER = 1;
    public const char Sentinel = '¤';
    public const ushort SentinelAttr = 0x5D;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SMALL_RECT { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize, dwCursorPosition; public ushort wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct CHAR_INFO { public char UnicodeChar; public ushort Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct CONSOLE_CURSOR_INFO { public uint dwSize; public int bVisible; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateConsoleScreenBuffer(uint access, uint share, IntPtr security, uint flags, IntPtr reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleScreenBufferSize(IntPtr h, COORD size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleWindowInfo(IntPtr h, bool absolute, ref SMALL_RECT rect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleCursorInfo(IntPtr h, out CONSOLE_CURSOR_INFO info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleOutputW(IntPtr h, [Out] CHAR_INFO[] buffer, COORD size, COORD coord, ref SMALL_RECT region);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FillConsoleOutputCharacterW(IntPtr h, char ch, uint length, COORD at, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FillConsoleOutputAttribute(IntPtr h, ushort attr, uint length, COORD at, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int AllocConsoleWithOptions(ref AllocOptions options, out int result);
    [StructLayout(LayoutKind.Sequential)] private struct AllocOptions { public int Mode; public int UseShowWindow; public ushort ShowWindow; }

    private IntPtr _buffer = IntPtr.Zero;
    private Win32ConsoleDriver? _driver;
    private GridSize _grid;

    public string Id => "console-win32";
    public string Driver => nameof(Win32ConsoleDriver);
    public string Boundary => "console host cell buffer read back with ReadConsoleOutputW after the driver's WriteConsoleOutputW (off-screen buffer)";
    public PathCapabilities Capabilities { get; } = new(CaretModel.Native, CursorShape: true, ForegroundOfBlankCells: true,
        "16-colour attribute byte passed through unchanged; host palette (RGB) is Layer C");
    public string? Unavailable { get; }

    /// <summary>How the console was obtained, for the report.</summary>
    public string ConsoleSource { get; private set; } = "none";

    public ConsoleOutputPath(bool attach = true)
    {
        if (!OperatingSystem.IsWindows()) { Unavailable = "Win32 console API exists only on Windows"; return; }
        if (!attach) return;
        IntPtr probe = Create();
        if (probe == IntPtr.Zero || probe == new IntPtr(-1))
        {
            // No console attached (redirected test host): create a windowless one (Windows 11 24H2+).
            try
            {
                var options = new AllocOptions { Mode = 2 /* ALLOC_CONSOLE_MODE_NO_WINDOW */ };
                int hr = AllocConsoleWithOptions(ref options, out _);
                if (hr == 0) ConsoleSource = "AllocConsoleWithOptions(NoWindow)";
            }
            catch (EntryPointNotFoundException) { }
            probe = Create();
        }
        else ConsoleSource = GetConsoleWindow() == IntPtr.Zero ? "inherited windowless console" : "inherited console (private buffer; window untouched)";
        if (probe == IntPtr.Zero || probe == new IntPtr(-1)) { Unavailable = $"no console available (CreateConsoleScreenBuffer error {Marshal.GetLastWin32Error()})"; return; }
        CloseHandle(probe);
    }

    private static IntPtr Create() =>
        CreateConsoleScreenBuffer(GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, CONSOLE_TEXTMODE_BUFFER, IntPtr.Zero);

    private GridSize _bufferSize;

    public void Begin(GridSize grid)
    {
        CloseBuffer();
        _buffer = Create();
        if (_buffer == IntPtr.Zero || _buffer == new IntPtr(-1)) throw new InvalidOperationException($"CreateConsoleScreenBuffer failed ({Marshal.GetLastWin32Error()}).");
        if (!TrySizeBuffer(grid, out string? failure)) throw new InvalidOperationException(failure);
        uint cells = (uint)(_bufferSize.Width * _bufferSize.Height);
        FillConsoleOutputCharacterW(_buffer, Sentinel, cells, default, out _);
        FillConsoleOutputAttribute(_buffer, SentinelAttr, cells, default, out _);
        _driver = new Win32ConsoleDriver();
        _driver.AttachOutputForTesting(_buffer, (ushort)grid.Width, (ushort)grid.Height);
        _grid = grid;
    }

    /// <summary>
    /// Sizes the private buffer to exactly <paramref name="grid"/>, or failing that keeps a larger
    /// buffer (cells outside the grid are then checked to stay untouched).
    /// </summary>
    private bool TrySizeBuffer(GridSize grid, out string? failure)
    {
        failure = null;
        if (!GetConsoleScreenBufferInfo(_buffer, out var info)) { failure = "GetConsoleScreenBufferInfo failed."; return false; }
        int winW = info.srWindow.Right - info.srWindow.Left + 1, winH = info.srWindow.Bottom - info.srWindow.Top + 1;
        if (winW > grid.Width || winH > grid.Height)
        {
            var rect = new SMALL_RECT { Left = 0, Top = 0, Right = (short)(Math.Min(winW, grid.Width) - 1), Bottom = (short)(Math.Min(winH, grid.Height) - 1) };
            SetConsoleWindowInfo(_buffer, true, ref rect);
        }
        if (SetConsoleScreenBufferSize(_buffer, new COORD { X = (short)grid.Width, Y = (short)grid.Height }))
        {
            _bufferSize = grid;
            return true;
        }
        int error = Marshal.GetLastWin32Error();
        GetConsoleScreenBufferInfo(_buffer, out var now);
        if (now.dwSize.X >= grid.Width && now.dwSize.Y >= grid.Height)
        {
            _bufferSize = new GridSize(now.dwSize.X, now.dwSize.Y);
            return true;
        }
        failure = $"console buffer {now.dwSize.X}x{now.dwSize.Y} cannot hold {grid} and SetConsoleScreenBufferSize failed ({error}); source {ConsoleSource}.";
        return false;
    }

    public void Apply(FixtureStep step, StepCapture capture)
    {
        var driver = _driver ?? throw new InvalidOperationException("Begin was not called.");
        if (step.ResizeTo is { } target)
        {
            // The host resizes the buffer; the driver learns the new window size from its record.
            if (!TrySizeBuffer(target, out string? failure)) throw new InvalidOperationException(failure);
            capture.Diagnostics.Add($"console buffer {_bufferSize} for grid {target}");
            ushort oldW = (ushort)_grid.Width, oldH = (ushort)_grid.Height;
            driver.SetWindowSizeForTests((ushort)target.Width, (ushort)target.Height);
            PathSupport.DrainQueue();
            bool reported = driver.ApplyWindowSize(oldW, oldH);
            capture.Check(reported, "driver reported the window-size change");
            capture.Check(TScreen.ScreenBuffer is { } sb && sb.Width == target.Width && sb.Height == target.Height,
                $"TScreen.ScreenBuffer reallocated at {target}");
            capture.Check(PathSupport.DrainResizeCommand(), "cmScreenResized queued");
            capture.Check(driver.GetCols() == target.Width && driver.GetRows() == target.Height, $"driver reports {target}");
            capture.Record(new CaptureOp("HostResize", W: target.Width, H: target.Height));
            _grid = target;
        }
        foreach (CellWrite w in step.Writes)
        {
            driver.WriteBuf(w.X, w.Y, w.Cells.Length, 1, w.Cells);
            capture.Record(new CaptureOp("WriteConsoleOutputW", w.X, w.Y, w.Cells.Length, 1));
        }
        driver.SetCaretPosition(step.Caret.X, step.Caret.Y);
        driver.SetCursorType(step.Caret.CursorType);
        capture.Record(new CaptureOp("SetConsoleCursorPosition", step.Caret.X, step.Caret.Y));
        capture.Record(new CaptureOp("SetConsoleCursorInfo", Detail: step.Caret.CursorType.ToString()));
    }

    public void Observe(StepCapture capture)
    {
        int w = capture.Grid.Width, h = capture.Grid.Height, bw = _bufferSize.Width, bh = _bufferSize.Height;
        var all = new CHAR_INFO[bw * bh];
        var region = new SMALL_RECT { Left = 0, Top = 0, Right = (short)(bw - 1), Bottom = (short)(bh - 1) };
        if (!ReadConsoleOutputW(_buffer, all, new COORD { X = (short)bw, Y = (short)bh }, default, ref region))
            throw new InvalidOperationException($"ReadConsoleOutputW failed ({Marshal.GetLastWin32Error()}).");
        var semantic = new SemanticCell[w * h];
        int extraBits = 0, outside = 0;
        for (int y = 0; y < bh; y++)
        {
            for (int x = 0; x < bw; x++)
            {
                CHAR_INFO c = all[y * bw + x];
                if (x >= w || y >= h)
                {
                    if (c.UnicodeChar != Sentinel || c.Attributes != SentinelAttr) outside++;
                    continue;
                }
                if ((c.Attributes & 0xFF00) != 0) extraBits++;
                semantic[y * w + x] = new SemanticCell(c.UnicodeChar.ToString(), c.Attributes & 0x0F, (c.Attributes >> 4) & 0x0F);
            }
        }
        if (bw != w || bh != h)
            capture.Check(outside == 0, $"no console cell outside the {w}x{h} grid was written (buffer {bw}x{bh}; {outside} changed)");
        if (extraBits > 0) capture.Diagnostics.Add($"{extraBits} cell(s) carry COMMON_LVB attribute bits");
        int sentinels = semantic.Count(c => c.Text == Sentinel.ToString());
        if (sentinels > 0) capture.Diagnostics.Add($"{sentinels} cell(s) still hold the pre-fill sentinel");
        GetConsoleScreenBufferInfo(_buffer, out var info);
        GetConsoleCursorInfo(_buffer, out var ci);
        string shape = ci.bVisible == 0 ? "hidden" : ci.dwSize >= 100 ? "block" : "underline";
        capture.Cells = semantic;
        capture.Diagnostics.Add($"console cursor dwSize={ci.dwSize} visible={ci.bVisible != 0}");
        capture.Cursor = new CursorState(info.dwCursorPosition.X, info.dwCursorPosition.Y, ci.bVisible != 0, shape);
    }

    private void CloseBuffer()
    {
        _driver?.DetachOutputForTesting();
        _driver = null;
        if (_buffer != IntPtr.Zero && _buffer != new IntPtr(-1)) CloseHandle(_buffer);
        _buffer = IntPtr.Zero;
    }

    public void Dispose() => CloseBuffer();
}
