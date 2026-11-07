using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>
/// Runs the Win32 console path in a child process hosted by <c>conhost.exe --headless</c> with an
/// explicit size, so the qualification never depends on (or touches) the caller's console: a
/// pseudo-console host refuses buffer resizing, and the caller's window may be smaller than the
/// grid. The child runs <see cref="ConsoleOutputPath"/> for every fixture at one grid and writes
/// its results as JSON; nothing is shown on screen.
/// </summary>
public sealed class ConsoleHostPath : IOutputPath
{
    private readonly Dictionary<GridSize, List<PathFixtureResult>> _cache = new();
    private readonly ConsoleOutputPath _shape = new(attach: false);

    public string Id => _shape.Id;
    public string Driver => _shape.Driver;
    public string Boundary => _shape.Boundary + "; child process under conhost.exe --headless sized per grid";
    public PathCapabilities Capabilities => _shape.Capabilities;
    public string? Unavailable { get; }
    public string ConsoleSource { get; private set; } = "conhost.exe --headless (child per grid)";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        WriteIndented = false,
    };

    public ConsoleHostPath()
    {
        if (!OperatingSystem.IsWindows()) Unavailable = "Win32 console API exists only on Windows";
        else if (!File.Exists(Conhost)) Unavailable = $"{Conhost} not found";
    }

    private static string Conhost => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");

    /// <summary>Host size: large enough for the grid and its resize target.</summary>
    public static GridSize HostSize(GridSize grid)
    {
        GridSize target = Fixtures.ResizeTarget(grid);
        return new GridSize(Math.Max(grid.Width, target.Width), Math.Max(grid.Height, target.Height));
    }

    public PathFixtureResult Result(string fixture, GridSize grid)
    {
        if (!_cache.TryGetValue(grid, out var results))
            _cache[grid] = results = RunChild(grid);
        return results.FirstOrDefault(r => r.Fixture == fixture)
               ?? new PathFixtureResult { Path = Id, Driver = Driver, Fixture = fixture, Grid = grid, Outcome = Outcomes.Error, Reason = "child produced no result" };
    }

    private List<PathFixtureResult> RunChild(GridSize grid)
    {
        string resultFile = Path.Combine(Path.GetTempPath(), $"tsv-console-{Guid.NewGuid():N}.json");
        GridSize host = HostSize(grid);
        try
        {
            var psi = new ProcessStartInfo(Conhost)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string a in new[] { "--headless", "--width", host.Width.ToString(), "--height", host.Height.ToString(), "--" }) psi.ArgumentList.Add(a);
            string[] external = Fixtures.ExternalFixtures.Where(e => e.Grid == grid).SelectMany(e => new[] { "--tvs1", e.File }).ToArray();
            foreach (string a in ChildCommand.Arguments(["console-child", grid.ToString(), resultFile, .. external])) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            // conhost renders the child's console as VT on its stdout; drain it so it never blocks.
            Task drain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            Task<string> err = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(true);
                return Failed(grid, "console child did not exit within 60 s");
            }
            process.StandardInput.Close();
            drain.Wait(5_000);
            if (!File.Exists(resultFile)) return Failed(grid, $"console child exit code {process.ExitCode}, no result file; {err.Result.Trim()}");
            var doc = JsonSerializer.Deserialize<ChildDocument>(File.ReadAllText(resultFile), Json)
                      ?? throw new InvalidDataException("empty console child result");
            if (doc.Schema != ArtifactWriter.Schema) throw new InvalidDataException($"console child result schema '{doc.Schema}' is not {ArtifactWriter.Schema}");
            ConsoleSource = $"conhost.exe --headless per grid, sized to max(grid, resize target), e.g. {host} for {grid}; child: {doc.ConsoleSource}";
            return doc.Results;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return Failed(grid, "corrupt console child result: " + ex.Message);
        }
        finally
        {
            try { File.Delete(resultFile); } catch { }
        }
    }

    private List<PathFixtureResult> Failed(GridSize grid, string reason) =>
        Fixtures.All.Select(f => new PathFixtureResult { Path = Id, Driver = Driver, Fixture = f, Grid = grid, Outcome = Outcomes.Error, Reason = reason }).ToList();

    /// <summary>The child side: run every fixture at <paramref name="grid"/> in this process's console.</summary>
    public static int Child(GridSize grid, string resultFile, IEnumerable<string> tvs1Files)
    {
        foreach (string file in tvs1Files) Fixtures.AddTvs1(file);
        using var path = new ConsoleOutputPath();
        var doc = new ChildDocument { Schema = ArtifactWriter.Schema, ConsoleSource = path.Unavailable ?? path.ConsoleSource };
        foreach (string fixture in Fixtures.All.Concat(Fixtures.ExternalFixtures.Where(e => e.Grid == grid).Select(e => e.Id)))
            doc.Results.Add(QualificationRunner.RunInProcess(path, fixture, grid));
        File.WriteAllText(resultFile, JsonSerializer.Serialize(doc, Json));
        return 0;
    }

    public sealed class ChildDocument
    {
        public string Schema { get; set; } = "";
        public string ConsoleSource { get; set; } = "";
        public List<PathFixtureResult> Results { get; } = new();
    }

    public void Begin(GridSize grid) => throw new NotSupportedException("runs in a conhost child; use Result()");
    public void Apply(FixtureStep step, StepCapture capture) => throw new NotSupportedException();
    public void Observe(StepCapture capture) => throw new NotSupportedException();
    public void Dispose() { }
}

/// <summary>How to start this executable again as a child (apphost when present, else the dotnet host).</summary>
internal static class ChildCommand
{
    public static IEnumerable<string> Arguments(params string[] args)
    {
        string dll = typeof(ChildCommand).Assembly.Location;
        string apphost = Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null);
        if (File.Exists(apphost)) return new[] { apphost }.Concat(args);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        return new[] { dotnet, dll }.Concat(args);
    }

    public static string ShellLine(params string[] args) => string.Join(" ", Arguments(args).Select(a => "'" + a.Replace("'", "'\\''") + "'"));
}
