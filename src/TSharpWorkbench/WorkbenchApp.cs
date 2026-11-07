using TSharpWorkbench.Storage;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

internal static class WorkbenchEntry
{
    internal static int Run(string[] args)
    {
        try
        {
            var options = Cli.Parse(args);
            var preferences = WorkbenchPreferences.Resolve(options);
            string? projectPath = options.GetValueOrDefault("--project");
            WorkbenchProject? project = null;
            // Plain `tui` shows the Start window; `tui --target` keeps creating an untitled project from the arguments.
            bool start = true;
            return TSharpVisionRuntime.Run(() => new WorkbenchApp(preferences.Target, preferences.Output, preferences, project, projectPath, start));
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
    }
}

internal sealed class WorkbenchApp : TApplication
{
    internal const ushort Environments = 0x7bb3, CatalogWindow = 0x7bb4;
    internal WorkbenchOverview? Overview { get; private set; }
    private const ushort Open = 0x7de0, OpenStartPage = 0x7d9b;
    // Initialized before TProgram's constructor calls InitStatusLine.
    private readonly WorkbenchNotifications notifications = new();
    private readonly string target, output;
    private WorkbenchDialog? main;
    private WorkbenchStartWindow? startWindow;
    private WorkbenchMessagesWindow? messagesWindow;
    internal WorkbenchHelpWindow? HelpWindow { get; private set; }
    private bool showingHelp;
    internal string HelpPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Help", "TSharpWorkbench.hlp");
    private readonly WorkbenchPreferences preferences;
    private readonly WorkbenchProject? startupProject;
    private readonly string? startupProjectPath;
    private readonly bool showStart;
    internal WorkbenchApp(string target, string output, WorkbenchPreferences? preferences = null, WorkbenchProject? startupProject = null,
        string? startupProjectPath = null, bool showStart = false)
    {
        this.target = target; this.output = output; this.preferences = preferences ?? new(target, output, null, "80x25");
        this.startupProject = startupProject; this.startupProjectPath = startupProjectPath; this.showStart = showStart;
        notifications.Changed = OnNotification;
        // Prepared targets live where the user settings say (PreparedTargetsRoot), else in the per-user data folder.
        PreparationStore.Configured = WorkbenchPreferences.Load(this.preferences.StoragePath).PreparedTargetsRoot;
        TargetDiagnostics.Preparation = new TargetPreparationService();
        WorkbenchRecorder.Install(this);
        Post(Open);
    }
    internal const ushort NewProject = 0x7d80, OpenProject = 0x7d81, SaveProject = 0x7d82, SaveAsProject = 0x7d83,
        CloseProject = 0x7d84, ProjectWindow = 0x7d85, Shortcuts = 0x7d86, About = 0x7d87, MessagesWindow = 0x7d89,
        ComparisonWindow = 0x7d8a, TestModes = WorkbenchDialog.Capabilities, LaunchApplication = 0x7d8c, StopApplication = 0x7d8d;
    internal const ushort NoProjectContext = WorkbenchStartWindow.Context, EmptyDesktopContext = 0x7d8b, DialogContext = 0x7dc4;
    internal WorkbenchNotifications Notifications => notifications;
    internal WorkbenchStartWindow? Start => startWindow;
    internal WorkbenchMessagesWindow? Messages => messagesWindow;
    internal string? PreferencesPath => preferences.StoragePath;

    public override TMenuBar InitMenuBar(TRect r)
    {
        r.b.y = r.a.y + 1;
        return new WorkbenchMenuBar(r,
            new TSubMenu("~P~roject", Keys.kbAltP) +
            new TMenuItem("~N~ew...", NewProject, Keys.kbCtrlN, 0, "Ctrl+N") +
            new TMenuItem("~O~pen...", OpenProject, Keys.kbCtrlO, 0, "Ctrl+O") +
            new TMenuItem("~S~ave", SaveProject, Keys.kbCtrlS, 0, "Ctrl+S") +
            new TMenuItem("Save ~A~s...", SaveAsProject, Keys.kbNoKey) +
            new TMenuItem("~C~lose", CloseProject, Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("~P~roperties...", WorkbenchDialog.ProjectInfo, Keys.kbNoKey, 0, "F3") +
            TMenuItem.NewLine() +
            new TMenuItem("E~x~it", Views.cmQuit, Keys.kbAltX, 0, "Alt+X") +
            new TSubMenu("~T~ests", Keys.kbAltT) +
            new TMenuItem("Test ~E~xplorer...", WorkbenchTestExplorer.Command, Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("Scenario ~M~anager...", WorkbenchDialog.ManageScenarios, Keys.kbNoKey) +
            new TSubMenu("~R~uns", Keys.kbAltR) +
            new TMenuItem("Run E~x~plorer...", WorkbenchDialog.ProgressWindow, Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("~N~ew Run...", WorkbenchDialog.Run, Keys.kbF5, 0, "F5") +
            new TMenuItem("Cancel R~u~n", WorkbenchDialog.CancelRun, Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("~L~aunch Application", LaunchApplication, Keys.kbCtrlF9, 0, "Ctrl+F9") +
            new TMenuItem("S~t~op Application", StopApplication, Keys.kbCtrlF2, 0, "Ctrl+F2") +
            TMenuItem.NewLine() +
            new TMenuItem("~R~esults...", WorkbenchDialog.Results, Keys.kbF6, 0, "F6") +
            new TMenuItem("~H~istory...", WorkbenchDialog.RunHistory, Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("Open Output ~F~older", WorkbenchDialog.OpenOutput, Keys.kbNoKey) +
            new TSubMenu("~B~aselines", Keys.kbAltB) +
            new TMenuItem("Baseline E~x~plorer...", WorkbenchDialog.BaselineBrowse, Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("~R~eview Candidates...", WorkbenchDialog.ReviewCandidates, Keys.kbNoKey) +
            new TMenuItem("~C~ompare...", WorkbenchDialog.CompareResult, Keys.kbNoKey) +
            new TSubMenu("Settin~g~s", Keys.kbAltG) +
            new TMenuItem("~P~roject...", WorkbenchDialog.Advanced, Keys.kbNoKey, 0, "F4") +
            new TMenuItem("~E~nvironments...", Environments, Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("Target ~D~iagnostics...", WorkbenchDialog.DiagnoseTarget, Keys.kbNoKey) +
            new TMenuItem("Prepare Target ~C~opy...", WorkbenchDialog.PrepareTargetCommand, Keys.kbNoKey) +
            new TMenuItem("~F~ix Original Target...", WorkbenchDialog.FixIssuesCommand, Keys.kbNoKey) +
            new TSubMenu("~W~indow", Keys.kbAltW) +
            new TMenuItem("~T~est Explorer", WorkbenchTestExplorer.Command, Keys.kbNoKey) +
            new TMenuItem("Scen~a~rio Manager", WorkbenchDialog.ManageScenarios, Keys.kbNoKey, 0, "Alt+2") +
            new TMenuItem("~R~un Explorer", WorkbenchDialog.ProgressWindow, Keys.kbNoKey, 0, "Alt+3") +
            new TMenuItem("Re~s~ults", WorkbenchDialog.Results, Keys.kbNoKey, 0, "Alt+4") +
            new TMenuItem("Baseline E~x~plorer", ComparisonWindow, Keys.kbNoKey, 0, "Alt+5") +
            new TMenuItem("~M~essages", MessagesWindow, Keys.kbNoKey, 0, "Alt+6") +
            TMenuItem.NewLine() +
            new TMenuItem("~N~ext Window", Views.cmNext, Keys.kbNoKey) +
            new TMenuItem("Pre~v~ious Window", Views.cmPrev, Keys.kbNoKey) +
            new TMenuItem("S~i~ze/Move", Views.cmResize, Keys.kbCtrlF5, 0, "Ctrl+F5") +
            new TMenuItem("~Z~oom", Views.cmZoom, Keys.kbNoKey) +
            new TMenuItem("~C~lose Window", Views.cmClose, Keys.kbAltF3, 0, "Alt+F3") +
            new TSubMenu("~H~elp", Keys.kbAltH) +
            new TMenuItem("~I~ndex", WorkbenchHelpContext.Command(HelpTopic.Index), Keys.kbNoKey) +
            new TMenuItem("~S~tart Page", WorkbenchHelpContext.Command(HelpTopic.Start), Keys.kbNoKey) +
            new TMenuItem("Project over~v~iew", WorkbenchHelpContext.Command(HelpTopic.Workspace), Keys.kbNoKey) +
            new TMenuItem("~P~roject information / properties", WorkbenchHelpContext.Command(HelpTopic.Properties), Keys.kbNoKey) +
            new TMenuItem("~T~arget diagnostics / preparation", WorkbenchHelpContext.Command(HelpTopic.Diagnostics), Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("S~c~enarios", WorkbenchHelpContext.Command(HelpTopic.Scenarios), Keys.kbNoKey) +
            new TMenuItem("Scenario ~E~ditor", WorkbenchHelpContext.Command(HelpTopic.Editor), Keys.kbNoKey) +
            new TMenuItem("~R~ecorder", WorkbenchHelpContext.Command(HelpTopic.Recorder), Keys.kbNoKey) +
            new TMenuItem("Run c~o~nfiguration", WorkbenchHelpContext.Command(HelpTopic.Run), Keys.kbNoKey) +
            new TMenuItem("Run ~M~onitor", WorkbenchHelpContext.Command(HelpTopic.Monitor), Keys.kbNoKey) +
            new TMenuItem("Res~u~lts", WorkbenchHelpContext.Command(HelpTopic.Results), Keys.kbNoKey) +
            new TMenuItem("~B~aselines / comparison", WorkbenchHelpContext.Command(HelpTopic.Baselines), Keys.kbNoKey) +
            new TMenuItem("Messa~g~es", WorkbenchHelpContext.Command(HelpTopic.Messages), Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("Pre~f~erences", WorkbenchHelpContext.Command(HelpTopic.Preferences), Keys.kbNoKey) +
            new TMenuItem("~K~eyboard shortcuts", WorkbenchHelpContext.Command(HelpTopic.Keyboard), Keys.kbNoKey) +
            new TMenuItem("Black-Box / White-Box mo~d~es", TestModes, Keys.kbNoKey) +
            new TMenuItem("Troubles~h~ooting", WorkbenchHelpContext.Command(HelpTopic.Troubleshooting), Keys.kbNoKey) +
            TMenuItem.NewLine() +
            new TMenuItem("~A~bout TSharpWorkbench", About, Keys.kbNoKey));
    }
    public override TStatusLine InitStatusLine(TRect r) => CreateStatusLine(r, notifications);
    internal static TStatusLine CreateStatusLine(TRect r, WorkbenchNotifications? notifications = null)
    {
        r.a.y = r.b.y - 1;
        TStatusItem Exit() => new(null, Keys.kbAltX, Views.cmQuit);
        // F-keys ascending, then the other keys, then the context keys. F4 is the Workspace's own key
        // (no other context binds it); "Ins Mark next" is the first label hidden while a notice needs the room.
        var workspace = new TStatusDef(WorkbenchDialog.Context, WorkbenchDialog.Context) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F2~ Refresh", Keys.kbF2, WorkbenchDialog.Load) +
            new TStatusItem("~F3~ Project", Keys.kbF3, WorkbenchDialog.ProjectInfo) +
            new TStatusItem("~F4~ Properties", Keys.kbF4, WorkbenchDialog.Advanced) +
            new TStatusItem("~F5~ Run", Keys.kbF5, WorkbenchDialog.Run) +
            new TStatusItem("~F6~ Results", Keys.kbF6, WorkbenchDialog.Results) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) +
            new TStatusItem("~Space~ Mark", Keys.kbNoKey, WorkbenchDialog.ToggleMark) +
            new TStatusItem("~Ins~ Mark next", Keys.kbNoKey, 0) + Exit();
        // TStatusDef + TStatusDef merges items; link context definitions explicitly.
        var results = new TStatusDef(WorkbenchResultsDialog.Context, WorkbenchResultsDialog.Context) +
            new TStatusItem("~Enter~ Inspect", Keys.kbNoKey, WorkbenchResultsDialog.ResultDetails) +
            new TStatusItem("~Tab~ Next pane", Keys.kbNoKey, 0) +
            new TStatusItem("~Esc~ Close", Keys.kbEsc, Views.cmClose) +
            new TStatusItem("~F5~ Run", Keys.kbF5, WorkbenchDialog.Run) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) + Exit();
        var dialogs = new TStatusDef(DialogContext, DialogContext) +
            new TStatusItem("~Esc~ Cancel", Keys.kbEsc, Views.cmCancel) +
            new TStatusItem("~Tab~ Next field", Keys.kbNoKey, 0) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts);
        var messages = new TStatusDef(WorkbenchMessagesWindow.Context, WorkbenchMessagesWindow.Context) +
            new TStatusItem("~Esc~ Close", Keys.kbNoKey, WorkbenchMessagesWindow.CloseMessages) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) + Exit();
        var monitor = new TStatusDef(WorkbenchRunMonitor.Context, WorkbenchRunMonitor.Context) +
            new TStatusItem("~Esc~ Back to workspace", Keys.kbNoKey, 0) +
            new TStatusItem("~Alt+C~ Cancel run", Keys.kbNoKey, WorkbenchDialog.CancelRun) +
            new TStatusItem("~F6~ Results", Keys.kbF6, WorkbenchDialog.Results) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) + Exit();
        // The Start Page names its own Enter and Esc (they act in the window; the labels carry no key). The empty desktop
        // after Esc has the same keys without them.
        var noProject = new TStatusDef(NoProjectContext, NoProjectContext) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~Ctrl+N~ New project", Keys.kbCtrlN, NewProject) +
            new TStatusItem("~Ctrl+O~ Open project", Keys.kbCtrlO, OpenProject) +
            new TStatusItem("~Enter~ Open", Keys.kbNoKey, WorkbenchStartWindow.OpenRecent) +
            new TStatusItem("~Esc~ Close", Keys.kbNoKey, Views.cmClose) +
            new TStatusItem("~Alt+X~ Exit", Keys.kbAltX, Views.cmQuit);
        var emptyDesktop = new TStatusDef(EmptyDesktopContext, EmptyDesktopContext) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) +
            new TStatusItem("~Ctrl+N~ New project", Keys.kbCtrlN, NewProject) +
            new TStatusItem("~Ctrl+O~ Open project", Keys.kbCtrlO, OpenProject) +
            new TStatusItem("~Alt+X~ Exit", Keys.kbAltX, Views.cmQuit);
        var comparison = new TStatusDef(WorkbenchComparisonWindow.Context, WorkbenchComparisonWindow.Context) +
            new TStatusItem("~Arrows~ Pan", Keys.kbNoKey, 0) +
            new TStatusItem("~N~ Next", Keys.kbNoKey, WorkbenchComparisonWindow.NextDiff) +
            new TStatusItem("~P~ Previous", Keys.kbNoKey, WorkbenchComparisonWindow.PreviousDiff) +
            new TStatusItem("~Alt+D/E/A~ View", Keys.kbNoKey, 0) +
            new TStatusItem("~Esc~ Close", Keys.kbEsc, Views.cmClose) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts);
        var scenarioFiles = new TStatusDef(WorkbenchScenariosWindow.Context, WorkbenchScenariosWindow.Context) +
            new TStatusItem("~Ins~ Add files", Keys.kbNoKey, WorkbenchDialog.AddScenarioFiles) +
            new TStatusItem("~Del~ Remove", Keys.kbNoKey, WorkbenchDialog.RemoveScenario) +
            new TStatusItem("~F2~ Refresh", Keys.kbF2, WorkbenchDialog.Load) +
            new TStatusItem("~Esc~ Close", Keys.kbNoKey, WorkbenchScenariosWindow.CloseScenarios) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) + Exit();
        var fallback = new TStatusDef(0, 0xffff) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) +
            new TStatusItem("~Alt+X~ Exit", Keys.kbAltX, Views.cmQuit);
        var scenarioEditor = new TStatusDef(WorkbenchScenarioEditorWindow.Context, WorkbenchScenarioEditorWindow.Context) +
            new TStatusItem("~Ins~ New step", Keys.kbNoKey, WorkbenchScenarioEditorWindow.NewStep) +
            new TStatusItem("~Enter~ Edit", Keys.kbNoKey, WorkbenchScenarioEditorWindow.EditStep) +
            new TStatusItem("~Del~ Delete", Keys.kbNoKey, WorkbenchScenarioEditorWindow.DeleteStep) +
            new TStatusItem("~Ctrl+S~ Save", Keys.kbNoKey, WorkbenchScenarioEditorWindow.SaveDocument) +
            new TStatusItem("~Esc~ Close", Keys.kbNoKey, WorkbenchScenarioEditorWindow.CloseEditor) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) + Exit();
        var recording = new TStatusDef(WorkbenchRecordingWindow.Context, WorkbenchRecordingWindow.Context) +
            new TStatusItem("~F1~ Help", Keys.kbF1, Shortcuts) +
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu) + Exit();
        workspace.Next = results; results.Next = dialogs; dialogs.Next = messages; messages.Next = monitor; monitor.Next = comparison; comparison.Next = noProject; noProject.Next = emptyDesktop; emptyDesktop.Next = scenarioFiles; scenarioFiles.Next = scenarioEditor; scenarioEditor.Next = recording; recording.Next = fallback;
        var help = new TStatusDef(WorkbenchHelpWindow.Context, WorkbenchHelpWindow.Context) +
            new TStatusItem("~F1~ Index", Keys.kbF1, Shortcuts) +
            new TStatusItem("~Tab~ Link", Keys.kbNoKey, 0) +
            new TStatusItem("~Enter~ Open", Keys.kbNoKey, 0) +
            new TStatusItem("~Backspace~ Back", Keys.kbNoKey, 0) +
            new TStatusItem("~Esc~ Close", Keys.kbNoKey, 0);
        help.Next = workspace;
        return new WorkbenchStatusLine(r, help, notifications);
    }

    public override void GetEvent(ref TEvent ev)
    {
        base.GetEvent(ref ev);
        // Nested modal loops read through this root, but otherwise consume resize
        // commands before TProgram.HandleEvent can apply the driver dimensions.
        if (ev.What == Events.evCommand && ev.message.command == Views.cmScreenResized)
        {
            base.HandleEvent(ref ev);
            // Recompose the final control positions after the framework re-exposes
            // the tree; intermediate layout draws can be occluded by old siblings.
            if (main is not null) { main.FreeBuffer(); main.GetBuffer(); main.Redraw(); }
            DeskTop?.FreeBuffer();
            Redraw();
            if (DeskTop?.TopView() is WorkbenchOverview information)
                information.FitToDesktop();
            else if (DeskTop?.TopView() is TDialog dialog && !ReferenceEquals(dialog, main))
            {
                int x = Math.Max(0, (DeskTop.size.x - dialog.size.x) / 2);
                int y = Math.Max(0, (DeskTop.size.y - dialog.size.y) / 2);
                dialog.Locate(new(x, y, x + dialog.size.x, y + dialog.size.y));
            }
        }
        // F1 inside a modal dialog: the status line converted the key, but the modal loop does not know the command.
        bool f1 = ev.What == Events.evCommand && ev.message.command == Shortcuts || ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF1;
        if (f1 && DeskTop is not null)
        { ev.What = Events.evNothing; ShowContextHelp(); }
    }

    public override void Idle()
    {
        base.Idle();
        UpdateCommands();
        (StatusLine as WorkbenchStatusLine)?.Refresh();
        if (MenuBar is WorkbenchMenuBar bar) { bar.AppRunning = () => WorkbenchTargetLauncher.Running || main?.Busy == true; bar.Refresh(); }
    }

    // Disabled commands
    private static readonly ushort[] ProjectCommands =
    [
        WorkbenchTestExplorer.Command, CatalogWindow, Environments, SaveProject, SaveAsProject, CloseProject, WorkbenchDialog.ProjectInfo, WorkbenchDialog.Advanced, WorkbenchDialog.DiagnoseTarget, WorkbenchDialog.PrepareTargetCommand, WorkbenchDialog.FixIssuesCommand, WorkbenchDialog.Run, WorkbenchDialog.Load,
        WorkbenchDialog.Details, WorkbenchDialog.ToggleMark, WorkbenchDialog.SelectGroup, WorkbenchDialog.MarkAll,
        WorkbenchDialog.ClearSelection, WorkbenchDialog.Results, WorkbenchDialog.RunHistory, WorkbenchDialog.OpenRun,
        WorkbenchDialog.OpenCaptures, WorkbenchDialog.OpenOutput, WorkbenchDialog.CompareResult, WorkbenchDialog.ReviewCandidates,
        WorkbenchDialog.ApproveCandidate, WorkbenchDialog.ReplaceBaseline, WorkbenchDialog.BaselineBrowse, WorkbenchDialog.Rerun,
        WorkbenchDialog.EarlierRun, WorkbenchDialog.LaterRun, WorkbenchDialog.ShowResultScenario,
        WorkbenchDialog.ProgressWindow, ComparisonWindow, ProjectWindow,
        WorkbenchDialog.NewScenario, WorkbenchDialog.AddScenarioFiles, WorkbenchDialog.AddScenarioFolder, WorkbenchDialog.ManageScenarios,
        WorkbenchDialog.OpenScenario, WorkbenchDialog.RemoveScenario, WorkbenchDialog.ValidateScenarios, WorkbenchDialog.OpenScenarioExternal
    ];
    private void UpdateCommands()
    {
        foreach (ushort command in ProjectCommands) { if (main is null) DisableCommand(command); else EnableCommand(command); }

        if (main?.Busy == true) EnableCommand(WorkbenchDialog.CancelRun); else DisableCommand(WorkbenchDialog.CancelRun);
        if (main is not null && !WorkbenchTargetLauncher.Running) EnableCommand(LaunchApplication); else DisableCommand(LaunchApplication);
        if (WorkbenchTargetLauncher.Running) EnableCommand(StopApplication); else DisableCommand(StopApplication);
        // Edit and Remove stay enabled with a project; without a selected attached file they say what to
        // select (WorkbenchDialog.SelectedScenarioFile / ScenarioSelectionHint) instead of being silently greyed out.
    }
    private string DisabledReason(ushort command) => command == WorkbenchDialog.CancelRun ? "No run is active."
        : command == StopApplication ? "The application is not running."
        : command == LaunchApplication && main is not null ? "The application is already running."
        : "No project is open. Ctrl+O opens a project; Ctrl+N creates one.";

    private void OnNotification()
    {
        (StatusLine as WorkbenchStatusLine)?.Refresh();
        messagesWindow?.Reload();
    }

    private void CreateProject(string targetPath, string outputPath, WorkbenchPreferences? settings = null, bool showWindow = true)
    {
        startWindow?.EndModal(Views.cmCancel);
        main = new WorkbenchDialog(DeskTop!, targetPath, outputPath, settings ?? preferences with { }, notifications);
        main.Closed = () => { Overview?.EndModal(Views.cmCancel); main = null; };
        main.QuitReady = () => { if (main?.ConfirmDiscard() != false) EndModal(Views.cmQuit); };
        main.ProjectSaved = AddRecent;
        main.ShowMessagesFor = ShowMessages;
        // Native projects use this window as a controller; keep it hidden before insertion to avoid a startup flash.
        if (!showWindow) main.Hide();
        DeskTop!.Insert(main);
        if (showWindow) main.Select();
    }
    internal WorkbenchDialog? Project => main;
    public override ushort GetHelpCtx()
    {
        ushort context = base.GetHelpCtx();
        return main is null && context == Views.hcNoContext ? startWindow is null ? EmptyDesktopContext : NoProjectContext : context;
    }
    internal void ActivateProject(WorkbenchProject project, string path)
    {
        if (main is not null && !main.CanReplace(() => ActivateProject(project, path))) return;
        main?.CloseApproved();
        var view =WorkbenchPreferences.Load(preferences.StoragePath).ViewState(path);
        var settings = new WorkbenchPreferences(project.Target, project.OutputRoot, view?.Group ?? project.Group, string.Join(',', project.ScreenSizes))
            { SelectedIds = project.SelectedIds, Search = view?.Search ?? project.Search, StoragePath = preferences.StoragePath };
        CreateProject(project.Target, project.OutputRoot, settings);
        main!.ApplyProject(project, path, view);
        AddRecent(path);
    }
    internal bool CreateNativeProject(string path)
    {
        if (main is { Busy: true } && !main.CanReplace(() => CreateNativeProject(path))) return false;
        if (File.Exists(path)) { notifications.Warning("Choose a new .tsw name; New never overwrites an existing project."); return false; }
        NativeProject? native = null;
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (!Directory.Exists(folder) && WorkbenchPreferences.SamePath(folder, ProjectLocation.Home)) Directory.CreateDirectory(folder);
            native = NativeProject.Create(path, Path.GetFileNameWithoutExtension(path));
            WorkbenchEnvironments.Provision(native.Session);
            return ActivateNative(native, () => CreateNativeProject(path), fresh: true);
        }
        catch (Exception ex) { native?.Dispose(); notifications.Warning("Cannot create native project: " + ex.Message); return false; }
    }
    internal bool OpenNativeProject(string path, RecoveryChoice? recovery = null)
    {
        NativeProject? native = null;
        try
        {
            try { native = NativeProject.Open(path, recovery ?? RecoveryChoice.Cancel); }
            catch (RecoveryRequiredException) when (recovery is null)
            {
                var choice = NativeRecoveryDialog.Choose(DeskTop!);
                if (choice == RecoveryChoice.Cancel) return false;
                native = NativeProject.Open(path, choice);
            }
            return ActivateNative(native, () => OpenNativeProject(path, recovery));
        }
        catch (Exception ex) { native?.Dispose(); notifications.Warning("Cannot open native project: " + ex.Message); return false; }
    }
    private bool ActivateNative(NativeProject native, Action retry, bool fresh = false)
    {
        try
        {
            var project = native.Read();
            if (main is not null && !main.CanReplace(retry)) { if (fresh || !native.Session.IsDirty) native.Session.Close(fresh ? CloseChoice.Discard : CloseChoice.Save); native.Dispose(); return false; }
            main?.CloseApproved();
            string path = native.Session.ArchivePath;
            var view = WorkbenchPreferences.Load(preferences.StoragePath).ViewState(path);
            var settings = new WorkbenchPreferences(project.Target, project.OutputRoot, view?.Group, string.Join(',', project.ScreenSizes))
                { SelectedIds = project.SelectedIds, Search = view?.Search ?? "", StoragePath = preferences.StoragePath };
            CreateProject(project.Target, project.OutputRoot, settings, showWindow: false);
            main!.ApplyProject(project, path, view); main.Native = native;
            AddRecent(path); Post(WorkbenchDialog.ProjectInfo); return true;
        }
        catch { native.Dispose(); throw; }
    }
    private void AddRecent(string path)
    {
        try { WorkbenchPreferences.Update(preferences.StoragePath, p => p.WithRecent(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { notifications.Warning("Could not update the recent project list.", ex.ToString()); }
    }
    private string[] RecentProjects() => WorkbenchPreferences.Load(preferences.StoragePath).RecentProjects.Where(p => Path.GetExtension(p).Equals(".tsw", StringComparison.OrdinalIgnoreCase)).ToArray();

    internal void ShowStart() => Post(OpenStartPage);

    private void RunStartPage()
    {
        if (startWindow is not null) return;

        ushort choice;
        string? recentPath;
        using (var dialog = new WorkbenchStartWindow(DeskTop!, RecentProjects()))
        {
            startWindow = dialog;
            try
            {
                choice = DeskTop!.ExecView(dialog);
                recentPath = dialog.RecentPath;
            }
            finally { startWindow = null; }
        }

        switch (choice)
        {
            case WorkbenchStartWindow.OpenRecent when recentPath is not null:
                Post(WorkbenchStartWindow.OpenRecent, new WorkbenchStartWindow.RecentProject(recentPath));
                break;
            case OpenProject:
            case NewProject:
            case Views.cmQuit:
                Post(choice);
                break;
        }
    }
    internal void ShowMessages(WorkbenchNotice? focus = null)
    {
        if (messagesWindow is null)
        {
            messagesWindow = new WorkbenchMessagesWindow(DeskTop!, notifications, WorkbenchPreferences.Load(preferences.StoragePath).Window("messages"));
            var window = messagesWindow;
            messagesWindow.Closed = () =>
            {
                messagesWindow = null;
                try { WorkbenchPreferences.Update(preferences.StoragePath, p => p.WithWindow("messages", window.GetBounds())); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            };
            DeskTop!.Insert(messagesWindow);
        }
        messagesWindow.Select();
        if (focus is not null) messagesWindow.Focus(focus);
    }

    private void OpenRecent(string path)
    {
        if (!File.Exists(path))
        {
            Forget(path);
            notifications.Warning($"Recent project not found: {Path.GetFileName(path)}. It was removed from the recent list.", "Missing recent project: " + path);
            return;
        }
        OpenNativeProject(path);
    }
    private void Forget(string path)
    {
        try { WorkbenchPreferences.Update(preferences.StoragePath, p => p.WithoutRecent(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static ushort KeyCommand(TEvent ev)
    {
        ushort command = ev.keyDown.keyCode switch
        {
            Keys.kbCtrlN => NewProject, Keys.kbCtrlO => OpenProject, Keys.kbCtrlS => SaveProject, Keys.kbF1 => Shortcuts,
            Keys.kbF2 => WorkbenchDialog.Load, Keys.kbF3 => WorkbenchDialog.ProjectInfo, Keys.kbF4 => WorkbenchDialog.Advanced, Keys.kbF5 => WorkbenchDialog.Run,
            Keys.kbF6 => WorkbenchDialog.Results, _ => (ushort)0
        };
        if (command != 0) return command;
        // Fixed window numbers, equal to the TWindow numbers and the Window menu. This route runs before
        // TProgram's Alt+1..9 selection, which would only select an existing window; these also open Scenarios, Results
        // and Messages. Alt+3: Run Configuration when idle, the Run Monitor while a run is active (never starts a run).
        return TMenuView.GetAltChar(ev.keyDown.keyCode, ev.keyDown.charScan.charCode, ev.keyDown.controlKeyState) switch
        {
            '1' => ProjectWindow, '2' => WorkbenchDialog.ManageScenarios, '3' => WorkbenchDialog.ProgressWindow,
            '4' => WorkbenchDialog.Results, '5' => ComparisonWindow, '6' => MessagesWindow, _ => (ushort)0
        };
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown)
        {
            notifications.NoteInput();
            // Top-level menu mnemonics take precedence over work-area buttons.
            // F10 still opens the menu bar; modal dialogs retain framework routing.
            if (DeskTop?.current is TWindow area && DeskTop.TopView() is not TDialog)
            {
                char key = char.ToUpperInvariant(TMenuView.GetAltChar(ev.keyDown.keyCode, ev.keyDown.charScan.charCode, ev.keyDown.controlKeyState));
                if (key != '\0' && MenuBar?.FindItem(key) is null)
                {
                    TButton? match = null;
                    area.ForEach((view, _) => {
                        if (view is TButton button && button.Title is { } title)
                        {
                            int mark = title.IndexOf('~');
                            if (mark >= 0 && mark + 1 < title.Length && char.ToUpperInvariant(title[mark + 1]) == key) match = button;
                        }
                    }, 0);
                    if (match is not null) { ClearEvent(ref ev); if (!match.GetState(Views.sfDisabled)) match.Press(); return; }
                }
            }
            // Ctrl+S in the scenario editor saves the scenario document, not the project.
            if (ev.keyDown.keyCode == Keys.kbCtrlS && DeskTop?.current is WorkbenchScenarioEditorWindow scenarioEditor)
            { ClearEvent(ref ev); scenarioEditor.Execute(WorkbenchScenarioEditorWindow.SaveDocument); return; }
            ushort command = KeyCommand(ev);
            if (command != 0)
            {
                if (!CommandEnabled(command)) { ClearEvent(ref ev); notifications.Warning(DisabledReason(command)); return; }
                ev.What = Events.evCommand; ev.message.command = command; ev.message.infoPtr = null;
            }
        }
        if (ev.What == Events.evCommand)
        {
            ushort command = ev.message.command;
            if (command == Open)
            {
                ClearEvent(ref ev);
                if (startupProjectPath is not null && startupProject is null) { OpenNativeProject(startupProjectPath); if (main is null) ShowStart(); }
                else if (startupProject is not null) { ActivateProject(startupProject, startupProjectPath!); }
                else if (showStart) ShowStart();
                else CreateProject(target, output, preferences);
                return;
            }
            if (command == OpenStartPage)
            {
                ClearEvent(ref ev);
                RunStartPage();
                return;
            }
            if (command == Views.cmQuit)
            {
                ClearEvent(ref ev);
                if (DeskTop?.TopView() is TView modal && modal != this)
                { modal.EndModal(Views.cmCancel); Post(Views.cmQuit); return; }
                if (main is null) { WorkbenchTargetLauncher.Stop(); EndModal(Views.cmQuit); } else main.RequestQuit();
                return;
            }
            if (command is LaunchApplication or StopApplication)
            {
                ClearEvent(ref ev);
                if (command == StopApplication) { WorkbenchTargetLauncher.Stop(); notifications.Info("Application stopped."); }
                else if (main is null) notifications.Warning(DisabledReason(command));
                else
                {
                    try { WorkbenchTargetLauncher.Start(main.CaptureProject()); notifications.Info("Application launched (no recording)."); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
                    { notifications.Warning("Launch: " + ex.Message); }
                }
                (MenuBar as WorkbenchMenuBar)?.Refresh();
                return;
            }
            if (command == WorkbenchStartWindow.OpenRecent && ev.message.infoPtr is WorkbenchStartWindow.RecentProject recent)
            { ClearEvent(ref ev); OpenRecent(recent.Path); return; }
            if (WorkbenchHelpContext.IsCommand(command))
            { ClearEvent(ref ev); ShowHelp((HelpTopic)(command - WorkbenchHelpContext.CommandBase)); return; }
            if (command == WorkbenchDialog.ProjectInfo && main?.Native is not null) { ClearEvent(ref ev); ShowOverview(); return; }
            if (command is WorkbenchTestExplorer.Command or CatalogWindow or Environments or NewProject or OpenProject or SaveProject or SaveAsProject or CloseProject or ProjectWindow or Shortcuts or About
                or MessagesWindow or ComparisonWindow or TestModes or WorkbenchDialog.OperationError)
            {
                ClearEvent(ref ev);
                try { Execute(command); }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                { WorkbenchErrorDialog.Show(DeskTop!, notifications, "Cannot Open Project", ex.Message, ex.ToString(), n => ShowMessages(n)); }
                return;
            }
            if (WorkbenchDialog.IsCommand(command))
            {
                if (main is null) { ClearEvent(ref ev); notifications.Warning(DisabledReason(command)); }
                else main.HandleEvent(ref ev);
                return;
            }
        }
        base.HandleEvent(ref ev);
    }

    private void Execute(ushort command)
    {
        switch (command)
        {
            case NewProject:
                string? created = WorkbenchDialog.ChooseProjectPath(DeskTop!, true, null,
                    Path.Combine(ProjectLocation.Home, "Untitled.tsw"));
                if (created is not null) CreateNativeProject(created);
                break;
            case OpenProject:
                string? path = WorkbenchDialog.ChooseProjectPath(DeskTop!, false, main?.ProjectPath ?? RecentProjects().FirstOrDefault());
                if (path is not null) OpenNativeProject(path);
                break;
            case WorkbenchTestExplorer.Command:
                if (main?.Native is not null)
                {
                    WorkbenchTestExplorer.Show(DeskTop!, main);
                    main.RefreshNativeTitle();
                }
                break;
            case Environments:
                if (main?.Native is not null) WorkbenchEnvironments.Show(DeskTop!, main);
                break;
            case SaveProject: if (main is null) notifications.Warning(DisabledReason(command)); else main.SaveProject(false); break;
            case SaveAsProject: if (main is null) notifications.Warning(DisabledReason(command)); else main.SaveProject(true); break;
            case CloseProject: main?.Close(); break;
            case ProjectWindow: if (main?.Native is not null) ShowOverview(); else main?.Select(); break;
            case CatalogWindow: main?.Select(); break;
            case ComparisonWindow: if (main?.Native is not null) main.ShowBaselineExplorer(); else main?.SelectComparison(); break;
            case MessagesWindow: case WorkbenchDialog.OperationError: ShowMessages(); break;
            case Shortcuts: ShowContextHelp(); break;
            case TestModes: ShowHelp(HelpTopic.Modes); break;
            case About: ShowInformation("About TSharpWorkbench", WorkbenchHelp.About()); break;
        }
    }

    private void ShowOverview()
    {
        if (main?.Native is null || Overview is not null) return;
        var project = main;
        ushort choice;
        using (var dialog = new WorkbenchOverview(DeskTop!, project))
        {
            Overview = dialog;
            project.OverviewChanged = dialog.Reload;
            try { choice = DeskTop!.ExecView(dialog); }
            finally
            {
                project.OverviewChanged = null;
                Overview = null;
            }
        }
        if (choice != Views.cmCancel && ReferenceEquals(main, project)) Post(choice);
    }

    private void ShowContextHelp()
    {
        if (HelpWindow is not null) { HelpWindow.Viewer!.GoToIndex(); return; }
        var modal = DeskTop?.TopView();
        var topic = WorkbenchHelpContext.Resolve(modal is not null && modal != DeskTop ? modal : DeskTop?.current);
        if (topic == HelpTopic.Index) topic = WorkbenchHelpContext.Resolve(DeskTop?.current);
        if (topic == HelpTopic.Index && main is not null) topic = HelpTopic.Workspace;
        ShowHelp(topic);
    }
    internal void ShowHelp(HelpTopic topic)
    {
        if (HelpWindow is not null) { HelpWindow.Viewer!.NavigateTo((int)topic); HelpWindow.Select(); return; }
        if (showingHelp) return;
        showingHelp = true;
        try {
            using var resource = new WorkbenchHelpResource(HelpPath);
            int w = Math.Min(112, DeskTop!.size.x - 2), h = Math.Min(38, DeskTop.size.y - 2);
            using var window = new WorkbenchHelpWindow(new(0, 0, w, h), resource.File, topic);
            HelpWindow = window;
            try { DeskTop.ExecView(window); } finally { HelpWindow = null; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            notifications.Warning("Help unavailable. Reinstall or redeploy Workbench including Help/TSharpWorkbench.hlp and its .sha256 file.");
            ShowInformation("Help unavailable", ["The installed help resource is missing, damaged or incompatible.",
                "Reinstall or redeploy the complete Workbench distribution,", "including Help/TSharpWorkbench.hlp and its .sha256 file.",
                "Project operations remain available.", "", ex.Message]);
        }
        finally { showingHelp = false; }
    }
    // Static, readable information dialogs (Help menu): text sized to the content, Close as the default button.
    private void ShowInformation(string title, string[] lines)
    {
        int w = Math.Min(Math.Max(60, lines.Max(l => l.Length) + 6), DeskTop!.size.x - 2);
        int h = Math.Min(lines.Length + 6, DeskTop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), title); dialog.options |= Views.ofCentered; dialog.helpCtx = DialogContext;
        dialog.Insert(new TStaticText(new(3, 2, w - 2, h - 3), string.Join('\n', lines.Select(l => WorkbenchText.Fit(l, w - 5)))));
        var close = new TButton(new(w - 15, h - 3, w - 3, h - 1), "Close", Views.cmOK, ButtonConstants.bfDefault); dialog.Insert(close); close.Select();
        DeskTop.ExecView(dialog);
    }
}

internal sealed partial class WorkbenchDialog : TWindow
{
    internal const ushort Load = 0x7de1, Run = 0x7de2, RunGroup = 0x7de3, RunAll = 0x7de4, Details = 0x7de5, Results = 0x7de6;
    internal const ushort BrowseTarget = 0x7de7, BrowseOutput = 0x7de8, Context = 0x7de0;
    internal const ushort Advanced = 0x7de9, Capabilities = 0x7dea, ClearSelection = 0x7deb, SelectGroup = 0x7dec, CancelRun = 0x7ded;
    internal const ushort OperationError = 0x7dee;
    internal const ushort SelectMode = 0x7dcf, MarkAll = 0x7dce, OpenOutput = 0x7dcd, ToggleMark = 0x7dcc, NewProperties = 0x7dcb;
    internal static bool IsCommand(ushort command) => command is >= Load and <= OperationError || command is SelectMode or MarkAll or OpenOutput or ToggleMark or NewProperties or DiagnoseTarget or ProjectInfo
        || command is PrepareTargetCommand or FixIssuesCommand || command is >= BaselineBrowse and <= ShowResultScenario || IsScenarioCommand(command);
    internal const ushort BaselineBrowse=0x7d60, CompareResult=0x7d61, ReviewCandidates=0x7d62,
        ApproveCandidate=0x7d63, ReplaceBaseline=0x7d64, ProgressWindow=0x7d65, OpenRun=0x7d66, RunHistory=0x7d67, OpenCaptures=0x7d68,
        Rerun=0x7d69, EarlierRun=0x7d6a, LaterRun=0x7d6b, ShowResultScenario=0x7d6c;
    private const ushort Dispatch = 0x7def;
    private readonly List<(TView View, Func<int, int, TRect> Bounds)> layout = [];
    private void Place(TView view, Func<int, int, TRect> bounds)
    { view.growMode = 0; layout.Add((view, bounds)); view.ChangeBounds(bounds(size.x, size.y)); Insert(view); }

    public override void ChangeBounds(TRect bounds)
    {
        base.ChangeBounds(bounds);
        if (layout is null) return;
        Lock();
        foreach (var item in layout) item.View.ChangeBounds(item.Bounds(size.x, size.y));
        Unlock();
        // The previous group image contains controls at their old coordinates.
        // Rebuild it after relayout so a later parent repaint cannot restore ghosts.
        FreeBuffer();
        if (initialized) { UpdateDetails(); UpdateHeader(); }
        DrawView();
    }
    private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
    // One-line caption; text longer than the view ends with an ellipsis instead of wrapping.
    private sealed class Line(TRect bounds, string text = "") : TStaticText(bounds, text)
    {
        internal string Current => Text;
        internal void SetText(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }
    private readonly TDeskTop desktop;
    private readonly WorkbenchNotifications notifications;
    private readonly TInputLine target, output;
    private readonly ScenarioList scenarios;
    private readonly GroupTree groups;
    private readonly TListBox detailPane;
    private string detailKey = "";
    private readonly Line columns, detailTitle, caption;
    private string targetDriver => sessionRun.Mode;
    private readonly TInputLine search;
    private string lastSearch = "";
    private int lastGroup = -1;
    private int completed, passed, failed, blocked, cancelled, skipped, total;
    private RunState runState = RunState.Finished;
    private bool cancelRequested;
    private string latestEvent = "", visualText = "";
    private WorkbenchRunRequest? lastRequest;
    private Action? afterRun;
    private string currentExecution = "";
    private bool initialSelectionRestored;
    private bool initialized;
    private readonly Dictionary<string, (string Target, DateTimeOffset Began, TimeSpan Duration, string TargetPath)> runInfo = [];
    private readonly ScenarioRunner runner = new();
    private ScenarioDescriptor[] catalog = [];
    private string[] groupNames = [];
    private readonly List<BatchResult> history = [];
    private CancellationTokenSource? cancellation;
    private string? catalogTarget;
    private string? catalogBuild;
    // Attached-file stamps taken before the current catalog's documents were read (run-boundary freshness).
    private Dictionary<string, ScenarioFiles.FileStamp?>? catalogStamps;
    private bool busy;
    private bool quitRequested;
    private readonly WorkbenchPreferences preferences;
    internal TInputLine SearchBox => search;
    internal string CaptionText => caption.Current;
    // The project summary the Workspace header showed before the redesign; Project > Information... shows it now.
    internal string HeaderText => string.Join("\n", summary.Target, summary.Output, summary.Setup, summary.Loaded, summary.Marked, summary.LastRun);
    internal string ColumnHeader => columns.Current;
    internal string DetailTitle => detailTitle.Current;

    internal void RequestQuit()
    {
        if (busy && activeRequest is null) { quitRequested = true; cancellation?.Cancel(); Status("Cancelling scenario loading before exit..."); }
        else if (busy) ConfirmLeavingRun("exit", () => { if (Native is null && !CloseScenarioEditors()) return; SavePreferences(); QuitReady?.Invoke(); });
        else { if (Native is null && !CloseScenarioEditors()) return; SavePreferences(); QuitReady?.Invoke(); }
    }

    // Layout (, simplified by the redesign): the Groups pane on the left, the Scenarios list and the
    // Details pane on the right and one caption row. The project summary is in Project > Information...; Run...,
    // Results and Properties... are keys (F5, F6, F4; Alt+U, Alt+L, Alt+O) and menu items. Positions are functions of
    // the window size.
    // The first row of the right pane is its tool row - the Scenarios label and Search with a wide field
    // - followed by one empty row, so the field never sits on the column headers; the headers and the list start one row
    // lower. removed the "Native-runnable only" check box from this row (no scenario is hidden by
    // driver support; a run records what a driver cannot execute as Skipped).
    private static int GroupsEdge(int w) => Math.Clamp(30 + (w - 140) * 3 / 10, 16, 40);
    private const int ListTop = 4;
    private static int ListBottom(int h) => 3 + Math.Max(3, (h - 6) * 6 / 13);
    private static (int Label, int InputEnd) SearchRow(int w)
    {
        int gx = GroupsEdge(w), label = gx + 15;
        int end = Math.Max(label + 18, Math.Min(label + 7 + 48, w - 3));
        return (label, end);
    }

    internal WorkbenchDialog(TDeskTop desktop, string targetPath, string outputPath, WorkbenchPreferences? preferences = null,
        WorkbenchNotifications? notifications = null)
        : base(desktop.GetExtent(), "Untitled project", 1)
    {
        this.desktop = desktop;
        this.notifications = notifications ?? new();
        this.preferences = preferences ?? new(targetPath, outputPath, null, "80x25");
        helpCtx = Context;
        growMode = Views.gfGrowHiX | Views.gfGrowHiY;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        state &= unchecked((ushort)~Views.sfShadow);
        target = new(new TRect(0, 0, 1, 1), 32768); target.SetData(targetPath);
        output = new(new TRect(0, 0, 1, 1), 32768); output.SetData(outputPath);

        // Insertion order is the Tab order: Groups, Search, Scenarios, Details.
        var groupScroll = new TScrollBar(new(0,0,1,1));
        Place(groupScroll, (w,h) => new(GroupsEdge(w),2,GroupsEdge(w)+1,h-2));
        groups = new(new(0,0,1,1), groupScroll);
        Place(groups, (w,h) => new(2,2,GroupsEdge(w),h-2));
        Place(new WindowLabel(new(0,0,1,1), "~G~roups", groups), (w,h) => new(1,1,GroupsEdge(w),2));
        search = new(new(0,0,1,1), 128); search.SetData(this.preferences.Search);
        Place(search, (w,h) => new(SearchRow(w).Label+7,1,SearchRow(w).InputEnd,2));
        // Alt+E: Alt+S is the Scenarios menu.
        Place(new WindowLabel(new(0,0,1,1), "S~e~arch", search), (w,h) => new(SearchRow(w).Label-1,1,SearchRow(w).Label+7,2));
        var scenarioScroll = new TScrollBar(new(0,0,1,1));
        Place(scenarioScroll, (w,h) => new(w-3,ListTop,w-2,ListBottom(h)));
        scenarios = new(new(0,0,1,1), scenarioScroll);
        Place(scenarios, (w,h) => new(GroupsEdge(w)+2,ListTop,w-3,ListBottom(h)));
        Place(new WindowLabel(new(0,0,1,1), "S~c~enarios", scenarios), (w,h) => new(GroupsEdge(w)+1,1,GroupsEdge(w)+13,2));
        columns = new(new(0,0,1,1)); Place(columns, (w,h) => new(GroupsEdge(w)+2,ListTop-1,w-3,ListTop));
        var detailScroll = new TScrollBar(new(0,0,1,1));
        Place(detailScroll, (w,h) => new(w-3,ListBottom(h)+1,w-2,h-2));
        detailPane = new(new(0,0,1,1), 1, detailScroll);
        Place(detailPane, (w,h) => new(GroupsEdge(w)+2,ListBottom(h)+1,w-3,h-2));
        Place(new WindowLabel(new(0,0,1,1), "~D~etails", detailPane), (w,h) => new(GroupsEdge(w)+1,ListBottom(h),GroupsEdge(w)+11,ListBottom(h)+1));
        detailTitle = new(new(0,0,1,1)); Place(detailTitle, (w,h) => new(GroupsEdge(w)+11,ListBottom(h),w-2,ListBottom(h)+1));
        caption = new(new(0,0,1,1)); Place(caption, (w,h) => new(2,h-2,w-2,h-1));
        scenarios.Changed = UpdateSelection;
        scenarios.LastResult = LastResult;
        scenarios.Note = Note;
        InitializeProjectDefaults();
        initialized = true;
        savedProject = CaptureProject();
        UpdateSelection();
        scenarios.Select();
        if (targetPath.Length > 0) Post(Load);
    }

    private static string ApplicationName(string path) => string.IsNullOrWhiteSpace(path) ? "none" : Path.GetFileNameWithoutExtension(path);
    private string? FilterGroup => groups.SelectedGroup;
    private string ModeDescription() => WorkbenchCapabilities.Label(targetDriver);
    private void UpdateSelection()
    {
        if (initialized) { UpdateDetails(); UpdateProjectTitle(); UpdateHeader(); }
    }
    private void ApplyFilter()
    {
        lastSearch = search.Data; lastGroup = groups.focused;
        scenarios.Filter(FilterGroup, search.Data.Trim()); UpdateSelection();
    }

    internal static string InitialDirectory(string value)
    {
        try
        {
            string? path = Path.GetFullPath(value);
            if (File.Exists(path)) path = Path.GetDirectoryName(path);
            while (path is not null && !Directory.Exists(path)) path = Path.GetDirectoryName(path);
            if (path is not null) return path;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        return Environment.CurrentDirectory;
    }

    // Browse commands without menu items or keys; Project Properties has the Browse buttons. (F3 is Project Information
    // since the redesign, F4 Project Properties since the redesign.)
    private void SelectTarget()
    {
        string pattern = OperatingSystem.IsWindows() ? "*.exe" : "*";
        using var picker = new TFileDialog(pattern, "Select target executable", "Executable ~n~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdNoLoadDir, 0);
        picker.fileName!.MaxLen = 32767;
        picker.SetData(Path.Combine(InitialDirectory(target.Data), pattern));
        ushort result = desktop.ExecView(picker);
        if (result is not (Views.cmFileOpen or Views.cmOK)) return;
        picker.GetFileName(out string selected);
        if (!File.Exists(selected)) { Error("Select an existing executable file. The target is unchanged."); return; }
        if (WorkbenchPreferences.SamePath(target.Data, selected)) return;
        target.SetData(Path.GetFullPath(selected));
        InvalidateCatalog();
        SavePreferences();
        LoadCatalog();
    }

    private void SelectOutput()
    {
        using var picker = new WorkbenchDirectoryDialog(InitialDirectory(output.Data));
        if (desktop.ExecView(picker) != Views.cmOK) return;
        output.SetData(picker.SelectedPath);
        SavePreferences();
        Status("Runs folder selected. Each run creates a new run folder there.");
    }

    // Every message goes through the application notification channel, so it is visible whichever window is active.
    internal void Status(string value) => notifications.Info(value);
    private void Error(string message, string? details = null) => notifications.Warning(message, details);
    private void Fail(string title, string message, string? details) =>
        WorkbenchErrorDialog.Show(desktop, notifications, title, message, details, n => ShowMessagesFor?.Invoke(n));
    internal Action<WorkbenchNotice>? ShowMessagesFor { get; set; }
    private void Deliver(Action action) => Post(Dispatch, new Work(action));

    public override bool Valid(ushort command)
    {
        if (command == Views.cmClose && !approvedClose)
        {
            if (busy && activeRequest is null) { Error("Scenarios are still loading. Try again when loading has finished."); return false; }
            if (busy) { ConfirmLeavingRun("continue", Close); return false; }
            if (Native is null && !CloseScenarioEditors()) return false;
            if (!ConfirmDiscard()) return false;
        }
        return base.Valid(command);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (!initialized) { base.HandleEvent(ref ev); return; }
        if (!busy && catalogTarget is not null && target.Data != catalogTarget) InvalidateCatalog();
        if (ev.What == Events.evBroadcast && ev.message.command == Views.cmListItemFocused && ReferenceEquals(ev.message.infoPtr, groups))
        { ApplyFilter(); }
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter && current is not TButton && current != groups)
        { if (current == scenarios) scenarios.SelectItem(scenarios.focused); ClearEvent(ref ev); return; }
        // The mnemonics of the removed Run..., Results and Properties... buttons stay Workspace keys.
        if (ev.What == Events.evKeyDown && TMenuView.GetAltChar(ev.keyDown.keyCode, ev.keyDown.charScan.charCode, ev.keyDown.controlKeyState) switch
            { 'U' => Run, 'L' => Results, 'O' => Advanced, _ => (ushort)0 } is var alias and not 0)
        { ev.What = Events.evCommand; ev.message.command = alias; ev.message.infoPtr = null; }
        // Esc in the Workspace does nothing; it never cancels a run (Run Monitor > Cancel run or Runs > Cancel Run do).
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEsc) { ClearEvent(ref ev); return; }
        if (ev.What == Events.evCommand && ev.message.command == Dispatch && ev.message.infoPtr is Work work)
        { ClearEvent(ref ev); work.Action(); return; }
        if (ev.What == Events.evCommand && IsCommand(ev.message.command))
        {
            ushort command = ev.message.command; ClearEvent(ref ev);
            if (command == CancelRun)
            {
                if (!busy || activeRequest is null) { Error("No run is active."); return; }
                RequestCancel(); return;
            }
            // Scenario file management decides itself what an active run or loading allows.
            if (IsScenarioCommand(command))
            {
                try { ScenarioCommand(command); }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
                { Error(ex.Message, ex.ToString()); }
                return;
            }
            // Results, run history, evidence and comparison stay usable during a run (completed artifacts only).
            // Project Properties stays available while scenarios load; a changed executable restarts the loading.
            if (busy && !(command is Advanced or PrepareTargetCommand or FixIssuesCommand && activeRequest is null) && command is not (DiagnoseTarget or ProjectInfo or Results or Capabilities or OperationError or ProgressWindow or CompareResult or BaselineBrowse or ReviewCandidates
                or ApproveCandidate or ReplaceBaseline or OpenRun or RunHistory or OpenCaptures or OpenOutput or EarlierRun or LaterRun or Rerun or ShowResultScenario
                or Details or ToggleMark or ClearSelection or MarkAll or SelectGroup))
            { Error(activeRequest is null ? "Scenarios are loading. Wait for loading to finish." : "Tests are running. Wait for the run to finish, or use Runs > Cancel Run."); return; }
            try
            {
                if (Native is not null && command is MarkAll or ClearSelection)
                {
                    MarkedTests.Clear();
                    if (command == MarkAll) MarkedTests.UnionWith(Native.Session.Tests.Select(t => t.Id));
                    TestExplorer?.DrawView();
                    Status($"{MarkedTests.Count} Tests marked.");
                    return;
                }
                if (command == MarkAll)
                {
                    int skipped = catalog.Count(s => s.ExpectedResult != ScenarioOutcomes.Passed);
                    foreach (var entry in catalog.Where(s => s.ExpectedResult == ScenarioOutcomes.Passed)) scenarios.RestoreMarks([entry.Id]);
                    UpdateSelection();
                    Status($"Marked all {catalog.Length - skipped} runnable scenarios" + (skipped > 0 ? $"; {skipped} expected-failure scenario{(skipped == 1 ? " is" : "s are")} not marked." : "."));
                    return;
                }
                if (command == OpenOutput) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory.Exists(output.Data) ? output.Data : InitialDirectory(output.Data)) { UseShellExecute = true }); return; }
                if (command == BrowseTarget) { SelectTarget(); return; }
                if (command == BrowseOutput) { SelectOutput(); return; }
                if (command == Load) { LoadCatalog(); return; }
                if (command == Results) { ShowResults(); return; }
                if (command == RunHistory) { ShowRunHistory(); return; }
                if (command == Advanced) { ShowProperties(false); return; }
                if (command == ProjectInfo) { ShowProjectInfo(); return; }
                if (command == DiagnoseTarget) { ShowTargetDiagnostics(); return; }
                if (command == PrepareTargetCommand) { PrepareProjectTarget(); return; }
                if (command == FixIssuesCommand) { ShowFixIssues(); return; }
                if (command == NewProperties) { ShowProperties(true); return; }
                if (command == Capabilities) { Browse("Test mode capabilities", WorkbenchCapabilities.Describe().Concat(NativeCapableScenarios())); return; }
                if (command == SelectMode) { ConfigureRun(); return; }
                if (command == ProgressWindow && Native is not null) { ShowRunExplorer(); return; }
                if (command == Run && Native is not null) { ConfigureMatrix(); return; }
                if (command == ProgressWindow)
                {
                    // Alt+3 / Window > Run configuration / monitor: the same monitor instance while a run is
                    // active (Esc there goes back to the window active now); otherwise Run Configuration, exactly as F5.
                    if (busy && activeRequest is not null && runningWindow is not null)
                    {
                        if (!ReferenceEquals(desktop.current, runningWindow)) runningWindow.Previous = desktop.current;
                        runningWindow.Select();
                        // Already frontmost but not current (it opened without focus): Select alone does not move focus.
                        if (!ReferenceEquals(desktop.current, runningWindow)) desktop.SetCurrent(runningWindow, selectMode.normalSelect);
                        return;
                    }
                    if (busy) { Error("Scenarios are loading. Wait for loading to finish, then press Alt+3 or F5."); return; }
                    command = Run;
                }
                if (command == BaselineBrowse) { ShowBaselineExplorer(); return; }
                if (command == CompareResult) { CompareSelected(); return; }
                // One candidate review for the selected checkpoint; approve or replace follows the store ().
                if (command is ReviewCandidates or ApproveCandidate or ReplaceBaseline) { ReviewSelected(); return; }
                if (command is EarlierRun or LaterRun) { NavigateRuns(command == EarlierRun ? -1 : 1); return; }
                if (command == Rerun) { var (batch, result, _) = SelectedResult(); RerunExecution(batch, result); return; }
                if (command == ShowResultScenario) { ShowResultScenarioDocument(); return; }
                if (command == OpenRun) { OpenHistoricalRun(); return; }
                if (command == OpenCaptures) { OpenSelectedCaptures(); return; }
                if (command == OperationError) { ShowMessagesFor?.Invoke(notifications.History.LastOrDefault()!); return; }
                if (command == ClearSelection) { scenarios.ClearMarks(); Status("All marks cleared."); return; }
                if (command == Details)
                {
                    detailPane.Select(); UpdateCaption();
                    if (scenarios.Current is null) Error("No scenario is focused. Choose All scenarios or clear the search.");
                    return;
                }
                if (command == ToggleMark)
                {
                    if (scenarios.Current is not { } focused) { Error("No scenario is focused. Choose All scenarios or clear the search."); return; }
                    scenarios.Toggle(focused.Id); return;
                }
                if (command is Run or RunGroup or RunAll && catalogTarget is not null && catalogBuild != BuildIdentity(target.Data))
                {
                    throw new ArgumentException("The target build changed. Refresh with F2 and select scenarios from the current catalog.");
                }
                if (catalog.Length == 0 || target.Data != catalogTarget) throw new ArgumentException("No scenarios are loaded for this target. Press F2 to load them.");
                if (command == SelectGroup)
                {
                    if (FilterGroup is { } group) { scenarios.MarkGroup(group); Status($"Marked the runnable scenarios of group {group}."); }
                    else Error("Choose a group in the Groups pane first; All scenarios is not a group (use Mark all runnable).");
                    return;
                }
                if (command is Run or RunGroup or RunAll) { ConfigureRun(); return; }
            }
            catch (Exception ex) { Error(ex.Message, ex.ToString()); }
            return;
        }
        base.HandleEvent(ref ev);
        if (search.Data != lastSearch || groups.focused != lastGroup) ApplyFilter();
        UpdateDetails(); UpdateProjectTitle(); UpdateCaption();
    }

    private void InvalidateCatalog()
    {
        catalog = []; catalogTarget = null; catalogBuild = null; catalogStamps = null; groupNames = []; compiledRaw = null; compiledTarget = null; loadedOnce = false;
        scenarios.LoadCatalog([]); groups.Load([]);
        initialSelectionRestored = true; search.SetData("");
        UpdateSelection();
    }

    // An apphost can remain identical while its managed application assembly is rebuilt.
    // Check both at discovery/run boundaries, without hashing large binaries on each UI event.
    internal static string? BuildIdentity(string path)
    {
        try
        {
            string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); }
            string managed = Path.ChangeExtension(path, ".dll");
            return Hash(path) + (managed != path && File.Exists(managed) ? ":" + Hash(managed) : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private void LoadCatalog()
    {
        string path;
        if (string.IsNullOrWhiteSpace(target.Data)) { Error("No target executable is set. Set it in Project Properties (Alt+O), then press F2."); return; }
        try { path = Path.GetFullPath(target.Data); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { Error("The target executable path is not valid. Correct it in Project Properties (Alt+O), then press F2.", ex.ToString()); return; }
        if (!File.Exists(path)) { Error($"Target executable not found: {Path.GetFileName(path)}. Correct it in Project Properties (Alt+O), then press F2.", "Missing target: " + path); return; }
        target.SetData(path);
        string? build = BuildIdentity(path);
        if (build is null) { InvalidateCatalog(); Error("The target build cannot be read. Rebuild the target, then refresh with F2.", "Unreadable target build: " + path); return; }
        bool sameBuild = catalogTarget == path && catalogBuild == build;
        if (catalogTarget == path && !sameBuild) initialSelectionRestored = true;
        // Switching to a prepared copy of the same application keeps the marks (and may resume Run Configuration).
        string[] marks = carriedMarks ?? (catalogTarget == path ? scenarios.MarkedIds.ToArray() : []);
        if (carriedMarks is not null) initialSelectionRestored = true;
        carriedMarks = null;
        var resume = afterCatalog; afterCatalog = null;
        string? previousGroup = FilterGroup;
        SavePreferences();
        // Keep the last usable catalog and marks until discovery succeeds. A failed
        // refresh must not discard project selection. Target changes clear explicitly.
        UpdateSelection();
        Status("Loading scenarios...");
        // Each attached file is checked on its own; a missing or invalid file is left out of the catalog and
        // reported (Scenarios window), instead of hiding every other scenario. The compiled catalog is discovered only
        // when it can exist: never for a Proxy project, never for an ordinary application without the diagnostic host
        // (it would only wait for the handshake deadline).
        string[] files = projectDefaults.ScenarioFiles;
        string[]? legacy = projectDefaults.LegacyFixtures;
        bool ordinary = ScenarioFiles.HostsDiagnostics(path) == false;
        StartWork(async token =>
        {
            var stamps = ScenarioFiles.Stamp(files);
            var inspected = files.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(f => f, ScenarioFiles.Inspect, StringComparer.OrdinalIgnoreCase);
            var states = ScenarioFiles.Check(files, path, null, ordinary, inspected);
            bool discover = !ordinary && !states.Any(a => a.Info.Proxy && a.Bound is not null);
            ScenarioDescriptor[]? compiled = null;
            if (discover)
            {
                // A failed catalog handshake is a diagnostic-host fact of this target (Target Diagnostics explains it).
                try { compiled = await runner.DiscoverRawAsync(path, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Deliver(() => { discoveryFailure = ex.Message; discoveryTarget = path; UpdateHeader(); }); throw; }
                states = ScenarioFiles.Check(files, path, compiled, false, inspected, legacy);
            }
            var raw = ScenarioFiles.Catalog(compiled ?? [], states);
            var found = ScenarioSelection.Runnable(raw);
            string unbound = Cli.FixtureOnlyNote(raw);
            if (build != BuildIdentity(path)) throw new IOException("Target build changed during discovery. Refresh with F2.");
            Deliver(() =>
            {
                compiledRaw = compiled; compiledTarget = compiled is null ? null : path; attached = states; loadedOnce = true;
                if (compiled is not null) { discoveryFailure = null; discoveryTarget = path; }
                ReloadScenariosWindow();
                catalog = found; catalogTarget = path; catalogBuild = build; catalogStamps = stamps; detailKey = "";
                scenarios.LoadCatalog(found);
                groupNames = Cli.Groups(found);
                groups.Load(found, initialSelectionRestored ? previousGroup : preferences.Group);
                string[] requested = initialSelectionRestored ? marks : preferences.SelectedIds;
                scenarios.RestoreMarks(requested);
                string[] stale = requested.Where(id => !found.Any(s => s.Id == id)).ToArray();
                initialSelectionRestored = true; ApplyFilter();
                var problems = states.Where(a => a.Problem is not null).ToArray();
                int usable = files.Length - problems.Length;
                if (stale.Length == 0 && found.Length == 0 && files.Length == 0)
                    Error(ScenarioFiles.NoneAttached + (ordinary ? " This ordinary application has no compiled diagnostic scenarios and does not need to be recompiled." : ""));
                else if (stale.Length == 0 && problems.Length > 0)
                    Error($"Loaded {found.Length} scenarios; {problems.Length} of {files.Length} attached file{(files.Length == 1 ? " was" : "s were")} not loaded "
                        + $"({string.Join(", ", problems.GroupBy(a => a.Status).Select(g => $"{g.Count()} {g.Key}"))}). See Scenario Manager (Alt+2) for status and Messages for causes.",
                        string.Join("\n", problems.Select(a => $"{a.Path}: {a.Status} - {a.Problem}")));
                else if (stale.Length == 0 && states.FirstOrDefault(a => a.Note is not null)?.Note is { } note)
                    Error($"Loaded {found.Length} scenarios from {usable} file{(usable == 1 ? "" : "s")}. {note}");
                else if (stale.Length == 0) Status($"Loaded {found.Length} scenarios" + (usable > 0 ? $" from {usable} file{(usable == 1 ? "" : "s")}." : ".") + unbound);
                else Error("Missing/stale scenario IDs were removed from the marks: " + string.Join(", ", stale), "Stale scenario IDs not in the current catalog: " + string.Join(", ", stale));
                UpdateDetails();
                // Resumed once loading has fully ended (not busy): e.g. Run Configuration after Prepare target.
                if (resume is not null) afterRun = resume;
            });
        });
    }

    private void SavePreferences()
    {
        string sizes = sessionRun.ScreenSizes.SequenceEqual(["80x25", "140x40"]) ? "standard" : string.Join(',', sessionRun.ScreenSizes);
        try
        {
            WorkbenchPreferences.Update(preferences.StoragePath, saved => saved with
            {
                Target = target.Data, Output = output.Data, Group = FilterGroup, Size = sizes,
                SelectedIds = scenarios.MarkedIds.ToArray(), Search = search.Data
            });
            SaveViewState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error("Could not save the Workbench preferences.", ex.ToString()); }
    }

    private void RunBatch(WorkbenchRunRequest request)
    {
        SavePreferences();
        var p=request.Project; string path=p.Target;
        if (BuildIdentity(path)!=request.Build) throw new ArgumentException("The target changed during configuration. Refresh the scenarios with F2.");
        // Never launch an old snapshot of a modified, deleted or detached JSON document.
        if (ScenarioFiles.StaleRun(request.Scenarios, projectDefaults.ScenarioFiles, catalogStamps) is { } stale) throw new ArgumentException(stale);
        total=request.Scenarios.Length*p.ScreenSizes.Length;completed=passed=failed=blocked=cancelled=skipped=0;
        runState=RunState.Starting;cancelRequested=false;currentExecution="starting the owned target";latestEvent="Run started.";visualText=p.TestType=="visual"?"compared when the run finishes":"not compared (no baselines in this mode)";
        DateTimeOffset began=DateTimeOffset.UtcNow;
        activeRequest=request;activeRunDirectory="Awaiting run directory";
        resultIndex=history.Count;runStarted=began;liveDurations=[];executionClock=null;resultsWindow?.Reload();
        var previousDirectories=Directory.Exists(p.OutputRoot)?Directory.GetDirectories(p.OutputRoot,"run-*").ToHashSet(StringComparer.OrdinalIgnoreCase):[];
        ShowMonitor();
        Status($"Run started: {RunLabel(p)}; {total} execution{(total == 1 ? "" : "s")}.");
        StartWork(async token =>
        {
            BatchResult batch;
            try
            {
                batch=await runner.RunAsync(path,request.Scenarios,request.Options(),message=>
                {
                    string[] created=Directory.Exists(p.OutputRoot)?Directory.GetDirectories(p.OutputRoot,"run-*").Where(d=>!previousDirectories.Contains(d)).ToArray():[];
                    Deliver(()=>{activeRunDirectory=created.Length==1?created[0]:"Concurrent folders: exact run directory available on completion";RunProgress(message);});
                },token);
                if(p.TestType=="visual"&&p.VisualBehavior=="candidates") CaptureCandidates(batch,request);
                File.WriteAllText(Path.Combine(batch.RunRoot,"workbench-request.json"),System.Text.Json.JsonSerializer.Serialize(new {
                    target=p.Target,build=request.Build,testType=p.TestType,visualBehavior=p.VisualBehavior,driver=p.Mode,
                    baselineManifestHash=request.BaselineManifestHash,scope=p.RunScope,scenarioIds=request.Scenarios.Select(s=>s.Id),sizes=p.ScreenSizes,baselineStore=p.BaselineStore,
                    outputRoot=p.OutputRoot,inspect=p.Inspect,allow=p.Allow,timeout=p.Timeout,scenarioTimeout=p.ScenarioTimeout
                },ScenarioRunner.Json));
            }
            catch(Exception ex) when (ex is not OperationCanceledException)
            {
                // The runner has returned: every owned target it launched was disposed before the exception reached here.
                Deliver(()=>RunEndedUnexpectedly(ex));
                return;
            }
            Deliver(()=>
            {
                // Results follows the finished run only while it showed the live run; a browsed earlier run stays shown.
                bool followLive=resultIndex>=history.Count;
                history.Add(batch);if(followLive)resultIndex=history.Count-1;runInfo[batch.RunRoot]=(ApplicationName(path),began,DateTimeOffset.UtcNow-began,path);durations[batch.RunRoot]=liveDurations;
                requests[batch.RunRoot]=request;activeRunDirectory=batch.RunRoot;RecordRunOutcome(path,batch);
                (passed,failed,blocked,cancelled)=Categories(batch);skipped=Skipped(batch);completed=batch.Results.Length;
                if(batch.Visual is { } v)visualText=$"Match {v.Count(VisualStatuses.Match)}   Mismatch {v.Count(VisualStatuses.Mismatch)}   Missing {v.Count(VisualStatuses.BaselineMissing)}"
                    +(v.Entries.Count(e=>e.Status is VisualStatuses.BaselineInvalid or VisualStatuses.NormalizationError or VisualStatuses.NotCaptured) is int other and >0?$"   Other {other}":"");
                int unclean=batch.Results.Count(r=>(r.TargetExited||r.Checkpoints.Length>0)&&!r.CleanupSucceeded);
                latestEvent=unclean==0?"Cleanup finished; no owned target is running. F6 shows the results.":$"! {unclean} execution(s) reported incomplete cleanup; see Results.";
                runState=cancelRequested?RunState.Cancelled:RunState.Finished;
                Status(cancelRequested?$"Run cancelled ({completed-cancelled} of {total} completed): {Counts(batch)}. Completed artifacts are kept. F6 shows the results."
                    :$"Run finished: {ResultText.RunVerdict(batch)} ({Counts(batch)}). F6 shows the results.");   // the Results header verdict
                UpdateMonitor();
                resultsWindow?.Reload();
                scenarios.DrawView(); UpdateHeader();
            });
        });
        UpdateMonitor();
    }

    private void ShowMonitor()
    {
        // The monitor opens without taking keyboard focus: the user keeps browsing the window that was active.
        var focus=desktop.current;
        if(runningWindow is null)
        {
            runningWindow=new(desktop,()=>busy&&activeRequest is not null,RequestCancel,notifications,WorkbenchPreferences.Load(preferences.StoragePath).Window("monitor"));
            var monitor=runningWindow;
            runningWindow.Closed=()=>{runningWindow=null;RememberWindow("monitor",monitor);};
            desktop.Insert(runningWindow);
        }
        else runningWindow.MakeFirst();
        if(focus is not null&&!ReferenceEquals(focus,runningWindow)&&ReferenceEquals(focus.owner,desktop))desktop.SetCurrent(focus,selectMode.normalSelect);
        runningWindow.Previous=focus is null||ReferenceEquals(focus,runningWindow)?this:focus;
    }

    private void UpdateMonitor()
    {
        if(activeRequest is null&&runState is RunState.Starting or RunState.Running or RunState.Cancelling)return;
        var p=(activeRequest??lastRequest)?.Project; if(p is null)return;
        int scenariosCount=total/Math.Max(1,p.ScreenSizes.Length);
        runningWindow?.Update(new(runState,$"{RunLabel(p)} | {scenariosCount} scenario{(scenariosCount==1?"":"s")} x {p.ScreenSizes.Length} size{(p.ScreenSizes.Length==1?"":"s")}",
            completed,total,passed,failed,blocked,cancelled,currentExecution,visualText,
            $"{RunDriver(p)}  |  Sizes {string.Join(", ",p.ScreenSizes)}",latestEvent,activeRunDirectory){Skipped=skipped});
    }

    // Cancel run (Run Monitor, Test menu, Tests Are Running): one request; the run reaches Cancelled after target cleanup.
    private void RequestCancel()
    {
        if(!busy||activeRequest is null){Error("No run is active.");return;}
        if(cancelRequested){Status("Cancellation is already in progress; waiting for the owned target to close.");return;}
        cancelRequested=true;runState=RunState.Cancelling;latestEvent="Cancellation requested; waiting for the owned target cleanup.";
        cancellation?.Cancel();
        if (activeMatrix is not null) { matrixProgress = "Cancelling; waiting for owned-process cleanup."; ReloadRunExplorer(); }
        Status("Cancelling the run; waiting for the owned target to close. Completed executions are kept.");
        UpdateMonitor();UpdateHeader();
    }

    // Tests Are Running (): Keep running is the default; Esc and the close icon keep running. Choosing
    // cancellation cancels, waits for the owned target cleanup without blocking the event loop, then continues.
    private void ConfirmLeavingRun(string verb, Action proceed)
    {
        if(cancelRequested){afterRun=proceed;Status($"Cancellation is in progress; the Workbench will {verb} after the owned target closes.");return;}
        int w=Math.Min(72,desktop.size.x-2),h=10;
        string action=verb=="exit"?"Exiting":"Continuing";
        using var dialog=new TDialog(new(0,0,w,h),RunningTitle){helpCtx=WorkbenchApp.DialogContext};dialog.options|=Views.ofCentered;
        dialog.Insert(new TStaticText(new(3,2,w-3,6),$"A run is in progress ({completed-cancelled} of {total} finished).\n{action} cancels it, waits for the target to close, then {(verb=="exit"?"exits":"continues")}.\nFinished executions stay in the run folder."));
        var keep=new TButton(new(3,h-3,22,h-1),"~K~eep running",Views.cmCancel,ButtonConstants.bfDefault);dialog.Insert(keep);
        dialog.Insert(new TButton(new(24,h-3,24+(verb=="exit"?23:27),h-1),verb=="exit"?"Cancel run and e~x~it":"Cancel run and ~c~ontinue",Views.cmYes,ButtonConstants.bfNormal));
        keep.Select();
        if(desktop.ExecView(dialog)!=Views.cmYes){Status("The run continues.");return;}
        if(!busy){proceed();return;}
        afterRun=proceed;RequestCancel();
    }
    internal const string RunningTitle="Tests Are Running";

    // Run Ended Unexpectedly (): a runner failure, not a test failure. Plain text; details in Messages.
    private void RunEndedUnexpectedly(Exception ex)
    {
        runState=RunState.EndedUnexpectedly;
        string folder=Directory.Exists(activeRunDirectory)?Path.GetFileName(activeRunDirectory):"";
        int done=completed-cancelled;
        latestEvent="! The run ended unexpectedly. Details in Messages (Alt+6).";
        UpdateMonitor();
        string kept=folder.Length==0?"No run folder was created; nothing was kept.":$"{done} finished execution{(done==1?"":"s")} in {folder}.";
        string message=$"The run stopped before its results were recorded.\n\nCause     {Plain(ex)}\nKept      {kept}\nTarget    The runner has returned; no owned target process is left running.\n\nNext      Fix the cause (F2 if the target changed), then run again.\n          Run Explorer > Open run... shows the kept executions.";
        var notice=notifications.Error("Run ended unexpectedly: "+Plain(ex),ex.ToString());
        int w=Math.Min(84,desktop.size.x-2);
        var lines=WorkbenchText.Wrap(message+"\n          Full technical details are in the Messages window.",w-6).ToArray();
        int h=Math.Min(Math.Min(20,desktop.size.y),lines.Length+6);
        using var dialog=new TDialog(new(0,0,w,h),EndedTitle){helpCtx=WorkbenchApp.DialogContext};dialog.options|=Views.ofCentered;
        dialog.Insert(new TStaticText(new(3,2,w-3,h-3),string.Join('\n',lines)));
        var ok=new TButton(new(3,h-3,15,h-1),"OK",Views.cmOK,ButtonConstants.bfDefault);dialog.Insert(ok);
        dialog.Insert(new TButton(new(16,h-3,35,h-1),"Show ~m~essages",Views.cmYes,ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(36,h-3,57,h-1),"~R~efresh scenarios",Views.cmNo,ButtonConstants.bfNormal));
        ok.Select();
        ushort choice=desktop.ExecView(dialog);
        if(choice==Views.cmYes)ShowMessagesFor?.Invoke(notice);
        else if(choice==Views.cmNo)Post(Load);
    }
    internal const string EndedTitle="Run Ended Unexpectedly";
    // A plain one-sentence cause, without exception type names or stack traces.
    private static string Plain(Exception ex)
    {
        string text=(ex is AggregateException {InnerException:{} inner}?inner:ex).Message.ReplaceLineEndings(" ").Trim();
        return text.Length==0?"The runner reported an internal error.":text.EndsWith('.')?text:text+".";
    }

    private void RunProgress(string message)
    {
        TrackProgress(message);
        if (message.StartsWith("Running ",StringComparison.Ordinal))
        {
            currentExecution = message["Running ".Length..].Replace(" / "," at ");
            if(runState==RunState.Starting)runState=RunState.Running;
        }
        else if (message.StartsWith("  checkpoint ",StringComparison.Ordinal)) latestEvent = currentExecution+": "+message.Trim();
        if (new[] { "Passed", "Failed", "Error", "TimedOut", "Blocked", "Aborted", "Skipped" }.Any(outcome => message.StartsWith(outcome + ":", StringComparison.Ordinal)))
        {
            completed++;
            // Aborted (cancelled) executions are not failures; genuine failures before cancellation stay failures.
            if (message.StartsWith("Passed:")) passed++;
            else if (message.StartsWith("Blocked:")) blocked++;
            else if (message.StartsWith("Aborted:")) cancelled++;
            else if (message.StartsWith("Skipped:")) skipped++;
            else failed++;
            latestEvent = message.StartsWith("Passed:") ? message : message.Split(':')[0].ToUpperInvariant()+message[message.IndexOf(':')..];
        }
        UpdateMonitor();
        resultsWindow?.Reload();
        UpdateHeader();
        notifications.Progress((cancelRequested?$"Cancelling: {completed-cancelled} of {total} completed. ":$"Running {Math.Min(completed + 1, total)} of {total}: ")
            +$"{passed} passed, {failed} failed, {blocked} blocked, " + (skipped > 0 ? $"{skipped} skipped, " : "") + $"{cancelled} cancelled. {currentExecution}");
    }
    // Skipped is its own category: never failed, blocked or cancelled.
    internal static (int Passed,int Failed,int Blocked,int Cancelled) Categories(BatchResult batch) =>
        (batch.Results.Count(r => r.Outcome == ScenarioOutcomes.Passed),
         batch.Results.Count(r => r.Outcome is not (ScenarioOutcomes.Passed or ScenarioOutcomes.Blocked or ScenarioOutcomes.Aborted or ScenarioOutcomes.Skipped)),
         batch.Results.Count(r => r.Outcome == ScenarioOutcomes.Blocked), batch.Results.Count(r => r.Outcome == ScenarioOutcomes.Aborted));
    internal static int Skipped(BatchResult batch) => batch.Results.Count(r => r.Outcome == ScenarioOutcomes.Skipped);
    internal static string Counts(BatchResult batch)
    {
        var (p,f,b,c)=Categories(batch); int s=Skipped(batch);
        return $"{p} passed / {f} failed / {b} blocked" + (s > 0 ? $" / {s} skipped" : "") + (c > 0 ? $" / {c} cancelled" : "");
    }

    private void StartWork(Func<CancellationToken, Task> action)
    {
        busy = true; cancellation = new(); UpdateSelection();
        CancellationToken token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            try { await action(token); }
            catch (OperationCanceledException)
            {
                Deliver(() =>
                {
                    if (activeRequest is not null) { runState = RunState.Cancelled; latestEvent = "Cancelled; owned target cleanup finished."; UpdateMonitor(); }
                    Status("Cancelled; owned target cleanup finished. Marks are kept.");
                });
            }
            catch (Exception ex)
            {
                Deliver(() => Error("The operation ended unexpectedly: " + ex.Message + " Details in Messages (Alt+6).", ex.ToString()));
            }
            finally
            {
                Deliver(() =>
                {
                    if (activeMatrix is not null) { activeMatrix = null; matrixProgress = cancelRequested ? "Run cancelled after cleanup; see Messages for retained evidence." : "Run ended unexpectedly; see Messages for retained evidence."; }
                    busy = false; ReloadRunExplorer(); lastRequest = activeRequest ?? lastRequest; activeRequest = null; cancellation?.Dispose(); cancellation = null; UpdateSelection(); UpdateHeader();
                    if (quitRequested) { quitRequested = false; SavePreferences(); QuitReady?.Invoke(); }
                    // A project operation confirmed in Tests Are Running continues only now, after the owned cleanup.
                    if (afterRun is { } next) { afterRun = null; next(); }
                });
            }
        });
    }

    // "Cancelled (N of M completed)" when executions were aborted by cancellation; aborted ones never count as failed.
    internal static string Completion(BatchResult batch)
    {
        int aborted = Categories(batch).Cancelled;
        return aborted == 0 ? $"{batch.Results.Length} completed" : $"Cancelled ({batch.Results.Length - aborted} of {batch.Results.Length} completed)";
    }
    internal static IEnumerable<string> ProxyDetails(BatchResult batch, ScenarioResult result)
    {
        if (result.Checkpoints.Length == 0) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(SafeArtifact(batch.RunRoot,result.Checkpoints[0].Metadata)));
            var root=doc.RootElement;
            if (!root.TryGetProperty("proxy",out var proxy) || proxy.ValueKind!=System.Text.Json.JsonValueKind.Object) return [];
            var details=new List<string> {
                "Proxy requested: "+proxy.GetProperty("requestedBackend").GetString(),
                "Proxy actual: "+proxy.GetProperty("actualBackend").GetString(),
                $"Actual grid: {root.GetProperty("width")}x{root.GetProperty("height")}",
                "Logical keys/mouse; accepted-queue ACK; native evidence separately classified" };
            if(root.TryGetProperty("console",out var console) && console.ValueKind==System.Text.Json.JsonValueKind.Object)
            {
                details.Add("Console host: "+console.GetProperty("hostMode").GetString());
                details.Add($"Native buffer: {console.GetProperty("bufferWidth")}x{console.GetProperty("bufferHeight")}; window: {console.GetProperty("windowWidth")}x{console.GetProperty("windowHeight")}");
                details.Add("Supported: initial grid, runtime resize, idle wait, logical checkpoints");
            }
            if(root.TryGetProperty("proxyNative",out var native) && native.ValueKind==System.Text.Json.JsonValueKind.Object)
            {
                details.Add("Native evidence: "+native.GetProperty("kind").GetString());
                details.Add("Native artifact: "+native.GetProperty("artifact").GetString());
                foreach(var property in native.GetProperty("details").EnumerateObject()) details.Add(property.Name+": "+property.Value.GetString());
            }
            return details;
        }
        catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {return ["Proxy metadata unavailable: "+ex.Message];}
    }

    internal static IEnumerable<string> NativeDetails(BatchResult batch, ScenarioResult result)
    {
        if (batch.Driver == "NullDriver" || result.Checkpoints.Length == 0) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(SafeArtifact(batch.RunRoot,result.Checkpoints[0].Metadata)));
            var native = doc.RootElement.GetProperty("native");
            return new[] { "driver", "backend", "gpu.device.name", "gpu.adapterKind", "renderingPath", "pathSelection", "captureBoundary", "processId", "windowId" }
                .Where(key=>native.TryGetProperty(key,out _)).Select(key=>key+": "+native.GetProperty(key).GetString()).ToArray();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        { return ["Native metadata unavailable: "+ex.Message]; }
    }

    private int Browse(string title, IEnumerable<string> lines, bool wrap = true, string? acceptLabel = null)
    {
        int w = Math.Min(100, desktop.size.x - 4), h = Math.Min(22, desktop.size.y);
        using var dialog = new TDialog(new TRect(0, 0, w, h), title); dialog.options |= Views.ofCentered;
        dialog.helpCtx = 0x7dc4;
        var scroll = new TScrollBar(new TRect(w - 3, 2, w - 2, h - 4)); dialog.Insert(scroll);
        var list = new TListBox(new TRect(2, 2, w - 3, h - 4), 1, scroll); dialog.Insert(list);
        var collection = new TStringCollection();
        foreach (string line in lines)
        {
            if (!wrap) collection.Insert(line);
            else for (int i = 0; i < Math.Max(1, line.Length); i += w - 7) collection.Insert(line.Substring(i, Math.Min(w - 7, line.Length - i)));
        }
        list.NewList(collection);
        dialog.Insert(new TButton(new TRect(w - 15, h - 3, w - 3, h - 1), acceptLabel ?? (wrap ? "Close" : "~D~etails"), Views.cmOK, ButtonConstants.bfDefault));
        list.Select();
        return desktop.ExecView(dialog) == Views.cmOK ? list.focused : -1;
    }
}
