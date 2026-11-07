using System.Reflection;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// The qualified framework package (package mode; declared stable NuGet family) and the two
// Reconciliation corrections: a raw catalog never lists fixture-only registrations, and a target without its
// project explains why its fixture-only registrations are not listed.
[Collection("NonParallel")]
public sealed class FixturePackageTests
{
    private static string Candidate => Infrastructure.FrameworkPackageVersion.Expected + "+";
    private static string Target => TestWorkbenchHostTests.SampleTarget;

    // ── framework candidate ──────────────────────────────────────────────────

    [Fact]
    public void LoadedFrameworkIsTheQualifiedPackageCandidate()
    {
        string version = typeof(TView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith(Candidate, version);
        // Every framework family member beside the tests (core and drivers) is the same candidate build.
        var family = Directory.GetFiles(AppContext.BaseDirectory, "TSharpVision*.dll");
        Assert.Contains(family, f => Path.GetFileName(f) == "TSharpVision.Drivers.Console.dll");
        Assert.All(family, f => Assert.StartsWith(Candidate, System.Diagnostics.FileVersionInfo.GetVersionInfo(f).ProductVersion));
        // The Workbench assembly under test and the staged sample target resolve the same framework file.
        string staged = Path.Combine(Path.GetDirectoryName(Target)!, "TSharpVision.dll");
        Assert.Equal(File.ReadAllBytes(typeof(TView).Assembly.Location), File.ReadAllBytes(staged));
    }

    // F1: keyboard Size/Move could only move. TV-1.1-FIX-3: Shift+arrows resize from the bottom-right edge.
    [Fact]
    public void PackageSizeMoveResizesWithShiftArrowsMovesWithArrowsAndEscRestores()
    {
        using var scope = new Infrastructure.DriverScope(80, 25);
        TEventQueue.Resume();
        var desktop = new Desktop(new TRect(0, 1, 80, 24));
        desktop.SetState(Constants.Views.sfActive, true); desktop.SetState(Constants.Views.sfExposed, true);
        var window = new TWindow(new TRect(10, 5, 40, 15), "Resize", 1);
        desktop.Insert(window); window.SetState(Constants.Views.sfSelected, true);

        SizeMove(scope.Driver, window, Shifted(Constants.Keys.kbRight), Shifted(Constants.Keys.kbRight), Shifted(Constants.Keys.kbDown), Key(Constants.Keys.kbEnter));
        Assert.Equal(new TRect(10, 5, 42, 16), window.GetBounds());          // origin fixed, 2 wider, 1 taller
        SizeMove(scope.Driver, window, Shifted(Constants.Keys.kbLeft), Shifted(Constants.Keys.kbUp), Shifted(Constants.Keys.kbUp), Key(Constants.Keys.kbEnter));
        Assert.Equal(new TRect(10, 5, 41, 14), window.GetBounds());
        SizeMove(scope.Driver, window, Key(Constants.Keys.kbRight), Key(Constants.Keys.kbDown), Key(Constants.Keys.kbEnter));
        Assert.Equal(new TRect(11, 6, 42, 15), window.GetBounds());           // plain arrows still move
        SizeMove(scope.Driver, window, Shifted(Constants.Keys.kbRight), Key(Constants.Keys.kbLeft), Key(Constants.Keys.kbEsc));
        Assert.Equal(new TRect(11, 6, 42, 15), window.GetBounds());           // Esc restores the original bounds
    }

    private static TEvent Key(ushort code, uint control = 0)
    {
        var ev = new TEvent { What = Constants.Events.evKeyDown };
        ev.keyDown.keyCode = code; ev.keyDown.controlKeyState = control;
        return ev;
    }
    // A bare owner whose event source is the screen (no application), as in the framework's own DragView tests.
    private sealed class Desktop(TRect bounds) : TGroup(bounds)
    {
        public override void GetEvent(ref TEvent e) => TScreen.GetEvent(ref e);
        public override void PutEvent(ref TEvent e) => TEventQueue.Enqueue(e);
    }
    private static TEvent Shifted(ushort code) => Key(code, Constants.Keys.kbLeftShift);

    private static void SizeMove(NullDriver driver, TWindow window, params TEvent[] keys)
    {
        foreach (var key in keys) driver.EnqueueKey(key);
        var resize = new TEvent { What = Constants.Events.evCommand }; resize.message.command = Constants.Views.cmResize;
        window.HandleEvent(ref resize);
        Assert.False(window.GetState(Constants.Views.sfDragging));
    }

    // The same capability on a real Workbench modeless window inside the running application.
    [Fact]
    public async Task WorkbenchMessagesWindowResizesFromTheKeyboard()
    {
        string root = Folder();
        await Drive(140, 40, root, async (app, bridge) =>
        {
            await Until(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16);
            Enqueue(Key((ushort)(Constants.Keys.kbAlt1 + 5 * 0x100)));        // Alt+6: Messages
            await Until(bridge, () => app.Messages is { } m && ReferenceEquals(app.DeskTop!.current, m));
            TRect before = default, after = default;
            // Leave room to grow inside the desktop (the window opens at its saved or default size).
            await bridge.Invoke(() => { app.Messages!.Locate(new TRect(10, 5, 90, 25)); before = app.Messages.GetBounds(); });
            // The keyboard Size/Move loop reads only key events: space them as a person types.
            foreach (var key in new[] { Key(Constants.Keys.kbCtrlF5), Shifted(Constants.Keys.kbRight), Shifted(Constants.Keys.kbRight),
                Shifted(Constants.Keys.kbRight), Shifted(Constants.Keys.kbDown), Key(Constants.Keys.kbEnter) })
            { Enqueue(key); await Task.Delay(300); }
            await Task.Delay(500);
            await bridge.Invoke(() => after = app.Messages!.GetBounds());
            Assert.Equal(before.a, after.a);
            Assert.Equal(new TPoint(before.b.x + 3, before.b.y + 1), after.b);
            await bridge.Invoke(() => Assert.False(app.Messages!.GetState(Constants.Views.sfDragging)));
        });
    }

    // ── reconciliation corrections ─────────────────────────────────

    [Fact]
    public void ScenarioListNeverListsOrMarksFixtureOnlyRegistrationsFromARawCatalog()
    {
        using var scope = new Infrastructure.DriverScope(80, 25);
        var fixture = new TestScenario("fixture/only", "Fixture only", "fixture", "d").FixtureOnly("r1")
            .RegisterCheckpointExpectation("ready", _ => true).Descriptor;
        var compiled = new TestScenario("compiled/one", "Compiled", "compiled", "d").Checkpoint("initial").Descriptor;
        var list = new ScenarioList(new TRect(0, 0, 60, 10), new TScrollBar(new TRect(60, 0, 61, 10)));
        list.LoadCatalog([fixture, compiled]);
        Assert.Equal(new[] { "Compiled" }, list.Strings);
        Assert.Throws<ArgumentException>(() => list.Toggle("fixture/only"));
        list.RestoreMarks(["fixture/only", "compiled/one"]); list.MarkGroup("fixture");
        Assert.Equal(new[] { "compiled/one" }, list.MarkedIds);
    }

    [Fact]
    public async Task TargetWithoutItsProjectExplainsUnlistedFixtureOnlyRegistrations()
    {
        string root = Folder();
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["list", "--target", Target], output, error));
        Assert.DoesNotContain("sample/fixture-only/notes", output.ToString());
        Assert.Contains("Note: 1 fixture-only registration is not runnable on its own; add the external scenario files that bind them", error.ToString());

        // With an external scenario that binds it, nothing is unexplained: no note.
        string source = Path.Combine(root, "notes.json");
        File.WriteAllText(source, """{"schema":"tsharpworkbench.scenario","version":1,"id":"external/notes-flow","title":"External notes flow","description":"d","group":"external","fixture":"sample/fixture-only/notes","expectedOutcome":"passed","steps":[{"kind":"checkpoint","id":"idle"}]}""");
        string project = Path.Combine(root, "notes.twproj");
        new WorkbenchProject { Target = Target, OutputRoot = root, ScenarioFiles = [source] }.Save(project);
        error.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.RunAsync(["list", project], output, error));
        Assert.DoesNotContain("fixture-only", error.ToString());
        Assert.Equal("", Cli.FixtureOnlyNote([new TestScenario("x/y", "X", "x", "d").Checkpoint("c").Descriptor]));

        // TUI: the load status says the same, once, for the target without its project.
        await Drive(80, 25, root, async (app, bridge) =>
        {
            await Until(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16);
            await bridge.Invoke(() =>
            {
                Assert.DoesNotContain("Notes fixture (fixture-only)", app.Project!.TestList.Strings);
                Assert.Contains(app.Notifications.History, n => n.Message.Contains("1 fixture-only registration is not runnable on its own"));
            });
        });
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static string Folder()
    {
        string root = Environment.GetEnvironmentVariable("TSWB_FIXTURE_RESULTS") ?? Path.Combine(Path.GetTempPath(), "tsw-fixture-1a");
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Enqueue(TEvent ev) { ev.keyDown.charScan = new CharScanType(ev.keyDown.keyCode); TEventQueue.Enqueue(ev); }

    private static async Task Drive(int width, int height, string root, Func<WorkbenchApp, UiBridge, Task> body)
    {
        using var driver = new Infrastructure.DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(Target, Path.Combine(root, "runs"), new(Target, Path.Combine(root, "runs"), null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
        var bridge = new UiBridge(); app.DeskTop!.Insert(bridge);
        Exception? failure = null;
        Task worker = Task.Run(async () =>
        {
            try { await body(app, bridge); }
            catch (Exception ex) { failure = ex; Enqueue(Key(Constants.Keys.kbEnter)); }
            finally { await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Constants.Views.cmQuit); }).WaitAsync(TimeSpan.FromSeconds(20)); }
        });
        AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(60)), "Worker did not stop.");
        Assert.Null(failure);
    }

    private static async Task Until(UiBridge bridge, Func<bool> condition, int seconds = 60)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            bool met = false;
            await bridge.Invoke(() => met = condition()).WaitAsync(timeout.Token);
            if (met) return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class UiBridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal UiBridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Constants.Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddb, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Constants.Events.evCommand && ev.message.command == 0x7ddb && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); }
            else base.HandleEvent(ref ev);
        }
    }
}
