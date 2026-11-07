using TSharpVision.Constants;
using TSharpVision;
using TSharpWorkbench.Testing;

// Target diagnostics in the Workspace. The static inspection and its evaluation live in TargetDiagnostics; this
// part supplies what only the Workspace knows (the catalog it discovered, the attached documents, the session run
// configuration and what happened at runtime in this session) and opens the Target Diagnostics dialog.
internal sealed partial class WorkbenchDialog
{
    internal const ushort DiagnoseTarget = 0x7dca;
    // Runtime facts of this session per deployment (Verify target, the last run's launches); dropped when the deployment changes.
    private readonly Dictionary<string, RuntimeFacts> runtimeFacts = new(StringComparer.OrdinalIgnoreCase);
    private string? discoveryFailure, discoveryTarget;

    private RuntimeFacts? RuntimeFor(TargetInspection i) => runtimeFacts.TryGetValue(i.Target, out var facts) && facts.Stamp == i.Stamp ? facts : null;
    private void RecordRuntime(TargetInspection i, Func<RuntimeFacts, RuntimeFacts> change)
    {
        if (!i.Set) return;
        runtimeFacts[i.Target] = change(RuntimeFor(i) ?? new RuntimeFacts(i.Stamp));
    }

    // What discovery and the attached documents know about this executable (null for another executable, e.g. one being
    // edited in Project Properties).
    private CatalogFacts? CatalogFor(string path)
    {
        if (!WorkbenchPreferences.SamePath(path, target.Data)) return null;
        var compiled = CompiledForTarget;
        string? failure = discoveryTarget is not null && WorkbenchPreferences.SamePath(discoveryTarget, path) ? discoveryFailure : null;
        int native = OperatingSystem.IsWindows() ? catalog.Count(s => WorkbenchCapabilities.NativeModes.Any(m => NativeExecution.Supports(s, m))) : 0;
        return new(compiled is not null, compiled?.Count(s => s.External is null && !s.IsFixtureOnly) ?? 0,
            compiled is null ? 0 : ScenarioFiles.BindableFixtures(compiled, projectDefaults.LegacyFixtures).Length, native, failure, attached);
    }

    // The project's own context: Proxy when it uses Black-Box Mode documents (or the executable is an ordinary application),
    // diagnostic host when it uses fixture-bound documents or the target can host compiled scenarios; the session's drivers.
    internal DiagnosticContext ProjectContext(string? exe = null)
    {
        var inspection = TargetDiagnostics.Inspect(exe ?? target.Data);
        var documents = projectDefaults.ScenarioFiles.Select(f => attached.FirstOrDefault(a => WorkbenchPreferences.SamePath(a.Path, f))?.Info ?? ScenarioFiles.Inspect(f))
            .Where(i => i.Document is not null).ToArray();
        bool proxyDocuments = documents.Any(d => d.Proxy), fixtureDocuments = documents.Any(d => !d.Proxy);
        bool proxy = proxyDocuments || !fixtureDocuments && inspection.HostsDiagnostics == false;
        bool diagnostic = fixtureDocuments || !proxyDocuments && inspection.HostsDiagnostics != false;
        var p = sessionRun;
        bool native = p.TestType == "rendering" && diagnostic;
        return new(proxy, diagnostic, proxy ? p.ProxyBackend : null, proxy && !native ? p.ProxyCapture : null, native ? p.Mode : null);
    }
    internal TargetReport DiagnoseProject(string? exe = null)
    {
        var inspection = TargetDiagnostics.Inspect(exe ?? target.Data);
        return TargetDiagnostics.Evaluate(inspection, ProjectContext(exe), CatalogFor(inspection.Target), RuntimeFor(inspection));
    }
    // The selected combination of a run (Run Configuration preflight and the final revalidation before the request).
    internal TargetReport DiagnoseRun(WorkbenchProject p, IEnumerable<ScenarioDescriptor> run)
    {
        var inspection = TargetDiagnostics.Inspect(p.Target);
        return TargetDiagnostics.Evaluate(inspection, TargetDiagnostics.ForRun(run, p), CatalogFor(inspection.Target), RuntimeFor(inspection));
    }
    private ScenarioDescriptor[] SafeRun(WorkbenchProject p)
    {
        try { return ResolveRun(p); } catch (ArgumentException) { return []; }
    }

    internal void RefreshHeader() => UpdateHeader();
    // Readiness for the Workspace header: "Ready" or the first blocking problem, with the route to the details.
    private string TargetReadiness() => TargetState().Text;
    // The readiness text and, when the target is not ready, its first blocking problem exactly as the diagnostics name it.
    private (string Text, string? Problem) TargetState()
    {
        if (target.Data.Length == 0 || !File.Exists(target.Data)) return ("", null);
        var report = DiagnoseProject();
        // A remediable deployment problem points at Prepare target; a prepared copy shows its state.
        var (offer, _) = TargetDiagnostics.Preparation.Offer(report);
        string prepared = report.Findings.FirstOrDefault(f => f.Component == "Preparation") is { } p ? p.Short + " | " : "";
        return report.Ready ? (prepared + (report.Findings.Any(f => f.Code is "RUN002" or "RUN005") ? "Ready (verified)" : "Ready"), null)
            : (report.Readiness + (offer is OfferKind.Prepare or OfferKind.Reprepare ? " | Prepare target..." : " - Settings > Target Diagnostics..."), report.Readiness);
    }

    // Target Diagnostics for an executable (the project's, or the one being edited in Project Properties).
    internal void ShowTargetDiagnostics(string? exe = null, Func<TargetReport>? report = null, bool startVerify = false)
    {
        string path = (exe ?? target.Data).Trim();
        var (verify, record, text) = Verification(path);
        // TSW PRODUCT-3: Fix Issues is offered beside Verify target while the diagnostics report something it addresses.
        using var dialog = new WorkbenchTargetDiagnosticsDialog(desktop, report ?? (() => DiagnoseProject(path)), verify, record, text, () => ShowFixIssues(path));
        if (startVerify && verify is not null) dialog.Post(WorkbenchTargetDiagnosticsDialog.VerifyCommand);
        desktop.ExecView(dialog);
        UpdateHeader(); RecheckScenarioFiles();
    }

    // Verify target for an executable: the existing, owned session lifecycles (the Proxy handshake for an ordinary
    // application, the diagnostic host's catalog handshake otherwise), always headless; the outcome is recorded as a
    // runtime fact of the deployment as it is when the verification ends.
    private (Func<CancellationToken, Task<TargetVerification>>? Verify, Action<TargetVerification?, StartupFailure?> Record, string Text) Verification(string path)
    {
        var inspection = TargetDiagnostics.Inspect(path);
        var context = ProjectContext(path);
        bool proxy = context.Proxy || inspection.HostsDiagnostics == false;
        string name = Path.GetFileName(inspection.Target);
        string? unavailable = !inspection.Set ? "no target executable is set." : !inspection.Exists ? $"{name} does not exist."
            : busy && activeRequest is not null ? "a test run is active; verify when it has finished." : null;
        Func<CancellationToken, Task<TargetVerification>>? verify = unavailable is not null ? null
            : token => runner.VerifyAsync(inspection.Target, proxy, projectDefaults.TargetArguments, projectDefaults.TargetEnvironment, token);
        string text = unavailable ?? $"Verify target starts {name} once, headless (NullDriver), "
            + (proxy ? "with the Workbench Proxy, " : "through its diagnostic host, ")
            + $"completes the connection handshake and closes it again (at most {ScenarioRunner.VerifySeconds + 5} s). No scenario runs, nothing is captured, "
            + "and the project is not changed. The application's own startup code runs, as in a test run, with the project's arguments and environment "
            + "and a temporary profile. Other display drivers are not activated.";
        return (verify, (ok, failure) =>
        {
            RecordRuntime(TargetDiagnostics.Inspect(path), f => ok is not null ? f with { Verified = ok, VerifyFailure = null, LastStartupFailure = null } : f with { Verified = null, VerifyFailure = failure });
            if (ok is not null) Status($"Verify target: {name} started and connected ({(ok.Proxy ? "Proxy" : "diagnostic host")}); no scenario ran.");
            else Error($"Verify target: {name} did not start or connect: {failure?.Reason}", failure?.Technical);
        }, text);
    }

    // TSW PRODUCT-3: Fix Issues for an executable (the project's, or the one edited in Project Properties): the plan of the
    // diagnosed problems, an explicit confirmation, the in-place copy of absent Workbench support files, the re-check and
    // optionally Verify target. Never while a test run is active (the dialog refuses then).
    internal const ushort FixIssuesCommand = 0x7d30;
    // Every Fix entry point (Project > Fix issues, Properties, Target Diagnostics) opens the "Fix the original
    // target" mode; its chooser hands over to "Prepare a copy" (PrepareTarget), which can hand back. One engine underneath.
    internal void ShowFixIssues(string? exe = null)
    {
        string path = (exe ?? target.Data).Trim();
        if (path.Length == 0) { Error("No target executable is set. Set it in Project Properties (Alt+O)."); return; }
        if (FixOriginal(path)) return;
        // Prepare a copy chosen: from Project Properties the accepted copy goes into the edited executable field (OK applies
        // it); from the Workspace the project uses it at once, as Settings > Prepare Target Copy... does.
        var properties = desktop.TopView() as WorkbenchPropertiesDialog;
        var outcome = PrepareTarget(path, WorkbenchPreferences.SamePath(path, target.Data));
        if (outcome.UseExecutable is not { } use) return;
        if (properties is not null) { properties.ExecutableField.SetData(use); var ev = new TEvent { What = Events.evNothing }; properties.HandleEvent(ref ev); }
        else UsePreparedTarget(use);
    }

    // The Fix mode window; true when it closed normally, false when the user chose "Prepare a copy" instead.
    private bool FixOriginal(string path)
    {
        var (verify, record, text) = Verification(path);
        var service = new TargetRepairService(TargetDiagnostics.Preparation);
        RepairOutcome? outcome; ushort answer;
        using (var dialog = new WorkbenchFixIssuesDialog(desktop, service, path, () => DiagnoseProject(path), ProjectContext(path), verify, record,
            () => busy && activeRequest is not null, text))
        {
            answer = desktop.ExecView(dialog);
            outcome = dialog.Outcome;
        }
        TargetDiagnostics.Forget(path); TargetPreparationService.ForgetStatus();
        if (outcome is { Success: true }) Status("Fix issues: " + outcome.Message);
        else if (outcome is { Cancelled: false, Success: false } failed && failed.Changes.Length + failed.Details.Length > 0) Error("Fix issues: " + failed.Message, string.Join("\n", failed.Details));
        UpdateHeader(); RecheckScenarioFiles();
        return answer != PreparationModes.SwitchCommand;
    }

    // The last run's launches are runtime facts of the deployment: a startup failure, or a target that connected.
    private void RecordRunOutcome(string path, BatchResult batch)
    {
        var inspection = TargetDiagnostics.Inspect(path);
        var startup = batch.Results.Select(StartupFailure.Of).FirstOrDefault(f => f is not null);
        bool connected = batch.Results.Any(r => r.Checkpoints.Length > 0 || r.Outcome == ScenarioOutcomes.Passed);
        if (startup is null && !connected) return;
        RecordRuntime(inspection, f => f with { LastStartupFailure = startup, LastRunConnected = connected });
    }
}
