using System.Diagnostics;
using System.Globalization;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Interaction recorder. The user drives the real target (an ordinary application under the Workbench
// Proxy, in its own visible console or SDL window); the target's Proxy copies the input its real backend delivered, Workbench
// polls it over the existing authenticated pipe, and the converter turns the raw events into schema-v1 steps the user reviews
// and inserts into the internal editor. Nothing here infers intent, adds checkpoints/assertions or approves baselines.

/// <summary>Startup wiring (lead patch: one line in WorkbenchApp's constructor).</summary>
internal static class WorkbenchRecorder
{
    /// <summary>Visible backends a person can type into. NullDriver is headless and cannot be recorded.</summary>
    internal static readonly string[] Backends = ["Win32ConsoleDriver", "SDLDriver", "SDLGpuDriver"];
    internal static readonly string[] BackendLabels = ["Console window", "SDL window", "SDL GPU window"];

    /// <summary>Smart capture (Ctrl+Shift picks, Pick Mode, live evidence sampling, checkpoint proposals and
    /// assertion suggestions with a review after Stop). Precedence: test seam <see cref="SmartCaptureOverride"/>, then the
    /// environment variable TSW_SMART_RECORDER (1 = on, 0 = off), then the user setting (WorkbenchPreferences.SmartRecorder,
    /// the recording window's Smart capture check box), else ON (owner decision 1: an explicit OFF setting stays OFF).</summary>
    internal static bool SmartCapture => ResolveSmartCapture(SmartCaptureOverride, Environment.GetEnvironmentVariable("TSW_SMART_RECORDER"), Setting);
    /// <summary>The value forced by the test seam or the environment (the check box is then read-only), else null.</summary>
    internal static bool? SmartCaptureForced => SmartCaptureOverride ?? EnvironmentChoice(Environment.GetEnvironmentVariable("TSW_SMART_RECORDER"));
    /// <summary>Pure precedence: test seam, then TSW_SMART_RECORDER (1/0; anything else is ignored), then the setting, else ON.</summary>
    internal static bool ResolveSmartCapture(bool? overrideValue, string? environment, bool? setting) => overrideValue ?? EnvironmentChoice(environment) ?? setting ?? true;
    private static bool? EnvironmentChoice(string? value) => value switch { "1" => true, "0" => false, _ => null };
    /// <summary>Test seam: forces <see cref="SmartCapture"/> on or off (null = environment, then setting).</summary>
    internal static bool? SmartCaptureOverride { get; set; }
    /// <summary>Settings file of the running Workbench (null = the default user settings path).</summary>
    internal static string? PreferencesPath { get; set; }

    private static bool? Setting
    {
        get { try { return WorkbenchPreferences.Load(PreferencesPath).SmartRecorder; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }
    }

    /// <summary>Persists the user's Smart capture choice (read-modify-write of the settings file). False when it could not be saved.</summary>
    internal static bool SaveSmartCapture(bool enabled)
    {
        try { WorkbenchPreferences.Update(PreferencesPath, p => p with { SmartRecorder = enabled }); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static void Install(WorkbenchApp app)
    {
        PreferencesPath = app.PreferencesPath;
        WorkbenchScenarioEditorWindow.RecordRequested = editor => Open(app, editor);
        // Decision 5: the recording window offers Fix Issues (the existing consent + backup path) for a stale Proxy.
        WorkbenchRecordingWindow.FixIssues ??= exe => { if (app.Project is not { } project) return false; project.ShowFixIssues(exe); return true; };
    }

    /// <summary>Why recording is unavailable for this editor context, or null when it can start.</summary>
    internal static string? Unavailable(ScenarioEditorContext context)
    {
        if (!context.Proxy) return "Recording is available for ordinary-application (proxy-logical) scenarios only. Diagnostic "
            + "(compiled Testing) targets run headless through their own host, so there is no window to type into.";
        if (context.ReadOnly) return "This document is read-only; recorded steps cannot be inserted into it.";
        if (context.TargetPath is not { Length: > 0 } target) return "The project has no target executable; set it in Settings > Project Properties first.";
        if (!File.Exists(target)) return Path.GetFileName(target) + " does not exist; recording needs the target executable.";
        if (!OperatingSystem.IsWindows()) return "Physical recording is qualified on Windows only.";
        return null;
    }

    /// <summary>Recording grid: the document's first default grid, else the project's first run size, else 80x25.</summary>
    internal static string Grid(ScenarioEditorContext context)
    {
        foreach (string candidate in (context.DocumentGridDefaults ?? []).Concat(context.ScreenSizes))
            if (ScenarioSelection.TryParseSize(candidate, out _, out _)) return candidate;
        return "80x25";
    }

    /// <summary>The project's Proxy backend when it is a visible one, else the Console window.</summary>
    internal static int DefaultBackend(ScenarioEditorContext context) => Math.Max(0, Array.IndexOf(Backends, context.ProxyBackend));

    private static void Open(WorkbenchApp app, WorkbenchScenarioEditorWindow editor)
    {
        var desktop = app.DeskTop!;
        if (WorkbenchRecordingWindow.Active is { owner: not null } existing)
        {
            existing.Select();
            app.Notifications.Info(ReferenceEquals(existing.Editor, editor) ? "The recording window is already open for this scenario."
                : "Another recording window is open; finish or close it first (one recording at a time).");
            return;
        }
        var context = editor.RecordingContext();
        if (Unavailable(context) is { } reason) { app.Notifications.Warning("Record: " + reason); return; }
        var window = new WorkbenchRecordingWindow(desktop, editor, context, app.Notifications);
        desktop.Insert(window);
        window.Select();
    }
}

// ── session controller ─────────────────────────────────────────────────────────────────────────────────────────────────

internal enum RecordingState { Ready, Starting, Recording, Paused, Stopping, Stopped, Cancelled, Failed }

/// <summary>Thrown when the target's deployed Workbench Proxy predates recording (no RecordKinds in the handshake).</summary>
internal sealed class RecorderUnavailableException(string message) : IOException(message);

/// <summary>Decision 5: Smart capture was required but the target's Proxy negotiated no Smart Recorder capture. The
/// session ends before anything is recorded (never a silent downgrade to input only).</summary>
internal sealed class SmartCaptureUnavailableException(string message) : IOException(message);

/// <summary>
/// One recording session: launches the target once through the existing owned Proxy session (visible backend, recording flag,
/// temporary profile), starts the Proxy recorder, polls it, and on Stop/Cancel shuts the target down and removes its temporary
/// root. Pipe operations are serialized (one outstanding request, as the protocol requires). Not thread-affine; the window
/// marshals <see cref="Changed"/> to the UI thread.
/// </summary>
internal sealed class RecordingSession : IAsyncDisposable
{
    internal const int LifetimeSeconds = 1800, PollMilliseconds = 150;
    private readonly string target, size;
    private readonly string[] arguments;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly SemaphoreSlim pipe = new(1, 1);
    private readonly CancellationTokenSource cancel = new(), polling = new();
    private readonly object gate = new();
    private readonly List<RecordedInput> events = [];
    private readonly Stopwatch clock = new();
    private TargetSession? session;
    private Task? pollLoop;
    private long next;
    private volatile RecordingState state = RecordingState.Ready;

    internal RecordingSession(string target, string backend, string size, string[]? arguments = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!WorkbenchRecorder.Backends.Contains(backend)) throw new ArgumentException("Recording needs a visible backend (Console, SDL or SDL GPU window).");
        this.target = target; Backend = backend; this.size = size;
        this.arguments = arguments ?? []; this.environment = environment ?? new Dictionary<string, string>();
        var (w, h) = ScenarioSelection.ParseSize(size); Width = w; Height = h;
    }

    internal string Backend { get; }
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal RecordingState State => state;
    internal string? Failure { get; private set; }
    internal string? Ended { get; private set; }
    internal RecordingBatch? Last { get; private set; }
    internal ScreenSnapshot? StartScreen { get; private set; }
    internal ScreenSnapshot? EndScreen { get; private set; }
    internal int? ProcessId { get; private set; }
    internal string? HostMode { get; private set; }
    internal bool? WindowVisible { get; private set; }
    internal bool? Cleanup { get; private set; }
    internal bool? TargetExited { get; private set; }
    internal string? StopOutcome { get; private set; }
    internal bool JournalUsed { get; private set; }
    internal TimeSpan Elapsed => clock.Elapsed;
    internal event Action? Changed;
    internal IReadOnlyList<RecordedInput> Events { get { lock (gate) return events.ToArray(); } }
    /// <summary>Boundaries, retained snapshots and coverage of this recording (see RecordingCaptureTimeline). Kept after
    /// Stop for review; cleared on Cancel and Dispose.</summary>
    internal RecordingCaptureTimeline Capture { get; } = new();
    /// <summary>Why Smart Recorder capture is unavailable or partial for this target (null = full capture).</summary>
    internal string? CaptureDiagnostic => Capture.Diagnostic;
    /// <summary>The last RecordInspect was refused as busy (nonfatal); the reason, else null.</summary>
    internal string? InspectBusy { get; private set; }

    // ── Opt-ins (default OFF; set before StartAsync) ──
    /// <summary>Ask the target to intercept Ctrl+Shift+left press/drag as picks (sent with RecordStart; needs picks/1).</summary>
    internal bool ModifierPicks { get; set; }
    /// <summary>Take live RecordInspect samples of states that stayed unchanged for a poll (evidence for proposals).</summary>
    internal bool SampleInspections { get; set; }
    /// <summary>Smart capture is required; a Proxy without Smart Recorder capture ends the session before recording
    /// (<see cref="SmartUnavailable"/>) instead of silently recording input only.</summary>
    internal bool RequireSmartCapture { get; set; }
    /// <summary>The session failed because <see cref="RequireSmartCapture"/> was not met.</summary>
    internal bool SmartUnavailable { get; set; }
    /// <summary>The target supports picks (Pick Mode; Ctrl+Shift only when <see cref="ModifierPicks"/> was requested).</summary>
    internal bool PicksAvailable => Capture.PicksEnabled;
    /// <summary>Why the last ArmPickAsync/CancelPickAsync was refused (nonfatal), else null.</summary>
    internal string? PickRefused { get; private set; }
    internal bool Live => state is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping;

    private void Notify() { try { Changed?.Invoke(); } catch (Exception) { /* a closed window must not break the session */ } }

    /// <summary>Test/capture seam: a session in a given state holding the given events, without any process.</summary>
    internal static RecordingSession ForTesting(string backend, string size, RecordingState state, IEnumerable<RecordedInput> recorded,
        Func<string, long, long?, Task<Reply>>? transport = null)
    {
        var s = new RecordingSession("unused.exe", backend, size) { state = state, transport = transport };
        s.events.AddRange(recorded);
        s.Last = new([], s.events.Count == 0 ? 0 : s.events[^1].Sequence, state is RecordingState.Recording, state is RecordingState.Paused, 0, 7, 2, 0, s.Width, s.Height, backend);
        return s;
    }

    internal async Task StartAsync()
    {
        if (state != RecordingState.Ready) throw new InvalidOperationException("A recording session starts once.");
        state = RecordingState.Starting; Notify();
        try
        {
            session = new TargetSession(target, null, size, cancel.Token, null, LifetimeSeconds, Backend, true, arguments, environment, record: true);
            var hello = await session.ConnectAsync();
            ProcessId = session.ProcessId;
            HostMode = hello.Target?.Console?.HostMode; WindowVisible = hello.Target?.Console?.WindowVisible;
            if (hello.Capabilities?.Mode != "proxy-logical") throw new IOException("The target did not start a Workbench Proxy session.");
            if (hello.Capabilities.RecordKinds is not { Length: > 0 })
                throw new RecorderUnavailableException("The target's Workbench Proxy was deployed before recording existed. Update it with "
                    + "Settings > Target Diagnostics... (Fix Issues) or Prepare target, then record again.");
            // Smart Recorder capture only as far as advertised; an older Proxy keeps legacy recording (CaptureDiagnostic says why).
            Capture.Negotiate(hello.Capabilities.RecordFeatures);
            if (RequireSmartCapture && !Capture.BoundariesEnabled)
            {
                SmartUnavailable = true;
                throw new SmartCaptureUnavailableException("The target's Workbench Proxy has no Smart capture (it is older than this Workbench); nothing was recorded.");
            }
            await Send(Protocol.Operations.Start, Protocol.States.Started);
            string[]? options = ModifierPicks && Capture.PicksEnabled ? [Protocol.RecordOptions.ModifierPicks] : null;
            var started = await Send(Protocol.Operations.RecordStart, Protocol.States.Recording, recordOptions: options);
            StartScreen = Decode(started.Recording?.Snapshot);
            Merge(started.Recording);
            clock.Start();
            state = RecordingState.Recording;
            pollLoop = Task.Run(PollAsync);
        }
        catch (Exception ex)
        {
            Failure = cancel.IsCancellationRequested ? null : Describe(ex);
            state = cancel.IsCancellationRequested ? RecordingState.Cancelled : RecordingState.Failed;
            await CleanupAsync(readJournal: false);
        }
        Notify();
    }

    internal async Task PauseAsync(bool pause)
    {
        if (state is not (RecordingState.Recording or RecordingState.Paused)) return;
        try
        {
            var reply = await Send(pause ? Protocol.Operations.RecordPause : Protocol.Operations.RecordResume, Protocol.States.Recording);
            Merge(reply.Recording);
            if (state is RecordingState.Recording or RecordingState.Paused) state = pause ? RecordingState.Paused : RecordingState.Recording;
            if (pause) clock.Stop(); else clock.Start();
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested) { await TargetEndedAsync(ex); }
        Notify();
    }

    /// <summary>Ends the recording, keeps the recorded input, shuts the target down and cleans up.</summary>
    internal async Task StopAsync()
    {
        if (state is not (RecordingState.Recording or RecordingState.Paused)) return;
        state = RecordingState.Stopping; Notify();
        polling.Cancel();
        try { if (pollLoop is not null) await pollLoop; } catch (Exception) { }
        if (state != RecordingState.Stopping) { Notify(); return; } // the target ended meanwhile (already cleaned up)
        try
        {
            var stopped = await Send(Protocol.Operations.RecordStop, Protocol.States.Recording);
            EndScreen = Decode(stopped.Recording?.Snapshot);
            Merge(stopped.Recording);
            while (stopped.Recording is { Events.Length: >= 512 } or { Boundaries.Length: >= 512 })
                Merge((stopped = await Send(Protocol.Operations.RecordPoll, Protocol.States.Recording)).Recording);
            // The frozen store stays readable after RecordStop; fetch what is left before Shutdown (bounded). A failure
            // here leaves SnapshotsPending set, so coverage is never reported complete.
            try { await FetchSnapshotsAsync(StopSnapshotBatches); } catch (Exception ex) when (ex is IOException or TimeoutException) { }
            try { await Send(Protocol.Operations.Shutdown, Protocol.States.ShutdownAccepted); } catch (Exception) { }
            clock.Stop();
            await CleanupAsync(readJournal: false);
            state = RecordingState.Stopped;
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested) { await TargetEndedAsync(ex); }
        Notify();
    }

    /// <summary>Discards everything and stops/cleans up the target. Safe in any state.</summary>
    internal async Task CancelAsync()
    {
        if (state is RecordingState.Cancelled) return;
        bool live = Live || session is not null;
        state = RecordingState.Cancelled;
        cancel.Cancel(); polling.Cancel();
        try { if (pollLoop is not null) await pollLoop; } catch (Exception) { }
        if (live) await CleanupAsync(readJournal: false);
        lock (gate) events.Clear();
        Capture.Clear();
        clock.Stop();
        Notify();
    }

    public async ValueTask DisposeAsync()
    {
        if (Live || session is not null) await CancelAsync();
        Capture.Clear();
        polling.Dispose(); cancel.Dispose(); pipe.Dispose();
    }

    private async Task PollAsync()
    {
        while (!polling.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollMilliseconds, polling.Token);
                await PollOnceAsync();
                Notify();
            }
            catch (OperationCanceledException) when (polling.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested || polling.IsCancellationRequested) return;
                await TargetEndedAsync(ex); Notify(); return;
            }
        }
    }

    internal const int StopSnapshotBatches = 64;

    /// <summary>One poll pass (poll task only, never the UI thread): events and boundaries until a short batch, then the
    /// retained snapshots the target announced, in bounded batches.</summary>
    internal async Task PollOnceAsync()
    {
        RecordingBatch? batch;
        do { batch = (await Send(Protocol.Operations.RecordPoll, Protocol.States.Recording)).Recording; Merge(batch); }
        while (batch is { Events.Length: >= 512 } or { Boundaries.Length: >= 512 } && !polling.IsCancellationRequested);
        await FetchSnapshotsAsync(RecordingCaptureTimeline.SnapshotBatchesPerPoll);
        if (SampleInspections) await SampleAsync();
    }

    private long sampleCandidate = -1, sampled = -1;

    // At most one live inspection per poll, of the latest state boundary once it stayed the latest for a whole poll
    // (quasi-settled; never during a burst of changes) and was not sampled yet. Transient states with their own retained
    // snapshot are not re-sampled. The detector matches a sample to a boundary by Hash AND AfterSequence only.
    private async Task SampleAsync()
    {
        if (state != RecordingState.Recording || !Capture.InspectEnabled) return;
        var latest = Capture.Boundaries.LastOrDefault(b => b.Kind != Protocol.BoundaryKinds.Pick);
        if (latest is null || latest.Id == sampled) return;
        if (latest.Id != sampleCandidate || latest.SnapshotId is not null) { sampleCandidate = latest.Id; return; }
        sampled = latest.Id;
        // A refusal (the recording ended in between) is not a target failure; anything else still is.
        try { if (await InspectAsync() is { } sample) Capture.AddInspection(sample); }
        catch (IOException ex) when (ex.Message.StartsWith("Unsupported operation or invalid state", StringComparison.Ordinal)) { }
    }

    /// <summary>Arms the target's one-shot Pick Mode (the next left press/drag in the target is a pick, swallowed).
    /// False when unsupported, not recording, paused or refused (<see cref="PickRefused"/> says why); never fatal.</summary>
    internal Task<bool> ArmPickAsync() => PickAsync(Protocol.Operations.RecordPickArm);

    /// <summary>Disarms Pick Mode (a gesture already in progress still completes in the target).</summary>
    internal Task<bool> CancelPickAsync() => PickAsync(Protocol.Operations.RecordPickCancel);

    private async Task<bool> PickAsync(string operation)
    {
        if (!Capture.PicksEnabled) { PickRefused = "The target's Workbench Proxy does not support picking; re-prepare it (Fix Issues or Prepare target)."; return false; }
        if (state is not RecordingState.Recording) { PickRefused = "Picking needs an active, unpaused recording."; return false; }
        try
        {
            var reply = await Send(operation, Protocol.States.Recording);
            Merge(reply.Recording); PickRefused = null;
            Notify();
            return true;
        }
        catch (IOException ex) when (ex.Message.Contains("Unsupported operation or invalid state", StringComparison.Ordinal))
        { PickRefused = ex.Message; return false; }
    }

    /// <summary>Detector + pick resolver + planner over an immutable copy of this recording (never inserted,
    /// never persisted). Final after Stop or target exit.</summary>
    internal SmartRecordingAnalysis Analyze(RecordingConversion conversion, IReadOnlyCollection<string>? existingCheckpointIds = null) =>
        SmartRecordingAnalyzer.Analyze(Events, Capture, conversion, Width, Height, state is RecordingState.Stopped, existingCheckpointIds ?? []);

    // At most `batches` RecordSnapshots replies (each <= 8 snapshots, <= 1 MiB); stops when nothing advances.
    private async Task FetchSnapshotsAsync(int batches)
    {
        for (int i = 0; i < batches && Capture.SnapshotsPending && !cancel.IsCancellationRequested; i++)
        {
            var reply = await Send(Protocol.Operations.RecordSnapshots, Protocol.States.Recording, Capture.SnapshotCursor);
            if (!Capture.AddSnapshots(reply.Snapshots)) break;
        }
    }

    /// <summary>Read-only live inspection (cells + inspection from one UI pass). Null when the target does not advertise
    /// it, the recording is not live, or the target answered busy (nonfatal: <see cref="InspectBusy"/> says why; the session
    /// continues). Shares the single-pipe semaphore with polling.</summary>
    internal async Task<RecordInspection?> InspectAsync()
    {
        if (!Capture.InspectEnabled || state is not (RecordingState.Recording or RecordingState.Paused)) return null;
        try
        {
            var reply = await Send(Protocol.Operations.RecordInspect, Protocol.States.Inspected);
            InspectBusy = null;
            return reply.Inspect;
        }
        catch (TargetBusyException busy) { InspectBusy = busy.Message.Length > 0 ? busy.Message : "busy"; return null; }
    }

    // The target quit, crashed or timed out while recording: keep what was received plus the journal, then clean up.
    private async Task TargetEndedAsync(Exception reason)
    {
        polling.Cancel();
        if (state is RecordingState.Cancelled or RecordingState.Stopped or RecordingState.Failed) return;
        clock.Stop();
        Ended = "The target ended the recording (" + Describe(reason) + "). The input recorded up to that point was kept; "
            + "review the last steps: one of them may have quit the application.";
        await CleanupAsync(readJournal: true);
        state = RecordingState.Stopped;
    }

    private Task? cleanup;
    // Exactly one cleanup per session, whoever asks first (Start failure, Stop, Cancel, target exit); later callers await it.
    private Task CleanupAsync(bool readJournal) { lock (gate) return cleanup ??= CleanupCoreAsync(readJournal); }

    private async Task CleanupCoreAsync(bool readJournal)
    {
        var owned = session;
        if (owned is null) return;
        try
        {
            if (readJournal)
            {
                await owned.CollectDiagnosticsAsync();
                var journal = RecordingJournal.Read(Path.Combine(owned.StateDirectory, RecordingJournal.FileName));
                lock (gate)
                {
                    long last = events.Count == 0 ? 0 : events[^1].Sequence;
                    var missing = journal.Where(e => e.Sequence > last).ToArray();
                    if (missing.Length > 0) { events.AddRange(missing); JournalUsed = true; }
                }
                // Boundaries from their own background-written journal (snapshot payloads are never persisted).
                Capture.Recover(RecordingBoundaryJournal.Read(Path.Combine(owned.StateDirectory, RecordingBoundaryJournal.FileName)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { await owned.StopAsync(); } catch (Exception) { }
        await owned.DisposeAsync();
        session = null;
        Cleanup = owned.Cleanup; StopOutcome = owned.StopOutcome;
        TargetExited = ProcessId is not { } pid || Exited(pid);
    }

    private static bool Exited(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private Func<string, long, long?, Task<Reply>>? transport; // test seam: (operation, after, marksAfter) -> reply

    // One outstanding request on the pipe (polling, Stop, Pause, Inspect, snapshot retrieval all share it). `after` defaults
    // to the event cursor; RecordSnapshots passes the snapshot cursor. MarksAfter only once capture was negotiated.
    private async Task<Reply> Send(string operation, string expected, long? afterOverride = null, string[]? recordOptions = null)
    {
        await pipe.WaitAsync();
        try
        {
            long after; lock (gate) after = afterOverride ?? next;
            long? marks = Capture.MarksAfter;
            if (transport is not null) return TargetSession.Validate(await transport(operation, after, marks), TestId, TestId, expected);
            var owned = session ?? throw new IOException("The recording session has ended.");
            return await owned.SendAsync(operation, expected, after: after, marksAfter: marks, recordOptions: recordOptions);
        }
        finally { pipe.Release(); }
    }

    /// <summary>Test seam: the reply Id/Instance a <see cref="ForTesting"/> transport must answer with.</summary>
    internal const string TestId = "test";

    private void Merge(RecordingBatch? batch)
    {
        if (batch is null) return;
        lock (gate)
        {
            long last = events.Count == 0 ? 0 : events[^1].Sequence;
            events.AddRange(batch.Events.Where(e => e.Sequence > last));
            next = Math.Max(next, batch.Next);
            Last = batch;
            if (events.Count == 0) { Width = batch.Width; Height = batch.Height; }
        }
        Capture.Merge(batch);
    }

    private static ScreenSnapshot? Decode(string? cells)
    {
        if (cells is null) return null;
        try { return ScreenSnapshot.Deserialize(Convert.FromBase64String(cells)); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException) { return null; }
    }

    private static string Describe(Exception ex) => ex switch
    {
        RecorderUnavailableException => ex.Message,
        TimeoutException => "the target did not answer in time",
        OperationCanceledException => "the session ended",
        _ => ex.Message,
    };
}

// ── raw events -> schema-v1 steps ──────────────────────────────────────────────────────────────────────────────────────

internal sealed record RecordedStep(ScenarioDocumentStep Step, long First, long Last);

/// <summary>Converter output. Notes are review information (never steps); Mismatches come from the replay self-check.</summary>
internal sealed record RecordingConversion(IReadOnlyList<RecordedStep> Steps, IReadOnlyList<string> Notes, IReadOnlyList<string> Mismatches,
    int Skipped)
{
    internal IReadOnlyList<ScenarioDocumentStep> DocumentSteps => Steps.Select(s => s.Step).ToArray();
}

/// <summary>
/// Deterministic mapping of recorded input to schema-v1 steps (see agent-c-report.md §5). Printable characters become text
/// runs; named keys become key steps (wrapped in modifiers steps when that reproduces the delivered modifier state);
/// mouse presses become click/double/down-up steps at the delivered target-grid cell; wheel notches are coalesced; a grid
/// change becomes a resize step. No timing, waits, checkpoints or assertions are ever produced.
/// </summary>
internal static class RecordingConverter
{
    internal const int LongPauseMilliseconds = 1500, MaximumNotes = 40;
    private const uint ModifierMask = Keys.kbShift | Keys.kbCtrlShift | Keys.kbAltShift;

    private sealed class Draft(string kind, string name, long first, long last)
    {
        internal string Kind = kind, Name = name; internal long First = first, Last = last;
        internal int X, Y, Count = 1; internal string? Note; internal int W, H;
        internal ScenarioDocumentStep Build() => Kind switch
        {
            "text" => ScenarioDocumentStep.Text(Name, Note),
            "key" => ScenarioDocumentStep.Key(Name, Note),
            "modifiers" => ScenarioDocumentStep.Modifier(Name[..Name.IndexOf('-')], Name.EndsWith("-down", StringComparison.Ordinal), Note),
            "mouse" => ScenarioDocumentStep.Mouse(Name, X, Y, Count, Note),
            "release" => ScenarioDocumentStep.Release(Note),
            "resize" => ScenarioDocumentStep.Resize(W, H, Note),
            _ => throw new InvalidOperationException(Kind),
        };
    }

    internal static RecordingConversion Convert(IReadOnlyList<RecordedInput> recorded, int width, int height)
    {
        var drafts = new List<Draft>();
        var notes = new List<string>();
        int skipped = 0;
        void Note(string text) { if (notes.Count < MaximumNotes) notes.Add(text); else if (notes.Count == MaximumNotes) notes.Add("(more notes omitted)"); }
        TestModifiers held = TestModifiers.None;
        int gridW = width, gridH = height;
        // pending mouse press
        (string Button, int X, int Y, bool Moved, long First, bool Double)? press = null;
        bool chord = false, outside = false;
        string? pendingNote = null;
        RecordedInput? previous = null;

        void Add(Draft d) { if (pendingNote is not null) { d.Note = pendingNote; pendingNote = null; } drafts.Add(d); }
        void Modifiers(TestModifiers desired, long seq)
        {
            foreach (var m in new[] { TestModifiers.Alt, TestModifiers.Ctrl, TestModifiers.Shift })
                if ((held & m) != 0 && (desired & m) == 0) { Add(new("modifiers", InputSteps.ModifierName(m, false), seq, seq)); held &= ~m; }
            foreach (var m in new[] { TestModifiers.Shift, TestModifiers.Ctrl, TestModifiers.Alt })
                if ((held & m) == 0 && (desired & m) != 0) { Add(new("modifiers", InputSteps.ModifierName(m, true), seq, seq)); held |= m; }
        }

        foreach (var e in recorded)
        {
            if (previous is { } p && e.Milliseconds - p.Milliseconds > LongPauseMilliseconds && e.Kind != "resize")
                Note($"{(e.Milliseconds - p.Milliseconds) / 1000.0:0.0} s pause before step {drafts.Count + 1}: if the application works asynchronously "
                    + "there, add a checkpoint with retry before it (replay does not wait for wall-clock time).");
            previous = e;
            if (e.AfterGap) pendingNote = "recorded after a pause; input made while paused was not recorded";
            switch (e.Kind)
            {
                case "resize":
                    if (e.Width == gridW && e.Height == gridH) break;
                    Modifiers(TestModifiers.None, e.Sequence);
                    gridW = e.Width; gridH = e.Height;
                    if (drafts.LastOrDefault() is { Kind: "resize" } last) { last.W = gridW; last.H = gridH; last.Last = e.Sequence; }
                    else Add(new("resize", "", e.Sequence, e.Sequence) { W = gridW, H = gridH });
                    Note($"The target grid changed to {gridW}x{gridH} (window resized) before step {drafts.Count}; a resize step was recorded. "
                        + "Delete it if the resize was accidental; mouse cells after it refer to the new grid.");
                    break;
                case "key":
                {
                    var mods = Mods(e.ControlKeyState);
                    bool printable = e.Text is { Length: > 0 } text && InputSteps.IsValidText(text) && (mods & (TestModifiers.Ctrl | TestModifiers.Alt)) == 0;
                    if (printable)
                    {
                        Modifiers(TestModifiers.None, e.Sequence);
                        if (drafts.LastOrDefault() is { Kind: "text" } run && run.Name.Length + e.Text!.Length <= 48 && !e.AfterGap)
                        { run.Name += e.Text; run.Last = e.Sequence; }
                        else Add(new("text", e.Text!, e.Sequence, e.Sequence));
                        break;
                    }
                    if (TestKeys.NameOf(e.KeyCode) is not { } name)
                    {
                        skipped++;
                        Note($"A key (code 0x{e.KeyCode:x4}) has no TSharpVision key name and no printable text; it was not converted.");
                        break;
                    }
                    bool encoded = name.StartsWith("kbAlt", StringComparison.Ordinal) || name.StartsWith("kbCtrl", StringComparison.Ordinal)
                        || name.StartsWith("kbShift", StringComparison.Ordinal);
                    bool wrap = !encoded && mods != TestModifiers.None
                        && SimulatedKeyboard.Compose(e.KeyCode, mods, out _) is { } composed && composed.keyDown.keyCode == e.KeyCode;
                    Modifiers(wrap ? mods : TestModifiers.None, e.Sequence);
                    Add(new("key", name, e.Sequence, e.Sequence));
                    break;
                }
                case "mouse":
                {
                    // Some backends report pointer cells outside the grid (pointer captured beyond the window during a drag).
                    // Replay never clamps, so such a press or notch is not converted.
                    bool off = e.X < 0 || e.Y < 0 || e.X >= e.Width || e.Y >= e.Height;
                    if (off && e.What == Events.evMouseWheel)
                    { skipped++; Note($"A wheel notch at cell {e.X},{e.Y} outside the {e.Width}x{e.Height} grid was not converted."); break; }
                    if (off && e.What is Events.evMouseDown or Events.evMouseUp || off && press is not null) outside = true;
                    if (off && e.What != Events.evMouseUp) break;
                    if (outside && e.What == Events.evMouseUp)
                    {
                        outside = false; press = null; chord = false; skipped++;
                        Note($"A mouse press before step {drafts.Count + 1} reached a cell outside the target grid; it was not converted.");
                        break;
                    }
                    string? button = Button(e.Buttons);
                    switch (e.What)
                    {
                        case Events.evMouseDown:
                            if (press is not null || button is null) { chord = true; break; }
                            Modifiers(Mods(e.ControlKeyState), e.Sequence);
                            press = (button, e.X, e.Y, false, e.Sequence, (e.EventFlags & Events.meDoubleClick) != 0);
                            break;
                        case Events.evMouseMove:
                            if (press is { } held1 && (e.X, e.Y) != (held1.X, held1.Y)) press = held1 with { Moved = true };
                            break;
                        case Events.evMouseUp:
                            if (press is not { } down)
                            {
                                if (chord) { chord = false; skipped++; Note($"A mouse button chord (two buttons held) before step {drafts.Count + 1} cannot be replayed; it was not converted."); }
                                break;
                            }
                            press = null;
                            if (chord) { chord = false; skipped++; Note($"A mouse button chord (two buttons held) before step {drafts.Count + 1} cannot be replayed; it was not converted."); break; }
                            if (!down.Moved && (e.X, e.Y) == (down.X, down.Y))
                            {
                                if (down.Double && drafts.LastOrDefault() is { Kind: "mouse" } prior && prior.Name == "click-" + down.Button && (prior.X, prior.Y) == (down.X, down.Y))
                                { prior.Name = "double-" + down.Button; prior.Last = e.Sequence; }
                                else
                                {
                                    if (drafts.LastOrDefault() is { Kind: "mouse" } single && single.Name == "click-" + down.Button && (single.X, single.Y) == (down.X, down.Y))
                                        Note($"Steps {drafts.Count} and {drafts.Count + 1} are two separate clicks on cell {down.X},{down.Y}; a fast replay may merge them "
                                            + "into a double click (the framework's double-click interval applies). Add a step between them if that matters.");
                                    Add(new("mouse", "click-" + down.Button, down.First, e.Sequence) { X = down.X, Y = down.Y });
                                }
                            }
                            else
                            {
                                Add(new("mouse", "down-" + down.Button, down.First, down.First) { X = down.X, Y = down.Y });
                                Add(new("mouse", "up-" + down.Button, e.Sequence, e.Sequence) { X = e.X, Y = e.Y });
                            }
                            break;
                        case Events.evMouseWheel:
                        {
                            string? direction = (e.EventFlags & Events.meWheelUp) != 0 ? "up" : (e.EventFlags & Events.meWheelDown) != 0 ? "down"
                                : (e.EventFlags & Events.meWheelLeft) != 0 ? "left" : (e.EventFlags & Events.meWheelRight) != 0 ? "right" : null;
                            if (direction is null) { skipped++; break; }
                            Modifiers(Mods(e.ControlKeyState), e.Sequence);
                            if (drafts.LastOrDefault() is { Kind: "mouse" } wheel && wheel.Name == "wheel-" + direction && (wheel.X, wheel.Y) == (e.X, e.Y)
                                && wheel.Count < InputSteps.MaximumWheelCount && !e.AfterGap)
                            { wheel.Count++; wheel.Last = e.Sequence; }
                            else Add(new("mouse", "wheel-" + direction, e.Sequence, e.Sequence) { X = e.X, Y = e.Y });
                            break;
                        }
                    }
                    break;
                }
            }
        }
        if (press is { } open)
        {
            Add(new("mouse", "down-" + open.Button, open.First, open.First) { X = open.X, Y = open.Y });
            Note("The recording ended while a mouse button was held; a release step was added.");
            Add(new("release", "", open.First, open.First));
            held = TestModifiers.None;
        }
        else if (held != TestModifiers.None) Modifiers(TestModifiers.None, recorded.Count == 0 ? 0 : recorded[^1].Sequence);

        var steps = new List<RecordedStep>();
        foreach (var d in drafts)
        {
            try { steps.Add(new(d.Build(), d.First, d.Last)); }
            catch (ArgumentException ex) { skipped++; Note("A recorded step could not be expressed in schema v1 and was left out: " + ex.Message); }
        }
        return new(steps, notes, SelfCheck(steps.Select(s => s.Step).ToArray(), recorded, width, height), skipped);
    }

    private static TestModifiers Mods(uint state) =>
        ((state & Keys.kbShift) != 0 ? TestModifiers.Shift : 0) | ((state & Keys.kbCtrlShift) != 0 ? TestModifiers.Ctrl : 0)
        | ((state & Keys.kbAltShift) != 0 ? TestModifiers.Alt : 0);

    private static string? Button(byte buttons) => buttons switch
    {
        (byte)Events.mbLeftButton => "left", (byte)Events.mbRightButton => "right", (byte)Events.mbMiddleButton => "middle", _ => null,
    };

    /// <summary>
    /// Replays the steps through the same InputPlanner the Proxy uses and compares the key presses, mouse presses/releases and
    /// wheel notches it would deliver with the ones that were recorded. Returns human-readable differences (empty = equal).
    /// </summary>
    internal static IReadOnlyList<string> SelfCheck(IReadOnlyList<ScenarioDocumentStep> steps, IReadOnlyList<RecordedInput> recorded, int width, int height)
    {
        var planned = new List<TEvent>();
        var input = InputSnapshot.Empty;
        foreach (var step in steps)
        {
            if (step.Kind == "resize") { var (w, h) = ScenarioSelection.ParseSize(step.Name); width = w; height = h; continue; }
            string kind = step.Kind switch { "modifiers" => InputSteps.Modifier, "keyDown" => InputSteps.KeyDown, "keyUp" => InputSteps.KeyUp, _ => step.Kind };
            try
            {
                var plan = InputPlanner.Create(kind, step.Name, input, width, height, _ => null);
                planned.AddRange(plan.Events.Select(p => p.Event)); input = plan.After;
            }
            catch (InvalidOperationException ex) { return ["Step '" + step.Parameters + "' cannot be replayed: " + ex.Message]; }
        }
        static bool Compared(ushort what) => what is Events.evKeyDown or Events.evMouseDown or Events.evMouseUp or Events.evMouseWheel;
        // Presses that reached cells outside the grid are reported by the converter and never replayed; leave them out here.
        var expected = recorded.Where(e => Compared(e.What) && !(e.Kind == "mouse" && (e.X < 0 || e.Y < 0 || e.X >= e.Width || e.Y >= e.Height))).ToArray();
        var actual = planned.Where(e => Compared(e.What)).ToArray();
        var problems = new List<string>();
        int count = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < count && problems.Count < 8; i++)
        {
            var r = expected[i]; var a = actual[i];
            string? difference = null;
            if (r.What != a.What) difference = $"event kind {r.What:x} recorded, {a.What:x} replayed";
            else if (r.What == Events.evKeyDown)
            {
                bool text = r.Text is { Length: > 0 } t && InputSteps.IsValidText(t) && (Mods(r.ControlKeyState) & (TestModifiers.Ctrl | TestModifiers.Alt)) == 0;
                if (text) { if (a.keyDown.text != r.Text) difference = $"text '{r.Text}' recorded, '{a.keyDown.text}' replayed"; }
                else if (a.keyDown.keyCode != r.KeyCode) difference = $"key {TestKeys.NameOf(r.KeyCode)} recorded, {TestKeys.NameOf(a.keyDown.keyCode)} replayed";
                else if (TestKeys.NameOf(r.KeyCode) is { } name && !name.StartsWith("kbAlt", StringComparison.Ordinal) && !name.StartsWith("kbCtrl", StringComparison.Ordinal)
                    && !name.StartsWith("kbShift", StringComparison.Ordinal) && Mods(r.ControlKeyState) != Mods(a.keyDown.controlKeyState))
                    difference = $"{name}: modifier state not reproduced";
            }
            else if (r.What is Events.evMouseDown or Events.evMouseUp)
            {
                if ((a.mouse.where.x, a.mouse.where.y) != (r.X, r.Y)) difference = $"mouse at {r.X},{r.Y} recorded, {a.mouse.where.x},{a.mouse.where.y} replayed";
                else if (r.What == Events.evMouseDown && (a.mouse.buttons != r.Buttons || (a.mouse.eventFlags & Events.meDoubleClick) != (r.EventFlags & Events.meDoubleClick)))
                    difference = $"mouse button/double-click at {r.X},{r.Y} differs";
            }
            else if (r.What == Events.evMouseWheel && (a.mouse.where.x, a.mouse.where.y) != (r.X, r.Y)) difference = $"wheel at {r.X},{r.Y} differs";
            if (difference is not null) problems.Add($"Event {i + 1}: {difference}.");
        }
        if (expected.Length != actual.Length && problems.Count < 8)
            problems.Add($"{expected.Length} recorded presses/releases/wheel notches, {actual.Length} replayed (some recorded input was not representable).");
        return problems;
    }

    internal static string Describe(RecordedInput e) => e.Kind switch
    {
        "key" => e.Text is { Length: > 0 } t && InputSteps.IsValidText(t) ? $"key '{t}'" : "key " + (TestKeys.NameOf(e.KeyCode) ?? "0x" + e.KeyCode.ToString("x4", CultureInfo.InvariantCulture)),
        "mouse" => $"mouse {e.What switch { Events.evMouseDown => "down", Events.evMouseUp => "up", Events.evMouseWheel => "wheel", _ => "drag" }} at {e.X},{e.Y}",
        "resize" => $"grid {e.Width}x{e.Height}",
        _ => e.Kind,
    };
}
