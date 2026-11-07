using TSharpVision;
using TSharpVision.Constants;

namespace TSharpWorkbench.SampleTarget;

/// <summary>The explicitly registered catalog. Every UI interaction is queued logical keyboard input or a real logical
/// resize; registered actions appear only in the self-test group, where they model background producers.</summary>
internal static class SampleScenarios
{
    internal const string Dialog = "sample/dialog/basic", Menu = "sample/menu/navigation", MenuSelection = "sample/menu/selection",
        Status = "sample/status/context", Resize = "sample/screen/resize", Fixture = "sample/fixture/window", FixturePath = "sample/fixture/path", Burst = "selftest/input/queued-burst",
        Failure = "selftest/failure/wrong-expectations", Blocked = "selftest/failure/blocked-ui", Crash = "selftest/failure/target-crash";

    private static readonly string[] Tree = [Protocol.EvidenceKinds.ViewTree], Menus = [Protocol.EvidenceKinds.Menu],
        Everything = Protocol.EvidenceKinds.All;
    // The sample's own palette knowledge: TSharpVision draws shadows with attribute 0x08 (dark grey on black).
    private const int ShadowAttribute = 0x08;

    internal static TestTargetDefinition Definition => new()
    {
        Scenarios = [DialogScenario(), MenuScenario(), MenuSelectionScenario(), StatusScenario(), ResizeScenario(), FixtureScenario(), FixturePathScenario(), BurstScenario(),
            FailureScenario(), BlockedScenario(), CrashScenario(), ExternalFixtureScenarios.Deferred(), .. SampleInputScenarios.All(),
            FixtureOnlyScenarios.NotesFixture()],
        CreateApplication = _ => new SampleApp(),
    };

    private static SampleApp App(TestTargetContext c) => (SampleApp)c.Application;
    private static TMenuBox? OpenBox(TestTargetContext c) => c.TopView as TMenuBox;
    private static string? StatusFirst(TestTargetContext c) => c.Application.StatusLine?.Items?.Text;
    private static string? Clean(TestTargetContext c) => c.NoModalView && App(c).NoteCount == 0 ? null : "A window or modal view is still open.";
    private static ViewSelector Id(string id) => ViewSelector.ById(id);

    private static TestScenario DialogScenario() =>
        new TestScenario(Dialog, "Basic dialog", "sample/dialog", "Opens the sample's modal TDialog with F3, moves focus with Tab and Shift+Tab, cancels with Esc.")
            .Kind(ScenarioKinds.Dialog).Grid(60, 20).Metadata("area", "dialogs")
            .Conditions("Desktop idle, no window open; the dialog is centered on the desktop.")
            .Checkpoint("initial", c => c.NoModalView && App(c).NoteCount == 0, "empty desktop, no modal view", Tree,
                UiAssert.Modal("no-modal", null), UiAssert.Absent("no-dialog", ViewSelector.OfType("TDialog")))
            .Key("kbF3", "status/menu hot key -> cmShowDialog -> DeskTop.ExecView")
            // Deliberately no expectation predicate: the settled input acknowledgement must be enough for the assertions.
            .Checkpoint("dialog-open", null, "modal TDialog with the input line focused", Tree,
                UiAssert.Modal("dialog-is-modal", Id("sample-dialog")),
                UiAssert.Focused("input-focused", Id("name-input")),
                UiAssert.Within("dialog-in-working-area", Id("sample-dialog")),
                UiAssert.NotClipped("dialog-not-clipped", Id("sample-dialog")),
                UiAssert.Shadow("dialog-shadow", Id("sample-dialog"), ShadowAttribute),
                UiAssert.Aligned("buttons-share-top", ViewSelector.OfType("TButton"), "top", Id("ok-button")),
                UiAssert.Spacing("buttons-two-cells-apart", Id("ok-button"), Id("cancel-button"), "horizontal", 2, 2),
                UiAssert.Within("buttons-inside-dialog", Id("cancel-button"), Id("sample-dialog")),
                UiAssert.ScreenContains("title-shown", "Sample dialog"))
            .Key("kbTab")
            .Checkpoint("focus-next", c => c.TopView is TDialog { current: TButton }, "focus moved to the OK button", Tree,
                UiAssert.Focused("ok-focused", Id("ok-button")))
            .Key("kbTab")
            .Checkpoint("focus-cancel", null, "focus moved to the Cancel button", Tree,
                UiAssert.Focused("cancel-focused", Id("cancel-button")))
            .Key("kbShiftTab")
            .Key("kbShiftTab")
            .Checkpoint("focus-back", null, "Shift+Tab twice returns to the input line", Tree,
                UiAssert.Focused("input-focused-again", Id("name-input")))
            .Key("kbEsc")
            .Checkpoint("dialog-closed", c => c.NoModalView, "dialog closed, desktop restored", Tree,
                UiAssert.Modal("no-modal-after-close", null), UiAssert.Absent("dialog-gone", Id("sample-dialog")))
            .Completion(Clean);

    private static TestScenario MenuScenario() =>
        new TestScenario(Menu, "Menu navigation", "sample/menu", "Opens File with Alt+F, moves the selection, moves to Window, opens the Arrange submenu, closes with Esc.")
            .Kind(ScenarioKinds.Menu)
            .Checkpoint("initial", c => c.NoModalView, "menu bar idle", Menus, UiAssert.MenuDepth("no-open-menu", 0))
            .Key("kbAltF")
            .Checkpoint("menu-open", c => OpenBox(c) is { ParentMenu: TMenuBar, Current.Name: "~N~ew note" }, "File drop-down executing its modal loop", Menus,
                UiAssert.MenuDepth("one-level", 1), UiAssert.MenuSelected("new-note-selected", "New note"),
                UiAssert.MenuSelected("bar-on-file", "File", 0), UiAssert.MenuItem("print-disabled", "Print", "disabled"),
                UiAssert.MenuItem("dialog-enabled", "Dialog...", "enabled"))
            .Key("kbDown")
            .Checkpoint("item-selected", c => OpenBox(c)?.Current?.Name == "~D~ialog...", "selection moved to Dialog...", Menus,
                UiAssert.MenuSelected("dialog-selected", "Dialog..."))
            .Key("kbRight")
            .Checkpoint("next-menu", c => OpenBox(c) is { ParentMenu: TMenuBar, Current.Name: "~A~rrange" }, "Window drop-down open", Menus,
                UiAssert.MenuSelected("bar-on-window", "Window", 0), UiAssert.MenuSelected("arrange-selected", "Arrange"))
            .Key("kbEnter")
            .Checkpoint("submenu-open", c => OpenBox(c) is { ParentMenu: TMenuBox, Current.Name: "~T~ile" }, "Arrange submenu open over the Window drop-down", Menus,
                UiAssert.MenuDepth("two-levels", 2), UiAssert.SubmenuOpen("arrange-open", "Arrange"), UiAssert.MenuSelected("tile-selected", "Tile"))
            .Key("kbEsc")
            .Checkpoint("submenu-closed", c => OpenBox(c) is { ParentMenu: TMenuBar, Current.Name: "~A~rrange" }, "back in the Window drop-down", Menus,
                UiAssert.MenuDepth("back-to-one", 1))
            .Key("kbEsc")
            .Checkpoint("menu-closed", c => c.NoModalView, "every menu loop has returned", Menus, UiAssert.MenuDepth("closed", 0))
            .Completion(Clean);

    // No checkpoint here has an expectation predicate: correctness rests on settled input plus menu assertions.
    private static TestScenario MenuSelectionScenario() =>
        new TestScenario(MenuSelection, "Menu selection and mnemonics", "sample/menu",
                "F10 activates the bar, Down opens File, three identical Down keys move over a disabled entry, Alt+W switches menus by mnemonic, Enter opens a submenu, Esc closes everything.")
            .Kind(ScenarioKinds.Menu)
            .Checkpoint("idle", null, "bar idle", Everything, UiAssert.MenuDepth("idle-depth", 0), UiAssert.Modal("idle-no-modal", null))
            .Key("kbF10", "status item -> cmMenu (this application's menu key; not a library convention)")
            .Checkpoint("bar-active", null, "menu bar loop active, File highlighted, no drop-down", Menus,
                UiAssert.MenuDepth("bar-only", 0), UiAssert.MenuSelected("file-highlighted", "File", 0))
            .Key("kbDown")
            .Checkpoint("file-open", null, "File drop-down open", Menus,
                UiAssert.MenuDepth("file-depth", 1), UiAssert.MenuSelected("first-entry", "New note"))
            .Key("kbDown").Key("kbDown").Key("kbDown", "three identical keys; the disabled entry is selectable but not executable")
            .Checkpoint("repeated-down", null, "selection on Exit after passing Print (disabled)", Menus,
                UiAssert.MenuSelected("exit-selected", "Exit"), UiAssert.MenuItem("print-is-disabled", "Print", "disabled"))
            .Key("kbAltW", "mnemonic of the bar entry Window, pressed inside the File drop-down")
            .Checkpoint("mnemonic-window", null, "Window drop-down opened by its mnemonic", Menus,
                UiAssert.MenuDepth("window-depth", 1), UiAssert.MenuSelected("bar-window", "Window", 0), UiAssert.MenuSelected("arrange-first", "Arrange"))
            .Key("kbEnter")
            .Checkpoint("submenu", null, "Arrange submenu open", Everything,
                UiAssert.MenuDepth("submenu-depth", 2), UiAssert.SubmenuOpen("arrange-submenu", "Arrange"), UiAssert.Modal("menu-box-modal", ViewSelector.OfType("TMenuBox")))
            .Key("kbEsc").Key("kbEsc")
            .Checkpoint("restored", null, "all menus closed, desktop restored", Menus, UiAssert.MenuDepth("restored-depth", 0))
            .Completion(Clean);

    private static TestScenario StatusScenario() =>
        new TestScenario(Status, "Status line context", "sample/status", "Default status line, F4 opens a note window (note help context), Alt+F3 closes it.")
            // The desktop items plus the hint need 63 columns; narrower grids are refused rather than failing the hint assertion.
            .Kind(ScenarioKinds.Status).Grid(64, 12)
            .Checkpoint("desktop-status", c => c.NoModalView && App(c).NoteCount == 0 && StatusFirst(c) == "~Alt+X~ Exit", "desktop status items, no window",
                [Protocol.EvidenceKinds.Status],
                UiAssert.StatusContext("desktop-context", Views.hcNoContext), UiAssert.StatusItem("exit-visible", "Alt+X Exit"),
                UiAssert.StatusItem("new-note-enabled", "F4 New note", "enabled"), UiAssert.StatusItem("no-close-item", "Alt+F3 Close note", "absent"),
                UiAssert.StatusHint("desktop-hint", "Desktop context"))
            .Key("kbF4", "status-line item -> cmNewNote")
            .Checkpoint("note-status", c => App(c).NoteCount == 1 && StatusFirst(c) == "~Alt+F3~ Close note", "note context status items", Everything,
                UiAssert.StatusContext("note-context", SampleHelp.NoteWindow), UiAssert.StatusItem("close-visible", "Alt+F3 Close note"),
                UiAssert.StatusItem("next-visible", "F6 Next"), UiAssert.StatusItem("exit-gone", "Alt+X Exit", "absent"),
                UiAssert.StatusHint("note-hint", "Note context"), UiAssert.Visible("note-window", Id("note-1")),
                UiAssert.Within("note-in-desktop", Id("note-1")))
            .Key("kbAltF3", "status-line item -> cmClose")
            .Checkpoint("desktop-restored", c => App(c).NoteCount == 0 && StatusFirst(c) == "~Alt+X~ Exit", "desktop status items again",
                [Protocol.EvidenceKinds.Status],
                UiAssert.StatusContext("context-restored", Views.hcNoContext), UiAssert.StatusHint("hint-restored", "Desktop context"))
            .Completion(Clean);

    private static TestScenario ResizeScenario() =>
        new TestScenario(Resize, "Logical screen resize", "sample/screen",
                "Opens a note window, resizes the logical screen to 100x30 and then to 60x20 through the NullDriver; the application processes cmScreenResized.")
            .Kind(ScenarioKinds.Screen).Grid(60, 20)
            .Key("kbF4")
            .Checkpoint("before", c => App(c).NoteCount == 1, "note window at the initial grid", Everything,
                UiAssert.WithinScreen("app-in-screen", ViewSelector.OfType("SampleApp")), UiAssert.Within("note-in-desktop", Id("note-1")))
            .Resize(100, 30, "grid change processed by TProgram.HandleScreenResize")
            .Checkpoint("resized", null, "layout after the resize", Everything,
                UiAssert.WithinScreen("desktop-in-screen", ViewSelector.OfType("TDeskTop")),
                UiAssert.Aligned("status-on-last-row", ViewSelector.OfType("TStatusLine"), "bottom", ViewSelector.OfType("SampleApp")),
                UiAssert.Within("note-still-in-desktop", Id("note-1")), UiAssert.StatusItem("status-visible", "Alt+F3 Close note"),
                UiAssert.TextAt("status-on-row-29", 1, 29, "Alt+F3 Close note"))
            .Resize(60, 20)
            .Checkpoint("minimum", null, "layout at the scenario's minimum grid", Everything,
                UiAssert.WithinScreen("desktop-in-small-screen", ViewSelector.OfType("TDeskTop")),
                UiAssert.NotClipped("note-not-clipped", Id("note-1")), UiAssert.Visible("note-visible", Id("note-1")),
                UiAssert.TextAt("status-on-row-19", 1, 19, "Alt+F3 Close note"))
            .Key("kbAltF3")
            .Checkpoint("closed", c => App(c).NoteCount == 0, "note closed at the small grid", [Protocol.EvidenceKinds.Status],
                UiAssert.StatusItem("desktop-items", "Alt+X Exit"))
            .Completion(Clean);

    // Fixture model: explicit setup writes into the controller-owned, disposable working directory; the UI shows it; completion
    // verifies cleanup and the target's isolation. The controller fingerprints the fixture after setup and after exit.
    private static TestScenario FixtureScenario() =>
        new TestScenario(Fixture, "Fixture-backed window", "sample/fixture",
                "Setup writes readme.txt into the disposable working directory and shows it in a window; Alt+F3 closes it; completion checks profile isolation.")
            .Kind(ScenarioKinds.Window).Grid(60, 20)
            .Safety(new ScenarioSafety(FixtureFiles: true, Notes: "Reads readme.txt that its own setup wrote into the controller-owned fixture directory."))
            .Conditions("Runs only inside a controller-created fixture; never touches the real user profile.")
            .Setup(c =>
            {
                string file = Path.Combine(c.WorkingDirectory, "readme.txt");
                File.WriteAllText(file, "Fixture line written by scenario setup.");
                var window = new TWindow(new TRect(2, 1, 52, 8), "Fixture", 1) { growMode = 0 };
                window.Insert(new TStaticText(new TRect(2, 2, 48, 3), File.ReadAllText(file)));
                c.Identify(window, "fixture-window");
                c.Application.DeskTop!.Insert(window);
            })
            .Checkpoint("fixture-shown", null, "fixture content shown in a window", Tree,
                UiAssert.Visible("window-visible", Id("fixture-window")), UiAssert.Within("window-in-desktop", Id("fixture-window")),
                UiAssert.ScreenContains("fixture-text", "Fixture line written by scenario setup."), UiAssert.Modal("no-modal", null))
            .Key("kbAltF3", "Window > Close menu hot key")
            .Checkpoint("closed", c => App(c).NoteCount == 0, "window closed", Tree, UiAssert.Absent("window-gone", Id("fixture-window")))
            .Completion(c => Isolation(c) ?? Clean(c));

    /// <summary>The target runs with per-user directories redirected below its disposable root and without the session secret.</summary>
    private static string? Isolation(TestTargetContext c)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(c.StateDirectory))!;
        if (Environment.GetEnvironmentVariable(TestTargetHost.SecretVariable) is not null) return "Session secret still present in the target environment.";
        foreach (string key in new[] { "HOME", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_DATA_HOME" })
            if (Environment.GetEnvironmentVariable(key) is not { } value || !Path.GetFullPath(value).StartsWith(root, StringComparison.Ordinal))
                return key + " is not redirected below the disposable root.";
        return Path.GetFullPath(c.WorkingDirectory) == Path.GetFullPath(Environment.CurrentDirectory) ? null : "Working directory is not the fixture.";
    }

    /// <summary>Columns of the path line; the window shows ".." plus the path's tail, like a file manager's panel header.</summary>
    internal const int PathColumns = 36;

    // The controller's per-execution fixture directory (tsharp-diagnostics-<32 hex>/fixture) on screen, left-truncated to a
    // fixed width. Everything but the run's identifier is deterministic, so visual baselines need exactly one narrow rule.
    private static TestScenario FixturePathScenario() =>
        new TestScenario(FixturePath, "Fixture path display", "sample/fixture",
                "Shows the disposable working directory, left-truncated to a fixed width, in a window; Alt+F3 closes it.")
            .Kind(ScenarioKinds.Window).Grid(60, 20)
            .Safety(new ScenarioSafety(Notes: "Reads only the path of the controller-owned fixture directory."))
            .Setup(c =>
            {
                string path = Path.GetFullPath(c.WorkingDirectory);
                string shown = path.Length <= PathColumns ? path : ".." + path[^(PathColumns - 2)..];
                var window = new TWindow(new TRect(2, 1, PathColumns + 10, 7), "Working directory", 1) { growMode = 0 };
                window.Insert(new TStaticText(new TRect(2, 2, PathColumns + 2, 3), shown));
                window.Insert(new TStaticText(new TRect(2, 3, PathColumns + 2, 4), "Přípona: žluťoučký kůň"));
                c.Identify(window, "path-window");
                c.Application.DeskTop!.Insert(window);
            })
            .Checkpoint("path-shown", null, "the fixture path and a Czech label in a window", Tree,
                UiAssert.Visible("path-window-visible", Id("path-window")), UiAssert.ScreenContains("fixture-tail", "fixture"),
                UiAssert.ScreenContains("czech-label", "žluťoučký kůň"))
            .Key("kbAltF3", "Window > Close menu hot key")
            .Checkpoint("closed", c => App(c).NoteCount == 0, "window closed", Tree, UiAssert.Absent("path-window-gone", Id("path-window")))
            .Completion(Clean);

    // Self-test: keys submitted while a background producer floods the framework event queue. Queued events take priority
    // over keyboard input, so the key is consumed late; the acknowledgement must still cover it.
    private static TestScenario BurstScenario() =>
        new TestScenario(Burst, "Queued-input burst", "selftest/input",
                "A registered action starts a background producer that queues 40 commands; F3 is submitted while they are pending and a checkpoint follows immediately.")
            .Kind(ScenarioKinds.Mixed).Grid(60, 20).Metadata("purpose", "input acknowledgement self-test")
            .Action("start-burst", c =>
            {
                var driver = c.Driver;
                var producer = new Thread(() =>
                {
                    for (int i = 0; i < 40; i++) { driver.EnqueueCommand(SampleCommands.Tick); if (i % 8 == 7) Thread.Sleep(1); }
                }) { IsBackground = true, Name = "sample-burst" };
                producer.Start();
            }, "background producer: models timers/IO completions, not user input")
            .Key("kbF3")
            .Checkpoint("dialog-after-burst", null, "dialog open although commands were queued ahead of the key", Tree,
                UiAssert.Modal("dialog-modal", Id("sample-dialog")), UiAssert.Focused("input-focused", Id("name-input")))
            .Key("kbEsc")
            .Checkpoint("all-ticks", c => App(c).Ticks == 40, "every queued command processed exactly once", Tree, UiAssert.Modal("closed", null))
            .Completion(c => App(c).Ticks == 40 ? Clean(c) : $"Expected 40 ticks, saw {App(c).Ticks}.");

    // Deliberate failure demonstration: correct UI, wrong expectations. Excluded from bulk runs.
    private static TestScenario FailureScenario() =>
        new TestScenario(Failure, "Deliberately wrong assertions", "selftest/failure",
                "Opens the real dialog and asserts the wrong focus, the wrong title text and a wrong menu depth, to prove failures are detected and reported.")
            .Kind(ScenarioKinds.Dialog).Grid(60, 20).ExpectFailure()
            .Key("kbF3")
            .Checkpoint("dialog-open", c => c.TopView is TDialog, "dialog open", Everything,
                UiAssert.Modal("dialog-is-modal", Id("sample-dialog")),
                UiAssert.Focused("wrong-focus", Id("cancel-button")),
                UiAssert.ScreenContains("wrong-title", "Settings dialog"),
                UiAssert.MenuDepth("wrong-menu-depth", 1),
                UiAssert.Shadow("shadow-without-attribute", Id("sample-dialog"), null).AsWarning())
            .Key("kbEsc")
            .Checkpoint("closed", c => c.NoModalView, "dialog closed", Tree, UiAssert.Modal("no-modal", null))
            .Completion(Clean);

    // Deliberate crash demonstration: an exception escapes the event loop (from a posted event, outside any scenario guard).
    private static TestScenario CrashScenario() =>
        new TestScenario(Crash, "Target crash", "selftest/failure",
                "A registered action posts an event whose handler throws, terminating the target; the controller reports the crash and cleans up.")
            .Kind(ScenarioKinds.Mixed).ExpectFailure()
            .Checkpoint("initial", c => c.NoModalView)
            .Action("crash", c =>
            {
                var bomb = new CrashView();
                c.Application.Insert(bomb);
                bomb.Post(CrashView.Command, null);
            }, "the throw happens later, in TProgram.GetEvent's posted-event delivery")
            .Checkpoint("never")
            .Completion(Clean);

    private sealed class CrashView() : TView(new TRect(0, 0, 0, 0))
    {
        internal const ushort Command = 0x7dd0;
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evCommand && ev.message.command == Command) throw new InvalidOperationException("Deliberate sample crash.");
            base.HandleEvent(ref ev);
        }
    }

    // Deliberate timeout demonstration: an action blocks the UI thread longer than a short operation limit.
    private static TestScenario BlockedScenario() =>
        new TestScenario(Blocked, "Blocked UI thread", "selftest/failure",
                "A registered action blocks the UI thread for 3 s; with a short operation limit the input acknowledgement times out and the run stays bounded.")
            .Kind(ScenarioKinds.Mixed).ExpectFailure()
            .Checkpoint("initial", c => c.NoModalView)
            .Action("block-ui", _ => Thread.Sleep(3000), "simulates a hung handler")
            .Checkpoint("after-block")
            .Completion(Clean);
}
