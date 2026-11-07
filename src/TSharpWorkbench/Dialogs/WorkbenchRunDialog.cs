using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

internal enum RunArea { Scope, Mode, Sizes, Driver, Evidence, Baselines, Limits, General, Target }
internal sealed record RunProblem(RunArea Area, string Text);
// Live evaluation of a requested run (the former DescribeRun/ValidateRun data): blocking problems, summary lines and
// the inline texts of the sizes and baseline sections. adds the compact facts Basic shows.
internal sealed record RunAssessment(RunProblem[] Problems, string[] Summary, string SizeNote, string Store, string StoreState)
{
    internal int Scenarios { get; init; }
    internal int Executions { get; init; }
    // "TVDemo.exe - Ready" or "TVDemo.exe - Not ready: <reason>" (target diagnostics of the selected combination).
    internal string Target { get; init; } = "";
    internal (int Gated, int Approved, int Missing, int Invalid)? Baselines { get; init; }
}
internal sealed record RunDialogContext(string GroupLabel, Func<string, int?> ScopeCount, string[] NativeModes, string NativeUnavailable,
    bool CatalogHasProxy, Func<WorkbenchProject, RunAssessment> Assess, WorkbenchProject Defaults, string? Rerun = null)
{
    // The full target diagnostics of the selected combination (a read-only modal), and where the output goes.
    internal Action<WorkbenchProject>? TargetDetails { get; init; }
    internal string Output { get; init; } = "";
    // Whether Prepare target can remedy the blocked target of this configuration (both presentations offer it).
    internal Func<WorkbenchProject, bool>? CanPrepare { get; init; }
}

// The one configuration both presentations edit. Switching Basic <-> Advanced hands the draft over unchanged,
// including texts that do not parse yet (custom sizes, limits) and the Save-as-defaults choice; nothing is applied.
internal sealed record RunDraft(WorkbenchProject Project, bool SaveDefaults = false)
{
    internal string? CustomText { get; init; }
    internal bool? CustomOn { get; init; }
    internal string? StepText { get; init; }
    internal string? ScenarioText { get; init; }
}

// Reading shared by both presentations: sizes and limits parse with the same rules and report the same problems.
internal static class RunFields
{
    internal const int StepDefault = 10000, ScenarioDefault = 40;
    internal static string CustomText(WorkbenchProject p, RunDraft? draft)
    {
        string[] custom = p.ScreenSizes.Where(s => s is not ("80x25" or "140x40")).ToArray();
        return draft?.CustomText ?? (custom.Length > 0 ? string.Join(",", custom) : ScenarioSelection.Format(p.CustomWidth, p.CustomHeight));
    }
    internal static bool CustomOn(WorkbenchProject p, RunDraft? draft) => draft?.CustomOn ?? p.ScreenSizes.Any(s => s is not ("80x25" or "140x40"));
    internal static string StepText(WorkbenchProject p, RunDraft? draft) => draft?.StepText ?? (p.Timeout ?? StepDefault).ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static string ScenarioText(WorkbenchProject p, RunDraft? draft) => draft?.ScenarioText ?? (p.ScenarioTimeout ?? ScenarioDefault).ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static (string[] Sizes, int Width, int Height) Sizes(WorkbenchProject draft, bool s80, bool s140, bool customOn, string customText, List<RunProblem> local)
    {
        var sizes = new List<string>();
        if (s80) sizes.Add("80x25");
        if (s140) sizes.Add("140x40");
        int cw = draft.CustomWidth, ch = draft.CustomHeight;
        string[] tokens = customText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bad = tokens.Where(t => !ScenarioSelection.TryParseSize(t, out _, out _)).ToArray();
        var good = tokens.Where(t => ScenarioSelection.TryParseSize(t, out _, out _))
            .Select(t => { var (x, y) = ScenarioSelection.ParseSize(t); return ScenarioSelection.Format(x, y); }).Distinct().ToArray();
        if (customOn)
        {
            if (tokens.Length == 0) local.Add(new(RunArea.Sizes, "Enter at least one custom size (WxH), or clear Custom."));
            foreach (string t in bad)
                local.Add(new(RunArea.Sizes, $"Custom size '{t}' is not valid: use WxH, width {GridLimits.MinWidth}..{GridLimits.MaxWidth}, height {GridLimits.MinHeight}..{GridLimits.MaxHeight}."));
            if (good.Length + sizes.Count > 16) local.Add(new(RunArea.Sizes, "At most 16 screen sizes per run."));
            sizes.AddRange(good);
        }
        // Remember the first valid custom size even while Custom is cleared, so re-checking it restores the value.
        if (good.Length > 0) (cw, ch) = ScenarioSelection.ParseSize(good[0]);
        if (sizes.Count == 0 && local.All(p => p.Area != RunArea.Sizes)) local.Add(new(RunArea.Sizes, "Select at least one target screen size."));
        return (sizes.Count == 0 ? draft.ScreenSizes : sizes.Distinct().ToArray(), cw, ch);
    }
    internal static int? Limit(string text, int? current, int fallback, int min, int max, string what, string unit, List<RunProblem> local)
    {
        if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) && value >= min && value <= max)
            return value == (current ?? fallback) ? current : value;
        local.Add(new(RunArea.Limits, $"{what} must be a whole number of {unit} from {min} to {max}."));
        return current;
    }
    internal static TSItem Items(params string[] values)
    { TSItem? item = null; foreach (string value in values.Reverse()) item = new(value, item!); return item!; }
}

// The only run dialog (): edits a copy of the session run configuration, without nested settings dialogs.
// Cancel, Esc and the close icon change nothing. Run returns the configuration, and whether it also becomes the
// project defaults, only while no blocking problem exists; the Run button is disabled until then.
internal sealed class WorkbenchRunDialog : TDialog
{
    internal const string DialogTitle = "Run Configuration";
    // TButton contract: a button's availability is its command's availability, and cmCommandSetChanged
    // re-synchronizes it. Run therefore has its own command, enabled only while no problem exists (never the shared
    // cmOK, which other dialogs need); pressing it ends the dialog with cmOK, which Valid still checks.
    internal const ushort RunCommand = 0x7d9b;
    // Ends the dialog to show the other presentation with the same draft (never runs, never applies).
    internal const ushort SwitchCommand = 0x7d9c;
    // Ends the dialog to prepare the target; Run Configuration reopens afterwards with the same draft.
    internal const ushort PrepareCommand = 0x7d56;
    private const int StepDefault = RunFields.StepDefault, ScenarioDefault = RunFields.ScenarioDefault;
    private readonly RunDialogContext context;
    private readonly WorkbenchProject draft;
    private readonly string[] nativeModes;
    private readonly bool ready;
    private string assessed = "";
    // A native driver of the draft (e.g. a Re-run's recorded driver) that this catalog cannot run: reported, never replaced.
    private readonly string? missingNative;
    private readonly uint initialNative;
    private RunProblem[] problems = [];

    private sealed class Note(TRect bounds, string text = "") : TStaticText(bounds, text)
    {
        internal string Current => Text;
        internal void SetText(string text) { if (Text == text) return; Text = text; DrawView(); }
    }

    internal TGroupBox WhatBox { get; }
    internal TGroupBox ModeBox { get; }
    internal TGroupBox SizesBox { get; }
    internal TGroupBox DriverBox { get; }
    internal TGroupBox EvidenceBox { get; }
    internal TGroupBox BaselinesBox { get; }
    internal TGroupBox LimitsBox { get; }
    internal TRadioButtons Scope { get; }
    internal TRadioButtons Mode { get; }
    internal TCheckBoxes Sizes { get; }
    internal TInputLine CustomSizes { get; }
    internal TRadioButtons ProxyDrivers { get; }
    internal TRadioButtons NativeDrivers { get; }
    internal TRadioButtons Evidence { get; }
    internal TRadioButtons VisualPolicy { get; }
    internal TInputLine StepTimeout { get; }
    internal TInputLine ScenarioTimeout { get; }
    internal TRadioButtons Inspection { get; }
    internal TListBox Summary { get; }
    internal TCheckBoxes SaveDefaults { get; }
    internal TButton RunButton { get; }
    internal TButton BasicButton { get; }
    internal TButton PrepareButton { get; }
    private readonly Note scopeCounts, modeNote, sizeNote, driverNote, evidenceNote, storeLine, storeState, stepRange, scenarioRange, summaryMore;

    internal WorkbenchProject Accepted { get; private set; }
    internal bool SaveAsDefaults { get; private set; }
    internal IReadOnlyList<RunProblem> Problems => problems;
    internal string[] SummaryLines => Enumerable.Range(0, Summary.range).Select(i => Summary.GetText(i, 1000)).ToArray();
    internal string SummaryHint => summaryMore.Current;
    internal string Inline => string.Join("\n", new[] { scopeCounts, modeNote, sizeNote, driverNote, evidenceNote, storeLine, storeState, stepRange, scenarioRange }.Select(n => n.Current));

    // Size (): clamp(W-20, 118, 128) x clamp(H-6, 34, 38) of the screen, limited to the desktop.
    internal static TRect Bounds(TDeskTop desktop)
    {
        int w = Math.Min(Math.Clamp(desktop.size.x - 20, 118, 128), desktop.size.x);
        int h = Math.Min(Math.Clamp(desktop.size.y - 4, 34, 38), desktop.size.y);
        return new(0, 0, w, h);
    }

    // The configuration and texts to hand to the other presentation.
    internal RunDraft Draft => new(Read().Project, SaveDefaults.value == 1 && !RerunSelected)
        { CustomText = CustomSizes.Data, CustomOn = (Sizes.value & 4) != 0, StepText = StepTimeout.Data, ScenarioText = ScenarioTimeout.Data };

    internal WorkbenchRunDialog(TDeskTop desktop, WorkbenchProject session, RunDialogContext context, RunDraft? handover = null)
        : base(Bounds(desktop), DialogTitle)
    {
        session = handover?.Project ?? session;
        this.context = context; draft = session; Accepted = session;
        nativeModes = context.NativeModes;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y, lw = (w - 5) / 2, rx = 2 + lw + 1, rw = w - 2 - rx;
        bool native = session.TestType == "rendering";
        // Re-run from Results adds a fourth scope (one row); the left column moves down by that row.
        int extra = context.Rerun is null ? 0 : 1;

        // Insertion order is the Tab order: What to run, Test mode, sizes, driver, evidence, baselines, limits.
        WhatBox = new(new(2, 1, 2 + lw, 6 + extra), "What to run");
        Scope = new(new(2, 1, 30, 4 + extra), extra == 0 ? Items("~M~arked scenarios", "Current ~g~roup", "~A~ll runnable scenarios")
            : Items("~M~arked scenarios", "Current ~g~roup", "~A~ll runnable scenarios", "Re-run from Re~s~ults"));   // Alt+E is Inspection
        Scope.SetData(session.RunScope switch { "group" => 1, "all" => 2, RerunScope.Scope when extra == 1 => 3, _ => 0 }); WhatBox.Insert(Scope);
        scopeCounts = new(new(31, 1, lw - 2, 4 + extra)); WhatBox.Insert(scopeCounts);
        Insert(WhatBox);

        ModeBox = new(new(rx, 1, rx + rw, 8), "Test mode");
        Mode = new(new(2, 1, 24, 4), Items("~F~unctional", "~V~isual regression", "~N~ative driver"));
        Mode.SetData(session.TestType switch { "visual" => 1, "rendering" => 2, _ => 0 }); ModeBox.Insert(Mode);
        ModeBox.Insert(new TStaticText(new(25, 1, rw - 2, 4), "steps and assertions\ncompare with baselines\naudited workflows only"));
        modeNote = new(new(2, 4, rw - 2, 6)); ModeBox.Insert(modeNote);
        if (nativeModes.Length == 0) Mode.SetButtonState(4, false);
        Insert(ModeBox);

        SizesBox = new(new(2, 6 + extra, 2 + lw, 12 + extra), "Target screen sizes");
        Sizes = new(new(2, 1, 36, 2), Items("~8~0x25", "~1~40x40", "~C~ustom"));
        Sizes.value = (session.ScreenSizes.Contains("80x25") ? 1u : 0u) | (session.ScreenSizes.Contains("140x40") ? 2u : 0u) | (RunFields.CustomOn(session, handover) ? 4u : 0u);
        SizesBox.Insert(Sizes);
        CustomSizes = new(new(37, 1, lw - 2, 2), 200);
        CustomSizes.SetData(RunFields.CustomText(session, handover));
        SizesBox.Insert(CustomSizes);
        SizesBox.Insert(new TStaticText(new(2, 2, lw - 2, 3), $"Custom: WxH, comma separated, {GridLimits.MinWidth}x{GridLimits.MinHeight}..{GridLimits.MaxWidth}x{GridLimits.MaxHeight}"));
        sizeNote = new(new(2, 3, lw - 2, 5)); SizesBox.Insert(sizeNote);
        Insert(SizesBox);

        DriverBox = new(new(rx, 8, rx + rw, 16), "Target display driver");
        ProxyDrivers = new(new(2, 1, rw - 2, 5), Items(WorkbenchCapabilities.ProxyBackends.Select(WorkbenchCapabilities.ProxyChoice).ToArray()));
        ProxyDrivers.SetData(Math.Max(0, Array.IndexOf(WorkbenchCapabilities.ProxyBackends, session.ProxyBackend)));
        for (int i = 1; i < WorkbenchCapabilities.ProxyBackends.Length; i++)
            if (!context.CatalogHasProxy || WorkbenchCapabilities.ProxyUnavailable(WorkbenchCapabilities.ProxyBackends[i]) is not null) ProxyDrivers.SetButtonState(1u << i, false);
        DriverBox.Insert(ProxyDrivers);
        NativeDrivers = new(new(2, 1, rw - 2, 1 + Math.Max(1, nativeModes.Length)),
            Items(nativeModes.Length == 0 ? ["No native driver for this catalog"] : nativeModes.Select(WorkbenchCapabilities.NativeChoice).ToArray()));
        string preferred = session.PreferredNativeMode ?? (NativeExecution.IsMode(session.Mode) ? session.Mode : "SDLDriver");
        NativeDrivers.SetData(Math.Max(0, Array.IndexOf(nativeModes, native && NativeExecution.IsMode(session.Mode) ? session.Mode : preferred)));
        if (nativeModes.Length == 0) NativeDrivers.SetButtonState(1, false);
        if (native && NativeExecution.IsMode(session.Mode) && !nativeModes.Contains(session.Mode)) missingNative = session.Mode;
        initialNative = NativeDrivers.value;
        DriverBox.Insert(NativeDrivers);
        driverNote = new(new(2, 6, rw - 2, 7)); DriverBox.Insert(driverNote);
        Insert(DriverBox);

        EvidenceBox = new(new(rx, 16, rx + rw, 23), "Native evidence (next to logical checkpoints)");
        Evidence = new(new(2, 1, rw - 2, 5), Items(ProxyCaptureKinds.All.Select(WorkbenchCapabilities.EvidenceChoice).ToArray()));
        Evidence.SetData(Math.Max(0, Array.IndexOf(ProxyCaptureKinds.All, session.ProxyCapture))); EvidenceBox.Insert(Evidence);
        evidenceNote = new(new(2, 5, rw - 2, 6)); EvidenceBox.Insert(evidenceNote);
        Insert(EvidenceBox);

        BaselinesBox = new(new(2, 12 + extra, 2 + lw, 18 + extra), "Visual baselines");
        storeLine = new(new(2, 1, lw - 2, 2)); BaselinesBox.Insert(storeLine);
        storeState = new(new(2, 2, lw - 2, 3)); BaselinesBox.Insert(storeState);
        VisualPolicy = new(new(2, 3, lw - 2, 5), Items("Compare with approved baselines", "Compare and ~k~eep candidates for review"));
        VisualPolicy.SetData(session.VisualBehavior == "candidates" ? 1 : 0); BaselinesBox.Insert(VisualPolicy);
        Insert(BaselinesBox);

        LimitsBox = new(new(2, 18 + extra, 2 + lw, 23 + extra), "Limits and inspection");
        StepTimeout = new(new(19, 1, 27, 2), 6); StepTimeout.SetData(RunFields.StepText(session, handover));
        LimitsBox.Insert(StepTimeout); LimitsBox.Insert(new TLabel(new(1, 1, 18, 2), "Step ~t~imeout", StepTimeout));
        stepRange = new(new(28, 1, lw - 2, 2)); LimitsBox.Insert(stepRange);
        ScenarioTimeout = new(new(19, 2, 27, 3), 4); ScenarioTimeout.SetData(RunFields.ScenarioText(session, handover));
        LimitsBox.Insert(ScenarioTimeout); LimitsBox.Insert(new TLabel(new(1, 2, 18, 3), "Scenario t~i~meout", ScenarioTimeout));
        scenarioRange = new(new(28, 2, lw - 2, 3)); LimitsBox.Insert(scenarioRange);
        Inspection = new(new(19, 3, Math.Min(lw - 2, 57), 4), Items("Declared", "All", "None"));
        Inspection.SetData(session.Inspect switch { "all" => 1, "none" => 2, _ => 0 });
        LimitsBox.Insert(Inspection); LimitsBox.Insert(new TLabel(new(1, 3, 18, 4), "Insp~e~ction", Inspection));
        Insert(LimitsBox);

        // A free row separates the summary from the check box where the height allows it (160x50); at 34 rows they are adjacent.
        int summaryBottom = h > 34 ? h - 6 : h - 5;
        var scroll = new TScrollBar(new(w - 3, 24 + extra, w - 2, summaryBottom)); Insert(scroll);
        Summary = new(new(2, 24 + extra, w - 3, summaryBottom), 1, scroll); Insert(Summary);
        Insert(new TLabel(new(1, 23 + extra, 12, 24 + extra), "S~u~mmary", Summary));
        // At 34 rows (120x36) the list shows five rows; say when more follow, so no reason stays unnoticed.
        summaryMore = new(new(12, 23 + extra, w - 3, 24 + extra)); Insert(summaryMore);
        // Unchecked on every open (): running changes only the session configuration unless this is ticked.
        // A switch from Basic hands over the choice made there in this same dialog session.
        SaveDefaults = new(new(2, h - 5, 48, h - 4), Items("Save these settings as project ~d~efaults")); Insert(SaveDefaults);
        if (handover?.SaveDefaults == true) SaveDefaults.value = 1;
        // Owner spacing rule: one empty row above the buttons; the button shadow row stays above the bottom frame.
        BasicButton = new(new(2, h - 3, 15, h - 1), "<< ~B~asic", SwitchCommand, ButtonConstants.bfNormal); Insert(BasicButton);
        // No free mnemonic letter remains in this dialog for "Prepare" (Basic offers Alt+P); Tab or the mouse reach it.
        PrepareButton = new(new(16, h - 3, 36, h - 1), "Prepare target...", PrepareCommand, ButtonConstants.bfNormal); Insert(PrepareButton);
        RunButton = new(new(w - 34, h - 3, w - 19, h - 1), "~R~un", RunCommand, ButtonConstants.bfDefault); Insert(RunButton);
        Insert(new TButton(new(w - 17, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        ready = true; Refresh();
        // Default focus is Run, so F5 Enter repeats the session configuration; with a problem, focus its section.
        if (problems.Length == 0) RunButton.Select(); else (SectionOf(problems[0].Area) ?? (TView)WhatBox).Select();
    }

    private static TSItem Items(params string[] values)
    { TSItem? item = null; foreach (string value in values.Reverse()) item = new(value, item!); return item!; }

    private TView? SectionOf(RunArea area) => area switch
    {
        RunArea.Scope => WhatBox, RunArea.Mode => ModeBox, RunArea.Sizes => SizesBox, RunArea.Driver => DriverBox,
        RunArea.Evidence => EvidenceBox, RunArea.Baselines => BaselinesBox, RunArea.Limits => LimitsBox, _ => null
    };

    private bool NativeMode => Mode.value == 2;
    internal bool RerunSelected => context.Rerun is not null && Scope.value == 3;
    private string SelectedBackend => WorkbenchCapabilities.ProxyBackends[Math.Clamp((int)ProxyDrivers.value, 0, WorkbenchCapabilities.ProxyBackends.Length - 1)];
    private string? SelectedNative => nativeModes.Length == 0 ? null : nativeModes[Math.Clamp((int)NativeDrivers.value, 0, nativeModes.Length - 1)];

    // Reads the controls into a configuration; values that cannot be parsed keep the session value and are reported.
    private (WorkbenchProject Project, List<RunProblem> Local) Read()
    {
        var local = new List<RunProblem>();
        var (sizes, cw, ch) = RunFields.Sizes(draft, (Sizes.value & 1) != 0, (Sizes.value & 2) != 0, (Sizes.value & 4) != 0, CustomSizes.Data, local);
        if (NativeMode && missingNative is not null && NativeDrivers.value == initialNative)
            local.Add(new(RunArea.Driver, $"{WorkbenchCapabilities.TargetDriverName(missingNative)} is not available for this catalog; nothing was substituted. Choose another target display driver."));
        int? step = RunFields.Limit(StepTimeout.Data, draft.Timeout, StepDefault, 100, 30000, "Step timeout", "milliseconds", local);
        int? scenario = RunFields.Limit(ScenarioTimeout.Data, draft.ScenarioTimeout, ScenarioDefault, 5, 600, "Scenario timeout", "seconds", local);

        string type = Mode.value switch { 1 => "visual", 2 => "rendering", _ => "functional" };
        string nativeMode = SelectedNative ?? draft.PreferredNativeMode ?? "SDLDriver";
        var p = draft with
        {
            TestType = type, Mode = type == "rendering" ? nativeMode : "NullDriver",
            PreferredNativeMode = SelectedNative ?? draft.PreferredNativeMode,
            RunScope = Scope.value switch { 1 => "group", 2 => "all", 3 when context.Rerun is not null => RerunScope.Scope, _ => "selected" },
            ProxyBackend = SelectedBackend, ProxyCapture = ProxyCaptureKinds.All[Math.Clamp((int)Evidence.value, 0, ProxyCaptureKinds.All.Length - 1)],
            VisualBehavior = VisualPolicy.value == 1 ? "candidates" : "compare",
            Inspect = Inspection.value switch { 1 => "all", 2 => "none", _ => "declared" },
            ScreenSizes = sizes, CustomWidth = cw, CustomHeight = ch,
            Timeout = step, ScenarioTimeout = scenario
        };
        // The assessment uses the requested sizes; an empty selection is already a local problem.
        return (p, local);
    }

    private static string Count(int? n) => n is null ? "-" : n == 1 ? "1 scenario" : $"{n} scenarios";
    private static void Enable(TView view, bool enabled)
    {
        if (view.GetState(Views.sfDisabled) == !enabled) return;
        view.SetState(Views.sfDisabled, !enabled); view.DrawView();
    }
    private static void Show(TView view, bool visible)
    {
        if (view.GetState(Views.sfVisible) == visible) return;
        if (visible) view.Show(); else view.Hide();
    }

    private void Refresh()
    {
        if (!ready) return;
        bool native = NativeMode, visual = Mode.value == 1;
        // Sections that do not apply stay visible and explain why.
        Show(ProxyDrivers, !native); Show(NativeDrivers, native);
        Enable(CustomSizes, (Sizes.value & 4) != 0);
        Enable(VisualPolicy, visual);
        // A Re-run is session-only: it can never become the project defaults.
        if (RerunSelected && SaveDefaults.value != 0) { SaveDefaults.value = 0; SaveDefaults.DrawView(); }
        Enable(SaveDefaults, !RerunSelected);
        string backend = SelectedBackend;
        uint allowed = 1u;
        if (!native) for (int i = 1; i < ProxyCaptureKinds.All.Length; i++) if (ProxyCaptureKinds.ForBackend(backend).Contains(ProxyCaptureKinds.All[i])) allowed |= 1u << i;
        Evidence.SetButtonState(~allowed & 0xfu, false); Evidence.SetButtonState(allowed, true); Evidence.DrawView();
        // A section whose controls are all disabled stays visible but is skipped by Tab, so focus never lands on an empty box.
        foreach (var box in new[] { WhatBox, ModeBox, SizesBox, DriverBox, EvidenceBox, BaselinesBox, LimitsBox })
        {
            bool any = false;
            for (TView? v = box.last?.Next; v is not null; v = v.Next)
            {
                if ((v.options & Views.ofSelectable) != 0 && (v.state & (Views.sfVisible | Views.sfDisabled)) == Views.sfVisible) { any = true; break; }
                if (ReferenceEquals(v, box.last)) break;
            }
            if (any) box.options |= Views.ofSelectable; else box.options &= unchecked((ushort)~Views.ofSelectable);
        }

        int? group = context.ScopeCount("group");
        scopeCounts.SetText($"{Count(context.ScopeCount("selected"))}\n"
            + WorkbenchText.Fit(group is null ? context.GroupLabel + " (not a group)" : $"{context.GroupLabel}: {Count(group)}", scopeCounts.size.x)
            + $"\n{Count(context.ScopeCount("all"))}" + (context.Rerun is null ? "" : "\n" + WorkbenchText.Fit(context.Rerun, scopeCounts.size.x)));
        string description = Mode.value switch
        {
            1 => "Also compares logical screens with baselines.",
            2 => "Audited diagnostic workflows only (compiled or bound to an audited fixture).",
            _ => "Steps, checkpoints and assertions."
        };
        modeNote.SetText(description + (nativeModes.Length == 0 ? "\n" + WorkbenchText.Fit("Native driver unavailable: " + context.NativeUnavailable, modeNote.size.x) : ""));

        var (p, local) = Read();
        string key = p.Fingerprint() + "|" + SaveDefaults.value + "|" + string.Join("|", local.Select(l => l.Text));
        if (key == assessed) return;
        assessed = key;
        RunAssessment a;
        try { a = context.Assess(p); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        { a = new([new(RunArea.General, ex.Message)], [], "", "", ""); }
        problems = local.Concat(a.Problems.Where(x => !(x.Area == RunArea.Sizes && local.Any(l => l.Area == RunArea.Sizes)))).ToArray();

        string Inline(RunArea area, string fallback, int width, int lines)
        {
            var found = problems.Where(x => x.Area == area).Select(x => "! " + x.Text).ToArray();
            string text = found.Length == 0 ? fallback : string.Join("\n", found);
            var wrapped = WorkbenchText.Wrap(text, width).ToArray();
            if (wrapped.Length > lines) wrapped = [.. wrapped[..(lines - 1)], WorkbenchText.Fit(wrapped[lines - 1] + " (see Summary)", width)];
            return string.Join("\n", wrapped);
        }
        int lw = sizeNote.size.x, rw = driverNote.size.x;
        sizeNote.SetText(Inline(RunArea.Sizes, a.SizeNote, lw, 2));
        string driverFallback = native ? "The target's own driver; the Workbench window keeps its own."
            : context.CatalogHasProxy ? "Target only; never changes the Workbench window."
            : "Window drivers need application (Proxy) scenarios.";
        driverNote.SetText(Inline(RunArea.Driver, driverFallback, rw, 1));
        evidenceNote.SetText(Inline(RunArea.Evidence, native ? "Native driver mode keeps the driver's own native PNG."
            : backend == "NullDriver" ? "Choose a window driver to collect native evidence." : "Never shown as a logical PNG.", evidenceNote.size.x, 1));
        storeLine.SetText(WorkbenchText.Fit("Store   " + a.Store, storeLine.size.x));
        storeState.SetText(Inline(RunArea.Baselines, visual ? a.StoreState : "Not used: only Visual regression compares baselines.", storeState.size.x, 1));
        bool stepBad = local.Any(x => x.Text.StartsWith("Step", StringComparison.Ordinal)), scenarioBad = local.Any(x => x.Text.StartsWith("Scenario", StringComparison.Ordinal));
        stepRange.SetText((stepBad ? "! " : "") + "ms  (100..30000)");
        scenarioRange.SetText((scenarioBad ? "! " : "") + "s   (5..600)");

        var lines = new List<string>();
        foreach (var x in problems) lines.Add("! " + (problems.Length == 1 ? "Cannot run: " : "") + x.Text);
        if (problems.Length > 0) lines.Add("");
        lines.AddRange(a.Summary.Select(s => problems.Length > 0 && s.StartsWith("Runs ", StringComparison.Ordinal) ? "Would run " + s[5..] : s));
        lines.Add(DefaultsText(p));
        lines.Add(problems.Length == 0 ? "Ready to run." : $"! Run is unavailable: {problems.Length} problem{(problems.Length == 1 ? "" : "s")}.");
        var rows = new TStringCollection(); int width = Math.Max(1, Summary.size.x - 1);
        foreach (string line in lines) foreach (string row in WorkbenchText.Wrap(line, width)) rows.Insert(row);
        int top = Summary.topItem; Summary.NewList(rows); Summary.topItem = Math.Clamp(top, 0, Math.Max(0, rows.Count - 1));
        summaryMore.SetText(rows.Count <= Summary.size.y ? ""
            : WorkbenchText.Fit($"{rows.Count} lines, {Summary.size.y} shown: Alt+U, then Down or PgDn reads all", summaryMore.size.x));
        SetRunAvailable(problems.Length == 0);
        // Prepare target is offered only while the target deployment blocks this run and preparation can help.
        bool prepare = problems.Any(x => x.Area == RunArea.Target) && context.CanPrepare?.Invoke(p) == true;
        if (prepare) EnableCommand(PrepareCommand); else DisableCommand(PrepareCommand);
        if (prepare != PrepareButton.GetState(Views.sfVisible)) { if (prepare) PrepareButton.Show(); else PrepareButton.Hide(); }
    }
    private void SetRunAvailable(bool available)
    {
        if (available) EnableCommand(RunCommand); else DisableCommand(RunCommand);
        Enable(RunButton, available);   // the state the next cmCommandSetChanged derives, applied now
    }
    public override void ShutDown() { EnableCommand(RunCommand); EnableCommand(PrepareCommand); base.ShutDown(); }

    private string DefaultsText(WorkbenchProject p)
    {
        if (RerunSelected) return "Re-run: the session configuration and the project defaults stay unchanged.";
        bool differs = WorkbenchDialog.RunFieldsDiffer(p, context.Defaults);
        if (SaveDefaults.value == 1)
            return differs ? "These settings also become the project defaults (the project is marked changed; Ctrl+S saves it)."
                : "These settings already are the project defaults.";
        return differs ? "Session only: the project defaults stay unchanged (the command-line runner keeps using them)."
            : "These settings are the project defaults.";
    }

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
        if (ev.What == Events.evCommand && ev.message.command == RunCommand && (state & Views.sfModal) != 0) { ClearEvent(ref ev); EndModal(Views.cmOK); return; }
        if (ev.What == Events.evCommand && ev.message.command == SwitchCommand && (state & Views.sfModal) != 0) { ClearEvent(ref ev); EndModal(SwitchCommand); return; }
        if (ev.What == Events.evCommand && ev.message.command == PrepareCommand && (state & Views.sfModal) != 0) { ClearEvent(ref ev); EndModal(PrepareCommand); return; }
        int before = (int)Mode.value;
        base.HandleEvent(ref ev);
        // Switching to Native driver clears proxy evidence: native runs keep the driver's own PNG instead.
        if (before != 2 && Mode.value == 2 && Evidence.value != 0) { Evidence.value = 0; Evidence.DrawView(); }
        Refresh();
    }
}
