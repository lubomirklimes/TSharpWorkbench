using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// TSW PRODUCT-3: Fix Issues. A modal dialog (a concise plan + confirmation, like Target Diagnostics): the plan view lists
// every finding of the target diagnostics with what Fix Issues would do about it (FIX / OK / MANUAL / REFUSED), the
// selected row's source artifact, expected identity, destination, conflict and permission notes. Nothing is written until
// Fix issues... is confirmed (Cancel is the default). The repair runs in the background, Close/Esc cancel it and wait for
// the rollback; afterwards the diagnostics run again and the rows show FIXED / UNRESOLVED / MANUAL / REFUSED. "Fix and
// verify" then starts the target once, headless, through the existing Verify target lifecycle.
internal sealed class WorkbenchFixIssuesDialog : TDialog
{
    internal const string DialogTitle = "Fix Issues", ConfirmTitle = "Fix Issues - Confirm", VerifyTitle = "Verify Target";
    internal const ushort FixCommand = 0x7d31, VerifyCommand = 0x7d32, CompatibilityCommand = 0x7d58;
    internal const string CompatibilityTitle = "Target Compatibility";
    private const ushort Finished = 0x7d33, Progressed = 0x7d34, Verified = 0x7d35;
    private readonly TDeskTop desktop;
    private readonly TargetRepairService service;
    private readonly string executable;
    private readonly Func<TargetReport> diagnose;
    private readonly DiagnosticContext context;
    private readonly Func<CancellationToken, Task<TargetVerification>>? verify;
    private readonly Action<TargetVerification?, StartupFailure?>? record;
    private readonly Func<bool> runActive;
    private readonly string verifyText;
    private readonly TListBox list;
    private readonly TCheckBoxes replace;
    private readonly Line modeNote, targetLine, summary, details, status;
    // The mode chooser (1 = Fix the original target; choosing 0 hands over to Prepare a copy).
    internal TRadioButtons Mode { get; }
    private RepairItem[] rows = [];
    private int shownFocus = -1;
    private uint replaceValue;
    private CancellationTokenSource? running;
    // provisional: a "Fix and verify" beside another build of the same framework version; the added files
    // stay only once Verify target succeeded (a failed, cancelled or abandoned verification removes them again).
    private bool closeAfter, verifyAfter, provisional;
    internal RepairPlan Plan { get; private set; }
    internal RepairOutcome? Outcome { get; private set; }
    internal TargetVerification? Verification { get; private set; }
    internal TButton FixButton { get; }
    internal TButton VerifyButton { get; }
    internal TButton CompatibilityButton { get; }
    // Test seams: the answer of the confirmation (cmYes = Fix and verify, cmNo = Fix only), and the automatic removal
    // after a failed verification.
    internal Func<ushort>? ConfirmAnswer { get; init; }
    internal Func<RepairOutcome, (bool Complete, string[] Notes)>? Undo { get; init; }
    internal bool Provisional => provisional;
    internal TCheckBoxes ReplaceOption => replace;
    internal TListBox Items => list;
    internal string StatusText => status.Current;
    internal string SummaryText => summary.Current;
    internal string DetailText => details.Current;
    internal string[] Rows => rows.Select(Row).ToArray();
    internal bool Busy => running is not null;

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        // A line with more than one row wraps its text instead of cutting it.
        public override void GetText(out string result) => result = string.Join("\n", (size.y > 1 ? Text.Split('\n').SelectMany(l => WorkbenchText.Wrap(l, size.x)) : Text.Split('\n'))
            .Take(size.y).Select(l => WorkbenchText.Fit(l, size.x)));
    }
    private sealed class Done(RepairOutcome? outcome, Exception? error) : IInfo
    {
        internal RepairOutcome? Outcome { get; } = outcome;
        internal Exception? Error { get; } = error;
    }
    private sealed class Step(PreparationProgress progress) : IInfo { internal PreparationProgress Progress { get; } = progress; }
    private sealed class Checked(TargetVerification? result, Exception? error) : IInfo
    {
        internal TargetVerification? Result { get; } = result;
        internal Exception? Error { get; } = error;
    }
    // At most one pending progress event: the worker never floods the UI queue.
    private sealed class Reporter(WorkbenchFixIssuesDialog owner) : IProgress<PreparationProgress>
    {
        private int pending;
        public void Report(PreparationProgress value) { if (Interlocked.Exchange(ref pending, 1) == 0) owner.Post(Progressed, new Step(value)); }
        internal void Shown() => Interlocked.Exchange(ref pending, 0);
    }
    private readonly Reporter reporter;

    internal static TRect Bounds(TDeskTop desktop) => new(0, 0, Math.Min(112, desktop.size.x - 2), Math.Min(32, desktop.size.y));

    // diagnose: the full diagnostics of the target (UI thread); context: the diagnosed context, used for the plan and the
    // background re-check; verify: null when this target cannot be verified here.
    internal WorkbenchFixIssuesDialog(TDeskTop desktop, TargetRepairService service, string executable, Func<TargetReport> diagnose, DiagnosticContext context,
        Func<CancellationToken, Task<TargetVerification>>? verify, Action<TargetVerification?, StartupFailure?>? record, Func<bool> runActive, string verifyText)
        : base(Bounds(desktop), DialogTitle)
    {
        this.desktop = desktop; this.service = service; this.executable = executable; this.diagnose = diagnose; this.context = context;
        this.verify = verify; this.record = record; this.runActive = runActive; this.verifyText = verifyText;
        reporter = new Reporter(this);
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y;
        // The mode chooser of Prepare Target, at the same place (Fix the original target selected).
        Mode = PreparationModes.Insert(this, PreparationModes.Fix);
        modeNote = new Line(new(3, 2, w - 2, 3)); Insert(modeNote); modeNote.Set(PreparationModes.Note(PreparationModes.Fix));
        targetLine = new Line(new(2, 3, w - 2, 4)); Insert(targetLine);
        // The summary has two rows (it is never cut at 120x36); the list starts one row lower.
        summary = new Line(new(2, 4, w - 2, 6)); Insert(summary);
        var scroll = new TScrollBar(new(w - 3, 7, w - 2, h - 13)); Insert(scroll);
        list = new TListBox(new(2, 7, w - 3, h - 13), 1, scroll); Insert(list);
        Insert(new TLabel(new(1, 6, 40, 7), "~I~ssues and what Fix Issues does", list));
        details = new Line(new(2, h - 12, w - 2, h - 6)); Insert(details);
        // One option row: replacing an earlier Workbench build (verified backup). No acknowledgment exists any
        // more; the framework version decides.
        replace = new TCheckBoxes(new(2, h - 6, 76, h - 5), new TSItem("~R~eplace a different Workbench build (verified backup, rollback)", null!));
        Insert(replace);
        status = new Line(new(2, h - 5, w - 2, h - 3)); Insert(status);
        // Owner spacing rule: the status lines, then the buttons on the last rows above the bottom frame.
        FixButton = new(new(2, h - 3, 20, h - 1), "~F~ix issues...", FixCommand, ButtonConstants.bfNormal); Insert(FixButton);
        VerifyButton = new(new(21, h - 3, 41, h - 1), "~V~erify target...", VerifyCommand, ButtonConstants.bfNormal); Insert(VerifyButton);
        CompatibilityButton = new(new(42, h - 3, 62, h - 1), "Com~p~atibility...", CompatibilityCommand, ButtonConstants.bfNormal); Insert(CompatibilityButton);
        var close = new TButton(new(w - 15, h - 3, w - 3, h - 1), "Close", Views.cmCancel, ButtonConstants.bfDefault); Insert(close);
        Plan = Replan(false);
        Show(Plan.Items);
        status.Set(Plan.Healthy ? "Nothing to fix. Verify target confirms runtime activation." : Plan.CanRepair
            ? "Nothing has been changed. Fix issues... shows exactly what will be copied and asks first."
            : Plan.RequiresRebuild ? "Fix is disabled: " + PreparationVerdict.RebuildAdvice(service.Service.Payload?.FrameworkVersion ?? "")
            : "Nothing can be fixed safely, so Fix is disabled. Each REFUSED, BLOCKED or MANUAL row says why and what to do.");
        list.Select();
    }

    // The plan is computed with the static diagnostics of the diagnosed context (the same function the background
    // re-check uses), so the confirmed plan can be compared with the one executed.
    private TargetReport Static(string exe) => TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), context);
    private RepairPlan Replan(bool allowReplace)
    {
        TargetDiagnostics.Forget(executable); TargetPreparationService.ForgetStatus();
        var plan = service.Plan(executable, Static(executable), allowReplace, runActive());
        targetLine.Set($"Target   {Path.GetFileName(plan.Executable)}   in {WorkbenchText.Path(plan.Folder, Math.Max(10, size.x - 20 - Path.GetFileName(plan.Executable).Length))}");
        summary.Set(plan.Counts + "  |  " + plan.Summary);
        // The replace option exists only for a different Workbench Proxy/Testing build; it is off until the user ticks it.
        replace.SetState(Views.sfDisabled, !plan.NeedsReplaceApproval); replace.DrawView();
        UpdateButtons(plan);
        return plan;
    }
    private void UpdateButtons(RepairPlan plan)
    {
        bool fix = running is null && Outcome is not { Success: true } && plan.CanRepair, check = running is null && verify is not null;
        bool compatibility = running is null && plan.Install is not null;
        if (fix) EnableCommand(FixCommand); else DisableCommand(FixCommand);
        if (check) EnableCommand(VerifyCommand); else DisableCommand(VerifyCommand);
        if (compatibility) EnableCommand(CompatibilityCommand); else DisableCommand(CompatibilityCommand);
        FixButton.SetState(Views.sfDisabled, !fix); FixButton.DrawView();
        VerifyButton.SetState(Views.sfDisabled, !check); VerifyButton.DrawView();
        if (CompatibilityButton is not null) { CompatibilityButton.SetState(Views.sfDisabled, !compatibility); CompatibilityButton.DrawView(); }
    }

    // ── compatibility ─────────────────────────────────────────────

    // Both builds side by side (core and drivers), how they are compared (by version; SHA-256 and MVID are information)
    // and what Verify target proves. Read-only. For another build of the same version the static API check is listed as
    // information; it never decides.
    internal string[] CompatibilityLines()
    {
        var payload = service.Service.Payload;
        var framework = Plan.Install?.Framework;
        string state = framework is null ? "not inspected" : framework.Match switch
        {
            FrameworkMatch.Identical => "Compatible - identical build",
            FrameworkMatch.SameVersion => "Compatible - same version, other build (Verify target confirms it)",
            FrameworkMatch.SameMajor => "Compatible major - Verify target and scenarios confirm runtime support",
            FrameworkMatch.DifferentVersion => PreparationVerdict.RequiresRebuild + " (different version)",
            _ => "Not established"
        };
        var lines = new List<string> { $"Compatibility of {Path.GetFileName(Plan.Executable)} with this Workbench's Proxy", $"State        {state}", "" };
        string deps = Path.ChangeExtension(Plan.Install?.Inspection.Report.Inspection.Main?.Path ?? Plan.Executable, ".deps.json");
        foreach (string file in new[] { "TSharpVision.dll", "TSharpVision.Drivers.SDL.dll", "TSharpVision.Drivers.Console.dll" })
        {
            string app = Path.Combine(Plan.Folder, file), own = payload is null ? "" : Path.Combine(payload.Directory, file);
            string origin = PreparationVerdict.ProjectReferenced(deps, Path.GetFileNameWithoutExtension(file)) ? " (project reference: built from source)" : File.Exists(app) ? " (package)" : "";
            bool both = File.Exists(app) && File.Exists(own);
            string have = both ? FrameworkCompatibility.Declared(TargetDiagnostics.ReadAssembly(app)) : "", want = both ? FrameworkCompatibility.Declared(TargetDiagnostics.ReadAssembly(own)) : "";
            lines.Add(file);
            lines.Add("  application  " + TargetRepairService.Identity(app) + origin);
            lines.Add("  Proxy built  " + (File.Exists(own) ? TargetRepairService.Identity(own).Replace("application: ", "") + " (payload)" : "-"));
            lines.Add("  version      " + (!both ? "-" : string.Equals(have, want, StringComparison.OrdinalIgnoreCase) ? $"same ({have})" : $"DIFFERENT ({have} vs {want})"));
            lines.Add("  same build   " + (both ? (Preparation.FileHash(app) == Preparation.FileHash(own) ? "yes (identical content)" : "no - other build (information only)") : "-"));
        }
        lines.Add("");
        lines.AddRange(WorkbenchText.Wrap("How this is decided: " + FrameworkCompatibility.Policy, 100));
        lines.Add("");
        lines.AddRange(WorkbenchText.Wrap("Runtime limits: " + FrameworkCompatibility.RuntimeLimits, 100));
        if (framework?.Match is FrameworkMatch.SameVersion or FrameworkMatch.SameMajor && payload is not null)
        {
            var probe = TargetPreparationService.ProbeFor(payload, Plan.Folder);
            lines.Add("");
            lines.AddRange(WorkbenchText.Wrap("Static API check (information only, never a gate): " + probe.Summary, 100));
            lines.AddRange(probe.Hard.Take(20).Select(h => "  ! " + h));
            if (probe.Hard.Length > 20) lines.Add($"  ... and {probe.Hard.Length - 20} more");
        }
        if (Plan.RequiresRebuild)
        {
            lines.Add("");
            lines.AddRange(WorkbenchText.Wrap("Refused: " + (Plan.Blockers.FirstOrDefault() ?? ""), 100));
            lines.AddRange(WorkbenchText.Wrap("What would be needed: " + PreparationVerdict.RebuildAdvice(payload?.FrameworkVersion ?? ""), 100));
        }
        return lines.ToArray();
    }

    private static string Row(RepairItem i) => $"{RepairItem.Label(i.State),-10} {(i.Code.Length == 0 ? "-" : i.Code),-9} {i.Name,-32} {i.Summary}";
    private void Show(RepairItem[] items)
    {
        // Rows that need attention first, then what is already fine.
        rows = items.OrderBy(i => i.State switch { RepairState.Fix or RepairState.Unresolved => 0, RepairState.Refused => 1, RepairState.Blocked => 2, RepairState.Manual => 3, RepairState.Fixed => 4, _ => 5 }).ToArray();
        var strings = new TStringCollection();
        int width = Math.Max(10, list.size.x - 1);
        foreach (var r in rows) strings.Insert(WorkbenchText.Fit(Row(r), width));
        if (rows.Length == 0) strings.Insert("No findings: the diagnostics report nothing to fix.");
        list.NewList(strings);
        shownFocus = -1; ShowDetails();
    }
    private void ShowDetails()
    {
        if (list.focused == shownFocus) return;
        shownFocus = list.focused;
        if (shownFocus < 0 || shownFocus >= rows.Length) { details.Set(""); return; }
        var r = rows[shownFocus];
        int width = details.size.x - 13;
        string Field(string label, string? value) => $"{label,-12} " + WorkbenchText.Fit(value is { Length: > 0 } v ? v : "-", width);
        details.Set(string.Join('\n',
            Field("Source", r.Source is { } s ? WorkbenchText.Path(s, width) : null),
            Field("Expected", r.Expected),
            Field("Destination", r.Destination is { } d ? WorkbenchText.Path(d, width) : null),
            Field("Conflict", r.Conflict),
            Field("Permission", r.Permission),
            Field(r.State is RepairState.Manual or RepairState.Refused or RepairState.Unresolved ? "What to do" : "Note", r.Detail)));
    }

    // ── fix ─────────────────────────────────────────────────────────────────────────

    private void StartFix()
    {
        if (running is not null) { status.Set("Fix issues is already running; Close cancels it."); return; }
        Plan = Replan((replace.value & 1) != 0); Show(Plan.Items);
        if (!Plan.CanRepair) { status.Set("Nothing was changed: " + (Plan.Blockers.FirstOrDefault() ?? "nothing can be fixed safely.")); return; }
        ushort answer = ConfirmAnswer is { } given ? given() : Confirm();
        if (answer is not (Views.cmYes or Views.cmNo)) { status.Set("Nothing was changed."); return; }
        verifyAfter = answer == Views.cmYes && verify is not null;
        provisional = verifyAfter && Plan.OtherBuild;
        running = new CancellationTokenSource();
        var token = running.Token;
        UpdateButtons(Plan);
        status.Set("Fixing: staging and checking the files...  Close cancels and restores the folder.");
        var plan = Plan; bool active = runActive();
        _ = Task.Run(async () =>
        {
            RepairOutcome? outcome = null; Exception? error = null;
            try { outcome = await service.RepairAsync(plan, Static, reporter, token, active); }
            catch (Exception ex) { error = ex; }
            Post(Finished, new Done(outcome, error));
        });
    }

    // Explicit confirmation of exactly what will be written. Cancel is the default.
    private ushort Confirm()
    {
        var changes = Plan.Items.Where(i => i.State == RepairState.Fix).ToArray();
        int w = Math.Min(96, desktop.size.x - 2);
        var text = new List<string>
        {
            $"Copy {changes.Length} Workbench support file{(changes.Length == 1 ? "" : "s")} from this Workbench into",
            Plan.Folder, ""
        };
        text.AddRange(changes.Take(8).Select(c => $"  {(c.Summary.StartsWith("different", StringComparison.Ordinal) ? "replace" : "add    ")}  {c.Name,-30} {c.Expected?.Split(", SHA-256")[0]}"));
        if (changes.Length > 8) text.Add($"  ... and {changes.Length - 8} more");
        text.Add("");
        text.Add("Never changed: the application executable and assemblies, deps.json, runtimeconfig.json, TSharpVision.dll and every "
            + "other existing file" + (Plan.AllowReplace && Plan.Install!.Count(PlanAction.Replace) > 0 ? " (except the Workbench build replaced above, backed up first)." : "."));
        text.Add("Keep the target closed. A failure or Cancel restores the folder. Afterwards the diagnostics run again"
            + (verify is not null ? "; Fix and verify also starts the target once, headless (no scenario), to confirm the Proxy loads." : "."));
        if (Plan.OtherBuild)
            text.Add("The framework is another build of TSharpVision " + (service.Service.Payload?.FrameworkVersion ?? "") + " (same version): it is kept. "
                + (verify is not null ? "With Fix and verify the added files stay only if the verification succeeds; otherwise they are removed again." : "Verify target confirms it later."));
        var lines = text.SelectMany(t => t.Length == 0 ? [""] : WorkbenchText.Wrap(t, w - 6)).ToArray();
        int h = Math.Min(desktop.size.y, lines.Length + 7);
        using var dialog = new TDialog(new(0, 0, w, h), ConfirmTitle) { helpCtx = WorkbenchApp.DialogContext };
        dialog.options |= Views.ofCentered;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 4), string.Join('\n', lines)));
        int x = 3;
        if (verify is not null) { dialog.Insert(new TButton(new(x, h - 3, x + 20, h - 1), "~F~ix and verify", Views.cmYes, ButtonConstants.bfNormal)); x += 21; }
        dialog.Insert(new TButton(new(x, h - 3, x + 15, h - 1), "Fix ~o~nly", Views.cmNo, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 16, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfDefault);
        dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog);
    }

    private void Complete(Done done)
    {
        running?.Dispose(); running = null;
        TargetDiagnostics.Forget(executable); TargetPreparationService.ForgetStatus();
        if (done.Error is { } error)
        {
            Outcome = new(false, error is OperationCanceledException, "Fix issues failed: " + error.Message, null, Plan.Items, [], [error.ToString()]);
            status.Set("! " + Outcome.Message);
        }
        else
        {
            var outcome = done.Outcome!;
            // The final picture comes from the full diagnostics of the Workspace (catalog, runtime facts), re-run now.
            var after = diagnose();
            Outcome = outcome with { After = after, Items = TargetRepairService.Outcome(Plan, after, outcome.Success) };
            Show(Outcome.Items);
            string counts = $"{Outcome.Count(RepairState.Fixed)} fixed, {Outcome.Count(RepairState.Unresolved)} unresolved, {Outcome.Count(RepairState.Manual) + Outcome.Count(RepairState.Refused)} manual";
            if (outcome.Success && provisional)
                Outcome = Outcome with { Items = Outcome.Items.Select(i => i.State == RepairState.Fixed && Plan.Items.Any(p => p.State == RepairState.Fix && p.Name == i.Name)
                    ? i with { State = RepairState.Unresolved, Summary = "copied - verification pending" } : i).ToArray() };
            if (outcome.Success && provisional) Show(Outcome.Items);
            status.Set(outcome.Success && provisional ? $"Copied {outcome.Changes.Length} file(s) beside another build of the same framework version. Verifying the runtime before they are kept..."
                : outcome.Success ? $"{outcome.Message} ({counts})"
                : outcome.Cancelled ? "Cancelled. " + (outcome.RolledBack ? outcome.RollbackComplete ? "Every copied file was removed again." : "! Rollback incomplete: " + string.Join(" ", outcome.Details.Take(2)) : "Nothing was changed.")
                : "! " + outcome.Message);
            summary.Set(outcome.Success ? (after.Ready ? "Re-checked: Ready (static checks)." : "Re-checked: " + after.Readiness + ".") + (verifyAfter ? " Verifying the runtime..." : "") : Plan.Summary);
            if (!outcome.Success) { Plan = Replan(Plan.AllowReplace); }
        }
        UpdateButtons(Plan);
        // A provisional fix is kept only once verified: closing before that removes the added files again.
        if (closeAfter && provisional && Outcome is { Success: true }) RemoveUnverified("the window was closed before the verification");
        if (closeAfter) { EndModal(Views.cmCancel); return; }
        if (verifyAfter && Outcome is { Success: true }) StartVerify(confirmed: true);
    }

    // Removes the files a provisional fix added, when they are not verified (only files that still hold
    // exactly what was installed; never while the target runs). The outcome says what happened.
    private void RemoveUnverified(string reason)
    {
        if (Outcome is not { Success: true, Undone: false } o || o.Record is null) return;
        int count = o.Changes.Length;
        var (complete, notes) = Undo is { } undo ? undo(o)
            : service.IsRunning(executable) ? (false, [$"{Path.GetFileName(executable)} is running; the added files were left in place. Close it and remove them: " + string.Join(", ", o.Changes)])
            : TargetPreparationService.UndoInstall(o.Record, reason);
        TargetDiagnostics.Forget(executable); TargetPreparationService.ForgetStatus();
        var after = diagnose();
        Outcome = o with { Undone = complete, After = after, Items = TargetRepairService.Outcome(Plan, after, false)
            .Select(i => Plan.Items.Any(p => p.State == RepairState.Fix && p.Name == i.Name) ? i with { Summary = complete ? "failed verification - removed again" : "failed verification - NOT removed" } : i).ToArray() };
        Show(Outcome.Items);
        status.Set($"! {char.ToUpperInvariant(reason[0])}{reason[1..]}: " + (complete ? $"the {count} added file{(count == 1 ? " was" : "s were")} removed again; the folder is as before."
            : "removing the added files was INCOMPLETE: " + string.Join(" ", notes)));
        summary.Set(after.Ready ? "Re-checked: Ready (static checks)." : "Re-checked: " + after.Readiness + ".");
        UpdateButtons(Plan);
    }

    // ── verify (the existing Verify target lifecycle: headless, bounded, owned cleanup) ─────────────────

    private void StartVerify(bool confirmed)
    {
        verifyAfter = false;
        if (running is not null || verify is null) { status.Set(verify is null ? "Verify target is unavailable here: " + verifyText : "Busy; Close cancels."); return; }
        if (!confirmed && VerifyConsent() != Views.cmYes) { status.Set("Verification not started."); return; }
        running = new CancellationTokenSource();
        var token = running.Token;
        UpdateButtons(Plan);
        status.Set($"Verifying: starting the target headless and waiting for its handshake (at most {ScenarioRunner.VerifySeconds + 5} s). Close cancels.");
        var task = verify;
        _ = Task.Run(async () =>
        {
            TargetVerification? result = null; Exception? error = null;
            try { result = await task(token); }
            catch (Exception ex) { error = ex; }
            Post(Verified, new Checked(result, error));
        });
    }
    private ushort VerifyConsent()
    {
        int w = Math.Min(78, desktop.size.x - 2);
        var lines = WorkbenchText.Wrap(verifyText, w - 6).ToArray();
        int h = Math.Min(desktop.size.y, lines.Length + 7);
        using var dialog = new TDialog(new(0, 0, w, h), VerifyTitle) { helpCtx = WorkbenchApp.DialogContext };
        dialog.options |= Views.ofCentered;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 4), string.Join('\n', lines)));
        dialog.Insert(new TButton(new(3, h - 3, 25, h - 1), "~S~tart verification", Views.cmYes, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 16, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfDefault);
        dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog);
    }
    private void CompleteVerify(Checked done)
    {
        running?.Dispose(); running = null;
        if (done.Error is OperationCanceledException) status.Set("Verification cancelled; the target was stopped and its temporary files removed.");
        else if (done.Error is { } error)
        {
            var failure = StartupFailure.Classify(error.Message);
            record?.Invoke(null, failure);
            string cleanup = error is TargetVerificationException v ? $" Target {v.StopOutcome}; cleanup {(v.Cleanup ? "complete" : "INCOMPLETE")}." : "";
            status.Set("! Verification failed: " + failure.Reason + cleanup);
        }
        else if (done.Result is { } result)
        {
            Verification = result;
            record?.Invoke(result, null);
            status.Set($"Verified in {result.Duration.TotalSeconds:F1} s: {result.Identity.Assembly} connected ({(result.Proxy ? "Proxy session" : "diagnostic host")}); "
                + $"target {result.StopOutcome}; cleanup {(result.Cleanup ? "complete" : "INCOMPLETE")}.");
        }
        // A provisional fix that did not verify is removed again; one that verified is "verified working".
        bool riskPending = provisional && Outcome is { Success: true, Undone: false, Record: not null };
        if (riskPending && done.Result is null)
        {
            RemoveUnverified(done.Error is OperationCanceledException ? "the verification was cancelled" : "the verification failed (" + StartupFailure.Classify(done.Error?.Message ?? "").Reason + ")");
            if (closeAfter) EndModal(Views.cmCancel);
            return;
        }
        // The runtime fact is part of the diagnostics now: the rows and the summary follow it.
        var after = diagnose();
        if (Outcome is { } o)
        {
            Outcome = o with { After = after, Items = TargetRepairService.Outcome(Plan, after, o.Success) };
            if (riskPending) Outcome = Outcome with { Items = Outcome.Items.Select(i => i.State == RepairState.Fixed && Plan.Items.Any(p => p.State == RepairState.Fix && p.Name == i.Name)
                ? i with { Summary = "copied and verified working (Proxy handshake)" } : i).ToArray() };
            Show(Outcome.Items);
        }
        else { Plan = Replan(Plan.AllowReplace); Show(Plan.Items); }
        if (riskPending) provisional = false;
        if (riskPending && done.Result is { } ok)
            status.Set($"Verified working (same version, other build): {ok.Identity.Assembly} connected through the Proxy in {ok.Duration.TotalSeconds:F1} s; target {ok.StopOutcome}; "
                + $"cleanup {(ok.Cleanup ? "complete" : "INCOMPLETE")}. The added files stay; run your scenarios to prove behaviour.");
        summary.Set(after.Ready ? (after.Findings.Any(f => f.Code == "RUN002") ? "Re-checked: Ready (runtime verified)." : "Re-checked: Ready (static checks).") : "Re-checked: " + after.Readiness + ".");
        UpdateButtons(Plan);
        if (closeAfter) EndModal(Views.cmCancel);
    }

    public override bool Valid(ushort command)
    {
        // Leaving while work runs cancels it and waits for the rollback or the owned process cleanup.
        if (command is Views.cmCancel or Views.cmClose && running is not null)
        {
            closeAfter = true; running.Cancel();
            status.Set("Cancelling; waiting until the folder is restored and every owned process has closed...");
            return false;
        }
        return base.Valid(command);
    }
    public override void ShutDown() { EnableCommand(FixCommand); EnableCommand(VerifyCommand); EnableCommand(CompatibilityCommand); base.ShutDown(); }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand)
        {
            switch (ev.message.command)
            {
                case Finished when ev.message.infoPtr is Done done: ClearEvent(ref ev); Complete(done); return;
                case Verified when ev.message.infoPtr is Checked check: ClearEvent(ref ev); CompleteVerify(check); return;
                case Progressed when ev.message.infoPtr is Step step:
                    ClearEvent(ref ev); reporter.Shown();
                    if (running is not null)
                        status.Set($"Fixing: {step.Progress.Phase} ({Math.Min(step.Progress.Files, step.Progress.TotalFiles)} of {step.Progress.TotalFiles})...  Close cancels and restores the folder.");
                    return;
                case FixCommand: ClearEvent(ref ev); StartFix(); return;
                case VerifyCommand: ClearEvent(ref ev); StartVerify(confirmed: false); return;
                case CompatibilityCommand: ClearEvent(ref ev); WorkbenchPrepareTargetDialog.ShowLines(desktop, CompatibilityTitle, CompatibilityLines()); return;
            }
        }
        base.HandleEvent(ref ev);
        // The other mode: hand over to Prepare a copy (never while a repair or verification runs).
        if (Mode is not null && Mode.value == PreparationModes.Copy && (state & Views.sfModal) != 0)
        {
            if (running is null) { EndModal(PreparationModes.SwitchCommand); return; }
            Mode.value = PreparationModes.Fix; Mode.DrawView();
        }
        if (replace is not null && replace.value != replaceValue && running is null)
        {
            replaceValue = replace.value;
            Outcome = null;
            Plan = Replan((replaceValue & 1) != 0); Show(Plan.Items);
            status.Set((replaceValue & 1) != 0 ? "A different Workbench build will be replaced after a verified backup (still nothing changed)." : "Copy only: nothing existing is replaced.");
        }
        if (list is not null) ShowDetails();
    }
}
