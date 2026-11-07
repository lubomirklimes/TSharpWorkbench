using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Application shell, Start window, Workspace, Project Properties, notifications, view state and the
// Unsaved Project Changes dialog. The real WorkbenchApp runs on NullDriver at Workbench grids (not target grids);
// keys go through the shared event queue so status line, menu bar and modal loops see them as a user would.
[Collection("NonParallel")]
public sealed class WorkspaceUxTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;

    // ── A. shell, menus, keyboard routing and window numbers ─────────────────────────

    [Fact]
    public void MenuTreeShortcutsAndWorkspaceMnemonicsFollowTheApprovedShell()
    {
        using var driver = new DriverScope(140, 40);
        using var app = new WorkbenchApp("", Temp(), new("", Temp(), null, "80x25"));
        var top = Items(app.MenuBar!.Menu!).ToArray();
        Assert.Equal(["~P~roject", "~S~cenarios", "~T~est", "~R~esults", "~B~aselines", "~W~indow", "~H~elp"], top.Select(i => i.Name ?? "").ToArray());
        var all = top.SelectMany(t => Items(t.SubMenu)).Where(i => i.Name is not null).ToArray();
        string Shortcut(ushort command) => all.Where(i => i.Command == command).Select(i => i.Param ?? "").Distinct().Single();   // Refresh (F2) is in Test and Scenarios
        Assert.Equal("Ctrl+N", Shortcut(WorkbenchApp.NewProject)); Assert.Equal("Ctrl+O", Shortcut(WorkbenchApp.OpenProject));
        Assert.Equal("Ctrl+S", Shortcut(WorkbenchApp.SaveProject)); Assert.Equal("Alt+X", Shortcut(Views.cmQuit));
        Assert.Equal("F5", Shortcut(WorkbenchDialog.Run)); Assert.Equal("F2", Shortcut(WorkbenchDialog.Load));
        Assert.Equal("F1", Shortcut(WorkbenchApp.Shortcuts)); Assert.Equal("Alt+F3", Shortcut(Views.cmClose)); Assert.Equal("Ctrl+F5", Shortcut(Views.cmResize));
        Assert.Equal(["Alt+1", "Alt+3", "Alt+4", "Alt+5", "Alt+6"],
            new[] { WorkbenchApp.ProjectWindow, WorkbenchDialog.ProgressWindow, WorkbenchDialog.Results, WorkbenchApp.ComparisonWindow, WorkbenchApp.MessagesWindow }
                .Select(c => all.Where(i => i.Command == c).Select(i => i.Param ?? "").Single(p => p.StartsWith("Alt+"))).ToArray());
        Assert.Equal("F6", all.First(i => i.Command == WorkbenchDialog.Results).Param);
        // F3/F4 are no longer global commands; no key code is bound twice.
        Assert.DoesNotContain(all, i => i.KeyCode is Keys.kbF3 or Keys.kbF4);
        var keys = all.Where(i => i.KeyCode != Keys.kbNoKey).Select(i => i.KeyCode).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.DoesNotContain(all, i => i.Command is WorkbenchDialog.BrowseTarget or WorkbenchDialog.BrowseOutput);
        // Every implemented feature keeps a menu home (approve and replace are one Review candidate... item).
        foreach (ushort command in new[] { WorkbenchDialog.CancelRun, WorkbenchDialog.Details, WorkbenchDialog.ToggleMark, WorkbenchDialog.SelectGroup,
            WorkbenchDialog.MarkAll, WorkbenchDialog.ClearSelection, WorkbenchDialog.OpenRun, WorkbenchDialog.OpenCaptures,
            WorkbenchDialog.OpenOutput, WorkbenchDialog.CompareResult, WorkbenchDialog.ReviewCandidates, WorkbenchDialog.Rerun,
            WorkbenchDialog.EarlierRun, WorkbenchDialog.LaterRun, WorkbenchDialog.BaselineBrowse, WorkbenchDialog.Advanced, WorkbenchApp.TestModes, WorkbenchApp.About,
            WorkbenchApp.SaveAsProject, WorkbenchApp.CloseProject, Views.cmNext, Views.cmPrev, Views.cmZoom })
            Assert.Contains(all, i => i.Command == command);
        Assert.Contains(all, i => i.Name == "~A~bout TSharpWorkbench");
        Assert.DoesNotContain(all, i => i.Name!.Contains("TestWorkbench"));

        // No control of the modeless Workspace uses a menu-bar letter as its mnemonic.
        var main = new WorkbenchDialog(app.DeskTop!, "", Temp());
        string[] reserved = ["P", "T", "R", "B", "W", "H", "X"];
        foreach (string text in Children(main).Select(Label).Where(t => t.Contains('~')))
            Assert.DoesNotContain(text.Split('~')[1].ToUpperInvariant(), reserved);
        main.Close();
    }

    [Fact]
    public async Task KeysWindowNumbersHintsAndDisabledCommandsRouteThroughTheApplication()
    {
        string root = Temp();
        try
        {
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                Press(Keys.kbF1); var keys = await Wait(bridge, () => app.HelpWindow);
                await bridge.Invoke(() => Assert.Contains("Project / Workspace", Text()));
                await Dismiss(bridge, app, keys);
                Press(Keys.kbF6); await Until(bridge, () => StatusRow().Contains("! No results yet"));
                await bridge.Invoke(() => Assert.Contains(app.Notifications.History, n => n.Level == NoticeLevel.Warning && n.Message.StartsWith("No results yet")));
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() =>
                {
                    Assert.Equal(6, messages.number); Assert.Same(messages, app.DeskTop!.current);
                    Assert.Contains("No results yet", string.Join("\n", Rows(messages.Entries)));
                });
                PressAlt('1'); await Until(bridge, () => ReferenceEquals(app.DeskTop!.current, main));
                // Idle Alt+3 opens Run Configuration (never a run); Esc changes nothing.
                PressAlt('3'); var config = await Wait(bridge, () => app.DeskTop!.TopView() is TDialog d && d.title is WorkbenchRunDialog.DialogTitle or WorkbenchRunBasicDialog.DialogTitle ? d : null);
                Press(Keys.kbEsc); await Until(bridge, () => !ReferenceEquals(app.DeskTop!.TopView(), config));
                await bridge.Invoke(() => Assert.False(main.Busy));
                PressAlt('5'); await Until(bridge, () => StatusRow().Contains("! No baseline comparison is open"));
                // Help dialogs: Test modes and About are static, platform-aware text with the product name.
                Send(WorkbenchApp.TestModes); var modes = await Wait(bridge, () => app.HelpWindow);
                await bridge.Invoke(() => Assert.Contains("Native driver", Text()));
                await Dismiss(bridge, app, modes);
                Send(WorkbenchApp.About); var about = await Wait(bridge, () => Modal(app, "About TSharpWorkbench"));
                await bridge.Invoke(() => { Assert.Contains("TSharpWorkbench", Text()); Assert.Contains("Version ", Text()); Assert.DoesNotContain("TW-6", Text()); });
                await Dismiss(bridge, app, about);
                // F1 inside a modal dialog opens the reference on top; Esc returns to the dialog.
                Send(WorkbenchDialog.Advanced); var properties = await Wait(bridge, () => Modal(app, "Project Properties"));
                Press(Keys.kbF1); keys = await Wait(bridge, () => app.HelpWindow);
                await Dismiss(bridge, app, keys); await Wait(bridge, () => Modal(app, "Project Properties"));
                await Dismiss(bridge, app, properties);
                // Without a project, project commands are disabled and their keys explain why.
                await bridge.Invoke(() => { app.Messages?.Close(); main.CloseApproved(); });
                await Until(bridge, () => app.Project is null);
                await Settle(bridge);
                Press(Keys.kbF5); await Until(bridge, () => StatusRow().Contains("! No project is open"));
                await bridge.Invoke(() => { Assert.False(TSharpVision.TView.CommandEnabled(WorkbenchDialog.Run)); Assert.Contains("Ctrl+N New project", StatusRow()); });
            });
        }
        finally { Delete(root); }
    }

    // ── B. Start window and recent projects ──────────────────────────────────────────

    [Fact]
    public void RecentProjectsAreNewestFirstDeduplicatedAndLimitedToEight()
    {
        var p = new WorkbenchPreferences("", "", null, "80x25");
        for (int i = 0; i < 10; i++) p = p.WithRecent(Path.Combine(Path.GetTempPath(), $"p{i}.twproj"));
        p = p.WithRecent(Path.Combine(Path.GetTempPath(), "x", "..", "p5.twproj"));
        Assert.Equal(8, p.RecentProjects.Length);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "p5.twproj"), p.RecentProjects[0]);
        Assert.Single(p.RecentProjects, r => r.EndsWith("p5.twproj"));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "p9.twproj"), p.RecentProjects[1]);
        string root = Temp(), path = Path.Combine(root, "settings.json");
        try
        {
            // Older settings files without the fields still load; new fields round-trip.
            Directory.CreateDirectory(root);
            File.WriteAllText(path, """{"Target":"a.exe","Output":"out","Group":null,"Size":"80x25","SelectedIds":["one"],"Search":"s"}""");
            var old = WorkbenchPreferences.Load(path);
            Assert.Equal("a.exe", old.Target); Assert.Empty(old.RecentProjects); Assert.Empty(old.ViewStates);
            WorkbenchPreferences.Update(path, x => x.WithRecent(Path.Combine(root, "a.twproj")).WithViewState(Path.Combine(root, "a.twproj"), new("q", "g", ["c"], true)));
            var loaded = WorkbenchPreferences.Load(path);
            Assert.Equal(["one"], loaded.SelectedIds); Assert.Equal("s", loaded.Search);
            Assert.Equal(new WorkbenchViewState("q", "g", ["c"], true) with { CollapsedGroups = loaded.ViewState(Path.Combine(root, "a.twproj"))!.CollapsedGroups },
                loaded.ViewState(Path.Combine(root, "A.TWPROJ")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task StartWindowListsRecentProjectsOpensOneAndHandlesMissingEntries()
    {
        string root = Temp();
        try
        {
            string project = SaveProject(root, "start.twproj", new WorkbenchProject { Name = "Start sample", Target = Target, OutputRoot = Path.Combine(root, "runs") });
            string missing = Path.Combine(root, "gone", "missing.twproj");
            string settings = Path.Combine(root, "settings.json");
            new WorkbenchPreferences("", "", null, "80x25") { RecentProjects = [missing, project] }.Save(settings);
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var start = await Wait(bridge, () => app.Start);
                await bridge.Invoke(() =>
                {
                    Assert.Null(app.Project);
                    Assert.Equal([missing, project], start.Entries.Select(e => e.Path).ToArray());
                    Assert.False(start.Entries[0].Available); Assert.Equal("Start sample", start.Entries[1].Name);
                    Assert.Same(start.Recent, start.current); Assert.Contains("Recent projects", Text()); AssertButtonRow(start);
                    Assert.Contains("Ctrl+N New project", StatusRow());
                });
                // Enter on a missing entry: warning, entry pruned from user settings, Start stays open.
                Press(Keys.kbEnter);
                await Until(bridge, () => StatusRow().Contains("! Recent project not found"));
                await bridge.Invoke(() => { Assert.Equal([project], start.Entries.Select(e => e.Path).ToArray()); Assert.Equal([project], WorkbenchPreferences.Load(settings).RecentProjects); });
                // Esc closes Start and leaves a usable empty desktop.
                Press(Keys.kbEsc); await Until(bridge, () => app.Start is null);
                await bridge.Invoke(() => { Assert.Null(app.Project); Assert.Contains("Ctrl+O Open project", StatusRow()); });
                await bridge.Invoke(() => app.ShowStart());
                start = await Wait(bridge, () => app.Start);
                Press(Keys.kbEnter);
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Null(app.Start); Assert.Equal(project, main.ProjectPath); Assert.False(main.Dirty);
                    Assert.Equal(project, WorkbenchPreferences.Load(settings).RecentProjects[0]);
                });
            }, showStart: true, settings: settings);
            Assert.Equal(new WorkbenchProject { Name = "Start sample", Target = Target, OutputRoot = Path.Combine(root, "runs") }.Fingerprint(),
                WorkbenchProject.Load(project).Fingerprint()); // opening never rewrites the recent project file
        }
        finally { Delete(root); }
    }

    // ── C/E. Workspace geometry, scenario operations and view state ──────────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task WorkspaceLayoutTabOrderMarkingFilteringAndDetails(int width, int height)
    {
        string root = Temp();
        try
        {
            await Drive(width, height, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() =>
                {
                    var bounds = main.GetExtent();
                    Assert.Equal(app.DeskTop!.size.x, main.size.x); Assert.Equal(app.DeskTop.size.y, main.size.y);
                    var views = Children(main).Where(v => v is not TSharpVision.TFrame).ToArray();
                    Assert.All(views, v => Assert.True(v.origin.x >= 1 && v.origin.y >= 1 && v.origin.x + v.size.x <= main.size.x - 1 && v.origin.y + v.size.y <= main.size.y - 1,
                        $"{v.GetType().Name} at {v.origin.x},{v.origin.y} size {v.size.x}x{v.size.y} outside {main.size.x}x{main.size.y}"));
                    var selectable = views.Where(v => (v.options & Views.ofSelectable) != 0).ToArray();
                    foreach (var a in selectable) foreach (var b in selectable.Where(b => !ReferenceEquals(a, b)))
                        Assert.False(Intersect(a, b), $"{Label(a)} overlaps {Label(b)}");
                    // No buttons (Run..., Results and Properties... are F5/F6/F4, Alt+U/L/O and menu items);
                    // the removed grid radios, custom sizes, Cancel and Clear buttons stay gone.
                    Assert.Empty(views.OfType<TButton>());
                    Assert.Empty(views.OfType<TRadioButtons>());
                    Assert.Single(views.OfType<TInputLine>()); // Search only
                    Assert.True(main.TestList.size.y >= (height >= 50 ? 16 : height >= 40 ? 12 : 10));
                    Assert.True(main.DetailPane.size.y >= (height >= 50 ? 20 : height >= 40 ? 14 : 12));
                    // Tab order: Groups, Search, Scenarios, Details, then Groups and Search again (no filter).
                    main.Tree.Select();
                    var order = new List<string> { Label(main.current!) };
                    for (int i = 0; i < 5; i++) { main.SelectNext(false); order.Add(Label(main.current!)); }
                    Assert.Equal(["GroupTree", "TInputLine", "ScenarioList", "TListBox", "GroupTree", "TInputLine"], order);
                    Assert.Contains("Target    TSharpWorkbench.SampleTarget", main.HeaderText);
                    Assert.Contains("Scenarios  16 loaded", main.HeaderText); Assert.Contains("Marked     0 of 16", main.HeaderText);
                    Assert.Contains("Run setup Functional | headless target (NullDriver)", main.HeaderText);
                    foreach (string column in new[] { "Scenario", "ID", "Grids", "Last", "Note" }) Assert.Contains(column, main.ColumnHeader);
                    Assert.StartsWith("[ ] Basic dialog", main.TestList.GetText(0, main.TestList.size.x));
                    Assert.Contains("sample/dialog/basic", main.TestList.GetText(0, main.TestList.size.x));
                    Assert.Contains("60x20..200x100", main.TestList.GetText(0, main.TestList.size.x));
                    Assert.Contains("expected fail", Enumerable.Range(0, main.TestList.range).Select(i => main.TestList.GetText(i, main.TestList.size.x)).First(t => t.Contains("Deliberately wrong")));
                    Capture($"workspace-{width}x{height}");

                    // Marking: Space, Enter, Ins (moves down); focus changes never change marks.
                    main.TestList.Select(); main.TestList.FocusItemNum(0);
                    Key(main, Keys.kbSpace); Assert.Equal(["sample/dialog/basic"], main.TestList.MarkedIds.ToArray());
                    Key(main, Keys.kbIns); Assert.Empty(main.TestList.MarkedIds); Assert.Equal(1, main.TestList.focused);
                    Key(main, Keys.kbIns); Assert.Equal(2, main.TestList.focused);
                    Key(main, Keys.kbEnter); Assert.Equal(2, main.TestList.MarkedIds.Count);
                    Key(main, Keys.kbDown); Key(main, Keys.kbPgDn); Key(main, Keys.kbHome); main.Tree.Select(); main.TestList.Select();
                    Assert.Equal(["sample/menu/navigation", "sample/menu/selection"], main.TestList.MarkedIds.Order().ToArray());
                    Assert.True(main.Dirty);
                    Assert.Contains("Marked 2 of 16  |  Showing 16  |  Group: All scenarios", main.CaptionText);
                    Assert.Contains("Marked     2 of 16", main.HeaderText);
                    Assert.Contains("[x] Menu navigation", main.TestList.GetText(1, 200));
                    Assert.True(main.SaveProject(false, Path.Combine(root, $"layout-{width}.twproj"))); Assert.False(main.Dirty);

                    // Search and group focus filter the list, keep marks and never dirty the project.
                    main.SearchBox.SetData("status"); Nudge(main);
                    Assert.Equal(1, main.TestList.range); Assert.Equal(2, main.TestList.MarkedIds.Count); Assert.Contains("Showing 1", main.CaptionText);
                    main.SearchBox.SetData(""); Nudge(main);
                    int selftest = Enumerable.Range(0, main.Tree.range).First(i => main.Tree.GetText(i, 200).Contains("selftest"));
                    main.Tree.Select(); main.Tree.FocusItemNum(selftest); Nudge(main);
                    Assert.Contains("Group: selftest", main.CaptionText); Assert.Equal(9, main.TestList.range);
                    Key(main, Keys.kbLeft); Assert.Contains("selftest", main.Tree.Collapsed);
                    Assert.Equal(2, main.TestList.MarkedIds.Count);
                    Assert.False(main.Dirty);
                    main.Tree.FocusItemNum(0); Nudge(main);

                    // No "Native-runnable only" filter; every scenario stays listed.
                    Assert.Empty(views.OfType<TCheckBoxes>()); Assert.Equal(16, main.TestList.range);
                    Assert.False(main.Dirty);

                    // Test > Scenario details moves focus to the Details pane, which shows the complete metadata.
                    main.TestList.Select(); main.TestList.FocusItemNum(3);
                    Command(app, WorkbenchDialog.Details); Assert.Same(main.DetailPane, main.current);
                    string details = string.Join("\n", Rows(main.DetailPane));
                    Assert.Equal("Status line context  (sample/status/context)", main.DetailTitle);
                    Assert.Contains("Group sample/status  |  kind status  |  expected Passed  |  grids 64x12..200x100", details);
                    Assert.Contains("Runs as    compiled diagnostic scenario", details);
                    Assert.Contains("Last result not run", details);
                    Assert.Contains("Baselines  no baseline store configured", details);
                    Assert.Contains("checkpoint desktop-status", details); Assert.Contains("Safety:", details);
                    Assert.Contains("Details: Up/Down scroll", main.CaptionText);
                    Key(main, Keys.kbShiftTab); Assert.Same(main.TestList, main.current); Assert.Equal(3, main.TestList.focused);
                    Capture($"workspace-details-{width}x{height}");
                });
            });
            // View state is user state per project path, not project data.
            var state = WorkbenchPreferences.Load(Path.Combine(root, "settings.json")).ViewState(Path.Combine(root, $"layout-{width}.twproj"));
            Assert.NotNull(state); Assert.Contains("selftest", state.CollapsedGroups);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ViewStateIsRestoredFromUserSettingsWithoutDirtyingTheProject()
    {
        string root = Temp();
        try
        {
            string project = SaveProject(root, "view.twproj", new WorkbenchProject { Name = "View", Target = Target, OutputRoot = Path.Combine(root, "runs"), Search = "file-search" });
            string settings = Path.Combine(root, "settings.json");
            WorkbenchPreferences.Update(settings, p => p.WithViewState(project, new("menu", "sample/menu", [], false)));
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await LoadedProject(app, bridge);
                await bridge.Invoke(() =>
                {
                    Assert.Equal("menu", main.SearchBox.Data); Assert.Equal("sample/menu", main.Tree.SelectedGroup);
                    Assert.Equal(2, main.TestList.range); Assert.False(main.Dirty);
                });
            }, settings: settings);
        }
        finally { Delete(root); }
    }

    // ── D. Project Properties ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectPropertiesRoundTripValidationConsequenceAndCancel()
    {
        string root = Temp();
        try
        {
            string path = Path.Combine(root, "props.twproj");
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => { main.TestList.Toggle("sample/dialog/basic"); Assert.True(main.SaveProject(false, path)); });
                string saved = File.ReadAllText(path);

                // Cancel preserves every value and writes nothing.
                Task opening = bridge.Invoke(() => Command(main, WorkbenchDialog.Advanced));
                var dialog = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
                await bridge.Invoke(() =>
                {
                    Assert.True(dialog.NameField.GetState(Views.sfFocused)); AssertButtonRow(dialog);   // fields are in group boxes since the redesign
                    Assert.Equal(Target, dialog.ExecutableField.Data); Assert.Equal(Path.Combine(root, "runs"), dialog.RunsField.Data);
                    Assert.Contains(path, Text()); Assert.Contains("Elevation (administrator rights) is never allowed", Text());
                    Assert.Contains("Scenario files: Scenarios menu.", Text());
                    Assert.Contains("Automatic: next to the project file", Text());   // <project folder>\runs
                    dialog.NameField.SetData("Changed but cancelled");
                    dialog.ExecutableField.SetData(Path.Combine(root, "other.exe")); Nudge(dialog);
                    Assert.Contains("clears the 1 marked scenario", dialog.NoticeText);
                    Capture("project-properties-consequence");
                });
                Press(Keys.kbEsc); await opening.WaitAsync(TimeSpan.FromSeconds(30));
                await bridge.Invoke(() =>
                {
                    Assert.False(main.Dirty); Assert.Equal(Target, main.CaptureProject().Target); Assert.Contains("sample/dialog/basic", main.TestList.MarkedIds);
                    Assert.Equal(saved, File.ReadAllText(path));
                });

                // OK validates: invalid store keeps the dialog open with an inline reason; valid values apply.
                string store = Path.Combine(root, "store.json");
                File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [], [new("sample/status/context", "desktop-status", ["80x25"], "Disposable store")]), ScenarioRunner.Json));
                opening = bridge.Invoke(() => Command(main, WorkbenchDialog.Advanced));
                dialog = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
                await bridge.Invoke(() =>
                {
                    dialog.NameField.SetData("Renamed sample");
                    // A new .json name is valid (nothing is created); a name without .json is not.
                    dialog.StoreField.SetData(Path.Combine(root, "missing-store.txt"));
                    Assert.False(dialog.Valid(Views.cmOK)); Assert.StartsWith("! Baseline manifest:", dialog.NoticeText);
                    dialog.StoreField.SetData(store); dialog.AllowField.value = 5; // Network + Devices
                    dialog.NameField.SetData(""); Assert.False(dialog.Valid(Views.cmOK)); Assert.Contains("Project name is required", dialog.NoticeText);
                    dialog.NameField.SetData("Renamed sample");
                    Assert.True(dialog.Valid(Views.cmOK), dialog.NoticeText);
                    Command(dialog, Views.cmOK);
                });
                await opening.WaitAsync(TimeSpan.FromSeconds(30));
                await bridge.Invoke(() =>
                {
                    Assert.True(main.Dirty);
                    Assert.Contains("project properties", string.Join(";", main.ChangedGroups()));
                    Assert.Contains("sample/dialog/basic", main.TestList.MarkedIds); // target unchanged: marks kept
                    Assert.True(main.SaveProject(false));
                });
                var reloaded = WorkbenchProject.Load(path);
                Assert.Equal("Renamed sample", reloaded.Name); Assert.Equal(store, reloaded.BaselineStore);
                Assert.Equal(["network", "devices"], reloaded.Allow); Assert.Equal(1, reloaded.Version);

                // New project opens Properties with focus on the executable.
                await bridge.Invoke(() => Send(WorkbenchApp.NewProject));
                dialog = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
                await bridge.Invoke(() => { Assert.True(dialog.ExecutableField.GetState(Views.sfFocused)); Assert.Equal("", dialog.ExecutableField.Data); });
                Press(Keys.kbEsc);
                await Until(bridge, () => Modal(app, "Project Properties") is null);
                await bridge.Invoke(() => { Assert.NotNull(app.Project); Assert.Null(app.Project!.ProjectPath); Assert.Contains("Untitled project", app.Project.title!); });
            });
        }
        finally { Delete(root); }
    }

    // ── F. Unsaved Project Changes, with the actual dialog ───────────────────────────

    [Fact]
    public async Task UnsavedChangesDefaultsToCancelAndNeverWritesWithoutAnExplicitChoice()
    {
        string root = Temp();
        try
        {
            string path = Path.Combine(root, "dirty.twproj");
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => { Assert.True(main.SaveProject(false, path)); main.TestList.Toggle("sample/dialog/basic"); Assert.True(main.Dirty); });
                string before = File.ReadAllText(path);
                DateTime written = File.GetLastWriteTimeUtc(path);

                // Close: Enter (nothing chosen) cancels.
                Send(WorkbenchApp.CloseProject);
                var dialog = await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(() =>
                {
                    var buttons = Children(dialog).OfType<TButton>().OrderBy(b => b.origin.x).ToArray();
                    Assert.Equal(["~S~ave", "~D~iscard", "Cancel"], buttons.Select(Label).ToArray());
                    Assert.Equal(ButtonConstants.bfDefault, buttons[2].Flags & ButtonConstants.bfDefault);
                    Assert.Equal(0, buttons[0].Flags & ButtonConstants.bfDefault); Assert.Equal(0, buttons[1].Flags & ButtonConstants.bfDefault);
                    Assert.Same(buttons[2], dialog.current);
                    Assert.Contains("marked scenarios (now 1 marked)", Text()); Assert.Contains("Save them to dirty.twproj?", Text());
                    Capture("unsaved-changes-cancel-default");
                });
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                await bridge.Invoke(() => { Assert.Same(main, app.Project); Assert.True(main.Dirty); });
                // Esc cancels.
                Send(WorkbenchApp.CloseProject); await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                // The close icon / Alt+F3 cancels.
                Send(WorkbenchApp.CloseProject); await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                Send(Views.cmClose); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                // Alt+X: Cancel keeps the application running.
                Press(Keys.kbAltX); await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                Press(Keys.kbEnter); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                // Opening another project: Cancel keeps the current one.
                await bridge.Invoke(() => Send(WorkbenchApp.NewProject)); await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(main, app.Project); Assert.True(main.Dirty); });
                Assert.Equal(before, File.ReadAllText(path)); Assert.Equal(written, File.GetLastWriteTimeUtc(path));

                // Explicit Discard closes without writing.
                Send(WorkbenchApp.CloseProject); dialog = await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                Press(Keys.kbAltD); await Until(bridge, () => app.Project is null);
                Assert.Equal(before, File.ReadAllText(path));

                // Untitled project: Save opens Save as; cancelling it aborts the close and writes nothing.
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
                main = await LoadedProject(app, bridge);
                Send(WorkbenchApp.NewProject); await Wait(bridge, () => Modal(app, "Project Properties")); Press(Keys.kbEsc);
                await Until(bridge, () => app.Project is { ProjectPath: null } && Modal(app, "Project Properties") is null);
                var untitled = app.Project!;
                await bridge.Invoke(() => untitled.TestList.ClearMarks());
                await bridge.Invoke(() => { untitled.SearchBox.SetData("x"); Nudge(untitled); });
                await bridge.Invoke(() => Assert.False(untitled.Dirty)); // view state only
                Task properties = bridge.Invoke(() => Command(untitled, WorkbenchDialog.Advanced));
                var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
                await bridge.Invoke(() => { props.NameField.SetData("Untitled edited"); Command(props, Views.cmOK); }); await properties;
                await bridge.Invoke(() => Assert.True(untitled.Dirty));
                string[] files = Directory.GetFiles(root, "*.twproj", SearchOption.AllDirectories);
                Send(WorkbenchApp.CloseProject); await Wait(bridge, () => Modal(app, WorkbenchDialog.UnsavedTitle));
                await bridge.Invoke(() => Assert.Contains("has not been saved yet", Text()));
                Press(Keys.kbAltS); var saveAs = await Wait(bridge, () => Modal(app, "Save project as"));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Save project as") is null && Modal(app, WorkbenchDialog.UnsavedTitle) is null);
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(untitled, app.Project); Assert.True(untitled.Dirty); });
                Assert.Equal(files, Directory.GetFiles(root, "*.twproj", SearchOption.AllDirectories));
                await bridge.Invoke(() => untitled.CloseApproved());
            });
        }
        finally { Delete(root); }
    }

    // ── J. Open / Save as start in the project folder ────────────────────────────────

    [Fact]
    public async Task OpenAndSaveAsStartInTheProjectFolderWithTheProjectWildcard()
    {
        string root = Temp(), cwd = Environment.CurrentDirectory;
        try
        {
            string folder = Path.Combine(root, "projects"); Directory.CreateDirectory(folder);
            string path = SaveProject(folder, "picker.twproj", new WorkbenchProject { Name = "Picker", Target = Target, OutputRoot = Path.Combine(root, "runs") });
            string sep = Path.DirectorySeparatorChar.ToString();
            await Drive(140, 40, root, async (app, bridge) =>
            {
                await Loaded(app, bridge);
                await bridge.Invoke(() =>
                {
                    // Confirmation of the finding: a literal project file name leaves the framework dialog in the process directory.
                    using var literal = new TFileDialog("*.twproj", "probe", "~N~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdNoLoadDir, 0);
                    literal.SetData(path);
                    Assert.NotEqual(folder + sep, literal.Directory);
                    Assert.Equal(Path.Combine(folder, "*.twproj"), WorkbenchDialog.ProjectDialogStart(path));
                    app.ActivateProject(WorkbenchProject.Load(path), path);
                });
                var main = await LoadedProject(app, bridge);
                Send(WorkbenchApp.OpenProject); var picker = (TFileDialog)await Wait(bridge, () => Modal(app, "Open project"));
                await bridge.Invoke(() =>
                {
                    Assert.Equal(folder + sep, picker.Directory); Assert.Equal("*.twproj", picker.WildCard);
                    Assert.Contains("picker.twproj", Enumerable.Range(0, picker.fileList!.range).Select(i => picker.fileList.GetText(i, 255)));
                    Capture("project-open-dialog");
                });
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Open project") is null);
                await bridge.Invoke(() => Assert.Same(main, app.Project));
                Send(WorkbenchApp.SaveAsProject); picker = (TFileDialog)await Wait(bridge, () => Modal(app, "Save project as"));
                await bridge.Invoke(() => { Assert.Equal(folder + sep, picker.Directory); Assert.Equal("*.twproj", picker.WildCard); picker.fileName!.SetData(path); });
                Send(Views.cmOK); var replace = await Wait(bridge, () => Modal(app, "Replace project?"));
                await bridge.Invoke(() => Assert.Same(Children(replace).OfType<TButton>().Single(b => b.Command == Views.cmCancel), replace.current));
                Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Replace project?") is null && Modal(app, "Save project as") is null);
                await bridge.Invoke(() => main.CloseApproved());
            });
            Assert.Equal(cwd, Environment.CurrentDirectory);
        }
        finally { Delete(root); }
    }

    // ── G/I. Notifications while Results is in front; basic Run → Results ─────────────

    [Fact]
    public async Task NotificationsStayVisibleWhileResultsIsActiveAndMessagesKeepsHistory()
    {
        string root = Temp();
        try
        {
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                await bridge.Invoke(() => { main.TestList.Toggle("sample/status/context"); RunAccepted(main); });
                await Until(bridge, () => main.Busy, 30);
                await Until(bridge, () => !main.Busy, 120);
                await bridge.Invoke(() => Assert.Contains("Run finished: PASSED", StatusRow()));
                Press(Keys.kbF6); var results = await Wait(bridge, () => Children(app.DeskTop!).OfType<WorkbenchResultsDialog>().SingleOrDefault());
                await bridge.Invoke(() =>
                {
                    Assert.Same(results, app.DeskTop!.current); Assert.Equal(4, results.number);
                    Assert.Contains(results.ExecutionRows, r => r.Contains(" +  Passed") && r.Contains("80x25") && r.Contains("sample/status/context"));
                    Assert.Contains("Passed", main.TestList.GetText(3, 200));
                });
                // A rejected command while Results covers the workspace: visible in the status line, logged in Messages.
                Send(WorkbenchDialog.CompareResult);
                await Until(bridge, () => StatusRow().Contains("! This execution has no logical baseline comparison"));
                await bridge.Invoke(() => { Assert.Same(results, app.DeskTop!.current); Capture("notification-results-front"); });
                string run = Directory.GetDirectories(Path.Combine(root, "runs"), "run-*").Single();
                string[] artifacts = Directory.GetFiles(run).Order().ToArray();
                PressAlt('6'); var messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() =>
                {
                    string rows = string.Join("\n", Rows(messages.Entries));
                    Assert.Contains("Warning   This execution has no logical baseline comparison", rows);
                    Assert.Contains("Info      Run finished: PASSED", rows);
                    Assert.Contains("Warning", Rows(messages.Entries)[0]);
                    Assert.NotEmpty(Rows(messages.DetailLines));
                    Command(messages, WorkbenchMessagesWindow.ClearHistory);
                    Assert.Empty(app.Notifications.History); Assert.Equal(0, messages.Entries.range);
                });
                Assert.Equal(artifacts, Directory.GetFiles(run).Order().ToArray());
                Press(Keys.kbEsc); await Until(bridge, () => app.Messages is null);
                await bridge.Invoke(() => main.CloseApproved());
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task StandardErrorDialogShowsPlainTextAndKeepsTechnicalDetailsInMessages()
    {
        string root = Temp();
        try
        {
            await Drive(140, 40, root, async (app, bridge) =>
            {
                var main = await Loaded(app, bridge);
                // A real blocking failure: saving into a folder that does not exist.
                string target = Path.Combine(root, "missing-folder", "copy.twproj");
                Task saving = bridge.Invoke(() => Assert.False(main.SaveProject(false, target)));
                var dialog = await Wait(bridge, () => Modal(app, "Cannot Save Project"));
                await bridge.Invoke(() =>
                {
                    string text = Text();
                    Assert.Contains("Could not write copy.twproj: the folder does not exist.", text);
                    Assert.DoesNotContain(".tmp", text); Assert.DoesNotContain("Exception", text);
                    Assert.Same(Children(dialog).OfType<TButton>().Single(b => b.Command == Views.cmOK), dialog.current);
                    var error = app.Notifications.History.Last();
                    Assert.Equal(NoticeLevel.Error, error.Level); Assert.Contains("DirectoryNotFoundException", error.Details);
                    Assert.Contains("! Cannot Save Project", StatusRow());
                    Capture("error-dialog-save-failed");
                });
                Press(Keys.kbAltM); await saving.WaitAsync(TimeSpan.FromSeconds(20));
                var messages = await Wait(bridge, () => app.Messages);
                await bridge.Invoke(() =>
                {
                    Assert.Contains("ERROR", Rows(messages.Entries)[0]);
                    Assert.Contains("DirectoryNotFoundException", string.Join("\n", Rows(messages.DetailLines)));
                    Assert.True(main.Dirty || !File.Exists(target));
                });
                Assert.False(Directory.Exists(Path.Combine(root, "missing-folder")));
            });
        }
        finally { Delete(root); }
    }

    // ── harness ──────────────────────────────────────────────────────────────────────

    private static async Task Drive(int width, int height, string root, Func<WorkbenchApp, Bridge, Task> body, bool showStart = false, string? settings = null)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs");
        settings ??= Path.Combine(root, "settings.json");
        using var driver = new DriverScope((ushort)width, (ushort)height);
        using var app = new WorkbenchApp(showStart ? "" : Target, output, new(showStart ? "" : Target, output, null, "80x25") { StoragePath = settings },
            showStart: showStart);
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
                // Keep the session's notices with the failure: swallowed command errors are reported there.
                failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                await bridge.Invoke(() => { for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel); app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
            }
        });
        AppLifecycleGuard.Run(app);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(20)), "Worker did not stop.");
        Assert.Null(failure);
    }
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static async Task<WorkbenchDialog> LoadedProject(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static void RunAccepted(WorkbenchDialog main)
    {
        var accept = new TEvent { What = Events.evCommand }; accept.message.command = Views.cmOK; main.PutEvent(ref accept);
        Command(main, WorkbenchDialog.Run);
    }
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    {
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path;
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "ui-" + Guid.NewGuid().ToString("N")[..12]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static IEnumerable<TMenuItem> Items(TMenu menu) { for (var i = menu.Items; i is not null; i = i.Next) yield return i; }
    private static string Label(TView view) => view switch
    {
        TButton b => b.Title ?? "", TLabel l => LabelText(l), TCheckBoxes c => c.GetItemText(0), _ => view.GetType().Name
    };
    // Owner layout adjustment: the bottom action buttons sit one row lower, with a free row above them and only the
    // button shadow row between them and the bottom frame.
    private static void AssertButtonRow(TGroup group)
    {
        var views = Children(group).Where(v => v is not TSharpVision.TFrame).ToArray();
        int row = views.OfType<TButton>().Max(b => b.origin.y);
        var buttons = views.OfType<TButton>().Where(b => b.origin.y == row).ToArray();
        Assert.True(buttons.Length >= 2);
        Assert.All(buttons, b => Assert.Equal(2, b.size.y));
        Assert.Equal(group.size.y - 3, row);
        Assert.DoesNotContain(views, v => v.origin.y <= row - 1 && v.origin.y + v.size.y > row - 1 && v.size.x > 0 && !(v is TButton b && b.origin.y < row - 1));
    }
    private static string LabelText(TLabel label) { label.GetText(out string text); return text; }
    private static bool Intersect(TView a, TView b) =>
        a.origin.x < b.origin.x + b.size.x && b.origin.x < a.origin.x + a.size.x && a.origin.y < b.origin.y + b.size.y && b.origin.y < a.origin.y + a.size.y;
    private static string[] Rows(TListBox list) => Enumerable.Range(0, list.range).Select(i => list.GetText(i, 400)).ToArray();
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string Text() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width * s.Height).Select(i => s[i % s.Width, i / s.Width].Character).ToArray()); }
    private static string StatusRow() { var s = ScreenSnapshot.CopyRoot(); return new string(Enumerable.Range(0, s.Width).Select(x => s[x, s.Height - 1].Character).ToArray()); }
    private static void Capture(string name)
    {
        if (Environment.GetEnvironmentVariable("TSWB_WORKSPACE_EVIDENCE") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
        File.WriteAllBytes(Path.Combine(root, name + ".png"), LogicalPng.Encode(screen));
    }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Key(TView view, ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new(key); view.HandleEvent(ref ev); }
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void PressAlt(char digit) => Press((ushort)(Keys.kbAlt1 + (digit - '1') * 0x100));
    private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    private static async Task Dismiss(Bridge bridge, WorkbenchApp app, TView top)
    { Press(Keys.kbEsc); await Until(bridge, () => !ReferenceEquals(app.DeskTop!.TopView(), top)); }
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
