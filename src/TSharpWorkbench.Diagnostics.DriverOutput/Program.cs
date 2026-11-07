using TSharpWorkbench.Diagnostics.DriverOutput;

// Driver output qualification (Layer B).
//   run [--output <dir>] [--grids 80x25,140x40,20x6] [--paths console-win32,terminal-ansi,...] [--no-parity] [--pty]
//       [--tvs1 <file-or-dir>]   extra read-only fixtures from TVS1 logical screens (e.g. approved logical baselines)
//   pty-child <WxH>       (internal: the real AnsiTerminalDriver on the PTY created by `script`)
//   console-child <WxH> <result.json>  (internal: the Win32 console path inside conhost --headless)
// Exit codes: 0 pass, 1 mismatch, 2 harness error, 64 usage.

if (args.Length >= 2 && args[0] == "pty-child")
    return QualificationRunner.PtyChild(GridSize.Parse(args[1]));
if (args.Length >= 3 && args[0] == "console-child")
    return ConsoleHostPath.Child(GridSize.Parse(args[1]), args[2], args.Skip(3).Where((a, i) => i % 2 == 1));

if (args.Length == 0 || args[0] != "run")
{
    Console.Error.WriteLine("usage: run [--output <dir>] [--grids WxH,...] [--paths id,...] [--no-parity] [--pty]");
    return 64;
}

string? output = null;
var grids = Fixtures.DefaultGrids.ToList();
HashSet<string>? only = null;
bool parity = true, pty = OperatingSystem.IsLinux();
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output": output = args[++i]; break;
        case "--grids": grids = args[++i].Split(',').Select(GridSize.Parse).ToList(); break;
        case "--paths": only = args[++i].Split(',').ToHashSet(); break;
        case "--no-parity": parity = false; break;
        case "--pty": pty = true; break;
        case "--tvs1":
            string source = args[++i];
            foreach (string file in Directory.Exists(source) ? Directory.GetFiles(source, "*.tvs1").Order().ToArray() : new[] { source }) Fixtures.AddTvs1(file);
            break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 64;
    }
}

output ??= Path.Combine(TSharpWorkbench.Support.WorkbenchPaths.OutputRoot, "artifacts", "driver-output");

using var paths = new DriverPaths();
var run = new QualificationRun
{
    RunId = ArtifactWriter.NewRunId(),
    Began = DateTimeOffset.UtcNow,
    Paths = paths.All,
    Grids = grids,
    SdlBackend = paths.SdlRenderer.Unavailable ?? $"video={paths.SdlRenderer.VideoDriver} renderer={paths.SdlRenderer.RenderDriver} cell={paths.SdlRenderer.CellWidth}x{paths.SdlRenderer.CellHeight}",
};

foreach (IOutputPath path in paths.All.Where(p => only == null || only.Contains(p.Id)))
    foreach (string fixture in Fixtures.All)
        foreach (GridSize grid in grids)
        {
            PathFixtureResult r = QualificationRunner.Run(path, fixture, grid);
            run.Results.Add(r);
            Console.WriteLine($"{r.Path,-18} {r.Fixture,-7} {r.Grid,-7} {r.Outcome}{(r.Reason is { } why ? " â€” " + why : "")}");
        }

foreach (var (id, file, grid) in Fixtures.ExternalFixtures)
    foreach (IOutputPath path in paths.All.Where(p => only == null || only.Contains(p.Id)))
    {
        PathFixtureResult r = QualificationRunner.Run(path, id, grid);
        run.Results.Add(r);
        Console.WriteLine($"{r.Path,-18} {id} {r.Outcome}{(r.Reason is { } why ? " â€” " + why : "")}");
    }

if (parity)
{
    run.Parity.AddRange(InputParity.Run(paths.SdlRenderer.Unavailable == null ? paths.SdlRenderer : null));
    Console.WriteLine($"input parity: {run.Parity.Count(p => p.Outcome == Outcomes.Verified)} verified, " +
                      $"{run.Parity.Count(p => p.Outcome == Outcomes.Mismatch)} mismatch, {run.Parity.Count(p => p.Outcome == Outcomes.NotSupported)} not supported, " +
                      $"{run.Parity.Count(p => p.Outcome == Outcomes.Unavailable)} unavailable");
}

if (pty)
    foreach (GridSize grid in grids.Where(g => g.Width >= 20))
    {
        PtySmokeResult p = QualificationRunner.RunPtySmoke(grid);
        run.PtySmoke.Add(p);
        Console.WriteLine($"pty smoke {grid}: {p.Outcome} {p.Detail}");
    }

run.ConsoleSource = paths.Console.Unavailable ?? paths.Console.ConsoleSource;
string dir = ArtifactWriter.Write(run, output);
Console.WriteLine($"artifacts: {dir}");
Console.WriteLine($"exit code {run.ExitCode}");
return run.ExitCode;
