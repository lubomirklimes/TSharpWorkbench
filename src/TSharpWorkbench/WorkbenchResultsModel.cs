using System.Text.Json;
using TSharpWorkbench.Testing;

// Read-only presentation of recorded results for the Results, Comparison and candidate windows. Every value
// comes from ScenarioResult/VisualRunSummary, run.json or the files of the run folder; nothing here writes artifacts.
// Missing data is shown as "not recorded" instead of being derived or invented.

// The run setup a result was produced with: the session request, or what a historical run folder recorded.
internal sealed record ResultSetup(string TestType, string Mode, string ProxyBackend, string ProxyCapture, string VisualBehavior,
    string[] Sizes, string Inspect, int? Timeout, int? ScenarioTimeout, string? BaselineStore, string Source, string[] NotRecorded)
{
    internal static ResultSetup FromProject(WorkbenchProject p) => new(p.TestType, p.Mode, p.ProxyBackend, p.ProxyCapture, p.VisualBehavior,
        p.ScreenSizes.ToArray(), p.Inspect, p.Timeout, p.ScenarioTimeout, p.BaselineStore, "the run request of this session", []);

    // Historical runs: run.json (runner options, requested driver) and, when present, workbench-request.json (Workbench run
    // request). Fields that neither file records are listed in NotRecorded; null when run.json cannot be read.
    internal static ResultSetup? FromRun(BatchResult batch)
    {
        if (batch.RunRoot.Length == 0) return null;
        try
        {
            using var run = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(batch.RunRoot, "run.json")));
            var json = run.RootElement;
            var missing = new List<string>();
            string driver = json.TryGetProperty("requestedDriver", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "NullDriver";
            JsonElement options = json.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
            string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Number(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
            string[] sizes = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("sizes", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => ScenarioSelection.TryParseSize(x, out _, out _)).ToArray() : [];
            if (sizes.Length == 0) sizes = batch.Results.Select(r => r.Size).Distinct().ToArray();
            JsonElement request = default;
            string requestPath = Path.Combine(batch.RunRoot, "workbench-request.json");
            JsonDocument? requestDoc = null;
            try
            {
                if (File.Exists(requestPath)) { requestDoc = JsonDocument.Parse(File.ReadAllBytes(requestPath)); request = requestDoc.RootElement; }
                string? type = Text(request, "testType");
                if (type is null) { missing.Add("test mode (inferred from the run)"); type = driver != "NullDriver" ? "rendering" : batch.Visual is not null ? "visual" : "functional"; }
                string? behavior = Text(request, "visualBehavior");
                if (behavior is null && type == "visual") missing.Add("baseline behaviour");
                string? backend = Text(options, "proxyBackend");
                if (backend is null) missing.Add("target display driver");
                string? capture = Text(options, "proxyCapture");
                if (capture is null) missing.Add("native evidence");
                string? inspect = Text(options, "inspect") ?? Text(request, "inspect");
                if (inspect is null) missing.Add("inspection");
                return new(type, driver, backend ?? "NullDriver", capture ?? ProxyCaptureKinds.None, behavior ?? "compare", sizes, inspect ?? "declared",
                    Number(options, "operationMilliseconds") ?? Number(request, "timeout"), Number(options, "scenarioSeconds") ?? Number(request, "scenarioTimeout"),
                    Text(request, "baselineStore") ?? (batch.Visual?.Manifest is { Length: > 0 } m ? m : null),
                    requestDoc is null ? "run.json of this run folder" : "run.json and workbench-request.json of this run folder", missing.ToArray());
            }
            finally { requestDoc?.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    internal string ModeName => TestType switch { "visual" => "Visual regression", "rendering" => "Native driver", _ => "Functional" };
    internal string Driver => WorkbenchCapabilities.TargetDriverName(TestType == "rendering" ? Mode : ProxyBackend);
    internal string Label => ModeName + (TestType == "visual" ? VisualBehavior == "candidates" ? " (keep candidates)" : " (compare)" : "")
        + " | " + Driver + (ProxyCapture != ProxyCaptureKinds.None && TestType != "rendering" ? " + " + WorkbenchCapabilities.EvidenceName(ProxyCapture) : "");
}

// One artifact of a checkpoint or run, with its provenance in plain words. Native artifacts never share a label with
// logical PNGs ().
internal sealed record EvidenceRow(string Label, string File, string Layer, bool Native);

// One line of the Results inspector; rows with a path open that file (or folder) with Enter.
internal sealed record InspectorRow(string Text, string? Path = null, bool Folder = false, bool Native = false)
{
    // Enter on this row shows the original technical text (exception and target stderr) in a modal viewer.
    internal string? Technical { get; init; }
}

internal static class ResultText
{
    internal static string Marker(ScenarioResult r) => r.Outcome switch
    { ScenarioOutcomes.Passed => "+", ScenarioOutcomes.Blocked => "!", ScenarioOutcomes.Aborted => "-", ScenarioOutcomes.Skipped => "~", _ => "X" };

    // Execution verdict: upper case when not Passed; Aborted executions are Cancelled, never Failed.
    internal static string Execution(ScenarioResult r) => r.Outcome switch
    {
        ScenarioOutcomes.Passed => "Passed",
        ScenarioOutcomes.Aborted => "CANCELLED",
        ScenarioOutcomes.Blocked => "BLOCKED",
        ScenarioOutcomes.TimedOut => "TIMED OUT",
        ScenarioOutcomes.Error => "ERROR",
        ScenarioOutcomes.Skipped => "Skipped",
        _ => r.ExpectedResult == ScenarioOutcomes.Failed ? "Failed (expected)" : "FAILED"
    };

    private static string InitialGrid(string gridKey) => VisualBaselineSet.TryParseGridKey(gridKey, out _, out string initial) ? initial : gridKey;
    internal static VisualEntryResult[] VisualEntries(BatchResult batch, ScenarioResult r) =>
        batch.Visual?.Entries.Where(e => e.ScenarioId == r.ScenarioId && InitialGrid(e.Grid) == r.Size).ToArray() ?? [];

    private static int Rank(string status) => status switch
    {
        VisualStatuses.Mismatch => 0, VisualStatuses.BaselineInvalid => 1, VisualStatuses.NormalizationError => 2,
        VisualStatuses.NotCaptured => 3, VisualStatuses.BaselineMissing => 4, VisualStatuses.Match => 5, _ => 6
    };
    // Visual verdict of an execution (worst gated checkpoint); null when the run did not compare baselines at all.
    internal static string? Visual(BatchResult batch, ScenarioResult r)
    {
        if (batch.Visual is null) return null;
        var entries = VisualEntries(batch, r);
        return entries.Length == 0 ? VisualStatuses.NotRun : entries.Select(e => e.Status).OrderBy(Rank).First();
    }
    internal static string VisualColumn(string? status) => status switch
    {
        VisualStatuses.Match => "Match", VisualStatuses.Mismatch => "MISMATCH", VisualStatuses.BaselineMissing => "MISSING",
        VisualStatuses.BaselineInvalid => "INVALID", VisualStatuses.NormalizationError => "NORM. ERROR", VisualStatuses.NotCaptured => "NOT CAPTURED",
        _ => "-"
    };
    internal static string VisualPhrase(string? status) => status switch
    {
        null => "Not compared (this run did not compare baselines)",
        VisualStatuses.Match => "Match", VisualStatuses.Mismatch => "MISMATCH", VisualStatuses.BaselineMissing => "MISSING baseline (no matching baseline for this environment)",
        VisualStatuses.BaselineInvalid => "INVALID baseline", VisualStatuses.NormalizationError => "NORMALIZATION ERROR",
        VisualStatuses.NotCaptured => "NOT CAPTURED", _ => "Not gated (no baseline entry for this execution)"
    };

    // Header verdict: FAILED only for Failed/Error/TimedOut executions, then BLOCKED, CANCELLED, visual gate (exit code 3).
    internal static string RunVerdict(BatchResult batch)
    {
        if (batch.Results.Length == 0) return "NO EXECUTIONS";
        var (passed, failed, blocked, cancelled) = WorkbenchDialog.Categories(batch);
        return failed > 0 ? "FAILED" : blocked > 0 ? "BLOCKED" : cancelled > 0 ? "CANCELLED" : passed == 0 ? "SKIPPED"
            : WorkbenchMatrix.Aggregate("Passed", batch.Visual is null ? "Not requested" : batch.Visual.Passed ? "Match" : "Failed").ToUpperInvariant();
    }
    internal static string VisualSummary(BatchResult batch)
    {
        if (batch.Visual is not { } v) return "Visual: not compared";
        if (v.Entries.All(e => e.Status == VisualStatuses.NotRun)) return "Visual: no gated captures";
        var parts = new List<string> { $"{v.Count(VisualStatuses.Match)} match" };
        void Add(string status, string word) { int n = v.Count(status); if (n > 0) parts.Add($"{n} {word}"); }
        Add(VisualStatuses.Mismatch, "mismatch"); Add(VisualStatuses.BaselineMissing, "missing"); Add(VisualStatuses.BaselineInvalid, "invalid");
        Add(VisualStatuses.NormalizationError, "normalization error"); Add(VisualStatuses.NotCaptured, "not captured");
        return "Visual: " + string.Join(", ", parts);
    }

    internal static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name && !Path.IsPathRooted(name);
    internal static bool Exists(string root, string name) => ValidName(name) && File.Exists(Path.Combine(root, name));

    // The checkpoint's proxyNative record (kind, artifact, details), or a reason why it cannot be read; null when absent.
    internal static (string? Kind, string? Artifact, string? Problem) ProxyNative(string root, CheckpointResult c)
    {
        if (!Exists(root, c.Metadata)) return (null, null, null);
        try
        {
            string path = Path.Combine(root, c.Metadata);
            if (new FileInfo(path).Length > 16 * 1024 * 1024) return (null, null, "metadata exceeds 16 MiB");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("proxyNative", out var native) || native.ValueKind != JsonValueKind.Object) return (null, null, null);
            string? kind = native.TryGetProperty("kind", out var k) ? k.GetString() : null;
            string? artifact = native.TryGetProperty("artifact", out var a) ? a.GetString() : null;
            return (kind, artifact, ValidName(artifact) ? null : "the recorded native artifact name is not a flat run file");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return (null, null, "metadata unavailable: " + ex.Message); }
    }

    // Every artifact of a checkpoint, logical first. In Native driver runs the checkpoint PNG is the driver's native PNG.
    internal static EvidenceRow[] Evidence(BatchResult batch, CheckpointResult c)
    {
        var rows = new List<EvidenceRow>();
        if (batch.Driver == "NullDriver") rows.Add(new("Logical PNG", c.Png, "cells rendered by Workbench", false));
        else if (batch.Driver == "Win32ConsoleDriver") rows.Add(new("Native console", c.Png, "owned conhost host window (native driver)", true));
        else rows.Add(new(batch.Driver.StartsWith("SDLGpu", StringComparison.Ordinal) ? "Native GPU pixels" : "Native pixels", c.Png,
            WorkbenchCapabilities.TargetDriverName(batch.Driver) + " renderer target (native driver)", true));
        rows.Add(new("Cells (TVS1)", c.Cells, "canonical logical cells", false));
        rows.Add(new("Metadata", c.Metadata, "backend and native details", false));
        var (kind, artifact, problem) = ProxyNative(batch.RunRoot, c);
        if (problem is not null) rows.Add(new("Native evidence", artifact ?? "", "! " + problem, true));
        else if (artifact is not null)
            rows.Add(kind switch
            {
                ProxyCaptureKinds.Gpu => new("GPU frame", artifact, "SDL GPU completed frame (native)", true),
                ProxyCaptureKinds.Console => new("Console cells", artifact, "owned conhost console cells (native text, not pixels)", true),
                _ => new("Native pixels", artifact, "SDL renderer pixels (native)", true)
            });
        if (c.Visual is { } v)
        {
            if (v.Diff is { } diff) rows.Add(new("Comparison", diff, "baseline comparison record (JSON)", false));
            if (v.DiffPng is { } diffPng) rows.Add(new("Difference PNG", diffPng, "expected | actual | difference, logical", false));
            if (v.CandidatePng is { } candidatePng) rows.Add(new("Candidate PNG", candidatePng, "unreviewed logical candidate", false));
            if (v.Candidate is { } candidate) rows.Add(new("Candidate cells", candidate, "unreviewed candidate TVS1", false));
            if (v.CandidateMetadata is { } candidateMeta) rows.Add(new("Candidate data", candidateMeta, "candidate provenance (JSON)", false));
        }
        else if (Exists(batch.RunRoot, BaselineStem(c) + ".candidate.png"))
            rows.Add(new("Candidate PNG", BaselineStem(c) + ".candidate.png", "review copy of a matched capture (logical)", false));
        return rows.ToArray();
    }
    /// <summary>Findings listed per layout-audit assertion in the Results inspector (the rest: run report and run.json).</summary>
    internal const int LayoutFindingRows = 25, LayoutCoverageRows = 12;

    // Drilldown of a layout-audit result: profile, scope, counts, every listed finding (class, rule, view, rect, message,
    // evidence) and the coverage (evaluated / not applicable / gaps / suppressed / truncation / notes). Bounded; long rows wrap.
    internal static List<InspectorRow> Layout(AssertionResult a, LayoutAuditReport report)
    {
        const string Pad = "         ", Sub = "                   ";
        var rows = new List<InspectorRow>();
        var c = report.Coverage;
        int info = report.Findings.Length - report.Errors - report.Warnings;
        rows.Add(new($"{Pad}layout    profile {c.Profile}, scope {c.Scope ?? "(screen)"}: {c.AuditedNodes} of {c.ScopeNodes} view(s) audited"
            + (c.Complete ? ", complete coverage" : ", PARTIAL coverage")));
        rows.Add(new($"{Pad}findings  {report.Errors} error, {report.Warnings} warning, {info} info; {c.Suppressed.Length} suppressed"
            + (c.Profile == LayoutProfiles.Strict ? " (strict: warnings also fail)" : " (only error-class findings fail)")));
        foreach (var f in report.Findings.Take(LayoutFindingRows))
        {
            string mark = f.Class switch { LayoutFindingClasses.Error => "ERROR", LayoutFindingClasses.Warning => "warn ", _ => "info " };
            rows.Add(new($"{Pad}  {mark} {f.Rule} at {f.Rect} on {f.View}" + (f.OtherPath is { } other ? $" vs {other}{(f.OtherRect is { } r ? " " + r : "")}" : "")
                + (f.Confidence != "high" ? $" (confidence {f.Confidence})" : "")));
            rows.Add(new($"{Sub}{f.Message}"));
            if (f.Evidence is { Length: > 0 } evidence) rows.Add(new($"{Sub}evidence: {evidence}"));
        }
        if (report.Findings.Length > LayoutFindingRows)
            rows.Add(new($"{Pad}  ... {report.Findings.Length - LayoutFindingRows} more finding(s): see the run report (Markdown) and run.json"));
        rows.Add(new($"{Pad}coverage  evaluated {c.Evaluated.Length}: {List(c.Evaluated)}"));
        if (c.NotApplicable.Length > 0) rows.Add(new($"{Sub}not applicable {c.NotApplicable.Length}: {List(c.NotApplicable)}"));
        if (c.Unavailable.Length == 0) rows.Add(new($"{Sub}gaps: none"));
        else
        {
            rows.Add(new($"{Sub}gaps {c.Unavailable.Length} (not evaluated on this evidence):"));
            foreach (var g in c.Unavailable.Take(LayoutCoverageRows)) rows.Add(new($"{Sub}  {g.Rule}{(g.Path is { } p ? " on " + p : "")}: {g.Reason}"));
            if (c.Unavailable.Length > LayoutCoverageRows) rows.Add(new($"{Sub}  ... {c.Unavailable.Length - LayoutCoverageRows} more gap(s)"));
        }
        if (c.Suppressed.Length > 0)
        {
            rows.Add(new($"{Sub}suppressed {c.Suppressed.Length}:"));
            foreach (var x in c.Suppressed.Take(LayoutCoverageRows)) rows.Add(new($"{Sub}  {x.Rule} on {x.Path}: {x.Reason}"));
            if (c.Suppressed.Length > LayoutCoverageRows) rows.Add(new($"{Sub}  ... {c.Suppressed.Length - LayoutCoverageRows} more"));
        }
        if (c.Truncated) rows.Add(new($"{Sub}TRUNCATED tree: {c.OmittedNodes} view(s) inside the scope were not captured"));
        foreach (string note in c.Notes) rows.Add(new($"{Sub}note: {note}"));
        return rows;
    }

    private static string List(string[] rules) => rules.Length == 0 ? "none" : string.Join(", ", rules);

    private static string BaselineStem(CheckpointResult c) => c.Visual?.Baseline ?? Path.GetFileNameWithoutExtension(c.Cells);

    internal static int Passed(CheckpointResult c) => c.Assertions.Count(a => a.Outcome == AssertionOutcomes.Pass);
    internal static bool Failing(CheckpointResult c) => c.Assertions.Any(a => a.Outcome != AssertionOutcomes.Pass && a.Severity == AssertionSeverity.Error);

    // The checkpoint list: Overview, the recorded checkpoints and, for executions that did not pass, the checkpoints of
    // the scenario definition that were not reached (only when the current catalog still has that scenario).
    internal static (string Text, CheckpointResult? Checkpoint)[] Checkpoints(BatchResult batch, ScenarioResult r, ScenarioDescriptor? descriptor, int width)
    {
        var rows = new List<(string, CheckpointResult?)> { ("Overview", null) };
        int name = Math.Clamp(width - 17, 8, 24);
        foreach (var c in r.Checkpoints)
        {
            string assertions = c.Assertions.Length == 0 ? "captured" : $"{Passed(c)}/{c.Assertions.Length}" + (Failing(c) ? "  FAILED" : " passed");
            string visual = c.Visual is { } v ? "  " + VisualColumn(v.Status) : "";
            rows.Add((WorkbenchText.Column(c.Checkpoint, name) + " " + assertions + visual, c));
        }
        if (r.Outcome != ScenarioOutcomes.Passed && descriptor is not null)
            foreach (var step in descriptor.Steps.Where(s => s.Kind == ScenarioStep.Checkpoint && r.Checkpoints.All(c => c.Checkpoint != s.Name)))
                rows.Add((WorkbenchText.Column(step.Name, name) + " not reached", null));
        return rows.ToArray();
    }

    // Overview of one execution (): result, visual, expectations, target lifecycle, backend, paths.
    internal static List<InspectorRow> Overview(BatchResult batch, ScenarioResult r, ScenarioDescriptor? descriptor, TimeSpan? duration, ResultSetup? setup)
    {
        var rows = new List<InspectorRow>();
        void Add(string label, string text, string? path = null, bool folder = false) => rows.Add(new($"{label,-11}{text}", path, folder));
        int failed = r.FailedAssertions.Count();
        string firstFailure = r.Checkpoints.FirstOrDefault(Failing) is { } fc ? $" at checkpoint {fc.Checkpoint}: {fc.Assertions.Count(a => a.Outcome != AssertionOutcomes.Pass && a.Severity == AssertionSeverity.Error)} assertion(s) failed" : "";
        int assertions = r.Checkpoints.Sum(c => c.Assertions.Length);
        // A target that failed before its first checkpoint is explained in plain words; the category (ERROR,
        // TIMED OUT) is kept, and the original text stays one Enter away.
        if (StartupFailure.Of(r) is { } startup)
        {
            Add("Result", $"{Execution(r)} - {startup.Title}");
            Add("Reason", startup.Reason);
            Add("Action", startup.Action);
            rows.Add(new($"{"Technical",-11}Technical details... (Enter shows the original error and the target's stderr)") { Technical = r.Reason });
        }
        else
        {
            Add("Functional", Execution(r) + (r.Outcome == ScenarioOutcomes.Passed ? $" (all {assertions} assertion{(assertions == 1 ? "" : "s")} passed)" : firstFailure));
            if (r.Reason.Length > 0 && r.Outcome != ScenarioOutcomes.Passed) Add("Reason", r.Reason);
        }
        string? visual = Visual(batch, r);
        var entries = VisualEntries(batch, r);
        if (visual is null || entries.Length == 0) Add("Visual", VisualPhrase(visual));
        else foreach (var (e, i) in entries.Select((e, i) => (e, i)))
            Add(i == 0 ? "Visual" : "", $"{VisualColumn(e.Status)} at checkpoint {e.Checkpoint} ({e.Grid}): {e.Reason}");
        Add("Expected", r.ExpectedResult == ScenarioOutcomes.Passed ? "Passed" : "Failed (expected-failure scenario; counted as failed)");
        Add("Scenario", descriptor is null ? r.ScenarioId : $"{descriptor.Name} ({r.ScenarioId})");
        Add("Source", descriptor is null ? "not in the current catalog (removed, renamed or another target); this run's evidence is unchanged"
            : descriptor.External is not { Source.Length: > 0 } x ? "compiled diagnostic scenario (no external JSON document)"
            : $"external JSON {Path.GetFileName(x.Source)}" + (x.FixtureId.Length > 0 ? $", bound to fixture {x.FixtureId}" : ", Black-Box Mode") + " (Results > Show in Scenarios)");
        if (entries.Select(e => e.BaselineScenarioId).FirstOrDefault(b => b is not null) is { } baselineId)
            Add("Baselines", $"baseline identity {baselineId} (explicit executionScenario mapping); run identity stays {r.ScenarioId}");
        Add("Grid", r.Size + (setup is null ? "" : "   test mode " + setup.ModeName));
        Add("Duration", duration is { } d ? $"{d.TotalSeconds:F1} s (measured by the Workbench)" : "not recorded for this run");
        Add("Checks", $"{r.Checkpoints.Length} checkpoint{(r.Checkpoints.Length == 1 ? "" : "s")}, {assertions} assertion{(assertions == 1 ? "" : "s")}" + (failed > 0 ? $", {failed} failed" : ""));
        Add("Target", (r.TargetExited ? "exited normally" : "did not report a normal exit") + " | fixture integrity " + (r.FixtureIntegrityVerified ? "verified" : "not verified")
            + " | cleanup " + (r.CleanupSucceeded ? "succeeded" : "INCOMPLETE"));
        Add("Backend", $"requested {WorkbenchCapabilities.TargetDriverName(batch.Driver == "NullDriver" ? setup?.ProxyBackend ?? "NullDriver" : batch.Driver)}");
        foreach (string line in WorkbenchDialog.ProxyDetails(batch, r)) Add("", line);
        foreach (string line in WorkbenchDialog.NativeDetails(batch, r)) Add("", line);
        bool native = r.Checkpoints.Any(c => Evidence(batch, c).Any(e => e.Native));
        Add("Evidence", "logical cells and logical PNG; native evidence " + (native ? "recorded (see the checkpoints)" : "not recorded"));
        if (batch.RunRoot.Length > 0)
        {
            Add("Report", batch.Report!, batch.Report);
            Add("Metadata", Path.Combine(batch.RunRoot, "run.json"), Path.Combine(batch.RunRoot, "run.json"));
            string request = Path.Combine(batch.RunRoot, "workbench-request.json");
            if (File.Exists(request)) Add("Request", request, request);
            Add("Folder", batch.RunRoot, batch.RunRoot, true);
        }
        return rows;
    }

    // Checkpoint view (/§9.12): assertions with expected/actual as recorded, inspection, visual, evidence rows.
    internal static List<InspectorRow> Checkpoint(BatchResult batch, ScenarioResult r, CheckpointResult c)
    {
        var rows = new List<InspectorRow>();
        rows.Add(new(c.Assertions.Length == 0 ? "Assertions  none declared for this checkpoint"
            : $"Assertions  {Passed(c)} of {c.Assertions.Length} passed"));
        foreach (var a in c.Assertions)
        {
            string state = a.Outcome == AssertionOutcomes.Pass ? "PASS" : a.Severity == AssertionSeverity.Warning ? a.Outcome + " (warning)" : a.Outcome;
            rows.Add(new($"  {state,-6} {a.Id}  {a.Kind}"));
            rows.Add(new($"         expected  {a.Expected}"));
            if (a.Outcome != AssertionOutcomes.Pass || a.Actual is not null)
                rows.Add(new($"         actual    {a.Actual ?? "not recorded"}"));
            if (a.Explanation.Length > 0 && (a.Outcome != AssertionOutcomes.Pass || a.Layout is not null)) rows.Add(new($"         message   {a.Explanation}"));
            if (a.Layout is { } layout) rows.AddRange(Layout(a, layout));
        }
        rows.Add(new(c.InspectionErrors.Length == 0 ? "Inspection  no inspection errors" : $"Inspection  {c.InspectionErrors.Length} error(s)"));
        foreach (string e in c.InspectionErrors) rows.Add(new("  ! " + e));
        rows.Add(new("Visual      " + (c.Visual is { } v ? $"{VisualPhrase(v.Status)}: {v.Reason}"
            + (v.DifferingCells > 0 ? $" ({v.GlyphDifferences} glyph, {v.AttributeDifferences} attribute)" : "")
            + (v.Normalizations.Length > 0 ? $"; normalizations {string.Join(", ", v.Normalizations)} ({v.MaskedCells} cells masked)" : "")
            : batch.Visual is null ? "not compared (no baseline comparison in this run)" : "not gated (no baseline entry for this checkpoint)")));
        rows.Add(new("Evidence (Enter opens the file)"));
        foreach (var e in Evidence(batch, c))
        {
            bool exists = Exists(batch.RunRoot, e.File);
            string file = e.File.Length == 0 ? "-" : e.File;
            string head = $"  {e.Label,-16} {file}", tail = e.Layer + (exists || e.Layer.StartsWith('!') ? "" : "   ! missing");
            string? path = ValidName(e.File) ? Path.Combine(batch.RunRoot, e.File) : null;
            // Long rows keep the provenance on its own line under the file name (both lines open the file).
            if (head.Length + 3 + tail.Length <= 92) rows.Add(new(head + "   " + tail, path, false, e.Native));
            else { rows.Add(new(head, path, false, e.Native)); rows.Add(new(new string(' ', 19) + tail, path, false, e.Native)); }
        }
        return rows;
    }
}
