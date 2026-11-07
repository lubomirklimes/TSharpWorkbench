using TSharpVision;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Focused follow-ups: the Run Configuration summary says when its list holds more rows than it shows (120x36), and
// the Scenarios window (Alt+2) remembers its geometry like the other modeless windows.
[Collection("NonParallel")]
public sealed class WindowGeometryFollowUpTests
{
    private static RunDialogContext Context(WorkbenchProject defaults, RunProblem[] problems) =>
        new("All scenarios", _ => 1, [], "no audited compiled native scenarios in this catalog.", true,
            _ => new(problems, ["Runs 1 scenario.", "Target proxy-target.exe", "Output runs"], "", "", ""), defaults);

    private static RunProblem[] ManyProblems => Enumerable.Range(1, 6)
        .Select(i => new RunProblem(RunArea.General, $"Reason {i}: scenario external/demo/{i} cannot run with the selected driver.")).ToArray();

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public void RunConfigurationSummaryKeepsEveryReasonReachable(int width, int height)
    {
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new TApplication();
        var project = new WorkbenchProject { Target = "proxy-target.exe" };
        using var dialog = new WorkbenchRunDialog(app.DeskTop!, project, Context(project, ManyProblems));
        // Every reason is a row of the scrollable list, first in the list, and none is cut to "...".
        string[] lines = dialog.SummaryLines;
        for (int i = 1; i <= 6; i++) Assert.Contains(lines, l => l.Contains($"Reason {i}:"));
        Assert.StartsWith("! Reason 1:", lines[0]);
        Assert.True(dialog.Summary.range > dialog.Summary.size.y, "six reasons and the summary exceed the visible rows");
        // The hint names the hidden rows and how to reach them; the list itself is keyboard reachable (Alt+U).
        Assert.Contains($"{dialog.Summary.range} lines, {dialog.Summary.size.y} shown", dialog.SummaryHint);
        Assert.Contains("Alt+U", dialog.SummaryHint);
        Assert.True(dialog.RunButton.GetState(Constants.Views.sfDisabled));
        // Scrolling to the end shows the last row (the verdict) inside the list bounds.
        dialog.Summary.FocusItemNum(dialog.Summary.range - 1);
        Assert.InRange(dialog.Summary.range - 1, dialog.Summary.topItem, dialog.Summary.topItem + dialog.Summary.size.y - 1);
        Assert.Contains("Run is unavailable: 6 problems", dialog.Summary.GetText(dialog.Summary.range - 1, 200));
        // Hint and list stay inside the dialog, below the last group box and above the Save-defaults row.
        Assert.True(dialog.Summary.origin.y + dialog.Summary.size.y <= dialog.SaveDefaults.origin.y);
        Assert.True(dialog.Summary.origin.x + dialog.Summary.size.x < dialog.size.x);
        app.ShutDown();
    }

    [Fact]
    public void RunConfigurationSummaryHintIsEmptyWhenEverythingFits()
    {
        using var driver = new DriverScope(160, 50);
        using var app = new TApplication();
        var project = new WorkbenchProject { Target = "proxy-target.exe" };
        using var dialog = new WorkbenchRunDialog(app.DeskTop!, project, Context(project, []));
        Assert.True(dialog.Summary.range <= dialog.Summary.size.y);
        Assert.Equal("", dialog.SummaryHint);
        Assert.Contains("Ready to run.", dialog.SummaryLines);
        app.ShutDown();
    }

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public void ScenariosWindowUsesRememberedGeometryOnlyWhenItFits(int width, int height)
    {
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new TApplication();
        var desktop = app.DeskTop!;
        TRect fallback = WorkbenchScenariosWindow.DefaultBounds(desktop);
        Assert.True(fallback.b.x - fallback.a.x >= Math.Min(114, desktop.size.x));
        var remembered = new TRect(1, 1, 1 + Math.Min(116, desktop.size.x - 1), 1 + Math.Min(24, desktop.size.y - 1));
        Assert.Equal(remembered, WorkbenchScenariosWindow.DefaultBounds(desktop, remembered));
        // Off the desktop (saved on a larger grid) or below the minimum size: back to the centred default.
        Assert.Equal(fallback, WorkbenchScenariosWindow.DefaultBounds(desktop, new TRect(10, 2, desktop.size.x + 5, 30)));
        Assert.Equal(fallback, WorkbenchScenariosWindow.DefaultBounds(desktop, new TRect(0, 0, 60, 10)));
        using var window = new WorkbenchScenariosWindow(desktop, remembered);
        Assert.Equal(remembered, window.GetBounds());
        app.ShutDown();
    }

    [Fact]
    public void ScenariosWindowGeometryIsSavedOnCloseAndRestoredOnReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "ui-geometry-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "settings.json");
            var moved = new TRect(2, 1, 120, 30);
            WorkbenchPreferences.Update(settings, p => p.WithWindow("scenarios", moved));
            Assert.Equal(moved, WorkbenchPreferences.Load(settings).Window("scenarios"));
            using var driver = new DriverScope(140, 40);
            using var app = new TApplication();
            using var window = new WorkbenchScenariosWindow(app.DeskTop!, WorkbenchPreferences.Load(settings).Window("scenarios"));
            Assert.Equal(moved, window.GetBounds());
            app.ShutDown();
        }
        finally { Directory.Delete(root, true); }
    }
}
