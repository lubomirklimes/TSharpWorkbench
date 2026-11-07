using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

/// <summary>What the editor window needs from the Workbench project that opened it (implemented by WorkbenchDialog).</summary>
internal interface IScenarioEditorHost
{
    /// <summary>Fixture binding context of the current target (raw catalog; null catalog = not discovered yet).</summary>
    ScenarioBindingContext EditorBinding { get; }
    /// <summary>Recorder context values of the open project (target, grids). Null when no project is open any more.</summary>
    WorkbenchProject? EditorProject { get; }
    string? EditorProjectPath { get; }
    /// <summary>Called after bytes were written (Save / Save As). <paramref name="previousPath"/> is the path before a Save As.</summary>
    void EditorSaved(WorkbenchScenarioEditorWindow editor, ScenarioSaveResult result, string? previousPath);
    void EditorClosed(WorkbenchScenarioEditorWindow editor);
    /// <summary>Opens the document in the external editor (TSHARPWORKBENCH_EDITOR / system editor).</summary>
    void EditorOpenExternal(string path);
    /// <summary>File choice for Save As; null = cancelled.</summary>
    string? EditorChoosePath(string suggested);
    void EditorStatus(string message, bool warning = false);
    string? EditorSaveProblem(ScenarioDocument document) => null;
}

// The structured scenario editor, a modeless window (Scenarios > New scenario / Open in editor,
// Scenarios window). Header summary on top, the ordered step list on the left, the selected step's fields on the right,
// validation problems below, two rows of buttons (step operations; document operations). Steps and assertions are edited in
// small modal dialogs (WorkbenchScenarioStepDialog.cs). Read-only documents are shown with their problems and never written.
internal sealed class WorkbenchScenarioEditorWindow : TWindow
{
    internal const ushort Context = 0x7d20, NewStep = 0x7d21, EditStep = 0x7d22, DuplicateStep = 0x7d23, DeleteStep = 0x7d24,
        MoveUp = 0x7d25, MoveDown = 0x7d26, EditMetadata = 0x7d27, ValidateDocument = 0x7d28, SaveDocument = 0x7d29, SaveDocumentAs = 0x7d2a,
        CloseEditor = 0x7d2b, DocumentInformation = 0x7d3e;
    private static readonly ushort[] EditCommands = [NewStep, EditStep, DuplicateStep, DeleteStep, MoveUp, MoveDown, EditMetadata, SaveDocument];
    internal const string Hint = "Ins New step  Enter Edit  Del Delete  Alt+U/Alt+D Move  Shift+Up/Down Select range  Ctrl+S Save  Esc Close";

    /// <summary>Optional recorder entry point (Agent C): when set before the window is created, a Rec~o~rd... button
    /// is shown in the step row and invokes it with this editor.</summary>
    internal static Action<WorkbenchScenarioEditorWindow>? RecordRequested { get; set; }
    internal const ushort RecordCommand = 0x7d1f;   // in the recorder's command range (0x7d10..0x7d1f), posted only by that button

    private readonly IScenarioEditorHost? host;
    private ScenarioDocument document;
    private readonly StepList list;
    private readonly TListBox details, problemList;
    private readonly Line infoHint, columns, detailTitle, problemTitle;
    private readonly WorkbenchReadOnlyField summaryId, summarySteps, summaryMode, summaryGroup, summaryExpected;
    private readonly WorkbenchReadOnlyChecks summaryState;
    private readonly TScrollBar problemScroll;
    private IReadOnlyList<ScenarioDocumentProblem> problems = [];
    private int shownRevision = -1, shownFocus = -2, shownAnchor = -2;
    private readonly List<(TView View, Func<int, int, TRect> Bounds)> layout = [];

    internal ScenarioDocument Document => document;
    internal IReadOnlyList<ScenarioDocumentProblem> Problems => problems;
    internal TListBox Steps => list;
    internal TListBox StepFields => details;
    internal TListBox ProblemList => problemList;
    internal string HeaderText { get; private set; } = "";
    internal string HintText => document.ReadOnly ? "Read-only; External editor (Alt+L). Esc closes." : Hint;
    internal Action? Closed { get; set; }

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }

    // The step list: one row per step; Shift+Up/Down extends a range from the anchor (shown with > markers).
    internal sealed class StepList(TRect bounds, TScrollBar scroll) : WorkbenchListBox(bounds, 1, scroll)
    {
        internal IReadOnlyList<ScenarioDocumentStep> Items = [];
        internal IReadOnlyList<ScenarioDocumentProblem> Problems = [];
        internal int Anchor = -1;
        internal (int Start, int Count) Range => Items.Count == 0 ? (0, 0)
            : Anchor < 0 || Anchor == focused ? (Math.Clamp(focused, 0, Items.Count - 1), 1) : (Math.Min(Anchor, focused), Math.Abs(Anchor - focused) + 1);
        internal static (int Kind, int Parameters, int Note) Widths(int width)
        {
            const int kind = 11;
            int rest = Math.Max(2, width - 5 - kind - 2), parameters = rest * 56 / 100;
            return (kind, parameters, rest - parameters);
        }
        private static string OneLine(string value) => value.ReplaceLineEndings(" ").Replace('\t', ' ');
        internal static string Header(int width)
        {
            var (k, p, n) = Widths(width);
            return WorkbenchText.Fit("   # " + WorkbenchText.Column("Kind", k) + " " + WorkbenchText.Column("Parameters", p) + " " + WorkbenchText.Column("Note", n), width);
        }
        public override string GetText(int item, int maxChars)
        {
            if (Items.ElementAtOrDefault(item) is not { } s)
                return item == 0 ? "No steps. Ins (New step) adds the first step; a scenario needs at least one checkpoint." : "";
            int width = Math.Min(maxChars, Math.Max(1, size.x - 1));
            var (k, p, n) = Widths(width);
            var (start, count) = Range;
            bool marked = count > 1 && item >= start && item < start + count;
            bool error = Problems.Any(x => x.Step == item && x.Severity == ScenarioProblemSeverity.Error);
            bool warning = !error && Problems.Any(x => x.Step == item);
            string flag = error ? "!" : warning ? "?" : marked ? ">" : " ";
            return WorkbenchText.Fit(flag + (item + 1).ToString().PadLeft(3) + " " + WorkbenchText.Column(s.Kind, k) + " " + WorkbenchText.Column(OneLine(s.Parameters), p) + " " + WorkbenchText.Column(OneLine(s.Note ?? ""), n), width);
        }
        public override bool IsSelected(int item) { var (start, count) = Range; return count > 1 ? item >= start && item < start + count : base.IsSelected(item); }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode is Keys.kbUp or Keys.kbDown && (ev.keyDown.controlKeyState & Keys.kbCtrlShift) == 0)
            {
                bool shift = (ev.keyDown.controlKeyState & Keys.kbShift) != 0;
                if (shift && Anchor < 0) Anchor = focused;
                if (!shift) Anchor = -1;
                base.HandleEvent(ref ev);
                DrawView();
                return;
            }
            if (ev.What == Events.evMouseDown) Anchor = -1;
            base.HandleEvent(ref ev);
        }
    }

    internal WorkbenchScenarioEditorWindow(TDeskTop desktop, ScenarioDocument document, IScenarioEditorHost? host, TRect? remembered = null)
        : base(DefaultBounds(desktop, remembered), "Scenario editor", 0)
    {
        this.document = document; this.host = host;
        helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        void Caption(string text, int left, int right, int row) =>
            Place(new TStaticText(default, text), (w, h) => new(left, row, right, row + 1));
        WorkbenchReadOnlyField SummaryField(int left, int right, int row)
        {
            var field = new WorkbenchReadOnlyField(default);
            Place(field, (w, h) => new(left, row, right, row + 1)); return field;
        }
        Caption("ID", 2, 5, 2); summaryId = SummaryField(5, 29, 2);
        summaryState = new WorkbenchReadOnlyChecks(default, new TSItem("Saved", new TSItem("Valid", new TSItem("Read-only", null!))));
        Place(summaryState, (w, h) => new(31, 2, 77, 3));
        Caption("Steps", 80, 87, 2); summarySteps = SummaryField(87, 94, 2);
        Caption("Mode", 2, 8, 3); summaryMode = SummaryField(8, 20, 3);
        Caption("Group", 23, 30, 3); summaryGroup = SummaryField(30, 50, 3);
        Caption("Expected", 53, 63, 3); summaryExpected = SummaryField(63, 73, 3);
        infoHint = new Line(default); Place(infoHint, (w, h) => new(w - 16, 3, w - 2, 4));
        infoHint.Set("Ctrl+P Info");
        columns = new Line(default); Place(columns, (w, h) => new(4, 5, Split(w), 6));
        var scroll = new TScrollBar(default); Place(scroll, (w, h) => new(Split(w), 6, Split(w) + 1, ListBottom(h)));
        list = new StepList(default, scroll) { ActivationCommand = EditStep }; Place(list, (w, h) => new(3, 6, Split(w), ListBottom(h)));
        detailTitle = new Line(default); Place(detailTitle, (w, h) => new(Split(w) + 3, 5, w - 2, 6));
        var detailScroll = new TScrollBar(default); Place(detailScroll, (w, h) => new(w - 3, 6, w - 2, ListBottom(h)));
        details = new TListBox(default, 1, detailScroll); Place(details, (w, h) => new(Split(w) + 3, 6, w - 3, ListBottom(h)));
        problemScroll = new TScrollBar(default); Place(problemScroll, (w, h) => new(w - 3, ProblemTop(h), w - 2, ProblemBottom(h)));
        problemList = new TListBox(default, 1, problemScroll); Place(problemList, (w, h) => new(3, ProblemTop(h), w - 3, ProblemBottom(h)));
        problemTitle = new Line(default); Place(problemTitle, (w, h) => new(2, ListBottom(h), w - 2, ListBottom(h) + 1));
        // Tab order: steps, fields, visible problems, then actions. Local mnemonics avoid P T R B G W H X.
        // Delete uses Alt+Y because Alt+T belongs to the Tests menu.
        var stepActions = new List<(string Text, ushort Command)>
        {
            ("~N~ew step...", NewStep), ("Ed~i~t...", EditStep), ("Dupli~c~ate", DuplicateStep),
            ("Delete (~Y~)", DeleteStep), ("Move ~u~p", MoveUp), ("Move ~d~own", MoveDown)
        };
        if (RecordRequested is not null) stepActions.Add(("Rec~o~rd...", RecordCommand));
        void Actions((string Text, ushort Command)[] actions, int bottomOffset)
        {
            int totalWidth = actions.Sum(a => a.Command == WorkbenchDialog.OpenScenarioExternal ? 20 : 14), used = 0;
            for (int i = 0; i < actions.Length; i++)
            {
                var action = actions[i]; int index = i, offset = used;
                int width = action.Command == WorkbenchDialog.OpenScenarioExternal ? 20 : 14;
                Place(new TButton(default, action.Text, action.Command, ButtonConstants.bfNormal), (w, h) =>
                {
                    int left = 2 + offset + index * (w - 4 - totalWidth) / (actions.Length - 1);
                    return new(left, h - bottomOffset, left + width, h - bottomOffset + 2);
                });
                used += width;
            }
        }
        Actions(stepActions.ToArray(), 6);
        Actions([("~M~etadata...", EditMetadata), ("~V~alidate", ValidateDocument), ("~S~ave", SaveDocument),
            ("Save ~a~s...", SaveDocumentAs), ("Externa~l~ editor", WorkbenchDialog.OpenScenarioExternal), ("Clos~e~", CloseEditor)], 3);
        document.Changed += OnChanged;
        Refresh(true);
        list.Select();
    }

    private static int Split(int w) => w * 61 / 100;
    private int ListBottom(int h) => h - 8 - Math.Min(problems.Count, 3);
    private int ProblemTop(int h) => ListBottom(h) + 1;
    private int ProblemBottom(int h) => h - 7;
    private void Reflow()
    {
        var focus = current;
        Lock();
        foreach (var item in layout) item.View.ChangeBounds(item.Bounds(size.x, size.y));
        bool visible = problems.Count > 0;
        if (problemList.GetState(Views.sfVisible) != visible) problemList.SetState(Views.sfVisible, visible);
        if (problemScroll.GetState(Views.sfVisible) != visible) problemScroll.SetState(Views.sfVisible, visible);
        Unlock();
        if (focus?.GetState(Views.sfVisible) == true && !ReferenceEquals(current, focus)) focus.Select();
    }
    private void Place(TView view, Func<int, int, TRect> bounds) { view.growMode = 0; layout.Add((view, bounds)); view.ChangeBounds(bounds(size.x, size.y)); Insert(view); }
    public override void ChangeBounds(TRect bounds)
    {
        base.ChangeBounds(bounds);
        if (layout is null) return;
        Reflow();
        FreeBuffer();
        if (list is not null) { shownRevision = -1; Refresh(false); }
        DrawView();
    }

    // Centered with a small margin; large enough for both button rows at the minimum 120x36 grid (desktop 120x34).
    internal static TRect DefaultBounds(TDeskTop desktop, TRect? remembered = null)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        if (remembered is { } r && r.a.x >= 0 && r.a.y >= 0 && r.b.x <= dw && r.b.y <= dh && r.b.x - r.a.x >= Math.Min(MinWidth, dw) && r.b.y - r.a.y >= Math.Min(MinHeight, dh)) return r;
        int w = Math.Min(dw, Math.Max(MinWidth, dw - 6)), h = Math.Min(dh, Math.Max(MinHeight, dh - 2));
        int x = (dw - w) / 2, y = (dh - h) / 2;
        return new(x, y, x + w, y + h);
    }
    internal const int MinWidth = 112, MinHeight = 28;
    public override void SizeLimits(ref TPoint min, ref TPoint max) { base.SizeLimits(ref min, ref max); min = new(Math.Min(MinWidth, max.x), Math.Min(MinHeight, max.y)); }

    // ── state and display ─────────────────────────────────────────────────────────────────────────────────

    private void OnChanged() { }   // displays refresh from HandleEvent/Refresh; kept for future observers

    /// <summary>Re-validates and redraws everything that depends on the document.</summary>
    internal void Refresh(bool revalidate = true)
    {
        if (list is null || details is null) return;   // broadcasts arrive while the constructor inserts controls
        var focus = current;
        if (revalidate || shownRevision != document.Revision)
        {
            problems = document.Validate(host?.EditorBinding);
            shownRevision = document.Revision;
            Reflow();
            list.Items = document.Steps; list.Problems = problems;
            int keep = Math.Clamp(list.focused, 0, Math.Max(0, document.Steps.Count - 1));
            list.SetRange(document.Steps.Count);
            if (list.Anchor >= document.Steps.Count) list.Anchor = -1;
            list.FocusItemNum(keep); list.DrawView();
            var lines = new TStringCollection();
            foreach (var p in problems) lines.Insert(WorkbenchText.Fit(p.Display, Math.Max(10, problemList.size.x - 1)));
            problemTitle.Set(problems.Count == 0 ? "Validation: valid" : $"Problems: {problems.Count}  (Tab to list; Enter selects affected step)");
            problemList.NewList(lines);
            shownFocus = -2;
        }
        UpdateTitle(); UpdateHeader(); UpdateCommands();
        columns.Set(StepList.Header(list.size.x - 1));
        ShowStep();
        if (focus?.GetState(Views.sfVisible) == true && !ReferenceEquals(current, focus)) focus.Select();
    }

    private string FileName => document.Path is null ? "untitled" : System.IO.Path.GetFileName(document.Path);
    private void UpdateTitle()
    {
        string identity = string.IsNullOrWhiteSpace(document.Title) ? "untitled" : WorkbenchText.Fit(document.Title, 48);
        string id = string.IsNullOrWhiteSpace(document.Id) ? "no ID" : WorkbenchText.Fit(document.Id, 24);
        string next = "Scenario " + identity + " [" + id + "]" + (document.Dirty ? " *" : "") + (document.ReadOnly ? " [read-only]" : "");
        if (title == next) return;
        title = next; frame?.DrawView();
    }
    private void UpdateHeader()
    {
        int errors = problems.Count(p => p.Severity == ScenarioProblemSeverity.Error), warnings = problems.Count - errors;
        string mode = document.ReadOnly && !document.HeaderNames.Any() ? "Mode unavailable" : document.Proxy ? "Black-Box" : "White-Box";
        string state = document.ReadOnly ? "READ-ONLY" : document.Path is null ? "New / unsaved" : document.Dirty ? "Modified / unsaved" : "Saved";
        string validation = errors > 0 ? $"{errors} errors" : "valid";
        if (warnings > 0) validation += $", {warnings} warnings";
        summaryId.SetData(WorkbenchText.Fit(document.Id, summaryId.size.x));
        summarySteps.SetData(document.Steps.Count.ToString());
        summaryMode.SetData(mode);
        summaryGroup.SetData(WorkbenchText.Fit(document.Group ?? "external (default)", summaryGroup.size.x));
        summaryExpected.SetData(document.ExpectedOutcome);
        summaryState.value = (!document.Dirty && document.Path is not null ? 1u : 0u) | (errors == 0 ? 2u : 0u) | (document.ReadOnly ? 4u : 0u);
        summaryState.DrawView();
        HeaderText = $"ID {document.Id}  {state}  {validation}  {document.Steps.Count} steps\n{mode}  Group {document.Group}  Expected {document.ExpectedOutcome}  Ctrl+P Info";
    }
    // Commands are window-specific; set them whenever this window is (re)focused or changes (the command set is global).
    private void UpdateCommands()
    {
        if (!GetState(Views.sfActive) && owner is not null && !ReferenceEquals(owner.current, this)) return;
        bool editable = !document.ReadOnly;
        foreach (ushort c in EditCommands) { if (editable) EnableCommand(c); else DisableCommand(c); }
        if (editable && document.Steps.Count == 0) { DisableCommand(EditStep); DisableCommand(DuplicateStep); DisableCommand(DeleteStep); DisableCommand(MoveUp); DisableCommand(MoveDown); }
        EnableCommand(ValidateDocument); EnableCommand(SaveDocumentAs); EnableCommand(CloseEditor); EnableCommand(DocumentInformation);
        if (document.ReadOnly) DisableCommand(SaveDocumentAs);
    }
    public override void SetState(ushort aState, bool enable)
    {
        base.SetState(aState, enable);
        if (enable && (aState & (Views.sfActive | Views.sfFocused)) != 0 && list is not null) UpdateCommands();
    }

    private void ShowStep()
    {
        var (start, count) = list.Range;
        if (list.focused == shownFocus && list.Anchor == shownAnchor) return;
        shownFocus = list.focused; shownAnchor = list.Anchor;
        var lines = new TStringCollection();
        foreach (string line in Describe(start, count)) lines.Insert(line);
        details.NewList(lines);
        detailTitle.Set(document.Steps.Count == 0 ? "Selected step" : count > 1 ? $"Selected steps {start + 1}-{start + count}" : $"Step {start + 1}: {(document.Steps[start].Kind == "checkpoint" ? "Checkpoint" : ScenarioSchemaV1.StepTitle(document.Steps[start].Kind))}");
    }

    private IEnumerable<string> DetailField(string label, string value)
    {
        const int labelWidth = 12;
        int width = Math.Max(10, details.size.x - labelWidth - 2);
        bool first = true;
        foreach (string line in value.ReplaceLineEndings("\n").Split('\n'))
            foreach (string part in WorkbenchText.Wrap(line, width).DefaultIfEmpty(""))
            {
                yield return (first ? WorkbenchText.Column(label, labelWidth) : new string(' ', labelWidth)) + " " + part;
                first = false;
            }
    }

    private IEnumerable<string> Describe(int start, int count)
    {
        if (document.Steps.Count == 0) { yield return "No step selected."; yield break; }
        if (count > 1)
        {
            foreach (string line in WorkbenchText.Wrap($"{count} steps selected. Duplicate, Delete and Move act on this range.", details.size.x - 1)) yield return line;
            yield break;
        }
        var step = document.Steps[start];
        string Value(System.Text.Json.Nodes.JsonNode? node) => node is System.Text.Json.Nodes.JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>() : node?.ToJsonString() ?? "null";
        foreach (string field in ScenarioSchemaV1.StepFields(step.Kind).Where(f => f is not ("assertions" or "evidence") && step.Has(f)))
            foreach (string line in DetailField(field, Value(step.Node(field)))) yield return line;
        if (step.Kind == "checkpoint")
        {
            string evidence = step.Node("evidence") is System.Text.Json.Nodes.JsonArray captures ? string.Join(", ", captures.Select(Value)) : "default";
            foreach (string line in DetailField("evidence", evidence.Length == 0 ? "none" : evidence)) yield return line;
            var assertions = step.Node("assertions") as System.Text.Json.Nodes.JsonArray;
            foreach (string line in DetailField("assertions", assertions is { Count: > 0 } ? assertions.Count.ToString() : "0 (capture only)")) yield return line;
            int number = 1;
            foreach (var assertion in assertions ?? [])
            {
                if (assertion is System.Text.Json.Nodes.JsonObject item)
                {
                    foreach (string line in DetailField($"  {number++}.", $"{item["kind"]} [{item["id"]}]")) yield return line;
                    foreach (var property in item.Where(p => p.Key is not ("kind" or "id")))
                        foreach (string line in DetailField(property.Key, Value(property.Value))) yield return line;
                }
                else foreach (string line in DetailField($"  {number++}.", Value(assertion))) yield return line;
            }
        }
        if (step.Note is { Length: > 0 } note)
        {
            yield return "";
            foreach (string line in DetailField("note", note)) yield return line;
        }
    }

    private void ShowDocumentInformation()
    {
        if (owner is not TDeskTop desktop) return;
        int w = Math.Min(90, desktop.size.x - 2);
        string[] values = [$"ID: {document.Id}", $"Title: {document.Title}",
            $"Mode: {(document.Proxy ? "Black-Box" : "White-Box")}    Group: {document.Group}",
            $"Expected: {document.ExpectedOutcome}    Steps: {document.Steps.Count}",
            $"Fixture: {document.Fixture ?? "(none)"}", "", "File:", document.Path ?? "(not saved yet)"];
        if (document.ReadOnlyReason is { } reason) values = [.. values, "", "Read-only: " + reason];
        string[] rows = values.SelectMany(v => WorkbenchText.Wrap(v, w - 7).DefaultIfEmpty("")).ToArray();
        int h = Math.Min(desktop.size.y, rows.Length + 6);
        using var dialog = new TDialog(new(0, 0, w, h), "Scenario Information") { helpCtx = Context };
        dialog.options |= Views.ofCentered;
        var scroll = new TScrollBar(new(w - 4, 2, w - 3, h - 4)); dialog.Insert(scroll);
        var contents = new TListBox(new(3, 2, w - 4, h - 4), 1, scroll); dialog.Insert(contents);
        var text = new TStringCollection(); foreach (string row in rows) text.Insert(row); contents.NewList(text);
        dialog.Insert(new TButton(new(w - 17, h - 3, w - 3, h - 1), "Close", Views.cmCancel, ButtonConstants.bfDefault));
        contents.Select(); desktop.ExecView(dialog);
    }

    // ── recorder API (Agent C) ────────────────────────────────────────────────────────────────────────────

    /// <summary>The selected step range (start, count); count is 0 only for an empty document.</summary>
    internal (int Start, int Count) Selection => list.Range;

    /// <summary>Selects steps start..start+count-1 (clamped).</summary>
    internal void SelectSteps(int start, int count = 1)
    {
        if (document.Steps.Count == 0) return;
        start = Math.Clamp(start, 0, document.Steps.Count - 1);
        int end = Math.Clamp(start + Math.Max(1, count) - 1, start, document.Steps.Count - 1);
        list.Anchor = end > start ? start : -1;
        list.FocusItemNum(end); list.DrawView(); shownFocus = -2; ShowStep();
    }

    /// <summary>The editor's current document/target/project context for a recorder (see ScenarioEditorContext).</summary>
    internal ScenarioEditorContext RecordingContext()
    {
        var project = host?.EditorProject;
        var (start, count) = list.Range;
        string[]? defaults = document.HeaderNode("grid") is System.Text.Json.Nodes.JsonObject g && g["defaults"] is System.Text.Json.Nodes.JsonArray a
            ? a.Select(d => d?.ToString() ?? "").ToArray() : null;
        return new(document.Path, document.Proxy, document.Fixture, document.ReadOnly,
            project is { Target.Length: > 0 } ? project.Target : null, project?.TargetArguments ?? [], project?.TargetEnvironment ?? new Dictionary<string, string>(),
            host?.EditorProjectPath, project?.ProxyBackend ?? "NullDriver", project?.ScreenSizes ?? [], defaults,
            start, count, document.Steps.Count, host?.EditorBinding.Fixture(document.Fixture));
    }

    /// <summary>
    /// Puts recorded steps into the document (the recorder never mutates it directly) and selects them. Append: after the
    /// last step. InsertAtSelection: before the selected range. ReplaceSelection: in place of the selected range. Returns
    /// the inserted range. Throws InvalidOperationException for a read-only document or ReplaceSelection without a selection;
    /// ArgumentException for an empty list. Nothing is saved; the user reviews, edits and saves.
    /// </summary>
    internal (int Start, int Count) ApplyRecording(RecordedInsertMode mode, IReadOnlyList<ScenarioDocumentStep> steps)
    {
        if (document.ReadOnly) throw new InvalidOperationException("The document is read-only: " + document.ReadOnlyReason);
        if (steps.Count == 0) throw new ArgumentException("No recorded steps.");
        var (start, count) = list.Range;
        int at;
        switch (mode)
        {
            case RecordedInsertMode.Append: at = document.Steps.Count; document.Append(steps); break;
            case RecordedInsertMode.InsertAtSelection: at = document.Steps.Count == 0 ? 0 : start; document.Insert(at, steps); break;
            case RecordedInsertMode.ReplaceSelection:
                if (count == 0) throw new InvalidOperationException("Select the steps to replace first.");
                at = start; document.ReplaceRange(start, count, steps); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        Refresh(true);
        SelectSteps(at, steps.Count);
        Status($"{steps.Count} recorded step{(steps.Count == 1 ? "" : "s")} {(mode == RecordedInsertMode.ReplaceSelection ? $"replaced {count} step{(count == 1 ? "" : "s")}" : "inserted")}; review them, then Save (Ctrl+S).");
        return (at, steps.Count);
    }

    // ── commands ──────────────────────────────────────────────────────────────────────────────────────────

    private void Status(string message, bool warning = false) => host?.EditorStatus(message, warning);

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown)
        {
            bool inList = ReferenceEquals(current, list);
            bool ctrl = (ev.keyDown.controlKeyState & Keys.kbCtrlShift) != 0;
            ushort command = ev.keyDown.keyCode switch
            {
                Keys.kbEsc => CloseEditor,
                Keys.kbIns => NewStep,
                Keys.kbDel when inList => DeleteStep,
                Keys.kbEnter when inList => EditStep,
                Keys.kbEnter when ReferenceEquals(current, problemList) => 0xffff,
                Keys.kbUp when inList && ctrl => MoveUp,
                Keys.kbDown when inList && ctrl => MoveDown,
                Keys.kbCtrlS => SaveDocument,
                Keys.kbCtrlP => DocumentInformation,
                Keys.kbAltV => ValidateDocument,
                _ => (ushort)0
            };
            if (command == 0xffff) { ClearEvent(ref ev); GoToProblem(); return; }
            if (command != 0)
            {
                ClearEvent(ref ev);
                if (command == CloseEditor || CommandEnabled(command)) Execute(command);
                else Status(document.ReadOnly ? "This document is read-only; it cannot be changed here." : "Nothing is selected.", true);
                return;
            }
        }
        if (ev.What == Events.evCommand && ev.message.command is >= NewStep and <= CloseEditor or DocumentInformation)
        { ushort command = ev.message.command; ClearEvent(ref ev); Execute(command); return; }
        if (ev.What == Events.evCommand && ev.message.command == RecordCommand && RecordRequested is { } record)
        { ClearEvent(ref ev); record(this); return; }
        if (ev.What == Events.evCommand && ev.message.command == WorkbenchDialog.OpenScenarioExternal)
        {
            ClearEvent(ref ev);
            if (document.Path is { } path) host?.EditorOpenExternal(path); else Status("Save the document first; the external editor opens a file.", true);
            return;
        }
        base.HandleEvent(ref ev);
        if (list is not null && shownRevision != document.Revision) Refresh(true);
        else ShowStep();
    }

    private void GoToProblem()
    {
        if (problems.ElementAtOrDefault(problemList.focused) is { Step: { } step } && step < document.Steps.Count) { SelectSteps(step); list.Select(); }
    }

    internal void Execute(ushort command)
    {
        try
        {
            switch (command)
            {
                case DocumentInformation: ShowDocumentInformation(); break;
                case NewStep: AddStep(); break;
                case EditStep: Edit(); break;
                case DuplicateStep:
                {
                    var (start, count) = list.Range; int at = document.Duplicate(start, count); Refresh(true); SelectSteps(at, count);
                    Status($"Duplicated {count} step{(count == 1 ? "" : "s")}."); break;
                }
                case DeleteStep:
                {
                    var (start, count) = list.Range; document.Remove(start, count); list.Anchor = -1; Refresh(true); SelectSteps(Math.Min(start, document.Steps.Count - 1));
                    Status($"Deleted {count} step{(count == 1 ? "" : "s")}; Close without saving restores the file as it was."); break;
                }
                case MoveUp or MoveDown:
                {
                    var (start, count) = list.Range; int at = document.Move(start, count, command == MoveUp ? -1 : 1); Refresh(true); SelectSteps(at, count); break;
                }
                case EditMetadata: EditHeader(); break;
                case ValidateDocument:
                {
                    Refresh(true);
                    int errors = problems.Count(p => p.Severity == ScenarioProblemSeverity.Error);
                    Status(errors == 0 ? $"{FileName}: valid schema v1" + (problems.Count > 0 ? $" ({problems.Count} warning{(problems.Count == 1 ? "" : "s")})." : ".")
                        : $"{FileName}: {errors} problem{(errors == 1 ? "" : "s")}; the first is {problems.First(p => p.Severity == ScenarioProblemSeverity.Error).Location}.", errors > 0);
                    break;
                }
                case SaveDocument: Save(false); break;
                case SaveDocumentAs: Save(true); break;
                case CloseEditor: Close(); break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { Status(ex.Message, true); }
    }

    private ScenarioDescriptor? BoundFixture => host?.EditorBinding.Fixture(document.Fixture);

    private void AddStep()
    {
        string? kind = WorkbenchStepKindDialog.Choose(owner as TDeskTop ?? (TDeskTop)owner!, document.Proxy, null);
        if (kind is null) return;
        var created = WorkbenchScenarioStepDialog.Edit(owner as TDeskTop ?? (TDeskTop)owner!, ScenarioDocumentStep.FromJson(new() { ["kind"] = kind }), document.Proxy, BoundFixture, true);
        if (created is null) return;
        var (start, count) = list.Range;
        int at = document.Steps.Count == 0 ? 0 : start + count;
        document.Insert(at, created);
        Refresh(true); SelectSteps(at);
        Status($"Added a {created.Kind} step as step {at + 1}.");
    }

    private void Edit()
    {
        var (start, count) = list.Range;
        if (document.Steps.Count == 0) return;
        if (count > 1) { SelectSteps(start); }
        var edited = WorkbenchScenarioStepDialog.Edit(owner as TDeskTop ?? (TDeskTop)owner!, document.Steps[start], document.Proxy, BoundFixture, false);
        if (edited is null) return;
        document.Replace(start, edited);
        Refresh(true); SelectSteps(start);
    }

    private void EditHeader()
    {
        if (WorkbenchScenarioMetadataDialog.Edit(owner as TDeskTop ?? (TDeskTop)owner!, document, host?.EditorBinding ?? ScenarioBindingContext.Unknown)) { Refresh(true); Status("Metadata changed; Save (Ctrl+S) writes it."); }
    }

    internal bool SaveForProject()
    {
        if (!document.Dirty) return true;
        Save(false); return !document.Dirty;
    }
    private void Save(bool saveAs)
    {
        var desktop = owner as TDeskTop ?? (TDeskTop)owner!;
        if (document.ReadOnly) { Status("This document is read-only and is never written: " + document.ReadOnlyReason, true); return; }
        if (host?.EditorSaveProblem(document) is { } problem) { Status(problem, true); return; }
        string? previous = document.Path;
        string? path = null;
        if (saveAs || document.Path is null)
        {
            string suggested = document.Path ?? System.IO.Path.Combine(Environment.CurrentDirectory, ProjectLocation.SafeName(document.Id.Split('/').Last() is { Length: > 0 } n ? n : "scenario") + ".scenario.json");
            path = host?.EditorChoosePath(suggested);
            if (path is null) { Status("Save as cancelled; nothing was written."); return; }
            if (System.IO.Path.GetExtension(path).Length == 0) path += ".json";
        }
        var binding = host?.EditorBinding;
        var result = document.Save(path, false, binding);
        if (result.TargetExists)
        {
            if (WorkbenchDialog.Confirm(desktop, "Replace File?", $"{System.IO.Path.GetFileName(result.Path)} already exists. Replace it with this scenario?\n\n{result.Path}", "~R~eplace") != Views.cmYes)
            { Status("Nothing was written."); return; }
            result = document.Save(path, true, binding);
        }
        if (result.ExternalChange)
        {
            ushort choice = ExternalChangeChoice(desktop, result.Message);
            if (choice == Views.cmYes) { Reload(); return; }
            if (choice == Views.cmNo) { Save(true); return; }
            Status("Nothing was written; the file on disk was changed by someone else.", true);
            return;
        }
        Refresh(true);
        if (!result.Saved) { Status(result.Message, true); return; }
        if (!result.Written) { Status(result.Message); return; }
        UpdateTitle();
        host?.EditorSaved(this, result, previous);
        if (host is null) Status(result.Message);
    }

    // "Changed on disk": Reload (discard edits), Save as..., Cancel (default).
    private static ushort ExternalChangeChoice(TDeskTop desktop, string message)
    {
        var lines = WorkbenchText.Wrap(message, Math.Min(72, desktop.size.x - 10)).ToArray();
        int w = Math.Min(80, desktop.size.x - 2), h = Math.Min(lines.Length + 7, desktop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), "File Changed on Disk"); dialog.options |= Views.ofCentered; dialog.helpCtx = WorkbenchApp.DialogContext;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 4), string.Join('\n', lines)));
        dialog.Insert(new TButton(new(3, h - 3, 17, h - 1), "~R~eload", Views.cmYes, ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(18, h - 3, 34, h - 1), "~S~ave as...", Views.cmNo, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 15, h - 3, w - 3, h - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfDefault); dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog);
    }

    /// <summary>Discards the edits and reads the file again (after an external change).</summary>
    internal void Reload()
    {
        if (document.Path is not { } path) return;
        ScenarioDocument fresh;
        try { fresh = ScenarioDocument.Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status($"{System.IO.Path.GetFileName(path)} cannot be read: {ex.Message}", true); return; }
        document.Changed -= OnChanged;
        document = fresh; document.Changed += OnChanged;
        list.Anchor = -1; Refresh(true); SelectSteps(0);
        Status($"Reloaded {System.IO.Path.GetFileName(path)} from disk.");
    }

    private bool approvedClose;
    /// <summary>Closes without asking again (the caller already asked through <see cref="Valid"/>).</summary>
    internal void CloseApproved() { approvedClose = true; Close(); }

    public override bool Valid(ushort command)
    {
        if (command == Views.cmClose && !approvedClose && document.Dirty && !document.ReadOnly)
        {
            var desktop = owner as TDeskTop;
            if (desktop is null) return base.Valid(command);
            ushort choice = WorkbenchDialog.Prompt(desktop, "Unsaved Scenario", $"{FileName} has unsaved changes.\n\nSave them before closing?", true);
            if (choice == Views.cmCancel) return false;
            if (choice == Views.cmYes)
            {
                Save(false);
                if (document.Dirty) return false;
            }
        }
        return base.Valid(command);
    }

    public override void ShutDown()
    {
        document.Changed -= OnChanged;
        host?.EditorClosed(this);
        Closed?.Invoke();
        base.ShutDown();
    }
}
