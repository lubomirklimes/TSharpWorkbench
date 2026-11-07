using TSharpVision;
using TSharpVision.Constants;

internal enum RunState { Starting, Running, Cancelling, Finished, Cancelled, EndedUnexpectedly }

// Everything the Run Monitor shows, computed by the workspace from the active request and runner progress.
internal sealed record RunMonitorView(RunState State, string Setup, int Completed, int Total, int Passed, int Failed, int Blocked,
    int Cancelled, string Now, string Visual, string Target, string Latest, string Folder)
{
    /// <summary>Executions skipped because the platform/driver cannot run them (not failures).</summary>
    internal int Skipped { get; init; }
}

// Run Monitor (window 3, Alt+3 while a run is active): modeless, bottom-right, never the default focus of Cancel run. Esc returns to the
// previously active window and the run continues; Close while a run is active does the same instead of closing.
internal sealed class WorkbenchRunMonitor : TWindow
{
    internal const ushort Context = 0x7d98;
    internal const string WindowTitle = "Run Monitor";
    private readonly Func<bool> active;
    private readonly Action cancel;
    private readonly WorkbenchNotifications notifications;
    internal Action? Closed { get; set; }
    // The window Esc returns to; the workspace when the monitor was activated some other way.
    internal TView? Previous { get; set; }
    internal TProgressBar Progress { get; }
    internal TButton CancelButton { get; }
    internal TButton ResultsButton { get; }
    internal RunMonitorView? View { get; private set; }
    internal RunState State => View?.State ?? RunState.Starting;
    internal string Text => string.Join("\n", lines.Select(l => l.Current));

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void SetText(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = WorkbenchText.Fit(Text, size.x);
    }
    // TProgressBar maps its fill to the dialog scroll-bar role; in a blue window that role draws blue blocks on blue.
    // Same control and drawing, with the window roles: scroll-bar page for the track, active frame (white) for the fill.
    private sealed class WindowProgressBar(TRect bounds) : TProgressBar(bounds, 0, 1)
    {
        private static readonly TPalette WindowPalette = new("\x04\x02\x05\x04", 4);
        public override TPalette GetPalette() => WindowPalette;
    }
    private readonly Line setup, stateLine, count, now, finished, visual, target, latest, folder, hint;
    private readonly Line[] lines;

    internal static TRect DefaultBounds(TDeskTop desktop, TRect? remembered = null)
    {
        int dw = desktop.size.x, dh = desktop.size.y;
        // Remembered geometry (user settings) when it still fits the desktop; otherwise bottom-right.
        if (remembered is { } r && r.a.x >= 0 && r.a.y >= 0 && r.b.x <= dw && r.b.y <= dh && r.b.x - r.a.x >= Math.Min(60, dw) && r.b.y - r.a.y >= Math.Min(16, dh)) return r;
        int w = Math.Min(73, dw), h = Math.Min(16, dh);
        int x = Math.Max(0, dw - w - 1), y = Math.Max(0, dh - h - 1);
        return new(x, y, x + w, y + h);
    }

    internal WorkbenchRunMonitor(TDeskTop desktop, Func<bool> active, Action cancel, WorkbenchNotifications notifications, TRect? remembered = null)
        : base(DefaultBounds(desktop, remembered), WindowTitle, 3)
    {
        this.active = active; this.cancel = cancel; this.notifications = notifications; helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        int w = size.x, h = size.y;
        Line Row(int y, int x0 = 2, int x1 = -2, byte grow = Views.gfGrowHiX)
        { var line = new Line(new(x0, y, x1 < 0 ? w + x1 : x1, y + 1)) { growMode = grow }; Insert(line); return line; }
        setup = Row(1); stateLine = Row(2);
        Progress = new WindowProgressBar(new(2, 3, w - 14, 4)) { growMode = Views.gfGrowHiX }; Insert(Progress);
        count = Row(3, w - 13, w - 2, Views.gfGrowLoX | Views.gfGrowHiX);
        now = Row(5); finished = Row(6); visual = Row(7); target = Row(8); latest = Row(9); folder = Row(10);
        byte bottom = (byte)(Views.gfGrowLoY | Views.gfGrowHiY);
        hint = Row(h - 5, 2, -2, (byte)(Views.gfGrowHiX | bottom));
        lines = [setup, stateLine, count, now, finished, visual, target, latest, folder, hint];
        CancelButton = new TButton(new(2, h - 3, 20, h - 1), "~C~ancel run", WorkbenchDialog.CancelRun, ButtonConstants.bfNormal) { growMode = bottom };
        Insert(CancelButton);
        ResultsButton = new TButton(new(21, h - 3, 36, h - 1), "~R~esults", WorkbenchDialog.Results, ButtonConstants.bfDefault) { growMode = bottom };
        Insert(ResultsButton);
        // The Results button is the safe default: an accidental Enter never cancels.
        ResultsButton.Select();
    }

    internal void Update(RunMonitorView view)
    {
        View = view;
        setup.SetText(view.Setup);
        int done = Math.Max(0, view.Completed - view.Cancelled);
        stateLine.SetText(view.State switch
        {
            RunState.Starting => "Starting... launching the owned target.",
            RunState.Running => $"Running {Math.Min(done + 1, Math.Max(1, view.Total))} of {view.Total}",
            RunState.Cancelling => "CANCELLING - waiting for the owned target to close...",
            RunState.Cancelled => $"CANCELLED ({done} of {view.Total} completed)",
            RunState.EndedUnexpectedly => $"! ENDED UNEXPECTEDLY ({done} of {view.Total} completed)",
            _ => view.Failed + view.Blocked + view.Skipped == 0 ? $"Finished: all {done} passed"
                : $"Finished: {view.Passed} passed, {view.Failed} failed, {view.Blocked} blocked" + (view.Skipped > 0 ? $", {view.Skipped} skipped" : "")
        });
        // No false 100%: cancelled executions never count as completed; an empty run has an empty range.
        Progress.SetRange(0, Math.Max(1, view.Total)); Progress.Value = done;
        count.SetText($"{done} of {view.Total}".PadLeft(count.size.x));
        now.SetText("Now        " + view.Now);
        finished.SetText($"Finished   Passed {view.Passed}   Failed {view.Failed}   Blocked {view.Blocked}   " + (view.Skipped > 0 ? $"Skipped {view.Skipped}   " : "") + $"Cancelled {view.Cancelled}");
        visual.SetText("Visual     " + view.Visual);
        target.SetText("Target     " + view.Target);
        latest.SetText("Latest     " + view.Latest);
        folder.SetText("Folder     " + WorkbenchText.Path(view.Folder, Math.Max(10, folder.size.x - 11)));
        bool running = view.State is RunState.Starting or RunState.Running or RunState.Cancelling;
        hint.SetText(running ? "Esc returns to the workspace; the run continues in the background."
            : "Esc returns to the workspace; Close (Alt+F3) closes this monitor.");
        ApplyCancelState();
    }

    // Cancel run is a global command, so TButton re-reads its state from the command set on every cmCommandSetChanged
    // broadcast; the monitor's own state (Starting/Running and an active request, never Cancelling) is re-applied after it.
    private void ApplyCancelState()
    {
        bool canCancel = View is { State: RunState.Starting or RunState.Running } && active();
        if (CancelButton.GetState(Views.sfDisabled) == canCancel)
        {
            if (!canCancel && ReferenceEquals(current, CancelButton)) ResultsButton.Select();
            CancelButton.SetState(Views.sfDisabled, !canCancel); CancelButton.DrawView();
        }
    }

    internal void ReturnToPrevious()
    {
        var target = Previous is { owner: not null } previous && !ReferenceEquals(previous, this) ? previous : null;
        if (target is not null) target.Select();
        else if (owner is TGroup desktop) desktop.SelectNext(false);
    }

    public override void ChangeBounds(TRect bounds) { base.ChangeBounds(bounds); if (View is { } view) Update(view); }
    public override void SizeLimits(ref TPoint min, ref TPoint max) { base.SizeLimits(ref min, ref max); min = new(Math.Min(60, max.x), Math.Min(16, max.y)); }
    public override void ShutDown() { base.ShutDown(); Closed?.Invoke(); }

    public override bool Valid(ushort command)
    {
        if (command == Views.cmClose && active())
        {
            ReturnToPrevious();
            notifications.Info("The Run Monitor stays open while tests run. Use Cancel run to stop; Alt+3 shows it again.");
            return false;
        }
        return base.Valid(command);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        // Esc never cancels a run: it only returns to the previous window.
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEsc) { ClearEvent(ref ev); ReturnToPrevious(); return; }
        if (ev.What == Events.evCommand && ev.message.command == WorkbenchDialog.CancelRun) { ClearEvent(ref ev); cancel(); return; }
        // A TWindow has no default-button handling: Enter presses the focused button, otherwise the default Results.
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter)
        {
            ClearEvent(ref ev);
            if (ReferenceEquals(current, CancelButton) && !CancelButton.GetState(Views.sfDisabled)) cancel();
            else { var results = new TEvent { What = Events.evCommand }; results.message.command = WorkbenchDialog.Results; PutEvent(ref results); }
            return;
        }
        bool commandSet = ev.What == Events.evBroadcast && ev.message.command == Views.cmCommandSetChanged;
        base.HandleEvent(ref ev);
        if (commandSet) ApplyCancelState();
    }
}
