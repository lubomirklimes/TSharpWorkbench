using System.Diagnostics;
using System.Globalization;
using SDL3;
using TSharpVision.Drivers;
using TSharpVision.Drivers.SDL.Gpu;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Diagnostics.NativePixels;

// Attaches only to the actual application's driver. All GPU work stays on its existing worker.
internal sealed class GpuApplicationExecution : INativeExecution
{
    private readonly SDLGpuDriver driver = new();
    private readonly Dictionary<string,string> identity = new();
    private readonly int cellWidth, cellHeight;
    private long lastFrame, lastPresented;
    public IDriver Driver => driver;
    public string Routing => "SDL_PushEvent(verified target window ID) -> SDLGpuDriver.PumpMessages/SDL_PollEvent -> production translation -> TScreen.GetEvent -> Commander";
    internal GpuApplicationExecution(int width, int height, string mode)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Commander GPU qualification requires Windows.");
        if (!NativeExecution.IsGpu(mode)) throw new InvalidOperationException("Unsupported GPU mode.");
        try
        {
            SDL.SetHint(SDL.Hints.VideoDriver, "windows");
            Environment.SetEnvironmentVariable("TSHARPVISION_SDLGPU_BACKEND", NativeExecution.Backend(mode));
            FontSpec.Default.Apply();
            ScreenDriverFactory.WindowTitle = "Owned Commander " + Environment.ProcessId;
            driver.SimulateShaderPipelineFailureForTesting = NativeExecution.GpuPath(mode) == "cpu-composite";
            driver.Initialize();
            var renderer = driver.RendererForTesting ?? throw new InvalidOperationException("GPU driver detached.");
            if (renderer.Backend != NativeExecution.Backend(mode) || renderer.ShaderPipelineActive != (NativeExecution.GpuPath(mode) == "shader")
                || SDL.GetCurrentVideoDriver() != "windows") throw new InvalidOperationException("Actual GPU backend/path differs from requested mode.");
            cellWidth = renderer.CellWidth; cellHeight = renderer.CellHeight;
            SDL.SetWindowSize(driver.WindowForTesting, width * cellWidth, height * cellHeight);
            SDL.SyncWindow(driver.WindowForTesting);
            var limit = Stopwatch.StartNew();
            while (driver.GetCols() != width || driver.GetRows() != height)
            {
                driver.PumpMessages();
                if (limit.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("GPU initial grid was not adopted.");
                Thread.Sleep(5);
            }
            // Only before application construction, never during an application capture.
            NativeSupport.DrainEvents();
            uint props = SDL.GetGPUDeviceProperties(renderer.DeviceForTesting);
            string name = SDL.GetStringProperty(props, "SDL.gpu.device.name", "") ?? "";
            identity["gpu.adapterKind"] = SdlGpuPath.ClassifyAdapter(name);
            if (identity["gpu.adapterKind"] != "hardware adapter") throw new InvalidOperationException("GPU hardware identity not proven.");
            identity["gpu.device.name"] = name;
            foreach (string property in new[] { "driver_name", "driver_version", "driver_info" })
                identity["gpu.device." + property] = SDL.GetStringProperty(props, "SDL.gpu.device." + property, "") ?? "";
            identity["driver"] = nameof(SDLGpuDriver); identity["requestedMode"] = mode;
            identity["backend"] = renderer.Backend; identity["renderingPath"] = NativeExecution.GpuPath(mode);
            identity["pathSelection"] = driver.SimulateShaderPipelineFailureForTesting ? "explicit diagnostic simulation of pipeline construction failure; real production CPU fallback" : "explicit shader request; construction fallback refused";
            identity["gpu.shaderPipelineActive"] = renderer.ShaderPipelineActive.ToString();
            identity["videoDriver"] = SDL.GetCurrentVideoDriver()!; identity["sdlVersion"] = Sdl2dPath.SdlVersion();
            identity["processId"] = Environment.ProcessId.ToString(); identity["executable"] = Environment.ProcessPath ?? "unknown";
            identity["assemblyPath"] = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "unknown";
            identity["windowId"] = SDL.GetWindowID(driver.WindowForTesting).ToString();
            identity["hwnd"] = SDL.GetPointerProperty(SDL.GetWindowProperties(driver.WindowForTesting), SDL.Props.WindowWin32HWNDPointer, IntPtr.Zero).ToInt64().ToString();
            identity["font"] = FontSpec.Default.Path; identity["fontSize"] = FontSpec.Default.PointSize.ToString();
            identity["cellWidth"] = cellWidth.ToString(); identity["cellHeight"] = cellHeight.ToString();
            NativeSupport.FontFit(FontSpec.Default, driver.FontFit.ToString(), renderer.GlyphsForTesting, identity);
            identity["captureBoundary"] = "Render-worker rerender of actual submitted Commander application snapshot into readable GPU texture of swapchain format; production shader or CPU fallback; DownloadFromGPUTexture + bounded fence. Not swapchain, compositor or monitor readback.";
            identity["inputProof"] = "Programmatic target-window SDL events and production GPU driver translation; not physical input";
        }
        catch { driver.Shutdown(); throw; }
    }
    private IntPtr Window()
    {
        var window = driver.WindowForTesting;
        if (window == IntPtr.Zero || SDL.GetWindowID(window).ToString() != identity["windowId"])
            throw new InvalidOperationException("Owned GPU window identity changed.");
        return window;
    }
    public void Inject(string key) => ApplicationExecution.InjectKey(Window(), key);
    public void InjectMouse(TEvent ev) => ApplicationExecution.InjectPointer(Window(), cellWidth, cellHeight, ev);
    public NativeCheckpoint Capture()
    {
        var window = Window();
        var renderer = driver.RendererForTesting ?? throw new InvalidOperationException("GPU renderer detached.");
        SDL.GetWindowSizeInPixels(window, out int width, out int height);
        if (width != TScreen.ScreenWidth * cellWidth || height != TScreen.ScreenHeight * cellHeight)
            throw new InvalidOperationException("GPU window/grid geometry changed.");
        var pending = renderer.RequestCaptureForTesting((uint)width, (uint)height, TimeSpan.FromSeconds(3));
        // The installed callback submits the driver's own live application buffer. No logical replay.
        driver.MessageLoop!(renderer);
        if (!pending.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("GPU worker capture timed out.");
        var frame = pending.GetAwaiter().GetResult();
        if (frame.Backend != identity["backend"] || frame.Mode != identity["renderingPath"]
            || frame.Frame <= lastFrame || frame.FramesPresented <= lastPresented
            || frame.Columns != TScreen.ScreenWidth || frame.Rows != TScreen.ScreenHeight
            || frame.Width != width || frame.Height != height)
            throw new InvalidOperationException("GPU capture submission/backend/path/grid provenance mismatch.");
        lastFrame = frame.Frame; lastPresented = frame.FramesPresented;
        var details = new Dictionary<string,string>(identity);
        NativeSupport.WindowMetrics(window, details);
        details["screenGrid"] = $"{frame.Columns}x{frame.Rows}";
        details["gpu.frame"] = frame.Frame.ToString(); details["gpu.nativeFormat"] = frame.NativeFormat;
        details["gpu.fenceWaitMs"] = frame.GpuWait.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture);
        details["swapchain.framesPresented"] = frame.FramesPresented.ToString();
        details["swapchain.framesSkipped"] = frame.FramesSkipped.ToString();
        var image = new NativeImage(width, height, frame.Rgba);
        return new(Convert.ToBase64String(image.EncodePng()), ScreenSnapshot.Hash(frame.Rgba), "renderer-target", width, height, details);
    }
    public void Dispose()
    {
        var renderer = driver.RendererForTesting;
        driver.Shutdown(); // bounded worker join precedes texture/device/window release
        if (driver.RendererForTesting is not null || driver.WindowForTesting != IntPtr.Zero
            || renderer?.DeviceForTesting is not (null or 0) || SDL.WasInit(0) != 0 || TTF.WasInit() != 0)
            throw new InvalidOperationException("GPU renderer/device/window/SDL cleanup incomplete.");
        Console.Error.WriteLine(NativeExecution.GpuCleanupMarker);
    }
}
