using System.Diagnostics;
using TSharpVision;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Manual;

// Manual: renders every step and assertion editor dialog to <repo>/artifacts/editor-dialogs/*.png.
// Skipped in normal runs; runs under "Debug Test" or with EDITOR_DIALOGS_CAPTURE set.
[Collection("NonParallel")]
public sealed class EditorDialogCaptureTests
{
    public sealed class ManualFactAttribute : FactAttribute
    {
        public ManualFactAttribute()
        {
            if (!Debugger.IsAttached && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDITOR_DIALOGS_CAPTURE")))
                Skip = "Manual capture: run with Debug Test (or set EDITOR_DIALOGS_CAPTURE).";
        }
    }

    [ManualFact, Trait("Category", "Manual")]
    public void CaptureAllStepAndAssertionDialogs()
    {
        string output = Path.Combine(RepositoryRoot(), "artifacts", "editor-dialogs");
        using var scope = new DriverScope(120, 36);
        using var app = new TApplication();
        string result = EditorDialogCapture.Run(app, output);
        Assert.DoesNotContain("errors:", result);
        Assert.Equal(ScenarioSchemaV1.StepKinds.Length + ScenarioSchemaV1.AssertionKinds.Length,
            Directory.GetFiles(output, "*.png").Length);
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TSharpWorkbench.slnx not found.");
    }
}
