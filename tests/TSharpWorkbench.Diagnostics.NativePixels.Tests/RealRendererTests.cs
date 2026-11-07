using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativePixels.Tests;

/// <summary>
/// Layer C on the real SDL renderers: the production drivers render the driver-output fixtures (composite
/// screen with Czech text, borders, shadow, 16 colours, blocks; single-cell, rectangle and edge
/// updates; block and underline caret; resize 80x25↔140x40, 20x6→80x25; 20x6 clipping) and the
/// pixels read back from the renderer are compared with the independent raster oracle. A path that
/// cannot run here is skipped with the reason — never passed.
/// </summary>
[Collection(RealRendererCollection.Name)]
public sealed class RealRendererTests(RealRendererFixture fixture)
{
    public static TheoryData<string> RequiredPaths => new() { "sdl2d-software", "sdl-gpu-shader", "sdl-gpu-fallback" };

    private PathResult Require(string path)
    {
        var (result, dir, log) = fixture.Run(path);
        Assert.True(result != null, $"{path}: child produced no result ({dir})\n{log}");
        Skip.If(result!.Outcome is "PlatformUnavailable" or "BackendUnavailable", $"{path}: {result.Unavailable}");
        Assert.True(result.Error == null, $"{path}: {result.Error}");
        return result;
    }

    private static StepResult Step(PathResult r, string fixture, string step, string grid) =>
        r.Steps.Single(s => s.Fixture == fixture && s.Step == step && s.Grid == grid);

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Every_step_matches_the_independent_oracle(string path)
    {
        PathResult r = Require(path);
        Assert.Equal(39, r.Steps.Count); // screen 3 grids × 7 steps + resize 3 grids × 2 steps + czech 3 grids × 4 steps
        foreach (StepResult s in r.Steps)
        {
            Assert.True(s.Outcome == "Match", $"{path} {s.Fixture} {s.Step} {s.Grid}: {s.Reason}");
            Assert.Equal(ExpectedSource.IndependentOracle, s.Oracle!.ExpectedSource);
            Assert.Equal(0, s.Oracle.Differing);
            Assert.Equal(0, s.Oracle.AlphaDifferences);
            Assert.True(s.Oracle.ExactMatches > 0);
        }
        Assert.Equal("Match", r.Outcome);
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Framebuffer_dimensions_follow_the_renderer_cell_size(string path)
    {
        PathResult r = Require(path);
        string[] cell = r.Identity["cell"].Split('x');
        int cw = int.Parse(cell[0]), ch = int.Parse(cell[1]);
        foreach (var (grid, w, h) in new[] { ("80x25", 80, 25), ("140x40", 140, 40), ("20x6", 20, 6) })
        {
            StepResult full = Step(r, "screen", "full", grid);
            Assert.Equal((w * cw, h * ch), (full.Width, full.Height));
        }
        Assert.Contains(r.Steps, s => s.Checks.Any(c => c.StartsWith("framebuffer", StringComparison.Ordinal)));
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Partial_updates_change_exactly_the_expected_cells(string path)
    {
        PathResult r = Require(path);
        foreach (string grid in new[] { "80x25", "140x40", "20x6" })
        {
            foreach (string step in new[] { "single-cell", "rect", "edge", "cursor-block", "cursor-underline", "cursor-hidden" })
            {
                UpdateCheck u = Step(r, "screen", step, grid).Update!;
                Assert.True(u.Passed, $"{path} {step} {grid}: {string.Join("; ", u.Samples)}");
                Assert.True(u.ExpectedChangedCells > 0, $"{path} {step} {grid}: the step changed nothing");
                Assert.Equal(u.ExpectedChangedCells, u.ActualChangedCells);
            }
            Assert.Equal(1, Step(r, "screen", "single-cell", grid).Update!.ExpectedChangedCells);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Resize_redraws_at_the_new_size_without_stale_content(string path)
    {
        PathResult r = Require(path);
        foreach (var (grid, target) in new[] { ("80x25-to-140x40", "140x40"), ("140x40-to-80x25", "80x25"), ("20x6-to-80x25", "80x25") })
        {
            StepResult after = Step(r, "resize", "after", grid);
            Assert.Equal("Match", after.Outcome);
            Assert.Contains($"SDL WindowResized reached the driver: {target}", after.Checks);
            Assert.Contains("cmScreenResized queued", after.Checks);
            Assert.Contains($"TScreen.ScreenBuffer reallocated at {target}", after.Checks);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Borders_shadows_blocks_colours_and_caret_pass_the_pixel_only_checks(string path)
    {
        PathResult r = Require(path);
        var checkedTotals = new Dictionary<string, int>();
        foreach (StepResult s in r.Steps)
        {
            Assert.True(s.Geometry!.Passed, $"{path} {s.Fixture} {s.Step} {s.Grid}: {string.Join("; ", s.Geometry.Failures)}");
            foreach (var (k, v) in s.Geometry.Checked) checkedTotals[k] = checkedTotals.GetValueOrDefault(k) + v;
        }
        foreach (string check in new[] { "box-arms", "box-join-horizontal", "box-join-vertical", "shadow-cell-colours", "block-full",
                     "block-upper-half", "block-lower-half", "block-left-half", "block-right-half", "glyph-has-ink", "cell-colour-range",
                     "blank-cell-background", "caret-underline-bar" })
            Assert.True(checkedTotals.GetValueOrDefault(check) > 0, $"{path}: check '{check}' never ran");
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Every_negative_control_is_detected(string path)
    {
        PathResult r = Require(path);
        Assert.Equal(12, r.NegativeControls.Count);
        foreach (NegativeControl n in r.NegativeControls)
            Assert.True(n.Detected, $"{path} {n.Name}: {n.Mutation} → {n.Observed}");
    }

    [SkippableTheory]
    [MemberData(nameof(RequiredPaths))]
    public void Native_resources_are_released(string path)
    {
        PathResult r = Require(path);
        Assert.Contains(r.Cleanup, c => c.StartsWith("SDL_WasInit after driver shutdown = 0", StringComparison.Ordinal));
        Assert.Contains(r.Cleanup, c => c == "TTF_WasInit after driver shutdown = 0");
    }

    [SkippableFact]
    public void Software_renderer_is_the_cpu_backend()
    {
        PathResult r = Require("sdl2d-software");
        Assert.Equal("software", r.Identity["render.driver.actual"]);
        Assert.Equal("offscreen", r.Identity["video.driver"]);
        // SDL 2D draws the shared cell masks 1:1, so no glyph is scaled and every cell is verified.
        Assert.All(r.Steps, s => Assert.Equal(0, s.UnverifiedCells));
        Assert.True(r.Steps.Max(s => s.Oracle!.MaxPartialDeviation) <= r.PartialTolerance);
    }

    [SkippableFact]
    public void Gpu_shader_path_executes_the_shader_pipeline_on_a_real_device()
    {
        PathResult r = Require("sdl-gpu-shader");
        Assert.Equal("True", r.Identity["gpu.shaderPipelineActive"]);
        Assert.False(string.IsNullOrEmpty(r.Identity["gpu.backend"]));
        Assert.All(r.Steps, s => Assert.Equal("shader", s.CaptureDetails["gpu.mode"]));
        Assert.All(r.Steps, s => Assert.Equal(0, s.UnverifiedCells));
        // Frames are identified: each capture is a later frame than the one before.
        Assert.True(r.Steps.Zip(r.Steps.Skip(1)).All(p => p.Second.Frame > p.First.Frame));
    }

    [SkippableFact]
    public void Gpu_fallback_path_runs_the_production_fallback_selection()
    {
        PathResult r = Require("sdl-gpu-fallback");
        Assert.Equal("False", r.Identity["gpu.shaderPipelineActive"]);
        Assert.All(r.Steps, s => Assert.Equal("cpu-composite", s.CaptureDetails["gpu.mode"]));
    }
}
