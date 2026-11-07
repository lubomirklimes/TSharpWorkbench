using System.Text;

namespace TSharpWorkbench.Testing;

/// <summary>Human- and machine-readable views of a run's visual gate (<c>visual.json</c> and the report.md section).</summary>
internal static class VisualReport
{
    private const int RowsShown = 6, CellsShown = 12;

    internal static object Json(VisualRunSummary visual) => new
    {
        format = "tsharpvision.visual-run/1", manifest = Path.GetFileName(visual.Manifest),
        gate = visual.Passed ? "Passed" : "NotPassed",
        counts = Statuses().ToDictionary(s => s, visual.Count),
        approval = "Approved baselines change only through the explicit 'baseline approve' command; this run wrote none.",
        entries = visual.Entries,
    };

    private static string[] Statuses() => [VisualStatuses.Match, VisualStatuses.Mismatch, VisualStatuses.BaselineMissing, VisualStatuses.BaselineInvalid,
        VisualStatuses.NormalizationError, VisualStatuses.NotCaptured, VisualStatuses.NotRun];

    internal static void Markdown(StringBuilder md, BatchResult batch)
    {
        var visual = batch.Visual!;
        md.AppendLine().AppendLine("## Visual baselines").AppendLine();
        md.AppendLine($"Manifest `{Path.GetFileName(visual.Manifest)}` — gate **{(visual.Passed ? "PASSED" : "NOT PASSED")}**: "
            + string.Join(", ", Statuses().Select(s => $"{visual.Count(s)} {s}")) + ".").AppendLine();
        md.AppendLine("Layer A only: canonical logical cells (UTF-16 code unit + attribute byte) after the listed normalizations, compared exactly. "
            + "A missing baseline is never a pass; candidates are unreviewed and approved only by an explicit `baseline approve`.").AppendLine();
        md.AppendLine("| Scenario | Checkpoint | Grid | Status | Differing (glyph/attr) | Normalizations (masked cells) | Diff | Candidate |").AppendLine("|---|---|---|---|---|---|---|---|");
        var details = batch.Results.SelectMany(r => r.Checkpoints.Where(c => c.Visual is not null).Select(c => (r, c, v: c.Visual!))).ToArray();
        foreach (var e in visual.Entries)
        {
            var d = details.FirstOrDefault(x => x.v.Baseline == e.BaselineKey()).v;
            md.AppendLine($"| `{e.ScenarioId}` | `{e.Checkpoint}` | {e.Grid} | {e.Status} | "
                + (d is null ? "" : $"{d.DifferingCells} ({d.GlyphDifferences}/{d.AttributeDifferences})") + " | "
                + (d is null ? "" : d.Normalizations.Length == 0 ? "none" : string.Join(", ", d.Normalizations) + $" ({d.MaskedCells})") + " | "
                + (e.Diff is null ? "" : $"`{e.Diff}`" + (e.DiffPng is null ? "" : $", `{e.DiffPng}`")) + " | "
                + (e.Candidate is null ? "" : $"`{e.Candidate}`") + " |");
        }
        var problems = visual.Entries.Where(e => e.Status is not (VisualStatuses.Match or VisualStatuses.NotRun)).ToArray();
        if (problems.Length == 0) return;
        md.AppendLine().AppendLine("### Visual findings").AppendLine();
        foreach (var e in problems)
        {
            md.AppendLine($"- **{e.Status}** `{e.BaselineKey()}`: {Escape(e.Reason)}");
            if (e.Status != VisualStatuses.Mismatch || e.Diff is null) continue;
            string path = Path.Combine(batch.RunRoot, e.Diff);
            if (!File.Exists(path)) continue;
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            if (!json.RootElement.TryGetProperty("comparison", out var c)) continue;
            md.AppendLine($"  - expected `{json.RootElement.GetProperty("expected").GetProperty("approved").GetString()}` (baseline store), "
                + $"actual `{json.RootElement.GetProperty("actual").GetProperty("cells").GetString()}`, diff image `{e.DiffPng}`");
            foreach (var o in c.GetProperty("observations").EnumerateArray().Take(RowsShown))
                md.AppendLine($"  - row {o.GetProperty("y").GetInt32()} cols {o.GetProperty("fromX").GetInt32()}..{o.GetProperty("toX").GetInt32()}: "
                    + $"{o.GetProperty("kind").GetString()}{(o.GetProperty("shift").GetInt32() is var s && s != 0 ? $" by {s:+#;-#}" : "")} — "
                    + $"expected `{Escape(o.GetProperty("expected").GetString()!)}`, actual `{Escape(o.GetProperty("actual").GetString()!)}`");
            md.AppendLine("  - | x,y | glyph exp → act | attr exp → act | hints |");
            md.AppendLine("    |---|---|---|---|");
            foreach (var d in c.GetProperty("differences").EnumerateArray().Take(CellsShown))
            {
                var ex = d.GetProperty("expected"); var ac = d.GetProperty("actual");
                md.AppendLine($"    | {d.GetProperty("x").GetInt32()},{d.GetProperty("y").GetInt32()} | "
                    + $"`{Escape(ex.GetProperty("text").GetString()!)}` {ex.GetProperty("code").GetString()} → `{Escape(ac.GetProperty("text").GetString()!)}` {ac.GetProperty("code").GetString()} | "
                    + $"{ex.GetProperty("attribute").GetString()} → {ac.GetProperty("attribute").GetString()} | {string.Join(", ", d.GetProperty("hints").EnumerateArray().Select(h => h.GetString()))} |");
            }
        }
    }

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("`", "'").Replace("\r", " ").Replace("\n", " ");
}
