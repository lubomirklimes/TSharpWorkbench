using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Optional logical demo export belongs to Workbench; framework assertions stay in GroupBoxTests.
[Collection("NonParallel")]
public sealed class GroupBoxDemoCaptureTests
{
    public sealed class CaptureTheoryAttribute : TheoryAttribute
    {
        public CaptureTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TGROUPBOX_ARTIFACTS")))
                Skip = "Set TGROUPBOX_ARTIFACTS to export the logical GroupBox demo.";
        }
    }

    [CaptureTheory]
    [InlineData(80, 25)]
    [InlineData(140, 40)]
    public void ExportDemo(int width, int height)
    {
        using var scope = new DriverScope((ushort)width, (ushort)height);
        using var app = new TApplication();
        int left = (app.DeskTop!.size.x - 64) / 2;
        int top = (app.DeskTop.size.y - 19) / 2;
        var dialog = GroupBoxCaptureFixture.CreateGroupBoxDemoDialog(new TRect(left, top, left + 64, top + 19));
        app.DeskTop.Insert(dialog); app.Redraw();
        if (Environment.GetEnvironmentVariable("TGROUPBOX_ARTIFACTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var snapshot = ScreenSnapshot.CopyRoot();
            string stem = Path.Combine(directory, $"groupbox-demo-{width}x{height}");
            File.WriteAllBytes(stem + ".png", LogicalPng.Encode(snapshot));
            File.WriteAllBytes(stem + ".tvs", snapshot.Serialize());
            File.WriteAllLines(stem + ".txt", Enumerable.Range(0, height).Select(y =>
                new string(Enumerable.Range(0, width).Select(x => snapshot[x, y].Character).ToArray())));
        }
    }
}
