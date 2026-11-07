using System.Globalization;
using System.Text.RegularExpressions;
using TSharpVision.Constants;

namespace TSharpWorkbench.Testing;

// Extended input simulation. Everything here is testing-side: the simulated device produces ordinary TSharpVision
// TEvents (evKeyDown, evKeyUp, evModifierChanged, evMouseDown/Up/Move, evMouseWheel) and hands them to the event loop
// exactly where a driver would. No runtime event kind, field or rule is added or changed.

/// <summary>Logical modifier keys a scenario can hold. They project onto <c>kbShift</c>, <c>kbCtrlShift</c> and
/// <c>kbAltShift</c>, the logical state every driver reports (left/right are not distinguished).</summary>
[Flags]
public enum TestModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>Simulated mouse buttons. Side buttons exist in the runtime but are not simulated.</summary>
public enum TestMouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// The logical screen cell a mouse step targets. A literal cell is resolved against the grid when the step runs:
/// zero or positive coordinates are absolute (0,0 = top-left), negative ones count from the far edge (-1 = last column or
/// row), so one scenario can address the right end of the menu bar or the status row at every grid. A named point is
/// registered by the scenario (<see cref="TestScenario.Point"/>) and resolved by the application on the UI thread, for
/// targets whose position depends on layout. Nothing is clamped: a cell outside the current grid fails the step.
/// </summary>
public readonly record struct TestPoint
{
    private TestPoint(int x, int y, string? name) { X = x; Y = y; Name = name; }
    public int X { get; }
    public int Y { get; }
    /// <summary>The registered point's name, or null for a literal cell.</summary>
    public string? Name { get; }

    public static TestPoint At(int x, int y)
    {
        if (Math.Abs(x) >= GridLimits.MaxWidth || Math.Abs(y) >= GridLimits.MaxHeight)
            throw new ArgumentException($"Cell ({x},{y}) can never be on a logical grid (at most {GridLimits.MaxWidth}x{GridLimits.MaxHeight}).");
        return new(x, y, null);
    }

    public static TestPoint Named(string name)
    {
        if (!IsPointName(name)) throw new ArgumentException("Invalid point name (lowercase words joined by hyphens, at most 32 characters): " + name);
        return new(0, 0, name);
    }

    public static implicit operator TestPoint((int X, int Y) cell) => At(cell.X, cell.Y);

    public override string ToString() => Name is null
        ? X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture) : ":" + Name;

    internal static bool IsPointName(string? name) => name is { Length: <= 32 } && Protocol.IsValidName(name);

    internal static bool TryParse(string text, out TestPoint point)
    {
        point = default;
        if (text.StartsWith(':'))
        {
            if (!IsPointName(text[1..])) return false;
            point = new(0, 0, text[1..]);
            return true;
        }
        string[] parts = text.Split(',');
        if (parts.Length != 2 || !TryCoordinate(parts[0], GridLimits.MaxWidth, out int x) || !TryCoordinate(parts[1], GridLimits.MaxHeight, out int y)) return false;
        point = new(x, y, null);
        return true;
    }

    private static bool TryCoordinate(string text, int limit, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && Math.Abs(value) < limit
        && text == value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Step-name grammar of the input kinds. Steps stay <c>(Kind, Name)</c> pairs so that catalogs, the wire and the
/// artifacts keep their shape; every name is at most 48 characters.
/// <list type="bullet">
/// <item><c>modifier</c>: <c>shift-down</c>, <c>shift-up</c>, <c>ctrl-down</c>, <c>ctrl-up</c>, <c>alt-down</c>, <c>alt-up</c>.</item>
/// <item><c>key-down</c> / <c>key-up</c>: a <see cref="Keys"/> constant name.</item>
/// <item><c>text</c>: 1..48 characters typed one key event each (no control characters or surrogates).</item>
/// <item><c>mouse</c>: <c>move@P</c>; <c>down-B@P</c>, <c>up-B@P</c>, <c>click-B@P</c>, <c>double-B@P</c> with B = left, right or
/// middle; <c>wheel-D@P</c> or <c>wheel-D@P*N</c> with D = up, down, left or right and N = 1..20. P is <c>x,y</c> or
/// <c>:point-name</c> (see <see cref="TestPoint"/>).</item>
/// <item><c>release</c>: <c>all</c> — releases every held button, key and modifier through the event pipeline.</item>
/// </list>
/// </summary>
public static class InputSteps
{
    public const string KeyDown = "key-down", KeyUp = "key-up", Text = "text", Modifier = "modifier", Mouse = "mouse", Release = "release";
    public const string ReleaseAll = "all";
    /// <summary>The kinds added by the redesign (in addition to <c>key</c>, <c>action</c> and <c>resize</c>).</summary>
    public static readonly string[] Kinds = [KeyDown, KeyUp, Text, Modifier, Mouse, Release];
    public const int MaximumWheelCount = 20;

    private static readonly Regex ModifierPattern = new("^(shift|ctrl|alt)-(down|up)$", RegexOptions.CultureInvariant);
    private static readonly Regex MousePattern = new(
        "^(?<verb>move|(?<press>down|up|click|double)-(?<button>left|right|middle)|wheel-(?<wheel>up|down|left|right))@(?<at>[^@*]+)(\\*(?<count>[0-9]{1,2}))?$",
        RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="name"/> is a well-formed step of the input <paramref name="kind"/>.</summary>
    public static bool IsValid(string kind, string? name)
    {
        if (name is not { Length: > 0 and <= 48 }) return false;
        return kind switch
        {
            Modifier => ModifierPattern.IsMatch(name),
            KeyDown or KeyUp => TestKeys.TryResolve(name, out _),
            Text => IsValidText(name),
            Mouse => TryParseMouse(name, out _),
            Release => name == ReleaseAll,
            _ => false,
        };
    }

    internal static bool IsValidText(string text) => text.Length is > 0 and <= 48 && !text.Any(c => char.IsControl(c) || char.IsSurrogate(c));

    internal static string ModifierName(TestModifiers modifier, bool down) => modifier switch
    {
        TestModifiers.Shift => "shift", TestModifiers.Ctrl => "ctrl", TestModifiers.Alt => "alt",
        _ => throw new ArgumentException("Exactly one modifier expected."),
    } + (down ? "-down" : "-up");

    internal static (TestModifiers Modifier, bool Down) ParseModifier(string name)
    {
        var m = ModifierPattern.Match(name);
        if (!m.Success) throw new InvalidOperationException("Invalid modifier step: " + name);
        var modifier = m.Groups[1].Value switch { "shift" => TestModifiers.Shift, "ctrl" => TestModifiers.Ctrl, _ => TestModifiers.Alt };
        return (modifier, m.Groups[2].Value == "down");
    }

    internal static string ButtonName(TestMouseButton button) => button switch
    {
        TestMouseButton.Left => "left", TestMouseButton.Right => "right", TestMouseButton.Middle => "middle",
        _ => throw new ArgumentException("Unsupported mouse button: " + button),
    };

    /// <summary>A parsed mouse step. <see cref="Verb"/> is move, down, up, click, double or wheel.</summary>
    internal sealed record MouseStep(string Verb, TestMouseButton Button, uint WheelFlag, TestPoint At, int Count);

    internal static bool TryParseMouse(string name, out MouseStep step)
    {
        step = null!;
        var m = MousePattern.Match(name);
        if (!m.Success || !TestPoint.TryParse(m.Groups["at"].Value, out var at)) return false;
        bool wheel = m.Groups["wheel"].Success;
        int count = 1;
        if (m.Groups["count"].Success)
        {
            count = int.Parse(m.Groups["count"].Value, CultureInfo.InvariantCulture);
            if (!wheel || count is < 1 or > MaximumWheelCount || m.Groups["count"].Value != count.ToString(CultureInfo.InvariantCulture)) return false;
        }
        string verb = wheel ? "wheel" : m.Groups["press"].Success ? m.Groups["press"].Value : "move";
        var button = m.Groups["button"].Value switch { "right" => TestMouseButton.Right, "middle" => TestMouseButton.Middle, _ => TestMouseButton.Left };
        uint flag = m.Groups["wheel"].Value switch
        {
            "up" => Events.meWheelUp, "down" => Events.meWheelDown, "left" => Events.meWheelLeft, "right" => Events.meWheelRight, _ => 0,
        };
        step = new(verb, button, flag, at, count);
        return true;
    }

    internal static string FormatMouse(string verb, TestPoint at, TestMouseButton? button = null, string? wheel = null, int count = 1) =>
        verb + (button is { } b ? "-" + ButtonName(b) : "") + (wheel is null ? "" : "-" + wheel) + "@" + at
        + (count == 1 ? "" : "*" + count.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// The simulated keyboard's composition of a key with held modifiers. It mirrors the documented composition of the
/// Windows console driver (the reference implementation of the key contract): Alt wins over Ctrl, Ctrl over Shift;
/// function keys, navigation, Backspace/Tab/Enter/Escape and Space take their modified constants where the contract has
/// one and otherwise keep their identity with the modifier state attached; letters, digits and characters typed with
/// Ctrl or Alt become their Ctrl/Alt constants. Every event carries the held logical state in <c>controlKeyState</c>.
/// Other drivers (terminal, SDL) are not mirrored here; their native parity is a later qualification.
/// </summary>
internal static class SimulatedKeyboard
{
    private static readonly ushort[] Function = Codes("kbF{0}", 1, 12), ShiftFunction = Codes("kbShiftF{0}", 1, 12),
        CtrlFunction = Codes("kbCtrlF{0}", 1, 12), AltFunction = Codes("kbAltF{0}", 1, 12);
    private static readonly Dictionary<ushort, ushort> NavigationCtrl = new()
    {
        [Keys.kbHome] = Keys.kbCtrlHome, [Keys.kbUp] = Keys.kbUp, [Keys.kbPgUp] = Keys.kbCtrlPgUp, [Keys.kbLeft] = Keys.kbCtrlLeft,
        [Keys.kbRight] = Keys.kbCtrlRight, [Keys.kbEnd] = Keys.kbCtrlEnd, [Keys.kbDown] = Keys.kbDown, [Keys.kbPgDn] = Keys.kbCtrlPgDn,
        [Keys.kbIns] = Keys.kbCtrlIns, [Keys.kbDel] = Keys.kbCtrlDel,
    };
    private static readonly Dictionary<ushort, (ushort Shift, ushort Ctrl, ushort Alt)> Editing = new()
    {
        [Keys.kbBack] = (Keys.kbBack, Keys.kbCtrlBack, Keys.kbAltBack), [Keys.kbTab] = (Keys.kbShiftTab, Keys.kbTab, Keys.kbTab),
        [Keys.kbEnter] = (Keys.kbEnter, Keys.kbCtrlEnter, Keys.kbEnter), [Keys.kbEsc] = (Keys.kbEsc, Keys.kbEsc, Keys.kbEsc),
    };

    private static ushort[] Codes(string pattern, int first, int last) => Enumerable.Range(first, last - first + 1)
        .Select(i => TestKeys.TryResolve(string.Format(CultureInfo.InvariantCulture, pattern, i), out ushort code) ? code
            : throw new InvalidOperationException("Missing key constant for " + pattern)).ToArray();

    private static ushort Named(string name) => TestKeys.TryResolve(name, out ushort code) ? code : throw new InvalidOperationException("Missing key constant " + name);

    /// <summary>The logical control state of held modifiers, as drivers report it.</summary>
    internal static uint State(TestModifiers modifiers) =>
        ((modifiers & TestModifiers.Shift) != 0 ? Keys.kbShift : 0)
        | ((modifiers & TestModifiers.Ctrl) != 0 ? Keys.kbCtrlShift : 0)
        | ((modifiers & TestModifiers.Alt) != 0 ? Keys.kbAltShift : 0);

    internal static string Describe(TestModifiers modifiers) => modifiers == TestModifiers.None ? "none"
        : string.Join('+', new[] { TestModifiers.Shift, TestModifiers.Ctrl, TestModifiers.Alt }.Where(m => (modifiers & m) != 0));

    /// <summary>
    /// The key-down a press of the unmodified key <paramref name="code"/> produces while <paramref name="modifiers"/> are held.
    /// With nothing held it is exactly the historical <see cref="TestKeys.KeyDown"/> event. Returns null with a reason when
    /// the key has no composition (for example a constant that is already a modified key).
    /// </summary>
    internal static TEvent? Compose(ushort code, TestModifiers modifiers, out string? problem)
    {
        problem = null;
        if (modifiers == TestModifiers.None) return TestKeys.KeyDown(code);
        bool shift = (modifiers & TestModifiers.Shift) != 0, control = (modifiers & TestModifiers.Ctrl) != 0, alt = (modifiers & TestModifiers.Alt) != 0;
        uint state = State(modifiers);
        int index = Array.IndexOf(Function, code);
        ushort command = 0;
        if (index >= 0) command = alt ? AltFunction[index] : control ? CtrlFunction[index] : shift ? ShiftFunction[index] : Function[index];
        else if (NavigationCtrl.TryGetValue(code, out ushort ctrl))
            command = control && shift && code == Keys.kbIns ? Keys.kbCtrlShiftIns
                : control && shift && code == Keys.kbDel ? Keys.kbCtrlShiftDel
                : control ? ctrl
                : shift && code == Keys.kbIns ? Keys.kbShiftIns
                : shift && code == Keys.kbDel ? Keys.kbShiftDel
                : code;
        else if (Editing.TryGetValue(code, out var editing))
            command = alt && editing.Alt != code ? editing.Alt : control && editing.Ctrl != code ? editing.Ctrl : shift ? editing.Shift : code;
        else if (code == Keys.kbSpace) return ComposeCharacter(' ', modifiers);
        if (command == 0)
        {
            problem = $"{TestKeys.NameOf(code) ?? "0x" + code.ToString("x4", CultureInfo.InvariantCulture)} cannot be pressed with held {Describe(modifiers)}: "
                + "name an unmodified function, navigation or editing key (F1–F12, arrows, Home/End/PgUp/PgDn, Ins/Del, Back, Tab, Enter, Esc, Space) or type characters with a text step.";
            return null;
        }
        var ev = TestKeys.KeyDown(command);
        ev.keyDown.controlKeyState = state;
        return ev;
    }

    /// <summary>One typed character. Plain and Shift-held characters are text; with Ctrl or Alt held, letters, digits,
    /// Space, '-' and '=' become their Ctrl/Alt constants exactly where the key contract has one.</summary>
    internal static TEvent ComposeCharacter(char ch, TestModifiers modifiers)
    {
        bool control = (modifiers & TestModifiers.Ctrl) != 0, alt = (modifiers & TestModifiers.Alt) != 0;
        uint state = State(modifiers);
        char upper = char.ToUpperInvariant(ch);
        bool letter = upper is >= 'A' and <= 'Z', digit = ch is >= '0' and <= '9';
        ushort command = 0;
        if (!(control && alt))
        {
            if (letter && (control || alt)) command = Named((alt ? "kbAlt" : "kbCtrl") + upper);
            else if (alt && digit) command = Named("kbAlt" + ch);
            else if (alt && ch == ' ') command = Keys.kbAltSpace;
            else if (alt && ch == '-') command = Keys.kbAltMinus;
            else if (alt && ch == '=') command = Keys.kbAltEqual;
        }
        TEvent ev;
        if (command != 0) ev = TestKeys.KeyDown(command);
        else
        {
            ev = new TEvent { What = Events.evKeyDown };
            ev.keyDown.text = ch.ToString();
            if (ch <= byte.MaxValue) { ev.keyDown.keyCode = ch; ev.keyDown.charScan = new CharScanType((byte)ch, 0); }
        }
        ev.keyDown.controlKeyState = state;
        return ev;
    }
}

/// <summary>A non-modifier key held by a key-down step, with the identity its release reports.</summary>
internal sealed record HeldKey(string Name, ushort KeyCode, CharScanType CharScan);

/// <summary>Logical input state of the simulated devices: held modifiers, held keys, held mouse buttons and the pointer.
/// Immutable; each planned event carries the state after it.</summary>
internal sealed record InputSnapshot(TestModifiers Modifiers, HeldKey[] Keys, byte Buttons, (int X, int Y)? Pointer)
{
    internal static readonly InputSnapshot Empty = new(TestModifiers.None, [], 0, null);
    internal bool Holding => Modifiers != TestModifiers.None || Keys.Length > 0 || Buttons != 0;

    internal string Describe() =>
        $"modifiers={SimulatedKeyboard.Describe(Modifiers)}; keys={(Keys.Length == 0 ? "none" : string.Join('+', Keys.Select(k => k.Name)))}; "
        + $"buttons={DescribeButtons(Buttons)}; pointer={(Pointer is { } p ? p.X.ToString(CultureInfo.InvariantCulture) + "," + p.Y.ToString(CultureInfo.InvariantCulture) : "none")}";

    internal static string DescribeButtons(byte buttons) => buttons == 0 ? "none"
        : string.Join('+', new[] { ((byte)Events.mbLeftButton, "left"), ((byte)Events.mbRightButton, "right"), ((byte)Events.mbMiddleButton, "middle") }
            .Where(b => (buttons & b.Item1) != 0).Select(b => b.Item2));
}

/// <summary>
/// Turns one input step into the exact event sequence a device produces, validating everything first: an invalid step
/// (unknown key, button not held, chord, cell outside the grid, unavailable point) is refused before any event exists, so
/// a refused step never leaves partial state. Pure: no event loop, view or driver is touched.
/// </summary>
internal static class InputPlanner
{
    /// <summary>One event to submit; <see cref="Mouse"/> events go to the framework queue, the rest to the key path.</summary>
    internal sealed record Planned(TEvent Event, bool Mouse, string Label, InputSnapshot After);

    internal sealed record Plan(IReadOnlyList<Planned> Events, string? Detail, InputSnapshot After);

    internal static Plan Create(string kind, string name, InputSnapshot state, int width, int height, Func<string, TPoint?> point)
    {
        var events = new List<Planned>();
        string? detail = null;
        switch (kind)
        {
            case ScenarioStep.Key:
            {
                ushort code = Code(name);
                var ev = SimulatedKeyboard.Compose(code, state.Modifiers, out string? problem) ?? throw new InvalidOperationException(problem);
                if (state.Modifiers != TestModifiers.None)
                    detail = $"{name} with held {SimulatedKeyboard.Describe(state.Modifiers)} -> {TestKeys.NameOf(ev.keyDown.keyCode) ?? Hex(ev.keyDown.keyCode)}";
                events.Add(new(ev, false, "key-down " + name, state));
                break;
            }
            case InputSteps.KeyDown:
            {
                if (state.Keys.Any(k => k.Name == name)) throw new InvalidOperationException($"{name} is already held.");
                var ev = SimulatedKeyboard.Compose(Code(name), state.Modifiers, out string? problem) ?? throw new InvalidOperationException(problem);
                events.Add(new(ev, false, "key-down " + name, state with { Keys = [.. state.Keys, new(name, ev.keyDown.keyCode, ev.keyDown.charScan)] }));
                detail = $"holds {name} ({TestKeys.NameOf(ev.keyDown.keyCode) ?? Hex(ev.keyDown.keyCode)})";
                break;
            }
            case InputSteps.KeyUp:
                events.Add(KeyRelease(state, state.Keys.FirstOrDefault(k => k.Name == name) ?? throw new InvalidOperationException($"{name} is not held (no key-down step pressed it).")));
                break;
            case InputSteps.Text:
                if (!InputSteps.IsValidText(name)) throw new InvalidOperationException("Invalid text step.");
                foreach (char ch in name) events.Add(new(SimulatedKeyboard.ComposeCharacter(ch, state.Modifiers), false, "text '" + ch + "'", state));
                if (state.Modifiers != TestModifiers.None) detail = "typed with held " + SimulatedKeyboard.Describe(state.Modifiers);
                break;
            case InputSteps.Modifier:
            {
                var (modifier, down) = InputSteps.ParseModifier(name);
                if (down == ((state.Modifiers & modifier) != 0))
                    throw new InvalidOperationException($"{modifier} is {(down ? "already held" : "not held")}.");
                var after = state with { Modifiers = down ? state.Modifiers | modifier : state.Modifiers & ~modifier };
                events.Add(new(ModifierChange(after.Modifiers), false, name, after));
                detail = "held modifiers: " + SimulatedKeyboard.Describe(after.Modifiers);
                break;
            }
            case InputSteps.Release:
                if (name != InputSteps.ReleaseAll) throw new InvalidOperationException("Invalid release step.");
                events.AddRange(ReleaseEverything(state));
                detail = events.Count == 0 ? "nothing was held" : "released " + string.Join(", ", events.Select(e => e.Label));
                break;
            case InputSteps.Mouse:
                if (!InputSteps.TryParseMouse(name, out var step)) throw new InvalidOperationException("Invalid mouse step.");
                var (x, y) = Resolve(step.At, width, height, point);
                detail = $"at cell ({x},{y}) of {width}x{height}";
                PlanMouse(step, state, x, y, events);
                break;
            default:
                throw new InvalidOperationException("Input kind " + kind + " is not simulated.");
        }
        return new(events, detail, events.Count == 0 ? state : events[^1].After);
    }

    /// <summary>Everything still held, released in device order: mouse buttons, then keys (last pressed first), then
    /// modifiers (Alt, Ctrl, Shift), each with the state that remains.</summary>
    internal static List<Planned> ReleaseEverything(InputSnapshot state)
    {
        var events = new List<Planned>();
        if (state.Buttons != 0 && state.Pointer is { } p)
        {
            string held = InputSnapshot.DescribeButtons(state.Buttons);
            state = state with { Buttons = 0 };
            events.Add(new(Mouse(Events.evMouseUp, p.X, p.Y, 0, 0, state.Modifiers), true, held + "-up", state));
        }
        foreach (var key in state.Keys.Reverse())
        {
            var released = KeyRelease(state, key);
            events.Add(released); state = released.After;
        }
        foreach (var modifier in new[] { TestModifiers.Alt, TestModifiers.Ctrl, TestModifiers.Shift })
        {
            if ((state.Modifiers & modifier) == 0) continue;
            state = state with { Modifiers = state.Modifiers & ~modifier };
            events.Add(new(ModifierChange(state.Modifiers), false, InputSteps.ModifierName(modifier, false), state));
        }
        return events;
    }

    private static Planned KeyRelease(InputSnapshot state, HeldKey key)
    {
        var after = state with { Keys = state.Keys.Where(k => !ReferenceEquals(k, key)).ToArray() };
        var ev = new TEvent { What = Events.evKeyUp };
        ev.keyDown.keyCode = key.KeyCode; ev.keyDown.charScan = key.CharScan; ev.keyDown.controlKeyState = SimulatedKeyboard.State(state.Modifiers);
        return new(ev, false, "key-up " + key.Name, after);
    }

    private static TEvent ModifierChange(TestModifiers modifiers)
    {
        var ev = new TEvent { What = Events.evModifierChanged };
        ev.Modifiers = SimulatedKeyboard.State(modifiers);
        return ev;
    }

    private static void PlanMouse(InputSteps.MouseStep step, InputSnapshot state, int x, int y, List<Planned> events)
    {
        byte button = step.Button switch
        {
            TestMouseButton.Left => (byte)Events.mbLeftButton, TestMouseButton.Right => (byte)Events.mbRightButton,
            _ => (byte)Events.mbMiddleButton,
        };
        string buttonName = InputSteps.ButtonName(step.Button);
        if (step.Verb is "down" or "click" or "double" && state.Buttons != 0)
            throw new InvalidOperationException($"Cannot press the {buttonName} button while {InputSnapshot.DescribeButtons(state.Buttons)} is held: "
                + "the framework queue never reports a second press during a held button (button chords are not representable).");
        if (step.Verb == "up" && (state.Buttons & button) == 0)
            throw new InvalidOperationException($"The {buttonName} button is not held (no down step pressed it).");

        // A real pointer travels to the cell first; the one move carries the held buttons, so a move while pressed is a drag.
        if (state.Pointer != (x, y))
        {
            state = state with { Pointer = (x, y) };
            events.Add(new(Mouse(Events.evMouseMove, x, y, state.Buttons, Events.meMouseMoved, state.Modifiers), true, $"move to ({x},{y})", state));
        }
        void Press(uint flags, string label)
        {
            state = state with { Buttons = button };
            events.Add(new(Mouse(Events.evMouseDown, x, y, button, flags, state.Modifiers), true, label, state));
        }
        void Release(string label)
        {
            state = state with { Buttons = 0 };
            // Drivers report a release with an empty button mask.
            events.Add(new(Mouse(Events.evMouseUp, x, y, 0, 0, state.Modifiers), true, label, state));
        }
        switch (step.Verb)
        {
            case "down": Press(0, buttonName + "-down"); break;
            case "up": Release(buttonName + "-up"); break;
            case "click": Press(0, buttonName + "-down"); Release(buttonName + "-up"); break;
            case "double":
                Press(0, buttonName + "-down"); Release(buttonName + "-up");
                // The classification is explicit: the host disables the framework's wall-clock double-click window.
                Press(Events.meDoubleClick, buttonName + "-down (double-click)"); Release(buttonName + "-up");
                break;
            case "wheel":
                for (int i = 0; i < step.Count; i++)
                    events.Add(new(Mouse(Events.evMouseWheel, x, y, state.Buttons, step.WheelFlag, state.Modifiers), true, "wheel " + WheelName(step.WheelFlag), state));
                break;
        }
    }

    private static string WheelName(uint flag) => flag == Events.meWheelUp ? "up" : flag == Events.meWheelDown ? "down" : flag == Events.meWheelLeft ? "left" : "right";

    private static TEvent Mouse(ushort what, int x, int y, byte buttons, uint flags, TestModifiers modifiers)
    {
        var ev = new TEvent { What = what };
        ev.mouse.where = new TPoint(x, y); ev.mouse.buttons = buttons; ev.mouse.eventFlags = flags;
        ev.mouse.controlKeyState = SimulatedKeyboard.State(modifiers);
        return ev;
    }

    /// <summary>The absolute cell of <paramref name="at"/> on a <paramref name="width"/> x <paramref name="height"/> grid.</summary>
    internal static (int X, int Y) Resolve(TestPoint at, int width, int height, Func<string, TPoint?> point)
    {
        int x, y;
        string what;
        if (at.Name is { } name)
        {
            var resolved = point(name) ?? throw new InvalidOperationException($"Point ':{name}' is not available on the current screen.");
            (x, y, what) = (resolved.x, resolved.y, $"Point ':{name}' resolved to ({resolved.x},{resolved.y}), which");
        }
        else
        {
            x = at.X < 0 ? width + at.X : at.X; y = at.Y < 0 ? height + at.Y : at.Y;
            what = at.X < 0 || at.Y < 0 ? $"Cell ({at.X},{at.Y}) = ({x},{y})" : $"Cell ({x},{y})";
        }
        if (x < 0 || y < 0 || x >= width || y >= height)
            throw new InvalidOperationException($"{what} is outside the {width}x{height} screen (columns 0..{width - 1}, rows 0..{height - 1}).");
        return (x, y);
    }

    private static ushort Code(string name) => TestKeys.TryResolve(name, out ushort code) ? code : throw new InvalidOperationException("Unknown key constant " + name + ".");
    private static string Hex(ushort code) => "0x" + code.ToString("x4", CultureInfo.InvariantCulture);
}
