using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>Exact comparison of two renderers' captures of the same input (consistency, not correctness).</summary>
public sealed class CrossRendererResult
{
    public required string A { get; init; }
    public required string B { get; init; }
    public required string Capture { get; init; }
    public required PixelComparison Comparison { get; init; }
    public string? DiffPng { get; set; }
}

/// <summary>A native screenshot proposed for owner review. Never approved automatically.</summary>
/// <param name="Key">
/// Baseline identity: fontFit / font / path / capture. Condense and Uniform captures never share a
/// key; the native-pixel default run has fontFit "default".
/// </param>
public sealed record BaselineCandidate(string File, string Sha256, int Width, int Height, string Status, string Key = "", string FontFit = "default");

/// <summary>One font × fit-mode configuration every path runs with.</summary>
/// <param name="ConfigFile">The generated .cfg file (null for the native-pixel default run).</param>
public sealed record FontVariant(string Fit, string FontId, string? ConfigFile)
{
    public string Prefix => ConfigFile == null ? "" : $"{Fit.ToLowerInvariant()}--{FontId}--";
}

/// <summary>Orchestrates one run: one child process per renderer path, then cross-renderer checks and reports.</summary>
public static class Parent
{
    public const string Schema = "tsharpvision.native-pixels/v1";

    /// <summary>Paths whose qualification native-pixel requires.</summary>
    public static readonly string[] Required = ["sdl2d-software", "sdl-gpu-shader", "sdl-gpu-fallback"];

    /// <summary>
    /// Known not runnable for readback, with the evidence. SDL 3.4.10's own "gpu" SDL_Renderer
    /// backend faults (0xC0000005) inside SDL_RenderReadPixels on a hidden window; it is not a
    /// backend TSharpVision selects, so it is reported instead of crashing every run.
    /// </summary>
    public static readonly Dictionary<string, string> Excluded = new()
    {
        ["sdl2d-gpu"] = "NotRun: SDL_RenderReadPixels faults with an access violation (0xC0000005) inside SDL 3.4.10's 'gpu' SDL_Renderer backend (hidden window); readback not possible on that backend",
    };

    public static int Run(string outputRoot, string[]? only, List<string> tvs1, PngPolicy png, List<GridSize> grids, TimeSpan timeout,
        string[]? fits = null, string[]? fonts = null)
    {
        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        string dir = Path.Combine(outputRoot, "run-" + runId);
        Directory.CreateDirectory(dir);

        // One real .cfg per font × fontFit, read by every child through the product loader.
        var variants = new List<FontVariant>();
        if (fits == null) variants.Add(new FontVariant("Condense", "consolas20", null));
        else
            foreach (string fontId in fonts ?? ["consolas20"])
                foreach (string fit in fits)
                {
                    string mode = fit.Equals("uniform", StringComparison.OrdinalIgnoreCase) ? "Uniform" : fit.Equals("condense", StringComparison.OrdinalIgnoreCase) ? "Condense" : throw new ArgumentException($"unknown fontFit '{fit}'");
                    var (fontName, size) = FontSpec.Known[fontId];
                    string cfg = Path.Combine(dir, $"{mode.ToLowerInvariant()}--{fontId}.cfg");
                    File.WriteAllText(cfg, FontSpec.ConfigText(fontName, size, mode));
                    variants.Add(new FontVariant(mode, fontId, cfg));
                }
        var began = DateTimeOffset.UtcNow;
        string self = Path.ChangeExtension(typeof(Parent).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null);

        // Backend inventory in its own process.
        var (invCode, invOut, _) = Exec(self, ["backends"], TimeSpan.FromSeconds(60));
        BackendInventory inventory = invCode == 0 ? JsonSerializer.Deserialize<BackendInventory>(invOut, ChildRunner.Json) ?? new() : new();

        var paths = new List<string>(Required);
        paths.AddRange(inventory.RenderDrivers.Where(d => d != "software").Select(d => "sdl2d-" + d));
        if (inventory.GpuDrivers.Contains("vulkan") && inventory.GpuDrivers.FirstOrDefault() != "vulkan")
            paths.AddRange(["sdl-gpu-shader-vulkan", "sdl-gpu-fallback-vulkan"]);
        if (only != null) paths = paths.Where(only.Contains).ToList();

        if (fits != null)
        {
            // Product masks per mode against the oracle, metrics and leakage checks (own process, no window).
            var auditArgs = new List<string> { "font-fit-audit", Path.Combine(dir, "font-fit-metrics.json"), "--fonts", string.Join(',', variants.Select(v => v.FontId).Distinct()) };
            var (ac, ao, ae) = Exec(self, auditArgs, TimeSpan.FromSeconds(180));
            File.WriteAllText(Path.Combine(dir, "font-fit-audit--child.log"), $"exit {ac}\n--- stdout\n{ao}\n--- stderr\n{ae}");
            Console.WriteLine($"font-fit-audit: exit {ac} {ao.Trim()}");
        }

        var results = new List<PathResult>();
        foreach (FontVariant variant in variants)
        foreach (string id in paths)
        {
            string label = variant.Prefix + id;
            if (Excluded.TryGetValue(id, out string? why))
            {
                results.Add(new PathResult { Path = id, Variant = variant.Prefix, Outcome = "NotRun", Unavailable = why });
                Console.WriteLine($"{label,-48} NotRun — {why}");
                continue;
            }
            bool required = Required.Contains(id);
            var childArgs = new List<string> { "child", id, dir, "--png", (required ? png : PngPolicy.Minimal).ToString(), "--grids", string.Join(',', grids) };
            foreach (string f in tvs1) { childArgs.Add("--tvs1"); childArgs.Add(f); }
            if (variant.ConfigFile != null) childArgs.AddRange(["--config", variant.ConfigFile, "--font-id", variant.FontId]);
            var sw = Stopwatch.StartNew();
            var (code, stdout, stderr) = Exec(self, childArgs, timeout);
            File.WriteAllText(Path.Combine(dir, $"{label}--child.log"), $"exit {code}\n--- stdout\n{stdout}\n--- stderr\n{stderr}");
            string resultFile = Path.Combine(dir, ChildRunner.ResultName(id, variant.Prefix));
            PathResult r = File.Exists(resultFile)
                ? JsonSerializer.Deserialize<PathResult>(File.ReadAllText(resultFile), ChildRunner.Json)!
                : new PathResult { Path = id, Outcome = "Error", Error = code == -1 ? $"child timed out after {timeout.TotalSeconds:F0} s and was killed" : $"child exited {code} without a result: {Tail(stderr + stdout)}" };
            r.Variant = variant.Prefix;
            results.Add(r);
            Console.WriteLine($"{label,-48} {r.Outcome,-10} {r.Steps.Count(s => s.Outcome == "Match")}/{r.Steps.Count} steps, negative controls {r.NegativeControls.Count(n => n.Applicable && n.Detected)}/{r.NegativeControls.Count(n => n.Applicable)}{(r.NegativeControls.Any(n => !n.Applicable) ? $" (+{r.NegativeControls.Count(n => !n.Applicable)} not applicable)" : "")}, {sw.Elapsed.TotalSeconds:F1} s{(r.Unavailable is { } u ? " — " + u : "")}{(r.Error is { } e ? " — " + e.Split('\n')[0] : "")}");
        }

        var cross = new List<CrossRendererResult>();
        var candidates = new List<BaselineCandidate>();
        foreach (FontVariant variant in variants)
        {
            List<PathResult> mine = results.Where(r => r.Variant == variant.Prefix).ToList();
            cross.AddRange(CrossRenderer(dir, mine, variant.Prefix));
            candidates.AddRange(Candidates(dir, mine, variant));
        }
        cross.AddRange(CrossMode(dir, results, variants));
        var finished = DateTimeOffset.UtcNow;

        bool requiredOk = Required.Where(paths.Contains).All(id => results.Where(r => r.Path == id).All(r => r is { Outcome: "Match", Steps.Count: > 0 }));
        bool anyMismatch = results.Any(r => r.Outcome == "Mismatch");
        bool anyError = results.Any(r => r.Outcome == "Error");
        bool requiredUnavailable = Required.Where(paths.Contains).Any(id => results.Any(r => r.Path == id && r.Outcome is "BackendUnavailable" or "PlatformUnavailable"));
        int exit = anyError ? 2 : anyMismatch ? 1 : requiredUnavailable ? 3 : requiredOk ? 0 : 1;

        Dictionary<string, string> machine = Machine();
        var run = new
        {
            schema = Schema,
            layer = "C",
            runId,
            began,
            finished,
            exitCode = exit,
            machine,
            inventory,
            font = FontSpec.Default,
            fontVariants = variants.Select(v => new { v.Fit, v.FontId, config = v.ConfigFile == null ? null : Path.GetFileName(v.ConfigFile), cfgText = v.ConfigFile == null ? null : File.ReadAllText(v.ConfigFile), prefix = v.Prefix }),
            commanderFixtures = tvs1.Select(Path.GetFileName),
            excluded = Excluded,
            paths = results.Select(Summary),
            crossRenderer = cross.Select(c => new { c.A, c.B, c.Capture, expectedSource = c.Comparison.ExpectedSource, outcome = c.Comparison.Outcome, differing = c.Comparison.Differing, cells = c.Comparison.DifferingCellCount, cellsWithinOne = c.Comparison.DifferingCellsWithinOne, maxDeviation = c.Comparison.MaxChannelDeviation, c.DiffPng }),
            nativeBaselines = new
            {
                approvedLocation = "(none yet — owner decision)",
                candidates,
                note = "No native baseline is approved. Every candidate is NoApprovedBaseline; a missing approved baseline is never reported as a native-baseline match.",
            },
        };
        File.WriteAllText(Path.Combine(dir, "run.json"), JsonSerializer.Serialize(run, ChildRunner.Json));
        File.WriteAllText(Path.Combine(dir, "baseline-candidates.json"), JsonSerializer.Serialize(new { schema = Schema, status = "candidates for owner review; none approved", candidates }, ChildRunner.Json));
        File.WriteAllText(Path.Combine(dir, "report.md"), Report.Write(runId, began, finished, exit, machine, inventory, results, cross, candidates, tvs1));

        string[] files = Directory.GetFiles(dir);
        Console.WriteLine($"artifacts: {dir} ({files.Length} files, {files.Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0:F1} MB)");
        Console.WriteLine($"exit code {exit}");
        return exit;
    }

    private static object Summary(PathResult r) => new
    {
        r.Path,
        r.Variant,
        r.Outcome,
        required = Required.Contains(r.Path),
        r.Unavailable,
        r.Description,
        r.CaptureBoundary,
        r.Identity,
        r.Placement,
        r.PartialTolerance,
        steps = r.Steps.Count,
        stepsMatched = r.Steps.Count(s => s.Outcome == "Match"),
        pixelsCompared = r.Steps.Sum(s => s.Oracle?.PixelsCompared ?? 0),
        exactPixels = r.Steps.Sum(s => s.Oracle?.ExactMatches ?? 0),
        toleratedPartialPixels = r.Steps.Sum(s => s.Oracle?.WithinPartialTolerance ?? 0),
        maxPartialDeviation = r.Steps.Select(s => s.Oracle?.MaxPartialDeviation ?? 0).DefaultIfEmpty().Max(),
        unverifiedPixels = r.Steps.Sum(s => s.Oracle?.UnverifiedPixels ?? 0),
        unverifiedCells = r.Steps.Sum(s => s.UnverifiedCells),
        differingPixels = r.Steps.Sum(s => s.Oracle?.Differing ?? 0),
        geometryChecks = r.Steps.Sum(s => s.Geometry?.Checked.Values.Sum() ?? 0),
        geometryFailures = r.Steps.Sum(s => s.Geometry?.Failed.Values.Sum() ?? 0),
        negativeControls = r.NegativeControls.Select(n => new { n.Name, n.Detected, n.Applicable }),
        r.Cleanup,
        r.Seconds,
        error = r.Error?.Split('\n')[0],
    };

    // ── Cross-renderer consistency ─────────────────────────────────────────

    private static readonly (string A, string B)[] Pairs =
    [
        ("sdl2d-software", "sdl-gpu-shader"),
        ("sdl-gpu-shader", "sdl-gpu-fallback"),
        ("sdl2d-software", "sdl2d-direct3d11"),
        ("sdl-gpu-shader", "sdl-gpu-shader-vulkan"),
        ("sdl-gpu-shader-vulkan", "sdl-gpu-fallback-vulkan"),
    ];

    private static List<CrossRendererResult> CrossRenderer(string dir, List<PathResult> results, string prefix = "")
    {
        var list = new List<CrossRendererResult>();
        foreach (var (a, b) in Pairs)
        {
            PathResult? ra = results.FirstOrDefault(r => r.Path == a), rb = results.FirstOrDefault(r => r.Path == b);
            if (ra == null || rb == null || !ra.Identity.TryGetValue("cell", out string? cell)) continue;
            int cw = int.Parse(cell.Split('x')[0]), ch = int.Parse(cell.Split('x')[1]);
            foreach (StepResult sa in ra.Steps.Where(s => s.NativePng != null))
            {
                string stem = sa.NativePng![(prefix.Length + a.Length + 2)..];
                string fb = Path.Combine(dir, prefix + b + "--" + stem);
                if (!File.Exists(fb)) continue;
                NativeImage ia = NativeImage.DecodePng(File.ReadAllBytes(Path.Combine(dir, sa.NativePng)));
                NativeImage ib = NativeImage.DecodePng(File.ReadAllBytes(fb));
                PixelComparison c = PixelComparer.CompareExact(ia, ib, cw, ch, ExpectedSource.RendererCapture);
                var cr = new CrossRendererResult { A = prefix + a, B = prefix + b, Capture = stem.Replace(".native.png", ""), Comparison = c };
                bool representative = prefix.Length == 0
                    ? stem.StartsWith("screen-full--80x25", StringComparison.Ordinal) || stem.StartsWith("commander-dialog", StringComparison.Ordinal)
                    : stem.StartsWith("czech-full--80x25", StringComparison.Ordinal) && (b is "sdl-gpu-shader" or "sdl-gpu-fallback");
                if (representative || (c.Differing > 0 && list.Count(x => x.DiffPng != null) < 12))
                {
                    cr.DiffPng = $"cross--{prefix}{a}-vs-{b}--{cr.Capture}.diff.png";
                    File.WriteAllBytes(Path.Combine(dir, cr.DiffPng), PixelComparer.DiffImage(ia, null, ib, 0).EncodePng());
                }
                list.Add(cr);
            }
        }
        return list;
    }

    private static List<BaselineCandidate> Candidates(string dir, List<PathResult> results, FontVariant variant) =>
        results.Where(r => Required.Contains(r.Path))
            .SelectMany(r => r.Steps.Where(s => s.NativePng != null && s.Outcome == "Match").Select(s => (r.Path, Step: s)))
            .Select(x =>
            {
                StepResult s = x.Step;
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, s.NativePng!));
                string fit = variant.ConfigFile == null ? "default" : variant.Fit;
                return new BaselineCandidate(s.NativePng!, Convert.ToHexStringLower(SHA256.HashData(bytes)), s.Width, s.Height,
                    "NoApprovedBaseline (candidate, owner review required)",
                    $"{fit}/{variant.FontId}/{x.Path}/{s.Fixture}-{s.Step}/{s.Grid}", fit);
            }).ToList();

    /// <summary>
    /// The same path and capture under Condense and Uniform (same font). Not a pass/fail
    /// comparison: it measures how much Uniform actually changes the frame.
    /// </summary>
    private static List<CrossRendererResult> CrossMode(string dir, List<PathResult> results, List<FontVariant> variants)
    {
        var list = new List<CrossRendererResult>();
        foreach (string font in variants.Select(v => v.FontId).Distinct())
        {
            FontVariant? c = variants.FirstOrDefault(v => v.FontId == font && v.Fit == "Condense" && v.ConfigFile != null);
            FontVariant? u = variants.FirstOrDefault(v => v.FontId == font && v.Fit == "Uniform");
            if (c == null || u == null) continue;
            foreach (PathResult rc in results.Where(r => r.Variant == c.Prefix && r.Identity.ContainsKey("cell")))
            {
                string[] cell = rc.Identity["cell"].Split('x');
                int cw = int.Parse(cell[0]), ch = int.Parse(cell[1]);
                foreach (StepResult sc in rc.Steps.Where(s => s.NativePng != null))
                {
                    string stem = sc.NativePng![c.Prefix.Length..];
                    string fu = Path.Combine(dir, u.Prefix + stem);
                    if (!File.Exists(fu)) continue;
                    NativeImage ic = NativeImage.DecodePng(File.ReadAllBytes(Path.Combine(dir, sc.NativePng)));
                    NativeImage iu = NativeImage.DecodePng(File.ReadAllBytes(fu));
                    PixelComparison cmp = PixelComparer.CompareExact(ic, iu, cw, ch, ExpectedSource.RendererCapture);
                    var cr = new CrossRendererResult { A = c.Prefix.TrimEnd('-'), B = u.Prefix.TrimEnd('-'), Capture = stem.Replace(".native.png", ""), Comparison = cmp };
                    bool representative = rc.Path is "sdl2d-software" or "sdl-gpu-shader" && (stem.Contains("--czech-full--80x25") || stem.Contains("--screen-full--80x25") || stem.Contains("--czech-full--140x40"));
                    if (representative)
                    {
                        cr.DiffPng = $"cross--condense-vs-uniform--{font}--{cr.Capture}.diff.png";
                        File.WriteAllBytes(Path.Combine(dir, cr.DiffPng), PixelComparer.DiffImage(ic, null, iu, 0).EncodePng());
                    }
                    list.Add(cr);
                }
            }
        }
        return list;
    }

    // ── Process helpers ─────────────────────────────────────────────────────

    /// <summary>Runs a process with a hard timeout; on timeout the whole tree is killed and the code is -1.</summary>
    public static (int Code, string Stdout, string Stderr) Exec(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(5000);
            return (-1, stdout.ToString(), stderr.ToString());
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Tail(string s) => s.Length <= 600 ? s.Trim() : s[^600..].Trim();

    public static Dictionary<string, string> Machine()
    {
        var m = new Dictionary<string, string>
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
        };
        if (OperatingSystem.IsWindows())
        {
            var (code, stdout, _) = Exec("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name + ' | driver ' + $_.DriverVersion }"], TimeSpan.FromSeconds(30));
            if (code == 0) m["videoControllers"] = string.Join("; ", stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return m;
    }
}
