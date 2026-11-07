using System.Collections.Concurrent;
using System.Text.Json;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativePixels.Tests;

/// <summary>
/// Runs each real renderer path once, in its own child process of the diagnostics executable
/// (native graphics state never enters the test host; a hang is killed by the timeout), and keeps
/// the parsed result for the tests of that path.
/// </summary>
public sealed class RealRendererFixture
{
    private readonly ConcurrentDictionary<string, Lazy<(PathResult? Result, string Dir, string Log)>> _runs = new();

    public static string OutputRoot { get; } = Path.Combine(
        RunSupport.FindWorkbenchRoot() ?? Path.GetTempPath(), "TestResults", "native-pixels", "native-pixels-tests",
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);

    public (PathResult? Result, string Dir, string Log) Run(string path) =>
        _runs.GetOrAdd(path, p => new Lazy<(PathResult?, string, string)>(() => Execute(p))).Value;

    /// <summary>
    /// Runs <paramref name="path"/> with its configuration read from a real .cfg file
    /// (<c>[graphics] fontName / fontSize / fontFit</c>) through the normal configuration loader.
    /// <paramref name="fit"/> is written verbatim, so an invalid value can be tested too.
    /// </summary>
    public (PathResult? Result, string Dir, string Log) Run(string path, string fit, string fontId = "consolas20") =>
        _runs.GetOrAdd($"{fit}--{fontId}--{path}", _ => new Lazy<(PathResult?, string, string)>(() => Execute(path, fit, fontId))).Value;

    /// <summary>
    /// Runs <paramref name="path"/> with no .cfg file; the child sets
    /// <c>GraphicsDriverConfiguration.FontFit = <paramref name="fit"/></c> in code (pinned font).
    /// </summary>
    public (PathResult? Result, string Dir, string Log) RunProgrammatic(string path, string fit) =>
        _runs.GetOrAdd($"programmatic-{fit}--{path}", _ => new Lazy<(PathResult?, string, string)>(() =>
        {
            string prefix = $"programmatic-{fit.ToLowerInvariant()}--consolas20--";
            string dir = Path.Combine(OutputRoot, prefix + path);
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "TSharpWorkbench.Diagnostics.NativePixels.exe" : "TSharpWorkbench.Diagnostics.NativePixels");
            var (code, stdout, stderr) = Parent.Exec(exe, ["child", path, dir, "--png", "minimal", "--programmatic-fit", fit], TimeSpan.FromSeconds(240));
            string file = Path.Combine(dir, ChildRunner.ResultName(path, prefix));
            PathResult? result = File.Exists(file) ? JsonSerializer.Deserialize<PathResult>(File.ReadAllText(file), ChildRunner.Json) : null;
            return (result, dir, $"exit {code}\n{stdout}\n{stderr}");
        })).Value;

    private static (PathResult?, string, string) Execute(string path, string fit, string fontId)
    {
        string prefix = $"{fit.ToLowerInvariant()}--{fontId}--";
        string dir = Path.Combine(OutputRoot, prefix + path);
        Directory.CreateDirectory(dir);
        string cfg = Path.Combine(dir, $"{fit.ToLowerInvariant()}--{fontId}.cfg");
        var (fontName, size) = FontSpec.Known[fontId];
        File.WriteAllText(cfg, FontSpec.ConfigText(fontName, size, fit));
        string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "TSharpWorkbench.Diagnostics.NativePixels.exe" : "TSharpWorkbench.Diagnostics.NativePixels");
        var (code, stdout, stderr) = Parent.Exec(exe, ["child", path, dir, "--png", "minimal", "--config", cfg, "--font-id", fontId], TimeSpan.FromSeconds(240));
        string log = $"exit {code}\n{stdout}\n{stderr}";
        string file = Path.Combine(dir, ChildRunner.ResultName(path, prefix));
        PathResult? result = File.Exists(file) ? JsonSerializer.Deserialize<PathResult>(File.ReadAllText(file), ChildRunner.Json) : null;
        return (result, dir, log);
    }

    private static (PathResult?, string, string) Execute(string path)
    {
        string dir = Path.Combine(OutputRoot, path);
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "TSharpWorkbench.Diagnostics.NativePixels.exe" : "TSharpWorkbench.Diagnostics.NativePixels");
        var (code, stdout, stderr) = Parent.Exec(exe, ["child", path, dir, "--png", "minimal"], TimeSpan.FromSeconds(240));
        string log = $"exit {code}\n{stdout}\n{stderr}";
        string file = Path.Combine(dir, ChildRunner.ResultName(path));
        PathResult? result = File.Exists(file) ? JsonSerializer.Deserialize<PathResult>(File.ReadAllText(file), ChildRunner.Json) : null;
        return (result, dir, log);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealRendererCollection : ICollectionFixture<RealRendererFixture>
{
    public const string Name = "Real SDL renderers";
}
