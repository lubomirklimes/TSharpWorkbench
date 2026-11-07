using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TuiStartupProjectTests
{
    [Fact]
    public void NativeProjectSavesProxyScenarioWithoutFixtureId()
    {
        string root = Path.Combine(Path.GetTempPath(), "tsw-proxy-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? previous = NativeProject.WorkspaceRoot;
        NativeProject.WorkspaceRoot = Path.Combine(root, "work");
        try
        {
            string archive = Path.Combine(root, "project.tsw");
            using var project = NativeProject.Create(archive, "Proxy project");
            var document = ScenarioDocument.New("1", "Proxy scenario", "Uses an ordinary target.");
            string path = project.CreateScenario(document.SerializeText());

            project.Store(project.Read() with { ScenarioFiles = [path] });
            project.Session.Save();

            var scenario = Assert.Single(project.Session.Scenarios);
            Assert.Equal("1", scenario.Id);
            Assert.Null(scenario.FixtureId);
            Assert.True(File.Exists(archive));
        }
        finally
        {
            NativeProject.WorkspaceRoot = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupProjectOverridesRememberedTargetAndPreservesExternalRunConfiguration()
    {
        using var driver = new DriverScope(140, 40);
        var project = new WorkbenchProject
        {
            Name = "TVDemo startup", Target = "", OutputRoot = Path.GetFullPath("TestResults"),
            ScenarioFiles = [Path.GetFullPath("scenarios/menu.scenario.json")],
            SelectedIds = ["tvdemo/menu"], ScreenSizes = ["80x25", "140x40"],
            ProxyBackend = "SDLDriver", ProxyCapture = "renderer-pixels"
        };
        string path = Path.GetFullPath("tvdemo.twproj");
        // Opening a project records it in the recent list; keep that in a disposable settings file.
        string settings = Path.Combine(Path.GetTempPath(), "ui-startup-" + Guid.NewGuid().ToString("N") + ".json");
        using var app = new WorkbenchApp("remembered-missing-target.exe", "remembered-output",
            new("remembered-missing-target.exe", "remembered-output", null, "80x25") { StoragePath = settings }, project, path);
        // Process the actual queued startup event, without starting target discovery/execution.
        var ev = new TEvent(); app.GetEvent(ref ev); app.HandleEvent(ref ev);
        Assert.NotNull(app.Project);
        Assert.Equal(path, app.Project.ProjectPath);
        var actual = app.Project.CaptureProject();
        Assert.Equal(project.Name, actual.Name);
        Assert.Equal(project.Target, actual.Target);
        Assert.Equal(project.ScenarioFiles, actual.ScenarioFiles);
        Assert.Equal(project.SelectedIds, actual.SelectedIds);
        Assert.Equal(project.ScreenSizes, actual.ScreenSizes);
        Assert.Equal(project.ProxyBackend, actual.ProxyBackend);
        Assert.Equal(project.ProxyCapture, actual.ProxyCapture);
        Assert.False(app.Project.Dirty);
        Assert.Null(app.Start);
        Assert.Equal(path, WorkbenchPreferences.Load(settings).RecentProjects[0]);
        File.Delete(settings);
    }

    [Fact]
    public void PlainStartupCanDismissTheStartDialogAndTargetStartupKeepsAnUntitledProject()
    {
        using var driver = new DriverScope(140, 40);
        string settings = Path.Combine(Path.GetTempPath(), "ui-start-" + Guid.NewGuid().ToString("N") + ".json");
        using (var plain = new WorkbenchApp("", "out", new("", "out", null, "80x25") { StoragePath = settings }, showStart: true))
        {
            var ev = new TEvent(); plain.GetEvent(ref ev); plain.HandleEvent(ref ev);
            var escape = new TEvent { What = Events.evKeyDown };
            escape.keyDown.keyCode = Keys.kbEsc;
            TEventQueue.Enqueue(escape);
            plain.GetEvent(ref ev); plain.HandleEvent(ref ev);
            Assert.Null(plain.Start); Assert.Null(plain.Project);
            Assert.Equal(WorkbenchApp.EmptyDesktopContext, plain.GetHelpCtx());
        }
        using (var untitled = new WorkbenchApp("", "out", new("", "out", null, "80x25") { StoragePath = settings }))
        {
            var ev = new TEvent(); untitled.GetEvent(ref ev); untitled.HandleEvent(ref ev);
            Assert.Null(untitled.Start); Assert.NotNull(untitled.Project); Assert.Null(untitled.Project.ProjectPath);
        }
        Assert.False(File.Exists(settings)); // nothing was opened, so nothing was recorded
    }

    [Fact]
    public void InvalidStartupProjectFailsBeforeStartingTheTui()
    {
        Assert.Equal(2, WorkbenchEntry.Run(["--project", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".twproj")]));
    }
    [Fact]
    public void CapturePickerIncludesLogicalAndNativeFilesWithoutBaselinesAndRejectsEscapes()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TSharpWorkbench.Source.slnx"))) repo = repo.Parent;
        string root = Path.Combine(repo!.FullName, "TestResults", "capture-picker", "capture-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "logical.png"), []);
            File.WriteAllBytes(Path.Combine(root, "native.png"), []);
            File.WriteAllText(Path.Combine(root, "checkpoint.json"), """{"proxyNative":{"artifact":"native.png","kind":"gpu-completed"}}""");
            var checkpoint = new TSharpWorkbench.Testing.CheckpointResult("selected", "80x25", "logical.png", "cells.tvs1", "checkpoint.json", "", "", "", [], []);
            var result = new TSharpWorkbench.Testing.ScenarioResult("tvdemo/combo", "80x25", "Passed", "", [checkpoint], true, true, true);
            var batch = new TSharpWorkbench.Testing.BatchResult(root, [result]);
            var choices = WorkbenchDialog.CaptureChoices(batch, result);
            Assert.Equal(["logical.png", "native.png"], choices.Select(c => c.Name).ToArray());
            Assert.Contains("Logical PNG", choices[0].Label);
            Assert.Contains("Native gpu-completed", choices[1].Label);
            Assert.Null(batch.Visual);
            File.WriteAllText(Path.Combine(root, "checkpoint.json"), """{"proxyNative":{"artifact":"../outside.png","kind":"gpu-completed"}}""");
            Assert.Throws<InvalidDataException>(() => WorkbenchDialog.CaptureChoices(batch, result));
        }
        finally { Directory.Delete(root, true); }
    }    [Fact]
    public void ResultsCloseButtonClosesItsOwnModelessWindow()
    {
        using var driver = new DriverScope(140, 40);
        using var app = new TApplication();
        bool closed = false;
        var window = new WorkbenchResultsDialog(app.DeskTop!, new EmptyResults());
        window.Closed = () => closed = true;
        app.DeskTop!.Insert(window); window.Select();
        TButton? close = null;
        window.ForEachView(view => { if (view is TButton button && button.Command == WorkbenchResultsDialog.CloseResults) close = button; });
        Assert.NotNull(close);
        close.Press();
        var ev = new TEvent(); app.GetEvent(ref ev); app.HandleEvent(ref ev);
        Assert.True(closed);
        Assert.Null(window.owner);
    }
    // The Results window reads its run from a host; an empty run is enough for the Close button.
    private sealed class EmptyResults : IResultsHost
    {
        public ResultsView View() => new(new TSharpWorkbench.Testing.BatchResult("", []), ["Run       none", "", "", ""], false, "", _ => null, null);
        public WorkbenchNotifications Notifications { get; } = new();
        public TSharpWorkbench.Testing.ScenarioDescriptor? Descriptor(string id) => null;
        public void Navigate(int direction) { }
        public void OpenRun() { }
        public void Rerun(TSharpWorkbench.Testing.BatchResult batch, TSharpWorkbench.Testing.ScenarioResult result) { }
        public void Compare(TSharpWorkbench.Testing.BatchResult batch, TSharpWorkbench.Testing.ScenarioResult result, TSharpWorkbench.Testing.CheckpointResult? checkpoint) { }
        public void ReviewCandidate(TSharpWorkbench.Testing.BatchResult batch, TSharpWorkbench.Testing.ScenarioResult result, TSharpWorkbench.Testing.CheckpointResult? checkpoint) { }
        public void OpenPath(string path, bool folder) { }
    }}
