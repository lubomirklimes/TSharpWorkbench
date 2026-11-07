using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSharpWorkbench.Testing;

/// <summary>Options of one batch run. Null <see cref="Sizes"/> runs every scenario at its own default grids.</summary>
/// <param name="Inspect"><c>declared</c> (default: evidence the checkpoints request), <c>all</c> or <c>none</c>.</param>
/// <param name="Allow">Capabilities scenarios may declare (<c>network</c>, <c>processes</c>, <c>devices</c>); <c>elevation</c> is never allowed.</param>
/// <param name="OperationMilliseconds">Target bound for one operation (100..30000); null keeps the target default (10 s).</param>
/// <param name="ScenarioSeconds">Bound for one scenario execution (5..600); null keeps the default (40 s target, +5 s controller).</param>
/// <param name="Baselines">Path of a visual baseline manifest (<c>visual-baselines.json</c>). When set, every captured checkpoint
/// the manifest gates is compared with its approved baseline (see <see cref="VisualBaselineSet"/>); null = no visual comparison.</param>
public sealed record RunOptions(string Output, string[]? Sizes = null, string Inspect = "declared", string[]? Allow = null,
    int? OperationMilliseconds = null, int? ScenarioSeconds = null, string? Baselines = null)
{
    internal string Driver { get; init; } = "NullDriver";
    public string ProxyBackend { get; init; } = "NullDriver";
    public string ProxyCapture { get; init; } = ProxyCaptureKinds.None;
    public string? ProxyShutdownKey { get; init; }
    public string[] TargetArguments { get; init; } = [];
    public Dictionary<string, string> TargetEnvironment { get; init; } = new();
    public static readonly string[] InspectModes = ["declared", "all", "none"];
    public static readonly string[] Capabilities = ["network", "processes", "devices"];
}

/// <summary>Result of <see cref="ScenarioRunner.VerifyAsync"/>: the identity and capabilities the target reported at its
/// handshake, and how the owned process ended. Proof of activation on the headless backend only.</summary>
public sealed record TargetVerification(bool Proxy, TargetIdentity Identity, string Mode, string[] CaptureKinds, int? Scenarios,
    string StopOutcome, int? ExitCode, bool Cleanup, TimeSpan Duration);

/// <summary>A verification that did not complete; the process was stopped and its temporary root cleaned (see <see cref="Cleanup"/>).</summary>
public sealed class TargetVerificationException(string message, Exception inner, string stopOutcome, bool cleanup) : IOException(message, inner)
{
    public string StopOutcome { get; } = stopOutcome;
    public bool Cleanup { get; } = cleanup;
}

// The only execution core: CLI and TUI both call it. No Workbench views or application types are referenced here.
public sealed class ScenarioRunner
{
    /// <summary>Serializer settings of every artifact the runner writes (camelCase, indented, nulls omitted).</summary>
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Local diagnostic files only; keep '+' and '>' readable in versions and routing strings.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Runnable discovery: the target's catalog without bare fixture-only registrations
    /// (<see cref="ScenarioSelection.Runnable"/>). Identical to the raw catalog for targets without such registrations.</summary>
    public async Task<ScenarioDescriptor[]> DiscoverAsync(string target, CancellationToken token = default) =>
        ScenarioSelection.Runnable(await DiscoverRawAsync(target, token));

    /// <summary>Runnable discovery with external scenarios bound against the raw catalog
    /// (<see cref="DiscoverRawAsync(string, IEnumerable{string}, CancellationToken)"/>), minus bare fixture-only registrations.</summary>
    public async Task<ScenarioDescriptor[]> DiscoverAsync(string target, IEnumerable<string> files, CancellationToken token = default) =>
        ScenarioSelection.Runnable(await DiscoverRawAsync(target, files, token));

    /// <summary>Runnable discovery with an explicit legacy-fixture allowance (<see cref="DiscoverRawAsync(string, IEnumerable{string}, IReadOnlyCollection{string}?, CancellationToken)"/>).</summary>
    public async Task<ScenarioDescriptor[]> DiscoverAsync(string target, IEnumerable<string> files, IReadOnlyCollection<string>? legacyFixtures, CancellationToken token = default) =>
        ScenarioSelection.Runnable(await DiscoverRawAsync(target, files, legacyFixtures, token));

    /// <summary>Raw fixture advertisement: every registration the target advertises, fixture-only ones included. For binding,
    /// validation and fingerprints; not a source of directly executable IDs.</summary>
    public async Task<ScenarioDescriptor[]> DiscoverRawAsync(string target, CancellationToken token = default)
    {
        await using var session = new TargetSession(target, null, "80x25", token);
        Reply hello = await session.ConnectAsync();
        var catalog = hello.Scenarios ?? throw new IOException("Target did not advertise scenarios.");
        ValidateCatalog(catalog);
        await session.SendAsync(Protocol.Operations.Shutdown, Protocol.States.ShutdownAccepted);
        await session.WaitForExitAsync();
        return catalog;
    }

    /// <summary>Lifetime bound (seconds) of one <see cref="VerifyAsync"/> session; the controller deadline adds 5 s.</summary>
    public const int VerifySeconds = 20;

    /// <summary>Explicit target verification: starts the owned target once on the headless backend, completes the
    /// connection handshake (Proxy session, or the diagnostic host's catalog) and shuts it down again. No scenario is started,
    /// nothing is captured and no project file is touched. The session is bounded by <see cref="VerifySeconds"/>, honours
    /// <paramref name="token"/>, and always stops and cleans up the process and its temporary root before returning.</summary>
    /// <exception cref="TargetVerificationException">The target did not start, connect or hand over; the message carries the
    /// controller's reason and the bounded start of the target's stderr.</exception>
    public async Task<TargetVerification> VerifyAsync(string target, bool proxy, string[]? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew();
        var session = new TargetSession(target, null, "80x25", token, null, VerifySeconds, "NullDriver", proxy, arguments, environment);
        Reply? hello = null; Exception? failure = null; string? stderr = null;
        try
        {
            hello = await session.ConnectAsync();
            if (proxy && hello.Capabilities?.Mode != "proxy-logical") throw new IOException("Proxy capabilities missing: the target did not start a Workbench Proxy session.");
            if (!proxy) ValidateCatalog(hello.Scenarios ?? throw new IOException("Target did not advertise scenarios."));
            await session.SendAsync(Protocol.Operations.Shutdown, Protocol.States.ShutdownAccepted);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            failure = ex;
            await session.CollectDiagnosticsAsync();
            stderr = session.Diagnostics;
        }
        finally
        {
            try { await session.StopAsync(); } catch (Exception ex) when (failure is null) { failure = ex; }
            await session.DisposeAsync();
        }
        if (failure is not null)
            throw new TargetVerificationException(failure.Message + (stderr is { Length: > 0 } && !failure.Message.Contains(stderr, StringComparison.Ordinal) ? " Target stderr: " + stderr : ""),
                failure, session.StopOutcome, session.Cleanup);
        return new(proxy, hello!.Target ?? throw new IOException("Target identity missing."), hello.Capabilities?.Mode ?? (proxy ? "proxy-logical" : "diagnostic"),
            hello.Capabilities?.CaptureKinds ?? [], proxy ? null : hello.Scenarios?.Length, session.StopOutcome, session.ExitCode, session.Cleanup, clock.Elapsed);
    }

    /// <summary>Parse files before discovery, then resolve fixture capabilities against the advertised raw catalog. The result is
    /// raw: it keeps fixture-only registrations beside the external scenarios bound to them.</summary>
    public Task<ScenarioDescriptor[]> DiscoverRawAsync(string target, IEnumerable<string> files, CancellationToken token = default) =>
        DiscoverRawAsync(target, files, null, token);

    /// <summary>As <see cref="DiscoverRawAsync(string, IEnumerable{string}, CancellationToken)"/>, allowing the explicitly named
    /// ordinary compiled scenarios as legacy fixtures (<see cref="ExternalScenarios.Merge(ScenarioDescriptor[], IEnumerable{ExternalScenarioFile}, IReadOnlyCollection{string}?)"/>).</summary>
    public async Task<ScenarioDescriptor[]> DiscoverRawAsync(string target, IEnumerable<string> files, IReadOnlyCollection<string>? legacyFixtures, CancellationToken token = default)
    {
        var documents = files.Select(ExternalScenarios.Load).ToArray();
        if (documents.Any(d => d.Scenario.Backend == "proxy-logical"))
        {
            if (documents.Any(d => d.Scenario.Backend != "proxy-logical")) throw new ArgumentException("Proxy and compiled scenarios require separate projects.");
            if (!File.Exists(target)) throw new ArgumentException("Target executable does not exist.");
            return ExternalScenarios.Merge([], documents);
        }
        return ExternalScenarios.Merge(await DiscoverRawAsync(target, token), documents, legacyFixtures);
    }

    private static readonly Regex MetadataKey = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    internal static void ValidateCatalog(ScenarioDescriptor[] catalog)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (catalog.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 128
            || s.Id.Any(char.IsControl) || !ids.Add(s.Id) || string.IsNullOrWhiteSpace(s.Name)
            || string.IsNullOrWhiteSpace(s.Group) || s.Description is null || !ValidGrid(s.Grid)
            || s.Backend is not ("diagnostic-logical" or "proxy-logical")
            || !ScenarioKinds.All.Contains(s.Kind) || s.ExpectedResult is not (ScenarioOutcomes.Passed or ScenarioOutcomes.Failed)
            || (s.Metadata is not null && (s.Metadata.Count > 32 || s.Metadata.Any(kv => !MetadataKey.IsMatch(kv.Key) || kv.Key.Length > 64 || kv.Value is null || kv.Value.Length > 512)))
            || (s.Conditions?.Length > 1024) || (s.Safety?.Notes?.Length > 1024) || !(s.IsFixtureOnly ? ValidFixtureOnly(s) : ValidSteps(s.Steps, s.Grid))))
            throw new IOException("Invalid target catalog.");
    }

    /// <summary>A fixture-only registration: the explicit role and current contract version, a reviewed revision, no steps of
    /// its own, at least one advertised checkpoint expectation (unique valid names, ordinal order), advertised adapters and
    /// the diagnostic backend. Ordinary scenarios never take this branch: their validation is unchanged.</summary>
    private static bool ValidFixtureOnly(ScenarioDescriptor s) => s.Contract is { } c
        && c.Role == FixtureContract.FixtureOnlyRole && c.ContractVersion == FixtureContract.CurrentVersion
        && !string.IsNullOrWhiteSpace(c.Revision) && c.Revision.Length <= 128 && !c.Revision.Any(char.IsControl)
        && c.Checkpoints is { Length: > 0 and <= 256 } && c.Checkpoints.All(Protocol.IsValidName)
        && c.Checkpoints.SequenceEqual(c.Checkpoints.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        && s.Steps is { Length: 0 } && s.Adapters is { Actions: not null, Points: not null } && s.External is null
        && s.Backend == "diagnostic-logical" && s.ExpectedResult == ScenarioOutcomes.Passed;

    private static bool ValidGrid(ScenarioGrid? grid) => grid is not null && grid.MinWidth >= GridLimits.MinWidth && grid.MinHeight >= GridLimits.MinHeight
        && grid.MaxWidth <= GridLimits.MaxWidth && grid.MaxHeight <= GridLimits.MaxHeight && grid.MinWidth <= grid.MaxWidth && grid.MinHeight <= grid.MaxHeight
        && grid.Defaults is { Length: > 0 and <= 16 } && grid.Defaults.All(grid.Supports);

    private static bool ValidSteps(ScenarioStep[]? steps, ScenarioGrid grid)
    {
        if (steps is null || steps.Length is 0 or > 256 || steps.Any(step => step is null)) return false;
        var checkpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            bool valid = step.Kind switch
            {
                ScenarioStep.Key => TestKeys.TryResolve(step.Name, out _),
                ScenarioStep.Action => Protocol.IsValidName(step.Name),
                ScenarioStep.Resize => grid.Supports(step.Name),
                ScenarioStep.KeyDown or ScenarioStep.KeyUp or ScenarioStep.Text or ScenarioStep.Modifier or ScenarioStep.Mouse or ScenarioStep.Release
                    => InputSteps.IsValid(step.Kind, step.Name),
                ScenarioStep.Checkpoint => Protocol.IsValidName(step.Name) && checkpoints.Add(step.Name)
                    && (step.Evidence ?? []).All(Protocol.EvidenceKinds.All.Contains)
                    && (step.Assertions ?? []).Length <= 64
                    && (step.Assertions ?? []).All(a => a is not null && Protocol.IsValidName(a.Id) && AssertionKinds.All.Contains(a.Kind)
                        && a.Severity is AssertionSeverity.Error or AssertionSeverity.Warning)
                    && (step.Assertions ?? []).Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() == (step.Assertions ?? []).Length,
                _ => false
            };
            if (step.Retry is { } retry && (step.Kind != ScenarioStep.Checkpoint || retry.Attempts is < 1 or > 20 || retry.IntervalMilliseconds is < 0 or > 30000)) return false;
            if (!valid || (step.Kind != ScenarioStep.Checkpoint && (step.Evidence is not null || step.Assertions is not null))) return false;
        }
        return checkpoints.Count > 0;
    }

    /// <summary>Compatibility overload: explicit grids, default options.</summary>
    public Task<BatchResult> RunAsync(string target, ScenarioDescriptor[] selected, string[] sizes, string output,
        Action<string>? progress = null, CancellationToken token = default) =>
        RunAsync(target, selected, new RunOptions(output, sizes), progress, token);

    public async Task<BatchResult> RunAsync(string target, ScenarioDescriptor[] selected, RunOptions options,
        Action<string>? progress = null, CancellationToken token = default)
    {
        // Supported execution boundary: a bare fixture-only registration is never a run target, whichever caller selected it.
        if (selected.FirstOrDefault(s => s?.IsFixtureOnly == true) is { } fixtureOnly) throw new ArgumentException(ScenarioSelection.NotRunnable(fixtureOnly.Id));
        ValidateCatalog(selected);
        if (options.ProxyBackend is not ("NullDriver" or "SDLDriver" or "SDLGpuDriver" or "Win32ConsoleDriver")) throw new ArgumentException("Unsupported proxy backend; no fallback.");
        if (selected.Any(s => s.Backend == "proxy-logical") && options.Driver != "NullDriver") throw new ArgumentException("Proxy logical sessions cannot use native capture providers: existing providers create and own another backend; shared-owner UI/render-thread capture is unavailable.");
        if (!ProxyCaptureKinds.All.Contains(options.ProxyCapture)) throw new ArgumentException("Unsupported proxy capture kind; no fallback.");
        if (options.ProxyCapture != ProxyCaptureKinds.None && (options.Driver != "NullDriver" || selected.Any(s => s.Backend != "proxy-logical")
            || !ProxyCaptureKinds.ForBackend(options.ProxyBackend).Contains(options.ProxyCapture)))
            throw new ArgumentException("Requested proxy native capture is unsupported for this backend/scenario; no fallback.");
        if (selected.Length == 0) throw new ArgumentException("No scenarios selected.");
        if (options.Driver != "NullDriver" && !NativeExecution.IsMode(options.Driver)) throw new ArgumentException("Unsupported native driver; no fallback performed.");
        if (options.Driver != "NullDriver" && options.Baselines is not null) throw new ArgumentException("Logical visual baselines cannot gate native PNGs.");
        if (options.Sizes is { } explicitSizes && (explicitSizes.Length == 0 || explicitSizes.Any(s => !ScenarioSelection.TryParseSize(s, out _, out _))))
            throw new ArgumentException("Unsupported size.");
        if (!RunOptions.InspectModes.Contains(options.Inspect)) throw new ArgumentException("Inspect mode must be declared, all or none.");
        if ((options.Allow ?? []).Any(a => !RunOptions.Capabilities.Contains(a))) throw new ArgumentException("Allowed capabilities: " + string.Join(", ", RunOptions.Capabilities) + ".");
        if (options.OperationMilliseconds is < 100 or > 30000) throw new ArgumentException("Operation timeout must be 100..30000 ms.");
        if (options.ScenarioSeconds is < 5 or > 600) throw new ArgumentException("Scenario timeout must be 5..600 s.");
        if (selected.Select(s => s.Id).Distinct().Count() != selected.Length) throw new ArgumentException("Duplicate scenario ID.");
        foreach (var s in selected)
            foreach (var grid in options.Sizes ?? s.Grid.Defaults) ExternalScenarios.ValidateExecution(s, grid);
        ArtifactNames.EnsureDistinct(selected);
        // An invalid manifest is refused before anything runs (normalization configuration error).
        VisualBaselineSet? baselines = options.Baselines is null ? null : VisualBaselineSet.Load(options.Baselines);
        // An explicit baseline identity mapping is used only with the validated fixture binding it names.
        if (baselines is not null && selected.Select(s => baselines.BindingProblem(s.Id, selected)).FirstOrDefault(p => p is not null) is { } mapping)
            throw new VisualBaselineConfigurationException(mapping + " Nothing was run.");
        string output = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(output);
        object sweep = selected.Any(s => s.Backend == "proxy-logical")
            ? new { policy = "Proxy runs clean only their own session roots; no global orphan sweep." } : TempRoots.SweepOrphans();
        // One flat, self-contained, disposable directory per run. Nothing outside it is read or written.
        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..12];
        string runRoot = Path.Combine(output, "run-" + runId);
        Directory.CreateDirectory(runRoot);
        DateTimeOffset began = DateTimeOffset.UtcNow;
        var results = new List<ScenarioResult>();
        var nativeLifecycle = new List<object>();
        TargetIdentity? identity = null;
        // The run's baseline environment - this OS, the driver the logical cells come from (the Proxy backend
        // of ordinary applications, NullDriver for diagnostic runs) and the logical capture. Only that environment is compared.
        var gate = baselines is null ? null : new VisualGate(baselines, runRoot,
            BaselineEnvironment.Current(selected.Any(s => s.Backend == "proxy-logical") ? options.ProxyBackend : options.Driver));
        foreach (var scenario in selected)
        foreach (string size in (options.Sizes ?? scenario.Grid.Defaults).Distinct(StringComparer.Ordinal))
        {
            progress?.Invoke($"Running {scenario.Id} / {size}");
            var (result, seen, lifecycle) = await RunOneAsync(target, scenario, size, runRoot, options, gate, progress, token);
            identity ??= seen; results.Add(result);
            if (lifecycle is not null) nativeLifecycle.Add(lifecycle);
            progress?.Invoke($"{result.Outcome}: {scenario.Id} / {size}" + (result.Outcome == ScenarioOutcomes.Passed ? "" : " - " + result.Reason));
        }
        var visual = gate?.Finish(results, selected);
        var batch = new BatchResult(runRoot, results.ToArray()) { Visual = visual, Driver = options.Driver };
        if (visual is not null) await File.WriteAllTextAsync(Path.Combine(runRoot, "visual.json"), JsonSerializer.Serialize(VisualReport.Json(visual), Json));
        var run = new
        {
            schema = Protocol.Schema, protocolVersion = Protocol.Version, runId,
            layers = options.ProxyCapture != ProxyCaptureKinds.None ? ProxyNativeLayers : identity?.Console is not null ? ConsoleProxyLayers : LayersFor(options.Driver), captureLayer = BoundaryFor(options.Driver), requestedDriver = options.Driver,
            renderer = RendererFor(NativeExecution.ActualDriver(options.Driver)),
            artifactPathBase = "run-root", review = "Unreviewed",
            target = Path.GetFileName(target), targetIdentity = identity,
            controller = new { testingVersion = TestTargetHost.Identity("controller", 0, 0).TestingVersion, environment = Provenance.Environment() },
            options = new { sizes = options.Sizes ?? ["scenario defaults"], options.Inspect, allow = options.Allow ?? [], options.OperationMilliseconds, options.ScenarioSeconds, options.ProxyBackend, options.ProxyCapture },
            unavailable = options.ProxyCapture != ProxyCaptureKinds.None ? new[] { "swapchain/compositor/physical display capture", "physical input hardware", "native approved baselines" } : options.Driver != "NullDriver" ? new[] { "resize and unaudited mouse scenarios", "monitor/compositor presentation", "physical input hardware", "native approved baselines" } : new[] { "Layer B driver output", "Layer C native pixels", "cursor position",
                "native input devices (keyboard and mouse are simulated logical TEvents; driver input parity is a later qualification)" },
            orphanSweep = sweep, began, ended = DateTimeOffset.UtcNow,
            scenarios = selected.Select(s => s with { Steps = s.Steps.Select(x => x.Kind == InputSteps.Text ? x with { Name = "[redacted]" } : x).ToArray() }), results, nativeLifecycle,
            passed = results.Count(r => r.Outcome == ScenarioOutcomes.Passed), failed = results.Count(r => r.Outcome is not (ScenarioOutcomes.Passed or ScenarioOutcomes.Skipped)),
            skipped = results.Count(r => r.Outcome == ScenarioOutcomes.Skipped),
            checkpoints = results.Sum(r => r.Checkpoints.Length),
            assertions = new
            {
                pass = results.Sum(r => r.Checkpoints.Sum(c => c.Assertions.Count(a => a.Outcome == AssertionOutcomes.Pass))),
                fail = results.Sum(r => r.Checkpoints.Sum(c => c.Assertions.Count(a => a.Outcome == AssertionOutcomes.Fail))),
                unsupported = results.Sum(r => r.Checkpoints.Sum(c => c.Assertions.Count(a => a.Outcome == AssertionOutcomes.Unsupported))),
            },
            visual = visual is null ? null : VisualReport.Json(visual),
            exitCode = batch.ExitCode,
        };
        await File.WriteAllTextAsync(Path.Combine(runRoot, "run.json"), JsonSerializer.Serialize(run, Json));
        await File.WriteAllTextAsync(Path.Combine(runRoot, "report.md"), RunReport.Markdown(batch, selected, identity, began, options, sweep));
        return batch;
    }

    private static readonly object ProxyNativeLayers = new
    {
        logical = "logical TVS1/PNG retained separately",
        driverOutput = "native evidence in proxyNative; full driver tracing not collected",
        nativePixels = "separate renderer/GPU artifact when requested; native-console-cells are text/attributes, not pixels",
        presentation = "swapchain/compositor/physical display not measured"
    };
    private static readonly object ConsoleProxyLayers = new
    {
        logical = "captured: TScreen.ScreenBuffer cells (Layer A)",
        driverOutput = "native console cell readback equality reported separately; no raw output dump",
        nativePixels = "not captured; PNG is logical"
    };
    internal static readonly object Layers = new
    {
        logical = "captured: TScreen.ScreenBuffer cells (Layer A)",
        driverOutput = "not captured (Layer B)",
        nativePixels = "not captured (Layer C)",
    };
    internal static readonly object NativeLayers = new
    {
        logical = "TVS1 application cells retained for semantic assertions only",
        driverOutput = "production SDLDriver rendering and input translation",
        nativePixels = "actual Commander SDL_Renderer readback (renderer-target); not compositor presentation",
    };

    internal static string BoundaryFor(string driver) => driver switch { "NullDriver" => "logical", "Win32ConsoleDriver" => "host-window", _ => "renderer-target" };
    internal static string RendererFor(string driver) => driver switch { "NullDriver" => LogicalPng.Renderer, "Win32ConsoleDriver" => "Owned classic conhost; PrintWindow client-area capture", "SDLGpuDriver" => "Actual Commander SDL GPU; readable GPU texture rerender of submitted application snapshot + fence", _ => "Actual Commander SDL_Renderer software" };
    internal static object LayersFor(string driver) => driver == "NullDriver" ? Layers : driver == "Win32ConsoleDriver" ? ConsoleLayers : NativeExecution.IsGpu(driver) ? GpuLayers : NativeLayers;
    private static readonly object GpuLayers = new { logical = "TVS1 application cells for semantics only", driverOutput = "production SDLGpuDriver and native SDL translation", nativePixels = "render-worker readable GPU texture rerender of actual submitted application snapshot; GPU download + fence; not swapchain/compositor" };
    private static readonly object ConsoleLayers = new { logical = "TVS1 application cells retained for semantic assertions only", driverOutput = "production Win32ConsoleDriver; separate native output-buffer readback hash", nativePixels = "actual owned classic conhost window via PrintWindow (host-window)" };

    private static async Task<(ScenarioResult, TargetIdentity?, object?)> RunOneAsync(string target, ScenarioDescriptor scenario, string size,
        string runRoot, RunOptions options, VisualGate? gate, Action<string>? progress, CancellationToken token)
    {
        bool proxy = scenario.Backend == "proxy-logical";
        string outcome = ScenarioOutcomes.Error, reason = "Incomplete";
        var checkpoints = new List<CheckpointResult>();
        var interactions = new List<InteractionRecord>();
        TargetIdentity? identity = null;
        bool exited = false, integrity = false, cleanup = false;
        TargetSession? session = null;
        Dictionary<string, string>? baseline = null;
        object? lifecycle = null;
        string grid = size, step = "launch";
        InteractionRecord? lastInput = null;
        string? released = null;
        try
        {
            string[] refused = (scenario.Safety ?? ScenarioSafety.None).RequiredCapabilities().Where(c => !(options.Allow ?? []).Contains(c)).ToArray();
            if (token.IsCancellationRequested) { outcome = ScenarioOutcomes.Aborted; reason = "Batch cancelled."; }
            // A verified hard platform/driver prerequisite that is not met is Skipped (counted, never a failure);
            // every other refusal below stays Blocked. Nothing runs and there is no fallback either way.
            else if (options.Driver != "NullDriver" && NativeExecution.Refusal(scenario, options.Driver) is { } nativeRefusal)
            { outcome = ScenarioOutcomes.Skipped; reason = ScenarioOutcomes.SkippedPrefix + nativeRefusal + ". Only audited F10, nested menu, mouse menu and Copy here / Rename here cancellation workflows run on native drivers; no logical fallback performed."; }
            else if (proxy && options.ProxyBackend == "Win32ConsoleDriver" && !OperatingSystem.IsWindows())
            { outcome = ScenarioOutcomes.Skipped; reason = ScenarioOutcomes.SkippedPrefix + "the Win32ConsoleDriver backend requires Windows; no fallback performed."; }
            else if (!scenario.Grid.Supports(size)) { outcome = ScenarioOutcomes.Blocked; reason = $"Scenario does not support grid {size} (supported {scenario.Grid.Describe()})."; }
            else if (refused.Length > 0)
            {
                outcome = ScenarioOutcomes.Blocked;
                reason = refused.Contains("elevation") ? "Scenario declares elevation; diagnostic capture never elevates."
                    : "Scenario declares capabilities this run did not allow: " + string.Join(", ", refused) + " (use --allow).";
            }
            else
            {
                session = new TargetSession(target, scenario.External?.FixtureId ?? scenario.Id, size, token, options.OperationMilliseconds, options.ScenarioSeconds, proxy ? options.ProxyBackend : options.Driver, proxy, options.TargetArguments, options.TargetEnvironment);
                Reply hello = await session.ConnectAsync();
                progress?.Invoke($"  Proxy requested: {options.ProxyBackend}; actual: {hello.Target?.Driver}; host: {hello.Target?.Console?.HostMode ?? "redirected"}; grid: {hello.Target?.Width}x{hello.Target?.Height}; resize: {hello.Capabilities?.Resize}; input: {hello.Capabilities?.InputAcknowledgement}; native capture: {hello.Capabilities?.NativeCapture}");
                identity = hello.Target;
                if (identity?.Driver != (proxy ? options.ProxyBackend : NativeExecution.ActualDriver(options.Driver))) throw new IOException("Actual target driver mismatch.");
                if (proxy && hello.Capabilities?.Mode != "proxy-logical") throw new IOException("Proxy capabilities missing; compiled fallback refused.");
                if (proxy && options.ProxyCapture != ProxyCaptureKinds.None && hello.Capabilities?.CaptureKinds.Contains(options.ProxyCapture) != true)
                    throw new IOException("Target does not advertise the requested native capture kind; no fallback.");
                var advertised = proxy ? scenario : hello.Scenarios?.FirstOrDefault(s => s.Id == (scenario.External?.FixtureId ?? scenario.Id));
                if (advertised is null || !advertised.Grid.Supports(size)) throw new IOException("Scenario/catalog changed since selection.");
                if (!proxy && scenario.External is null && advertised.IsFixtureOnly) throw new FixtureBindingException(ScenarioSelection.NotRunnable(advertised.Id));
                if (!proxy && scenario.External is { } binding && (advertised.Adapters is null || ExternalScenarios.BindingFingerprint(advertised) != binding.FixtureHash))
                    throw advertised.IsFixtureOnly
                        ? new FixtureBindingException($"Fixture-only contract of '{advertised.Id}' changed since scenario '{scenario.Id}' was bound "
                            + $"(bound {binding.FixtureHash}, advertised {ExternalScenarios.BindingFingerprint(advertised)}, revision {advertised.Contract!.Revision}); refresh the catalog.")
                        : new IOException("Fixture capabilities/catalog changed since external scenario validation; refresh catalog.");
                if (!proxy && scenario.External is not null && advertised.IsFixtureOnly && ExternalScenarios.UnboundCheckpoint(scenario, advertised) is { } unbound)
                    throw new FixtureBindingException(unbound);
                baseline = FixtureState(session.Fixture);
                step = "start";
                await session.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
                int index = 0;
                foreach (var s in scenario.Steps)
                {
                    step = $"step {++index} ({s.Kind} {(s.Kind == InputSteps.Text ? "[redacted]" : s.Name)})";
                    if (s.Kind == ScenarioStep.Checkpoint)
                    {
                        progress?.Invoke($"  checkpoint {s.Name}");
                        // Declared evidence plus whatever the checkpoint's assertions need; "none" leaves them UNSUPPORTED.
                        string[] evidence = options.Inspect switch
                        {
                            "all" => Protocol.EvidenceKinds.All,
                            "none" => [],
                            _ => (s.Evidence ?? []).Concat((s.Assertions ?? []).SelectMany(a => a.RequiredEvidence())).Distinct(StringComparer.Ordinal).ToArray(),
                        };
                        var attempts = new List<CheckpointAttempt>();
                        var clock = Stopwatch.StartNew();
                        var retry = s.Retry ?? new CheckpointRetry(1, 0);
                        Reply reply;
                        for (int attempt = 1; ; attempt++)
                        {
                            reply = await session.SendAsync(attempt == 1 ? Protocol.Operations.Checkpoint : Protocol.Operations.Recapture,
                                Protocol.States.Captured, checkpoint: s.Name, evidence: evidence, captureKind: proxy ? options.ProxyCapture : null);
                            var capture = reply.Capture ?? throw new IOException("Missing checkpoint capture.");
                            if (proxy && options.ProxyCapture != ProxyCaptureKinds.None && capture.ProxyNative is null) throw new IOException("Requested native capture missing; no fallback.");
                            byte[] cells = Convert.FromBase64String(capture.Cells);
                            var evaluated = AssertionEvaluator.Evaluate(s.Assertions ?? [], ScreenSnapshot.Deserialize(cells), capture.Inspection, scenario.Id, s.Name);
                            attempts.Add(new(attempt, clock.ElapsedMilliseconds, ScreenSnapshot.Hash(cells), evaluated));
                            if (attempt >= retry.Attempts || !evaluated.Any(a => a.Outcome == AssertionOutcomes.Fail)) break;
                            await session.DelayAsync(retry.IntervalMilliseconds);
                        }
                        checkpoints.Add(await WriteCheckpointAsync(runRoot, scenario, size, grid, hello.Target!, reply.Capture, s, evidence, interactions,
                            gate, session.Fixture, options.Driver, attempts.ToArray()));
                        interactions.Clear();
                        if (scenario.External is not null && checkpoints[^1].Assertions.FirstOrDefault(a => a.Severity == AssertionSeverity.Error && a.Outcome != AssertionOutcomes.Pass) is { } failure)
                            throw new ExternalAssertionFailure($"Checkpoint '{s.Name}' assertion '{failure.Id}' {failure.Outcome}: expected {failure.Expected}; actual {failure.Actual}. {failure.Explanation}");
                    }
                    else
                    {
                        Reply reply = await session.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: new InputStep(s.Kind, s.Name));
                        var record = reply.Interaction ?? throw new IOException("Missing interaction record.");
                        if (record.Kind != s.Kind || record.Name != s.Name) throw new IOException("Interaction record does not match the request.");
                        interactions.Add(record); lastInput = record;
                        if (s.Kind == ScenarioStep.Resize) grid = s.Name;
                    }
                }
                step = "completion";
                released = (await session.SendAsync(Protocol.Operations.Complete, Protocol.States.Completed)).Interaction?.Detail;
                await session.SendAsync(Protocol.Operations.Shutdown, Protocol.States.ShutdownAccepted, input: proxy && options.ProxyShutdownKey is { } key ? new InputStep(ScenarioStep.Key, key) : null);
                await session.WaitForExitAsync();
                exited = true;
                integrity = SameFixture(baseline, FixtureState(session.Fixture));
                if (!integrity) throw new IOException("Fixture changed during the scenario.");
                var failed = checkpoints.SelectMany(c => c.Assertions).Where(a => a.Severity == AssertionSeverity.Error && a.Outcome != AssertionOutcomes.Pass).ToArray();
                var inspectionErrors = checkpoints.Where(c => c.InspectionErrors.Length > 0).ToArray();
                if (failed.Length > 0)
                {
                    var first = failed[0];
                    outcome = ScenarioOutcomes.Failed;
                    reason = $"{failed.Length} assertion(s) did not pass. First: '{first.Id}' {first.Outcome} at checkpoint '{first.Checkpoint}' ({first.Grid}): expected {first.Expected}, actual {first.Actual ?? "n/a"}. {first.Explanation}";
                }
                else if (inspectionErrors.Length > 0)
                {
                    outcome = ScenarioOutcomes.Failed;
                    reason = $"Inspection failed at checkpoint '{inspectionErrors[0].Checkpoint}': {inspectionErrors[0].InspectionErrors[0]}";
                }
                else
                {
                    outcome = ScenarioOutcomes.Passed;
                    int assertions = checkpoints.Sum(c => c.Assertions.Length);
                    reason = $"{checkpoints.Count} checkpoint(s) captured; {assertions} assertion(s) checked; completion, fixture integrity and target exit verified."
                        + (released is null ? "" : " Input " + released + ".");
                }
            }
        }
        catch (Exception ex)
        {
            outcome = ex is ExternalAssertionFailure ? ScenarioOutcomes.Failed : token.IsCancellationRequested ? ScenarioOutcomes.Aborted : session?.Expired == true || ex is TimeoutException ? ScenarioOutcomes.TimedOut : ScenarioOutcomes.Error;
            // Binding diagnostics name scenario, fixture and checkpoint IDs (which contain '/'), never local paths.
            reason = (ex is ExternalAssertionFailure or FixtureBindingException ? ex.Message : ex is IOException or TimeoutException or NotSupportedException && !ex.Message.Contains('\\') && !ex.Message.Contains('/') ? ex.Message : "Diagnostic operation failed (" + ex.GetType().Name + ").")
                + $" [{step}, grid {grid}]"
                // The target appends the held input state to a refused or timed-out input step; otherwise name the last one.
                + (lastInput is { } last && !ex.Message.Contains("[last input:", StringComparison.Ordinal)
                    ? $" Last input: {last.Kind} {(last.Kind == InputSteps.Text ? "[redacted]" : last.Name)} (#{last.Sequence}, {last.Acknowledgement})" + (last.InputState is null ? "." : $"; input state: {last.InputState}.") : "");
            if (session is not null) await session.CollectDiagnosticsAsync();
            if (session?.Diagnostics is { Length: > 0 } diagnostics) reason += " Target stderr: " + diagnostics;
        }
        finally
        {
            if (session is not null)
            {
                try { await session.StopAsync(); }
                catch { outcome = ScenarioOutcomes.Error; reason = "Owned target termination failed."; }
                // EOF can arrive before the target process closes stderr. Collect again after the bounded
                // owned-process stop, so a crash is not reduced to a generic pipe EOF by that race.
                if (outcome is ScenarioOutcomes.Error or ScenarioOutcomes.TimedOut && !reason.Contains("Target stderr:", StringComparison.Ordinal))
                {
                    await session.CollectDiagnosticsAsync();
                    if (session.Diagnostics is { Length: > 0 } finalDiagnostics) reason += " Target stderr: " + finalDiagnostics;
                }
                try { if (baseline is not null) integrity = SameFixture(baseline, FixtureState(session.Fixture)); } catch { integrity = false; }
                await session.DisposeAsync(); cleanup = session.Cleanup;
                if (NativeExecution.IsGpu(options.Driver))
                {
                    lifecycle = new { scenarioId = scenario.Id, size, actualDriver = "SDLGpuDriver", requestedMode = options.Driver,
                        session.StopOutcome, session.ExitCode, session.GpuCleanupVerified, fixtureIntegrity = integrity, processCleanup = cleanup };
                    if (outcome == ScenarioOutcomes.Passed && !session.GpuCleanupVerified)
                    { outcome = ScenarioOutcomes.Error; reason = "GPU provider resource release evidence missing."; }
                }
                if ((!cleanup || !integrity) && outcome is ScenarioOutcomes.Passed or ScenarioOutcomes.Failed) { outcome = ScenarioOutcomes.Error; reason = "Fixture integrity or cleanup incomplete. " + reason; }
            }
        }
        return (new ScenarioResult(scenario.Id, size, outcome, reason, checkpoints.ToArray(), exited, integrity, cleanup, scenario.ExpectedResult), identity, lifecycle);
    }

    private static async Task<CheckpointResult> WriteCheckpointAsync(string runRoot, ScenarioDescriptor scenario, string initial, string grid,
        TargetIdentity identity, CheckpointCapture? capture, ScenarioStep step, string[] evidence, List<InteractionRecord> interactions,
        VisualGate? gate = null, string? fixture = null, string requestedDriver = "NullDriver", CheckpointAttempt[]? attempts = null)
    {
        string name = step.Name;
        if (capture is null || capture.Checkpoint != name) throw new IOException("Missing or mismatched checkpoint capture.");
        byte[] cells = Convert.FromBase64String(capture.Cells);
        ScreenSnapshot snapshot = ScreenSnapshot.Deserialize(cells);
        var (width, height) = ScenarioSelection.ParseSize(grid);
        // The checkpoint grid is the CURRENT grid (after any resize), checked three ways.
        if (snapshot.Width != width || snapshot.Height != height || capture.Width != width || capture.Height != height)
            throw new IOException($"Capture grid mismatch: expected {grid}, target reported {capture.Width}x{capture.Height}, cells {snapshot.Width}x{snapshot.Height}.");
        if (evidence.Length > 0 && capture.Inspection is null) throw new IOException("Requested inspection evidence missing.");
        var inspection = capture.Inspection;
        if (inspection is not null && evidence.Any(e => e switch
            {
                Protocol.EvidenceKinds.ViewTree => inspection.ViewTree is null,
                Protocol.EvidenceKinds.Menu => inspection.Menu is null,
                _ => inspection.Status is null,
            } && !inspection.Errors.Any(x => x.StartsWith(e + ":", StringComparison.Ordinal))))
            throw new IOException("Inspection evidence silently missing.");
        var assertions = AssertionEvaluator.Evaluate(step.Assertions ?? [], snapshot, inspection, scenario.Id, name);
        byte[] raster = LogicalPng.Raster(snapshot), png = LogicalPng.Encode(snapshot, raster);
        Dictionary<string, string>? native = null;
        string pixelsHash = ScreenSnapshot.Hash(raster);
        if (identity.Driver != "NullDriver" && capture.Proxy is null)
        {
            native = capture.Native is null ? throw new IOException("Native evidence missing; logical fallback refused.") : new(capture.Native);
            if (native.GetValueOrDefault("schema") != "tsharpvision.testing/native.1" || native.GetValueOrDefault("driver") != identity.Driver
                || native.GetValueOrDefault("captureLayer") != (identity.Driver == "Win32ConsoleDriver" ? "host-window" : "renderer-target")) throw new IOException("Native evidence identity mismatch.");
            if (NativeExecution.IsGpu(requestedDriver) &&
                (native.GetValueOrDefault("backend") != NativeExecution.Backend(requestedDriver)
                || native.GetValueOrDefault("renderingPath") != NativeExecution.GpuPath(requestedDriver)
                || native.GetValueOrDefault("requestedMode") != requestedDriver
                || native.GetValueOrDefault("gpu.adapterKind") != "hardware adapter"))
                throw new IOException("Native GPU backend/path/hardware mismatch; no fallback accepted.");
            png = Convert.FromBase64String(native["png"]); native.Remove("png");
            if (png.Length < 24 || !png.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw new IOException("Invalid native PNG.");
            pixelsHash = native["pixelsSha256"];
        }
        else if (capture.Native is not null) throw new IOException("Unexpected native payload for logical target.");
        string stem = ArtifactNames.Stem(scenario.Id, name, grid, initial);
        var proxyNative = capture.ProxyNative is null ? null : await ProxyNativeArtifacts.SaveAsync(runRoot, stem, identity, capture);
        var result = new CheckpointResult(name, grid, stem + ".png", stem + ".tvs1", stem + ".json", ScreenSnapshot.Hash(cells), ScreenSnapshot.Hash(png),
            pixelsHash, assertions, inspection?.Errors ?? []);
        await WriteNewAsync(Path.Combine(runRoot, result.Cells), cells);
        await WriteNewAsync(Path.Combine(runRoot, result.Png), png);
        await WriteNewAsync(Path.Combine(runRoot, result.Metadata), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = Protocol.Schema, protocolVersion = Protocol.Version, scenarioId = scenario.Id, checkpointId = name,
            size = grid, initialSize = initial, proxyNative,
            // Layer A only: the composed TScreen.ScreenBuffer cells. Never a driver or native screenshot.
            captureLayer = native is null ? "logical" : native["captureLayer"], layers = capture.ProxyNative is not null ? ProxyNativeLayers : capture.Proxy is null ? LayersFor(identity.Driver) : capture.Console is not null ? ConsoleProxyLayers : Layers, proxy = capture.Proxy, console = capture.Console, consoleOutputMatchesRoot = capture.ConsoleOutputMatchesRoot, width, height, driver = identity.Driver, native,
            target = new { assembly = identity.Assembly, version = identity.AssemblyVersion, mvid = identity.Mvid },
            tsharpVisionVersion = identity.FrameworkVersion, testingVersion = identity.TestingVersion,
            capturedAt = capture.CapturedAt, stableObservations = capture.Observations,
            renderer = capture.Proxy is null ? RendererFor(identity.Driver) : LogicalPng.Renderer, cellPixels = new { width = native is null ? LogicalPng.CellWidth : int.Parse(native["cellWidth"]), height = native is null ? LogicalPng.CellHeight : int.Parse(native["cellHeight"]) }, cursor = native is null ? "omitted" : identity.Driver == "Win32ConsoleDriver" ? "host-rendered cursor" : "renderer cursor",
            inputRouting = new { key = capture.Proxy is not null ? "Proxy -> TEventQueue -> application event loop (accepted-queue ACK)" : native is null ? TestTargetHost.KeyRouting : identity.Driver == "Win32ConsoleDriver" ? "Target-owned WriteConsoleInputW -> actual Win32ConsoleDriver translation" : "Target-owned SDL_PushEvent -> actual SDL driver translation", action = capture.Proxy is not null ? "unsupported" : TestTargetHost.ActionRouting, resize = capture.Proxy is not null ? "Negotiated backend grid capability; framework resize through application event loop" : native is null ? TestTargetHost.ResizeRouting : "unsupported",
                mouse = capture.Proxy is not null ? "Proxy -> TEventQueue -> application event loop" : native is null ? TestTargetHost.MouseRouting : identity.Driver == "Win32ConsoleDriver" ? "unsupported" : "Target-owned SDL_PushEvent mouse -> actual SDL driver mouse translation", release = capture.Proxy is not null ? "Existing InputPlanner -> proxy queue" : native is null ? TestTargetHost.ReleaseRouting : "unsupported (audited clicks finish released)" },
            surface = capture.Surface, interactionsSincePreviousCheckpoint = interactions.Select(i => i.Kind == InputSteps.Text ? i with { Name = "[redacted]", Detail = null } : i).ToArray(),
            evidenceRequested = evidence,
            inspection,
            assertions,
            attempts,
            assertionSummary = new
            {
                pass = assertions.Count(a => a.Outcome == AssertionOutcomes.Pass), fail = assertions.Count(a => a.Outcome == AssertionOutcomes.Fail),
                unsupported = assertions.Count(a => a.Outcome == AssertionOutcomes.Unsupported),
            },
            artifacts = new { png = result.Png, cells = result.Cells }, cellsSha256 = result.CellsHash, pngSha256 = result.PngHash,
            // PNG bytes depend on the platform's deflate output; the raster hash is the cross-platform pixel identity.
            pixelsSha256 = result.PixelsHash,
            result = "Captured", review = "Unreviewed",
        }, Json));
        // Compared after the capture's own artifacts exist, so a mismatch never loses the source capture.
        if (gate is not null && await gate.EvaluateAsync(scenario, name, grid, initial, snapshot, stem, NormalizationContext.FromFixture(fixture), identity) is { } visual)
            result = result with { Visual = visual };
        return result;
    }

    // Artifacts are never overwritten: a name clash is a defect, not a reason to lose evidence.
    private static async Task WriteNewAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes);
    }

    private static Dictionary<string, string> FixtureState(string root)
    {
        if (!TargetSession.HasNoLinks(root)) throw new IOException("Fixture contains a link.");
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), p => Directory.Exists(p) ? "directory" :
                ScreenSnapshot.Hash(File.ReadAllBytes(p)) + ";mtime=" + File.GetLastWriteTimeUtc(p).Ticks + ";attributes=" + (int)File.GetAttributes(p), StringComparer.Ordinal);
    }
    private static bool SameFixture(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(kv => b.GetValueOrDefault(kv.Key) == kv.Value);
}

/// <summary>Execution environment recorded with every run. No user, machine or credential information.</summary>
internal static class Provenance
{
    internal static object Environment() => new
    {
        os = RuntimeInformation.OSDescription, osPlatform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "other",
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(), processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        runtime = RuntimeInformation.FrameworkDescription, elevated = System.Environment.IsPrivilegedProcess,
    };
}

/// <summary>Disposable per-execution temp roots: <c>%TEMP%/tsharp-diagnostics-&lt;32 hex&gt;/{fixture,state,profile}</c> plus an
/// ownership marker. The sweep deletes only roots that are provably this tool's and abandoned.</summary>
internal static class TempRoots
{
    internal const string Prefix = "tsharp-diagnostics-", Marker = "owner.json", Tool = "tsharpvision-testworkbench";
    internal static readonly TimeSpan MinimumOrphanAge = TimeSpan.FromHours(1);
    private static readonly Regex Name = new("^tsharp-diagnostics-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    internal sealed record Owner(string Tool, int Pid, long ProcessStart, DateTimeOffset Created);

    internal static void WriteMarker(string root)
    {
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(root, Marker), JsonSerializer.Serialize(new Owner(Tool, self.Id, self.StartTime.ToUniversalTime().Ticks, DateTimeOffset.UtcNow)));
    }

    /// <summary>Deletes abandoned roots only when ALL hold: exact name pattern directly under the temp directory; not a link;
    /// a valid marker written by this tool; marker and directory older than <see cref="MinimumOrphanAge"/>; the recorded
    /// controller process is gone (or its PID was reused by a process started at another time); no link anywhere inside;
    /// only the expected top-level entries. Anything else is left alone and counted as skipped.</summary>
    internal static object SweepOrphans(string? temp = null, TimeSpan? minimumAge = null)
    {
        temp ??= Path.GetTempPath();
        var age = minimumAge ?? MinimumOrphanAge;
        int examined = 0, deleted = 0;
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateDirectories(temp, Prefix + "*").ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new { examined, deleted, skipped = new { unreadableTemp = 1 } }; }
        foreach (string root in candidates)
        {
            examined++;
            try
            {
                if (!Name.IsMatch(Path.GetFileName(root))) { Skip("name"); continue; }
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { Skip("link"); continue; }
                string marker = Path.Combine(root, Marker);
                if (!File.Exists(marker)) { Skip("no-marker"); continue; }
                var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(marker));
                if (owner is null || owner.Tool != Tool) { Skip("foreign-marker"); continue; }
                if (DateTimeOffset.UtcNow - owner.Created < age || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(root) < age) { Skip("recent"); continue; }
                if (Alive(owner)) { Skip("owner-running"); continue; }
                if (Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Any(n => n is not (Marker or "fixture" or "state" or "profile"))) { Skip("unexpected-content"); continue; }
                if (!TargetSession.HasNoLinks(root)) { Skip("link-inside"); continue; }
                Directory.Delete(root, true); deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Skip("error"); }
        }
        return new { examined, deleted, skipped };
    }

    private static bool Alive(Owner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.Pid);
            return process.StartTime.ToUniversalTime().Ticks == owner.ProcessStart;
        }
        catch (ArgumentException) { return false; } // no such process
        catch (Exception) { return true; }          // cannot tell: treat as alive (keep)
    }
}

internal sealed class TargetSession : IAsyncDisposable
{
    internal static TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private readonly string target, instance = Guid.NewGuid().ToString("N"), secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string? scenario;
    private readonly string size, root, state, profile;
    private readonly int? operationMs, lifetimeSeconds;
    private readonly string driver;
    private readonly bool proxy;
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource pending;
    private readonly CancellationToken external;
    private NamedPipeClientStream? pipe;
    private Process? child;
    private OwnedConsoleProcess? consoleOwner;
    private bool disposed;
    private int launchAttempted;
    internal int? OwnedProcessId => consoleOwner?.ProcessId;
    private Task<string>[] drains = [];
    public string Fixture { get; }
    public bool Cleanup { get; private set; }
    internal string StopOutcome { get; private set; } = "not started";
    internal int? ExitCode { get; private set; }
    internal string? CleanupFailure { get; private set; }
    internal bool GpuCleanupVerified => drains.Length == 2 && drains[1].IsCompletedSuccessfully
        && drains[1].Result.Contains(NativeExecution.GpuCleanupMarker, StringComparison.Ordinal);
    public bool Expired => deadline.IsCancellationRequested;
    /// <summary>Bounded start of the target's stderr, when it has exited.</summary>
    public string? Diagnostics => drains.Length == 2 && drains[1].IsCompletedSuccessfully
        ? Tail(drains[1].Result) : StartupDiagnostics();

    private string? StartupDiagnostics()
    {
        try
        {
            string path = Path.Combine(state, "proxy-startup-error.txt");
            return File.Exists(path) ? Tail(File.ReadAllText(path)) : null;
        }
        catch (IOException) { return null; }
    }

    private readonly string[] launchArguments;
    private readonly bool record;
    /// <summary>The controller-owned state directory (recording journal) until the session is disposed.</summary>
    internal string StateDirectory => state;
    internal bool HasExited => child is { HasExited: true };
    internal int? ProcessId => child?.Id;
    private readonly Dictionary<string, string> launchEnvironment;
    public TargetSession(string target, string? scenario, string size, CancellationToken token, int? operationMs = null, int? lifetimeSeconds = null, string driver = "NullDriver", bool proxy = false, string[]? arguments = null, IReadOnlyDictionary<string, string>? environment = null, bool record = false)
    {
        this.record = record;
        TargetLaunch.Validate(arguments ?? [], environment ?? new Dictionary<string, string>());
        launchArguments = arguments?.ToArray() ?? [];
        launchEnvironment = environment is null ? new() : new(environment);
        this.target = Path.GetFullPath(target); this.scenario = scenario; this.size = size; external = token;
        this.operationMs = operationMs; this.lifetimeSeconds = lifetimeSeconds;
        this.driver = driver; this.proxy = proxy;
        if (!File.Exists(this.target)) throw new ArgumentException("Target executable does not exist.");
        deadline = new(lifetimeSeconds is { } s ? TimeSpan.FromSeconds(s + 5) : Deadline);
        pending = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
        root = Path.Combine(Path.GetTempPath(), TempRoots.Prefix + Guid.NewGuid().ToString("N"));
        Fixture = Path.Combine(root, "fixture");
        state = Path.Combine(root, "state");
        profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(Fixture); Directory.CreateDirectory(state); Directory.CreateDirectory(profile);
        TempRoots.WriteMarker(root);
    }

    // Retry gaps share the session lifetime, target-exit and caller-cancellation bound.
    internal Task DelayAsync(int milliseconds) => Task.Delay(milliseconds, pending.Token);

    public async Task<Reply> ConnectAsync()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if (Interlocked.Exchange(ref launchAttempted,1)!=0) throw new InvalidOperationException("Target session launch is single-use.");
        bool ownedConsole = proxy && driver == "Win32ConsoleDriver";
        if (ownedConsole && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned Console requires Windows.");
        var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Fixture, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!proxy) start.ArgumentList.Add(TestTargetHost.ActivationArgument);
        string Expand(string value) => TargetLaunch.Expand(value, state, Fixture, Path.GetDirectoryName(target)!);
        foreach (string argument in launchArguments) start.ArgumentList.Add(Expand(argument));
        foreach (var pair in launchEnvironment) start.Environment[pair.Key] = Expand(pair.Value);
        // Inherited framework/driver selection never reaches the target; only this session's values do.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("TSHARPVISION_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        // Best-effort profile isolation for code that locates per-user directories through the environment. Windows
        // known folders (SHGetKnownFolderPath) are unaffected; applications must use the context's StateDirectory.
        // TMPDIR is kept: the local IPC endpoint lives there on Unix.
        foreach (string key in new[] { "HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "APPDATA", "LOCALAPPDATA" })
            start.Environment[key] = key == "HOME" ? profile : Path.Combine(profile, key.ToLowerInvariant());
        start.Environment[TestTargetHost.InstanceVariable] = instance;
        start.Environment[TestTargetHost.SecretVariable] = secret;
        start.Environment[TestTargetHost.StateVariable] = state;
        start.Environment[TestTargetHost.SizeVariable] = size;
        if (operationMs is { } ms) start.Environment[TestTargetHost.OperationLimitVariable] = ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (lifetimeSeconds is { } life) start.Environment[TestTargetHost.LifetimeVariable] = life.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (scenario is not null) start.Environment[TestTargetHost.ScenarioVariable] = scenario;
        if (record && proxy) start.Environment[TestTargetHost.RecordVariable] = "1";
        if (!proxy && driver != "NullDriver") start.Environment[NativeExecution.Variable] = driver;
        if (proxy)
        {
            start.Environment["TSHARPVISION_DRIVER_DECORATOR"] = "TSharpWorkbench.Proxy.WorkbenchProxyDriverFactory, TSharpWorkbench.Proxy";
            start.Environment["TSHARPVISION_DRIVER"] = driver;
            start.Environment["TSHARPVISION_PROXY_SESSION"] = "1";
        }
        if (ownedConsole)
        {
            start.Environment["TSHARPVISION_OWNED_CONSOLE"] = "1";
            consoleOwner = OwnedConsoleProcess.Start(start);
            child = consoleOwner.Process;
        }
        else child = Process.Start(start) ?? throw new IOException("Target launch failed.");
        child.EnableRaisingEvents = true; child.Exited += (_, _) => { try { pending.Cancel(); } catch (ObjectDisposedException) { } };
        if (!ownedConsole) drains = [child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync()];
        if (child.HasExited) pending.Cancel();
        pipe = new NamedPipeClientStream(".", "tsharp-diagnostics-" + instance, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(pending.Token); }
        catch (OperationCanceledException) when (child.HasExited && !external.IsCancellationRequested)
        { throw new IOException($"Target exited ({child.ExitCode}) before connecting; {StartupDiagnostics() ?? "verify the explicitly selected automation mode and matching deployment."}"); }
        Reply hello = await SendAsync(Protocol.Operations.Handshake, Protocol.States.Ready);
        var (width, height) = ScenarioSelection.ParseSize(size);
        if (hello.Target is null) throw new IOException("Target identity missing.");
        if ((proxy || scenario is not null) && (hello.Target.Driver != NativeExecution.ActualDriver(driver) || hello.Target.Width != width || hello.Target.Height != height))
            throw new IOException("Target driver/grid mismatch.");
        if (ownedConsole && (hello.Target.Console is not { } console || console.ProcessId != child.Id
            || console.WindowHandle == 0 || console.WindowWidth != width || console.WindowHeight != height
            || console.BufferWidth != width || console.BufferHeight != height))
            throw new IOException("Owned console native evidence missing or inconsistent.");
        if (ownedConsole) consoleOwner!.ConsoleWindow = hello.Target.Console!.WindowHandle;
        return hello;
    }

    public async Task<Reply> SendAsync(string operation, string expected, string? checkpoint = null, InputStep? input = null, string[]? evidence = null, string? captureKind = null, long? after = null, long? marksAfter = null, string[]? recordOptions = null)
    {
        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(pending.Token);
        operationDeadline.CancelAfter(operationMs ?? 10000);
        var operationToken = operationDeadline.Token;
        string id = Guid.NewGuid().ToString("N");
        try
        {
            await Protocol.WriteAsync(pipe!, new Request(Protocol.Version, Protocol.Schema, instance, id, secret, operation, scenario, checkpoint, input,
                evidence is { Length: > 0 } ? evidence : null, captureKind, after, marksAfter, recordOptions is { Length: > 0 } ? recordOptions : null), operationToken);
            return Validate(await Protocol.ReadAsync<Reply>(pipe!, operationToken), id, instance, expected);
        }
        catch (OperationCanceledException) when (!pending.IsCancellationRequested)
        { throw new TimeoutException("Target operation deadline expired."); }
    }

    internal static Reply Validate(Reply reply, string id, string instance, string expected)
    {
        // A peer built against another v1 wire schema must never be treated as compatible.
        if (reply.Schema != Protocol.Schema || reply.ProtocolVersion != Protocol.Version) throw new IOException("Target protocol schema mismatch.");
        if (reply.Id != id || reply.Instance != instance) throw new IOException("Response identity mismatch.");
        if (reply.State != expected && reply.State == Protocol.States.Rejected && reply.Reason?.StartsWith(Protocol.TimeoutPrefix, StringComparison.Ordinal) == true)
            throw new TimeoutException(reply.Reason[Protocol.TimeoutPrefix.Length..]);
        // A nonfatal "busy" rejection (read-only inspection did not reach a UI boundary in time); the session continues.
        if (reply.State != expected && reply.State == Protocol.States.Rejected && reply.Reason?.StartsWith(Protocol.BusyPrefix, StringComparison.Ordinal) == true)
            throw new TargetBusyException(reply.Reason[Protocol.BusyPrefix.Length..]);
        if (reply.State != expected && reply.State == Protocol.States.Rejected && reply.Reason?.StartsWith(Protocol.BindingPrefix, StringComparison.Ordinal) == true)
            throw new FixtureBindingException(reply.Reason[Protocol.BindingPrefix.Length..]);
        if (reply.State != expected) throw new IOException(reply.Reason ?? "Unexpected target state.");
        return reply;
    }

    public async Task WaitForExitAsync()
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, external);
        await child!.WaitForExitAsync(wait.Token);
        await Task.WhenAll(drains);
        if (child.ExitCode != 0) throw new IOException("Target shutdown failed (exit code " + child.ExitCode + ").");
    }

    /// <summary>When the target already exited (crash), waits briefly for its stderr so the failure reason can include it.</summary>
    public async Task CollectDiagnosticsAsync()
    {
        if (child is null || drains.Length != 2) return;
        try { if (child.HasExited) await drains[1].WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }

    public async Task StopAsync()
    {
        pipe?.Dispose(); // EOF ends the target session; the host unwinds nested loops and quits.
        if (child is null) return;
        if (StopOutcome != "not started")
        {
            if (consoleOwner is not null) await consoleOwner.WaitForWindowReleaseAsync();
            return;
        }
        if (child.HasExited) StopOutcome = "already exited";
        else
        {
            try
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await child.WaitForExitAsync(grace.Token); StopOutcome = "graceful EOF exit";
            }
            catch (OperationCanceledException)
            {
                if (consoleOwner is not null) consoleOwner.Terminate(); else child.Kill(true); using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(killed.Token); StopOutcome = "owned target terminated after grace timeout";
            }
        }
        ExitCode = child.ExitCode;
        consoleOwner?.Dispose();
        if (consoleOwner is not null) await consoleOwner.WaitForWindowReleaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await StopAsync();
            try { await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (HasNoLinks(root)) { Directory.Delete(root, true); Cleanup = true; }
        }
        catch (Exception ex) { Cleanup = false; CleanupFailure = ex.ToString(); }
        consoleOwner?.Dispose(); child?.Dispose(); pending.Dispose(); deadline.Dispose();
    }

    private static string? Tail(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        // The start carries the exception type, message and innermost frames; keep it rather than the tail.
        text = text.Length > 600 ? text[..600] + "..." : text;
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }

    internal static bool HasNoLinks(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if ((attributes & FileAttributes.Directory) != 0 && !HasNoLinks(path)) return false;
        }
        return true;
    }
}
