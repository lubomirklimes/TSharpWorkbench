using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// External scenario management (Scenarios menu, Scenarios window, Add Files/Folder, Remove, Validate,
// Refresh, New Scenario, Open in Editor). The real WorkbenchApp runs on NullDriver; keys and commands go through the shared
// event queue. Documents, projects and runs live in a disposable folder; ProjectLocation.Home points there.
// Optional logical screenshots: TSWB_SCENARIO_MANAGEMENT_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class ScenarioManagementTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;
    private const string FixtureId = "sample/fixture-only/notes";

    // ── A: menu ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScenariosMenuPositionMnemonicsAvailabilityAndKeys()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Wait(bridge, () => app.Start);
            await bridge.Invoke(() =>
            {
                var top = Items(app.MenuBar!.Menu!.Items).ToArray();
                Assert.Equal(["Project", "Scenarios", "Test", "Results", "Baselines", "Window", "Help"], top.Select(i => i.Name!.Replace("~", "")).ToArray());
                Assert.Equal(top.Length, top.Select(i => Mnemonic(i.Name!)).Distinct().Count());
                Assert.Equal('S', Mnemonic(top[1].Name!));
                var items = Items(top[1].SubMenu.Items).Where(i => i.Name is not null).ToArray();
                Assert.Equal(["New scenario...", "Edit scenario...", "Remove scenario...", "Add files...", "Add folder...", "Manage scenarios...", "Validate scenarios", "Refresh catalog"],
                    items.Select(i => i.Name!.Replace("~", "")).ToArray());
                Assert.Equal(items.Length, items.Select(i => Mnemonic(i.Name!)).Distinct().Count());
                // Every top-level mnemonic is free in the Workspace window (the menu bar would take it first).
                foreach (var shortcut in new[] { 'G', 'E', 'C', 'D', 'U', 'L', 'O' }) Assert.DoesNotContain(shortcut, top.Select(i => Mnemonic(i.Name!)));
                // Window menu: Scenarios is window 2, the second entry, Alt+2.
                var window = Items(top[5].SubMenu.Items).Where(i => i.Name is not null).Select(i => i.Name!.Replace("~", "")).ToArray();
                Assert.Equal("Scenarios", window[1]);
                // No project: every Scenarios command is disabled, and keys explain why.
                foreach (ushort c in ScenarioCommands) Assert.False(TView.CommandEnabled(c));
            });
            PressAlt('2'); await Settle(bridge);
            await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Message.StartsWith("No project is open")));
            // Open the menu by keyboard (Alt+S) without a project: it opens; Esc closes.
            Press(Keys.kbAltS); await Until(bridge, () => app.DeskTop!.TopView() is not WorkbenchStartWindow || MenuOpen(app));
            await Shot(bridge, "scenarios-menu-no-project");
            Press(Keys.kbEsc); await Settle(bridge);
            // A project: every Scenarios command is enabled (Edit/Remove explain a missing selection).
            Press(Keys.kbCtrlN); await Wait(bridge, () => Modal(app, "Project Properties")); Press(Keys.kbEsc);
            await Until(bridge, () => app.Project is not null && Modal(app, "Project Properties") is null);
            await Settle(bridge);
            await bridge.Invoke(() =>
            {
                foreach (ushort c in ScenarioCommands) Assert.True(TView.CommandEnabled(c), $"command {c:x}");
                Assert.Contains("none - Scenarios > Add files", app.Project!.HeaderText);
            });
            // Alt+S, M: Manage scenarios opens the Scenarios window (keyboard activation through the menu).
            Press(Keys.kbAltS); await Until(bridge, () => MenuOpen(app));
            await Shot(bridge, "scenarios-menu-project");
            Press('m');
            var window = await Wait(bridge, () => app.Project!.ScenariosWindow);
            await bridge.Invoke(() =>
            {
                Assert.Same(window, app.DeskTop!.current);
                Assert.Empty(window.Items); Assert.Equal("Scenarios: 0 | Ready: 0 | Problems: 0", window.SummaryText);
                Assert.StartsWith("No external scenarios are attached", window.Entries.GetText(0, 200));
            });
            await Shot(bridge, "scenarios-window-empty");
            // F10 still opens the menu bar.
            Press(Keys.kbF10); await Until(bridge, () => MenuOpen(app)); Press(Keys.kbEsc); await Settle(bridge);
            // Alt+2 again: the same window, never a second one.
            await bridge.Invoke(() => app.Project!.Select());
            PressAlt('2'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, window));
            await bridge.Invoke(() => Assert.Single(Children(app.DeskTop!).OfType<WorkbenchScenariosWindow>()));
        }, showStart: true);
        Delete(root);
    }

    // ── B + C: attach files, Save, close, reopen ─────────────────────────────────────

    [Fact]
    public async Task AddFilesValidatesEachFileAndSaveReopenRestoresThem()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string a = Write(docs, "alpha.scenario.json", Proxy("ext/alpha", "Alpha")), b = Write(docs, "beta.scenario.json", Proxy("ext/beta", "Beta"));
        string malformed = Write(docs, "malformed.json", "{ \"schema\": \"tsharpworkbench.scenario\", ");
        string unsupported = Write(docs, "future.json", Proxy("ext/future", "Future").Replace("\"version\": 1", "\"version\": 2"));
        string duplicate = Write(docs, "alpha-copy.json", Proxy("ext/alpha", "Alpha again"));
        string manifest = Write(docs, "visual-baselines.json", """{ "version": 1, "baselines": [] }""");
        string missing = Path.Combine(docs, "missing.json");
        var before = Directory.GetFiles(docs).ToDictionary(f => f, File.ReadAllBytes);
        string project = Path.Combine(home, "Attach", "Attach.twproj");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Wait(bridge, () => app.Start);
            Press(Keys.kbCtrlN);
            var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            Press(Keys.kbAltN); await Settle(bridge); Type("Attach");
            Press(Keys.kbAltE); await Settle(bridge); Type(Target); await Settle(bridge);
            await bridge.Invoke(() => Assert.Contains("none attached - Scenarios > Add files or Add folder", Text()));
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            var main = app.Project!;
            await Wait(bridge, () => main is { Busy: false } ? main : null, 60);
            // Add files: each Add attaches one validated file and reopens the dialog in the same folder; Done ends.
            Send(WorkbenchDialog.AddScenarioFiles);
            var picker = (TFileDialog)await Wait(bridge, () => Modal(app, "Add scenario files"));
            await Shot(bridge, "add-files-dialog");
            await AddFile(app, bridge, a);
            await Wait(bridge, () => Modal(app, "Add scenario files (1 added)"));
            await AddFile(app, bridge, b);
            await Wait(bridge, () => Modal(app, "Add scenario files (2 added)"));
            // Refused files: the project is unchanged, the error names the file and the cause.
            foreach (var (file, cause) in new[] { (a, "already attached"), (duplicate, "Duplicate"), (malformed, "Not valid JSON"),
                (unsupported, "Unsupported"), (manifest, "Not a scenario document"), (missing, "does not exist") })
            {
                await AddFile(app, bridge, file);
                var error = await Wait(bridge, () => Modal(app, "Cannot Add Scenario File"));
                await bridge.Invoke(() => { string text = Text(); Assert.Contains(Path.GetFileName(file), text); Assert.Contains(cause, text); });
                if (cause == "Not valid JSON") await Shot(bridge, "add-files-malformed-error");
                Press(Keys.kbEnter); await Wait(bridge, () => Modal(app, "Add scenario files (2 added)"));
            }
            Press(Keys.kbEsc);   // Done
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.Count == 2 ? main : null, 30);
            await bridge.Invoke(() =>
            {
                Assert.Equal([a, b], main.ScenarioFileList);
                Assert.True(main.Dirty); Assert.Contains(" *", main.title);
                Assert.Contains("attached scenario files (now 2 attached)", main.ChangedGroups());
                Assert.Equal(["Alpha", "Beta"], main.TestList.Strings.ToArray());
                Assert.Contains("2 loaded from 2 files", main.HeaderText);
                Assert.Contains(app.Notifications.History, n => n.Message.StartsWith("Loaded 2 scenarios from 2 files."));
                // Mark one; Save before any run.
                main.TestList.FocusItemNum(1); main.TestList.Toggle("ext/beta");
            });
            Press(Keys.kbCtrlS); await Wait(bridge, () => Modal(app, "Save project as")); Press(Keys.kbEnter);
            await Until(bridge, () => File.Exists(project) && !main.Dirty);
            await bridge.Invoke(() => Command(app, WorkbenchApp.CloseProject));
            await Until(bridge, () => app.Project is null);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
            var reopened = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count == 2 ? p : null, 30);
            await bridge.Invoke(() =>
            {
                Assert.Equal([a, b], reopened.ScenarioFileList); Assert.False(reopened.Dirty);
                Assert.Equal(["ext/beta"], reopened.TestList.MarkedIds.ToArray());
            });
        }, showStart: true);
        using (var json = JsonDocument.Parse(File.ReadAllText(project)))
        {
            Assert.Equal([Path.Combine("..", "..", "docs", "alpha.scenario.json"), Path.Combine("..", "..", "docs", "beta.scenario.json")],
                json.RootElement.GetProperty("scenarioFiles").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal("runs", json.RootElement.GetProperty("outputRoot").GetString());     // Runs default unchanged
            Assert.False(json.RootElement.TryGetProperty("baselineStore", out var store) && store.ValueKind != JsonValueKind.Null);
        }
        Assert.False(Directory.Exists(Path.Combine(home, "Attach", "runs")));
        // Documents are never copied, moved or rewritten.
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(docs).Order());
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        // Relocation: Save as elsewhere recomputes relative references; the documents resolve from the new place.
        string moved = Path.Combine(root, "elsewhere", "deeper", "moved.twproj"); Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        WorkbenchProject.Load(project).Save(moved);
        Assert.Equal([a, b], WorkbenchProject.Load(moved).ScenarioFiles);
        Delete(root);
    }

    // ── D: add folder ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFolderShowsCountsFiltersDuplicatesRecursesAndCancelsCleanly()
    {
        string root = Temp(), home = Path.Combine(root, "home"), suite = Path.Combine(root, "suite"), sub = Path.Combine(suite, "nested");
        Directory.CreateDirectory(sub);
        string one = Write(suite, "one.scenario.json", Proxy("suite/one", "One")), two = Write(suite, "two.scenario.json", Proxy("suite/two", "Two"));
        Write(suite, "run.json", """{ "schema": "TSharpWorkbench.Run", "results": [] }""");
        Write(suite, "visual-baselines.json", """{ "version": 1, "baselines": [] }""");
        Write(suite, "broken.json", "{ nope");
        string three = Write(sub, "three.scenario.json", Proxy("suite/three", "Three"));
        Write(sub, "one-again.json", Proxy("suite/one", "One again"));
        string path = SaveProject(root, "folder.twproj", new WorkbenchProject { Name = "Folder", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [one] });
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 16);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count == 1 ? p : null, 30);
            async Task<WorkbenchAddFolderDialog> Open()
            {
                Send(WorkbenchDialog.AddScenarioFolder);
                var folder = (WorkbenchDirectoryDialog)await Wait(bridge, () => Modal(app, "Select scenario folder"));
                await bridge.Invoke(() => { folder.dirInput!.SetData(suite); folder.dirInput.Focus(); });
                Press(Keys.kbEnter);
                return (WorkbenchAddFolderDialog)await Wait(bridge, () => Modal(app, WorkbenchAddFolderDialog.Title));
            }
            var dialog = await Open();
            await bridge.Invoke(() =>
            {
                // Default: not recursive. two is new, one is attached, broken is rejected, run.json and the manifest are ignored.
                Assert.Equal(0u, dialog.Recursive.value); Assert.False(dialog.Scan.Recursive);
                Assert.Equal([two], dialog.Scan.Add.Select(i => i.Path).ToArray());
                Assert.Equal([one], dialog.Scan.AlreadyAttached.Select(i => i.Path).ToArray());
                Assert.Single(dialog.Scan.Rejected); Assert.Contains("JSON error", dialog.Scan.Rejected[0].Reason);
                Assert.Equal(2, dialog.Scan.Ignored.Length);
                Assert.Contains("subfolders not included", dialog.SummaryText);
                Assert.Contains("1 valid to attach   1 already attached   1 rejected   2 ignored", dialog.SummaryText);
            });
            await Shot(bridge, "add-folder-non-recursive");
            // Recursive: three is found; one-again duplicates an attached ID and is rejected.
            Press(' '); await Settle(bridge);
            await bridge.Invoke(() =>
            {
                Assert.True(dialog.Scan.Recursive);
                Assert.Equal([two, three], dialog.Scan.Add.Select(i => i.Path).ToArray());
                Assert.Contains(dialog.Scan.Rejected, r => r.Reason.StartsWith("Duplicate ID 'suite/one'"));
                Assert.Contains("in this folder and its subfolders", dialog.SummaryText);
            });
            await Shot(bridge, "add-folder-recursive");
            // Cancel: nothing changes.
            Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() => { Assert.Equal([one], main.ScenarioFileList); Assert.False(main.Dirty); });
            // Attach (recursive): order kept, new files appended.
            dialog = await Open();
            Press(' '); await Settle(bridge);
            Press(Keys.kbEnter);
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.Count == 3 ? main : null, 30);
            await bridge.Invoke(() =>
            {
                Assert.Equal([one, two, three], main.ScenarioFileList); Assert.True(main.Dirty);
                Assert.Contains(app.Notifications.History, n => n.Message.Contains("Attached 2 scenario files from suite and its subfolders.") && n.Message.Contains("2 rejected and 2 ignored"));
            });
            // A folder with nothing new: Attach refuses and explains; Cancel closes.
            dialog = await Open();
            await bridge.Invoke(() => Assert.Empty(dialog.Scan.Add));
            Press(Keys.kbEnter); await Settle(bridge);
            await bridge.Invoke(() => { Assert.Same(dialog, app.DeskTop!.TopView()); Assert.StartsWith("Nothing to attach", dialog.SummaryText); });
            Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            await bridge.Invoke(() => Assert.Equal([one, two, three], main.ScenarioFileList));
        });
        Delete(root);
    }

    // ── E: Scenarios window ────────────────────────────────────────────────────────

    [Fact]
    public async Task ScenariosWindowShowsStatusRemovesWithoutDeletingValidatesRefreshesAndReopens()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string good = Write(docs, "good.scenario.json", Proxy("win/good", "Good")), other = Write(docs, "other.scenario.json", Proxy("win/other", "Other"));
        string bad = Write(docs, "bad.json", "[1, 2"), gone = Path.Combine(docs, "gone.json");
        string path = SaveProject(root, "window.twproj", new WorkbenchProject { Name = "Window", Target = Target, OutputRoot = Path.Combine(root, "runs"),
            ScenarioFiles = [good, gone, bad, other], SelectedIds = ["win/good", "win/other"] });
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 16);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count == 2 ? p : null, 30);
            await bridge.Invoke(() =>
            {
                // Tolerant loading: the two good files load; the others are reported, not hidden behind one error.
                Assert.Equal(["win/good", "win/other"], main.TestList.MarkedIds.Order().ToArray());
                Assert.Contains(app.Notifications.History, n => n.Message.StartsWith("Loaded 2 scenarios; 2 of 4 attached files were not loaded (1 Missing, 1 JSON error)."));
            });
            PressAlt('2');
            var window = await Wait(bridge, () => main.ScenariosWindow);
            await bridge.Invoke(() =>
            {
                // Valid Proxy documents whose target (the SampleTarget) has no Proxy deployment are "Valid", not "Ready".
                Assert.Equal(["Valid", "Missing", "JSON error", "Valid"], window.Items.Select(i => i.Status).ToArray());
                Assert.Equal("win/good", window.Selected!.Info.Id);
                Assert.Contains("Black-Box", window.Entries.GetText(0, 120));
                Assert.DoesNotContain(Path.GetFileName(good), window.Entries.GetText(0, 120));
            });
            await Shot(bridge, "scenarios-window-mixed-status");
            // Keyboard: Down selects the missing row; Tab moves to the next control.
            Press(Keys.kbDown); await Settle(bridge);
            await bridge.Invoke(() =>
            {
                Assert.Equal(gone, window.Selected!.Path);
                Assert.Equal("Missing", window.Selected!.Status);
                Assert.True(window.Entries.GetState(Views.sfFocused));
                Assert.Equal(gone, main.SelectedScenarioFile());
                Assert.True(TView.CommandEnabled(WorkbenchDialog.RemoveScenario));
            });
            await Shot(bridge, "scenarios-window-missing-selected");
            Press(Keys.kbTab); await Settle(bridge);
            await bridge.Invoke(() => Assert.False(window.Entries.GetState(Views.sfFocused)));
            Press(Keys.kbShiftTab); await Settle(bridge);
            // Validate (Alt+V): individual causes; focus moves to the first problem.
            Press(Keys.kbAltV); await Settle(bridge);
            await bridge.Invoke(() =>
            {
                var notice = app.Notifications.History.Last(n => n.Message.StartsWith("Validation:"));
                Assert.StartsWith("Validation: 2 of 4 attached files have problems (1 Missing, 1 JSON error).", notice.Message);
                Assert.Contains(gone + ": Missing", notice.Details); Assert.Contains(bad + ": JSON error - Not valid JSON", notice.Details);
                Assert.Equal(gone, window.Selected!.Path);
            });
            // Remove (Del): the confirmation's default is Cancel; nothing changes.
            Press(Keys.kbDel);
            await Wait(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle));
            await bridge.Invoke(() => Assert.Contains("The JSON file is NOT deleted", Text()));
            await Shot(bridge, "remove-confirmation");
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle) is null);
            await bridge.Invoke(() => Assert.Equal(4, main.ScenarioFileList.Length));
            // Remove the bad file (Alt+R in the confirmation): only the reference goes; the file stays.
            await bridge.Invoke(() => window.Focus(bad));
            Press(Keys.kbDel); await Wait(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle)); Press(Keys.kbAltR);
            await Until(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle) is null && main.ScenarioFileList.Length == 3);
            await bridge.Invoke(() =>
            {
                Assert.Equal([good, gone, other], main.ScenarioFileList); Assert.True(File.Exists(bad)); Assert.True(main.Dirty);
                Assert.Equal(other, window.Selected!.Path);   // the next file keeps the selection position
            });
            // Removing a marked scenario: its mark is dropped with a notice, never remapped.
            await bridge.Invoke(() => window.Focus(good));
            Press(Keys.kbDel); await Wait(bridge, () => Modal(app, WorkbenchDialog.RemoveTitle)); Press(Keys.kbAltR);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.Count == 1 ? main : null, 30);
            await bridge.Invoke(() =>
            {
                Assert.Equal(["win/other"], main.TestList.MarkedIds.ToArray()); Assert.True(File.Exists(good));
                Assert.Contains(app.Notifications.History, n => n.Message.Contains("Missing/stale scenario IDs were removed from the marks: win/good"));
            });
            // Refresh (F2) reloads a changed document; the mark of the unchanged ID is kept.
            File.WriteAllText(other, Proxy("win/other", "Other, edited"));
            Press(Keys.kbF2);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.FirstOrDefault() == "Other, edited" ? main : null, 30);
            await bridge.Invoke(() => { Assert.Equal(["win/other"], main.TestList.MarkedIds.ToArray()); Assert.Equal("Other, edited", window.Items.Last().Info.Document!.Scenario.Name); });
            // Close (Esc) and reopen from the Window menu command: one window. A moved window's geometry is saved on
            // close (user settings) and restored on reopen, like Results, Comparison, Messages and the Run Monitor.
            var moved = new TRect(1, 1, 1 + window.size.x, 1 + window.size.y);
            await bridge.Invoke(() => window.Locate(moved));
            Press(Keys.kbEsc); await Until(bridge, () => main.ScenariosWindow is null);
            await bridge.Invoke(() => Assert.Equal(moved, WorkbenchPreferences.Load(app.PreferencesPath).Window("scenarios")));
            Send(WorkbenchDialog.ManageScenarios); Send(WorkbenchDialog.ManageScenarios);
            var again = await Wait(bridge, () => main.ScenariosWindow);
            await Settle(bridge);
            await bridge.Invoke(() => { Assert.Single(Children(app.DeskTop!).OfType<WorkbenchScenariosWindow>()); Assert.Equal(2, again.Items.Length); Assert.Equal(moved, again.GetBounds()); });
        });
        Assert.True(File.Exists(good) && File.Exists(bad) && File.Exists(other));
        Delete(root);
    }

    // ── E/H: layout at the three grids ─────────────────────────────────────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task ScenariosWindowAndDialogsFitEachGrid(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string[] files = Enumerable.Range(1, 6).Select(i => Write(docs, $"flow-{i}.scenario.json", Proxy($"grid/flow-{i}", $"Flow {i} with a longer readable title"))).ToArray();
        string bad = Write(docs, "unsupported.json", Proxy("grid/x", "X").Replace("\"version\": 1", "\"version\": 3"));
        string path = SaveProject(root, "grid.twproj", new WorkbenchProject { Name = "Grid", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [.. files, bad] });
        await Drive(width, height, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 16);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count == 6 ? p : null, 30);
            PressAlt('2');
            var window = await Wait(bridge, () => main.ScenariosWindow);
            await Settle(bridge);
            await bridge.Invoke(() =>
            {
                var desk = app.DeskTop!.GetExtent();
                var bounds = window.GetBounds();
                Assert.True(bounds.a.x >= 0 && bounds.a.y >= 0 && bounds.b.x <= desk.b.x && bounds.b.y <= desk.b.y);
                var views = Children(window).Where(v => v is not TFrame).ToArray();
                foreach (var v in views) { var r = v.GetBounds(); Assert.True(r.a.x >= 1 && r.a.y >= 1 && r.b.x <= window.size.x - 1 && r.b.y <= window.size.y, v.GetType().Name); }
                var buttons = views.OfType<TButton>().Select(b => b.GetBounds()).OrderBy(r => r.a.y).ThenBy(r => r.a.x).ToArray();
                Assert.Equal(8, buttons.Length);
                for (int i = 1; i < buttons.Length; i++) Assert.True(buttons[i].a.y >= buttons[i - 1].b.y || buttons[i].a.x >= buttons[i - 1].b.x, "buttons overlap");
                Assert.True(window.Entries.size.y >= 7, "list rows");
                Assert.Single(Children(window).OfType<TListBox>());
                Assert.Equal(7, window.Items.Length);
                Assert.Equal("Unsupported", window.Items.Last().Status);
            });
            await Shot(bridge, "scenarios-window-seven-files");
            await bridge.Invoke(() => window.Focus(bad));
            await Shot(bridge, "scenarios-window-unsupported-selected");
            // Resize by keyboard is the framework's; the window grows with the desktop (Zoom) and keeps its layout.
            await bridge.Invoke(() => window.Zoom());
            await Settle(bridge);
            await bridge.Invoke(() => Assert.True(window.Entries.size.y > 7));
            await bridge.Invoke(() => window.Zoom());
            // New Scenario dialog and Add Folder dialog fit too.
            Send(WorkbenchDialog.NewScenario);
            var create = (WorkbenchNewScenarioDialog)await Wait(bridge, () => Modal(app, WorkbenchNewScenarioDialog.Title));
            await bridge.Invoke(() => { var r = create.GetBounds(); var d = app.DeskTop!.GetExtent(); Assert.True(r.a.x >= 0 && r.b.x <= d.b.x && r.b.y <= d.b.y); });
            await Shot(bridge, "new-scenario-proxy");
            Press(Keys.kbEsc); await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
        });
        Delete(root);
    }

    // ── F: execution integration ─────────────────────────────────────────────────

    // Fixture-bound: New Scenario writes a document bound to the SampleTarget's fixture-only registration (its first
    // advertised checkpoint expectation), attaches it, and it runs. A document naming an unadvertised expectation is
    // shown as Unbound with the actionable cause (typed, strict binding preserved).
    [Fact]
    public async Task FixtureBoundScenarioIsCreatedBoundAndRun()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "docs"), runs = Path.Combine(root, "runs");
        Directory.CreateDirectory(docs);
        string strict = Write(docs, "made-up.scenario.json", Fixture("ext/made-up", "made-up"));
        string path = SaveProject(root, "fixture.twproj", new WorkbenchProject { Name = "Fixture", Target = Target, OutputRoot = runs, ScenarioFiles = [strict] });
        string created = Path.Combine(docs, "notes-idle.scenario.json");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 16);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
            await bridge.Invoke(() =>
            {
                var state = Assert.Single(main.AttachedScenarios);
                Assert.Equal("Unbound", state.Status);
                Assert.Contains("checkpoint 'made-up' does not bind to an advertised checkpoint expectation", state.Problem);
                Assert.DoesNotContain(main.TestList.Strings, n => n.Contains("Notes fixture"));   // the bare fixture-only registration is not runnable
                // Compiled self-tests, including the intentional negative ones, are unchanged.
                Assert.Contains(main.TestList.Strings, n => n.Length > 0);
            });
            // New Scenario: fixture-bound, the advertised fixture, then a Save as-style file choice.
            Send(WorkbenchDialog.NewScenario);
            var dialog = (WorkbenchNewScenarioDialog)await Wait(bridge, () => Modal(app, WorkbenchNewScenarioDialog.Title));
            await bridge.Invoke(() =>
            {
                dialog.Kind.value = 1; dialog.Kind.DrawView();
                dialog.IdField.SetData("ext/notes-idle"); dialog.TitleField.SetData("Notes idle");
                Nudge(dialog);
                Assert.Equal(FixtureId, dialog.Fixture?.Id);
            });
            await Shot(bridge, "new-scenario-fixture-bound");
            Press(Keys.kbEnter);
            var save = (TFileDialog)await Wait(bridge, () => Modal(app, "Save new scenario as"));
            await bridge.Invoke(() => { Assert.EndsWith("notes-idle.scenario.json", save.fileName!.Data); save.fileName.SetData(created); });
            Press(Keys.kbEnter);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.Contains("Notes idle") ? main : null, 60);
            await bridge.Invoke(() =>
            {
                Assert.Equal([strict, created], main.ScenarioFileList);
                Assert.Equal("Bound", main.AttachedScenarios[1].Status);
                Assert.Equal(FixtureId, main.AttachedScenarios[1].Bound!.External!.FixtureId);
                Assert.StartsWith(ExternalScenarios.ContractFingerprintPrefix, main.AttachedScenarios[1].Bound!.External!.FixtureHash);
            });
            using (var json = JsonDocument.Parse(File.ReadAllText(created)))
            {
                Assert.Equal(FixtureId, json.RootElement.GetProperty("fixture").GetString());
                Assert.Equal("idle", json.RootElement.GetProperty("steps")[0].GetProperty("id").GetString());
                Assert.False(json.RootElement.TryGetProperty("backend", out _));
            }
            // Run it (marked, F5, Run).
            await bridge.Invoke(() => { main.TestList.ClearMarks(); main.TestList.Toggle("ext/notes-idle"); });
            Press(Keys.kbF5);
            await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
            Press(Keys.kbEnter);
            await Until(bridge, () => main.Busy, 30);
            await Until(bridge, () => !main.Busy, 120);
            await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Message.StartsWith("Run finished: PASSED")));
        });
        var batch = LastRun(runs);
        var result = Assert.Single(batch.Results);
        Assert.Equal("ext/notes-idle", result.ScenarioId); Assert.Equal(ScenarioOutcomes.Passed, result.Outcome);
        Delete(root);
    }

    // Proxy: an ordinary application (TVDemo) with no files explains the empty catalog at once (no 45 s handshake wait,
    // no recompilation); a real Proxy document attached from disk and a New Scenario Proxy template both run.
    [TvDemoFact]
    public async Task ProxyScenarioOnTvDemoRunsWithoutSpecialCompilation()
    {
        string root = Temp(), home = Path.Combine(root, "home"), runs = Path.Combine(root, "runs");
        var tvdemo = WorkbenchProject.Load(TvDemoProject);
        string about = tvdemo.ScenarioFiles.Single(f => f.EndsWith("about.scenario.json", StringComparison.Ordinal));
        string path = SaveProject(root, "ordinary.twproj", new WorkbenchProject { Name = "Ordinary", Target = tvdemo.Target, OutputRoot = runs, ScreenSizes = ["80x25"] });
        string smoke = Path.Combine(root, "smoke.scenario.json");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 16);
            var started = DateTime.UtcNow;
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 20);
            await bridge.Invoke(() =>
            {
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "an ordinary application without files is not launched for discovery");
                Assert.Contains(app.Notifications.History, n => n.Message.StartsWith(ScenarioFiles.NoneAttached) && n.Message.Contains("does not need to be recompiled"));
                Assert.Contains("none - Scenarios > Add files", main.HeaderText);
                Assert.Contains(Lines(main.DetailPane), l => l.StartsWith("No external scenarios are attached."));
            });
            await Shot(bridge, "workspace-empty-catalog-ordinary-app");
            Send(WorkbenchDialog.AddScenarioFiles);
            await Wait(bridge, () => Modal(app, "Add scenario files"));
            await AddFile(app, bridge, about);
            await Wait(bridge, () => Modal(app, "Add scenario files (1 added)")); Press(Keys.kbEsc);
            // New Scenario: Proxy logical template, attached and loaded at once (no target launch for Proxy discovery).
            await Until(bridge, () => app.DeskTop!.TopView() is not TDialog);
            Send(WorkbenchDialog.NewScenario);
            var dialog = (WorkbenchNewScenarioDialog)await Wait(bridge, () => Modal(app, WorkbenchNewScenarioDialog.Title));
            await bridge.Invoke(() => { Assert.True(dialog.Proxy); Assert.True(dialog.Fixtures.GetState(Views.sfDisabled)); });
            Type("tvdemo/smoke"); Press(Keys.kbTab); Type("TVDemo first screen"); Press(Keys.kbEnter);
            var save = (TFileDialog)await Wait(bridge, () => Modal(app, "Save new scenario as"));
            await bridge.Invoke(() => save.fileName!.SetData(smoke));
            Press(Keys.kbEnter);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.Count == 2 ? main : null, 30);
            await bridge.Invoke(() =>
            {
                Assert.Equal(["Ready", "Ready"], main.AttachedScenarios.Select(a => a.Status).ToArray());
                main.TestList.ClearMarks(); main.TestList.Toggle("tvdemo/about"); main.TestList.Toggle("tvdemo/smoke");
            });
            Press(Keys.kbF5);
            await Wait(bridge, () => app.DeskTop!.TopView() as WorkbenchRunDialog);
            Press(Keys.kbEnter);
            await Until(bridge, () => main.Busy, 30);
            await Until(bridge, () => !main.Busy, 180);
        });
        var batch = LastRun(runs);
        Assert.Equal(["tvdemo/about", "tvdemo/smoke"], batch.Results.Select(r => r.ScenarioId).Order().ToArray());
        Assert.All(batch.Results, r => Assert.Equal(ScenarioOutcomes.Passed, r.Outcome));
        using (var json = JsonDocument.Parse(File.ReadAllText(smoke))) Assert.Equal("proxy-logical", json.RootElement.GetProperty("backend").GetString());
        Delete(root);
    }

    // ── G: regression ──────────────────────────────────────────────────────────

    // Legacy projects (TVDemo example with relative scenarioFiles; a compiled-only SampleTarget project) load unchanged; the
    // compiled negative self-tests keep their expected-failure role; the CLI stays strict (one bad file fails discovery).
    [Fact]
    public async Task LegacyProjectsAndCompiledSelfTestsAreUnchanged()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        string example = Path.Combine(RepositoryRoot(), "examples", "TVDemo", "tvdemo.twproj");
        var legacy = WorkbenchProject.Load(example);
        Assert.Equal(6, legacy.ScenarioFiles.Length);
        Assert.All(legacy.ScenarioFiles, f => Assert.True(File.Exists(f)));
        Assert.All(ScenarioFiles.Check(legacy.ScenarioFiles, "", null, false), a => Assert.Equal("No target", a.Status));
        string compiledOnly = SaveProject(root, "compiled.twproj", new WorkbenchProject { Name = "Compiled", Target = Target, OutputRoot = Path.Combine(root, "runs") });
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge, 16);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(compiledOnly), compiledOnly));
            var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
            await bridge.Invoke(() =>
            {
                Assert.Empty(main.AttachedScenarios);
                Assert.Contains("loaded", main.HeaderText); Assert.DoesNotContain("none - Scenarios", main.HeaderText);
                Assert.DoesNotContain(app.Notifications.History, n => n.Message.StartsWith(ScenarioFiles.NoneAttached));
                Assert.True(ScenarioFiles.HostsDiagnostics(Target));
            });
        });
        var compiled = await new ScenarioRunner().DiscoverAsync(Target);
        Assert.Contains(compiled, s => s.ExpectedResult == ScenarioOutcomes.Failed);   // intentional compiled negative self-tests
        // The library (CLI) path stays strict: a malformed attached file fails discovery as before.
        string bad = Write(root, "bad.json", "{");
        await Assert.ThrowsAsync<ExternalScenarioException>(() => new ScenarioRunner().DiscoverRawAsync(Target, [bad]));
        Delete(root);
    }

    // Folder scanning and document classification without the UI.
    [Fact]
    public void DocumentClassificationAndTemplates()
    {
        string root = Temp(); Directory.CreateDirectory(root);
        Assert.Equal(ScenarioFileStatus.Missing, ScenarioFiles.Inspect(Path.Combine(root, "none.json")).Status);
        Assert.Equal(ScenarioFileStatus.Missing, ScenarioFiles.Inspect(root).Status);
        Assert.Equal(ScenarioFileStatus.JsonError, ScenarioFiles.Inspect(Write(root, "a.json", "{,}")).Status);
        Assert.Equal(ScenarioFileStatus.NotScenario, ScenarioFiles.Inspect(Write(root, "b.json", "[]")).Status);
        Assert.Equal(ScenarioFileStatus.NotScenario, ScenarioFiles.Inspect(Write(root, "c.json", """{"schema":"TSharpVision.TestWorkbench","version":1}""")).Status);
        Assert.Equal(ScenarioFileStatus.UnsupportedSchema, ScenarioFiles.Inspect(Write(root, "d.json", """{"schema":"tsharpworkbench.scenario","version":2}""")).Status);
        var invalid = ScenarioFiles.Inspect(Write(root, "e.json", Proxy("x/y", "Y").Replace("\"passed\"", "\"maybe\"")));
        Assert.Equal(ScenarioFileStatus.Invalid, invalid.Status); Assert.Contains("expectedOutcome", invalid.Message); Assert.DoesNotContain(root, invalid.Message);
        // Mixed kinds: the first valid document decides; the other kind is refused with the reason.
        string p = Write(root, "p.json", Proxy("k/p", "P")), f = Write(root, "f.json", Fixture("k/f", "idle"));
        var mixed = ScenarioFiles.Check([p, f], Target, null, false);
        Assert.Equal(["Valid", "Mixed"], mixed.Select(m => m.Status).ToArray());   // No Proxy beside the SampleTarget
        // Proxy compatibility: the diagnostic SampleTarget folder has no proxy decorator deployment; the note says so.
        Assert.Equal(File.Exists(Path.Combine(Path.GetDirectoryName(Target)!, "TSharpWorkbench.Proxy.dll")), mixed[0].Note is null);
        if (mixed[0].Note is { } note) Assert.Contains("TSharpWorkbench.Proxy.dll", note);
        Assert.Equal(["Unbound"], ScenarioFiles.Check([f], Target, null, true).Select(m => m.Status).ToArray());   // ordinary target
        Assert.Equal(["Not bound"], ScenarioFiles.Check([f], Target, null, false).Select(m => m.Status).ToArray());
        // Templates are valid schema-v1 documents.
        string proxy = Write(root, "t1.json", ScenarioFiles.Template("t/one", "One", "external", null));
        Assert.Equal("proxy-logical", ExternalScenarios.Load(proxy).Scenario.Backend);
        var hosts = ScenarioFiles.HostsDiagnostics(Target);
        Assert.True(hosts);
        Delete(root);
    }

    // Open in Editor: an explicitly configured editor gets the path as one argument (spaces safe, no shell).
    [Fact]
    public async Task OpenInEditorPassesThePathAsOneArgument()
    {
        string root = Temp(), home = Path.Combine(root, "home"), docs = Path.Combine(root, "with space");
        Directory.CreateDirectory(docs);
        string doc = Write(docs, "my scenario.json", Proxy("ed/one", "One"));
        string path = SaveProject(root, "editor.twproj", new WorkbenchProject { Name = "Editor", Target = Target, OutputRoot = Path.Combine(root, "runs"), ScenarioFiles = [doc] });
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        var previous = WorkbenchDialog.EditorLauncher; string? variable = Environment.GetEnvironmentVariable(ScenarioFiles.EditorVariable);
        WorkbenchDialog.EditorLauncher = s => { started.Add(s); return true; };
        Environment.SetEnvironmentVariable(ScenarioFiles.EditorVariable, "\"C:\\Tools\\My Editor\\edit.exe\"");
        try
        {
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await Loaded(app, bridge, 16);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count == 1 ? p : null, 30);
                // From the Workspace: the focused external scenario is the selection.
                await bridge.Invoke(() => { main.Select(); Assert.Equal(doc, main.SelectedScenarioFile()); });
                Send(WorkbenchDialog.OpenScenarioExternal); await Settle(bridge);
                WorkbenchDialog.EditorLauncher = _ => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified.");
                Send(WorkbenchDialog.OpenScenarioExternal);
                await Wait(bridge, () => Modal(app, "Cannot Open Editor"));
                await bridge.Invoke(() => Assert.Contains(ScenarioFiles.EditorVariable, Text()));
                Press(Keys.kbEnter);
            });
        }
        finally { WorkbenchDialog.EditorLauncher = previous; Environment.SetEnvironmentVariable(ScenarioFiles.EditorVariable, variable); }
        var start = Assert.Single(started);
        Assert.Equal("C:\\Tools\\My Editor\\edit.exe", start.FileName); Assert.False(start.UseShellExecute);
        Assert.Equal([doc], start.ArgumentList.ToArray());
        Delete(root);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static readonly ushort[] ScenarioCommands = [WorkbenchDialog.NewScenario, WorkbenchDialog.AddScenarioFiles, WorkbenchDialog.AddScenarioFolder,
        WorkbenchDialog.ManageScenarios, WorkbenchDialog.OpenScenario, WorkbenchDialog.RemoveScenario, WorkbenchDialog.ValidateScenarios];

    private static string Proxy(string id, string title) => $$"""
        {
          "schema": "tsharpworkbench.scenario",
          "version": 1,
          "id": "{{id}}",
          "title": "{{title}}",
          "description": "Test document.",
          "group": "{{id.Split('/')[0]}}",
          "backend": "proxy-logical",
          "expectedOutcome": "passed",
          "steps": [ { "kind": "checkpoint", "id": "initial" } ]
        }
        """;
    private static string Fixture(string id, string checkpoint) =>
        $$"""{"schema":"tsharpworkbench.scenario","version":1,"id":"{{id}}","title":"{{id}}","description":"d","group":"external","fixture":"{{FixtureId}}","expectedOutcome":"passed","steps":[{"kind":"checkpoint","id":"{{checkpoint}}"}]}""";
    private static string Write(string folder, string name, string text) { string path = Path.Combine(folder, name); File.WriteAllText(path, text); return path; }

    private static async Task AddFile(WorkbenchApp app, Bridge bridge, string file)
    {
        await bridge.Invoke(() => { var picker = (TFileDialog)app.DeskTop!.TopView()!; picker.fileName!.SetData(file); picker.fileName.Focus(); });
        Press(Keys.kbEnter);
        await Settle(bridge);
    }
    private static string[] Lines(TListBox list) => Enumerable.Range(0, list.range).Select(i => list.GetText(i, 1000)).ToArray();
    private static IEnumerable<TMenuItem> Items(TMenuItem? first) { for (var i = first; i is not null; i = i.Next) yield return i; }
    private static char Mnemonic(string name) { int i = name.IndexOf('~'); return char.ToUpperInvariant(name[i + 1]); }
    private static bool MenuOpen(WorkbenchApp app) => Children(app.DeskTop!.owner!).Any(v => v is TMenuBox) || app.DeskTop!.owner!.current is TMenuView or TMenuBox;
    private static BatchResult LastRun(string runs) =>
        WorkbenchRunHistory.Load(Path.Combine(Directory.GetDirectories(runs, "run-*").OrderByDescending(Directory.GetCreationTimeUtc).First(), "run.json")).Batch;

    public sealed class TvDemoFactAttribute : FactAttribute
    {
        public TvDemoFactAttribute() { if (!File.Exists(TvDemoProject)) Skip = "Run scripts/prepare-tvdemo.ps1 first."; }
    }
    private static string TvDemoProject => Environment.GetEnvironmentVariable("TSW_TVDEMO_PROJECT") is { Length: > 0 } custom ? custom
        : Path.Combine(RepositoryRoot(), "artifacts", "tvdemo", "current", "tvdemo.twproj");
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body, bool showStart = false)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            using var app = new WorkbenchApp(showStart ? "" : Target, output, new(showStart ? "" : Target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") }, showStart: showStart);
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        if (app.Project is { Busy: true }) Command(app.Project, WorkbenchDialog.CancelRun);
                        app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                    }).WaitAsync(TimeSpan.FromSeconds(20));
                }
            });
            AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(240)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge, int count) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= count ? p : null, 60);
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "mux1b-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    // Logical screenshots (NullDriver cell screen of the real WorkbenchApp, diagnostic bitmap font; not desktop-native).
    private static async Task Shot(Bridge bridge, string state)
    {
        await Settle(bridge);
        if (Environment.GetEnvironmentVariable("TSWB_SCENARIO_MANAGEMENT_EVIDENCE") is not { Length: > 0 } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string name = $"mux1b-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(ScenarioManagementTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Press(char letter) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = letter; ev.keyDown.charScan = new CharScanType(letter); TEventQueue.Enqueue(ev); }
    private static void PressAlt(char digit) => Press((ushort)(Keys.kbAlt1 + (digit - '1') * 0x100));
    private static void Type(string text) { foreach (char c in text) Press(c); }
    private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    private static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(60); await bridge.Invoke(() => { }); }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
