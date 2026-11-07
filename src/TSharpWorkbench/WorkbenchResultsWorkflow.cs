using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// A Re-run request from Results: one execution (scenario and grid) of an earlier run with the setup that run recorded.
// It is a session operation: it opens Run Configuration for review and changes neither the session configuration nor
// the project defaults; the run gets a new run folder.
internal sealed record RerunScope(string ScenarioId, string Size, ResultSetup? Setup, string RunName, string? RunStore)
{
    internal const string Scope = "rerun";
    internal WorkbenchProject Apply(WorkbenchProject session) => (Setup is not { } s ? session : session with
    {
        TestType = s.TestType, Mode = s.TestType == "rendering" ? s.Mode : "NullDriver",
        PreferredNativeMode = s.TestType == "rendering" && NativeExecution.IsMode(s.Mode) ? s.Mode : session.PreferredNativeMode,
        ProxyBackend = s.ProxyBackend, ProxyCapture = s.ProxyCapture, VisualBehavior = s.VisualBehavior,
        Inspect = s.Inspect, Timeout = s.Timeout, ScenarioTimeout = s.ScenarioTimeout
    }) with { ScreenSizes = [Size], RunScope = Scope };
}

// The workspace side of Results, run history navigation, Re-run, Baseline Comparison and candidate approval.
internal sealed partial class WorkbenchDialog : IResultsHost
{
    // Per-execution durations measured by the Workbench from runner progress (session runs only; run.json has none).
    private readonly Dictionary<string, Dictionary<string, TimeSpan>> durations = [];
    private Dictionary<string, TimeSpan> liveDurations = [];
    private (string Key, DateTimeOffset Began)? executionClock;
    private DateTimeOffset runStarted;
    private RerunScope? pendingRerun;
    // Test seam: the operating-system "open" of an artifact or folder (UseShellExecute); never used for writing.
    internal static Action<string, bool> Opener { get; set; } = (path, _) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    internal WorkbenchResultsDialog? ResultsWindow => resultsWindow;
    internal WorkbenchComparisonWindow? Comparison => comparisonWindow?.owner is not null ? comparisonWindow : null;
    internal BatchResult? ViewedRun => resultIndex >= 0 && resultIndex < history.Count ? history[resultIndex] : null;

    private void TrackProgress(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (message.StartsWith("Running ", StringComparison.Ordinal)) { executionClock = (message["Running ".Length..].Trim(), now); return; }
        int colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0 || executionClock is not { } clock) return;
        string rest = message[(colon + 2)..]; int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
        string key = (dash < 0 ? rest : rest[..dash]).Trim();
        if (key == clock.Key) { liveDurations[key] = now - clock.Began; executionClock = null; }
    }
    private Func<ScenarioResult, TimeSpan?> DurationsOf(string root) =>
        durations.TryGetValue(root, out var map) ? r => map.TryGetValue(r.ScenarioId + " / " + r.Size, out var d) ? d : null : _ => null;

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException)
        { Error(ex.Message.ReplaceLineEndings(" "), ex.ToString()); }
    }

    // ── Results window ───────────────────────────────────────────────────────────────

    private void ShowResults()
    {
        if (history.Count == 0 && Native is { } native)
        {
            var report = ReadMatrixHistory().SelectMany(r => r.Results.Reverse()).FirstOrDefault(r => r.Report is not null)?.Report;
            if (report is not null)
            {
                try { Remember(WorkbenchRunHistory.Load(native.Session.ResolveArtifact(report))); }
                catch (Exception ex) { Error("Cannot open archived results: " + ex.Message); return; }
            }
        }
        if (history.Count == 0 && activeRequest is null) { Error("No results yet. Configure a run with F5."); return; }
        if (resultsWindow is null) resultIndex = activeRequest is null ? history.Count - 1 : history.Count;
        OpenResultsWindow();
    }
    private void OpenResultsWindow()
    {
        if (resultsWindow is not null) { resultsWindow.Reload(); resultsWindow.Select(); return; }
        resultsWindow = new WorkbenchResultsDialog(desktop, this, WorkbenchPreferences.Load(preferences.StoragePath).Window("results"));
        var window = resultsWindow;
        resultsWindow.Closed = () => { resultsWindow = null; RememberWindow("results", window); };
        desktop.Insert(resultsWindow); resultsWindow.Select();
    }
    private void RememberWindow(string name, TView window)
    {
        try { WorkbenchPreferences.Update(preferences.StoragePath, p => p.WithWindow(name, window.GetBounds())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal static string[] RunFolders(string folder)
    {
        // The runner names folders run-<UTC yyyyMMdd-HHmmss>-<random>; within one second the folder creation time decides,
        // then the full name, so the order is deterministic and chronological for runner-created folders.
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder, "run-*")
                .OrderBy(d => Path.GetFileName(d)[..Math.Min(19, Path.GetFileName(d).Length)], StringComparer.Ordinal)
                .ThenBy(Directory.GetCreationTimeUtc).ThenBy(d => Path.GetFileName(d), StringComparer.Ordinal).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    private ResultSetup? SetupOf(BatchResult batch) =>
        requests.TryGetValue(batch.RunRoot, out var request) ? ResultSetup.FromProject(request.Project) : ResultSetup.FromRun(batch);

    ResultsView IResultsHost.View()
    {
        if (resultIndex >= history.Count || resultIndex < 0)
        {
            if (activeRequest is { } active)
            {
                var p = active.Project; var setup = ResultSetup.FromProject(p);
                int done = completed - cancelled;
                return new(new BatchResult("", []) { Driver = p.Mode },
                [
                    $"Run       in progress   started {runStarted.ToLocalTime():dd.MM.yyyy HH:mm:ss}   {(cancelRequested ? "Cancelling" : "Running")} {Math.Min(done + 1, Math.Max(1, total))} of {total}   (this session)",
                    $"Setup     {setup.Label} | {string.Join(", ", p.ScreenSizes)} | {Path.GetFileName(p.Target)}",
                    $"Output    {activeRunDirectory}",
                    $"Verdict   RUNNING   {done}/{total} completed; {passed} passed / {failed} failed / {blocked} blocked / " + (skipped > 0 ? $"{skipped} skipped / " : "") + $"{cancelled} cancelled    Now {currentExecution}"
                ], true, "", _ => null, setup);
            }
            if (history.Count == 0) return new(new BatchResult("", []), ["Run       none in this session", "", "", ""], false, "", _ => null, null);
            resultIndex = history.Count - 1;
        }
        var batch = history[resultIndex];
        (string Target, DateTimeOffset Began, TimeSpan Duration, string TargetPath) info = runInfo.TryGetValue(batch.RunRoot, out var i) ? i : ("Unknown target", DateTimeOffset.MinValue, TimeSpan.Zero, "");
        string folder = Path.GetDirectoryName(batch.RunRoot) ?? "";
        var runs = RunFolders(folder);
        int position = Array.FindIndex(runs, r => WorkbenchPreferences.SamePath(r, batch.RunRoot));
        string where = position < 0 ? $"in {Path.GetFileName(folder)}" : $"run {position + 1} of {runs.Length} in {Path.GetFileName(folder)}";
        var setupOf = SetupOf(batch);
        var matrixResult = FindMatrixResult(batch);
        return new(batch,
        [
            $"Run       {Path.GetFileName(batch.RunRoot)}   {info.Began.ToLocalTime():dd.MM.yyyy HH:mm:ss}   {info.Duration.TotalSeconds:F1} s   {where}   ("
                + (requests.ContainsKey(batch.RunRoot) ? "this session" : "historical run") + ")",
            $"Setup     {setupOf?.Label ?? "setup not recorded"} | {string.Join(", ", batch.Results.Select(r => r.Size).Distinct())} | {info.Target}",
            matrixResult is { } mr ? $"Test {mr.Entry.TestId}: {mr.Entry.Title} | Requested {mr.Entry.Environment.Name} | Actual {mr.ActualOs}/{mr.ActualDriver}/{mr.ActualCapture}/{mr.ActualGrid}"
                : $"External run: {WorkbenchText.Path(batch.RunRoot, 100)}   report.md, run.json",
            $"Overall   {ResultText.RunVerdict(batch)}   {Completion(batch)} | {Counts(batch)}   {ResultText.VisualSummary(batch)}"
        ], false, batch.RunRoot, DurationsOf(batch.RunRoot), setupOf) {
            Provenance = matrixResult is { } entry ? [
                $"Test ID    {entry.Entry.TestId}", $"Test title {entry.Entry.Title}",
                $"Description {entry.Entry.Description}", $"Preconditions (documentation) {entry.Entry.Preconditions}",
                $"Requested Environment {entry.Entry.Environment.Id}: {entry.Entry.Environment.Name} | {entry.Entry.Environment.OperatingSystem}/{entry.Entry.Environment.Driver}/{entry.Entry.Environment.Capture}/{entry.Entry.Environment.Width}x{entry.Entry.Environment.Height}",
                $"Actual {entry.ActualOs ?? "unknown"}/{entry.ActualDriver ?? "unknown"}/{entry.ActualCapture ?? "unknown"}/{entry.ActualGrid ?? "unknown"}; initial {entry.InitialGrid ?? "unknown"}",
                $"Overall {entry.Overall} | Functional {entry.Functional ?? entry.Outcome} | Visual {entry.Visual ?? "not recorded"}"
            ] : ["External/legacy result: no project Test identity was recorded."]
        };
    }
    WorkbenchNotifications IResultsHost.Notifications => notifications;
    ScenarioDescriptor? IResultsHost.Descriptor(string id) => catalog.FirstOrDefault(s => s.Id == id);
    void IResultsHost.Navigate(int direction) => Guard(() => NavigateRuns(direction));
    void IResultsHost.OpenRun() => Guard(OpenHistoricalRun);
    void IResultsHost.Rerun(BatchResult batch, ScenarioResult result) => Guard(() => RerunExecution(batch, result));
    void IResultsHost.Compare(BatchResult batch, ScenarioResult result, CheckpointResult? checkpoint) => Guard(() => CompareCheckpoint(batch, result, checkpoint));
    void IResultsHost.ReviewCandidate(BatchResult batch, ScenarioResult result, CheckpointResult? checkpoint) => Guard(() => ReviewCandidate(batch, result, checkpoint));
    void IResultsHost.OpenPath(string path, bool folder) => OpenPath(path, folder);
    private void OpenPath(string path, bool folder)
    {
        if (folder ? !Directory.Exists(path) : !File.Exists(path))
        { Error($"{Path.GetFileName(path)} is missing; it may have been deleted after the run was loaded."); return; }
        try { Opener(path, folder); Status((folder ? "Opened folder " : "Opened ") + Path.GetFileName(path) + " with the system default application."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { Error($"{Path.GetFileName(path)} could not be opened: {ex.Message}", ex.ToString()); }
    }

    private void SelectRun(int index, string? skipped = null)
    {
        resultIndex = index;
        OpenResultsWindow();
        var batch = history[index];
        if (skipped is not null) Error(skipped, skipped);
        else Status($"Run {Path.GetFileName(batch.RunRoot)} loaded: {ResultText.RunVerdict(batch)}.");
    }
    private int Remember(WorkbenchRunHistory.Loaded loaded)
    {
        int existing = history.FindIndex(b => WorkbenchPreferences.SamePath(b.RunRoot, loaded.Batch.RunRoot));
        if (existing >= 0) return existing;
        history.Add(loaded.Batch); runInfo[loaded.Batch.RunRoot] = (loaded.Target, loaded.Began, loaded.Duration, "");
        return history.Count - 1;
    }

    // Earlier/later run (< Run, Run >, Ctrl+Left/Right): the run-* folders next to the shown run in RunFolders order.
    // Unreadable folders are skipped and reported; navigation never writes.
    internal void NavigateRuns(int direction)
    {
        bool live = resultIndex >= history.Count || resultIndex < 0;
        string currentRoot = live ? "" : history[resultIndex].RunRoot;
        string folder = currentRoot.Length > 0 ? Path.GetDirectoryName(currentRoot)! : Path.GetFullPath(output.Data);
        var runs = RunFolders(folder).Where(r => activeRequest is null || !WorkbenchPreferences.SamePath(r, activeRunDirectory)).ToList();
        int index = live ? runs.Count : runs.FindIndex(r => WorkbenchPreferences.SamePath(r, currentRoot));
        int start;
        if (index >= 0) start = index + direction;
        else
        {
            int before = runs.Count(r => string.CompareOrdinal(Path.GetFileName(r), Path.GetFileName(currentRoot)) < 0);
            start = direction < 0 ? before - 1 : before;
        }
        var skipped = new List<string>();
        for (int i = start; i >= 0 && i < runs.Count; i += direction)
        {
            int known = history.FindIndex(b => WorkbenchPreferences.SamePath(b.RunRoot, runs[i]));
            try
            {
                int selected = known >= 0 ? known : Remember(WorkbenchRunHistory.Load(Path.Combine(runs[i], "run.json")));
                SelectRun(selected, skipped.Count == 0 ? null
                    : $"Skipped {skipped.Count} unreadable run folder{(skipped.Count == 1 ? "" : "s")}: {string.Join("; ", skipped)}");
                return;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                or InvalidOperationException or ArgumentException or FormatException)
            { skipped.Add($"{Path.GetFileName(runs[i])} ({ex.Message.ReplaceLineEndings(" ")})"); }
        }
        if (direction > 0 && !live && activeRequest is not null)
        { resultIndex = history.Count; OpenResultsWindow(); Status("Showing the run in progress."); return; }
        string which = direction < 0 ? "earlier" : "later";
        if (skipped.Count > 0) Error($"No {which} readable run in {Path.GetFileName(folder)}; {skipped.Count} unreadable run folder{(skipped.Count == 1 ? " was" : "s were")} skipped.", string.Join("\n", skipped));
        else Error($"No {which} run in {Path.GetFileName(folder)}. The shown run is unchanged.");
    }

    // TFileDialog navigates only for wildcard input: a literal "run.json" mask left the dialog in the process directory
    // and turned directory rows into "<dir>/run.json" selections. Browse *.json from the output root (or its nearest
    // existing ancestor); WorkbenchRunHistory.Load rejects anything that is not an existing run.json manifest, and an
    // invalid file never replaces the shown run.
    private void OpenHistoricalRun()
    {
        using var picker = new TFileDialog("*.json", "Open completed run", "Run ~m~etadata", FileDialogOptions.fdOpenButton | FileDialogOptions.fdNoLoadDir, 0); picker.helpCtx = 0x7dc4;
        picker.SetData(Path.Combine(InitialDirectory(output.Data), "*.json")); if (desktop.ExecView(picker) is not (Views.cmOK or Views.cmFileOpen)) return;
        picker.GetFileName(out string path); var loaded = WorkbenchRunHistory.Load(path);
        SelectRun(Remember(loaded));
    }

    // The execution (and checkpoint) the Results window has selected; menu commands act on it.
    private (BatchResult Batch, ScenarioResult Result, CheckpointResult? Checkpoint) SelectedResult()
    {
        if (history.Count == 0) throw new ArgumentException("No completed run. Use Results to select an execution.");
        if (resultsWindow?.Selected is { } selected && ViewedRun is { } shown) return (shown, selected, resultsWindow.SelectedCheckpoint);
        var batch = history[Math.Clamp(resultIndex, 0, history.Count - 1)];
        return (batch, batch.Results.FirstOrDefault() ?? throw new ArgumentException("Empty run."), null);
    }
    private void OpenSelectedCaptures()
    {
        if (history.Count == 0) throw new ArgumentException("No completed run. Use Results to select an execution.");
        if (resultsWindow is null) ShowResults(); else OpenResultsWindow();
        resultsWindow?.FocusEvidence();
    }

    // ── Re-run ───────────────────────────────────────────────────────────────────────

    private WorkbenchMatrix.Result? FindMatrixResult(BatchResult batch) => Native is null ? null :
        ReadMatrixHistory().SelectMany(r => r.Results).FirstOrDefault(r => r.Report is { } report &&
            WorkbenchPreferences.SamePath(Path.GetDirectoryName(Native.Session.ResolveArtifact(report)), batch.RunRoot));

    private void RerunExecution(BatchResult batch, ScenarioResult result)
    {
        if (busy) { Error(activeRequest is null ? "Scenarios are loading. Re-run is available when loading has finished." : "Tests are running. Re-run opens Run Configuration when the run has finished."); return; }
        if (catalog.Length == 0 || target.Data != catalogTarget) throw new ArgumentException("No scenarios are loaded for this target. Press F2 to load them, then use Re-run again.");
        if (FindMatrixResult(batch) is { } matrix)
        {
            ConfigureMatrix(matrix.Entry.TestId, matrix.Entry.Environment.Id);
            return;
        }
        var setup = SetupOf(batch);
        ConfigureRun(new RerunScope(result.ScenarioId, result.Size, setup, Path.GetFileName(batch.RunRoot), setup?.BaselineStore));
    }
    // From a Results execution to its external JSON document in the Scenarios window (window 2). Read-only: the
    // document is only located; removed, renamed or compiled scenarios are explained instead.
    private void ShowResultScenarioDocument()
    {
        var (_, result, _) = SelectedResult();
        ShowScenarioDocument(result.ScenarioId);
    }

    private void ShowScenarioDocument(string scenarioId)
    {
        var current = catalog.FirstOrDefault(s => s.Id == scenarioId);
        if (current is null)
            throw new ArgumentException($"{scenarioId} is not in the current catalog: its document was removed or renamed, or the target changed. The historical run is unchanged; F2 refreshes the catalog.");
        if (current.External is not { Source.Length: > 0 } external)
            throw new ArgumentException($"{scenarioId} is a compiled diagnostic scenario of the target; it has no external JSON document.");
        if (!projectDefaults.ScenarioFiles.Any(f => WorkbenchPreferences.SamePath(f, external.Source)))
            throw new ArgumentException($"The document of {scenarioId} ({external.Source}) is no longer attached to this project; F2 refreshes the catalog.");
        ShowScenarios(external.Source);
        Status($"{scenarioId}: {Relative(external.Source)}" + (external.FixtureId.Length > 0 ? $" (bound to fixture {external.FixtureId})" : " (Black-Box Mode)"));
    }

    // Summary lines of a Re-run (shown by AssessRun while the Re-run scope is chosen).
    private IEnumerable<string> RerunSummary(RerunScope rerun, WorkbenchProject p)
    {
        yield return $"Re-run of {rerun.ScenarioId} at {rerun.Size} from {rerun.RunName}: a new run folder is created; the original run is not changed.";
        yield return "Re-run is a one-off session run: the session configuration and the project defaults stay unchanged.";
        if (rerun.Setup is { } s)
        {
            yield return "Setup restored from " + s.Source + ".";
            if (s.NotRecorded.Length > 0) yield return "Not recorded in that run (current values kept): " + string.Join(", ", s.NotRecorded) + ".";
        }
        else yield return "The setup of that run cannot be read; the current session settings are used.";
        if (p.TestType == "visual" && rerun.RunStore is { } store && p.BaselineStore is { } current && !WorkbenchPreferences.SamePath(store, current))
            yield return $"! The original run used the baseline store {store}; this run uses the project store {current}.";
    }

    // ── Baseline Comparison ──────────────────────────────────────────────────────────

    private VisualBaselineSet ResultBaselineSet(BatchResult batch)
    {
        if (batch.Visual?.Manifest == NativeProject.BaselineArtifact && Native is { } native)
        {
            string manifest = projectDefaults.BaselineStore ?? throw new ArgumentException("No active project baseline store.");
            if (native.OwnedBaselineArtifact(manifest) is null) throw new ArgumentException("Activate the owned baseline store before review.");
            using var request = JsonDocument.Parse(File.ReadAllBytes(SafeArtifact(batch.RunRoot, "workbench-request.json")));
            if (request.RootElement.GetProperty("baselineManifestHash").GetString() != ScreenSnapshot.Hash(File.ReadAllBytes(manifest)))
                throw new ArgumentException("The project baseline policy changed. Run again before review.");
            return VisualBaselineSet.Load(manifest);
        }
        if (string.IsNullOrWhiteSpace(batch.Visual?.Manifest) || !File.Exists(batch.Visual.Manifest))
            throw new ArgumentException("The original baseline store of this historical run is unavailable. Its results and artifacts remain readable; configure the original store and make a fresh visual run before comparison or approval.");
        return VisualBaselineSet.Load(batch.Visual.Manifest);
    }
    private void CompareSelected() { var (batch, result, checkpoint) = SelectedResult(); CompareCheckpoint(batch, result, checkpoint); }
    private void CompareCheckpoint(BatchResult batch, ScenarioResult r, CheckpointResult? c)
    {
        CheckpointResult[] targets = c is not null ? [c] : r.Checkpoints;
        var logical = targets.FirstOrDefault(t => t.Visual?.Diff is not null);
        var nativeCheckpoint = targets.FirstOrDefault(t => ResultText.Evidence(batch, t).Any(e => e.Native));
        if (logical is null && nativeCheckpoint is null)
            throw new ArgumentException("This execution has no logical baseline comparison" + (c is null ? "" : " for this checkpoint") + ". Functional captures are unreviewed, not matches.");
        var model = logical is not null ? LogicalComparison(batch, r, logical) : EvidenceComparison(batch, r, nativeCheckpoint!);
        Action? review = !model.Native && batch.Driver == "NullDriver" && logical?.Visual?.Baseline is { } key && ResultText.Exists(batch.RunRoot, key + ".candidate.json")
            ? () => Guard(() => ReviewCandidate(batch, r, logical)) : null;
        if (comparisonWindow?.owner is not null) comparisonWindow.Close();
        comparisonWindow = new WorkbenchComparisonWindow(desktop, model, path => OpenPath(path, false), review,
            WorkbenchPreferences.Load(preferences.StoragePath).Window("comparison")) { WorkbenchNotificationsHook = text => Error(text) };
        var window = comparisonWindow;
        comparisonWindow.Closed = () => RememberWindow("comparison", window);
        desktop.Insert(comparisonWindow); comparisonWindow.Select();
        Status($"Comparison of {r.ScenarioId} > {model.Checkpoint} at {model.Grid}: " + (model.Native ? "native evidence only (no approved native baselines)." : ResultText.VisualPhrase(model.Status) + "."));
    }
    private ComparisonModel LogicalComparison(BatchResult batch, ScenarioResult r, CheckpointResult cp)
    {
        var v = cp.Visual!;
        var set = ResultBaselineSet(batch);
        string gridKey = cp.Size == r.Size ? cp.Size : cp.Size + "-from-" + r.Size;
        var entry = set.FindForExecution(r.ScenarioId, cp.Checkpoint, gridKey) ?? throw new ArgumentException("This checkpoint is no longer gated by the baseline store. Run a fresh comparison.");
        using var diff = JsonDocument.Parse(File.ReadAllBytes(SafeArtifact(batch.RunRoot, v.Diff!)));
        string? recorded = diff.RootElement.TryGetProperty("expected", out var e) && e.TryGetProperty("cellsSha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        // The environment the run compared in (legacy runs: this machine, NullDriver), never another one.
        var approved = set.LoadApproved(entry, gridKey, BaselineEnvironment.FromComparison(diff.RootElement));
        ScreenSnapshot? expected = null;
        if (approved.Cells is not null)
        {
            if (recorded is null) throw new ArgumentException("An approved baseline was added after this run. Run a fresh comparison.");
            if (recorded != approved.CellsSha256) throw new ArgumentException("Approved baseline changed since this run. Run a fresh comparison.");
            expected = approved.Cells;
        }
        else if (v.Status is not (VisualStatuses.BaselineMissing or VisualStatuses.BaselineInvalid))
            throw new ArgumentException($"The approved baseline is now {approved.Status}: {approved.Reason} Run a fresh comparison.");
        ScreenSnapshot? actual; string source;
        if (v.Candidate is { } candidate) { actual = ScreenSnapshot.Deserialize(File.ReadAllBytes(SafeArtifact(batch.RunRoot, candidate))); source = $"normalized capture {candidate} (logical TVS1 cells)"; }
        else if (v.Status == VisualStatuses.Match) { actual = approved.Cells; source = "equal to the approved baseline after normalization (Match; logical TVS1 cells)"; }
        else { actual = ScreenSnapshot.Deserialize(File.ReadAllBytes(SafeArtifact(batch.RunRoot, cp.Cells))); source = $"raw capture {cp.Cells} (not normalized: {v.Status}; logical TVS1 cells)"; }
        string normalization = v.Normalizations.Length == 0 ? "none applied" : $"{v.Normalizations.Length} rule(s) applied: {string.Join(", ", v.Normalizations)} ({v.MaskedCells} cells masked before comparison)";
        bool ownedStore = Native?.OwnedBaselineArtifact(set.ManifestPath) is not null;
        string store = ownedStore ? "Project-owned baseline store" : Relative(set.ManifestPath);
        return new(r.ScenarioId, cp.Checkpoint, cp.Size == r.Size ? cp.Size : $"{cp.Size} (started at {r.Size})", v.Baseline, store, approved.CellsSha256,
            Path.GetFileName(batch.RunRoot), v.Status, v.Reason, expected, actual, source, normalization, ResultText.Evidence(batch, cp), batch.RunRoot, false);
    }
    private static ComparisonModel EvidenceComparison(BatchResult batch, ScenarioResult r, CheckpointResult cp)
    {
        var rows = ResultText.Evidence(batch, cp);
        var native = rows.Where(x => x.Native).ToArray();
        string source = string.Join("; ", native.Select(x => $"{x.Label}: {x.Layer}"));
        return new(r.ScenarioId, cp.Checkpoint, cp.Size, "", "", null, Path.GetFileName(batch.RunRoot), "NativeEvidence",
            "native captures are not compared with approved baselines", null, null, source.Length == 0 ? "native evidence" : source,
            "not applicable", rows, batch.RunRoot, true);
    }

    // ── candidate review and approval ────────────────────────────────────────────────

    private void ReviewSelected() { var (batch, result, checkpoint) = SelectedResult(); ReviewCandidate(batch, result, checkpoint); }
    private void ReviewCandidate(BatchResult batch, ScenarioResult r, CheckpointResult? c)
    {
        if (batch.Driver != "NullDriver" || batch.Visual is null)
            throw new ArgumentException("Only logical baseline candidates from Visual regression runs can be reviewed. Native approval is unavailable.");
        string[] keys = (c is not null ? [c] : r.Checkpoints).Select(t => t.Visual?.Baseline).OfType<string>()
            .Where(k => ResultText.Exists(batch.RunRoot, k + ".candidate.json")).Distinct().ToArray();
        if (keys.Length == 0) throw new ArgumentException("No candidate for this " + (c is null ? "execution" : "checkpoint") + ". Compare and keep candidates also keeps review copies of matches.");
        if (keys.Length > 1) throw new ArgumentException($"This execution has {keys.Length} candidates. Choose the checkpoint in Results (Checkpoints, Alt+K) first.");
        var set = ResultBaselineSet(batch);
        var review = CandidateReview.Load(batch, set, keys[0]);
        if (batch.Visual.Manifest == NativeProject.BaselineArtifact && Native is { } project)
        {
            var recorded = ReadMatrixHistory().SelectMany(run => run.Results).FirstOrDefault(result =>
                result.Report is { } report && WorkbenchPreferences.SamePath(project.Session.ResolveArtifact(report), Path.Combine(batch.RunRoot, "run.json")));
            var captured = r.Checkpoints.SingleOrDefault(checkpoint => checkpoint.Visual?.Baseline == review.Key);
            if (recorded is null || captured is null ||
                !string.Equals(recorded.ActualOs, review.Environment.Platform, StringComparison.OrdinalIgnoreCase) ||
                recorded.ActualDriver != review.Environment.Driver ||
                review.File.InitialGrid != r.Size || review.File.Grid != captured.Size ||
                recorded.Entry.ScenarioId != r.ScenarioId ||
                (review.File.ExecutionScenarioId ?? review.File.ScenarioId) != r.ScenarioId)
                throw new ArgumentException("Candidate identity does not match the recorded execution and checkpoint. Nothing was approved.");
        }
        bool ownedStore = Native?.OwnedBaselineArtifact(set.ManifestPath) is not null;
        string store = ownedStore ? "Project-owned baseline store" : Relative(set.ManifestPath);
        bool approve;
        using (var dialog = new WorkbenchCandidateDialog(desktop, review, store, png => OpenPath(png, false), ownedStore))
            approve = desktop.ExecView(dialog) == Views.cmYes && dialog.Confirm.value == 1;
        if (!approve) { Status("Candidate review closed; the baseline store is unchanged."); return; }
        if (!WorkbenchBaselineDialogs.Confirm(desktop, review, store, Native?.OwnedBaselineArtifact(store) is not null)) { Status("Approval cancelled; the baseline store is unchanged."); return; }
        string title = review.Replace ? "Cannot Replace Baseline" : "Cannot Approve Baseline";
        if (busy && activeRequest is not null)
        { Fail(title, "A run is active and reads the baseline store. Wait for the run to finish, then approve. Nothing was written.", null); return; }
        try
        {
            var current = VisualBaselineSet.Load(set.ManifestPath);
            if (review.Changed(current) is { } changed) { Fail(title, changed + " Nothing was written.", null); return; }
            // All store checks and writes stay in VisualBaselineSet.Approve (explicit key, approve-1, replace only when the store has one).
            if (Native is { } native && native.OwnedBaselineArtifact(current.ManifestPath) is not null)
            {
                string prefix = "baselines/approval-" + Guid.NewGuid().ToString("N");
                native.Session.AdoptBaselineStore(current.ManifestPath, staged =>
                {
                    VisualBaselineSet.Load(staged).Approve(review.RunRoot, [review.Key], "approve-1", review.Replace);
                    ValidateAdoptedStore(staged);
                }, prefix);
                string manifest = native.Session.ResolveArtifact(prefix + "/visual-baselines.json");
                var next = CaptureProject() with { BaselineStore = manifest };
                native.Store(next);
                projectDefaults = projectDefaults with { BaselineStore = manifest };
                sessionRun = sessionRun with { BaselineStore = manifest };
            }
            else current.Approve(review.RunRoot, [review.Key], "approve-1", review.Replace);
            RefreshNativeTitle();
            baselineExplorer?.Reload();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            string reason = ex switch
            {
                UnauthorizedAccessException => "Access to the baseline store was denied. Check the folder permissions.",
                ArgumentException a => a.Message.ReplaceLineEndings(" "),
                _ => "The baseline store could not be written: " + ex.Message.ReplaceLineEndings(" ") + " Check approved/" + review.Key + ".* before the next run."
            };
            Fail(title, reason + (ex is ArgumentException ? " Nothing was written." : ""), ex.ToString());
            return;
        }
        Status($"{(review.Replace ? "Replaced" : "Approved")} the baseline of {review.Key} from {review.RunName} in {store}. Run Visual regression again to compare.");
        resultsWindow?.SetNote($"{(review.Replace ? "Replaced" : "Approved")} just now: {review.Key}. Run again to compare.");
    }

    private void BrowseBaselines()
    {
        if (projectDefaults.BaselineStore is null) throw new ArgumentException("No baseline store is configured. Set one in Project Properties (Alt+O).");
        int? candidates = null;
        if (ViewedRun is { RunRoot.Length: > 0 } run && Directory.Exists(run.RunRoot))
            try { candidates = Directory.GetFiles(run.RunRoot, "*.candidate.json").Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        WorkbenchBaselineDialogs.StoreStatus(desktop, projectDefaults.BaselineStore, Relative(projectDefaults.BaselineStore), candidates, notifications, BaselineEnvironmentFor(SessionProject()));
    }
}
