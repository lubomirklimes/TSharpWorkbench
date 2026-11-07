using System.Diagnostics;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;
using FixTests = TSharpWorkbench.Tests.Core.FixIssuesTests;
using PrepareTests = TSharpWorkbench.Tests.Core.PrepareTests;
using Ux2 = TSharpWorkbench.Tests.Core.TvDemoDeploymentUxTests;

namespace TSharpWorkbench.Tests.Manual;

// Manual: drives the real WorkbenchApp (NullDriver, 120x36) through the windows of the chapter
// "Diagnostika a příprava targetu" and writes logical PNGs to <repo>/artifacts/target-windows/*.png.
// Uses a disposable copy of diagnostics/ProxyOrdinary (Proxy missing) and the built proxy payload.
// Skipped in normal runs; runs under "Debug Test" or with TARGET_WINDOWS_CAPTURE set.
[Collection("NonParallel")]
public sealed class TargetWindowsCaptureTests
{
    public sealed class ManualFactAttribute : FactAttribute
    {
        public ManualFactAttribute()
        {
            if (!Debugger.IsAttached && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TARGET_WINDOWS_CAPTURE")))
                Skip = "Manual capture: run with Debug Test (or set TARGET_WINDOWS_CAPTURE).";
        }
    }

    private const int Width = 120, Height = 36;

    [ManualFact, Trait("Category", "Manual")]
    public async Task CaptureTargetDiagnosticsAndPreparation()
    {
        string repo = RepositoryRoot();
        string output = Path.Combine(repo, "artifacts", "target-windows");
        Directory.CreateDirectory(output);
        string? payload = Ux2.Payload() ?? Existing(Path.Combine(repo, "src", "TSharpWorkbench", "bin", "Debug", "net10.0", ProxyPayload.Folder), ProxyPayload.DepsFile);
        string? ordinary = FixTests.Original() ?? Existing(Path.Combine(repo, "diagnostics", "ProxyOrdinary", "bin", "Debug", "net10.0"), "ProxyOrdinary.exe");
        Assert.True(payload is not null, "Proxy payload not found; build src/TSharpWorkbench first.");
        Assert.True(ordinary is not null, "ProxyOrdinary.exe not found; build diagnostics/ProxyOrdinary first.");
        string? previousPayload = Environment.GetEnvironmentVariable(ProxyPayload.EnvironmentOverride);
        Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, payload);

        var errors = new List<string>();
        string root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            string exe = CopyTarget(ordinary!, Path.Combine(root, "app"));
            TargetDiagnostics.Preparation = new TargetPreparationService(Path.Combine(root, "prepared"), payload);
            TargetPreparationService.ForgetStatus();
            string projectPath = PrepareTests.Project(root, exe, tvdemo: false);

            await Drive(Width, Height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                async Task Shot(string name, Func<Task> open, Func<Task>? close = null)
                {
                    try
                    {
                        await open();
                        await Settle(bridge);
                        await bridge.Invoke(() => File.WriteAllBytes(Path.Combine(output, name + ".png"), LogicalPng.Encode(ScreenSnapshot.CopyRoot())));
                    }
                    catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                    try { if (close is not null) await close(); }
                    catch (Exception ex) { errors.Add($"{name} (close): {ex.Message}"); }
                }
                TView? Top() => app.DeskTop!.TopView();
                Task<TDialog> Titled(string title) => Wait(bridge, () => Top() is TDialog { title: var t } d && t == title ? d : null, 10);
                async Task Back<T>() where T : TView { Press(Keys.kbEsc); await Until(bridge, () => Top() is T, 10); }
                async Task Away<T>() where T : TView { Press(Keys.kbEsc); await Until(bridge, () => Top() is not T, 10); }

                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(projectPath), projectPath));
                await Wait(bridge, () => app.Project is { Busy: false } p && p.ProjectPath == projectPath && p.TestList.Strings.Count == 1 ? p : null, 60);

                // Target Diagnostics and its Verify Target confirmation.
                await Shot("target-diagnostics", async () => { Send(WorkbenchDialog.DiagnoseTarget); await Wait(bridge, () => Top() as WorkbenchTargetDiagnosticsDialog, 10); });
                await Shot("target-diagnostics-verify-target", async () => { Press(Keys.kbAltV); await Titled(WorkbenchTargetDiagnosticsDialog.ConfirmTitle); },
                    () => Back<WorkbenchTargetDiagnosticsDialog>());
                if (Top() is WorkbenchTargetDiagnosticsDialog) await Away<WorkbenchTargetDiagnosticsDialog>();

                // Fix Issues with its confirmation, compatibility list and Verify Target.
                await Shot("fix-issues", async () => { Send(WorkbenchDialog.FixIssuesCommand); await Wait(bridge, () => Top() as WorkbenchFixIssuesDialog, 10); });
                await Shot("fix-issues-confirm", async () => { Press(Keys.kbAltF); await Titled(WorkbenchFixIssuesDialog.ConfirmTitle); },
                    () => Back<WorkbenchFixIssuesDialog>());
                await Shot("target-compatibility", async () => { Press(Keys.kbAltP); await Titled(WorkbenchFixIssuesDialog.CompatibilityTitle); },
                    () => Back<WorkbenchFixIssuesDialog>());
                await Shot("fix-issues-verify-target", async () =>
                {
                    var fix = (WorkbenchFixIssuesDialog)Top()!;
                    await bridge.Invoke(() => Command(fix, WorkbenchFixIssuesDialog.VerifyCommand));
                    await Titled(WorkbenchFixIssuesDialog.VerifyTitle);
                }, () => Back<WorkbenchFixIssuesDialog>());
                if (Top() is WorkbenchFixIssuesDialog) await Away<WorkbenchFixIssuesDialog>();

                // Prepare Target, its file list, New folder, progress and Use Prepared Target.
                WorkbenchPrepareTargetDialog? prepare = null;
                await Shot("prepare-target", async () => { Send(WorkbenchDialog.PrepareTargetCommand); prepare = await Wait(bridge, () => Top() as WorkbenchPrepareTargetDialog, 10); });
                await Shot("preparation-files", async () => { Press(Keys.kbAltF); await Titled(WorkbenchPrepareTargetDialog.FilesTitle); },
                    () => Back<WorkbenchPrepareTargetDialog>());
                await Shot("new-destination-folder", async () => { Press(Keys.kbAltN); await Wait(bridge, () => Top() as WorkbenchNewFolderDialog, 10); },
                    () => Back<WorkbenchPrepareTargetDialog>());
                WorkbenchPreparationProgressDialog? progress = null;
                await Shot("preparing", async () => { Press(Keys.kbAltP); progress = await Wait(bridge, () => Top() as WorkbenchPreparationProgressDialog, 10); });
                await Shot("preparing-finished", () => Until(bridge, () => progress!.Result is not null, 120));
                await Shot("use-prepared-target", async () => { Press(Keys.kbEsc); await Titled(WorkbenchPrepareTargetDialog.UseTitle); },
                    async () => { Press(Keys.kbEsc); await Until(bridge, () => Top() is not TDialog, 10); });

                // Recover Project (normally shown only for a .tsw that needs recovery).
                await Shot("recover-project", async () =>
                {
                    _ = bridge.Invoke(() => NativeRecoveryDialog.Choose(app.DeskTop!));
                    await Titled("Recover Project");
                }, async () => { Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Recover Project") is null, 10); });
            }, target: exe);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, previousPayload);
            Delete(root);
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static string? Existing(string folder, string file) => File.Exists(Path.Combine(folder, file)) ? Path.GetFullPath(folder) : null;

    private static string CopyTarget(string folderOrExe, string target)
    {
        string source = File.Exists(folderOrExe) ? Path.GetDirectoryName(folderOrExe)! : folderOrExe;
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(file, to);
        }
        return Path.Combine(target, "ProxyOrdinary.exe");
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found.");
    }
}
