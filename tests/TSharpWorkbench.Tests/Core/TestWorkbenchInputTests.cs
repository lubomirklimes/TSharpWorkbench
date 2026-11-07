using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Extended input simulation. Planner tests check the exact events a step produces and every refusal without an event
// loop; the process-level tests run the sample target's input self-tests, whose checkpoints require the exact event
// sequence the application received through the real pipeline.
[Collection("NonParallel")]
public sealed class TestWorkbenchInputTests
{
    private const string KeyboardId = "selftest/input/keyboard-state", MouseId = "selftest/input/mouse-lifecycle",
        MixedId = "selftest/input/mixed-dialog", RefusedId = "selftest/failure/input-off-screen";

    private static InputPlanner.Plan Plan(string kind, string name, InputSnapshot? state = null, int width = 80, int height = 25,
        Func<string, TPoint?>? point = null) =>
        InputPlanner.Create(kind, name, state ?? InputSnapshot.Empty, width, height, point ?? (_ => null));

    private static InputSnapshot After(InputSnapshot state, params (string Kind, string Name)[] steps) =>
        steps.Aggregate(state, (s, step) => Plan(step.Kind, step.Name, s).After);

    // ── keyboard ───────────────────────────────────────────────────────────

    [Fact]
    public void HeldModifiersComposeKeysLikeTheKeyContractAndCarryTheirState()
    {
        // Nothing held: exactly the historical key event.
        var plain = Assert.Single(Plan("key", "kbF5").Events).Event;
        Assert.Equal(TestKeys.KeyDown(Keys.kbF5).keyDown.keyCode, plain.keyDown.keyCode);
        Assert.Equal(0u, plain.keyDown.controlKeyState);

        var shift = After(InputSnapshot.Empty, ("modifier", "shift-down"));
        var f5 = Assert.Single(Plan("key", "kbF5", shift).Events).Event;
        Assert.Equal((Keys.kbShiftF5, (uint)Keys.kbShift), (f5.keyDown.keyCode, f5.keyDown.controlKeyState));
        var both = After(shift, ("modifier", "ctrl-down"));
        Assert.Equal(Keys.kbCtrlF5, Plan("key", "kbF5", both).Events[0].Event.keyDown.keyCode); // Ctrl wins over Shift
        Assert.Equal((uint)(Keys.kbShift | Keys.kbCtrlShift), Plan("key", "kbF5", both).Events[0].Event.keyDown.controlKeyState);
        var alt = After(both, ("modifier", "alt-down"));
        Assert.Equal(Keys.kbAltF5, Plan("key", "kbF5", alt).Events[0].Event.keyDown.keyCode); // Alt wins over Ctrl
        Assert.Equal(Keys.kbShiftTab, Plan("key", "kbTab", shift).Events[0].Event.keyDown.keyCode);
        Assert.Equal(Keys.kbCtrlShiftIns, Plan("key", "kbIns", both).Events[0].Event.keyDown.keyCode);
        Assert.Equal(Keys.kbUp, Plan("key", "kbUp", shift).Events[0].Event.keyDown.keyCode); // Shift+Up keeps the identity, carries the state
        // A precomposed constant is never a held modifier.
        Assert.Contains("cannot be pressed with held Shift", Assert.Throws<InvalidOperationException>(() => Plan("key", "kbShiftF5", shift)).Message);
        // Ctrl/Alt + characters become their constants; Shift is state on text.
        var ctrl = After(InputSnapshot.Empty, ("modifier", "ctrl-down"));
        Assert.Equal(Keys.kbCtrlQ, Plan("text", "q", ctrl).Events[0].Event.keyDown.keyCode);
        Assert.Equal(Keys.kbAltF, Plan("text", "f", After(InputSnapshot.Empty, ("modifier", "alt-down"))).Events[0].Event.keyDown.keyCode);
        var typed = Plan("text", "aB", shift).Events.Select(e => e.Event).ToArray();
        Assert.Equal(new[] { "a", "B" }, typed.Select(e => e.keyDown.text));
        Assert.All(typed, e => Assert.Equal((uint)Keys.kbShift, e.keyDown.controlKeyState));
    }

    [Fact]
    public void ModifierTransitionsAreExplicitStatefulAndRefusedWhenInconsistent()
    {
        var down = Plan("modifier", "shift-down");
        var ev = Assert.Single(down.Events).Event;
        Assert.Equal((Events.evModifierChanged, (uint)Keys.kbShift), (ev.What, ev.Modifiers));
        Assert.Contains("already held", Assert.Throws<InvalidOperationException>(() => Plan("modifier", "shift-down", down.After)).Message);
        Assert.Contains("not held", Assert.Throws<InvalidOperationException>(() => Plan("modifier", "ctrl-up", down.After)).Message);
        var three = After(down.After, ("modifier", "ctrl-down"), ("modifier", "alt-down"));
        var partial = Plan("modifier", "shift-up", three);
        Assert.Equal((uint)(Keys.kbCtrlShift | Keys.kbAltShift), partial.Events[0].Event.Modifiers);
        Assert.Equal(TestModifiers.Ctrl | TestModifiers.Alt, partial.After.Modifiers);

        // Key down / up with the press's identity; release-all in device order (buttons, keys, modifiers Alt-Ctrl-Shift).
        var held = Plan("key-down", "kbF2", three);
        Assert.Equal(Keys.kbAltF2, held.Events[0].Event.keyDown.keyCode);
        Assert.Contains("already held", Assert.Throws<InvalidOperationException>(() => Plan("key-down", "kbF2", held.After)).Message);
        Assert.Contains("not held", Assert.Throws<InvalidOperationException>(() => Plan("key-up", "kbF3", held.After)).Message);
        var pressed = After(held.After, ("mouse", "down-left@3,4"));
        var release = Plan("release", "all", pressed);
        Assert.Equal(new[] { "left-up", "key-up kbF2", "alt-up", "ctrl-up", "shift-up" }, release.Events.Select(e => e.Label));
        Assert.Equal(new[] { Events.evMouseUp, Events.evKeyUp, Events.evModifierChanged, Events.evModifierChanged, Events.evModifierChanged },
            release.Events.Select(e => e.Event.What));
        Assert.Equal(Keys.kbAltF2, release.Events[1].Event.keyDown.keyCode);
        Assert.False(release.After.Holding);
        Assert.Empty(Plan("release", "all").Events); // nothing held: nothing sent
    }

    // ── mouse ──────────────────────────────────────────────────────────────

    [Fact]
    public void MouseStepsProduceTheDeviceEventSequenceWithExplicitDoubleClickAndHeldState()
    {
        var click = Plan("mouse", "click-left@12,6");
        Assert.Equal(new[] { Events.evMouseMove, Events.evMouseDown, Events.evMouseUp }, click.Events.Select(e => e.Event.What));
        Assert.All(click.Events, e => Assert.Equal(new TPoint(12, 6), e.Event.mouse.where));
        Assert.Equal((byte)Events.mbLeftButton, click.Events[1].Event.mouse.buttons);
        Assert.Equal(0, click.Events[2].Event.mouse.buttons);
        // Already there: no move. Right button is its own mask.
        var right = Plan("mouse", "click-right@12,6", click.After);
        Assert.Equal(new[] { Events.evMouseDown, Events.evMouseUp }, right.Events.Select(e => e.Event.What));
        Assert.Equal((byte)Events.mbRightButton, right.Events[0].Event.mouse.buttons);
        var dbl = Plan("mouse", "double-left@12,6", click.After).Events;
        Assert.Equal(new[] { false, false, true, false }, dbl.Select(e => e.Event.mouse.doubleClick));

        // Drag: the move carries the held button; release has an empty mask. Modifiers ride on every event.
        var shift = After(InputSnapshot.Empty, ("modifier", "shift-down"));
        var down = Plan("mouse", "down-left@20,8", shift);
        var move = Plan("mouse", "move@25,8", down.After);
        Assert.Equal((byte)Events.mbLeftButton, Assert.Single(move.Events).Event.mouse.buttons);
        Assert.Equal(Events.meMouseMoved, move.Events[0].Event.mouse.eventFlags);
        var up = Plan("mouse", "up-left@30,8", move.After);
        Assert.Equal(new[] { Events.evMouseMove, Events.evMouseUp }, up.Events.Select(e => e.Event.What));
        Assert.All(down.Events.Concat(move.Events).Concat(up.Events), e => Assert.Equal((uint)Keys.kbShift, e.Event.mouse.controlKeyState));
        Assert.Equal(0, up.After.Buttons);

        // Wheel: signed direction, repeat count, edge-anchored cell, held button carried.
        var wheel = Plan("mouse", "wheel-down@-10,-5*3", move.After).Events;
        Assert.Equal(new TPoint(70, 20), wheel[0].Event.mouse.where);
        Assert.Equal(3, wheel.Count(e => e.Event.What == Events.evMouseWheel && e.Event.mouse.eventFlags == Events.meWheelDown));
        Assert.All(wheel, e => Assert.Equal((byte)Events.mbLeftButton, e.Event.mouse.buttons));
        Assert.Equal(Events.meWheelRight, Plan("mouse", "wheel-right@0,0*2").Events[^1].Event.mouse.eventFlags);
    }

    [Fact]
    public void InvalidMouseStepsAreRefusedBeforeAnyEventWithClearReasons()
    {
        string Refused(string name, InputSnapshot? state = null, int w = 80, int h = 25, Func<string, TPoint?>? point = null) =>
            Assert.Throws<InvalidOperationException>(() => Plan("mouse", name, state, w, h, point)).Message;
        Assert.Equal("Cell (80,5) is outside the 80x25 screen (columns 0..79, rows 0..24).", Refused("click-left@80,5"));
        Assert.Contains("(-81,0) = (-1,0) is outside", Refused("move@-81,0"));
        Assert.Contains("outside the 60x20 screen", Refused("move@70,5", w: 60, h: 20)); // no assumption about 80x25
        Assert.Contains("not available", Refused("click-left@:gone"));
        Assert.Contains("resolved to (90,1)", Refused("click-left@:far", point: _ => new TPoint(90, 1)));
        var held = Plan("mouse", "down-left@3,3").After;
        Assert.Contains("button chords", Refused("click-right@3,3", held));
        Assert.Contains("not held", Refused("up-right@3,3", held));
        Assert.Contains("not held", Refused("up-left@3,3"));

        // Grammar: the wire, the catalog and registration agree.
        foreach (string bad in new[] { "click@1,1", "click-left@1", "click-left@1,1*2", "wheel-up@1,1*0", "wheel-up@1,1*21", "wheel-up@01,1",
                     "move@200,1", "move@1,100", "click-left@:Bad", "drag-left@1,1", "move 1,1" })
            Assert.False(InputSteps.IsValid("mouse", bad), bad);
        Assert.True(InputSteps.IsValid("mouse", "wheel-left@-1,-1*20"));
        Assert.False(InputSteps.IsValid("modifier", "win-down"));
        Assert.False(InputSteps.IsValid("text", "a\tb"));
        Assert.False(InputSteps.IsValid("key-down", "kbNoSuchKey"));
        var request = new Request(Protocol.Version, Protocol.Schema, "i", "r", "s", Protocol.Operations.Input, "x");
        Assert.True(Protocol.Authenticate(request with { Input = new("mouse", "click-left@:ok-button") }, "i", "s"));
        Assert.True(Protocol.Authenticate(request with { Input = new("modifier", "alt-up") }, "i", "s"));
        Assert.False(Protocol.Authenticate(request with { Input = new("mouse", "click-left@999,1") }, "i", "s"));
        Assert.False(Protocol.Authenticate(request with { Input = new("gesture", "x") }, "i", "s"));
    }

    [Fact]
    public void ScenarioDslRecordsReadableStepsAndValidatesRegistration()
    {
        var s = new TestScenario("x/input", "X", "x", "d")
            .Point("target", _ => new TPoint(1, 1))
            .WithModifiers(TestModifiers.Shift | TestModifiers.Ctrl, b => b.Key("kbF5").Click(TestPoint.Named("target")))
            .Drag((1, 2), (5, 2), [(3, 2)]).Wheel((-1, -2), -2).HorizontalWheel((0, 0), 1).DoubleClick((4, 4)).RightClick((4, 4))
            .KeyDown("kbF2").KeyUp("kbF2").Text("abc").ReleaseAll().Checkpoint("done");
        Assert.Equal(new[]
        {
            "modifier shift-down", "modifier ctrl-down", "key kbF5", "mouse click-left@:target", "modifier ctrl-up", "modifier shift-up",
            "mouse down-left@1,2", "mouse move@3,2", "mouse move@5,2", "mouse up-left@5,2", "mouse wheel-down@-1,-2*2", "mouse wheel-right@0,0",
            "mouse double-left@4,4", "mouse click-right@4,4", "key-down kbF2", "key-up kbF2", "text abc", "release all", "checkpoint done",
        }, s.Descriptor.Steps.Select(x => x.Kind + " " + x.Name));
        Assert.Throws<ArgumentException>(() => s.Click(TestPoint.Named("unknown")));
        Assert.Throws<ArgumentException>(() => s.Point("target", _ => null));
        Assert.Throws<ArgumentException>(() => s.Wheel((0, 0), 0));
        Assert.Throws<ArgumentException>(() => s.Wheel((0, 0), 21));
        Assert.Throws<ArgumentException>(() => s.ModifierDown(TestModifiers.None));
        Assert.Throws<ArgumentException>(() => s.Text(""));
        Assert.Throws<ArgumentException>(() => TestPoint.At(200, 0));
        Assert.Throws<ArgumentException>(() => TestPoint.Named("Bad Name"));
        // The controller accepts the catalog it produces.
        ScenarioRunner.ValidateCatalog([s.Descriptor]);
        var record = new InteractionRecord("mouse", "click-left@1,1", TestTargetHost.MouseRouting, 3, "settled", "at cell (1,1)", "modifiers=none");
        Assert.Equal(record, JsonSerializer.Deserialize<InteractionRecord>(JsonSerializer.Serialize(record)));
    }

    // ── real sample target ─────────────────────────────────────────────────

    [Fact]
    public async Task SampleTargetInputSelfTestsPassThroughTheRealPipelineAtStandardAndCustomGrids()
    {
        string root = Path.Combine(Path.GetTempPath(), "wb-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new ScenarioRunner();
            var catalog = await runner.DiscoverAsync(TestWorkbenchHostTests.SampleTarget);
            var selected = catalog.Where(s => s.Id is KeyboardId or MouseId or MixedId).ToArray();
            var batch = await runner.RunAsync(TestWorkbenchHostTests.SampleTarget, selected, new RunOptions(root, ["80x25", "140x40", "61x21"]));
            Assert.True(batch.ExitCode == 0, string.Join("\n", batch.Results.Select(r => $"{r.ScenarioId} {r.Size}: {r.Outcome} {r.Reason}")));
            Assert.Equal(9, batch.Results.Length);
            Assert.All(batch.Results, r => Assert.True(r.TargetExited && r.FixtureIntegrityVerified && r.CleanupSucceeded));
            // Completion released what the mouse scenario left held, through the pipeline, and said so.
            Assert.All(batch.Results.Where(r => r.ScenarioId == MouseId),
                r => Assert.Contains("Input still held at scenario end, released left-up, alt-up.", r.Reason));
            Assert.All(batch.Results.Where(r => r.ScenarioId != MouseId), r => Assert.DoesNotContain("released", r.Reason));
            var held = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, ArtifactNames.Stem(MouseId, "held", "80x25") + ".json"))).RootElement;
            var last = held.GetProperty("interactionsSincePreviousCheckpoint").EnumerateArray().Last();
            Assert.Equal(("mouse", "down-left@5,5", TestTargetHost.MouseRouting), (last.GetProperty("kind").GetString(), last.GetProperty("name").GetString(), last.GetProperty("routing").GetString()));
            Assert.Equal("modifiers=Alt; keys=none; buttons=left; pointer=5,5", last.GetProperty("inputState").GetString());
            Assert.Equal(Protocol.Acknowledgements.Settled, last.GetProperty("acknowledgement").GetString());
            var typed = JsonDocument.Parse(File.ReadAllText(Path.Combine(batch.RunRoot, ArtifactNames.Stem(KeyboardId, "shift-ctrl", "80x25") + ".json"))).RootElement
                .GetProperty("interactionsSincePreviousCheckpoint").EnumerateArray().ToArray();
            Assert.Contains(typed, i => i.GetProperty("name").GetString() == "kbF7" && i.GetProperty("detail").GetString() == "kbF7 with held Shift+Ctrl -> kbCtrlF7");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnOffScreenStepFailsWithTheStepAndHeldStateAndTheTargetIsStillCleanedUp()
    {
        string root = Path.Combine(Path.GetTempPath(), "wb-test-" + Guid.NewGuid().ToString("N"));
        int before = Directory.GetDirectories(Path.GetTempPath(), TempRoots.Prefix + "*").Length;
        try
        {
            var runner = new ScenarioRunner();
            var catalog = await runner.DiscoverAsync(TestWorkbenchHostTests.SampleTarget);
            Assert.DoesNotContain(ScenarioSelection.Resolve(catalog, [], "selftest", false), s => s.Id == RefusedId); // expected failure: never bulk
            var batch = await runner.RunAsync(TestWorkbenchHostTests.SampleTarget, ScenarioSelection.Resolve(catalog, [RefusedId], null, false),
                new RunOptions(root, ["80x25"]));
            var result = Assert.Single(batch.Results);
            Assert.Equal(ScenarioOutcomes.Error, result.Outcome);
            Assert.Contains("Cell (199,99) is outside the 80x25 screen", result.Reason);
            Assert.Contains("[last input: mouse click-left@199,99 (refused before any event); input state: modifiers=Shift; keys=none; buttons=left; pointer=30,8]", result.Reason);
            Assert.Contains("step 4 (mouse click-left@199,99)", result.Reason);
            Assert.Single(result.Checkpoints); // "held" was captured before the refusal
            Assert.True(result.CleanupSucceeded);
            Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), TempRoots.Prefix + "*").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
