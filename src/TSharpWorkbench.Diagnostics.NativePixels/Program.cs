using System.Text.Json;
using TSharpWorkbench.Diagnostics.DriverOutput;
using TSharpWorkbench.Diagnostics.NativePixels;

// Native-pixel qualification (Layer C).
//   run [--output <dir>] [--paths id,...] [--tvs1 <file-or-dir>]... [--commander <approved-dir>] [--png representative|minimal]
//       [--grids 80x25,140x40,20x6] [--timeout <s>]
//   child <path-id> <dir> [--tvs1 <file>]... [--png ...] [--grids ...] [--config <cfg> --font-id <id>]
//                                                                        (internal: one renderer path, own process;
//                                                                         --config: font/fontFit variant from a real .cfg;
//                                                                         --programmatic-fit <mode>: FontFit set in code)
//   run ... --font-fit condense,uniform [--fonts consolas20,jetbrains20]  (every path per fit mode and font)
//   font-fit-audit <out.json> [--fonts consolas20,...]                   (product masks per mode vs the oracle, no window)
//   backends                                                             (internal: SDL render/GPU drivers as JSON)
//   lifecycle <sdl-2d|sdl-gpu|sdl-gpu-fallback> <dir> [--modal] [--veto] [--busy]  (real app, exit from outside)
//   glyph-audit <out.json>                                               (product glyph masks vs the oracle, no window)
// Exit codes: 0 pass, 1 mismatch, 2 harness error, 3 required path unavailable, 64 usage.

if (args.Length >= 3 && args[0] == "lifecycle")
    return LifecycleChild.Run(args[1], args[2], args.Skip(3).ToHashSet());

if (args.Length == 2 && args[0] == "glyph-audit")
    return GlyphAudit.Execute(args[1]);

if (args.Length >= 2 && args[0] == "font-fit-audit")
    return FontFitAudit.Execute(args[1], args.Length >= 4 && args[2] == "--fonts" ? args[3].Split(',') : ["consolas20", "jetbrains20"]);

if (args.Length >= 1 && args[0] == "backends")
{
    Console.WriteLine(JsonSerializer.Serialize(Backends.Probe(), ChildRunner.Json));
    return 0;
}

if (args.Length >= 3 && args[0] == "child")
{
    var (tvs1, png, grids) = ParseCommon(args.Skip(3).ToArray(), out var childRest);
    FontSpec font = childRest.TryGetValue("--config", out string? cfg)
        ? FontSpec.FromConfigFile(cfg, childRest.GetValueOrDefault("--font-id") ?? "font")
        : childRest.TryGetValue("--programmatic-fit", out string? fit)
            ? FontSpec.ProgrammaticFit(fit)
            : FontSpec.Default;
    PathResult r = ChildRunner.Run(args[1], args[2], font, tvs1, png, grids);
    Console.WriteLine($"{r.Path}: {r.Outcome}{(r.Unavailable is { } u ? " — " + u : "")}{(r.Error is { } e ? " — " + e.Split('\n')[0] : "")}");
    return r.Outcome switch { "Match" => 0, "Mismatch" => 1, "Error" => 2, _ => 3 };
}

if (args.Length == 0 || args[0] != "run")
{
    Console.Error.WriteLine("usage: run [--output <dir>] [--paths id,...] [--tvs1 <file-or-dir>] [--commander <approved-dir>] [--png representative|minimal] [--grids WxH,...] [--timeout <s>]");
    return 64;
}

{
    var (tvs1, png, grids) = ParseCommon(args.Skip(1).ToArray(), out var rest);
    string? output = rest.GetValueOrDefault("--output");
    output ??= Path.Combine(RunSupport.WorkbenchOutputRoot, "artifacts", "native-pixels");
    int timeout = int.Parse(rest.GetValueOrDefault("--timeout") ?? "240");
    string[]? only = rest.GetValueOrDefault("--paths")?.Split(',');
    string[]? fits = rest.GetValueOrDefault("--font-fit")?.Split(',');
    string[]? fonts = rest.GetValueOrDefault("--fonts")?.Split(',');
    return Parent.Run(output, only, tvs1, png, grids, TimeSpan.FromSeconds(timeout), fits, fonts);
}

static (List<string> Tvs1, PngPolicy Png, List<GridSize> Grids) ParseCommon(string[] a, out Dictionary<string, string> rest)
{
    var tvs1 = new List<string>();
    var png = PngPolicy.Representative;
    var grids = Fixtures.DefaultGrids.ToList();
    rest = new Dictionary<string, string>();
    for (int i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--tvs1":
                string src = a[++i];
                tvs1.AddRange(Directory.Exists(src) ? Directory.GetFiles(src, "*.tvs1").Order() : [src]);
                break;
            case "--commander":
                string dir = a[++i];
                tvs1.AddRange(RunSupport.CommanderSelection.Select(n => Path.Combine(dir, n + ".tvs1")));
                break;
            case "--png": png = Enum.Parse<PngPolicy>(a[++i], ignoreCase: true); break;
            case "--grids": grids = a[++i].Split(',').Select(GridSize.Parse).ToList(); break;
            default:
                if (a[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < a.Length) rest[a[i]] = a[++i];
                else throw new ArgumentException($"unknown option {a[i]}");
                break;
        }
    }
    return (tvs1, png, grids);
}
