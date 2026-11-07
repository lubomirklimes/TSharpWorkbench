using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TSharpVision.Drivers;
using TSharpVision.Drivers.Console;
using TSharpWorkbench.Testing;
using static TSharpWorkbench.Diagnostics.NativeHosts.Win32;

namespace TSharpWorkbench.Diagnostics.NativeHosts;

// Owns only an explicitly launched console lease. Scenario execution and authentication remain in Testing.
internal sealed class ApplicationExecution : INativeExecution
{
    private readonly Win32ConsoleDriver driver = new();
    private readonly ConsoleProbe probe = new();
    private readonly Process target = Process.GetCurrentProcess();
    private Process? launcher, lease;
    private IntPtr input, output, window;
    private readonly IntPtr originalInput = GetStdHandle(-10), originalOutput = GetStdHandle(-11);
    private readonly string directory;
    private bool attached;
    public IDriver Driver => driver;
    public string Routing => "WriteConsoleInputW(verified owned CONIN$) -> Win32ConsoleDriver.PumpMessages/ReadConsoleInputW -> production translation -> TScreen.GetEvent -> Commander";

    internal ApplicationExecution(int width, int height)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Console execution requires Windows.");
        // A sibling of fixture/state/profile, inside the controller's marked root. Even a
        // forced stop during console startup is covered by the existing owned-root cleanup.
        string state = Path.GetFullPath(Environment.GetEnvironmentVariable(TestTargetHost.StateVariable)
            ?? throw new InvalidOperationException("Owned diagnostic state root is missing."));
        directory = Path.Combine(Path.GetDirectoryName(state)!, "console-host");
        Directory.CreateDirectory(directory);
        try
        {
            // The controller creates a private no-window process. Refuse visible/shared inherited consoles.
            var members = new uint[8];
            uint count = GetConsoleProcessList(members, (uint)members.Length);
            if (GetConsoleWindow() != IntPtr.Zero || count > 1 || count == 1 && members[0] != Environment.ProcessId)
                throw new InvalidOperationException("Refused inherited/shared console; no input or capture performed.");
            FreeConsole();
            string executable = Path.ChangeExtension(typeof(ApplicationExecution).Assembly.Location, ".exe");
            // ShellExecute is intentional: the existing NativeHosts mechanism forces classic conhost.
            launcher = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                $"\"{executable}\" console-lease {Environment.ProcessId} \"{directory}\"") { UseShellExecute = true })
                ?? throw new InvalidOperationException("Owned conhost launch failed.");
            string ready = Path.Combine(directory, "lease.json");
            var clock = Stopwatch.StartNew();
            while (!File.Exists(ready) && !launcher.HasExited && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(25);
            if (!File.Exists(ready)) throw new TimeoutException("Owned console lease did not become ready.");
            using var json = JsonDocument.Parse(File.ReadAllText(ready));
            lease = Process.GetProcessById(json.RootElement.GetProperty("pid").GetInt32());
            if (!string.Equals(lease.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)
                || lease.StartTime.ToUniversalTime() < target.StartTime.ToUniversalTime())
                throw new InvalidOperationException("Console lease process identity mismatch.");
            if (!AttachConsole((uint)lease.Id)) throw new InvalidOperationException("Cannot attach the owned console lease.");
            attached = true;
            input = OpenConsoleInput(); output = OpenConsoleOutput();
            if (input == new IntPtr(-1) || output == new IntPtr(-1) || !SetStdHandle(-10, input) || !SetStdHandle(-11, output))
                throw new InvalidOperationException("Cannot install owned console handles.");
            window = GetConsoleWindow(); VerifyOwnership();
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            ConsoleSide.Configure(probe, $"{width}x{height}", "Consolas:16");
            ConsoleSide.Describe(probe);
            if (probe.SizeResult != "ok" || probe.WindowColumns != width || probe.WindowRows != height)
                throw new InvalidOperationException("Owned Console grid was not adopted: " + probe.SizeResult);
            driver.Initialize();
            if (!GetConsoleMode(input, out uint mode) || (mode & 0x206) != 0
                || !GetConsoleMode(output, out _) || Console.IsInputRedirected || Console.IsOutputRedirected
                || driver.GetCols() != width || driver.GetRows() != height)
                throw new InvalidOperationException("Production Console driver did not attach; no fallback allowed.");
        }
        catch { Dispose(); throw; }
    }

    private void VerifyOwnership()
    {
        var members = new uint[8];
        uint count = GetConsoleProcessList(members, (uint)members.Length);
        GetWindowThreadProcessId(window, out uint windowPid);
        if (lease is null || windowPid != lease.Id || lease.HasExited || launcher is null || launcher.HasExited || window == IntPtr.Zero
            || !IsWindow(window) || ClassName(window) != "ConsoleWindowClass" || GetConsoleWindow() != window
            || count != 2 || !members.Take(2).Order().SequenceEqual(new uint[] { (uint)lease.Id, (uint)Environment.ProcessId }.Order()))
            throw new InvalidOperationException("Owned classic console membership/window verification failed; no input or capture performed.");
    }

    public void Inject(string key)
    {
        VerifyOwnership();
        var (vk, scan, ch, state) = key switch
        {
            "kbF10" => (0x79, 0x44, '\0', 0u), "kbRight" => (0x27, 0x4d, '\0', 0x100u),
            "kbLeft" => (0x25, 0x4b, '\0', 0x100u), "kbDown" => (0x28, 0x50, '\0', 0x100u),
            "kbUp" => (0x26, 0x48, '\0', 0x100u), "kbEnter" => (0x0d, 0x1c, '\r', 0u),
            "kbEsc" => (0x1b, 1, '\x1b', 0u), "kbTab" => (9, 0x0f, '\t', 0u),
            "kbShiftF5" => (0x74, 0x3f, '\0', 0x10u), "kbShiftF6" => (0x75, 0x40, '\0', 0x10u),
            "kbAltW" => (0x57, 0x11, 'w', 2u), "kbAltX" => (0x58, 0x2d, 'x', 2u),
            _ => throw new InvalidOperationException("Unsupported native Console key: " + key)
        };
        var records = new[] { true, false }.Select(down => new INPUT_RECORD { EventType = 1, bKeyDown = down ? 1 : 0,
            wRepeatCount = 1, wVirtualKeyCode = (ushort)vk, wVirtualScanCode = (ushort)scan, UnicodeChar = ch, dwControlKeyState = state }).ToArray();
        if (!WriteConsoleInputW(input, records, 2, out uint written) || written != 2)
            throw new InvalidOperationException("Owned WriteConsoleInputW failed.");
    }

    public void InjectMouse(TEvent ev) => throw new InvalidOperationException("Console mouse application scenarios are unsupported.");

    public NativeCheckpoint Capture()
    {
        VerifyOwnership(); ConsoleSide.Describe(probe);
        // Real driver output buffer evidence is separate from PrintWindow host pixels.
        var readback = ConsoleSide.Readback() ?? throw new InvalidOperationException("Console output readback unavailable.");
        Thread.Sleep(120);
        var frame = WindowCapture.Capture(window) ?? throw new InvalidOperationException("Owned host-window capture unavailable.");
        if (frame.Rgba.Where((_, i) => i % 4 != 3).Distinct().Take(2).Count() < 2)
            throw new InvalidOperationException("Owned host-window capture is blank; no logical fallback allowed.");
        GetWindowThreadProcessId(window, out uint windowPid);
        var facts = new Dictionary<string, string>
        {
            ["driver"] = nameof(Win32ConsoleDriver), ["driverType"] = driver.GetType().AssemblyQualifiedName!,
            ["host"] = "classic conhost (explicit test-owned console lease)", ["hostExecutable"] = Path.Combine(Environment.SystemDirectory, "conhost.exe"),
            ["hostLauncherPid"] = launcher!.Id.ToString(), ["hostVersion"] = FileVersionInfo.GetVersionInfo(launcher.MainModule!.FileName!).FileVersion!,
            ["hostLauncherExecutable"] = launcher.MainModule.FileName!, ["hostStartedUtc"] = launcher.StartTime.ToUniversalTime().ToString("O"), ["leasePid"] = lease!.Id.ToString(), ["processId"] = Environment.ProcessId.ToString(),
            ["hwnd"] = window.ToInt64().ToString(), ["windowProcessId"] = windowPid.ToString(), ["windowClass"] = ClassName(window),
            ["executable"] = Environment.ProcessPath!, ["assemblyPath"] = System.Reflection.Assembly.GetEntryAssembly()!.Location,
            ["captureBoundary"] = WindowCapture.Mechanism, ["dpi"] = GetDpiForWindow(window).ToString(),
            ["os"] = RuntimeInformation.OSDescription, ["font"] = probe.FontFace, ["fontWeight"] = probe.FontWeight.ToString(),
            ["fontCellWidth"] = probe.FontWidth.ToString(), ["fontCellHeight"] = probe.FontHeight.ToString(),
            ["cellWidth"] = (frame.Width / probe.WindowColumns).ToString(), ["cellHeight"] = (frame.Height / probe.WindowRows).ToString(),
            ["palette"] = string.Join(",", probe.ColorTable), ["screenGrid"] = $"{probe.WindowColumns}x{probe.WindowRows}",
            ["outputReadbackCellsSha256"] = ScreenSnapshot.Hash(System.Text.Encoding.UTF8.GetBytes(readback.Chars)),
            ["inputProof"] = "Programmatic KEY_EVENT_RECORD injection into private CONIN$; production driver consumption and subsequent empty framework probe; not physical keyboard qualification"
        };
        return new(Convert.ToBase64String(frame.EncodePng()), ScreenSnapshot.Hash(frame.Rgba), "host-window", frame.Width, frame.Height, facts);
    }

    public void Dispose()
    {
        driver.Shutdown();
        if (attached)
        {
            SetStdHandle(-10, originalInput); SetStdHandle(-11, originalOutput);
            if (input != IntPtr.Zero && input != new IntPtr(-1)) CloseHandle(input);
            if (output != IntPtr.Zero && output != new IntPtr(-1)) CloseHandle(output);
            FreeConsole(); attached = false;
        }
        File.WriteAllText(Path.Combine(directory, "stop"), "stop");
        if (lease is not null && !lease.WaitForExit(5000)) { lease.Kill(); lease.WaitForExit(5000); }
        if (launcher is not null && !launcher.WaitForExit(5000)) { launcher.Kill(); launcher.WaitForExit(5000); }
        if (window != IntPtr.Zero && IsWindow(window)) throw new InvalidOperationException("Owned console window remained after cleanup.");
        lease?.Dispose(); launcher?.Dispose(); target.Dispose();
        Directory.Delete(directory, true);
    }

    internal static int Lease(int targetPid, string directory)
    {
        // No application, scenario input, pipe secret or commands: simply keep this owned console alive.
        using var parent = Process.GetProcessById(targetPid);
        Json.Write(Path.Combine(directory, "lease.json"), new { pid = Environment.ProcessId });
        var clock = Stopwatch.StartNew();
        while (!parent.HasExited && !File.Exists(Path.Combine(directory, "stop")) && clock.Elapsed < TimeSpan.FromMinutes(11)) Thread.Sleep(100);
        return 0;
    }

    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int kind, IntPtr handle);
}
