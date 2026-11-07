using System.Diagnostics;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Target diagnostics (static inspection, context-sensitive blocking, Verify target), the Basic/Advanced Run
// Configuration and friendly startup failures. Deployments are disposable copies of a prepared package-only TVDemo
// deployment (TSWB_DIAGNOSTICS_TVDEMO_TARGET, else artifacts/diagnostics-ux/tvdemo/current/target, else artifacts/tvdemo/current/target)
// and of the compiled SampleTarget. Logical screenshots: TSWB_DIAGNOSTICS_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class TargetDiagnosticsUxTests
{
    private const string TvDemoExe = "TSharpVision.Samples.TVDemo.exe";
    private const string ProxyFailure = "Target exited (-532462766) before connecting. System.IO.FileNotFoundException: Could not load assembly 'TSharpWorkbench.Proxy, "
        + "Culture=neutral, PublicKeyToken=null'. at TSharpVision.Drivers.ScreenDriverFactory.Decorate(IDriver backend, String providerName) [launch, grid 80x25]";

    // ── deployments ──────────────────────────────────────────────────────────────────

    internal static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    internal static string? PreparedTvDemo()
    {
        string root = RepositoryRoot();
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable("TSWB_DIAGNOSTICS_TVDEMO_TARGET"),
            Path.Combine(root, "artifacts", "diagnostics-ux", "tvdemo", "current", "target", TvDemoExe), Path.Combine(root, "artifacts", "tvdemo", "current", "target", TvDemoExe) })
            if (candidate is { Length: > 0 } && File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
    // A disposable copy of the prepared deployment (top-level files; no symbols or XML docs), minus the named files.
    private static string CopyTvDemo(string root, params string[] without)
    {
        string source = Path.GetDirectoryName(PreparedTvDemo() ?? throw new InvalidOperationException("No prepared TVDemo deployment: run scripts/prepare-tvdemo.ps1."))!;
        string target = Path.Combine(root, "tvdemo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || without.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, name));
        }
        return Path.Combine(target, TvDemoExe);
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "target-diagnostics-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
    private static DiagnosticContext ProxyRun(string backend = "NullDriver", string capture = "none") => new(true, false, backend, capture, null, Run: true);
    private static DiagnosticContext CompiledRun(string? native = null) => new(false, true, null, null, native, Run: true);
    private static TargetReport Report(string exe, DiagnosticContext context, CatalogFacts? catalog = null) =>
        TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), context, catalog);
    private static DiagnosticFinding Finding(TargetReport report, string code) =>
        report.Findings.SingleOrDefault(f => f.Code == code) ?? throw new Xunit.Sdk.XunitException($"{code} missing: " + string.Join(", ", report.Findings.Select(f => f.Code)));
    private static int Running(string exe) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Count(p =>
    { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); } });

    // ── A. static diagnostics ─────────────────────────────────────────────────────────

    [Fact]
    public void PreparedTvDemoDeploymentIsAcceptedWithoutClaimingRuntimeSuccess()
    {
        if (PreparedTvDemo() is null) return;
        string root = Temp();
        try
        {
            string exe = CopyTvDemo(root);
            var report = Report(exe, ProxyRun());
            Assert.True(report.Ready, string.Join("\n", TargetDiagnostics.Lines(report)));
            Assert.Equal(DiagnosticSeverity.Ok, Finding(report, "EXE000").Severity);
            Assert.Equal(DiagnosticSeverity.Ok, Finding(report, "FW000").Severity);
            Assert.Equal("Proxy assembly found; deployment appears complete.", Finding(report, "PROXY000").Message);
            // Honest state: files exist, the runtime activation is not claimed.
            Assert.Equal(DiagnosticSeverity.Info, Finding(report, "RUN001").Severity);
            Assert.Contains(TargetDiagnostics.Lines(report), l => l.Contains("RUN001") && l.EndsWith("Runtime activation not verified."));
            Assert.Contains("Ready (static checks; runtime not verified)", report.Summary[^1].Text);
            // The deployment registers every Proxy backend this platform offers; SDL needs its native libraries (present).
            Assert.All(report.Drivers.Where(d => d.Contains("(NullDriver)") || d.Contains("(SDLDriver)")), d => Assert.Contains("deployed", d));
            foreach (string backend in new[] { "SDLDriver", "SDLGpuDriver", "Win32ConsoleDriver" })
                Assert.True(Report(exe, ProxyRun(backend)).Ready, backend);
            // An ordinary application: the diagnostic host is "not installed" information, never a Proxy blocker.
            Assert.Equal(DiagnosticSeverity.Info, Finding(report, "HOST001").Severity);
            Assert.False(Finding(report, "HOST001").Blocking);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void MissingProxyBlocksProxyScenariosOnlyAndNamesTheSearchedPath()
    {
        if (PreparedTvDemo() is null) return;
        string root = Temp();
        try
        {
            string exe = CopyTvDemo(root, TargetDiagnostics.ProxyFile);
            string searched = Path.Combine(Path.GetDirectoryName(exe)!, TargetDiagnostics.ProxyFile);
            foreach (string backend in new[] { "NullDriver", "SDLDriver" })
            {
                var report = Report(exe, ProxyRun(backend));
                var proxy = Finding(report, "PROXY001");
                Assert.Equal((DiagnosticSeverity.Error, true, searched), (proxy.Severity, proxy.Blocking, proxy.Path));
                Assert.Contains("Searched: " + searched, proxy.Detail);
                Assert.Contains("Target: " + exe, proxy.Detail);
                Assert.Contains("prepare", proxy.Action);
                Assert.Equal("Proxy missing", report.Readiness);
                Assert.Equal("Proxy scenarios cannot run", report.Summary[^1].Text);
            }
            // The same deployment for a compiled-diagnostic context: the missing Proxy is information, not an error.
            var compiled = Report(exe, CompiledRun());
            Assert.Equal(DiagnosticSeverity.Info, Finding(compiled, "PROXY001").Severity);
            Assert.False(Finding(compiled, "PROXY001").Blocking);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void MissingDependenciesAreContextual()
    {
        if (PreparedTvDemo() is null) return;
        string root = Temp();
        try
        {
            // The Proxy references TSharpWorkbench.Testing: required for every Proxy run.
            string noTesting = CopyTvDemo(root, TargetDiagnostics.TestingFile);
            Assert.True(Finding(Report(noTesting, ProxyRun()), "PROXY002").Blocking);
            // SDL's native library: required by SDL backends only, never by the headless one.
            string noSdl = CopyTvDemo(root, "SDL3.dll");
            Assert.True(Report(noSdl, ProxyRun()).Ready);
            var sdl = Report(noSdl, ProxyRun("SDLDriver"));
            Assert.Equal("DRV003", Assert.Single(sdl.Blocking).Code);
            Assert.Contains("SDL3.dll", sdl.Blocking[0].Message);
            // GPU additionally needs SDL3_shadercross; the software SDL window does not.
            string noShader = CopyTvDemo(root, "SDL3_shadercross.dll");
            Assert.True(Report(noShader, ProxyRun("SDLDriver")).Ready);
            Assert.Contains("SDL3_shadercross.dll", Assert.Single(Report(noShader, ProxyRun("SDLGpuDriver")).Blocking).Message);
            // Unsupported backend: a driver whose assembly is not deployed is never substituted.
            string noConsole = CopyTvDemo(root, "TSharpVision.Drivers.Console.dll");
            var console = Report(noConsole, ProxyRun("Win32ConsoleDriver"));
            Assert.Equal("DRV001", Assert.Single(console.Blocking).Code);
            Assert.True(Report(noConsole, ProxyRun("SDLDriver")).Ready);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void IncompatibleFrameworkIdentityIsAnErrorForProxyRuns()
    {
        if (PreparedTvDemo() is null) return;
        string root = Temp();
        try
        {
            string exe = CopyTvDemo(root);
            string folder = Path.GetDirectoryName(exe)!;
            // Replace the framework with an assembly of another identity/version (1.0 instead of 1.1).
            File.Copy(Path.Combine(folder, TargetDiagnostics.TestingFile), Path.Combine(folder, "TSharpVision.dll"), true);
            var report = Report(exe, ProxyRun());
            var framework = report.Findings.First(f => f.Component == "Framework");
            Assert.Equal(("FW002", DiagnosticSeverity.Error, true), (framework.Code, framework.Severity, framework.Blocking));
            Assert.Contains(report.Blocking, f => f.Code == "PROXY003");
            // Missing framework entirely.
            File.Delete(Path.Combine(folder, "TSharpVision.dll"));
            Assert.True(Finding(Report(exe, ProxyRun()), "FW001").Blocking);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void CompiledDiagnosticTargetNeedsNoProxyAndFixtureProblemsAreExplained()
    {
        string sample = TestWorkbenchHostTests.SampleTarget;
        // Compiled diagnostic scenarios on the SampleTarget (no Proxy deployed): not blocked; host not yet verified.
        var unverified = Report(sample, CompiledRun());
        Assert.True(unverified.Ready, string.Join("\n", TargetDiagnostics.Lines(unverified)));
        Assert.Equal("HOST002", unverified.Findings.Single(f => f.Component == "Diagnostic host").Code);
        Assert.Equal(DiagnosticSeverity.Info, Finding(unverified, "PROXY001").Severity);
        // After discovery (the authoritative catalog), the host is available.
        var discovered = Report(sample, CompiledRun(), new CatalogFacts(true, 16, 3, 0, null, []));
        Assert.Equal(DiagnosticSeverity.Ok, Finding(discovered, "HOST000").Severity);
        // A failed catalog handshake is "incompatible" and blocks diagnostic runs.
        var failed = Report(sample, CompiledRun(), new CatalogFacts(false, 0, 0, 0, "Target protocol schema mismatch.", []));
        Assert.True(Finding(failed, "HOST003").Blocking);
        if (PreparedTvDemo() is null) return;
        string root = Temp();
        try
        {
            // Fixture-bound documents on an ordinary application: no diagnostic host (blocking), the binding is explained,
            // and nothing falls back to Proxy mode or another fixture.
            string exe = CopyTvDemo(root);
            string doc = Path.Combine(root, "fixture.scenario.json");
            File.WriteAllText(doc, """{"schema":"tsharpworkbench.scenario","version":1,"id":"demo/fixture","title":"Fixture","description":"Fixture-bound document.","group":"demo","expectedOutcome":"passed","fixture":"sample/notes","steps":[{"kind":"checkpoint","id":"ready"}]}""");
            var attached = ScenarioFiles.Check([doc], exe, null, ScenarioFiles.HostsDiagnostics(exe) == false);
            Assert.True(attached[0].Status == "Unbound", attached[0].Status + ": " + attached[0].Problem);
            var report = Report(exe, CompiledRun(), new CatalogFacts(false, 0, 0, 0, null, attached));
            Assert.True(Finding(report, "HOST001").Blocking);
            var fixture = Finding(report, "FIX001");
            Assert.Contains("sample/notes", fixture.Message);
            Assert.Contains("no Proxy mode is substituted", fixture.Action);
            // Proxy scenarios on the same ordinary application stay runnable: missing diagnostic support never blocks them.
            Assert.True(Report(exe, ProxyRun()).Ready);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void StaticInspectionStartsNothingWritesNothingAndFollowsTheDeployment()
    {
        if (PreparedTvDemo() is null) return;
        string root = Temp();
        try
        {
            string exe = CopyTvDemo(root);
            string folder = Path.GetDirectoryName(exe)!;
            string Snapshot() => string.Join("|", Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order()
                .Select(f => f + ":" + (File.Exists(f) ? new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks : "d")));
            string before = Snapshot();
            var watch = Stopwatch.StartNew();
            var first = Report(exe, ProxyRun("SDLDriver"));
            Assert.True(first.Ready);
            Assert.Equal(0, Running(exe));
            Assert.Equal(before, Snapshot());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "static inspection is bounded");
            // The deployment changes on disk: the next inspection sees it (no stale cache), and again after a repair.
            File.Move(Path.Combine(folder, TargetDiagnostics.ProxyFile), Path.Combine(root, TargetDiagnostics.ProxyFile));
            Assert.Equal("Proxy missing", Report(exe, ProxyRun()).Readiness);
            File.Move(Path.Combine(root, TargetDiagnostics.ProxyFile), Path.Combine(folder, TargetDiagnostics.ProxyFile));
            Assert.True(Report(exe, ProxyRun()).Ready);
            // Another executable path is another inspection.
            Assert.Equal("Target missing", Report(Path.Combine(folder, "missing.exe"), ProxyRun()).Readiness);
            Assert.Equal("No target", Report("", ProxyRun()).Readiness);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task VerifyTargetHandshakesAndAlwaysCleansUp()
    {
        if (PreparedTvDemo() is null || !OperatingSystem.IsWindows()) return;
        string root = Temp();
        try
        {
            var runner = new ScenarioRunner();
            string ready = CopyTvDemo(root);
            var verified = await runner.VerifyAsync(ready, proxy: true);
            Assert.True(verified.Proxy);
            Assert.Equal("NullDriver", verified.Identity.Driver);
            Assert.Equal((80, 25), (verified.Identity.Width, verified.Identity.Height));
            Assert.True(verified.Cleanup);
            Assert.True(verified.Duration < TimeSpan.FromSeconds(ScenarioRunner.VerifySeconds + 5));
            Assert.Equal(0, Running(ready));
            // Missing Proxy: the real startup error, classified; the process is gone and the temporary root removed.
            string broken = CopyTvDemo(root, TargetDiagnostics.ProxyFile);
            var error = await Assert.ThrowsAsync<TargetVerificationException>(() => runner.VerifyAsync(broken, proxy: true));
            Assert.True(error.Cleanup);
            var failure = StartupFailure.Classify(error.Message);
            Assert.Equal(StartupFailureKind.MissingManagedAssembly, failure.Kind);
            Assert.Equal("Required assembly TSharpWorkbench.Proxy.dll was not found.", failure.Reason);
            Assert.Equal(0, Running(broken));
            // Cancellation stops and cleans the session as well.
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.VerifyAsync(ready, proxy: true, token: cancel.Token));
            await Task.Delay(500);
            Assert.Equal(0, Running(ready));
        }
        finally { Delete(root); }
    }

    // ── B. startup failures in Results ───────────────────────────────────────────────────

    [Fact]
    public void StartupFailuresAreClassifiedOnlyWhenTheCauseIsClear()
    {
        var proxy = StartupFailure.Classify(ProxyFailure);
        Assert.Equal((StartupFailureKind.MissingManagedAssembly, "Required assembly TSharpWorkbench.Proxy.dll was not found."), (proxy.Kind, proxy.Reason));
        Assert.Equal(ProxyFailure, proxy.Technical);
        Assert.Equal(StartupFailureKind.MissingNativeDependency, StartupFailure.Classify("Target exited (1) before connecting; System.DllNotFoundException: Unable to load DLL 'SDL3.dll' or one of its dependencies").Kind);
        Assert.Equal(StartupFailureKind.IncompatibleDeployment, StartupFailure.Classify("Target exited (1) before connecting; System.IO.FileLoadException: Could not load file or assembly 'TSharpVision, Version=1.2.0.0'").Kind);
        Assert.Equal(StartupFailureKind.HandshakeFailure, StartupFailure.Classify("Target protocol schema mismatch. [launch, grid 80x25]").Kind);
        Assert.Equal(StartupFailureKind.RuntimeInitialization, StartupFailure.Classify("Target exited (1) before connecting; System.TypeInitializationException: boom").Kind);
        Assert.Equal(StartupFailureKind.ExitedUnexpectedly, StartupFailure.Classify("Target exited (3) before connecting; verify the explicitly selected automation mode and matching deployment.").Kind);
        Assert.Equal(StartupFailureKind.MissingExecutable, StartupFailure.Classify("Target executable does not exist.").Kind);
        var unknown = StartupFailure.Classify("Diagnostic operation failed (IOException). [launch, grid 80x25]");
        Assert.Equal((StartupFailureKind.Unknown, "Target failed to start or connect. See technical details."), (unknown.Kind, unknown.Reason));
        // Only executions that ended at launch are startup failures; categories are never changed.
        ScenarioResult Result(string outcome, string reason) => new("tvdemo/about", "80x25", outcome, reason, [], false, false, true, ScenarioOutcomes.Passed);
        Assert.NotNull(StartupFailure.Of(Result(ScenarioOutcomes.Error, ProxyFailure)));
        Assert.Null(StartupFailure.Of(Result(ScenarioOutcomes.Failed, "1 assertion(s) did not pass. [step 2 (checkpoint about), grid 80x25]")));
        Assert.Null(StartupFailure.Of(Result(ScenarioOutcomes.Error, "Target operation deadline expired. [step 1 (key kbF10), grid 80x25]")));
        Assert.Null(StartupFailure.Of(Result(ScenarioOutcomes.Blocked, "Scenario does not support grid 300x10.")));
    }

    [Fact]
    public void ResultsOverviewIsFriendlyAndKeepsTheTechnicalText()
    {
        var batch = new BatchResult("", [new("tvdemo/about", "80x25", ScenarioOutcomes.Error, ProxyFailure, [], false, false, true, ScenarioOutcomes.Passed)]) { Driver = "NullDriver" };
        var rows = ResultText.Overview(batch, batch.Results[0], null, null, null);
        Assert.Equal("Result     ERROR - Target startup failed", rows[0].Text);
        Assert.Equal("Reason     Required assembly TSharpWorkbench.Proxy.dll was not found.", rows[1].Text);
        Assert.StartsWith("Action     ", rows[2].Text);
        var technical = Assert.Single(rows, r => r.Technical is not null);
        Assert.Equal(ProxyFailure, technical.Technical);
        Assert.DoesNotContain(rows, r => r.Text.Contains("ScreenDriverFactory.Decorate") && r.Technical is null);
        // The category is unchanged: an error, counted as failed, exit code of an error.
        Assert.Equal(ScenarioOutcomes.Error, batch.Results[0].Outcome);
        Assert.Equal((0, 1, 0, 0), WorkbenchDialog.Categories(batch));
        Assert.Equal(9, Cli.ProjectExitCode(batch));
        // Any other failure keeps its original Result/Reason presentation (historical runs read the same way).
        var failed = batch with { Results = [new("tvdemo/about", "80x25", ScenarioOutcomes.Failed, "1 assertion(s) did not pass. [step 2 (checkpoint about), grid 80x25]", [], true, true, true, ScenarioOutcomes.Passed)] };
        var plain = ResultText.Overview(failed, failed.Results[0], null, null, null);
        Assert.StartsWith("Result     FAILED", plain[0].Text);
        Assert.Equal("Reason     1 assertion(s) did not pass. [step 2 (checkpoint about), grid 80x25]", plain[1].Text);
        Assert.DoesNotContain(plain, r => r.Technical is not null);
    }

    // ── C. presentation preference ────────────────────────────────────────────────────

    [Fact]
    public void BasicIsTheDefaultAndAStoredChoiceIsKept()
    {
        Assert.Equal(WorkbenchPreferences.BasicView, WorkbenchPreferences.ResolveRunView(null));
        Assert.Equal(WorkbenchPreferences.BasicView, WorkbenchPreferences.ResolveRunView("unknown"));
        Assert.Equal(WorkbenchPreferences.AdvancedView, WorkbenchPreferences.ResolveRunView("advanced"));
        Assert.Equal(WorkbenchPreferences.BasicView, WorkbenchPreferences.ResolveRunView("basic", WorkbenchPreferences.AdvancedView));
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            // A settings file written before the redesign (no RunView) loads unchanged and resolves to the product default.
            string old = Path.Combine(root, "old.json");
            File.WriteAllText(old, """{"Target":"a.exe","Output":"runs","Group":null,"Size":"80x25","RecentProjects":["p.twproj"]}""");
            var loaded = WorkbenchPreferences.Load(old);
            Assert.Null(loaded.RunView);
            Assert.Equal(["p.twproj"], loaded.RecentProjects);
            Assert.Equal(WorkbenchPreferences.BasicView, WorkbenchPreferences.ResolveRunView(loaded.RunView));
            // The choice is stored next to the other user settings and never removes them.
            WorkbenchPreferences.Update(old, p => p with { RunView = WorkbenchPreferences.AdvancedView });
            var updated = WorkbenchPreferences.Load(old);
            Assert.Equal((WorkbenchPreferences.AdvancedView, "a.exe"), (updated.RunView, updated.Target));
            Assert.Equal(["p.twproj"], updated.RecentProjects);
        }
        finally { Delete(root); }
    }

    // ── D. Workbench UI ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task BasicRunConfigurationLayoutSwitchingAndSessionSafety(int width, int height)
    {
        string root = Temp();
        try
        {
            await Drive(width, height, root, TestWorkbenchHostTests.SampleTarget, WorkbenchPreferences.BasicView, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge, 16);
                string before = "", session = "";
                await bridge.Invoke(() => { main.TestList.Toggle("sample/status/context"); before = main.CaptureProject().Fingerprint(); session = main.SessionRun.Fingerprint(); });
                Press(Keys.kbF5);
                var basic = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunBasicDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(WorkbenchRunBasicDialog.DialogTitle, basic.title);
                    var desk = app.DeskTop!;
                    Assert.True(basic.size.x <= 86 && basic.size.y <= 32 && basic.size.y <= desk.size.y && basic.size.x <= desk.size.x);
                    Assert.Same(basic.RunButton, basic.current);
                    Assert.False(basic.RunButton.GetState(Views.sfDisabled));
                    Assert.Equal(0u, basic.SaveDefaults.value);
                    Assert.Empty(ProductQualificationTests.DuplicateMnemonics(basic));
                    // Buttons on one row, an empty row above, the shadow row above the bottom frame.
                    var buttons = Children(basic).OfType<TButton>().Where(b => b.GetState(Views.sfVisible)).OrderBy(b => b.origin.x).ToArray();
                    Assert.Equal(["Advanc~e~d >>", "~T~arget details...", "~R~un", "Cancel"], buttons.Select(b => b.Title).ToArray());
                    Assert.All(buttons, b => Assert.Equal(basic.size.y - 3, b.origin.y));
                    Assert.True(basic.SaveDefaults.origin.y == basic.size.y - 5);
                    // No group boxes: Basic is plain labels and controls.
                    Assert.DoesNotContain(Children(basic), v => v is TGroupBox);
                    Assert.Contains("1 scenario x 1 size = 1 execution", basic.SummaryText);
                    Assert.Contains("Target: TSharpWorkbench.SampleTarget.exe - Ready", basic.SummaryText);
                    Assert.Contains("Driver: headless (NullDriver)", basic.SummaryText);
                    Assert.Contains("Native diagnostic unavailable", basic.Inline);
                    Assert.False(basic.Mode.ButtonState(2));
                    // A compiled catalog: window drivers are disabled, not hidden.
                    Assert.True(basic.Display.ButtonState(0)); Assert.False(basic.Display.ButtonState(1));
                });
                await Shot(bridge, $"basic-ready-{width}x{height}", "Basic Run Configuration, ready (compiled SampleTarget)");
                // Tab follows the reading order: scope, display, sizes, custom, mode, save defaults, buttons.
                var order = new List<string>();
                await bridge.Invoke(() => basic.Scope.Focus());
                for (int i = 0; i < 9; i++)
                {
                    await bridge.Invoke(() => order.Add(basic.current switch { TButton b => b.Title, { } v when v == basic.Scope => "scope", { } v when v == basic.Display => "display",
                        { } v when v == basic.Sizes => "sizes", { } v when v == basic.Mode => "mode", { } v when v == basic.SaveDefaults => "defaults", { } v => v.GetType().Name, null => "" }));
                    Press(Keys.kbTab); await Settle(bridge);
                }
                Assert.Equal(["scope", "display", "sizes", "mode", "defaults", "Advanc~e~d >>", "~T~arget details...", "~R~un", "Cancel"], order);
                // Invalid custom size: Run disabled, the problem is shown first, Enter does not run.
                await bridge.Invoke(() => { basic.Sizes.value = 1 | 4; basic.CustomSizes.SetData("100x30,300x10"); Nudge(basic); });
                await bridge.Invoke(() =>
                {
                    Assert.True(basic.RunButton.GetState(Views.sfDisabled));
                    Assert.StartsWith("! Cannot run: Custom size '300x10'", basic.SummaryText);
                });
                // Switch to Advanced: the same values arrive (scope, sizes text, mode); nothing runs, nothing is applied.
                await bridge.Invoke(() => { basic.Scope.value = 2; basic.Mode.value = 1; basic.SaveDefaults.value = 1; Nudge(basic); });
                Press(Keys.kbAltE);
                var advanced = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal((2u, 1u, 5u, "100x30,300x10", 1u), (advanced.Scope.value, advanced.Mode.value, advanced.Sizes.value, advanced.CustomSizes.Data, advanced.SaveDefaults.value));
                    Assert.True(advanced.RunButton.GetState(Views.sfDisabled));
                    Assert.Equal("<< ~B~asic", advanced.BasicButton.Title);
                    Assert.False(main.Busy); Assert.Equal(before, main.CaptureProject().Fingerprint());
                    Assert.Equal(WorkbenchPreferences.AdvancedView, main.RunView);
                    // Advanced keeps its seven group boxes.
                    Assert.Equal(7, Children(advanced).OfType<TGroupBox>().Count());
                    Assert.True(advanced.size.x > basic.size.x && advanced.size.y > basic.size.y);
                    advanced.CustomSizes.SetData("100x30"); advanced.StepTimeout.SetData("5000"); advanced.VisualPolicy.value = 1; Nudge(advanced);
                });
                await Shot(bridge, $"advanced-{width}x{height}", "Advanced Run Configuration (sections, << Basic)");
                // Back to Basic: the Advanced-only choices are kept and disclosed.
                Press(Keys.kbAltB);
                basic = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunBasicDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal((2u, 1u, 5u, "100x30", 1u), (basic.Scope.value, basic.Mode.value, basic.Sizes.value, basic.CustomSizes.Data, basic.SaveDefaults.value));
                    Assert.Contains("Advanced options active: Keep baseline candidates for review", basic.SummaryText);
                    Assert.Contains("step timeout 5000 ms", basic.SummaryText);
                    Assert.Contains("Comparison: Compare and keep candidates", basic.SummaryText);
                    Assert.Equal(WorkbenchPreferences.BasicView, main.RunView);
                    Assert.Equal(before, main.CaptureProject().Fingerprint()); Assert.Equal(session, main.SessionRun.Fingerprint());
                });
                await Shot(bridge, $"basic-visual-{width}x{height}", "Basic Run Configuration, Visual regression with an Advanced option disclosed");
                // Esc cancels: session and project unchanged.
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunBasicDialog);
                await bridge.Invoke(() => { Assert.Equal(before, main.CaptureProject().Fingerprint()); Assert.Equal(session, main.SessionRun.Fingerprint()); });
                // Close (X) cancels the same way; reopening starts unchecked.
                Press(Keys.kbF5); basic = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunBasicDialog);
                await bridge.Invoke(() => Assert.Equal(0u, basic.SaveDefaults.value));
                Send(Views.cmClose); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunBasicDialog);
                // Run from Basic: session only, project not dirty, the run is the configured one.
                Press(Keys.kbF5); basic = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunBasicDialog);
                await bridge.Invoke(() => { basic.Sizes.value = 1 | 2; Nudge(basic); });
                Press(Keys.kbEnter);
                await Until(bridge, () => main.Busy);
                await Until(bridge, () => !main.Busy, 120);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(["80x25", "140x40"], main.SessionRun.ScreenSizes);
                    Assert.Equal(before, main.CaptureProject().Fingerprint());
                });
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ProxyProjectBlockedByMissingProxyAndFriendlyStartupFailure()
    {
        if (PreparedTvDemo() is null) return;
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyTvDemo(root, TargetDiagnostics.ProxyFile);
            string about = Path.Combine(RepositoryRoot(), "examples", "TVDemo", "scenarios", "about.scenario.json");
            string projectPath = Path.Combine(root, "tvdemo.twproj");
            new WorkbenchProject { Name = "TVDemo", Target = exe, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [about], SelectedIds = ["tvdemo/about"],
                ScreenSizes = ["80x25", "140x40"], ProxyBackend = "SDLDriver" }.Save(projectPath);
            string projectText = File.ReadAllText(projectPath);
            await Drive(140, 40, root, TestWorkbenchHostTests.SampleTarget, WorkbenchPreferences.BasicView, async (app, bridge) =>
            {
                await Loaded(app, bridge, 16);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                var main = await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count == 1 && p.ProjectPath == projectPath ? p : null, 60);
                await bridge.Invoke(() => Assert.True(main.HeaderText.Contains("TSharpVision.Samples.TVDemo.exe | Proxy missing | Prepare target... |"), main.HeaderText));
                await Shot(bridge, "workspace-proxy-missing-140x40", "Workspace header readiness: Proxy missing");
                // Scenarios window: the document is valid; the deployment is not ready.
                await bridge.Invoke(() =>
                {
                    var a = Assert.Single(main.AttachedScenarios);
                    Assert.Equal("Valid", a.Status);
                    Assert.Null(a.Problem);
                    Assert.Contains("Target deployment: not ready", a.Note);
                });
                // Basic Run Configuration: blocked before anything starts, with the corrective action.
                Press(Keys.kbF5);
                var basic = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunBasicDialog);
                await bridge.Invoke(() =>
                {
                    Assert.True(basic.RunButton.GetState(Views.sfDisabled));
                    Assert.StartsWith("! Target not ready: Proxy assembly missing: TSharpWorkbench.Proxy.dll.", basic.SummaryText);
                    Assert.Contains("Target: TSharpVision.Samples.TVDemo.exe - Not ready: Proxy assembly missing", basic.SummaryText);
                    Assert.Equal(1u, basic.Display.value);   // SDL window stays selected; nothing substituted
                    Assert.Contains(basic.Problems, p => p.Area == RunArea.Target);
                });
                await Shot(bridge, "basic-blocked-140x40", "Basic Run Configuration blocked: Proxy assembly missing");
                Press(Keys.kbEnter); await Settle(bridge);
                await bridge.Invoke(() => { Assert.False(main.Busy); Assert.Same(basic, app.DeskTop!.TopView()); });
                Assert.Equal(0, Running(exe));
                // Target details from Basic (without switching to Advanced): the full report, read-only.
                Press(Keys.kbAltT);
                var details = await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchTargetDiagnosticsDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Contains(details.ReportLines, l => l.Contains("PROXY001") && l.Contains("[blocks the selected operation]"));
                    Assert.Contains(details.ReportLines, l => l.Contains("Searched: " + Path.Combine(Path.GetDirectoryName(exe)!, TargetDiagnostics.ProxyFile)));
                    Assert.Contains(details.ReportLines, l => l.Contains("SDL window (SDLDriver)"));
                });
                await Shot(bridge, "target-diagnostics-details-140x40", "Target Diagnostics details (static inspection)");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is WorkbenchRunBasicDialog);
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchRunBasicDialog);
                // Project Properties shows the compact diagnostics of the edited executable.
                Press(Keys.kbAltO);
                var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: "Project Properties" } d ? d : null);
                await bridge.Invoke(() =>
                {
                    Assert.Contains("Proxy      ERR  Proxy missing", props.DiagnosticsText);
                    Assert.Contains("Readiness  ERR  Proxy scenarios cannot run", props.DiagnosticsText);
                    Assert.Contains("Executable OK   TSharpVision.Samples.TVDemo.exe", props.DiagnosticsText);
                    Assert.Empty(ProductQualificationTests.DuplicateMnemonics(props));
                    Assert.True(props.origin.y + props.size.y <= app.DeskTop!.size.y);
                });
                await Shot(bridge, "properties-missing-proxy-140x40", "Project Properties, Target diagnostics: Proxy missing");
                // The deployment is repaired while the dialog is open: the area follows it.
                File.Copy(Path.Combine(Path.GetDirectoryName(PreparedTvDemo()!)!, TargetDiagnostics.ProxyFile), Path.Combine(Path.GetDirectoryName(exe)!, TargetDiagnostics.ProxyFile));
                await bridge.Invoke(() => Nudge(props));
                await bridge.Invoke(() => { Assert.Contains("Proxy      OK   Proxy found", props.DiagnosticsText); Assert.Contains("Readiness  OK   Ready", props.DiagnosticsText); });
                await Shot(bridge, "properties-ready-140x40", "Project Properties, Target diagnostics: ready (static checks)");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPropertiesDialog);
                await bridge.Invoke(() => { Assert.False(main.Dirty); Assert.Contains("| Ready |", main.HeaderText); });
                // Nothing was run so far. Now a real startup failure through the runner itself (the engine has no preflight;
                // this is what an unprepared deployment did before the redesign), and Results explains it.
                Assert.False(Directory.Exists(Path.Combine(root, "runs")));
                File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, TargetDiagnostics.ProxyFile));
                var scenario = ExternalScenarios.Merge([], [ExternalScenarios.Load(about)]).Single();
                var failed = await new ScenarioRunner().RunAsync(exe, [scenario], new RunOptions(Path.Combine(root, "runs"), ["80x25"]));
                var result = Assert.Single(failed.Results);
                Assert.Equal(ScenarioOutcomes.Error, result.Outcome);
                Assert.Contains("TSharpWorkbench.Proxy", result.Reason);
                Assert.Equal(0, Running(exe));
                await bridge.Invoke(() => main.NavigateRuns(-1));
                var results = await Wait(bridge, () => app.DeskTop!.current as WorkbenchResultsDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Contains(results.InspectorRows, r => r.StartsWith("Result     ERROR - Target startup failed", StringComparison.Ordinal));
                    Assert.Contains(results.InspectorRows, r => r.Contains("Required assembly TSharpWorkbench.Proxy.dll was not found."));
                    Assert.DoesNotContain(results.InspectorRows, r => r.Contains(" at TSharpVision."));
                    int technical = Array.FindIndex(results.InspectorRows, r => r.Contains("Technical details..."));
                    Assert.True(technical > 0);
                    results.Inspector.FocusItemNum(technical); results.Inspector.Select();
                });
                await Shot(bridge, "results-startup-failure-140x40", "Results: friendly startup failure (missing Proxy), category ERROR kept");
                Press(Keys.kbEnter);
                var viewer = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchResultsDialog.TechnicalTitle } d ? d : null);
                await bridge.Invoke(() =>
                {
                    var list = Children(viewer).OfType<TListBox>().Single();
                    string all = string.Concat(Enumerable.Range(0, list.range).Select(i => list.GetText(i, 400)));
                    Assert.Contains("FileNotFoundException", all);
                    Assert.Contains("TSharpWorkbench.Proxy", all);
                });
                await Shot(bridge, "technical-details-140x40", "Technical Details: original exception and target stderr");
                Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog { title: WorkbenchResultsDialog.TechnicalTitle });
            });
            Assert.Equal(projectText, File.ReadAllText(projectPath));
        }
        finally { Delete(root); }
    }

    // ── harness ──────────────────────────────────────────────────────────────────────

    private static async Task Drive(int width, int height, string root, string target, string view, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs-sample");
        string settings = Path.Combine(root, "settings.json");
        new WorkbenchPreferences(target, output, null, "80x25") { RunView = view }.Save(settings);
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = settings });
        var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try { await body(app, bridge); await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); }); }
            catch (Exception ex)
            {
                failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                await bridge.Invoke(() =>
                {
                    for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                    app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                }).WaitAsync(TimeSpan.FromSeconds(10));
            }
        });
        AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(30)), "Worker did not stop.");
        Assert.Null(failure);
    }
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge, int count) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= count ? p : null, 60);
    private static async Task Shot(Bridge bridge, string name, string state)
    {
        if (Environment.GetEnvironmentVariable("TSWB_DIAGNOSTICS_EVIDENCE") is not { Length: > 0 } root) return;
        await Settle(bridge);
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, captureType = "logical", driver = "NullDriver", grid = $"{screen.Width}x{screen.Height}", test = nameof(TargetDiagnosticsUxTests),
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
