using System.Text;
using TSharpVision.Drivers.Terminal;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>
/// ANSI/VT terminal output. The real <see cref="AnsiTerminalDriver"/> output methods write their
/// sequences to <see cref="Console.Out"/>, which is redirected into a UTF-8 byte stream; the
/// bytes are interpreted by the independent <see cref="VtOracle"/>. The driver is attached at a
/// fixed size without touching a TTY (raw mode, keyboard negotiation and SIGWINCH-less size
/// polling are not part of this path; the Linux PTY smoke covers a real TTY).
/// </summary>
public sealed class TerminalOutputPath : IOutputPath
{
    private readonly TextWriter _originalOut = Console.Out;
    private MemoryStream? _stream;
    private long _consumed;
    private StreamWriter? _writer;
    private AnsiTerminalDriver? _driver;
    private VtOracle? _oracle;

    public string Id => "terminal-ansi";
    public string Driver => nameof(AnsiTerminalDriver);
    public string Boundary => "UTF-8 bytes written to Console.Out (the driver's real output stream), interpreted by an independent VT oracle";
    public PathCapabilities Capabilities { get; } = new(CaretModel.Native, CursorShape: false, ForegroundOfBlankCells: true,
        "24-bit SGR (38;2/48;2) from the VGA palette only, no 16-colour fallback; cursor shape not conveyed (DECTCEM show/hide only)");
    public string? Unavailable => null;

    /// <summary>The raw bytes of the last step, for byte-level assertions in tests.</summary>
    public byte[] LastStepBytes { get; private set; } = [];

    public VtOracle Oracle => _oracle ?? throw new InvalidOperationException("Begin was not called.");

    public void Begin(GridSize grid)
    {
        Detach();
        _stream = new MemoryStream();
        _consumed = 0;
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _oracle = new VtOracle(grid.Width, grid.Height);
        _driver = new AnsiTerminalDriver();
        _driver.AttachOutputForTesting((ushort)grid.Width, (ushort)grid.Height);
    }

    public void Apply(FixtureStep step, StepCapture capture)
    {
        var driver = _driver ?? throw new InvalidOperationException("Begin was not called.");
        // Console.Out is redirected only while the driver runs, so nothing else lands in the capture.
        Console.SetOut(_writer!);
        try { ApplyRedirected(driver, step, capture); }
        finally { Console.SetOut(_originalOut); }
    }

    private void ApplyRedirected(AnsiTerminalDriver driver, FixtureStep step, StepCapture capture)
    {
        if (step.ResizeTo is { } target)
        {
            // What PollResize does after TIOCGWINSZ reports a new size; the terminal reflows first.
            Oracle.Resize(target.Width, target.Height);
            driver.AttachOutputForTesting((ushort)target.Width, (ushort)target.Height);
            ScreenBuffer buffer = driver.AllocateScreenBuffer();
            capture.Check(buffer.Width == target.Width && buffer.Height == target.Height, $"driver allocates a {target} screen buffer");
            capture.Record(new CaptureOp("TerminalResize", W: target.Width, H: target.Height));
        }
        foreach (CellWrite w in step.Writes) driver.WriteBuf(w.X, w.Y, w.Cells.Length, 1, w.Cells);
        driver.SetCaretPosition(step.Caret.X, step.Caret.Y);
        driver.SetCursorType(step.Caret.CursorType);
    }

    public void Observe(StepCapture capture)
    {
        var stream = _stream ?? throw new InvalidOperationException("Begin was not called.");
        _writer!.Flush();
        byte[] all = stream.ToArray();
        LastStepBytes = all[(int)_consumed..];
        _consumed = all.Length;

        VtOracle oracle = Oracle;
        int diagnosticsBefore = oracle.Diagnostics.Count;
        oracle.Tokens.Clear();
        oracle.TokenCounts.Clear();
        oracle.Feed(LastStepBytes);
        oracle.Settle();

        foreach (CaptureOp op in oracle.Tokens) capture.Record(op);
        foreach (var (kind, count) in oracle.TokenCounts) capture.OperationCounts[kind] = count;
        capture.OperationsTotal = oracle.Tokens.Count;
        capture.Diagnostics.Add($"{LastStepBytes.Length} bytes emitted");
        capture.Diagnostics.AddRange(oracle.Diagnostics.Skip(diagnosticsBefore));
        capture.Check(oracle.Diagnostics.Count == diagnosticsBefore, "every emitted sequence understood by the oracle");
        capture.Cells = oracle.SemanticCells();
        capture.Cursor = new CursorState(oracle.CursorX, oracle.CursorY, oracle.CursorVisible, "not-conveyed");
    }

    private void Detach()
    {
        _driver?.DetachOutputForTesting();
        _driver = null;
        Console.SetOut(_originalOut);
        _writer?.Dispose();
        _writer = null;
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose() => Detach();
}
