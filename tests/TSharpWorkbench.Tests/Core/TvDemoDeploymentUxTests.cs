using System.Diagnostics;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Automatic target preparation. The original deployment is a plain package-only TVDemo build without Proxy
// (TSWB_TVDEMO_ORIGINAL, else artifacts/deployment-ux/tvdemo-original/bin/TVDemo.Package/release); each test works on a
// disposable slim copy (top-level files, Help and runtimes/<this RID>). The Proxy payload is the one staged beside the
// Workbench build (TSWB_PROXY_PAYLOAD, else ../TSharpWorkbench/<configuration>/proxy-payload). Prepared copies go to a
// per-test root. Logical screenshots: TSWB_DEPLOYMENT_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class TvDemoDeploymentUxTests
{
    private const string TvDemoExe = "TSharpVision.Samples.TVDemo.exe";

    // ── environment ──────────────────────────────────────────────────────────────────

    internal static string? Payload()
    {
        string base_ = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable("TSWB_PROXY_PAYLOAD"),
            Path.Combine(base_, "..", "..", "TSharpWorkbench", Path.GetFileName(base_), ProxyPayload.Folder) })
            if (candidate is { Length: > 0 } && File.Exists(Path.Combine(candidate, ProxyPayload.DepsFile))) return Path.GetFullPath(candidate);
        return null;
    }
    internal static string? Original()
    {
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable("TSWB_TVDEMO_ORIGINAL"),
            Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "artifacts", "deployment-ux", "tvdemo-original", "bin", "TVDemo.Package", "release", TvDemoExe) })
            if (candidate is { Length: > 0 } && File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
    // As TargetDiagnosticsUxTests: without the payload or the original deployment these tests return (the report names the environment).
    private static bool Available()
    {
        if (Payload() is null || Original() is null) return false;
        Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, Payload());
        return true;
    }
    // A disposable slim copy of the original deployment, in its own folder.
    private static string CopyOriginal(string root, string name = "app")
    {
        string source = Path.GetDirectoryName(Original()!)!;
        string target = Path.Combine(root, name);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string sub in new[] { "Help", Path.Combine("runtimes", ProxyPayload.CurrentRid()) })
        {
            string from = Path.Combine(source, sub);
            if (!Directory.Exists(from)) continue;
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(file, to);
            }
        }
        return Path.Combine(target, TvDemoExe);
    }
    private static string Temp() => Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "artifacts", "deployment-ux", "t", Guid.NewGuid().ToString("N")[..8]);
    private static void Delete(string root)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(root)) { foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)) f.IsReadOnly = false; Directory.Delete(root, true); } return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
    private static TargetPreparationService Service(string root, Func<string, long?>? free = null, Action<string>? hook = null)
    {
        var service = new TargetPreparationService(Path.Combine(root, "prepared"), Payload()) { FreeSpace = free ?? Preparation.FreeSpace, BeforeWrite = hook };
        TargetDiagnostics.Preparation = service;
        TargetPreparationService.ForgetStatus();
        return service;
    }
    // Every file of a folder with its content hash (to prove a folder was not changed).
    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(folder, f), Preparation.FileHash, StringComparer.OrdinalIgnoreCase);
    private static PreparationPlan Plan(TargetPreparationService service, string exe, PreparationStrategy strategy = PreparationStrategy.PreparedCopy,
        PreparationCapabilities? capabilities = null) => service.CreatePlan(service.Inspect(exe), strategy, capabilities ?? TargetPreparationService.DefaultCapabilities);
    private static DiagnosticContext ProxyRun(string backend = "NullDriver") => new(true, false, backend, null, null, Run: true);
    private static int Running(string exe) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Count(p =>
    { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); } });

    // ── A. inspection and planning ──────────────────────────────────────────────────────

    [Fact]
    public void MissingProxyIsDetectedAndThePlanDeploysTheProxyClosure()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            var service = Service(root);
            var inspection = service.Inspect(exe);
            Assert.Null(inspection.Unsupported);
            Assert.Contains(inspection.Report.Findings, f => f.Code == "PROXY001" && f.Blocking);
            Assert.Equal(OfferKind.Prepare, service.Offer(TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), ProxyRun())).Kind);
            var plan = service.CreatePlan(inspection, PreparationStrategy.PreparedCopy, TargetPreparationService.DefaultCapabilities);
            Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
            var payload = service.Payload!;
            // Exactly the closure from the Proxy's deps.json: Proxy and Testing are added; the framework is never deployed.
            Assert.Equal(["TSharpWorkbench.Proxy.dll", "TSharpWorkbench.Testing.dll"], plan.Entries.Where(e => e.Action == PlanAction.Add).Select(e => e.Path).Order().ToArray());
            Assert.DoesNotContain(plan.Entries, e => e.Action is PlanAction.Add or PlanAction.Replace && e.Path == "TSharpVision.dll");
            Assert.All(plan.Entries.Where(e => e.Action is not PlanAction.Copy), e => Assert.Contains(payload.Assets, a => a.Destination == e.Path));
            // Existing compatible dependencies are kept (SDL driver, SDL3-CS, console driver, natives from runtimes/<rid>).
            Assert.Contains(plan.Entries, e => e.Action == PlanAction.Keep && e.Path == "TSharpVision.Drivers.SDL.dll");
            Assert.Contains(plan.Entries, e => e.Action == PlanAction.Provided && e.Path == "SDL3.dll");
            Assert.Equal("Compatible", plan.Components.Single(c => c.Name == "Framework").State);
            Assert.Equal("Missing (fixable)", plan.Components.Single(c => c.Name == "Proxy and Testing").State);   // Wording
            Assert.Contains(plan.Compatibility, l => l.StartsWith("Framework: TSharpVision " + payload.FrameworkVersion + ": identical build", StringComparison.Ordinal));
            // Planning is read-only: nothing was created.
            Assert.False(Directory.Exists(Path.Combine(root, "prepared")));
            Assert.StartsWith(Path.Combine(root, "prepared"), plan.Destination);
            Assert.Contains("PreparedTargets", PreparationStore.DefaultRoot());
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task PreparedCopySucceedsLeavesTheOriginalUnchangedAndIsReady()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            var before = Snapshot(Path.GetDirectoryName(exe)!);
            var service = Service(root);
            var plan = Plan(service, exe);
            var reports = new List<PreparationProgress>();
            var result = await service.ExecuteAsync(plan, new SyncProgress(reports.Add));
            Assert.True(result.Success, result.Message + string.Join("\n", result.Details));
            Assert.Equal(before, Snapshot(Path.GetDirectoryName(exe)!));
            string prepared = result.PreparedExecutable!;
            Assert.Equal(Path.Combine(plan.Destination, TvDemoExe), prepared);
            var manifest = JsonSerializer.Deserialize<PreparationManifest>(File.ReadAllText(Path.Combine(plan.Destination, PreparationManifest.FileName)), Preparation.Json)!;
            Assert.True(manifest.Verified);
            Assert.Equal(PreparationManifest.CurrentSchema, manifest.Schema);
            Assert.Equal(exe, manifest.SourceExecutable);
            Assert.Equal(service.Payload!.FrameworkVersion, manifest.FrameworkVersion);
            Assert.Equal(["proxy", "sdl", "console"], manifest.Capabilities);
            Assert.All(manifest.Files, f => Assert.Equal(f.Sha256, Preparation.FileHash(Path.Combine(plan.Destination, f.Path))));
            Assert.DoesNotContain(Path.GetDirectoryName(exe)!, string.Join("|", Directory.GetFileSystemEntries(Path.Combine(root, "prepared"), "*", SearchOption.AllDirectories)));
            // Identities: the Proxy and Testing are the payload's; the framework stays the application's (identical).
            Assert.Equal(service.Payload.ProxySha256, Preparation.FileHash(Path.Combine(plan.Destination, TargetDiagnostics.ProxyFile)));
            Assert.Equal(Preparation.FileHash(Path.Combine(Path.GetDirectoryName(exe)!, "TSharpVision.dll")), Preparation.FileHash(Path.Combine(plan.Destination, "TSharpVision.dll")));
            Assert.Equal(PreparedState.Ready, service.Status(plan.Destination, deep: true)!.State);
            foreach (string backend in new[] { "NullDriver", "SDLDriver", "SDLGpuDriver", "Win32ConsoleDriver" })
            {
                var report = TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(prepared), ProxyRun(backend));
                Assert.True(report.Ready, backend + ": " + string.Join("\n", TargetDiagnostics.Lines(report)));
                Assert.Contains(report.Findings, f => f.Code == "PREP000");
            }
            // Progress was meaningful and never claimed completion before publication.
            Assert.Contains(reports, r => r.Phase == "Copying files" && r.Files > 0 && r.Files < r.TotalFiles);
            Assert.Equal("Prepared", reports[^1].Phase);
            Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(root, "prepared", plan.ApplicationId)), d => Path.GetFileName(d).StartsWith(".staging", StringComparison.Ordinal));
            // Reused: the original now has an existing Ready prepared copy, offered by Inspect.
            Assert.Equal(PreparedState.Ready, service.Inspect(exe).Existing?.State);
            // The prepared copy itself offers Re-prepare, never an in-place installation into it.
            var again = service.Inspect(prepared);
            Assert.True(again.IsPreparedCopy); Assert.Equal(exe, again.Source);
            Assert.Contains(service.CreatePlan(again, PreparationStrategy.InPlaceInstallation, TargetPreparationService.DefaultCapabilities).Problems, p => p.Contains("Re-prepare"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task PreparedTvDemoRunsAboutAndMenuOnEveryBackendAndCleansUp()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            var service = Service(root);
            var result = await service.ExecuteAsync(Plan(service, exe));
            Assert.True(result.Success, result.Message);
            string prepared = result.PreparedExecutable!;
            // Runtime activation of the prepared copy (headless Proxy handshake), then the real scenarios on every backend.
            var verified = await new ScenarioRunner().VerifyAsync(prepared, true);
            Assert.True(verified.Proxy && verified.Cleanup);
            string scenarios = Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios");
            var catalog = ExternalScenarios.Merge([], [ExternalScenarios.Load(Path.Combine(scenarios, "about.scenario.json")), ExternalScenarios.Load(Path.Combine(scenarios, "menu.scenario.json"))]).ToArray();
            var lines = new List<string>();
            foreach (string backend in new[] { "NullDriver", "SDLDriver", "SDLGpuDriver", "Win32ConsoleDriver" })
            {
                var batch = await new ScenarioRunner().RunAsync(prepared, catalog, new RunOptions(Path.Combine(root, "runs-" + backend), ["80x25"]) { ProxyBackend = backend });
                foreach (var r in batch.Results) lines.Add($"{backend} {r.ScenarioId} {r.Size}: {r.Outcome} ({r.Checkpoints.Length} checkpoints) {r.Reason}");
                Assert.All(batch.Results, r => Assert.True(r.Outcome == ScenarioOutcomes.Passed, $"{backend} {r.ScenarioId}: {r.Outcome} {r.Reason}"));
                Assert.All(batch.Results.SelectMany(r => r.Checkpoints), c => Assert.True(File.Exists(Path.Combine(batch.RunRoot, c.Png))));
                Assert.Equal(0, Running(prepared));
            }
            if (Environment.GetEnvironmentVariable("TSWB_DEPLOYMENT_EVIDENCE") is { Length: > 0 } evidence)
            { Directory.CreateDirectory(evidence); File.WriteAllLines(Path.Combine(evidence, "..", "runtime-matrix.txt"), lines); }
            Assert.Equal(8, lines.Count);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void IncompatibleAndUncertainFrameworksAndConflictsAreRefused()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            var service = Service(root);
            string version = TargetDiagnostics.Inspect(Original()!).FrameworkPackageVersion!;
            // Same package version, different content. The version decides; another build of the same version is
            // supported (kept, never replaced) and confirmed at runtime by Verify target.
            string uncertain = CopyOriginal(root, "uncertain");
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(uncertain)!, "TSharpVision.dll"), "x");
            var plan = Plan(service, uncertain);
            Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
            Assert.True(plan.OtherBuild);
            Assert.Equal(PreparationVerdict.SameVersion, plan.Components.Single(c => c.Name == "Framework").State);
            Assert.DoesNotContain(plan.Entries, e => e.Action is PlanAction.Add or PlanAction.Replace && e.Path == "TSharpVision.dll");
            // Another framework version: incompatible, never replaced, for both methods.
            string incompatible = CopyOriginal(root, "incompatible");
            string deps = Path.ChangeExtension(incompatible, ".deps.json");
            File.WriteAllText(deps, File.ReadAllText(deps).Replace("\"TSharpVision/" + version + "\"", "\"TSharpVision/2.0.0-other\"").Replace("\"TSharpVision\": \"" + version + "\"", "\"TSharpVision\": \"2.0.0-other\""));
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(incompatible)!, "TSharpVision.dll"), "x");
            foreach (var strategy in new[] { PreparationStrategy.PreparedCopy, PreparationStrategy.InPlaceInstallation })
            {
                var refused = Plan(service, incompatible, strategy);
                Assert.Equal("Version mismatch", refused.Components.Single(c => c.Name == "Framework").State);   // Was "Incompatible"
                Assert.Contains(refused.Problems, p => p.Contains("2.0.0-other") && p.Contains("major"));
            }
            // A conflicting native library refuses the plan (natives are never identified by version). The
            // SDL component is always deployed, so it refuses even when only the headless Proxy is asked for.
            string conflict = CopyOriginal(root, "conflict");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(conflict)!, "SDL3.dll"), "x");
            foreach (var wanted in new[] { TargetPreparationService.DefaultCapabilities, PreparationCapabilities.Proxy | PreparationCapabilities.Console })
            {
                var withSdl = Plan(service, conflict, capabilities: wanted);
                Assert.Contains(withSdl.Entries, e => e.Action == PlanAction.Conflict && e.Path == "SDL3.dll");
                Assert.False(withSdl.CanExecute);
                Assert.Contains(withSdl.Problems, p => p.StartsWith("SDL3.dll:"));
            }
            Assert.False(Directory.Exists(Path.Combine(root, "prepared")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void UnsupportedTargetsAreExplainedAndNeverPrepared()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            var service = Service(root);
            Directory.CreateDirectory(root);
            // A native executable (no .NET application assembly).
            string native = Path.Combine(root, "native", "where.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), native);
            Assert.Contains("Native programs and single-file bundles", service.Inspect(native).Unsupported);
            // A bundled runtime is supported when its major version meets the Proxy requirement.
            string selfContained = CopyOriginal(root, "self");
            string config = Path.ChangeExtension(selfContained, ".runtimeconfig.json");
            File.WriteAllText(config, "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}]}}");
            var selfInspection = service.Inspect(selfContained);
            Assert.Null(selfInspection.Unsupported);
            var repair = new TargetRepairService(service).Plan(selfContained, TargetDiagnostics.Evaluate(selfInspection.Target, ProxyRun()), allowReplace: false);
            Assert.True(repair.CanRepair, repair.Summary);
            Assert.Contains(repair.Items, i => i.Code == "PROXY001" && i.State == RepairState.Fix);
            File.WriteAllText(config, "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.0\"}]}}");
            Assert.Contains(".NET 8", service.Inspect(selfContained).Unsupported);
            // An older runtime than the Proxy needs.
            string old = CopyOriginal(root, "old");
            File.WriteAllText(Path.ChangeExtension(old, ".runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net8.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.0\"}}}");
            Assert.Contains(".NET 8", service.Inspect(old).Unsupported);
            // A diagnostic build hosts its own scenarios: no preparation (and none offered).
            var host = service.Inspect(TestWorkbenchHostTests.SampleTarget);
            Assert.Contains("diagnostic build", host.Unsupported);
            Assert.Equal(OfferKind.None, service.Offer(TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(TestWorkbenchHostTests.SampleTarget), ProxyRun())).Kind);
            // A missing executable.
            Assert.Contains("does not exist", service.Inspect(Path.Combine(root, "missing.exe")).Unsupported);
            foreach (string exe in new[] { native, selfContained, old })
                Assert.False(Plan(service, exe).CanExecute);
            Assert.False(Directory.Exists(Path.Combine(root, "prepared")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ChangedSourceMissingSourceFileAndInsufficientSpaceAreRefused()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            var service = Service(root);
            // Insufficient disk space is reported in the plan; nothing is created.
            var tight = new TargetPreparationService(Path.Combine(root, "prepared"), Payload()) { FreeSpace = _ => 1 << 20 };
            var full = Plan(tight, exe);
            Assert.Contains(full.Problems, p => p.StartsWith("Insufficient disk space", StringComparison.Ordinal));
            Assert.False((await tight.ExecuteAsync(full)).Success);
            // A source file removed after the plan was shown: execution refuses (the approved plan no longer describes it).
            var plan = Plan(service, exe);
            File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, "TSharpVision.Samples.TVDemo.cfg"));
            var result = await service.ExecuteAsync(plan);
            Assert.False(result.Success);
            Assert.Contains("changed since the plan was shown", result.Message);
            Assert.False(Directory.Exists(Path.Combine(root, "prepared")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task CancellationAndFailuresNeverPublishAndKeepThePreviousDeployment()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            var good = Service(root);
            var first = await good.ExecuteAsync(Plan(good, exe));
            Assert.True(first.Success);
            string app = Path.Combine(root, "prepared", Preparation.ApplicationId(exe));
            string active = File.ReadAllText(Path.Combine(app, TargetPreparationService.ActiveDeployment.FileName));
            // Cancellation after a few files: the staging folder is removed and nothing new is published.
            using var cts = new CancellationTokenSource();
            int files = 0;
            var cancelling = Service(root, hook: _ => { if (++files == 4) cts.Cancel(); });
            var cancelled = await cancelling.ExecuteAsync(Plan(cancelling, exe), null, cts.Token);
            Assert.True(cancelled.Cancelled); Assert.False(cancelled.Success); Assert.True(cancelled.RollbackComplete);
            Assert.Contains("previous prepared copy (Ready) is unchanged", cancelled.Message);
            // A failed copy (I/O error mid-way) and a failure after publishing (verification) leave no deployment behind.
            foreach (string at in new[] { "TSharpVision.dll", "<verify>" })
            {
                var failing = Service(root, hook: p => { if (p == at) throw new IOException("injected failure at " + at); });
                var failed = await failing.ExecuteAsync(Plan(failing, exe));
                Assert.False(failed.Success); Assert.Contains("injected failure", failed.Message);
            }
            Assert.Equal([Path.GetDirectoryName(first.PreparedExecutable!)!], Directory.GetDirectories(app));
            Assert.Equal(active, File.ReadAllText(Path.Combine(app, TargetPreparationService.ActiveDeployment.FileName)));
            Assert.Equal(PreparedState.Ready, Service(root).Status(Path.GetDirectoryName(first.PreparedExecutable!)!, deep: true)!.State);
            // Re-prepare succeeds into a new versioned folder; the previous one is kept, the active reference moves.
            var service = Service(root);
            var second = await service.ExecuteAsync(Plan(service, first.PreparedExecutable!));
            Assert.True(second.Success, second.Message);
            Assert.NotEqual(first.PreparedExecutable, second.PreparedExecutable);
            Assert.True(File.Exists(first.PreparedExecutable));
            Assert.Equal(Path.GetDirectoryName(second.PreparedExecutable!), service.Existing(exe)!.Folder);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task StaleAndCorruptPreparedDeploymentsAreDetected()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            var service = Service(root);
            var result = await service.ExecuteAsync(Plan(service, exe));
            string folder = result.Deployment!, prepared = result.PreparedExecutable!;
            // A changed original: Outdated (still runnable: a warning, not a blocker).
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(exe)!, "TSharpVision.Samples.TVDemo.cfg"), "\n");
            TargetPreparationService.ForgetStatus();
            var outdated = service.Status(folder)!;
            Assert.Equal(PreparedState.Outdated, outdated.State);
            Assert.Contains("the original application changed since preparation", outdated.Reasons);
            var report = TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(prepared), ProxyRun());
            Assert.Contains(report.Findings, f => f.Code == "PREP001" && !f.Blocking);
            Assert.True(report.Ready);
            Assert.Equal(PreparedState.Outdated, service.Status(folder, wanted: PreparationCapabilities.Proxy)!.State);
            // Corruption with the same size and write time: only the deep hash check finds it.
            string testing = Path.Combine(folder, TargetDiagnostics.TestingFile);
            var time = File.GetLastWriteTimeUtc(testing);
            var bytes = File.ReadAllBytes(testing); bytes[^1] ^= 0xff; File.WriteAllBytes(testing, bytes); File.SetLastWriteTimeUtc(testing, time);
            TargetPreparationService.ForgetStatus();
            var deep = service.Status(folder, deep: true)!;
            Assert.Equal(PreparedState.Incomplete, deep.State);
            Assert.Contains(deep.Reasons, r => r.Contains("TSharpWorkbench.Testing.dll was modified"));
            // A missing prepared file: Incomplete, blocking every run.
            string sdl = Path.Combine(folder, "runtimes", ProxyPayload.CurrentRid(), "native", "SDL3.dll");
            Assert.True(File.Exists(sdl));
            File.Delete(sdl);
            TargetPreparationService.ForgetStatus(); TargetDiagnostics.Forget(prepared);
            var blocked = TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(prepared), ProxyRun());
            Assert.Contains(blocked.Findings, f => f.Code == "PREP002" && f.Blocking);
            Assert.False(blocked.Ready);
            // A manifest that names a path outside the deployment is never followed.
            string manifestPath = Path.Combine(folder, PreparationManifest.FileName);
            var manifest = JsonSerializer.Deserialize<PreparationManifest>(File.ReadAllText(manifestPath), Preparation.Json)!;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with { Files = [.. manifest.Files, new("../../outside.txt", 1, "00", 0, "application", "application")] }, Preparation.Json));
            TargetPreparationService.ForgetStatus();
            var escaped = service.Status(folder)!;
            Assert.True(escaped.Reasons.Any(r => r.Contains("outside the deployment")), string.Join("\n", escaped.Reasons));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task DuplicateExecutableNamesDoNotCollideAndLinksAreRefused()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string a = CopyOriginal(root, "a"), b = CopyOriginal(root, "b");
            var service = Service(root);
            Assert.NotEqual(Preparation.ApplicationId(a), Preparation.ApplicationId(b));
            Assert.StartsWith("TSharpVision.Samples.TVDemo-", Preparation.ApplicationId(a));
            Assert.DoesNotContain(Path.GetFileName(root), Preparation.ApplicationId(a));
            var ra = await service.ExecuteAsync(Plan(service, a));
            var rb = await service.ExecuteAsync(Plan(service, b));
            Assert.True(ra.Success && rb.Success);
            Assert.NotEqual(Path.GetDirectoryName(Path.GetDirectoryName(ra.PreparedExecutable)), Path.GetDirectoryName(Path.GetDirectoryName(rb.PreparedExecutable)));
            // A junction (or symbolic link) inside the application folder: refused with an explanation, never followed.
            string linked = CopyOriginal(root, "linked");
            string link = Path.Combine(Path.GetDirectoryName(linked)!, "outside");
            string outside = Path.Combine(root, "outside-target"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
                mklink.WaitForExit();
            }
            if (!Directory.Exists(link)) return; // neither a symbolic link nor a junction could be created
            var plan = Plan(service, linked);
            Assert.Contains(plan.Problems, p => p.Contains("outside is a symbolic link or junction"));
            Assert.DoesNotContain(plan.Entries, e => e.Path.Contains("secret.txt"));
            Directory.Delete(link);
        }
        finally { Delete(root); }
    }

    // ── B. in-place installation ───────────────────────────────────────────────────────

    [Fact]
    public async Task InPlaceInstallationAddsOnlyListedFilesAndPreservesEverythingElse()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            File.WriteAllText(Path.Combine(folder, "user-notes.txt"), "mine");
            var before = Snapshot(folder);
            var service = Service(root);
            var plan = Plan(service, exe, PreparationStrategy.InPlaceInstallation);
            Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
            Assert.Equal(folder, plan.Destination);
            Assert.Equal(2, plan.Changes.Count());
            Assert.Contains(plan.FileLines(), l => l.Contains("the original application folder is modified"));
            var result = await service.ExecuteAsync(plan);
            Assert.True(result.Success, result.Message);
            var after = Snapshot(folder);
            Assert.Equal(before.Count + 2, after.Count);
            Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
            Assert.Equal(service.Payload!.ProxySha256, after[TargetDiagnostics.ProxyFile]);
            Assert.Equal(["added " + Path.Combine(folder, "TSharpWorkbench.Proxy.dll"), "added " + Path.Combine(folder, "TSharpWorkbench.Testing.dll")], result.Changes.Order().ToArray());
            Assert.True(TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), ProxyRun("SDLDriver")).Ready);
            // No stray temporary or probe files; the record says "installed".
            Assert.DoesNotContain(after.Keys, k => k.Contains(".tsw-") || k.Contains("probe"));
            string record = Directory.GetFiles(Path.Combine(root, "prepared"), InstallRecord.FileName, SearchOption.AllDirectories).Single();
            Assert.Equal("installed", JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(record), Preparation.Json)!.State);
            // Nothing to install the second time.
            Assert.False(Plan(service, exe, PreparationStrategy.InPlaceInstallation).Changes.Any());
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task InPlaceReplacementIsBackedUpAndRolledBackOnFailureAndCancellation()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            // An earlier Workbench Proxy (same assembly identity, other content) is replaced, never silently.
            File.Copy(Path.Combine(Payload()!, TargetDiagnostics.ProxyFile), Path.Combine(folder, TargetDiagnostics.ProxyFile));
            File.AppendAllText(Path.Combine(folder, TargetDiagnostics.ProxyFile), "old");
            var before = Snapshot(folder);
            var failing = Service(root, hook: p => { if (p == "<verify>") throw new IOException("injected verification failure"); });
            var plan = Plan(failing, exe, PreparationStrategy.InPlaceInstallation);
            Assert.Contains(plan.Entries, e => e.Action == PlanAction.Replace && e.Path == TargetDiagnostics.ProxyFile);
            var failed = await failing.ExecuteAsync(plan);
            Assert.False(failed.Success); Assert.True(failed.RolledBack); Assert.True(failed.RollbackComplete, failed.Message);
            Assert.Contains("Every change was rolled back", failed.Message);
            Assert.Equal(before, Snapshot(folder));
            string backup = Directory.GetFiles(Path.Combine(root, "prepared"), TargetDiagnostics.ProxyFile, SearchOption.AllDirectories).First(f => f.Contains("backup"));
            Assert.Equal(before[TargetDiagnostics.ProxyFile], Preparation.FileHash(backup));
            // Cancellation during publishing (after the first installed file): rolled back as well.
            using var cts = new CancellationTokenSource();
            var cancelling = Service(root, hook: p => { if (p == TargetDiagnostics.TestingFile || p == TargetDiagnostics.ProxyFile && File.Exists(Path.Combine(folder, TargetDiagnostics.TestingFile))) cts.Cancel(); });
            var cancelled = await cancelling.ExecuteAsync(Plan(cancelling, exe, PreparationStrategy.InPlaceInstallation), null, cts.Token);
            Assert.True(cancelled.Cancelled); Assert.True(cancelled.RollbackComplete, cancelled.Message);
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void RollbackNeverDeletesFilesModifiedAfterInstallation()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string folder = Path.Combine(root, "app"), work = Path.Combine(root, "work");
            Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.Combine(work, "backup"));
            File.WriteAllText(Path.Combine(folder, "added.dll"), "installed");
            File.WriteAllText(Path.Combine(folder, "changed.dll"), "someone else");
            File.WriteAllText(Path.Combine(folder, "replaced.dll"), "installed2");
            File.WriteAllText(Path.Combine(work, "backup", "replaced.dll"), "original");
            string H(string text) => Preparation.Hash(text);
            var record = new InstallRecord
            {
                Id = "t", TargetDirectory = folder,
                Files = [new("added.dll", "added", H("installed"), null, null, true), new("changed.dll", "added", H("installed by workbench"), null, null, true),
                    new("replaced.dll", "replaced", H("installed2"), H("original"), Path.Combine("backup", "replaced.dll"), true)]
            };
            var (complete, notes) = TargetPreparationService.Rollback(record, work);
            Assert.False(complete);
            Assert.False(File.Exists(Path.Combine(folder, "added.dll")));
            Assert.Equal("someone else", File.ReadAllText(Path.Combine(folder, "changed.dll")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(folder, "replaced.dll")));
            Assert.Contains(notes, n => n.Contains("changed.dll was modified after installation"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task InPlacePermissionFailuresAndProtectedLocationsAreReportedClearly()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            Assert.Contains("protected location", Preparation.ProtectedLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "App")));
            Assert.Contains("macOS application bundle", Preparation.ProtectedLocation(Path.Combine(root, "My.app", "Contents", "MacOS")));
            Assert.Null(Preparation.ProtectedLocation(root));
            if (!OperatingSystem.IsWindows()) return; // the ACL-based permission check is Windows-only
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var service = Service(root);
            var plan = Plan(service, exe, PreparationStrategy.InPlaceInstallation);
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData, System.Security.AccessControl.AccessControlType.Deny);
            acl.AddAccessRule(deny); info.SetAccessControl(acl);
            try
            {
                var result = await service.ExecuteAsync(plan);
                Assert.False(result.Success);
                Assert.Contains("not writable", result.Message);
                Assert.Contains("Nothing in the application folder was changed", result.Message);
            }
            finally { acl.RemoveAccessRule(deny); info.SetAccessControl(acl); }
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    // ── C. UI ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task PrepareTargetFromTheWorkspaceUsesThePreparedCopyExplicitly(int width, int height)
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyOriginal(root);
            string original = Snapshot(Path.GetDirectoryName(exe)!).Count.ToString();
            string about = Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios", "about.scenario.json");
            string menu = Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios", "menu.scenario.json");
            string projectPath = Path.Combine(root, "tvdemo.twproj");
            new WorkbenchProject { Name = "TVDemo", Target = exe, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [about, menu], SelectedIds = ["tvdemo/about"],
                ScreenSizes = ["80x25"], ProxyBackend = "SDLDriver" }.Save(projectPath);
            string projectText = File.ReadAllText(projectPath);
            string grid = $"{width}x{height}";
            await Drive(width, height, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count == 2 && p.ProjectPath == projectPath ? p : null, 60);
                await bridge.Invoke(() =>
                {
                    Assert.Contains("TSharpVision.Samples.TVDemo.exe | Proxy missing | Prepare target... |", main.HeaderText);
                    Assert.Equal(["tvdemo/about"], main.TestList.MarkedIds.ToArray());
                });
                await Shot(bridge, $"workspace-proxy-missing-{grid}", "Workspace: Proxy missing, Prepare target offered");
                Send(WorkbenchDialog.PrepareTargetCommand);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(0u, dialog.Method.value);   // Prepared copy is the default
                    Assert.Equal(PreparationStrategy.PreparedCopy, dialog.Strategy);
                    Assert.Contains("Proxy missing (PROXY001)", dialog.Text);
                    Assert.Contains("Framework            Compatible", dialog.Text);
                    Assert.Contains("Preparation does not modify the original application.", dialog.NoticeText);
                    Assert.False(dialog.PrepareButton.GetState(Views.sfDisabled));
                    Assert.True(dialog.UseExistingButton.GetState(Views.sfDisabled));
                    Assert.Empty(ProductQualificationTests.DuplicateMnemonics(dialog));
                    Assert.True(dialog.origin.y + dialog.size.y <= app.DeskTop!.size.y && dialog.origin.x + dialog.size.x <= app.DeskTop.size.x);
                    // Enter on the method does not prepare (no default button).
                });
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(dialog, app.DeskTop!.TopView()); Assert.False(Directory.Exists(Path.Combine(root, "prepared"))); });
                await Shot(bridge, $"prepare-target-prepared-copy-{grid}", "Prepare Target: Prepared copy (default)");
                // Tab order = reading order.
                var order = new List<string>();
                for (int i = 0; i < 9; i++)
                {
                    await bridge.Invoke(() => order.Add(dialog.current switch { TButton b => b.Title, { } v when v == dialog.Method => "method", { } v when v == dialog.Components => "components", { } v when v == dialog.DestinationField => "destination", _ => "?" }));
                    Press(Keys.kbTab); await Settle(bridge);
                }
                // The destination field and its Browse / New folder buttons follow the mode chooser.
                Assert.Equal(["method", "destination", "~B~rowse...", "~N~ew folder...", "components", "Show ~f~iles...", "~P~repare", "Cancel", "method"], order);
                // The in-place mode is "Fix the original target", one in-place policy shared
                // with Fix Issues. Choosing it hands over to the Fix window (visibly marked as changing the original); nothing changes.
                await bridge.Invoke(() => { dialog.Method.value = 1; Nudge(dialog); });
                var fixMode = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(1u, fixMode.Mode.value);
                    Assert.Contains(fixMode.Rows, r => r.StartsWith("FIX") && r.Contains(TargetDiagnostics.ProxyFile));
                });
                await Shot(bridge, $"prepare-target-in-place-{grid}", "Fix the original target (in place) chosen from Prepare Target");
                Assert.Equal(original, Snapshot(Path.GetDirectoryName(exe)!).Count.ToString());
                // Back to Prepare a copy: a fresh Prepare Target window for the same executable.
                await bridge.Invoke(() => { fixMode.Mode.value = 0; Nudge(fixMode); });
                dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() => Assert.Equal(0u, dialog.Method.value));
                // Full file list.
                Press(Keys.kbAltF);
                var files = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchPrepareTargetDialog.FilesTitle } d ? d : null);
                await bridge.Invoke(() =>
                {
                    var list = Children(files).OfType<TListBox>().Single();
                    string all = string.Join("\n", Enumerable.Range(0, list.range).Select(i => list.GetText(i, 300)));
                    Assert.Contains("ADD      TSharpWorkbench.Proxy.dll", all);
                    Assert.Contains("COPY     TSharpVision.Samples.TVDemo.exe", all);
                });
                await Shot(bridge, $"prepare-target-files-{grid}", "Preparation file plan");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() == dialog);
                // Prepare: progress, success, then the explicit "use the prepared copy" choice.
                Press(Keys.kbAltP);
                var progress = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPreparationProgressDialog);
                await Until(bridge, () => progress.Result is not null, 120);
                await bridge.Invoke(() => { Assert.True(progress.Result!.Success); Assert.Contains("Prepared and verified", progress.Text); });
                await Shot(bridge, $"prepared-successfully-{grid}", "Prepared successfully");
                Press(Keys.kbEnter);
                var use = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: "Use Prepared Target" } d ? d : null);
                await Shot(bridge, $"use-prepared-target-{grid}", "Use prepared target confirmation (affirmative default)");
                Press(Keys.kbEnter);
                await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
                var prepared = await Wait(bridge, () => main.CaptureProject().Target is { } t && t != exe && !main.Busy && main.TestList.Strings.Count == 2 ? t : null, 60);
                await bridge.Invoke(() =>
                {
                    Assert.StartsWith(Path.Combine(root, "prepared"), prepared);
                    Assert.True(main.Dirty);
                    Assert.Equal([about, menu], main.CaptureProject().ScenarioFiles);
                    Assert.Equal(["tvdemo/about"], main.TestList.MarkedIds.ToArray());
                    Assert.Contains("| Prepared: Ready | Ready |", main.HeaderText);
                });
                await Shot(bridge, $"prepared-target-ready-{grid}", "Workspace: prepared target ready");
                // The prepared copy becomes outdated when the original changes; the header and Prepare Target say so.
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(exe)!, "TSharpVision.Samples.TVDemo.cfg"), "\n");
                TargetPreparationService.ForgetStatus();
                await bridge.Invoke(() => main.RefreshHeader());
                await Until(bridge, () => main.HeaderText.Contains("Prepared: Outdated"));
                Send(WorkbenchDialog.PrepareTargetCommand);
                var again = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() => Assert.Contains("Prepared target: Outdated - the original application changed since preparation", again.Text));
                await Shot(bridge, $"prepared-target-outdated-{grid}", "Prepare Target on an outdated prepared copy (Re-prepare)");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPrepareTargetDialog);
            });
            // Nothing was saved: the project file is unchanged.
            Assert.Equal(projectText, File.ReadAllText(projectPath));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task RunConfigurationOffersPrepareAndResumesTheDraftWithoutRunning()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyOriginal(root);
            string about = Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios", "about.scenario.json");
            string projectPath = Path.Combine(root, "tvdemo.twproj");
            new WorkbenchProject { Name = "TVDemo", Target = exe, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [about], SelectedIds = ["tvdemo/about"],
                ScreenSizes = ["80x25"], ProxyBackend = "SDLDriver" }.Save(projectPath);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count == 1 && p.ProjectPath == projectPath ? p : null, 60);
                string session = "";
                await bridge.Invoke(() => session = main.SessionRun.Fingerprint());
                Press(Keys.kbF5);
                var basic = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunBasicDialog);
                await bridge.Invoke(() =>
                {
                    Assert.True(basic.RunButton.GetState(Views.sfDisabled));
                    Assert.True(basic.PrepareButton.GetState(Views.sfVisible));
                    Assert.Empty(ProductQualificationTests.DuplicateMnemonics(basic));
                    // A draft change that must survive preparation.
                    basic.Sizes.value = 1 | 4; basic.CustomSizes.SetData("100x30"); basic.Mode.value = 0; Nudge(basic);
                });
                await Shot(bridge, "basic-blocked-prepare-140x40", "Run Tests blocked: Proxy missing, Prepare offered");
                Press(Keys.kbAltP);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                Press(Keys.kbAltP);
                var progress = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPreparationProgressDialog);
                await Until(bridge, () => progress.Result is not null, 120);
                Press(Keys.kbEnter);
                await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: "Use Prepared Target" } d ? d : null);
                Press(Keys.kbEnter);
                // Run Tests reopens by itself once the catalog of the prepared copy has loaded, with the draft intact.
                var resumed = await Wait(bridge, () => app.DeskTop!.TopView() is WorkbenchRunBasicDialog b && b != basic ? b : null, 60);
                await bridge.Invoke(() =>
                {
                    Assert.Equal((5u, "100x30"), (resumed.Sizes.value, resumed.CustomSizes.Data));
                    Assert.False(resumed.RunButton.GetState(Views.sfDisabled), resumed.SummaryText);
                    Assert.False(resumed.PrepareButton.GetState(Views.sfVisible));
                    Assert.Contains("Target: TSharpVision.Samples.TVDemo.exe - Ready", resumed.SummaryText);
                    Assert.False(main.Busy);   // never runs by itself
                    Assert.Equal(session, main.SessionRun.Fingerprint());
                    Assert.Equal(["tvdemo/about"], main.TestList.MarkedIds.ToArray());
                });
                await Shot(bridge, "basic-after-preparation-140x40", "Run Tests after preparation: draft kept, ready, not started");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunBasicDialog);
                Assert.False(Directory.Exists(Path.Combine(root, "runs")));
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ProgressIsResponsiveAndCancellationLeavesNothingReady()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyOriginal(root);
            string projectPath = Path.Combine(root, "tvdemo.twproj");
            new WorkbenchProject { Name = "TVDemo", Target = exe, OutputRoot = Path.Combine(root, "runs"),
                ScenarioFiles = [Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios", "about.scenario.json")] }.Save(projectPath);
            using var gate = new ManualResetEventSlim(false);
            int written = 0;
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == projectPath ? p : null, 60);
                // The service blocks after five files until the test releases it: the UI must stay responsive meanwhile.
                await bridge.Invoke(() => TargetDiagnostics.Preparation = new TargetPreparationService(Path.Combine(root, "prepared"), Payload())
                { BeforeWrite = _ => { if (Interlocked.Increment(ref written) == 6) gate.Wait(TimeSpan.FromSeconds(60)); } });
                Send(WorkbenchDialog.PrepareTargetCommand);
                var dialog = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                Press(Keys.kbAltP);
                var progress = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPreparationProgressDialog);
                await Until(bridge, () => progress.Text.Contains("Copying files: 5 of"));
                await bridge.Invoke(() =>
                {
                    Assert.True(progress.Running);
                    Assert.Contains("Transferred:", progress.Text);
                    Assert.True(progress.Bar.Value < progress.Bar.Maximum);
                });
                await Shot(bridge, "preparation-progress-140x40", "Preparation progress (TProgressBar, files and bytes)");
                // Esc cancels (and waits for the cleanup); the work is released only afterwards.
                Press(Keys.kbEsc); await Settle(bridge);
                await bridge.Invoke(() => Assert.Contains("cancelling", progress.Text));
                gate.Set();
                await Until(bridge, () => progress.Result is not null, 60);
                await bridge.Invoke(() =>
                {
                    Assert.True(progress.Result!.Cancelled);
                    Assert.Contains("Preparation cancelled", progress.Text);
                    Assert.True(progress.Bar.Value < progress.Bar.Maximum);
                });
                await Shot(bridge, "preparation-cancelled-140x40", "Preparation cancelled: staging removed, original unchanged");
                Press(Keys.kbEnter);
                await Until(bridge, () => app.DeskTop!.TopView() == dialog);
                await bridge.Invoke(() => Assert.Contains("Preparation cancelled.", dialog.NotesText));
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() => { Assert.Equal(exe, main.CaptureProject().Target); Assert.False(main.Dirty); });
            });
            string app = Path.Combine(root, "prepared", Preparation.ApplicationId(exe));
            Assert.Empty(Directory.Exists(app) ? Directory.GetDirectories(app) : []);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task PropertiesOfferPrepareContextuallyAndCompatibilityConflictsAreShown()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyOriginal(root);
            string bad = CopyOriginal(root, "bad");
            // Another declared framework version (another build of the same version would be supported).
            string badDeps = Path.ChangeExtension(bad, ".deps.json");
            string deployed = TargetDiagnostics.Inspect(bad).FrameworkPackageVersion!;
            string originalDeps = File.ReadAllText(badDeps);
            string conflictingDeps = originalDeps.Replace("\"TSharpVision/" + deployed + "\"", "\"TSharpVision/2.0.0-other\"")
                .Replace("\"TSharpVision\": \"" + deployed + "\"", "\"TSharpVision\": \"2.0.0-other\"");
            Assert.NotEqual(originalDeps, conflictingDeps);
            File.WriteAllText(badDeps, conflictingDeps);
            string projectPath = Path.Combine(root, "tvdemo.twproj");
            new WorkbenchProject { Name = "TVDemo", Target = exe, OutputRoot = Path.Combine(root, "runs"), SelectedIds = ["tvdemo/about"],
                ScenarioFiles = [Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios", "about.scenario.json")] }.Save(projectPath);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == projectPath && p.TestList.Strings.Count == 1 ? p : null, 60);
                Press(Keys.kbAltO);
                var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: "Project Properties" } d ? d : null);
                await bridge.Invoke(() =>
                {
                    Assert.False(props.PrepareButton.GetState(Views.sfDisabled));
                    Assert.Empty(ProductQualificationTests.DuplicateMnemonics(props));
                    // A compiled diagnostic target needs no preparation: unavailable.
                    props.ExecutableField.SetData(TestWorkbenchHostTests.SampleTarget); Nudge(props);
                    Assert.True(props.PrepareButton.GetState(Views.sfDisabled));
                    // Another declared framework version (FW002): Prepare stays reachable to explain why it refuses.
                    props.ExecutableField.SetData(bad); Nudge(props);
                    Assert.False(props.PrepareButton.GetState(Views.sfDisabled));
                });
                Press(Keys.kbAltA);
                var conflict = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() =>
                {
                    Assert.True(conflict.PrepareButton.GetState(Views.sfDisabled));
                    Assert.Equal(PreparationVerdict.RequiresRebuild, conflict.OutcomeText);
                    Assert.Contains("2.0.0-other", conflict.NotesText);
                    Assert.Contains("Framework            Version mismatch", conflict.Text);
                });
                await Shot(bridge, "compatibility-conflict-140x40", "Prepare Target: different framework version, Prepare unavailable");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() == props);
                // The original: prepared from Properties; the field takes the prepared copy, OK applies it, marks kept.
                await bridge.Invoke(() => { props.ExecutableField.SetData(exe); Nudge(props); });
                Press(Keys.kbAltA);
                await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                Press(Keys.kbAltP);
                var progress = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPreparationProgressDialog);
                await Until(bridge, () => progress.Result is not null, 120);
                Press(Keys.kbEnter);
                await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: "Use Prepared Target" } d ? d : null);
                Press(Keys.kbEnter);
                await Until(bridge, () => app.DeskTop!.TopView() == props);
                await bridge.Invoke(() =>
                {
                    Assert.StartsWith(Path.Combine(root, "prepared"), props.ExecutableField.Data);
                    Assert.Contains("Prepared copy selected. OK applies it", props.NoticeText);
                    Assert.False(main.Dirty);
                });
                Press(Keys.kbEnter);   // OK
                await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPropertiesDialog);
                await Wait(bridge, () => !main.Busy && main.TestList.Strings.Count == 1 && main.CaptureProject().Target != exe ? "" : null, 60);
                await bridge.Invoke(() => { Assert.True(main.Dirty); Assert.Equal(["tvdemo/about"], main.TestList.MarkedIds.ToArray()); });
                // Switching back to the original (another deployment of the same application) keeps the marks too.
                Press(Keys.kbAltO);
                props = (WorkbenchPropertiesDialog)await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: "Project Properties" } d ? d : null);
                await bridge.Invoke(() => { props.ExecutableField.SetData(exe); Nudge(props); });
                Press(Keys.kbEnter);
                await Wait(bridge, () => !main.Busy && main.CaptureProject().Target == exe && main.TestList.Strings.Count == 1 ? "" : null, 60);
                await bridge.Invoke(() => Assert.Equal(["tvdemo/about"], main.TestList.MarkedIds.ToArray()));
            });
        }
        finally { Delete(root); }
    }

    // ── harness (as TargetDiagnosticsUxTests) ─────────────────────────────────────────────────────────

    private sealed class SyncProgress(Action<PreparationProgress> report) : IProgress<PreparationProgress> { public void Report(PreparationProgress value) => report(value); }

    private static async Task Drive(int width, int height, string root, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string target = TestWorkbenchHostTests.SampleTarget, output = Path.Combine(root, "runs-sample"), settings = Path.Combine(root, "settings.json");
        new WorkbenchPreferences(target, output, null, "80x25") { RunView = WorkbenchPreferences.BasicView, PreparedTargetsRoot = Path.Combine(root, "prepared") }.Save(settings);
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = settings });
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try
            {
                await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
                await body(app, bridge); await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
            }
            catch (Exception ex)
            {
                failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                await bridge.Invoke(() =>
                {
                    for (int i = 0; i < 6 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                    app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                }).WaitAsync(TimeSpan.FromSeconds(10));
            }
        });
        AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(30)), "Worker did not stop.");
        Assert.Null(failure);
    }
    private static async Task Shot(Bridge bridge, string name, string state)
    {
        if (Environment.GetEnvironmentVariable("TSWB_DEPLOYMENT_EVIDENCE") is not { Length: > 0 } root) return;
        await Settle(bridge);
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, captureType = "logical", driver = "NullDriver", grid = $"{screen.Width}x{screen.Height}", test = nameof(TvDemoDeploymentUxTests),
                expectedState = state, renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    private static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(60); await bridge.Invoke(() => { }); }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
