using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using H = TSharpWorkbench.Tests.Core.PrepareTests;
using C = TSharpWorkbench.Tests.Core.CompatibilityTests;

namespace TSharpWorkbench.Tests.Core;

// The destination of a prepared copy (engine and the real Prepare Target window), and the risk-accepted
// Fix in the real Fix Issues window. Targets as CompatibilityTests. Logical captures (NullDriver cell
// rendering, NOT native screenshots): TSWB_PUBLISH_CAPTURE.
[Collection("NonParallel")]
public sealed class DestinationTests
{
    private static Dictionary<string, string> Tree(string folder) => Directory.Exists(folder)
        ? Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(folder, f), f => File.Exists(f) ? Preparation.FileHash(f) : "<dir>", StringComparer.OrdinalIgnoreCase)
        : [];

    // ── engine: what a destination is ────────────────────────────────────────────────────────

    [Fact]
    public async Task DestinationChecksClassifyEveryKindAndChangeNothing()
    {
        if (!C.Available(nameof(DestinationChecksClassifyEveryKindAndChangeNothing))) return;
        string root = C.Temp();
        try
        {
            string exe = C.Ordinary(root);
            string source = Path.GetDirectoryName(exe)!;
            var service = C.Service(root);
            var inspection = service.Inspect(exe);
            string out_ = Path.Combine(root, "out");
            Directory.CreateDirectory(Path.Combine(out_, "empty"));
            Directory.CreateDirectory(Path.Combine(out_, "full")); File.WriteAllText(Path.Combine(out_, "full", "keep.txt"), "user data");
            File.WriteAllText(Path.Combine(out_, "file"), "a file");
            // A junction to a plain folder (no elevation needed).
            Directory.CreateDirectory(Path.Combine(root, "real"));
            string junction = Path.Combine(out_, "junction");
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.Combine(root, "real")}\"") { CreateNoWindow = true, UseShellExecute = false }))
                mklink!.WaitForExit();
            var before = Tree(root);
            DestinationState Check(string? input) => PreparationDestination.Check(input, inspection, service, Path.Combine(service.Root, "proposed"));
            Assert.Equal(DestinationKind.Proposed, Check(null).Kind);
            var nested = Check(Path.Combine(out_, "a", "b", "c"));
            Assert.Equal(DestinationKind.New, nested.Kind);
            Assert.Equal(3, nested.Missing.Length);
            Assert.True(nested.Usable);
            Assert.Equal(DestinationKind.Empty, Check(Path.Combine(out_, "empty") + "\\").Kind);
            Assert.Equal(DestinationKind.NotEmpty, Check(Path.Combine(out_, "full")).Kind);
            Assert.Equal(DestinationKind.Conflict, Check(Path.Combine(out_, "file")).Kind);
            foreach (var (input, why) in new[]
            {
                ("", "Enter a destination"), ("relative\\folder", "full path"), (Path.Combine(out_, "x", "..", "y"), "not allowed"), (Path.Combine(out_, "CON"), "reserved"),
                (Path.Combine(out_, "bad|name"), "not allowed"), (Path.Combine(source, "inside"), "inside the application"), (root, "contains the application"),
                (Path.GetPathRoot(root)!, "root"), (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WorkbenchTest"), "protected"),
                (Path.Combine(junction, "copy"), "symbolic link or junction"), (Path.Combine(AppContext.BaseDirectory, "copy"), "Workbench installation")
            })
            {
                var state = Check(input);
                Assert.Equal(DestinationKind.Invalid, state.Kind);
                Assert.False(state.Usable);
                Assert.Contains(why, state.Message, StringComparison.OrdinalIgnoreCase);
            }
            // A plan with an unusable destination cannot execute and writes nothing.
            var refused = service.CreatePlan(inspection, PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl, Path.Combine(out_, "full"));
            Assert.False(refused.CanExecute);
            Assert.StartsWith("Destination: ", refused.Problems[0]);
            Assert.False((await service.ExecuteAsync(refused)).Success);
            Assert.Equal(before, Tree(root));
        }
        finally { C.Delete(root); }
    }

    [Fact]
    public async Task PrepareIntoAChosenNestedFolderRecordsItAndTheCopyRunsAScenario()
    {
        if (!C.Available(nameof(PrepareIntoAChosenNestedFolderRecordsItAndTheCopyRunsAScenario))) return;
        string root = C.Temp();
        try
        {
            string exe = C.Ordinary(root);
            var original = C.Snapshot(Path.GetDirectoryName(exe)!);
            var service = C.Service(root);
            string chosen = Path.Combine(root, "tests", "nested", "ordinary-copy");
            var plan = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl, chosen);
            Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
            Assert.Equal(chosen, plan.Destination);
            Assert.Equal(chosen, plan.ChosenDestination);
            Assert.Contains(plan.FileLines(), l => l.StartsWith("Destination  " + chosen));
            var result = await service.ExecuteAsync(plan);
            Assert.True(result.Success, result.Message);
            Assert.Equal(Path.Combine(chosen, Path.GetFileName(exe)), result.PreparedExecutable);
            Assert.Contains(chosen, result.Message);
            Assert.True(File.Exists(Path.Combine(chosen, PreparationManifest.FileName)));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(chosen)!, ".tsw-staging-*"));
            Assert.Equal(original, C.Snapshot(Path.GetDirectoryName(exe)!));          // the original is only read
            // The copy is the application's active deployment (recorded by its full path) and Ready.
            TargetPreparationService.ForgetStatus();
            var existing = service.Existing(exe, deep: true);
            Assert.NotNull(existing);
            Assert.Equal(PreparedState.Ready, existing!.State);
            Assert.True(WorkbenchPreferences.SamePath(chosen, existing.Folder));
            Assert.Equal(PreparedState.Ready, service.Inspect(exe).Existing?.State);
            // The same folder again is an existing copy (never overwritten); its free sibling is proposed instead.
            var again = PreparationDestination.Check(chosen, service.Inspect(exe), service, "");
            Assert.Equal(DestinationKind.ExistingCopy, again.Kind);
            Assert.Contains("this application", again.Message);
            Assert.False(service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl, chosen).CanExecute);
            Assert.Equal(chosen + "-2", PreparationDestination.NextFree(chosen));
            // Run from the prepared copy: Verify target and a JSON Proxy scenario.
            string prepared = result.PreparedExecutable!;
            var verified = await new ScenarioRunner().VerifyAsync(prepared, true);
            Assert.True(verified.Proxy && verified.Cleanup);
            var catalog = ExternalScenarios.Merge([], [ExternalScenarios.Load(Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "diagnostics", "ProxyOrdinary", "counter.scenario.json"))]).ToArray();
            var batch = await new ScenarioRunner().RunAsync(prepared, catalog, new RunOptions(Path.Combine(root, "runs"), ["80x25"]) { ProxyBackend = "NullDriver" });
            Assert.Equal(ScenarioOutcomes.Passed, Assert.Single(batch.Results).Outcome);
            C.Evidence("destination-nested-copy.txt", [result.Message, .. result.Details, $"verify: proxy={verified.Proxy} cleanup={verified.Cleanup}", $"scenario: {batch.Results[0].Outcome}"]);
        }
        finally { C.Delete(root); }
    }

    [Fact]
    public async Task CancelledOrFailedPreparationLeavesTheChosenFolderAsItWas()
    {
        if (!C.Available(nameof(CancelledOrFailedPreparationLeavesTheChosenFolderAsItWas))) return;
        string root = C.Temp();
        try
        {
            string exe = C.Ordinary(root);
            using var cancel = new CancellationTokenSource();
            int written = 0;
            var service = C.Service(root, path => { if (!path.StartsWith('<') && ++written == 2) cancel.Cancel(); });
            // Missing parents: none of them stays.
            string nested = Path.Combine(root, "made", "by", "prepare");
            var plan = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl, nested);
            var result = await service.ExecuteAsync(plan, null, cancel.Token);
            Assert.False(result.Success);
            Assert.True(result.Cancelled);
            Assert.True(result.RollbackComplete);
            Assert.False(Directory.Exists(Path.Combine(root, "made")));
            // An empty folder the user made: still there, still empty.
            string empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var failing = C.Service(root, path => { if (path == "<verify>") throw new IOException("simulated failure after publishing"); });
            var plan2 = failing.CreatePlan(failing.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl, empty);
            Assert.Equal(DestinationKind.Empty, plan2.DestinationCheck!.Kind);
            var result2 = await failing.ExecuteAsync(plan2);
            Assert.False(result2.Success);
            Assert.True(Directory.Exists(empty));
            Assert.Empty(Directory.GetFileSystemEntries(empty));
            Assert.Empty(Directory.GetDirectories(root, ".tsw-staging-*"));
        }
        finally { C.Delete(root); }
    }

    [Fact]
    public async Task UnwritableDestinationFailsBeforeAnythingIsCreated()
    {
        if (!OperatingSystem.IsWindows() || !C.Available(nameof(UnwritableDestinationFailsBeforeAnythingIsCreated))) return;
        string root = C.Temp();
        string locked = Path.Combine(root, "locked");
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.WriteData,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        try
        {
            string exe = C.Ordinary(root);
            Directory.CreateDirectory(locked);
            var info = new DirectoryInfo(locked);
            var acl = info.GetAccessControl(); acl.AddAccessRule(rule); info.SetAccessControl(acl);
            var service = C.Service(root);
            var plan = service.CreatePlan(service.Inspect(exe), PreparationStrategy.PreparedCopy, PreparationCapabilities.Proxy | PreparationCapabilities.Sdl, Path.Combine(locked, "copy"));
            Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));   // the folder looks writable; the write itself decides
            var result = await service.ExecuteAsync(plan);
            Assert.False(result.Success);
            Assert.Contains("not writable", result.Message);
            Assert.Empty(Directory.GetFileSystemEntries(locked));
        }
        finally
        {
            if (Directory.Exists(locked)) { var info = new DirectoryInfo(locked); var acl = info.GetAccessControl(); acl.RemoveAccessRule(rule); info.SetAccessControl(acl); }
            C.Delete(root);
        }
    }

    [Fact]
    public void NewFolderNamesAreValidated()
    {
        Assert.Null(PreparationDestination.NameProblem("TVDemo-copy", nested: false));
        Assert.Null(PreparationDestination.NameProblem(@"tests\TVDemo", nested: true));
        Assert.NotNull(PreparationDestination.NameProblem(@"tests\TVDemo", nested: false));
        foreach (string bad in new[] { "", "  ", "..", @"a\..\b", @"C:\abs", "con", "name.", "a*b", @"a\\b" })
            Assert.NotNull(PreparationDestination.NameProblem(bad, nested: true));
    }

    // ── the real Prepare Target window ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task PrepareWindowShowsTheDestinationCreatesAFolderKeepsItAcrossModeSwitchesAndOffersOpen(int width, int height)
    {
        if (!C.Available(nameof(PrepareWindowShowsTheDestinationCreatesAFolderKeepsItAcrossModeSwitchesAndOffersOpen))) return;
        string root = C.Temp();
        var opened = new List<string>();
        var open = WorkbenchPrepareTargetDialog.OpenFolder;
        WorkbenchPrepareTargetDialog.OpenFolder = opened.Add;
        try
        {
            string grid = $"{width}x{height}";
            string exe = C.Ordinary(root);
            var original = C.Snapshot(Path.GetDirectoryName(exe)!);
            C.Service(root);
            string projectPath = H.Project(root, exe, tvdemo: false);
            string made = "";
            await H.Drive(width, height, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                await H.Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == projectPath && p.TestList.Strings.Count == 1 ? p : null, 60);
                H.Send(WorkbenchDialog.PrepareTargetCommand);
                var prepare = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(DestinationKind.Proposed, prepare.Destination.Kind);
                    Assert.StartsWith(Path.Combine(root, "prepared"), prepare.DestinationField.Data);
                    Assert.Contains(prepare.Destination.Path, prepare.DestinationText.Replace("\n", ""));   // the full path, never elided
                    Assert.False(prepare.PrepareButton.GetState(Views.sfDisabled));
                    H.Layout(prepare, app.DeskTop!);
                });
                await H.Shot(bridge, $"prepare-destination-proposed-{grid}", "Prepare a copy: proposed destination with Browse and New folder");
                // Browse...: the framework directory tree; Esc changes nothing.
                H.Press(Keys.kbAltB);
                var browse = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchDirectoryDialog);
                await H.Shot(bridge, $"prepare-destination-browse-{grid}", "Browse... (directory tree; creates nothing)");
                H.Press(Keys.kbEsc);
                await H.Until(bridge, () => app.DeskTop!.TopView() == prepare);
                // New folder...: Cancel creates nothing; Create makes exactly the folder it shows.
                H.Press(Keys.kbAltN);
                var folder = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchNewFolderDialog);
                await bridge.Invoke(() => { made = folder.CreatesText; Assert.EndsWith("ProxyOrdinary-prepared", made); H.Layout(folder, app.DeskTop!); });
                await H.Shot(bridge, $"prepare-new-folder-{grid}", "New folder: name, full path, Create / Cancel");
                H.Press(Keys.kbEsc);
                await H.Until(bridge, () => app.DeskTop!.TopView() == prepare);
                Assert.False(Directory.Exists(made));
                H.Press(Keys.kbAltN);
                folder = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchNewFolderDialog);
                await bridge.Invoke(() => { folder.NameField.SetData(@"..\escape"); H.Nudge(folder); Assert.StartsWith("! ", folder.MessageText); });
                H.Press(Keys.kbEnter);   // refused: stays open, creates nothing
                await H.Settle(bridge);
                await bridge.Invoke(() => Assert.Same(folder, app.DeskTop!.TopView()));
                await bridge.Invoke(() => { folder.NameField.SetData("ProxyOrdinary-prepared"); H.Nudge(folder); });
                H.Press(Keys.kbEnter);
                await H.Until(bridge, () => app.DeskTop!.TopView() == prepare);
                Assert.True(Directory.Exists(made));
                Assert.False(Directory.Exists(Path.Combine(root, "..", "escape")));
                await bridge.Invoke(() =>
                {
                    Assert.Equal(made, prepare.DestinationField.Data);
                    Assert.Equal(DestinationKind.Empty, prepare.Destination.Kind);
                    Assert.Equal(made, prepare.ChosenDestination);
                });
                await H.Shot(bridge, $"prepare-destination-chosen-{grid}", "Prepare a copy: the new empty folder as destination");
                // An unusable destination disables Prepare and says why.
                await bridge.Invoke(() => { prepare.SetDestination(Path.GetDirectoryName(exe)!); });
                await bridge.Invoke(() => { Assert.True(prepare.PrepareButton.GetState(Views.sfDisabled)); Assert.Contains("! Invalid", prepare.DestinationText); });
                await H.Shot(bridge, $"prepare-destination-invalid-{grid}", "Prepare a copy: an invalid destination (inside the application) disables Prepare");
                await bridge.Invoke(() => prepare.SetDestination(made));
                // Mode switch and back: the chosen folder stays.
                await bridge.Invoke(() => { prepare.Method.value = PreparationModes.Fix; H.Nudge(prepare); });
                var fix = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog);
                await bridge.Invoke(() => { fix.Mode.value = PreparationModes.Copy; H.Nudge(fix); });
                prepare = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() => Assert.Equal(made, prepare.DestinationField.Data));
                // Prepare: the copy goes into the chosen folder; the result names it and offers to open it.
                H.Press(Keys.kbAltP);
                var progress = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPreparationProgressDialog);
                await H.Until(bridge, () => progress.Result is not null, 60);
                await bridge.Invoke(() => Assert.True(progress.Result!.Success, progress.Result.Message));
                H.Press(Keys.kbEsc);
                var use = await H.Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchPrepareTargetDialog.UseTitle } d ? d : null);
                await H.Shot(bridge, $"prepare-use-open-destination-{grid}", "Use Prepared Target: full destination and Open destination");
                await bridge.Invoke(() => H.Layout(use, app.DeskTop!));
                H.Press(Keys.kbAltO);
                await H.Until(bridge, () => opened.Count == 1);
                Assert.Equal(made, opened[0]);
                H.Press(Keys.kbAltK);
                await H.Until(bridge, () => app.DeskTop!.TopView() is not TDialog { title: WorkbenchPrepareTargetDialog.UseTitle });
                Assert.True(File.Exists(Path.Combine(made, Path.GetFileName(exe))));
                Assert.Equal(original, C.Snapshot(Path.GetDirectoryName(exe)!));
                // Reopened: the occupied folder proposes its free sibling, Use existing offers the copy.
                H.Send(WorkbenchDialog.PrepareTargetCommand);
                prepare = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchPrepareTargetDialog);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(made + "-2", prepare.DestinationField.Data);
                    Assert.Equal(DestinationKind.New, prepare.Destination.Kind);
                    Assert.False(prepare.UseExistingButton.GetState(Views.sfDisabled));
                });
                await H.Shot(bridge, $"prepare-destination-reopened-{grid}", "Prepare a copy reopened: free sibling proposed, Use existing enabled");
                H.Press(Keys.kbEsc);
                await H.Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchPrepareTargetDialog);
                Assert.False(Directory.Exists(made + "-2"));
            });
            // The chosen folder is remembered in the user settings (an additive field).
            var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "settings.json"))).RootElement;
            Assert.Contains(settings.GetProperty("PrepareDestinations").EnumerateObject(), p => WorkbenchPreferences.SamePath(p.Value.GetString(), made));
        }
        finally { WorkbenchPrepareTargetDialog.OpenFolder = open; C.Delete(root); }
    }

    // ── the real Fix Issues window: another build of the same version (no acknowledgment) ───────────

    private static bool SameVersion(TargetPreparationService service, string exe) =>
        FrameworkCompatibility.Declared(TargetDiagnostics.Inspect(exe).Framework) == FrameworkCompatibility.Core(service.Payload!.FrameworkVersion);

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task FixWindowOffersAnOtherBuildOfTheSameVersionAndVerifies(int width, int height)
    {
        if (!C.Available(nameof(FixWindowOffersAnOtherBuildOfTheSameVersionAndVerifies), ordinary: false, local: true)) return;
        string root = C.Temp();
        try
        {
            string grid = $"{width}x{height}";
            string exe = C.Copy(C.LocalTvDemo()!, root, "tvdemo-local");
            string folder = Path.GetDirectoryName(exe)!;
            var before = C.Snapshot(folder);
            var service = C.Service(root);
            if (!SameVersion(service, exe)) { C.Evidence($"fix-window-skipped-{grid}.txt", ["the local build has another framework version: refused by version"]); return; }
            string projectPath = H.Project(root, exe, tvdemo: true);
            await H.Drive(width, height, root, async (app, bridge) =>
            {
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                await H.Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == projectPath && p.TestList.Strings.Count == 1 ? p : null, 60);
                H.Send(WorkbenchDialog.FixIssuesCommand);
                var fix = await H.Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchFixIssuesDialog);
                await bridge.Invoke(() =>
                {
                    Assert.False(fix.FixButton.GetState(Views.sfDisabled));
                    Assert.True(fix.Plan.OtherBuild);
                    Assert.Contains(fix.Rows, r => r.StartsWith("OK") && r.Contains("TSharpVision.dll") && r.Contains("same version, other build"));
                    Assert.Contains(fix.Rows, r => r.StartsWith("FIX") && r.Contains(TargetDiagnostics.ProxyFile));
                    H.Layout(fix, app.DeskTop!);
                });
                await H.Shot(bridge, $"fix-other-build-{grid}", "Fix original target: same version, other build - offered without acknowledgment");
                H.Press(Keys.kbAltP);
                await H.Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchFixIssuesDialog.CompatibilityTitle } d ? d : null);
                await bridge.Invoke(() =>
                {
                    var lines = fix.CompatibilityLines();
                    Assert.Contains(lines, l => l.StartsWith("  version      same ("));
                    Assert.Contains(lines, l => l.Contains("no - other build (information only)"));
                    Assert.Contains(lines, l => l.StartsWith("How this is decided"));
                    Assert.Contains(lines, l => l.StartsWith("Runtime limits"));
                    C.Evidence("compatibility-window.txt", lines);
                });
                await H.Shot(bridge, $"fix-compatibility-details-{grid}", "Compatibility...: both builds, version comparison, runtime limits");
                H.Press(Keys.kbEsc);
                await H.Until(bridge, () => app.DeskTop!.TopView() == fix);
                // The confirmation (Cancel default) changes nothing; Fix and verify then adds, verifies and keeps the files.
                H.Press(Keys.kbAltF);
                await H.Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchFixIssuesDialog.ConfirmTitle } d ? d : null);
                await H.Shot(bridge, $"fix-other-build-confirm-{grid}", "Confirmation: files added, framework kept, verification decides");
                H.Press(Keys.kbEnter);
                await H.Until(bridge, () => app.DeskTop!.TopView() == fix);
                await bridge.Invoke(() => Assert.Equal("Nothing was changed.", fix.StatusText));
                Assert.Equal(before, C.Snapshot(folder));
                H.Press(Keys.kbAltF);
                await H.Wait(bridge, () => app.DeskTop!.TopView() is TDialog { title: WorkbenchFixIssuesDialog.ConfirmTitle } d ? d : null);
                H.Press(Keys.kbAltF);
                await H.Until(bridge, () => fix.Outcome is not null && !fix.Busy && (fix.Verification is not null || fix.Outcome.Undone), 90);
                await bridge.Invoke(() =>
                {
                    Assert.True(fix.Outcome!.Success, fix.StatusText);
                    Assert.NotNull(fix.Verification);
                    Assert.StartsWith("Verified working (same version, other build)", fix.StatusText);
                    Assert.Contains(fix.Rows, r => r.Contains("verified working"));
                });
                await H.Shot(bridge, $"fix-other-build-verified-{grid}", "Fix and verify: copied and verified working");
                H.Press(Keys.kbEsc);
                await H.Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchFixIssuesDialog);
            });
            var after = C.Snapshot(folder);
            Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
            Assert.Equal(2, after.Count - before.Count);
        }
        finally { C.Delete(root); }
    }

    [Fact]
    public async Task FailedVerificationRemovesTheAddedFilesAgain()
    {
        if (!C.Available(nameof(FailedVerificationRemovesTheAddedFilesAgain))) return;
        string root = C.Temp();
        try
        {
            string exe = C.OtherBuild(root);
            string folder = Path.GetDirectoryName(exe)!;
            var before = C.Snapshot(folder);
            var service = C.Service(root);
            await H.Drive(140, 40, root, async (app, bridge) =>
            {
                WorkbenchFixIssuesDialog dialog = null!;
                await bridge.Invoke(() => dialog = new WorkbenchFixIssuesDialog(app.DeskTop!, new TargetRepairService(service) { IsRunning = _ => false }, exe, () => C.Diagnose(exe),
                    new DiagnosticContext(true, false, "NullDriver", null, null, Run: true),
                    _ => throw new TargetVerificationException("Target did not complete the handshake (simulated).", new IOException("simulated"), "killed", true),
                    null, () => false, "simulated verify") { ConfirmAnswer = () => Views.cmYes });
                _ = bridge.Invoke(() => app.DeskTop!.ExecView(dialog));
                await H.Until(bridge, () => app.DeskTop!.TopView() == dialog);
                await bridge.Invoke(() => Assert.True(dialog.Plan.OtherBuild && dialog.Plan.CanRepair));
                H.Press(Keys.kbAltF);
                await H.Until(bridge, () => dialog.Outcome is { Undone: true } || dialog.StatusText.Contains("INCOMPLETE"), 60);
                await bridge.Invoke(() =>
                {
                    Assert.Contains("removed again", dialog.StatusText);
                    Assert.Contains(dialog.Rows, r => r.Contains("failed verification - removed again"));
                });
                await H.Shot(bridge, "fix-other-build-verification-failed-140x40", "Fix and verify: verification failed, added files removed again");
                await bridge.Invoke(() => dialog.EndModal(Views.cmCancel));
                await H.Until(bridge, () => app.DeskTop!.TopView() != dialog);
            });
            Assert.Equal(before, C.Snapshot(folder));
        }
        finally { C.Delete(root); }
    }
}
