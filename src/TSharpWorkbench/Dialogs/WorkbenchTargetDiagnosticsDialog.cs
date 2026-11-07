using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Target Diagnostics, the full report (static inspection, diagnostic host, runtime facts) and the explicit
// Verify target action. Verify asks first (it starts the application once), runs bounded and cancellable in the background
// and always waits for the owned process cleanup before the dialog can close. It never runs a scenario, never saves the
// project and never edits a file; the outcome is kept as a runtime fact of this deployment for the session only.
internal sealed class WorkbenchTargetDiagnosticsDialog : TDialog
{
    internal const string DialogTitle = "Target Diagnostics", ConfirmTitle = "Verify Target";
    internal const ushort VerifyCommand = 0x7d9f;
    private const ushort Finished = 0x7d93;
    private readonly TDeskTop desktop;
    private readonly Func<TargetReport> report;
    private readonly Func<CancellationToken, Task<TargetVerification>>? verify;
    private readonly Action<TargetVerification?, StartupFailure?>? record;
    private readonly string verifyText;
    private readonly TListBox list;
    private readonly Line status;
    private CancellationTokenSource? running;
    private bool closeAfter;
    internal TButton VerifyButton { get; }
    // TSW PRODUCT-3: Fix Issues beside Verify target; enabled while the report has something Fix Issues addresses.
    internal const ushort FixCommand = 0x7d36;
    private readonly Action? fixIssues;
    internal TButton FixButton { get; }
    internal string StatusText => status.Current;
    internal string[] ReportLines { get; private set; } = [];
    internal bool Verifying => running is not null;

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }
    private sealed class Done(TargetVerification? result, Exception? error) : IInfo
    {
        internal TargetVerification? Result { get; } = result;
        internal Exception? Error { get; } = error;
    }

    internal static TRect Bounds(TDeskTop desktop) => new(0, 0, Math.Min(112, desktop.size.x - 2), Math.Min(32, desktop.size.y));

    // verify: null when this target cannot be verified here (then the button explains why); verifyText explains what
    // Verify will start, for the confirmation.
    internal WorkbenchTargetDiagnosticsDialog(TDeskTop desktop, Func<TargetReport> report, Func<CancellationToken, Task<TargetVerification>>? verify,
        Action<TargetVerification?, StartupFailure?>? record, string verifyText, Action? fixIssues = null)
        : base(Bounds(desktop), DialogTitle)
    {
        this.desktop = desktop; this.report = report; this.verify = verify; this.record = record; this.verifyText = verifyText; this.fixIssues = fixIssues;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y;
        var scroll = new TScrollBar(new(w - 3, 2, w - 2, h - 5)); Insert(scroll);
        list = new TListBox(new(2, 2, w - 3, h - 5), 1, scroll); Insert(list);
        Insert(new TLabel(new(1, 1, 12, 2), "~R~eport", list));
        status = new Line(new(2, h - 4, w - 2, h - 3)); Insert(status);
        // Owner spacing rule: the status line, then the buttons on the last rows above the bottom frame.
        VerifyButton = new(new(2, h - 3, 21, h - 1), "~V~erify target...", VerifyCommand, ButtonConstants.bfNormal); Insert(VerifyButton);
        FixButton = new(new(22, h - 3, 40, h - 1), "~F~ix issues...", FixCommand, ButtonConstants.bfNormal); Insert(FixButton);
        var close = new TButton(new(w - 15, h - 3, w - 3, h - 1), "Close", Views.cmCancel, ButtonConstants.bfDefault); Insert(close);
        Reload();
        status.Set(!FixButton.GetState(Views.sfDisabled) ? "Fix issues... shows what it would copy into the target folder and asks first. Verify target starts the target once."
            : verify is null ? "Verify target is unavailable here: " + verifyText : "Static inspection only. Verify target starts the target once (no scenario) to confirm runtime activation.");
        list.Select();
    }

    internal void Reload()
    {
        var r = report();
        ReportLines = TargetDiagnostics.Lines(r).ToArray();
        bool fix = fixIssues is not null && running is null && TargetRepairService.Relevant(r);
        if (fix) EnableCommand(FixCommand); else DisableCommand(FixCommand);
        if (FixButton is not null) { FixButton.SetState(Views.sfDisabled, !fix); FixButton.DrawView(); }
        var rows = new TStringCollection();
        int width = Math.Max(10, list.size.x - 1);
        foreach (string line in ReportLines)
        {
            int indent = line.Length - line.TrimStart().Length;
            var wrapped = WorkbenchText.Wrap(line.TrimStart(), Math.Max(10, width - indent)).ToArray();
            foreach (string part in wrapped) rows.Insert(new string(' ', Math.Min(indent, width / 2)) + part);
            if (wrapped.Length == 0) rows.Insert("");
        }
        int top = list.topItem; list.NewList(rows); list.topItem = Math.Clamp(top, 0, Math.Max(0, rows.Count - 1));
    }

    private void StartVerify()
    {
        if (running is not null) { status.Set("Verification is already running; Close cancels it."); return; }
        if (verify is null) { status.Set("Verify target is unavailable here: " + verifyText); return; }
        if (Consent(desktop, verifyText) != Views.cmYes) { status.Set("Verification not started."); return; }
        running = new CancellationTokenSource();
        var token = running.Token;
        DisableCommand(VerifyCommand); VerifyButton.SetState(Views.sfDisabled, true); VerifyButton.DrawView();
        status.Set($"Verifying: starting the target headless and waiting for its handshake (at most {ScenarioRunner.VerifySeconds + 5} s). Close cancels.");
        var task = verify;
        _ = Task.Run(async () =>
        {
            TargetVerification? result = null; Exception? error = null;
            try { result = await task(token); }
            catch (Exception ex) { error = ex; }
            Post(Finished, new Done(result, error));
        });
    }

    // Explicit consent: the application's own startup code runs. Cancel is the default.
    private static ushort Consent(TDeskTop desktop, string text)
    {
        int w = Math.Min(78, desktop.size.x - 2);
        var lines = WorkbenchText.Wrap(text, w - 6).ToArray();
        int h = Math.Min(desktop.size.y, lines.Length + 7);
        using var dialog = new TDialog(new(0, 0, w, h), ConfirmTitle) { helpCtx = WorkbenchApp.DialogContext };
        dialog.options |= Views.ofCentered;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 4), string.Join('\n', lines)));
        dialog.Insert(new TButton(new(3, h - 3, 25, h - 1), "~S~tart verification", Views.cmYes, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 16, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfDefault);
        dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog);
    }

    private void Complete(Done done)
    {
        running?.Dispose(); running = null;
        EnableCommand(VerifyCommand); VerifyButton.SetState(Views.sfDisabled, false); VerifyButton.DrawView();
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
            record?.Invoke(result, null);
            status.Set($"Verified in {result.Duration.TotalSeconds:F1} s: {result.Identity.Assembly} connected ({(result.Proxy ? "Proxy session" : "diagnostic host")}); "
                + $"target {result.StopOutcome}; cleanup {(result.Cleanup ? "complete" : "INCOMPLETE")}.");
        }
        Reload();
        if (closeAfter) EndModal(Views.cmCancel);
    }

    public override bool Valid(ushort command)
    {
        // Leaving while a verification runs cancels it and waits for the owned process cleanup; the dialog closes after it.
        if (command is Views.cmCancel or Views.cmClose && running is not null)
        {
            closeAfter = true; running.Cancel();
            status.Set("Cancelling the verification; waiting for the target to close...");
            return false;
        }
        return base.Valid(command);
    }
    public override void ShutDown() { EnableCommand(VerifyCommand); EnableCommand(FixCommand); base.ShutDown(); }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && ev.message.command == Finished && ev.message.infoPtr is Done done) { ClearEvent(ref ev); Complete(done); return; }
        if (ev.What == Events.evCommand && ev.message.command == VerifyCommand) { ClearEvent(ref ev); StartVerify(); return; }
        if (ev.What == Events.evCommand && ev.message.command == FixCommand)
        {
            ClearEvent(ref ev);
            if (running is not null) { status.Set("Verification is running; Close cancels it."); return; }
            fixIssues?.Invoke();
            Reload();
            status.Set(TargetRepairService.Relevant(report()) ? "Some issues remain; see the report." : "Report refreshed after Fix Issues.");
            return;
        }
        base.HandleEvent(ref ev);
    }
}
