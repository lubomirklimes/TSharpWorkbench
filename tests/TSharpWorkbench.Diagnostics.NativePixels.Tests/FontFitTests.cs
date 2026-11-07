using System.Text.Json;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativePixels.Tests;

/// <summary>
/// <c>[graphics] fontFit</c>. The product's rasteriser is compared with the oracle per mode
/// (<c>font-fit-audit</c>, own process), and the real renderers run with their configuration read
/// from a real .cfg file: Uniform must match the Uniform oracle exactly, an explicit Condense must
/// render the pixels, and an invalid value must stop the driver.
/// </summary>
[Collection(RealRendererCollection.Name)]
public sealed class FontFitTests(RealRendererFixture fixture)
{
    public static TheoryData<string> RequiredPaths => new() { "sdl2d-software", "sdl-gpu-shader", "sdl-gpu-fallback" };

    private static readonly Lazy<(FontFitAuditResult? Result, string Log)> Audit = new(RunAudit);

    private static (FontFitAuditResult?, string) RunAudit()
    {
        string file = Path.Combine(RealRendererFixture.OutputRoot, "font-fit-metrics.json");
        Directory.CreateDirectory(RealRendererFixture.OutputRoot);
        string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "TSharpWorkbench.Diagnostics.NativePixels.exe" : "TSharpWorkbench.Diagnostics.NativePixels");
        string fonts = FontInstalled("jetbrains20") ? "consolas20,jetbrains20" : "consolas20";
        var (code, stdout, stderr) = Parent.Exec(exe, ["font-fit-audit", file, "--fonts", fonts], TimeSpan.FromSeconds(180));
        FontFitAuditResult? r = File.Exists(file) ? JsonSerializer.Deserialize<FontFitAuditResult>(File.ReadAllText(file), ChildRunner.Json) : null;
        return (r, $"exit {code}\n{stdout}\n{stderr}");
    }

    private static bool FontInstalled(string id) =>
        FontSpec.IsInstalled(id);

    private static FontFitFontResult RequireAudit(string fontId)
    {
        Skip.IfNot(OperatingSystem.IsWindows() && File.Exists(FontSpec.Default.Path), "the pinned font (Consolas) is Windows-only");
        Skip.IfNot(FontInstalled(fontId), $"{FontSpec.Known[fontId].FontName} is not installed");
        var (r, log) = Audit.Value;
        Assert.True(r != null, "font-fit-audit produced no result\n" + log);
        Assert.True(r!.Error == null, r.Error);
        return r.Fonts.Single(f => f.FontId == fontId);
    }

    private PathResult Require(string path, string fit)
    {
        Skip.IfNot(OperatingSystem.IsWindows() && File.Exists(FontSpec.Default.Path), "the pinned font (Consolas) is Windows-only");
        var (result, dir, log) = fixture.Run(path, fit);
        Assert.True(result != null, $"{path} {fit}: child produced no result ({dir})\n{log}");
        Skip.If(result!.Outcome is "PlatformUnavailable" or "BackendUnavailable", $"{path}: {result.Unavailable}");
        Assert.True(result.Error == null, $"{path}: {result.Error}");
        return result;
    }

    private PathResult RequireDefault(string path)
    {
        var (result, dir, log) = fixture.Run(path);
        Assert.True(result != null, $"{path}: child produced no result ({dir})\n{log}");
        Skip.If(result!.Outcome is "PlatformUnavailable" or "BackendUnavailable", $"{path}: {result.Unavailable}");
        return result;
    }

    private NativeImage Png(string path, string? fit, string fixtureName, string step, string grid)
    {
        PathResult r = fit == null ? RequireDefault(path) : Require(path, fit);
        string dir = fit == null ? fixture.Run(path).Dir : fixture.Run(path, fit).Dir;
        StepResult s = r.Steps.Single(x => x.Fixture == fixtureName && x.Step == step && x.Grid == grid);
        Assert.NotNull(s.NativePng);
        return NativeImage.DecodePng(File.ReadAllBytes(Path.Combine(dir, s.NativePng!)));
    }

    // ── Product rasteriser vs oracle, both modes (no window) ──────────────────────

    [SkippableTheory]
    [InlineData("consolas20")]
    [InlineData("jetbrains20")]
    public void Every_audit_check_passes(string fontId)
    {
        FontFitFontResult f = RequireAudit(fontId);
        Assert.NotEmpty(f.Checks);
        Assert.All(f.Checks, c => Assert.True(c.Value, $"{fontId}: {c.Key}"));
        Assert.All(f.Glyphs, g => Assert.True(g.CondenseDifferingPixels == 0 && g.UniformDifferingPixels == 0,
            $"{fontId} {g.Char} {g.Codepoint}: condense {g.CondenseDifferingPixels} / uniform {g.UniformDifferingPixels} px differ from the oracle"));
    }

    [SkippableFact]
    public void Consolas20_Uniform_draws_all_text_at_one_reduced_size_derived_from_the_font()
    {
        FontFitFontResult f = RequireAudit("consolas20");
        Assert.Equal("11x20", f.Cell);                       // cell geometry unchanged
        Assert.Equal("20", f.Condense.TextPointSize);
        Assert.Equal("18", f.Uniform.TextPointSize);         // measured, not configured
        Assert.Equal(18, f.Uniform.OracleTextPointSize);
        Assert.Equal(2, f.Uniform.RejectedSizes.Count);      // 20 pt (À too tall) and 19 pt (Æ too wide)
        Assert.Equal(16, f.Uniform.TextBaseline);
        Assert.Equal(278, f.Uniform.RepertoireGlyphs);

        // One size: no ordinary text glyph is re-rasterised; under Condense the accented capitals are.
        var text = f.Glyphs.Where(g => g.InRepertoire).ToList();
        Assert.All(text, g => Assert.Null(g.UniformFit));
        Assert.Contains(text, g => g.Char == "Ž" && g.CondenseFit != null);

        // Plain ASCII shrinks too (H 13 → 11 rows), and accented capitals keep their accents on the same baseline.
        FontFitGlyph H = text.Single(g => g.Char == "H"), Z = text.Single(g => g.Char == "Ž"), x = text.Single(g => g.Char == "x");
        Assert.Equal(13, H.CondenseInk[3] - H.CondenseInk[1]);
        Assert.True(H.UniformInk[3] - H.UniformInk[1] < 13, $"H uniform ink {string.Join(",", H.UniformInk)}");
        Assert.True(x.UniformInk[3] - x.UniformInk[1] < x.CondenseInk[3] - x.CondenseInk[1], "x-height reduced");
        Assert.Equal(H.UniformInk[3], Z.UniformInk[3]);      // bottoms on the baseline row
        Assert.Equal(16, H.UniformInk[3]);
        Assert.True(Z.UniformInk[3] - Z.UniformInk[1] > H.UniformInk[3] - H.UniformInk[1], "Ž keeps its caron above the H-height body");
        Assert.All(text, g => Assert.Null(g.UniformPartsVerdict));

        // Descenders reach the cell's bottom rows but stay inside it.
        FontFitGlyph gch = text.Single(g => g.Char == "g");
        Assert.True(gch.UniformInk[3] <= 20 && gch.UniformInk[3] > 16, $"g rows {gch.UniformInk[1]}..{gch.UniformInk[3]}");

        // Only glyphs outside the repertoire may take the Condense fit from the common size.
        Assert.All(f.Glyphs.Where(g => g.UniformFit != null), g => Assert.False(g.InRepertoire, g.Char));
    }

    [SkippableFact]
    public void A_font_whose_text_already_fits_keeps_its_size_and_pixels_under_Uniform()
    {
        FontFitFontResult f = RequireAudit("jetbrains20");
        Assert.Equal("12x27", f.Cell);
        Assert.Equal("20", f.Uniform.TextPointSize);
        Assert.Empty(f.Uniform.RejectedSizes);
        Assert.Equal(f.Condense.TextBaseline, f.Uniform.TextBaseline);
        Assert.All(f.Glyphs.Where(g => g.InRepertoire), g => Assert.Equal(g.CondenseInk, g.UniformInk));
    }

    // ── Real renderers configured from a real .cfg file ───────────────────────────

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Uniform_frames_match_the_Uniform_oracle_on_every_step(string path)
    {
        PathResult r = Require(path, "Uniform");
        Assert.Equal("Uniform", r.Identity["fontFit.configured"]);
        Assert.Equal("Uniform", r.Identity["fontFit.driver"]);
        Assert.Equal("Uniform", r.Identity["fontFit.renderer"]);
        Assert.Equal("uniform--consolas20.cfg", r.Identity["fontFit.configFile"]);
        Assert.Equal("18", r.Identity["fontFit.textPointSize"]);
        Assert.Equal("16", r.Identity["fontFit.textBaseline"]);
        Assert.Equal("11x20", r.Identity["cell"]);
        Assert.Equal(39, r.Steps.Count);
        foreach (StepResult s in r.Steps)
        {
            Assert.True(s.Outcome == "Match", $"{path} uniform {s.Fixture} {s.Step} {s.Grid}: {s.Reason}");
            Assert.Equal(0, s.Oracle!.Differing);
            Assert.Equal(0, s.UnverifiedCells);
        }
        Assert.Equal("Match", r.Outcome);
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Uniform_negative_controls_are_all_detected(string path)
    {
        PathResult r = Require(path, "Uniform");
        string[] uniform =
        [
            "uniform-ascii-at-original-size", "uniform-accents-shrunk-individually", "uniform-horizontal-only",
            "uniform-baseline-one-row-up", "uniform-shrunk-box-drawing", "uniform-cropped-descenders",
        ];
        Assert.All(uniform, n => Assert.Contains(r.NegativeControls, c => c.Name == n));
        Assert.Contains(r.NegativeControls, c => c.Name == "clipped-accents-cut-capture");
        Assert.Contains(r.NegativeControls, c => c.Name == "dimensions");
        Assert.All(r.NegativeControls, n => Assert.True(n.Applicable && n.Detected, $"{path}: {n.Name} not detected — {n.Observed}"));
        Assert.Equal(18, r.NegativeControls.Count);
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void An_explicit_Condense_cfg_renders_exactly_the_default_pixels(string path)
    {
        PathResult c = Require(path, "Condense");
        Assert.Equal("Condense", c.Identity["fontFit.renderer"]);
        Assert.Equal("Match", c.Outcome);
        foreach (var (fix, grid) in new[] { ("screen", "80x25"), ("czech", "80x25") })
        {
            NativeImage a = Png(path, null, fix, "full", grid), b = Png(path, "Condense", fix, "full", grid);
            PixelComparison cmp = PixelComparer.CompareExact(a, b, 11, 20, ExpectedSource.RendererCapture);
            Assert.True(cmp.Differing == 0, $"{path} {fix}: Condense from the cfg differs from the default by {cmp.Differing} px");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Uniform_changes_the_text_but_not_the_frame_geometry(string path)
    {
        PathResult c = Require(path, "Condense"), u = Require(path, "Uniform");
        Assert.Equal(c.Steps.Select(s => (s.Fixture, s.Step, s.Grid, s.Width, s.Height)), u.Steps.Select(s => (s.Fixture, s.Step, s.Grid, s.Width, s.Height)));
        NativeImage a = Png(path, "Condense", "czech", "full", "80x25"), b = Png(path, "Uniform", "czech", "full", "80x25");
        PixelComparison cmp = PixelComparer.CompareExact(a, b, 11, 20, ExpectedSource.RendererCapture);
        Assert.True(cmp.Differing > 5000, $"{path}: Uniform differs from Condense by only {cmp.Differing} px");
    }

    [SkippableFact]
    public void Uniform_is_pixel_identical_on_GPU_shader_and_fallback_and_within_one_step_on_SDL_2D()
    {
        foreach (var (fix, grid) in new[] { ("screen", "80x25"), ("czech", "80x25") })
        {
            NativeImage shader = Png("sdl-gpu-shader", "Uniform", fix, "full", grid);
            NativeImage fallback = Png("sdl-gpu-fallback", "Uniform", fix, "full", grid);
            NativeImage software = Png("sdl2d-software", "Uniform", fix, "full", grid);
            Assert.Equal(0, PixelComparer.CompareExact(shader, fallback, 11, 20, ExpectedSource.RendererCapture).Differing);
            Assert.True(PixelComparer.CompareExact(software, shader, 11, 20, ExpectedSource.RendererCapture).MaxChannelDeviation <= 1);
        }
    }

    [SkippableTheory]
    [InlineData("sdl2d-software")]
    [InlineData("sdl-gpu-shader")]
    public void An_invalid_fontFit_in_the_cfg_stops_the_driver_instead_of_falling_back(string path)
    {
        // The configuration loader rejects the value, so no driver is created.
        Skip.IfNot(OperatingSystem.IsWindows() && File.Exists(FontSpec.Default.Path), "the pinned font (Consolas) is Windows-only");
        var (result, dir, log) = fixture.Run(path, "Smaller");
        Assert.True(result != null, $"{path}: child produced no result ({dir})\n{log}");
        Assert.Empty(result!.Steps);
        Assert.NotNull(result.Unavailable);
        Assert.Contains("[graphics] fontFit value 'Smaller'", result.Unavailable);
        Assert.Contains("Condense, Uniform", result.Unavailable);
    }

    // ── FontFit selected in code, no .cfg file ───────────────────────────

    [SkippableTheory]
    [InlineData("sdl2d-software", "Uniform")]
    [InlineData("sdl-gpu-shader", "Uniform")]
    [InlineData("sdl-gpu-fallback", "Uniform")]
    [InlineData("sdl-gpu-shader", "Condense")]
    public void A_programmatic_FontFit_renders_exactly_like_the_same_mode_from_a_cfg(string path, string fit)
    {
        Skip.IfNot(OperatingSystem.IsWindows() && File.Exists(FontSpec.Default.Path), "the pinned font (Consolas) is Windows-only");
        var (p, dir, log) = fixture.RunProgrammatic(path, fit);
        Assert.True(p != null, $"{path} programmatic {fit}: child produced no result ({dir})\n{log}");
        Skip.If(p!.Outcome is "PlatformUnavailable" or "BackendUnavailable", $"{path}: {p.Unavailable}");
        Assert.True(p.Error == null, $"{path}: {p.Error}");
        Assert.Equal($"{fit} (programmatic)", p.Identity["fontFit.configured"]);
        Assert.Equal("(in-memory configuration)", p.Identity["fontFit.configFile"]);
        Assert.Equal(fit, p.Identity["fontFit.driver"]);
        Assert.Equal(fit, p.Identity["fontFit.renderer"]);
        Assert.Equal("11x20", p.Identity["cell"]);
        Assert.Equal("Match", p.Outcome);

        PathResult c = Require(path, fit);
        Assert.Equal(c.Identity["fontFit.textPointSize"], p.Identity["fontFit.textPointSize"]);
        foreach (var (fix, grid) in new[] { ("screen", "80x25"), ("czech", "80x25") })
        {
            StepResult s = p.Steps.Single(x => x.Fixture == fix && x.Step == "full" && x.Grid == grid);
            NativeImage a = NativeImage.DecodePng(File.ReadAllBytes(Path.Combine(dir, s.NativePng!)));
            NativeImage b = Png(path, fit, fix, "full", grid);
            PixelComparison cmp = PixelComparer.CompareExact(a, b, 11, 20, ExpectedSource.RendererCapture);
            Assert.True(cmp.Differing == 0, $"{path} {fix}: programmatic {fit} differs from the cfg by {cmp.Differing} px");
        }
    }
}
