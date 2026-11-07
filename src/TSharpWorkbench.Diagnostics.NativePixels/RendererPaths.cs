using System.Runtime.InteropServices;
using TSharpVision.Config;
using TSharpVision.Constants;
using TSharpWorkbench.Diagnostics.DriverOutput;
using TSharpVision.Drivers;
using TSharpVision.Drivers.SDL;
using TSharpVision.Drivers.SDL.Gpu;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>Font the qualification pins so captures are reproducible on any Windows machine.</summary>
/// <param name="Path">Font file the oracle rasterises (and the drivers load).</param>
/// <param name="PointSize">Configured <c>[graphics] fontSize</c>.</param>
/// <param name="Fit"><c>[graphics] fontFit</c> as configured: Condense or Uniform.</param>
/// <param name="ConfigFile">
/// A real .cfg file the drivers get their configuration from, through the normal
/// <see cref="TSharpVisionConfigurationLoader"/>. Null = the native-pixel in-memory configuration.
/// </param>
/// <param name="Id">Short font id used in artifact names (consolas20).</param>
/// <param name="Programmatic">
/// No .cfg file; <paramref name="Fit"/> is set in code as
/// <see cref="GraphicsDriverConfiguration.FontFit"/> of the in-memory configuration.
/// </param>
public sealed record FontSpec(string Path, int PointSize, string Fit = "Condense", string? ConfigFile = null, string? Id = null, bool Programmatic = false)
{
    /// <summary>Consolas ships with every Windows 10/11 installation; 20 pt is the drivers' default size.</summary>
    public static FontSpec Default => new(System.IO.Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts", "consola.ttf"), 20, Id: "consolas20");

    /// <summary>Fonts a run can select by id: file name (resolved like <c>[graphics] fontName</c>) and size.</summary>
    public static readonly Dictionary<string, (string FontName, int Size)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["consolas20"] = ("consola.ttf", 20),
        ["jetbrains20"] = ("JetBrainsMono-Regular.ttf", 20),
        ["cascadia20"] = ("CascadiaMono.ttf", 20),
    };

    /// <summary>Whether the font <paramref name="id"/> of <see cref="Known"/> is installed on this machine.</summary>
    public static bool IsInstalled(string id) =>
        TSharpVision.Drivers.SDL.Rendering.Fonts.SdlFontLocator.ProbeFontPathByName(Known[id].FontName) != null;

    public bool Uniform => string.Equals(Fit, "Uniform", StringComparison.OrdinalIgnoreCase);

    /// <summary>Artifact name prefix: <c>uniform--consolas20--</c> for a variant, empty for the native-pixel default.</summary>
    public string Prefix =>
        Programmatic ? $"programmatic-{Fit.ToLowerInvariant()}--{Id}--"
        : ConfigFile == null ? "" : $"{Fit.ToLowerInvariant()}--{Id}--";

    /// <summary>The .cfg text for <paramref name="fontName"/>, <paramref name="size"/> and <paramref name="fit"/> (the documented syntax).</summary>
    public static string ConfigText(string fontName, int size, string fit) =>
        $"# Native-pixel variant (generated)\n[driver]\nname=sdl\n\n[graphics]\nfontName={fontName}\nfontSize={size}\nfontFit={fit}\n";

    /// <summary>Reads a variant back from its .cfg file through the product's configuration loader.</summary>
    /// A cfg the loader rejects (invalid fontFit) yields the default font with the file kept, so the
    /// path's own <see cref="Apply"/> reloads it and reports the loader's error as unavailable. Its
    /// verbatim value only names the artifacts (<see cref="Prefix"/>); no driver ever sees it.
    public static FontSpec FromConfigFile(string file, string id)
    {
        TSharpVisionConfiguration cfg;
        try { cfg = TSharpVisionConfigurationLoader.LoadFromPath(file); }
        catch (InvalidOperationException)
        {
            string verbatim = IniConfigurationReader.Parse(File.ReadAllText(file)).Get("graphics", "fontFit")?.Trim() ?? "invalid";
            return Default with { Fit = verbatim, ConfigFile = file, Id = id };
        }
        string fontName = cfg.Graphics.FontName ?? throw new InvalidOperationException($"{file}: no [graphics] fontName");
        string path = TSharpVision.Drivers.SDL.Rendering.Fonts.SdlFontLocator.ProbeFontPathByName(fontName)
            ?? throw new InvalidOperationException($"{file}: font '{fontName}' is not installed");
        return new FontSpec(path, cfg.Graphics.FontSize ?? 20, cfg.Graphics.FontFit.ToString(), file, id);
    }

    /// <summary>The pinned font with <paramref name="fit"/> selected in code, no .cfg file.</summary>
    public static FontSpec ProgrammaticFit(string fit) =>
        Default with { Fit = Enum.Parse<FontFitMode>(fit, ignoreCase: true).ToString(), Programmatic = true };

    public void Apply() => ScreenDriverFactory.Configuration = ConfigFile != null
        ? TSharpVisionConfigurationLoader.LoadFromPath(ConfigFile)
        : new TSharpVisionConfiguration
        {
            Graphics = new GraphicsDriverConfiguration
            {
                FontName = Path,
                FontSize = PointSize,
                FontFit = Enum.Parse<FontFitMode>(Fit, ignoreCase: true),
            },
        };
}

/// <summary>Pixels read back from a frame a real renderer produced, plus how they were obtained.</summary>
public sealed class NativeCapture
{
    public required NativeImage Image { get; init; }
    public required string NativeFormat { get; init; }
    public required string Method { get; init; }
    public long Frame { get; init; }
    public Dictionary<string, string> Details { get; } = new();
}

/// <summary>One real SDL rendering path, driven through its production driver.</summary>
public interface INativePath : IDisposable
{
    string Id { get; }
    string Description { get; }
    /// <summary>The exact stage whose pixels are read back.</summary>
    string CaptureBoundary { get; }
    /// <summary>Null when usable; otherwise PlatformUnavailable / BackendUnavailable with the reason.</summary>
    string? Unavailable { get; }
    int CellWidth { get; }
    int CellHeight { get; }
    FontGlyphPlacement Placement { get; }
    /// <summary>Largest per-channel deviation accepted on partial-coverage pixels.</summary>
    int PartialTolerance { get; }
    Dictionary<string, string> Identity { get; }
    /// <summary>Resizes the real window to <paramref name="grid"/> and waits until the driver adopted it.</summary>
    void EnsureGrid(GridSize grid, List<string> checks, List<string> failures);
    void Write(CellWrite write);
    void SetCaret(CaretRequest caret);
    /// <summary>Renders one frame through the driver and returns its pixels.</summary>
    NativeCapture Capture();
    /// <summary>Shuts the driver down and reports what the cleanup verified.</summary>
    List<string> Shutdown();
}

internal static class NativeSupport
{
    public static void DrainEvents()
    {
        TEventQueue.Resume();
        TEvent ev = default;
        for (int i = 0; i < 1000; i++)
        {
            TEventQueue.GetNextEvent(ref ev);
            if (ev.What == Events.evNothing) break;
        }
    }

    public static bool DrainResizeCommand()
    {
        TEventQueue.Resume();
        bool resized = false;
        TEvent ev = default;
        for (int i = 0; i < 1000; i++)
        {
            TEventQueue.GetNextEvent(ref ev);
            if (ev.What == Events.evNothing) break;
            if (ev.What == Events.evCommand && ev.message.command == Views.cmScreenResized) resized = true;
        }
        return resized;
    }

    /// <summary>
    /// The fit mode as configured, as resolved by the driver from its configuration, and
    /// as used by the renderer's glyph rasteriser, plus the common text size it measured.
    /// </summary>
    public static void FontFit(FontSpec font, string driverFit, TSharpVision.Drivers.SDL.Rendering.Fonts.SdlCellGlyphRasterizer? glyphs, Dictionary<string, string> into)
    {
        into["fontFit.configured"] = font.Programmatic ? $"{font.Fit} (programmatic)"
            : font.ConfigFile == null ? "(not configured: default)" : font.Fit;
        into["fontFit.configFile"] = font.ConfigFile == null ? "(in-memory configuration)" : System.IO.Path.GetFileName(font.ConfigFile);
        into["fontFit.driver"] = driverFit;
        if (glyphs == null) return;
        into["fontFit.renderer"] = glyphs.FontFit.ToString();
        // Uniform is measured when the rasteriser is created (driver initialisation). Condense
        // measures lazily on the render thread, so its baseline is not read from here.
        into["fontFit.textPointSize"] = glyphs.UniformPointSize.ToString("0.#");
        if (glyphs.FontFit.ToString() == "Uniform")
        {
            into["fontFit.textBaseline"] = glyphs.TextBaseline.ToString();
            into["fontFit.measureMs"] = glyphs.UniformMeasureMilliseconds.ToString("F1");
            into["fontFit.uniformPen"] = glyphs.UniformPen.ToString();
            into["fontFit.uniformExtremes"] = glyphs.UniformExtremes;
            into["fontFit.uniformRepertoire"] = glyphs.UniformMeasuredGlyphs.ToString();
            into["fontFit.uniformRejected"] = string.Join("; ", glyphs.UniformRejectedSizes);
        }
    }

    public static void WindowMetrics(IntPtr window, Dictionary<string, string> into)
    {
        SDL3.SDL.GetWindowSize(window, out int lw, out int lh);
        SDL3.SDL.GetWindowSizeInPixels(window, out int pw, out int ph);
        into["window.logical"] = $"{lw}x{lh}";
        into["window.pixels"] = $"{pw}x{ph}";
        into["window.pixelDensity"] = SDL3.SDL.GetWindowPixelDensity(window).ToString("F2");
        into["window.displayScale"] = SDL3.SDL.GetWindowDisplayScale(window).ToString("F2");
        into["window.flags"] = SDL3.SDL.GetWindowFlags(window).ToString();
    }

    /// <summary>Waits (bounded) until the driver reports the grid, pumping its real event loop.</summary>
    public static void Resize(IDriver driver, IntPtr window, GridSize grid, int cw, int ch, List<string> checks, List<string> failures, bool record)
    {
        if (driver.GetCols() == grid.Width && driver.GetRows() == grid.Height) return;
        SDL3.SDL.SetWindowSize(window, grid.Width * cw, grid.Height * ch);
        SDL3.SDL.SyncWindow(window);
        for (int i = 0; i < 200 && (driver.GetCols() != grid.Width || driver.GetRows() != grid.Height); i++)
        {
            driver.PumpMessages();
            Thread.Sleep(2);
        }
        bool reached = driver.GetCols() == grid.Width && driver.GetRows() == grid.Height;
        bool buffer = TScreen.ScreenBuffer is { } sb && sb.Width == grid.Width && sb.Height == grid.Height;
        bool command = DrainResizeCommand();
        if (!record)
        {
            if (!reached) throw new InvalidOperationException($"window resize to {grid} did not reach the driver ({driver.GetCols()}x{driver.GetRows()})");
            return;
        }
        (reached ? checks : failures).Add($"SDL WindowResized reached the driver: {grid}");
        (buffer ? checks : failures).Add($"TScreen.ScreenBuffer reallocated at {grid}");
        (command ? checks : failures).Add("cmScreenResized queued");
    }
}

/// <summary>
/// SDLDriver + SDLRenderer on a real SDL_Renderer backend. The frame is read back with
/// SDL_RenderReadPixels from the renderer's target after every cell is drawn and before
/// SDL_RenderPresent, through the renderer's internal before-present hook.
/// </summary>
public sealed class Sdl2dPath : INativePath
{
    private readonly SDLDriver? _driver;
    private readonly string _requested;

    public string Id { get; }
    public string Description { get; }
    public string CaptureBoundary => "SDL_RenderReadPixels on the SDL_Renderer target after the full frame is drawn, before SDL_RenderPresent (renderer framebuffer; not the OS compositor or display)";
    public string? Unavailable { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public FontGlyphPlacement Placement => FontGlyphPlacement.CellPlaced;
    public int PartialTolerance { get; init; } = 1;
    public Dictionary<string, string> Identity { get; } = new();

    public Sdl2dPath(string id, string videoDriver, string renderDriver, FontSpec font)
    {
        Id = id;
        _requested = renderDriver;
        Description = $"SDLDriver → SDLRenderer → SDL_Renderer '{renderDriver}' (video '{videoDriver}', hidden window)";
        try
        {
            SDL3.SDL.SetHint(SDL3.SDL.Hints.VideoDriver, videoDriver);
            SDL3.SDL.SetHint(SDL3.SDL.Hints.RenderDriver, renderDriver);
            font.Apply();
            var driver = new SDLDriver { ExtraWindowFlagsForTesting = SDL3.SDL.WindowFlags.Hidden };
            driver.Initialize();
            TScreen.ScreenBuffer = driver.AllocateScreenBuffer();
            SDLRenderer renderer = driver.RendererForTesting ?? throw new InvalidOperationException("SDLDriver stayed detached (TSharpVision_NO_SDL?)");
            _driver = driver;
            CellWidth = renderer.CellWidth;
            CellHeight = renderer.CellHeight;
            string actual = SDL3.SDL.GetRendererName(driver.RendererHandleForTesting) ?? "?";
            Identity["sdl.version"] = SdlVersion();
            Identity["video.driver"] = SDL3.SDL.GetCurrentVideoDriver() ?? "?";
            Identity["render.driver.requested"] = renderDriver;
            Identity["render.driver.actual"] = actual;
            Identity["execution"] = actual == "software" ? "CPU (SDL software renderer)" : $"GPU-accelerated SDL_Renderer backend '{actual}' (adapter not exposed by SDL_Renderer)";
            Identity["cell"] = $"{CellWidth}x{CellHeight}";
            Identity["font"] = $"{font.Path} {font.PointSize}pt";
            NativeSupport.FontFit(font, driver.FontFit.ToString(), renderer.GlyphsForTesting, Identity);
            NativeSupport.WindowMetrics(driver.WindowForTesting, Identity);
            if (actual != renderDriver)
                Unavailable = $"BackendUnavailable: requested SDL renderer '{renderDriver}', SDL created '{actual}'";
        }
        catch (Exception ex)
        {
            Unavailable = $"BackendUnavailable: {ex.GetBaseException().Message}";
        }
    }

    public void EnsureGrid(GridSize grid, List<string> checks, List<string> failures) =>
        NativeSupport.Resize(_driver!, _driver!.WindowForTesting, grid, CellWidth, CellHeight, checks, failures, record: true);

    public void Write(CellWrite w) => _driver!.WriteBuf(w.X, w.Y, w.Cells.Length, 1, w.Cells);

    public void SetCaret(CaretRequest c)
    {
        _driver!.SetCaretPosition(c.X, c.Y);
        _driver.SetCursorType(c.CursorType);
    }

    public NativeCapture Capture()
    {
        SDLRenderer renderer = _driver!.RendererForTesting!;
        NativeCapture? capture = null;
        renderer.BeforePresentForTesting = handle => capture = ReadBack(handle);
        try { _driver.PumpMessages(); }
        finally { renderer.BeforePresentForTesting = null; }
        NativeSupport.DrainEvents();
        return capture ?? throw new InvalidOperationException("the driver rendered no frame (not dirty)");
    }

    internal static NativeCapture ReadBack(IntPtr renderer)
    {
        SDL3.SDL.GetCurrentRenderOutputSize(renderer, out int ow, out int oh);
        IntPtr surf = SDL3.SDL.RenderReadPixels(renderer, null);
        if (surf == IntPtr.Zero) throw new InvalidOperationException("SDL_RenderReadPixels failed: " + SDL3.SDL.GetError());
        try
        {
            SDL3.SDL.Surface raw = Marshal.PtrToStructure<SDL3.SDL.Surface>(surf);
            string native = SDL3.SDL.GetPixelFormatName(raw.Format) ?? raw.Format.ToString();
            IntPtr conv = SDL3.SDL.ConvertSurface(surf, SDL3.SDL.PixelFormat.ABGR8888);
            if (conv == IntPtr.Zero) throw new InvalidOperationException("ConvertSurface failed: " + SDL3.SDL.GetError());
            try
            {
                SDL3.SDL.Surface s = Marshal.PtrToStructure<SDL3.SDL.Surface>(conv);
                var rgba = new byte[s.Width * s.Height * 4];
                for (int y = 0; y < s.Height; y++)
                    Marshal.Copy(s.Pixels + y * s.Pitch, rgba, y * s.Width * 4, s.Width * 4);
                var c = new NativeCapture
                {
                    Image = new NativeImage(s.Width, s.Height, rgba),
                    NativeFormat = native,
                    Method = "SDL_RenderReadPixels (before SDL_RenderPresent), converted losslessly to RGBA8",
                };
                c.Details["render.output"] = $"{ow}x{oh}";
                c.Details["render.driver"] = SDL3.SDL.GetRendererName(renderer) ?? "?";
                return c;
            }
            finally { SDL3.SDL.DestroySurface(conv); }
        }
        finally { SDL3.SDL.DestroySurface(surf); }
    }

    public List<string> Shutdown()
    {
        var notes = new List<string>();
        if (_driver == null) return notes;
        _driver.Shutdown();
        notes.Add($"SDL_WasInit after driver shutdown = {SDL3.SDL.WasInit(0)} (0 = SDL fully shut down)");
        notes.Add($"TTF_WasInit after driver shutdown = {SDL3.TTF.WasInit()}");
        return notes;
    }

    public void Dispose() { }

    internal static string SdlVersion()
    {
        int v = SDL3.SDL.GetVersion();
        return $"{v / 1000000}.{v / 1000 % 1000}.{v % 1000}";
    }
}

/// <summary>
/// SDLGpuDriver + SDLGpuRenderer on a real SDL_GPU device with a hidden window. Frames run on the
/// renderer's own render thread; the capture seam renders the requested frame a second time with
/// the same production code (shader pipeline, or the CPU-composite fallback's upload + clear + blit)
/// into a readable texture of the swapchain format, downloads it through a transfer buffer and
/// waits (bounded) on a fence.
/// </summary>
public sealed class SdlGpuPath : INativePath
{
    private readonly SDLGpuDriver? _driver;
    private readonly bool _fallback;

    public string Id { get; }
    public string Description { get; }
    public string CaptureBoundary => _fallback
        ? "GPU render target of the swapchain format: CPU-composited frame uploaded, cleared and 1:1 blitted by the production fallback code, then DownloadFromGPUTexture (GPU memory; not the swapchain image, OS compositor or display)"
        : "GPU render target of the swapchain format: production shader pipeline (bg + glyph passes, atlas sampling, blending), then DownloadFromGPUTexture (GPU memory; not the swapchain image, OS compositor or display)";
    public string? Unavailable { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public FontGlyphPlacement Placement => FontGlyphPlacement.CellPlaced;
    public int PartialTolerance { get; init; } = 1;
    public Dictionary<string, string> Identity { get; } = new();
    public TimeSpan GpuTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public SdlGpuPath(string id, bool fallback, FontSpec font)
    {
        Id = id;
        _fallback = fallback;
        Description = fallback
            ? "SDLGpuDriver → SDLGpuRenderer CPU-composite fallback (shader pipeline construction failed via test seam) → SDL_GPU upload/blit"
            : "SDLGpuDriver → SDLGpuRenderer → TerminalGpuPipeline → SDL_GPU render pass (real shaders)";
        try
        {
            SDL3.SDL.SetHint(SDL3.SDL.Hints.VideoDriver, "windows");
            font.Apply();
            var driver = new SDLGpuDriver
            {
                ExtraWindowFlagsForTesting = SDL3.SDL.WindowFlags.Hidden,
                SimulateShaderPipelineFailureForTesting = fallback,
            };
            driver.Initialize();
            TScreen.ScreenBuffer = driver.AllocateScreenBuffer();
            SDLGpuRenderer renderer = driver.RendererForTesting ?? throw new InvalidOperationException("SDLGpuDriver stayed detached (TSharpVision_NO_SDL?)");
            _driver = driver;
            CellWidth = renderer.CellWidth;
            CellHeight = renderer.CellHeight;
            IntPtr device = renderer.DeviceForTesting;
            uint props = SDL3.SDL.GetGPUDeviceProperties(device);
            string name = SDL3.SDL.GetStringProperty(props, "SDL.gpu.device.name", "") ?? "";
            Identity["sdl.version"] = Sdl2dPath.SdlVersion();
            Identity["video.driver"] = SDL3.SDL.GetCurrentVideoDriver() ?? "?";
            Identity["gpu.backend"] = renderer.Backend;
            Identity["gpu.device.name"] = name.Length == 0 ? "(not exposed)" : name;
            Identity["gpu.device.driver"] = SDL3.SDL.GetStringProperty(props, "SDL.gpu.device.driver_name", "") ?? "";
            Identity["gpu.device.driverVersion"] = SDL3.SDL.GetStringProperty(props, "SDL.gpu.device.driver_version", "") ?? "";
            Identity["gpu.device.driverInfo"] = SDL3.SDL.GetStringProperty(props, "SDL.gpu.device.driver_info", "") ?? "";
            Identity["gpu.swapchainFormat"] = SDL3.SDL.GetGPUSwapchainTextureFormat(device, driver.WindowForTesting).ToString();
            Identity["gpu.shaderPipelineActive"] = renderer.ShaderPipelineActive.ToString();
            Identity["gpu.adapterKind"] = ClassifyAdapter(name);
            Identity["execution"] = renderer.ShaderPipelineActive ? "GPU shader pipeline" : "CPU compositing + GPU upload/blit";
            Identity["cell"] = $"{CellWidth}x{CellHeight}";
            Identity["font"] = $"{font.Path} {font.PointSize}pt";
            NativeSupport.FontFit(font, driver.FontFit.ToString(), renderer.GlyphsForTesting, Identity);
            NativeSupport.WindowMetrics(driver.WindowForTesting, Identity);
            if (renderer.ShaderPipelineActive == fallback)
                Unavailable = fallback
                    ? "Error: the fallback was requested but the shader pipeline is active"
                    : "BackendUnavailable: the shader pipeline could not be created on this device (see stderr); the renderer fell back to CPU compositing";
        }
        catch (Exception ex)
        {
            Unavailable = $"PlatformUnavailable: {ex.GetBaseException().Message}";
        }
    }

    /// <summary>Hardware vs. software implementation, from the adapter name SDL exposes.</summary>
    public static string ClassifyAdapter(string name) =>
        name.Length == 0 ? "unknown (adapter name not exposed)"
        : name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("WARP", StringComparison.OrdinalIgnoreCase)
          || name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || name.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase)
            ? "software implementation"
            : "hardware adapter";

    public void EnsureGrid(GridSize grid, List<string> checks, List<string> failures) =>
        NativeSupport.Resize(_driver!, _driver!.WindowForTesting, grid, CellWidth, CellHeight, checks, failures, record: true);

    public void Write(CellWrite w) => _driver!.WriteBuf(w.X, w.Y, w.Cells.Length, 1, w.Cells);

    public void SetCaret(CaretRequest c)
    {
        _driver!.SetCaretPosition(c.X, c.Y);
        _driver.SetCursorType(c.CursorType);
    }

    public NativeCapture Capture()
    {
        SDLGpuRenderer renderer = _driver!.RendererForTesting!;
        SDL3.SDL.GetWindowSizeInPixels(_driver.WindowForTesting, out int pw, out int ph);
        Task<GpuFrameCapture> pending = renderer.RequestCaptureForTesting((uint)pw, (uint)ph, GpuTimeout);
        _driver.PumpMessages();
        NativeSupport.DrainEvents();
        if (!pending.Wait(GpuTimeout + TimeSpan.FromSeconds(10)))
            throw new TimeoutException("the render thread did not serve the capture (no frame submitted?)");
        GpuFrameCapture g = pending.Result;
        var c = new NativeCapture
        {
            Image = new NativeImage(g.Width, g.Height, g.Rgba),
            NativeFormat = g.NativeFormat,
            Method = "readable GPU texture (swapchain format) rendered by the production frame code, DownloadFromGPUTexture + fence, converted losslessly to RGBA8",
            Frame = g.Frame,
        };
        c.Details["gpu.mode"] = g.Mode;
        c.Details["gpu.backend"] = g.Backend;
        c.Details["gpu.fenceWaitMs"] = g.GpuWait.TotalMilliseconds.ToString("F2");
        c.Details["frame.grid"] = $"{g.Columns}x{g.Rows}";
        c.Details["swapchain.framesPresented"] = g.FramesPresented.ToString();
        c.Details["swapchain.framesSkipped"] = g.FramesSkipped.ToString();
        return c;
    }

    public List<string> Shutdown()
    {
        var notes = new List<string>();
        if (_driver == null) return notes;
        SDLGpuRenderer? renderer = _driver.RendererForTesting;
        long presented = renderer?.FramesPresented ?? 0, skipped = renderer?.FramesSkipped ?? 0;
        _driver.Shutdown(); // joins the render thread (throws on timeout), releases GPU resources, destroys device and window
        notes.Add($"swapchain frames presented={presented} skipped={skipped} (hidden window)");
        notes.Add($"SDL_WasInit after driver shutdown = {SDL3.SDL.WasInit(0)} (0 = SDL fully shut down)");
        notes.Add($"TTF_WasInit after driver shutdown = {SDL3.TTF.WasInit()}");
        return notes;
    }

    public void Dispose() { }
}
