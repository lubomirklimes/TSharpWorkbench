using TSharpWorkbench.Storage;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Keep the existing internal type name for diagnostic consumers; this is a plain TWindow.
internal sealed partial class WorkbenchDialog
{
    // Saved project defaults (project data, .twproj) versus the in-memory session run configuration:
    // the session starts as a copy of the run defaults and may differ without dirtying the project.
    private WorkbenchProject projectDefaults = new();
    private WorkbenchProject sessionRun = new();
    private WorkbenchProject savedProject = new();
    private bool approvedClose;
    internal string? ProjectPath { get; private set; }
    internal Action? Closed { get; set; }
    internal Action? QuitReady { get; set; }
    internal Action<string>? ProjectSaved { get; set; }
    internal bool Busy => busy;
    // Only project data marks the project dirty: properties, marked scenario IDs and saved run defaults.
    // Search text, group focus and collapsed groups are per-user view state (user settings).
    internal NativeProject? Native { get; set; }
    private CloseChoice? nativeCloseChoice;
    internal bool Dirty => (Native is not null && ScenarioEditors.Any(e => e.Document.Dirty)) || (Native?.Session.IsDirty ?? false) || ProjectData(CaptureProject()).Fingerprint() != ProjectData(savedProject).Fingerprint();
    internal ScenarioList TestList => scenarios;
    internal GroupTree Tree => groups;
    internal TListBox DetailPane => detailPane;
    private static WorkbenchProject ProjectData(WorkbenchProject p) => p with { Search = "", Group = null, CollapsedGroups = [] };

    private void InitializeProjectDefaults()
    {
        var launch = preferences.LaunchOptions;
        int? Number(string key) => launch.TryGetValue(key, out var value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        string[] sizes = preferences.Size is "standard" or "both" ? ["80x25", "140x40"] : SafeSizes(preferences.Size);
        string? custom = sizes.FirstOrDefault(s => s is not ("80x25" or "140x40"));
        int cw = 100, ch = 30;
        if (custom is not null && ScenarioSelection.TryParseSize(custom, out int w, out int h)) { cw = w; ch = h; }
        string targetDriver = launch.GetValueOrDefault("--driver", "NullDriver");
        projectDefaults = new()
        {
            Name = string.IsNullOrWhiteSpace(target.Data) ? "Untitled project" : ApplicationName(target.Data),
            ProxyBackend = launch.GetValueOrDefault("--proxy-backend", "NullDriver"), ProxyCapture = launch.GetValueOrDefault("--proxy-capture", "none"),
            Mode = targetDriver, TestType = targetDriver!="NullDriver"?"rendering":launch.ContainsKey("--baselines")?"visual":"functional", Inspect = launch.GetValueOrDefault("--inspect", "declared"),
            Allow = launch.TryGetValue("--allow", out var allow) ? allow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [],
            BaselineStore = launch.GetValueOrDefault("--baselines"), Timeout = Number("--timeout"), ScenarioTimeout = Number("--scenario-timeout"),
            ScreenSizes = sizes, CustomWidth = cw, CustomHeight = ch
        };
        sessionRun = projectDefaults;
    }
    private static string[] SafeSizes(string value)
    {
        try { var parsed = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); foreach (string s in parsed) ScenarioSelection.ParseSize(s); return parsed.Length == 0 ? ["80x25"] : parsed; }
        catch (ArgumentException) { return ["80x25"]; }
    }
    internal WorkbenchProject CaptureProject() => projectDefaults with
    {
        Target = target.Data, OutputRoot = output.Data,
        SelectedIds = (initialSelectionRestored ? scenarios.MarkedIds : preferences.SelectedIds).Order(StringComparer.Ordinal).ToArray(),
        Group = initialSelectionRestored ? FilterGroup : preferences.Group, Search = search.Data,
        CollapsedGroups = groups.Collapsed.Order(StringComparer.Ordinal).ToArray()
    };
    internal void ApplyProject(WorkbenchProject project, string path, WorkbenchViewState? view = null)
    {
        project.Validate(); projectDefaults = project; sessionRun = project; ProjectPath = Path.GetFullPath(path);
        groups.Collapsed.Clear(); groups.Collapsed.UnionWith(view?.CollapsedGroups ?? project.CollapsedGroups);
        savedProject = CaptureProject(); UpdateProjectTitle(); UpdateHeader();
        if (project.Target.Length > 0 && !File.Exists(project.Target))
            Error($"The project target is missing: {Path.GetFileName(project.Target)}. Correct it in Project Properties (Alt+O), then press F2.", "Missing project target: " + project.Target);
    }

    // TFileDialog navigates only when SetData receives a wildcard. A literal "<project>.twproj" left the dialog in the
    // process directory (the Open Run defect pattern), so start from the project's (or most recent) folder.
    internal static string ProjectDialogStart(string? current) =>
        Path.Combine(current is null ? Environment.CurrentDirectory : InitialDirectory(current), "*.tsw");
    // An untitled project's Save as lists the nearest existing folder and proposes the suggested file
    // (<Documents>\TSharpWorkbench\<name>\<name>.twproj); Enter accepts it, typing replaces it.
    // The framework file dialog opens 49 columns wide; wider, a long path stays readable (its controls grow with it).
    internal static void Widen(TFileDialog picker, TDeskTop desktop) => picker.GrowTo(Math.Max(picker.size.x, Math.Min(100, desktop.size.x - 4)), picker.size.y);
    internal static string? ChooseProjectPath(TDeskTop desktop, bool save, string? current, string? suggested = null)
    {
        using var picker = new TFileDialog("*.tsw", save ? "Save project as" : "Open project", "Project ~n~ame",
            (ushort)((save ? FileDialogOptions.fdOKButton : FileDialogOptions.fdOpenButton) | FileDialogOptions.fdNoLoadDir), 0);
        picker.fileName!.MaxLen = 32767;
        picker.helpCtx = 0x7dc4; Widen(picker, desktop);
        picker.SetData(ProjectDialogStart(current ?? suggested));
        if (suggested is not null && current is null) { picker.fileName.SetData(suggested); picker.fileName.SelectAll(true); }
        if (desktop.ExecView(picker) is not (Views.cmFileOpen or Views.cmOK)) return null;
        picker.GetFileName(out string path);
        if (save && Path.GetExtension(path).Length == 0) path += ".tsw";
        return Path.GetFullPath(path);
    }
    // Saving needs only project configuration: no run, result, capture or baseline. Scenario loading may
    // still be in progress (it can take the whole discovery deadline for an ordinary application); only an active run waits.
    internal bool SaveProject(bool saveAs, string? explicitPath = null)
    {
        if (busy && activeRequest is not null) { Error("Tests are running. Save the project when the run has finished, or cancel the run (Test > Cancel run)."); return false; }
        if (Native is null) { Error("This session has no native project. Create or open a .tsw project first."); return false; }
        string? copyPath = saveAs ? explicitPath ?? ChooseProjectPath(desktop, true, ProjectPath) : null;
        if (saveAs && copyPath is null) return false;
        if (saveAs && (busy || ScenarioEditors.Any(e => e.Document.Dirty)))
        {
            Error("Finish scenario loading or running and save open scenario drafts before Save As.");
            return false;
        }
        try
        {
            foreach (var editor in ScenarioEditors)
                if (!editor.SaveForProject()) { editor.Select(); return false; }
            Native.Store(CaptureProject());
            if (copyPath is not null)
            {
                Native.Session.SaveCopy(copyPath);
                var prepared = NativeProject.Open(copyPath);
                WorkbenchProject settings;
                try
                {
                    settings = prepared.Read();
                    Native.Session.Close(CloseChoice.Discard);
                }
                catch
                {
                    prepared.Dispose();
                    throw;
                }
                foreach (var editor in ScenarioEditors.ToArray()) editor.CloseApproved();
                Native = prepared;
                history.Clear(); runInfo.Clear(); requests.Clear(); resultIndex = 0;
                resultsWindow?.Reload(); ReloadRunExplorer(); baselineExplorer?.Reload();
                ApplyProject(settings, copyPath);
                InvalidateCatalog();
                RecheckScenarioFiles();
                LoadCatalog();
                ProjectSaved?.Invoke(copyPath);
                Status("Switched to " + copyPath);
                return true;
            }
            Native.Session.Save();
            savedProject = CaptureProject(); UpdateProjectTitle(); UpdateHeader();
            ProjectSaved?.Invoke(ProjectPath!); Status("Saved native project: " + ProjectPath); return true;
        }
        catch (Exception ex)
        {
            Fail("Cannot Save Project", SaveFailure(ProjectPath, ex) + " Your changes remain recoverable; the project stays open.", ex.ToString());
            return false;
        }
    }
    // Plain reason without the temporary file name the atomic save writes first.
    private static string SaveFailure(string? path, Exception ex)
    {
        string name = path is null ? "the project file" : Path.GetFileName(path);
        string reason = ex switch
        {
            DirectoryNotFoundException => "the folder does not exist",
            UnauthorizedAccessException => "access is denied",
            ArgumentException a => a.Message.TrimEnd('.'),
            _ => System.Text.RegularExpressions.Regex.Replace(ex.Message, @"[^\s'""]*\.twproj\.[0-9a-f]{32}\.tmp", name).TrimEnd('.')
        };
        return $"Could not write {name}: {reason}.";
    }
    // While a run is active, Tests Are Running asks first; a confirmed cancellation retries the operation after cleanup,
    // where the unsaved-changes check then applies as usual.
    internal bool CanReplace(Action retry)
    {
        if (busy && activeRequest is null) { Error("Scenarios are still loading. Try again when loading has finished."); return false; }
        if (busy) { ConfirmLeavingRun("continue", retry); return false; }
        return (Native is not null || CloseScenarioEditors()) && ConfirmDiscard();
    }
    // Unsaved Project Changes: Cancel is the default; Enter without choosing, Esc and the close icon all cancel.
    // Save and Discard need an explicit choice; a cancelled Save as aborts the original close, switch or exit.
    internal bool ConfirmDiscard()
    {
        if (!Dirty) { nativeCloseChoice = CloseChoice.Save; return true; }
        string[] changes = ChangedGroups();
        if (Native is not null && ScenarioEditors.Any(e => e.Document.Dirty)) changes = [.. changes, "open scenario editor changes"];
        ushort choice = UnsavedChanges(desktop, projectDefaults.Name, changes, ProjectPath);
        if (choice == Views.cmYes) { if (!SaveProject(false)) return false; nativeCloseChoice = CloseChoice.Save; return true; }
        if (choice == Views.cmNo) { nativeCloseChoice = CloseChoice.Discard; return true; }
        nativeCloseChoice = null; return false;
    }
    internal string[] ChangedGroups()
    {
        var now = ProjectData(CaptureProject()); var saved = ProjectData(savedProject);
        var changes = new List<string>();
        if (now.Name != saved.Name || now.Target != saved.Target || now.OutputRoot != saved.OutputRoot || now.BaselineStore != saved.BaselineStore
            || !now.Allow.SequenceEqual(saved.Allow)) changes.Add("project properties (name, target, runs folder, baseline store or capabilities)");
        if (!now.ScenarioFiles.SequenceEqual(saved.ScenarioFiles)) changes.Add($"attached scenario files (now {now.ScenarioFiles.Length} attached)");
        if (!now.SelectedIds.SequenceEqual(saved.SelectedIds)) changes.Add($"marked scenarios (now {now.SelectedIds.Length} marked)");
        var runNow = now with { Name = "", Target = "", OutputRoot = "", BaselineStore = null, Allow = [], SelectedIds = [], ScenarioFiles = [] };
        var runSaved = saved with { Name = "", Target = "", OutputRoot = "", BaselineStore = null, Allow = [], SelectedIds = [], ScenarioFiles = [] };
        if (runNow.Fingerprint() != runSaved.Fingerprint()) changes.Add("run defaults (from Run Configuration)");
        return changes.ToArray();
    }
    internal const string UnsavedTitle = "Unsaved Project Changes";
    internal static ushort UnsavedChanges(TDeskTop desktop, string name, string[] changes, string? path)
    {
        string[] lines = [$"{name} has unsaved changes:", "", .. changes.Select(c => "  - " + c), "",
            path is null ? "The project has not been saved yet. Save asks for a file name." : $"Save them to {Path.GetFileName(path)}?"];
        int w = Math.Min(Math.Max(68, lines.Max(l => l.Length) + 6), desktop.size.x - 2), h = lines.Length + 6;
        using var dialog = new TDialog(new(0,0,w,h), UnsavedTitle); dialog.options |= Views.ofCentered;
        dialog.helpCtx = 0x7dc4;
        dialog.Insert(new TStaticText(new(3,2,w-2,h-4), string.Join('\n', lines.Select(l => WorkbenchText.Fit(l, w - 5)))));
        dialog.Insert(new TButton(new(3,h-3,17,h-1), "~S~ave", Views.cmYes, ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(19,h-3,33,h-1), "~D~iscard", Views.cmNo, ButtonConstants.bfNormal));
        var cancel = new TButton(new(35,h-3,49,h-1), "Cancel", Views.cmCancel, ButtonConstants.bfDefault); dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog);
    }
    internal static ushort Prompt(TDeskTop desktop, string title, string text, bool discard)
    {
        if (!discard) { ShowMessage(desktop, title, text); return Views.cmCancel; }
        int w = Math.Min(74, desktop.size.x - 2), h = 10;
        using var dialog = new TDialog(new(0,0,w,h), title); dialog.options |= Views.ofCentered;
        dialog.helpCtx = 0x7dc4;
        dialog.Insert(new TStaticText(new(2,2,w-2,6), text));
        dialog.Insert(new TButton(new(2,7,19,9), discard ? "~S~ave" : "~R~eplace", Views.cmYes, ButtonConstants.bfNormal));
        if (discard) dialog.Insert(new TButton(new(21,7,38,9), "~D~iscard", Views.cmNo, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w-19,7,w-2,9), "Cancel", Views.cmCancel, ButtonConstants.bfDefault); dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog);
    }
    internal static void ShowMessage(TDeskTop desktop, string title, string text, ushort context = 0x7dc4)
    {
        int w = Math.Min(76, desktop.size.x - 2), h = Math.Min(18, desktop.size.y);
        using var dialog = new TDialog(new(0,0,w,h), title); dialog.options |= Views.ofCentered;
        dialog.helpCtx = context;
        var scroll = new TScrollBar(new(w-3,2,w-2,h-4)); dialog.Insert(scroll);
        var list = new TListBox(new(2,2,w-3,h-4), 1, scroll); dialog.Insert(list);
        var lines = new TStringCollection(); foreach (string line in Wrapped([text], w-7)) lines.Insert(line); list.NewList(lines);
        dialog.Insert(new TButton(new(w-15,h-3,w-2,h-1), "~C~lose", Views.cmCancel, ButtonConstants.bfDefault)); list.Select(); desktop.ExecView(dialog);
    }
    internal void CloseApproved() { approvedClose = true; Close(); }
    public override void ShutDown()
    {
        SaveViewState();
        TestExplorer?.Close(); EnvironmentManager?.EndModal(Views.cmCancel);
        matrixWindow?.Close();
        baselineExplorer?.Close();
        resultsWindow?.Close(); runningWindow?.Close(); scenariosWindow?.Close(); if (comparisonWindow?.owner is not null) comparisonWindow.Close();
        foreach (var editor in ScenarioEditors.ToArray()) editor.CloseApproved();
        if (Native is not null)
        {
            try { if (nativeCloseChoice is { } choice) Native.Session.Close(choice); }
            catch (Exception ex) { notifications.Warning("Project working files retained for recovery: " + ex.Message); }
            finally { Native.Dispose(); Native = null; }
        }
        base.ShutDown(); Closed?.Invoke();
    }
    public override void SizeLimits(ref TPoint min, ref TPoint max)
    {
        base.SizeLimits(ref min, ref max); min = new(Math.Min(100, max.x), Math.Min(28, max.y));
    }
    internal void RefreshNativeTitle() { UpdateProjectTitle(); OverviewChanged?.Invoke(); }
    private void UpdateProjectTitle()
    {
        if (Native is not null)
        {
            try { Native.Store(CaptureProject()); }
            catch (Exception ex) { notifications.Warning("Project changes need attention before Save: " + ex.Message); }
        }
        string file = ProjectPath is null ? "not saved" : Path.GetFileName(ProjectPath);
        string next = projectDefaults.Name + (Dirty ? " *" : "") + " (" + file + ")";
        if (title == next) return;
        title = next; frame?.DrawView();
    }
    private void SaveViewState()
    {
        if (ProjectPath is null || !initialized) return;
        var state = new WorkbenchViewState(search.Data, FilterGroup, groups.Collapsed.Order(StringComparer.Ordinal).ToArray(), false);
        try { WorkbenchPreferences.Update(preferences.StoragePath, p => p.WithViewState(ProjectPath, state)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error("Could not save the view settings.", ex.ToString()); }
    }
    private static IEnumerable<string> Wrapped(IEnumerable<string> source, int width)
    {
        width = Math.Max(1, width);
        foreach (string text in source)
            foreach (string line in text.Replace("\r", "").Split('\n'))
                for (int i = 0; i < Math.Max(1, line.Length); i += width) yield return line.Substring(i, Math.Min(width, line.Length - i));
    }

    // ── header, caption and Details pane ───────────────────────────────────────────

    private ScenarioResult? LastExecution(string id) => history.LastOrDefault(b => runInfo.TryGetValue(b.RunRoot, out var info) && info.TargetPath == target.Data
        && b.Results.Any(r => r.ScenarioId == id))?.Results.LastOrDefault(r => r.ScenarioId == id);
    private string LastResult(ScenarioDescriptor s) => LastExecution(s.Id)?.Outcome switch
    { null => "-", ScenarioOutcomes.Passed => "Passed", string other => other.ToUpperInvariant() };
    private string Note(ScenarioDescriptor s)
    {
        if (targetDriver != "NullDriver" && !NativeExecution.Supports(s, targetDriver)) return "not runnable with " + targetDriver;
        if (s.ExpectedResult != ScenarioOutcomes.Passed) return "expected fail";
        return (s.Safety ?? ScenarioSafety.None).FixtureFiles ? "fixture" : "";
    }
    // The session run configuration, marked when it differs from the saved project defaults.
    private string RunSetup(WorkbenchProject p)
    {
        string mode = ModeName(p);
        string driver = WorkbenchCapabilities.TargetDriverName(p.TestType == "rendering" ? p.Mode : p.ProxyBackend);
        if (driver == "headless (NullDriver)") driver = "headless target (NullDriver)";
        int scope = p.RunScope switch { "group" => FilterGroup is { } g ? catalog.Count(s => ScenarioSelection.InGroup(s.Group, g)) : 0,
            "all" => catalog.Count(s => s.ExpectedResult == ScenarioOutcomes.Passed), _ => scenarios.MarkedIds.Count };
        string scopeText = p.RunScope switch { "group" => "current group", "all" => "all runnable", _ => "marked" };
        int executions = scope * p.ScreenSizes.Length;
        return (SessionDiffers ? "(session only) " : "")
            + $"{mode} | {driver} | {string.Join(", ", p.ScreenSizes)} | {scopeText}: {executions} execution{(executions == 1 ? "" : "s")}";
    }
    // The project summary: computed where the Workspace header was updated, shown only by
    // Project > Information... (WorkbenchProjectInfoDialog), which reads these strings and never diagnoses while drawing.
    private sealed record ProjectSummary(string Target, string Output, string Setup, string Loaded, string Marked, string LastRun, string State);
    private ProjectSummary summary = new("", "", "", "", "", "", "");
    private WorkbenchProjectInfoDialog? projectInfo;
    // The target state last reported as a contextual warning; a warning is posted once per new problem, never repeated.
    private string? warnedTarget;
    private void UpdateHeader()
    {
        if (!initialized) return;
        string exe = target.Data;
        // The deployment's readiness for this project's scenarios and session drivers (static; details in
        // Project > Target diagnostics), between the executable and its folder.
        string readiness = "", state;
        string? problem = null;
        if (exe.Length == 0) state = "not checked: no target executable (Project > Properties, F4)";
        else if (!File.Exists(exe)) state = "not checked: the target executable is missing (Project > Properties, F4)";
        else
        {
            try { (readiness, problem) = TargetState(); state = readiness; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BadImageFormatException or ArgumentException)
            { state = "diagnostics unavailable: " + ex.Message; }
        }
        bool provisional = ProjectPath is null && ProjectLocation.IsDefaultRuns(output.Data, null, projectDefaults.Name);
        string moves = provisional ? "  (moves with Save as)" : "";
        int files = projectDefaults.ScenarioFiles.Length;
        string last = "none in this session";
        if (activeRequest is not null) last = cancelRequested ? $"cancelling ({completed - cancelled} of {total} completed)"
            : $"running {Math.Min(completed - cancelled + 1, Math.Max(1, total))} of {total} (Alt+3 monitor)";
        else if (history.LastOrDefault(b => runInfo.TryGetValue(b.RunRoot, out var i) && i.TargetPath == target.Data) is { } batch)
        {
            var (p, f, _, c) = Categories(batch);
            last = $"{runInfo[batch.RunRoot].Began.ToLocalTime():HH:mm}  {p} passed, {f} failed" + (c > 0 ? $", {c} cancelled" : "");
        }
        summary = new(
            "Target    " + (exe.Length == 0 ? "none - set it in Project Properties (F4)"
                : Path.GetFileName(exe) + (File.Exists(exe) ? (readiness.Length > 0 ? " | " + readiness + " |" : "") + "   in " + Relative(Path.GetDirectoryName(exe) ?? "") : "   (missing - check Project Properties)")),
            "Output    " + Relative(output.Data) + moves,
            "Run setup " + RunSetup(SessionProject()),
            "Scenarios  " + (catalog.Length == 0 ? busy && activeRequest is null ? "loading..."
                    : files == 0 && (loadedOnce || target.Data.Length == 0) ? "none - Scenarios > Add files"
                    : loadedOnce ? $"none usable in {files} file{(files == 1 ? "" : "s")} (Alt+2)" : "not loaded (F2 loads them)"
                : $"{catalog.Length} loaded" + (files > 0 ? $" from {files} file{(files == 1 ? "" : "s")}" : "")),
            $"Marked     {scenarios.MarkedIds.Count} of {catalog.Length}",
            "Last run   " + last,
            state);
        // A target that is not ready is said once in the message area (the exact first blocking problem, never hidden or
        // reworded), with the routes to the details; loading and runs report their own progress there first.
        if (problem is null) warnedTarget = null;
        else if (!busy && warnedTarget != exe + "|" + problem)
        {
            warnedTarget = exe + "|" + problem;
            Error($"Target not ready: {problem}. Project > Information... (F3) shows the project state; Project > Target diagnostics... explains each cause.");
        }
        columns.SetText(scenarios.Header(scenarios.size.x));
        UpdateCaption();
        projectInfo?.Reload();
        OverviewChanged?.Invoke();
    }

    // ── Project Information ────────────────────────────────────────────────────────

    internal const ushort ProjectInfo = 0x7dc9;
    internal WorkbenchProjectInfoDialog? ProjectInfoDialog => projectInfo;
    // Read-only, with Close only: Properties (F4) and Target diagnostics stay on the Project menu and
    // keys. F3 and Project > Information... both arrive here; a second request while it is open opens nothing.
    private void ShowProjectInfo()
    {
        if (projectInfo is not null) return;
        UpdateHeader();
        using var dialog = new WorkbenchProjectInfoDialog(desktop, ProjectInfoRows);
        projectInfo = dialog;
        try { desktop.ExecView(dialog); }
        finally { projectInfo = null; }
    }
    internal (string Label, string Value)[] ProjectInfoRows()
    {
        string exe = target.Data;
        string[] files = projectDefaults.ScenarioFiles;
        string last = summary.LastRun["Last run   ".Length..];
        if (activeRequest is null && history.LastOrDefault(b => runInfo.TryGetValue(b.RunRoot, out var i) && i.TargetPath == target.Data) is { } batch)
            last = $"{runInfo[batch.RunRoot].Began.ToLocalTime():yyyy-MM-dd HH:mm}  {ResultText.RunVerdict(batch)} ({Counts(batch)})  {Path.GetFileName(batch.RunRoot)}";
        bool provisional = ProjectPath is null && ProjectLocation.IsDefaultRuns(output.Data, null, projectDefaults.Name);
        var rows = new List<(string, string)>
        {
            ("Project", projectDefaults.Name + (Dirty ? "  (unsaved changes, Ctrl+S saves)" : "")),
            ("Project file", ProjectPath ?? "not saved yet (Project > Save as... chooses the file)"),
            ("Target", exe.Length == 0 ? "none (Project > Properties, F4)" : exe + (File.Exists(exe) ? "" : "  (missing)")),
            ("Target state", summary.State),
            ("Scenarios", summary.Loaded["Scenarios  ".Length..]),
            ("Scenario files", files.Length == 0 ? "none attached (Scenarios > Add files...)" : $"{files.Length} attached"),
        };
        rows.AddRange(files.Select(f => ("", f + (File.Exists(f) ? "" : "  (missing)"))));
        rows.Add(("Marked", summary.Marked["Marked     ".Length..]));
        rows.Add(("Last run", last));
        rows.Add(("Run setup", summary.Setup["Run setup ".Length..]));
        rows.Add(("Runs folder", (output.Data.Length == 0 ? "none" : output.Data) + (provisional ? "  (moves with Save as)" : "")));
        return rows.ToArray();
    }
    private string Relative(string path)
    {
        if (ProjectPath is null || path.Length == 0) return path;
        try
        {
            string relative = Path.GetRelativePath(Path.GetDirectoryName(ProjectPath)!, path);
            return relative.Length < path.Length && !Path.IsPathRooted(relative) ? relative : path;
        }
        catch (ArgumentException) { return path; }
    }
    private void UpdateCaption()
    {
        if (!initialized) return;
        string hint = current == detailPane ? "Details: Up/Down scroll, Shift+Tab returns to Scenarios"
            : current == groups ? "Groups: Left/Right collapse or expand, Enter toggles a group"
            : current == search ? "Search filters by name or ID; marks are kept"
            : "Space or Enter marks, Ins marks and moves down";
        caption.SetText($"Marked {scenarios.MarkedIds.Count} of {catalog.Length}  |  Showing {scenarios.range}  |  Group: {FilterGroup ?? "All scenarios"}" + "  |  " + hint);
    }
    private void UpdateDetails()
    {
        string id = scenarios.Current?.Id ?? "";
        string key = id + ":" + detailPane.size.x + ":" + history.Count + ":" + targetDriver + ":" + projectDefaults.BaselineStore
            + ":" + catalog.Length + ":" + projectDefaults.ScenarioFiles.Length + ":" + loadedOnce + ":" + busy;
        if (detailKey == key) return;
        detailKey = key;
        var lines = new TStringCollection();
        if (scenarios.Current is { } scenario)
        {
            detailTitle.SetText($"{scenario.Name}  ({scenario.Id})");
            var last = LastExecution(id);
            string lastText = last is null ? "not run for this target in this session"
                : $"{last.Outcome} at {last.Size}" + (last.Outcome == ScenarioOutcomes.Passed ? "" : StartupFailure.Of(last) is { } startup
                    ? $" - {startup.Title}: {startup.Reason} (Results shows the technical details)" : " - " + last.Reason);
            IEnumerable<string> details = [scenario.Description, "ID         " + scenario.Id,
                $"Group {scenario.Group}  |  kind {scenario.Kind}  |  expected {scenario.ExpectedResult}  |  grids {scenario.Grid.Describe()}, defaults {string.Join(", ", scenario.Grid.Defaults)}",
                "Runs as    " + (scenario.External is null ? "compiled diagnostic scenario of the target" : scenario.Backend == "proxy-logical"
                    ? "external JSON scenario on an owned proxy target" : "external JSON scenario on a diagnostic fixture"),
                "Last result " + lastText,
                "Baselines  " + BaselineDescription(scenario),
                "Mode       " + ModeDescription() + (targetDriver != "NullDriver" ? (NativeExecution.Supports(scenario, targetDriver) ? "; supported by this native driver" : "; not runnable with this native driver") : ""),
                "", "Definition, steps and checkpoints",
                .. Cli.DescribeLines(scenario).Skip(4)];
            foreach (string line in details.SelectMany(d => WorkbenchText.Wrap(d, detailPane.size.x - 1))) lines.Insert(line);
        }
        else
        {
            detailTitle.SetText("");
            int files = projectDefaults.ScenarioFiles.Length;
            string empty = busy && activeRequest is null ? "Loading the scenarios of " + ApplicationName(target.Data) + "..."
                : files == 0 && (loadedOnce || target.Data.Length == 0) ? ScenarioFiles.NoneAttached
                : loadedOnce ? $"None of the {files} attached scenario file{(files == 1 ? "" : "s")} can be used. See Scenario Manager (Alt+2) for status and Messages for causes."
                : "No scenarios loaded. F2 loads the catalog of " + ApplicationName(target.Data) + ".";
            foreach (string line in WorkbenchText.Wrap(catalog.Length == 0 ? empty : "No focused scenario. Choose All scenarios or clear the search.", Math.Max(10, detailPane.size.x - 1)))
                lines.Insert(line);
        }
        detailPane.NewList(lines);
    }
    private string BaselineDescription(ScenarioDescriptor scenario)
    {
        if (projectDefaults.BaselineStore is null) return "no baseline store configured";
        try
        {
            var store = VisualBaselineSet.Load(projectDefaults.BaselineStore);
            var entries = store.EntriesForExecution(scenario.Id).ToArray();
            if (entries.Length == 0)
                return store.Manifest.Baselines.Select(e => store.UnmappedFixtureIdentity(e, scenario)).Any(x => x is not null)
                    ? "not gated: bound fixture " + scenario.External!.FixtureId + " has baselines, but no executionScenario mapping (never borrowed)"
                    : "no gated checkpoints for this scenario";
            if (store.BindingProblem(scenario.Id, catalog) is { } problem) return problem;
            string mapping = entries[0].ExecutionScenario is null ? "" : "baseline identity " + entries[0].Scenario + " (explicit mapping); ";
            var environment = BaselineEnvironmentFor(SessionProject(), [scenario]);
            return mapping + string.Join("; ", entries.SelectMany(e => e.Grids.Select(g => e.Checkpoint + " " + g + ": "
                + store.LoadApproved(e, g, environment).Status))) + " (" + environment + ")";
        }
        catch (Exception ex) { return "store unavailable or invalid: " + ex.Message; }
    }
    internal string[] NativeCapableScenarios() => OperatingSystem.IsWindows()
        ? WorkbenchCapabilities.NativeModes.SelectMany(mode => catalog.Where(s => NativeExecution.Supports(s, mode)).Select(s => mode + " supports " + s.Id)).ToArray()
        : [];

    // ── Project Properties ─────────────────────────────────────────────────────────

    private void ShowProperties(bool focusExecutable)
    {
        using var dialog = new WorkbenchPropertiesDialog(desktop, CaptureProject(), target.Data, output.Data, ProjectPath, scenarios.MarkedIds.Count,
            exe => DiagnoseProject(exe), (exe, verify) => ShowTargetDiagnostics(exe, null, verify),
            exe => PrepareTarget(exe, WorkbenchPreferences.SamePath(exe, target.Data)).UseExecutable, exe => PreparationOffer(exe), exe => ShowFixIssues(exe));
        if (focusExecutable) dialog.FocusExecutable();
        // Cancel changes nothing, but the deployment may have changed (or been verified) meanwhile: refresh the readiness.
        if (desktop.ExecView(dialog) != Views.cmOK) { UpdateHeader(); return; }
        var values = dialog.Accepted;
        if (Native is not null && values.BaselineStore is { } source &&
            !WorkbenchPreferences.SamePath(source, projectDefaults.BaselineStore))
        {
            if (busy) { Error("Wait for the current operation before adopting a baseline store."); return; }
            if (Confirm(desktop, "Adopt Baseline Store", "Copy this source into the project and enable visual comparison? The source is unchanged. Cancel leaves Project Properties unchanged.", "~A~dopt") != Views.cmYes) return;
            if (!AdoptBaselinePath(source)) return;
            values = values with { BaselineStore = projectDefaults.BaselineStore };
        }
        bool changed = !WorkbenchPreferences.SamePath(target.Data, values.Target) && !(target.Data.Length == 0 && values.Target.Length == 0);
        // A prepared copy of the same application (or back to its original) keeps the marks.
        string[]? keep = changed && target.Data.Length > 0 && values.Target.Length > 0 && SameApplication(target.Data, values.Target) ? scenarios.MarkedIds.ToArray() : null;
        int cleared = keep is null ? scenarios.MarkedIds.Count : 0;
        projectDefaults = projectDefaults with { Name = values.Name, BaselineStore = values.BaselineStore, Allow = values.Allow, TargetArguments = values.Arguments, TargetEnvironment = values.Environment };
        target.SetData(values.Target); output.SetData(values.OutputRoot);
        detailKey = "";
        if (changed) { InvalidateCatalog(); carriedMarks = keep; }
        SavePreferences();
        UpdateSelection();
        if (changed && values.Target.Length > 0)
        {
            Status(cleared > 0 ? $"Executable changed; {cleared} mark{(cleared == 1 ? " was" : "s were")} cleared. Loading scenarios..."
                : keep is not null ? "Executable changed to another deployment of the same application; marks are kept. Loading scenarios..." : "Executable changed. Loading scenarios...");
            // Loading for the previous executable is cancelled first; the new loading starts after its cleanup.
            if (busy) { afterRun = LoadCatalog; cancellation?.Cancel(); }
            else LoadCatalog();
        }
        else if (changed && busy) cancellation?.Cancel();
        else Status(ProjectPath is null ? "Project properties updated. Ctrl+S saves the project (no run is needed)." : "Project properties updated. Ctrl+S saves the project.");
    }
}
