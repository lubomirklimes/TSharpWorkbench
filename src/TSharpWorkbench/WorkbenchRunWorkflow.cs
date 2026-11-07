using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

internal sealed partial class WorkbenchDialog
{
    private WorkbenchRunMonitor? runningWindow;
    private WorkbenchResultsDialog? resultsWindow;
    private WorkbenchComparisonWindow? comparisonWindow;
    internal void SelectComparison()
    {
        if(comparisonWindow?.owner is not null)comparisonWindow.Select();
        else Error("No baseline comparison is open. Use Baselines > Compare with baseline on a Visual regression result.");
    }
    private WorkbenchRunRequest? activeRequest;
    private readonly Dictionary<string,WorkbenchRunRequest> requests=[];
    private int resultIndex;
    private string activeRunDirectory="";
    private BatchResult CurrentResults()=>resultIndex<history.Count&&resultIndex>=0?history[resultIndex]:new BatchResult("",[]){Driver=activeRequest?.Project.Mode??"NullDriver"};
    internal static string ModeName(WorkbenchProject p) => p.TestType switch { "visual" => "Visual regression", "rendering" => "Native driver", _ => "Functional" };
    private static string RunDriver(WorkbenchProject p) => WorkbenchCapabilities.TargetDriverName(p.TestType == "rendering" ? p.Mode : p.ProxyBackend);
    private static string RunLabel(WorkbenchProject p) => ModeName(p) + " | " + RunDriver(p)
        + (p.TestType == "visual" ? " | " + (p.VisualBehavior == "compare" ? "compare approved" : "compare and keep candidates") : "");
    private ScenarioDescriptor[] ScopeSelection(WorkbenchProject p)=>p.RunScope==RerunScope.Scope?RerunSelection():ScenarioSelection.Resolve(catalog,
        p.RunScope=="selected"?scenarios.MarkedIds:[],p.RunScope=="group"?FilterGroup??throw new ArgumentException("Choose a group in the Groups pane first; All scenarios is not a group."):null,p.RunScope=="all");
    // Re-run scope: the one scenario of the Results execution, looked up in the current catalog (never silently replaced).
    private ScenarioDescriptor[] RerunSelection()
    {
        if(pendingRerun is not {} rerun)throw new ArgumentException("Re-run is available from Results (select an execution, then Re-run...).");
        return catalog.Where(s=>s.Id==rerun.ScenarioId).Take(1).ToArray() is {Length:1} found?found
            :throw new ArgumentException($"{rerun.ScenarioId} is not in the current catalog of {Path.GetFileName(target.Data)}. Refresh with F2, or choose another scope.");
    }
    // Every selected scenario runs; one the chosen native driver cannot execute is recorded as Skipped by the
    // runner (counted in Results), never silently left out of the run.
    private ScenarioDescriptor[] ResolveRun(WorkbenchProject p)=>ScopeSelection(p);
    private string[] AvailableNativeModes() => OperatingSystem.IsWindows() ? WorkbenchCapabilities.NativeModes.Where(m => catalog.Any(s => NativeExecution.Supports(s, m))).ToArray() : [];
    private int? ScopeCount(string scope)
    {
        try { return ScopeSelection(sessionRun with { RunScope = scope }).Length; }
        catch (ArgumentException) { return null; }
    }

    // Run fields: what Run Configuration edits. They form the session configuration and, when saved, the project run defaults.
    internal static WorkbenchProject WithRunFields(WorkbenchProject target, WorkbenchProject run) => target with
    {
        Mode = run.Mode, ProxyBackend = run.ProxyBackend, ProxyCapture = run.ProxyCapture, PreferredNativeMode = run.PreferredNativeMode,
        RunScope = run.RunScope, TestType = run.TestType, VisualBehavior = run.VisualBehavior, ScreenSizes = run.ScreenSizes.ToArray(),
        CustomWidth = run.CustomWidth, CustomHeight = run.CustomHeight, Inspect = run.Inspect, Timeout = run.Timeout, ScenarioTimeout = run.ScenarioTimeout
    };
    internal static bool RunFieldsDiffer(WorkbenchProject a, WorkbenchProject b) =>
        WithRunFields(new WorkbenchProject(), a).Fingerprint() != WithRunFields(new WorkbenchProject(), b).Fingerprint();
    // The project as Run Configuration and the runner see it: project data plus the session run configuration.
    internal WorkbenchProject SessionProject() => WithRunFields(CaptureProject(), sessionRun);
    internal WorkbenchProject SessionRun => sessionRun;
    internal bool SessionDiffers => RunFieldsDiffer(sessionRun, projectDefaults);

    // Live assessment for Run Configuration: blocking problems by section, plain summary lines (/§9.7).
    internal RunAssessment AssessRun(WorkbenchProject p)
    {
        var problems = new List<RunProblem>();
        void Add(RunArea area, string text) => problems.Add(new(area, text));
        bool rerun = p.RunScope == RerunScope.Scope;
        try { (rerun ? p with { RunScope = "selected" } : p).Validate(); } catch (ArgumentException ex) { Add(RunArea.General, ex.Message); }
        bool native = p.TestType == "rendering", visual = p.TestType == "visual";
        ScenarioDescriptor[] selected = [];
        try { selected = ScopeSelection(p); }
        catch (ArgumentException ex)
        {
            Add(RunArea.Scope, p.RunScope == "selected" && scenarios.MarkedIds.Count == 0
                ? "No scenarios are marked. Mark scenarios in the Workspace (Space), or choose another scope; marks are kept."
                : ex.Message == "No scenarios selected." ? "This scope contains no runnable scenarios." : ex.Message);
        }
        string[] nativeModes = AvailableNativeModes();
        if (native && nativeModes.Length == 0) Add(RunArea.Mode, "Native driver is unavailable: " + NativeUnavailableReason());
        else if (native) { try { WorkbenchCapabilities.Validate(p.Mode); } catch (ArgumentException ex) { Add(RunArea.Driver, ex.Message); } }
        var eligible = native ? selected.Where(s => NativeExecution.Supports(s, p.Mode)).ToArray() : selected;
        int unsupported = selected.Length - eligible.Length;
        // The whole selection runs; what the native driver cannot execute is recorded as Skipped (platform/
        // driver unsupported) and counted in Results. Only a selection the driver can execute none of is a problem.
        var run = selected;
        var warnings = new List<string>();
        if (native && unsupported > 0 && nativeModes.Length > 0)
        {
            if (eligible.Length == 0 && selected.Length > 0)
                Add(RunArea.Scope, $"None of the {selected.Length} selected scenario{(selected.Length == 1 ? "" : "s")} can run on {WorkbenchCapabilities.TargetDriverName(p.Mode)}: only audited diagnostic workflows can (compiled, or external scenarios bound to an audited fixture). Choose Headless or Functional, or other scenarios; marks are kept.");
            else warnings.Add($"{unsupported} of {selected.Length} selected scenario{(selected.Length == 1 ? "" : "s")} will be recorded as Skipped (platform/driver unsupported): {WorkbenchCapabilities.TargetDriverName(p.Mode)} cannot run {(unsupported == 1 ? "it" : "them")}.");
            var first = selected.First(s => !NativeExecution.Supports(s, p.Mode));
            warnings.Add($"Not native: {first.Id}: {NativeExecution.Refusal(first, p.Mode)}" + (unsupported > 1 ? $" (and {unsupported - 1} more)." : "."));
        }
        if (native && eligible.Length > 0) warnings.Add($"Native eligible: {eligible.Length} scenario{(eligible.Length == 1 ? "" : "s")}"
            + (eligible.Count(s => s.External is not null) is > 0 and int bound ? $" ({bound} external, bound to audited fixtures; run identity stays external)." : "."));
        var bad = run.SelectMany(s => p.ScreenSizes.Where(g => !s.Grid.Supports(g)).Select(g => $"{s.Id}: {g} unsupported (range {s.Grid.Describe()})")).ToArray();
        foreach (string b in bad.Take(3)) Add(RunArea.Sizes, b);
        if (bad.Length > 3) Add(RunArea.Sizes, $"... and {bad.Length - 3} more incompatible scenario/size combinations.");
        string sizeNote = run.Length == 0 ? "Supported sizes are shown once scenarios are selected."
            : run.Select(s => s.Grid.Describe()).Distinct().Count() == 1
                ? $"{(run.Length == 1 ? "The selected scenario supports" : $"All {run.Length} selected scenarios support")} {run[0].Grid.Describe()}."
                : $"Selected scenarios support {run.Max(s => s.Grid.MinWidth)}x{run.Max(s => s.Grid.MinHeight)}..{run.Min(s => s.Grid.MaxWidth)}x{run.Min(s => s.Grid.MaxHeight)} in common.";

        int compiled = run.Count(s => s.Backend != "proxy-logical");
        if (!native)
        {
            if (WorkbenchCapabilities.ProxyUnavailable(p.ProxyBackend) is { } reason) Add(RunArea.Driver, reason);
            else if (p.ProxyBackend != "NullDriver" && compiled > 0)
                Add(RunArea.Driver, $"{WorkbenchCapabilities.TargetDriverName(p.ProxyBackend)} applies only to ordinary application scenarios; {compiled} selected {(compiled == 1 ? "is a compiled diagnostic scenario" : "are compiled diagnostic scenarios")}. Choose Headless, or Native driver.");
            if (p.ProxyCapture != ProxyCaptureKinds.None)
            {
                if (!ProxyCaptureKinds.ForBackend(p.ProxyBackend).Contains(p.ProxyCapture))
                    Add(RunArea.Evidence, $"{Capital(WorkbenchCapabilities.EvidenceName(p.ProxyCapture))} {(p.ProxyBackend == "NullDriver" ? "needs a window driver" : "is not available with " + WorkbenchCapabilities.TargetDriverName(p.ProxyBackend))}. Choose None or another driver.");
                else if (compiled > 0) Add(RunArea.Evidence, "Native evidence needs ordinary application scenarios; compiled diagnostics keep logical checkpoints only.");
            }
        }
        else if (p.ProxyCapture != ProxyCaptureKinds.None) Add(RunArea.Evidence, "Native driver mode keeps the driver's own PNG; choose None.");

        string store = p.BaselineStore is null ? "not configured (Project Properties, Alt+O)" : Relative(p.BaselineStore);
        string storeState = "";
        int gated = 0;
        (int, int, int, int)? baselineCounts = null;
        if (p.BaselineStore is null) { if (visual) Add(RunArea.Baselines, "Visual regression needs a baseline store. Set it in Project Properties (Alt+O)."); }
        else
        {
            try
            {
                var set = VisualBaselineSet.Load(p.BaselineStore);
                var environment = BaselineEnvironmentFor(p, run);
                int approved = 0, missing = 0, invalid = 0;
                foreach (var s in run)
                foreach (string g in p.ScreenSizes)
                foreach (var e in set.EntriesForExecution(s.Id))
                foreach (string k in e.Grids.Where(k => VisualBaselineSet.TryParseGridKey(k, out _, out string initial) && initial == g))
                {
                    gated++;
                    string status = set.LoadApproved(e, k, environment).Status;
                    if (status == VisualStatuses.BaselineMissing) missing++; else if (status == VisualStatuses.BaselineInvalid) invalid++; else approved++;
                }
                baselineCounts = (gated, approved, missing, invalid);
                // The same mapping validation the runner applies before it starts.
                if (run.Select(s => set.BindingProblem(s.Id, catalog)).FirstOrDefault(x => x is not null) is { } mapping && visual) Add(RunArea.Baselines, mapping);
                int mapped = run.Count(s => set.EntriesForExecution(s.Id).Any(e => e.ExecutionScenario is not null));
                int unmappedFixture = run.Count(s => !set.EntriesForExecution(s.Id).Any() && set.Manifest.Baselines.Any(e => set.UnmappedFixtureIdentity(e, s) is not null));
                if (unmappedFixture > 0 && visual) warnings.Add($"{unmappedFixture} selected external scenario{(unmappedFixture == 1 ? " is" : "s are")} bound to a fixture with baselines but not mapped (executionScenario); those baselines are refused, never borrowed.");
                storeState = (mapped > 0 ? $"{mapped} scenario{(mapped == 1 ? "" : "s")} use an explicit baseline identity mapping. " : "")
                    + (gated == 0 ? "No gated checkpoints for this selection: visual results will not pass."
                    : $"{gated} gated checkpoint{(gated == 1 ? "" : "s")}: {approved} approved, {missing} missing" + (invalid > 0 ? $", {invalid} invalid" : "") + $" ({environment})");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                storeState = "The store cannot be read.";
                if (visual) Add(RunArea.Baselines, "The baseline store cannot be read: " + ex.Message);
            }
        }
        // Preflight: the target deployment for exactly this combination (scenarios, test mode, driver, evidence).
        // A missing optional component blocks only the modes that need it; ValidateRun repeats this before the request.
        var deployment = DiagnoseRun(p, run);
        foreach (var f in deployment.Blocking) Add(RunArea.Target, $"Target not ready: {f.Message} {f.Action}".TrimEnd());
        string targetState = Path.GetFileName(p.Target) + " - " + (deployment.Ready
            ? deployment.Findings.Any(f => f.Code is "RUN002" or "RUN005") ? "Ready (runtime verified)" : "Ready (static checks)"
            : "Not ready: " + deployment.Blocking[0].Message);
        if (p.Timeout is < 100 or > 30000) Add(RunArea.Limits, "Step timeout must be 100..30000 ms.");
        if (p.ScenarioTimeout is < 5 or > 600) Add(RunArea.Limits, "Scenario timeout must be 5..600 s.");
        if (p.Allow.Any(a => !RunOptions.Capabilities.Contains(a)))
            Add(RunArea.General, "This runner allows only network, processes or devices capabilities; elevation is never allowed. Change them in Project Properties.");

        string exe = Path.GetFileName(p.Target);
        int executions = run.Length * p.ScreenSizes.Length;
        var summary = new List<string>(warnings)
        {
            $"Runs {run.Length} scenario{(run.Length == 1 ? "" : "s")} x {p.ScreenSizes.Length} screen size{(p.ScreenSizes.Length == 1 ? "" : "s")} = {executions} execution{(executions == 1 ? "" : "s")} of {exe} ({string.Join(", ", p.ScreenSizes)}).",
            "Target deployment: " + targetState + ".",
            native ? $"Native driver: the target runs on {WorkbenchCapabilities.TargetDriverName(p.Mode)}. Evidence: the driver's native PNG ({ScenarioRunner.BoundaryFor(p.Mode)}), logical cells for assertions."
                : run.Length > 0 && compiled == run.Length
                    ? "Compiled diagnostic scenarios run headless. Evidence: logical checkpoints only (cells, TVS1, logical PNG)."
                    : $"The target runs {(p.ProxyBackend == "NullDriver" ? "headless (NullDriver)" : "with " + WorkbenchCapabilities.TargetDriverName(p.ProxyBackend))}. Evidence: logical checkpoints"
                        + (p.ProxyCapture == ProxyCaptureKinds.None ? " only (cells, TVS1, logical PNG)." : $" plus native {WorkbenchCapabilities.EvidenceName(p.ProxyCapture)}."),
            visual ? $"Visual: compare {gated} gated checkpoint{(gated == 1 ? "" : "s")} with approved baselines; one without an approved baseline is reported as Missing (not a pass)."
                + (p.VisualBehavior == "candidates" ? " Candidates are kept for review; nothing is approved automatically." : "")
                : "No baseline comparison.",
            "These settings affect the tested target only, never the Workbench window.",
            $"Limits: step {p.Timeout ?? 10000} ms, scenario {p.ScenarioTimeout ?? 40} s; inspection {p.Inspect}.",
            "Results go to a new run folder in " + Relative(p.OutputRoot) + "."
        };
        int expected = run.Count(s => s.ExpectedResult != ScenarioOutcomes.Passed);
        if (expected > 0) summary.Add($"{expected} selected scenario{(expected == 1 ? " is" : "s are")} expected to fail.");
        if (rerun && pendingRerun is { } scope) summary.InsertRange(0, RerunSummary(scope, p));
        return new(problems.ToArray(), summary.ToArray(), sizeNote, store, storeState)
            { Scenarios = run.Length, Executions = executions, Target = targetState, Baselines = baselineCounts };
    }
    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    private static string NativeUnavailableReason() => !OperatingSystem.IsWindows() ? "native drivers run on Windows only."
        : "no audited diagnostic workflows (compiled, or external scenarios bound to an audited fixture).";

    // Final check before a run starts; the dialog already showed every problem inline.
    private void ValidateRun(WorkbenchProject p)
    {
        if (AssessRun(p).Problems.FirstOrDefault() is { } problem) throw new ArgumentException(problem.Text);
        if (ResolveRun(p).Length == 0) throw new ArgumentException("No tests selected for this scope.");
    }
    // Re-run (from Results) adds a fourth scope with the recorded setup applied to the draft; the user reviews it and
    // presses Run. A re-run never updates the session configuration or the project defaults.
    // Run Configuration opens in the presentation the user last chose (Basic by default). Both edit one draft and
    // are assessed by AssessRun; switching or visiting Project Properties reopens with the same draft and applies nothing.
    internal string RunView => WorkbenchPreferences.Load(preferences.StoragePath).EffectiveRunView;
    private void SetRunView(string view)
    {
        try { WorkbenchPreferences.Update(preferences.StoragePath, p => p with { RunView = view }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error("Could not remember the Run Configuration view.", ex.ToString()); }
    }
    private void ConfigureRun(RerunScope? rerun = null, RunDraft? resume = null)
    {
        pendingRerun = rerun;
        try
        {
            WorkbenchProject accepted; bool save;
            RunDraft? draft = resume;
            while (true)
            {
                var context = new RunDialogContext(FilterGroup ?? "All scenarios", ScopeCount, AvailableNativeModes(), NativeUnavailableReason(),
                    catalog.Any(s => s.Backend == "proxy-logical"), AssessRun, projectDefaults,
                    rerun is null ? null : $"{rerun.ScenarioId} at {rerun.Size} (from {rerun.RunName})")
                    { TargetDetails = p => ShowTargetDiagnostics(p.Target, () => DiagnoseRun(p, SafeRun(p))), Output = Relative(output.Data),
                      CanPrepare = p => TargetDiagnostics.Preparation.Offer(DiagnoseRun(p, SafeRun(p))).Kind is OfferKind.Prepare or OfferKind.Reprepare };
                var session = rerun?.Apply(SessionProject()) ?? SessionProject();
                bool basic = RunView == WorkbenchPreferences.BasicView;
                ushort result; RunDraft next;
                if (basic)
                {
                    using var dialog = new WorkbenchRunBasicDialog(desktop, session, context, draft);
                    result = desktop.ExecView(dialog); accepted = dialog.Accepted; save = dialog.SaveAsDefaults; next = dialog.Draft;
                }
                else
                {
                    using var dialog = new WorkbenchRunDialog(desktop, session, context, draft);
                    result = desktop.ExecView(dialog); accepted = dialog.Accepted; save = dialog.SaveAsDefaults; next = dialog.Draft;
                }
                if (result == WorkbenchRunDialog.SwitchCommand) { SetRunView(basic ? WorkbenchPreferences.AdvancedView : WorkbenchPreferences.BasicView); draft = next; continue; }
                if (result == WorkbenchRunBasicDialog.PropertiesCommand)
                {
                    ShowProperties(false);
                    // A changed executable reloads the catalog: the run is configured again once it has loaded.
                    if (busy || catalog.Length == 0) return;
                    draft = next with { Project = WithRunFields(SessionProject(), next.Project) };
                    continue;
                }
                // Prepare the blocked target, then return to this configuration with the draft intact. A prepared
                // copy the user chose to use changes the project's executable: Run Configuration reopens once its catalog
                // has loaded and revalidates everything. Nothing ever runs without an explicit Run.
                if (result == WorkbenchRunDialog.PrepareCommand)
                {
                    var outcome = PrepareTarget();
                    if (outcome.UseExecutable is { } prepared)
                    {
                        var keep = next;
                        UsePreparedTarget(prepared, () => ConfigureRun(rerun, keep with { Project = WithRunFields(SessionProject(), keep.Project) }));
                        return;
                    }
                    draft = next with { Project = WithRunFields(SessionProject(), next.Project) };
                    continue;
                }
                if (result != Views.cmOK) return;
                break;
            }
            var full = accepted with { Target = Path.GetFullPath(accepted.Target), OutputRoot = Path.GetFullPath(accepted.OutputRoot),
                BaselineStore = accepted.BaselineStore is null ? null : Path.GetFullPath(accepted.BaselineStore) };
            if (full.RunScope == RerunScope.Scope)
            {
                ValidateRun(full);
                var once = new WorkbenchRunRequest(full, ResolveRun(full), catalogBuild!);
                if (BuildIdentity(full.Target) != once.Build) throw new ArgumentException("The target changed. Refresh the scenarios with F2 before running.");
                RunBatch(once);
                return;
            }
            StartConfigured(full, save);
        }
        finally { pendingRerun = null; }
    }
    private void StartConfigured(WorkbenchProject p, bool save)
    {
        // Session configuration (): Run changes only the in-memory session unless the user ticked
        // "Save these settings as project defaults"; then the project becomes dirty only if the defaults differ.
        sessionRun = WithRunFields(sessionRun, p);
        if (save) projectDefaults = WithRunFields(projectDefaults, p);
        UpdateProjectTitle(); UpdateHeader(); ApplyFilter();
        ValidateRun(p);
        // The request is an immutable serialized snapshot; later session or project edits never reach the active run.
        var request = new WorkbenchRunRequest(p, ResolveRun(p), catalogBuild!);
        if (BuildIdentity(p.Target) != request.Build) throw new ArgumentException("The target changed. Refresh the scenarios with F2 before running.");
        RunBatch(request);
    }
    internal static (string Label, string Name)[] CaptureChoices(BatchResult batch, ScenarioResult result)
    {
        var choices = new List<(string Label, string Name)>();
        foreach (var checkpoint in result.Checkpoints)
        {
            // Preserve the actual evidence layer; proxy logical PNGs remain separate from native captures.
            string layer = batch.Driver == "NullDriver" ? "Logical PNG" : ScenarioRunner.BoundaryFor(batch.Driver);
            SafeArtifact(batch.RunRoot, checkpoint.Png);
            choices.Add(($"{layer}: {checkpoint.Checkpoint} ({checkpoint.Size})", checkpoint.Png));
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(SafeArtifact(batch.RunRoot, checkpoint.Metadata)));
            if (metadata.RootElement.TryGetProperty("proxyNative", out var native))
            {
                string name = native.GetProperty("artifact").GetString() ?? throw new InvalidDataException("Missing native artifact filename.");
                SafeArtifact(batch.RunRoot, name);
                choices.Add(($"Native {native.GetProperty("kind").GetString()}: {checkpoint.Checkpoint} ({checkpoint.Size})", name));
            }
        }
        return choices.ToArray();
    }
    internal static string SafeArtifact(string root,string name)
    {
        if(string.IsNullOrWhiteSpace(name)||Path.GetFileName(name)!=name||Path.IsPathRooted(name))throw new InvalidDataException("Artifact must be a flat run-relative filename.");
        string path=Path.Combine(root,name);if(!File.Exists(path))throw new FileNotFoundException("Artifact missing",path);return path;
    }
    // The baseline environment of a run of this configuration: this OS, the Proxy backend for ordinary
    // application scenarios (NullDriver for diagnostic ones), logical capture. Run Configuration counts only its baselines.
    internal static BaselineEnvironment BaselineEnvironmentFor(WorkbenchProject p, IEnumerable<ScenarioDescriptor>? run = null) =>
        BaselineEnvironment.Current(p.TestType != "rendering" && (run ?? []).Any(s => s.Backend == "proxy-logical") ? p.ProxyBackend : BaselineEnvironment.DefaultDriver);
    private BaselineEnvironment BaselineEnvironmentFor(WorkbenchProject p) => BaselineEnvironmentFor(p, catalog);
    private static void CaptureCandidates(BatchResult batch,WorkbenchRunRequest request)
    {
        // Already emits normalized candidates for non-matches. A match proves equality of
        // canonical cells and masks, so retain that validated canonical capture as a review copy.
        var project=request.Project;
        if(ScreenSnapshot.Hash(File.ReadAllBytes(project.BaselineStore!))!=request.BaselineManifestHash)
            throw new InvalidDataException("Baseline policy changed during capture. Make a fresh candidate run.");
        var set=VisualBaselineSet.Load(project.BaselineStore!);
        foreach(var r in batch.Results)
        foreach(var c in r.Checkpoints.Where(c=>c.Visual?.Status==VisualStatuses.Match))
        {
            var v=c.Visual!;string g=c.Size==r.Size?c.Size:c.Size+"-from-"+r.Size;
            using var diff=JsonDocument.Parse(File.ReadAllBytes(SafeArtifact(batch.RunRoot,v.Diff!)));
            var environment=BaselineEnvironment.FromComparison(diff.RootElement);
            var entry=set.FindForExecution(r.ScenarioId,c.Checkpoint,g)!;var approved=set.LoadApproved(entry,g,environment);
            if(approved.Cells is null||approved.CellsSha256!=diff.RootElement.GetProperty("expected").GetProperty("cellsSha256").GetString())throw new InvalidDataException("Baseline changed while retaining matched candidate.");
            var applied=diff.RootElement.GetProperty("normalizations").Deserialize<AppliedNormalization[]>(ScenarioRunner.Json)!;
            string Mask(IEnumerable<BaselineNormalization> values)=>JsonSerializer.Serialize(values,ScenarioRunner.Json);
            if(Mask(approved.File!.Normalizations)!=Mask(applied.Select(a=>new BaselineNormalization(a.Rule,a.Kind,a.Description,a.Spans))))
                throw new InvalidDataException("Baseline masks changed during capture. Make a fresh candidate run.");
            using var capture=JsonDocument.Parse(File.ReadAllBytes(SafeArtifact(batch.RunRoot,c.Metadata)));
            var identity=capture.RootElement.GetProperty("target");
            var f=approved.File! with {Status="candidate",ReplacesSha256=approved.CellsSha256,Platform=environment.Platform,ExecutionScenarioId=entry.ExecutionScenario,
                Driver=environment.Driver==BaselineEnvironment.DefaultDriver?null:environment.Driver,
                Target=identity.GetProperty("assembly").GetString()+" "+identity.GetProperty("version").GetString(),
                Review="Unreviewed candidate from this execution's canonical Match; explicit owner approval required."};
            File.WriteAllBytes(Path.Combine(batch.RunRoot,v.Baseline+".candidate.tvs1"),approved.Cells.Serialize());
            File.WriteAllBytes(Path.Combine(batch.RunRoot,v.Baseline+".candidate.png"),LogicalPng.Encode(approved.Cells));
            File.WriteAllText(Path.Combine(batch.RunRoot,v.Baseline+".candidate.json"),JsonSerializer.Serialize(f,ScenarioRunner.Json));
        }
    }
}
