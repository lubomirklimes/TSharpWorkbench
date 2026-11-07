using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSharpWorkbench.Testing;

// Protocol v1, extended in place. Schema is a mandatory positional field with no
// default, so a peer built against an earlier contract deserializes it as null or a different identity and is refused.
public sealed record Request(int Version, string Schema, string Instance, string Id, string Secret, string Operation,
    string? Scenario = null, string? Checkpoint = null, InputStep? Input = null, string[]? Evidence = null, string? CaptureKind = null,
    long? After = null, long? MarksAfter = null, string[]? RecordOptions = null);

public sealed record Reply(string Id, string State, string Schema, string? Instance = null, string? Reason = null,
    int ProtocolVersion = Protocol.Version, TargetIdentity? Target = null, ScenarioDescriptor[]? Scenarios = null,
    CheckpointCapture? Capture = null, InteractionRecord? Interaction = null, AutomationCapabilities? Capabilities = null,
    RecordingBatch? Recording = null, RecordInspection? Inspect = null, SnapshotBatch? Snapshots = null);

/// <summary>One input event the target's real backend delivered to the application while a
/// recording was active. <see cref="Origin"/> names where the Proxy observed it: <c>backend-key</c> (returned by the real
/// driver's ReadKeyEvent) or <c>backend-queue</c> (appended to the framework queue during the real driver's
/// PumpMessages/WaitForEvent). Scripted protocol input is refused while recording, so it can never appear here.
/// Kind: <c>key</c>, <c>mouse</c> or <c>resize</c>. Coordinates are target-grid cells; Width/Height the grid at delivery.</summary>
public sealed record RecordedInput(long Sequence, string Origin, long Milliseconds, string Kind, ushort What, ushort KeyCode,
    string? Text, uint ControlKeyState, int X, int Y, byte Buttons, uint EventFlags, int Width, int Height, bool AfterGap = false);

/// <summary>Recorded events after a cursor, with session counters. Snapshot (TVS1, base64) only on RecordStart/RecordStop.</summary>
/// <param name="Unrecorded">Events delivered but deliberately not recorded (paused, key-ups, standalone modifiers, hover moves).</param>
/// <param name="OtherQueue">Queue events that did not come from the backend (application enqueues); forwarded, never recorded.</param>
/// <param name="RefusedInjections">Scripted input attempts refused while recording (expected 0).</param>
public sealed record RecordingBatch(RecordedInput[] Events, long Next, bool Active, bool Paused, long Dropped, long Unrecorded,
    long OtherQueue, long RefusedInjections, int Width, int Height, string Backend, string? Snapshot = null)
{
    /// <summary>Boundaries after the request's <see cref="Request.MarksAfter"/>, at most one batch; empty
    /// when the request carried no MarksAfter (an older controller) or the target has no support.</summary>
    public RecordedBoundary[] Boundaries { get; init; } = [];
    /// <summary>Cursor for the next poll's MarksAfter (the last returned boundary id, else the request's cursor).</summary>
    public long BoundaryNext { get; init; }
    /// <summary>Highest retained-snapshot id the target holds (0 = none); fetch with RecordSnapshots.</summary>
    public long SnapshotLatest { get; init; }
    /// <summary>Loss/coverage counters of this recording; null from an older target.</summary>
    public CaptureCoverage? Coverage { get; init; }
    /// <summary>The target's pick state (<see cref="Protocol.PickStates"/>); null from a target without
    /// <see cref="Protocol.RecordFeatures.Picks"/>.</summary>
    public string? PickState { get; init; }
}

/// <summary>One causal UI boundary observed on the target's UI thread while recording.</summary>
/// <param name="Id">Monotonic per recording (1-based); the order boundaries were observed.</param>
/// <param name="Kind">One of <see cref="Protocol.BoundaryKinds"/>: where on the UI thread it was observed.</param>
/// <param name="Cause">One of <see cref="Protocol.BoundaryCauses"/>: whether delivered input preceded it since the previous boundary.</param>
/// <param name="AfterSequence">The last RECORDED input sequence delivered to the application before this boundary (0 = none);
/// the same numbering as <see cref="RecordedInput.Sequence"/> and converter step ranges. Never a poll timestamp.</param>
/// <param name="Delivered">Count of physical (backend) inputs delivered so far, recorded or not (key-ups, hover, paused ones).</param>
/// <param name="Generation">Monotonic logical-screen generation: increases when a WriteBuf actually changed logical cells.</param>
/// <param name="Hash">Deterministic hash of the composed logical root (<c>ProxyScreenHash</c>) at this boundary.</param>
/// <param name="ModalTop">Type name of the modal top view; null when it is the application itself.</param>
/// <param name="MenuDepth">Executing menu levels (bar = 1 when it executes, plus each open drop-down/popup); 0 = no menu loop.</param>
/// <param name="SnapshotId">Retained snapshot taken at this boundary, if any.</param>
/// <param name="SnapshotDropped">A snapshot was due here but the bounded store refused it (see <see cref="CaptureCoverage"/>).</param>
public sealed record RecordedBoundary(long Id, string Kind, string Cause, long AfterSequence, long Delivered, long Generation,
    ulong Hash, long Milliseconds, int Width, int Height, string? ModalTop, int MenuDepth, bool AfterGap = false,
    long? SnapshotId = null, bool SnapshotDropped = false)
{
    /// <summary>Set only on a boundary of Kind <see cref="Protocol.BoundaryKinds.Pick"/>. Such a boundary
    /// is an instrumentation anchor, not a UI state change: its AfterSequence/Generation/Hash/ModalTop/MenuDepth are the
    /// state at the gesture's press (what the user pointed at); SnapshotId is the pick's frozen snapshot.</summary>
    public RecordedPick? Pick { get; init; }
}

/// <summary>One completed pick gesture, fully swallowed by the target's Proxy (never delivered, never
/// recorded as input, never replayed).</summary>
/// <param name="Kind"><see cref="Protocol.PickKinds"/>: <c>control</c> (press and release on the same cell, within the
/// threshold) or <c>region</c> (a drag).</param>
/// <param name="Trigger"><see cref="Protocol.PickTriggers"/>: Ctrl+Shift+left button or the one-shot Pick Mode.</param>
/// <param name="X">Press cell (target grid).</param>
/// <param name="EndX">Release cell, clipped to the grid.</param>
/// <param name="Region">Normalized inclusive-exclusive cell rectangle (control: 1x1 at the press), clipped to the grid.</param>
/// <param name="AnchorBoundaryId">Last state boundary emitted before the press (0 = none): the state the pick belongs to.</param>
/// <param name="HitPath">Path, in the pick snapshot's view tree, of the view a live front-to-back hit test found at the
/// press (null when none or not computed). A capture-only path: never a selector.</param>
/// <param name="Swallowed">Physical events the gesture consumed (press, moves, auto-repeats, release).</param>
public sealed record RecordedPick(string Kind, string Trigger, int X, int Y, int EndX, int EndY, CellRect Region,
    long AnchorBoundaryId, string? HitPath, int Swallowed);

/// <summary>An immutable full logical snapshot retained in target memory at a boundary of a transient
/// (menu/modal) state, captured in the same UI pass as its boundary. <see cref="Cells"/> is TVS1 base64.</summary>
/// <param name="Reason">One of <see cref="Protocol.SnapshotReasons"/>.</param>
public sealed record RetainedSnapshot(long Id, long BoundaryId, string Reason, long AfterSequence, long Delivered, long Generation,
    ulong Hash, long Milliseconds, string Cells, int Width, int Height, CheckpointInspection? Inspection)
{
    /// <summary>Set when part of the snapshot was left out of the reply (never silently):
    /// <see cref="Protocol.SnapshotOmissions"/>. Cells (at most 200x100 TVS1) are never omitted.</summary>
    public string? Omitted { get; init; }
}

/// <summary>Explicit loss and coverage counters. Coverage is complete only when nothing was dropped,
/// failed or skipped; consumers must never present partial evidence as full coverage.</summary>
public sealed record CaptureCoverage(long Boundaries, long BoundariesDropped, long BoundariesSuppressed, long Snapshots,
    long SnapshotsDropped, long SnapshotsReleased, int SnapshotsRetained, long SnapshotBytesRetained, long CaptureFailures,
    long SkippedUnsafe)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Complete => BoundariesDropped == 0 && SnapshotsDropped == 0 && CaptureFailures == 0 && SkippedUnsafe == 0;
    /// <summary>Boundary-journal lines lost (writer queue full or file error). The journal is only a crash
    /// copy, so this does not affect <see cref="Complete"/>; a consumer that recovers from the journal must honour it.</summary>
    public long JournalDropped { get; init; }
    /// <summary>Picks emitted, gestures aborted (no pick: focus/capture loss, second press, pause/stop,
    /// refused), physical events swallowed by gestures, and double-click flags cleared on a press that followed a swallowed
    /// press. Aborted gestures and refused picks never affect <see cref="Complete"/> (input coverage), but are reported.</summary>
    public long Picks { get; init; }
    public long PicksAborted { get; init; }
    public long GestureEventsSwallowed { get; init; }
    public long DoubleClicksCleared { get; init; }
}

/// <summary>Read-only inspection while recording; cells, metadata and generation from ONE UI pass.</summary>
public sealed record RecordInspection(long Generation, ulong Hash, long AfterSequence, long Delivered, long Milliseconds,
    string Cells, int Width, int Height, CheckpointInspection? Inspection, int UiThread);

/// <summary>Retained snapshots after a cursor, bounded by count and serialized bytes.</summary>
/// <param name="Next">Cursor for the next RecordSnapshots After; snapshots up to it are released by the target.</param>
/// <param name="Latest">Highest retained id the target holds; more remain when Latest &gt; Next.</param>
public sealed record SnapshotBatch(RetainedSnapshot[] Snapshots, long Next, long Latest, CaptureCoverage Coverage);

/// <summary>Append-only JSONL copy of a recording, written by the target's Proxy into the controller-owned session state
/// directory, so events delivered after the last poll survive an application quit or crash. Deleted with the session root.</summary>
public static class RecordingJournal
{
    public const string FileName = "recording.jsonl";

    public static IReadOnlyList<RecordedInput> Read(string path)
    {
        var result = new List<RecordedInput>();
        if (!File.Exists(path)) return result;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try { if (JsonSerializer.Deserialize<RecordedInput>(line) is { } e) result.Add(e); }
            catch (JsonException) { break; } // a torn final line (crash) ends the readable journal
        }
        return result;
    }
}

/// <summary>One line of <see cref="RecordingBoundaryJournal"/>. Type: <c>header</c> (format + payload policy),
/// <c>boundary</c>, <c>loss</c> (Lost lines never written; Detail <c>capture</c> = the recorder's bound refused them,
/// <c>journal</c> = the writer queue was full or the file failed), <c>pause</c>, <c>resume</c>, <c>stop</c> (clean end).</summary>
public sealed record BoundaryJournalLine(string Type, RecordedBoundary? Boundary = null, long Lost = 0, string? Detail = null);

/// <summary>What a boundary journal holds. <see cref="Clean"/> only when a stop line ended it with no loss and no torn line;
/// snapshot payloads are never persisted (privacy), so a boundary's SnapshotId is recoverable only from live retrieval.</summary>
public sealed record BoundaryJournalContent(RecordedBoundary[] Boundaries, long CaptureLost, long JournalLost, bool Stopped, bool Torn,
    string? Format, string? SnapshotPayloads)
{
    public bool Clean => Stopped && !Torn && CaptureLost == 0 && JournalLost == 0;
}

/// <summary>Crash copy of the boundaries of a recording, a SEPARATE file next to <see cref="RecordingJournal"/> so a
/// reader of <c>recording.jsonl</c> (RecordedInput lines) is never confused. Written by a background writer in the target
/// (never on the UI thread), bounded, with explicit loss lines. Deleted with the controller-owned session root.</summary>
public static class RecordingBoundaryJournal
{
    public const string FileName = "recording-boundaries.jsonl", Format = "tsw-recording-boundaries/1", PayloadsNotPersisted = "not-persisted";
    public const string Header = "header", Boundary = "boundary", Loss = "loss", Pause = "pause", Resume = "resume", Stop = "stop";
    public const string LossCapture = "capture", LossJournal = "journal";

    public static BoundaryJournalContent Read(string path)
    {
        var result = new List<RecordedBoundary>();
        long capture = 0, journal = 0; bool stopped = false, torn = false; string? format = null, payloads = null;
        if (!File.Exists(path)) return new([], 0, 0, false, false, null, null);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            BoundaryJournalLine? entry;
            try { entry = JsonSerializer.Deserialize<BoundaryJournalLine>(line); }
            catch (JsonException) { torn = true; break; } // a torn final line (crash) ends the readable journal
            switch (entry?.Type)
            {
                // Format v1 never persists snapshot payloads (only ids inside boundaries): stated explicitly to readers.
                case Header: format = entry.Detail; payloads = format == Format ? PayloadsNotPersisted : null; break;
                case Boundary when entry.Boundary is { } b && (result.Count == 0 || b.Id > result[^1].Id): result.Add(b); break;
                case Loss when entry.Detail == LossCapture: capture += entry.Lost; break;
                case Loss: journal += entry.Lost; break;
                case Stop: stopped = true; break;
            }
        }
        return new(result.ToArray(), capture, journal, stopped, torn, format, payloads);
    }
}

/// <summary>Who answered: the compiled target, its framework and Testing builds, and the actual logical grid.</summary>
public sealed record TargetIdentity(string Assembly, string? AssemblyVersion, string? Mvid, string? FrameworkVersion,
    string? TestingVersion, string Driver, int Width, int Height)
{
    public ConsoleHostEvidence? Console { get; init; }
}

/// <summary>Native console facts from the target, separate from logical checkpoint cells.</summary>
public sealed record ConsoleHostEvidence(string HostMode, int ProcessId, long WindowHandle, uint InputMode, uint OutputMode,
    int BufferWidth, int BufferHeight, int WindowWidth, int WindowHeight, int CursorX, int CursorY, int CursorSize, bool CursorVisible)
{
    public bool WindowVisible { get; init; }
    public int MaximumWidth { get; init; }
    public int MaximumHeight { get; init; }
}

/// <summary>One step of input sent to the target. Kind is <c>key</c> (queued logical input), <c>action</c>
/// (a direct scenario action registered by the application; never presented as user input), <c>resize</c>
/// (Name = <c>WxH</c>; a real framework resize through the NullDriver) or one of the simulated-device kinds
/// <c>key-down</c>, <c>key-up</c>, <c>text</c>, <c>modifier</c>, <c>mouse</c>, <c>release</c> (grammar: <see cref="InputSteps"/>).</summary>
public sealed record InputStep(string Kind, string Name);

/// <summary>How the target routed an <see cref="InputStep"/> and how far its processing was confirmed.</summary>
/// <param name="Sequence">Per-execution submission number (1-based) of host-submitted input.</param>
/// <param name="Acknowledgement">One of <see cref="Protocol.Acknowledgements"/>.</param>
/// <param name="InputState">Simulated-device state after the step (held modifiers, keys, buttons, pointer); null for
/// actions and resizes. Additive in Earlier peers ignore it.</param>
public sealed record InteractionRecord(string Kind, string Name, string Routing, long Sequence = 0,
    string Acknowledgement = "", string? Detail = null, string? InputState = null);

/// <summary>Whole logical screen at a named checkpoint (Width/Height = the grid at that moment). Surface is optional,
/// descriptive metadata; Inspection holds the requested semantic evidence taken in the same UI pass as the cells.</summary>
public sealed record CheckpointCapture(string Checkpoint, string Cells, int Width, int Height, int Observations,
    DateTimeOffset CapturedAt, SurfaceInfo? Surface, CheckpointInspection? Inspection = null)
{
    /// <summary>Optional versioned native evidence. Absent for logical captures.</summary>
    public Dictionary<string, string>? Native { get; init; }
    public ProxyNativeCapture? ProxyNative { get; init; }
    public ProxyCaptureEvidence? Proxy { get; init; }
    public ConsoleHostEvidence? Console { get; init; }
    public bool? ConsoleOutputMatchesRoot { get; init; }
}

/// <summary>The modal top view and focused view when captured. A TWindow is not required.</summary>
public sealed record SurfaceInfo(string TopView, bool TopViewIsWindow, int X, int Y, int Width, int Height,
    string? Focused, int HelpContext);

/// <summary>Logical proxy evidence, never native presentation. Epoch changes when root storage changes.</summary>
public sealed record ProxyCaptureEvidence(long Revision, long Epoch, bool WritesMatchRoot, bool Resynchronized,
    bool BufferReplaced, int UiThread, string Boundary, string RequestedBackend, string ActualBackend);
public sealed record AutomationCapabilities(string Mode, string[] InputKinds, string[] Evidence, bool Resize,
    string InputAcknowledgement, string CheckpointBoundary, bool NativeCapture = false)
{
    public string[] CaptureKinds { get; init; } = [];
    /// <summary>Physical input kinds a recording session can observe; empty = no recording support.</summary>
    public string[] RecordKinds { get; init; } = [];
    /// <summary>Smart Recorder capture features (<see cref="Protocol.RecordFeatures"/>); empty = legacy recording only.</summary>
    public string[] RecordFeatures { get; init; } = [];
}

public static class Protocol
{
    public const int Version = 1;
    /// <summary>Wire-contract identity inside v1. Change it whenever the v1 contract changes incompatibly.</summary>
    public const string Schema = "tsharpvision.testing/v1+checkpoints.2";
    public const int MaximumBytes = 4 * 1024 * 1024;
    /// <summary>Prefix of a rejection reason caused by a bounded wait in the target (the controller reports TimedOut).</summary>
    public const string TimeoutPrefix = "Timeout: ";
    /// <summary>Prefix of a rejection by the target's fixture-only binding boundary (unbound checkpoint, direct execution).</summary>
    public const string BindingPrefix = "Fixture binding: ";

    public static class Operations
    {
        public const string Handshake = "Handshake", Start = "Start", Input = "Input", Checkpoint = "Checkpoint",
            Complete = "Complete", Shutdown = "Shutdown", Recapture = "Recapture";
        /// <summary>Recording operations (additive; earlier proxies reject them as unavailable).</summary>
        public const string RecordStart = "RecordStart", RecordPoll = "RecordPoll", RecordPause = "RecordPause",
            RecordResume = "RecordResume", RecordStop = "RecordStop";
        /// <summary>Read-only inspection while recording (UI boundary, nonfatal busy) and bounded
        /// retained-snapshot retrieval (transport thread). Earlier proxies reject both as unavailable.</summary>
        public const string RecordInspect = "RecordInspect", RecordSnapshots = "RecordSnapshots";
        /// <summary>(additive, transport thread): arm/cancel the one-shot Pick Mode; reply carries a RecordingBatch.</summary>
        public const string RecordPickArm = "RecordPickArm", RecordPickCancel = "RecordPickCancel";
    }

    public static class States
    {
        public const string Ready = "Ready", Started = "Started", Routed = "Routed", Captured = "Captured",
            Completed = "Completed", ShutdownAccepted = "ShutdownAccepted", Rejected = "Rejected";
        public const string Recording = "Recording";
        /// <summary>A RecordInspect reply.</summary>
        public const string Inspected = "Inspected";
    }

    /// <summary>Prefix of a nonfatal rejection (the UI did not reach a boundary in time); the session continues.</summary>
    public const string BusyPrefix = "Busy: ";

    /// <summary>Capability features advertised in <see cref="AutomationCapabilities.RecordFeatures"/>.</summary>
    public static class RecordFeatures
    {
        public const string Inspect = "record-inspect/1", Boundaries = "boundaries/1", Snapshots = "transient-snapshots/1";
        /// <summary>The capture set; <c>CaptureCoverage</c> completeness is defined over exactly these.</summary>
        public static readonly string[] All = [Inspect, Boundaries, Snapshots];
        /// <summary>(additive, optional, never part of <see cref="All"/>): pick gestures (Pick Mode operations, pick
        /// boundaries and snapshots; Ctrl+Shift only with <see cref="RecordOptions.ModifierPicks"/>).</summary>
        public const string Picks = "picks/1";
        public static readonly string[] Optional = [Picks];
    }

    /// <summary>Where a boundary was observed. <c>idle</c>: IEventLoopObserver.OnIdleBoundary (every delivered input
    /// read and its synchronous handling and drawing returned). <c>pre-input</c>: in the proxy's input path right before the
    /// next forwarded input is handed out (the previous input's synchronous handling and drawing returned).</summary>
    public static class BoundaryKinds
    {
        public const string Idle = "idle", PreInput = "pre-input";
        /// <summary>An instrumentation anchor carrying <see cref="RecordedBoundary.Pick"/> (not a UI state change).</summary>
        public const string Pick = "pick";
    }

    /// <summary><c>start</c> = first boundary of a recording; <c>input</c> = physical input was delivered since the
    /// previous boundary; <c>application</c> = the screen changed with no delivered input (timer, posted/queued event).</summary>
    public static class BoundaryCauses
    {
        public const string Start = "start", Input = "input", Application = "application";
        /// <summary>Cause of a pick boundary (swallowed instrumentation input; never counted as Delivered).</summary>
        public const string Pick = "pick";
    }

    /// <summary>Why a snapshot was retained.</summary>
    public static class SnapshotReasons
    {
        public const string MenuOpen = "menu-open", MenuChange = "menu-change", ModalOpen = "modal-open",
            ModalChange = "modal-change", TransientLast = "transient-last";
        /// <summary>The frozen snapshot of a pick, taken at the gesture's press.</summary>
        public const string Pick = "pick";
    }

    /// <summary>(additive): <see cref="RecordedPick.Kind"/>.</summary>
    public static class PickKinds
    {
        public const string Control = "control", Region = "region";
    }

    /// <summary>(additive): <see cref="RecordedPick.Trigger"/>.</summary>
    public static class PickTriggers
    {
        public const string CtrlShift = "ctrl-shift", PickMode = "pick-mode";
    }

    /// <summary>(additive): <see cref="RecordingBatch.PickState"/>. <c>off</c>: no pick pending; <c>armed</c>: Pick Mode
    /// waits for the next left press; <c>capturing</c>: a gesture is being swallowed until its release.</summary>
    public static class PickStates
    {
        public const string Off = "off", Armed = "armed", Capturing = "capturing";
    }

    /// <summary>Per-recording opt-ins sent in <see cref="Request.RecordOptions"/> on RecordStart. A target
    /// ignores names it does not know; an older target ignores the field. Default (absent): no modifier gestures.</summary>
    public static class RecordOptions
    {
        /// <summary>Intercept Ctrl+Shift+left press/drag as a pick (live). Without it only an armed Pick Mode picks.</summary>
        public const string ModifierPicks = "pick-ctrl-shift";
    }

    /// <summary><see cref="RetainedSnapshot.Omitted"/> values. The snapshot would not fit one protocol frame:
    /// <c>view-tree-size</c> = its view tree was left out (menu/status kept); <c>inspection-size</c> = the whole inspection was
    /// left out. Cells and metadata are always kept.</summary>
    public static class SnapshotOmissions
    {
        public const string ViewTreeSize = "view-tree-size", InspectionSize = "inspection-size";
    }

    public static class InputKinds
    {
        public const string Key = "key", Action = "action", Resize = "resize";
        public const string KeyDown = InputSteps.KeyDown, KeyUp = InputSteps.KeyUp, Text = InputSteps.Text,
            Modifier = InputSteps.Modifier, Mouse = InputSteps.Mouse, Release = InputSteps.Release;

        /// <summary>Whether an input step is well formed on the wire. kinds are additive inside the v1 schema: a target
        /// advertises them only in its own catalog, and an earlier controller refuses such a catalog as invalid (fail closed).</summary>
        public static bool IsValid(InputStep step) => step.Kind is Key or Action or Resize
            ? step.Name is { Length: > 0 and <= 48 } : InputSteps.Kinds.Contains(step.Kind) && InputSteps.IsValid(step.Kind, step.Name);
    }

    /// <summary>How far the target confirmed an input. <c>settled</c>: the input was taken by the event loop (a key read
    /// by <c>TScreen.GetEvent</c> from the host's driver queue, an action executed, a resize applied) and afterwards the
    /// loop reached an empty input probe with no pending framework event or posted event. <c>dispatched</c>: taken and
    /// control returned to an event loop, but quiescence was not observed within the operation limit (for example a
    /// continuously posting application). Neither proves application state; checkpoint expectations and assertions do.</summary>
    public static class Acknowledgements
    {
        public const string Settled = "settled", Dispatched = "dispatched";
    }

    /// <summary>Opt-in semantic evidence a checkpoint may request in addition to the always-captured cells.</summary>
    public static class EvidenceKinds
    {
        public const string ViewTree = "view-tree", Menu = "menu", Status = "status";
        public static readonly string[] All = [ViewTree, Menu, Status];
    }

    private static readonly Regex NamePattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Checkpoint and action names: lowercase ASCII words joined by single hyphens, at most 48 characters.
    /// They appear verbatim in flat artifact names, where <c>--</c> separates components.</summary>
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 48 } && NamePattern.IsMatch(name);

    public static bool Authenticate(Request request, string instance, string secret) =>
        request.Version == Version && request.Schema == Schema && request.Instance == instance
        && request.Id is { Length: > 0 and <= 64 } && request.Operation is { Length: > 0 and <= 32 }
        && (request.Scenario is null || request.Scenario.Length <= 128)
        && (request.Checkpoint is null || IsValidName(request.Checkpoint))
        && (request.Input is null || (request.Input.Kind is not null && InputKinds.IsValid(request.Input)))
        && (request.Evidence is null || (request.Evidence.Length <= EvidenceKinds.All.Length && request.Evidence.All(EvidenceKinds.All.Contains)))
        && (request.CaptureKind is null || ProxyCaptureKinds.All.Contains(request.CaptureKind))
        && request.Secret is not null && request.Secret.Length == secret.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(request.Secret), System.Text.Encoding.UTF8.GetBytes(secret));

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
        if (body.Length is < 1 or > MaximumBytes) throw new InvalidDataException("Frame exceeds limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid frame length.");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidDataException("Empty message.");
    }
}

/// <summary>The target answered a read-only request with a nonfatal busy rejection; the session stays usable.</summary>
public sealed class TargetBusyException(string message) : IOException(message);
