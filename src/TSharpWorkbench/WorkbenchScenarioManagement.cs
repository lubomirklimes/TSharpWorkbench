using System.Diagnostics;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// The Scenarios menu. The project's scenarioFiles list is project data (Save/Save as persist it, a change
// marks the project changed); the documents themselves are only read. Attaching or removing a file refreshes the catalog
// in the background (cancellable, never a test run); Validate checks the documents and, for fixture-bound documents,
// their binding against the target's advertised fixtures.
// Open in editor and New scenario open the internal structured editor
// (WorkbenchScenarioEditorWindow). The menu names it Edit scenario...; the external editor is the
// editor window's External editor button only (OpenScenarioExternal, no longer a menu item).
internal sealed partial class WorkbenchDialog : IScenarioEditorHost
{
    internal const ushort NewScenario = 0x7d40, AddScenarioFiles = 0x7d41, AddScenarioFolder = 0x7d42, ManageScenarios = 0x7d43,
        OpenScenario = 0x7d44, RemoveScenario = 0x7d45, ValidateScenarios = 0x7d46, OpenScenarioExternal = 0x7d2c, CreateScenarioTest = 0x7b8e;
    private static bool IsScenarioCommand(ushort command) => command is >= NewScenario and <= ValidateScenarios or OpenScenarioExternal or CreateScenarioTest;
    private readonly List<WorkbenchScenarioEditorWindow> editors = [];
    /// <summary>The open scenario editor windows of this project.</summary>
    internal IReadOnlyList<WorkbenchScenarioEditorWindow> ScenarioEditors => editors;
    private WorkbenchScenariosWindow? scenariosWindow;
    private AttachedScenario[] attached = [];
    // The target's raw catalog from the last discovery (compiled registrations and fixtures), for binding checks.
    private ScenarioDescriptor[]? compiledRaw;
    private string? compiledTarget;
    private (string Path, DateTime Written, bool? Hosts)? hostsCache;
    private bool loadedOnce;
    private string? scenarioFolder;
    internal WorkbenchScenariosWindow? ScenariosWindow => scenariosWindow;
    internal AttachedScenario[] AttachedScenarios => attached;
    internal string[] ScenarioFileList => projectDefaults.ScenarioFiles;
    // Test seam: starts the editor process; returns false when nothing started.
    internal static Func<ProcessStartInfo, bool> EditorLauncher { get; set; } = start => { using var process = Process.Start(start); return process is not null; };

    // The file Open in Editor and Remove act on: the Scenarios window's selection when that window is active, otherwise the
    // Workspace's focused external scenario, otherwise the Scenarios window's selection. Null: nothing to act on.
    internal string? SelectedScenarioFile()
    {
        if (desktop.current is WorkbenchScenarioEditorWindow editor && editors.Contains(editor)) return editor.Document.Path;
        if (scenariosWindow is not null && ReferenceEquals(desktop.current, scenariosWindow)) return scenariosWindow.Selected?.Info.Compiled is null ? scenariosWindow.Selected?.Path : null;
        if (ReferenceEquals(desktop.current, this) && scenarios.Current?.External?.Source is { Length: > 0 } source
            && projectDefaults.ScenarioFiles.Any(f => WorkbenchPreferences.SamePath(f, source))) return source;
        return scenariosWindow?.Selected?.Info.Compiled is null ? scenariosWindow?.Selected?.Path : null;
    }

    // What Edit scenario... and Remove scenario... say when no attached file is selected.
    internal string ScenarioSelectionHint()
    {
        if (projectDefaults.ScenarioFiles.Length == 0)
            return "This project has no attached scenario files. Scenario Manager > New... creates one; Add files... or Add folder... attaches existing JSON files.";
        if (ReferenceEquals(desktop.current, this) && scenarios.Current is { External: null } compiled)
            return $"{compiled.Id} is a compiled scenario of the target, not an attached JSON file; only attached files can be edited or removed. Choose one in Scenarios > Manage scenarios (Alt+2).";
        return "Select an attached scenario file first: Scenarios > Manage scenarios (Alt+2), or an external scenario in the Workspace.";
    }

    private void ScenarioCommand(ushort command)
    {
        if (command == CreateScenarioTest)
        {
            if (Native is null || scenariosWindow?.Selected is not { } selected) return;
            string? id = selected.Info.Id;
            if (selected.Info.Compiled is { } compiled)
            {
                if (CompiledForTarget?.Any(s => s.Id == compiled.Id && !s.IsFixtureOnly) != true)
                { Error("This compiled scenario is unavailable. Refresh scenarios first."); return; }
                Native.Session.PutScenario(new(compiled.Id, null, null, compiled.Backend));
            }
            if (id is null) { Error("Select a valid scenario first."); return; }
            WorkbenchTestExplorer.EditTest(desktop, Native.Session, null, id, TestScenarioMetadata);
            RefreshNativeTitle();
            return;
        }
        if (command == ManageScenarios) { ShowScenarios(); return; }
        if (command == ValidateScenarios) { Validate(); return; }
        if (command == OpenScenario) { OpenSelectedInEditor(); return; }
        if (command == OpenScenarioExternal) { if (SelectedScenarioFile() is { } file) OpenScenarioExternally(file); else Error(ScenarioSelectionHint()); return; }
        if (busy && activeRequest is not null) { Error("Tests are running. Change the attached scenario files when the run has finished."); return; }
        if (command == AddScenarioFiles) AddFiles();
        else if (command == AddScenarioFolder) AddFolder();
        else if (command == RemoveScenario) RemoveSelected();
        else if (command == NewScenario && CreateScenario() is { } created) OpenScenarioEditor(created);
    }

    // ── internal editor ───────────────────────────────────────────────────────────────────

    private void OpenSelectedInEditor()
    {
        if (scenariosWindow?.Selected?.Info.Compiled is { } compiled && ReferenceEquals(desktop.current, scenariosWindow))
        { ShowMessage(desktop, "Compiled Scenario", $"{compiled.Id}\n{compiled.Name}\n{compiled.Description}\nCompiled scenarios are read-only. Use Link Test to assign this scenario."); return; }
        if (SelectedScenarioFile() is not { } path) { Error(ScenarioSelectionHint()); return; }
        OpenScenarioEditor(path);
    }

    /// <summary>Opens <paramref name="path"/> in the internal editor (an already open editor of that file is brought to the
    /// front). Invalid documents open read-only with their problems; a missing file is reported and nothing opens.</summary>
    internal WorkbenchScenarioEditorWindow? OpenScenarioEditor(string path)
    {
        if (editors.FirstOrDefault(e => e.Document.Path is { } p && WorkbenchPreferences.SamePath(p, path)) is { } open)
        { open.Select(); if (!ReferenceEquals(desktop.current, open)) desktop.SetCurrent(open, selectMode.normalSelect); return open; }
        if (!File.Exists(path)) { Error($"{Path.GetFileName(path)} does not exist; it cannot be opened. Remove it from the project or restore the file.", "Missing scenario file: " + path); return null; }
        if (Native is not null) path = Native.Session.EditArtifact(Native.ArtifactName(path));
        ScenarioDocument document;
        try { document = ScenarioDocument.Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error($"{Path.GetFileName(path)} cannot be read: {ex.Message}", ex.ToString()); return null; }
        return ShowEditor(document);
    }

    /// <summary>Shows <paramref name="document"/> (loaded or new) in a new editor window of this project.</summary>
    internal WorkbenchScenarioEditorWindow ShowEditor(ScenarioDocument document)
    {
        var window = new WorkbenchScenarioEditorWindow(desktop, document, this, WorkbenchPreferences.Load(preferences.StoragePath).Window("scenario-editor"));
        window.Closed = () => RememberWindow("scenario-editor", window);
        editors.Add(window);
        desktop.Insert(window);
        window.Select();
        if (!ReferenceEquals(desktop.current, window)) desktop.SetCurrent(window, selectMode.normalSelect);
        string name = document.Path is null ? "the new scenario" : Path.GetFileName(document.Path);
        if (document.ReadOnly) Error($"Opened {name} read-only: {document.ReadOnlyReason}", string.Join("\n", document.LoadProblems.Select(p => p.Display)));
        else Status($"Editing {name}: Ins adds a step, Enter edits, Ctrl+S saves; Esc closes.");
        return window;
    }

    /// <summary>Asks about unsaved editor changes (Save / Discard / Cancel each); false when the user cancelled.
    /// Closes the editors that may close. For project close and exit (wired by the lead in WorkbenchApp).</summary>
    internal bool CloseScenarioEditors()
    {
        foreach (var editor in editors.ToArray())
        {
            if (!editor.Valid(Views.cmClose)) { editor.Select(); return false; }
            editor.CloseApproved();   // saved, unchanged or explicitly discarded
        }
        return true;
    }

    ScenarioBindingContext IScenarioEditorHost.EditorBinding => new(CompiledForTarget, projectDefaults.LegacyFixtures);
    WorkbenchProject? IScenarioEditorHost.EditorProject => owner is null ? null : CaptureProject();
    string? IScenarioEditorHost.EditorProjectPath => owner is null ? null : ProjectPath;
    void IScenarioEditorHost.EditorClosed(WorkbenchScenarioEditorWindow editor) => editors.Remove(editor);
    void IScenarioEditorHost.EditorOpenExternal(string path) => OpenScenarioExternally(path);

    private void OpenScenarioExternally(string path)
    {
        if (Native is null) { OpenInEditor(path); return; }
        if (editors.Any(e => WorkbenchPreferences.SamePath(e.Document.Path, path) && e.Document.Dirty))
        { Error("Save the built-in editor draft before checking out an external copy."); return; }
        try
        {
            string artifact = Native.ArtifactName(path);
            string checkout = Native.Session.CheckoutArtifact(artifact);
            string original = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(checkout)));
            if (!EditorLauncher(ScenarioFiles.EditorCommand(checkout))) throw new IOException("The external editor did not start.");
            if (Confirm(desktop, "Import External Scenario", "Save and close the external editor before importing. The checkout is kept at:\n" + checkout, "~I~mport") != Views.cmYes)
            { Status("No changes imported. Checkout retained: " + checkout); return; }
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) != original)
                throw new IOException("The managed scenario changed during checkout.");
            using var input = new FileStream(checkout, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024) throw new IOException("Scenario exceeds the 1 MiB limit.");
            var prior = ScenarioFiles.Inspect(path).Document;
            var edited = ScenarioFiles.Inspect(checkout).Document;
            if (prior is null || edited is null || edited.Scenario.Id != prior.Scenario.Id ||
                edited.Scenario.Backend != prior.Scenario.Backend || edited.Fixture != prior.Fixture)
                throw new IOException("Import requires valid JSON with the original ID, backend, and fixture.");
            Native.Session.PutArtifact(artifact, input);
            foreach (var editor in editors.Where(e => WorkbenchPreferences.SamePath(e.Document.Path, path)).ToArray()) editor.CloseApproved();
            SetScenarioFiles(projectDefaults.ScenarioFiles, "Imported changes. Project Save writes the archive.", path);
        }
        catch (Exception ex) { Error("External edit was not completed: " + ex.Message); }
    }
    string? IScenarioEditorHost.EditorChoosePath(string suggested)
    {
        if (Native is not null) { Status("Save As is unavailable for managed scenarios."); return null; }
        string? folder = Path.GetDirectoryName(suggested);
        if (folder is null || !Directory.Exists(folder)) suggested = Path.Combine(ScenarioStart(), Path.GetFileName(suggested));
        return ChooseScenarioPath(suggested, "Save scenario as");
    }
    string? IScenarioEditorHost.EditorSaveProblem(ScenarioDocument document)
    {
        if (Native is null || document.Path is not { } path) return null;
        string artifact = Native.ArtifactName(path);
        var prior = Native.Session.Scenarios.FirstOrDefault(s => s.ArtifactPath == artifact);
        return prior is not null && prior.Id != document.Id ? "Managed scenario IDs are stable. Keep the existing ID to preserve test assignments." : null;
    }
    void IScenarioEditorHost.EditorStatus(string message, bool warning) { if (warning) Error(message); else Status(message); }

    // After the editor wrote a document: an attached document refreshes the catalog (never a run, never a baseline);
    // during an active run the refresh is left to F2, and the run-boundary check (ScenarioFiles.StaleRun) refuses any run of the
    // changed document until then. A document saved under a new name is attached only when the user confirms.
    void IScenarioEditorHost.EditorSaved(WorkbenchScenarioEditorWindow editor, ScenarioSaveResult result, string? previousPath)
    {
        if (owner is null || result.Path is not { } path) return;
        string name = Path.GetFileName(path);
        var files = projectDefaults.ScenarioFiles;
        if (!files.Any(f => WorkbenchPreferences.SamePath(f, path)))
        {
            var info = ScenarioFiles.Inspect(path);
            if (AttachProblem(info, files) is { } problem) { Status($"Saved {name}; it is not attached to this project ({problem})"); return; }
            if (Confirm(desktop, "Attach Scenario?", $"Saved {name}.\n\nAttach it to this project so it is listed in the Workspace catalog?\n\n{path}", "~A~ttach") != Views.cmYes)
            { Status($"Saved {name}; it is not attached to this project."); return; }
            scenarioFolder = Path.GetDirectoryName(path);
            SetScenarioFiles([.. files, path], $"Saved {name} and attached it; Ctrl+S saves the project.", path);
            return;
        }
        UpdateProjectTitle();
        RecheckScenarioFiles(path);
        if (busy && activeRequest is not null)
        { Error($"Saved {name}. A run is active: press F2 after it to refresh Scenario Manager; until then a run of this scenario is refused as stale."); return; }
        if (target.Data.Length == 0 || !File.Exists(target.Data)) { Status($"Saved {name}. Set the executable in Project Properties (Alt+O) to load the scenarios."); return; }
        Status($"Saved {name}; refreshing the catalog (no scenario runs).");
        if (busy) { afterRun = LoadCatalog; cancellation?.Cancel(); }
        else LoadCatalog();
    }

    internal void ShowScenarios(string? focus = null)
    {
        if (scenariosWindow is null)
        {
            scenariosWindow = new WorkbenchScenariosWindow(desktop, WorkbenchPreferences.Load(preferences.StoragePath).Window("scenarios"));
            var window = scenariosWindow;
            window.Closed = () => { if (ReferenceEquals(scenariosWindow, window)) scenariosWindow = null; RememberWindow("scenarios", window); };
            desktop.Insert(window);
            if (window.size.x != desktop.size.x || window.size.y != desktop.size.y) window.Zoom();
        }
        RecheckScenarioFiles(focus);
        scenariosWindow.Select();
        if (!ReferenceEquals(desktop.current, scenariosWindow)) desktop.SetCurrent(scenariosWindow, selectMode.normalSelect);
    }

    private ScenarioDescriptor[]? CompiledForTarget => compiledTarget is not null && WorkbenchPreferences.SamePath(compiledTarget, target.Data) ? compiledRaw : null;
    private bool OrdinaryTarget()
    {
        string path = target.Data;
        if (path.Length == 0 || !File.Exists(path)) return false;
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (hostsCache is not { } cache || cache.Path != path || cache.Written != written) hostsCache = cache = (path, written, ScenarioFiles.HostsDiagnostics(path));
        return cache.Hosts == false;
    }
    // Recheck the attached documents (fast: reading and parsing only) and redisplay them.
    private void RecheckScenarioFiles(string? focus = null)
    {
        attached = ScenarioFiles.Check(projectDefaults.ScenarioFiles, target.Data, CompiledForTarget, OrdinaryTarget(), null, projectDefaults.LegacyFixtures);
        ReloadScenariosWindow(focus);
    }
    private void ReloadScenariosWindow(string? focus = null)
    {
        if (scenariosWindow is null) return;
        var compiled = (CompiledForTarget ?? []).Where(s => !s.IsFixtureOnly && s.External is null && !attached.Any(a => a.Info.Id == s.Id))
            .Select(s => new AttachedScenario(new ScenarioFileInfo("compiled/" + s.Id, ScenarioFileStatus.Valid, "Compiled read-only", Compiled: s), "Compiled", null, s));
        var storedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Native is { } native)
        {
            var references = native.Session.Scenarios.Where(reference => reference.ArtifactPath is not null)
                .ToDictionary(reference => reference.ArtifactPath!, reference => reference.Id, StringComparer.Ordinal);
            foreach (var item in attached.Where(item => item.Info.Id is null))
                if (references.TryGetValue(native.ArtifactName(item.Path), out string? id))
                    storedIds[item.Path] = id;
        }
        scenariosWindow.Load([.. attached, .. compiled], focus, storedIds);
    }

    private string ScenarioStart()
    {
        string? start = scenarioFolder ?? projectDefaults.ScenarioFiles.LastOrDefault() ?? ProjectPath ?? (target.Data.Length > 0 ? target.Data : null);
        return InitialDirectory(start ?? Environment.CurrentDirectory);
    }

    // Why a file cannot be attached, or null. Checked against the current attachments before anything changes.
    private string? AttachProblem(ScenarioFileInfo info, IReadOnlyList<string> files)
    {
        if (files.Any(f => WorkbenchPreferences.SamePath(f, info.Path))) return "it is already attached to this project.";
        if (info.Status != ScenarioFileStatus.Valid) return ScenarioFiles.StatusLabel(info.Status) + ": " + info.Message;
        var check = ScenarioFiles.Check([.. files, info.Path], target.Data, null, false);
        var mine = check.Last();
        return mine.Status is "Duplicate ID" or "Mixed" ? mine.Status + ": " + mine.Problem : null;
    }

    internal void SetScenarioFiles(string[] files, string message, string? focus)
    {
        projectDefaults = projectDefaults with { ScenarioFiles = files };
        detailKey = ""; UpdateProjectTitle(); UpdateHeader();
        RecheckScenarioFiles(focus);
        Status(message);
        if (target.Data.Length == 0 || !File.Exists(target.Data))
        { Status(message + " Set the executable in Project Properties (Alt+O) to load the scenarios."); return; }
        // The new list is loaded in the background; a loading already in progress is cancelled and restarted.
        if (busy) { afterRun = LoadCatalog; cancellation?.Cancel(); }
        else LoadCatalog();
    }

    // Add Files: the framework file dialog selects one file at a time, so Add attaches it and the dialog opens again in the
    // same folder (repeat-add); Done ends. Each file is validated before it is attached; a refused file changes nothing.
    private void AddFiles()
    {
        string start = ScenarioStart(); int added = 0; string? last = null;
        var files = projectDefaults.ScenarioFiles.ToList();
        while (true)
        {
            using var picker = new TFileDialog("*.json", added == 0 ? "Add scenario files" : $"Add scenario files ({added} added)", "Scenario ~f~ile",
                FileDialogOptions.fdAddButton | FileDialogOptions.fdDoneButton | FileDialogOptions.fdNoLoadDir, 0);
            picker.fileName!.MaxLen = 32767; picker.helpCtx = WorkbenchApp.DialogContext; Widen(picker, desktop);
            picker.SetData(Path.Combine(start, "*.json"));
            if (desktop.ExecView(picker) != Views.cmFileOpen) break;
            picker.GetFileName(out string selected);
            start = InitialDirectory(selected);
            var info = ScenarioFiles.Inspect(selected);
            if (AttachProblem(info, files) is { } problem)
            {
                Fail("Cannot Add Scenario File", $"{Path.GetFileName(info.Path)} was not attached: {problem}\n\nFile: {info.Path}\nThe project is unchanged.", $"{info.Path}: {problem}");
                continue;
            }
            string managed;
            try { managed = Native?.Attach(info.Path) ?? info.Path; }
            catch (Exception ex) { Error("Scenario was not imported: " + ex.Message); continue; }
            files.Add(managed); added++; last = managed; scenarioFolder = Path.GetDirectoryName(info.Path);
            projectDefaults = projectDefaults with { ScenarioFiles = files.ToArray() };
            UpdateProjectTitle(); RecheckScenarioFiles(last);
        }
        if (added > 0) SetScenarioFiles(files.ToArray(), $"Attached {added} scenario file{(added == 1 ? "" : "s")}; Ctrl+S saves the project.", last);
    }

    private void AddFolder()
    {
        using var picker = new WorkbenchDirectoryDialog(ScenarioStart(), "Select scenario folder");
        if (desktop.ExecView(picker) != Views.cmOK) { Status("Add folder cancelled; the project is unchanged."); return; }
        WorkbenchAddFolderDialog dialog;
        try { dialog = new WorkbenchAddFolderDialog(desktop, picker.SelectedPath, projectDefaults.ScenarioFiles); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Fail("Cannot Read Folder", $"{picker.SelectedPath} cannot be read: {ex.Message}", ex.ToString()); return; }
        using (dialog)
        {
            if (desktop.ExecView(dialog) != Views.cmOK) { Status("Add folder cancelled; the project is unchanged."); return; }
            var scan = dialog.Scan;
            scenarioFolder = scan.Folder;
            string[] imported;
            try { imported = Native?.Import(scan.Add.Select(i => i.Path)) ?? scan.Add.Select(i => i.Path).ToArray(); }
            catch (Exception ex) { Error("Import canceled: " + ex.Message); return; }
            string[] files = [.. projectDefaults.ScenarioFiles, .. imported];
            string skipped = scan.Rejected.Length + scan.Ignored.Length == 0 ? "" : $" {scan.Rejected.Length} rejected and {scan.Ignored.Length} ignored files were not attached (details in Messages).";
            if (scan.Rejected.Length + scan.Ignored.Length > 0)
                notifications.Info($"Add folder: {scan.Rejected.Length} rejected, {scan.Ignored.Length} ignored in {scan.Folder}.",
                    string.Join("\n", scan.Rejected.Select(r => "Rejected " + r.Info.Path + ": " + r.Reason).Concat(scan.Ignored.Select(i => "Ignored " + i.Path + ": " + i.Message))));
            SetScenarioFiles(files, $"Attached {scan.Add.Length} scenario file{(scan.Add.Length == 1 ? "" : "s")} from {Path.GetFileName(scan.Folder)}"
                + (scan.Recursive ? " and its subfolders" : "") + "." + skipped, imported.FirstOrDefault());
        }
    }

    // Remove scenario detaches the reference only; the JSON file is never deleted (nothing here writes or deletes it).
    // The remaining order is kept.
    internal const string RemoveTitle = "Remove Scenario from Project";
    private void RemoveSelected()
    {
        if (SelectedScenarioFile() is not { } path) { Error(ScenarioSelectionHint()); return; }
        var files = projectDefaults.ScenarioFiles;
        int index = Array.FindIndex(files, f => WorkbenchPreferences.SamePath(f, path));
        if (index < 0) { Error($"{Path.GetFileName(path)} is not attached to this project."); return; }
        string? id = attached.FirstOrDefault(a => WorkbenchPreferences.SamePath(a.Path, path))?.Info.Id;
        var reference = Native?.Session.Scenarios.FirstOrDefault(s => s.ArtifactPath == Native.ArtifactName(path));
        var dependents = reference is null ? [] : Native!.Session.Tests.Where(t => t.ScenarioId == reference.Id).Select(t => t.Title).ToArray();
        if (dependents.Length > 0) { Error("Reassign these tests in Test Explorer first: " + string.Join(", ", dependents)); return; }
        if (editors.Any(e => WorkbenchPreferences.SamePath(e.Document.Path, path) && e.Document.Dirty))
        { Error("Save or discard the scenario draft in its editor first."); return; }
        string text = $"Remove {Path.GetFileName(path)}{(id is null ? "" : $" ({id})")} from this project?\n\n"
            + "This only detaches the file from the project. The JSON file is NOT deleted; it stays on disk and can be added again:\n" + path;
        if (Confirm(desktop, RemoveTitle, text, "~R~emove") != Views.cmYes) { Status("Nothing was removed."); return; }
        if (reference is not null) Native!.Session.RemoveScenario(reference.Id);
        foreach (var editor in editors.Where(e => WorkbenchPreferences.SamePath(e.Document.Path, path)).ToArray()) editor.CloseApproved();
        string[] remaining = files.Where((_, i) => i != index).ToArray();
        string? next = remaining.ElementAtOrDefault(Math.Min(index, remaining.Length - 1));
        SetScenarioFiles(remaining, $"Removed {Path.GetFileName(path)} from the project; the file was not deleted. Ctrl+S saves the project.", next);
    }
    // A confirmation whose default (Enter, Esc) is Cancel.
    internal static ushort Confirm(TDeskTop desktop, string title, string text, string action)
    {
        var lines = text.Split('\n').SelectMany(l => WorkbenchText.Wrap(l, Math.Min(80, desktop.size.x - 8))).ToArray();
        int w = Math.Min(Math.Max(60, lines.Max(l => l.Length) + 6), desktop.size.x - 2), h = Math.Min(lines.Length + 7, desktop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), title); dialog.options |= Views.ofCentered; dialog.helpCtx = WorkbenchApp.DialogContext;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 4), string.Join('\n', lines)));
        dialog.Insert(new TButton(new(3, h - 3, 17, h - 1), action, Views.cmYes, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 17, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfDefault); dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog);
    }

    // Open in external editor (the original behaviour, now the explicit secondary option).
    private void OpenInEditor(string? explicitPath = null)
    {
        if ((explicitPath ?? SelectedScenarioFile()) is not { } path) { Error(ScenarioSelectionHint()); return; }
        if (!File.Exists(path)) { Error($"{Path.GetFileName(path)} does not exist; it cannot be opened. Remove it from the project or restore the file.", "Missing scenario file: " + path); return; }
        var start = ScenarioFiles.EditorCommand(path);
        try
        {
            if (!EditorLauncher(start)) throw new InvalidOperationException("The editor process did not start.");
            Status($"Opened {Path.GetFileName(path)} in {Path.GetFileName(start.FileName)}. After saving it there, Refresh (F2) reloads the scenario"
                + (editors.Any(e => e.Document.Path is { } p && WorkbenchPreferences.SamePath(p, path)) ? "; the internal editor refuses to overwrite those changes (it offers Reload)." : "."));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Fail("Cannot Open Editor", $"The editor '{start.FileName}' could not be started for {Path.GetFileName(path)}: {ex.Message}\n\n"
                + $"Set the environment variable {ScenarioFiles.EditorVariable} to the full path of your editor and restart the Workbench.", ex.ToString());
        }
    }

    // Validate: every attached document (existence, JSON, schema/version, document rules, duplicate IDs, one kind per
    // project) and, for fixture-bound documents, the binding against the target's advertised fixtures. Binding needs the
    // target's raw catalog: when it is not known yet it is discovered in the background (no scenario runs).
    private void Validate()
    {
        ShowScenarios();
        if (projectDefaults.ScenarioFiles.Length == 0) { Status(ScenarioFiles.NoneAttached); return; }
        bool needBinding = attached.Any(a => a.Status == "Not bound") && target.Data.Length > 0 && File.Exists(target.Data);
        if (!needBinding) { ReportValidation(); return; }
        if (busy) { Status("Scenarios are loading; Validate checks the fixture bindings when you run it again after loading."); ReportValidation(); return; }
        string path = Path.GetFullPath(target.Data);
        Status($"Validating: discovering the fixtures of {Path.GetFileName(path)} (no scenario runs)...");
        StartWork(async token =>
        {
            var raw = await runner.DiscoverRawAsync(path, token);
            Deliver(() => { compiledRaw = raw; compiledTarget = path; RecheckScenarioFiles(); ReportValidation(); });
        });
    }
    private void ReportValidation()
    {
        RecheckScenarioFiles();
        var problems = attached.Where(a => a.Problem is not null).ToArray();
        var deploymentNotes = attached.Where(a => a.Note is not null).ToArray();
        int n = attached.Length;
        string details = string.Join("\n", attached.Select(a =>
            $"{a.Path}: {a.Status}" + ((a.Problem ?? a.Note) is { } issue ? " - " + issue : "")));
        if (problems.Length == 0)
        {
            notifications.Post(deploymentNotes.Length > 0 ? NoticeLevel.Warning : NoticeLevel.Info,
                $"Validation passed: {n} attached scenario file{(n == 1 ? " is" : "s are")} valid"
                + (attached.Any(a => a.Status == "Bound") ? " and bound to the target's fixtures." : ".")
                + (deploymentNotes.Length > 0 ? " See Messages for deployment warnings." : ""), details);
            if (deploymentNotes.Length > 0) scenariosWindow?.Focus(deploymentNotes[0].Path);
            return;
        }
        string kinds = string.Join(", ", problems.GroupBy(a => a.Status).Select(g => $"{g.Count()} {g.Key}"));
        notifications.Warning($"Validation: {problems.Length} of {n} attached file{(n == 1 ? " has" : "s have")} problems ({kinds}). See Messages for each cause.", details);
        scenariosWindow?.Focus(problems[0].Path);
    }

    // New Scenario: a minimal valid document, written only after it validated (and, fixture-bound, bound to its fixture),
    // then attached. An existing file is replaced only after confirmation.
    // Returns the created file (the caller opens it in the internal editor), null when nothing was created.
    private string? CreateScenario()
    {
        RecheckScenarioFiles();
        bool? projectProxy = attached.FirstOrDefault(a => a.Info.Status == ScenarioFileStatus.Valid && a.Status is not ("Mixed" or "Duplicate ID" or "Duplicate"))?.Info.Proxy;
        var compiled = CompiledForTarget;
        var used = attached.Select(a => a.Info.Id).OfType<string>().Concat(catalog.Select(s => s.Id)).Concat(compiled?.Select(s => s.Id) ?? []);
        using var dialog = new WorkbenchNewScenarioDialog(desktop, compiled is null ? [] : ScenarioFiles.BindableFixtures(compiled, projectDefaults.LegacyFixtures), projectProxy, used);
        if (desktop.ExecView(dialog) != Views.cmOK) return null;
        string id = dialog.ScenarioId; var fixture = dialog.Fixture;
        if (Native is not null)
        {
            string managed = Native.CreateScenario(ScenarioFiles.Template(id, dialog.ScenarioTitle, dialog.Group, fixture));
            SetScenarioFiles([.. projectDefaults.ScenarioFiles, managed], "Created a managed scenario. Project Save writes the archive.", managed);
            return managed;
        }
        string suggested = Path.Combine(ScenarioStart(), ProjectLocation.SafeName(id.Split('/').Last()) + ".scenario.json");
        string? path = ChooseScenarioPath(suggested);
        if (path is null) return null;
        if (Path.GetExtension(path).Length == 0) path += ".json";
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) { Error("Scenario documents use the .json extension; nothing was created."); return null; }
        string folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) { Error($"The folder {folder} does not exist; nothing was created."); return null; }
        if (editors.Any(e => e.Document.Path is { } p && WorkbenchPreferences.SamePath(p, path) && e.Document.Dirty))
        { Error($"{Path.GetFileName(path)} is open in the scenario editor with unsaved changes; save or close it first. Nothing was created."); return null; }
        if (File.Exists(path) && Confirm(desktop, "Replace File?", $"{Path.GetFileName(path)} already exists. Replace it with the new scenario?\n\n{path}", "~R~eplace") != Views.cmYes) return null;
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, ScenarioFiles.Template(id, dialog.ScenarioTitle, dialog.Group, fixture));
            var document = ExternalScenarios.Load(temp);
            if (fixture is not null) ExternalScenarios.Merge(compiled!, [document], projectDefaults.LegacyFixtures);
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is ExternalScenarioException or IOException or UnauthorizedAccessException)
        {
            Fail("Cannot Create Scenario", $"{Path.GetFileName(path)} was not created: {ScenarioFiles.Detail(ex, temp)}", ex.ToString());
            return null;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        scenarioFolder = folder;
        // An editor still showing the replaced file (unchanged) is closed; the new document opens fresh.
        foreach (var stale in editors.Where(e => e.Document.Path is { } p && WorkbenchPreferences.SamePath(p, path)).ToArray()) stale.CloseApproved();
        var files = projectDefaults.ScenarioFiles;
        if (files.Any(f => WorkbenchPreferences.SamePath(f, path))) { RecheckScenarioFiles(path); Post(Load); Status($"Replaced {Path.GetFileName(path)}; it stays attached."); return path; }
        SetScenarioFiles([.. files, Path.GetFullPath(path)], $"Created {Path.GetFileName(path)} and attached it; the scenario editor is open. Ctrl+S saves the project.", path);
        return path;
    }
    private string? ChooseScenarioPath(string suggested, string title = "Save new scenario as")
    {
        using var picker = new TFileDialog("*.json", title, "Scenario ~f~ile", FileDialogOptions.fdOKButton | FileDialogOptions.fdNoLoadDir, 0);
        picker.fileName!.MaxLen = 32767; picker.helpCtx = WorkbenchApp.DialogContext; Widen(picker, desktop);
        picker.SetData(Path.Combine(Path.GetDirectoryName(suggested)!, "*.json"));
        picker.fileName.SetData(suggested); picker.fileName.SelectAll(true);
        if (desktop.ExecView(picker) is not (Views.cmFileOpen or Views.cmOK)) return null;
        picker.GetFileName(out string path);
        return Path.GetFullPath(path);
    }
}
