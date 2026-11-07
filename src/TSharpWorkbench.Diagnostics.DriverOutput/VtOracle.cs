using System.Text;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>
/// Test oracle: a small, independent interpreter of the VT/xterm sequences a terminal driver may
/// emit. It does not share code with the production encoder. It reconstructs the cell state a
/// conforming terminal would show (text, 24-bit or 16-colour foreground/background, caret) and
/// reports every sequence it does not understand as a diagnostic instead of dropping it.
/// </summary>
/// <remarks>
/// Supported: UTF-8 text with deferred (pending) auto-wrap; CUP/HVP; ED 2; SGR 0, 30–37, 39, 40–47,
/// 49, 90–97, 100–107, 38;2 and 48;2; DECSET/DECRST 25, 1049 (alternate screen) and the mouse
/// modes 1000/1002/1003/1006 (recorded, no visible effect); kitty keyboard queries/push/pop
/// (<c>CSI ? u</c>, <c>CSI &gt; n u</c>, <c>CSI &lt; u</c>) and primary DA (<c>CSI c</c>) as
/// recorded no-ops; OSC terminated by BEL or ST; BEL. Anything else is a diagnostic.
/// </remarks>
public sealed class VtOracle
{
    private struct Cell { public string Text; public int Fg; public int Bg; }

    private Cell[] _main, _alt;
    private bool _onAlt;
    private int _fg = -1, _bg = -1;
    private bool _wrapPending;
    private readonly List<byte> _pendingUtf8 = new();
    private readonly StringBuilder _sequence = new();
    private State _state = State.Ground;
    private Cell[]? _lastAltSnapshot;

    private enum State { Ground, Escape, Csi, Osc, OscEscape }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool OnAlternateScreen => _onAlt;
    public int BellCount { get; private set; }
    public long BytesFed { get; private set; }
    public List<string> Diagnostics { get; } = new();
    public List<CaptureOp> Tokens { get; } = new();
    public int TokenTotal { get; private set; }
    public Dictionary<string, int> TokenCounts { get; } = new();
    public HashSet<int> MouseModes { get; } = new();

    public const int MaxDiagnostics = 200, MaxTokens = 400;

    public VtOracle(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid terminal size {width}x{height}.");
        Width = width; Height = height;
        _main = Blank(width * height);
        _alt = Blank(width * height);
    }

    private static Cell[] Blank(int n)
    {
        var cells = new Cell[n];
        Array.Fill(cells, new Cell { Text = " ", Fg = -1, Bg = -1 });
        return cells;
    }

    private Cell[] Screen => _onAlt ? _alt : _main;

    /// <summary>Resizes like xterm: the top-left content is kept, new cells are blank, the caret is clamped.</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid terminal size {width}x{height}.");
        _main = Copy(_main, width, height);
        _alt = Copy(_alt, width, height);
        Width = width; Height = height;
        CursorX = Math.Min(CursorX, width - 1);
        CursorY = Math.Min(CursorY, height - 1);
        _wrapPending = false;
    }

    private Cell[] Copy(Cell[] src, int width, int height)
    {
        var dst = Blank(width * height);
        for (int y = 0; y < Math.Min(Height, height); y++)
            for (int x = 0; x < Math.Min(Width, width); x++)
                dst[y * width + x] = src[y * Width + x];
        return dst;
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        BytesFed += bytes.Length;
        foreach (byte b in bytes)
        {
            if (_pendingUtf8.Count > 0 || b >= 0x80)
            {
                FeedUtf8(b);
                continue;
            }
            Step((char)b);
        }
    }

    private void FeedUtf8(byte b)
    {
        if (_pendingUtf8.Count == 0)
        {
            int need = b >= 0xF0 && b < 0xF8 ? 4 : b >= 0xE0 ? 3 : b >= 0xC2 && b < 0xE0 ? 2 : 0;
            if (need == 0) { Diagnose($"invalid UTF-8 lead byte 0x{b:X2}"); Print("�"); return; }
            _pendingUtf8.Add(b);
            return;
        }
        if ((b & 0xC0) != 0x80)
        {
            Diagnose($"truncated UTF-8 sequence before 0x{b:X2}");
            _pendingUtf8.Clear();
            Print("�");
            if (b < 0x80) Step((char)b); else FeedUtf8(b);
            return;
        }
        _pendingUtf8.Add(b);
        byte lead = _pendingUtf8[0];
        int expected = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : 2;
        if (_pendingUtf8.Count < expected) return;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(_pendingUtf8.ToArray()); }
        catch (DecoderFallbackException) { Diagnose("invalid UTF-8 sequence"); text = "�"; }
        _pendingUtf8.Clear();
        if (_state != State.Ground)
        {
            if (_state is State.Osc) _sequence.Append(text);
            else Diagnose($"non-ASCII character inside an escape sequence: {Escape(_sequence.ToString())}");
            return;
        }
        Print(text);
    }

    private void Step(char ch)
    {
        switch (_state)
        {
            case State.Ground:
                if (ch == '\x1b') { _state = State.Escape; _sequence.Clear(); }
                else if (ch == '\a') { BellCount++; Token("BEL"); }
                else if (ch < 0x20 || ch == 0x7F) Diagnose($"unsupported control character U+{(int)ch:X4}");
                else Print(ch.ToString());
                break;

            case State.Escape:
                if (ch == '[') { _state = State.Csi; _sequence.Clear(); }
                else if (ch == ']') { _state = State.Osc; _sequence.Clear(); }
                else { _state = State.Ground; Diagnose($"unsupported escape ESC {Escape(ch.ToString())}"); }
                break;

            case State.Csi:
                _sequence.Append(ch);
                if (ch >= 0x40 && ch <= 0x7E) { _state = State.Ground; Csi(_sequence.ToString()); }
                else if (_sequence.Length > 64) { _state = State.Ground; Diagnose($"unterminated CSI {Escape(_sequence.ToString())}"); }
                break;

            case State.Osc:
                if (ch == '\a') { _state = State.Ground; Token("OSC", detail: _sequence.ToString()); }
                else if (ch == '\x1b') _state = State.OscEscape;
                else if (_sequence.Length > 512) { _state = State.Ground; Diagnose("unterminated OSC"); }
                else _sequence.Append(ch);
                break;

            case State.OscEscape:
                _state = State.Ground;
                if (ch == '\\') Token("OSC", detail: _sequence.ToString());
                else Diagnose($"unsupported OSC terminator ESC {Escape(ch.ToString())}");
                break;
        }
    }

    private void Print(string text)
    {
        if (_wrapPending)
        {
            _wrapPending = false;
            CursorX = 0;
            if (++CursorY >= Height)
            {
                // Writing past the bottom-right corner scrolls a real terminal: never expected from a driver.
                Diagnose("text after the bottom-right cell scrolled the screen");
                Array.Copy(Screen, Width, Screen, 0, Width * (Height - 1));
                Array.Fill(Screen, new Cell { Text = " ", Fg = -1, Bg = -1 }, Width * (Height - 1), Width);
                CursorY = Height - 1;
            }
        }
        Screen[CursorY * Width + CursorX] = new Cell { Text = text, Fg = _fg, Bg = _bg };
        CountText();
        if (CursorX == Width - 1) _wrapPending = true;
        else CursorX++;
    }

    private int _textRun;
    private void CountText()
    {
        _textRun++;
        TokenCounts["TEXT-CELL"] = TokenCounts.TryGetValue("TEXT-CELL", out int n) ? n + 1 : 1;
    }

    private void FlushTextRun()
    {
        if (_textRun == 0) return;
        Record(new CaptureOp("TEXT", CursorX, CursorY, _textRun));
        _textRun = 0;
    }

    private void Csi(string body)
    {
        char final = body[^1];
        string parameters = body[..^1];
        char prefix = parameters.Length > 0 && parameters[0] is '?' or '>' or '<' or '=' ? parameters[0] : '\0';
        if (prefix != '\0') parameters = parameters[1..];
        if (parameters.Any(c => c is not (>= '0' and <= '9') and not ';' and not ':'))
        {
            Diagnose($"unsupported CSI {Escape(body)}");
            return;
        }
        int[] args = parameters.Length == 0 ? [] : parameters.Split(';').Select(p => p.Length == 0 ? 0 : int.Parse(p.Split(':')[0])).ToArray();

        switch ((prefix, final))
        {
            case ('\0', 'H'):
            case ('\0', 'f'):
            {
                int row = args.Length > 0 && args[0] > 0 ? args[0] : 1;
                int col = args.Length > 1 && args[1] > 0 ? args[1] : 1;
                if (row > Height || col > Width) Diagnose($"CUP {row};{col} outside {Width}x{Height}, clamped");
                CursorY = Math.Min(row, Height) - 1;
                CursorX = Math.Min(col, Width) - 1;
                _wrapPending = false;
                Token("CUP", CursorX, CursorY);
                return;
            }
            case ('\0', 'J') when args.Length == 1 && args[0] == 2:
                Array.Fill(Screen, new Cell { Text = " ", Fg = _fg, Bg = _bg });
                Token("ED2");
                return;
            case ('\0', 'm'):
                Sgr(args.Length == 0 ? [0] : args, body);
                return;
            case ('?', 'h'):
            case ('?', 'l'):
                foreach (int mode in args) PrivateMode(mode, final == 'h');
                return;
            case ('?', 'u'):
            case ('>', 'u'):
            case ('<', 'u'):
                Token("KITTY", detail: body);
                return;
            case ('\0', 'c'):
                Token("DA", detail: body);
                return;
        }
        Diagnose($"unsupported CSI {Escape(body)}");
    }

    private void PrivateMode(int mode, bool set)
    {
        switch (mode)
        {
            case 25:
                CursorVisible = set;
                Token(set ? "DECTCEM-SHOW" : "DECTCEM-HIDE");
                return;
            case 1049:
                if (set && !_onAlt) { _onAlt = true; _alt = Blank(Width * Height); }
                else if (!set && _onAlt) { _lastAltSnapshot = (Cell[])_alt.Clone(); _onAlt = false; }
                Token(set ? "ALTSCREEN-ENTER" : "ALTSCREEN-LEAVE");
                return;
            case 1000:
            case 1002:
            case 1003:
            case 1006:
                if (set) MouseModes.Add(mode); else MouseModes.Remove(mode);
                Token(set ? "MOUSE-ON" : "MOUSE-OFF", detail: mode.ToString());
                return;
        }
        Diagnose($"unsupported private mode {mode} {(set ? "set" : "reset")}");
    }

    private void Sgr(int[] args, string body)
    {
        for (int i = 0; i < args.Length; i++)
        {
            int a = args[i];
            if (a == 0) { _fg = -1; _bg = -1; }
            else if (a is >= 30 and <= 37) _fg = Vga.Rgb[AnsiToVga[a - 30]];
            else if (a is >= 90 and <= 97) _fg = Vga.Rgb[AnsiToVga[a - 90] + 8];
            else if (a == 39) _fg = -1;
            else if (a is >= 40 and <= 47) _bg = Vga.Rgb[AnsiToVga[a - 40]];
            else if (a is >= 100 and <= 107) _bg = Vga.Rgb[AnsiToVga[a - 100] + 8];
            else if (a == 49) _bg = -1;
            else if (a is 38 or 48 && i + 4 < args.Length && args[i + 1] == 2)
            {
                int r = args[i + 2], g = args[i + 3], b = args[i + 4];
                if (r > 255 || g > 255 || b > 255) Diagnose($"SGR colour component out of range in {Escape(body)}");
                int rgb = (Math.Min(r, 255) << 16) | (Math.Min(g, 255) << 8) | Math.Min(b, 255);
                if (a == 38) _fg = rgb; else _bg = rgb;
                i += 4;
            }
            else { Diagnose($"unsupported SGR parameter {a} in {Escape(body)}"); return; }
        }
        Token("SGR", detail: body);
    }

    // ANSI colour numbers run black, red, green, yellow, blue, magenta, cyan, white;
    // VGA indices run black, blue, green, cyan, red, magenta, brown, light grey.
    private static readonly int[] AnsiToVga = [0, 4, 2, 6, 1, 5, 3, 7];

    private void Token(string kind, int x = 0, int y = 0, string? detail = null)
    {
        FlushTextRun();
        Record(new CaptureOp(kind, x, y, Detail: detail));
    }

    private void Record(CaptureOp op)
    {
        TokenTotal++;
        TokenCounts[op.Kind] = TokenCounts.TryGetValue(op.Kind, out int n) ? n + 1 : 1;
        if (Tokens.Count < MaxTokens) Tokens.Add(op);
    }

    private void Diagnose(string message)
    {
        FlushTextRun();
        if (Diagnostics.Count < MaxDiagnostics) Diagnostics.Add($"@{BytesFed}: {message}");
    }

    /// <summary>Flushes a pending text run so token totals are complete.</summary>
    public void Settle()
    {
        FlushTextRun();
        if (_state != State.Ground) Diagnose($"output ended inside an escape sequence ({_state})");
        if (_pendingUtf8.Count > 0) Diagnose("output ended inside a UTF-8 sequence");
    }

    /// <summary>The visible cells, mapped to VGA palette indices.</summary>
    public SemanticCell[] SemanticCells(bool lastAlternateScreen = false)
    {
        Cell[] source = lastAlternateScreen && _lastAltSnapshot != null ? _lastAltSnapshot : Screen;
        return source.Select(c => new SemanticCell(c.Text, c.Fg < 0 ? -1 : Vga.IndexOf(c.Fg), c.Bg < 0 ? -1 : Vga.IndexOf(c.Bg))).ToArray();
    }

    /// <summary>Whether the alternate screen was left at least once (its last content is kept).</summary>
    public bool HasAlternateSnapshot => _lastAltSnapshot != null;

    private static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c == 0x7F ? $"\\x{(int)c:X2}" : c.ToString()));
}
