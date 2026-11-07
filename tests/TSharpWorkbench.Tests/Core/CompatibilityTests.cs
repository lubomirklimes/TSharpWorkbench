using System.Diagnostics;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Independent preparation/repair regression fixtures. Matching-major frameworks are eligible for runtime
// verification; hashes and minor versions are diagnostic information. Optional fixture variants here are
// not prerequisites for the single real TVDemo release gate.
[Collection("NonParallel")]
public sealed class CompatibilityTests
{
    private const string TvDemoExe = "TSharpVision.Samples.TVDemo.exe";

    // ── environment ──────────────────────────────────────────────────────────────────

    internal static string? LocalTvDemo()
    {
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable("TSWB_TVDEMO_LOCAL"),
            Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "artifacts", "tsw-product-3b", "targets", "tvdemo-local", TvDemoExe) })
            if (candidate is { Length: > 0 } && File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
    // The public TSharpVision 1.0.0 assembly (a real, older framework build) from the NuGet package cache.
    internal static string? OlderFramework()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "tsharpvision", "1.0.0", "lib", "net10.0", "TSharpVision.dll");
        return File.Exists(path) ? path : null;
    }
    internal static bool Available(string test, bool ordinary = true, bool local = false, bool older = false)
    {
        var missing = new List<string>();
        if (TvDemoDeploymentUxTests.Payload() is null) missing.Add("payload (TSWB_PROXY_PAYLOAD)");
        if (ordinary && FixIssuesTests.Original() is null) missing.Add("ProxyOrdinary (TSWB_PRODUCT_TARGET)");
        if (local && LocalTvDemo() is null) missing.Add("TVDemo local framework build (TSWB_TVDEMO_LOCAL)");
        if (older && OlderFramework() is null) missing.Add("TSharpVision 1.0.0 in the NuGet package cache");
        if (missing.Count > 0)
        {
            if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_SKIPS") is { Length: > 0 } skips)
                File.AppendAllText(skips, $"{test}: skipped, missing {string.Join(", ", missing)}{Environment.NewLine}");
            return false;
        }
        Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, TvDemoDeploymentUxTests.Payload());
        return true;
    }
    internal static string Temp() => Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "artifacts", "tsw-product-3b", "t", Guid.NewGuid().ToString("N")[..8]);
    // A disposable copy: top-level files without PDB/XML, Help/ and runtimes/<this RID>/.
    internal static string Copy(string exe, string root, string name)
    {
        string source = Path.GetDirectoryName(exe)!, target = Path.Combine(root, name);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
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
        return Path.Combine(target, Path.GetFileName(exe));
    }
    internal static string Ordinary(string root, string name = "app") => Copy(FixIssuesTests.Original()!, root, name);
    internal static void Delete(string root)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var d in new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.AllDirectories)) d.Attributes &= ~FileAttributes.ReadOnly;
                    foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)) f.IsReadOnly = false;
                    Directory.Delete(root, true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
    internal static TargetPreparationService Service(string root, Action<string>? hook = null)
    {
        var service = new TargetPreparationService(Path.Combine(root, "prepared"), TvDemoDeploymentUxTests.Payload()) { BeforeWrite = hook };
        TargetDiagnostics.Preparation = service;
        TargetPreparationService.ForgetStatus();
        return service;
    }
    internal static Dictionary<string, string> Snapshot(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(folder, f), Preparation.FileHash, StringComparer.OrdinalIgnoreCase);
    internal static TargetReport Diagnose(string exe)
    {
        TargetDiagnostics.Forget(exe);
        return TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), new DiagnosticContext(true, false, "NullDriver", null, null, Run: true));
    }
    private static TargetRepairService Repair(TargetPreparationService service) => new(service) { IsRunning = _ => false };
    internal static void Evidence(string name, IEnumerable<string> lines)
    {
        if (Environment.GetEnvironmentVariable("TSWB_COMPAT_EVIDENCE") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); File.WriteAllLines(Path.Combine(dir, name), lines);
    }
    private static int Running(string exe) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Count(p => { using (p) { try { return WorkbenchPreferences.SamePath(p.MainModule?.FileName, exe); } catch (Exception) { return false; } } });
    private static ScenarioDescriptor[] Scenarios(params string[] files) =>
        ExternalScenarios.Merge([], files.Select(ExternalScenarios.Load).ToArray()).ToArray();
    private static string[] Lines(RepairPlan plan) =>
        [$"summary: {plan.Summary}", $"counts: {plan.Counts}", $"other build {plan.OtherBuild}, can repair {plan.CanRepair}",
            .. plan.Items.Select(i => $"{RepairItem.Label(i.State),-10} {i.Code,-9} {i.Name,-34} {i.Summary} | {i.Conflict}"),
            .. plan.Blockers.Select(b => "! " + b), .. plan.Notes.Select(n => "note: " + n)];
    // A ProxyOrdinary copy whose TSharpVision.dll is another build of the same version (bytes appended; metadata intact).
    internal static string OtherBuild(string root, string name = "other")
    {
        string exe = Ordinary(root, name);
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(exe)!, "TSharpVision.dll"), "x");
        return exe;
    }
    private static async Task<(bool Ok, string Line)> Verify(string exe)
    {
        try
        {
            var v = await new ScenarioRunner().VerifyAsync(exe, true);
            return (v.Proxy && v.Cleanup, $"verify: connected {v.Identity.Assembly}, proxy={v.Proxy}, stop={v.StopOutcome}, cleanup={v.Cleanup}, {v.Duration.TotalSeconds:F1} s");
        }
        catch (TargetVerificationException ex) { return (false, $"verify FAILED: {StartupFailure.Classify(ex.Message).Reason} (stop {ex.StopOutcome}, cleanup {ex.Cleanup}) | {ex.Message}"); }
    }

    // ── the policy itself ───────────────────────────────────────────────────────────────

    [Fact]
    public void MajorDecidesAndBuildMetadataIsInformationOnly()
    {
        AssemblyFacts Facts(string informational, Version version) => new("TSharpVision", "", version, informational, new Dictionary<string, Version?>());
        var expected = Facts("1.2.0+reference", new Version(1, 2, 0, 0));
        foreach (string version in new[] { "1.0.0", "1.1.0", "1.2.1", "1.9.7", "1.2.0-preview.1" })
        {
            var identity = Version.Parse(version.Split('-')[0] + ".0");
            var comparison = FrameworkCompatibility.Compare(Facts(version, identity), version, expected, "1.2.0", "a", "b");
            Assert.True(comparison.Compatible, comparison.Reason);
            Assert.Equal(FrameworkMatch.SameMajor, comparison.Match);
        }
        Assert.Equal(FrameworkMatch.SameVersion, FrameworkCompatibility.Compare(Facts("1.2.0+different", expected.Version!), "1.2.0", expected, "1.2.0", "a", "b").Match);
        Assert.Equal(FrameworkMatch.Identical, FrameworkCompatibility.Compare(Facts("1.2.0", expected.Version!), "1.2.0", expected, "1.2.0", "same", "same").Match);
        Assert.False(FrameworkCompatibility.Compare(Facts("2.0.0", new Version(2, 0, 0, 0)), "2.0.0", expected, "1.2.0", "a", "b").Compatible);
        Assert.False(FrameworkCompatibility.Compare(Facts("1.2.0", expected.Version!), "2.0.0", expected, "1.2.0", "a", "b").Compatible);
        Assert.Contains("does not guarantee", FrameworkCompatibility.Policy);
        Assert.Contains("never replaced", FrameworkCompatibility.Policy);
    }

    // ── same version, same hash ──────────────────────────────────────────────────────────

    [Fact]
    public async Task IdenticalPublicBuildFixCopiesOnlyMissingFilesVerifiesAndRunsAJsonScenario()
    {
        if (!Available(nameof(IdenticalPublicBuildFixCopiesOnlyMissingFilesVerifiesAndRunsAJsonScenario))) return;
        string root = Temp();
        try
        {
            string exe = Ordinary(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var service = Service(root);
            var report = Diagnose(exe);
            Assert.DoesNotContain(report.Findings, f => f.Code is "FW002" or "FW003");
            var plan = Repair(service).Plan(exe, report);
            Assert.True(plan.CanRepair, string.Join("\n", plan.Blockers));
            Assert.False(plan.OtherBuild);
            Assert.Equal(FrameworkMatch.Identical, plan.Install!.Framework!.Match);
            Assert.DoesNotContain(plan.Items, i => i.State == RepairState.Refused);
            var outcome = await Repair(service).RepairAsync(plan, Diagnose);
            Assert.True(outcome.Success, outcome.Message);
            var after = Snapshot(folder);
            Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
            Assert.Equal(before.Count + plan.Count(RepairState.Fix), after.Count);
            var (ok, line) = await Verify(exe);
            Assert.True(ok, line);
            var batch = await new ScenarioRunner().RunAsync(exe, Scenarios(Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "diagnostics", "ProxyOrdinary", "counter.scenario.json")),
                new RunOptions(Path.Combine(root, "runs"), ["80x25"]) { ProxyBackend = "NullDriver" });
            var r = Assert.Single(batch.Results);
            Assert.Equal(ScenarioOutcomes.Passed, r.Outcome);
            Assert.Equal(0, Running(exe));
            Evidence("identical-fix-verify-scenario.txt", [.. Lines(plan), "repair: " + outcome.Message, line, $"scenario {r.ScenarioId}: {r.Outcome} ({r.Checkpoints.Length} checkpoints)"]);
        }
        finally { Delete(root); }
    }

    // ── same version, different hash ────────────────────────────────────────────────────────

    [Fact]
    public async Task SameVersionOtherBuildIsFixedAndPreparedWithoutAcknowledgmentAndVerified()
    {
        if (!Available(nameof(SameVersionOtherBuildIsFixedAndPreparedWithoutAcknowledgmentAndVerified))) return;
        string root = Temp();
        try
        {
            string exe = OtherBuild(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var service = Service(root);
            var report = Diagnose(exe);
            var fw = Assert.Single(report.Findings, f => f.Code == "FW003");
            Assert.False(fw.Blocking);
            Assert.Equal(DiagnosticSeverity.Info, fw.Severity);
            Assert.DoesNotContain(report.Findings, f => f.Code == "FW002");
            Assert.True(TargetRepairService.Relevant(report));

            // Fix (in place) and Prepare (copy) use the same policy: both are offered, no acknowledgment exists.
            var fix = Repair(service).Plan(exe, report);
            Assert.True(fix.CanRepair, string.Join("\n", fix.Blockers));
            Assert.True(fix.OtherBuild);
            Assert.Contains(fix.Items, i => i.State == RepairState.Ok && i.Name == "TSharpVision.dll");
            Assert.All(fix.Install!.Changes, e => Assert.Equal(PlanAction.Add, e.Action));
            Assert.DoesNotContain(fix.Install.Changes, e => e.Path == "TSharpVision.dll");
            var copy = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, TargetPreparationService.DefaultCapabilities);
            Assert.True(copy.CanExecute, string.Join("\n", copy.Problems));
            Assert.Equal(PreparationVerdict.SameVersion, copy.Components.Single(c => c.Name == "Framework").State);
            Assert.Contains("Verify target confirms", PreparationVerdict.For(copy, service.Payload!.FrameworkVersion).Next);
            Assert.Equal(before, Snapshot(folder));   // planning is read-only

            // The prepared copy: the application's own framework bytes are copied, the Proxy added, Verify connects.
            var prepared = await service.ExecuteAsync(copy);
            Assert.True(prepared.Success, prepared.Message);
            string preparedDir = Path.GetDirectoryName(prepared.PreparedExecutable!)!;
            Assert.Equal(Preparation.FileHash(Path.Combine(folder, "TSharpVision.dll")), Preparation.FileHash(Path.Combine(preparedDir, "TSharpVision.dll")));
            var (copyOk, copyLine) = await Verify(prepared.PreparedExecutable!);
            Assert.True(copyOk, copyLine);
            Assert.Equal(before, Snapshot(folder));   // the original is only read

            // Fix the original: only added files; nothing existing changes; Verify and a JSON scenario pass.
            var outcome = await Repair(service).RepairAsync(fix, Diagnose);
            Assert.True(outcome.Success, outcome.Message);
            var after = Snapshot(folder);
            Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
            // The core is never added or replaced; a missing driver of the same version (TSharpVision.Drivers.SDL) may be added.
            Assert.DoesNotContain(after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase), a => a.Equals("TSharpVision.dll", StringComparison.OrdinalIgnoreCase));
            var (ok, line) = await Verify(exe);
            Assert.True(ok, line);
            var batch = await new ScenarioRunner().RunAsync(exe, Scenarios(Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "diagnostics", "ProxyOrdinary", "counter.scenario.json")),
                new RunOptions(Path.Combine(root, "runs"), ["80x25"]) { ProxyBackend = "NullDriver" });
            Assert.Equal(ScenarioOutcomes.Passed, Assert.Single(batch.Results).Outcome);
            Assert.Equal(0, Running(exe));
            Evidence("same-version-other-build.txt", [$"FW003 ({fw.Severity}, blocking {fw.Blocking}): {fw.Message}", .. Lines(fix), "prepare: " + prepared.Message, "copy " + copyLine,
                "repair: " + outcome.Message, "original " + line, $"scenario: {batch.Results[0].Outcome}"]);
        }
        finally { Delete(root); }
    }

    // ── same version from a ProjectReference build (the owner's local TVDemo) ──────────────────────────

    [Fact]
    public async Task OwnerTvDemoProjectReferenceBuildOfTheSameVersionIsFixedVerifiedAndRunsAScenario()
    {
        if (!Available(nameof(OwnerTvDemoProjectReferenceBuildOfTheSameVersionIsFixedVerifiedAndRunsAScenario), ordinary: false, local: true)) return;
        string root = Temp();
        try
        {
            string exe = Copy(LocalTvDemo()!, root, "tvdemo-local");
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var service = Service(root);
            var payload = service.Payload!;
            var framework = TargetDiagnostics.Inspect(exe).Framework!;
            Assert.True(PreparationVerdict.ProjectReferenced(Path.ChangeExtension(exe, ".deps.json"), "TSharpVision"));
            Assert.NotEqual(payload.FrameworkSha256, Preparation.FileHash(framework.Path));
            if (!FrameworkCompatibility.SameMajor(FrameworkCompatibility.Declared(framework), payload.FrameworkVersion))
            {
                // A snapshot outside the supported major remains refused (never by hash).
                Assert.False(Repair(service).Plan(exe, Diagnose(exe)).CanRepair);
                Evidence("tvdemo-local.txt", [$"target {exe}: declared {FrameworkCompatibility.Declared(framework)}, refused (other version)"]);
                return;
            }
            var report = Diagnose(exe);
            Assert.DoesNotContain(report.Findings, f => f.Component == "Framework" && f.Blocking);
            var fix = Repair(service).Plan(exe, report);
            List<string> lines = [$"target {exe}", $"framework {framework.Display}, SHA-256 {Preparation.FileHash(framework.Path)[..12]} (payload {payload.FrameworkSha256[..12]})", .. Lines(fix)];
            Assert.True(fix.CanRepair, string.Join("\n", fix.Blockers));
            Assert.True(fix.OtherBuild);
            Assert.Contains(fix.Items, i => i.State == RepairState.Ok && i.Name == "TSharpVision.Drivers.SDL.dll");
            Assert.Contains(fix.Items, i => i.State == RepairState.Fix && i.Name == TargetDiagnostics.ProxyFile);
            Assert.All(fix.Install!.Changes, e => Assert.Equal(PlanAction.Add, e.Action));
            var outcome = await Repair(service).RepairAsync(fix, Diagnose);
            lines.AddRange(["", "repair: " + outcome.Message, .. outcome.Changes.Select(c => "  " + c)]);
            Assert.True(outcome.Success, outcome.Message);
            Assert.NotNull(outcome.Record);
            var after = Snapshot(folder);
            Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
            var (ok, line) = await Verify(exe);
            lines.Add(line);
            Assert.True(ok, line);
            string scenarios = Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios");
            var batch = await new ScenarioRunner().RunAsync(exe, Scenarios(Path.Combine(scenarios, "about.scenario.json")), new RunOptions(Path.Combine(root, "runs"), ["80x25"]) { ProxyBackend = "NullDriver" });
            lines.AddRange(batch.Results.Select(r => $"scenario {r.ScenarioId} {r.Size}: {r.Outcome} ({r.Checkpoints.Length} checkpoints) {r.Reason}"));
            Assert.Equal(ScenarioOutcomes.Passed, Assert.Single(batch.Results).Outcome);
            Assert.Equal(0, Running(exe));
            // Undo (what a failed Fix and verify triggers): the folder is exactly as before; a second undo removes nothing.
            var (complete, notes) = TargetPreparationService.UndoInstall(outcome.Record!, "test undo");
            lines.Add($"undo: complete={complete} {string.Join(" ", notes)}");
            Assert.True(complete, string.Join("\n", notes));
            Assert.Equal(before, Snapshot(folder));
            Assert.False(TargetPreparationService.UndoInstall(outcome.Record!, "again").Complete);
            Evidence("tvdemo-local.txt", lines);
        }
        finally { Delete(root); }
    }

    // ── different versions ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DifferentFrameworkMajorInDepsIsRefusedInBothModes()
    {
        if (!Available(nameof(DifferentFrameworkMajorInDepsIsRefusedInBothModes))) return;
        string root = Temp();
        try
        {
            string exe = Ordinary(root);
            string folder = Path.GetDirectoryName(exe)!;
            string deployedVersion = TargetDiagnostics.Inspect(exe).FrameworkPackageVersion!;
            string deps = Path.ChangeExtension(exe, ".deps.json");
            File.WriteAllText(deps, File.ReadAllText(deps).Replace($"TSharpVision/{deployedVersion}", "TSharpVision/2.0.0"));
            var before = Snapshot(folder);
            var service = Service(root);
            var report = Diagnose(exe);
            var fw = Assert.Single(report.Findings, f => f.Code == "FW002");
            Assert.True(fw.Blocking);
            foreach (var strategy in new[] { PreparationStrategy.PreparedCopy, PreparationStrategy.InPlaceInstallation })
            {
                var plan = service.CreatePlan(service.Inspect(exe), strategy, PreparationCapabilities.Proxy);
                Assert.Equal(PreparationVerdict.VersionMismatch, plan.Components.Single(c => c.Name == "Framework").State);
                Assert.Equal(FrameworkMatch.DifferentVersion, plan.Framework!.Match);
                Assert.False(plan.CanExecute);
                Assert.Contains(plan.Problems, p => p.Contains("2.0.0") && p.Contains("major"));
                Assert.Equal(PreparationVerdict.RequiresRebuild, PreparationVerdict.For(plan, service.Payload!.FrameworkVersion).Outcome);
            }
            var fix = Repair(service).Plan(exe, report);
            Assert.False(fix.CanRepair);
            Assert.True(fix.RequiresRebuild);
            Assert.False(TargetRepairService.Relevant(report));
            Assert.False((await Repair(service).RepairAsync(fix, Diagnose)).Success);
            Assert.Equal(before, Snapshot(folder));
            Evidence("different-version.txt", [$"FW002: {fw.Message}", fw.Detail, .. Lines(fix)]);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void OlderFrameworkWithinSameMajorIsEligibleForRuntimeVerification()
    {
        if (!Available(nameof(OlderFrameworkWithinSameMajorIsEligibleForRuntimeVerification), older: true)) return;
        string root = Temp();
        try
        {
            string exe = Ordinary(root);
            string folder = Path.GetDirectoryName(exe)!;
            // A genuine older 1.x assembly remains useful evidence that minor equality is not a static gate.
            File.Copy(OlderFramework()!, Path.Combine(folder, "TSharpVision.dll"), true);
            var before = Snapshot(folder);
            var service = Service(root);
            var report = Diagnose(exe);
            Assert.DoesNotContain(report.Findings, f => f.Code == "FW002" && f.Blocking);
            var copy = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy);
            Assert.True(copy.CanExecute, string.Join("; ", copy.Problems));
            Assert.Equal(FrameworkMatch.SameMajor, copy.Framework!.Match);
            Assert.True(copy.Framework.Compatible);
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    // ── payload integrity, repair rollback, real verification failure ─────────────────────────────────

    [Fact]
    public async Task TamperedOrCorruptedPayloadIsRefusedAndNothingIsWritten()
    {
        if (!Available(nameof(TamperedOrCorruptedPayloadIsRefusedAndNothingIsWritten))) return;
        string root = Temp();
        try
        {
            foreach (var (name, damage) in new (string, Action<string>)[]
            {
                ("tampered", payload => { using var stream = new FileStream(Path.Combine(payload, TargetDiagnostics.TestingFile), FileMode.Open, FileAccess.ReadWrite); stream.Seek(-1, SeekOrigin.End); stream.WriteByte(0x5a); }),
                ("truncated", payload => { using var stream = new FileStream(Path.Combine(payload, TargetDiagnostics.ProxyFile), FileMode.Open, FileAccess.ReadWrite); stream.SetLength(stream.Length / 2); }),
                ("manifest-missing", payload => File.Delete(Path.Combine(payload, PayloadManifest.FileName))),
            })
            {
                string payload = Path.Combine(root, "payload-" + name);
                foreach (string file in Directory.GetFiles(TvDemoDeploymentUxTests.Payload()!, "*", SearchOption.AllDirectories))
                {
                    string to = Path.Combine(payload, Path.GetRelativePath(TvDemoDeploymentUxTests.Payload()!, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(file, to);
                }
                damage(payload);
                string clean = Ordinary(root, "clean-" + name);
                var cleanBefore = Snapshot(Path.GetDirectoryName(clean)!);
                var broken = new TargetPreparationService(Path.Combine(root, "prepared-" + name), payload);
                Assert.Null(broken.Payload);
                Assert.NotNull(broken.PayloadProblem);
                var refused = new TargetRepairService(broken) { IsRunning = _ => false }.Plan(clean, Diagnose(clean));
                Assert.False(refused.CanRepair);
                Assert.False((await new TargetRepairService(broken) { IsRunning = _ => false }.RepairAsync(refused, Diagnose)).Success);
                Assert.False(broken.CreatePlan(broken.Inspect(clean), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy).CanExecute);
                Assert.Equal(cleanBefore, Snapshot(Path.GetDirectoryName(clean)!));
                Assert.False(Directory.Exists(Path.Combine(root, "prepared-" + name)));
                Evidence($"payload-{name}.txt", [broken.PayloadProblem!]);
            }
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task RepairOfAnOtherBuildRollsBackOnFailureAndCancellation()
    {
        if (!Available(nameof(RepairOfAnOtherBuildRollsBackOnFailureAndCancellation))) return;
        string root = Temp();
        try
        {
            string exe = OtherBuild(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            int writes = 0;
            // A write failure after the first file was published: everything already written is removed again.
            var failing = Service(root, path => { if (!path.StartsWith("stage:") && !path.StartsWith("<") && ++writes == 2) throw new IOException("simulated disk failure"); });
            var plan = Repair(failing).Plan(exe, Diagnose(exe));
            Assert.True(plan.CanRepair, string.Join("\n", plan.Blockers));
            var outcome = await Repair(failing).RepairAsync(plan, Diagnose);
            Assert.False(outcome.Success);
            Assert.True(outcome.RolledBack && outcome.RollbackComplete, outcome.Message);
            Assert.Equal(before, Snapshot(folder));
            // Cancellation before anything is written.
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var service = Service(root);
            var again = await Repair(service).RepairAsync(Repair(service).Plan(exe, Diagnose(exe)), Diagnose, null, cancelled.Token);
            Assert.False(again.Success);
            Assert.Equal(before, Snapshot(folder));
            Evidence("repair-rollback.txt", ["failure: " + outcome.Message, "cancelled: " + again.Message]);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task RealVerifyFailureIsReportedWithItsCause()
    {
        if (!Available(nameof(RealVerifyFailureIsReportedWithItsCause))) return;
        string root = Temp();
        try
        {
            // A same-version deployment that cannot start the Proxy: the SDL driver assembly the Proxy loads at its handshake
            // is missing. The real verification fails, names the cause and cleans up.
            string exe = OtherBuild(root);
            var service = Service(root);
            var outcome = await Repair(service).RepairAsync(Repair(service).Plan(exe, Diagnose(exe)), Diagnose);
            Assert.True(outcome.Success, outcome.Message);
            File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, "TSharpVision.Drivers.SDL.dll"));
            var (ok, line) = await Verify(exe);
            Assert.False(ok);
            Assert.StartsWith("verify FAILED", line);
            Assert.Contains("cleanup True", line);
            Assert.Equal(0, Running(exe));
            Evidence("verify-failure.txt", [line]);
        }
        finally { Delete(root); }
    }
}
