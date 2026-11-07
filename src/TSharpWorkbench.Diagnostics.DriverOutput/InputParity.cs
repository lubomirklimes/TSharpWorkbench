using System.Text;
using TSharpVision.Constants;
using TSharpVision.Drivers.Console;
using TSharpVision.Drivers.SDL;
using TSharpVision.Drivers.Terminal;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>Logical modifier flags compared in parity (left/right are not distinguished).</summary>
[Flags]
public enum KeyMods { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }

/// <summary>One expected event of the TSharpVision input contract. Null members are not compared.</summary>
public sealed record ExpectedEvent(ushort What, ushort? KeyCode = null, byte? CharCode = null, string? Text = null, KeyMods? Mods = null,
    uint? Modifiers = null, int? X = null, int? Y = null, byte? Buttons = null, uint? MouseFlags = null)
{
    public static ExpectedEvent Key(ushort keyCode, KeyMods mods = KeyMods.None) => new(Events.evKeyDown, KeyCode: keyCode, Mods: mods);
    public static ExpectedEvent Up(ushort keyCode) => new(Events.evKeyUp, KeyCode: keyCode);
    public static ExpectedEvent Mouse(ushort what, int x, int y, byte buttons, uint flags = 0) => new(what, X: x, Y: y, Buttons: buttons, MouseFlags: flags);
}

/// <summary>Result of one parity case on one driver.</summary>
public sealed record ParityResult(string Case, string Driver, string Outcome, string Native, string Expected, string Actual, string? Note = null);

/// <summary>
/// Input translator parity: each case is fed to every driver as that platform's native
/// representation (KEY/MOUSE_EVENT_RECORD fields, terminal bytes, SDL events) through the drivers'
/// own translators, and the resulting TEvents are compared with one expectation written from the
/// framework's key and event constants. Mouse events reach the framework the way each driver
/// publishes them (through <see cref="TEventQueue"/>), with timing-based double-click synthesis
/// disabled so only the driver's own double-click reporting is observed.
/// </summary>
public static class InputParity
{
    public const string Console = "console-win32", Terminal = "terminal-ansi", TerminalKitty = "terminal-kitty", Sdl = "sdl";
    public const int MouseX = 10, MouseY = 5;
    private const uint MOUSE_MOVED = 1, DOUBLE_CLICK = 2, MOUSE_WHEELED = 4, MOUSE_HWHEELED = 8;
    private const uint LeftBtn = 1, RightBtn = 2;
    private const uint ConsoleShift = Win32KeyTranslator.SHIFT_PRESSED, ConsoleCtrl = Win32KeyTranslator.LEFT_CTRL_PRESSED,
        ConsoleAlt = Win32KeyTranslator.LEFT_ALT_PRESSED, Enhanced = Win32KeyTranslator.ENHANCED_KEY;

    private sealed record KeyCase(string Id, ExpectedEvent[] Expect,
        (bool Down, ushort Vk, ushort Scan, char Ch, uint State)[] Console,
        string? Terminal, string? TerminalKitty,
        Func<SDLDriver, string>? Sdl,
        string? TerminalNote = null);

    private static string SdlKey(SDLDriver d, SDL3.SDL.Keycode key, SDL3.SDL.Keymod mod = SDL3.SDL.Keymod.None, bool down = true)
    {
        if (down) d.ProcessOrdinaryKeyDown((uint)key, (ushort)mod); else d.ProcessOrdinaryKeyUp((uint)key, (ushort)mod);
        return $"SDL_EVENT_KEY_{(down ? "DOWN" : "UP")} {key}{(mod == 0 ? "" : "+" + mod)}";
    }

    private static readonly KeyCase[] KeyCases =
    [
        new("char-a", [new ExpectedEvent(Events.evKeyDown, CharCode: (byte)'a', Text: "a", Mods: KeyMods.None)],
            [(true, 0x41, 0x1E, 'a', 0)], "a", null,
            d => { d.ProcessOrdinaryKeyDown((uint)SDL3.SDL.Keycode.A, 0); d.ProcessTextInput("a", 0); return "KEY_DOWN A, TEXT_INPUT \"a\""; }),
        new("f5", [ExpectedEvent.Key(Keys.kbF5)], [(true, 0x74, 0x3F, '\0', 0)], "\x1b[15~", null, d => SdlKey(d, SDL3.SDL.Keycode.F5)),
        new("shift-f5", [ExpectedEvent.Key(Keys.kbShiftF5, KeyMods.Shift)], [(true, 0x74, 0x3F, '\0', ConsoleShift)], "\x1b[15;2~", null,
            d => SdlKey(d, SDL3.SDL.Keycode.F5, SDL3.SDL.Keymod.LShift)),
        new("ctrl-a", [new ExpectedEvent(Events.evKeyDown, KeyCode: Keys.kbCtrlA, Mods: KeyMods.Ctrl)], [(true, 0x41, 0x1E, '\x01', ConsoleCtrl)], "\x01", null,
            d => SdlKey(d, SDL3.SDL.Keycode.A, SDL3.SDL.Keymod.LCtrl), "a C0 byte carries no Ctrl state in legacy ANSI"),
        new("alt-x", [ExpectedEvent.Key(Keys.kbAltX, KeyMods.Alt)], [(true, 0x58, 0x2D, 'x', ConsoleAlt)], "\x1bx", null,
            d => SdlKey(d, SDL3.SDL.Keycode.X, SDL3.SDL.Keymod.LAlt)),
        new("escape", [ExpectedEvent.Key(Keys.kbEsc)], [(true, 0x1B, 0x01, '\x1b', 0)], "\x1b", null, d => SdlKey(d, SDL3.SDL.Keycode.Escape)),
        new("tab", [ExpectedEvent.Key(Keys.kbTab)], [(true, 0x09, 0x0F, '\t', 0)], "\t", null, d => SdlKey(d, SDL3.SDL.Keycode.Tab)),
        new("shift-tab", [ExpectedEvent.Key(Keys.kbShiftTab, KeyMods.Shift)], [(true, 0x09, 0x0F, '\t', ConsoleShift)], "\x1b[Z", null,
            d => SdlKey(d, SDL3.SDL.Keycode.Tab, SDL3.SDL.Keymod.LShift)),
        new("arrow-up", [ExpectedEvent.Key(Keys.kbUp)], [(true, 0x26, 0x48, '\0', Enhanced)], "\x1b[A", null, d => SdlKey(d, SDL3.SDL.Keycode.Up)),
        new("arrow-right", [ExpectedEvent.Key(Keys.kbRight)], [(true, 0x27, 0x4D, '\0', Enhanced)], "\x1b[C", null, d => SdlKey(d, SDL3.SDL.Keycode.Right)),
        new("shift-down-up",
            [new ExpectedEvent(Events.evModifierChanged, Modifiers: Keys.kbShift), new ExpectedEvent(Events.evModifierChanged, Modifiers: 0)],
            [(true, 0x10, 0x2A, '\0', ConsoleShift), (false, 0x10, 0x2A, '\0', 0)], null, "\x1b[57441;2:1u\x1b[57441;1:3u",
            d => { d.ProcessModifierKey((uint)SDL3.SDL.Keycode.LShift, true); d.ProcessModifierKey((uint)SDL3.SDL.Keycode.LShift, false); return "KEY_DOWN LShift, KEY_UP LShift"; },
            "legacy ANSI byte streams carry no standalone modifier transitions"),
        new("f5-release", [ExpectedEvent.Key(Keys.kbF5), ExpectedEvent.Up(Keys.kbF5)],
            [(true, 0x74, 0x3F, '\0', 0), (false, 0x74, 0x3F, '\0', 0)], null, "\x1b[15;1:1~\x1b[15;1:3~",
            d => SdlKey(d, SDL3.SDL.Keycode.F5) + ", " + SdlKey(d, SDL3.SDL.Keycode.F5, down: false),
            "legacy ANSI byte streams carry no key releases"),
    ];

    private sealed record MouseCase(string Id, ExpectedEvent[] Expect,
        (uint Buttons, uint Flags, int X, int Y)[] Console, string? Terminal, Func<SdlMouse, string>? Sdl, string? TerminalNote = null);

    private static readonly MouseCase[] MouseCases =
    [
        new("left-click",
            [ExpectedEvent.Mouse(Events.evMouseDown, MouseX, MouseY, (byte)Events.mbLeftButton), ExpectedEvent.Mouse(Events.evMouseUp, MouseX, MouseY, 0)],
            [(LeftBtn, 0, MouseX, MouseY), (0, 0, MouseX, MouseY)], "\x1b[<0;11;6M\x1b[<0;11;6m",
            m => m.Button(1, true, 1) + ", " + m.Button(1, false, 1)),
        new("right-click",
            [ExpectedEvent.Mouse(Events.evMouseDown, MouseX, MouseY, (byte)Events.mbRightButton), ExpectedEvent.Mouse(Events.evMouseUp, MouseX, MouseY, 0)],
            [(RightBtn, 0, MouseX, MouseY), (0, 0, MouseX, MouseY)], "\x1b[<2;11;6M\x1b[<2;11;6m",
            m => m.Button(3, true, 1) + ", " + m.Button(3, false, 1)),
        new("double-click",
            [ExpectedEvent.Mouse(Events.evMouseDown, MouseX, MouseY, (byte)Events.mbLeftButton, Events.meDoubleClick), ExpectedEvent.Mouse(Events.evMouseUp, MouseX, MouseY, 0)],
            [(LeftBtn, DOUBLE_CLICK, MouseX, MouseY), (0, 0, MouseX, MouseY)], null,
            m => m.Button(1, true, 2) + ", " + m.Button(1, false, 2),
            "SGR mouse reports carry no click count; TEventQueue synthesises double clicks from timing"),
        new("drag",
            [ExpectedEvent.Mouse(Events.evMouseDown, MouseX, MouseY, (byte)Events.mbLeftButton),
             ExpectedEvent.Mouse(Events.evMouseMove, MouseX + 2, MouseY, (byte)Events.mbLeftButton, Events.meMouseMoved),
             ExpectedEvent.Mouse(Events.evMouseUp, MouseX + 2, MouseY, 0)],
            [(LeftBtn, 0, MouseX, MouseY), (LeftBtn, MOUSE_MOVED, MouseX + 2, MouseY), (0, 0, MouseX + 2, MouseY)],
            "\x1b[<0;11;6M\x1b[<32;13;6M\x1b[<0;13;6m",
            m => m.Button(1, true, 1) + ", " + m.Motion(MouseX + 2, MouseY, held: true) + ", " + m.Button(1, false, 1, MouseX + 2)),
        new("wheel-up", [ExpectedEvent.Mouse(Events.evMouseWheel, MouseX, MouseY, 0, Events.meWheelUp)],
            [(unchecked((uint)(120 << 16)), MOUSE_WHEELED, MouseX, MouseY)], "\x1b[<64;11;6M", m => m.Wheel(0, 1)),
        new("wheel-down", [ExpectedEvent.Mouse(Events.evMouseWheel, MouseX, MouseY, 0, Events.meWheelDown)],
            [(unchecked((uint)(-120 << 16)), MOUSE_WHEELED, MouseX, MouseY)], "\x1b[<65;11;6M", m => m.Wheel(0, -1)),
        new("wheel-right", [ExpectedEvent.Mouse(Events.evMouseWheel, MouseX, MouseY, 0, Events.meWheelRight)],
            [(unchecked((uint)(120 << 16)), MOUSE_HWHEELED, MouseX, MouseY)], "\x1b[<66;11;6M", m => m.Wheel(1, 0)),
    ];

    /// <summary>Pushes real SDL events into an attached <see cref="SDLDriver"/> and pumps it.</summary>
    public sealed class SdlMouse(SDLDriver driver, int cellWidth, int cellHeight)
    {
        private float Px(int col) => col * cellWidth + cellWidth / 2f;
        private float Py(int row) => row * cellHeight + cellHeight / 2f;

        public string Button(byte button, bool down, byte clicks, int col = MouseX)
        {
            var e = new SDL3.SDL.Event();
            e.Button = new SDL3.SDL.MouseButtonEvent
            {
                Type = down ? SDL3.SDL.EventType.MouseButtonDown : SDL3.SDL.EventType.MouseButtonUp,
                Button = button, Down = down, Clicks = clicks, X = Px(col), Y = Py(MouseY),
            };
            Push(e);
            return $"MOUSE_BUTTON_{(down ? "DOWN" : "UP")} b{button} clicks={clicks} px=({Px(col)},{Py(MouseY)})";
        }

        public string Motion(int col, int row, bool held)
        {
            var e = new SDL3.SDL.Event();
            e.Motion = new SDL3.SDL.MouseMotionEvent
            {
                Type = SDL3.SDL.EventType.MouseMotion, X = Px(col), Y = Py(row),
                State = held ? SDL3.SDL.MouseButtonFlags.Left : 0,
            };
            Push(e);
            return $"MOUSE_MOTION px=({Px(col)},{Py(row)}){(held ? " left held" : "")}";
        }

        public string Wheel(float x, float y)
        {
            var e = new SDL3.SDL.Event();
            e.Wheel = new SDL3.SDL.MouseWheelEvent
            {
                Type = SDL3.SDL.EventType.MouseWheel, X = x, Y = y, Direction = SDL3.SDL.MouseWheelDirection.Normal,
                MouseX = Px(MouseX), MouseY = Py(MouseY),
            };
            Push(e);
            return $"MOUSE_WHEEL x={x} y={y}";
        }

        private void Push(SDL3.SDL.Event e)
        {
            if (!SDL3.SDL.PushEvent(ref e)) throw new InvalidOperationException($"SDL_PushEvent failed: {SDL3.SDL.GetError()}");
            driver.PumpMessages();
        }
    }

    /// <summary>Runs every case on every driver. <paramref name="sdl"/> is null when SDL could not be attached.</summary>
    public static List<ParityResult> Run(SdlRendererOutputPath? sdl)
    {
        var results = new List<ParityResult>();
        int savedDelay = TEventQueue.DoubleDelay;
        TEventQueue.DoubleDelay = int.MinValue;
        try
        {
            PathSupport.DrainQueue();
            foreach (KeyCase c in KeyCases) results.AddRange(RunKey(c));
            foreach (MouseCase c in MouseCases) results.AddRange(RunMouse(c, sdl));
        }
        finally { TEventQueue.DoubleDelay = savedDelay; }
        return results;
    }

    private static IEnumerable<ParityResult> RunKey(KeyCase c)
    {
        string expected = string.Join(", ", c.Expect.Select(Describe));

        // Win32 console: KEY_EVENT_RECORD fields through the driver's record processing.
        var console = new Win32ConsoleDriver();
        foreach (var r in c.Console) console.ProcessKeyRecord(r.Down, r.Vk, r.Scan, r.Ch, r.State);
        yield return Judge(c.Id, Console, string.Join(", ", c.Console.Select(r => $"{(r.Down ? "down" : "up")} vk=0x{r.Vk:X2} scan=0x{r.Scan:X2} ch=U+{(int)r.Ch:X4} state=0x{r.State:X3}")),
            c.Expect, DrainKeys(console.ReadKeyEvent), expected);

        // Terminal legacy ANSI byte stream through the driver's decoder.
        if (c.Terminal is { } bytes) yield return Judge(c.Id, Terminal, Show(bytes), c.Expect, DecodeTerminal(bytes, kitty: false), expected);
        else yield return new ParityResult(c.Id, Terminal, Outcomes.NotSupported, "n/a", expected, "n/a", c.TerminalNote);

        // Terminal with the kitty keyboard protocol negotiated.
        string kittyBytes = c.TerminalKitty ?? c.Terminal ?? "";
        yield return Judge(c.Id, TerminalKitty, Show(kittyBytes), c.Expect, DecodeTerminal(kittyBytes, kitty: true), expected);

        // SDL: the driver's key-down / key-up / text-input / modifier processing.
        var sdl = new SDLDriver();
        string native = c.Sdl!(sdl);
        yield return Judge(c.Id, Sdl, native, c.Expect, DrainKeys(sdl.ReadKeyEvent), expected);
    }

    private static IEnumerable<ParityResult> RunMouse(MouseCase c, SdlRendererOutputPath? sdlPath)
    {
        string expected = string.Join(", ", c.Expect.Select(Describe));

        PathSupport.DrainQueue();
        foreach (var r in c.Console) TEventQueue.Enqueue(Win32ConsoleDriver.TranslateMouse(r.Buttons, r.Flags, (short)r.X, (short)r.Y));
        yield return Judge(c.Id, Console, string.Join(", ", c.Console.Select(r => $"buttons=0x{r.Buttons:X8} flags=0x{r.Flags:X} at ({r.X},{r.Y})")),
            c.Expect, MouseEvents(), expected);

        if (c.Terminal is { } bytes)
        {
            PathSupport.DrainQueue();
            foreach (TEvent ev in DecodeTerminal(bytes, kitty: false))
                if (IsMouse(ev)) TEventQueue.Enqueue(ev);
            yield return Judge(c.Id, Terminal, Show(bytes), c.Expect, MouseEvents(), expected);
        }
        else yield return new ParityResult(c.Id, Terminal, Outcomes.NotSupported, "n/a", expected, "n/a", c.TerminalNote);

        if (sdlPath?.Unavailable is null && sdlPath != null)
        {
            PathSupport.DrainQueue();
            var mouse = new SdlMouse(sdlPath.DriverInstance, sdlPath.CellWidth, sdlPath.CellHeight);
            string native = c.Sdl!(mouse);
            yield return Judge(c.Id, Sdl, native, c.Expect, MouseEvents(), expected);
        }
        else yield return new ParityResult(c.Id, Sdl, Outcomes.Unavailable, "n/a", expected, "n/a", sdlPath?.Unavailable ?? "SDL not attached");
    }

    private delegate bool KeyReader(out TEvent ev);

    private static List<TEvent> DrainKeys(KeyReader read)
    {
        var events = new List<TEvent>();
        while (events.Count < 64 && read(out TEvent ev)) events.Add(ev);
        return events;
    }

    private static bool IsMouse(TEvent e) => (e.What & (Events.evMouse | Events.evMouseWheel)) != 0;

    private static List<TEvent> MouseEvents() => PathSupport.DrainQueue().Where(IsMouse).ToList();

    private static List<TEvent> DecodeTerminal(string text, bool kitty)
    {
        long clock = 0;
        var decoder = new TerminalInputDecoder(() => clock);
        if (kitty)
        {
            decoder.BeginKeyboardNegotiation();
            decoder.Feed("\x1b[?0u"u8, _ => { });
            decoder.Feed("\x1b[?31u"u8, _ => { });
            if (decoder.NegotiationState != KittyNegotiationState.Active) throw new InvalidOperationException("kitty negotiation did not become active");
        }
        decoder.Feed(Encoding.UTF8.GetBytes(text), _ => { });
        clock += TerminalInputDecoder.EscapeDelayMilliseconds + 10; // a lone ESC is published after the escape delay
        var events = new List<TEvent>();
        while (events.Count < 64 && decoder.TryRead(out TEvent ev)) events.Add(ev);
        return events;
    }

    private static KeyMods ModsOf(uint state) =>
        ((state & Keys.kbShift) != 0 ? KeyMods.Shift : 0) | ((state & Keys.kbCtrlShift) != 0 ? KeyMods.Ctrl : 0) | ((state & Keys.kbAltShift) != 0 ? KeyMods.Alt : 0);

    private static ParityResult Judge(string id, string driver, string native, ExpectedEvent[] expect, List<TEvent> actual, string expectedText)
    {
        string actualText = actual.Count == 0 ? "(no events)" : string.Join(", ", actual.Select(Describe));
        var problems = new List<string>();
        if (actual.Count != expect.Length) problems.Add($"{actual.Count} event(s), expected {expect.Length}");
        for (int i = 0; i < Math.Min(actual.Count, expect.Length); i++)
        {
            ExpectedEvent e = expect[i];
            TEvent a = actual[i];
            if (a.What != e.What) { problems.Add($"#{i} What 0x{a.What:X4} != 0x{e.What:X4}"); continue; }
            if (e.KeyCode is { } kc && a.keyDown.keyCode != kc) problems.Add($"#{i} keyCode 0x{a.keyDown.keyCode:X4} != 0x{kc:X4}");
            if (e.CharCode is { } cc && a.keyDown.charScan.charCode != cc) problems.Add($"#{i} charCode 0x{a.keyDown.charScan.charCode:X2} != 0x{cc:X2}");
            if (e.Text is { } t && a.keyDown.text != t) problems.Add($"#{i} text '{a.keyDown.text}' != '{t}'");
            if (e.Mods is { } m && ModsOf(a.keyDown.controlKeyState) != m) problems.Add($"#{i} modifiers {ModsOf(a.keyDown.controlKeyState)} != {m}");
            if (e.Modifiers is { } mod && a.Modifiers != mod) problems.Add($"#{i} Modifiers 0x{a.Modifiers:X} != 0x{mod:X}");
            if (e.X is { } x && (a.mouse.where.x != x || a.mouse.where.y != e.Y)) problems.Add($"#{i} at ({a.mouse.where.x},{a.mouse.where.y}) != ({x},{e.Y})");
            if (e.Buttons is { } b && a.mouse.buttons != b) problems.Add($"#{i} buttons 0x{a.mouse.buttons:X2} != 0x{b:X2}");
            const uint compared = Events.meDoubleClick | Events.meMouseMoved | Events.meWheelUp | Events.meWheelDown | Events.meWheelLeft | Events.meWheelRight;
            if (e.MouseFlags is { } f && (a.mouse.eventFlags & compared) != f) problems.Add($"#{i} flags 0x{a.mouse.eventFlags & compared:X2} != 0x{f:X2}");
        }
        return new ParityResult(id, driver, problems.Count == 0 ? Outcomes.Verified : Outcomes.Mismatch, native, expectedText, actualText,
            problems.Count == 0 ? null : string.Join("; ", problems));
    }

    public static string Describe(ExpectedEvent e) => e.What switch
    {
        Events.evKeyDown or Events.evKeyUp =>
            $"{(e.What == Events.evKeyDown ? "KeyDown" : "KeyUp")}({(e.KeyCode is { } k ? $"0x{k:X4}" : e.CharCode is { } c ? $"char 0x{c:X2}" : "?")}{(e.Text is { } t ? $" '{t}'" : "")}{(e.Mods is { } m && m != KeyMods.None ? " " + m : "")})",
        Events.evModifierChanged => $"ModifierChanged(0x{e.Modifiers:X})",
        _ => $"{MouseName(e.What)}({e.X},{e.Y} b=0x{e.Buttons:X} f=0x{e.MouseFlags:X})",
    };

    public static string Describe(TEvent e) => e.What switch
    {
        Events.evKeyDown or Events.evKeyUp =>
            $"{(e.What == Events.evKeyDown ? "KeyDown" : "KeyUp")}(0x{e.keyDown.keyCode:X4}{(e.keyDown.text.Length > 0 ? $" '{e.keyDown.text}'" : "")}{(ModsOf(e.keyDown.controlKeyState) is var m && m != KeyMods.None ? " " + m : "")})",
        Events.evModifierChanged => $"ModifierChanged(0x{e.Modifiers:X})",
        _ when IsMouse(e) => $"{MouseName(e.What)}({e.mouse.where.x},{e.mouse.where.y} b=0x{e.mouse.buttons:X} f=0x{e.mouse.eventFlags:X})",
        _ => $"What=0x{e.What:X4}",
    };

    private static string MouseName(ushort what) => what switch
    {
        Events.evMouseDown => "MouseDown", Events.evMouseUp => "MouseUp", Events.evMouseMove => "MouseMove",
        Events.evMouseWheel => "MouseWheel", Events.evMouseAuto => "MouseAuto", _ => $"0x{what:X4}",
    };

    private static string Show(string bytes) => string.Concat(bytes.Select(ch => ch < 0x20 ? $"\\x{(int)ch:X2}" : ch.ToString()));
}
