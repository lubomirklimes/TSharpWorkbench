using TSharpVision.Drivers.SDL;
using TSharpVision.Drivers.SDL.Gpu;
using TSharpVision.Drivers.SDL.Rendering.GlyphComposition;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>
/// The real <see cref="SDLDriver"/> on SDL's offscreen video driver with the software renderer: no
/// window is shown and no GPU is needed, yet Initialize, WriteBuf, window resize, PumpMessages,
/// <see cref="SDLRenderer"/> and the SDL calls all run. The renderer's internal trace records each
/// fill and glyph beside the SDL call that draws it.
/// </summary>
public sealed class SdlRendererOutputPath : IOutputPath
{
    private SDLDriver? _driver;
    private CaretRequest _caret;

    public string Id => "sdl-renderer";
    public string Driver => nameof(SDLDriver) + " + SDLRenderer";
    public string Boundary => "SDLRenderer render trace (fill rect + colour, glyph + class + colour) recorded at the SDL_RenderFillRect/SDL_RenderTexture call sites; SDL offscreen video driver, software renderer";
    public PathCapabilities Capabilities { get; } = new(CaretModel.Drawn, CursorShape: true, ForegroundOfBlankCells: false,
        "16-colour VGA palette; caret drawn into the cell; every render redraws the full frame");
    public string? Unavailable { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public string VideoDriver { get; } = "n/a";
    public string RenderDriver { get; } = "n/a";

    public SDLDriver DriverInstance => _driver ?? throw new InvalidOperationException(Unavailable);

    public SdlRendererOutputPath()
    {
        try
        {
            SDL3.SDL.SetHint(SDL3.SDL.Hints.VideoDriver, "offscreen");
            SDL3.SDL.SetHint(SDL3.SDL.Hints.RenderDriver, "software");
            TScreen.ScreenWidth = 120; TScreen.ScreenHeight = 37;
            var driver = new SDLDriver();
            driver.Initialize();
            TScreen.ScreenBuffer = driver.AllocateScreenBuffer();
            SDLRenderer renderer = driver.RendererForTesting ?? throw new InvalidOperationException("SDLDriver stayed detached (TSharpVision_NO_SDL?).");
            CellWidth = renderer.CellWidth;
            CellHeight = renderer.CellHeight;
            VideoDriver = SDL3.SDL.GetCurrentVideoDriver() ?? "?";
            RenderDriver = SDL3.SDL.GetRendererName(driver.RendererHandleForTesting) ?? "?";
            _driver = driver;
        }
        catch (Exception ex)
        {
            Unavailable = $"SDL offscreen initialisation failed: {ex.GetBaseException().Message}";
        }
    }

    public void Begin(GridSize grid)
    {
        var driver = DriverInstance;
        ResizeTo(grid, null);
        _caret = default;
        driver.SetCursorType(0);
    }

    private void ResizeTo(GridSize grid, StepCapture? capture)
    {
        var driver = DriverInstance;
        if (driver.GetCols() == grid.Width && driver.GetRows() == grid.Height) return;
        IntPtr window = SDL3.SDL.GetRenderWindow(driver.RendererHandleForTesting);
        SDL3.SDL.SetWindowSize(window, grid.Width * CellWidth, grid.Height * CellHeight);
        SDL3.SDL.SyncWindow(window);
        PathSupport.DrainQueue();
        for (int i = 0; i < 50 && (driver.GetCols() != grid.Width || driver.GetRows() != grid.Height); i++)
        {
            driver.PumpMessages();
            Thread.Sleep(1);
        }
        bool reached = driver.GetCols() == grid.Width && driver.GetRows() == grid.Height;
        if (!reached && capture == null)
            throw new InvalidOperationException($"SDL window resize to {grid} did not reach the driver (driver reports {driver.GetCols()}x{driver.GetRows()}).");
        if (capture != null)
        {
            capture.Check(reached, $"SDL WindowResized reached the driver: grid {grid}");
            capture.Check(TScreen.ScreenBuffer is { } sb && sb.Width == grid.Width && sb.Height == grid.Height, $"TScreen.ScreenBuffer reallocated at {grid}");
            capture.Check(PathSupport.DrainResizeCommand(), "cmScreenResized queued");
            capture.Record(new CaptureOp("SDL_SetWindowSize", W: grid.Width * CellWidth, H: grid.Height * CellHeight));
        }
        else PathSupport.DrainQueue();
    }

    public void Apply(FixtureStep step, StepCapture capture)
    {
        var driver = DriverInstance;
        if (step.ResizeTo is { } target) ResizeTo(target, capture);
        foreach (CellWrite w in step.Writes) driver.WriteBuf(w.X, w.Y, w.Cells.Length, 1, w.Cells);
        driver.SetCaretPosition(step.Caret.X, step.Caret.Y);
        driver.SetCursorType(step.Caret.CursorType);
        _caret = step.Caret;
    }

    public void Observe(StepCapture capture)
    {
        var driver = DriverInstance;
        SDLRenderer renderer = driver.RendererForTesting!;
        var trace = new SdlRenderTrace();
        renderer.Trace = trace;
        try { driver.PumpMessages(); }
        finally { renderer.Trace = null; }
        PathSupport.DrainQueue();
        SdlTraceDecoder.Decode(trace, capture, CellWidth, CellHeight);
        SdlTraceDecoder.InferBlockCaret(capture, _caret);
    }

    public void Dispose()
    {
        try { _driver?.Shutdown(); } catch { }
        _driver = null;
    }
}

/// <summary>Turns an <see cref="SdlRenderTrace"/> frame back into cells (independent of the renderer's loop).</summary>
internal static class SdlTraceDecoder
{
    public static void Decode(SdlRenderTrace trace, StepCapture capture, int cw, int ch)
    {
        int w = capture.Grid.Width, h = capture.Grid.Height;
        List<SdlRenderOp> ops = trace.Operations;
        int begin = ops.FindLastIndex(o => o.Kind == SdlRenderOpKind.BeginFrame);
        if (begin < 0)
        {
            capture.Failures.Add("no frame was rendered");
            capture.Cells = Enumerable.Repeat(new SemanticCell("", -1, -1), w * h).ToArray();
            return;
        }
        int frames = ops.Count(o => o.Kind == SdlRenderOpKind.BeginFrame);
        if (frames > 1) capture.Diagnostics.Add($"{frames} frames rendered; the last one is decoded");
        SdlRenderOp frame = ops[begin];
        capture.Check(frame.X == 0 && frame.Y == 0 && frame.Width == w && frame.Height == h,
            $"frame region {frame.Width}x{frame.Height} at ({frame.X},{frame.Y}) covers the {w}x{h} grid");
        capture.Check(ops.Skip(begin).Any(o => o.Kind == SdlRenderOpKind.Present), "frame presented");

        var cells = Enumerable.Repeat(new SemanticCell("", -1, -1), w * h).ToArray();
        string? drawnCaret = null;
        int misaligned = 0, unknownColours = 0;
        foreach (SdlRenderOp op in ops.Skip(begin + 1))
        {
            if (op.Kind is SdlRenderOpKind.BeginFrame or SdlRenderOpKind.Present) continue;
            capture.Record(new CaptureOp(op.Kind.ToString(), op.X, op.Y, op.Width, op.Height,
                op.Character == '\0' ? null : op.Character.ToString(), Fg: $"#{op.Argb & 0xFFFFFF:X6}", Detail: op.Kind is SdlRenderOpKind.Fill ? null : op.GlyphClass.ToString()));
            int col = op.X / cw, row = op.Y / ch;
            int colour = Vga.IndexOf((int)(op.Argb & 0xFFFFFF));
            if (colour < 0) unknownColours++;
            bool fullCell = op.X % cw == 0 && op.Y % ch == 0 && op.Width == cw && op.Height == ch;
            if (op.Kind == SdlRenderOpKind.Fill && op.Height == 2 && op.Width == cw && op.X % cw == 0 && (op.Y + 2) % ch == 0)
            {
                drawnCaret = $"underline at ({col},{(op.Y + 2) / ch - 1})";
                continue;
            }
            if (!fullCell || col >= w || row >= h) { misaligned++; continue; }
            int i = row * w + col;
            switch (op.Kind)
            {
                case SdlRenderOpKind.Fill:
                    cells[i] = cells[i] with { Bg = colour, Text = cells[i].Text.Length == 0 ? " " : cells[i].Text };
                    break;
                case SdlRenderOpKind.Glyph:
                    cells[i] = cells[i] with { Text = op.Character.ToString(), Fg = colour };
                    break;
                case SdlRenderOpKind.GlyphMissing:
                    cells[i] = cells[i] with { Text = "�", Fg = colour };
                    capture.Diagnostics.Add($"no glyph for U+{(int)op.Character:X4} at ({col},{row})");
                    break;
            }
        }
        int unfilled = cells.Count(c => c.Text.Length == 0);
        capture.Check(unfilled == 0, $"every cell has a background fill ({unfilled} missing)");
        capture.Check(misaligned == 0, $"every operation lies on the cell grid ({misaligned} off-grid)");
        if (unknownColours > 0) capture.Diagnostics.Add($"{unknownColours} operation(s) with a non-VGA colour");
        capture.DrawnCaret = drawnCaret;
        capture.Cells = cells;
        if (trace.Dropped > 0) capture.Diagnostics.Add($"trace dropped {trace.Dropped} operation(s)");
    }

    /// <summary>Marks a block caret: the renderer swapped colours at that cell (no separate operation exists).</summary>
    public static void InferBlockCaret(StepCapture capture, CaretRequest caret)
    {
        if (caret.CursorType >= 100 && capture.DrawnCaret == null)
            capture.DrawnCaret = $"block at ({caret.X},{caret.Y})";
    }
}

/// <summary>Shared state for the two SDL GPU renderer paths: the real renderer with its font, no GPU device.</summary>
internal sealed class SdlGpuFixture : IDisposable
{
    public SDLGpuRenderer Renderer { get; }
    public int CellWidth => Renderer.CellWidth;
    public int CellHeight => Renderer.CellHeight;

    public SdlGpuFixture()
    {
        Renderer = new SDLGpuRenderer(IntPtr.Zero, new SDLGpuOptions());
        Renderer.LoadFontForTesting();
    }

    public void Dispose() => Renderer.Dispose();
}

/// <summary>
/// The SDL GPU driver's state between frames: SDLGpuDriver.WriteBuf copies each row segment into
/// its screen buffer and every render submits the whole buffer. Attaching the real SDLGpuDriver
/// needs a GPU device and a presentable swapchain, so this mirrors only that copy; the
/// translation under test (vertex build / CPU compositing) is the real renderer code.
/// </summary>
internal sealed class GpuDriverBuffer
{
    public ScreenBuffer Buffer { get; private set; } = new(1, 1);
    public CaretRequest Caret { get; private set; }

    public void Reset(GridSize grid) => Buffer = new ScreenBuffer((uint)grid.Width, (uint)grid.Height);

    public void Apply(FixtureStep step)
    {
        if (step.ResizeTo is { } target) Reset(target);
        foreach (CellWrite w in step.Writes)
            for (int i = 0; i < w.Cells.Length; i++)
                Buffer.SetChar((uint)(w.X + i), (uint)w.Y, w.Cells[i]);
        Caret = step.Caret;
    }
}

/// <summary>
/// SDL GPU shader path: the real <c>TerminalGpuPipeline.BuildVertices</c> fills the background and
/// glyph vertex streams (into ordinary memory instead of a mapped transfer buffer) with the real
/// renderer as glyph source and a CPU-only atlas. The streams are decoded independently: NDC
/// corners back to cells, vertex colours back to VGA indices, atlas UVs back to glyph keys.
/// </summary>
public sealed unsafe class SdlGpuShaderOutputPath : IOutputPath
{
    private readonly SdlGpuFixture? _gpu;
    private readonly GpuDriverBuffer _state = new();
    private TerminalAtlas? _atlas;

    public string Id => "sdl-gpu-shader";
    public string Driver => "SDLGpuRenderer + TerminalGpuPipeline";
    public string Boundary => "background/glyph vertex streams from TerminalGpuPipeline.BuildVertices (what the GPU copy pass uploads), decoded independently; no GPU device";
    public PathCapabilities Capabilities { get; } = new(CaretModel.Drawn, CursorShape: true, ForegroundOfBlankCells: false,
        "16-colour VGA palette; caret drawn into the cell; every frame rebuilds all vertices");
    public string? Unavailable { get; }

    internal SdlGpuShaderOutputPath(SdlGpuFixture? gpu, string? unavailable)
    {
        _gpu = gpu;
        Unavailable = unavailable;
    }

    public void Begin(GridSize grid)
    {
        _state.Reset(grid);
        _atlas?.Dispose();
        _atlas = new TerminalAtlas(_gpu!.CellWidth, _gpu.CellHeight);
    }

    public void Apply(FixtureStep step, StepCapture capture)
    {
        _state.Apply(step);
        if (step.ResizeTo is { } t) capture.Record(new CaptureOp("GridResize", W: t.Width, H: t.Height));
    }

    public void Observe(StepCapture capture)
    {
        SDLGpuRenderer renderer = _gpu!.Renderer;
        TerminalAtlas atlas = _atlas!;
        int w = capture.Grid.Width, h = capture.Grid.Height, cw = _gpu.CellWidth, ch = _gpu.CellHeight;
        var bg = new TerminalGpuPipeline.BgVertex[(w * h + 1) * 6];
        var glyph = new TerminalGpuPipeline.GlyphVertex[w * h * 6];
        int bgCount, glyphCount;
        fixed (TerminalGpuPipeline.BgVertex* pb = bg)
        fixed (TerminalGpuPipeline.GlyphVertex* pg = glyph)
            TerminalGpuPipeline.BuildVertices(pb, pg, _state.Buffer, (uint)w, (uint)h, cw, ch,
                _state.Caret.X, _state.Caret.Y, _state.Caret.CursorType, renderer, atlas, out bgCount, out glyphCount);

        capture.OperationCounts["bg-vertices"] = bgCount;
        capture.OperationCounts["glyph-vertices"] = glyphCount;
        capture.Check(bgCount % 6 == 0 && glyphCount % 6 == 0, "vertex counts are whole quads");

        float gridW = w * cw, gridH = h * ch;
        int ToPx(float ndc, float size) => (int)MathF.Round((ndc + 1f) / 2f * size);
        int ToPy(float ndc, float size) => (int)MathF.Round((1f - ndc) / 2f * size);

        var cells = Enumerable.Repeat(new SemanticCell("", -1, -1), w * h).ToArray();
        string? drawnCaret = null;
        int offGrid = 0;
        for (int q = 0; q < bgCount / 6; q++)
        {
            var tl = bg[q * 6]; var br = bg[q * 6 + 4];
            int x0 = ToPx(tl.X, gridW), y0 = ToPy(tl.Y, gridH), x1 = ToPx(br.X, gridW), y1 = ToPy(br.Y, gridH);
            int colour = Vga.IndexOf((tl.R << 16) | (tl.G << 8) | tl.B);
            if (q < 400) capture.Record(new CaptureOp("BgQuad", x0, y0, x1 - x0, y1 - y0, Fg: $"#{tl.R:X2}{tl.G:X2}{tl.B:X2}"));
            if (x1 - x0 == cw && y1 - y0 == 2 && x0 % cw == 0 && y1 % ch == 0) { drawnCaret = $"underline at ({x0 / cw},{y1 / ch - 1})"; continue; }
            if (x0 % cw != 0 || y0 % ch != 0 || x1 - x0 != cw || y1 - y0 != ch) { offGrid++; continue; }
            int i = y0 / ch * w + x0 / cw;
            cells[i] = new SemanticCell(" ", -1, colour);
        }
        // The 1.1 atlas is square and sized per instance (including HiDPI cells).
        int atlasW = atlas.Size, atlasH = atlas.Size;
        int wrongAlpha = 0;
        var checkedSlots = new HashSet<int>();
        for (int q = 0; q < glyphCount / 6; q++)
        {
            var tl = glyph[q * 6]; var br = glyph[q * 6 + 4];
            int x0 = ToPx(tl.X, gridW), y0 = ToPy(tl.Y, gridH), x1 = ToPx(br.X, gridW), y1 = ToPy(br.Y, gridH);
            if (x0 % cw != 0 || y0 % ch != 0 || x1 - x0 != cw || y1 - y0 != ch) { offGrid++; continue; }
            int slot = (int)MathF.Round(tl.U * atlasW / cw) + (int)MathF.Round(tl.V * atlasH / ch) * atlas.SlotColumns;
            int i = y0 / ch * w + x0 / cw;
            int colour = Vga.IndexOf((tl.R << 16) | (tl.G << 8) | tl.B);
            if (!atlas.TryGetKey(slot, out GpuGlyphKey key)) { cells[i] = cells[i] with { Text = "�", Fg = colour }; continue; }
            cells[i] = cells[i] with { Text = key.Character.ToString(), Fg = colour };
            if (checkedSlots.Add(slot) && renderer.GetAlpha(key) is { } alpha && !alpha.AsSpan().SequenceEqual(atlas.ReadSlot(slot))) wrongAlpha++;
        }
        capture.Record(new CaptureOp("GlyphQuads", W: glyphCount / 6, Detail: $"{checkedSlots.Count} atlas slots used"));
        foreach (var (c, idx) in cells.Select((c, idx) => (c, idx)).Where(t => t.c.Text.Length == 0).Take(1))
            capture.Diagnostics.Add($"cell ({idx % w},{idx / w}) has no background quad");
        capture.Check(cells.All(c => c.Text.Length != 0), "every cell has a background quad");
        capture.Check(offGrid == 0, $"every quad lies on the cell grid ({offGrid} off-grid)");
        capture.Check(wrongAlpha == 0, $"atlas slot contents equal the glyph source masks ({wrongAlpha} differ)");
        capture.DrawnCaret = drawnCaret;
        capture.Cells = cells;
        SdlTraceDecoder.InferBlockCaret(capture, _state.Caret);
    }

    public void Dispose() => _atlas?.Dispose();
}

/// <summary>
/// SDL GPU CPU-compositing fallback (used when the shader pipeline cannot be created): the real
/// <c>CompositeScreen</c> renders the frame into the CPU pixel buffer that would be uploaded. Each
/// cell block is checked against the glyph source's own alpha mask: pixels with alpha 0 must be
/// exactly the background colour, alpha 255 exactly the foreground, partial alpha between the two.
/// This verifies placement, colours and glyph identity without reproducing the blend formula.
/// </summary>
public sealed class SdlGpuCompositeOutputPath : IOutputPath
{
    private readonly SdlGpuFixture? _gpu;
    private readonly GpuDriverBuffer _state = new();

    public string Id => "sdl-gpu-composite";
    public string Driver => "SDLGpuRenderer (CPU compositing fallback)";
    public string Boundary => "CPU frame buffer from SDLGpuRenderer.CompositeScreen before GPU upload, verified per cell against the renderer's glyph masks; no GPU device";
    public PathCapabilities Capabilities { get; } = new(CaretModel.Drawn, CursorShape: true, ForegroundOfBlankCells: false,
        "16-colour VGA palette; caret drawn into the cell; every frame recomposites all cells");
    public string? Unavailable { get; }

    internal SdlGpuCompositeOutputPath(SdlGpuFixture? gpu, string? unavailable)
    {
        _gpu = gpu;
        Unavailable = unavailable;
    }

    public void Begin(GridSize grid) => _state.Reset(grid);

    public void Apply(FixtureStep step, StepCapture capture)
    {
        _state.Apply(step);
        if (step.ResizeTo is { } t) capture.Record(new CaptureOp("GridResize", W: t.Width, H: t.Height));
    }

    public void Observe(StepCapture capture)
    {
        SDLGpuRenderer renderer = _gpu!.Renderer;
        int w = capture.Grid.Width, h = capture.Grid.Height, cw = _gpu.CellWidth, ch = _gpu.CellHeight;
        CaretRequest caret = _state.Caret;
        renderer.SetCursor(caret.X, caret.Y, caret.CursorType);
        byte[] px = renderer.CompositeForTesting(_state.Buffer, (uint)w, (uint)h);
        capture.Record(new CaptureOp("CompositeScreen", W: w * cw, H: h * ch, Detail: $"{px.Length} bytes RGBA8"));
        int stride = w * cw * 4;
        int Pixel(int x, int y) { int o = y * stride + x * 4; return (px[o] << 16) | (px[o + 1] << 8) | px[o + 2]; }

        var cells = new SemanticCell[w * h];
        string? drawnCaret = null;
        int inconsistent = 0;
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                TScreenChar expectedCell = _state.Buffer.GetChar((uint)col, (uint)row);
                char c = expectedCell.Character is '\0' ? ' ' : expectedCell.Character;
                bool caretCell = caret.CursorType != 0 && col == caret.X && row == caret.Y;
                int checkRows = ch;
                if (caretCell && caret.CursorType < 100)
                {
                    // Underline caret: the bottom two pixel rows are a bar in the foreground colour.
                    checkRows = ch - 2;
                    int bar = Pixel(col * cw, row * ch + ch - 1);
                    if (Enumerable.Range(0, cw).All(dx => Pixel(col * cw + dx, row * ch + ch - 1) == bar && Pixel(col * cw + dx, row * ch + ch - 2) == bar))
                        drawnCaret = $"underline at ({col},{row})";
                }
                byte[]? alpha = c == ' ' ? null : renderer.GetAlpha(renderer.CreateKey(c, GlyphPhase.ForCell(col, row, cw, ch)));
                int bg = -1, fg = -1;
                bool ok = true;
                for (int dy = 0; dy < checkRows && ok; dy++)
                {
                    for (int dx = 0; dx < cw && ok; dx++)
                    {
                        int a = alpha?[dy * cw + dx] ?? 0;
                        int p = Pixel(col * cw + dx, row * ch + dy);
                        if (a == 0) { if (bg < 0) bg = p; else ok = p == bg; }
                        else if (a == 255) { if (fg < 0) fg = p; else ok = p == fg; }
                    }
                }
                // Partial coverage must lie between background and foreground on every channel.
                if (ok && alpha != null && bg >= 0 && fg >= 0)
                    for (int dy = 0; dy < checkRows && ok; dy++)
                        for (int dx = 0; dx < cw && ok; dx++)
                        {
                            int a = alpha[dy * cw + dx];
                            if (a is 0 or 255) continue;
                            int p = Pixel(col * cw + dx, row * ch + dy);
                            for (int shift = 0; shift <= 16 && ok; shift += 8)
                            {
                                int v = (p >> shift) & 0xFF, lo = Math.Min((bg >> shift) & 0xFF, (fg >> shift) & 0xFF), hi = Math.Max((bg >> shift) & 0xFF, (fg >> shift) & 0xFF);
                                ok = v >= lo && v <= hi;
                            }
                        }
                if (!ok) inconsistent++;
                cells[row * w + col] = ok
                    ? new SemanticCell(c.ToString(),
                        fg < 0 ? (c == ' ' ? -1 : Vga.Unobservable) : Vga.IndexOf(fg),
                        bg < 0 ? Vga.Unobservable : Vga.IndexOf(bg))
                    : new SemanticCell("�", -1, -1);
            }
        }
        capture.Check(inconsistent == 0, $"every cell block is consistent with its glyph mask ({inconsistent} inconsistent)");
        capture.Diagnostics.Add("text identity is verified against the expected glyph mask, not decoded from pixels");
        capture.DrawnCaret = drawnCaret;
        capture.Cells = cells;
        SdlTraceDecoder.InferBlockCaret(capture, caret);
    }

    public void Dispose() { }
}
