using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>Everything a run produced, ready to be written as flat artifacts.</summary>
public sealed class QualificationRun
{
    public required string RunId { get; init; }
    public required DateTimeOffset Began { get; init; }
    public required IReadOnlyList<IOutputPath> Paths { get; init; }
    public required IReadOnlyList<GridSize> Grids { get; init; }
    public List<PathFixtureResult> Results { get; } = new();
    public List<ParityResult> Parity { get; } = new();
    public List<PtySmokeResult> PtySmoke { get; } = new();
    public string ConsoleSource { get; set; } = "n/a";
    public string SdlBackend { get; set; } = "n/a";

    public bool OutputPassed => Results.All(r => r.Outcome is Outcomes.Match or Outcomes.Unavailable);
    public bool ParityPassed => Parity.All(p => p.Outcome != Outcomes.Mismatch);
    public bool PtyPassed => PtySmoke.All(p => p.Outcome is Outcomes.Match or Outcomes.Unavailable);
    public int ExitCode => Results.Any(r => r.Outcome == Outcomes.Error) ? 2 : OutputPassed && ParityPassed && PtyPassed ? 0 : 1;
}

/// <summary>Writes a run as a flat directory: <c>&lt;path&gt;--&lt;fixture&gt;-output--&lt;grid&gt;.json</c>, comparisons, parity, run.json, report.md.</summary>
public static class ArtifactWriter
{
    public const string Schema = "tsharpvision.driver-output/v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string NewRunId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

    public static string CaptureName(string path, string fixture, GridSize grid) => $"{path}--{fixture}-output--{grid}.json";
    public static string ComparisonName(string fixture, GridSize grid) => $"driver-comparison--{fixture}--{grid}.json";
    public static string ExpectedName(string fixture, string step, GridSize grid) => $"expected--{fixture}-{step}--{grid}.tvs1";

    public static string Write(QualificationRun run, string outputRoot)
    {
        string dir = Path.Combine(outputRoot, "run-" + run.RunId);
        Directory.CreateDirectory(dir);

        foreach (PathFixtureResult r in run.Results)
            File.WriteAllText(Path.Combine(dir, CaptureName(r.Path, r.Fixture, r.Grid)), JsonSerializer.Serialize(CaptureDocument(r, run), Json));

        foreach (var group in run.Results.GroupBy(r => (r.Fixture, r.Grid)))
        {
            File.WriteAllText(Path.Combine(dir, ComparisonName(group.Key.Fixture, group.Key.Grid)), JsonSerializer.Serialize(CrossDriver(group.ToList()), Json));
            // The expected logical screen of each step's last state, as TVS1 (logical format), once per fixture/grid.
            var model = new LogicalScreen(group.Key.Grid, new TScreenChar(' ', 0x07));
            foreach (FixtureStep step in Fixtures.Steps(group.Key.Fixture, group.Key.Grid))
            {
                if (step.ResizeTo is { } t) model = new LogicalScreen(t, new TScreenChar(' ', 0x07));
                foreach (CellWrite w in step.Writes) model.Apply(w);
            }
            File.WriteAllBytes(Path.Combine(dir, ExpectedName(group.Key.Fixture, "final", model.Grid)),
                new ScreenSnapshot(model.Grid.Width, model.Grid.Height, model.Cells).Serialize());
        }

        File.WriteAllText(Path.Combine(dir, "input-parity.json"), JsonSerializer.Serialize(new { schema = Schema, layer = "B", results = run.Parity }, Json));
        foreach (PtySmokeResult p in run.PtySmoke)
            File.WriteAllText(Path.Combine(dir, $"terminal-ansi-pty--screen-output--{p.Grid}.json"), JsonSerializer.Serialize(p, Json));

        File.WriteAllText(Path.Combine(dir, "run.json"), JsonSerializer.Serialize(RunDocument(run), Json));
        File.WriteAllText(Path.Combine(dir, "report.md"), Report(run, dir));
        return dir;
    }

    public static object CaptureDocument(PathFixtureResult r, QualificationRun run)
    {
        IOutputPath? path = run.Paths.FirstOrDefault(p => p.Id == r.Path);
        return new
        {
            schema = Schema,
            layer = "B",
            path = r.Path,
            driver = r.Driver,
            boundary = path?.Boundary,
            capabilities = path?.Capabilities,
            platform = Platform.Name,
            fixture = r.Fixture,
            grid = r.Grid.ToString(),
            outcome = r.Outcome,
            reason = r.Reason,
            steps = r.Captures.Zip(r.Comparisons, (c, cmp) => new
            {
                step = c.Step,
                grid = c.Grid.ToString(),
                outcome = cmp.Outcome,
                comparison = cmp,
                cursor = c.Cursor,
                drawnCaret = c.DrawnCaret,
                operationsTotal = c.OperationsTotal,
                operationCounts = c.OperationCounts,
                operations = c.Operations,
                diagnostics = c.Diagnostics,
                text = Text.Rows(c.Cells, c.Grid, s => s.Text.Length == 0 ? "\u2400" : s.Text),
                fg = Text.Rows(c.Cells, c.Grid, s => Vga.Describe(s.Fg)),
                bg = Text.Rows(c.Cells, c.Grid, s => Vga.Describe(s.Bg)),
            }),
        };
    }

    /// <summary>Per step: each path's outcome, and how far each pair of paths' captured semantics differ.</summary>
    public static object CrossDriver(List<PathFixtureResult> results)
    {
        var ran = results.Where(r => r.Captures.Count > 0).ToList();
        var steps = ran.SelectMany(r => r.Captures.Select(c => c.Step)).Distinct().ToList();
        return new
        {
            schema = Schema,
            layer = "B",
            fixture = results[0].Fixture,
            grid = results[0].Grid.ToString(),
            paths = results.Select(r => new { r.Path, r.Outcome, r.Reason }),
            steps = steps.Select(step => new
            {
                step,
                outcomes = ran.ToDictionary(r => r.Path, r => r.Comparisons.FirstOrDefault(c => c.Step == step)?.Outcome ?? "n/a"),
                pairs = (from a in ran
                         from b in ran
                         where string.CompareOrdinal(a.Path, b.Path) < 0
                         let ca = a.Captures.FirstOrDefault(c => c.Step == step)
                         let cb = b.Captures.FirstOrDefault(c => c.Step == step)
                         where ca != null && cb != null && ca.Cells.Length == cb.Cells.Length
                         select new
                         {
                             a = a.Path,
                             b = b.Path,
                             textDiffers = ca.Cells.Zip(cb.Cells).Count(p => p.First.Text != p.Second.Text),
                             bgDiffers = ca.Cells.Zip(cb.Cells).Count(p => p.First.Bg != p.Second.Bg),
                             fgDiffersOnInk = ca.Cells.Zip(cb.Cells).Count(p => p.First.Text != " " && p.First.Fg != p.Second.Fg),
                             note = "differences at the caret cell are expected between native-caret and drawn-caret paths",
                         }).ToList(),
            }),
        };
    }

    private static object RunDocument(QualificationRun run) => new
    {
        schema = Schema,
        layer = "B",
        layerNote = "Layer B = driver translation. Layer A (logical TVS1) uses logical baselines; Layer C (native pixels/screenshots) is not captured.",
        runId = run.RunId,
        began = run.Began,
        environment = Platform.Describe(),
        consoleSource = run.ConsoleSource,
        sdlBackend = run.SdlBackend,
        grids = run.Grids.Select(g => g.ToString()),
        fixtures = Fixtures.All,
        paths = run.Paths.Select(p => new { id = p.Id, driver = p.Driver, boundary = p.Boundary, available = p.Unavailable == null, unavailable = p.Unavailable, capabilities = p.Capabilities }),
        results = run.Results.Select(r => new
        {
            r.Path, r.Fixture, grid = r.Grid.ToString(), r.Outcome, r.Reason,
            steps = r.Comparisons.Select(c => new { c.Step, c.Outcome, c.CellsCompared, c.TextMismatches, c.FgMismatches, c.BgMismatches, failures = c.Failures }),
            artifact = CaptureName(r.Path, r.Fixture, r.Grid),
        }),
        parity = new
        {
            verified = run.Parity.Count(p => p.Outcome == Outcomes.Verified),
            mismatch = run.Parity.Count(p => p.Outcome == Outcomes.Mismatch),
            notSupported = run.Parity.Count(p => p.Outcome == Outcomes.NotSupported),
            unavailable = run.Parity.Count(p => p.Outcome == Outcomes.Unavailable),
        },
        ptySmoke = run.PtySmoke.Select(p => new { grid = p.Grid.ToString(), p.Outcome, p.Detail }),
        exitCode = run.ExitCode,
    };

    public static string Report(QualificationRun run, string dir)
    {
        var md = new StringBuilder();
        md.AppendLine("# Driver output qualification (Layer B)").AppendLine();
        md.AppendLine($"Result: **{(run.ExitCode == 0 ? "PASS" : run.ExitCode == 2 ? "ERROR" : "FAIL")}** (exit code {run.ExitCode}). " +
                      "Layer B is the drivers' translation of cells and native input; it is not a native-pixel (Layer C) result.").AppendLine();
        md.AppendLine("| Item | Value |").AppendLine("|---|---|");
        md.AppendLine($"| Run directory | `{dir}` |");
        md.AppendLine($"| Started (UTC) | {run.Began:yyyy-MM-dd HH:mm:ss} |");
        md.AppendLine($"| Platform | {Platform.Name} — {JsonSerializer.Serialize(Platform.Describe())} |");
        md.AppendLine($"| Console source | {run.ConsoleSource} |");
        md.AppendLine($"| SDL backend | {run.SdlBackend} |");
        md.AppendLine($"| Grids | {string.Join(", ", run.Grids)} |").AppendLine();

        md.AppendLine("## Paths").AppendLine();
        md.AppendLine("| Path | Driver code run | Observation boundary | Available | Capabilities |").AppendLine("|---|---|---|---|---|");
        foreach (IOutputPath p in run.Paths)
            md.AppendLine($"| `{p.Id}` | {p.Driver} | {p.Boundary} | {(p.Unavailable == null ? "yes" : "no — " + p.Unavailable)} | {p.Capabilities.Notes} |");

        md.AppendLine().AppendLine("## Output results").AppendLine();
        md.AppendLine("| Path | Fixture | Grid | Outcome | Steps (match/total) | Mismatching cells (text/fg/bg) | Failures |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (PathFixtureResult r in run.Results)
        {
            string failures = string.Join("; ", r.Comparisons.SelectMany(c => c.Failures.Select(f => c.Step + ": " + f)).Take(4));
            md.AppendLine($"| `{r.Path}` | {r.Fixture} | {r.Grid} | {r.Outcome}{(r.Reason is { } why ? " — " + why : "")} | " +
                          $"{r.Comparisons.Count(c => c.Outcome == Outcomes.Match)}/{r.Comparisons.Count} | " +
                          $"{r.Comparisons.Sum(c => c.TextMismatches)}/{r.Comparisons.Sum(c => c.FgMismatches)}/{r.Comparisons.Sum(c => c.BgMismatches)} | {Cell(failures)} |");
        }

        md.AppendLine().AppendLine("## Input translator parity").AppendLine();
        md.AppendLine("| Case | Driver | Outcome | Native input | Actual | Note |").AppendLine("|---|---|---|---|---|---|");
        foreach (ParityResult p in run.Parity)
            md.AppendLine($"| {p.Case} | `{p.Driver}` | {p.Outcome} | {Cell(p.Native)} | {Cell(p.Actual)} | {Cell(p.Note ?? "")} |");

        md.AppendLine().AppendLine("## Linux PTY smoke").AppendLine();
        if (run.PtySmoke.Count == 0) md.AppendLine("Not run.");
        foreach (PtySmokeResult p in run.PtySmoke)
            md.AppendLine($"- {p.Grid}: **{p.Outcome}** {Cell(p.Detail)} {string.Join("; ", p.Checks)}");

        md.AppendLine().AppendLine("## Artifacts").AppendLine();
        md.AppendLine("Flat directory: `<path>--<fixture>-output--<grid>.json` (per-step captures and comparisons), " +
                      "`driver-comparison--<fixture>--<grid>.json`, `expected--<fixture>-final--<grid>.tvs1` (Layer A reference), " +
                      "`input-parity.json`, `run.json`, this report.");
        return md.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
