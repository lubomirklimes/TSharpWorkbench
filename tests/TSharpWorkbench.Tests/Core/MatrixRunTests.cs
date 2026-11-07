using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class MatrixRunTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task MarkConfigureRunAndReopenOffline(int width, int height)
    {
        string root = Temp(), archive = Path.Combine(root, "Matrix.tsw"), output = Path.Combine(root, "external");
        Directory.CreateDirectory(root);
        string? priorRoot = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            const string scenario = "sample/dialog/basic";
            using (var native = NativeProject.Create(archive, "Matrix"))
            {
                string manifest = Path.Combine(root, "visual-baselines.json");
                File.WriteAllText(manifest, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat,
                    [], [new(scenario, "initial", ["80x25"], "Sample dialog baseline")]), ScenarioRunner.Json));
                native.Session.AdoptBaselineStore(manifest, WorkbenchDialog.ValidateAdoptedStore);
                native.Store(new() { Name = "Matrix", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = output,
                    TestType = "visual", BaselineStore = native.Session.ResolveArtifact(NativeProject.BaselineArtifact) });
                native.Session.PutScenario(new(scenario, null, null, "diagnostic-logical"));
                native.Session.PutEnvironment(new("a-local", "Local", WorkbenchMatrix.LocalOs, "NullDriver", 80, 25, "none"));
                native.Session.PutEnvironment(new("b-foreign", "Foreign", WorkbenchMatrix.LocalOs == "linux" ? "windows" : "linux", "NullDriver", 80, 25, "none"));
                native.Session.PutTest(new("test-1", "Dialog opens and closes", "", "", scenario, ["b-foreign", "a-local"]));
                native.Session.Close(CloseChoice.Save);
            }
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.OpenNativeProject(archive)));
                var project = await Loaded(app, bridge, 1);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Send(WorkbenchTestExplorer.Command);
                await Wait(bridge, () => project.TestExplorer);
                Press(Keys.kbSpace);
                Press(Keys.kbF5);
                await Wait(bridge, () => Modal(app, "Configure Test Run"));
                await Capture(bridge, "configure-test-run");
                Assert.False(project.Busy);
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Configure Test Run") is null);
                Assert.Empty(project.Native!.Session.ListArtifacts("runs/"));
                Press(Keys.kbF5);
                await Wait(bridge, () => Modal(app, "Configure Test Run"));
                Press(Keys.kbDown);
                Press(Keys.kbSpace);
                Press(Keys.kbAltS);
                await Until(bridge, () => !project.Busy && project.Native!.Session.ListArtifacts("runs/").Any(p => p.EndsWith("/matrix.json")), 60);
                await Capture(bridge, "run-explorer-completed");
                var run = Read(project.Native!.Session);
                Assert.Equal(["Visual failed", "Skipped"], run.Results.Select(r => r.Outcome).ToArray());
                Assert.Equal(["a-local", "b-foreign"], run.Results.Select(r => r.Entry.Environment.Id).ToArray());
                Assert.All(run.Results, r => Assert.Equal(scenario, r.Entry.ScenarioId));
                Assert.Null(run.Results[1].Report);
                Assert.Equal("NullDriver", run.Results[0].ActualDriver);
                Assert.Equal("logical", run.Results[0].ActualCapture);
                Assert.Equal("80x25", run.Results[0].InitialGrid);
                await bridge.Invoke(() => Assert.True(project.SaveProject(false)));
                Send(WorkbenchApp.CloseProject);
                await Until(bridge, () => app.Project is null);
                // Rename the external evidence directory. The archive reader must work independently of it.
                Directory.Move(output, Path.Combine(root, "external-away"));
                string relocated = Path.Combine(root, "Relocated.tsw");
                File.Move(archive, relocated); archive = relocated;
                using (var reopened = ProjectSession.Open(archive, workspaceRoot: NativeProject.WorkspaceRoot))
                {
                    var stored = Read(reopened);
                    var loaded = WorkbenchRunHistory.Load(reopened.ResolveArtifact(stored.Results[0].Report!));
                    Assert.NotEmpty(loaded.Batch.Results.Single().Checkpoints);
                    Assert.Equal(scenario, loaded.Batch.Results.Single().ScenarioId);
                    reopened.Close(CloseChoice.Save);
                }
                await bridge.Invoke(() => Assert.True(app.OpenNativeProject(archive)));
                await Loaded(app, bridge, 1);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Send(WorkbenchDialog.ProgressWindow);
                await Wait(bridge, () => app.DeskTop!.current as WorkbenchRunExplorer);
                Press(Keys.kbEnter);
                await Wait(bridge, () => Modal(app, "Execution Details"));
                await Capture(bridge, "archived-execution-details");
                Press(Keys.kbAltO);
                await Wait(bridge, () => app.DeskTop!.current as WorkbenchResultsDialog);
                await Capture(bridge, "archived-results");
                Send(WorkbenchDialog.Rerun);
                await Wait(bridge, () => Modal(app, "Configure Test Run"));
                await Capture(bridge, "project-rerun-configuration");
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Configure Test Run") is null);
                Send(WorkbenchDialog.BaselineBrowse);
                var explorer = await Wait(bridge, () => app.DeskTop!.current as WorkbenchBaselineExplorer);
                await Capture(bridge, "baseline-explorer-candidate");
                await bridge.Invoke(() => Descendants(explorer).OfType<TInputLine>().Single().SetData("Candidate"));
                Press(Keys.kbAltL);
                Press(Keys.kbEnter);
                var review = await Wait(bridge, () => Modal(app, WorkbenchCandidateDialog.ApproveTitle) as WorkbenchCandidateDialog);
                await Capture(bridge, "approve-candidate");
                await bridge.Invoke(() => review.Confirm.Select());
                Press(Keys.kbSpace);
                Press(Keys.kbAltA);
                await Wait(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)));
                await Capture(bridge, "approve-confirmation");
                Press(Keys.kbAltA);
                await Until(bridge, () => Modal(app, WorkbenchBaselineDialogs.ConfirmTitle(false)) is null);
                await bridge.Invoke(() =>
                {
                    var active = app.Project!;
                    var set = VisualBaselineSet.Load(active.CaptureProject().BaselineStore!);
                    var entry = Assert.Single(set.Manifest.Baselines);
                    Assert.Equal(VisualStatuses.Match, set.LoadApproved(entry, "80x25").Status);
                    Assert.True(active.SaveProject(false));
                });
                await Until(bridge, () => app.Project is { Busy: false });
                Send(WorkbenchApp.CloseProject);
                await Until(bridge, () => app.Project is null);
                File.Move(Path.Combine(root, "visual-baselines.json"), Path.Combine(root, "source-away.json"));
                await bridge.Invoke(() => Assert.True(app.OpenNativeProject(archive)));
                var fresh = await Loaded(app, bridge, 1);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Send(WorkbenchDialog.ProgressWindow);
                await Wait(bridge, () => app.DeskTop!.current as WorkbenchRunExplorer);
                Press(Keys.kbEnter);
                await Wait(bridge, () => Modal(app, "Execution Details"));
                Press(Keys.kbAltO);
                await Wait(bridge, () => app.DeskTop!.current as WorkbenchResultsDialog);
                Send(WorkbenchDialog.Rerun);
                await Wait(bridge, () => Modal(app, "Configure Test Run"));
                Press(Keys.kbAltS);
                await Until(bridge, () => !fresh.Busy && fresh.Native!.Session.ListArtifacts("runs/").Count(p => p.EndsWith("/matrix.json")) == 2, 60);
                var session = fresh.Native!.Session;
                var reports = session.ListArtifacts("runs/").Where(p => p.EndsWith("/run.json"))
                    .Select(p => WorkbenchRunHistory.Load(session.ResolveArtifact(p)).Batch.Visual!).ToArray();
                Assert.Contains(reports, visual => visual.Entries.Any(e => e.Status == VisualStatuses.Match));
                Assert.Contains(reports, visual => visual.Entries.Any(e => e.Status == VisualStatuses.BaselineMissing));
                await bridge.Invoke(() => Assert.True(fresh.SaveProject(false)));
                Send(WorkbenchApp.CloseProject);
                await Until(bridge, () => app.Project is null);

            }, showStart: true);
        }
        finally { NativeProject.WorkspaceRoot = priorRoot; Delete(root); }
    }

    [Fact]
    public async Task CancellationAndUnavailableScenarioDoNotLaunchOrArchive()
    {
        var entry = new WorkbenchMatrix.Entry("test", "Test", "missing",
            new("local", "Local", "host", "NullDriver", 80, 25, "none"));
        var request = new WorkbenchRunRequest(new WorkbenchProject(), [], "unused");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var runner = new ScenarioRunner();
        var aborted = await WorkbenchMatrix.Execute(entry, request, runner, _ => { }, _ => throw new InvalidOperationException(), cancelled.Token);
        var blocked = await WorkbenchMatrix.Execute(entry, request, runner, _ => { }, _ => throw new InvalidOperationException(), default);
        Assert.Equal("Aborted", aborted.Outcome);
        Assert.Equal("Blocked", blocked.Outcome);
        Assert.Null(aborted.Report);
        Assert.Null(blocked.Report);
    }

    private static WorkbenchMatrix.Run Read(ProjectSession session)
    {
        string index = Assert.Single(session.ListArtifacts("runs/").Where(p => p.EndsWith("/matrix.json")));
        using var input = File.OpenRead(session.ResolveArtifact(index));
        return JsonSerializer.Deserialize<WorkbenchMatrix.Run>(input, ScenarioRunner.Json)!;
    }
}
