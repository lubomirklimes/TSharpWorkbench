using System.Text.Encodings.Web;
using System.Text.Json;
using TSharpWorkbench.Diagnostics.DriverOutput;
using TSharpVision.Drivers.SDL.Rendering.Fonts;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>Outcome of one fixture step on one path.</summary>
public sealed class StepResult
{
    public required string Fixture { get; init; }
    public required string Step { get; init; }
    public required string Grid { get; init; }
    public string Outcome { get; set; } = "Match";
    public string? Reason { get; set; }
    public string? NativeFormat { get; set; }
    public string? CaptureMethod { get; set; }
    public long Frame { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public Dictionary<string, string> CaptureDetails { get; set; } = new();
    public PixelComparison? Oracle { get; set; }
    public GeometryResult? Geometry { get; set; }
    public UpdateCheck? Update { get; set; }
    public DiacriticResult? Diacritics { get; set; }
    public List<string> Checks { get; } = new();
    public List<string> Failures { get; } = new();
    public int UnverifiedCells { get; set; }
    public List<string> UnverifiedSamples { get; set; } = new();
    public List<string> MissingFromFont { get; set; } = new();
    public string? NativePng { get; set; }
    public string? DiffPng { get; set; }
}

/// <summary>Pixels that changed between consecutive frames versus the cells the oracle says changed.</summary>
public sealed class UpdateCheck
{
    public int ExpectedChangedCells { get; set; }
    public int ActualChangedCells { get; set; }
    public int UnexpectedChangedCells { get; set; }
    public int MissingChangedCells { get; set; }
    public List<string> Samples { get; } = new();
    public bool Passed => UnexpectedChangedCells == 0 && MissingChangedCells == 0;
}

public sealed class NegativeControl
{
    public required string Name { get; init; }
    public required string Mutation { get; init; }
    public bool Detected { get; set; }
    public string Observed { get; set; } = "";
    /// <summary>
    /// False when the simulated defect draws exactly the expected frame for this font (for example
    /// the older clip rule on a font whose accents fit its ascent, or Uniform defects when the
    /// common size is the configured size): nothing to detect, so it neither passes nor fails.
    /// </summary>
    public bool Applicable { get; set; } = true;
}

public sealed class PathResult
{
    public required string Path { get; init; }
    /// <summary>Font × fontFit variant prefix (<c>uniform--consolas20--</c>); empty for the native-pixel default.</summary>
    public string Variant { get; set; } = "";
    public string Description { get; set; } = "";
    public string CaptureBoundary { get; set; } = "";
    public string Outcome { get; set; } = "Match";
    public string? Unavailable { get; set; }
    public Dictionary<string, string> Identity { get; set; } = new();
    public int PartialTolerance { get; set; }
    public string Placement { get; set; } = "";
    public List<StepResult> Steps { get; } = new();
    public List<NegativeControl> NegativeControls { get; } = new();
    public List<string> Cleanup { get; } = new();
    public string? Error { get; set; }
    public double Seconds { get; set; }
}

/// <summary>Which captures are kept as PNG files (all steps are always compared).</summary>
public enum PngPolicy { Representative, Minimal }

/// <summary>Runs every fixture through one native path inside this (child) process.</summary>
public static class ChildRunner
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Results have get-only collections; the parent reads them back.
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ResultName(string path) => $"{path}--path-result.json";

    /// <summary>Result file of <paramref name="path"/> in a variant (<see cref="FontSpec.Prefix"/>).</summary>
    public static string ResultName(string path, string prefix) => $"{prefix}{path}--path-result.json";

    public static INativePath CreatePath(string id, FontSpec font) => id switch
    {
        "sdl2d-software" => new Sdl2dPath(id, "offscreen", "software", font),
        "sdl-gpu-shader" => new SdlGpuPath(id, fallback: false, font),
        "sdl-gpu-fallback" => new SdlGpuPath(id, fallback: true, font),
        // Optional: the same driver forced onto SDL_GPU's Vulkan backend through its documented
        // TSHARPVISION_SDLGPU_BACKEND option (this child process only).
        "sdl-gpu-shader-vulkan" => WithEnvironment("TSHARPVISION_SDLGPU_BACKEND", "vulkan", () => new SdlGpuPath(id, fallback: false, font)),
        "sdl-gpu-fallback-vulkan" => WithEnvironment("TSHARPVISION_SDLGPU_BACKEND", "vulkan", () => new SdlGpuPath(id, fallback: true, font)),
        _ when id.StartsWith("sdl2d-", StringComparison.Ordinal) => new Sdl2dPath(id, "windows", id["sdl2d-".Length..], font),
        _ => throw new ArgumentException($"unknown path '{id}'"),
    };

    private static INativePath WithEnvironment(string name, string value, Func<INativePath> create)
    {
        Environment.SetEnvironmentVariable(name, value);
        return create();
    }

    public static PathResult Run(string pathId, string outputDir, FontSpec font, IReadOnlyList<string> tvs1Files, PngPolicy pngs, IReadOnlyList<GridSize> grids)
    {
        var started = DateTime.UtcNow;
        var result = new PathResult { Path = pathId };
        Directory.CreateDirectory(outputDir);
        INativePath? path = null;
        GlyphMaskOracle? masks = null;
        try
        {
            path = CreatePath(pathId, font);
            result.Description = path.Description;
            result.CaptureBoundary = path.CaptureBoundary;
            result.Identity = path.Identity;
            result.PartialTolerance = path.PartialTolerance;
            result.Placement = path.Placement.ToString();
            if (path.Unavailable != null)
            {
                result.Unavailable = path.Unavailable;
                result.Outcome = path.Unavailable.StartsWith("Error", StringComparison.Ordinal) ? "Error" : path.Unavailable.Split(':')[0];
                return result;
            }

            masks = new GlyphMaskOracle(font.Path, font.PointSize, SdlFontLocator.ProbeSymbolFontPath(), font.Uniform);
            result.Identity["oracle.cell"] = $"{masks.CellWidth}x{masks.CellHeight}";
            result.Identity["oracle.symbolFallback"] = masks.SymbolFontPath ?? "(none)";
            result.Identity["oracle.ascent"] = masks.Ascent.ToString();
            result.Identity["oracle.textBaseline"] = masks.TextBaseline.ToString();
            result.Identity["oracle.descenderDepth"] = masks.DescenderDepth.ToString();
            result.Identity["oracle.accentOverflow"] = masks.AccentOverflow.ToString();
            result.Identity["oracle.fontFit"] = masks.Uniform ? "Uniform" : "Condense";
            result.Identity["oracle.textPointSize"] = masks.UniformPointSize.ToString();
            if (masks.Uniform)
            {
                result.Identity["oracle.uniformPen"] = masks.UniformPen.ToString();
                result.Identity["oracle.uniformExtremes"] = $"above {masks.UniformMaxAbove}, below {masks.UniformMaxBelow}, width {masks.UniformMaxWidth}";
                result.Identity["oracle.uniformRepertoire"] = masks.UniformRepertoireSize.ToString();
                result.Identity["oracle.uniformTrace"] = string.Join("; ", masks.UniformTrace);
                if (result.Identity.GetValueOrDefault("fontFit.textPointSize") is { } product && product != masks.UniformPointSize.ToString())
                    throw new InvalidOperationException($"renderer text size {product}pt differs from the documented Uniform size {masks.UniformPointSize}pt");
            }
            if ((font.ConfigFile != null || font.Programmatic) && result.Identity.GetValueOrDefault("fontFit.renderer") is { } used
                && !string.Equals(used, font.Fit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"configured fontFit={font.Fit} but the renderer uses {used}");
            if (masks.CellWidth != path.CellWidth || masks.CellHeight != path.CellHeight)
                throw new InvalidOperationException($"renderer cell {path.CellWidth}x{path.CellHeight} differs from the font's documented cell {masks.CellWidth}x{masks.CellHeight}");

            var fixtures = new List<(string Fixture, GridSize Grid)>();
            foreach (GridSize g in grids) fixtures.Add((Fixtures.Screen, g));
            foreach (GridSize g in grids) fixtures.Add((Fixtures.Resize, g));
            foreach (GridSize g in grids) fixtures.Add((CzechFixture.Id, g));
            foreach (string file in tvs1Files)
            {
                var snap = TSharpWorkbench.Testing.ScreenSnapshot.Deserialize(File.ReadAllBytes(file));
                string id = CommanderFixtureId(file);
                External[id] = snap;
                fixtures.Add((id, new GridSize(snap.Width, snap.Height)));
            }

            foreach (var (fixture, grid) in fixtures)
                RunFixture(path, masks, fixture, grid, outputDir, pngs, result, font.Prefix);

            RunNegativeControls(path, masks, result);
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
            result.Outcome = "Error";
        }
        finally
        {
            masks?.Dispose();
            try { if (path != null) result.Cleanup.AddRange(path.Shutdown()); }
            catch (Exception ex) { result.Cleanup.Add("shutdown failed: " + ex.Message); result.Outcome = "Error"; }
            path?.Dispose();
            result.Seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);
            if (result.Outcome == "Match" && result.Steps.Any(s => s.Outcome != "Match")) result.Outcome = "Mismatch";
            if (result.Outcome == "Match" && result.NegativeControls.Any(n => n.Applicable && !n.Detected)) result.Outcome = "Mismatch";
            File.WriteAllText(System.IO.Path.Combine(outputDir, ResultName(pathId, font.Prefix)), JsonSerializer.Serialize(result, Json));
        }
        return result;
    }

    // Read-only TVS1 logical screens (approved logical Commander baselines) used as input cells.
    private static readonly Dictionary<string, TSharpWorkbench.Testing.ScreenSnapshot> External = new();

    /// <summary><c>commander-input-panel-selection--desktop--80x25.tvs1</c> → <c>commander-panel-selection-desktop</c>.</summary>
    public static string CommanderFixtureId(string file)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(file);
        int last = name.LastIndexOf("--", StringComparison.Ordinal);
        if (last > 0 && name[(last + 2)..].Contains('x')) name = name[..last];
        return name.Replace("commander-input-", "commander-").Replace("commander-menu-status-availability", "commander-menu-status").Replace("--", "-");
    }

    private static IReadOnlyList<FixtureStep> Steps(string fixture, GridSize grid)
    {
        if (fixture == CzechFixture.Id) return CzechFixture.Steps(grid);
        if (!External.TryGetValue(fixture, out var s)) return Fixtures.Steps(fixture, grid);
        var rows = new List<CellWrite>(s.Height);
        for (int y = 0; y < s.Height; y++)
            rows.Add(new CellWrite(0, y, Enumerable.Range(0, s.Width).Select(x => s[x, y]).ToArray()));
        return [new FixtureStep("full", rows, new CaretRequest(0, 0, 0))];
    }

    private static bool KeepPng(string fixture, string step, GridSize grid, PngPolicy policy) => policy switch
    {
        PngPolicy.Minimal => fixture is Fixtures.Screen or CzechFixture.Id && step == "full" && grid == new GridSize(80, 25),
        _ => fixture == Fixtures.Screen ? step is "full" or "cursor-underline"
             : fixture == Fixtures.Resize ? step == "after"
             : fixture == CzechFixture.Id ? step is "full" or "resized" || (step == "cursor-block" && grid == new GridSize(80, 25))
             : step == "full",
    };

    // The last capture of each step, kept for the update check and the negative controls.
    private static readonly Dictionary<string, (NativeImage Image, ExpectedRaster Raster, LogicalScreen Screen, CaretRequest Caret)> Kept = new();

    private static void RunFixture(INativePath path, GlyphMaskOracle masks, string fixture, GridSize grid, string dir, PngPolicy pngs, PathResult result, string prefix)
    {
        var setupChecks = new List<string>();
        var setupFailures = new List<string>();
        path.EnsureGrid(grid, setupChecks, setupFailures);
        if (setupFailures.Count > 0) throw new InvalidOperationException($"{fixture} {grid}: " + string.Join("; ", setupFailures));

        var screen = new LogicalScreen(grid, new TScreenChar(' ', 0x07));
        GridSize current = grid;
        NativeImage? previous = null;
        ExpectedRaster? previousRaster = null;
        foreach (FixtureStep step in Steps(fixture, grid))
        {
            string gridName = step.ResizeTo is { } rt ? $"{grid}-to-{rt}" : current.ToString();
            var sr = new StepResult { Fixture = fixture, Step = step.Name, Grid = gridName };
            result.Steps.Add(sr);
            try
            {
                if (step.ResizeTo is { } target)
                {
                    path.EnsureGrid(target, sr.Checks, sr.Failures);
                    current = target;
                    screen = new LogicalScreen(target, new TScreenChar(' ', 0x07));
                    previous = null;
                }
                foreach (CellWrite w in step.Writes) { path.Write(w); screen.Apply(w); }
                path.SetCaret(step.Caret);

                NativeCapture capture = path.Capture();
                NativeImage img = capture.Image;
                sr.NativeFormat = capture.NativeFormat;
                sr.CaptureMethod = capture.Method;
                sr.Frame = capture.Frame;
                sr.Width = img.Width;
                sr.Height = img.Height;
                sr.CaptureDetails = capture.Details;

                int expectW = current.Width * path.CellWidth, expectH = current.Height * path.CellHeight;
                bool dims = img.Width == expectW && img.Height == expectH;
                (dims ? sr.Checks : sr.Failures).Add($"framebuffer {img.Width}x{img.Height} px = {current} cells × {path.CellWidth}x{path.CellHeight}");
                int opaque = 0;
                for (int i = 3; i < img.Rgba.Length; i += 4) if (img.Rgba[i] == 255) opaque++;
                (opaque * 4 == img.Rgba.Length ? sr.Checks : sr.Failures).Add($"every captured pixel is opaque ({img.Rgba.Length / 4 - opaque} not)");

                ExpectedRaster raster = RasterOracle.Build(screen, step.Caret, masks, path.Placement, img.Width, img.Height);
                sr.UnverifiedCells = raster.UnverifiedCellCount;
                sr.UnverifiedSamples = raster.UnverifiedCells.Take(8).ToList();
                sr.MissingFromFont = raster.MissingFromFont.ToList();
                LogicalScreen snapshot = screen;
                sr.Oracle = PixelComparer.Compare(raster, img, path.PartialTolerance,
                    (c, r) => c < snapshot.Grid.Width && r < snapshot.Grid.Height ? snapshot[c, r].Character.ToString() : "");
                sr.Geometry = GeometryChecks.Run(screen, step.Caret, img, path.CellWidth, path.CellHeight);
                sr.Diacritics = DiacriticChecks.Run(screen, step.Caret, img, masks);

                if (previous != null && previousRaster != null && previous.Width == img.Width && previous.Height == img.Height)
                    sr.Update = CheckUpdate(previous, img, previousRaster, raster, path.CellWidth, path.CellHeight);

                bool failed = !dims || !sr.Oracle.Passed || !sr.Geometry.Passed || !sr.Diacritics.Passed || sr.Update is { Passed: false } || sr.Failures.Count > 0;
                if (failed)
                {
                    sr.Outcome = "Mismatch";
                    sr.Reason = string.Join("; ", new[]
                    {
                        sr.Oracle.Passed ? null : "oracle: " + sr.Oracle.Reason,
                        sr.Geometry.Passed ? null : "geometry: " + string.Join(", ", sr.Geometry.Failed.Select(f => $"{f.Key}×{f.Value}")),
                        sr.Diacritics.Passed ? null : $"diacritics: {sr.Diacritics.FailedCells} cell(s) lost an ink part: " + string.Join("; ", sr.Diacritics.Failures.Take(3)),
                        sr.Update is { Passed: false } u ? $"update: {u.UnexpectedChangedCells} unexpected / {u.MissingChangedCells} missing changed cells" : null,
                        sr.Failures.Count > 0 ? string.Join(", ", sr.Failures) : null,
                    }.Where(s => s != null));
                }

                string stem = $"{prefix}{path.Id}--{fixture}-{step.Name}--{gridName}";
                if (failed || KeepPng(fixture, step.Name, grid, pngs))
                {
                    sr.NativePng = stem + ".native.png";
                    File.WriteAllBytes(System.IO.Path.Combine(dir, sr.NativePng), img.EncodePng());
                }
                if (failed || (fixture == Fixtures.Screen && step.Name == "full" && grid == new GridSize(80, 25)))
                {
                    sr.DiffPng = stem + ".diff.png";
                    File.WriteAllBytes(System.IO.Path.Combine(dir, sr.DiffPng), PixelComparer.DiffImage(raster.Image, raster.Classes, img, path.PartialTolerance).EncodePng());
                }

                if (fixture == Fixtures.Screen && grid == new GridSize(80, 25))
                    Kept[step.Name] = (img, raster, CloneScreen(screen), step.Caret);
                if (fixture == CzechFixture.Id && step.Name == "full" && grid == new GridSize(80, 25))
                    Kept["czech-full"] = (img, raster, CloneScreen(screen), step.Caret);
                previous = img;
                previousRaster = raster;
            }
            catch (Exception ex)
            {
                sr.Outcome = "Error";
                sr.Reason = ex.GetBaseException().Message;
                throw;
            }
        }
        File.WriteAllText(System.IO.Path.Combine(dir, $"{prefix}{path.Id}--{fixture}--{grid}.pixels.json"),
            JsonSerializer.Serialize(new
            {
                schema = "tsharpvision.native-pixels/v1",
                layer = "C",
                path = path.Id,
                fixture,
                grid = grid.ToString(),
                steps = result.Steps.Where(s => s.Fixture == fixture && s.Grid.StartsWith(grid.ToString(), StringComparison.Ordinal)),
            }, Json));
    }

    private static LogicalScreen CloneScreen(LogicalScreen s)
    {
        var copy = new LogicalScreen(s.Grid, default);
        for (int y = 0; y < s.Grid.Height; y++)
            copy.Apply(new CellWrite(0, y, Enumerable.Range(0, s.Grid.Width).Select(x => s[x, y]).ToArray()));
        return copy;
    }

    /// <summary>
    /// Cells whose pixels changed between two consecutive captures must be exactly the cells
    /// whose expected rasters changed: anything else is stale content or collateral damage.
    /// </summary>
    public static UpdateCheck CheckUpdate(NativeImage before, NativeImage after, ExpectedRaster expectedBefore, ExpectedRaster expectedAfter, int cw, int ch)
    {
        var u = new UpdateCheck();
        int cols = after.Width / cw, rows = after.Height / ch;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                bool actual = false, expected = false, unverified = false;
                for (int y = r * ch; y < (r + 1) * ch; y++)
                    for (int x = c * cw; x < (c + 1) * cw; x++)
                    {
                        int p = y * after.Width + x;
                        actual |= before.Rgb(x, y) != after.Rgb(x, y);
                        expected |= expectedBefore.Image.Rgb(x, y) != expectedAfter.Image.Rgb(x, y);
                        unverified |= expectedAfter.Classes[p] == PixelClass.Unverified || expectedBefore.Classes[p] == PixelClass.Unverified;
                    }
                if (expected) u.ExpectedChangedCells++;
                if (actual) u.ActualChangedCells++;
                if (actual && !expected && !unverified) { u.UnexpectedChangedCells++; if (u.Samples.Count < 10) u.Samples.Add($"({c},{r}) changed but should not have"); }
                if (expected && !actual) { u.MissingChangedCells++; if (u.Samples.Count < 10) u.Samples.Add($"({c},{r}) should have changed (stale)"); }
            }
        }
        return u;
    }

    // ── Negative controls: synthetic mutations of real captures must be detected ─────────────

    private static void RunNegativeControls(INativePath path, GlyphMaskOracle masks, PathResult result)
    {
        if (!Kept.TryGetValue("full", out var full) || !Kept.TryGetValue("single-cell", out var single)) return;
        int cw = path.CellWidth, ch = path.CellHeight, tol = path.PartialTolerance;
        foreach (NegativeControl n in NegativeControls.Run(full.Image, full.Raster, full.Screen, full.Caret, single.Image, single.Raster, cw, ch, tol))
            result.NegativeControls.Add(n);
        if (Kept.TryGetValue("czech-full", out var czech))
        {
            foreach (NegativeControl n in NegativeControls.ClippedAccents(czech.Image, czech.Raster, czech.Screen, czech.Caret, masks, tol))
                result.NegativeControls.Add(n);
            if (masks.Uniform)
                foreach (NegativeControl n in NegativeControls.UniformDefects(czech.Image, czech.Raster, czech.Screen, czech.Caret, masks, tol))
                    result.NegativeControls.Add(n);
        }
    }
}

/// <summary>Deliberate corruptions of captured pixels; each one must make the comparison fail.</summary>
public static class NegativeControls
{
    public static List<NegativeControl> Run(NativeImage full, ExpectedRaster fullRaster, LogicalScreen screen, CaretRequest caret,
        NativeImage single, ExpectedRaster singleRaster, int cw, int ch, int tol)
    {
        var list = new List<NegativeControl>();
        string Verdict(NativeImage img, ExpectedRaster raster)
        {
            PixelComparison c = PixelComparer.Compare(raster, img, tol);
            GeometryResult g = GeometryChecks.Run(screen, caret, img, cw, ch);
            return $"oracle {c.Outcome} ({c.Differing} px, {c.DifferingCellCount} cells); geometry {(g.Passed ? "pass" : string.Join(",", g.Failed.Keys))}";
        }
        bool Failed(NativeImage img, ExpectedRaster raster) =>
            !PixelComparer.Compare(raster, img, tol).Passed || !GeometryChecks.Run(screen, caret, img, cw, ch).Passed;

        void Add(string name, string mutation, NativeImage mutated, ExpectedRaster raster)
        {
            list.Add(new NegativeControl { Name = name, Mutation = mutation, Detected = Failed(mutated, raster), Observed = Verdict(mutated, raster) });
        }

        // Baseline sanity: the unmutated capture passes, otherwise the controls prove nothing.
        list.Add(new NegativeControl { Name = "unmutated", Mutation = "none (must pass)", Detected = !Failed(full, fullRaster), Observed = Verdict(full, fullRaster) });

        (int Col, int Row) Find(Func<char, byte, bool> pred)
        {
            for (int r = 0; r < screen.Grid.Height; r++)
                for (int c = 0; c < screen.Grid.Width; c++)
                    if (pred(screen[c, r].Character, screen[c, r].Attr)) return (c, r);
            throw new InvalidOperationException("no suitable cell");
        }

        // 1. One pixel.
        {
            var m = full.Clone();
            (int c, int r) = Find((x, a) => x == ' ' && (a >> 4) != 0);
            int px = c * cw + cw / 2, py = r * ch + ch / 2;
            m.Set(px, py, m.Rgb(px, py) ^ 0x000001);
            Add("one-pixel", $"pixel ({px},{py}) blue channel +-1 in blank cell ({c},{r})", m, fullRaster);
        }
        // 2. Foreground colour of one glyph.
        {
            var m = full.Clone();
            (int c, int r) = Find((x, a) => x == 'P' && (a & 0x0F) == 0x0E);
            int fg = Vga.Rgb[screen[c, r].Attr & 0x0F];
            Recolour(m, c, r, cw, ch, fg, Vga.Rgb[0x0F]);
            Add("foreground-colour", $"ink of 'P' at ({c},{r}) yellow → white", m, fullRaster);
        }
        // 3. Background colour of one cell.
        {
            var m = full.Clone();
            (int c, int r) = Find((x, a) => x == ' ' && (a >> 4) == 1);
            Recolour(m, c, r, cw, ch, Vga.Rgb[1], Vga.Rgb[4]);
            Add("background-colour", $"blank window cell ({c},{r}) blue → red", m, fullRaster);
        }
        // 4. Remove a glyph.
        {
            var m = full.Clone();
            (int c, int r) = Find((x, a) => x == 'A' && a == 0x1E);
            int bg = Vga.Rgb[screen[c, r].Attr >> 4];
            for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++) m.Set(c * cw + x, r * ch + y, bg);
            Add("remove-glyph", $"'A' at ({c},{r}) replaced by background", m, fullRaster);
        }
        // 5. Shift a border cell's ink by one pixel.
        {
            var m = full.Clone();
            (int c, int r) = Find((x, a) => x == '║');
            int bg = Vga.Rgb[screen[c, r].Attr >> 4];
            var copy = m.Clone();
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    m.Set(c * cw + x, r * ch + y, x == 0 ? bg : copy.Rgb(c * cw + x - 1, r * ch + y));
            Add("border-shift", $"'║' at ({c},{r}) shifted right by 1 px", m, fullRaster);
        }
        // 6. Stale pixels after an update: the single-cell frame keeps the old cell.
        {
            var m = single.Clone();
            int sc = screen.Grid.Width / 2, sr = screen.Grid.Height / 2;
            m.Blit(full, sc * cw, sr * ch, cw, ch, sc * cw, sr * ch);
            var update = ChildRunner.CheckUpdate(full, m, fullRaster, singleRaster, cw, ch);
            bool detected = !PixelComparer.Compare(singleRaster, m, tol).Passed && !update.Passed;
            list.Add(new NegativeControl
            {
                Name = "stale-after-update", Mutation = $"single-cell update at ({sc},{sr}) left showing the previous pixels",
                Detected = detected, Observed = $"oracle {PixelComparer.Compare(singleRaster, m, tol).Outcome}; update check {update.MissingChangedCells} missing",
            });
        }
        // 7. Expected framebuffer dimensions differ.
        {
            var bigger = RasterResize(fullRaster, full.Width + cw, full.Height);
            PixelComparison c = PixelComparer.Compare(bigger, full, tol);
            list.Add(new NegativeControl { Name = "dimensions", Mutation = $"expected {bigger.Image.Width}x{bigger.Image.Height}, captured {full.Width}x{full.Height}", Detected = c.Outcome == "DimensionMismatch", Observed = c.Outcome + ": " + c.Reason });
        }
        // 8. Corrupt capture data: a damaged PNG and a truncated pixel buffer.
        {
            byte[] png = full.EncodePng();
            png[png.Length / 2] ^= 0xFF;
            string observed;
            bool detected;
            try { NativeImage.DecodePng(png); detected = false; observed = "decoded without error"; }
            catch (CorruptCaptureException ex) { detected = true; observed = ex.Message; }
            list.Add(new NegativeControl { Name = "corrupt-png", Mutation = "one byte of the native PNG flipped", Detected = detected, Observed = observed });
            try { _ = new NativeImage(full.Width, full.Height, full.Rgba.AsSpan(0, full.Rgba.Length - 4).ToArray()); detected = false; observed = "accepted"; }
            catch (CorruptCaptureException ex) { detected = true; observed = ex.Message; }
            list.Add(new NegativeControl { Name = "truncated-buffer", Mutation = "pixel buffer 4 bytes short", Detected = detected, Observed = observed });
        }
        return list;
    }

    /// <summary>
    /// The original defect, simulated two ways on the Czech fixture. Both must fail the
    /// oracle and, independently of any placement rule, the anti-clipping check.
    /// </summary>
    public static List<NegativeControl> ClippedAccents(NativeImage capture, ExpectedRaster raster, LogicalScreen screen, CaretRequest caret,
        GlyphMaskOracle masks, int tol)
    {
        var list = new List<NegativeControl>();
        int cw = masks.CellWidth, ch = masks.CellHeight;
        NegativeControl Verdict(string name, string mutation, NativeImage img)
        {
            PixelComparison c = PixelComparer.Compare(raster, img, tol);
            DiacriticResult d = DiacriticChecks.Run(screen, caret, img, masks);
            return new NegativeControl
            {
                Name = name, Mutation = mutation, Detected = !c.Passed && !d.Passed,
                Observed = $"oracle {c.Outcome} ({c.Differing} px, {c.DifferingCellCount} cells); anti-clipping {d.FailedCells}/{d.MultiPartCells} multi-part cells failed" +
                           (d.Failures.Count > 0 ? " — e.g. " + d.Failures[0] : ""),
            };
        }

        // 1. The whole frame as the pre-fix GPU rule draws it: every font glyph on the font
        //    baseline with the rows above the ascent trimmed.
        ExpectedRaster legacy = RasterOracle.Build(screen, caret, masks, FontGlyphPlacement.LegacyClipAboveAscent, capture.Width, capture.Height);
        NegativeControl legacyControl = Verdict("clipped-accents-legacy-rule", "frame drawn by the older GPU rule (rows above the ascent trimmed)", legacy.Image);
        legacyControl.Applicable = !SameRgb(legacy.Image, raster.Image);
        list.Add(legacyControl);

        // 2. The real capture with the top rows of every accented capital painted over with the
        //    cell background (accent cut, letter body intact).
        var cut = capture.Clone();
        int cutCells = 0;
        int accentRows = AccentZoneRows(masks);
        for (int r = 0; r < screen.Grid.Height; r++)
            for (int c = 0; c < screen.Grid.Width; c++)
            {
                TScreenChar cell = screen[c, r];
                if (!char.IsUpper(cell.Character) || cell.Character < 'À' || (c + 1) * cw > cut.Width || (r + 1) * ch > cut.Height) continue;
                if (caret.CursorType != 0 && caret.X == c && caret.Y == r) continue;
                int bg = Vga.Rgb[(cell.Attr >> 4) & 0x0F];
                for (int y = 0; y < accentRows; y++)
                    for (int x = 0; x < cw; x++) cut.Set(c * cw + x, r * ch + y, bg);
                cutCells++;
            }
        list.Add(Verdict("clipped-accents-cut-capture", $"top {accentRows} rows (above the plain capitals) of {cutCells} accented capitals in the real capture set to background", cut));
        return list;
    }

    private static bool SameRgb(NativeImage a, NativeImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.Rgb(x, y) != b.Rgb(x, y)) return false;
        return true;
    }

    /// <summary>Cell rows above the ink of the plain capital H as placed: where capital accents live.</summary>
    public static int AccentZoneRows(GlyphMaskOracle masks)
    {
        byte[]? h = masks.Mask('H', 0, 0, FontGlyphPlacement.CellPlaced).Alpha;
        if (h == null) return 2;
        for (int y = 0; y < masks.CellHeight; y++)
            for (int x = 0; x < masks.CellWidth; x++)
                if (h[y * masks.CellWidth + x] != 0) return Math.Max(1, y);
        return 2;
    }

    /// <summary>
    /// Simulated fontFit=Uniform defects on the real Czech capture. Each is a whole frame drawn
    /// by a wrong rule (or the capture with descenders cut) and must fail the comparison with the
    /// Uniform oracle; the geometry check also catches shrunk box drawing.
    /// </summary>
    public static List<NegativeControl> UniformDefects(NativeImage capture, ExpectedRaster raster, LogicalScreen screen, CaretRequest caret,
        GlyphMaskOracle masks, int tol)
    {
        var list = new List<NegativeControl>();
        int cw = masks.CellWidth, ch = masks.CellHeight;
        NegativeControl Verdict(string name, string mutation, NativeImage img, bool geometry = false)
        {
            PixelComparison c = PixelComparer.Compare(raster, img, tol);
            GeometryResult g = GeometryChecks.Run(screen, caret, img, cw, ch);
            bool detected = geometry ? !c.Passed && !g.Passed : !c.Passed;
            return new NegativeControl
            {
                Name = name, Mutation = mutation, Detected = detected,
                Observed = $"oracle {c.Outcome} ({c.Differing} px, {c.DifferingCellCount} cells); geometry {(g.Passed ? "pass" : string.Join(",", g.Failed.Keys))}",
            };
        }
        void AddFrame(string name, string mutation, FontGlyphPlacement placement, bool geometry = false)
        {
            NativeImage frame = RasterOracle.Build(screen, caret, masks, placement, capture.Width, capture.Height).Image;
            NegativeControl n = Verdict(name, mutation, frame, geometry);
            n.Applicable = !SameRgb(frame, raster.Image);
            list.Add(n);
        }

        AddFrame("uniform-ascii-at-original-size", $"text drawn by the Condense rule ({masks.PointSize} pt, ASCII unscaled) instead of {masks.UniformPointSize} pt", FontGlyphPlacement.CondenseRule);
        AddFrame("uniform-accents-shrunk-individually", "rejected strategy: ASCII at the configured size, only overflowing (accented) glyphs shrunk", FontGlyphPlacement.PerGlyphShrink);
        AddFrame("uniform-horizontal-only", $"text condensed horizontally to {masks.UniformPointSize}/{masks.PointSize} but kept at full height", FontGlyphPlacement.HorizontalOnly);
        AddFrame("uniform-baseline-one-row-up", $"Uniform text on row {masks.TextBaseline - 1} instead of {masks.TextBaseline}", FontGlyphPlacement.BaselineOneRowUp);
        AddFrame("uniform-shrunk-box-drawing", "box drawing and block elements drawn at the Uniform text size", FontGlyphPlacement.TextSizedBoxes, geometry: true);

        // Descenders cut: the rows below the baseline of every g j p q y cell painted with the background.
        var cropped = capture.Clone();
        int croppedCells = 0;
        for (int r = 0; r < screen.Grid.Height; r++)
            for (int c = 0; c < screen.Grid.Width; c++)
            {
                TScreenChar cell = screen[c, r];
                if (!"gjpqy".Contains(cell.Character) || (c + 1) * cw > cropped.Width || (r + 1) * ch > cropped.Height) continue;
                if (caret.CursorType != 0 && caret.X == c && caret.Y == r) continue;
                int bg = Vga.Rgb[(cell.Attr >> 4) & 0x0F];
                for (int y = masks.TextBaseline; y < ch; y++)
                    for (int x = 0; x < cw; x++) cropped.Set(c * cw + x, r * ch + y, bg);
                croppedCells++;
            }
        list.Add(Verdict("uniform-cropped-descenders", $"rows {masks.TextBaseline}..{ch - 1} of {croppedCells} descender cells (g j p q y) set to background", cropped));
        return list;
    }

    private static void Recolour(NativeImage m, int c, int r, int cw, int ch, int from, int to)
    {
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
                if (m.Rgb(c * cw + x, r * ch + y) == from) m.Set(c * cw + x, r * ch + y, to);
    }

    private static ExpectedRaster RasterResize(ExpectedRaster r, int w, int h)
    {
        var img = new NativeImage(w, h);
        var classes = new PixelClass[w * h];
        img.Blit(r.Image, 0, 0, Math.Min(w, r.Image.Width), Math.Min(h, r.Image.Height), 0, 0);
        return new ExpectedRaster { Image = img, Classes = classes, CellWidth = r.CellWidth, CellHeight = r.CellHeight, Grid = r.Grid };
    }
}
