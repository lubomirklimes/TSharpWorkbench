using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.DriverOutput.Tests;

/// <summary>
/// One set of driver paths for the whole run: SDL is initialised once per process, the terminal path
/// redirects Console.Out while it drives the driver, and the queue checks use TEventQueue — all
/// process-wide, so the tests share one non-parallel collection.
/// </summary>
public sealed class DriverPathsFixture : IDisposable
{
    public DriverPaths Paths { get; } = new();

    public void Dispose() => Paths.Dispose();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverCollection : ICollectionFixture<DriverPathsFixture>
{
    public const string Name = "Driver output";
}

internal static class Expect
{
    public static readonly GridSize[] Grids = Fixtures.DefaultGrids;

    public static void Match(PathFixtureResult result)
    {
        string detail = result.Reason ?? string.Join("; ", result.Comparisons.Where(c => c.Outcome != Outcomes.Match)
            .Select(c => $"{c.Step}: {c.TextMismatches}/{c.FgMismatches}/{c.BgMismatches} " + string.Join(", ", c.Failures.Concat(c.FirstMismatches.Take(5).Select(m => m.ToString())))));
        Assert.True(result.Outcome == Outcomes.Match, $"{result.Path} {result.Fixture} {result.Grid}: {result.Outcome} — {detail}");
        Assert.NotEmpty(result.Comparisons);
    }
}
