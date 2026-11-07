using System.Text.Json;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpVision.Drivers.SDL;
using TSharpVision.Drivers.SDL.Gpu;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>What one lifecycle child observed, written after the real event loop and driver shutdown returned.</summary>
public sealed class LifecycleReport
{
    public string Driver { get; set; } = "";
    public long Hwnd { get; set; }
    public bool ShaderPipelineActive { get; set; }
    public int QuitRequests { get; set; }
    public int VetoedQuits { get; set; }
    public bool ModalOpened { get; set; }
    public ushort ModalResult { get; set; }
    public bool LoopExited { get; set; }
    public string? LoopError { get; set; }
    /// <summary>Renderer released and window destroyed. A GPU worker that failed to join makes the renderer's
    /// Dispose throw, which leaves the window attached, so this also proves the render thread stopped.</summary>
    public bool DriverDetached { get; set; }
    public bool SdlQuit { get; set; }
    public int BusyFrames { get; set; }
}

/// <summary>
/// A minimal real TSharpVision application on a real SDL window, run through the production
/// <see cref="AppLifecycleGuard"/> (event loop, <c>ShutDown</c>, <c>IDriver.Shutdown</c>). The test process
/// owns this child, requests exit from outside through genuine Win32 window messages (WM_CLOSE, Alt+X
/// key messages) and proves termination by the process exit code and this report.
/// </summary>
/// <remarks>
/// Usage: <c>lifecycle &lt;sdl-2d|sdl-gpu|sdl-gpu-fallback&gt; &lt;dir&gt; [--modal] [--veto] [--busy]</c>.
/// <c>ready.json</c> (window handle) is written on the first idle; <c>lifecycle.json</c> after shutdown.
/// </remarks>
public static class LifecycleChild
{
    public const string ReadyName = "ready.json", ReportName = "lifecycle.json";
    private const ushort cmOpenModal = 0x7e10;

    public static int Run(string driverId, string dir, ISet<string> flags)
    {
        Directory.CreateDirectory(dir);
        var report = new LifecycleReport { Driver = driverId };
        IDriver driver = driverId switch
        {
            "sdl-2d" => new SDLDriver(),
            "sdl-gpu" => new SDLGpuDriver(),
            "sdl-gpu-fallback" => new SDLGpuDriver { SimulateShaderPipelineFailureForTesting = true },
            _ => throw new ArgumentException($"unknown lifecycle driver {driverId}"),
        };
        StreamableRegistration.RegisterAll();
        TSharpVisionRuntime.Configure("Lifecycle");
        driver.Initialize();
        TDisplay.driver = driver;
        SDLGpuRenderer? gpu = (driver as SDLGpuDriver)?.RendererForTesting;
        report.ShaderPipelineActive = gpu?.ShaderPipelineActive ?? false;
        IntPtr window = driver is SDLGpuDriver g ? g.WindowForTesting : ((SDLDriver)driver).WindowForTesting;
        report.Hwnd = SDL3.SDL.GetPointerProperty(SDL3.SDL.GetWindowProperties(window), SDL3.SDL.Props.WindowWin32HWNDPointer, IntPtr.Zero).ToInt64();

        var app = new App(report, flags, dir);
        try
        {
            // The production entry point: Run, then app.ShutDown and TDisplay.driver.Shutdown in its finally.
            int code = AppLifecycleGuard.Run(app);
            report.LoopExited = code == 0;
        }
        catch (Exception ex) { report.LoopError = ex.ToString(); }

        report.DriverDetached = driver is SDLGpuDriver gd ? gd.RendererForTesting == null && gd.WindowForTesting == IntPtr.Zero
                                                          : ((SDLDriver)driver).RendererForTesting == null && ((SDLDriver)driver).WindowForTesting == IntPtr.Zero;
        report.SdlQuit = SDL3.SDL.WasInit(SDL3.SDL.InitFlags.Video) == 0;
        File.WriteAllText(Path.Combine(dir, ReportName), JsonSerializer.Serialize(report, ChildRunner.Json));
        return report.LoopExited && report.LoopError is null ? 0 : 2;
    }

    private sealed class BusyText(TRect bounds) : TStaticText(bounds, "busy")
    {
        internal void Set(string text) { Text = text; DrawView(); }
    }

    private sealed class App : TApplication
    {
        private readonly LifecycleReport _report;
        private readonly ISet<string> _flags;
        private readonly string _dir;
        private bool _ready;
        private int _tick;
        private BusyText? _busy;

        public App(LifecycleReport report, ISet<string> flags, string dir)
        {
            _report = report; _flags = flags; _dir = dir;
            if (flags.Contains("--busy") && DeskTop != null)
            {
                _busy = new BusyText(new TRect(2, 2, 40, 3));
                DeskTop.Insert(_busy);
            }
            if (flags.Contains("--modal")) Post(cmOpenModal);
        }

        public override TStatusLine InitStatusLine(TRect r)
        {
            r.a.y = r.b.y - 1;
            return new TStatusLine(r, new TStatusDef(0, 0xFFFF) + new TStatusItem("~Alt+X~ Exit", Keys.kbAltX, Views.cmQuit));
        }

        public override TMenuBar InitMenuBar(TRect r)
        {
            r.b.y = r.a.y + 1;
            return new TMenuBar(r, new TSubMenu("~F~ile", Keys.kbAltF) + new TMenuItem("E~x~it", Views.cmQuit, Keys.kbAltX, 0, "Alt+X"));
        }

        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evCommand && ev.message.command == Views.cmQuit) _report.QuitRequests++;
            if (ev.What == Events.evCommand && ev.message.command == cmOpenModal)
            {
                ClearEvent(ref ev);
                var dlg = new TDialog(new TRect(10, 5, 50, 13), "Modal");
                dlg.Insert(new TButton(new TRect(14, 4, 26, 6), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
                _report.ModalOpened = true;
                MarkReady();
                _report.ModalResult = ExecuteDialog(dlg);
                return;
            }
            base.HandleEvent(ref ev);
        }

        // The existing close policy: a view that is not valid for cmQuit keeps the application running.
        public override bool Valid(ushort command)
        {
            if (command == Views.cmQuit && _flags.Contains("--veto") && _report.VetoedQuits == 0)
            {
                _report.VetoedQuits++;
                File.WriteAllText(Path.Combine(_dir, "vetoed.txt"), "1");
                return false;
            }
            return base.Valid(command);
        }

        public override void Idle()
        {
            base.Idle();
            if (_busy != null)
            {
                // Keep the renderer saturated: new content every idle pass.
                _busy.Set($"busy frame {++_tick}");
                _report.BusyFrames = _tick;
            }
            if (!_ready && !_flags.Contains("--modal")) MarkReady();
        }

        private void MarkReady()
        {
            if (_ready) return;
            _ready = true;
            File.WriteAllText(Path.Combine(_dir, ReadyName), JsonSerializer.Serialize(_report, ChildRunner.Json));
        }
    }
}
