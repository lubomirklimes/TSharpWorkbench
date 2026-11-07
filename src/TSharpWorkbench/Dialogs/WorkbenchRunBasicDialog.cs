using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Run Tests, the compact (Basic) presentation of Run Configuration. It edits the same draft and is assessed by
// the same rules (RunDialogContext.Assess, the runner's revalidation) as the Advanced dialog; only the primary decisions
// are shown: what to run, target display, screen sizes and test mode. Everything Advanced configures stays in the draft
// unchanged, and anything that affects this run (kept baseline candidates, native evidence, limits, inspection, a GPU
// variant) is disclosed in the summary. Cancel, Esc and the close icon change nothing; Advanced >> switches without
// running or applying anything.
internal sealed class WorkbenchRunBasicDialog : TDialog
{
    internal const string DialogTitle = "Run Tests";
    internal const ushort TargetCommand = 0x7d9d, PropertiesCommand = 0x7d9e;
    // Target display rows: the target's own driver. Native diagnostic uses the same names (never a different meaning).
    internal static readonly string[] Displays = ["NullDriver", "SDLDriver", "SDLGpuDriver", "Win32ConsoleDriver"];
    private readonly RunDialogContext context;
    private readonly WorkbenchProject draft;
    private readonly RunDraft? handover;
    private readonly string[] nativeModes;
    private readonly uint initialDisplay, initialMode;
    private readonly bool ready;
    private string assessed = "";
    private RunProblem[] problems = [];
    private RunAssessment? assessment;

    private sealed class Note(TRect bounds, string text = "") : TStaticText(bounds, text)
    {
        internal string Current => Text;
        internal void SetText(string text) { if (Text == text) return; Text = text; DrawView(); }
    }

    internal TRadioButtons Scope { get; }
    internal TRadioButtons Display { get; }
    internal TCheckBoxes Sizes { get; }
    internal TInputLine CustomSizes { get; }
    internal TRadioButtons Mode { get; }
    internal TCheckBoxes SaveDefaults { get; }
    internal TButton AdvancedButton { get; }
    internal TButton DetailsButton { get; }
    internal TButton PropertiesButton { get; }
    internal TButton PrepareButton { get; }
    internal TButton RunButton { get; }
    private readonly Note scopeCounts, sizeNote, modeNote, summary;
    internal WorkbenchProject Accepted { get; private set; }
    internal bool SaveAsDefaults { get; private set; }
    internal IReadOnlyList<RunProblem> Problems => problems;
    internal string SummaryText => summary.Current;
    internal string Inline => string.Join("\n", scopeCounts.Current, sizeNote.Current, modeNote.Current);
    internal RunDraft Draft
    {
        get
        {
            var (p, _) = Read();
            // Only what Basic edits replaces the draft; the limit texts typed in Advanced travel unchanged.
            return new(p, SaveDefaults.value == 1 && !RerunSelected)
                { CustomText = CustomSizes.Data, CustomOn = (Sizes.value & 4) != 0, StepText = handover?.StepText, ScenarioText = handover?.ScenarioText };
        }
    }

    // Visibly smaller than Advanced (118..128 x 34..38): 86 x 32 at every supported Workbench grid.
    internal static TRect Bounds(TDeskTop desktop) => new(0, 0, Math.Min(86, desktop.size.x), Math.Min(32, desktop.size.y));

    internal WorkbenchRunBasicDialog(TDeskTop desktop, WorkbenchProject session, RunDialogContext context, RunDraft? handover = null)
        : base(Bounds(desktop), DialogTitle)
    {
        session = handover?.Project ?? session;
        this.context = context; this.handover = handover; draft = session; Accepted = session;
        nativeModes = context.NativeModes;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y, right = 46;
        bool native = session.TestType == "rendering";
        bool rerun = context.Rerun is not null;

        // Insertion order is the Tab order and the reading order: What to run, Target display, Screen sizes, Test mode.
        Scope = new(new(3, 2, 27, rerun ? 6 : 5), rerun ? RunFields.Items("~M~arked scenarios", "Current ~g~roup", "~A~ll runnable", "Re-run from Re~s~ults")
            : RunFields.Items("~M~arked scenarios", "Current ~g~roup", "~A~ll runnable"));
        Scope.SetData(session.RunScope switch { "group" => 1, "all" => 2, RerunScope.Scope when rerun => 3, _ => 0 });
        Insert(Scope); Insert(new TLabel(new(2, 1, 20, 2), "What to run", Scope));
        scopeCounts = new(new(28, 2, right - 1, 6)); Insert(scopeCounts);

        Display = new(new(right + 1, 2, w - 3, 6), RunFields.Items("~H~eadless (no window)", "SDL ~w~indow", "SDL GP~U~ window", "Windows c~o~nsole"));
        string current = native ? NativeExecution.ActualDriver(session.Mode) : session.ProxyBackend;
        initialDisplay = (uint)Math.Max(0, Array.IndexOf(Displays, current));
        Display.SetData((int)initialDisplay);
        Insert(Display); Insert(new TLabel(new(right, 1, w - 3, 2), "Target display", Display));

        Sizes = new(new(3, 8, 17, 11), RunFields.Items("~8~0x25", "~1~40x40", "~C~ustom"));
        Sizes.value = (session.ScreenSizes.Contains("80x25") ? 1u : 0u) | (session.ScreenSizes.Contains("140x40") ? 2u : 0u) | (RunFields.CustomOn(session, handover) ? 4u : 0u);
        Insert(Sizes); Insert(new TLabel(new(2, 7, 20, 8), "Screen sizes", Sizes));
        CustomSizes = new(new(17, 10, right - 2, 11), 200); CustomSizes.SetData(RunFields.CustomText(session, handover)); Insert(CustomSizes);
        sizeNote = new(new(2, 11, right - 1, 13)); Insert(sizeNote);

        Mode = new(new(right + 1, 8, w - 3, 11), RunFields.Items("~F~unctional", "~V~isual regression", "~N~ative diagnostic"));
        initialMode = session.TestType switch { "visual" => 1u, "rendering" => 2u, _ => 0u };
        Mode.SetData((int)initialMode);
        if (nativeModes.Length == 0) Mode.SetButtonState(4, false);
        Insert(Mode); Insert(new TLabel(new(right, 7, w - 3, 8), "Test mode", Mode));
        modeNote = new(new(right, 11, w - 2, 13)); Insert(modeNote);

        Insert(new TStaticText(new(2, 14, 12, 15), "Summary"));
        summary = new(new(3, 15, w - 2, h - 6)); Insert(summary);
        // Unchecked on every open (), as in Advanced; a switch from Advanced hands over the choice made there.
        SaveDefaults = new(new(2, h - 5, 48, h - 4), RunFields.Items("Save these settings as project ~d~efaults")); Insert(SaveDefaults);
        if (handover?.SaveDefaults == true) SaveDefaults.value = 1;
        // Owner spacing rule: one empty row above the buttons; the button shadow row stays above the bottom frame.
        AdvancedButton = new(new(2, h - 3, 18, h - 1), "Advanc~e~d >>", WorkbenchRunDialog.SwitchCommand, ButtonConstants.bfNormal); Insert(AdvancedButton);
        DetailsButton = new(new(19, h - 3, 38, h - 1), "~T~arget details...", TargetCommand, ButtonConstants.bfNormal); Insert(DetailsButton);
        PropertiesButton = new(new(39, h - 3, 55, h - 1), "Propert~i~es...", PropertiesCommand, ButtonConstants.bfNormal); Insert(PropertiesButton);
        // Shares the slot with Properties...; shown while the target deployment blocks the run and preparation can help.
        PrepareButton = new(new(39, h - 3, 55, h - 1), "~P~repare...", WorkbenchRunDialog.PrepareCommand, ButtonConstants.bfNormal); Insert(PrepareButton);
        RunButton = new(new(w - 30, h - 3, w - 17, h - 1), "~R~un", WorkbenchRunDialog.RunCommand, ButtonConstants.bfDefault); Insert(RunButton);
        Insert(new TButton(new(w - 16, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        ready = true; Refresh();
        // Default focus is Run when the configuration can run; otherwise the first control that needs attention.
        if (problems.Length == 0) RunButton.Select();
        else (problems[0].Area switch { RunArea.Sizes => (TView)Sizes, RunArea.Mode or RunArea.Baselines => Mode, RunArea.Driver or RunArea.Evidence => Display, _ => Scope }).Select();
    }

    private bool Native => Mode.value == 2;
    internal bool RerunSelected => context.Rerun is not null && Scope.value == 3;

    // Reads the controls into a configuration. Fields Basic does not show keep the draft's values.
    private (WorkbenchProject Project, List<RunProblem> Local) Read()
    {
        var local = new List<RunProblem>();
        var (sizes, cw, ch) = RunFields.Sizes(draft, (Sizes.value & 1) != 0, (Sizes.value & 2) != 0, (Sizes.value & 4) != 0, CustomSizes.Data, local);
        int? step = handover?.StepText is { } stepText ? RunFields.Limit(stepText, draft.Timeout, RunFields.StepDefault, 100, 30000, "Step timeout (Advanced)", "milliseconds", local) : draft.Timeout;
        int? scenario = handover?.ScenarioText is { } scenarioText ? RunFields.Limit(scenarioText, draft.ScenarioTimeout, RunFields.ScenarioDefault, 5, 600, "Scenario timeout (Advanced)", "seconds", local) : draft.ScenarioTimeout;
        string type = Mode.value switch { 1 => "visual", 2 => "rendering", _ => "functional" };
        string chosen = Displays[Math.Clamp((int)Display.value, 0, Displays.Length - 1)];
        string mode = "NullDriver", backend = draft.ProxyBackend;
        string? preferred = draft.PreferredNativeMode;
        if (type == "rendering")
        {
            if (Display.value == 0)
            {
                local.Add(new(RunArea.Driver, "Native diagnostic runs the target on its own display driver: choose SDL window, SDL GPU window or Windows console."));
                mode = preferred is { } p && NativeExecution.IsMode(p) ? p : "SDLDriver";
            }
            else
            {
                // An unchanged display keeps the exact native mode (for example a GPU variant chosen in Advanced).
                mode = Display.value == initialDisplay && initialMode == 2 && NativeExecution.IsMode(draft.Mode) && NativeExecution.ActualDriver(draft.Mode) == chosen ? draft.Mode
                    : preferred is { } p && NativeExecution.IsMode(p) && NativeExecution.ActualDriver(p) == chosen ? p : chosen;
                preferred = mode;
                if (!nativeModes.Contains(mode))
                    local.Add(new(RunArea.Driver, $"{WorkbenchCapabilities.TargetDriverName(mode)} is not available for Native diagnostic with this catalog; nothing was substituted. Choose another target display."));
            }
        }
        else backend = chosen;
        var project = draft with
        {
            TestType = type, Mode = mode, PreferredNativeMode = preferred, ProxyBackend = backend,
            // Native diagnostic keeps the driver's own PNG; the draft's native evidence applies to Functional and Visual runs.
            ProxyCapture = type == "rendering" ? ProxyCaptureKinds.None : draft.ProxyCapture,
            RunScope = Scope.value switch { 1 => "group", 2 => "all", 3 when context.Rerun is not null => RerunScope.Scope, _ => "selected" },
            ScreenSizes = sizes, CustomWidth = cw, CustomHeight = ch, Timeout = step, ScenarioTimeout = scenario
        };
        return (project, local);
    }

    private static string Count(int? n) => n is null ? "-" : n == 1 ? "1 scenario" : $"{n} scenarios";
    private static void Enable(TView view, bool enabled)
    {
        if (view.GetState(Views.sfDisabled) == !enabled) return;
        view.SetState(Views.sfDisabled, !enabled); view.DrawView();
    }

    private void Refresh()
    {
        if (!ready) return;
        bool native = Native, visual = Mode.value == 1;
        Enable(CustomSizes, (Sizes.value & 4) != 0);
        if (RerunSelected && SaveDefaults.value != 0) { SaveDefaults.value = 0; SaveDefaults.DrawView(); }
        Enable(SaveDefaults, !RerunSelected);
        // Display rows that cannot be used in the chosen mode are disabled; a selected one stays selected and is reported.
        uint allowed = 1u;
        for (int i = 1; i < Displays.Length; i++)
        {
            bool available = native ? nativeModes.Any(m => NativeExecution.ActualDriver(m) == Displays[i])
                : context.CatalogHasProxy && WorkbenchCapabilities.ProxyUnavailable(Displays[i]) is null;
            if (available) allowed |= 1u << i;
        }
        Display.SetButtonState(~allowed & 0xfu, false); Display.SetButtonState(allowed, true); Display.DrawView();

        int? group = context.ScopeCount("group");
        scopeCounts.SetText($"{Count(context.ScopeCount("selected"))}\n"
            + WorkbenchText.Fit(group is null ? "(not a group)" : Count(group), scopeCounts.size.x)
            + $"\n{Count(context.ScopeCount("all"))}" + (context.Rerun is null ? "" : "\n" + WorkbenchText.Fit(context.Rerun, scopeCounts.size.x)));

        var (p, local) = Read();
        string key = p.Fingerprint() + "|" + SaveDefaults.value + "|" + string.Join("|", local.Select(l => l.Text));
        if (key == assessed) return;
        assessed = key;
        RunAssessment a;
        try { a = context.Assess(p); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        { a = new([new(RunArea.General, ex.Message)], [], "", "", ""); }
        assessment = a;
        problems = local.Concat(a.Problems.Where(x => !(x.Area == RunArea.Sizes && local.Any(l => l.Area == RunArea.Sizes)))).ToArray();

        int lw = sizeNote.size.x, rw = modeNote.size.x;
        var sizeProblem = problems.FirstOrDefault(x => x.Area == RunArea.Sizes);
        sizeNote.SetText(sizeProblem is not null ? Wrap("! " + sizeProblem.Text, lw, 2) : Wrap("Custom: WxH, comma separated. " + a.SizeNote, lw, 2));
        modeNote.SetText(Wrap(Mode.value switch
        {
            1 => "Also compares logical screens with approved baselines.",
            2 => "Audited diagnostic workflows only.",
            _ => "Steps, checkpoints and assertions."
        } + (nativeModes.Length == 0 ? " Native diagnostic unavailable: " + context.NativeUnavailable : ""), rw, 2));

        // Properties... appears only while Visual regression has no baseline manifest (the one thing Basic cannot set itself).
        bool prepare = problems.Any(x => x.Area == RunArea.Target) && context.CanPrepare?.Invoke(p) == true;
        if (prepare) EnableCommand(WorkbenchRunDialog.PrepareCommand); else DisableCommand(WorkbenchRunDialog.PrepareCommand);
        if (prepare != PrepareButton.GetState(Views.sfVisible)) { if (prepare) PrepareButton.Show(); else PrepareButton.Hide(); }
        bool needsStore = visual && p.BaselineStore is null && !prepare;
        if (needsStore != PropertiesButton.GetState(Views.sfVisible)) { if (needsStore) PropertiesButton.Show(); else PropertiesButton.Hide(); }
        summary.SetText(SummaryLines(p, a));
        SetRunAvailable(problems.Length == 0);
    }

    private static string Wrap(string text, int width, int lines)
    {
        var wrapped = WorkbenchText.Wrap(text, Math.Max(1, width)).ToArray();
        if (wrapped.Length > lines) wrapped = [.. wrapped[..(lines - 1)], WorkbenchText.Fit(wrapped[lines - 1] + " ...", width)];
        return string.Join("\n", wrapped);
    }

    // The compact summary: blocking problems first (prominent), then the run, target, driver, output, and every
    // Advanced setting that changes what this run does or writes.
    private string SummaryLines(WorkbenchProject p, RunAssessment a)
    {
        int width = Math.Max(10, summary.size.x), rows = Math.Max(1, summary.size.y);
        var lines = new List<string>();
        foreach (var x in problems.Take(3)) lines.Add("! " + (x.Area == RunArea.Target ? "" : "Cannot run: ") + x.Text);
        if (problems.Length > 3) lines.Add($"! ... and {problems.Length - 3} more problem{(problems.Length - 3 == 1 ? "" : "s")} (Advanced >> lists all).");
        string sizesWord = p.ScreenSizes.Length == 1 ? "size" : "sizes";
        lines.Add($"{a.Scenarios} scenario{(a.Scenarios == 1 ? "" : "s")} x {p.ScreenSizes.Length} {sizesWord} = {a.Executions} execution{(a.Executions == 1 ? "" : "s")}");
        if (a.Target.Length > 0) lines.Add("Target: " + a.Target);
        string driver = p.TestType == "rendering" ? WorkbenchCapabilities.TargetDriverName(p.Mode) + " (Native diagnostic)" : WorkbenchCapabilities.TargetDriverName(p.ProxyBackend);
        lines.Add("Driver: " + driver + "   Output: " + (context.Output.Length > 0 ? context.Output : "the project's runs folder"));
        if (p.TestType == "visual")
        {
            lines.Add(p.BaselineStore is null ? "Visual baselines: Not configured (Properties... sets the manifest)"
                : a.Baselines is { } b ? $"Visual baselines: {b.Approved} approved, {b.Missing} missing" + (b.Invalid > 0 ? $", {b.Invalid} invalid" : "") + $" ({b.Gated} gated)"
                : "Visual baselines: " + a.StoreState);
            lines.Add("Comparison: " + (p.VisualBehavior == "candidates" ? "Compare and keep candidates for review (nothing is approved)" : "Compare only"));
        }
        var active = new List<string>();
        if (p.TestType == "visual" && p.VisualBehavior == "candidates") active.Add("Keep baseline candidates for review (writes candidate files)");
        if (p.TestType != "rendering" && p.ProxyCapture != ProxyCaptureKinds.None) active.Add("Native evidence: " + WorkbenchCapabilities.EvidenceName(p.ProxyCapture));
        if (p.TestType == "rendering" && draft.ProxyCapture != ProxyCaptureKinds.None)
            active.Add($"Native evidence {WorkbenchCapabilities.EvidenceName(draft.ProxyCapture)} is not used by Native diagnostic runs (driver PNG instead)");
        if (p.TestType == "rendering" && p.Mode != NativeExecution.ActualDriver(p.Mode)) active.Add("GPU path: " + WorkbenchCapabilities.TargetDriverName(p.Mode));
        if (p.Timeout is { } t && t != RunFields.StepDefault) active.Add($"step timeout {t} ms");
        if (p.ScenarioTimeout is { } s && s != RunFields.ScenarioDefault) active.Add($"scenario timeout {s} s");
        if (p.Inspect != "declared") active.Add("inspection " + p.Inspect);
        if (active.Count > 0) lines.Add("Advanced options active: " + string.Join("; ", active));
        lines.Add(RerunSelected ? "Re-run: the session configuration and the project defaults stay unchanged."
            : SaveDefaults.value == 1 ? "Also saved as the project defaults (the project is marked changed)."
            : WorkbenchDialog.RunFieldsDiffer(p, context.Defaults) ? "Session only: the project defaults stay unchanged." : "These settings are the project defaults.");
        var wrapped = lines.SelectMany(l => WorkbenchText.Wrap(l, width)).ToList();
        if (wrapped.Count > rows) wrapped = [.. wrapped.Take(rows - 1), WorkbenchText.Fit("... more in Advanced >> Summary", width)];
        return string.Join("\n", wrapped);
    }

    private void SetRunAvailable(bool available)
    {
        if (available) EnableCommand(WorkbenchRunDialog.RunCommand); else DisableCommand(WorkbenchRunDialog.RunCommand);
        Enable(RunButton, available);
    }
    public override void ShutDown() { EnableCommand(WorkbenchRunDialog.RunCommand); EnableCommand(WorkbenchRunDialog.PrepareCommand); base.ShutDown(); }

    public override bool Valid(ushort command)
    {
        if (command == Views.cmOK)
        {
            assessed = ""; Refresh();
            if (problems.Length > 0) return false;
            Accepted = Read().Project; SaveAsDefaults = SaveDefaults.value == 1 && !RerunSelected;
        }
        return base.Valid(command);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && (state & Views.sfModal) != 0)
            switch (ev.message.command)
            {
                case WorkbenchRunDialog.RunCommand: ClearEvent(ref ev); EndModal(Views.cmOK); return;
                case WorkbenchRunDialog.SwitchCommand: ClearEvent(ref ev); EndModal(WorkbenchRunDialog.SwitchCommand); return;
                case PropertiesCommand: ClearEvent(ref ev); EndModal(PropertiesCommand); return;
                case WorkbenchRunDialog.PrepareCommand: ClearEvent(ref ev); EndModal(WorkbenchRunDialog.PrepareCommand); return;
                case TargetCommand:
                    ClearEvent(ref ev);
                    if (context.TargetDetails is { } details) { details(Read().Project); assessed = ""; Refresh(); }
                    return;
            }
        base.HandleEvent(ref ev);
        Refresh();
    }
}
