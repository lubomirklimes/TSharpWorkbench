using TSharpWorkbench.Diagnostics.NativeHosts;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativeHosts.Tests;

/// <summary>
/// Real hosts: the diagnostics executable opens test-owned classic conhost / Windows Terminal windows
/// running the real application on the Console driver (small grid: calibration, desktop, dialog, Alt+X exit)
/// and captures their pixels. A host that is not installed is skipped, never reported as passed.
/// </summary>
public sealed class RealHostTests
{
    private static RunReport Run(string session)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "native console hosts are Windows-only");
        string root = Path.Combine(RunSupport.FindWorkbenchRoot() ?? Path.GetTempPath(), "TestResults", "native-hosts", "native-hosts-tests");
        string exe = Path.Combine(AppContext.BaseDirectory, "TSharpWorkbench.Diagnostics.NativeHosts.exe");
        var (code, stdout, stderr) = Parent.Exec(exe, ["run", "--output", root, "--sessions", session], TimeSpan.FromSeconds(180));
        string dir = stdout.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith("Native hosts ", StringComparison.Ordinal)).Split(": ", 2)[1];
        var report = Json.Read<RunReport>(Path.Combine(dir, "run.json"));
        Assert.True(code is 0 or 3, $"exit {code}\n{stdout}\n{stderr}");
        return report;
    }

    private static void AssertSession(SessionResult s)
    {
        Assert.True(s.Outcome is Outcome.Passed or Outcome.ManualReviewRequired, $"{s.Id}: {s.Outcome} {s.Error}\n" +
            string.Join("\n", s.Steps.SelectMany(x => x.Failures.Select(f => $"{x.Scenario}: {f}"))));
        Assert.Contains(s.Steps, x => x.Scenario == "calibrate" && x.Calibration?.Geometry != null && x.Png != null);
        Assert.All(s.Steps.Where(x => x.Cells != null), x => Assert.True(x.Cells!.CellsChecked >= 400));
        Assert.NotNull(s.Exit);
        Assert.True(s.Exit!.Exited && s.Exit.Code == 0 && s.Exit.WindowClosed, $"exit: {s.Exit.Method} exited={s.Exit.Exited} code={s.Exit.Code} closed={s.Exit.WindowClosed}");
        Assert.Equal(16, s.MeasuredPalette.Length);
    }

    [SkippableFact]
    public void Classic_conhost_renders_the_console_driver_application_and_exits()
    {
        var report = Run("conhost-console-app-40x12");
        var s = Assert.Single(report.Sessions);
        Assert.Equal("ConsoleWindowClass", s.WindowClass);
        AssertSession(s);
    }

    [SkippableFact]
    public void Windows_Terminal_renders_the_console_driver_application_and_exits()
    {
        var report = Run("wt-console-app-40x12");
        var s = Assert.Single(report.Sessions);
        Skip.If(s.Outcome == Outcome.PlatformUnavailable, $"Windows Terminal unavailable: {s.Error}");
        Assert.Equal("CASCADIA_HOSTING_WINDOW_CLASS", s.WindowClass);
        AssertSession(s);
    }
}
