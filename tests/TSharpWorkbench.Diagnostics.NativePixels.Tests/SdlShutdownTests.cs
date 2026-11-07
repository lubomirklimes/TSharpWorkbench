using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativePixels.Tests;

/// <summary>
/// A real TSharpVision application on a real SDL window (the diagnostics executable's
/// <c>lifecycle</c> child, run through <see cref="AppLifecycleGuard"/>) must terminate when exit is requested
/// from outside through genuine window messages: WM_CLOSE (SDL_EVENT_WINDOW_CLOSE_REQUESTED) and Alt+X key
/// messages (SDL keyboard → kbAltX → status line → cmQuit). Evidence is the child's own exit code within a
/// bound, its post-shutdown report, and the absence of surviving child processes. Only test-owned windows
/// receive messages; nothing is injected globally.
/// </summary>
[Collection(RealRendererCollection.Name)]
public sealed class SdlShutdownTests
{
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(10);

    [SkippableTheory]
    [InlineData("sdl-2d")]
    [InlineData("sdl-gpu")]
    [InlineData("sdl-gpu-fallback")]
    public void Native_window_close_terminates_the_application(string driver)
    {
        using var child = LifecycleProcess.Start(driver, nameof(Native_window_close_terminates_the_application));
        child.PostClose();
        var report = child.AssertExitedCleanly(ExitBound);
        Assert.Equal(1, report.QuitRequests);
        if (driver == "sdl-gpu-fallback") Assert.False(child.Ready.ShaderPipelineActive, "fallback path must not use the shader pipeline");
        if (driver == "sdl-gpu") Assert.True(child.Ready.ShaderPipelineActive, "GPU path must use the shader pipeline");
    }

    [SkippableTheory]
    [InlineData("sdl-2d")]
    [InlineData("sdl-gpu")]
    [InlineData("sdl-gpu-fallback")]
    public void Alt_X_exit_command_terminates_the_application(string driver)
    {
        using var child = LifecycleProcess.Start(driver, nameof(Alt_X_exit_command_terminates_the_application));
        child.PostAltX();
        var report = child.AssertExitedCleanly(ExitBound);
        Assert.Equal(1, report.QuitRequests);
    }

    [SkippableTheory]
    [InlineData("sdl-2d")]
    [InlineData("sdl-gpu")]
    public void Exit_after_a_cancelled_modal_dialog_terminates_the_application(string driver)
    {
        using var child = LifecycleProcess.Start(driver, nameof(Exit_after_a_cancelled_modal_dialog_terminates_the_application), "--modal");
        Thread.Sleep(400);
        child.PostKey(0x1B, 0x01, alt: false); // Esc cancels the dialog (established dialog behaviour)
        Thread.Sleep(400);
        Assert.False(child.Process.HasExited, "Esc must close the dialog, not the application");
        child.PostAltX();
        var report = child.AssertExitedCleanly(ExitBound);
        Assert.True(report.ModalOpened);
        Assert.Equal(TSharpVision.Constants.Views.cmCancel, report.ModalResult);
    }

    [SkippableTheory]
    [InlineData("sdl-2d")]
    [InlineData("sdl-gpu")]
    public void Native_close_while_a_modal_dialog_is_open_terminates_the_application(string driver)
    {
        using var child = LifecycleProcess.Start(driver, nameof(Native_close_while_a_modal_dialog_is_open_terminates_the_application), "--modal");
        Thread.Sleep(400);
        child.PostClose();
        var report = child.AssertExitedCleanly(ExitBound);
        Assert.True(report.ModalOpened);
    }

    [SkippableTheory]
    [InlineData("sdl-2d")]
    [InlineData("sdl-gpu")]
    public void A_vetoed_close_keeps_running_and_the_next_close_exits(string driver)
    {
        using var child = LifecycleProcess.Start(driver, nameof(A_vetoed_close_keeps_running_and_the_next_close_exits), "--veto");
        child.PostClose();
        Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(child.Dir, "vetoed.txt")), 5000), "close request never reached the application's Valid(cmQuit)");
        Thread.Sleep(500);
        Assert.False(child.Process.HasExited, "a vetoed close must not terminate the application");
        child.PostClose();
        var report = child.AssertExitedCleanly(ExitBound);
        Assert.Equal(1, report.VetoedQuits);
        Assert.Equal(2, report.QuitRequests);
    }

    [SkippableTheory]
    [InlineData("sdl-2d")]
    [InlineData("sdl-gpu")]
    public void Native_close_after_a_resize_terminates_the_application(string driver)
    {
        using var child = LifecycleProcess.Start(driver, nameof(Native_close_after_a_resize_terminates_the_application));
        child.Resize(700, 450);
        Thread.Sleep(500);
        child.Resize(1300, 800);
        Thread.Sleep(500);
        child.PostClose();
        child.AssertExitedCleanly(ExitBound);
    }

    [SkippableTheory]
    [InlineData("sdl-2d")]
    [InlineData("sdl-gpu")]
    public void Native_close_while_the_renderer_is_saturated_terminates_the_application(string driver)
    {
        using var child = LifecycleProcess.Start(driver, nameof(Native_close_while_the_renderer_is_saturated_terminates_the_application), "--busy");
        Thread.Sleep(800);
        child.PostClose();
        var report = child.AssertExitedCleanly(ExitBound);
        Assert.True(report.BusyFrames > 10, $"renderer was not kept busy ({report.BusyFrames} frames)");
    }

    [SkippableFact]
    public void Repeated_startup_and_shutdown_terminates_every_time()
    {
        for (int i = 0; i < 3; i++)
            foreach (string driver in new[] { "sdl-gpu", "sdl-2d" })
            {
                using var child = LifecycleProcess.Start(driver, $"{nameof(Repeated_startup_and_shutdown_terminates_every_time)}-{i}");
                if (i % 2 == 0) child.PostClose(); else child.PostAltX();
                child.AssertExitedCleanly(ExitBound);
            }
    }
}

/// <summary>A test-owned lifecycle child, its window and its output directory. Dispose kills only this process tree.</summary>
internal sealed class LifecycleProcess : IDisposable
{
    public Process Process { get; }
    public string Dir { get; }
    public IntPtr Hwnd { get; }
    public LifecycleReport Ready { get; }
    private readonly Task<string> _stderr, _stdout;

    private LifecycleProcess(Process p, string dir, LifecycleReport ready, Task<string> stdout, Task<string> stderr)
    { Process = p; Dir = dir; Ready = ready; Hwnd = new IntPtr(ready.Hwnd); _stdout = stdout; _stderr = stderr; }

    public static LifecycleProcess Start(string driver, string test, params string[] flags)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "native window messages are Windows-specific");
        string dir = Path.Combine(RealRendererFixture.OutputRoot, "lifecycle", $"{test}-{driver}-{Guid.NewGuid():N}"[..Math.Min(90, test.Length + driver.Length + 34)]);
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(AppContext.BaseDirectory, "TSharpWorkbench.Diagnostics.NativePixels.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (string a in new[] { "lifecycle", driver, dir }.Concat(flags)) psi.ArgumentList.Add(a);
        psi.Environment.Remove("TSHARPVISION_DRIVER");
        if (flags.Contains("--busy")) psi.Environment["TSHARPVISION_SDLGPU_CONTINUOUS"] = "1";
        var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        string ready = Path.Combine(dir, LifecycleChild.ReadyName);
        if (!SpinWait.SpinUntil(() => File.Exists(ready) || p.HasExited, 20000) || p.HasExited)
        {
            string log = p.HasExited ? stderr.Result : "";
            if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); }
            throw new Xunit.Sdk.XunitException($"lifecycle child did not become ready (exit {(p.HasExited ? p.ExitCode : -1)}): {log}");
        }
        LifecycleReport r = ReadJson(ready);
        Assert.NotEqual(0, r.Hwnd);
        Assert.True(Native.IsWindow(new IntPtr(r.Hwnd)));
        Thread.Sleep(200); // first frame presented
        return new LifecycleProcess(p, dir, r, stdout, stderr);
    }

    private static LifecycleReport ReadJson(string file)
    {
        for (int i = 0; ; i++)
        {
            try { return JsonSerializer.Deserialize<LifecycleReport>(File.ReadAllText(file), ChildRunner.Json)!; }
            catch (Exception) when (i < 20) { Thread.Sleep(50); }
        }
    }

    public void PostClose() => Assert.True(Native.PostMessage(Hwnd, 0x0010, 0, 0), "WM_CLOSE not posted");

    public void PostAltX()
    {
        Native.PostMessage(Hwnd, 0x0104, 0x12, 0x20380001); Thread.Sleep(40);           // WM_SYSKEYDOWN Alt
        PostKey(0x58, 0x2D, alt: true);                                               // X with Alt held
        Native.PostMessage(Hwnd, 0x0101, 0x12, unchecked((nint)0xC0380001)); Thread.Sleep(40); // WM_KEYUP Alt
    }

    public void PostKey(int vk, int scan, bool alt)
    {
        int ctx = alt ? 0x20000000 : 0;
        Native.PostMessage(Hwnd, alt ? 0x0104u : 0x0100u, vk, 1 | (scan << 16) | ctx); Thread.Sleep(40);
        Native.PostMessage(Hwnd, alt ? 0x0105u : 0x0101u, vk, unchecked((nint)(uint)(1 | (scan << 16) | ctx | unchecked((int)0xC0000000)))); Thread.Sleep(40);
    }

    public void Resize(int w, int h) => Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, w, h, 0x0002 | 0x0004 | 0x0010);

    public LifecycleReport AssertExitedCleanly(TimeSpan bound)
    {
        var children = Native.ChildProcessIds(Process.Id);
        bool exited = Process.WaitForExit((int)bound.TotalMilliseconds);
        Assert.True(exited, $"application did not terminate within {bound.TotalSeconds}s after the exit request");
        Process.WaitForExit();
        Assert.True(Process.ExitCode == 0, $"exit code {Process.ExitCode}: {_stderr.Result}");
        foreach (int id in children)
        {
            try { using var c = Process.GetProcessById(id); Assert.True(c.HasExited || !IsDescendantOf(c, Process.Id), $"child process {id} ({c.ProcessName}) survived"); }
            catch (ArgumentException) { } // already gone
        }
        Assert.False(Native.IsWindow(Hwnd), "SDL window still exists after exit");
        LifecycleReport report = ReadJson(Path.Combine(Dir, LifecycleChild.ReportName));
        Assert.True(report.LoopExited, report.LoopError);
        Assert.True(report.DriverDetached, "driver shutdown did not release renderer and window");
        Assert.True(report.SdlQuit, "SDL video subsystem still initialized after shutdown");
        return report;
    }

    // conhost is created per console client and ends with it; a PID reused by an unrelated process is not ours.
    private static bool IsDescendantOf(Process c, int parent) => Native.ParentProcessId(c.Id) == parent;

    public void Dispose()
    {
        try { if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(5000); } } catch { }
        Process.Dispose();
    }
}

internal static class Native
{
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr s, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr s, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static List<(int Id, int Parent)> Snapshot()
    {
        var list = new List<(int, int)>();
        IntPtr s = CreateToolhelp32Snapshot(0x2, 0);
        if (s == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32FirstW(s, ref e); ok; ok = Process32NextW(s, ref e)) list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID));
        }
        finally { CloseHandle(s); }
        return list;
    }

    public static List<int> ChildProcessIds(int parent) => Snapshot().Where(x => x.Parent == parent).Select(x => x.Id).ToList();
    public static int ParentProcessId(int id) => Snapshot().FirstOrDefault(x => x.Id == id).Parent;
}
