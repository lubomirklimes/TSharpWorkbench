using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Project creation and Project Properties. The real WorkbenchApp runs on
// NullDriver with the compiled SampleTarget; keys go through the shared event queue as a user's would. Every project,
// runs folder and manifest lives in a disposable folder, and ProjectLocation.Home points there (never the real Documents).
// Optional logical screenshots: TSWB_PROJECT_CREATION_EVIDENCE=<flat directory>.
[Collection("NonParallel")]
public sealed class ProjectCreationTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;

    // A: the owner's workflow. New project, name, executable, Save before any run (while scenarios are still loading),
    // close and reopen. root cause: Save refused while discovery ran ("Wait for the run to finish").
    [Fact]
    public async Task NewProjectIsSavedBeforeAnyRunAndReopens()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        string expected = Path.Combine(home, "MyTests", "MyTests.twproj"), runs = Path.Combine(home, "MyTests", "runs");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Wait(bridge, () => app.Start);
            Press(Keys.kbCtrlN);
            var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() =>
            {
                Assert.True(props.ExecutableField.GetState(Views.sfFocused));
                Assert.Equal(Path.Combine(home, "Untitled project", "runs"), props.RunsField.Data);   // provisional, not the current directory
                Assert.StartsWith("not saved yet; Save as suggests ", props.ProjectFileText); Assert.EndsWith(Path.Combine("Untitled project", "Untitled project.twproj"), props.ProjectFileText);
                Assert.StartsWith("Automatic", props.RunsNote);
                Assert.Equal("", props.StoreField.Data); Assert.StartsWith("Optional", props.StoreNote);
            });
            await Shot(bridge, "new-project-properties-empty");
            // Alt+N, name; Alt+E, executable. The typed name is kept; the automatic Runs folder follows the name.
            Press(Keys.kbAltN); await Settle(bridge); Type("MyTests");   // entering the field selects its value: typing replaces it
            Press(Keys.kbAltE); await Settle(bridge); Type(Target); await Settle(bridge);
            await bridge.Invoke(() =>
            {
                Assert.Equal("MyTests", props.NameField.Data); Assert.Equal(runs, props.RunsField.Data);
                Assert.Contains(Path.Combine("MyTests", "MyTests.twproj"), props.ProjectFileText);
            });
            await Shot(bridge, "new-project-before-save");
            Press(Keys.kbEnter);
            await Until(bridge, () => Modal(app, "Project Properties") is null);
            var main = app.Project!;
            // Ctrl+S at once: discovery may still run; Save does not wait for it and needs no run.
            Press(Keys.kbCtrlS);
            var picker = (TFileDialog)await Wait(bridge, () => Modal(app, "Save project as"));
            await bridge.Invoke(() => Assert.Equal(expected, picker.fileName!.Data));
            await Shot(bridge, "save-as-suggested-file");
            Press(Keys.kbEnter);
            await Until(bridge, () => File.Exists(expected) && !main.Dirty);
            await Wait(bridge, () => main is { Busy: false } && main.TestList.Strings.Count >= 16 ? main : null, 60);
            await bridge.Invoke(() =>
            {
                Assert.Equal(expected, main.ProjectPath); Assert.Contains("(MyTests.twproj)", main.title); Assert.DoesNotContain("not saved", main.title);
                Assert.Contains("Output    runs", main.HeaderText);
                Assert.False(Directory.Exists(runs), "Save never creates the runs folder; the first run does.");
                Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(expected)!));
            });
            await Shot(bridge, "new-project-after-save");
            // Existing project: Properties shows the file and the automatic folder, no "not saved yet".
            Press(Keys.kbAltO);
            props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() => { Assert.EndsWith(Path.Combine("MyTests", "MyTests.twproj"), props.ProjectFileText); Assert.DoesNotContain("not saved", props.ProjectFileText); Assert.Equal(runs, props.RunsField.Data); Assert.StartsWith("Automatic", props.RunsNote); });
            await Shot(bridge, "new-project-reopened-properties");
            Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Properties") is null);
            // Close and reopen.
            await bridge.Invoke(() => Command(app, WorkbenchApp.CloseProject));
            await Until(bridge, () => app.Project is null);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(expected), expected));
            var reopened = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
            await bridge.Invoke(() => { Assert.False(reopened.Dirty); Assert.Equal(expected, reopened.ProjectPath); });
        }, showStart: true);
        var project = WorkbenchProject.Load(expected);
        Assert.Equal("MyTests", project.Name); Assert.Equal(Target, project.Target); Assert.Equal(runs, project.OutputRoot);
        using (var json = JsonDocument.Parse(File.ReadAllText(expected))) Assert.Equal("runs", json.RootElement.GetProperty("outputRoot").GetString());
        Assert.False(Directory.Exists(runs));
        Delete(root);
    }

    // B: Save while scenario loading is in progress (deterministic: loading has started in the same UI turn).
    [Fact]
    public async Task SaveSucceedsWhileScenariosAreLoading()
    {
        string root = Temp(), home = Path.Combine(root, "home"), path = Path.Combine(root, "loading.twproj");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            var main = await Loaded(app, bridge);
            await bridge.Invoke(() =>
            {
                Command(main, WorkbenchDialog.Load);
                Assert.True(main.Busy);
                Assert.True(main.SaveProject(false, path));
                Assert.DoesNotContain(app.Notifications.History, n => n.Message.Contains("Wait for the run"));
                Send(WorkbenchDialog.Advanced);   // Project Properties opens while loading continues
            });
            var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() => Assert.DoesNotContain(app.Notifications.History, n => n.Message.Contains("Scenarios are loading")));
            Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await Wait(bridge, () => main is { Busy: false } ? main : null, 60);
        });
        Assert.True(File.Exists(path)); WorkbenchProject.Load(path);
        Delete(root);
    }

    // B: Save routing, Save after save, Save as to a separate file (automatic runs move along, the old file stays),
    // cancelled Save as, and invalid paths with useful errors.
    [Fact]
    public async Task SaveSaveAsCancelAndInvalidPaths()
    {
        string root = Temp(), home = Path.Combine(root, "home"), other = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(other);
        string first = Path.Combine(home, "Untitled project", "Untitled project.twproj"), second = Path.Combine(other, "copy.twproj");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Wait(bridge, () => app.Start);
            Press(Keys.kbCtrlN);
            await Wait(bridge, () => Modal(app, "Project Properties"));
            Press(Keys.kbEnter);     // no executable yet: a project needs only a name and a Runs folder
            await Until(bridge, () => Modal(app, "Project Properties") is null);
            var main = app.Project!;
            // Cancelled Save as creates nothing, not even the suggested folder.
            Press(Keys.kbCtrlS); await Wait(bridge, () => Modal(app, "Save project as"));
            Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Save project as") is null);
            await bridge.Invoke(() => { Assert.Null(main.ProjectPath); Assert.False(Directory.Exists(Path.GetDirectoryName(first))); });
            // Save on an unnamed project routes to Save as; Enter accepts the suggested file.
            Press(Keys.kbCtrlS); await Wait(bridge, () => Modal(app, "Save project as")); Press(Keys.kbEnter);
            await Until(bridge, () => File.Exists(first) && !main.Dirty);
            // Save after the first save updates that file without asking.
            Press(Keys.kbAltO); var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() => { props.NameField.SetData("Renamed"); props.AllowField.value = 1; });
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => Assert.True(main.Dirty));
            Press(Keys.kbCtrlS); await Until(bridge, () => !main.Dirty);
            await bridge.Invoke(() => Assert.Null(Modal(app, "Save project as")));
            Assert.Equal("Renamed", WorkbenchProject.Load(first).Name); Assert.Equal(["network"], WorkbenchProject.Load(first).Allow);
            string before = File.ReadAllText(first);
            // Save as: a separate file elsewhere; the automatic runs folder moves next to it.
            Send(WorkbenchApp.SaveAsProject);
            var picker = (TFileDialog)await Wait(bridge, () => Modal(app, "Save project as"));
            await bridge.Invoke(() => picker.fileName!.SetData("")); Type(Path.Combine(other, "copy")); Press(Keys.kbEnter);
            await Until(bridge, () => File.Exists(second) && !main.Dirty);
            await bridge.Invoke(() => Assert.Equal(second, main.ProjectPath));
            Assert.Equal(before, File.ReadAllText(first));
            Assert.Equal(Path.Combine(other, "runs"), WorkbenchProject.Load(second).OutputRoot);
            // Invalid targets: a missing folder that is not the suggested one, and a wrong extension.
            Task missing = bridge.Invoke(() => Assert.False(main.SaveProject(false, Path.Combine(root, "missing", "x.twproj"))));
            var error = await Wait(bridge, () => Modal(app, "Cannot Save Project"));
            await bridge.Invoke(() => Assert.Contains("the folder does not exist", Text()));
            await Shot(bridge, "save-invalid-folder-error");
            Press(Keys.kbEsc); await missing;
            Task extension = bridge.Invoke(() => Assert.False(main.SaveProject(false, Path.Combine(other, "x.txt"))));
            await Wait(bridge, () => Modal(app, "Cannot Save Project"));
            await bridge.Invoke(() => Assert.Contains(".twproj extension", Text()));
            Press(Keys.kbEsc); await extension;
            Assert.False(Directory.Exists(Path.Combine(root, "missing"))); Assert.False(File.Exists(Path.Combine(other, "x.txt")));
        }, showStart: true);
        Assert.False(Directory.Exists(Path.Combine(other, "runs")));
        Delete(root);
    }

    // C: baseline manifest. Empty is valid; an existing manifest is selectable; a new .json name (also in a new folder)
    // is accepted without creating anything; invalid names are refused; Cancel keeps the previous value; nothing is overwritten.
    [Fact]
    public async Task BaselineManifestSelectionAndNewFileName()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        string project = SaveProject(root, "store.twproj", new WorkbenchProject { Name = "Store sample", Target = Target, OutputRoot = Path.Combine(root, "runs") });
        string manifest = Manifest(root), invalid = Path.Combine(root, "store", "broken.json");
        File.WriteAllText(invalid, "{ \"format\": \"something else\" }");
        string manifestBefore = File.ReadAllText(manifest), invalidBefore = File.ReadAllText(invalid);
        string fresh = Path.Combine(root, "new-store", "visual-baselines.json");
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
            var main = await LoadedProject(app, bridge);
            // Empty baseline: OK accepted.
            Press(Keys.kbAltO); var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => Assert.False(main.Dirty));
            // Existing manifest through the picker (Alt+O inside the dialog).
            Press(Keys.kbAltO); props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            Press(Keys.kbAltO); var picker = (TFileDialog)await Wait(bridge, () => app.DeskTop!.TopView() as TFileDialog);
            await bridge.Invoke(() => Assert.Equal(WorkbenchPropertiesDialog.ManifestPickerTitle, picker.title));
            await bridge.Invoke(() => picker.fileName!.SetData("")); Type(manifest); Press(Keys.kbEnter);
            await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
            await bridge.Invoke(() => { Assert.Equal(manifest, props.StoreField.Data); Assert.StartsWith("External source", props.StoreNote); Assert.True(props.StoreField.GetState(Views.sfFocused)); });
            // Cancelled picker keeps the value.
            Press(Keys.kbAltO); picker = (TFileDialog)await Wait(bridge, () => app.DeskTop!.TopView() as TFileDialog);
            await bridge.Invoke(() => picker.fileName!.SetData("")); Type("other.json"); Press(Keys.kbEsc);
            await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
            await bridge.Invoke(() => Assert.Equal(manifest, props.StoreField.Data));
            // A new file name without extension, in a folder that does not exist yet.
            Press(Keys.kbAltO); picker = (TFileDialog)await Wait(bridge, () => app.DeskTop!.TopView() as TFileDialog);
            await bridge.Invoke(() => picker.fileName!.SetData("")); Type(Path.Combine(root, "new-store", "visual-baselines"));
            await Shot(bridge, "baseline-manifest-new-file-name");
            Press(Keys.kbEnter); await Until(bridge, () => ReferenceEquals(app.DeskTop!.TopView(), props));
            await bridge.Invoke(() => { Assert.Equal(fresh, props.StoreField.Data); Assert.StartsWith("New manifest:", props.StoreNote); });
            await Shot(bridge, "baseline-manifest-new-file-accepted");
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => { Assert.True(main.Dirty); Assert.True(main.SaveProject(false)); });
            Assert.False(Directory.Exists(Path.Combine(root, "new-store")), "A new manifest name creates nothing.");
            Assert.Equal(Path.Combine("new-store", "visual-baselines.json"), Raw(project, "baselineStore"));
            // Invalid entries are refused on OK with the reason; the dialog stays open.
            Press(Keys.kbAltO); props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            foreach (var (value, reason) in new[] { (Path.Combine(root, "store", "baselines.txt"), "must end in .json"), (Path.Combine(root, "store"), "is a folder"),
                (invalid, "is not a readable baseline manifest"), ("bad\0name.json", "the path is not valid") })
            {
                await bridge.Invoke(() => { props.StoreField.SetData(value); Command(props, Views.cmOK); });
                await Settle(bridge);
                await bridge.Invoke(() => { Assert.Same(props, app.DeskTop!.TopView()); Assert.Contains(reason, props.NoticeText); Assert.True(props.StoreField.GetState(Views.sfFocused)); });
            }
            await Shot(bridge, "baseline-manifest-invalid");
            // Clearing the field makes the baseline optional again.
            await bridge.Invoke(() => props.StoreField.SetData(""));
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => Assert.True(main.SaveProject(false)));
        });
        Assert.Null(WorkbenchProject.Load(project).BaselineStore);
        Assert.Equal(manifestBefore, File.ReadAllText(manifest)); Assert.Equal(invalidBefore, File.ReadAllText(invalid));
        Delete(root);
    }

    // D: Runs folder default and precedence: deterministic automatic folder, a typed folder survives name, executable and
    // project-location changes, relative paths resolve against the saved project, nothing is created.
    [Fact]
    public async Task RunsFolderDefaultAndManualOverride()
    {
        string root = Temp(), home = Path.Combine(root, "home"), custom = Path.Combine(root, "my-output");
        string other = Path.Combine(root, "target-copy.exe");
        Directory.CreateDirectory(root); File.Copy(Target, other);
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Wait(bridge, () => app.Start);
            Press(Keys.kbCtrlN);
            var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() =>
            {
                // Deterministic: the same name gives the same folder; an untouched name follows the executable.
                Assert.Equal(ProjectLocation.DefaultRuns(null, "Untitled project"), props.RunsField.Data);
                props.ExecutableField.SetData(Target); Nudge(props);
            });
            await bridge.Invoke(() =>
            {
                string name = Path.GetFileNameWithoutExtension(Target);
                Assert.Equal(name, props.NameField.Data); Assert.Equal(Path.Combine(home, name, "runs"), props.RunsField.Data);
                // Manual override.
                props.RunsField.SetData(custom); Nudge(props);
                Assert.StartsWith("Custom folder", props.RunsNote);
                props.NameField.SetData("Changed"); Nudge(props); props.ExecutableField.SetData(other); Nudge(props);
                Assert.Equal(custom, props.RunsField.Data);
                // An empty field takes the automatic folder on OK.
                props.RunsField.SetData(""); Nudge(props); Assert.StartsWith("Empty: OK uses the automatic folder", props.RunsNote);
                props.RunsField.SetData(custom); props.ExecutableField.SetData(Target); Nudge(props);
                Assert.Equal(custom, props.RunsField.Data); Assert.Equal("Changed", props.NameField.Data);
            });
            await Shot(bridge, "new-project-custom-runs");
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            var main = app.Project!;
            string path = Path.Combine(root, "saved", "custom.twproj"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await bridge.Invoke(() => Assert.True(main.SaveProject(true, path)));
            Assert.Equal(custom, WorkbenchProject.Load(path).OutputRoot);   // custom folder kept on Save as
            // Relative folder resolves against the saved project folder.
            Press(Keys.kbAltO); props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() => { props.RunsField.SetData(Path.Combine("out", "runs")); Nudge(props); });
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => Assert.True(main.SaveProject(false)));
            Assert.Equal(Path.Combine(root, "saved", "out", "runs"), WorkbenchProject.Load(path).OutputRoot);
            Assert.Equal(Path.Combine("out", "runs"), Raw(path, "outputRoot"));
            // Empty on a saved project: the automatic folder next to the project file.
            Press(Keys.kbAltO); props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() => { props.RunsField.SetData(""); Nudge(props); });
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => Assert.True(main.SaveProject(false)));
            Assert.Equal("runs", Raw(path, "outputRoot"));
        }, showStart: true);
        Assert.False(Directory.Exists(custom)); Assert.False(Directory.Exists(Path.Combine(root, "saved", "out"))); Assert.False(Directory.Exists(Path.Combine(root, "saved", "runs")));
        Assert.False(Directory.Exists(home));
        Delete(root);
    }

    // E: the regrouped dialog at the supported Workbench grids: fits the desktop, groups do not overlap, every control
    // lies inside its group, labels and fields align, mnemonics are unique, Tab visits every control in reading order,
    // long paths scroll horizontally, Enter is OK and Esc is Cancel.
    [Theory]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    [InlineData(120, 36)]
    public async Task PropertiesLayoutFocusAndKeyboard(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        string deep = Path.Combine(root, string.Join(Path.DirectorySeparatorChar, Enumerable.Range(1, 8).Select(i => $"a-rather-long-folder-name-{i}")));
        Directory.CreateDirectory(deep);
        string manifest = Manifest(deep);
        string project = SaveProject(deep, "layout.twproj", new WorkbenchProject
        {
            Name = "Layout sample", Target = Target, OutputRoot = Path.Combine(deep, "custom-runs"), BaselineStore = manifest, Allow = ["network"],
            TargetArguments = ["--mode", "demo"], TargetEnvironment = new() { ["TSW_SAMPLE"] = "1" }
        });
        string before = File.ReadAllText(project);
        await Drive(width, height, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
            var main = await LoadedProject(app, bridge);
            Press(Keys.kbAltO);
            var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() =>
            {
                var desk = app.DeskTop!;
                Assert.True(props.origin.x >= 0 && props.origin.y >= 0 && props.origin.x + props.size.x <= desk.size.x && props.origin.y + props.size.y <= desk.size.y);
                TGroupBox[] boxes = [props.ProjectBox, props.TargetBox, props.RunsBox, props.CapabilitiesBox];
                for (int i = 1; i < boxes.Length; i++) Assert.True(boxes[i].origin.y > boxes[i - 1].origin.y + boxes[i - 1].size.y - 1, "groups overlap");
                foreach (var box in boxes)
                {
                    Assert.True(box.origin.x + box.size.x <= props.size.x - 1 && box.origin.y + box.size.y <= props.size.y - 5);
                    foreach (var child in Children(box).Where(c => c.GetType() != typeof(TView) && (c.state & Views.sfVisible) != 0 && c.size.x > 0 && c is not TGroupBox && c.GetType().Name != "Background"))
                        Assert.True(child.origin.x >= 1 && child.origin.y >= 1 && child.origin.x + child.size.x <= box.size.x - 1 && child.origin.y + child.size.y <= box.size.y - 1,
                            $"{child.GetType().Name} at {child.origin.x},{child.origin.y} size {child.size.x}x{child.size.y} leaves {box.Title}");
                }
                // Fields share one left edge; each is wide enough for a path.
                TInputLine[] fields = [props.NameField, props.ExecutableField, props.RunsField, props.StoreField];
                Assert.Single(fields.Select(f => f.origin.x).Distinct());
                Assert.All(fields.Skip(1), f => Assert.True(f.size.x >= 60, $"field width {f.size.x}"));
                Assert.Empty(ProductQualificationTests.DuplicateMnemonics(props));
                Assert.EndsWith("layout.twproj", props.ProjectFileText); Assert.StartsWith(Path.GetPathRoot(project)!, props.ProjectFileText);
                // Static texts are not clipped: every label and note fits its view.
                foreach (var text in AllViews(props).OfType<TStaticText>().Where(t => t is not TLabel))
                { text.GetText(out string value); Assert.True(value.Replace("\n", "").Length <= text.size.x * text.size.y, $"clipped: {value}"); }
                Assert.True(props.NameField.GetState(Views.sfFocused));
            });
            await Shot(bridge, "existing-project-properties");
            // Tab order: name, executable, Browse, runs, Browse, manifest, Browse, capabilities, OK, Cancel, and back to name.
            var order = new List<string>();
            for (int i = 0; i < 14; i++)
            {
                await bridge.Invoke(() => order.Add(Focused(props)));
                Press(Keys.kbTab); await Settle(bridge);
            }
            // The Target diagnostics group (Verify target..., Details...) follows the target group.
            // Verify target lives in Target Diagnostics (Details...), no second Verify button here.
            Assert.Equal(["name", "executable", "Browse...", "arguments", "process-environment", "Details...", "runs", "Browse...", "manifest", "Browse...", "capabilities", "OK", "Cancel", "name"], order);
            // The last Tab moved on to the executable; Shift+Tab goes back to the name, then wraps to Cancel.
            Press(Keys.kbShiftTab); await Settle(bridge);
            await bridge.Invoke(() => { Assert.Equal("name", Focused(props)); Assert.Equal((0, props.NameField.Data.Length), (props.NameField.SelStart, props.NameField.SelEnd)); });
            Press(Keys.kbShiftTab); await Settle(bridge);
            await bridge.Invoke(() => Assert.Equal("Cancel", Focused(props)));
            // Mnemonics reach fields inside the groups.
            foreach (var (key, field) in new[] { (Keys.kbAltR, "runs"), (Keys.kbAltM, "manifest"), (Keys.kbAltE, "executable"), (Keys.kbAltN, "name") })
            { Press(key); await Settle(bridge); await bridge.Invoke(() => Assert.Equal(field, Focused(props))); }
            Press(Keys.kbAltW); await Settle(bridge);
            await bridge.Invoke(() => Assert.Equal(0u, props.AllowField.value & 1));
            // A long path scrolls horizontally inside its field.
            Press(Keys.kbAltR); Press(Keys.kbEnd); await Settle(bridge);
            await bridge.Invoke(() => { Assert.True(props.RunsField.FirstPos > 0); Assert.Equal(props.RunsField.Data.Length, props.RunsField.CurPos); });
            await Shot(bridge, "existing-project-long-path-end");
            // Esc discards (the Network change is not applied); Enter accepts.
            Press(Keys.kbEsc); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => Assert.False(main.Dirty));
            Press(Keys.kbAltO); props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => Assert.False(main.Dirty));
        });
        Assert.Equal(before, File.ReadAllText(project));
        Delete(root);
    }

    // F: legacy projects. An existing project with a custom runs folder ("../runs"), arguments and a baseline manifest loads,
    // shows its values, and OK + Save leave the file content unchanged (no silent migration).
    [Fact]
    public async Task LegacyProjectLoadsAndIsNotMigrated()
    {
        string root = Temp(), home = Path.Combine(root, "home"), folder = Path.Combine(root, "legacy", "project");
        Directory.CreateDirectory(folder);
        string manifest = Manifest(Path.Combine(root, "legacy"));
        string path = Path.Combine(folder, "legacy.twproj");
        // Written by hand in the shape (no testType: derived on load), relative paths, custom runs outside the folder.
        File.WriteAllText(path, $$"""
            {
              "schema": "TSharpVision.TestWorkbench",
              "version": 1,
              "name": "Legacy",
              "target": {{JsonSerializer.Serialize(Path.GetRelativePath(folder, Target))}},
              "outputRoot": "../runs",
              "baselineStore": "../store/visual-baselines.json",
              "allow": [ "devices" ]
            }
            """);
        string before = File.ReadAllText(path);
        await Drive(140, 40, root, home, async (app, bridge) =>
        {
            await Loaded(app, bridge);
            await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(path), path));
            var main = await LoadedProject(app, bridge);
            Press(Keys.kbAltO);
            var props = (WorkbenchPropertiesDialog)await Wait(bridge, () => Modal(app, "Project Properties"));
            await bridge.Invoke(() =>
            {
                Assert.Equal(Path.Combine(root, "legacy", "runs"), props.RunsField.Data); Assert.StartsWith("Custom folder", props.RunsNote);
                Assert.Equal(manifest, props.StoreField.Data); Assert.Equal(4u, props.AllowField.value);
            });
            Press(Keys.kbEnter); await Until(bridge, () => Modal(app, "Project Properties") is null);
            await bridge.Invoke(() => { Assert.False(main.Dirty); Assert.True(main.SaveProject(false)); });
        });
        var loaded = WorkbenchProject.Load(path);
        Assert.Equal(Path.Combine(root, "legacy", "runs"), loaded.OutputRoot); Assert.Equal(manifest, loaded.BaselineStore); Assert.Equal(["devices"], loaded.Allow);
        Assert.Equal("../runs", Raw(path, "outputRoot").Replace('\\', '/'));
        Assert.Equal("../store/visual-baselines.json", Raw(path, "baselineStore")!.Replace('\\', '/'));
        Delete(root);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static string Focused(WorkbenchPropertiesDialog props)
    {
        if (props.NameField.GetState(Views.sfFocused)) return "name";
        if (props.ExecutableField.GetState(Views.sfFocused)) return "executable";
        if (props.RunsField.GetState(Views.sfFocused)) return "runs";
        if (props.StoreField.GetState(Views.sfFocused)) return "manifest";
        if (props.ArgumentsField.GetState(Views.sfFocused)) return "arguments";
        if (props.ProcessEnvironmentField.GetState(Views.sfFocused)) return "process-environment";
        if (props.AllowField.GetState(Views.sfFocused)) return "capabilities";
        return AllViews(props).OfType<TButton>().SingleOrDefault(b => b.GetState(Views.sfFocused))?.Title?.Replace("~", "") ?? "none";
    }
    private static IEnumerable<TView> AllViews(TGroup group) =>
        Children(group).SelectMany(v => v is TGroup g ? AllViews(g).Prepend(v) : [v]);
    private static string? Raw(string project, string property)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(project));
        return json.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static string Manifest(string folder)
    {
        string store = Path.Combine(folder, "store", "visual-baselines.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, JsonSerializer.Serialize(new VisualBaselineManifest(VisualBaselineSet.ManifestFormat, [],
            [new("sample/status/context", "desktop-status", ["80x25"], "Disposable store")]), ScenarioRunner.Json));
        return store;
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
            Assert.True(worker.Wait(TimeSpan.FromSeconds(90)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static async Task<WorkbenchDialog> LoadedProject(WorkbenchApp app, Bridge bridge) =>
        await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p && p.TestList.Strings.Count >= 16 ? p : null, 60);
    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "mux1a-" + Guid.NewGuid().ToString("N")[..10]);
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
        if (Environment.GetEnvironmentVariable("TSWB_PROJECT_CREATION_EVIDENCE") is not { Length: > 0 } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string name = $"mux1a-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(ProjectCreationTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Nudge(TView view) { var ev = new TEvent { What = Events.evNothing }; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Press(char letter) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = letter; ev.keyDown.charScan = new CharScanType(letter); TEventQueue.Enqueue(ev); }
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
