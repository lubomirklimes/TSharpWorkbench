using System.Text.Json;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class TestWorkbenchWorkflowTests
{
    private static ScenarioDescriptor Entry(string id, string group, string expected = "Passed") =>
        new(id,id,"description",group,ScenarioGrid.Standard,[new(ScenarioStep.Checkpoint,"initial")], ExpectedResult: expected);

    [Fact]
    public void FiltersRetainMarksAndDetailsUseVisibleIdentityRatherThanCatalogIndex()
    {
        using var driver = new DriverScope();
        using var scroll = new TScrollBar(new(40,0,41,10));
        using var list = new ScenarioList(new(0,0,40,10),scroll);
        list.LoadCatalog([Entry("first","A"),Entry("second","B/child"),Entry("failure","B","Failed")]);
        list.Toggle("first"); list.Filter("B","SECOND");
        Assert.Equal("second",list.Current?.Id); Assert.Equal(1,list.range);
        list.SelectItem(0); Assert.Equal(new[] { "first","second" },list.MarkedIds.Order());
        list.Filter("B",""); list.MarkGroup("B"); Assert.DoesNotContain("failure",list.MarkedIds);
        list.Filter(null,"missing"); Assert.Null(list.Current); Assert.Equal(2,list.MarkedIds.Count);
        list.ClearMarks(); Assert.Empty(list.MarkedIds);
        list.Toggle("first"); list.LoadCatalog([Entry("first","A")]); Assert.Empty(list.MarkedIds);
    }

    [Fact]
    public void PreferencesKeepSameTargetSelectionButExplicitTargetAndArgumentsWin()
    {
        string root = Path.Combine(Path.GetTempPath(),"wb-settings-"+Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"settings.json");
        try
        {
            new WorkbenchPreferences("a.exe","saved-root","A","80x25") { SelectedIds=["first"],Search="first" }.Save(path);
            var same = WorkbenchPreferences.Resolve([],path);
            Assert.Equal(new[] { "first" },same.SelectedIds); Assert.Equal("A",same.Group); Assert.Equal("first",same.Search);
            var changed = WorkbenchPreferences.Resolve(new() { ["--target"]="b.exe",["--output"]="explicit-root",["--scenario"]="other" },path);
            Assert.Null(changed.Group); Assert.Equal("",changed.Search); Assert.Equal(new[] { "other" },changed.SelectedIds);
            Assert.Equal("explicit-root",changed.Output);
            File.WriteAllText(path,"bad JSON");
            Assert.EndsWith(Path.Combine("TSharpVision","TestWorkbench","runs"),WorkbenchPreferences.Resolve([],path).Output);
            Assert.DoesNotContain("LaunchOptions",JsonSerializer.Serialize(same));
        }
        finally { Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData("Console")]
    [InlineData("SDL")]
    [InlineData("NativePixels")]
    public void UnavailableLaunchModesAreRefusedWithoutFallback(string mode)
    {
        Assert.Equal(OperatingSystem.IsWindows() ? 6 : 1, WorkbenchCapabilities.Modes.Count(m => m.Executable));
        Assert.Contains("no fallback",Assert.Throws<ArgumentException>(() => WorkbenchPreferences.Resolve(new() { ["--driver"]=mode })).Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedFilterIncludesVisualGateFailuresAndAllNonPassingOutcomes()
    {
        var passed = new ScenarioResult("one","80x25","Passed","",[],true,true,true);
        var batch = new BatchResult("unused",[passed]) { Visual = new("manifest",[new("one","initial","80x25",VisualStatuses.BaselineMissing,"missing")]) };
        Assert.True(WorkbenchResultsDialog.IsFailure(batch,passed));
        Assert.False(WorkbenchResultsDialog.IsFailure(new("unused",[passed]),passed));
        foreach (string outcome in new[] { "Failed","Blocked","TimedOut","Error","Aborted" }) Assert.True(WorkbenchResultsDialog.IsFailure(batch,passed with { Outcome=outcome }));
    }

    [Fact]
    public void StatusContextsAreLinkedWithoutDuplicateOrContradictoryHints()
    {
        using var driver = new DriverScope();
        using var app = new TApplication();
        using var status = WorkbenchApp.CreateStatusLine(app.GetExtent());
        var main = status.Defs!;
        Assert.Equal(WorkbenchDialog.Context,main.Min);
        Assert.Equal(WorkbenchResultsDialog.Context,main.Next!.Min);
        var results = new List<TStatusItem>();
        for (var item=main.Next.Items;item is not null;item=item.Next) results.Add(item);
        Assert.Single(results, i => i.KeyCode == TSharpVision.Constants.Keys.kbEsc && i.Command == TSharpVision.Constants.Views.cmClose && i.Text == "~Esc~ Close");
        // Every context names F1 (Help since the redesign); no visible label is repeated within a context.
        for (var def = main; def is not null; def = def.Next)
        {
            var labels = new List<string>();
            for (var item = def.Items; item is not null; item = item.Next) if (item.Text is not null) labels.Add(item.Text);
            Assert.Equal(labels.Count, labels.Distinct().Count());
            Assert.Contains("~F1~ Help", labels);
        }
        Assert.IsType<WorkbenchStatusLine>(status);
        int escapes = 0;
        for (var item=main.Items;item is not null;item=item.Next) if(item.KeyCode==TSharpVision.Constants.Keys.kbEsc) escapes++;
        Assert.Equal(0,escapes); // Esc keeps the modeless project open; Alt+X exits the application.
    }
}
