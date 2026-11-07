using System.Diagnostics;
using TSharpWorkbench.Diagnostics.NativePixels;
using static TSharpWorkbench.Diagnostics.NativeHosts.Win32;

namespace TSharpWorkbench.Diagnostics.NativeHosts;

/// <summary>Window-only capture of a host's client area through PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT).</summary>
internal static class WindowCapture
{
    public const string Mechanism = "user32 PrintWindow(PW_CLIENTONLY|PW_RENDERFULLCONTENT) into a 32-bpp DIB section, per-monitor-DPI-aware process";

    public static NativeImage? Capture(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !GetClientRect(hwnd, out RECT rc)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr bmp = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!PrintWindow(hwnd, mem, PW_CLIENTONLY | PW_RENDERFULLCONTENT)) return null;
            var bgra = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(bits, bgra, 0, bgra.Length);
            var rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4) { rgba[i] = bgra[i + 2]; rgba[i + 1] = bgra[i + 1]; rgba[i + 2] = bgra[i]; rgba[i + 3] = 255; }
            return new NativeImage(w, h, rgba);
        }
        finally
        {
            SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }
}

/// <summary>
/// One test-owned host window running one child. Only the window identified through the child's own
/// console (and, for Windows Terminal, verified to be newly created) is captured, resized or closed;
/// only the child process (and the conhost process this harness started) is ever terminated.
/// </summary>
internal sealed class HostSession : IDisposable
{
    public const string Conhost = "conhost", WindowsTerminal = "windows-terminal";

    public string Host { get; }
    public string Mode { get; }
    public string Driver { get; }
    public string WorkDir { get; }
    public ConsoleProbe Probe { get; private set; } = new();
    public IntPtr Window { get; private set; }
    public string WindowClass => ClassName(Window);
    public Process? Child { get; private set; }
    public List<string> Log { get; } = new();
    private Process? _launcher;
    private int _seq;
    private readonly string _exe;

    private HostSession(string host, string mode, string driver, string work, string exe)
    { Host = host; Mode = mode; Driver = driver; WorkDir = work; _exe = exe; }

    public static HostSession Launch(string host, string mode, string driver, string grid, string exe, string work)
    {
        Directory.CreateDirectory(work);
        var s = new HostSession(host, mode, driver, work, exe);
        string[] wh = grid.Split('x');
        string childArgs = $"{mode} \"{work}\" --driver {driver}";
        List<IntPtr> before = TopLevelWindows("CASCADIA_HOSTING_WINDOW_CLASS");
        if (host == Conhost)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                $"\"{exe}\" {childArgs} --grid {grid} --font Consolas:16") { UseShellExecute = true }; // own new console window
            s._launcher = Process.Start(psi);
            s.Log.Add($"conhost.exe pid {s._launcher?.Id}: \"{exe}\" {childArgs} --grid {grid} --font Consolas:16");
        }
        else
        {
            var psi = new ProcessStartInfo("wt.exe", $"-w new --size {wh[0]},{wh[1]} --pos 40,40 -- \"{exe}\" {childArgs}") { UseShellExecute = false };
            using var wt = Process.Start(psi);
            s.Log.Add($"wt.exe -w new --size {wh[0]},{wh[1]} --pos 40,40 -- \"{exe}\" {childArgs}");
        }
        string ready = Path.Combine(work, HostChild.ReadyName);
        var sw = Stopwatch.StartNew();
        while (!File.Exists(ready) && sw.Elapsed < TimeSpan.FromSeconds(25))
        {
            if (s._launcher is { HasExited: true }) break;
            Thread.Sleep(100);
        }
        if (!File.Exists(ready))
        {
            string err = File.Exists(Path.Combine(work, "child-error.txt")) ? File.ReadAllText(Path.Combine(work, "child-error.txt")) : "no ready signal";
            s.Dispose();
            throw new InvalidOperationException($"{host} child did not become ready: {err}");
        }
        s.Probe = Json.Read<ConsoleProbe>(ready);
        try { s.Child = Process.GetProcessById(s.Probe.Pid); } catch (ArgumentException) { }

        if (host == Conhost)
        {
            s.Window = new IntPtr(s.Probe.ConsoleWindow);
            if (s.Probe.ConsoleWindowClass != "ConsoleWindowClass") { s.Dispose(); throw new InvalidOperationException($"not a classic console window: {s.Probe.ConsoleWindowClass}"); }
            SetWindowPos(s.Window, IntPtr.Zero, 40, 40, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
        }
        else
        {
            IntPtr owner = new(s.Probe.OwnerWindow);
            var fresh = TopLevelWindows("CASCADIA_HOSTING_WINDOW_CLASS").Except(before).ToList();
            s.Log.Add($"pseudo console window {s.Probe.ConsoleWindow:X} ({s.Probe.ConsoleWindowClass}), owner {owner:X} ({s.Probe.OwnerWindowClass}), new terminal windows: {string.Join(",", fresh.Select(f => f.ToString("X")))}");
            if (owner != IntPtr.Zero && fresh.Contains(owner)) s.Window = owner;
            else if (fresh.Count == 1 && owner == IntPtr.Zero) s.Window = fresh[0];
            else { s.Dispose(); throw new InvalidOperationException("cannot identify the test-owned Windows Terminal window; nothing was captured"); }
        }
        Thread.Sleep(400);
        return s;
    }

    public ControlAck Send(string cmd, TimeSpan? timeout = null)
    {
        int seq = ++_seq;
        string tmp = Path.Combine(WorkDir, $"cmd-{seq:D4}.tmp");
        File.WriteAllText(tmp, cmd);
        File.Move(tmp, Path.Combine(WorkDir, $"cmd-{seq:D4}.txt"));
        string ack = Path.Combine(WorkDir, $"ack-{seq:D4}.json");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < (timeout ?? TimeSpan.FromSeconds(12)))
        {
            if (File.Exists(ack))
            {
                try { return Json.Read<ControlAck>(ack); } catch (IOException) { } catch (System.Text.Json.JsonException) { }
            }
            if (Child is { HasExited: true }) break;
            Thread.Sleep(40);
        }
        return new ControlAck { Seq = seq, Command = cmd, Error = Child is { HasExited: true } ? "child exited" : "no acknowledgement" };
    }

    /// <summary>Captures until two consecutive captures agree (outside the given cells), up to six tries.</summary>
    public NativeImage? CaptureStable(Func<NativeImage, NativeImage, bool>? same = null)
    {
        NativeImage? prev = WindowCapture.Capture(Window);
        for (int i = 0; i < 6 && prev != null; i++)
        {
            Thread.Sleep(250);
            NativeImage? now = WindowCapture.Capture(Window);
            if (now == null) return prev;
            bool equal = now.Width == prev.Width && now.Height == prev.Height && (same?.Invoke(prev, now) ?? DiffPixels(prev, now) < prev.Width * prev.Height / 500);
            if (equal) return now;
            prev = now;
        }
        return prev;
    }

    public static int DiffPixels(NativeImage a, NativeImage b)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.Rgb(x, y) != b.Rgb(x, y)) n++;
        return n;
    }

    public List<NativeImage> CaptureSeries(int count, int intervalMs)
    {
        var list = new List<NativeImage>();
        for (int i = 0; i < count; i++)
        {
            if (WindowCapture.Capture(Window) is { } img) list.Add(img);
            Thread.Sleep(intervalMs);
        }
        return list;
    }

    // ── input ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Keyboard input into this session only. Classic conhost: WM_KEYDOWN/WM_KEYUP posted to the console
    /// window (the host's own keyboard handling translates them into input records). Otherwise, or for
    /// modifier chords, key records are written into this child's console input buffer by a helper that
    /// attaches to the child's console. Nothing is injected globally.
    /// </summary>
    public string SendKeys(string tokens, bool preferWindowMessages)
    {
        if (preferWindowMessages && Host == Conhost && !tokens.Contains("ALT+"))
        {
            foreach (string t in tokens.Split(','))
            {
                (ushort vk, ushort scan) = t switch
                {
                    "F10" => ((ushort)0x79, (ushort)0x44), "ENTER" => ((ushort)0x0D, (ushort)0x1C), "ESC" => ((ushort)0x1B, (ushort)0x01),
                    "DOWN" => ((ushort)0x28, (ushort)0x50), "UP" => ((ushort)0x26, (ushort)0x48),
                    _ when t.Length == 1 && char.IsLetter(t[0]) => ((ushort)char.ToUpperInvariant(t[0]), InputInjector.Scan(char.ToUpperInvariant(t[0]))),
                    _ => throw new ArgumentException($"key {t}"),
                };
                nint ext = vk is 0x28 or 0x26 ? 1 << 24 : 0;
                PostMessageW(Window, 0x0100, vk, 1 | (scan << 16) | ext);
                Thread.Sleep(30);
                PostMessageW(Window, 0x0101, vk, unchecked((nint)(uint)(1 | (scan << 16) | (int)ext | unchecked((int)0xC0000000))));
                Thread.Sleep(250);
            }
            return "WM_KEYDOWN/WM_KEYUP posted to the classic console window (host keyboard path)";
        }
        if (Child == null) throw new InvalidOperationException("child process unknown");
        var psi = new ProcessStartInfo(_exe, $"inject {Child.Id} {tokens}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"input injection failed: {err}");
        return "KEY_EVENT records written to the child's console input buffer (helper attached to the test-owned console; bypasses the host's keyboard handling)";
    }

    public void ResizeWindow(int width, int height)
    {
        SetWindowPos(Window, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER);
    }

    public (bool Exited, int Code, bool WindowClosed, double Seconds) WaitExit(TimeSpan bound)
    {
        var sw = Stopwatch.StartNew();
        bool exited = Child == null || Child.WaitForExit((int)bound.TotalMilliseconds);
        int code = -1;
        // The child is not this process's own child (conhost / Windows Terminal started it): its exit
        // code comes from the exit record it writes after AppLifecycleGuard and driver shutdown returned.
        string exitFile = Path.Combine(WorkDir, HostChild.ExitName);
        if (exited && File.Exists(exitFile))
            try { code = System.Text.Json.JsonDocument.Parse(File.ReadAllText(exitFile)).RootElement.GetProperty("Code").GetInt32(); } catch { }
        double seconds = sw.Elapsed.TotalSeconds;
        bool closed = false;
        while (sw.Elapsed < bound + TimeSpan.FromSeconds(5)) { if (!IsWindow(Window)) { closed = true; break; } Thread.Sleep(100); }
        return (exited, code, closed, seconds);
    }

    public uint Dpi => IsWindow(Window) ? GetDpiForWindow(Window) : 0;

    /// <summary>The image path and version of the process that owns the captured window (read only).</summary>
    public string WindowProcessImage()
    {
        try
        {
            GetWindowThreadProcessId(Window, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            string path = p.MainModule?.FileName ?? p.ProcessName;
            string? ver = p.MainModule?.FileVersionInfo.ProductVersion;
            return $"{path}{(ver != null ? " (" + ver + ")" : "")}";
        }
        catch (Exception ex) { return "unreadable: " + ex.Message; }
    }

    public void Dispose()
    {
        try { if (Child is { HasExited: false }) { Child.Kill(true); Child.WaitForExit(5000); } } catch { }
        try { if (_launcher is { HasExited: false }) { _launcher.Kill(true); _launcher.WaitForExit(5000); } } catch { }
        // A Windows Terminal window that stays open after its only tab ended (e.g. a non-zero exit) is
        // closed by message; it was verified to be this session's own new window.
        if (Host == WindowsTerminal && Window != IntPtr.Zero && IsWindow(Window))
        {
            PostMessageW(Window, 0x0010, 0, 0);
            for (int i = 0; i < 30 && IsWindow(Window); i++) Thread.Sleep(100);
        }
        Child?.Dispose();
        _launcher?.Dispose();
    }
}

/// <summary>The <c>inject</c> helper: attaches to one console and writes key records into its input buffer.</summary>
internal static class InputInjector
{
    public static ushort Scan(char upper) => upper switch
    {
        'A' => 0x1E, 'B' => 0x30, 'C' => 0x2E, 'D' => 0x20, 'E' => 0x12, 'F' => 0x21, 'G' => 0x22, 'H' => 0x23, 'I' => 0x17,
        'J' => 0x24, 'K' => 0x25, 'L' => 0x26, 'M' => 0x32, 'N' => 0x31, 'O' => 0x18, 'P' => 0x19, 'Q' => 0x10, 'R' => 0x13,
        'S' => 0x1F, 'T' => 0x14, 'U' => 0x16, 'V' => 0x2F, 'W' => 0x11, 'X' => 0x2D, 'Y' => 0x15, 'Z' => 0x2C, _ => 0,
    };

    public static int Run(uint pid, string tokens)
    {
        FreeConsole();
        if (!AttachConsole(pid)) { Console.Error.WriteLine($"AttachConsole({pid}) failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"); return 2; }
        IntPtr hin = OpenConsoleInput();
        try
        {
            foreach (string t in tokens.Split(','))
            {
                var recs = Records(t);
                if (!WriteConsoleInputW(hin, recs, (uint)recs.Length, out uint written) || written != recs.Length)
                { Console.Error.WriteLine($"WriteConsoleInputW failed for {t}"); return 2; }
                Thread.Sleep(250);
            }
            return 0;
        }
        finally { CloseHandle(hin); FreeConsole(); }
    }

    private static INPUT_RECORD Key(bool down, ushort vk, ushort scan, char ch, uint state) =>
        new() { EventType = 1, bKeyDown = down ? 1 : 0, wRepeatCount = 1, wVirtualKeyCode = vk, wVirtualScanCode = scan, UnicodeChar = ch, dwControlKeyState = state };

    private static INPUT_RECORD[] Records(string t)
    {
        const uint LeftAlt = 0x2, Enhanced = 0x100;
        if (t.StartsWith("ALT+", StringComparison.Ordinal) && t.Length == 5)
        {
            char u = t[4];
            ushort vk = u, sc = Scan(u);
            return [Key(true, 0x12, 0x38, '\0', LeftAlt), Key(true, vk, sc, char.ToLowerInvariant(u), LeftAlt), Key(false, vk, sc, char.ToLowerInvariant(u), LeftAlt), Key(false, 0x12, 0x38, '\0', 0)];
        }
        (ushort v, ushort s, char c, uint st) = t switch
        {
            "F10" => ((ushort)0x79, (ushort)0x44, '\0', 0u),
            "ENTER" => ((ushort)0x0D, (ushort)0x1C, '\r', 0u),
            "ESC" => ((ushort)0x1B, (ushort)0x01, '\x1b', 0u),
            "DOWN" => ((ushort)0x28, (ushort)0x50, '\0', Enhanced),
            "UP" => ((ushort)0x26, (ushort)0x48, '\0', Enhanced),
            _ when t.Length == 1 && char.IsLetter(t[0]) => ((ushort)char.ToUpperInvariant(t[0]), Scan(char.ToUpperInvariant(t[0])), char.ToLowerInvariant(t[0]), 0u),
            _ => throw new ArgumentException($"key {t}"),
        };
        return [Key(true, v, s, c, st), Key(false, v, s, c, st)];
    }
}
