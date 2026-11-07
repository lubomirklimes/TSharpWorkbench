using TSharpVision.Constants;
using TSharpWorkbench.Storage;
using Xunit;
using static TSharpWorkbench.Tests.Core.ProjectInformationUxTests;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class UiRemediationTests
{
    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task CompiledScenarioCreatesTestThroughUiAndWindowsRemainModeless(int width, int height)
    {
        string root = Temp(), archive = Path.Combine(root, "Journey.tsw");
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            await Drive(width, height, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.CreateNativeProject(archive)));
                var project = await Wait(bridge, () => app.Project);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbF4);
                var properties = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(() => properties.ExecutableField.SetData(TestWorkbenchHostTests.SampleTarget));
                await Capture(bridge, "properties-target");
                Press(Keys.kbEnter);
                await Until(bridge, () => Modal(app, "Project Properties") is null);
                await Loaded(app, bridge, 1);
                Send(WorkbenchDialog.ManageScenarios);
                var library = await Wait(bridge, () => project.ScenariosWindow);
                await bridge.Invoke(() => {
                    Assert.False(library.GetState(Views.sfModal));
                    int index = Array.FindIndex(library.Items, i => i.Info.Id == "sample/dialog/basic");
                    Assert.True(index >= 0);
                    library.Entries.FocusItemNum(index);
                });
                await bridge.Invoke(() => {
                    string group = library.Selected!.Info.Compiled!.Group;
                    library.GroupFilter.Text = group;
                    library.Load(library.Items);
                    Assert.All(library.Items, item => Assert.Equal(group, item.Info.Compiled?.Group ?? item.Info.Document?.Scenario.Group));
                    Assert.Equal("sample/dialog/basic", library.Selected!.Info.Id);
                });
                await Capture(bridge, "compiled-library-group-filtered");
                await Capture(bridge, "compiled-library");
                Press(Keys.kbAltK);
                var editor = await Wait(bridge, () => Modal(app, "New Test"));
                await bridge.Invoke(() => {
                    Descendants(editor).OfType<TInputLine>().OrderBy(x => x.origin.y).First().SetData("Compiled UI journey");
                    Descendants(editor).OfType<TListBox>().Single().Select();
                });
                Press(Keys.kbSpace); Press(Keys.kbAltS);
                await Until(bridge, () => Modal(app, "New Test") is null);
                await bridge.Invoke(() => Assert.Single(project.Native!.Session.Tests));
                Send(WorkbenchTestExplorer.Command);
                var explorer = await Wait(bridge, () => project.TestExplorer);
                Press(Keys.kbSpace);
                Send(WorkbenchApp.Environments);
                var environments = await Wait(bridge, () => project.EnvironmentManager);
                await bridge.Invoke(() => {
                    Assert.False(explorer.GetState(Views.sfModal));
                    Assert.True(environments.GetState(Views.sfModal));
                });
                Press(Keys.kbEsc);
                await Until(bridge, () => project.EnvironmentManager is null);
                Press(Keys.kbAlt1);
                await Wait(bridge, () => app.Overview);
                Press(Keys.kbEsc);
                await Until(bridge, () => app.Overview is null);
                Press(Keys.kbAlt2);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, library));
                Send(WorkbenchTestExplorer.Command);
                await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, explorer));
                await bridge.Invoke(() => Assert.Single(project.MarkedTests));
                await Capture(bridge, "retained-test-explorer");
                Press(Keys.kbF5);
                await Wait(bridge, () => Modal(app, "Configure Test Run"));
                await Capture(bridge, "focused-f5-configuration");
                Press(Keys.kbAltS);
                await Until(bridge, () => Modal(app, "Configure Test Run") is null);
                await Until(bridge, () => !project.Busy && project.Native!.Session.ListArtifacts("runs/").Any(x => x.EndsWith("matrix.json")));
                await Capture(bridge, "ui-only-compiled-run-complete");
                Press(Keys.kbF4);
                var processProperties = await Wait(bridge, () => Modal(app, "Project Properties") as WorkbenchPropertiesDialog);
                await bridge.Invoke(() => {
                    processProperties.ArgumentsField.SetData("[\"two words\",\"--flag\"]");
                    processProperties.ProcessEnvironmentField.SetData("{\"TSW_EXAMPLE\":\"value with spaces\"}");
                });
                Press(Keys.kbAltK);
                await Until(bridge, () => Modal(app, "Project Properties") is null);
                await bridge.Invoke(() => {
                    Assert.Equal(new[] { "two words", "--flag" }, project.CaptureProject().TargetArguments);
                    Assert.True(project.SaveProject(false));
                });
                Send(WorkbenchApp.CloseProject);
                await Until(bridge, () => app.Project is null);
                await bridge.Invoke(() => {
                    Assert.Null(project.TestExplorer); Assert.Null(project.EnvironmentManager); Assert.Null(project.ScenariosWindow);
                    Assert.True(app.OpenNativeProject(archive));
                    Assert.Equal("value with spaces", app.Project!.CaptureProject().TargetEnvironment["TSW_EXAMPLE"]);
                    Assert.Equal(new[] { "two words", "--flag" }, app.Project.CaptureProject().TargetArguments);
                    Assert.Single(app.Project!.Native!.Session.Tests);
                    Assert.Null(Assert.Single(app.Project.Native.Session.Scenarios).ArtifactPath);
                });
            }, showStart: true);
        }
        finally { NativeProject.WorkspaceRoot = previous; Delete(root); }
    }

    [Fact]
    public async Task MatrixShowsCompletedRowsAndCancelsWithRetainedHistory()
    {
        string root = Temp(), archive = Path.Combine(root, "Cancel.tsw");
        Directory.CreateDirectory(root);
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            using (var native = NativeProject.Create(archive, "Cancel"))
            {
                native.Store(new WorkbenchProject { Name = "Cancel", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs"), Timeout = 10000 });
                native.Session.PutEnvironment(new("local", "Local", "host", "NullDriver", 80, 25, "none"));
                foreach (var (id, scenario) in new[] { ("a-fast", "sample/dialog/basic"), ("b-slow", "selftest/failure/blocked-ui") })
                {
                    native.Session.PutScenario(new(scenario, null, null, "diagnostic-logical"));
                    native.Session.PutTest(new(id, id, "", "", scenario, ["local"]));
                }
                native.Session.Close(CloseChoice.Save);
            }
            await Drive(120, 36, root, Path.Combine(root, "home"), async (app, bridge) =>
            {
                await Wait(bridge, () => app.Start);
                await bridge.Invoke(() => Assert.True(app.OpenNativeProject(archive)));
                var project = await Loaded(app, bridge, 1);
                Send(WorkbenchDialog.MarkAll);
                Press(Keys.kbF5);
                await Wait(bridge, () => Modal(app, "Configure Test Run"));
                Press(Keys.kbAltS);
                await Until(bridge, () => project.ActiveMatrix?.Results.Any(r => r.Entry.TestId == "a-fast") == true);
                await bridge.Invoke(() => { Assert.True(project.MatrixCancellable); Assert.Equal("Passed", project.ActiveMatrix!.Results[0].Overall); });
                await Capture(bridge, "matrix-active-completed-row");
                Send(WorkbenchDialog.CancelRun);
                await Until(bridge, () => !project.MatrixCancellable);
                await Capture(bridge, "matrix-cancelling-or-cleaned");
                await Until(bridge, () => !project.Busy);
                await bridge.Invoke(() => {
                    Assert.Contains("cancelled", project.MatrixProgress, StringComparison.OrdinalIgnoreCase);
                    Assert.Null(project.ActiveMatrix);
                    var runWindow = Assert.IsType<WorkbenchRunExplorer>(app.DeskTop!.current);
                    Assert.True(Descendants(runWindow).OfType<TButton>().Single(b => b.Title == "Cancel r~u~n").GetState(Views.sfDisabled));
                    Assert.Single(project.Native!.Session.ListArtifacts("runs/").Where(x => x.EndsWith("matrix.json")));
                    Assert.True(project.SaveProject(false));
                });
                await Capture(bridge, "matrix-cancelled");
            }, showStart: true);
        }
        finally { NativeProject.WorkspaceRoot = previous; Delete(root); }
    }

    [Fact]
    public async Task RecoveryDefaultsToCancelAndHasContextualHelp()
    {
        string root = Temp();
        try
        {
            await Drive(120, 36, root, Path.Combine(root, "home"), async (app, bridge) => {
                await Wait(bridge, () => app.Start);
                RecoveryChoice? choice = null;
                var pending = bridge.Invoke(() => choice = NativeRecoveryDialog.Choose(app.DeskTop!));
                var recovery = await Wait(bridge, () => Modal(app, "Recover Project"));
                await bridge.Invoke(() => {
                    Assert.Equal(HelpTopic.Start, WorkbenchHelpContext.Resolve(recovery));
                    Assert.Equal(Views.cmCancel, Assert.IsType<TButton>(recovery.current).Command);
                    Assert.Equal(new[] { "~R~ecover", "~D~iscard", "~C~ancel" }, Descendants(recovery).OfType<TButton>().OrderBy(b => b.origin.x).Select(b => b.Title));
                });
                await Capture(bridge, "recovery-safe-default");
                Press(Keys.kbEsc); await pending;
                Assert.Equal(RecoveryChoice.Cancel, choice);
            }, showStart: true);
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData("Passed", "Match", "Passed")]
    [InlineData("Passed", "BaselineMissing", "Visual failed")]
    [InlineData("Passed", "Mismatch", "Visual failed")]
    [InlineData("Failed", "Match", "Failed")]
    [InlineData("Skipped", null, "Skipped")]
    public void OverallIncludesVisualVerdict(string functional, string? visual, string expected) =>
        Assert.Equal(expected, WorkbenchMatrix.Aggregate(functional, visual));
}

