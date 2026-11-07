using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// What the Results window needs from the workspace: the run to show and the actions that leave the window.
internal interface IResultsHost
{
    ResultsView View();
    WorkbenchNotifications Notifications { get; }
    ScenarioDescriptor? Descriptor(string id);
    void Navigate(int direction);
    void OpenRun();
    void Rerun(BatchResult batch, ScenarioResult result);
    void Compare(BatchResult batch, ScenarioResult result, CheckpointResult? checkpoint);
    void ReviewCandidate(BatchResult batch, ScenarioResult result, CheckpointResult? checkpoint);
    void OpenPath(string path, bool folder);
}

// One run as Results shows it: header lines, the batch (empty while a run is live) and per-execution durations measured
// by the Workbench for session runs (null for historical runs, whose run.json does not record them).
internal sealed record ResultsView(BatchResult Batch, string[] Header, bool Live, string RunRoot, Func<ScenarioResult, TimeSpan?> Duration, ResultSetup? Setup)
{
    internal string[] Provenance { get; init; } = [];
}

// Results (window 4-§9.12): header, Executions list with separate Result and Visual verdicts, the
// checkpoint list of the selected execution and an inspector (Overview, assertions, evidence rows). Modeless; Esc and
// Close close it; it never starts a run by itself (Re-run opens Run Configuration for review).
internal sealed class WorkbenchResultsDialog : TWindow
{
    internal const ushort Context = 0x7dc5, CloseResults = 0x7dc6;
    // ToggleFailed = All/Problems filter, PreviousRun/NextRun = earlier/later run, Locations = run folder, ResultDetails = inspect.
    internal const ushort ToggleFailed = 0x7dc0, PreviousRun = 0x7dc1, Locations = 0x7dc2, ResultDetails = 0x7dc3;
    internal const ushort NextRun = 0x7db0, OpenImage = 0x7db1, NativeEvidence = 0x7db2, CompareCheckpoint = 0x7db3, ReviewCandidate = 0x7db4,
        CopyPath = 0x7db5, OpenRunFile = 0x7db6, Rerun = 0x7db7;
    internal const string WindowTitle = "Results";
    internal Action? Closed { get; set; }
    // The Results-only commands return to the enabled default when the window closes.
    public override void ShutDown() { foreach (ushort command in SelectionCommands) EnableCommand(command); base.ShutDown(); Closed?.Invoke(); }

    private readonly IResultsHost host;
    private readonly List<(TView View, Func<int, int, TRect> Bounds)> layout = [];
    private readonly Line[] header;
    private readonly Line counts, columns, executionTitle, inspectorTitle;
    private readonly TRadioButtons filter;
    private readonly ExecutionList executions;
    private readonly TextList checkpoints;
    private readonly InspectorList inspector;
    private readonly TButton openImage, native, compare, review, copy, earlier, later, openRun, rerun, folder;
    private ResultsView view;
    private ScenarioResult[] visible = [];
    private (string Text, CheckpointResult? Checkpoint)[] checkpointRows = [];
    private string shownRoot = "\0", selectedKey = "", note = "";
    private int lastExecution = -1, lastCheckpoint = -1;
    private uint lastFilter;
    private bool ready;

    private sealed class Line(TRect bounds, string text = "") : TStaticText(bounds, text)
    {
        internal string Current => Text;
        internal void SetText(string value) { if (Text == value) return; Text = value; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }
    private class TextList(TRect bounds, TScrollBar scroll) : TListBox(bounds, 1, scroll)
    {
        internal string[] Rows = [];
        internal string Empty = "";
        internal void Load(string[] rows, int focus)
        {
            Rows = rows; SetRange(rows.Length);
            FocusItemNum(Math.Clamp(focus, 0, Math.Max(0, rows.Length - 1))); DrawView();
        }
        public override string GetText(int item, int maxChars) =>
            WorkbenchText.Fit(Rows.ElementAtOrDefault(item) ?? (item == 0 && Rows.Length == 0 ? Empty : ""), maxChars);
    }
    private sealed class ExecutionList(TRect bounds, TScrollBar scroll) : TextList(bounds, scroll);
    private sealed class InspectorList(TRect bounds, TScrollBar scroll) : TextList(bounds, scroll)
    {
        internal InspectorRow[] Items = [];
        internal InspectorRow? Current => Items.ElementAtOrDefault(focused);
        internal void Load(IEnumerable<InspectorRow> rows, int width)
        {
            // Long rows wrap; continuation rows keep the row's file so Enter opens it from any of its lines.
            var wrapped = new List<InspectorRow>();
            foreach (var row in rows)
            {
                // Continuation rows are indented, so they wrap at the width minus the indent (never wider than the list).
                int indent = Math.Min(11, width / 3);
                var lines = WorkbenchText.Wrap(row.Text, Math.Max(10, width)).ToArray();
                if (lines.Length > 1) lines = [lines[0], .. WorkbenchText.Wrap(string.Join(" ", lines.Skip(1)), Math.Max(10, width - indent))];
                for (int i = 0; i < lines.Length; i++) wrapped.Add(row with { Text = i == 0 ? lines[i] : new string(' ', indent) + lines[i] });
            }
            int keep = focused;
            Items = wrapped.ToArray();
            Load(Items.Select(r => r.Text).ToArray(), keep < Items.Length ? keep : 0);
        }
    }

    // Layout (): four header rows, the Executions list (9 rows at 140x40, 12 at 160x50), the checkpoint list
    // and inspector, an evidence action row and the navigation row; positions are functions of the window size.
    private static int ListRows(int h) => h >= 34 ? 9 + Math.Max(0, h - 38) * 3 / 10 : Math.Max(3, (h - 16) / 2);
    private static int PaneTop(int h) => 10 + ListRows(h);
    private static int Left(int w) => 2 + Math.Clamp(38 + (w - 140) * 3 / 10, 26, 46);
    private static int Actions(int w) => Math.Max(2, Math.Min(Left(w) + 3, w - 2 - 75));
    private void Place(TView view, Func<int, int, TRect> bounds)
    { view.growMode = 0; layout.Add((view, bounds)); view.ChangeBounds(bounds(size.x, size.y)); Insert(view); }
    private TButton Button(string title, ushort command, Func<int, int, TRect> bounds)
    { var button = new TButton(new(0, 0, 1, 1), title, command, ButtonConstants.bfNormal); Place(button, bounds); return button; }

    internal static TRect DefaultBounds(TDeskTop desktop, TRect? remembered)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        if (remembered is { } r && r.a.x >= 0 && r.a.y >= 0 && r.b.x <= dw && r.b.y <= dh && r.b.x - r.a.x >= Math.Min(100, dw) && r.b.y - r.a.y >= Math.Min(30, dh)) return r;
        return new(0, 0, dw, dh);
    }

    internal WorkbenchResultsDialog(TDeskTop desktop, IResultsHost host, TRect? remembered = null)
        : base(DefaultBounds(desktop, remembered), WindowTitle, 4)
    {
        this.host = host; view = host.View();
        helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        header = new Line[4];
        for (int i = 0; i < 4; i++) { int y = i + 2; header[i] = new(new(0, 0, 1, 1)); Place(header[i], (w, h) => new(2, y, w - 2, y + 1)); }
        // Insertion order is the Tab order: filter, Executions, Checkpoints, Details, evidence actions, navigation.
        filter = new(new(0, 0, 1, 1), new TSItem("~A~ll", new TSItem("Prob~l~ems only", null!)));
        Place(filter, (w, h) => new(15, 6, 45, 7));
        counts = new(new(0, 0, 1, 1)); Place(counts, (w, h) => new(47, 6, w - 2, 7));
        columns = new(new(0, 0, 1, 1)); Place(columns, (w, h) => new(2, 7, w - 3, 8));
        var executionScroll = new TScrollBar(new(0, 0, 1, 1));
        Place(executionScroll, (w, h) => new(w - 3, 8, w - 2, 8 + ListRows(h)));
        executions = new(new(0, 0, 1, 1), executionScroll) { Empty = "No executions." };
        Place(executions, (w, h) => new(2, 8, w - 3, 8 + ListRows(h)));
        Place(new WindowLabel(new(0, 0, 1, 1), "Exec~u~tions", executions), (w, h) => new(1, 6, 14, 7));
        Place(new Line(new(0, 0, 1, 1), new string('─', 400)), (w, h) => new(1, 8 + ListRows(h), w - 1, 9 + ListRows(h)));
        var checkpointScroll = new TScrollBar(new(0, 0, 1, 1));
        Place(checkpointScroll, (w, h) => new(Left(w), PaneTop(h), Left(w) + 1, h - 6));
        checkpoints = new(new(0, 0, 1, 1), checkpointScroll);
        Place(checkpoints, (w, h) => new(2, PaneTop(h), Left(w), h - 6));
        Place(new WindowLabel(new(0, 0, 1, 1), "Chec~k~points", checkpoints), (w, h) => new(1, PaneTop(h) - 1, 14, PaneTop(h)));
        executionTitle = new(new(0, 0, 1, 1)); Place(executionTitle, (w, h) => new(15, PaneTop(h) - 1, Left(w) + 1, PaneTop(h)));
        var inspectorScroll = new TScrollBar(new(0, 0, 1, 1));
        Place(inspectorScroll, (w, h) => new(w - 3, PaneTop(h), w - 2, h - 6));
        inspector = new(new(0, 0, 1, 1), inspectorScroll);
        Place(inspector, (w, h) => new(Left(w) + 3, PaneTop(h), w - 3, h - 6));
        Place(new WindowLabel(new(0, 0, 1, 1), "~D~etails", inspector), (w, h) => new(Left(w) + 2, PaneTop(h) - 1, Left(w) + 11, PaneTop(h)));
        inspectorTitle = new(new(0, 0, 1, 1)); Place(inspectorTitle, (w, h) => new(Left(w) + 12, PaneTop(h) - 1, w - 2, PaneTop(h)));
        openImage = Button("Open ~i~mage", OpenImage, (w, h) => new(Actions(w), h - 5, Actions(w) + 14, h - 3));
        native = Button("~N~ative", NativeEvidence, (w, h) => new(Actions(w) + 15, h - 5, Actions(w) + 25, h - 3));
        compare = Button("~C~ompare", CompareCheckpoint, (w, h) => new(Actions(w) + 26, h - 5, Actions(w) + 37, h - 3));
        review = Button("Re~v~iew candidate...", ReviewCandidate, (w, h) => new(Actions(w) + 38, h - 5, Actions(w) + 61, h - 3));
        copy = Button("Cop~y~ path", CopyPath, (w, h) => new(Actions(w) + 62, h - 5, Actions(w) + 75, h - 3));
        earlier = Button("< ~P~rev", PreviousRun, (w, h) => new(2, h - 3, 11, h - 1));
        later = Button("Nex~t~ >", NextRun, (w, h) => new(12, h - 3, 21, h - 1));
        openRun = Button("~O~pen run...", OpenRunFile, (w, h) => new(22, h - 3, 37, h - 1));
        rerun = Button("R~e~-run...", Rerun, (w, h) => new(38, h - 3, 51, h - 1));
        folder = Button("Run ~f~older", Locations, (w, h) => new(52, h - 3, 66, h - 1));
        Button("Clo~s~e", CloseResults, (w, h) => new(w - 13, h - 3, w - 2, h - 1));
        ready = true;
        Refresh();
        executions.Select();
    }

    // ── state ────────────────────────────────────────────────────────────────────────

    internal ScenarioResult? Selected => view.Live ? null : visible.ElementAtOrDefault(executions.focused);
    internal CheckpointResult? SelectedCheckpoint => checkpointRows.ElementAtOrDefault(checkpoints.focused).Checkpoint;
    internal BatchResult Batch => view.Batch;
    internal bool ProblemsOnly => filter.value == 1;
    internal string HeaderText => string.Join("\n", header.Select(l => l.Current));
    internal string[] ExecutionRows => executions.Rows;
    internal string[] CheckpointRows => checkpoints.Rows;
    internal string[] InspectorRows => inspector.Rows;
    internal string InspectorTitle => inspectorTitle.Current;
    internal string ColumnHeader => columns.Current;
    internal TListBox ExecutionsList => executions;
    internal TListBox CheckpointList => checkpoints;
    internal TListBox Inspector => inspector;
    internal TRadioButtons Filter => filter;
    internal TButton[] ActionButtons => [openImage, native, compare, review, copy];
    internal TButton[] NavigationButtons => [earlier, later, openRun, rerun, folder];

    internal void Reload() => Refresh();
    // A one-line inline status (rejected action, approval) shown at the top of the inspector until the selection changes.
    internal void SetNote(string text) { note = text; UpdateInspector(); }

    // Execution list row: marker, Result, Visual, Grid, Scenario (trailing ellipsis; full ID in the details), Time.
    private string Row(ScenarioResult r, int width)
    {
        int scenario = Math.Max(22, width - 54);
        var d = view.Duration(r);
        return $" {ResultText.Marker(r)}  {ResultText.Execution(r),-18}{ResultText.VisualColumn(ResultText.Visual(view.Batch, r)),-14}{r.Size,-9}"
            + WorkbenchText.Column(r.ScenarioId, scenario) + " " + (d is { } t ? $"{t.TotalSeconds:F1} s" : "-").PadLeft(7);
    }
    // Skipped (platform/driver unsupported) is listed under All, never as a problem.
    internal static bool IsFailure(BatchResult batch, ScenarioResult r) => r.Outcome is not (ScenarioOutcomes.Passed or ScenarioOutcomes.Skipped)
        || (batch.Visual?.Entries.Any(e => e.ScenarioId == r.ScenarioId && e.Grid == r.Size && VisualStatuses.Failing.Contains(e.Status)) ?? false)
        || VisualStatuses.Failing.Contains(ResultText.Visual(batch, r) ?? "");
    private static string Key(ScenarioResult r) => r.ScenarioId + "|" + r.Size;

    private void Refresh()
    {
        if (!ready) return;
        view = host.View();
        for (int i = 0; i < header.Length; i++) header[i].SetText(view.Header.ElementAtOrDefault(i) ?? "");
        var batch = view.Batch;
        bool newRun = view.RunRoot != shownRoot;
        if (newRun) { shownRoot = view.RunRoot; selectedKey = ""; note = ""; }
        int problems = batch.Results.Count(r => IsFailure(batch, r));
        counts.SetText(view.Live ? "Running: execution rows appear when the run finishes."
            : $"{batch.Results.Length} execution{(batch.Results.Length == 1 ? "" : "s")}, {problems} with problems");
        int width = Math.Max(20, executions.size.x - 1);
        columns.SetText(WorkbenchText.Fit("    Result            Visual        Grid     " + WorkbenchText.Column("Scenario", Math.Max(22, width - 54)) + "    Time", columns.size.x));
        visible = batch.Results.Where(r => filter.value == 0 || IsFailure(batch, r)).ToArray();
        executions.Empty = view.Live ? "The run is in progress; < Run shows earlier runs meanwhile."
            : filter.value == 1 ? "No problem executions in this run (Alt+A shows all)." : "No executions.";
        int focus = Array.FindIndex(visible, r => Key(r) == selectedKey);
        if (focus < 0) focus = Math.Max(0, Array.FindIndex(visible, r => IsFailure(batch, r)));
        lastFilter = filter.value;
        executions.Load(visible.Select(r => Row(r, width)).ToArray(), focus);
        lastExecution = -1;
        UpdateExecution();
    }

    private void UpdateExecution()
    {
        var r = Selected;
        string key = r is null ? "" : Key(r);
        bool same = key == selectedKey;
        if (key != selectedKey) note = "";
        selectedKey = key; lastExecution = executions.focused;
        int keep = same ? checkpoints.focused : 0;
        checkpointRows = r is null ? [] : ResultText.Checkpoints(view.Batch, r, host.Descriptor(r.ScenarioId), checkpoints.size.x);
        checkpoints.Empty = view.Live ? "Run in progress." : "No execution selected.";
        checkpoints.Load(checkpointRows.Select(c => c.Text).ToArray(), keep);
        executionTitle.SetText(r is null ? "" : $"{r.ScenarioId}  {r.Size}");
        lastCheckpoint = -1;
        UpdateInspector();
    }

    private void UpdateInspector()
    {
        if (!ready) return;
        lastCheckpoint = checkpoints.focused;
        var r = Selected; var c = SelectedCheckpoint;
        var rows = new List<InspectorRow>();
        if (note.Length > 0) { rows.Add(new(note)); rows.Add(new("")); }
        string title;
        if (view.Live)
        {
            title = "Run in progress";
            rows.Add(new("Execution rows appear when the run finishes: the runner records results when it returns."));
            rows.Add(new("The Run Monitor (Alt+3) shows progress. < Run (Ctrl+Left) shows earlier runs of this folder meanwhile."));
        }
        else if (r is null)
        {
            title = "";
            rows.Add(new(view.Batch.Results.Length == 0 ? "This run has no executions." : "No execution matches the Problems filter. Choose All (Alt+A)."));
        }
        else if (c is null)
        {
            var row = checkpointRows.ElementAtOrDefault(checkpoints.focused);
            if (checkpoints.focused > 0)
            {
                title = "Checkpoint " + row.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() + " (not reached)";
                rows.Add(new("This checkpoint of the scenario definition was not reached: the execution ended before it."));
                rows.Add(new("Result     " + ResultText.Execution(r) + (r.Reason.Length > 0 ? ": " + r.Reason : "")));
            }
            else
            {
                title = "Overview";
                rows.AddRange(view.Provenance.Select(line => new InspectorRow(line)));
                rows.AddRange(ResultText.Overview(view.Batch, r, host.Descriptor(r.ScenarioId), view.Duration(r), view.Setup));
            }
        }
        else
        {
            title = $"Checkpoint {c.Checkpoint} ({c.Size})";
            rows.AddRange(ResultText.Checkpoint(view.Batch, r, c));
        }
        var unavailable = UpdateButtons();
        if (unavailable.Count > 0) { rows.Add(new("")); rows.Add(new("Unavailable here")); rows.AddRange(unavailable.Select(u => new InspectorRow("  " + u))); }
        inspectorTitle.SetText(title);
        inspector.Load(rows, Math.Max(10, inspector.size.x - 1));
    }

    // TButton availability follows the application command set (Turbo Vision contract: the button re-reads
    // CommandEnabled on every cmCommandSetChanged). The selection-dependent actions own Results-only commands, so
    // availability is set on the command; the button is synchronized now instead of at the next broadcast.
    internal static readonly ushort[] SelectionCommands = [OpenImage, NativeEvidence, CompareCheckpoint, ReviewCandidate, CopyPath, Rerun, Locations];
    private static void Enable(TButton button, bool enabled)
    {
        if (enabled) EnableCommand(button.Command); else DisableCommand(button.Command);
        if (button.GetState(Views.sfDisabled) == !enabled) return;
        button.SetState(Views.sfDisabled, !enabled); button.DrawView();
    }

    // Actions are enabled for the selection; a disabled one is explained in the inspector ().
    private List<string> UpdateButtons()
    {
        var reasons = new List<string>();
        var r = Selected; var c = SelectedCheckpoint; var batch = view.Batch;
        void Set(TButton button, string? reason) { Enable(button, reason is null); if (reason is not null && r is not null) reasons.Add(reason); }
        CheckpointResult[] targets = c is not null ? [c] : r?.Checkpoints ?? [];
        Set(openImage, r is null ? "" : c is null ? "Open image: choose a recorded checkpoint in Checkpoints." : null);
        Set(native, r is null ? "" : c is null ? "Native: choose a recorded checkpoint." : ResultText.Evidence(batch, c).Any(e => e.Native && ResultText.Exists(batch.RunRoot, e.File)) ? null
            : "Native: no native evidence was recorded for this checkpoint (logical evidence only).");
        bool comparable = targets.Any(t => t.Visual?.Diff is not null || ResultText.Evidence(batch, t).Any(e => e.Native));
        Set(compare, r is null ? "" : comparable ? null : "Compare: no logical baseline comparison and no native evidence for this selection.");
        string? reviewReason = r is null ? "" : batch.Driver != "NullDriver" ? "Review candidate: native approval is unavailable; native runs are compared in the Native driver tools."
            : batch.Visual is null ? "Review candidate: only Visual regression runs keep baseline candidates."
            : !targets.Any(t => t.Visual?.Baseline is { } key && ResultText.Exists(batch.RunRoot, key + ".candidate.json"))
                ? "Review candidate: no candidate was kept for this " + (c is null ? "execution." : "checkpoint.") : null;
        Set(review, reviewReason);
        Enable(copy, r is not null || view.RunRoot.Length > 0);
        Enable(rerun, r is not null);
        if (r is null && !view.Live) reasons.Add("Re-run: select an execution.");
        Enable(folder, view.RunRoot.Length > 0 && Directory.Exists(view.RunRoot));
        reasons.RemoveAll(x => x.Length == 0);
        if (current is TButton { } focusedButton && focusedButton.GetState(Views.sfDisabled)) executions.Select();
        return reasons;
    }

    // ── actions ──────────────────────────────────────────────────────────────────────

    private void Warn(string text) { host.Notifications.Warning(text); }
    internal const string TechnicalTitle = "Technical Details";
    private void Open(InspectorRow row)
    {
        if (row.Technical is { } technical && owner is TDeskTop desktop)
        {
            var r = Selected;
            WorkbenchDialog.ShowMessage(desktop, TechnicalTitle, (r is null ? "" : $"{r.ScenarioId} at {r.Size}: {ResultText.Execution(r)}\n\n") + technical
                + "\n\nThe same text is recorded in this run's run.json and report.md.");
            return;
        }
        if (row.Path is null) { Warn("This row has no file to open."); return; }
        if (row.Folder ? !Directory.Exists(row.Path) : !File.Exists(row.Path))
        { Warn($"{Path.GetFileName(row.Path)} is missing from the run folder; it may have been deleted after the run was loaded."); return; }
        host.OpenPath(row.Path, row.Folder);
    }
    private void OpenFirst(Predicate<InspectorRow> match, string none)
    {
        int index = Array.FindIndex(inspector.Items, match);
        if (index < 0) { Warn(none); return; }
        inspector.FocusItemNum(index);
        Open(inspector.Items[index]);
    }
    private void OpenCheckpointImage()
    {
        if (Selected is null || SelectedCheckpoint is null) { Warn("Choose a recorded checkpoint first (Checkpoints, Alt+K)."); return; }
        OpenFirst(r => r.Path is not null && r.Text.TrimStart().StartsWith(ResultText.Evidence(view.Batch, SelectedCheckpoint)[0].Label, StringComparison.Ordinal),
            "The checkpoint image is not recorded.");
    }
    // Focus the evidence rows of the selected (or first) checkpoint, e.g. for Results > Open evidence.
    internal void FocusEvidence()
    {
        if (Selected is { } r && SelectedCheckpoint is null && r.Checkpoints.Length > 0) { checkpoints.FocusItemNum(1); UpdateInspector(); }
        int index = Array.FindIndex(inspector.Items, x => x.Path is not null && !x.Folder);
        if (index >= 0) { inspector.FocusItemNum(index); inspector.Select(); }
        else Warn("This execution has no captured checkpoints. Its Overview shows the reason.");
    }
    private void CopySelection()
    {
        string? path = inspector.Current?.Path ?? (view.RunRoot.Length > 0 ? view.RunRoot : null);
        if (path is null) { Warn("There is no path to copy."); return; }
        bool copied = ClipboardService.Current.IsAvailable && ClipboardService.Current.SetText(path);
        if (copied) host.Notifications.Info("Copied to the clipboard: " + path);
        else Warn("The clipboard is not available; the path stays visible in Details.");
    }
    private void Inspect()
    {
        if (Selected is not { } r) { Warn(view.Live ? "The run is in progress; there are no execution rows yet." : "Select an execution first."); return; }
        int failing = Array.FindIndex(checkpointRows, x => x.Checkpoint is { } c && (ResultText.Failing(c) || c.Visual is { } v && VisualStatuses.Failing.Contains(v.Status)));
        if (failing > 0) checkpoints.FocusItemNum(failing);
        checkpoints.Select();
        UpdateInspector();
    }
    private void Activate(TView list)
    {
        if (ReferenceEquals(list, executions)) Inspect();
        else if (ReferenceEquals(list, checkpoints)) { if (SelectedCheckpoint is null) inspector.Select(); else OpenCheckpointImage(); }
        else if (ReferenceEquals(list, inspector) && inspector.Current is { } row) Open(row);
    }

    private void Run(ushort command)
    {
        var r = Selected; var batch = view.Batch;
        switch (command)
        {
            case CloseResults: Close(); break;
            case ToggleFailed: filter.value = filter.value == 1 ? 0u : 1u; filter.DrawView(); Refresh(); break;
            case PreviousRun: host.Navigate(-1); break;
            case NextRun: host.Navigate(1); break;
            case Locations: if (view.RunRoot.Length > 0 && Directory.Exists(view.RunRoot)) host.OpenPath(view.RunRoot, true); else Warn("The run folder is not available."); break;
            case ResultDetails: Inspect(); break;
            case OpenImage: if (ReferenceEquals(current, inspector) && inspector.Current is { Path: not null } row) Open(row); else OpenCheckpointImage(); break;
            case NativeEvidence: OpenFirst(x => x.Native && x.Path is not null, "No native evidence was recorded for this checkpoint."); break;
            case CompareCheckpoint: if (r is null) Warn("Select an execution first."); else host.Compare(batch, r, SelectedCheckpoint); break;
            case ReviewCandidate: if (r is null) Warn("Select an execution first."); else host.ReviewCandidate(batch, r, SelectedCheckpoint); break;
            case CopyPath: CopySelection(); break;
            case OpenRunFile: host.OpenRun(); break;
            case Rerun: if (r is null) Warn("Select an execution to re-run."); else host.Rerun(batch, r); break;
        }
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown)
        {
            switch (ev.keyDown.keyCode)
            {
                case Keys.kbEsc: ClearEvent(ref ev); Close(); return;
                case Keys.kbCtrlLeft: ClearEvent(ref ev); host.Navigate(-1); return;
                case Keys.kbCtrlRight: ClearEvent(ref ev); host.Navigate(1); return;
                case Keys.kbEnter:
                    // Enter acts on the focused control: inspect an execution, open a checkpoint image or an evidence row,
                    // or press the focused button. It never starts a run or approves anything by itself.
                    ClearEvent(ref ev);
                    if (current is TButton button) { if (!button.GetState(Views.sfDisabled)) button.Press(); }
                    else if (current is not null) Activate(current);
                    return;
            }
        }
        if (ev.What == Events.evBroadcast && ev.message.command == Views.cmListItemSelected && ev.message.infoPtr is TView list
            && (ReferenceEquals(list, executions) || ReferenceEquals(list, checkpoints) || ReferenceEquals(list, inspector)))
        { ClearEvent(ref ev); Activate(list); return; }
        if (ev.What == Events.evCommand && ev.message.command is CloseResults or (>= ToggleFailed and <= ResultDetails) or (>= NextRun and <= Rerun))
        { ushort command = ev.message.command; ClearEvent(ref ev); Run(command); return; }
        base.HandleEvent(ref ev);
        if (!ready || owner is null) return;
        if (filter.value != lastFilter) Refresh();
        else if (executions.focused != lastExecution) UpdateExecution();
        else if (checkpoints.focused != lastCheckpoint) { note = ""; UpdateInspector(); }
    }

    public override void ChangeBounds(TRect bounds)
    {
        base.ChangeBounds(bounds);
        if (layout is null) return;
        Lock();
        foreach (var item in layout) item.View.ChangeBounds(item.Bounds(size.x, size.y));
        Unlock();
        FreeBuffer();
        if (ready) Refresh();
        DrawView();
    }
    public override void SizeLimits(ref TPoint min, ref TPoint max) { base.SizeLimits(ref min, ref max); min = new(Math.Min(100, max.x), Math.Min(30, max.y)); }
}
