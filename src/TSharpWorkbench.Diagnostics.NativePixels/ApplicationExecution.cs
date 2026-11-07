using System.Diagnostics;
using TSharpVision.Drivers;
using TSharpVision.Drivers.SDL;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using SDL3;

namespace TSharpWorkbench.Diagnostics.NativePixels;

// Attached to the actual application's production driver. No fixture replay, independent
// application, private-field reflection, framework event draining, or global input injection.
internal sealed class ApplicationExecution : INativeExecution
{
    private readonly SDLDriver driver = new();
    private readonly Dictionary<string, string> identity = new();
    private readonly int cellWidth, cellHeight;
    public IDriver Driver => driver;
    public string Routing => "SDL_PushEvent(target window) -> SDLDriver.PumpMessages/SDL_PollEvent -> production key translation -> TScreen.GetEvent -> Commander";

    internal ApplicationExecution(int width, int height)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Commander execution is qualified on Windows only.");
        try
        {
            SDL.SetHint(SDL.Hints.VideoDriver, "windows");
            SDL.SetHint(SDL.Hints.RenderDriver, "software");
            FontSpec.Default.Apply();
            ScreenDriverFactory.WindowTitle = "Owned Commander " + Environment.ProcessId;
            driver.Initialize();
            var renderer = driver.RendererForTesting ?? throw new InvalidOperationException("SDLDriver did not attach.");
            string backend = SDL.GetRendererName(driver.RendererHandleForTesting) ?? "unknown";
            if (backend != "software" || SDL.GetCurrentVideoDriver() != "windows")
                throw new InvalidOperationException("Requested windows/software backend was not selected; no fallback allowed.");
            cellWidth = renderer.CellWidth; cellHeight = renderer.CellHeight;
            SDL.SetWindowSize(driver.WindowForTesting, width * cellWidth, height * cellHeight);
            SDL.SyncWindow(driver.WindowForTesting);
            var limit = Stopwatch.StartNew();
            while (driver.GetCols() != width || driver.GetRows() != height)
            {
                driver.PumpMessages();
                if (limit.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native initial grid was not adopted.");
                Thread.Sleep(5);
            }
            // This is before constructing the application. Drop startup resize notifications only.
            NativeSupport.DrainEvents();
            identity["driver"] = nameof(SDLDriver);
            identity["backend"] = backend;
            identity["videoDriver"] = SDL.GetCurrentVideoDriver()!;
            identity["sdlVersion"] = Sdl2dPath.SdlVersion();
            identity["processId"] = Environment.ProcessId.ToString();
            identity["executable"] = Environment.ProcessPath ?? "unknown";
            identity["assemblyPath"] = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "unknown";
            identity["windowId"] = SDL.GetWindowID(driver.WindowForTesting).ToString();
            identity["hwnd"] = SDL.GetPointerProperty(SDL.GetWindowProperties(driver.WindowForTesting), SDL.Props.WindowWin32HWNDPointer, IntPtr.Zero).ToInt64().ToString();
            identity["font"] = FontSpec.Default.Path;
            identity["fontSize"] = FontSpec.Default.PointSize.ToString();
            identity["cellWidth"] = cellWidth.ToString(); identity["cellHeight"] = cellHeight.ToString();
            identity["captureBoundary"] = "SDL_RenderReadPixels before SDL_RenderPresent on the actual Commander SDL_Renderer; not compositor presentation";
            identity["inputProof"] = "Target-owned SDL event injection and real driver translation; not hardware input";
            NativeSupport.WindowMetrics(driver.WindowForTesting, identity);
        }
        catch { driver.Shutdown(); throw; }
    }

    public void Inject(string key) => InjectKey(driver.WindowForTesting, key);

    internal static void InjectKey(IntPtr window, string key)
    {
        var (code, scan, mod) = key switch
        {
            "kbF10" => (SDL.Keycode.F10, SDL.Scancode.F10, SDL.Keymod.None),
            "kbRight" => (SDL.Keycode.Right, SDL.Scancode.Right, SDL.Keymod.None),
            "kbLeft" => (SDL.Keycode.Left, SDL.Scancode.Left, SDL.Keymod.None),
            "kbDown" => (SDL.Keycode.Down, SDL.Scancode.Down, SDL.Keymod.None),
            "kbUp" => (SDL.Keycode.Up, SDL.Scancode.Up, SDL.Keymod.None),
            "kbEnter" => (SDL.Keycode.Return, SDL.Scancode.Return, SDL.Keymod.None),
            "kbEsc" => (SDL.Keycode.Escape, SDL.Scancode.Escape, SDL.Keymod.None),
            "kbTab" => (SDL.Keycode.Tab, SDL.Scancode.Tab, SDL.Keymod.None),
            "kbShiftF5" => (SDL.Keycode.F5, SDL.Scancode.F5, SDL.Keymod.LShift),
            "kbShiftF6" => (SDL.Keycode.F6, SDL.Scancode.F6, SDL.Keymod.LShift),
            "kbAltW" => ((SDL.Keycode)(uint)'w', SDL.Scancode.W, SDL.Keymod.LAlt),
            "kbAltX" => ((SDL.Keycode)(uint)'x', SDL.Scancode.X, SDL.Keymod.LAlt),
            _ => throw new InvalidOperationException("Unsupported native key: " + key),
        };
        uint windowId = SDL.GetWindowID(window);
        if (windowId == 0) throw new InvalidOperationException("Owned SDL window is gone.");
        foreach (bool down in new[] { true, false })
        {
            SDL.Event ev = default;
            ev.Key.Type = down ? SDL.EventType.KeyDown : SDL.EventType.KeyUp;
            ev.Key.WindowID = windowId; ev.Key.Timestamp = SDL.GetTicksNS();
            ev.Key.Key = code; ev.Key.Scancode = scan; ev.Key.Mod = mod; ev.Key.Down = down;
            if (!SDL.PushEvent(ref ev)) throw new InvalidOperationException("SDL_PushEvent failed: " + SDL.GetError());
        }
    }

    public void InjectMouse(TEvent input) => InjectPointer(driver.WindowForTesting, cellWidth, cellHeight, input);

    internal static void InjectPointer(IntPtr window, int cellWidth, int cellHeight, TEvent input)
    {
        uint windowId = SDL.GetWindowID(window);
        if (windowId == 0) throw new InvalidOperationException("Owned SDL window is gone.");
        SDL.Event ev = default;
        float x = (input.mouse.where.x + 0.5f) * cellWidth, y = (input.mouse.where.y + 0.5f) * cellHeight;
        if (input.What == Events.evMouseMove)
        {
            ev.Motion.Type = SDL.EventType.MouseMotion; ev.Motion.WindowID = windowId;
            ev.Motion.Timestamp = SDL.GetTicksNS(); ev.Motion.X = x; ev.Motion.Y = y;
        }
        else if (input.What is Events.evMouseDown or Events.evMouseUp)
        {
            ev.Button.Type = input.What == Events.evMouseDown ? SDL.EventType.MouseButtonDown : SDL.EventType.MouseButtonUp;
            ev.Button.WindowID = windowId; ev.Button.Timestamp = SDL.GetTicksNS();
            ev.Button.Button = 1; ev.Button.Down = input.What == Events.evMouseDown; ev.Button.Clicks = 1;
            ev.Button.X = x; ev.Button.Y = y;
        }
        else throw new InvalidOperationException("Unsupported native mouse event.");
        if (!SDL.PushEvent(ref ev)) throw new InvalidOperationException("SDL_PushEvent failed: " + SDL.GetError());
    }

    public NativeCheckpoint Capture()
    {
        var renderer = driver.RendererForTesting ?? throw new InvalidOperationException("Native renderer detached.");
        NativeCapture? capture = null;
        renderer.BeforePresentForTesting = handle => capture = Sdl2dPath.ReadBack(handle);
        // Render the driver's own current application buffer using its configured production render callback.
        // Never drain framework events or replace that buffer with a logical screenshot.
        try { driver.MessageLoop!(renderer); }
        finally { renderer.BeforePresentForTesting = null; }
        var frame = capture ?? throw new InvalidOperationException("Actual Commander renderer did not supply pixels.");
        var details = new Dictionary<string, string>(identity);
        NativeSupport.WindowMetrics(driver.WindowForTesting, details);
        details["screenGrid"] = $"{TScreen.ScreenWidth}x{TScreen.ScreenHeight}";
        return new(Convert.ToBase64String(frame.Image.EncodePng()), ScreenSnapshot.Hash(frame.Image.Rgba),
            "renderer-target", frame.Image.Width, frame.Image.Height, details);
    }

    public void Dispose()
    {
        driver.Shutdown();
        if (driver.RendererForTesting is not null || driver.WindowForTesting != IntPtr.Zero || SDL.WasInit(0) != 0 || TTF.WasInit() != 0)
            throw new InvalidOperationException("Native SDL cleanup did not release the renderer, window and SDL/TTF subsystems.");
    }
}
