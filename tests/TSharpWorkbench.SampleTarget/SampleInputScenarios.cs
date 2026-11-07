using System.Globalization;
using TSharpVision;
using TSharpVision.Constants;

namespace TSharpWorkbench.SampleTarget;

/// <summary>
/// Every device event the application's loops read, in order, as compact text. Installed by the self-tests only.
/// <c>S</c>/<c>C</c>/<c>A</c> = the logical Shift/Ctrl/Alt state carried by the event, <c>-</c> = none.
/// </summary>
internal sealed class InputLog
{
    internal readonly List<string> Entries = [];

    internal void Record(TEvent ev)
    {
        string? entry = ev.What switch
        {
            Events.evKeyDown => ev.keyDown.text.Length > 0 ? $"text '{ev.keyDown.text}' {State(ev.keyDown.controlKeyState)}"
                : $"down {TestKeys.NameOf(ev.keyDown.keyCode)} {State(ev.keyDown.controlKeyState)}",
            Events.evKeyUp => $"up {TestKeys.NameOf(ev.keyDown.keyCode)} {State(ev.keyDown.controlKeyState)}",
            Events.evModifierChanged => "mod " + State(ev.Modifiers),
            Events.evMouseDown => $"mdown {Buttons(ev.mouse.buttons)} {At(ev)} {State(ev.mouse.controlKeyState)}" + (ev.mouse.doubleClick ? " dbl" : ""),
            Events.evMouseUp => $"mup {At(ev)} {State(ev.mouse.controlKeyState)}",
            Events.evMouseMove => $"move {At(ev)} {Buttons(ev.mouse.buttons)} {State(ev.mouse.controlKeyState)}",
            Events.evMouseAuto => $"auto {At(ev)}",
            Events.evMouseWheel => $"wheel {Wheel(ev.mouse.eventFlags)} {At(ev)} {State(ev.mouse.controlKeyState)}",
            _ => null,
        };
        if (entry is not null) Entries.Add(entry);
    }

    internal static string State(uint state)
    {
        string s = ((state & Keys.kbShift) != 0 ? "S" : "") + ((state & Keys.kbCtrlShift) != 0 ? "C" : "") + ((state & Keys.kbAltShift) != 0 ? "A" : "");
        return s.Length == 0 ? "-" : s;
    }

    private static string At(TEvent ev) => ev.mouse.where.x.ToString(CultureInfo.InvariantCulture) + "," + ev.mouse.where.y.ToString(CultureInfo.InvariantCulture);
    private static string Buttons(byte b) => b == 0 ? "none" : b == Events.mbLeftButton ? "left" : b == Events.mbRightButton ? "right" : b == Events.mbMiddleButton ? "middle" : "0x" + b.ToString("x2", CultureInfo.InvariantCulture);
    private static string Wheel(uint f) => (f & Events.meWheelUp) != 0 ? "up" : (f & Events.meWheelDown) != 0 ? "down" : (f & Events.meWheelLeft) != 0 ? "left" : (f & Events.meWheelRight) != 0 ? "right" : "?";
}

/// <summary>
/// Self-tests of the simulated keyboard and mouse against the real event pipeline. Each scenario installs an
/// <see cref="InputLog"/> and its checkpoints require the exact event sequence the application received so far: the
/// right kinds, codes, cells, buttons and modifier states, in order, with nothing duplicated or invented.
/// </summary>
internal static class SampleInputScenarios
{
    internal const string Keyboard = "selftest/input/keyboard-state", Mouse = "selftest/input/mouse-lifecycle",
        Mixed = "selftest/input/mixed-dialog", Refused = "selftest/failure/input-off-screen";

    private static readonly string[] Tree = [Protocol.EvidenceKinds.ViewTree];

    internal static IEnumerable<TestScenario> All()
    {
        yield return KeyboardScenario();
        yield return MouseScenario();
        yield return MixedScenario();
        yield return RefusedScenario();
    }

    private static SampleApp App(TestTargetContext c) => (SampleApp)c.Application;
    private static List<string> Log(TestTargetContext c) => App(c).InputLog!.Entries;
    private static string K(ushort code) => TestKeys.NameOf(code)!;
    private static void Install(TestTargetContext c) => App(c).InputLog = new InputLog();

    /// <summary>The log equals the expected sequence exactly; the note names the first difference.</summary>
    private static Func<TestTargetContext, bool> Exactly(Func<TestTargetContext, string[]> expected) => c => Log(c).SequenceEqual(expected(c));
    private static Func<TestTargetContext, bool> Exactly(params string[] expected) => Exactly(_ => expected);

    private static TestScenario KeyboardScenario()
    {
        var shiftCtrl = new[]
        {
            "mod S", $"down {K(Keys.kbShiftF5)} S", $"down {K(Keys.kbShiftF5)} S", "mod SC", $"down {K(Keys.kbCtrlF7)} SC",
        };
        var normal = shiftCtrl.Concat(new[] { "mod C", $"down {K(Keys.kbCtrlF8)} C", "mod CA", "mod C", "mod -", $"down {K(Keys.kbF5)} -" }).ToArray();
        var final = normal.Concat(new[]
        {
            $"down {K(Keys.kbF2)} -", $"up {K(Keys.kbF2)} -", "mod S", "text 'a' S", "text 'B' S", "mod -", "mod C", $"down {K(Keys.kbCtrlQ)} C", "mod -",
        }).ToArray();
        return new TestScenario(Keyboard, "Held modifiers and key state", "selftest/input",
                "Holds Shift, presses F5 twice, adds Ctrl, presses F7, releases Shift, presses F8 with Ctrl held, taps Alt, releases Ctrl, presses F5; key-down/key-up of F2; text with Shift; Ctrl+Q through WithModifiers.")
            .Kind(ScenarioKinds.Mixed).Grid(60, 20).Metadata("purpose", "Keyboard state self-test")
            .Setup(Install)
            .Checkpoint("initial", Exactly(), "no device event yet")
            .ModifierDown(TestModifiers.Shift, "evModifierChanged: Shift")
            .Key("kbF5", "F5 with Shift held = kbShiftF5 carrying Shift").Key("kbF5", "a second press, Shift still held")
            .ModifierDown(TestModifiers.Ctrl, "Ctrl joins Shift")
            .Key("kbF7", "Ctrl wins over Shift: kbCtrlF7 carrying Shift+Ctrl")
            .Checkpoint("shift-ctrl", Exactly(shiftCtrl), "exactly: Shift, two Shift+F5, Shift+Ctrl, Ctrl+F7")
            .ModifierUp(TestModifiers.Shift, "partial release: Ctrl stays held")
            .Key("kbF8", "kbCtrlF8 carrying Ctrl only")
            .ModifierDown(TestModifiers.Alt).ModifierUp(TestModifiers.Alt, "a modifier transition with no other key")
            .ModifierUp(TestModifiers.Ctrl, "back to the normal state")
            .Key("kbF5", "plain F5, no state")
            .Checkpoint("normal", Exactly(normal), "exactly the transitions and keys, nothing duplicated")
            .KeyDown("kbF2").KeyUp("kbF2", "evKeyUp with the press's identity")
            .WithModifiers(TestModifiers.Shift, s => s.Text("aB", "text carries the Shift state"))
            .WithModifiers(TestModifiers.Ctrl, s => s.Text("q", "Ctrl+letter is kbCtrlQ"))
            .ReleaseAll("nothing held: no event")
            .Checkpoint("final", Exactly(final), "exactly the complete keyboard sequence")
            .Completion(c => c.NoModalView && Log(c).SequenceEqual(final) ? null : "Unexpected events: " + string.Join(" | ", Log(c)));
    }

    private static TestScenario MouseScenario()
    {
        string[] clicks =
        [
            "move 10,5 none -",
            "move 12,6 none -", "mdown left 12,6 -", "mup 12,6 -",
            "mdown right 12,6 -", "mup 12,6 -",
            "move 14,6 none -", "mdown left 14,6 -", "mup 14,6 -", "mdown left 14,6 - dbl", "mup 14,6 -",
            "mdown left 14,6 -", "mup 14,6 -",
            "move 20,8 none -", "mdown left 20,8 -", "move 25,8 left -", "move 30,8 left -", "mup 30,8 -",
        ];
        string[] Wheel(TestTargetContext c)
        {
            string at = $"{c.Width - 10},{c.Height - 5}";
            return [.. clicks, $"move {at} none -", $"wheel down {at} -", $"wheel down {at} -", $"wheel down {at} -", $"wheel right {at} -", $"wheel right {at} -"];
        }
        string[] ShiftDrag(TestTargetContext c) => [.. Wheel(c), "mod S", "move 20,8 none S", "mdown left 20,8 S", "move 22,9 left S", "mup 22,9 S", "mod -"];
        string[] Held(TestTargetContext c) => [.. ShiftDrag(c), "mod A", "move 5,5 none A", "mdown left 5,5 A"];
        string[] Released(TestTargetContext c) => [.. Held(c), "mup 5,5 A", "mod -"];
        return new TestScenario(Mouse, "Mouse lifecycle", "selftest/input",
                "Move, left/right click, double click, a click that must not become a double click, a drag through a waypoint, vertical and horizontal wheel at an edge-anchored cell, a Shift drag; Alt and the left button are left held for completion to release.")
            .Kind(ScenarioKinds.Mixed).Grid(60, 20).Metadata("purpose", "Mouse lifecycle self-test")
            .Setup(Install)
            .MouseMove((10, 5))
            .Click((12, 6)).RightClick((12, 6), "no move: the pointer is already there")
            .DoubleClick((14, 6), "second press carries meDoubleClick explicitly")
            .Click((14, 6), "an immediate click at the same cell stays a single click")
            .Drag((20, 8), (30, 8), [(25, 8)])
            .Checkpoint("clicks", Exactly(clicks), "exact click, double-click and drag sequence; no evMouseAuto")
            .Wheel((-10, -5), -3, "edge-anchored cell: 10 columns from the right, 5 rows from the bottom")
            .HorizontalWheel((-10, -5), 2)
            .Checkpoint("wheel", Exactly(Wheel), "three wheel-down and two wheel-right events at the resolved cell")
            .ModifierDown(TestModifiers.Shift).Drag((20, 8), (22, 9)).ModifierUp(TestModifiers.Shift)
            .Checkpoint("shift-drag", Exactly(ShiftDrag), "every drag event carries Shift")
            .ModifierDown(TestModifiers.Alt).MouseDown((5, 5), TestMouseButton.Left, "left as held at the end on purpose")
            .Checkpoint("held", Exactly(Held), "Alt and the left button held")
            .Completion(c => Log(c).SequenceEqual(Released(c)) ? null : "Completion did not release through the pipeline: " + string.Join(" | ", Log(c)));
    }

    private static TPoint? Center(TView? view) => view?.owner is null ? null : view.MakeGlobal(new TPoint(view.size.x / 2, 0));

    private static TestScenario MixedScenario() =>
        new TestScenario(Mixed, "Keyboard, mouse and keyboard in one dialog", "selftest/input",
                "F3 opens the dialog, Tab moves to OK, a click on the input line moves the focus back, text is typed into it, a click on Cancel closes the dialog.")
            .Kind(ScenarioKinds.Dialog).Grid(60, 20).Metadata("purpose", "Mixed input self-test")
            .Point("name-input", c => App(c).DialogInput is { owner: not null } input ? input.MakeGlobal(new TPoint(1, 0)) : null)
            .Point("cancel-button", c => Center(App(c).DialogCancel))
            .Key("kbF3")
            .Key("kbTab")
            .Checkpoint("ok-focused", c => c.TopView is TDialog { current: TButton }, "keyboard moved the focus to OK", Tree,
                UiAssert.Focused("ok-focused", ViewSelector.ById("ok-button")))
            .Click(TestPoint.Named("name-input"), TestMouseButton.Left, "mouse focus")
            .Checkpoint("mouse-focus", c => c.TopView is TDialog { current: TInputLine }, "the click focused the input line", Tree,
                UiAssert.Focused("input-focused", ViewSelector.ById("name-input")), UiAssert.Modal("still-modal", ViewSelector.ById("sample-dialog")))
            .Text("X")
            .Checkpoint("typed", c => App(c).DialogInput?.Data.Contains('X') == true, "typed text reached the focused input line", Tree,
                UiAssert.ScreenContains("typed-text", "X"))
            .Click(TestPoint.Named("cancel-button"), TestMouseButton.Left, "the button's own mouse tracking runs to the release")
            .Checkpoint("closed", c => c.NoModalView, "Cancel closed the dialog; desktop restored", Tree, UiAssert.Modal("no-modal", null))
            .Completion(c => c.NoModalView && App(c).NoteCount == 0 ? null : "A window or modal view is still open.");

    // Deliberate failure: an off-screen mouse step while Shift and the left button are held. The step is refused before
    // any event exists and the failure names the step and the held state; the target is still shut down and cleaned up.
    private static TestScenario RefusedScenario() =>
        new TestScenario(Refused, "Off-screen mouse step", "selftest/failure",
                "Holds Shift and the left button, then clicks (199,99), which is outside every grid this scenario allows.")
            .Kind(ScenarioKinds.Mixed).Grid(60, 20, 160, 60).ExpectFailure()
            .Setup(Install)
            .ModifierDown(TestModifiers.Shift).MouseDown((30, 8))
            .Checkpoint("held", Exactly("mod S", "move 30,8 none S", "mdown left 30,8 S"), "Shift and the left button held")
            .Click((199, 99), TestMouseButton.Left, "refused: outside the screen")
            .Checkpoint("never")
            .Completion(_ => null);
}
