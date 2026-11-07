using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>All driver paths of one qualification run, created once (SDL initialises once per process).</summary>
public sealed class DriverPaths : IDisposable
{
    private readonly SdlGpuFixture? _gpu;

    public ConsoleHostPath Console { get; }
    public TerminalOutputPath Terminal { get; }
    public SdlRendererOutputPath SdlRenderer { get; }
    public SdlGpuShaderOutputPath SdlGpuShader { get; }
    public SdlGpuCompositeOutputPath SdlGpuComposite { get; }

    public IReadOnlyList<IOutputPath> All => [Console, Terminal, SdlRenderer, SdlGpuShader, SdlGpuComposite];

    public DriverPaths()
    {
        Console = new ConsoleHostPath();
        Terminal = new TerminalOutputPath();
        SdlRenderer = new SdlRendererOutputPath();
        string? gpuUnavailable = null;
        try { _gpu = new SdlGpuFixture(); }
        catch (Exception ex) { gpuUnavailable = $"SDL_ttf font load failed: {ex.GetBaseException().Message}"; }
        SdlGpuShader = new SdlGpuShaderOutputPath(_gpu, gpuUnavailable);
        SdlGpuComposite = new SdlGpuCompositeOutputPath(_gpu, gpuUnavailable);
    }

    public void Dispose()
    {
        foreach (IOutputPath path in All) { try { path.Dispose(); } catch { } }
        try { _gpu?.Dispose(); } catch { }
    }
}

/// <summary>Runs fixtures through output paths and compares every step with the expected logical screen.</summary>
public static class QualificationRunner
{
    public static PathFixtureResult Run(IOutputPath path, string fixture, GridSize grid) =>
        path is ConsoleHostPath host && host.Unavailable == null ? host.Result(fixture, grid) : RunInProcess(path, fixture, grid);

    public static PathFixtureResult RunInProcess(IOutputPath path, string fixture, GridSize grid)
    {
        var result = new PathFixtureResult { Path = path.Id, Driver = path.Driver, Fixture = fixture, Grid = grid };
        if (path.Unavailable is { } why)
        {
            result.Outcome = Outcomes.Unavailable;
            result.Reason = why;
            return result;
        }
        try
        {
            path.Begin(grid);
            var model = new LogicalScreen(grid, new TScreenChar(' ', 0x07));
            foreach (FixtureStep step in Fixtures.Steps(fixture, grid))
            {
                if (step.ResizeTo is { } target) model = new LogicalScreen(target, new TScreenChar(' ', 0x07));
                foreach (CellWrite w in step.Writes) model.Apply(w);
                var capture = new StepCapture { Step = step.Name, Grid = model.Grid };
                path.Apply(step, capture);
                path.Observe(capture);
                StepComparison comparison = CaptureComparer.Compare(model, capture, step.Caret, path.Capabilities);
                result.Captures.Add(capture);
                result.Comparisons.Add(comparison);
                if (comparison.Outcome != Outcomes.Match) result.Outcome = Outcomes.Mismatch;
            }
        }
        catch (Exception ex)
        {
            result.Outcome = Outcomes.Error;
            result.Reason = ex.GetType().Name + ": " + ex.Message;
        }
        return result;
    }

    /// <summary>Linux only: the real driver initialised on a pseudo-terminal created by <c>script</c>.</summary>
    public static PtySmokeResult RunPtySmoke(GridSize grid)
    {
        if (!OperatingSystem.IsLinux()) return new PtySmokeResult(Outcomes.Unavailable, "PTY smoke runs on Linux only", grid);
        string? script = new[] { "/usr/bin/script", "/bin/script" }.FirstOrDefault(File.Exists);
        if (script == null) return new PtySmokeResult(Outcomes.Unavailable, "util-linux 'script' not installed", grid);

        string command = $"stty cols {grid.Width} rows {grid.Height}; exec {ChildCommand.ShellLine("pty-child", grid.ToString())}";
        var psi = new ProcessStartInfo(script) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (string a in new[] { "-q", "-e", "-c", command, "/dev/null" }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        using var bytes = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) { process.Kill(true); return new PtySmokeResult(Outcomes.Error, "child did not exit within 30 s", grid); }
        copy.Wait(5_000);

        var oracle = new VtOracle(grid.Width, grid.Height);
        byte[] output = bytes.ToArray();
        oracle.Feed(output);
        oracle.Settle();

        var model = new LogicalScreen(grid, new TScreenChar(' ', 0x07));
        foreach (FixtureStep step in PtyChildSteps(grid))
            foreach (CellWrite w in step.Writes) model.Apply(w);
        var capture = new StepCapture { Step = "pty", Grid = grid, Cells = oracle.SemanticCells(lastAlternateScreen: true) };
        StepComparison cmp = CaptureComparer.Compare(model, capture, new CaretRequest(0, 0, 0),
            new PathCapabilities(CaretModel.Native, false, true, "")) ;
        // The caret check needs the live cursor; the PTY run ends after Shutdown, so only cells are compared here.
        cmp.Failures.RemoveAll(f => f.StartsWith("cursor") || f.StartsWith("no cursor"));
        if (cmp.TextMismatches + cmp.FgMismatches + cmp.BgMismatches == 0 && cmp.Failures.Count == 0) cmp.Outcome = Outcomes.Match;

        var checks = new List<string>
        {
            $"{output.Length} bytes captured from the PTY (exit code {process.ExitCode})",
            $"alternate screen entered and left: {oracle.HasAlternateSnapshot}",
            $"mouse modes left enabled: {(oracle.MouseModes.Count == 0 ? "none" : string.Join(",", oracle.MouseModes))}",
            $"cursor visible after shutdown: {oracle.CursorVisible}",
        };
        bool ok = process.ExitCode == 0 && oracle.HasAlternateSnapshot && oracle.MouseModes.Count == 0 && oracle.CursorVisible
                  && cmp.Outcome == Outcomes.Match;
        return new PtySmokeResult(ok ? Outcomes.Match : Outcomes.Mismatch, stderr.Trim(), grid)
        {
            Comparison = cmp,
            Checks = checks,
            OracleDiagnostics = oracle.Diagnostics.ToList(),
            TokenCounts = new Dictionary<string, int>(oracle.TokenCounts),
        };
    }

    /// <summary>What the PTY child draws: the screen fixture's full, single-cell, rect and edge steps.</summary>
    public static IEnumerable<FixtureStep> PtyChildSteps(GridSize grid) =>
        Fixtures.Steps(Fixtures.Screen, grid).Where(s => s.Name is "full" or "single-cell" or "rect" or "edge");

    /// <summary>The child side: the real driver on the real TTY, nothing else written to stdout.</summary>
    public static int PtyChild(GridSize grid)
    {
        var driver = new TSharpVision.Drivers.Terminal.AnsiTerminalDriver();
        driver.Initialize();
        if (driver.GetCols() != grid.Width || driver.GetRows() != grid.Height)
        {
            driver.Shutdown();
            System.Console.Error.WriteLine($"PTY size {driver.GetCols()}x{driver.GetRows()} != {grid} (attached TTY?)");
            return 3;
        }
        foreach (FixtureStep step in PtyChildSteps(grid))
            foreach (CellWrite w in step.Writes) driver.WriteBuf(w.X, w.Y, w.Cells.Length, 1, w.Cells);
        driver.SetCaretPosition(0, 0);
        driver.Shutdown();
        return 0;
    }
}

public sealed record PtySmokeResult(string Outcome, string Detail, GridSize Grid)
{
    public StepComparison? Comparison { get; init; }
    public List<string> Checks { get; init; } = new();
    public List<string> OracleDiagnostics { get; init; } = new();
    public Dictionary<string, int> TokenCounts { get; init; } = new();
}

/// <summary>Platform facts written into run.json and the report.</summary>
public static class Platform
{
    public static object Describe() => new
    {
        os = RuntimeInformation.OSDescription,
        arch = RuntimeInformation.ProcessArchitecture.ToString(),
        runtime = RuntimeInformation.FrameworkDescription,
        framework = typeof(TScreen).Assembly.GetName().Version?.ToString(),
        machine = Environment.MachineName,
    };

    public static string Name => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "other";
}

internal static class Text
{
    public static string Rows(SemanticCell[] cells, GridSize grid, Func<SemanticCell, string> pick)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++) sb.Append(pick(cells[y * grid.Width + x]));
            if (y < grid.Height - 1) sb.Append('\n');
        }
        return sb.ToString();
    }
}
