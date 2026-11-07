using System.Text;
using System.Text.Json;

namespace TSharpWorkbench.Testing;

/// <summary>Human-readable summary written beside <c>run.json</c>, generated only from the current run.</summary>
internal static class RunReport
{
    internal static string Markdown(BatchResult batch, ScenarioDescriptor[] selected, TargetIdentity? identity, DateTimeOffset began,
        RunOptions options, object sweep)
    {
        var md = new StringBuilder();
        var results = batch.Results;
        int passed = results.Count(r => r.Outcome == ScenarioOutcomes.Passed);
        md.AppendLine("# TestWorkbench run report").AppendLine();
        int skipped = results.Count(r => r.Outcome == ScenarioOutcomes.Skipped);
        md.AppendLine($"Result: **{(batch.ExitCode == 0 ? "PASS" : "FAIL")}** — {passed}/{results.Length} execution(s) passed, "
            + (skipped > 0 ? $"{skipped} skipped (platform/driver unsupported), " : "")
            + $"{results.Sum(r => r.Checkpoints.Length)} checkpoint(s), review status **Unreviewed** (automated success is not visual approval).").AppendLine();
        md.AppendLine("| Item | Value |").AppendLine("|---|---|");
        md.AppendLine($"| Run directory | `{batch.RunRoot}` |");
        md.AppendLine($"| Started (UTC) | {began:yyyy-MM-dd HH:mm:ss} |");
        md.AppendLine($"| Application | {identity?.Assembly ?? "n/a"} {identity?.AssemblyVersion} |");
        md.AppendLine($"| TSharpVision / Testing | {identity?.FrameworkVersion ?? "n/a"} / {identity?.TestingVersion ?? "n/a"} |");
        md.AppendLine($"| Protocol schema | `{Protocol.Schema}` |");
        md.AppendLine(options.Driver == "NullDriver"
            ? $"| Capture layer | A — logical `TScreen.ScreenBuffer` cells via `{identity?.Driver ?? "NullDriver"}`; additional native evidence: `{options.ProxyCapture}` (separate artifacts) |"
            : options.Driver == "Win32ConsoleDriver" ? "| Capture layer | host-window — actual Commander Win32ConsoleDriver; owned classic conhost; programmatic console input |"
            : NativeExecution.IsGpu(options.Driver) ? $"| Capture layer | renderer-target - actual Commander SDLGpuDriver; {NativeExecution.Backend(options.Driver)} / {NativeExecution.GpuPath(options.Driver)}; programmatic target-owned SDL events |"
            : $"| Capture layer | renderer-target — actual Commander `{identity?.Driver ?? "unavailable"}`; Windows SDL software; target-owned SDL input injection | ");
        md.AppendLine(options.Driver == "NullDriver"
            ? $"| PNG renderer | `{LogicalPng.Renderer}` ({LogicalPng.CellWidth}x{LogicalPng.CellHeight} px per cell; a rendering of the cells, not a native screenshot) |"
            : options.Driver == "Win32ConsoleDriver" ? "| PNG renderer | NativeHosts PrintWindow of the verified private conhost client area; font/palette/DPI/process/grid/pixel metrics in checkpoint JSON. |"
            : NativeExecution.IsGpu(options.Driver) ? "| PNG renderer | Submitted Commander application snapshot rerendered by its production GPU worker into readable texture; GPU download + bounded fence. Backend/adapter/path/frame identity in checkpoint JSON. Not swapchain/compositor/monitor readback. |"
            : "| PNG renderer | SDL_RenderReadPixels before SDL_RenderPresent; actual production application buffer; not compositor presentation. Backend, HWND, executable, assembly and cell metrics in checkpoint JSON. |");
        md.AppendLine($"| Grids | {(options.Sizes is null ? "scenario defaults" : string.Join(", ", options.Sizes))} |");
        md.AppendLine($"| Inspection | {options.Inspect} |");
        md.AppendLine($"| Environment | {JsonSerializer.Serialize(Provenance.Environment())} |");
        md.AppendLine($"| Orphan temp sweep | {JsonSerializer.Serialize(sweep)} |").AppendLine();

        md.AppendLine("## Executions").AppendLine();
        md.AppendLine("| Scenario | Grid | Outcome | Checkpoints | Assertions (pass/fail/unsupported) | Reason |").AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var all = r.Checkpoints.SelectMany(c => c.Assertions).ToArray();
            string expected = r.ExpectedResult == ScenarioOutcomes.Passed ? "" : $" (expected {r.ExpectedResult})";
            md.AppendLine($"| `{r.ScenarioId}` | {r.Size} | {r.Outcome}{expected} | {r.Checkpoints.Length} | "
                + $"{all.Count(a => a.Outcome == AssertionOutcomes.Pass)}/{all.Count(a => a.Outcome == AssertionOutcomes.Fail)}/{all.Count(a => a.Outcome == AssertionOutcomes.Unsupported)} | {Cell(r.Reason)} |");
        }

        var problems = results.SelectMany(r => r.Checkpoints.SelectMany(c => c.Assertions.Where(a => a.Outcome != AssertionOutcomes.Pass)
            .Select(a => (r, c, a)))).ToArray();
        md.AppendLine().AppendLine("## Assertions not passed").AppendLine();
        if (problems.Length == 0) md.AppendLine("None.");
        else
        {
            md.AppendLine("| Scenario | Checkpoint | Grid | Assertion | Outcome | Severity | Expected | Actual | Location | Explanation |").AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var (r, c, a) in problems)
                md.AppendLine($"| `{r.ScenarioId}` | `{c.Checkpoint}` | {a.Grid} | `{a.Id}` ({a.Kind}) | {a.Outcome} | {a.Severity} | {Cell(a.Expected)} | {Cell(a.Actual ?? "n/a")} | {Cell(a.Location ?? "")} | {Cell(a.Explanation)} |");
        }

        LayoutFindings(md, results);

        md.AppendLine().AppendLine("## Artifacts").AppendLine();
        md.AppendLine("Flat directory; names are `<scenario>--<checkpoint>--<grid>.{png,tvs1,json}` (grid after a resize: `WxH-from-IxJ`).").AppendLine();
        foreach (var r in results.Where(r => r.Checkpoints.Length > 0))
        {
            md.AppendLine($"- `{r.ScenarioId}` @ {r.Size}");
            foreach (var c in r.Checkpoints)
                md.AppendLine($"  - `{c.Checkpoint}` ({c.Size}): `{c.Png}`, `{c.Cells}`, `{c.Metadata}` — cells sha256 `{c.CellsHash[..16]}…`"
                    + (c.InspectionErrors.Length > 0 ? " — inspection errors: " + string.Join("; ", c.InspectionErrors) : ""));
        }
        if (options.ProxyCapture != ProxyCaptureKinds.None)
        {
            md.AppendLine().AppendLine("## Additional native proxy evidence").AppendLine();
            foreach(var checkpoint in results.SelectMany(r => r.Checkpoints))
            {
                using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(batch.RunRoot,checkpoint.Metadata)));
                if(metadata.RootElement.TryGetProperty("proxyNative",out var native))
                    md.AppendLine($"- `{checkpoint.Checkpoint}`: `{native.GetProperty("kind").GetString()}` — `{native.GetProperty("artifact").GetString()}`; completion/path/device provenance in checkpoint JSON.");
            }
            md.AppendLine("GPU evidence is a readable rerender target; console cells are not window pixels. Swapchain/compositor/physical display capture is not claimed.");
        }
        if (batch.Visual is not null) VisualReport.Markdown(md, batch);
        md.AppendLine().AppendLine("## Not available in this run").AppendLine();
        if (options.ProxyCapture != ProxyCaptureKinds.None)
            md.AppendLine("- Swapchain/compositor/physical display pixels, physical input and native baseline approval.");
        else if (options.Driver == "NullDriver")
        {
            md.AppendLine("- Layer B (actual driver output) and Layer C (native rendered pixels): not captured in logical mode.");
            md.AppendLine("- Cursor position and shape; native input devices (keyboard and mouse input is simulated as logical TEvents).");
        }
        else md.AppendLine("- Resize, unaudited mouse workflows, monitor/compositor presentation and physical keyboard qualification: unavailable in this native subset.");
        md.AppendLine("- Visual approval: captures are disposable and unreviewed; approved baselines are a separate workflow.");
        return md.ToString();
    }

    /// <summary>Findings listed per layout-audit assertion in the report (the full report is in the checkpoint's run.json result).</summary>
    internal const int LayoutFindingsPerAssertion = 50;

    // One block per layout-audit result (every outcome): outcome, profile, scope, counts, coverage and the findings.
    internal static void LayoutFindings(StringBuilder md, IEnumerable<ScenarioResult> results)
    {
        var audits = results.SelectMany(r => r.Checkpoints.SelectMany(c => c.Assertions.Where(a => a.Layout is not null).Select(a => (r, c, a)))).ToArray();
        if (audits.Length == 0) return;
        md.AppendLine().AppendLine("## Layout findings").AppendLine();
        md.AppendLine("Composite `layout-audit` assertions. Finding class (error/warning/info) is separate from the assertion severity; "
            + "only error-class findings fail (strict: warnings too). Coverage gaps are rules or views that could not be evaluated on the captured evidence.");
        foreach (var (r, c, a) in audits)
        {
            var report = a.Layout!; var cov = report.Coverage;
            int info = report.Findings.Length - report.Errors - report.Warnings;
            md.AppendLine().AppendLine($"### `{r.ScenarioId}` / `{c.Checkpoint}` / `{a.Id}` ({a.Grid}) — {a.Outcome} ({a.Severity})").AppendLine();
            md.AppendLine($"- Profile `{cov.Profile}`, scope `{cov.Scope ?? "screen"}`: {cov.AuditedNodes} of {cov.ScopeNodes} view(s) audited; "
                + $"{report.Errors} error, {report.Warnings} warning, {info} info finding(s); {cov.Suppressed.Length} suppressed.");
            md.AppendLine($"- Evaluated ({cov.Evaluated.Length}): {Names(cov.Evaluated)}; not applicable ({cov.NotApplicable.Length}): {Names(cov.NotApplicable)}.");
            md.AppendLine(cov.Unavailable.Length == 0 ? "- Coverage gaps: none." : $"- Coverage gaps ({cov.Unavailable.Length}): "
                + string.Join("; ", cov.Unavailable.Select(g => $"`{g.Rule}`{(g.Path is { } p ? " on " + p : "")}: {Cell(g.Reason)}")) + ".");
            if (cov.Suppressed.Length > 0)
                md.AppendLine($"- Suppressed ({cov.Suppressed.Length}): " + string.Join("; ", cov.Suppressed.Select(x => $"`{x.Rule}` on {x.Path}: {Cell(x.Reason)}")) + ".");
            if (cov.Truncated) md.AppendLine($"- Truncated: {cov.OmittedNodes} view(s) inside the scope were not captured; the audit cannot pass.");
            foreach (string note in cov.Notes) md.AppendLine("- Note: " + Cell(note));
            if (report.Findings.Length == 0) { md.AppendLine("- Findings: none."); continue; }
            md.AppendLine().AppendLine("| Class | Rule | View | Rect | Message | Evidence |").AppendLine("|---|---|---|---|---|---|");
            foreach (var f in report.Findings.Take(LayoutFindingsPerAssertion))
                md.AppendLine($"| {f.Class} | `{f.Rule}` | {Cell(f.View)}{(f.OtherPath is { } o ? " vs " + o : "")} | {f.Rect} | {Cell(f.Message)} | {Cell(f.Evidence ?? "")} |");
            if (report.Findings.Length > LayoutFindingsPerAssertion)
                md.AppendLine().AppendLine($"{report.Findings.Length - LayoutFindingsPerAssertion} more finding(s) in the checkpoint result of run.json.");
        }
    }

    private static string Names(string[] rules) => rules.Length == 0 ? "none" : string.Join(", ", rules.Select(x => "`" + x + "`"));

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
