using TSharpWorkbench.Diagnostics.NativeHosts;
using TSharpWorkbench.Diagnostics.NativePixels;

// Native host rendering qualification (Layer C, Windows console hosts).
//   run [--output <dir>] [--commander <approved logical dir>] [--sessions id,...]
//   app <dir> [--driver console] [--grid WxH] [--font Face:px]          (internal: runs inside a host)
//   screen <dir> --driver console|terminal [--grid WxH] [--font Face:px] (internal: runs inside a host)
//   inject <pid> <key,key,...>                                           (internal: writes key records into one console)
// Exit codes: 0 pass (or manual review only), 1 failure, 2 harness error, 3 partial (host unavailable), 64 usage.

if (args.Length == 3 && args[0] == "console-lease")
    return ApplicationExecution.Lease(int.Parse(args[1]), args[2]);

if (args.Length >= 2 && args[0] is "app" or "screen")
    return HostChild.Run(args[0], args[1], Options(args.Skip(2)));

if (args.Length == 3 && args[0] == "inject")
    return InputInjector.Run(uint.Parse(args[1]), args[2]);

if (args.Length >= 1 && args[0] == "run")
{
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Host qualification is Windows-only (PlatformUnavailable)."); return 3; }
    var o = Options(args.Skip(1));
    string root = RunSupport.WorkbenchOutputRoot;
    string output = o.GetValueOrDefault("--output") ?? Path.Combine(root, "artifacts", "native-hosts");
    string? commander = o.GetValueOrDefault("--commander");
    // External baseline inputs are explicit; never discover a sibling Commander checkout.
    ISet<string>? only = o.GetValueOrDefault("--sessions")?.Split(',').ToHashSet();
    return HostRun.Run(output, commander, only);
}

Console.Error.WriteLine("usage: run [--output <dir>] [--commander <dir>] [--sessions id,...]");
return 64;

static Dictionary<string, string> Options(IEnumerable<string> a)
{
    var d = new Dictionary<string, string>();
    var list = a.ToList();
    for (int i = 0; i + 1 < list.Count; i += 2) d[list[i]] = list[i + 1];
    return d;
}
