using System.Diagnostics;
using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// TSW PRODUCT-3 / Agent A: Fix Issues. The ordinary target is a disposable copy of the package-mode diagnostics/ProxyOrdinary
// build (TSWB_PRODUCT_TARGET, else ../ProxyOrdinary/<configuration>/ProxyOrdinary.exe beside this test output) - an ordinary
// TSharpVision application without the Workbench Proxy. The Workbench-owned Proxy payload is the one staged beside the
// Workbench build (TSWB_PROXY_PAYLOAD, else ../TSharpWorkbench/<configuration>/proxy-payload). Logical screenshots (NullDriver
// cell rendering, NOT native): TSWB_PRODUCT_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class FixIssuesTests
{
    private const string TargetExe = "ProxyOrdinary.exe";

    // ── environment ──────────────────────────────────────────────────────────────────

    internal static string? Original()
    {
        string base_ = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable("TSWB_PRODUCT_TARGET"),
            Path.Combine(base_, "..", "..", "ProxyOrdinary", Path.GetFileName(base_), TargetExe) })
            if (candidate is { Length: > 0 } && File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
    private static bool Available()
    {
        if (TvDemoDeploymentUxTests.Payload() is not { } payload || Original() is null) return false;
        Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, payload);
        return true;
    }
    private static string Temp() => Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "artifacts", "tsw-product-3", "fix-issues", "t", Guid.NewGuid().ToString("N")[..8]);
    // A disposable copy of the ordinary target (no symbols), in its own folder.
    private static string CopyOriginal(string root, string name = "app")
    {
        string source = Path.GetDirectoryName(Original()!)!;
        string target = Path.Combine(root, name);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(file, to);
        }
        return Path.Combine(target, TargetExe);
    }
    private static void Delete(string root)
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
    private static TargetPreparationService Service(string root, Action<string>? hook = null)
    {
        var service = new TargetPreparationService(Path.Combine(root, "prepared"), TvDemoDeploymentUxTests.Payload()) { BeforeWrite = hook };
        TargetDiagnostics.Preparation = service;
        TargetPreparationService.ForgetStatus();
        return service;
    }
    private static string Project(string root, string exe)
    {
        string counter = Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "diagnostics", "ProxyOrdinary", "counter.scenario.json");
        string projectPath = Path.Combine(root, "ordinary.twproj");
        new WorkbenchProject { Name = "Ordinary", Target = exe, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [counter], SelectedIds = ["proxy/counter"],
            ScreenSizes = ["80x25"], ProxyBackend = "NullDriver" }.Save(projectPath);
        return projectPath;
    }

    // ── visual: the current Properties and Target Diagnostics windows for a target without the Proxy ──

    [Theory]
    [InlineData(140, 40)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public async Task CaptureProxyMissingWindows(int width, int height)
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyOriginal(root);
            Service(root);
            string projectPath = Project(root, exe);
            string grid = $"{width}x{height}";
            await Drive(width, height, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count == 1 && p.ProjectPath == projectPath ? p : null, 60);
                await bridge.Invoke(() => Assert.Contains("ProxyOrdinary.exe | Proxy missing |", main.HeaderText));
                await Shot(bridge, $"workspace-proxy-missing-{grid}", "Workspace header: Proxy missing");
                Press(Keys.kbAltO);
                var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPropertiesDialog);
                await bridge.Invoke(() => Assert.Contains("Proxy missing", props.DiagnosticsText));
                await Shot(bridge, $"properties-proxy-missing-{grid}", "Project Properties: Target diagnostics with Proxy missing");
                Send(WorkbenchPropertiesDialog.TargetDetails);
                var details = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchTargetDiagnosticsDialog);
                await bridge.Invoke(() => Assert.Contains(details.ReportLines, l => l.Contains("PROXY001")));
                await Shot(bridge, $"target-diagnostics-proxy-missing-{grid}", "Target Diagnostics (Details) with Proxy missing");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is WorkbenchPropertiesDialog);
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPropertiesDialog);
            });
        }
        finally { Delete(root); }
    }

    // ── service: plan, copy-only repair, re-verification ───────────────────────────────────────

    private static DiagnosticContext ProxyRun(string backend = "NullDriver") => new(true, false, backend, null, null, Run: true);
    private static TargetReport Diagnose(string exe) { TargetDiagnostics.Forget(exe); return TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), ProxyRun()); }
    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(folder, f), Preparation.FileHash, StringComparer.OrdinalIgnoreCase);
    private static TargetRepairService Repair(TargetPreparationService service, bool running = false) => new(service) { IsRunning = _ => running };
    private static int Running(string exe) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Count(p =>
    { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); } });

    [Fact]
    public async Task MissingProxyIsFixedByCopyOnlyAndTheTargetVerifies()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var service = Service(root);
            var repair = Repair(service);
            var report = Diagnose(exe);
            Assert.Contains(report.Findings, f => f.Code == "PROXY001" && f.Blocking);
            Assert.True(TargetRepairService.Relevant(report));
            var plan = repair.Plan(exe, report);
            Assert.True(plan.CanRepair, string.Join("\n", plan.Blockers));
            Assert.False(plan.NeedsReplaceApproval);
            var payload = service.Payload!;
            // Exactly the absent Workbench support files of the Proxy core, from this Workbench's payload, with identities.
            var fixes = plan.Items.Where(i => i.State == RepairState.Fix).ToArray();
            // Proxy core plus the SDL component the Proxy's handshake loads on every backend (copying only Proxy + Testing
            // does not start: FileNotFoundException at the handshake, see the report); no console driver for a headless project.
            Assert.Contains(fixes, i => i.Name == TargetDiagnostics.ProxyFile);
            Assert.Contains(fixes, i => i.Name == TargetDiagnostics.TestingFile);
            Assert.Contains(fixes, i => i.Name == "TSharpVision.Drivers.SDL.dll");
            Assert.Contains(fixes, i => i.Name == "SDL3-CS.dll");
            Assert.DoesNotContain(fixes, i => i.Name is "TSharpVision.dll" or "TSharpVision.Drivers.Console.dll" or TargetExe or "ProxyOrdinary.dll");
            Assert.All(fixes, i => Assert.Contains(payload.Assets, a => a.Destination == i.Name && a.Component is "proxy" or "sdl"));
            Assert.All(fixes, i =>
            {
                var asset = payload.Assets.Single(a => a.Destination == i.Name);
                Assert.Equal(asset.Source, i.Source);
                Assert.StartsWith(Path.GetFullPath(payload.Directory), i.Source);
                Assert.Contains(asset.Package + " " + asset.Version, i.Expected);
                Assert.Contains(asset.Sha256[..12], i.Expected);
                Assert.Equal(Path.Combine(folder, i.Name), i.Destination);
                Assert.Contains("absent", i.Conflict);
                Assert.Contains("no elevation", i.Permission);
            });
            Assert.Contains(fixes, i => i.Code == "PROXY001");
            Assert.Equal(before, Snapshot(folder));   // planning is read-only
            var steps = new List<PreparationProgress>();
            var outcome = await repair.RepairAsync(plan, Diagnose, new SyncProgress(steps.Add));
            Assert.True(outcome.Success, outcome.Message + "\n" + string.Join("\n", outcome.Details));
            Assert.True(outcome.After!.Ready, string.Join("\n", outcome.After.Blocking.Select(f => f.Message)));
            Assert.Contains(outcome.Items, i => i.State == RepairState.Fixed && i.Code == "PROXY001");
            Assert.DoesNotContain(outcome.Items, i => i.State == RepairState.Unresolved);
            // Only the planned files were added, with the payload's content; every original file is byte-identical.
            var now = Snapshot(folder);
            Assert.Equal(before.Count + fixes.Length, now.Count);
            Assert.All(fixes, i => Assert.Equal(payload.Assets.Single(a => a.Destination == i.Name).Sha256, now[i.Name]));
            Assert.All(before, kv => Assert.Equal(kv.Value, now[kv.Key]));
            Assert.Equal(payload.Assets.Single(a => a.Destination == TargetDiagnostics.ProxyFile).Sha256, now[TargetDiagnostics.ProxyFile]);
            Assert.Contains(steps, s => s.Phase == "Installing files");
            // Runtime proof: the repaired ordinary target really loads the Proxy and completes the Proxy handshake.
            var verified = await new ScenarioRunner().VerifyAsync(exe, true);
            Assert.True(verified.Proxy);
            Assert.True(verified.Cleanup);
            Assert.Equal(0, Running(exe));
            Evidence("service-repair.txt", [$"target {exe}", .. outcome.Changes, $"after: {outcome.After.Readiness}",
                $"verified: {verified.Identity.Assembly} on {verified.Identity.Driver} {verified.Identity.Width}x{verified.Identity.Height}, proxy={verified.Proxy}, stop={verified.StopOutcome}, cleanup={verified.Cleanup}, {verified.Duration.TotalSeconds:F1}s"]);
        }
        finally { Delete(root); }
    }

    // Copying Proxy + Testing alone is not a fix: the Proxy's handshake loads the SDL driver assembly on every backend. The
    // runtime failure is reproduced, the diagnostics now report it (PROXY007) and Fix Issues repairs it.
    [Fact]
    public async Task ProxyAndTestingAloneFailAtRuntimeAndFixIssuesCompletesTheDeployment()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var service = Service(root);
            foreach (string file in new[] { TargetDiagnostics.ProxyFile, TargetDiagnostics.TestingFile })
                File.Copy(Path.Combine(service.Payload!.Directory, file), Path.Combine(folder, file));
            var failure = await Assert.ThrowsAsync<TargetVerificationException>(() => new ScenarioRunner().VerifyAsync(exe, true));
            Assert.Contains("FileNotFoundException", failure.Message);
            Assert.True(failure.Cleanup);
            Assert.Equal(0, Running(exe));
            var report = Diagnose(exe);
            var missing = Assert.Single(report.Findings, f => f.Code == "PROXY007");
            Assert.True(missing.Blocking);
            Assert.EndsWith("TSharpVision.Drivers.SDL.dll", missing.Path);
            var repair = Repair(service);
            var plan = repair.Plan(exe, report);
            Assert.Contains(plan.Items, i => i.State == RepairState.Fix && i.Code == "PROXY007" && i.Name == "TSharpVision.Drivers.SDL.dll");
            Assert.Contains(plan.Items, i => i.State == RepairState.Ok && i.Name == TargetDiagnostics.ProxyFile);
            var outcome = await repair.RepairAsync(plan, Diagnose);
            Assert.True(outcome.Success, outcome.Message);
            Assert.Contains(outcome.Items, i => i.State == RepairState.Fixed && i.Code == "PROXY007");
            var verified = await new ScenarioRunner().VerifyAsync(exe, true);
            Assert.True(verified.Proxy && verified.Cleanup);
            Assert.Equal(0, Running(exe));
            Evidence("proxy-testing-only.txt", [$"Proxy + Testing only: {failure.Message}", $"diagnosed: {missing.Code} {missing.Message}", .. outcome.Changes,
                $"after Fix Issues: verified {verified.Identity.Assembly} proxy={verified.Proxy} cleanup={verified.Cleanup}"]);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task HealthyTargetIsANoOp()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            var service = Service(root);
            var repair = Repair(service);
            Assert.True((await repair.RepairAsync(repair.Plan(exe, Diagnose(exe)), Diagnose)).Success);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var report = Diagnose(exe);
            Assert.False(TargetRepairService.Relevant(report));
            var plan = repair.Plan(exe, report);
            Assert.True(plan.Healthy);
            Assert.False(plan.CanRepair);
            Assert.StartsWith("Nothing to fix", plan.Summary);
            Assert.All(plan.Items, i => Assert.Equal(RepairState.Ok, i.State));
            var outcome = await repair.RepairAsync(plan, Diagnose);
            Assert.False(outcome.Success);
            Assert.StartsWith("Nothing was changed", outcome.Message);
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    // A different build of a Workbench-owned assembly: refused by default; replaced only with the explicit approval, after a
    // verified backup (kept in the Workbench's own install record).
    [Fact]
    public async Task DifferentWorkbenchBuildIsRefusedUnlessApprovedThenBackedUp()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var service = Service(root);
            // Same assembly identity, different content (trailing bytes after the PE image): "an earlier Workbench build".
            string testing = Path.Combine(folder, TargetDiagnostics.TestingFile);
            File.Copy(Path.Combine(service.Payload!.Directory, TargetDiagnostics.TestingFile), testing);
            using (var s = new FileStream(testing, FileMode.Append)) s.Write(new byte[64]);
            var before = Snapshot(folder);
            var repair = Repair(service);
            var plan = repair.Plan(exe, Diagnose(exe));
            Assert.True(plan.NeedsReplaceApproval);
            Assert.False(plan.CanRepair);
            Assert.Contains(plan.Blockers, b => b.Contains("needs your approval"));
            var refused = Assert.Single(plan.Items, i => i.Name == TargetDiagnostics.TestingFile);
            Assert.Equal(RepairState.Refused, refused.State);
            Assert.Contains("differs from this Workbench's build", refused.Conflict);
            Assert.False((await repair.RepairAsync(plan, Diagnose)).Success);
            Assert.Equal(before, Snapshot(folder));
            // Approved: replaced with a verified backup, and the deployment is ready.
            var approved = repair.Plan(exe, Diagnose(exe), allowReplace: true);
            Assert.True(approved.CanRepair, string.Join("\n", approved.Blockers));
            Assert.Equal(RepairState.Fix, approved.Items.Single(i => i.Name == TargetDiagnostics.TestingFile).State);
            var outcome = await repair.RepairAsync(approved, Diagnose);
            Assert.True(outcome.Success, outcome.Message);
            Assert.Equal(service.Payload.Assets.Single(a => a.Destination == TargetDiagnostics.TestingFile).Sha256, Preparation.FileHash(testing));
            string record = Directory.GetFiles(Path.Combine(root, "prepared"), InstallRecord.FileName, SearchOption.AllDirectories).Single();
            var install = Preparation.ReadJson<InstallRecord>(record)!;
            Assert.Equal("installed", install.State);
            var backup = install.Files.Single(f => f.Action == "replaced");
            Assert.Equal(before[TargetDiagnostics.TestingFile], Preparation.FileHash(Path.Combine(Path.GetDirectoryName(record)!, backup.Backup!)));
        }
        finally { Delete(root); }
    }

    // An in-place Proxy from another Workbench build (same assembly version, other content) was reported as PROXY000
    // "deployment appears complete", so Fix Issues stayed disabled while recordings silently fell back to input only.
    [Fact]
    public async Task ProxyFromAnotherWorkbenchBuildIsAWarningThatEnablesFixIssues()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var service = Service(root);
            var repair = Repair(service);
            Assert.True((await repair.RepairAsync(repair.Plan(exe, Diagnose(exe)), Diagnose)).Success);
            var current = Diagnose(exe);
            Assert.Contains(current.Findings, f => f.Code == "PROXY000");
            Assert.DoesNotContain(current.Findings, f => f.Code == "PROXY008");

            // Same assembly identity, different content: "the Proxy of an earlier Workbench build".
            string proxy = Path.Combine(folder, TargetDiagnostics.ProxyFile);
            using (var s = new FileStream(proxy, FileMode.Append)) s.Write(new byte[64]);
            var stale = Diagnose(exe);
            var finding = Assert.Single(stale.Findings, f => f.Code == "PROXY008");
            Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
            Assert.Contains("Smart capture", finding.Detail);
            Assert.DoesNotContain(stale.Findings, f => f.Code == "PROXY000");
            Assert.True(stale.Ready, string.Join("\n", TargetDiagnostics.Lines(stale)));   // it still loads: a warning, not a blocker
            Assert.True(TargetRepairService.Relevant(stale));
            var approved = repair.Plan(exe, stale, allowReplace: true);
            Assert.True(approved.CanRepair, string.Join("\n", approved.Blockers));
            Assert.Equal(RepairState.Fix, approved.Items.Single(i => i.Name == TargetDiagnostics.ProxyFile).State);
            Assert.True((await repair.RepairAsync(approved, Diagnose)).Success);
            Assert.Contains(Diagnose(exe).Findings, f => f.Code == "PROXY000");
        }
        finally { Delete(root); }
    }

    // Unsafe changes are refused: an unrelated file with the Proxy's name and a framework that is not the Proxy's build.
    [Fact]
    public async Task UnrelatedFilesAndAnotherFrameworkBuildAreRefused()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var service = Service(root);
            var repair = Repair(service);
            // 1. A custom DLL named like the Proxy: never replaced.
            File.Copy(Path.Combine(folder, "ProxyOrdinary.dll"), Path.Combine(folder, TargetDiagnostics.ProxyFile));
            var before = Snapshot(folder);
            var report = Diagnose(exe);
            Assert.Contains(report.Findings, f => f.Code == "PROXY006");
            var plan = repair.Plan(exe, report, allowReplace: true);
            Assert.False(plan.CanRepair);
            var conflict = Assert.Single(plan.Items, i => i.Name == TargetDiagnostics.ProxyFile);
            Assert.Equal((RepairState.Refused, "PROXY006"), (conflict.State, conflict.Code));
            Assert.Contains("unrelated file", conflict.Conflict);
            Assert.False((await repair.RepairAsync(plan, Diagnose)).Success);
            Assert.Equal(before, Snapshot(folder));
            File.Delete(Path.Combine(folder, TargetDiagnostics.ProxyFile));
            // 2. A conflicting declared major is refused; the application's framework is never touched.
            string deps = Path.ChangeExtension(exe, ".deps.json");
            // Mutate the current package identity to a conflicting major and prove the mutation occurred.
            string declared = File.ReadAllText(deps), other = declared.Replace("TSharpVision/" + Infrastructure.FrameworkPackageVersion.Expected, "TSharpVision/2.0.0");
            Assert.NotEqual(declared, other);
            File.WriteAllText(deps, other);
            before = Snapshot(folder);
            plan = repair.Plan(exe, Diagnose(exe), allowReplace: true);
            Assert.False(plan.CanRepair);
            Assert.Contains(plan.Items, i => i.State == RepairState.Refused && i.Name == "TSharpVision.dll" && i.Detail.Contains("major"));
            Assert.DoesNotContain(plan.Items, i => i.State == RepairState.Fix && i.Name == "TSharpVision.dll");
            Assert.False((await repair.RepairAsync(plan, Diagnose)).Success);
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadOnlyDestinationFailsClearlyAndChangesNothing()
    {
        if (!Available() || !OperatingSystem.IsWindows()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var service = Service(root);
            var repair = Repair(service);
            var plan = repair.Plan(exe, Diagnose(exe));
            Assert.True(plan.CanRepair);
            var before = Snapshot(folder);
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData, System.Security.AccessControl.AccessControlType.Deny);
            acl.AddAccessRule(deny); info.SetAccessControl(acl);
            try
            {
                var outcome = await repair.RepairAsync(plan, Diagnose);
                Assert.False(outcome.Success);
                Assert.Contains("not writable", outcome.Message);
                Assert.Contains("Nothing in the application folder was changed", outcome.Message);
                Assert.Contains(outcome.Items, i => i.State == RepairState.Unresolved && i.Code == "PROXY001");
            }
            finally { acl.RemoveAccessRule(deny); info.SetAccessControl(acl); }
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task RunningTargetActiveRunLinkedFolderAndPreparedCopyAreRefused()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var service = Service(root);
            var before = Snapshot(folder);
            var running = Repair(service, running: true).Plan(exe, Diagnose(exe));
            Assert.False(running.CanRepair);
            Assert.Contains(running.Blockers, b => b.Contains("is running. Close it first"));
            Assert.Contains(running.Items, i => i.State == RepairState.Manual && i.Code == "PROXY001");
            var active = Repair(service).Plan(exe, Diagnose(exe), runActive: true);
            Assert.Contains(active.Blockers, b => b.StartsWith("A test run is active", StringComparison.Ordinal));
            // A plan confirmed while closed is refused when the target started in the meantime.
            var ok = Repair(service).Plan(exe, Diagnose(exe));
            var late = await Repair(service, running: true).RepairAsync(ok, Diagnose);
            Assert.False(late.Success);
            Assert.Contains("is running", late.Message);
            Assert.Equal(before, Snapshot(folder));
            // A folder reached through a junction is refused (links are never followed).
            if (OperatingSystem.IsWindows())
            {
                string junction = Path.Combine(root, "linked");
                using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{folder}\"") { CreateNoWindow = true, UseShellExecute = false })!) mk.WaitForExit();
                string viaLink = Path.Combine(junction, TargetExe);
                var linked = Repair(service).Plan(viaLink, Diagnose(viaLink));
                Assert.Contains(linked.Blockers, b => b.Contains("symbolic link or junction"));
                Directory.Delete(junction);
            }
            // A prepared copy (managed by Prepare target) is never changed in place.
            var prepare = await service.ExecuteAsync(service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl));
            Assert.True(prepare.Success, prepare.Message);
            var copy = Repair(service).Plan(prepare.PreparedExecutable!, Diagnose(prepare.PreparedExecutable!));
            Assert.Contains(copy.Blockers, b => b.Contains("prepared copy managed by Prepare target"));
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task CancellationDuringPublishingRestoresTheFolder()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            using var cts = new CancellationTokenSource();
            int published = 0;
            // Cancel after the first file was published into the application folder.
            var service = Service(root, hook: step => { if (!step.StartsWith("stage:", StringComparison.Ordinal) && !step.StartsWith('<') && ++published == 1) cts.Cancel(); });
            var repair = Repair(service);
            var plan = repair.Plan(exe, Diagnose(exe));
            var outcome = await repair.RepairAsync(plan, Diagnose, null, cts.Token);
            Assert.False(outcome.Success);
            Assert.True(outcome.Cancelled);
            Assert.True(outcome.RolledBack);
            Assert.True(outcome.RollbackComplete);
            Assert.Equal(before, Snapshot(folder));
            Assert.Contains(outcome.Items, i => i.State == RepairState.Unresolved && i.Code == "PROXY001");
            // Cancelled before anything starts: nothing at all.
            using var early = new CancellationTokenSource(); early.Cancel();
            var none = await Repair(Service(root)).RepairAsync(repair.Plan(exe, Diagnose(exe)), Diagnose, null, early.Token);
            Assert.True(none.Cancelled);
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task PartialFailureRollsBackEveryCopiedFile()
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            int published = 0;
            var service = Service(root, hook: step =>
            {
                if (!step.StartsWith("stage:", StringComparison.Ordinal) && !step.StartsWith('<') && ++published == 2) throw new IOException("simulated disk failure");
            });
            var repair = Repair(service);
            var outcome = await repair.RepairAsync(repair.Plan(exe, Diagnose(exe)), Diagnose);
            Assert.False(outcome.Success);
            Assert.False(outcome.Cancelled);
            Assert.Contains("simulated disk failure", outcome.Message);
            Assert.Contains("rolled back", outcome.Message);
            Assert.True(outcome.RolledBack && outcome.RollbackComplete);
            Assert.Equal(before, Snapshot(folder));
            Assert.Equal("rolled back", Preparation.ReadJson<InstallRecord>(Directory.GetFiles(Path.Combine(root, "prepared"), InstallRecord.FileName, SearchOption.AllDirectories).Single())!.State);
            // The plan is still offered afterwards and succeeds without the fault.
            var again = Repair(Service(root));
            Assert.True((await again.RepairAsync(again.Plan(exe, Diagnose(exe)), Diagnose)).Success);
        }
        finally { Delete(root); }
    }

    // ── UI: Properties > Fix issues > plan > confirmation > repair > re-check > Verify target ─────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public async Task FixIssuesFromPropertiesRepairsVerifiesAndRefreshesStatus(int width, int height)
    {
        if (!Available()) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyOriginal(root);
            string folder = Path.GetDirectoryName(exe)!;
            Service(root);
            string projectPath = Project(root, exe);
            string projectText = File.ReadAllText(projectPath);
            string grid = $"{width}x{height}";
            await Drive(width, height, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count == 1 && p.ProjectPath == projectPath ? p : null, 60);
                Press(Keys.kbAltO);
                var props = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPropertiesDialog);
                await bridge.Invoke(() =>
                {
                    Assert.False(props.FixButton.GetState(Views.sfDisabled));
                    Assert.Contains("Proxy      ERR  Proxy missing", props.DiagnosticsText);
                    Assert.Empty(ProductQualificationTests.DuplicateMnemonics(props));
                    Assert.True(props.FixButton.origin.x + props.FixButton.size.x <= props.DiagnosticsBox.size.x - 1);
                });
                Press(Keys.kbAltF);
                // Without the Workspace callback (lead patch) Properties routes through Target Diagnostics, which offer Fix Issues.
                var fix = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog ?? (TView?)(app.DeskTop!.TopView() as WorkbenchTargetDiagnosticsDialog));
                if (fix is WorkbenchTargetDiagnosticsDialog diagnostics)
                {
                    await bridge.Invoke(() => Assert.False(diagnostics.FixButton.GetState(Views.sfDisabled)));
                    await Shot(bridge, $"target-diagnostics-fix-offered-{grid}", "Target Diagnostics: Fix issues... beside Verify target");
                    Press(Keys.kbAltF);
                    fix = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog);
                }
                var dialog = (WorkbenchFixIssuesDialog)fix;
                await bridge.Invoke(() =>
                {
                    Assert.True(dialog.Plan.CanRepair);
                    Assert.StartsWith("FIX", dialog.Rows[0]);
                    Assert.Contains(dialog.Rows, r => r.StartsWith("FIX") && r.Contains("PROXY001") && r.Contains(TargetDiagnostics.ProxyFile));
                    Assert.Contains("Source", dialog.DetailText);
                    Assert.Contains("Destination", dialog.DetailText);
                    Assert.True(dialog.ReplaceOption.GetState(Views.sfDisabled));
                    Assert.Empty(ProductQualificationTests.DuplicateMnemonics(dialog));
                    Assert.True(dialog.origin.y + dialog.size.y <= app.DeskTop!.size.y);
                });
                Assert.False(File.Exists(Path.Combine(folder, TargetDiagnostics.ProxyFile)));
                await Shot(bridge, $"fix-issues-plan-{grid}", "Fix Issues: plan (nothing changed yet)");
                // Enter on the list does not fix; Fix issues... asks first and Cancel is the default.
                Press(Keys.kbAltF);
                var confirm = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchFixIssuesDialog.ConfirmTitle } d ? d : null);
                await Shot(bridge, $"fix-issues-confirm-{grid}", "Fix Issues: confirmation (Cancel default)");
                Press(Keys.kbEnter);
                await Until(bridge, () => app.DeskTop!.TopView() == dialog);
                await bridge.Invoke(() => Assert.Equal("Nothing was changed.", dialog.StatusText));
                Assert.False(File.Exists(Path.Combine(folder, TargetDiagnostics.ProxyFile)));
                Press(Keys.kbAltF);
                confirm = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchFixIssuesDialog.ConfirmTitle } d ? d : null);
                Press(Keys.kbAltF);   // Fix and verify
                await Until(bridge, () => dialog.Outcome is not null && !dialog.Busy && dialog.Verification is not null, 60);
                await bridge.Invoke(() =>
                {
                    Assert.True(dialog.Outcome!.Success, dialog.StatusText);
                    Assert.Contains(dialog.Rows, r => r.StartsWith("FIXED") && r.Contains("PROXY001"));
                    Assert.DoesNotContain(dialog.Rows, r => r.StartsWith("UNRESOLVED"));
                    Assert.StartsWith("Verified in", dialog.StatusText);
                    Assert.Contains("Ready (runtime verified)", dialog.SummaryText);
                    Assert.True(dialog.FixButton.GetState(Views.sfDisabled));
                });
                Assert.True(File.Exists(Path.Combine(folder, TargetDiagnostics.ProxyFile)));
                Assert.Equal(0, Running(exe));
                await Shot(bridge, $"fix-issues-fixed-verified-{grid}", "Fix Issues: fixed, re-checked and verified");
                Press(Keys.kbEsc);
                await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchFixIssuesDialog);
                if (app.DeskTop!.TopView() is WorkbenchTargetDiagnosticsDialog details)
                {
                    await bridge.Invoke(() => { Assert.True(details.FixButton.GetState(Views.sfDisabled)); Assert.Contains(details.ReportLines, l => l.Contains("PROXY000")); });
                    await Shot(bridge, $"target-diagnostics-after-fix-{grid}", "Target Diagnostics after Fix Issues");
                    Press(Keys.kbEsc);
                    await Until(bridge, () => app.DeskTop!.TopView() is WorkbenchPropertiesDialog);
                }
                await bridge.Invoke(() => Nudge(props));
                await bridge.Invoke(() =>
                {
                    Assert.Contains("Proxy      OK   Proxy found", props.DiagnosticsText);
                    Assert.Contains("Readiness  OK   Ready", props.DiagnosticsText);
                    Assert.True(props.FixButton.GetState(Views.sfDisabled));
                });
                await Shot(bridge, $"properties-after-fix-{grid}", "Project Properties after Fix Issues: ready, Fix disabled");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPropertiesDialog);
                await bridge.Invoke(() => { Assert.Contains("| Ready (verified) |", main.HeaderText); Assert.False(main.Dirty); });
                await Shot(bridge, $"workspace-after-fix-{grid}", "Workspace header after Fix Issues: Ready (verified)");
            });
            // Nothing ran and nothing was saved: Fix Issues is not a test execution.
            Assert.Equal(projectText, File.ReadAllText(projectPath));
            Assert.False(Directory.Exists(Path.Combine(root, "runs")));
        }
        finally { Delete(root); }
    }

    private sealed class SyncProgress(Action<PreparationProgress> report) : IProgress<PreparationProgress> { public void Report(PreparationProgress value) => report(value); }
    private static void Evidence(string name, IEnumerable<string> lines)
    {
        if (Environment.GetEnvironmentVariable("TSWB_PRODUCT_PROOF") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); File.WriteAllLines(Path.Combine(dir, name), lines);
    }

    // ── harness (as TargetDiagnosticsUxTests) ─────────────────────────────────────────────────────────

    private static async Task Drive(int width, int height, string root, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string target = TestWorkbenchHostTests.SampleTarget;
        string output = Path.Combine(root, "runs-sample");
        string settings = Path.Combine(root, "settings.json");
        new WorkbenchPreferences(target, output, null, "80x25") { RunView = WorkbenchPreferences.BasicView }.Save(settings);
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = settings });
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try { await Loaded(app, bridge); await body(app, bridge); await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); }); }
            catch (Exception ex)
            {
                failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                await bridge.Invoke(() =>
                {
                    for (int i = 0; i < 5 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                    app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                }).WaitAsync(TimeSpan.FromSeconds(10));
            }
        });
        AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(60)), "Worker did not stop.");
        Assert.Null(failure);
    }
    private static async Task Loaded(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 1 ? p : null, 60);
    private static async Task Shot(Bridge bridge, string name, string state)
    {
        if (Environment.GetEnvironmentVariable("TSWB_PRODUCT_EVIDENCE") is not { Length: > 0 } root) return;
        await Settle(bridge);
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".txt"), string.Join("\n", Enumerable.Range(0, screen.Height).Select(y =>
                new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character is var c && c >= ' ' ? c : ' ').ToArray()).TrimEnd())));
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, captureType = "logical (NullDriver cell rendering; NOT native)", driver = "NullDriver", grid = $"{screen.Width}x{screen.Height}",
                test = nameof(FixIssuesTests), expectedState = state, renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
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
    private static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(80); await bridge.Invoke(() => { }); }
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
