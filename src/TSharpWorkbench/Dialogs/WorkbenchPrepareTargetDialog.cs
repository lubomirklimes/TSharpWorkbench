using TSharpVision;
using TSharpVision.Constants;

// The two ways of making a target testable share one mode chooser, one summary and one engine
// (TargetPreparationService: inspection, plan, staged/hashed execution with rollback):
//  * Prepare a copy (default): this dialog. The application is copied to its own folder with the Workbench support files;
//    the original folder is only read. The destination is always visible.
//  * Fix the original target (in place): WorkbenchFixIssuesDialog. Only absent Workbench support files are added (an
//    earlier Workbench build is replaced only after approval, with a verified backup); explicit confirmation first.
// The chooser sits at the same place in both windows; choosing the other mode closes this window with SwitchCommand and
// the Workspace opens the other one for the same executable (the two lists of files are too different to share one frame:
// the copy lists the whole application, the fix lists each diagnosed problem with its source, identity and permission).
internal static class PreparationModes
{
    internal const ushort SwitchCommand = 0x7d3c;
    internal const int Copy = 0, Fix = 1;
    internal static TRadioButtons Insert(TDialog dialog, int mode)
    {
        var chooser = new TRadioButtons(new(3, 1, dialog.size.x - 3, 2), RunFields.Items("Prepare a ~c~opy (recommended)", "Fix the original ~t~arget (in place)"))
        { value = (uint)mode };
        dialog.Insert(chooser);
        return chooser;
    }
    // Short enough for the 112-column frame at 120x36 (never cut).
    internal static string Note(int mode) => mode == Fix
        ? "! Changes the ORIGINAL application folder: adds only missing Workbench files, asks first, keeps backups."
        : "Copies the application and the testing components to the destination below; the original is only read.";
}

// Prepare Target in the "Prepare a copy" mode. The summary comes first (outcome, next action,
// destination, counts by category, size); every file is listed under Show files (Preparation Files). Execution only on
// Prepare. Esc, Close and Cancel never change anything.
internal sealed class WorkbenchPrepareTargetDialog : TDialog
{
    internal const string DialogTitle = "Prepare Target", FilesTitle = "Preparation Files";
    internal const ushort ShowFilesCommand = 0x7d50, UseExistingCommand = 0x7d51, PrepareCommand = 0x7d52, BrowseCommand = 0x7d59, NewFolderCommand = 0x7d5a;
    private readonly TDeskTop desktop;
    private readonly TargetPreparationService service;
    private PreparationInspection inspection;
    private readonly Line status, modeNote, outcome, next, destinationState, destinationFull, files, debug, notice;
    private readonly Line[] componentRows;
    private readonly Line notes;
    private readonly Dictionary<string, PreparationPlan> plans = [];
    private string lastResult = "";
    // The destination folder. The field shows the proposed folder in the prepared-targets store until the
    // user types, browses or creates another one; checking it reads the file system only.
    private string shownProposed = "", checkedText = "";
    private bool custom;
    internal TInputLine DestinationField { get; }
    internal TButton BrowseButton { get; }
    internal TButton NewFolderButton { get; }
    internal DestinationState Destination { get; private set; } = null!;
    // The folder the user chose (null while the proposed one is shown): kept across mode switches and reopens.
    internal string? ChosenDestination => custom ? DestinationField.Data.Trim() : null;
    internal string DestinationText => string.Join("\n", destinationState.Current, destinationFull.Current);
    // Test seams: the folder picker and the New folder dialog (default: the real dialogs).
    internal Func<string, string?>? Browse { get; init; }
    internal Func<string, string, string?>? CreateFolder { get; init; }
    // The mode chooser (0 = Prepare a copy; choosing 1 hands over to Fix the original target).
    internal TRadioButtons Method { get; }
    internal TCheckBoxes Components { get; }
    internal TButton FilesButton { get; }
    internal TButton UseExistingButton { get; }
    internal TButton PrepareButton { get; }
    internal PreparationPlan Plan { get; private set; } = null!;
    internal PlanSummary Summary { get; private set; } = null!;
    internal PreparationResult? Result { get; private set; }
    internal PreparationInspection Inspection => inspection;
    internal string Text => string.Join("\n", new[] { modeNote, status, destinationState, destinationFull }.Concat(componentRows).Concat([outcome, next, files, debug, notes, notice]).Select(l => l.Current));
    internal string NotesText => notes.Current;
    internal string NoticeText => notice.Current;
    internal string OutcomeText => outcome.Current;
    internal Func<WorkbenchPreparationProgressDialog, ushort>? RunProgress { get; init; }

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = string.Join("\n", Text.Split('\n').Take(Math.Max(1, size.y)).Select(l => WorkbenchText.Fit(l, size.x)));
    }

    // The same frame as Fix Issues (112 x 32, limited to the desktop), so the mode chooser stays in place when switching.
    internal static TRect Bounds(TDeskTop desktop) => new(0, 0, Math.Min(112, desktop.size.x - 2), Math.Min(32, desktop.size.y));

    // destination: the folder chosen earlier (a mode switch or an earlier preparation); null proposes one.
    internal WorkbenchPrepareTargetDialog(TDeskTop desktop, TargetPreparationService service, PreparationInspection inspection, PreparationCapabilities? capabilities = null,
        string? destination = null)
        : base(Bounds(desktop), DialogTitle)
    {
        this.desktop = desktop; this.service = service; this.inspection = inspection;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y, value = 15, right = 52;
        void Label(int x, int y, string text) => Insert(new TStaticText(new(x, y, x + text.Length + 1, y + 1), text));
        Line Row(int x, int y, int x1, int rows = 1) { var line = new Line(new(x, y, x1, y + rows)); Insert(line); return line; }

        // Insertion order is the Tab order: mode, destination, Browse, New folder, components, Show files, Use existing,
        // Prepare, Cancel.
        Method = PreparationModes.Insert(this, PreparationModes.Copy);
        modeNote = Row(3, 2, w - 2); modeNote.Set(PreparationModes.Note(PreparationModes.Copy));
        string folder = Path.GetDirectoryName(inspection.Executable) ?? "", name = Path.GetFileName(inspection.Executable) + (inspection.IsPreparedCopy ? " (prepared copy)" : "");
        Label(2, 4, "Target"); Row(value, 4, w - 2).Set(name + "   in " + WorkbenchText.Path(folder, Math.Max(10, w - value - 2 - name.Length - 6)));
        Label(2, 5, "Status"); status = Row(value, 5, w - 2);

        // The destination folder, prominent and editable (the field scrolls; the full path is shown below).
        DestinationField = new TInputLine(new(value, 7, w - 35, 8), 32767); Insert(DestinationField);
        Insert(new TLabel(new(1, 7, value - 1, 8), "D~e~stination", DestinationField));
        BrowseButton = new(new(w - 34, 7, w - 20, 9), "~B~rowse...", BrowseCommand, ButtonConstants.bfNormal); Insert(BrowseButton);
        NewFolderButton = new(new(w - 19, 7, w - 2, 9), "~N~ew folder...", NewFolderCommand, ButtonConstants.bfNormal); Insert(NewFolderButton);
        destinationState = Row(value, 8, w - 35);
        Label(2, 9, "Full path"); destinationFull = Row(value, 9, w - 2, 2);
        if (destination is { Length: > 0 }) { DestinationField.SetData(destination); custom = true; }

        // One optional display. Proxy, Testing and the SDL driver with its native libraries are always
        // deployed (the Proxy loads the SDL driver on every display driver), so SDL is not an option.
        Components = new(new(3, 13, right - 1, 14), RunFields.Items("Windows c~o~nsole support"));
        var wanted = capabilities ?? TargetPreparationService.DefaultCapabilities;
        Components.value = wanted.HasFlag(PreparationCapabilities.Console) && OperatingSystem.IsWindows() ? 1u : 0u;
        if (!OperatingSystem.IsWindows()) Components.SetButtonState(1, false);
        Insert(Components); Insert(new TLabel(new(2, 12, 40, 13), "Optional test ~d~isplay", Components));
        Row(right, 13, w - 2, 2).Set("Proxy, Testing and SDL (window and GPU)\nare always deployed.");
        componentRows = [Row(3, 15, w - 2), Row(3, 16, w - 2), Row(3, 17, w - 2), Row(3, 18, w - 2)];
        Label(2, 20, "Outcome"); outcome = Row(value, 20, w - 2);
        Label(2, 21, "Next"); next = Row(value, 21, w - 2, 2);
        Label(2, 23, "Files"); files = Row(value, 23, w - 2);
        Label(2, 24, "Debug docs"); debug = Row(value, 24, w - 2);
        notes = Row(2, 25, w - 2, Math.Max(1, h - 30));
        notice = Row(2, h - 5, w - 2);
        // Owner spacing rule: the notice line, one empty row, then the buttons above the bottom frame.
        FilesButton = new(new(2, h - 3, 20, h - 1), "Show ~f~iles...", ShowFilesCommand, ButtonConstants.bfNormal); Insert(FilesButton);
        UseExistingButton = new(new(21, h - 3, 39, h - 1), "~U~se existing", UseExistingCommand, ButtonConstants.bfNormal); Insert(UseExistingButton);
        PrepareButton = new(new(w - 31, h - 3, w - 17, h - 1), "~P~repare", PrepareCommand, ButtonConstants.bfNormal); Insert(PrepareButton);
        Insert(new TButton(new(w - 16, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        Refresh();
        Method.Select();
    }

    // This dialog always prepares a copy; the in-place mode is Fix the original target (one in-place policy, not two).
    internal PreparationStrategy Strategy => PreparationStrategy.PreparedCopy;
    internal PreparationCapabilities Capabilities => PreparationCapabilities.Proxy | PreparationCapabilities.Sdl
        | ((Components.value & 1) != 0 && OperatingSystem.IsWindows() ? PreparationCapabilities.Console : 0);

    private PreparationPlan Current()
    {
        string key = Strategy + "|" + Capabilities;
        if (!plans.TryGetValue(key, out var plan)) plans[key] = plan = service.CreatePlan(inspection, Strategy, Capabilities);
        return plan;
    }

    // The plan Prepare executes and Show files lists: with the chosen destination (planned again only when it changes).
    private PreparationPlan Executable()
    {
        if (ChosenDestination is not { } chosen) return Plan;
        string key = Strategy + "|" + Capabilities + "|" + chosen;
        if (!plans.TryGetValue(key, out var plan)) plans[key] = plan = service.CreatePlan(inspection, Strategy, Capabilities, chosen);
        return plan;
    }

    // The destination state, re-checked when the field changes (cheap: no plan, no copy, nothing created).
    private void CheckDestination()
    {
        string text = DestinationField.Data.Trim();
        // The field follows the proposed folder until the user changes it.
        if (!custom && shownProposed.Length > 0 && text != shownProposed) custom = true;
        if (!custom && shownProposed != Plan.Destination) { shownProposed = Plan.Destination; DestinationField.SetData(shownProposed); DestinationField.DrawView(); text = shownProposed; }
        string key = (custom ? "c|" : "p|") + text;
        if (key == checkedText) return;
        checkedText = key;
        Destination = PreparationDestination.Check(custom ? text : null, inspection, service, Plan.Destination);
        destinationFree = service.FreeSpace(Destination.Path);
        destinationState.Set((Destination.Usable ? "" : "! ") + char.ToUpperInvariant(Destination.Label[0]) + Destination.Label[1..]
            + (Destination.Usable && destinationFree is { } free ? $"   {Preparation.Size(free)} free" : ""));
        // The full path (wrapped, never elided), then what the folder is, as far as two rows allow.
        var rows = WorkbenchText.Wrap(Destination.Path, destinationFull.size.x).ToList();
        if (rows.Count < destinationFull.size.y) rows.AddRange(WorkbenchText.Wrap(Destination.Message, destinationFull.size.x));
        destinationFull.Set(string.Join("\n", rows.Take(destinationFull.size.y)));
    }
    private long? destinationFree;

    private void Refresh()
    {
        // Broadcasts arrive while the constructor inserts controls.
        if (componentRows is null || notice is null || PrepareButton is null || UseExistingButton is null || FilesButton is null || NewFolderButton is null) return;
        Plan = Current();
        CheckDestination();
        Summary = PreparationVerdict.For(Plan, service.Payload?.FrameworkVersion);
        var i = inspection;
        status.Set(i.Prepared is { } prepared ? prepared.Summary
            : i.Unsupported is not null ? "Cannot be prepared - see below"
            : i.Report.Findings.FirstOrDefault(f => f.Component == "Proxy" && f.Severity != DiagnosticSeverity.Ok) is { } proxy ? $"{proxy.Short} ({proxy.Code}): {proxy.Message}"
            : "Proxy deployed beside the original (static checks); a prepared copy is optional");
        string[] order = ["Proxy and Testing", "Framework", "SDL window/GPU", "Windows console"];
        for (int r = 0; r < componentRows.Length; r++)
        {
            var c = Plan.Components.FirstOrDefault(x => x.Name == order[r]);
            componentRows[r].Set(c is null ? $"{order[r],-20} {(i.Unsupported is not null ? "-" : "not inspected")}" : $"{c.Name,-20} {c.State,-24} {c.Detail}");
        }
        outcome.Set(Summary.Outcome);
        next.Set(string.Join("\n", WorkbenchText.Wrap(Summary.Next, next.size.x).Take(next.size.y)));
        files.Set(Plan.Entries.Length == 0 && !Plan.CanExecute ? "-"
            : Summary.Counts + $"   {Preparation.Size(Plan.RequiredBytes)}");
        debug.Set(Plan.Entries.Length == 0 ? "-" : Summary.DebugDocs);
        // Notes: the result of the last attempt, then each problem (the outcome above names its kind), then warnings.
        var lines = new List<string>();
        if (lastResult.Length > 0) lines.Add(lastResult);
        lines.AddRange(Plan.Problems.Where(p => p != Summary.Next).Select(p => "! " + p));
        lines.AddRange(Plan.Warnings);
        if (Plan.CanExecute && lines.Count == 0) lines.AddRange(Plan.Compatibility.Take(1));
        notes.Set(string.Join("\n", lines.SelectMany(l => WorkbenchText.Wrap(l, notes.size.x)).Take(notes.size.y)));
        bool nothing = Plan.CanExecute && !Plan.Changes.Any();
        notice.Set(!Plan.CanExecute ? "Nothing can be prepared until the problems above are solved. Cancel leaves everything unchanged."
            : !Destination.Usable ? "Choose a new or empty destination folder (Browse... or New folder...). Nothing is ever overwritten."
            : nothing ? "Every selected component is already deployed: nothing to prepare."
            : "Preparation does not modify the original application. Then you choose whether to use the copy.");
        Available(PrepareCommand, PrepareButton, Plan.CanExecute && !nothing && Destination.Usable);
        Available(UseExistingCommand, UseExistingButton, inspection.Existing is { State: PreparedState.Ready } && !inspection.IsPreparedCopy);
        Available(ShowFilesCommand, FilesButton, Plan.Entries.Length > 0);
    }
    private void Available(ushort command, TButton button, bool on)
    {
        if (on) EnableCommand(command); else DisableCommand(command);
        if (button.GetState(Views.sfDisabled) == !on) return;
        button.SetState(Views.sfDisabled, !on); button.DrawView();
    }
    public override void ShutDown() { EnableCommand(PrepareCommand); EnableCommand(UseExistingCommand); EnableCommand(ShowFilesCommand); EnableCommand(BrowseCommand); EnableCommand(NewFolderCommand); base.ShutDown(); }

    internal void ShowFiles() => ShowLines(desktop, FilesTitle, Executable().FileLines().ToArray());

    // Browse... picks an existing folder (the framework directory tree; it creates nothing).
    private void BrowseDestination()
    {
        string start = PreparationDestination.ExistingAncestor(Destination.Path is { Length: > 0 } p && Path.IsPathFullyQualified(p) ? p : service.Root)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? picked;
        if (Browse is { } browse) picked = browse(start);
        else
        {
            using var picker = new WorkbenchDirectoryDialog(start, "Select destination folder") { helpCtx = WorkbenchApp.DialogContext };
            picked = desktop.ExecView(picker) == Views.cmOK && picker.SelectedPath.Length > 0 ? picker.SelectedPath : null;
        }
        if (picked is null) return;
        SetDestination(picked);
    }

    // New folder...: a focused dialog that asks before creating (inside the destination when it exists, else inside its
    // nearest existing parent). Cancel creates nothing.
    private void NewDestinationFolder()
    {
        string parent = Directory.Exists(Destination.Path) ? Destination.Path : PreparationDestination.ExistingAncestor(Destination.Path) ?? service.Root;
        string suggestion = Path.GetFileNameWithoutExtension(inspection.Source) + "-prepared";
        if (Directory.Exists(Path.Combine(parent, suggestion))) suggestion = Path.GetFileName(PreparationDestination.NextFree(Path.Combine(parent, suggestion)));
        string? created = CreateFolder is { } create ? create(parent, suggestion) : WorkbenchNewFolderDialog.Run(desktop, parent, suggestion, inspection, service);
        if (created is null) return;
        SetDestination(created);
    }
    internal void SetDestination(string folder)
    {
        custom = true;
        DestinationField.SetData(folder); DestinationField.DrawView();
        Refresh();
    }

    private void Prepare()
    {
        if (!Destination.Usable) return;
        var plan = Executable();
        if (!plan.CanExecute) { lastResult = "! " + plan.Problems[0]; Refresh(); return; }
        using var progress = new WorkbenchPreparationProgressDialog(desktop, plan, (report, token) => service.ExecuteAsync(plan, report, token));
        (RunProgress ?? (d => desktop.ExecView(d)))(progress);
        var result = progress.Result;
        if (result is { Success: true }) { Result = result; EndModal(Views.cmOK); return; }
        // Failed or cancelled: the dialog stays open with the outcome and a fresh plan (nothing is presented as ready).
        lastResult = result is null ? "Preparation did not start." : (result.Cancelled ? "" : "! ") + result.Message;
        plans.Clear(); checkedText = "";
        inspection = service.Inspect(inspection.Executable);
        Refresh();
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && (state & Views.sfModal) != 0)
            switch (ev.message.command)
            {
                case ShowFilesCommand: ClearEvent(ref ev); ShowFiles(); return;
                case UseExistingCommand: ClearEvent(ref ev); EndModal(UseExistingCommand); return;
                case PrepareCommand: ClearEvent(ref ev); Prepare(); return;
                case BrowseCommand: ClearEvent(ref ev); BrowseDestination(); return;
                case NewFolderCommand: ClearEvent(ref ev); NewDestinationFolder(); return;
            }
        base.HandleEvent(ref ev);
        // The other mode: hand over to Fix the original target (nothing has changed here).
        if (Method is not null && Method.value == PreparationModes.Fix && (state & Views.sfModal) != 0) { EndModal(PreparationModes.SwitchCommand); return; }
        Refresh();
    }

    // ── focused helpers ───────────────────────────────────────────────────────────────

    // The full file list (read-only).
    internal static void ShowLines(TDeskTop desktop, string title, string[] lines)
    {
        int w = Math.Min(112, desktop.size.x - 2), h = Math.Min(30, desktop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), title) { helpCtx = WorkbenchApp.DialogContext };
        dialog.options |= Views.ofCentered;
        var scroll = new TScrollBar(new(w - 3, 1, w - 2, h - 4)); dialog.Insert(scroll);
        var hscroll = new TScrollBar(new(2, h - 4, w - 3, h - 3)); dialog.Insert(hscroll);
        var list = new TListBox(new(2, 1, w - 3, h - 4), 1, scroll); dialog.Insert(list);
        var rows = new TStringCollection();
        foreach (string line in lines) rows.Insert(line.Length == 0 ? " " : line);
        list.NewList(rows);
        dialog.Insert(new TButton(new(w - 15, h - 3, w - 3, h - 1), "Close", Views.cmCancel, ButtonConstants.bfDefault));
        list.Select();
        desktop.ExecView(dialog);
    }

    // After a successful prepared copy: whether the project uses it. The affirmative default is used only when the user
    // started preparation from this project's unprepared target; the answer is always explicit.
    // The destination is shown in full and can be opened in the system file manager (the question stays).
    internal const string UseTitle = "Use Prepared Target";
    internal const ushort OpenDestinationCommand = 0x7d5b;
    internal static Action<string> OpenFolder { get; set; } = folder =>
    {
        try
        {
            using var _ = OperatingSystem.IsWindows() ? System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })
                : System.Diagnostics.Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [folder]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    };
    private sealed class UseDialog(TRect bounds, string folder) : TDialog(bounds, UseTitle)
    {
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evCommand && ev.message.command == OpenDestinationCommand) { ClearEvent(ref ev); OpenFolder(folder); return; }
            base.HandleEvent(ref ev);
        }
    }

    internal static ushort ConfirmUse(TDeskTop desktop, string preparedExe, string original, bool defaultYes)
    {
        int w = Math.Min(92, desktop.size.x - 2);
        string destination = Path.GetDirectoryName(preparedExe) ?? preparedExe;
        var lines = new List<string> { "The prepared copy passed validation. Destination folder:" };
        lines.AddRange(WorkbenchText.Wrap(destination, w - 8).Select(l => "  " + l));
        lines.Add("  executable " + Path.GetFileName(preparedExe));
        lines.AddRange(WorkbenchText.Wrap("Use it as this project's executable? The project is marked changed (not saved); scenario files, marks and the "
            + "session Run configuration are kept. The original stays available:", w - 6));
        lines.Add("  " + WorkbenchText.Path(original, w - 8));
        lines.AddRange(WorkbenchText.Wrap("To switch back later, select the original executable in Project Properties (Alt+O).", w - 6));
        int h = Math.Min(desktop.size.y, lines.Count + 6);
        using var dialog = new UseDialog(new(0, 0, w, h), destination) { helpCtx = WorkbenchApp.DialogContext };
        dialog.options |= Views.ofCentered;
        dialog.Insert(new TStaticText(new(2, 1, w - 2, h - 4), string.Join('\n', lines.Select(l => WorkbenchText.Fit(l, w - 4)))));
        var yes = new TButton(new(2, h - 3, 28, h - 1), "~U~se prepared target", Views.cmYes, defaultYes ? ButtonConstants.bfDefault : ButtonConstants.bfNormal);
        var open = new TButton(new(29, h - 3, 51, h - 1), "~O~pen destination", OpenDestinationCommand, ButtonConstants.bfNormal);
        var no = new TButton(new(w - 22, h - 3, w - 3, h - 1), "~K~eep original", Views.cmNo, defaultYes ? ButtonConstants.bfNormal : ButtonConstants.bfDefault);
        dialog.Insert(yes); dialog.Insert(open); dialog.Insert(no);
        (defaultYes ? yes : no).Select();
        ushort result = desktop.ExecView(dialog);
        return result == Views.cmYes ? Views.cmYes : Views.cmNo;
    }
}

// Preparation progress: the real TProgressBar, files and bytes, and Cancel. The work runs off the UI thread; progress
// arrives as posted events (at most one pending), so the event loop stays responsive. Cancel stops the remaining
// copying and waits for the cleanup; the dialog then shows the outcome and Close.
internal sealed class WorkbenchPreparationProgressDialog : TDialog
{
    private const ushort Update = 0x7d53, Finished = 0x7d54;
    private readonly Func<IProgress<PreparationProgress>, CancellationToken, Task<PreparationResult>> run;
    private readonly CancellationTokenSource cancel = new();
    private readonly Line stage, filesLine, bytesLine, outcome;
    private PreparationProgress? latest;
    private int pending;
    private bool started, closeAfter;
    internal TProgressBar Bar { get; }
    internal TButton CancelButton { get; }
    internal TButton CloseButton { get; }
    internal PreparationResult? Result { get; private set; }
    internal bool Running => started && Result is null;
    internal string Text => string.Join("\n", stage.Current, filesLine.Current, bytesLine.Current, outcome.Current);
    // Test seam: cancel automatically when this many files are done.
    internal int? CancelAfterFiles { get; init; }

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = string.Join("\n", Text.Split('\n').Take(Math.Max(1, size.y)).Select(l => WorkbenchText.Fit(l, size.x)));
    }
    private sealed class Done(PreparationResult result) : IInfo { internal PreparationResult Result { get; } = result; }

    internal WorkbenchPreparationProgressDialog(TDeskTop desktop, PreparationPlan plan, Func<IProgress<PreparationProgress>, CancellationToken, Task<PreparationResult>> run)
        : base(new(0, 0, Math.Min(80, desktop.size.x - 2), Math.Min(16, desktop.size.y)), "Preparing " + Path.GetFileNameWithoutExtension(plan.SourceExecutable))
    {
        this.run = run;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y;
        Line Row(int y, int rows = 1) { var line = new Line(new(2, y, w - 2, y + rows)); Insert(line); return line; }
        stage = Row(1);
        Bar = new TProgressBar(new(2, 3, w - 2, 4), 0, 1); Insert(Bar);
        filesLine = Row(5); bytesLine = Row(6);
        outcome = Row(8, Math.Max(1, h - 11));
        CancelButton = new(new(2, h - 3, 14, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfNormal); Insert(CancelButton);
        CloseButton = new(new(w - 15, h - 3, w - 3, h - 1), "Close", Views.cmCancel, ButtonConstants.bfDefault); Insert(CloseButton);
        CloseButton.Hide();
        stage.Set(plan.Strategy == PreparationStrategy.PreparedCopy ? $"Preparing {Path.GetFileNameWithoutExtension(plan.SourceExecutable)} (prepared copy)" : $"Installing into {plan.SourceDirectory}");
        filesLine.Set("Starting...");
        CancelButton.Select();
    }

    public override ushort Execute()
    {
        Start();
        return base.Execute();
    }
    internal void Start()
    {
        if (started) return;
        started = true;
        var progress = new Progress(this);
        var token = cancel.Token;
        _ = Task.Run(async () =>
        {
            PreparationResult result;
            try { result = await run(progress, token); }
            catch (Exception ex) { result = new(false, false, "Preparation failed: " + ex.Message, null, null, [], [ex.ToString()]); }
            Post(Finished, new Done(result));
        });
    }
    private sealed class Progress(WorkbenchPreparationProgressDialog owner) : IProgress<PreparationProgress>
    {
        public void Report(PreparationProgress value)
        {
            owner.latest = value;
            if (owner.CancelAfterFiles is { } n && value.Files >= n) owner.cancel.Cancel();
            if (Interlocked.Exchange(ref owner.pending, 1) == 0) owner.Post(Update, null);
        }
    }

    private void Show(PreparationProgress p)
    {
        if (Result is not null) return;
        stage.Set(p.Phase + (cancel.IsCancellationRequested ? " - cancelling..." : ""));
        Bar.SetRange(0, Math.Max(1, p.TotalFiles));
        // Never 100% before publication and verification succeeded.
        Bar.Value = Math.Min(p.Files, Math.Max(0, p.TotalFiles - 1));
        filesLine.Set($"Copying files: {p.Files} of {p.TotalFiles}");
        bytesLine.Set($"Transferred: {Preparation.Size(p.Bytes)} of {Preparation.Size(p.TotalBytes)}");
    }

    private void Complete(PreparationResult result)
    {
        Result = result;
        if (result.Success) { Bar.SetRange(0, 1); Bar.Value = 1; stage.Set("Prepared and verified"); }
        else stage.Set(result.Cancelled ? "Preparation cancelled" : "Preparation failed");
        outcome.Set(string.Join("\n", WorkbenchText.Wrap((result.Success ? "" : "! ") + result.Message, outcome.size.x)));
        CancelButton.Hide(); CloseButton.Show(); CloseButton.Select();
        if (closeAfter) EndModal(Views.cmCancel);
    }

    internal void RequestCancel()
    {
        if (!Running) return;
        cancel.Cancel();
        stage.Set(stage.Current.Replace(" - cancelling...", "") + " - cancelling...");
        outcome.Set("Cancelling: the remaining copying stops and the partial work is removed...");
    }

    public override bool Valid(ushort command)
    {
        // Leaving while the work runs cancels it and waits for its cleanup; the dialog closes afterwards.
        if (command is Views.cmCancel or Views.cmClose && Running) { closeAfter = command == Views.cmClose; RequestCancel(); return false; }
        return base.Valid(command);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && ev.message.command == Update)
        { ClearEvent(ref ev); Interlocked.Exchange(ref pending, 0); if (latest is { } p) Show(p); return; }
        if (ev.What == Events.evCommand && ev.message.command == Finished && ev.message.infoPtr is Done done) { ClearEvent(ref ev); Complete(done.Result); return; }
        base.HandleEvent(ref ev);
    }

    public override void ShutDown() { cancel.Dispose(); base.ShutDown(); }
}
