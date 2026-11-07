using System.Text;

namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>Human-readable run report (report.md).</summary>
public static class Report
{
    public static string Write(string runId, DateTimeOffset began, DateTimeOffset finished, int exit,
        Dictionary<string, string> machine, BackendInventory inventory, List<PathResult> results,
        List<CrossRendererResult> cross, List<BaselineCandidate> candidates, List<string> tvs1)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Native-pixel qualification (Layer C) — run {runId}");
        sb.AppendLine();
        sb.AppendLine($"{began:u} → {finished:u} · exit code {exit} · {machine.GetValueOrDefault("os")} · {machine.GetValueOrDefault("dotnet")}");
        sb.AppendLine($"Video controllers: {machine.GetValueOrDefault("videoControllers", "(not queried)")}");
        sb.AppendLine($"SDL {inventory.SdlVersion}; render drivers: {string.Join(", ", inventory.RenderDrivers)}; SDL_GPU drivers: {string.Join(", ", inventory.GpuDrivers)}");
        sb.AppendLine($"Font: {FontSpec.Default.Path} {FontSpec.Default.PointSize} pt (pinned; ships with Windows). Commander inputs (read-only TVS1): {tvs1.Count}");
        sb.AppendLine();
        sb.AppendLine("Expected images are computed by the independent raster oracle (logical cells + VGA palette + caret model + glyph coverage from the font / CP437 generator; no renderer code).");
        sb.AppendLine("Cross-renderer diffs compare two actual captures (consistency only). No owner-approved native baseline exists.");
        sb.AppendLine();
        sb.AppendLine("## Renderer qualification matrix");
        sb.AppendLine();
        if (results.Any(r => r.Variant.Length > 0))
            sb.AppendLine("Font-fit variants: path ids are prefixed `<fontFit>--<font>--`; each variant's drivers read a real .cfg file (`<fontFit>--<font>.cfg` in this directory) through the normal configuration loader.\n");
        sb.AppendLine("| Path | Outcome | Backend / execution | Steps matched | Exact px | Tolerated partial px (max dev) | Unverified cells | Geometry checks (failed) | Negative controls | Capture boundary |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (PathResult r in results)
        {
            string backend = r.Identity.TryGetValue("gpu.backend", out string? gb)
                ? $"SDL_GPU {gb} on {r.Identity.GetValueOrDefault("gpu.device.name")} ({r.Identity.GetValueOrDefault("gpu.adapterKind")}); {r.Identity.GetValueOrDefault("execution")}"
                : r.Identity.TryGetValue("render.driver.actual", out string? rd) ? $"SDL_Renderer {rd}; {r.Identity.GetValueOrDefault("execution")}" : "—";
            long exact = r.Steps.Sum(s => s.Oracle?.ExactMatches ?? 0), tol = r.Steps.Sum(s => s.Oracle?.WithinPartialTolerance ?? 0);
            int maxDev = r.Steps.Select(s => s.Oracle?.MaxPartialDeviation ?? 0).DefaultIfEmpty().Max();
            int geo = r.Steps.Sum(s => s.Geometry?.Checked.Values.Sum() ?? 0), geoFail = r.Steps.Sum(s => s.Geometry?.Failed.Values.Sum() ?? 0);
            sb.AppendLine($"| `{r.Variant}{r.Path}`{(Parent.Required.Contains(r.Path) ? " (required)" : "")} | **{r.Outcome}** | {backend} | {r.Steps.Count(s => s.Outcome == "Match")}/{r.Steps.Count} | {exact:N0} | {tol:N0} ({maxDev}) | {r.Steps.Sum(s => s.UnverifiedCells)} | {geo:N0} ({geoFail}) | {r.NegativeControls.Count(n => n.Applicable && n.Detected)}/{r.NegativeControls.Count(n => n.Applicable)}{(r.NegativeControls.Any(n => !n.Applicable) ? $" (+{r.NegativeControls.Count(n => !n.Applicable)} n/a)" : "")} | {(r.Unavailable ?? r.CaptureBoundary)} |");
        }
        sb.AppendLine();

        foreach (PathResult r in results.Where(r => r.Steps.Count > 0 || r.Error != null))
        {
            sb.AppendLine($"## `{r.Variant}{r.Path}`");
            sb.AppendLine();
            sb.AppendLine(r.Description);
            sb.AppendLine();
            sb.AppendLine($"Identity: {string.Join(" · ", r.Identity.Select(kv => $"{kv.Key}={kv.Value}"))}");
            sb.AppendLine($"Font placement model: {r.Placement}; partial-coverage tolerance ±{r.PartialTolerance}; {r.Seconds} s");
            if (r.Error != null) sb.AppendLine($"\n**Error:** `{r.Error.Split('\n')[0]}`");
            sb.AppendLine();
            sb.AppendLine("| Fixture | Step | Grid | Outcome | px | exact | tolerated | unverified | differing | geometry | update (unexp/missing) | PNG |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (StepResult s in r.Steps)
            {
                PixelComparison? o = s.Oracle;
                string geometry = s.Geometry == null ? "—" : s.Geometry.Passed ? $"{s.Geometry.Checked.Values.Sum()} ok" : string.Join(", ", s.Geometry.Failed.Select(f => $"{f.Key}×{f.Value}"));
                string update = s.Update == null ? "—" : $"{s.Update.UnexpectedChangedCells}/{s.Update.MissingChangedCells} of {s.Update.ExpectedChangedCells}";
                sb.AppendLine($"| {s.Fixture} | {s.Step} | {s.Grid} | {s.Outcome} | {s.Width}x{s.Height} | {o?.ExactMatches:N0} | {o?.WithinPartialTolerance:N0} | {o?.UnverifiedPixels:N0} | {o?.Differing:N0} | {geometry} | {update} | {s.NativePng ?? ""} |");
            }
            sb.AppendLine();
            var unverified = r.Steps.SelectMany(s => s.UnverifiedSamples).Distinct().Take(10).ToList();
            if (unverified.Count > 0)
                sb.AppendLine("Unverified glyph cells (shape not stated exactly by the oracle; still checked for ink and colour range): " + string.Join("; ", unverified) + "\n");
            var missing = r.Steps.SelectMany(s => s.MissingFromFont).Distinct().ToList();
            if (missing.Count > 0)
                sb.AppendLine("Characters missing from the font and the symbol fallback: " + string.Join(", ", missing) + "\n");
            foreach (StepResult s in r.Steps.Where(s => s.Outcome != "Match"))
                sb.AppendLine($"- **{s.Fixture} {s.Step} {s.Grid}**: {s.Reason}");
            if (r.NegativeControls.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Negative control | Mutation | Detected | Observed |");
                sb.AppendLine("|---|---|---|---|");
                foreach (NegativeControl n in r.NegativeControls)
                    sb.AppendLine($"| {n.Name} | {n.Mutation} | {(!n.Applicable ? "n/a (defect frame equals the expected frame for this font)" : n.Detected ? "yes" : "**NO**")} | {n.Observed} |");
            }
            if (r.Cleanup.Count > 0) sb.AppendLine("\nCleanup: " + string.Join("; ", r.Cleanup));
            sb.AppendLine();
        }

        if (cross.Count > 0)
        {
            sb.AppendLine("## Cross-renderer consistency (actual vs actual; not a correctness proof)");
            sb.AppendLine();
            sb.AppendLine("Rows `condense--<font>` vs `uniform--<font>` compare the two fit modes on the same path: they measure how much Uniform changes the frame, they are not expected to match.\n");
            sb.AppendLine("| A | B | Capture | Differing px | Cells | of which max dev ≤ 1 | Max dev | Diff |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (CrossRendererResult c in cross)
                sb.AppendLine($"| {c.A} | {c.B} | {c.Capture} | {c.Comparison.Differing:N0} | {c.Comparison.DifferingCellCount} | {c.Comparison.DifferingCellsWithinOne} | {c.Comparison.MaxChannelDeviation} | {c.DiffPng ?? ""} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Native baseline candidates");
        sb.AppendLine();
        sb.AppendLine($"{candidates.Count} native PNGs of required paths are listed in `baseline-candidates.json` with SHA-256. Status of every one: **NoApprovedBaseline** — owner review required; nothing was approved, nothing was compared against an approved native baseline.");
        return sb.ToString();
    }
}
