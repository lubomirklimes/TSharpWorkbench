using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// one Prepare / Fix experience. Targets (each copied into a disposable folder first):
//  * ProxyOrdinary: the package-mode diagnostics/ProxyOrdinary build (TSWB_PRODUCT_TARGET, else ../ProxyOrdinary/<config>/ beside
//    this test output), an ordinary application without the Workbench Proxy;
//  * TVDemo package build: TSWB_TVDEMO_ORIGINAL (the public 1.1.0 package family, byte-identical to the payload);
//  * TVDemo local framework build: TSWB_TVDEMO_LOCAL, else the TSharpVision checkout's samples/.../bin/Debug/net10.0 build
//    (ProjectReference to a modified framework working tree: same version 1.1.0, different content).
// The Workbench Proxy payload: TSWB_PROXY_PAYLOAD. Logical screenshots (NullDriver cell rendering, NOT native): TSWB_PUBLISH_CAPTURE.
// A test whose environment is missing returns early and writes the reason to TSWB_PUBLISH_SKIPS (so a skip is never silent).
[Collection("NonParallel")]
public sealed class PrepareTests
{
    private const string OrdinaryExe = "ProxyOrdinary.exe", TvDemoExe = "TSharpVision.Samples.TVDemo.exe";

    // ── environment ──────────────────────────────────────────────────────────────────

    private static string? LocalTvDemo()
    {
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable("TSWB_TVDEMO_LOCAL"),
            @"C:\TurboVision\GIT\TSharpVision\samples\TSharpVision.Samples.TVDemo\bin\Debug\net10.0\" + TvDemoExe })
            if (candidate is { Length: > 0 } && File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
    private static bool Available(string test, bool ordinary = true, bool package = false, bool local = false)
    {
        var missing = new List<string>();
        if (TvDemoDeploymentUxTests.Payload() is null) missing.Add("payload (TSWB_PROXY_PAYLOAD)");
        if (ordinary && FixIssuesTests.Original() is null) missing.Add("ProxyOrdinary (TSWB_PRODUCT_TARGET)");
        if (package && TvDemoDeploymentUxTests.Original() is null) missing.Add("TVDemo package build (TSWB_TVDEMO_ORIGINAL)");
        if (local && LocalTvDemo() is null) missing.Add("TVDemo local framework build (TSWB_TVDEMO_LOCAL)");
        if (missing.Count > 0)
        {
            if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_SKIPS") is { Length: > 0 } skips)
                File.AppendAllText(skips, $"{test}: skipped, missing {string.Join(", ", missing)}{Environment.NewLine}");
            return false;
        }
        Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, TvDemoDeploymentUxTests.Payload());
        return true;
    }
    private static string Temp() => Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "artifacts", "tsw-product-3a", "prepare", "t", Guid.NewGuid().ToString("N")[..8]);
    // A disposable copy of a deployment: top-level files (no PDB unless asked), Help/ and runtimes/<this RID>/.
    private static string Copy(string exe, string root, string name, bool symbols = false)
    {
        string source = Path.GetDirectoryName(exe)!, target = Path.Combine(root, name);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source).Where(f => symbols || !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
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
    private static string Ordinary(string root, string name = "app") => Copy(FixIssuesTests.Original()!, root, name);
    private static void Delete(string root)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)) f.IsReadOnly = false;
                    Directory.Delete(root, true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
    private static TargetPreparationService Service(string root)
    {
        var service = new TargetPreparationService(Path.Combine(root, "prepared"), TvDemoDeploymentUxTests.Payload());
        TargetDiagnostics.Preparation = service;
        TargetPreparationService.ForgetStatus();
        return service;
    }
    internal static string Project(string root, string exe, bool tvdemo)
    {
        string scenario = tvdemo ? Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "examples", "TVDemo", "scenarios", "about.scenario.json")
            : Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "diagnostics", "ProxyOrdinary", "counter.scenario.json");
        string projectPath = Path.Combine(root, (tvdemo ? "tvdemo" : "ordinary") + ".twproj");
        new WorkbenchProject { Name = tvdemo ? "TVDemo" : "Ordinary", Target = exe, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [scenario],
            SelectedIds = [tvdemo ? "tvdemo/about" : "proxy/counter"], ScreenSizes = ["80x25"], ProxyBackend = "NullDriver" }.Save(projectPath);
        return projectPath;
    }
    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(folder, f), Preparation.FileHash, StringComparer.OrdinalIgnoreCase);
    private static DiagnosticContext ProxyRun(string backend = "NullDriver") => new(true, false, backend, null, null, Run: true);
    private static TargetReport Diagnose(string exe) { TargetDiagnostics.Forget(exe); return TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), ProxyRun()); }

    // ── visual: Target Diagnostics, Fix Issues, Prepare Target and Preparation Files at the three grids ─────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task CapturePrepareAndFixWindows(int width, int height)
    {
        if (!Available(nameof(CapturePrepareAndFixWindows), local: true)) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string grid = $"{width}x{height}";
            string ordinary = Ordinary(root);
            string local = Copy(LocalTvDemo()!, root, "tvdemo-local");
            Service(root);
            foreach (var (exe, tag, tvdemo) in new[] { (ordinary, "ordinary-proxy-missing", false), (local, "tvdemo-local-framework", true) })
            {
                string projectPath = Project(root, exe, tvdemo);
                await Drive(width, height, root, async (app, bridge) =>
                {
                    await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                    await Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == projectPath && p.TestList.Strings.Count == 1 ? p : null, 60);
                    Send(WorkbenchDialog.DiagnoseTarget);
                    var diagnostics = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchTargetDiagnosticsDialog);
                    await Shot(bridge, $"target-diagnostics-{tag}-{grid}", "Target Diagnostics");
                    await bridge.Invoke(() => Layout(diagnostics, app.DeskTop!));
                    Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchTargetDiagnosticsDialog);
                    Send(WorkbenchDialog.FixIssuesCommand);
                    var fix = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog);
                    await Shot(bridge, $"fix-issues-{tag}-{grid}", "Fix Issues (fix original target)");
                    await bridge.Invoke(() => Layout(fix, app.DeskTop!));
                    Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchFixIssuesDialog);
                    Send(WorkbenchDialog.PrepareTargetCommand);
                    var prepare = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                    await Shot(bridge, $"prepare-target-{tag}-{grid}", "Prepare Target (prepared copy)");
                    await bridge.Invoke(() => Layout(prepare, app.DeskTop!));
                    bool files = false;
                    await bridge.Invoke(() => files = !prepare.FilesButton.GetState(Views.sfDisabled));
                    if (files)
                    {
                        Press(Keys.kbAltF);
                        var list = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchPrepareTargetDialog.FilesTitle } d ? d : null);
                        await Shot(bridge, $"preparation-files-{tag}-{grid}", "Preparation Files (details)");
                        Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() == prepare);
                    }
                    Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPrepareTargetDialog);
                });
            }
        }
        finally { Delete(root); }
    }

    // ── one engine, two modes: compatible targets ──────────────────────────────────────────────

    private static TargetRepairService Repair(TargetPreparationService service) => new(service) { IsRunning = _ => false };

    [Fact]
    public async Task CompatibleProxyOrdinaryPlansBothModesFromOnePayloadAndFixCopiesOnlyMissingFiles()
    {
        if (!Available(nameof(CompatibleProxyOrdinaryPlansBothModesFromOnePayloadAndFixCopiesOnlyMissingFiles))) return;
        string root = Temp();
        try
        {
            string exe = Ordinary(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var service = Service(root);
            var payload = service.Payload!;
            // Prepare a copy: the summary first, in the shared vocabulary.
            var copy = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl);
            var summary = PreparationVerdict.For(copy, payload.FrameworkVersion);
            Assert.True(copy.CanExecute, string.Join("\n", copy.Problems));
            Assert.Equal(PreparationVerdict.Ready, summary.Outcome);
            Assert.False(summary.RequiresRebuild || summary.Manual);
            Assert.StartsWith("copy ", summary.Counts);
            Assert.Contains("0 conflicts, 0 refused", summary.Counts);
            Assert.Equal("Compatible", copy.Components.Single(c => c.Name == "Framework").State);
            Assert.Equal(PreparationVerdict.Fixable, copy.Components.Single(c => c.Name == "Proxy and Testing").State);
            Assert.StartsWith(Path.Combine(root, "prepared"), copy.Destination);
            // Every application file is copied (never dropped by extension); the added files come from the one payload.
            Assert.Equal(before.Count, copy.Count(PlanAction.Copy));
            Assert.All(copy.Entries.Where(e => e.Action == PlanAction.Add), e => Assert.Contains(payload.Assets, a => a.Destination == e.Path && a.Sha256 == e.Sha256));
            // Fix the original target: the same payload assets, copy-only, through the same in-place engine.
            var report = Diagnose(exe);
            Assert.True(TargetRepairService.Relevant(report));
            var fix = Repair(service).Plan(exe, report);
            Assert.True(fix.CanRepair, string.Join("\n", fix.Blockers));
            Assert.Equal(PreparationStrategy.InPlaceInstallation, fix.Install!.Strategy);
            var fixes = fix.Items.Where(i => i.State == RepairState.Fix).Select(i => i.Name).ToHashSet();
            Assert.Subset(copy.Entries.Where(e => e.Action == PlanAction.Add).Select(e => e.Path).ToHashSet(), fixes);
            Assert.DoesNotContain(fix.Items, i => i.State == RepairState.Blocked);
            Assert.StartsWith("fix ", fix.Counts);
            Assert.Equal(before, Snapshot(folder));     // planning both modes is read-only
            var outcome = await Repair(service).RepairAsync(fix, Diagnose);
            Assert.True(outcome.Success, outcome.Message);
            var after = Snapshot(folder);
            // No wrong replacement: every original file is byte-identical; only the planned files were added.
            Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
            Assert.Equal(before.Count + fixes.Count, after.Count);
            Assert.False(TargetRepairService.Relevant(Diagnose(exe)));   // nothing left to fix: Fix is disabled
        }
        finally { Delete(root); }
    }

    [Fact]
    public void TvDemoPackageBuildIsCompatibleInBothModes()
    {
        if (!Available(nameof(TvDemoPackageBuildIsCompatibleInBothModes), ordinary: false, package: true)) return;
        string root = Temp();
        try
        {
            string exe = Copy(TvDemoDeploymentUxTests.Original()!, root, "tvdemo-package");
            var service = Service(root);
            var report = Diagnose(exe);
            Assert.Contains(report.Findings, f => f.Code == "FW000");
            Assert.DoesNotContain(report.Findings, f => f.Code is "FW002" or "FW003");
            Assert.Equal(service.Payload!.FrameworkSha256, Preparation.FileHash(Path.Combine(Path.GetDirectoryName(exe)!, "TSharpVision.dll")));
            var copy = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, TargetPreparationService.DefaultCapabilities);
            Assert.True(copy.CanExecute, string.Join("\n", copy.Problems));
            Assert.Equal(PreparationVerdict.Ready, PreparationVerdict.For(copy, service.Payload.FrameworkVersion).Outcome);
            Assert.DoesNotContain(copy.Components, c => PreparationVerdict.NeedsRebuild(c.State));
            var fix = Repair(service).Plan(exe, report);
            Assert.True(fix.CanRepair, string.Join("\n", fix.Blockers));
            Assert.True(TargetRepairService.Relevant(report));
        }
        finally { Delete(root); }
    }

    // ── The local framework build (same version: supported; another version: refused) ─────────────

    [Fact]
    public async Task LocalFrameworkBuildOfTvDemoIsDecidedByVersion()
    {
        if (!Available(nameof(LocalFrameworkBuildOfTvDemoIsDecidedByVersion), ordinary: false, local: true)) return;
        string root = Temp();
        try
        {
            string exe = Copy(LocalTvDemo()!, root, "tvdemo-local");
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            var service = Service(root);
            var framework = TargetDiagnostics.Inspect(exe).Framework!;
            Assert.NotEqual(service.Payload!.FrameworkSha256, Preparation.FileHash(framework.Path));
            Assert.True(PreparationVerdict.ProjectReferenced(Path.ChangeExtension(exe, ".deps.json"), "TSharpVision"));
            // The owner's live TVDemo build may carry another framework version (it moved to 1.2.0): then it is refused.
            bool same = FrameworkCompatibility.Declared(framework) == FrameworkCompatibility.Core(service.Payload.FrameworkVersion);
            var report = Diagnose(exe);
            var copy = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, TargetPreparationService.DefaultCapabilities);
            var summary = PreparationVerdict.For(copy, service.Payload.FrameworkVersion);
            var fix = Repair(service).Plan(exe, report, allowReplace: true);
            List<string> lines = [$"target {exe}", $"declared {FrameworkCompatibility.Declared(framework)}, payload {service.Payload.FrameworkVersion}, same version: {same}",
                .. report.Findings.Where(f => f.Component == "Framework").Select(f => $"{f.Code} {f.Severity} blocking={f.Blocking}: {f.Message}"),
                $"copy outcome: {summary.Outcome}", $"next: {summary.Next}", .. copy.Components.Select(c => $"{c.Name}: {c.State} - {c.Detail}"),
                .. copy.Problems.Select(p => "! " + p), $"fix: {fix.Summary}", .. fix.Items.Select(i => $"{RepairItem.Label(i.State)} {i.Name} {i.Summary}")];
            if (same)
            {
                // Same version, other build, ProjectReference: FW003 is information only; both modes are offered.
                var fw = Assert.Single(report.Findings, f => f.Code == "FW003");
                Assert.False(fw.Blocking);
                Assert.Equal(DiagnosticSeverity.Info, fw.Severity);
                Assert.DoesNotContain(report.Findings, f => f.Code == "FW002");
                Assert.True(copy.CanExecute, string.Join("\n", copy.Problems));
                Assert.Equal(PreparationVerdict.Ready, summary.Outcome);
                Assert.Equal(PreparationVerdict.SameVersion, copy.Components.Single(c => c.Name == "Framework").State);
                Assert.Contains(copy.Entries, e => e.Path == "TSharpVision.Drivers.SDL.dll" && e.Action == PlanAction.Copy);   // the application's own driver build
                Assert.DoesNotContain(copy.Entries, e => e.Action is PlanAction.Add or PlanAction.Replace && Path.GetFileName(e.Path).StartsWith("TSharpVision", StringComparison.OrdinalIgnoreCase));
                Assert.True(fix.CanRepair, string.Join("\n", fix.Blockers));
                Assert.True(fix.OtherBuild);
                Assert.True(TargetRepairService.Relevant(report));
                Assert.Contains(fix.Items, i => i.State == RepairState.Ok && i.Name == "TSharpVision.dll" && i.Summary.StartsWith("same version, other build"));
                Assert.Contains(fix.Items, i => i.State == RepairState.Ok && i.Name == "TSharpVision.Drivers.SDL.dll");
                Assert.All(fix.Install!.Changes, e => Assert.Equal(PlanAction.Add, e.Action));
            }
            else
            {
                Assert.Contains(report.Findings, f => f.Code == "FW002");
                Assert.False(copy.CanExecute);
                Assert.Equal(PreparationVerdict.RequiresRebuild, summary.Outcome);
                Assert.Equal(PreparationVerdict.VersionMismatch, copy.Components.Single(c => c.Name == "Framework").State);
                Assert.False(fix.CanRepair);
                Assert.True(fix.RequiresRebuild);
                var outcome = await Repair(service).RepairAsync(fix, Diagnose);
                Assert.False(outcome.Success);
            }
            Assert.Equal(before, Snapshot(folder));   // planning is read-only
            Evidence("tvdemo-local.txt", lines);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void SameVersionSdlDriverBuildIsKeptAndOtherVersionRefused()
    {
        if (!Available(nameof(SameVersionSdlDriverBuildIsKeptAndOtherVersionRefused), ordinary: false, package: true, local: true)) return;
        string root = Temp();
        try
        {
            string exe = Copy(TvDemoDeploymentUxTests.Original()!, root, "tvdemo-mixed");
            string folder = Path.GetDirectoryName(exe)!;
            // The package build with the SDL driver of the local framework build (same version = supported, kept).
            string local = Path.Combine(Path.GetDirectoryName(LocalTvDemo()!)!, "TSharpVision.Drivers.SDL.dll");
            File.Copy(local, Path.Combine(folder, "TSharpVision.Drivers.SDL.dll"), true);
            var before = Snapshot(folder);
            var service = Service(root);
            bool same = FrameworkCompatibility.FamilyProblem(TargetDiagnostics.ReadAssembly(local), TargetDiagnostics.ReadAssembly(Path.Combine(service.Payload!.Directory, "TSharpVision.Drivers.SDL.dll"))) is null;
            foreach (var capabilities in new[] { PreparationCapabilities.Proxy, TargetPreparationService.DefaultCapabilities })
            {
                var copy = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, capabilities);
                Assert.Equal("Compatible", copy.Components.Single(c => c.Name == "Framework").State);
                Assert.Equal(same, copy.CanExecute);
                if (same) Assert.Contains(copy.Entries, e => e.Path == "TSharpVision.Drivers.SDL.dll" && e.Action == PlanAction.Copy);
                else Assert.Contains(copy.Problems, p => p.StartsWith("TSharpVision.Drivers.SDL.dll: " + PreparationVerdict.VersionMismatch, StringComparison.Ordinal));
            }
            var fix = Repair(service).Plan(exe, Diagnose(exe), allowReplace: true);
            var sdl = Assert.Single(fix.Items, i => i.Name == "TSharpVision.Drivers.SDL.dll");
            Assert.Equal(same ? RepairState.Ok : RepairState.Refused, sdl.State);
            Assert.Contains(same ? "kept" : "never replaced", sdl.Summary);
            Assert.DoesNotContain(fix.Install!.Changes, e => e.Path == "TSharpVision.Drivers.SDL.dll");
            Assert.Equal(before, Snapshot(folder));
        }
        finally { Delete(root); }
    }

    // ── protected destination and cancellation ───────────────────────────────────────────────

    [Fact]
    public async Task ProtectedDestinationAndCancellationChangeNothing()
    {
        if (!Available(nameof(ProtectedDestinationAndCancellationChangeNothing)) || !OperatingSystem.IsWindows()) return;
        string root = Temp();
        try
        {
            string exe = Ordinary(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = Snapshot(folder);
            // Program Files is a protected in-place destination: refused before anything is planned, no elevation.
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Assert.NotNull(Preparation.ProtectedLocation(Path.Combine(programs, "Some App")));
            // A prepared-copy destination that refuses writes: the copy fails cleanly, nothing is published, original unchanged.
            string store = Path.Combine(root, "prepared");
            Directory.CreateDirectory(store);
            var info = new DirectoryInfo(store);
            var acl = info.GetAccessControl();
            var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateDirectories | System.Security.AccessControl.FileSystemRights.CreateFiles,
                System.Security.AccessControl.InheritanceFlags.None, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
            acl.AddAccessRule(deny); info.SetAccessControl(acl);
            var service = Service(root);
            try
            {
                var plan = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl);
                Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
                var result = await service.ExecuteAsync(plan);
                Assert.False(result.Success);
                Assert.Contains("the original application was not modified", result.Message);
            }
            finally { acl.RemoveAccessRule(deny); info.SetAccessControl(acl); }
            Assert.Empty(Directory.GetDirectories(store));
            Assert.Equal(before, Snapshot(folder));
            // Cancellation before execution: nothing published, original unchanged (copy and fix).
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var copy = await service.ExecuteAsync(service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl), null, cancel.Token);
            Assert.False(copy.Success); Assert.True(copy.Cancelled);
            var fixPlan = Repair(service).Plan(exe, Diagnose(exe));
            var fixed_ = await Repair(service).RepairAsync(fixPlan, Diagnose, null, cancel.Token);
            Assert.True(fixed_.Cancelled);
            Assert.Equal(before, Snapshot(folder));
            Assert.Empty(Directory.GetDirectories(store).Where(d => Directory.GetFiles(d, PreparationManifest.FileName, SearchOption.AllDirectories).Length > 0));
        }
        finally { Delete(root); }
    }

    // ── UI: one chooser in both windows, Fix disabled when nothing is fixable, Cancel default ──────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task ModeChooserSwitchesBetweenCopyAndFixAndCancelChangesNothing(int width, int height)
    {
        if (!Available(nameof(ModeChooserSwitchesBetweenCopyAndFixAndCancelChangesNothing), local: true)) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string grid = $"{width}x{height}";
            string exe = Ordinary(root);
            string folder = Path.GetDirectoryName(exe)!;
            string local = Copy(LocalTvDemo()!, root, "tvdemo-local");
            var before = Snapshot(folder);
            Service(root);
            string projectPath = Project(root, exe, tvdemo: false);
            await Drive(width, height, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                await Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == projectPath && p.TestList.Strings.Count == 1 ? p : null, 60);
                // Project > Fix issues opens the Fix mode; the chooser hands over to Prepare a copy and back.
                Send(WorkbenchDialog.FixIssuesCommand);
                var fix = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(1u, fix.Mode.value);
                    Assert.False(fix.FixButton.GetState(Views.sfDisabled));
                    Assert.StartsWith("fix ", fix.SummaryText);
                    Layout(fix, app.DeskTop!);
                });
                // Fix asks first; Cancel (the default) changes nothing.
                Press(Keys.kbAltF);
                await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchFixIssuesDialog.ConfirmTitle } d ? d : null);
                Press(Keys.kbEnter);
                await Until(bridge, () => app.DeskTop!.TopView() == fix);
                Assert.Equal(before, Snapshot(folder));
                await bridge.Invoke(() => { fix.Mode.value = 0; Nudge(fix); });
                var prepare = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(0u, prepare.Method.value);
                    Assert.Equal(PreparationVerdict.Ready, prepare.OutcomeText);
                    // The destination is an editable field showing the proposed folder in the store.
                    Assert.StartsWith(Path.Combine(root, "prepared"), prepare.DestinationField.Data);
                    Assert.Equal(DestinationKind.Proposed, prepare.Destination.Kind);
                    Assert.False(prepare.PrepareButton.GetState(Views.sfDisabled));
                    Assert.Equal(prepare.size, fix.size);   // the chooser stays in place: same frame
                    Layout(prepare, app.DeskTop!);
                });
                await Shot(bridge, $"switched-to-copy-{grid}", "Prepare a copy, reached from Fix issues through the mode chooser");
                Press(Keys.kbEsc);
                await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPrepareTargetDialog);
                Assert.False(Directory.Exists(Path.Combine(root, "prepared")) && Directory.GetDirectories(Path.Combine(root, "prepared")).Length > 0);
                Assert.Equal(before, Snapshot(folder));
            });
            // The local framework build: the same version from another build is offered by both modes (FW003 is
            // information); another version keeps Fix disabled and Prepare refuses with advice.
            bool same = FrameworkCompatibility.Declared(TargetDiagnostics.Inspect(local).Framework) == FrameworkCompatibility.Core(TargetDiagnostics.Preparation.Payload!.FrameworkVersion);
            string expected = same ? PreparationVerdict.Ready : PreparationVerdict.RequiresRebuild;
            string localProject = Project(root, local, tvdemo: true);
            await Drive(width, height, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(localProject), localProject));
                await Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == localProject && p.TestList.Strings.Count == 1 ? p : null, 60);
                Send(WorkbenchDialog.DiagnoseTarget);
                var diagnostics = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchTargetDiagnosticsDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(!same, diagnostics.FixButton.GetState(Views.sfDisabled));
                    Assert.Contains(diagnostics.ReportLines, l => l.Contains(same ? "FW003" : "FW002"));
                });
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchTargetDiagnosticsDialog);
                Send(WorkbenchDialog.FixIssuesCommand);
                var fix = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(!same, fix.FixButton.GetState(Views.sfDisabled));
                    Assert.Equal(same, fix.Rows.Any(r => r.StartsWith("FIX ")));
                    if (!same) { Assert.Contains(expected, fix.SummaryText); Assert.StartsWith("Fix is disabled", fix.StatusText); }
                });
                await bridge.Invoke(() => { fix.Mode.value = 0; Nudge(fix); });
                var prepare = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(expected, prepare.OutcomeText);
                    Assert.Equal(!same, prepare.PrepareButton.GetState(Views.sfDisabled));
                    Assert.Contains(same ? "Framework            " + PreparationVerdict.SameVersion : "Framework            Version mismatch", prepare.Text);
                });
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPrepareTargetDialog);
            });
        }
        finally { Delete(root); }
    }

    // Evidence only (opt-in TSWB_PUBLISH_LOAD_EXPERIMENT=1): what the runtime does when the package-built Proxy is put by hand beside
    // the local framework build, in a disposable copy. The product never does this (it refuses the deployment); the
    // experiment shows why version metadata cannot decide: a load that happens to succeed is still not identity.
    [Fact]
    public async Task LoadBehaviourOfThePackageProxyBesideTheLocalFrameworkBuild()
    {
        if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_LOAD_EXPERIMENT") != "1" || !Available(nameof(LoadBehaviourOfThePackageProxyBesideTheLocalFrameworkBuild), ordinary: false, local: true)) return;
        string root = Temp();
        try
        {
            string exe = Copy(LocalTvDemo()!, root, "experiment");
            string payload = TvDemoDeploymentUxTests.Payload()!;
            foreach (string file in new[] { TargetDiagnostics.ProxyFile, TargetDiagnostics.TestingFile })
                File.Copy(Path.Combine(payload, file), Path.Combine(Path.GetDirectoryName(exe)!, file));
            var lines = new List<string> { $"experiment target {exe} (local framework build + package Proxy/Testing copied by hand)" };
            try
            {
                var verified = await new ScenarioRunner().VerifyAsync(exe, true);
                lines.Add($"VerifyAsync: connected {verified.Identity.Assembly} on {verified.Identity.Driver}, proxy={verified.Proxy}, stop={verified.StopOutcome}, cleanup={verified.Cleanup}");
            }
            catch (Exception ex) { lines.Add("VerifyAsync failed: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
            Evidence("load-experiment.txt", lines);
        }
        finally { Delete(root); }
    }

    private static void Evidence(string name, IEnumerable<string> lines)
    {
        if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_PROOF") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); File.WriteAllLines(Path.Combine(dir, name), lines);
    }

    // ── layout: every child inside the dialog's interior, no two interactive or text views overlapping ──────────

    internal static void Layout(TDialog dialog, TGroup desktop)
    {
        var problems = LayoutProblems(dialog, desktop);
        // TSWB_PUBLISH_LAYOUT_LOG: record instead of failing (used for the BEFORE captures of the unchanged windows).
        if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_LAYOUT_LOG") is { Length: > 0 } log)
        { File.AppendAllLines(log, problems.Select(p => $"{desktop.size.x}x{desktop.size.y} desktop: {p}")); return; }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
    internal static List<string> LayoutProblems(TDialog dialog, TGroup desktop)
    {
        var problems = new List<string>();
        if (!(dialog.origin.x >= 0 && dialog.origin.y >= 0 && dialog.origin.x + dialog.size.x <= desktop.size.x && dialog.origin.y + dialog.size.y <= desktop.size.y))
            problems.Add($"{dialog.title} {dialog.size.x}x{dialog.size.y} at {dialog.origin.x},{dialog.origin.y} exceeds the desktop {desktop.size.x}x{desktop.size.y}");
        var views = Children(dialog).Where(v => v.GetState(Views.sfVisible) && v.size.x > 0 && v.size.y > 0 && v is not TScrollBar and not TFrame).ToArray();
        foreach (var v in views)
            if (!(v.origin.x >= 1 && v.origin.y >= 1 && v.origin.x + v.size.x <= dialog.size.x - 1 && v.origin.y + v.size.y <= dialog.size.y - 1))
                problems.Add($"{dialog.title}: {Describe(v)} leaves the interior");
        for (int i = 0; i < views.Length; i++)
            for (int j = i + 1; j < views.Length; j++)
            {
                var (a, b) = (views[i], views[j]);
                if (a.origin.x < b.origin.x + b.size.x && b.origin.x < a.origin.x + a.size.x && a.origin.y < b.origin.y + b.size.y && b.origin.y < a.origin.y + a.size.y)
                    problems.Add($"{dialog.title}: {Describe(a)} overlaps {Describe(b)}");
            }
        // Button titles fit their buttons (no truncated label).
        foreach (var button in views.OfType<TButton>())
            if (button.Title.Replace("~", "").Length + 2 > button.size.x) problems.Add($"{dialog.title}: button {button.Title} is truncated");
        problems.AddRange(ProductQualificationTests.DuplicateMnemonics(dialog).Select(m => $"{dialog.title}: duplicate mnemonic {m}"));
        return problems;
    }
    private static string? StaticText(TView v) { if (v is not TStaticText t) return null; t.GetText(out string s); return s; }
    private static string Describe(TView v) => $"{v.GetType().Name}({(v as TButton)?.Title ?? StaticText(v)?.Split('\n')[0] ?? ""}) {v.origin.x},{v.origin.y} {v.size.x}x{v.size.y}";

    // ── harness (as FixIssuesTests) ─────────────────────────────────────────────────────

    internal static async Task Drive(int width, int height, string root, Func<WorkbenchApp, Bridge, Task> body)
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
                await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 1 ? p : null, 60);
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
        Assert.True(worker.Wait(TimeSpan.FromSeconds(90)), "Worker did not stop.");
        Assert.Null(failure);
    }
    internal static async Task Shot(Bridge bridge, string name, string state)
    {
        if (Environment.GetEnvironmentVariable("TSWB_PUBLISH_CAPTURE") is not { Length: > 0 } root) return;
        await Settle(bridge);
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical-nulldriver.txt"), string.Join("\n", Enumerable.Range(0, screen.Height).Select(y =>
                new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character is var c && c >= ' ' ? c : ' ').ToArray()).TrimEnd())));
            File.WriteAllText(Path.Combine(root, name + ".logical-nulldriver.json"), JsonSerializer.Serialize(new
            {
                name, captureType = "logical-nulldriver (NullDriver cell rendering; NOT a native console or SDL frame)", driver = "NullDriver",
                grid = $"{screen.Width}x{screen.Height}", test = nameof(PrepareTests), expectedState = state, renderer = LogicalPng.Renderer,
                cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    internal static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    internal static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    internal static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    internal static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    internal static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(80); await bridge.Invoke(() => { }); }
    internal static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    internal static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    internal sealed class Bridge : TView
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
