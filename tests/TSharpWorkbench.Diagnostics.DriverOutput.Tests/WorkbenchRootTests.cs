using TSharpWorkbench.Support;

namespace TSharpWorkbench.Diagnostics.DriverOutput.Tests;

[CollectionDefinition("Workbench root environment", DisableParallelization = true)]
public sealed class WorkbenchRootEnvironment { }

[Collection("Workbench root environment")]
public sealed class WorkbenchRootTests
{
    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    public void ExplicitInvalidRootIsRejected(string suffix)
    {
        string? previous = Environment.GetEnvironmentVariable("TSHARPWORKBENCH_ROOT");
        try
        {
            string value = suffix == "" ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) : suffix;
            Environment.SetEnvironmentVariable("TSHARPWORKBENCH_ROOT", value);
            Assert.Throws<InvalidOperationException>(() => WorkbenchPaths.FindRoot());
        }
        finally { Environment.SetEnvironmentVariable("TSHARPWORKBENCH_ROOT", previous); }
    }

    [Fact]
    public void FutureWorkbenchMarkerNeedsNoFrameworkSolution()
    {
        string? previous = Environment.GetEnvironmentVariable("TSHARPWORKBENCH_ROOT");
        string root = Path.Combine(Path.GetTempPath(), "workbench-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "TSharpWorkbench.slnx"), "<Solution />");
            Environment.SetEnvironmentVariable("TSHARPWORKBENCH_ROOT", root);
            Assert.Equal(root, WorkbenchPaths.FindRoot());
            Assert.Equal(root, WorkbenchPaths.OutputRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TSHARPWORKBENCH_ROOT", previous);
            Directory.Delete(root, true);
        }
    }
}
