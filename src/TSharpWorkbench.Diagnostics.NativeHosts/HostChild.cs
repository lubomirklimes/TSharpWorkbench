using TSharpVision.Constants;
using TSharpWorkbench.Diagnostics.DriverOutput;
using TSharpVision.Drivers;
using TSharpVision.Drivers.Console;
using TSharpVision.Drivers.Terminal;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Diagnostics.NativeHosts;

/// <summary>
/// The process that runs inside a real console host. Two modes:
/// <list type="bullet">
/// <item><c>app</c>: a real TSharpVision application on the Win32 Console driver, run through
/// <see cref="AppLifecycleGuard"/>; the parent switches scenes through a file control channel and drives
/// menus/exit with real host input.</item>
/// <item><c>screen</c>: fixture screens written through a driver's production output path (Console:
/// WriteConsoleOutputW; Terminal: its VT stream attached to this console's stdout).</item>
/// </list>
/// Control: the parent writes <c>cmd-NNNN.txt</c>; the child answers <c>ack-NNNN.json</c> with the
/// host-buffer readback after the buffer settles. <c>ready.json</c> carries the host facts.
/// </summary>
internal static class HostChild
{
    public const string ReadyName = "ready.json", ExitName = "exit.json";

    public static int Run(string mode, string dir, Dictionary<string, string> opts)
    {
        Directory.CreateDirectory(dir);
        var probe = new ConsoleProbe { Mode = mode, Driver = opts.GetValueOrDefault("--driver", "console") };
        try
        {
            if (opts.ContainsKey("--grid") || opts.ContainsKey("--font"))
                ConsoleSide.Configure(probe, opts.GetValueOrDefault("--grid"), opts.GetValueOrDefault("--font"));
            Thread.Sleep(150);
            ConsoleSide.Describe(probe);
            int code = mode switch
            {
                "app" => RunApp(dir, probe),
                "screen" => RunScreen(dir, probe),
                _ => throw new ArgumentException($"unknown child mode {mode}"),
            };
            Json.Write(Path.Combine(dir, ExitName), new { Code = code });
            return code;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "child-error.txt"), ex.ToString());
            Json.Write(Path.Combine(dir, ExitName), new { Code = 2, Error = ex.Message });
            return 2;
        }
    }

    // ── app mode ────────────────────────────────────────────────────────────

    private static int RunApp(string dir, ConsoleProbe probe)
    {
        var driver = new Win32ConsoleDriver();
        driver.Initialize();
        TDisplay.driver = driver;
        StreamableRegistration.RegisterAll();
        ScreenDriverFactory.WindowTitle = "Host app";
        var app = new HostDemoApp();
        var channel = new ControlChannel(dir);
        bool running = true;
        var control = new Thread(() =>
        {
            HostScreen? last = null;
            while (Volatile.Read(ref running))
            {
                if (!channel.TryNext(out int seq, out string cmd)) { Thread.Sleep(40); continue; }
                var ack = new ControlAck { Seq = seq, Command = cmd };
                try
                {
                    bool ui = cmd != "dump";
                    if (ui && !app.Dispatch(cmd, TimeSpan.FromSeconds(5))) throw new TimeoutException("UI thread did not handle the command");
                    (ack.Readback, ack.Settled) = ConsoleSide.Settle(last, expectChange: false, TimeSpan.FromSeconds(3));
                    ack.Ok = ack.Readback != null;
                    last = ack.Readback;
                }
                catch (Exception ex) { ack.Error = ex.Message; }
                channel.Ack(ack);
            }
        }) { IsBackground = true, Name = "Control" };
        Json.Write(Path.Combine(dir, ReadyName), probe);
        control.Start();
        int code = AppLifecycleGuard.Run(app);
        Volatile.Write(ref running, false);
        return code;
    }

    // ── screen mode ─────────────────────────────────────────────────────────

    private static int RunScreen(string dir, ConsoleProbe probe)
    {
        IDriver driver;
        GridSize grid;
        if (probe.Driver == "terminal")
        {
            // No Windows TTY attach exists in the Terminal driver: attach its production output to stdout.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            ConsoleSide.EnableVirtualTerminalOutput();
            grid = new GridSize(probe.WindowColumns, probe.WindowRows);
            var t = new AnsiTerminalDriver();
            t.AttachOutputForTesting((ushort)grid.Width, (ushort)grid.Height);
            Console.Write("\x1b[2J");
            driver = t;
        }
        else
        {
            var c = new Win32ConsoleDriver();
            c.Initialize();
            grid = new GridSize(c.GetCols(), c.GetRows());
            driver = c;
        }
        var logical = new LogicalScreen(grid, new TScreenChar(' ', 0x07));
        var caret = new CaretRequest(0, 0, 0);
        driver.SetCursorType(0);
        var channel = new ControlChannel(dir);
        Json.Write(Path.Combine(dir, ReadyName), probe);
        HostScreen? last = null;
        IReadOnlyList<FixtureStep> steps = Fixtures.Steps(Fixtures.Screen, grid);

        while (true)
        {
            if (!channel.TryNext(out int seq, out string cmd)) { Thread.Sleep(40); continue; }
            var ack = new ControlAck { Seq = seq, Command = cmd };
            bool exit = false;
            try
            {
                string[] parts = cmd.Split(' ', 2);
                switch (parts[0])
                {
                    case "calibrate":
                        Write(driver, logical, Calibration.Canvas(grid).Rows());
                        caret = new CaretRequest(0, 0, 0);
                        break;
                    case "fixture":
                        FixtureStep step = steps.First(s => s.Name == parts[1]);
                        Write(driver, logical, step.Writes);
                        caret = step.Caret;
                        break;
                    case "tvs1":
                        ScreenSnapshot s = ScreenSnapshot.Deserialize(File.ReadAllBytes(parts[1]));
                        if (s.Width != grid.Width || s.Height != grid.Height)
                            throw new InvalidOperationException($"TVS1 is {s.Width}x{s.Height}, host grid is {grid}");
                        var rows = Enumerable.Range(0, s.Height)
                            .Select(y => new CellWrite(0, y, Enumerable.Range(0, s.Width).Select(x => s[x, y]).ToArray())).ToList();
                        Write(driver, logical, rows);
                        caret = new CaretRequest(0, 0, 0);
                        break;
                    case "dump":
                        break;
                    case "exit":
                        exit = true;
                        break;
                    default:
                        throw new ArgumentException($"unknown screen command {cmd}");
                }
                if (!exit)
                {
                    if (caret.CursorType == 0) driver.SetCursorType(0);
                    else { driver.SetCaretPosition(caret.X, caret.Y); driver.SetCursorType(caret.CursorType); }
                }
                (ack.Readback, ack.Settled) = ConsoleSide.Settle(last, expectChange: false, TimeSpan.FromSeconds(3));
                last = ack.Readback;
                ack.Logical = new HostScreen
                {
                    Source = "logical",
                    Width = grid.Width, Height = grid.Height,
                    Chars = new string(logical.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray()),
                    Attrs = logical.Cells.Select(c => (byte)c.Attr).ToArray(),
                    CursorX = caret.X, CursorY = caret.Y, CursorVisible = caret.CursorType != 0, CursorSize = caret.CursorType,
                };
                ack.Ok = true;
            }
            catch (Exception ex) { ack.Error = ex.Message; }
            if (exit)
            {
                if (driver is AnsiTerminalDriver t) t.DetachOutputForTesting(); else driver.Shutdown();
                channel.Ack(ack);
                return 0;
            }
            channel.Ack(ack);
        }
    }

    private static void Write(IDriver driver, LogicalScreen logical, IEnumerable<CellWrite> writes)
    {
        foreach (CellWrite w in writes)
        {
            logical.Apply(w);
            // Clip as the framework does before it reaches a driver.
            int x0 = Math.Max(0, w.X), x1 = Math.Min(logical.Grid.Width, w.X + w.Cells.Length);
            if (w.Y < 0 || w.Y >= logical.Grid.Height || x1 <= x0) continue;
            driver.WriteBuf(x0, w.Y, x1 - x0, 1, w.Cells.AsSpan(x0 - w.X, x1 - x0));
        }
    }
}

/// <summary>The calibration screen: two corner cells locate the grid, rows 1–2 sample all 16 colours.</summary>
internal static class Calibration
{
    public const byte Marker = 0xD0;     // bright magenta background
    public const int SwatchX = 2;

    public static Canvas Canvas(GridSize grid)
    {
        var c = new Canvas(grid, ' ', 0x00);
        c.Put(0, 0, ' ', Marker);
        c.Put(grid.Width - 1, grid.Height - 1, ' ', Marker);
        if (grid.Width >= SwatchX + 16 && grid.Height >= 4)
            for (int i = 0; i < 16; i++)
            {
                c.Put(SwatchX + i, 1, ' ', (byte)(i << 4));
                c.Put(SwatchX + i, 2, '█', (byte)i);
            }
        return c;
    }
}

/// <summary>File-based command channel (parent writes commands, child writes acknowledgements).</summary>
internal sealed class ControlChannel(string dir)
{
    private int _next = 1;

    public bool TryNext(out int seq, out string cmd)
    {
        seq = _next;
        string file = Path.Combine(dir, $"cmd-{seq:D4}.txt");
        cmd = "";
        if (!File.Exists(file)) return false;
        try { cmd = File.ReadAllText(file).Trim(); }
        catch (IOException) { return false; }
        _next++;
        return true;
    }

    public void Ack(ControlAck ack) => Json.Write(Path.Combine(dir, $"ack-{ack.Seq:D4}.json"), ack);
}

/// <summary>
/// The application shown in the hosts: menu bar with hotkeys, status line, an inactive window (single
/// frame), the active window (double frame, shadow) with ASCII, Czech, box/shade text, a list with a
/// selected row and an input line (cursor), and an optional modeless dialog.
/// </summary>
internal sealed class HostDemoApp : TApplication
{
    private const ushort cmControl = 0x7e40;
    private readonly object _gate = new();
    private string? _pending;
    private readonly ManualResetEventSlim _handled = new(false);
    private TWindow? _main;
    private TListBox? _list;
    private TInputLine? _input;
    private SettableText? _counter;
    private TDialog? _dialog;
    private CalibrationView? _calibration;
    private int _count;

    public HostDemoApp()
    {
        if (DeskTop == null) return;
        int w = DeskTop.size.x, h = DeskTop.size.y;
        var inactive = new TWindow(new TRect(Math.Max(0, w - 34), 1, w, Math.Min(h, 9)), "Inactive", 2);
        inactive.Insert(new TStaticText(new TRect(2, 2, 30, 3), "single frame when inactive"));
        DeskTop.Insert(inactive);

        _main = new TWindow(new TRect(1, 1, Math.Min(w - 2, 56), Math.Min(h - 1, 18)), "Žluťoučký kůň", 1);
        string[] lines =
        [
            "Plain ASCII 0123456789 ~!@#$%^&*()",
            Fixtures.Czech,
            Fixtures.CzechUpper,
            "ACDEINORSTUYZ acdeinorstuyz",
            Fixtures.SingleBox + " " + Fixtures.Shades,
        ];
        for (int i = 0; i < lines.Length; i++) _main.Insert(new TStaticText(new TRect(2, 1 + i, 52, 2 + i), lines[i]));
        _counter = new SettableText(new TRect(2, 6, 30, 7), "Counter: 0");
        _main.Insert(_counter);
        _list = new TListBox(new TRect(2, 8, 30, 12), 1, null);
        var items = new TStringCollection();
        items.Items.AddRange(["Item 1 normal", "Item 2 selected", "Item 3 normal", "Item 4 normal"]);
        _list.NewList(items);
        _main.Insert(_list);
        _input = new TInputLine(new TRect(2, 13, 30, 14), 40) { Data = "Kůň úpěl" };
        _main.Insert(_input);
        DeskTop.Insert(_main);
        _list.FocusItem(1);
        _list.Select();
    }

    public override TMenuBar InitMenuBar(TRect r)
    {
        r.b.y = r.a.y + 1;
        return new TMenuBar(r,
            new TSubMenu("~F~ile", Keys.kbAltF) +
                new TMenuItem("~N~ew", Views.cmNew, Keys.kbNoKey) +
                new TMenuItem("~O~pen...", Views.cmOpen, Keys.kbF3, 0, "F3") +
                new TMenuItem("E~x~it", Views.cmQuit, Keys.kbAltX, 0, "Alt+X") +
            new TSubMenu("~E~dit", Keys.kbAltE) +
                new TMenuItem("~C~opy", Views.cmCopy, Keys.kbNoKey) +
            new TSubMenu("~W~indow", Keys.kbAltW) +
                new TMenuItem("~N~ext", Views.cmNext, Keys.kbF6, 0, "F6"));
    }

    public override TStatusLine InitStatusLine(TRect r)
    {
        r.a.y = r.b.y - 1;
        return new TStatusLine(r, new TStatusDef(0, 0xFFFF) +
            new TStatusItem("~Alt+X~ Exit", Keys.kbAltX, Views.cmQuit) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu));
    }

    /// <summary>Runs a scene command on the UI thread (called from the control thread).</summary>
    public bool Dispatch(string cmd, TimeSpan timeout)
    {
        lock (_gate) { _pending = cmd; _handled.Reset(); }
        var ev = new TEvent { What = Events.evCommand };
        ev.message.command = cmControl;
        TEventQueue.Enqueue(ev);
        return _handled.Wait(timeout);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && ev.message.command == cmControl)
        {
            ClearEvent(ref ev);
            string? cmd;
            lock (_gate) { cmd = _pending; _pending = null; }
            try { if (cmd != null) Apply(cmd); }
            finally { _handled.Set(); }
            return;
        }
        base.HandleEvent(ref ev);
    }

    private void Apply(string cmd)
    {
        RemoveCalibration();
        switch (cmd)
        {
            case "calibrate":
                _calibration = new CalibrationView(new TRect(0, 0, size.x, size.y));
                Insert(_calibration);
                break;
            case "scene desktop":
                if (_dialog != null) { DeskTop?.Remove(_dialog); _dialog = null; }
                _main?.Select();
                _list?.Select();
                Redraw();
                break;
            case "scene cursor":
                _main?.Select();
                _input?.Select();
                Redraw();
                break;
            case "scene dialog":
                if (_dialog == null && DeskTop != null)
                {
                    int w = DeskTop.size.x, h = DeskTop.size.y;
                    int dw = Math.Min(44, w - 2), dh = Math.Min(10, h - 2);
                    _dialog = new TDialog(new TRect((w - dw) / 2, Math.Max(0, (h - dh) / 2), (w - dw) / 2 + dw, Math.Max(0, (h - dh) / 2) + dh), "Dialog — Příliš");
                    _dialog.Insert(new TStaticText(new TRect(2, 2, dw - 2, 3), "Dialog with frame and shadow"));
                    _dialog.Insert(new TButton(new TRect(4, dh - 3, 16, dh - 1), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
                    _dialog.Insert(new TButton(new TRect(18, dh - 3, 30, dh - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfNormal));
                    DeskTop.Insert(_dialog);
                }
                break;
            case "update":
                _counter?.Set($"Counter: {++_count}");
                break;
            case "quit":
                Post(Views.cmQuit);
                break;
            default:
                throw new ArgumentException($"unknown app command {cmd}");
        }
    }

    private void RemoveCalibration()
    {
        if (_calibration == null) return;
        Remove(_calibration);
        _calibration = null;
        Redraw();
    }

    private sealed class SettableText(TRect bounds, string text) : TStaticText(bounds, text)
    {
        internal void Set(string text) { Text = text; DrawView(); }
    }

    /// <summary>Full-screen calibration overlay (same cells as <see cref="Calibration.Canvas"/>).</summary>
    private sealed class CalibrationView : TView
    {
        public CalibrationView(TRect bounds) : base(bounds) { growMode = Views.gfGrowHiX | Views.gfGrowHiY; }

        public override void Draw()
        {
            var canvas = Calibration.Canvas(new GridSize(size.x, size.y));
            foreach (CellWrite row in canvas.Rows()) WriteBuf(0, row.Y, size.x, 1, row.Cells.AsSpan());
        }
    }
}
