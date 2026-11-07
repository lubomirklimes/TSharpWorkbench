using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// The recording session window. Modeless, opened from the scenario editor's Record button.
// Rows: what is recorded (target, window kind, grid), state, counters, what to do now; the converted steps live below; two
// button rows: session (Start, Pause/Resume, Stop, Cancel) and review (Append, Insert at selection, Replace selection, Discard).
// The person types into the TARGET's own window; keys pressed here (including Stop) never reach the target.
internal sealed class WorkbenchRecordingWindow : TWindow
{
    internal const ushort StartCommand = 0x7d10, PauseCommand = 0x7d11, ResumeCommand = 0x7d12, StopCommand = 0x7d13, CancelCommand = 0x7d14,
        AppendCommand = 0x7d15, InsertCommand = 0x7d16, ReplaceCommand = 0x7d17, DiscardCommand = 0x7d18, RefreshCommand = 0x7d19, Context = 0x7d1a,
        PickCommand = 0x7d1b, ReviewCommand = 0x7d1d,
        // Posted to itself only (never a button): runs the follow-up of a state change (auto Review, Smart unavailable).
        FollowUpCommand = 0x7d1e;
    internal const string WindowTitle = "Record Interaction";
    private static readonly ushort[] Commands = [StartCommand, PauseCommand, ResumeCommand, StopCommand, CancelCommand, AppendCommand, InsertCommand, ReplaceCommand, DiscardCommand, PickCommand, ReviewCommand];

    /// <summary>The single open recording window (one recording at a time).</summary>
    internal static WorkbenchRecordingWindow? Active { get; private set; }

    private readonly WorkbenchNotifications notifications;
    private readonly ScenarioEditorContext context;
    private readonly string grid;
    private readonly Line targetLine, stateLine, countLine, hintLine, notesLine;
    private readonly TRadioButtons backendChoice;
    private readonly TListBox stepList;
    private readonly List<string> rows = [];
    private RecordingConversion? conversion;
    private SmartRecordingAnalysis? analysis;
    private RecordingReviewModel? review;
    private bool closing;
    private bool smart;
    // Stop was the user's (auto Review only then); the review opened once; Smart capture skipped for this recording.
    private bool userStopped, autoReviewed, inputsOnly, followUpPosted;
    private readonly bool smartForced;
    private readonly SmartBox smartBox;
    private TButton? pickButton, reviewButton;

    internal WorkbenchScenarioEditorWindow Editor { get; }
    internal RecordingSession? Session { get; private set; }
    internal RecordingConversion? Conversion => conversion;
    /// <summary>Proposals of the stopped recording (Smart capture only; never inserted).</summary>
    internal SmartRecordingAnalysis? Analysis => analysis;
    /// <summary>The review of the stopped Smart recording (decisions; nothing is accepted by default).</summary>
    internal RecordingReviewModel? Review => review;
    internal bool SmartCaptureOn => smart;
    /// <summary>Test seam: answers the Replace selection confirmation of a Smart recording (null = ask with a dialog).</summary>
    internal static Func<string, bool>? ConfirmReplace { get; set; }
    /// <summary>Test seam: answers a choice dialog (title, text, option labels, default index) with the chosen index,
    /// or -1 for Cancel/Esc (null = a real dialog).</summary>
    internal static Func<string, string, string[], int, int>? Chooser { get; set; }
    /// <summary>Test seam: opens the review dialog (null = <see cref="WorkbenchRecordingReview.Review"/>).</summary>
    internal static Func<TDeskTop, RecordingReviewModel, bool>? ReviewOpener { get; set; }
    /// <summary>Decision 5: opens Fix Issues (existing consent + backup path) for the target executable; false when it
    /// is not available. Installed by <see cref="WorkbenchRecorder.Install"/>.</summary>
    internal static Func<string, bool>? FixIssues { get; set; }
    /// <summary>The review opened automatically after Stop (once per recording).</summary>
    internal bool AutoReviewed => autoReviewed;
    /// <summary>The user chose to record this session without Smart capture (old target Proxy); the setting is unchanged.</summary>
    internal bool InputsOnlyChosen => inputsOnly;
    internal string Grid => grid;
    internal string SelectedBackend => WorkbenchRecorder.Backends[Math.Clamp((int)backendChoice.value, 0, WorkbenchRecorder.Backends.Length - 1)];
    internal string Text => string.Join("\n", new[] { targetLine, stateLine, countLine, hintLine, notesLine }.Select(l => l.Current)) + "\n" + string.Join("\n", rows);
    /// <summary>Test seam: builds the session for Start (default: the real owned Proxy session).</summary>
    internal Func<string, RecordingSession>? SessionFactory { get; set; }

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }

    // The Smart capture setting (persisted; read-only when the environment or a test forces it).
    private sealed class SmartBox(TRect bounds) : TCheckBoxes(bounds, new TSItem("S~m~art capture", null!))
    {
        internal Action? Toggled;
        public override void Press(int item) { base.Press(item); Toggled?.Invoke(); }
    }

    internal static TRect DefaultBounds(TDeskTop desktop)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        int w = Math.Min(96, dw), h = Math.Min(26, dh);
        int x = Math.Max(0, dw - w - 1), y = Math.Max(0, (dh - h) / 2);
        return new(x, y, x + w, y + h);
    }

    internal WorkbenchRecordingWindow(TDeskTop desktop, WorkbenchScenarioEditorWindow editor, ScenarioEditorContext context, WorkbenchNotifications notifications,
        bool? smartCapture = null)
        : base(DefaultBounds(desktop), WindowTitle, 0)
    {
        Editor = editor; this.context = context; this.notifications = notifications;
        smart = smartCapture ?? WorkbenchRecorder.SmartCapture;
        smartForced = smartCapture is not null || WorkbenchRecorder.SmartCaptureForced is not null;
        grid = WorkbenchRecorder.Grid(context);
        helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        int w = size.x, h = size.y;
        byte growX = Views.gfGrowHiX, bottom = (byte)(Views.gfGrowLoY | Views.gfGrowHiY);
        Line Row(int y) { var line = new Line(new(2, y, w - 2, y + 1)) { growMode = growX }; Insert(line); return line; }
        targetLine = Row(1);
        Insert(new TStaticText(new(2, 2, 11, 3), "Window"));
        TSItem? chain = null;
        for (int i = WorkbenchRecorder.BackendLabels.Length - 1; i >= 0; i--) chain = new TSItem(WorkbenchRecorder.BackendLabels[i], chain!);
        backendChoice = new TRadioButtons(new(11, 2, Math.Min(w - 2, 70), 3), chain!) { value = (uint)WorkbenchRecorder.DefaultBackend(context) };
        Insert(backendChoice);
        smartBox = new SmartBox(w >= 94 ? new(72, 2, w - 2, 3) : new(Math.Max(42, w - 22), 6, w - 2, 7)) { growMode = Views.gfGrowLoX | Views.gfGrowHiX, value = smart ? 1u : 0u };
        smartBox.Toggled = () => SetSmart(smartBox.value != 0, persist: true);
        Insert(smartBox);
        stateLine = Row(3); countLine = Row(4); hintLine = Row(5);
        Insert(new TStaticText(new(2, 6, 40, 7), "Recorded steps (review, then insert)") { growMode = growX });
        var scroll = new TScrollBar(new(w - 3, 7, w - 2, h - 7)) { growMode = (byte)(Views.gfGrowLoX | Views.gfGrowHiX | Views.gfGrowHiY) };
        Insert(scroll);
        stepList = new TListBox(new(2, 7, w - 3, h - 7), 1, scroll) { growMode = (byte)(Views.gfGrowHiX | Views.gfGrowHiY) };
        Insert(stepList);
        notesLine = new Line(new(2, h - 6, w - 2, h - 5)) { growMode = (byte)(growX | bottom) }; Insert(notesLine);
        // Mnemonics avoid the menu bar's P S T R B W H (Alt+X is Exit) and the editor's buttons; Start has none (Tab to it).
        int x = 2;
        void Button(string text, int width, ushort command, int row)
        {
            var button = new TButton(new(x, h - 5 + row * 2, x + width, h - 3 + row * 2), text, command, ButtonConstants.bfNormal) { growMode = bottom };
            Insert(button); x += width + 1;
        }
        Button("~S~tart", 10, StartCommand, 0);
        Button("P~a~use", 10, PauseCommand, 0);
        Button("Res~u~me", 12, ResumeCommand, 0);
        Button("St~o~p", 9, StopCommand, 0);
        Button("~C~ancel", 11, CancelCommand, 0);
        pickX = x;
        x = 2;
        Button("Appe~n~d", 12, AppendCommand, 1);
        Button("Insert at se~l~ection", 24, InsertCommand, 1);
        Button("Replace sel~e~ction", 22, ReplaceCommand, 1);
        Button("Discar~d~", 12, DiscardCommand, 1);
        reviewX = x;
        SmartButtons();
        Active = this;
        UpdateView();
        backendChoice.Select();
    }

    private int pickX, reviewX;

    // Pick (session row, after Cancel) and Review (review row, after Discard) exist only with Smart capture; with
    // it off the window is exactly the legacy layout.
    private void SmartButtons()
    {
        byte bottom = (byte)(Views.gfGrowLoY | Views.gfGrowHiY);
        int h = size.y;
        if (smart && pickButton is null)
        {
            pickButton = new TButton(new(pickX, h - 5, pickX + 9, h - 3), "Pic~k~", PickCommand, ButtonConstants.bfNormal) { growMode = bottom };
            reviewButton = new TButton(new(reviewX, h - 3, reviewX + 13, h - 1), "Re~v~iew...", ReviewCommand, ButtonConstants.bfNormal) { growMode = bottom };
            Insert(pickButton); Insert(reviewButton);
        }
        else if (!smart && pickButton is not null)
        {
            foreach (var b in new[] { pickButton, reviewButton! }) { Remove(b); b.Dispose(); }
            pickButton = reviewButton = null;
        }
    }

    /// <summary>Turns Smart capture on or off for this window (Ready only) and, from the check box, saves the user setting.</summary>
    internal void SetSmart(bool on, bool persist = false)
    {
        if (State != RecordingState.Ready || smartForced) { smartBox.value = smart ? 1u : 0u; smartBox.DrawView(); return; }
        smart = on;
        smartBox.value = on ? 1u : 0u;
        if (persist && !WorkbenchRecorder.SaveSmartCapture(on)) notifications.Warning("Smart capture: the setting could not be saved; it applies to this window only.");
        SmartButtons();
        DrawView();
        UpdateView();
    }

    // ── state → view ─────────────────────────────────────────────────────────────────────────────────────────

    private RecordingState State => Session?.State ?? RecordingState.Ready;

    internal void UpdateView()
    {
        var state = State;
        string name = Path.GetFileName(context.TargetPath ?? "");
        targetLine.Set($"Target   {name}   grid {grid}   (ordinary application, Workbench Proxy)");
        string window = WorkbenchRecorder.BackendLabels[Array.IndexOf(WorkbenchRecorder.Backends, Session?.Backend ?? SelectedBackend)].ToLowerInvariant();
        stateLine.Set("State    " + state switch
        {
            RecordingState.Ready => "Ready. Choose the target window kind, then Start.",
            RecordingState.Starting => $"Starting {name} in its own {window}...",
            RecordingState.Recording => $"RECORDING - use the target's {window}; keys pressed here are not recorded.",
            RecordingState.Paused => "PAUSED - input in the target window is delivered but not recorded.",
            RecordingState.Stopping => "Stopping - closing the target...",
            RecordingState.Stopped => Session?.Ended is { } ended ? "Ended by the target. " + ended : "Stopped. The target was closed; review the steps below.",
            RecordingState.Cancelled => "Cancelled. Nothing was recorded; the target was closed.",
            _ => "! Could not record: " + (Session?.Failure ?? "unknown failure"),
        });
        var batch = Session?.Last;
        var events = Session?.Events ?? [];
        countLine.Set(Session is null ? "Input    nothing recorded yet"
            : $"Input    {events.Count} recorded   {batch?.Unrecorded ?? 0} delivered but not recorded (key releases, modifiers, hover, paused)"
              + (batch is { Dropped: > 0 } ? $", {batch.Dropped} over the limit" : "") + $"   {Session.Elapsed:mm\\:ss}");
        if (state is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping or RecordingState.Stopped)
            conversion = RecordingConverter.Convert(events, Session!.Width, Session.Height);
        else if (state is RecordingState.Cancelled or RecordingState.Failed) conversion = null;
        // Proposals are an internal model shown as diagnostics only after Stop; Append/Insert/Replace stay input-only.
        analysis = !smart || state != RecordingState.Stopped || conversion is null ? null : analysis
            ?? Session!.Analyze(conversion, CheckpointIds(0, 0));
        // Suggestions and the review model of the stopped recording (nothing accepted until the user reviews).
        if (analysis is null) review = null;
        else if (review is null || !ReferenceEquals(review.Analysis, analysis))
        {
            var suggestions = AssertionSuggester.Suggest(analysis, AssertionSuggester.TypedTexts(Session!.Events, conversion!), Session.Width, Session.Height);
            review = new RecordingReviewModel(analysis, suggestions, conversion!, CaptureStatus());
        }
        // The hint tells apart Smart capture off, unavailable (old target Proxy) and proposals not accepted.
        bool unavailable = SmartUnavailable(state);
        hintLine.Set(state switch
        {
            RecordingState.Ready => smart ? "Start opens the target in a separate window; interact there, then come back and press Stop."
                : "Start opens the target; press Stop when done. Smart capture is off: no checkpoints will be proposed.",
            RecordingState.Failed when Session?.SmartUnavailable == true =>
                "! Smart capture unavailable: the target's Workbench Proxy is too old. Nothing was recorded; Start again to choose.",
            RecordingState.Recording or RecordingState.Paused when unavailable =>
                "! Smart capture unavailable: the target's Workbench Proxy is older than this Workbench. Input only; Stop, then re-prepare.",
            RecordingState.Recording or RecordingState.Paused when smart && Session?.Capture.PickState == Protocol.PickStates.Armed =>
                "PICK MODE: click a control or drag a region in the target; not recorded. Pick cancels.",
            RecordingState.Recording or RecordingState.Paused when smart && Session?.PickRefused is { } refused => "Pick: " + refused,
            RecordingState.Recording or RecordingState.Paused => "Switch back here (click, or Alt+Tab) for Pause, Stop or Cancel. Limit: 30 minutes.",
            RecordingState.Stopped when conversion is { Steps.Count: > 0 } && unavailable =>
                "! Smart capture was unavailable (old target Proxy): nothing proposed. Re-prepare the target (Target Diagnostics).",
            RecordingState.Stopped when conversion is { Steps.Count: > 0 } && inputsOnly =>
                "Recorded inputs only (you chose this for the old target Proxy): no checkpoints proposed. Fix Issues updates the Proxy.",
            RecordingState.Stopped when conversion is { Steps.Count: > 0 } && !smart =>
                "Smart capture was off: no checkpoints proposed. Append, Insert or Replace inserts the inputs only.",
            RecordingState.Stopped when conversion is { Steps.Count: > 0 } && review is { Checkpoints.Count: > 0 } =>
                review.AnyAccepted ? $"Append, Insert or Replace puts the steps and {review.AcceptedCount} accepted checkpoint(s) into the editor; nothing is saved."
                : $"{review.Checkpoints.Count} proposed checkpoint(s) NOT accepted: Review... to accept; Append inserts the inputs only.",
            RecordingState.Stopped => conversion is { Steps.Count: > 0 } ? "Append, Insert at selection or Replace selection puts the steps into the editor; nothing is saved."
                : "Nothing convertible was recorded. Discard closes this window.",
            _ => "",
        });
        Rows();
        // Follow-ups of a state change run from the event loop (never inside a session notification or during Stopping).
        if (!followUpPosted && owner is TDeskTop && (WantsAutoReview(state) || state == RecordingState.Failed && Session?.SmartUnavailable == true))
        { followUpPosted = true; Post(FollowUpCommand, null!); }
        notesLine.Set(conversion is null ? "" : conversion.Mismatches.Count > 0 ? "! Replay check: " + conversion.Mismatches[0]
            : conversion.Steps.Count == 0 ? "" : $"Replay check: the {conversion.Steps.Count} steps reproduce the recorded presses."
              + (conversion.Notes.Count > 0 ? $" {conversion.Notes.Count} note(s) listed below the steps." : ""));
        UpdateCommands(state);
    }

    // Decision 3: once, after a Stop the user gave (not a target exit, Cancel, failure or legacy capture), when there is
    // at least one checkpoint proposal to review.
    private bool WantsAutoReview(RecordingState state) => !autoReviewed && smart && userStopped && state == RecordingState.Stopped
        && Session is { Ended: null } && review is { Checkpoints.Count: > 0 } && conversion is { Steps.Count: > 0 };

    private void FollowUp()
    {
        followUpPosted = false;
        if (owner is not TDeskTop desktop) return;
        if (WantsAutoReview(State)) { autoReviewed = true; OpenReview(desktop); return; }
        if (State == RecordingState.Failed && Session?.SmartUnavailable == true && Session is { } failed)
        {
            // The handshake showed a Proxy without Smart capture: nothing was recorded. Offer the same choice as before Start.
            Session = null; failed.Changed -= Refresh;
            _ = failed.DisposeAsync();
            OfferStaleProxy("The target connected, but its Workbench Proxy has no Smart capture (it is older than this Workbench). Nothing was recorded.");
        }
    }

    private void OpenReview(TDeskTop desktop)
    {
        if (review is not { } r) return;
        if (ReviewOpener is { } seam) seam(desktop, r); else WorkbenchRecordingReview.Review(desktop, r);
    }

    // Smart capture is on but the target's Proxy negotiated no Smart Recorder capture (deployed before Smart Recording).
    private bool SmartUnavailable(RecordingState state) => smart && Session is { } s && !s.Capture.BoundariesEnabled
        && state is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping or RecordingState.Stopped;

    // Left presses delivered with Ctrl+Shift (no Alt) while the target did not intercept picks: they are ordinary
    // input (recorded with modifier steps), which the user may have meant as picks.
    internal int CtrlShiftClicks => Session is not { } s || smart && s.Capture.PicksEnabled ? 0
        : s.Events.Count(e => e.Kind == "mouse" && e.What == Events.evMouseDown && e.Buttons == Events.mbLeftButton
            && (e.ControlKeyState & Keys.kbShift) != 0 && (e.ControlKeyState & Keys.kbCtrlShift) != 0 && (e.ControlKeyState & Keys.kbAltShift) == 0);

    private string CtrlShiftNote(int clicks) => $"{clicks} Ctrl+Shift click{(clicks == 1 ? " was" : "s were")} recorded as input (with modifier steps), not as pick"
        + $"{(clicks == 1 ? "" : "s")}: picking needs {(smart ? "a target Proxy from this Workbench (re-prepare the target)" : "Smart capture on before Start")}.";

    private void Rows()
    {
        rows.Clear();
        if (conversion is { } c)
        {
            int n = 0;
            foreach (var s in c.Steps) rows.Add($"{++n,3} {WorkbenchText.Column(s.Step.Kind, 10)} {s.Step.Parameters}{(s.Step.Note is { } note ? "   (" + note + ")" : "")}");
            int clicks = CtrlShiftClicks;
            if (c.Notes.Count + c.Mismatches.Count > 0 || clicks > 0) rows.Add("");
            if (clicks > 0) rows.Add("  Note: " + CtrlShiftNote(clicks));
            foreach (string note in c.Notes) rows.Add("  Note: " + note);
            foreach (string m in c.Mismatches) rows.Add("  ! Replay check: " + m);
            if (c.Steps.Count == 0) rows.Insert(0, "    (no steps yet)");
            if (analysis is { } a)
            {
                rows.Add("");
                foreach (string line in a.Summary) rows.Add("  Smart capture: " + line);
                foreach (string line in review?.Summary() ?? []) rows.Add("  Review: " + line);
                foreach (var cp in review?.Checkpoints ?? [])
                {
                    var p = cp.Proposal;
                    rows.Add($"  {(cp.Accepted ? "[x]" : "[ ]")} {cp.CheckpointId} {(!cp.Representable ? "cannot be placed" : p.InsertAfterStep < 0 ? "before step 1" : "after step " + (p.InsertAfterStep + 1))}: "
                        + $"{p.Kind}, {p.Confidence} confidence, {p.Evidence}, {cp.SelectedAssertions}/{cp.Assertions.Count(x => x.Selectable)} assertion(s)"
                        + $"{(p.Picks.Count > 0 ? $", {p.Picks.Count} pick(s)" : "")}{(p.NeedsReview ? ", NEEDS REVIEW" : "")} - {p.Reason}");
                }
            }
        }
        else rows.Add(State == RecordingState.Ready ? "    Steps appear here while you record." : "    (none)");
        int keep = stepList.focused;
        var lines = new TStringCollection();
        foreach (string row in rows) lines.Insert(WorkbenchText.Fit(row, Math.Max(10, stepList.size.x - 1)));
        stepList.NewList(lines);
        if (keep > 0 && keep < rows.Count) stepList.FocusItemNum(keep);
    }

    private void UpdateCommands(RecordingState state)
    {
        bool review = state == RecordingState.Stopped && conversion is { Steps.Count: > 0 } && Editor.owner is not null && !Editor.Document.ReadOnly;
        Set(StartCommand, state == RecordingState.Ready);
        Set(PauseCommand, state == RecordingState.Recording);
        Set(ResumeCommand, state == RecordingState.Paused);
        Set(StopCommand, state is RecordingState.Recording or RecordingState.Paused);
        Set(CancelCommand, state is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused);
        Set(AppendCommand, review);
        Set(InsertCommand, review);
        Set(ReplaceCommand, review && Editor.Selection.Count > 0);
        Set(DiscardCommand, state is RecordingState.Ready or RecordingState.Stopped or RecordingState.Cancelled or RecordingState.Failed);
        Set(PickCommand, smart && state == RecordingState.Recording && Session?.PicksAvailable == true);
        Set(ReviewCommand, smart && state == RecordingState.Stopped && review != null && owner is TDeskTop);
        backendChoice.SetState(Views.sfDisabled, state != RecordingState.Ready);
        smartBox.SetState(Views.sfDisabled, state != RecordingState.Ready || smartForced);
    }

    private static void Set(ushort command, bool enabled) { if (enabled) EnableCommand(command); else DisableCommand(command); }

    // ── commands ─────────────────────────────────────────────────────────────────────────────────────────────

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEsc) { ClearEvent(ref ev); if (Editor.owner is not null) Editor.Select(); return; }
        if (ev.What == Events.evCommand && ev.message.command == RefreshCommand) { ClearEvent(ref ev); UpdateView(); return; }
        if (ev.What == Events.evCommand && ev.message.command == FollowUpCommand) { ClearEvent(ref ev); FollowUp(); UpdateView(); return; }
        if (ev.What == Events.evCommand && Commands.Contains(ev.message.command))
        {
            ushort command = ev.message.command; ClearEvent(ref ev);
            if (CommandEnabled(command)) Execute(command);
            return;
        }
        base.HandleEvent(ref ev);
    }

    internal void Execute(ushort command)
    {
        switch (command)
        {
            case StartCommand: StartRecording(); break;
            case PauseCommand: Run(s => s.PauseAsync(true)); break;
            case ResumeCommand: Run(s => s.PauseAsync(false)); break;
            case StopCommand: userStopped = true; Run(s => s.StopAsync()); break;
            case CancelCommand: Run(s => s.CancelAsync()); notifications.Info("Recording cancelled; the target is being closed and nothing is kept."); break;
            case PickCommand: Run(s => s.Capture.PickState == Protocol.PickStates.Armed ? s.CancelPickAsync() : s.ArmPickAsync()); break;
            case AppendCommand: Apply(RecordedInsertMode.Append); break;
            case InsertCommand: Apply(RecordedInsertMode.InsertAtSelection); break;
            case ReplaceCommand: Apply(RecordedInsertMode.ReplaceSelection); break;
            case DiscardCommand: closing = true; Close(); break;
            case ReviewCommand: if (owner is TDeskTop desktop) OpenReview(desktop); break;
        }
        UpdateView();
    }

    // Decision 5: a Smart recording first probes the deployed Proxy's capability (metadata only). A stale Proxy is never
    // used silently: the user chooses Fix Issues (consent + backup), a deliberate inputs-only recording, or Cancel.
    private void StartRecording()
    {
        if (Session is not null) return;
        if (smart && context.TargetPath is { } exe && RecordingProxyProbe.Probe(exe) is { Capability: ProxyCapability.Stale } probe)
        {
            OfferStaleProxy(probe.Detail);
            return;
        }
        Launch();
    }

    private void Launch()
    {
        string backend = SelectedBackend;
        Attach(SessionFactory?.Invoke(backend)
            ?? new RecordingSession(context.TargetPath!, backend, grid, context.TargetArguments, context.TargetEnvironment)
            { ModifierPicks = smart, SampleInspections = smart });
        if (Session is { } s) s.RequireSmartCapture = smart;
        Run(s => s.StartAsync());
    }

    internal const string StaleProxyTitle = "Smart Capture Unavailable";
    internal static readonly string[] StaleProxyOptions = ["~F~ix Issues...", "Start ~i~nputs only", "Cancel"];

    private void OfferStaleProxy(string detail)
    {
        string text = detail + "\n\nSmart capture (checkpoint proposals, assertion suggestions, picks) needs the Workbench Proxy of this Workbench "
            + "in the target folder.\n\nFix Issues shows what would be replaced and asks for your approval first (a backup is kept). "
            + "Start inputs only records keys and mouse only, for this recording; your Smart capture setting stays as it is.";
        switch (Choose(StaleProxyTitle, text, StaleProxyOptions, 0))
        {
            case 0:
                string exe = context.TargetPath!;
                if (FixIssues?.Invoke(exe) != true) { notifications.Warning("Fix Issues is not available here; open Settings > Target Diagnostics... and use Fix Issues."); break; }
                if (RecordingProxyProbe.Probe(exe).Capability == ProxyCapability.Stale)
                    notifications.Warning("The target's Workbench Proxy was not updated, so Smart capture is still unavailable. Start again to choose.");
                else notifications.Info("The target's Workbench Proxy was checked again. Press Start to record with Smart capture.");
                break;
            case 1:
                inputsOnly = true; smart = false;
                smartBox.value = 0; smartBox.DrawView();
                SmartButtons(); DrawView();
                notifications.Info("Recording inputs only (old target Proxy); your Smart capture setting is unchanged.");
                Launch();
                break;
            default: notifications.Info("Recording not started."); break;
        }
        UpdateView();
    }

    // A choice dialog; the default (Enter) is the option at defaultIndex, Esc and Cancel return -1.
    private int Choose(string title, string text, string[] options, int defaultIndex)
    {
        if (Chooser is { } seam) return seam(title, text, options, defaultIndex);
        if (owner is not TDeskTop desktop) return -1;
        return ChooseDialog(desktop, title, text, options, defaultIndex);
    }

    internal static int ChooseDialog(TDeskTop desktop, string title, string text, string[] options, int defaultIndex)
    {
        var lines = text.Split('\n').SelectMany(l => WorkbenchText.Wrap(l, Math.Min(74, desktop.size.x - 8))).ToArray();
        int buttons = options.Sum(o => Math.Max(10, o.Replace("~", "").Length + 4) + 1) + 5;
        int w = Math.Min(Math.Max(Math.Max(60, buttons), lines.Max(l => l.Length) + 6), desktop.size.x - 2), h = Math.Min(lines.Length + 7, desktop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), title); dialog.options |= Views.ofCentered; dialog.helpCtx = Context;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 4), string.Join('\n', lines)));
        int x = 3; TButton? preferred = null;
        for (int i = 0; i < options.Length; i++)
        {
            bool cancel = i == options.Length - 1 && options[i] == "Cancel";
            int bw = Math.Max(10, options[i].Replace("~", "").Length + 4);
            var button = new TButton(new(x, h - 3, x + bw, h - 1), options[i], cancel ? Views.cmCancel : ChoiceCommands[i],
                i == defaultIndex ? ButtonConstants.bfDefault : ButtonConstants.bfNormal);
            dialog.Insert(button); x += bw + 1;
            if (i == defaultIndex) preferred = button;
        }
        preferred?.Select();
        ushort result = desktop.ExecView(dialog);
        return Array.IndexOf(ChoiceCommands, result) is var index and >= 0 && index < options.Length ? index : -1;
    }

    // Commands that end a TDialog's modal loop: at most two choices plus Cancel.
    private static readonly ushort[] ChoiceCommands = [Views.cmYes, Views.cmNo];

    /// <summary>Shows <paramref name="session"/> in this window (Start uses it; tests attach prepared sessions).</summary>
    internal void Attach(RecordingSession session)
    {
        Session = session;
        userStopped = autoReviewed = followUpPosted = false;
        session.Changed += Refresh;
        UpdateView();
    }

    private void Refresh() => Post(RefreshCommand, null!);

    /// <summary>Test seam: records that the user pressed Stop (as StopCommand does) for a session attached already stopped.</summary>
    internal void StoppedByUserForTesting() { userStopped = true; UpdateView(); }

    // Session work runs off the UI thread; its Changed notifications post RefreshCommand back to this window.
    private void Run(Func<RecordingSession, Task> work)
    {
        if (Session is not { } session) return;
        _ = Task.Run(async () =>
        {
            try { await work(session); }
            catch (Exception ex) { _ = ex; }
            finally { try { Post(RefreshCommand, null!); } catch (Exception) { } }
        });
    }

    // Checkpoint IDs that stay in the document when steps start..start+count-1 are replaced (count 0 = all stay).
    private string[] CheckpointIds(int start, int count) => Editor.Document.Steps.Where((st, i) => st.Kind == "checkpoint" && (i < start || i >= start + count))
        .Select(st => st.String("id")).OfType<string>().ToArray();

    private void Apply(RecordedInsertMode mode)
    {
        if (conversion is not { Steps.Count: > 0 } c) return;
        if (Editor.owner is null) { notifications.Warning("The scenario editor was closed; the recorded steps were not inserted."); return; }
        // Decision 4: proposals the user could still accept are never dropped without saying so. The safe default goes
        // back to Review; Cancel changes nothing. For Replace this one dialog is also the Replace confirmation.
        bool replaceConfirmed = false;
        if (smart && review is { AnyPending: true } unaccepted)
        {
            switch (Choose(PendingTitle, PendingText(unaccepted, mode), [PendingOptions[0], mode == RecordedInsertMode.ReplaceSelection ? "~R~eplace, accepted only" : PendingOptions[1], "Cancel"], 0))
            {
                case 0: if (owner is TDeskTop desktop) OpenReview(desktop); return;
                case 1: replaceConfirmed = true; break;
                default: notifications.Info("Nothing was inserted; the recording and your review decisions are kept."); return;
            }
        }
        try
        {
            // Accepted proposals only (the converter's steps exactly when nothing was accepted).
            IReadOnlyList<ScenarioDocumentStep> steps = c.DocumentSteps;
            // Say why nothing was added (Smart capture off, unavailable, or proposals not accepted).
            string added = "No checkpoint or assertion was added: add expected conditions yourself."
                + (SmartUnavailable(State) ? " Smart capture was unavailable: the target's Workbench Proxy is older than this Workbench (re-prepare the target)."
                    : !smart ? " Smart capture was off, so no checkpoints were proposed."
                    : review is { Checkpoints.Count: > 0 } pending ? $" {pending.Checkpoints.Count} proposed checkpoint{(pending.Checkpoints.Count == 1 ? " was" : "s were")} not accepted (Review...) and not inserted." : "")
                + (CtrlShiftClicks is > 0 and int clicks ? " " + CtrlShiftNote(clicks) : "");
            if (review is { AnyAccepted: true } r)
            {
                var (selStart, selCount) = Editor.Selection;
                var m = RecordingReviewMaterializer.Materialize(r, mode == RecordedInsertMode.ReplaceSelection ? CheckpointIds(selStart, selCount) : CheckpointIds(0, 0));
                if (!m.Ok) { notifications.Warning("Record: " + m.Problems[0] + " Nothing was inserted."); return; }
                steps = m.Steps;
                int skipped = r.Checkpoints.Count - r.AcceptedCount;
                added = $"{m.Checkpoints} accepted checkpoint{(m.Checkpoints == 1 ? "" : "s")} with {m.Assertions} assertion{(m.Assertions == 1 ? "" : "s")} (severity warning) "
                    + "were included; check them before you rely on them." + (m.Renamed.Count > 0 ? " Renamed to avoid duplicate IDs: " + string.Join(", ", m.Renamed) + "." : "")
                    + (skipped > 0 ? $" {skipped} other proposal{(skipped == 1 ? " was" : "s were")} not accepted and not inserted." : "");
            }
            if (smart && !replaceConfirmed && mode == RecordedInsertMode.ReplaceSelection && Editor.Selection.Count > 0 && !Confirmed(
                $"Replace the {Editor.Selection.Count} selected step{(Editor.Selection.Count == 1 ? "" : "s")} with {steps.Count} recorded step{(steps.Count == 1 ? "" : "s")}?\n\n"
                + "The selected steps are removed from the editor (nothing is saved until you Save)."))
                return;
            var (start, count) = Editor.ApplyRecording(mode, steps);
            notifications.Info($"Inserted {count} recorded step{(count == 1 ? "" : "s")} at step {start + 1}; review, edit and Save in the editor. " + added);
            closing = true; Close();
            if (Editor.owner is not null) Editor.Select();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { notifications.Warning("Record: " + ex.Message); }
    }

    internal const string PendingTitle = "Proposals Not Accepted";
    internal static readonly string[] PendingOptions = ["~B~ack to Review", "~I~nsert accepted only"];

    // The decision-4 text: what will be left out (could still be accepted) versus what can never be inserted.
    private string PendingText(RecordingReviewModel r, RecordedInsertMode mode)
    {
        static string N(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
        var lines = new List<string>();
        var left = new List<string>();
        if (r.PendingCheckpoints > 0) left.Add(N(r.PendingCheckpoints, "proposed checkpoint") + " not accepted");
        if (r.PendingAssertions > 0) left.Add(N(r.PendingAssertions, "suggested assertion") + " not selected in accepted checkpoints");
        lines.Add(string.Join(" and ", left) + ": they will NOT be inserted.");
        lines.Add(r.AnyAccepted ? $"Inserted with the recorded steps: {N(r.AcceptedCount, "accepted checkpoint")} with {N(r.SelectedAssertionCount, "assertion")}."
            : "Only the recorded input steps will be inserted.");
        if (r.UnrepresentableCheckpoints + r.NotInsertableAssertions > 0)
            lines.Add($"Not insertable in any case (not affected by this choice): {N(r.UnrepresentableCheckpoints, "proposal")} that cannot be placed, "
                + $"{N(r.NotInsertableAssertions, "assertion")} that cannot be checked or were withheld.");
        if (mode == RecordedInsertMode.ReplaceSelection)
            lines.Add($"Replace removes the {N(Editor.Selection.Count, "selected step")} from the editor (nothing is saved until you Save).");
        lines.Add("Back to Review lets you accept them first; Cancel inserts nothing.");
        return string.Join("\n\n", lines);
    }

    private bool Confirmed(string text) => ConfirmReplace?.Invoke(text)
        ?? (owner is TDeskTop desktop && WorkbenchDialog.Confirm(desktop, "Replace Selection?", text, "~R~eplace") == Views.cmYes);

    // Honest capture status for the review (never presented as a successful Smart recording when it was not one).
    private string[] CaptureStatus()
    {
        var lines = new List<string>();
        var s = Session!;
        if (!s.Capture.BoundariesEnabled)
            lines.Add("Not a Smart recording: the target's Workbench Proxy has no Smart Recorder capture (re-prepare it); input only.");
        else if (!s.Capture.PicksEnabled) lines.Add("The target's Workbench Proxy does not support picks; re-prepare it for Ctrl+Shift picks and Pick Mode.");
        if (s.JournalUsed || s.Capture.Recovered) lines.Add("The target ended the recording; UI boundaries came from the crash journal, so screens are missing.");
        // No backend claim for a recording that had no Smart capture at all. The Console qualification used injected
        // console input; a console hosted by Windows Terminal may keep Shift+click for its own selection (not verified).
        if (s.Capture.BoundariesEnabled)
            // Decision 6: Windows Terminal stays supported; it may keep Ctrl+Shift+click for its own selection, so Pick
            // (no modifier) is the reliable fallback. The qualification used injected console input, not a physical mouse.
            lines.Add(s.Backend == "Win32ConsoleDriver" ? "Console window: Smart capture and picking are qualified on the Win32 Console "
                + "with injected input. Windows Terminal may take Ctrl+Shift+click for its own selection: use Pick there."
                : $"{WorkbenchRecorder.BackendLabels[Math.Max(0, Array.IndexOf(WorkbenchRecorder.Backends, s.Backend))]}: Smart capture and picking are NOT qualified on this backend; check every proposal.");
        return [.. lines];
    }

    public override bool Valid(ushort command)
    {
        if (command == Views.cmClose && !closing)
        {
            var state = State;
            if (state is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping)
            {
                notifications.Info("A recording is in progress: Stop keeps it, Cancel discards it. The window closes afterwards with Discard.");
                return false;
            }
            if (state == RecordingState.Stopped && conversion is { Steps.Count: > 0 } && owner is TDeskTop desktop
                && WorkbenchDialog.Confirm(desktop, "Discard Recording?", $"{conversion.Steps.Count} recorded steps have not been inserted into the editor.\n\nDiscard them?", "~D~iscard") != Views.cmYes)
                return false;
        }
        return base.Valid(command);
    }

    public override void ShutDown()
    {
        if (ReferenceEquals(Active, this)) Active = null;
        foreach (ushort command in Commands) EnableCommand(command);
        // Closing the Workbench (or this window) never leaves a target running: cancel and wait for its bounded cleanup.
        if (Session is { Live: true } live)
        {
            try { Task.Run(() => live.CancelAsync()).Wait(TimeSpan.FromSeconds(20)); }
            catch (AggregateException) { }
        }
        base.ShutDown();
    }

    public override void SizeLimits(ref TPoint min, ref TPoint max) { base.SizeLimits(ref min, ref max); min = new(Math.Min(80, max.x), Math.Min(18, max.y)); }
}
