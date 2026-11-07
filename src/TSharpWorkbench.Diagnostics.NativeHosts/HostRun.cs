using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using TSharpWorkbench.Diagnostics.DriverOutput;
using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativeHosts;

public static class Outcome
{
    public const string Passed = "Passed", Failed = "Failed", NotRun = "NotRun", PlatformUnavailable = "PlatformUnavailable",
        NativeCaptureUnavailable = "NativeCaptureUnavailable", ManualReviewRequired = "ManualReviewRequired";
}

public sealed class StepResult
{
    public string Scenario { get; set; } = "";
    public string Grid { get; set; } = "";
    public string Outcome { get; set; } = NativeHosts.Outcome.Passed;
    public string? Expected { get; set; }
    public string? Input { get; set; }
    public string? Png { get; set; }
    public string? Pixels { get; set; }
    public int CaptureWidth { get; set; }
    public int CaptureHeight { get; set; }
    public List<string> Checks { get; } = new();
    public List<string> Failures { get; } = new();
    public List<string> ManualReview { get; } = new();
    public CalibrationResult? Calibration { get; set; }
    public CellCheck? Cells { get; set; }
    public UpdateCheck? Update { get; set; }
    public CursorCheck? Cursor { get; set; }

    public void Check(bool ok, string text)
    {
        Checks.Add((ok ? "PASS " : "FAIL ") + text);
        if (!ok) { Failures.Add(text); Outcome = NativeHosts.Outcome.Failed; }
    }

    public void Manual(string text)
    {
        ManualReview.Add(text);
        if (Outcome == NativeHosts.Outcome.Passed) Outcome = NativeHosts.Outcome.ManualReviewRequired;
    }
}

public sealed class ExitResult
{
    public string Method { get; set; } = "";
    public string Input { get; set; } = "";
    public bool Exited { get; set; }
    public int Code { get; set; }
    public bool WindowClosed { get; set; }
    public double Seconds { get; set; }
}

public sealed class SessionResult
{
    public string Id { get; set; } = "";
    public string Host { get; set; } = "";
    public string HostVersion { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Boundary { get; set; } = "";
    public string RequestedGrid { get; set; } = "";
    public string ActualGrid { get; set; } = "";
    public string Outcome { get; set; } = NativeHosts.Outcome.Passed;
    public string? Error { get; set; }
    public ConsoleProbe? Probe { get; set; }
    public string WindowClass { get; set; } = "";
    public uint Dpi { get; set; }
    public string CaptureMechanism { get; set; } = WindowCapture.Mechanism;
    public string CaptureBoundary { get; set; } = "";
    public string Font { get; set; } = "";
    public string PaletteSource { get; set; } = "";
    public string[] DeclaredPalette { get; set; } = [];
    public string[] MeasuredPalette { get; set; } = [];
    public List<StepResult> Steps { get; } = new();
    public ExitResult? Exit { get; set; }
    public List<string> Log { get; } = new();
}

public sealed class NegativeControlResult
{
    public string Name { get; set; } = "";
    public string Mutation { get; set; } = "";
    public bool Detected { get; set; }
    public string Observed { get; set; } = "";
}

public sealed class RunReport
{
    public string RunId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Os { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public double Seconds { get; set; }
    public Dictionary<string, string> Hosts { get; } = new();
    public Dictionary<string, string> TerminalSettings { get; } = new();
    public string? CommanderDirectory { get; set; }
    public List<SessionResult> Sessions { get; } = new();
    public List<NegativeControlResult> NegativeControls { get; } = new();
    public string Outcome { get; set; } = NativeHosts.Outcome.Passed;
}

/// <summary>The native-host parent: runs the session plan and writes flat artifacts into one run directory.</summary>
internal sealed class HostRun
{
    private readonly string _dir, _exe, _work;
    private readonly string? _commander;
    private readonly RunReport _report = new();
    private readonly ISet<string>? _only;

    public static readonly string[] CommanderScreens =
    [
        "commander-input-panel-selection--desktop--80x25",
        "commander-input-menu-mouse--managers-open--80x25",
        "commander-input-dialog-mouse--typed--80x25",
        "commander-input-jobs-indicator--jobs--140x40",
        "commander-menu-status-availability--viewer--140x40",
    ];

    private HostRun(string dir, string? commander, ISet<string>? only)
    {
        _dir = dir; _commander = commander; _only = only;
        _exe = Path.Combine(AppContext.BaseDirectory, "TSharpWorkbench.Diagnostics.NativeHosts.exe");
        _work = Path.Combine(Path.GetTempPath(), "wb-" + Path.GetFileName(dir));
    }

    public static int Run(string outputRoot, string? commander, ISet<string>? only)
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        string runId = $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        string dir = Path.Combine(outputRoot, runId);
        Directory.CreateDirectory(dir);
        var run = new HostRun(dir, commander, only);
        run._report.RunId = runId;
        int code = run.Execute();
        Console.WriteLine($"Native hosts {run._report.Outcome}: {dir}");
        return code;
    }

    private int Execute()
    {
        var sw = Stopwatch.StartNew();
        _report.StartedUtc = DateTime.UtcNow;
        _report.Machine = Environment.MachineName;
        _report.Os = Environment.OSVersion.VersionString;
        _report.CommanderDirectory = _commander;
        string conhostPath = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        _report.Hosts["conhost"] = File.Exists(conhostPath) ? FileVersionInfo.GetVersionInfo(conhostPath).FileVersion ?? "?" : "missing";
        bool wt = WindowsTerminalAvailable(out string wtInfo);
        _report.Hosts["windows-terminal"] = wtInfo;
        ReadTerminalSettings();

        string[] c80 = CommanderScreens.Where(s => s.EndsWith("80x25")).ToArray();
        string[] c140 = CommanderScreens.Where(s => s.EndsWith("140x40")).ToArray();
        var plan = new List<(string Id, string Host, string Mode, string Driver, string Grid, Action<SessionRunner> Body)>
        {
            ("conhost-console-app-80x25", HostSession.Conhost, "app", "console", "80x25", r => r.AppMain(exitByMenu: true)),
            ("conhost-console-app-140x40", HostSession.Conhost, "app", "console", "140x40", r => r.AppResize()),
            ("conhost-console-app-40x12", HostSession.Conhost, "app", "console", "40x12", r => r.AppSmall()),
            ("conhost-console-screen-80x25", HostSession.Conhost, "screen", "console", "80x25", r => r.Screen(full: true, c80)),
            ("conhost-console-screen-140x40", HostSession.Conhost, "screen", "console", "140x40", r => r.Screen(full: false, c140)),
            ("conhost-terminal-screen-80x25", HostSession.Conhost, "screen", "terminal", "80x25", r => r.Screen(full: true, [])),
            ("wt-console-app-80x25", HostSession.WindowsTerminal, "app", "console", "80x25", r => r.AppMain(exitByMenu: true)),
            ("wt-console-app-140x40", HostSession.WindowsTerminal, "app", "console", "140x40", r => r.AppResize()),
            ("wt-console-app-40x12", HostSession.WindowsTerminal, "app", "console", "40x12", r => r.AppSmall()),
            ("wt-console-screen-80x25", HostSession.WindowsTerminal, "screen", "console", "80x25", r => r.Screen(full: false, c80)),
            ("wt-terminal-screen-80x25", HostSession.WindowsTerminal, "screen", "terminal", "80x25", r => r.Screen(full: true, c80)),
            ("wt-terminal-screen-140x40", HostSession.WindowsTerminal, "screen", "terminal", "140x40", r => r.Screen(full: false, c140)),
        };

        foreach (var p in plan)
        {
            if (_only != null && !_only.Contains(p.Id)) continue;
            var result = new SessionResult
            {
                Id = p.Id, Host = p.Host, Mode = p.Mode, Driver = p.Driver, RequestedGrid = p.Grid,
                HostVersion = _report.Hosts[p.Host],
                Boundary = p.Mode == "app"
                    ? "real TSharpVision application (TApplication, AppLifecycleGuard) on the Win32 Console driver, real host input"
                    : p.Driver == "terminal"
                        ? "Terminal driver production VT output (WriteBuf/SetCaretPosition/SetCursorType) attached to the host's stdout; no application loop (driver has no Windows TTY attach)"
                        : "Console driver production output (WriteConsoleOutputW) of fixture screens; no application loop",
            };
            _report.Sessions.Add(result);
            if (p.Host == HostSession.WindowsTerminal && !wt) { result.Outcome = Outcome.PlatformUnavailable; result.Error = wtInfo; continue; }
            Console.WriteLine($"[{p.Id}]");
            HostSession? session = null;
            try
            {
                session = HostSession.Launch(p.Host, p.Mode, p.Driver, p.Grid, _exe, Path.Combine(_work, p.Id));
                result.Log.AddRange(session.Log);
                var runner = new SessionRunner(this, session, result);
                runner.Start();
                p.Body(runner);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Outcome = Outcome.Failed;
                result.Log.Add(ex.ToString());
            }
            finally
            {
                if (session != null) { result.Log.AddRange(session.Log.Skip(result.Log.Count > 0 ? 0 : 0).Except(result.Log)); session.Dispose(); }
            }
            Finish(result);
            Console.WriteLine($"  {result.Outcome}{(result.Error != null ? " — " + result.Error : "")}");
        }

        _report.Seconds = sw.Elapsed.TotalSeconds;
        var executed = _report.Sessions.Where(s => s.Outcome != Outcome.PlatformUnavailable && s.Outcome != Outcome.NotRun).ToList();
        _report.Outcome = _report.Sessions.Any(s => s.Outcome is Outcome.Failed) ? Outcome.Failed
            : _report.NegativeControls.Any(n => !n.Detected) ? Outcome.Failed
            : _report.Sessions.Any(s => s.Outcome is Outcome.PlatformUnavailable or Outcome.NativeCaptureUnavailable) ? "Partial"
            : _report.Sessions.Any(s => s.Outcome is Outcome.ManualReviewRequired) ? Outcome.ManualReviewRequired
            : executed.Count == 0 ? Outcome.NotRun : Outcome.Passed;
        Json.Write(Path.Combine(_dir, "run.json"), _report);
        File.WriteAllText(Path.Combine(_dir, "report.md"), Markdown());
        try { Directory.Delete(_work, recursive: true); } catch { }
        return _report.Outcome switch { Outcome.Passed or Outcome.ManualReviewRequired => 0, "Partial" => 3, _ => 1 };
    }

    private static void Finish(SessionResult r)
    {
        if (r.Outcome == Outcome.Failed) return;
        if (r.Steps.Any(s => s.Outcome == Outcome.NativeCaptureUnavailable)) r.Outcome = Outcome.NativeCaptureUnavailable;
        else if (r.Steps.Any(s => s.Outcome == Outcome.Failed) || r.Exit is { } e && (!e.Exited || e.Code != 0 || !e.WindowClosed)) r.Outcome = Outcome.Failed;
        else if (r.Steps.Any(s => s.Outcome == Outcome.ManualReviewRequired)) r.Outcome = Outcome.ManualReviewRequired;
    }

    private static bool WindowsTerminalAvailable(out string info)
    {
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (!File.Exists(alias)) { info = "not installed (no wt.exe app execution alias)"; return false; }
        string pkgRoot = @"C:\Program Files\WindowsApps";
        string? version = null;
        try
        {
            version = Directory.GetDirectories(pkgRoot, "Microsoft.WindowsTerminal_*_x64__8wekyb3d8bbwe")
                .Select(Path.GetFileName).OrderByDescending(n => n).FirstOrDefault()?.Split('_')[1];
        }
        catch (UnauthorizedAccessException) { }
        info = version != null ? $"Microsoft.WindowsTerminal {version}" : "installed (version not readable)";
        return true;
    }

    private void ReadTerminalSettings()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
        _report.TerminalSettings["file"] = path + " (read only)";
        if (!File.Exists(path)) return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
            var defaults = root["profiles"]?["defaults"];
            _report.TerminalSettings["defaultProfile"] = root["defaultProfile"]?.ToString() ?? "";
            _report.TerminalSettings["defaults.colorScheme"] = defaults?["colorScheme"]?.ToString() ?? "(built-in default: Campbell)";
            _report.TerminalSettings["defaults.font"] = defaults?["font"]?.ToJsonString() ?? "(built-in default: Cascadia Mono 12pt)";
            _report.TerminalSettings["defaults.cursorShape"] = defaults?["cursorShape"]?.ToString() ?? "(built-in default: bar)";
            _report.TerminalSettings["defaults.padding"] = defaults?["padding"]?.ToString() ?? "(built-in default: 8)";
            _report.TerminalSettings["defaults.adjustIndistinguishableColors"] = defaults?["adjustIndistinguishableColors"]?.ToString() ?? "(built-in default)";
            _report.TerminalSettings["windowingBehavior"] = root["windowingBehavior"]?.ToString() ?? "";
        }
        catch (Exception ex) { _report.TerminalSettings["error"] = ex.Message; }
    }

    internal string Artifact(string name) => Path.Combine(_dir, name);
    internal string? CommanderFile(string id) => _commander == null ? null : Path.Combine(_commander, id + ".tvs1");
    internal string Exe => _exe;
    internal RunReport Report => _report;
    internal string? TerminalScheme => _report.TerminalSettings.GetValueOrDefault("defaults.colorScheme");

    // ── report ──────────────────────────────────────────────────────────────

    private string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Native host rendering — {_report.RunId}");
        sb.AppendLine();
        sb.AppendLine($"Outcome: **{_report.Outcome}** · {_report.Sessions.Count} sessions · {_report.Seconds:0}s · {_report.Os}");
        sb.AppendLine();
        sb.AppendLine("Layer C: pixels are captured from the real host window (" + WindowCapture.Mechanism + ").");
        sb.AppendLine("Expected cells come from the host console buffer readback (app sessions) or the fixture's logical screen (screen sessions).");
        sb.AppendLine();
        sb.AppendLine("## Hosts");
        foreach (var (k, v) in _report.Hosts) sb.AppendLine($"- {k}: {v}");
        foreach (var (k, v) in _report.TerminalSettings) sb.AppendLine($"- terminal settings {k}: {v}");
        sb.AppendLine();
        sb.AppendLine("## Sessions");
        sb.AppendLine();
        sb.AppendLine("| Session | Outcome | Grid | DPI | Font | Palette | Steps (pass/fail/manual) | Exit |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in _report.Sessions)
        {
            string steps = $"{s.Steps.Count(x => x.Outcome == Outcome.Passed)}/{s.Steps.Count(x => x.Outcome == Outcome.Failed)}/{s.Steps.Count(x => x.Outcome == Outcome.ManualReviewRequired)}";
            string exit = s.Exit == null ? "—" : $"{s.Exit.Method}: {(s.Exit.Exited ? $"exit {s.Exit.Code}" : "NO EXIT")}, window {(s.Exit.WindowClosed ? "closed" : "OPEN")}, {s.Exit.Seconds:0.0}s";
            sb.AppendLine($"| {s.Id} | {s.Outcome}{(s.Error != null ? " — " + s.Error.Replace("|", "/") : "")} | {s.ActualGrid} | {s.Dpi} | {s.Font} | {s.PaletteSource} | {steps} | {exit} |");
        }
        foreach (var s in _report.Sessions.Where(s => s.Steps.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine($"### {s.Id}");
            sb.AppendLine();
            sb.AppendLine($"Boundary: {s.Boundary}. Window class {s.WindowClass}; capture boundary: {s.CaptureBoundary}.");
            if (s.MeasuredPalette.Length > 0) sb.AppendLine($"Measured palette: {string.Join(" ", s.MeasuredPalette)}");
            sb.AppendLine();
            foreach (var st in s.Steps)
            {
                sb.AppendLine($"- **{st.Scenario}** {st.Grid}: {st.Outcome}{(st.Png != null ? $" — `{st.Png}`" : "")}{(st.Input != null ? $" — input: {st.Input}" : "")}");
                if (st.Cells != null) sb.AppendLine($"  - cells: {st.Cells.CellsChecked} checked, {st.Cells.CellsFailed} failed; rules: {string.Join(", ", st.Cells.Rules.Select(r => $"{r.Key} {r.Value.Checked - r.Value.Failed}/{r.Value.Checked}"))}");
                if (st.Cells?.Diacritics.Pairs > 0) sb.AppendLine($"  - diacritics: {st.Cells.Diacritics.Pairs - st.Cells.Diacritics.Failed}/{st.Cells.Diacritics.Pairs} accented letters inked above their base letter ({st.Cells.Diacritics.TouchingCellTop} touch the cell top)");
                if (st.Update != null) sb.AppendLine($"  - update: {st.Update.LogicalChanged} cells changed in the buffer, {st.Update.PixelChanged} in pixels, stale {st.Update.Stale}, unexpected {st.Update.Spurious}");
                if (st.Cursor != null) sb.AppendLine($"  - cursor: expected {st.Cursor.Expected}; {st.Cursor.Outcome} — {st.Cursor.Detail}; varying cells: {string.Join(" ", st.Cursor.VaryingCells)}");
                foreach (var f in st.Failures.Take(8)) sb.AppendLine($"  - FAIL {f}");
                foreach (var f in st.Cells?.Samples.Take(6) ?? []) sb.AppendLine($"    - {f}");
                foreach (var m in st.ManualReview) sb.AppendLine($"  - MANUAL {m}");
            }
        }
        if (_report.NegativeControls.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Negative controls");
            foreach (var n in _report.NegativeControls) sb.AppendLine($"- {n.Name}: {(n.Detected ? "detected" : "NOT DETECTED")} — {n.Mutation}; {n.Observed}");
        }
        return sb.ToString();
    }
}

/// <summary>Steps of one session: calibration, scenes, input, resize, exit.</summary>
internal sealed class SessionRunner
{
    private readonly HostRun _run;
    private readonly HostSession _s;
    private readonly SessionResult _r;
    private Geometry? _g;
    private int[]? _palette;
    private NativeImage? _prevImg;
    private HostScreen? _prevExpected;
    private string _grid = "";

    public SessionRunner(HostRun run, HostSession s, SessionResult r) { _run = run; _s = s; _r = r; }

    private string HostTag => _s.Host;
    private string DriverTag => _s.Driver;

    public void Start()
    {
        _r.Probe = _s.Probe;
        _r.WindowClass = _s.WindowClass;
        if (_s.Host == HostSession.WindowsTerminal)
        {
            string image = _s.WindowProcessImage();
            _r.Log.Add("captured window process: " + image);
            var m = System.Text.RegularExpressions.Regex.Match(image, @"Microsoft\.WindowsTerminal_([0-9.]+)_");
            if (m.Success) { _r.HostVersion = "Microsoft.WindowsTerminal " + m.Groups[1].Value; _run.Report.Hosts[HostSession.WindowsTerminal] = _r.HostVersion; }
        }
        _r.Dpi = _s.Dpi;
        _r.CaptureBoundary = _s.Host == HostSession.Conhost
            ? "client area of the classic console window (text area; scroll bars when the buffer exceeds the window)"
            : "client area of the Windows Terminal window (includes its tab strip and padding; the grid is located by calibration)";
        if (_s.Driver == "terminal")
        {
            // The Terminal driver emits 24-bit SGR with the VGA RGB values, bypassing the host palette.
            _r.Font = _s.Host == HostSession.Conhost
                ? $"{_s.Probe.FontFace} {_s.Probe.FontWidth}x{_s.Probe.FontHeight}px (pinned for this console only: {_s.Probe.FontResult ?? "-"})"
                : "terminal profile defaults: " + (_run.Report.TerminalSettings.GetValueOrDefault("defaults.font") ?? "?");
            _r.PaletteSource = "VGA RGB emitted by the Terminal driver (SGR 38;2 / 48;2)";
            _r.DeclaredPalette = Vga.Rgb.Select(HostAnalysis.Hex).ToArray();
        }
        else if (_s.Host == HostSession.Conhost)
        {
            _r.Font = $"{_s.Probe.FontFace} {_s.Probe.FontWidth}x{_s.Probe.FontHeight}px (pinned for this console only: {_s.Probe.FontResult ?? "-"})";
            _r.PaletteSource = "conhost ColorTable (GetConsoleScreenBufferInfoEx)";
            _r.DeclaredPalette = _s.Probe.ColorTable;
        }
        else
        {
            _r.Font = "terminal profile defaults: " + (_run.Report.TerminalSettings.GetValueOrDefault("defaults.font") ?? "?");
            string scheme = _run.TerminalScheme ?? "";
            _r.PaletteSource = $"Windows Terminal scheme {scheme}";
            if (scheme == "CGA") _r.DeclaredPalette = Vga.Rgb.Select(HostAnalysis.Hex).ToArray();
        }
    }

    // ── session bodies ──────────────────────────────────────────────────────

    public void AppMain(bool exitByMenu)
    {
        if (!Calibrate()) return;
        var desktop = Scene("scene desktop", "desktop", cursorSeries: false, compare: false);
        if (desktop != null) { CheckSelection(desktop); CheckShadow(desktop); }
        Scene("scene cursor", "cursor", cursorSeries: true, compare: false);
        Scene("update", "partial-update", cursorSeries: false, compare: true);
        Scene("scene dialog", "dialog", cursorSeries: false, compare: false);
        Scene("scene desktop", "dialog-closed", cursorSeries: false, compare: true);
        Input("F10", "menu-bar", preferWindowMessages: true);
        Input("ENTER", "menu-file", preferWindowMessages: true);
        Exit("x", "File › Exit by keyboard navigation (F10, Enter, x)", preferWindowMessages: exitByMenu);
    }

    public void AppResize()
    {
        if (!Calibrate()) return;
        var d = Scene("scene desktop", "desktop", cursorSeries: false, compare: false);
        if (d != null) { CheckSelection(d); CheckShadow(d); }
        Scene("scene dialog", "dialog", cursorSeries: false, compare: false);
        Scene("scene desktop", "dialog-closed", cursorSeries: false, compare: true);
        // Host-initiated resize: shrink the host window by about 40 columns and 10 rows.
        if (_g != null && Win32.GetWindowRect(_s.Window, out var wr))
        {
            int w = wr.Right - wr.Left - (int)Math.Round(40 * _g.CellW), h = wr.Bottom - wr.Top - (int)Math.Round(10 * _g.CellH);
            var before = _s.Send("dump").Readback;
            _s.ResizeWindow(w, h);
            Thread.Sleep(1200);
            var after = _s.Send("dump").Readback;
            var step = new StepResult { Scenario = "resize-request", Grid = after != null ? $"{after.Width}x{after.Height}" : "?" };
            step.Check(after != null && before != null && (after.Width != before.Width || after.Height != before.Height),
                $"host grid changed after the window resize ({before?.Width}x{before?.Height} → {after?.Width}x{after?.Height})");
            _r.Steps.Add(step);
            _prevImg = null; _prevExpected = null;
            if (!Calibrate("calibrate-resized")) return;
            var resized = Scene("scene desktop", "resize", cursorSeries: false, compare: false);
            if (resized != null && _g != null)
            {
                var img = WindowCapture.Capture(_s.Window);
                if (img != null)
                {
                    var (ok, detail) = HostAnalysis.OutsideGridUniform(img, _g, (int)(_g.CellW * 0.75));
                    var last = _r.Steps[^1];
                    last.Check(ok, "no stale content outside the resized grid: " + detail);
                }
            }
        }
        Exit("ALT+X", "Alt+X (status line shortcut)", preferWindowMessages: false);
    }

    public void AppSmall()
    {
        if (!Calibrate()) return;
        Scene("scene desktop", "small-clipped", cursorSeries: false, compare: false);
        Scene("scene dialog", "small-dialog", cursorSeries: false, compare: false);
        Exit("ALT+X", "Alt+X (status line shortcut)", preferWindowMessages: false);
    }

    public void Screen(bool full, string[] commander)
    {
        if (!Calibrate()) return;
        var f = Scene("fixture full", "fixture-full", cursorSeries: false, compare: false);
        if (f != null) CheckShadow(f);
        if (full)
        {
            Scene("fixture single-cell", "fixture-single-cell", cursorSeries: false, compare: true);
            Scene("fixture rect", "fixture-rect", cursorSeries: false, compare: true);
            Scene("fixture edge", "fixture-edge", cursorSeries: false, compare: true);
            Scene("fixture cursor-block", "fixture-cursor-block", cursorSeries: true, compare: false);
            Scene("fixture cursor-underline", "fixture-cursor-underline", cursorSeries: true, compare: false);
            Scene("fixture cursor-hidden", "fixture-cursor-hidden", cursorSeries: true, compare: false);
        }
        foreach (string id in commander)
        {
            string? file = _run.CommanderFile(id);
            if (file == null || !File.Exists(file))
            {
                _r.Steps.Add(new StepResult { Scenario = "commander-" + id, Outcome = Outcome.NotRun, Expected = $"approved TVS1 not found: {file}" });
                continue;
            }
            string name = "commander-" + id.Replace("commander-", "").Replace("--", "-").Replace("-80x25", "").Replace("-140x40", "");
            Scene($"tvs1 {file}", name, cursorSeries: false, compare: false);
        }
        var sw = Stopwatch.StartNew();
        var ack = _s.Send("exit");
        var (exited, code, closed, _) = _s.WaitExit(TimeSpan.FromSeconds(10));
        double seconds = sw.Elapsed.TotalSeconds;
        _r.Exit = new ExitResult { Method = "exit command (non-interactive screen session)", Input = "control channel", Exited = exited, Code = code, WindowClosed = closed, Seconds = seconds };
    }

    // ── steps ───────────────────────────────────────────────────────────────

    private string Name(string scenario, string kind) => $"{HostTag}--{DriverTag}--{scenario}--{_grid}.{kind}";

    private bool Calibrate(string scenario = "calibrate")
    {
        var ack = _s.Send("calibrate");
        var step = new StepResult { Scenario = scenario };
        _r.Steps.Add(step);
        HostScreen? e = ack.Logical ?? ack.Readback;
        if (!ack.Ok || e == null) { step.Check(false, $"calibration command failed: {ack.Error}"); return false; }
        _grid = $"{e.Width}x{e.Height}";
        _r.ActualGrid = _grid;
        step.Grid = _grid;
        step.Check($"{e.Width}x{e.Height}" == _r.RequestedGrid || scenario != "calibrate",
            $"host grid {e.Width}x{e.Height} (requested {_r.RequestedGrid})");
        NativeImage? img = _s.CaptureStable();
        if (img == null) { step.Outcome = Outcome.NativeCaptureUnavailable; step.Failures.Add("window capture returned no image"); return false; }
        step.CaptureWidth = img.Width; step.CaptureHeight = img.Height;
        step.Png = Save(img, Name(scenario, "native.png"));
        CalibrationResult cal = HostAnalysis.Calibrate(img, e.Width, e.Height);
        step.Calibration = cal;
        if (cal.Unavailable != null) { step.Outcome = Outcome.NativeCaptureUnavailable; step.Failures.Add(cal.Unavailable); return false; }
        foreach (string c in cal.Checks) { if (c.StartsWith("PASS ")) step.Checks.Add(c); else if (c.StartsWith("FAIL ")) step.Check(false, c[5..]); else step.Checks.Add(c); }
        _g = cal.Geometry;
        if (cal.BackgroundRgb.Length == 16)
        {
            _palette = cal.BackgroundRgb;
            _r.MeasuredPalette = cal.Background;
            if (_r.DeclaredPalette.Length == 16)
            {
                int[] declared = _r.DeclaredPalette.Select(h => Convert.ToInt32(h[1..], 16)).ToArray();
                var off = Enumerable.Range(0, 16).Where(i => HostAnalysis.Dist(declared[i], _palette[i]) > 2).ToList();
                step.Check(off.Count == 0, $"rendered background palette equals the declared host palette ({_r.PaletteSource})" +
                    (off.Count > 0 ? $"; differs at {string.Join(", ", off.Select(i => $"{i:X}: {HostAnalysis.Hex(_palette[i])} vs {_r.DeclaredPalette[i]}"))}" : ""));
            }
        }
        else if (_palette == null) step.Manual("palette swatches did not fit the grid; colour checks use no measured palette");
        WritePixels(step, Name(scenario, "pixels.json"));
        return _g != null && _palette != null;
    }

    private HostScreen? Scene(string cmd, string scenario, bool cursorSeries, bool compare)
    {
        var ack = _s.Send(cmd);
        var step = new StepResult { Scenario = scenario, Grid = _grid };
        _r.Steps.Add(step);
        HostScreen? e = ack.Logical ?? ack.Readback;
        if (!ack.Ok || e == null) { step.Check(false, $"command '{cmd}' failed: {ack.Error}"); return null; }
        step.Expected = ack.Logical != null ? "fixture logical screen (Layer A); host buffer readback cross-checked" : "host console buffer readback";
        if (ack.Logical != null && ack.Readback != null)
        {
            bool same = ack.Logical.Chars == ack.Readback.Chars && ack.Logical.Attrs.AsSpan().SequenceEqual(ack.Readback.Attrs);
            if (same) step.Checks.Add("PASS host buffer readback equals the logical screen");
            else step.Checks.Add("NOTE host buffer readback differs from the logical screen (expected for VT-interpreted buffers); pixels are checked against the logical screen");
        }
        if (!ack.Settled) step.Checks.Add("NOTE host buffer did not settle within 3s");
        return Analyse(step, e, cursorSeries, compare);
    }

    private HostScreen? Input(string keys, string scenario, bool preferWindowMessages)
    {
        var step = new StepResult { Scenario = scenario, Grid = _grid };
        _r.Steps.Add(step);
        try { step.Input = $"{keys} — {_s.SendKeys(keys, preferWindowMessages)}"; }
        catch (Exception ex) { step.Check(false, ex.Message); return null; }
        Thread.Sleep(500);
        var ack = _s.Send("dump");
        if (!ack.Ok || ack.Readback == null) { step.Check(false, $"dump failed: {ack.Error}"); return null; }
        step.Expected = "host console buffer readback";
        step.Check(_prevExpected == null || !ack.Readback.SameCells(_prevExpected), "the input changed the host buffer (keys reached the application)");
        return Analyse(step, ack.Readback, cursorSeries: false, compare: true);
    }

    private void Exit(string keys, string method, bool preferWindowMessages)
    {
        var ex = new ExitResult { Method = method };
        var sw = Stopwatch.StartNew();
        try { ex.Input = _s.SendKeys(keys, preferWindowMessages); }
        catch (Exception e) { ex.Input = "FAILED: " + e.Message; }
        var (exited, code, closed, _) = _s.WaitExit(TimeSpan.FromSeconds(10));
        ex.Exited = exited; ex.Code = code; ex.WindowClosed = closed; ex.Seconds = sw.Elapsed.TotalSeconds;
        _r.Exit = ex;
    }

    private HostScreen? Analyse(StepResult step, HostScreen e, bool cursorSeries, bool compare)
    {
        if (_g == null || _palette == null) { step.Check(false, "no calibrated geometry"); return e; }
        if (e.Width != _g.Cols || e.Height != _g.Rows) { step.Check(false, $"host grid {e.Width}x{e.Height} differs from the calibrated {_g.Cols}x{_g.Rows}"); return e; }
        var exempt = new HashSet<(int, int)>();
        if (e.CursorVisible && e.CursorX >= 0 && e.CursorY >= 0 && e.CursorX < e.Width && e.CursorY < e.Height) exempt.Add((e.CursorX, e.CursorY));
        if (_prevExpected is { CursorVisible: true } pe) exempt.Add((pe.CursorX, pe.CursorY));
        var g = _g;
        NativeImage? img = _s.CaptureStable((a, b) => !Enumerable.Range(0, g.Rows).Any(r => Enumerable.Range(0, g.Cols)
            .Any(c => !exempt.Contains((c, r)) && HostAnalysis.CellDiffers(a, b, g.Cell(c, r)))));
        if (img == null) { step.Outcome = Outcome.NativeCaptureUnavailable; step.Failures.Add("window capture returned no image"); return e; }
        step.CaptureWidth = img.Width; step.CaptureHeight = img.Height;
        step.Png = Save(img, Name(step.Scenario, "native.png"));
        step.Check(img.Width >= g.Right && img.Height >= g.Bottom, $"capture {img.Width}x{img.Height} contains the grid ({g.Right}x{g.Bottom})");

        var cells = HostAnalysis.CheckCells(img, g, _palette, e, exempt);
        step.Cells = cells;
        step.Check(cells.Passed, $"{cells.CellsChecked - cells.CellsFailed}/{cells.CellsChecked} cells render the expected background, glyph ink, box edges and colours");
        foreach (var o in cells.Observations) step.Checks.Add("NOTE " + o);
        if (cells.Diacritics.Pairs > 0)
            step.Checks.Add($"{(cells.Diacritics.Failed == 0 ? "PASS" : "FAIL")} Czech diacritics: {cells.Diacritics.Pairs - cells.Diacritics.Failed}/{cells.Diacritics.Pairs} accented letters have ink above their base letter");
        step.Manual("exact glyph shapes (font identity, hinting) are not compared automatically; review the screenshot");

        if (compare && _prevImg != null && _prevExpected != null && _prevImg.Width == img.Width && _prevImg.Height == img.Height
            && _prevExpected.Width == e.Width && _prevExpected.Height == e.Height)
        {
            var u = HostAnalysis.CompareUpdate(_prevImg, img, g, _palette, _prevExpected, e, exempt);
            step.Update = u;
            step.Check(u.Passed, $"redraw: {u.PixelChanged} cells changed in pixels for {u.LogicalChanged} changed in the buffer (stale {u.Stale}, unexpected {u.Spurious}, neighbour overhang {u.NeighbourOverhang})");
            if (u.NeighbourOverhang > 0) step.Checks.Add($"NOTE {u.NeighbourOverhang} unchanged cells next to changed ones changed in pixels: glyph ink of the neighbour overhangs the cell boundary in this host's font");
        }
        if (cursorSeries)
        {
            var series = _s.CaptureSeries(8, 200);
            bool foreground = Win32.GetForegroundWindow() == _s.Window;
            var cc = HostAnalysis.CheckCursor(series, g, _palette, e, _prevImg, _prevExpected);
            cc.Detail += foreground ? " (window had keyboard focus)" : " (window did not have keyboard focus)";
            step.Cursor = cc;
            if (cc.Outcome == Outcome.ManualReviewRequired) step.Manual("cursor: " + cc.Detail);
            else step.Check(cc.Outcome == Outcome.Passed, $"cursor {cc.Expected}: {cc.Detail}");
        }

        if (_r.Id.Contains("console-app-80x25") && step.Scenario == "desktop" && _run.Report.NegativeControls.All(n => !n.Name.StartsWith(_r.Host)))
            NegativeControls(img, e, exempt);

        WritePixels(step, Name(step.Scenario, "pixels.json"));
        _prevImg = img; _prevExpected = e;
        return e;
    }

    private void CheckSelection(HostScreen e)
    {
        var step = _r.Steps[^1];
        int? Find(string text) { for (int r = 0; r < e.Height; r++) { int i = e.Chars.Substring(r * e.Width, e.Width).IndexOf(text, StringComparison.Ordinal); if (i >= 0) return r * e.Width + i; } return null; }
        int? sel = Find("Item 2 selected"), nor = Find("Item 1 normal");
        if (sel == null || nor == null || _g == null || _prevImg == null) { step.Manual("selected list row not found in the host buffer"); return; }
        int sc = sel.Value % e.Width, sr = sel.Value / e.Width, nc = nor.Value % e.Width, nr = nor.Value / e.Width;
        bool distinctAttr = e.Attrs[sel.Value] >> 4 != e.Attrs[nor.Value] >> 4;
        int ms = HostAnalysis.MeasuredBackground(_prevImg, _g, sc + 6, sr), mn = HostAnalysis.MeasuredBackground(_prevImg, _g, nc + 6, nr);
        step.Check(distinctAttr, $"selected row has its own background attribute in the host buffer ({e.Attrs[sel.Value]:X2} vs {e.Attrs[nor.Value]:X2})");
        step.Check(HostAnalysis.Dist(ms, mn) > 40, $"selected row is visibly distinct in pixels ({HostAnalysis.Hex(ms)} vs {HostAnalysis.Hex(mn)})");
    }

    private void CheckShadow(HostScreen e)
    {
        var step = _r.Steps[^1];
        if (_g == null || _palette == null || _prevImg == null) return;
        // The bottom-right corner of the topmost double frame; its shadow is one row down, two columns right.
        for (int r = e.Height - 2; r >= 0; r--)
            for (int c = e.Width - 3; c >= 0; c--)
            {
                if (e.Char(c, r) != '╝' || e.Attr(c + 1, r + 1) != 0x08) continue;
                int frameBg = HostAnalysis.MeasuredBackground(_prevImg, _g, c - 1, r);
                int shadowRight = HostAnalysis.MeasuredBackground(_prevImg, _g, c + 1, r);
                int shadowBelow = HostAnalysis.MeasuredBackground(_prevImg, _g, c, r + 1);
                bool rightIsShadow = e.Attr(c + 1, r) == 0x08 && HostAnalysis.Dist(shadowRight, _palette[0]) <= HostAnalysis.BgTolerance;
                bool belowIsShadow = HostAnalysis.Dist(shadowBelow, _palette[0]) <= HostAnalysis.BgTolerance;
                step.Check(rightIsShadow && belowIsShadow && HostAnalysis.Dist(frameBg, _palette[0]) > HostAnalysis.BgTolerance,
                    $"shadow right of and below the frame corner ({c},{r}) renders black ({HostAnalysis.Hex(shadowRight)}, {HostAnalysis.Hex(shadowBelow)}) beside the frame ({HostAnalysis.Hex(frameBg)})");
                int shadowCells = Enumerable.Range(0, e.Width * e.Height).Count(i => e.Attrs[i] == 0x08);
                int shadowFails = step.Cells?.Rules.Where(k => k.Key.StartsWith("shadow:")).Sum(k => k.Value.Failed) ?? 0;
                step.Check(shadowFails == 0, $"{shadowCells} shadow cells (attribute 08) render black background and dark-grey text ({shadowFails} failed)");
                return;
            }
        step.Checks.Add("NOTE no framed window with a shadow found in this screen");
    }

    private void NegativeControls(NativeImage img, HostScreen e, HashSet<(int, int)> exempt)
    {
        void Add(string name, string mutation, bool detected, string observed) =>
            _run.Report.NegativeControls.Add(new NegativeControlResult { Name = $"{_r.Host}: {name}", Mutation = mutation, Detected = detected, Observed = observed });

        var wrongBg = Clone(e);
        for (int r = 3; r < 6; r++) for (int c = 4; c < 10; c++) wrongBg.Attrs[r * e.Width + c] = (byte)((wrongBg.Attrs[r * e.Width + c] & 0x0F) | 0x40);
        var c1 = HostAnalysis.CheckCells(img, _g!, _palette!, wrongBg, exempt);
        Add("background colour", "expected background of a 6x3 block changed to red", c1.CellsFailed >= 12, $"{c1.CellsFailed} cells failed");

        var shifted = Clone(e);
        char[] chars = shifted.Chars.ToCharArray();
        int row = Enumerable.Range(0, e.Height).First(r => e.Chars.Substring(r * e.Width, e.Width).Contains("Plain ASCII"));
        for (int c = e.Width - 1; c > 0; c--) chars[row * e.Width + c] = e.Chars[row * e.Width + c - 1];
        shifted.Chars = new string(chars);
        var c2 = HostAnalysis.CheckCells(img, _g!, _palette!, shifted, exempt);
        Add("text shifted one column", "expected text row 'Plain ASCII…' shifted right by one cell", c2.CellsFailed >= 3, $"{c2.CellsFailed} cells failed");

        var half = _g! with { X0 = _g.X0 + _g.CellW / 2, Y0 = _g.Y0 + _g.CellH / 2 };
        var c3 = HostAnalysis.CheckCells(img, half, _palette!, e, exempt);
        Add("grid offset", "grid origin moved by half a cell", c3.CellsFailed >= 50, $"{c3.CellsFailed} cells failed");

        var blank = new NativeImage(img.Width, img.Height);
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++) blank.Set(x, y, 0x0C0C0C);
        var cal = HostAnalysis.Calibrate(blank, e.Width, e.Height);
        Add("blank capture", "uniform black image instead of the window", cal.Unavailable != null, cal.Unavailable ?? "calibrated");

        var glyphless = Clone(e);
        char[] g2 = glyphless.Chars.ToCharArray();
        for (int i = 0; i < g2.Length; i++) if (g2[i] != ' ' && !HostAnalysis.IsBlank(g2[i])) { g2[i] = ' '; }
        glyphless.Chars = new string(g2);
        var c4 = HostAnalysis.CheckCells(img, _g!, _palette!, glyphless, exempt);
        Add("missing text", "expected screen with every glyph replaced by a space", c4.CellsFailed >= 100, $"{c4.CellsFailed} cells failed");
    }

    private static HostScreen Clone(HostScreen e) => new()
    {
        Source = e.Source, Width = e.Width, Height = e.Height, Chars = e.Chars, Attrs = (byte[])e.Attrs.Clone(),
        CursorX = e.CursorX, CursorY = e.CursorY, CursorVisible = e.CursorVisible, CursorSize = e.CursorSize,
    };

    private string Save(NativeImage img, string name)
    {
        File.WriteAllBytes(_run.Artifact(name), img.EncodePng());
        return name;
    }

    private void WritePixels(StepResult step, string name)
    {
        step.Pixels = name;
        Json.Write(_run.Artifact(name), new
        {
            Session = _r.Id, _r.Host, _r.HostVersion, _r.Driver, _r.Boundary, step.Scenario, step.Grid, Dpi = _r.Dpi,
            _r.Font, _r.PaletteSource, Capture = new { Mechanism = _r.CaptureMechanism, _r.CaptureBoundary, Width = step.CaptureWidth, Height = step.CaptureHeight, Png = step.Png },
            Geometry = _g, step.Outcome, step.Expected, step.Input, step.Checks, step.Failures, step.ManualReview,
            step.Calibration, step.Cells, step.Update, step.Cursor, TimestampUtc = DateTime.UtcNow,
        });
    }
}
