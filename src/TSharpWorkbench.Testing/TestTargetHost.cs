using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using TSharpVision.Constants;
using TSharpVision.Drivers;

namespace TSharpWorkbench.Testing;

/// <summary>Live state handed to application callbacks. Every callback runs on the UI thread.</summary>
public sealed class TestTargetContext
{
    private readonly NullDriver? logicalDriver;
    internal TestTargetContext(TestScenario scenario, NullDriver? driver, int width, int height, string workingDirectory, string stateDirectory)
    {
        Scenario = scenario; logicalDriver = driver; Width = InitialWidth = width; Height = InitialHeight = height;
        WorkingDirectory = workingDirectory; StateDirectory = stateDirectory;
    }
    public TestScenario Scenario { get; }
    public TApplication Application { get; internal set; } = null!;
    /// <summary>The headless driver behind the host's input tracking. Host-submitted keys do not pass through its queue.</summary>
    public NullDriver Driver => logicalDriver ?? throw new InvalidOperationException("This action requires NullDriver and is unsupported in native execution.");
    /// <summary>Current logical grid (changes after a resize step).</summary>
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public int InitialWidth { get; }
    public int InitialHeight { get; }
    /// <summary>Controller-owned empty disposable directory; the process working directory.</summary>
    public string WorkingDirectory { get; }
    /// <summary>Controller-owned empty disposable directory for any state the application would otherwise persist.</summary>
    public string StateDirectory { get; }
    /// <summary>The modal top view, or the application itself when no dialog or menu is executing.</summary>
    public TView? TopView => Application.TopView();
    public bool NoModalView => TopView is null || ReferenceEquals(TopView, Application);
    /// <summary>Gives a view a stable diagnostic identity for inspection and selectors (see <see cref="ViewTreeInspector.Identify"/>).</summary>
    public void Identify(TView view, string id) => ViewTreeInspector.Identify(view, id);
}

/// <summary>A target-registered scenario: a descriptor plus the delegates its names refer to.
/// Only these registered names can be executed; nothing executable crosses the wire.</summary>
public sealed class TestScenario
{
    private readonly List<ScenarioStep> steps = [];
    private readonly Dictionary<string, string> metadata = new(StringComparer.Ordinal);
    private ScenarioGrid grid;
    private string kind = ScenarioKinds.Screen, expectedResult = ScenarioOutcomes.Passed;
    private ScenarioSafety safety = ScenarioSafety.None;
    private string? conditions, fixtureRevision;
    internal readonly Dictionary<string, Action<TestTargetContext>> Actions = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, (Func<TestTargetContext, bool> Expect, string Note)> Expectations = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Func<TestTargetContext, TPoint?>> Points = new(StringComparer.Ordinal);
    internal Func<TestTargetContext, bool>? ReadyPredicate { get; private set; }
    internal Func<TestTargetContext, string?>? CompletionCheck { get; private set; }
    internal Action<TestTargetContext>? SetupAction { get; private set; }
    internal Action<TestTargetContext>? CleanupAction { get; private set; }

    /// <param name="sizes">Default grids (the standard 80x25 and 140x40 when empty). Any grid inside the scenario's
    /// range (<see cref="Grid"/>) can still be requested.</param>
    public TestScenario(string id, string name, string group, string description, params string[] sizes)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) throw new ArgumentException("Invalid scenario ID.");
        Id = id; Name = name; Group = group; Description = description;
        foreach (string size in sizes) ScenarioSelection.ParseSize(size);
        grid = ScenarioGrid.Standard with { Defaults = sizes.Length == 0 ? ScenarioSelection.Sizes.ToArray() : sizes };
    }
    public string Id { get; }
    public string Name { get; }
    public string Group { get; }
    public string Description { get; }

    public ScenarioDescriptor Descriptor => new(Id, Name, Description, Group, grid, steps.ToArray(), kind, safety,
        metadata.Count == 0 ? null : new(metadata, StringComparer.Ordinal), expectedResult, conditions)
        {
            Adapters = new(Actions.Keys.Order(StringComparer.Ordinal).ToArray(), Points.Keys.Order(StringComparer.Ordinal).ToArray()),
            Contract = fixtureRevision is null ? null : new(FixtureContract.FixtureOnlyRole, FixtureContract.CurrentVersion, fixtureRevision,
                Expectations.Keys.Order(StringComparer.Ordinal).ToArray()),
        };

    /// <summary>Whether this registration is fixture-only (<see cref="FixtureOnly"/>).</summary>
    public bool IsFixtureOnly => fixtureRevision is not null;

    /// <summary>Makes this registration an explicit, non-runnable fixture. It has no scenario steps; its executable
    /// behaviour (setup, readiness, registered actions, points and checkpoint expectations, completion, cleanup) is driven only
    /// by external scenarios bound to it, and every checkpoint they author must name a registered expectation. It is advertised
    /// in the raw catalog, never in runnable discovery. <paramref name="reviewedRevision"/> is the application's reviewed
    /// contract revision (1..128 characters, no control characters); it is part of the contract fingerprint.</summary>
    public TestScenario FixtureOnly(string reviewedRevision)
    {
        if (string.IsNullOrWhiteSpace(reviewedRevision) || reviewedRevision.Length > 128 || reviewedRevision.Any(char.IsControl))
            throw new ArgumentException("Fixture-only registration needs a nonempty reviewed contract revision (at most 128 characters).");
        if (steps.Count > 0) throw new InvalidOperationException("A fixture-only registration has no scenario steps; register actions, points and expectations instead.");
        if (expectedResult != ScenarioOutcomes.Passed) throw new InvalidOperationException("Expected outcomes belong to the external scenarios bound to a fixture-only registration.");
        fixtureRevision = reviewedRevision; return this;
    }

    private void Step(ScenarioStep step)
    {
        if (IsFixtureOnly) throw new InvalidOperationException($"Fixture-only registration '{Id}' cannot script {step.Kind} steps; external scenarios own the steps.");
        steps.Add(step);
    }

    /// <summary>Restricts the grid range (inclusive). Defaults outside the range are rejected.</summary>
    public TestScenario Grid(int minWidth, int minHeight, int maxWidth = GridLimits.MaxWidth, int maxHeight = GridLimits.MaxHeight)
    {
        var range = grid with { MinWidth = minWidth, MinHeight = minHeight, MaxWidth = maxWidth, MaxHeight = maxHeight };
        if (minWidth < GridLimits.MinWidth || minHeight < GridLimits.MinHeight || maxWidth > GridLimits.MaxWidth || maxHeight > GridLimits.MaxHeight
            || minWidth > maxWidth || minHeight > maxHeight || range.Defaults.Any(d => !range.Supports(d)))
            throw new ArgumentException("Invalid grid range.");
        grid = range; return this;
    }

    /// <summary>Scenario kind for listing and reports; see <see cref="ScenarioKinds"/>.</summary>
    public TestScenario Kind(string value)
    {
        if (!ScenarioKinds.All.Contains(value)) throw new ArgumentException("Unknown scenario kind: " + value);
        kind = value; return this;
    }

    /// <summary>Declares external effects; the controller refuses capabilities a run did not allow. Not a sandbox.</summary>
    public TestScenario Safety(ScenarioSafety value) { safety = value; return this; }

    /// <summary>Application-owned metadata (at most 32 entries, keys ≤ 64 and values ≤ 512 characters).</summary>
    public TestScenario Metadata(string key, string value)
    {
        if (!Protocol.IsValidName(key) || value.Length > 512 || (metadata.Count >= 32 && !metadata.ContainsKey(key))) throw new ArgumentException("Invalid metadata entry.");
        metadata[key] = value; return this;
    }

    public TestScenario Conditions(string text) { conditions = text; return this; }

    /// <summary>Marks a deliberate failure demonstration: excluded from bulk selections unless explicitly requested.</summary>
    public TestScenario ExpectFailure()
    {
        if (IsFixtureOnly) throw new InvalidOperationException("Expected outcomes belong to the external scenarios bound to a fixture-only registration.");
        expectedResult = ScenarioOutcomes.Failed; return this;
    }

    /// <summary>Scenario setup on the UI thread after the application was created and before the event loop starts.
    /// Prepare fixtures under <see cref="TestTargetContext.WorkingDirectory"/> / <see cref="TestTargetContext.StateDirectory"/>.</summary>
    public TestScenario Setup(Action<TestTargetContext> setup) { SetupAction = setup; return this; }

    /// <summary>Scenario cleanup on the UI thread after the event loop ended: success, failure, cancellation or controller loss.
    /// It runs inside the target, whose lifetime the controller bounds (a hung cleanup is ended by process termination).</summary>
    public TestScenario Cleanup(Action<TestTargetContext> cleanup) { CleanupAction = cleanup; return this; }

    /// <summary>Queued logical key press, by its <see cref="Keys"/> constant name (for example <c>kbAltF</c>). With nothing
    /// held it is the historical key-down of exactly that constant (no release, no modifier state). While modifiers are held
    /// (<see cref="ModifierDown"/>) name the unmodified key: it is composed with them as a driver would (F5 with Shift held
    /// is <c>kbShiftF5</c> carrying the Shift state) — a precomposed constant is never a held modifier.</summary>
    public TestScenario Key(string keyName, string? note = null)
    {
        if (!TestKeys.TryResolve(keyName, out _)) throw new ArgumentException("Unknown key constant: " + keyName);
        Step(new(ScenarioStep.Key, keyName, note)); return this;
    }

    // ── extended input ─────────────────────────────────────────────────

    /// <summary>Presses and holds a non-modifier key (key-down, composed with held modifiers) until <see cref="KeyUp"/>.</summary>
    public TestScenario KeyDown(string keyName, string? note = null) => Input(InputSteps.KeyDown, keyName, note);

    /// <summary>Releases a key held by <see cref="KeyDown"/>: an <c>evKeyUp</c> with the press's identity and the current modifiers.</summary>
    public TestScenario KeyUp(string keyName, string? note = null) => Input(InputSteps.KeyUp, keyName, note);

    /// <summary>Types characters, one key-down each (1..48, no control characters). Held modifiers apply as on a keyboard:
    /// Shift is carried as state, Ctrl or Alt turn letters and digits into their Ctrl/Alt keys.</summary>
    public TestScenario Text(string text, string? note = null) => Input(InputSteps.Text, text, note);

    /// <summary>Presses and holds each modifier in <paramref name="modifiers"/> (Shift, Ctrl, Alt order), one
    /// <c>evModifierChanged</c> step each. They stay held across every following step until released.</summary>
    public TestScenario ModifierDown(TestModifiers modifiers, string? note = null)
    {
        foreach (var m in Single(modifiers)) Input(InputSteps.Modifier, InputSteps.ModifierName(m, true), note);
        return this;
    }

    /// <summary>Releases each modifier in <paramref name="modifiers"/> (Alt, Ctrl, Shift order), one step each; others stay held.</summary>
    public TestScenario ModifierUp(TestModifiers modifiers, string? note = null)
    {
        foreach (var m in Single(modifiers).Reverse()) Input(InputSteps.Modifier, InputSteps.ModifierName(m, false), note);
        return this;
    }

    /// <summary>Holds <paramref name="modifiers"/> for the steps <paramref name="body"/> adds, then releases them.</summary>
    public TestScenario WithModifiers(TestModifiers modifiers, Action<TestScenario> body, string? note = null)
    {
        ModifierDown(modifiers, note); body(this); return ModifierUp(modifiers, note);
    }

    /// <summary>Releases every held mouse button, key and modifier through the event pipeline (buttons, keys, then modifiers).
    /// Completion does the same for anything a scenario left held, and reports it.</summary>
    public TestScenario ReleaseAll(string? note = null) => Input(InputSteps.Release, InputSteps.ReleaseAll, note);

    /// <summary>Registers a named screen cell resolved by the application when a mouse step uses it (UI thread; null means
    /// "not on screen now", which fails that step). Register before the steps that use it.</summary>
    public TestScenario Point(string name, Func<TestTargetContext, TPoint?> resolve)
    {
        if (!TestPoint.IsPointName(name) || !Points.TryAdd(name, resolve)) throw new ArgumentException("Invalid or duplicate point name: " + name);
        return this;
    }

    /// <summary>Registers a named dynamic point without scripting a step (the contract of <see cref="Point"/>, which never
    /// scripts one either). External scenarios use it as <c>@:name</c> in a mouse step.</summary>
    public TestScenario RegisterPoint(string name, Func<TestTargetContext, TPoint?> resolve) =>
        resolve is null ? throw new ArgumentException("Invalid or duplicate point name: " + name) : Point(name, resolve);

    /// <summary>Moves the pointer (an <c>evMouseMove</c> carrying held buttons and modifiers). No event when already there.</summary>
    public TestScenario MouseMove(TestPoint at, string? note = null) => Mouse(InputSteps.FormatMouse("move", at), at, note);

    /// <summary>Moves to <paramref name="at"/> if needed and presses <paramref name="button"/>, which stays held.</summary>
    public TestScenario MouseDown(TestPoint at, string? note = null) => MouseDown(at, TestMouseButton.Left, note);
    public TestScenario MouseDown(TestPoint at, TestMouseButton button, string? note = null) =>
        Mouse(InputSteps.FormatMouse("down", at, button), at, note);

    /// <summary>Moves to <paramref name="at"/> if needed (a drag while the button is held) and releases <paramref name="button"/>.</summary>
    public TestScenario MouseUp(TestPoint at, string? note = null) => MouseUp(at, TestMouseButton.Left, note);
    public TestScenario MouseUp(TestPoint at, TestMouseButton button, string? note = null) =>
        Mouse(InputSteps.FormatMouse("up", at, button), at, note);

    /// <summary>A complete click: move if needed, press, release — each event delivered once the previous one settled.</summary>
    public TestScenario Click(TestPoint at, string? note = null) => Click(at, TestMouseButton.Left, note);
    public TestScenario Click(TestPoint at, TestMouseButton button, string? note = null) =>
        Mouse(InputSteps.FormatMouse("click", at, button), at, note);

    public TestScenario RightClick(TestPoint at, string? note = null) => Click(at, TestMouseButton.Right, note);

    /// <summary>Two clicks at one cell; the second press carries <c>meDoubleClick</c> explicitly (no wall-clock window).</summary>
    public TestScenario DoubleClick(TestPoint at, string? note = null) => DoubleClick(at, TestMouseButton.Left, note);
    public TestScenario DoubleClick(TestPoint at, TestMouseButton button, string? note = null) =>
        Mouse(InputSteps.FormatMouse("double", at, button), at, note);

    /// <summary>Press at <paramref name="from"/>, move through <paramref name="via"/> to <paramref name="to"/> with the button
    /// held, release at <paramref name="to"/>: one step per event, in that order.</summary>
    public TestScenario Drag(TestPoint from, TestPoint to, TestPoint[]? via = null, TestMouseButton button = TestMouseButton.Left, string? note = null)
    {
        MouseDown(from, button, note);
        foreach (var point in via ?? []) MouseMove(point, note);
        MouseMove(to, note);
        return MouseUp(to, button, note);
    }

    /// <summary>Vertical wheel over <paramref name="at"/>: positive <paramref name="delta"/> turns it up (away from the user),
    /// negative down; |delta| = 1..20 wheel events. Held buttons and modifiers are carried.</summary>
    public TestScenario Wheel(TestPoint at, int delta, string? note = null) => WheelStep(at, delta, delta > 0 ? "up" : "down", note);

    /// <summary>Horizontal wheel over <paramref name="at"/>: positive <paramref name="delta"/> right, negative left.</summary>
    public TestScenario HorizontalWheel(TestPoint at, int delta, string? note = null) => WheelStep(at, delta, delta > 0 ? "right" : "left", note);

    private TestScenario WheelStep(TestPoint at, int delta, string direction, string? note)
    {
        if (delta == 0 || Math.Abs(delta) > InputSteps.MaximumWheelCount) throw new ArgumentException($"Wheel delta must be ±1..{InputSteps.MaximumWheelCount}.");
        return Mouse(InputSteps.FormatMouse("wheel", at, null, direction, Math.Abs(delta)), at, note);
    }

    private TestScenario Mouse(string name, TestPoint at, string? note)
    {
        if (at.Name is { } point && !Points.ContainsKey(point)) throw new ArgumentException("Unregistered point: " + point + " (register it with Point first).");
        return Input(InputSteps.Mouse, name, note);
    }

    private TestScenario Input(string kind, string name, string? note)
    {
        if (!InputSteps.IsValid(kind, name)) throw new ArgumentException($"Invalid {kind} step: {name}");
        Step(new(kind, name, note)); return this;
    }

    private static IEnumerable<TestModifiers> Single(TestModifiers modifiers)
    {
        if (modifiers == TestModifiers.None || (modifiers & ~(TestModifiers.Shift | TestModifiers.Ctrl | TestModifiers.Alt)) != 0)
            throw new ArgumentException("Name one or more of Shift, Ctrl, Alt.");
        return new[] { TestModifiers.Shift, TestModifiers.Ctrl, TestModifiers.Alt }.Where(m => (modifiers & m) != 0);
    }

    /// <summary>Registers and scripts a direct application action. It is reported as an action, never as user input.</summary>
    public TestScenario Action(string name, Action<TestTargetContext> action, string? note = null)
    {
        if (IsFixtureOnly) throw new InvalidOperationException($"Fixture-only registration '{Id}' cannot script action steps; use RegisterAction.");
        if (!Protocol.IsValidName(name) || !Actions.TryAdd(name, action)) throw new ArgumentException("Invalid or duplicate action name: " + name);
        steps.Add(new(ScenarioStep.Action, name, note)); return this;
    }

    /// <summary>Registers a named direct application action without scripting a step. External scenarios invoke it with an
    /// <c>action</c> step (<c>"adapter": name</c>); it is reported as an action, never as user input.</summary>
    public TestScenario RegisterAction(string name, Action<TestTargetContext> action)
    {
        if (!Protocol.IsValidName(name) || action is null || !Actions.TryAdd(name, action)) throw new ArgumentException("Invalid or duplicate action name: " + name);
        return this;
    }

    /// <summary>Registers a named checkpoint expectation without scripting a checkpoint step. A checkpoint of that name (an
    /// external scenario's, or a later <see cref="Checkpoint"/> step without its own predicate) captures only after
    /// <paramref name="expect"/> holds within bounded observations. Fixture-only registrations advertise these names, and
    /// external checkpoints bound to them must use one.</summary>
    public TestScenario RegisterCheckpointExpectation(string name, Func<TestTargetContext, bool> expect, string? note = null)
    {
        if (!Protocol.IsValidName(name) || expect is null || Expectations.ContainsKey(name))
            throw new ArgumentException("Invalid or duplicate checkpoint expectation name: " + name);
        Expectations.Add(name, (expect, note ?? name)); return this;
    }

    /// <summary>Scripts a real logical resize: the NullDriver changes size and the framework processes <c>cmScreenResized</c>.</summary>
    public TestScenario Resize(int width, int height, string? note = null)
    {
        string size = ScenarioSelection.Format(width, height);
        if (!grid.Supports(width, height)) throw new ArgumentException($"Resize target {size} is outside the scenario grid range.");
        Step(new(ScenarioStep.Resize, size, note)); return this;
    }

    /// <summary>Scripts a named whole-screen checkpoint. <paramref name="expect"/> must hold, within bounded observations, before
    /// the stable screen is captured. <paramref name="evidence"/> requests semantic inspection (<see cref="Protocol.EvidenceKinds"/>);
    /// evidence needed by <paramref name="assertions"/> is requested automatically.</summary>
    public TestScenario Checkpoint(string name, Func<TestTargetContext, bool>? expect = null, string? note = null,
        string[]? evidence = null, params AssertionSpec[] assertions)
    {
        if (IsFixtureOnly) throw new InvalidOperationException($"Fixture-only registration '{Id}' cannot script checkpoint steps; use RegisterCheckpointExpectation.");
        if (!Protocol.IsValidName(name) || steps.Any(s => s.Kind == ScenarioStep.Checkpoint && s.Name == name))
            throw new ArgumentException("Invalid or duplicate checkpoint name: " + name);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (assertions.Any(a => !Protocol.IsValidName(a.Id) || !ids.Add(a.Id) || !AssertionKinds.All.Contains(a.Kind)
            || a.Severity is not (AssertionSeverity.Error or AssertionSeverity.Warning)))
            throw new ArgumentException("Invalid, duplicate or unknown assertion in checkpoint " + name);
        var requested = (evidence ?? []).Concat(assertions.SelectMany(a => a.RequiredEvidence())).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Any(e => !Protocol.EvidenceKinds.All.Contains(e))) throw new ArgumentException("Unknown evidence kind.");
        if (expect is not null) Expectations[name] = (expect, note ?? name);
        steps.Add(new(ScenarioStep.Checkpoint, name, note, requested.Length == 0 ? null : requested, assertions.Length == 0 ? null : assertions));
        return this;
    }

    /// <summary>Scenario readiness, evaluated before Start is accepted.</summary>
    public TestScenario Ready(Func<TestTargetContext, bool> ready) { ReadyPredicate = ready; return this; }

    /// <summary>Completion verification; return null when cleanup is verified, otherwise a failure reason.</summary>
    public TestScenario Completion(Func<TestTargetContext, string?> check) { CompletionCheck = check; return this; }
}

/// <summary>What an application supplies to become a diagnostic target.</summary>
public sealed class TestTargetDefinition
{
    public required IReadOnlyList<TestScenario> Scenarios { get; init; }
    /// <summary>Creates the real application. The NullDriver is installed already.</summary>
    public required Func<TestTargetContext, TApplication> CreateApplication { get; init; }
    /// <summary>Application readiness, in addition to the scenario's own; default: the event loop runs.</summary>
    public Func<TestTargetContext, bool>? Ready { get; init; }
    /// <summary>Application teardown on the UI thread after the event loop ends (success or failure).</summary>
    public Action<TestTargetContext>? Teardown { get; init; }
}

/// <summary>
/// Framework-neutral target side of the diagnostic protocol. The controller launches the target with
/// <see cref="ActivationArgument"/> and authenticated environment; the host installs a NullDriver (behind a testing-side
/// input tracker), creates the application, serves the pipe, and marshals every operation onto the UI thread through
/// posted events, which every event loop delivers (including the nested loops of menus and modal dialogs).
/// </summary>
public static class TestTargetHost
{
    public const string ActivationArgument = "--tsharpvision-test-target";
    public const string InstanceVariable = "TSHARPVISION_TESTING_INSTANCE", SecretVariable = "TSHARPVISION_TESTING_SECRET",
        ScenarioVariable = "TSHARPVISION_TESTING_SCENARIO", SizeVariable = "TSHARPVISION_TESTING_SIZE",
        StateVariable = "TSHARPVISION_TESTING_STATE", OperationLimitVariable = "TSHARPVISION_TESTING_OPERATION_MS",
        LifetimeVariable = "TSHARPVISION_TESTING_LIFETIME_S";
    /// <summary>Set to 1 by the controller for a Proxy recording session (longer lifetime bound, record operations).</summary>
    public const string RecordVariable = "TSHARPVISION_TESTING_RECORD";
    /// <summary>Bound for a whole target session. Default 40 s; the controller may set 5..600 s.</summary>
    internal static TimeSpan Lifetime { get; private set; } = TimeSpan.FromSeconds(40);
    /// <summary>Bound for one operation (readiness, input acknowledgement, checkpoint stabilization). Default 10 s;
    /// the controller may lower or raise it within 100 ms..30 s.</summary>
    internal static TimeSpan OperationLimit { get; private set; } = TimeSpan.FromSeconds(10);
    internal const int StableObservations = 3, MaximumObservations = 400;
    internal const string KeyRouting = "TestInputDriver(NullDriver) -> TScreen.GetEvent -> TProgram.GetEvent",
        ActionRouting = "Direct scenario action (UI thread; not user input)",
        ResizeRouting = "NullDriver.SimulateResize -> cmScreenResized -> TProgram.HandleScreenResize",
        MouseRouting = "TestInputDriver.PumpMessages -> TEventQueue (click classification) -> TScreen.GetEvent -> TProgram.GetEvent",
        ReleaseRouting = "TestInputDriver (key path and TEventQueue, as each event requires) -> TProgram.GetEvent";

    internal static string RoutingOf(string kind) => kind switch
    {
        ScenarioStep.Key or InputSteps.KeyDown or InputSteps.KeyUp or InputSteps.Text or InputSteps.Modifier => KeyRouting,
        InputSteps.Mouse => MouseRouting,
        InputSteps.Release => ReleaseRouting,
        ScenarioStep.Resize => ResizeRouting,
        _ => ActionRouting,
    };

    public static bool IsActivated(IReadOnlyList<string> args) => args.Count == 1 && args[0] == ActivationArgument;

    /// <summary>Runs the target. Returns 0 on clean shutdown, 2 for refused activation, 1 for failures.</summary>
    public static int Run(TestTargetDefinition definition)
    {
        string instance = Environment.GetEnvironmentVariable(InstanceVariable) ?? "";
        string secret = Environment.GetEnvironmentVariable(SecretVariable) ?? "";
        Environment.SetEnvironmentVariable(SecretVariable, null);
        if (!Guid.TryParseExact(instance, "N", out _) || secret.Length != 64) return 2;
        ScenarioDescriptor[] catalog;
        try { catalog = definition.Scenarios.Select(s => s.Descriptor).ToArray(); ScenarioRunner.ValidateCatalog(catalog); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { Console.Error.WriteLine("Invalid scenario catalog: " + ex.Message); return 2; }
        string? scenarioId = Environment.GetEnvironmentVariable(ScenarioVariable);
        string size = Environment.GetEnvironmentVariable(SizeVariable) ?? "80x25";
        if (!ScenarioSelection.TryParseSize(size, out int width, out int height)) return 2;
        if (Environment.GetEnvironmentVariable(OperationLimitVariable) is { } limit)
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out int ms) || ms is < 100 or > 30000) return 2;
            OperationLimit = TimeSpan.FromMilliseconds(ms);
        }
        if (Environment.GetEnvironmentVariable(LifetimeVariable) is { } life)
        {
            if (!int.TryParse(life, NumberStyles.None, CultureInfo.InvariantCulture, out int s) || s is < 5 or > 600) return 2;
            Lifetime = TimeSpan.FromSeconds(s);
        }
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        // Discovery: authenticated catalog only. No application, driver or fixture is touched.
        if (string.IsNullOrEmpty(scenarioId))
        {
            using var discovery = new Server(instance, secret, catalog, null);
            return discovery.ServeDiscoveryAsync().GetAwaiter().GetResult();
        }
        var scenario = definition.Scenarios.FirstOrDefault(s => s.Id == scenarioId);
        string state = Path.GetFullPath(Environment.GetEnvironmentVariable(StateVariable) ?? "");
        string fixture = Path.GetFullPath(Environment.CurrentDirectory);
        if (scenario is null || !scenario.Descriptor.Grid.Supports(width, height) || !Directory.Exists(state)
            || Directory.EnumerateFileSystemEntries(state).Any() || Directory.EnumerateFileSystemEntries(fixture).Any()) return 2;

        string? mode = Environment.GetEnvironmentVariable(NativeExecution.Variable);
        if (mode is not null && !NativeExecution.Supports(scenario.Descriptor, mode))
            throw new InvalidOperationException("Scenario is unsupported for native execution; no logical fallback performed.");
        using var native = mode is null ? null : NativeExecution.Create(mode, width, height);
        IDriver driver = native?.Driver ?? new NullDriver((ushort)width, (ushort)height);
        var input = new TestInputDriver(driver);
        if (native is null) input.Initialize();
        TDisplay.driver = input;
        var context = new TestTargetContext(scenario, driver as NullDriver, width, height, fixture, state);
        try { context.Application = definition.CreateApplication(context); scenario.SetupAction?.Invoke(context); }
        catch (Exception ex) { Console.Error.WriteLine("Test target application creation or scenario setup failed: " + ex.GetType().Name + ": " + ex.Message); return 1; }
        // Simulated double clicks are explicit (meDoubleClick on the second press). The framework's wall-clock window is
        // closed for this target process, so two separate clicks at one cell never merge however fast the host runs.
        if (native is null) TEventQueue.DoubleDelay = int.MinValue;
        using var server = new Server(instance, secret, catalog, new Session(definition, context, input) { Native = native });
        context.Application.Insert(server.Dispatcher);
        server.Start();
        int code;
        bool cleaned = true;
        try { code = AppLifecycleGuard.Run(context.Application); }
        finally
        {
            try { scenario.CleanupAction?.Invoke(context); }
            catch (Exception ex) { cleaned = false; Console.Error.WriteLine("Scenario cleanup failed: " + ex.GetType().Name + ": " + ex.Message); }
            try { definition.Teardown?.Invoke(context); }
            catch (Exception ex) { cleaned = false; Console.Error.WriteLine("Application teardown failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
        return server.CleanShutdown && code == 0 && cleaned ? 0 : 1;
    }

    internal static TargetIdentity Identity(string driver, int width, int height)
    {
        var entry = Assembly.GetEntryAssembly();
        static string? Informational(Assembly? a) => a?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new(entry?.GetName().Name ?? "unknown", Informational(entry) ?? entry?.GetName().Version?.ToString(),
            entry?.ManifestModule.ModuleVersionId.ToString(), Informational(typeof(TView).Assembly),
            Informational(typeof(TestTargetHost).Assembly), driver, width, height);
    }

    /// <summary>Scenario execution state. Checkpoints is touched only by the serial server loop; the rest on the UI thread.</summary>
    internal sealed class Session(TestTargetDefinition definition, TestTargetContext context, TestInputDriver input)
    {
        internal INativeExecution? Native;
        internal TestTargetDefinition Definition { get; } = definition;
        internal TestTargetContext Context { get; } = context;
        internal TestInputDriver Input { get; } = input;
        internal readonly HashSet<string> Checkpoints = new(StringComparer.Ordinal);
        internal Exception? ActionError;
        internal bool Started, Completed;
        internal long Sequence;
        /// <summary>Logical state of the simulated keyboard and mouse. Each execution is a fresh process, so it starts empty.</summary>
        internal InputSnapshot InputState = InputSnapshot.Empty;
        /// <summary>The last input step routed (kind, name, sequence), for failure reports.</summary>
        internal string? LastInput;
    }

    private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }

    /// <summary>Hidden, unselectable view that receives posted work. Posted events reach it from any loop.</summary>
    internal sealed class DispatchView : TView
    {
        internal const ushort Command = 0x7df2;
        internal DispatchView() : base(new TRect(0, 0, 0, 0))
        { options = 0; eventMask = Events.evCommand; state &= unchecked((ushort)~Views.sfVisible); }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evCommand && ev.message.command == Command && ev.message.infoPtr is Work work)
            { ClearEvent(ref ev); work.Action(); }
            else base.HandleEvent(ref ev);
        }
    }

    internal sealed class Server : IDisposable
    {
        private readonly string instance, secret;
        private readonly ScenarioDescriptor[] catalog;
        private readonly Session? session;
        private readonly CancellationTokenSource lifetime = new(Lifetime);
        private readonly NamedPipeServerStream pipe;
        private readonly HashSet<string> ids = new(StringComparer.Ordinal);
        private Task? task;
        internal DispatchView Dispatcher { get; } = new();
        internal bool CleanShutdown { get; private set; }

        internal Server(string instance, string secret, ScenarioDescriptor[] catalog, Session? session)
        {
            this.instance = instance; this.secret = secret; this.catalog = catalog; this.session = session;
            pipe = LocalPipe.Create("tsharp-diagnostics-" + instance);
        }

        internal void Start() => task = Task.Run(ServeAsync);

        internal async Task<int> ServeDiscoveryAsync()
        {
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                while (true)
                {
                    var request = await Protocol.ReadAsync<Request>(pipe, lifetime.Token);
                    if (!Accept(request)) { await Reject(request, "Authentication/protocol rejected."); return 2; }
                    if (request.Operation == Protocol.Operations.Handshake)
                        await Send(new Reply(request.Id, Protocol.States.Ready, Protocol.Schema, Target: Identity("none", 0, 0), Scenarios: catalog));
                    else if (request.Operation == Protocol.Operations.Shutdown)
                    { await Send(new Reply(request.Id, Protocol.States.ShutdownAccepted, Protocol.Schema)); return 0; }
                    else await Reject(request, "Discovery session accepts only Handshake and Shutdown.");
                }
            }
            catch (Exception) { return 1; }
        }

        private bool Accept(Request request) => Protocol.Authenticate(request, instance, secret) && ids.Add(request.Id) && ids.Count <= 4096;
        private Task Send(Reply reply) => Protocol.WriteAsync(pipe, reply with { Instance = instance }, lifetime.Token);
        private Task Reject(Request request, string reason) => Send(new Reply(request.Id ?? "", Protocol.States.Rejected, Protocol.Schema, Reason: reason));

        private async Task<T> OnUi<T>(Func<T> action, TimeSpan? limit = null)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.Post(DispatchView.Command, new Work(() =>
            {
                if (lifetime.IsCancellationRequested) { done.TrySetCanceled(); return; }
                try { done.TrySetResult(action()); } catch (Exception ex) { done.TrySetException(ex); }
            }));
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            bound.CancelAfter(limit ?? OperationLimit);
            try { return await done.Task.WaitAsync(bound.Token); }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            { throw new TimeoutException("UI thread did not respond within the operation limit."); }
        }

        private async Task ServeAsync()
        {
            var s = session!;
            bool nativeExitRequested = false;
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                bool authenticated = false;
                while (!lifetime.IsCancellationRequested)
                {
                    Request request = await Protocol.ReadAsync<Request>(pipe, lifetime.Token);
                    if (!Accept(request)) { await Reject(request, "Authentication/protocol rejected."); break; }
                    Reply reply;
                    try
                    {
                        if (!authenticated && request.Operation != Protocol.Operations.Handshake) throw new InvalidOperationException("Handshake required.");
                        if (authenticated && request.Scenario != s.Context.Scenario.Id) throw new InvalidOperationException("Scenario identity mismatch.");
                        switch (request.Operation)
                        {
                            case Protocol.Operations.Handshake when !authenticated:
                                await WaitUntil(() => s.Definition.Ready?.Invoke(s.Context) ?? true, "Application did not become ready.");
                                authenticated = true;
                                var identity = await OnUi(() => Identity(s.Input.Inner.GetType().Name, TScreen.ScreenWidth, TScreen.ScreenHeight));
                                reply = new(request.Id, Protocol.States.Ready, Protocol.Schema, Target: identity, Scenarios: catalog); break;
                            case Protocol.Operations.Start when !s.Started:
                                await WaitUntil(() => s.Context.Scenario.ReadyPredicate?.Invoke(s.Context) ?? true, "Scenario did not become ready.");
                                await OnUi(() => s.Started = true);
                                reply = new(request.Id, Protocol.States.Started, Protocol.Schema); break;
                            case Protocol.Operations.Input when s.Started && !s.Completed && request.Input is not null:
                                reply = new(request.Id, Protocol.States.Routed, Protocol.Schema, Interaction: await RouteAsync(request.Input)); break;
                            case Protocol.Operations.Checkpoint when s.Started && !s.Completed && request.Checkpoint is not null:
                                reply = new(request.Id, Protocol.States.Captured, Protocol.Schema, Capture: await CaptureAsync(request.Checkpoint, request.Evidence ?? [])); break;
                            case Protocol.Operations.Recapture when s.Started && !s.Completed && request.Checkpoint is not null:
                                if (!s.Checkpoints.Contains(request.Checkpoint)) throw new InvalidOperationException("Recapture requires an existing checkpoint.");
                                reply = new(request.Id, Protocol.States.Captured, Protocol.Schema, Capture: await CaptureAsync(request.Checkpoint, request.Evidence ?? [], true)); break;
                            case Protocol.Operations.Complete when s.Started && !s.Completed && s.Context.Scenario.IsFixtureOnly && s.Checkpoints.Count == 0:
                                // Every bound external scenario captures at least one expectation-checked checkpoint; a session
                                // that completes without one executed the bare registration directly.
                                throw new FixtureBindingException(ScenarioSelection.NotRunnable(s.Context.Scenario.Id));
                            case Protocol.Operations.Complete when s.Started && !s.Completed:
                                // Whatever the scenario left held is released through the real pipeline before completion is
                                // judged (a held button may still own a tracking loop), and reported with the reply.
                                InteractionRecord? released = await OnUi(() => s.InputState.Holding)
                                    ? await RoutePlannedAsync(new InputStep(InputSteps.Release, InputSteps.ReleaseAll), "still held at scenario end, ") : null;
                                await WaitUntil(() => s.Context.NoModalView, "A modal view is still executing at completion.");
                                string? problem = await OnUi(() => s.ActionError?.Message ?? s.Context.Scenario.CompletionCheck?.Invoke(s.Context));
                                if (problem is not null) throw new InvalidOperationException("Scenario incomplete: " + problem);
                                s.Completed = true;
                                reply = new(request.Id, Protocol.States.Completed, Protocol.Schema, Interaction: released); break;
                            case Protocol.Operations.Shutdown:
                                await Send(new Reply(request.Id, Protocol.States.ShutdownAccepted, Protocol.Schema));
                                CleanShutdown = !s.Started || s.Completed;
                                if (s.Native is { } native)
                                {
                                    try
                                    {
                                        await OnUi(() => { native.Inject("kbAltX"); return true; });
                                        await WaitUntil(() => NativeExecution.IsQuitConfirmation(s.Context), "Commander did not show its expected Quit confirmation with Yes focused.");
                                        await OnUi(() => { native.Inject("kbEnter"); return true; });
                                        nativeExitRequested = true;
                                    }
                                    catch { CleanShutdown = false; throw; }
                                }
                                else await OnUi(() => { s.Context.Application.EndModal(Views.cmQuit); return true; });
                                return;
                            default: throw new InvalidOperationException("Unsupported operation or scenario state.");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { reply = new(request.Id, Protocol.States.Rejected, Protocol.Schema, Reason: (ex is TimeoutException ? Protocol.TimeoutPrefix : ex is FixtureBindingException ? Protocol.BindingPrefix : "") + ex.Message); }
                    await Send(reply);
                }
            }
            catch (Exception) { /* EOF or deadline: the controller fails the run; owned-process cleanup stays authoritative. */ }
            finally
            {
                pipe.Dispose();
                // Bounded fail-closed lifetime: unwinds nested menu/dialog loops (TProgram turns them into cmCancel).
                // A completed native run exits through actual driver Alt+X. Do not race that input with
                // a posted logical quit. The controller still bounds/terminates an unresponsive owned process.
                if (!nativeExitRequested)
                {
                    if (!lifetime.IsCancellationRequested) lifetime.Cancel();
                    Dispatcher.Post(DispatchView.Command, new Work(() =>
                    {
                        // An aborted scenario delivers nothing more: undelivered simulated input is dropped and the logical
                        // state cleared without synthesizing releases, which could act (a button release clicks) while unwinding.
                        s.Input.Reset(); s.InputState = InputSnapshot.Empty;
                        s.Context.Application.EndModal(Views.cmQuit);
                    }));
                }
            }
        }

        /// <summary>Bounded readiness wait. Each observation is one event-loop pass; between unmet observations
        /// a short delay lets background producers progress.</summary>
        private async Task WaitUntil(Func<bool> predicate, string failure)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < OperationLimit)
            {
                if (await OnUi(predicate)) return;
                await Task.Delay(10, lifetime.Token);
            }
            throw new TimeoutException(failure);
        }

        /// <summary>
        /// Input acknowledgement. Each probe is a posted marker, delivered by <c>TProgram.GetEvent</c> at the start of a pass,
        /// before that pass reads input. <c>Taken</c>: the event loop took the input (for a key: <c>TScreen.GetEvent</c> read it
        /// from the tracker, so the pass that read it has dispatched it — or entered the nested loop it opened — before the
        /// probe ran). <c>Quiet</c>: since then the loop reached an empty keyboard probe (no pending event, nothing in the
        /// framework queue; the status line's Idle update follows) and no foreign post is queued. Arbitrary delays are never
        /// treated as proof.
        /// </summary>
        private async Task<string> SettleAsync(Func<(bool Taken, bool Quiet)> probe, string what)
        {
            var clock = Stopwatch.StartNew();
            bool taken = false; int polls = 0;
            while (clock.Elapsed < OperationLimit)
            {
                var (t, quiet) = await OnUi(() => { ThrowIfFailed(); return probe(); });
                if (t && quiet) return Protocol.Acknowledgements.Settled;
                taken |= t;
                if (++polls > 20) await Task.Delay(5, lifetime.Token);
            }
            if (taken) return Protocol.Acknowledgements.Dispatched;
            throw new TimeoutException($"{what} was submitted but the event loop did not take it within the operation limit.");
        }

        private async Task<InteractionRecord> RouteAsync(InputStep input)
        {
            var s = session!;
            var tracker = s.Input;
            if (s.Native is { } native)
            {
                if (input.Kind == InputSteps.Mouse)
                {
                    var plan = await OnUi(() => InputPlanner.Create(input.Kind, input.Name, s.InputState, s.Context.Width, s.Context.Height,
                        name => s.Context.Scenario.Points.TryGetValue(name, out var resolve) ? resolve(s.Context) : null));
                    long mouseSequence = await OnUi(() => ++s.Sequence);
                    foreach (var planned in plan.Events)
                    {
                        long pumps = 0, empty = 0;
                        await OnUi(() => { ThrowIfFailed(); pumps = tracker.Pumps; empty = tracker.EmptyProbes;
                            native.InjectMouse(planned.Event); s.InputState = planned.After; return true; });
                        await SettleAsync(() => (tracker.Pumps > pumps,
                            tracker.Pumps > pumps && tracker.EmptyProbes > empty && TEventQueue.PostedCount == 0), planned.Label);
                    }
                    return new(input.Kind, input.Name, native.Routing + " (mouse; production pixel-to-cell/button translation)", mouseSequence,
                        Protocol.Acknowledgements.Settled, "Programmatic native SDL mouse events; not physical mouse input.", plan.After.Describe());
                }
                if (input.Kind != ScenarioStep.Key) throw new InvalidOperationException("Unsupported native input kind; no logical substitution performed.");
                long mark = 0, sequence = 0;
                await OnUi(() => { ThrowIfFailed(); mark = tracker.Consumed; sequence = ++s.Sequence; native.Inject(input.Name); return true; });
                string ack = await SettleAsync(() => (tracker.Consumed > mark,
                    tracker.Consumed > mark && tracker.EmptyProbes > tracker.ConsumedMark && TEventQueue.PostedCount == 0), input.Name);
                return new(input.Kind, input.Name, native.Routing, sequence, ack, "Programmatic native driver events; not physical keyboard input.");
            }
            if (input.Kind == Protocol.InputKinds.Key || InputSteps.Kinds.Contains(input.Kind))
                return await RoutePlannedAsync(input);
            if (input.Kind == Protocol.InputKinds.Resize)
            {
                if (!ScenarioSelection.TryParseSize(input.Name, out int width, out int height)) throw new InvalidOperationException("Invalid resize grid.");
                if (!s.Context.Scenario.Descriptor.Grid.Supports(width, height)) throw new InvalidOperationException("Resize grid outside the scenario range.");
                long mark = 0, sequence = 0;
                await OnUi(() =>
                {
                    ThrowIfFailed(); sequence = ++s.Sequence; mark = tracker.EmptyProbes;
                    s.Context.Driver.SimulateResize((ushort)width, (ushort)height);
                    return true;
                });
                string ack = await SettleAsync(() => (true, tracker.EmptyProbes > mark && TEventQueue.PostedCount == 0), "Resize " + input.Name);
                string? problem = await OnUi(() =>
                {
                    var app = s.Context.Application;
                    if (TScreen.ScreenWidth != width || TScreen.ScreenHeight != height) return "screen grid did not change";
                    if (app.size.x != width || app.size.y != height)
                        return $"the application did not apply cmScreenResized (application bounds {app.size.x}x{app.size.y}; a modal view may have consumed it)";
                    s.Context.Width = width; s.Context.Height = height;
                    return null;
                });
                if (problem is not null) throw new InvalidOperationException("Resize to " + input.Name + " failed: " + problem + ".");
                return new(Protocol.InputKinds.Resize, input.Name, ResizeRouting, sequence, ack);
            }
            if (!s.Context.Scenario.Actions.TryGetValue(input.Name, out var action)) throw new InvalidOperationException("Unregistered scenario action.");
            long actionMark = 0, actionSequence = await OnUi(() => ++s.Sequence);
            bool started = false;
            // Separate post: an action that opens a modal view never blocks the reply; an empty probe inside that view's
            // nested loop settles it.
            Dispatcher.Post(DispatchView.Command, new Work(() =>
            {
                if (lifetime.IsCancellationRequested) return;
                actionMark = tracker.EmptyProbes; started = true;
                try { action(s.Context); } catch (Exception ex) { s.ActionError ??= ex; }
            }));
            string actionAck = await SettleAsync(() => (started, started && tracker.EmptyProbes > actionMark && TEventQueue.PostedCount == 0), "Action " + input.Name);
            return new(Protocol.InputKinds.Action, input.Name, ActionRouting, actionSequence, actionAck);
        }

        /// <summary>
        /// Keyboard, modifier, text, mouse and release steps. The whole step is planned and validated first (nothing is
        /// submitted for a refused step). Then each event is submitted alone and must settle before the next: a key or
        /// modifier event is read by <c>TScreen.GetEvent</c> from the tracker's key path, a mouse event is handed to
        /// <c>TEventQueue</c> by the tracker's <c>PumpMessages</c> (so the framework's click classification and every loop,
        /// including a view's own mouse-tracking loop, see it as driver input). One event per settled pass is the pace of a
        /// person, slower than the application, and makes the order of every event deterministic. The logical state
        /// advances as each event is submitted; a failure reports it.
        /// </summary>
        private async Task<InteractionRecord> RoutePlannedAsync(InputStep input, string? detailPrefix = null)
        {
            var s = session!;
            var tracker = s.Input;
            string ack = Protocol.Acknowledgements.Settled;
            long sequence;
            InputPlanner.Plan plan;
            try
            {
                (sequence, plan) = await OnUi(() =>
                {
                    ThrowIfFailed();
                    var context = s.Context;
                    s.LastInput = $"{input.Kind} {input.Name} (refused before any event)";
                    var created = InputPlanner.Create(input.Kind, input.Name, s.InputState, context.Width, context.Height, name =>
                        context.Scenario.Points.TryGetValue(name, out var resolve) ? resolve(context)
                            : throw new InvalidOperationException($"Point ':{name}' is not registered by the scenario."));
                    long next = ++s.Sequence;
                    s.LastInput = $"{input.Kind} {input.Name} (#{next})";
                    return (next, created);
                });
                foreach (var planned in plan.Events)
                {
                    long id = await OnUi(() => { ThrowIfFailed(); s.InputState = planned.After; return tracker.Submit(planned.Event, planned.Mouse); });
                    string settled = await SettleAsync(() =>
                    {
                        bool taken = tracker.Consumed >= id;
                        return (taken, taken && tracker.EmptyProbes > tracker.ConsumedMark && TEventQueue.PostedCount == 0);
                    }, $"{input.Kind} {input.Name} (#{sequence}) event '{planned.Label}'");
                    if (settled != Protocol.Acknowledgements.Settled) ack = settled;
                }
            }
            catch (TimeoutException ex) { throw new TimeoutException(ex.Message + " " + await StateNote()); }
            catch (InvalidOperationException ex) { throw new InvalidOperationException(ex.Message + " " + await StateNote()); }
            string? detail = plan.Detail is null && detailPrefix is null ? null : detailPrefix + plan.Detail;
            if (ack != Protocol.Acknowledgements.Settled) detail = (detail is null ? "" : detail + "; ") + "consumed and dispatched; event loop not quiescent within the limit";
            return new(input.Kind, input.Name, RoutingOf(input.Kind), sequence, ack, detail, plan.After.Describe());
        }

        /// <summary>The held input state and the last input step, appended to every input failure.</summary>
        private async Task<string> StateNote()
        {
            var s = session!;
            try { return await OnUi(() => $"[last input: {s.LastInput ?? "none"}; input state: {s.InputState.Describe()}]"); }
            catch (Exception) { return $"[last input: {s.LastInput ?? "none"}; input state: {s.InputState.Describe()}]"; }
        }

        private void ThrowIfFailed()
        {
            if (session!.ActionError is { } error) throw new InvalidOperationException("Scenario action failed: " + error.GetBaseException().Message);
        }

        private async Task<CheckpointCapture> CaptureAsync(string name, string[] evidence, bool recapture = false)
        {
            var s = session!;
            // Names are unique per execution; a name is consumed only by a successful capture.
            if (!recapture && s.Checkpoints.Contains(name)) throw new InvalidOperationException("Duplicate checkpoint name in this scenario execution.");
            var registration = s.Context.Scenario;
            // Host boundary: a fixture-only registration never captures a checkpoint that bypasses its typed
            // domain predicates, whatever the controller validated.
            if (registration.IsFixtureOnly && !registration.Expectations.ContainsKey(name))
                throw new FixtureBindingException($"Checkpoint '{name}' is not an advertised checkpoint expectation of fixture-only registration '{registration.Id}' "
                    + $"(advertised: {string.Join(", ", registration.Expectations.Keys.Order(StringComparer.Ordinal))}); no capture performed.");
            registration.Expectations.TryGetValue(name, out var expectation);
            byte[]? previous = null; int stable = 0, observations = 0;
            var clock = Stopwatch.StartNew();
            while (observations < MaximumObservations && clock.Elapsed < OperationLimit)
            {
                observations++;
                // The final observation (stability reached) also runs the requested inspectors in the same UI pass, so
                // cells and semantic evidence describe one moment.
                bool final = stable + 1 >= StableObservations;
                var (met, cells, surface, inspection) = await OnUi(() =>
                {
                    ThrowIfFailed();
                    if (!ReferenceEquals(TDisplay.driver, s.Input) || TScreen.ScreenWidth != s.Context.Width || TScreen.ScreenHeight != s.Context.Height)
                        throw new InvalidOperationException("Unexpected driver or grid.");
                    bool ok = expectation.Expect?.Invoke(s.Context) ?? true;
                    var snapshot = ScreenSnapshot.CopyRoot();
                    var found = ok && final && evidence.Length > 0 ? Inspectors.Capture(s.Context.Application, snapshot, evidence) : null;
                    return (ok, snapshot.Serialize(), ok ? Surface(s.Context) : null, found);
                });
                if (!met) { previous = null; stable = 0; await Task.Delay(10, lifetime.Token); continue; }
                stable = previous is not null && previous.AsSpan().SequenceEqual(cells) ? stable + 1 : 1;
                previous = cells;
                if (stable >= StableObservations && (evidence.Length == 0 || inspection is not null) && (recapture || s.Checkpoints.Add(name)))
                {
                    Dictionary<string, string>? nativeEvidence = null;
                    if (s.Native is { } native)
                    {
                        var frame = await OnUi(() => native.Capture());
                        nativeEvidence = new(frame.Provenance) { ["schema"] = "tsharpvision.testing/native.1", ["png"] = frame.Png,
                            ["pixelsSha256"] = frame.PixelsHash, ["captureLayer"] = frame.Layer,
                            ["pixelWidth"] = frame.PixelWidth.ToString(), ["pixelHeight"] = frame.PixelHeight.ToString() };
                    }
                    return new(name, Convert.ToBase64String(cells), s.Context.Width, s.Context.Height, observations, DateTimeOffset.UtcNow, surface, inspection) { Native = nativeEvidence };
                }
            }
            throw new TimeoutException(previous is null && expectation.Expect is not null
                ? $"Checkpoint '{name}' expectation not met: {expectation.Note}."
                : $"Checkpoint '{name}' screen did not stabilize within bounded observations.");
        }

        private static SurfaceInfo Surface(TestTargetContext context)
        {
            TView top = context.TopView ?? context.Application;
            var origin = top.MakeGlobal(new TPoint(0, 0));
            TView? focused = top is TGroup group ? group.current : null;
            while (focused is TGroup inner && inner.current is not null) focused = inner.current;
            return new(top.GetType().FullName ?? top.GetType().Name, top is TWindow, origin.x, origin.y, top.size.x, top.size.y,
                focused?.GetType().FullName, top.GetHelpCtx());
        }

        public void Dispose()
        {
            lifetime.Cancel();
            try { task?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            pipe.Dispose(); lifetime.Dispose(); Dispatcher.Dispose();
        }
    }
}

/// <summary>
/// Testing-side input tracker installed as the display driver in front of the target's <see cref="NullDriver"/>. Host-submitted
/// keys wait in its own FIFO, tagged with a sequence number, and are handed out by <see cref="ReadKeyEvent"/> — the same call
/// <c>TScreen.GetEvent</c> makes for any driver, after the framework event queue. It records which key the event loop actually
/// read and counts empty keyboard probes, which only happen when the pending event and the framework queue are empty. All
/// rendering, geometry and resize behaviour is the NullDriver's. Production event semantics are unchanged.
///
/// Simulated mouse events share the FIFO (one order for all simulated input) but are delivered as a driver delivers
/// mouse input: <see cref="PumpMessages"/>, which <c>TScreen.GetEvent</c> calls before it reads the framework queue, moves
/// them into <see cref="TEventQueue"/>, where the framework's click classification and every loop see them. "Taken" for a
/// mouse event means handed to the queue in that pump; the following empty probe proves the queue was drained past it.
/// The tracker reports the capabilities of the simulated devices (pointer, key releases, standalone modifier transitions).
/// </summary>
internal sealed class TestInputDriver(IDriver inner) : IDriver
{
    private readonly Queue<(long Sequence, TEvent Event, bool Mouse)> input = new();
    internal IDriver Inner { get; } = inner;
    internal long Submitted { get; private set; }
    /// <summary>Sequence of the last input the event loop read (a key) or the loop's pump handed to the framework queue (mouse).</summary>
    internal long Consumed { get; private set; }
    /// <summary>Number of keyboard probes that found no input at all.</summary>
    internal long EmptyProbes { get; private set; }
    /// <summary><see cref="EmptyProbes"/> at the moment the last input was taken.</summary>
    internal long ConsumedMark { get; private set; }
    internal long Pumps { get; private set; }

    internal long Submit(TEvent ev, bool mouse = false) { input.Enqueue((++Submitted, ev, mouse)); return Submitted; }

    /// <summary>Drops undelivered simulated input (aborted scenario). The counters stay monotonic.</summary>
    internal void Reset() => input.Clear();

    public bool ReadKeyEvent(out TEvent ev)
    {
        if (input.Count > 0 && !input.Peek().Mouse)
        {
            (long sequence, ev, _) = input.Dequeue();
            Consumed = sequence; ConsumedMark = EmptyProbes;
            return true;
        }
        if (Inner.ReadKeyEvent(out ev))
        {
            if (Inner is not NullDriver) { Consumed++; ConsumedMark = EmptyProbes; }
            return true;
        }
        // A mouse event still waiting for the next pump is pending input, not an empty probe.
        if (input.Count == 0) EmptyProbes++;
        return false;
    }

    public void PumpMessages()
    {
        Inner.PumpMessages();
        Pumps++;
        while (input.Count > 0 && input.Peek().Mouse)
        {
            var (sequence, ev, _) = input.Dequeue();
            TEventQueue.Enqueue(ev);
            Consumed = sequence; ConsumedMark = EmptyProbes;
        }
    }

    public void Initialize() => Inner.Initialize();
    public void Suspend() => Inner.Suspend();
    public void Resume() => Inner.Resume();
    public void Shutdown() => Inner.Shutdown();
    public ushort GetCols() => Inner.GetCols();
    public ushort GetRows() => Inner.GetRows();
    public TDisplay.SM GetScreenMode() => Inner.GetScreenMode();
    public void SetScreenMode(TDisplay.SM mode) => Inner.SetScreenMode(mode);
    public ScreenBuffer AllocateScreenBuffer() => Inner.AllocateScreenBuffer();
    public void ClearScreen(ushort cols, ushort rows) => Inner.ClearScreen(cols, rows);
    public ushort GetCursorType() => Inner.GetCursorType();
    public void SetCursorType(ushort cursorType) => Inner.SetCursorType(cursorType);
    public void SetCaretPosition(int x, int y) => Inner.SetCaretPosition(x, y);
    public void WriteBuf(int x, int y, int w, int h, Span<TScreenChar> buf) => Inner.WriteBuf(x, y, w, h, buf);
    public void MakeBeep() => Inner.MakeBeep();
    public bool SupportsMouse => true;
    public bool SupportsTrueColor => Inner.SupportsTrueColor;
    public bool SupportsGraphics => Inner.SupportsGraphics;
    public KeyboardCapabilities KeyboardCapabilities => ((IDriver)Inner).KeyboardCapabilities
        | KeyboardCapabilities.KeyReleaseEvents | KeyboardCapabilities.StandaloneModifierTransitions;
}

/// <summary>Resolves <see cref="Keys"/> constant names. Only declared key constants can be queued.</summary>
public static class TestKeys
{
    private static readonly Dictionary<string, ushort> Map = typeof(Keys).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(ushort) && f.Name.StartsWith("kb", StringComparison.Ordinal))
        .ToDictionary(f => f.Name, f => (ushort)f.GetRawConstantValue()!, StringComparer.Ordinal);
    // Deterministic reverse lookup: aliases share a value; the ordinal-first name wins.
    private static readonly Dictionary<ushort, string> Names = Map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    public static bool TryResolve(string name, out ushort code) => Map.TryGetValue(name, out code);

    /// <summary>A <see cref="Keys"/> constant name with this value, or null.</summary>
    public static string? NameOf(ushort code) => Names.TryGetValue(code, out string? name) ? name : null;

    public static TEvent KeyDown(ushort code)
    {
        var ev = new TEvent { What = Events.evKeyDown };
        ev.keyDown.keyCode = code; ev.keyDown.charScan = new CharScanType(code);
        return ev;
    }
}
