using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Project Properties (, regrouped by the redesign): the only home of name, target, runs folder, baseline manifest
// and allowed capabilities, for a new and for a saved project alike. Process arguments and environment are editable.
// OK validates and keeps the dialog open with an inline "!" line on
// failure; Cancel changes nothing. Nothing is created or written while the dialog is open.
internal sealed class WorkbenchPropertiesDialog : TDialog
{
    internal const ushort BrowseExecutable = 0x7da0, BrowseRuns = 0x7da1, BrowseStore = 0x7da2, VerifyTarget = 0x7da3, TargetDetails = 0x7da4, PrepareTarget = 0x7d57, FixIssues = 0x7d37;
    internal const string ManifestPickerTitle = "Baseline manifest (.json)";
    internal sealed record Values(string Name, string Target, string OutputRoot, string? BaselineStore, string[] Allow)
    {
        internal string[] Arguments { get; init; } = [];
        internal Dictionary<string, string> Environment { get; init; } = [];
    }
    private static readonly string[] Capabilities = ["network", "processes", "devices"];
    private readonly TDeskTop desktop;
    private readonly string originalTarget;
    private readonly string? projectPath;
    private readonly int marked;
    private readonly string[] preservedAllow;
    private readonly TInputLine name, executable, runs, store, arguments, processEnvironment;
    private readonly TCheckBoxes allow;
    private readonly Line projectFile, runsNote, storeNote, notice, diagnosticsTop, diagnosticsBody;
    // Target diagnostics of the executable being edited, and the route to the full report (and Verify target).
    private readonly Func<string, TargetReport>? diagnose;
    private readonly Action<string, bool>? openDiagnostics;
    // Prepare target for the executable being edited; returns the prepared copy the user chose to use (or null).
    private readonly Func<string, string?>? prepare;
    private readonly Func<string, (OfferKind Kind, string Text)>? offer;
    private string preparedNotice = "";
    internal TButton PrepareButton { get; }
    // TSW PRODUCT-3: Fix Issues for the executable being edited (beside Verify target). Without the callback it opens the
    // Target Diagnostics, which offer Fix Issues themselves.
    private readonly Action<string>? fixIssues;
    internal TButton FixButton { get; }
    // The automatic Runs folder for the current name; the field follows it while the user has not customized it.
    private string automaticRuns;
    private string lastExecutable;
    private string error = "";
    internal Values Accepted { get; private set; }
    internal TGroupBox ProjectBox { get; }
    internal TGroupBox TargetBox { get; }
    internal TGroupBox DiagnosticsBox { get; }
    internal TGroupBox RunsBox { get; }
    internal TGroupBox CapabilitiesBox { get; }
    internal TInputLine NameField => name;
    internal TInputLine ExecutableField => executable;
    internal TInputLine RunsField => runs;
    internal TInputLine StoreField => store;
    internal TInputLine ArgumentsField => arguments;
    internal TInputLine ProcessEnvironmentField => processEnvironment;
    internal TCheckBoxes AllowField => allow;
    internal string NoticeText => notice.Current;
    internal string RunsNote => runsNote.Current;
    internal string StoreNote => storeNote.Current;
    internal string ProjectFileText => projectFile.Current;
    internal string DiagnosticsText => diagnosticsTop.Current + "\n" + diagnosticsBody.Current;

    // One row per text line; each row ends with an ellipsis instead of wrapping.
    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = string.Join("\n", Text.Split('\n').Select(l => WorkbenchText.Fit(l, size.x)));
    }

    // 110x33 at every supported Workbench grid (120x36 leaves a 118x34 desktop), limited to the desktop. added the
    // Target diagnostics group; the groups now follow each other without a blank row so the dialog stays within 34 rows.
    internal static TRect Bounds(TDeskTop desktop) => new(0, 0, Math.Min(110, desktop.size.x - 2), Math.Min(33, desktop.size.y));

    internal WorkbenchPropertiesDialog(TDeskTop desktop, WorkbenchProject project, string target, string output, string? projectPath, int markedCount,
        Func<string, TargetReport>? diagnose = null, Action<string, bool>? openDiagnostics = null,
        Func<string, string?>? prepare = null, Func<string, (OfferKind Kind, string Text)>? offer = null, Action<string>? fixIssues = null)
        : base(Bounds(desktop), "Project Properties")
    {
        this.desktop = desktop; this.projectPath = projectPath; originalTarget = target; marked = markedCount;
        this.diagnose = diagnose; this.openDiagnostics = openDiagnostics; this.prepare = prepare; this.offer = offer; this.fixIssues = fixIssues;
        options |= Views.ofCentered; helpCtx = 0x7dc4;
        preservedAllow = project.Allow.Where(a => !Capabilities.Contains(a)).ToArray();
        Accepted = new(project.Name, target, output, project.BaselineStore, project.Allow);
        automaticRuns = ProjectLocation.DefaultRuns(projectPath, project.Name); lastExecutable = target;
        // Group boxes span the dialog; inside each: labels from x=2, fields from x=field, Browse buttons at the right.
        int w = size.x, h = size.y, gw = w - 4, field = 20, browse = gw - 16, edit = browse - 1, text = gw - 2;
        TStaticText Info(int x, int y, int right, string value) => new(new(x, y, right, y + 1), value);

        // Insertion order is the Tab order: project, target, runs and baselines, capabilities, OK, Cancel.
        // Mnemonics are unique in this dialog (n, e, b, a, f, t, d, r, s, m, o, w, p, v).
        ProjectBox = new(new(2, 2, w - 2, 6), "Project");
        name = new(new(field, 1, Math.Min(edit, field + 60), 2), 128); Put(name, project.Name); ProjectBox.Insert(name);
        ProjectBox.Insert(new TLabel(new(1, 1, field, 2), "Project ~n~ame", name));
        ProjectBox.Insert(Info(2, 2, field, "Project file"));
        projectFile = new Line(new(field + 1, 2, text, 3)); ProjectBox.Insert(projectFile);

        Insert(ProjectBox);

        TargetBox = new(new(2, 6, w - 2, 13), "Target application");
        executable = new(new(field, 1, edit, 2), 32768); Put(executable, target); TargetBox.Insert(executable);
        TargetBox.Insert(new TLabel(new(1, 1, field, 2), "~E~xecutable", executable));
        TargetBox.Insert(new TButton(new(browse, 1, gw - 2, 3), "~B~rowse...", BrowseExecutable, ButtonConstants.bfNormal));
        arguments = new(new(field, 2, edit, 3), 16384); Put(arguments, System.Text.Json.JsonSerializer.Serialize(project.TargetArguments)); TargetBox.Insert(arguments);
        TargetBox.Insert(new TLabel(new(1, 2, field, 3), "Ar~g~uments JSON", arguments));
        // Prepare target sits under Browse: it prepares the executable in the field (contextual availability).
        PrepareButton = new TButton(new(browse, 3, gw - 2, 5), "Prep~a~re...", PrepareTarget, ButtonConstants.bfNormal); TargetBox.Insert(PrepareButton);
        processEnvironment = new(new(field, 3, edit, 4), 16384); Put(processEnvironment, System.Text.Json.JsonSerializer.Serialize(project.TargetEnvironment)); TargetBox.Insert(processEnvironment);
        TargetBox.Insert(new TLabel(new(1, 3, field, 4), "Process en~v~ JSON", processEnvironment));
        TargetBox.Insert(Info(2, 4, field, "Scenario files")); TargetBox.Insert(Info(field + 1, 4, edit, Fit(ScenarioFiles(project), edit - field - 1)));
        TargetBox.Insert(Info(field + 1, 5, text, "Arguments: [" + "\"arg\"" + "]. Process environment: {" + "\"NAME\":\"value\"" + "}."));
        Insert(TargetBox);

        // Compact readiness of the edited executable (static inspection; nothing is started). Details opens the full report,
        // Target Diagnostics, which is the one home of Verify target (no second Verify button here).
        DiagnosticsBox = new(new(2, 13, w - 2, 18), "Target diagnostics");
        diagnosticsTop = new Line(new(2, 1, gw - 2, 2)); DiagnosticsBox.Insert(diagnosticsTop);
        diagnosticsBody = new Line(new(2, 2, gw - 36, 4)); DiagnosticsBox.Insert(diagnosticsBody);
        // TSW PRODUCT-3: Fix Issues first (the remedy next to the problem), enabled only while there is something it addresses.
        FixButton = new TButton(new(gw - 35, 2, gw - 17, 4), "~F~ix issues...", FixIssues, ButtonConstants.bfNormal); DiagnosticsBox.Insert(FixButton);
        DiagnosticsBox.Insert(new TButton(new(gw - 16, 2, gw - 2, 4), "~D~etails...", TargetDetails, ButtonConstants.bfNormal));
        Insert(DiagnosticsBox);

        RunsBox = new(new(2, 18, w - 2, 24), "Runs and baselines");
        runs = new(new(field, 1, edit, 2), 32768); Put(runs, output.Trim().Length == 0 ? automaticRuns : output); RunsBox.Insert(runs);
        RunsBox.Insert(new TLabel(new(1, 1, field, 2), "~R~uns folder", runs));
        RunsBox.Insert(new TButton(new(browse, 1, gw - 2, 3), "Brow~s~e...", BrowseRuns, ButtonConstants.bfNormal));
        runsNote = new Line(new(field + 1, 2, edit, 3)); RunsBox.Insert(runsNote);
        store = new(new(field, 3, edit, 4), 32768); Put(store, project.BaselineStore ?? ""); RunsBox.Insert(store);
        RunsBox.Insert(new TLabel(new(1, 3, field, 4), "Baseline ~m~anifest", store));
        RunsBox.Insert(new TButton(new(browse, 3, gw - 2, 5), "Br~o~wse...", BrowseStore, ButtonConstants.bfNormal));
        storeNote = new Line(new(field + 1, 4, edit, 5)); RunsBox.Insert(storeNote);
        Insert(RunsBox);

        CapabilitiesBox = new(new(2, 24, w - 2, 28), "Capabilities the target may use");
        allow = new(new(2, 1, 56, 2), Items("Net~w~ork", "Child ~p~rocesses", "Dev~i~ces"));
        allow.value = (project.Allow.Contains("network") ? 1u : 0u) | (project.Allow.Contains("processes") ? 2u : 0u) | (project.Allow.Contains("devices") ? 4u : 0u);
        CapabilitiesBox.Insert(allow);
        CapabilitiesBox.Insert(Info(58, 1, text, "All off by default."));
        CapabilitiesBox.Insert(Info(2, 2, text, "Elevation (administrator rights) is never allowed: the target always runs unelevated."));
        Insert(CapabilitiesBox);

        // Owner spacing rule: the notice line, one empty row, then the buttons above the bottom frame.
        notice = new Line(new(2, h - 5, w - 2, h - 4)); Insert(notice);
        Insert(new TButton(new(w - 32, h - 3, w - 18, h - 1), "O~K~", Views.cmOK, ButtonConstants.bfDefault));
        Insert(new TButton(new(w - 17, h - 3, w - 3, h - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfNormal));
        Enter(name);
        Refresh();
    }

    internal void FocusExecutable() => Enter(executable);
    // Focus with the whole value selected, so typing replaces it. A field alone in its group box keeps sfSelected while
    // focus is in another group, so the framework's select-on-entry does not run when keyboard focus comes back to it.
    private static void Enter(TInputLine field) { field.Focus(); field.SelectAll(true); field.DrawView(); }
    private TInputLine? FocusedField => new[] { name, executable, arguments, processEnvironment, runs, store }.FirstOrDefault(f => f.GetState(Views.sfFocused));

    // A value set by the dialog, not typed: no selection highlight on a field the user is not in.
    private static void Put(TInputLine field, string value) { field.SetData(value); if (!field.GetState(Views.sfFocused)) field.SelStart = field.SelEnd = 0; field.DrawView(); }
    private static string Fit(string value, int width) => WorkbenchText.Fit(value, Math.Max(1, width));
    private static TSItem Items(params string[] values)
    { TSItem? item = null; foreach (string value in values.Reverse()) item = new(value, item!); return item!; }
    private static string Arguments(WorkbenchProject p) => p.TargetArguments.Length == 0 ? "none" : $"{p.TargetArguments.Length}: {string.Join(' ', p.TargetArguments)}";
    private static string EnvironmentText(WorkbenchProject p) => p.TargetEnvironment.Count == 0 ? "none" : $"{p.TargetEnvironment.Count}: {string.Join(", ", p.TargetEnvironment.Keys)}";
    private static string ScenarioFiles(WorkbenchProject p) => p.ScenarioFiles.Length switch
    { 0 => "none attached - Scenario Manager > Add files... or Add folder...", 1 => "1 file - Tests > Scenario Manager (Alt+2)", int n => $"{n} files - Tests > Scenario Manager (Alt+2)" };

    private string ProjectName => name.Data.Trim().Length == 0 ? ProjectLocation.DefaultName : name.Data.Trim();
    private string BaseFolder => ProjectLocation.Folder(projectPath, ProjectName);
    private bool TargetChanged => !WorkbenchPreferences.SamePath(executable.Data.Trim(), originalTarget)
        && !(executable.Data.Trim().Length == 0 && originalTarget.Length == 0);

    // Follows the name into the automatic Runs folder and refreshes the read-only lines; called after every event.
    private void Refresh()
    {
        if (notice is null || executable is null) return; // broadcasts arrive while the constructor inserts controls
        if (executable.Data != lastExecutable) { lastExecutable = executable.Data; error = ""; NameFromExecutable(); }
        string next = ProjectLocation.DefaultRuns(projectPath, ProjectName);
        if (next != automaticRuns)
        {
            if (WorkbenchPreferences.SamePath(runs.Data.Trim(), automaticRuns)) Put(runs, next);
            automaticRuns = next;
        }
        const string unsaved = "not saved yet; Save as suggests ";
        projectFile.Set(projectPath is not null ? WorkbenchText.Path(projectPath, projectFile.size.x)
            : unsaved + WorkbenchText.Path(ProjectLocation.SuggestedFile(ProjectName), projectFile.size.x - unsaved.Length));
        string folder = runs.Data.Trim();
        runsNote.Set(folder.Length == 0 ? "Empty: OK uses the automatic folder next to the project file"
            : WorkbenchPreferences.SamePath(folder, automaticRuns)
                ? (projectPath is null ? "Automatic: next to the project file, moves with Save as" : "Automatic: next to the project file; the first run creates it")
                : "Custom folder: kept when the executable or location changes");
        storeNote.Set(StoreState());
        UpdateDiagnostics();
        if (error.Length > 0) { notice.Set("! " + error); return; }
        if (preparedNotice.Length > 0 && TargetChanged) { notice.Set(preparedNotice); return; }
        notice.Set(TargetChanged && marked > 0
            ? $"! Changing the executable reloads the scenarios and clears the {marked} marked scenario{(marked == 1 ? "" : "s")}."
            : TargetChanged && originalTarget.Length == 0 ? "Scenarios load after OK. Saving the project does not need them."
            : TargetChanged ? "Changing the executable reloads the scenarios." : "");
    }

    // The executable as typed, resolved like OK resolves it (relative to the project folder); "" when empty or invalid.
    private string EditedExecutable()
    {
        string exe = executable.Data.Trim();
        if (exe.Length == 0) return "";
        try { return Path.GetFullPath(exe, BaseFolder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return exe; }
    }
    // Recomputed on every refresh: the inspection is cached per file state, so typing or browsing a new executable, or a
    // deployment change on disk, shows at once. Nothing is started here.
    private void UpdateDiagnostics()
    {
        if (diagnosticsTop is null || diagnosticsBody is null) return;
        if (diagnose is null) { diagnosticsTop.Set("Target diagnostics: Settings > Target Diagnostics... in the Workspace."); diagnosticsBody.Set(""); return; }
        var report = diagnose(EditedExecutable());
        var (top, body) = TargetDiagnostics.Compact(report, diagnosticsTop.size.x, diagnosticsBody.size.x);
        diagnosticsTop.Set(top); diagnosticsBody.Set(body);
        bool fix = (fixIssues is not null || openDiagnostics is not null) && TargetRepairService.Relevant(report);
        if (fix) EnableCommand(FixIssues); else DisableCommand(FixIssues);
        if (FixButton is not null && FixButton.GetState(Views.sfDisabled) == fix) { FixButton.SetState(Views.sfDisabled, !fix); FixButton.DrawView(); }
        bool available = prepare is not null && offer is not null && EditedExecutable() is { Length: > 0 } exe && File.Exists(exe) && offer(exe).Kind != OfferKind.None;
        if (available) EnableCommand(PrepareTarget); else DisableCommand(PrepareTarget);
        if (PrepareButton is not null && PrepareButton.GetState(Views.sfDisabled) == available) { PrepareButton.SetState(Views.sfDisabled, !available); PrepareButton.DrawView(); }
    }

    private string StoreState()
    {
        string value = store.Data.Trim();
        if (value.Length == 0) return "Optional: needed only for Visual regression.";
        try
        {
            string full = Path.GetFullPath(value, BaseFolder);
            if (Directory.Exists(full)) return "This is a folder: enter a manifest file name ending in .json.";
            if (!IsJson(full)) return "A baseline manifest is a .json file.";
            return File.Exists(full) ? "External source; not adopted here. Baseline Explorer adopts and enables comparison."
                : "New manifest: nothing is created now; Visual regression needs it.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "The path is not valid."; }
    }
    private static bool IsJson(string path) => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    public override void ShutDown() { EnableCommand(PrepareTarget); EnableCommand(FixIssues); base.ShutDown(); }

    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return base.Valid(command);
        try { Accepted = Read(); error = ""; Refresh(); return base.Valid(command); }
        catch (ArgumentException ex) { error = ex.Message; Refresh(); return false; }
    }

    private Values Read()
    {
        string projectName = name.Data.Trim();
        if (projectName.Length == 0) { name.Focus(); throw new ArgumentException("Project name is required."); }
        string exe = executable.Data.Trim();
        if (exe.Length > 0)
        {
            string full = Full(exe, executable, "Executable");
            if (!File.Exists(full)) { executable.Focus(); throw new ArgumentException("Executable: no file exists at " + full + "."); }
            exe = full;
        }
        // An empty Runs folder takes the automatic one; no folder is created here (the first run creates it).
        string folder = runs.Data.Trim().Length == 0 ? automaticRuns : Full(runs.Data.Trim(), runs, "Runs folder");
        if (File.Exists(folder)) { runs.Focus(); throw new ArgumentException("Runs folder: " + folder + " is a file, not a folder."); }
        string? baselines = store.Data.Trim().Length == 0 ? null : Full(store.Data.Trim(), store, "Baseline manifest");
        if (baselines is not null)
        {
            if (Directory.Exists(baselines)) { store.Focus(); throw new ArgumentException("Baseline manifest: " + baselines + " is a folder; enter a file name ending in .json."); }
            if (!IsJson(baselines)) { store.Focus(); throw new ArgumentException("Baseline manifest: the file name must end in .json."); }
            // A new manifest name is kept as configured (Visual regression reports it until the file exists); an existing
            // file must be a readable manifest. The dialog never creates or overwrites a manifest.
            if (File.Exists(baselines))
            {
                try { VisualBaselineSet.Load(baselines); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
                { store.Focus(); throw new ArgumentException("Baseline manifest: " + baselines + " is not a readable baseline manifest (" + ex.Message + ")."); }
            }
        }
        string[] selected = Capabilities.Where((_, i) => (allow.value & (1u << i)) != 0).ToArray();
        try
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<string[]>(arguments.Data) ?? throw new ArgumentException("Arguments must be a JSON array.");
            var env = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(processEnvironment.Data) ?? throw new ArgumentException("Process environment must be a JSON object.");
            if (args.Any(a => a is null || a.Contains('\0')) || env.Any(e => string.IsNullOrWhiteSpace(e.Key) || e.Key.Contains('=') || e.Key.Contains('\0') || e.Value is null || e.Value.Contains('\0')))
                throw new ArgumentException("Arguments and environment must contain valid strings; environment names cannot contain '='.");
            return new(projectName, exe, folder, baselines, [.. selected, .. preservedAllow]) { Arguments = args, Environment = env };
        }
        catch (System.Text.Json.JsonException) { throw new ArgumentException("Use a JSON string array for arguments and a JSON string object for process environment."); }
    }
    private string Full(string value, TInputLine field, string label)
    {
        try { return Path.GetFullPath(value, BaseFolder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { field.Focus(); throw new ArgumentException(label + ": the path is not valid."); }
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && ev.message.command == PrepareTarget)
        {
            ClearEvent(ref ev);
            if (prepare is null) { error = "Prepare target is available for an open project."; Refresh(); return; }
            if (prepare(EditedExecutable()) is { } prepared)
            {
                Put(executable, prepared); lastExecutable = executable.Data; error = "";
                preparedNotice = "Prepared copy selected. OK applies it (scenario files and marks are kept); Cancel keeps the original.";
            }
            Refresh();
            return;
        }
        if (ev.What == Events.evCommand && ev.message.command == FixIssues)
        {
            ClearEvent(ref ev);
            if (fixIssues is not null) fixIssues(EditedExecutable());
            else if (openDiagnostics is not null) openDiagnostics(EditedExecutable(), false);
            else error = "Fix issues is available for an open project.";
            Refresh();
            return;
        }
        if (ev.What == Events.evCommand && ev.message.command is VerifyTarget or TargetDetails)
        {
            bool verify = ev.message.command == VerifyTarget; ClearEvent(ref ev);
            if (openDiagnostics is null) { error = "Target diagnostics are available for an open project."; Refresh(); return; }
            openDiagnostics(EditedExecutable(), verify);
            Refresh();
            return;
        }
        if (ev.What == Events.evCommand && ev.message.command is BrowseExecutable or BrowseRuns or BrowseStore)
        {
            ushort command = ev.message.command; ClearEvent(ref ev);
            error = "";
            if (command == BrowseExecutable) PickExecutable();
            else if (command == BrowseStore) PickManifest();
            else
            {
                using var picker = new WorkbenchDirectoryDialog(WorkbenchDialog.InitialDirectory(Start(runs)));
                if (desktop.ExecView(picker) == Views.cmOK) { runs.SetData(picker.SelectedPath); runs.DrawView(); }
                runs.Focus();
            }
            Refresh();
            return;
        }
        if (notice is null || executable is null) { base.HandleEvent(ref ev); return; }
        bool keyboard = ev.What == Events.evKeyDown;
        var before = FocusedField;
        base.HandleEvent(ref ev);
        if (keyboard && FocusedField is { } entered && entered != before) { entered.SelectAll(true); entered.DrawView(); }
        Refresh();
    }

    // An untouched default name follows the chosen executable (MyApp.exe gives "MyApp"); a typed name is never replaced.
    private void NameFromExecutable()
    {
        if (name.Data.Trim() != ProjectLocation.DefaultName) return;
        string exe = executable.Data.Trim();
        if (exe.Length == 0 || !File.Exists(exe)) return;
        Put(name, Path.GetFileNameWithoutExtension(exe));
    }

    // Framework file picker started from the field's folder with a wildcard, so it lists and navigates (rule).
    private void PickExecutable()
    {
        using var picker = Picker(OperatingSystem.IsWindows() ? "*.exe" : "*", "Select target executable", "Executable ~n~ame", FileDialogOptions.fdOpenButton, executable);
        if (desktop.ExecView(picker) is Views.cmFileOpen or Views.cmOK)
        {
            picker.GetFileName(out string selected);
            if (File.Exists(selected)) { executable.SetData(Path.GetFullPath(selected)); executable.DrawView(); }
            else error = "Select an existing file; " + Path.GetFileName(selected) + " was not found.";
        }
        executable.Focus();
    }

    // The manifest picker selects an existing .json or accepts a new file name, in an existing or a new folder. A name
    // without an extension gets .json. Nothing is created or overwritten; Cancel keeps the previous value.
    private void PickManifest()
    {
        using var picker = Picker("*.json", ManifestPickerTitle, "~M~anifest file", FileDialogOptions.fdOKButton, store);
        if (desktop.ExecView(picker) is Views.cmFileOpen or Views.cmOK)
        {
            picker.GetFileName(out string selected);
            if (Path.GetExtension(selected).Length == 0) selected += ".json";
            if (!IsJson(selected)) error = "Baseline manifest: the file name must end in .json.";
            else { store.SetData(Path.GetFullPath(selected)); store.DrawView(); }
        }
        store.Focus();
    }

    private TFileDialog Picker(string pattern, string title, string label, ushort button, TInputLine field)
    {
        var picker = new TFileDialog(pattern, title, label, (ushort)(button | FileDialogOptions.fdNoLoadDir), 0);
        picker.fileName!.MaxLen = 32767; picker.helpCtx = 0x7dc4; WorkbenchDialog.Widen(picker, desktop);
        picker.SetData(Path.Combine(WorkbenchDialog.InitialDirectory(Start(field)), pattern));
        return picker;
    }
    // Where a picker starts: the field's path (relative to the project folder), else the project folder.
    private string Start(TInputLine field)
    {
        try { return field.Data.Trim().Length == 0 ? BaseFolder : Path.GetFullPath(field.Data.Trim(), BaseFolder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return BaseFolder; }
    }
}
