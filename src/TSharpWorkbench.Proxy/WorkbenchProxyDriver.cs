using System.Runtime.InteropServices;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

/// <summary>Startup provider, intentionally not a selectable screen driver.</summary>
public sealed class WorkbenchProxyDriverFactory : IDriverDecorator
{
    public IDriver Decorate(IDriver backend, string? requestedBackend) =>
        WorkbenchProxyDriver.Wrap(backend, requestedBackend);
}

/// <summary>Copied logical checkpoint; equality describes observed writes BEFORE any resynchronization.</summary>
public sealed record ProxyCheckpoint(long Revision, ScreenSnapshot Root, ScreenSnapshot Observed,
    bool WritesMatchRoot, bool Resynchronized, bool BufferReplaced);

/// <summary>UI-thread driver decorator. Only Inject is callable by background producers.</summary>
/// <remarks>Owns Shutdown (never also calls backend IDisposable). Native providers must not own the same backend.
/// Outside a recording, real mouse/resize events bypass this interface. No native presentation guarantee.</remarks>
public class WorkbenchProxyDriver : IDriver, IDisposable, IEventLoopObserver
{
    private readonly IDriver backend;
    private readonly object lifecycle = new();
    private int state; // 0 new, 1 initialized, 2 stopped; no reinitialization after failure/shutdown
    private TScreenChar[] shadow = [];
    private int width, height;
    private bool known;
    private bool comparable; // Shadow is a valid baseline for ScreenGeneration (cleared wherever known is)
    private WeakReference<ScreenBuffer>? rootIdentity; // identity only; do not retain retired root storage
    private long revision;
    private ProxySessionHost? session;
    private long epoch;
    public void OnIdleBoundary(TProgram application)
    {
        // Boundary marks first (the session may serve RecordInspect/RecordStop in this pass); a RecordStart served
        // in this pass gets its start boundary from the same pass. Not recording: two volatile reads, nothing else.
        bool recording = Recorder.Active;
        if (recording) Boundaries.OnIdle(application);
        session?.OnIdleBoundary(application);
        if (!recording && Recorder.Active) Boundaries.OnIdle(application);
    }
    internal void Resize(ushort cols, ushort rows)
    {
        if (backend is NullDriver headless) headless.SimulateResize(cols, rows);
        else if (IsConsole) { ProxyConsole.Resize(cols, rows); backend.PumpMessages(); }
        else if (backend is IGridSizeDriver grid) grid.SetGridSize(cols, rows);
        else throw new NotSupportedException("Backend has no grid resize capability.");
    }

    internal bool IsConsole => ActualBackend == "TSharpVision.Drivers.Console.Win32ConsoleDriver";
    internal string[] CaptureKinds => backend is TSharpVision.Drivers.SDL.Gpu.SDLGpuDriver gpu && !gpu.DiagnosticShaderPipelineActive
        ? [ProxyCaptureKinds.Renderer] : ProxyCaptureKinds.ForBackend(ActualBackend.Split('.').Last());
    /// <summary>UI thread: starts a native capture of the frame matching the current root revision.</summary>
    /// <remarks>Only owner-thread work runs here; GPU completion and encoding finish on the returned task.</remarks>
    internal Task<ProxyNativeCapture> BeginCaptureNative(string kind, CancellationToken token)
    {
        lock (lifecycle)
        {
            if (state != 1) throw new InvalidOperationException("Cannot capture an inactive or disposed backend.");
            if (!CaptureKinds.Contains(kind)) throw new NotSupportedException("Native capture kind unsupported by this backend; no fallback.");
            var stamp = new Dictionary<string, string>
            {
                ["status"] = "completed",
                ["rootRevision"] = revision.ToString(),
                ["requestedBackend"] = RequestedBackend ?? "automatic",
                ["actualBackend"] = ActualBackend,
                ["grid"] = $"{TScreen.ScreenWidth}x{TScreen.ScreenHeight}"
            };
            var pending = IsConsole ? Task.FromResult(ProxyConsole.CaptureNativeCells()) : BorrowedNativeCapture.Begin(backend, kind, token);
            return Stamp(pending, stamp);
        }
    }

    private static async Task<ProxyNativeCapture> Stamp(Task<ProxyNativeCapture> pending, Dictionary<string, string> stamp)
    {
        var result = await pending.ConfigureAwait(false);
        foreach (var (key, value) in stamp) result.Details[key] = value;
        return result;
    }
    internal bool SupportsResize => IsConsole || backend is NullDriver or IGridSizeDriver;

    private WorkbenchProxyDriver(IDriver backend, string? requestedBackend)
    {
        this.backend = backend;
        RequestedBackend = requestedBackend;
        ActualBackend = backend.GetType().FullName!;
        Recorder = new ProxyInputRecorder(ActualBackend.Split('.').Last());
        Boundaries = new(this);
    }

    public string? RequestedBackend { get; }
    public string ActualBackend { get; }
    public (int X, int Y)? Caret { get; private set; }
    public ushort? CursorType { get; private set; }

    public static WorkbenchProxyDriver Wrap(IDriver backend, string? requestedBackend = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (backend is WorkbenchProxyDriver) throw new ArgumentException("Backend is already wrapped.", nameof(backend));
        return backend is IIdleWaitDriver waiting
            ? new WaitingProxy(backend, waiting, requestedBackend) : new WorkbenchProxyDriver(backend, requestedBackend);
    }

    private sealed class WaitingProxy(IDriver backend, IIdleWaitDriver waiting, string? requested)
        : WorkbenchProxyDriver(backend, requested), IIdleWaitDriver
    {
        public void WaitForEvent(int timeoutMilliseconds, WaitHandle wakeSignal)
        {
            if (!Forwarding) { waiting.WaitForEvent(timeoutMilliseconds, wakeSignal); return; }
            // Recording only: SDL backends translate native events inside the wait; whatever they queued is backend input.
            if (forwarded.Count != 0) return;
            waiting.WaitForEvent(timeoutMilliseconds, wakeSignal);
            DrainQueue(ProxyInputRecorder.BackendQueue);
        }
    }

    // ── recording seam (Workbench Proxy only; inactive outside a recording session) ───────────────────────
    // While a recording is active the proxy takes the framework queue's events off the queue right after the real backend
    // pumped (each classified exactly once, by TEventQueue.GetNextEvent, as TScreen.GetEvent would have) and hands them to
    // TScreen.GetEvent through ReadKeyEvent, ahead of backend keys - the same order the application would have seen.
    // Nothing is re-enqueued or synthesized. When no recording is active (and the forward FIFO is empty) every member below
    // takes exactly the pre-recording code path.
    private readonly Queue<(TEvent Event, string Origin)> forwarded = new();
    internal ProxyInputRecorder Recorder { get; private set; } = null!;

    // ── capture contract (Workstream B implements; signatures are shared) ──────────────────────────────────────────
    /// <summary>UI thread: monotonic logical-screen generation; increases when a WriteBuf actually changed logical cells.</summary>
    internal long ScreenGeneration { get; private set; }
    /// <summary>Bounded transient-snapshot store of the active recording (read by the transport thread).</summary>
    internal ProxyRetainedSnapshots Snapshots { get; } = new();
    // Created eagerly (constructor): the transport thread reads its counters, so it must never be created lazily.
    internal ProxyBoundaryTracker Boundaries { get; }
    /// <summary>Pick gestures (Ctrl+Shift+left when the recording opted in, one-shot Pick Mode when armed).</summary>
    internal ProxyPickGestures Gestures { get; } = new();
    // An engaged gesture keeps the forward path alive after Stop, so its release is still swallowed.
    private bool Forwarding => forwarded.Count != 0 || Recorder.Active || Gestures.Engaged;

    private void DrainQueue(string origin)
    {
        // Bounded: TEventQueue never synthesizes events when empty, so this ends with the queue; the cap guards a producer
        // thread that floods the queue concurrently.
        for (int i = 0; i < 4096; i++)
        {
            TEvent ev = default;
            TEventQueue.GetNextEvent(ref ev);
            if (ev.What == Events.evNothing) return;
            forwarded.Enqueue((ev, origin));
        }
    }

    public void Initialize()
    {
        lock (lifecycle)
        {
            if (state != 0) throw new InvalidOperationException("Proxy initialization is single-use.");
            try
            {
                bool activated = Environment.GetEnvironmentVariable("TSHARPVISION_PROXY_SESSION") == "1";
                if (activated && backend is IGridSizeDriver sized
                    && Environment.GetEnvironmentVariable(TestTargetHost.SizeVariable) is { } requested)
                {
                    var (w, h) = ScenarioSelection.ParseSize(requested);
                    sized.SetGridSize((ushort)w, (ushort)h);
                }
                if (activated && IsConsole)
                {
                    var (cw,ch) = ScenarioSelection.ParseSize(Environment.GetEnvironmentVariable(TestTargetHost.SizeVariable)!);
                    ProxyConsole.Resize(cw,ch);
                }
                backend.Initialize(); state = 1;
                if (activated && IsConsole)
                {
                    var native = ProxyConsole.Inspect();
                    if ((native.InputMode & 0x18) != 0x18 || (native.InputMode & 6) != 0
                        || native.WindowWidth != backend.GetCols() || native.WindowHeight != backend.GetRows())
                        throw new InvalidOperationException("Real Console driver did not activate expected modes/geometry.");
                }
                if (Environment.GetEnvironmentVariable("TSHARPVISION_PROXY_SESSION") == "1")
                {
                    if (backend is NullDriver && Environment.GetEnvironmentVariable(TestTargetHost.SizeVariable) is { } grid)
                    {
                        var (w, h) = ScenarioSelection.ParseSize(grid);
                        Resize((ushort)w, (ushort)h);
                    }
                    session = new ProxySessionHost(this);
                    Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER_DECORATOR", null);
                    Environment.SetEnvironmentVariable("TSHARPVISION_PROXY_SESSION", null);
                }
            }
            catch (Exception startup)
            {
                state = 2;
                if (Environment.GetEnvironmentVariable("TSHARPVISION_PROXY_SESSION") == "1"
                    && Environment.GetEnvironmentVariable(TestTargetHost.StateVariable) is { } diagnosticState)
                {
                    try { File.WriteAllText(Path.Combine(diagnosticState, "proxy-startup-error.txt"),
                        startup.GetType().Name + ": " + startup.Message); }
                    catch (IOException) { }
                }
                try { backend.Shutdown(); }
                catch (Exception cleanup) { throw new AggregateException("Backend startup and cleanup failed.", startup, cleanup); }
                throw;
            }
        }
    }

    public void Shutdown()
    {
        lock (lifecycle)
        {
            if (state == 2) return;
            state = 2; // one attempt even when backend shutdown throws
            Snapshots.Clear(); // Retained recording payloads never outlive the proxy
            session?.Dispose();
            backend.Shutdown();
        }
    }
    public void Dispose() => Shutdown();
    public void Suspend() => backend.Suspend();
    public void Resume() { known = comparable = false; backend.Resume(); }
    public ushort GetCols() => backend.GetCols();
    public ushort GetRows() => backend.GetRows();
    public TDisplay.SM GetScreenMode() => backend.GetScreenMode();
    public void SetScreenMode(TDisplay.SM mode) { known = comparable = false; backend.SetScreenMode(mode); }
    public ScreenBuffer AllocateScreenBuffer()
    {
        var buffer = backend.AllocateScreenBuffer();
        Reset((int)buffer.Width, (int)buffer.Height);
        // Allocation is logical storage, not proof of what a real display contains.
        return buffer;
    }
    public void ClearScreen(ushort cols, ushort rows)
    {
        known = comparable = false;
        backend.ClearScreen(cols, rows); // IDriver does not specify clear-cell attributes or backing-buffer mutation.
    }
    public ushort GetCursorType() => backend.GetCursorType();
    public void SetCursorType(ushort cursorType) { backend.SetCursorType(cursorType); CursorType = cursorType; }
    public void SetCaretPosition(int x, int y) { backend.SetCaretPosition(x, y); Caret = (x, y); }
    public void MakeBeep() => backend.MakeBeep();
    public void PumpMessages()
    {
        if (!Forwarding) { backend.PumpMessages(); return; }
        DrainQueue(ProxyInputRecorder.OtherQueue);   // already queued before the backend ran: not physical input
        backend.PumpMessages();
        DrainQueue(ProxyInputRecorder.BackendQueue); // appended by the real backend's translation of native events
    }

    public bool ReadKeyEvent(out TEvent ev)
    {
        while (forwarded.Count != 0)
        {
            if (Recorder.Active) Boundaries.BeforeInput(); // Pre-input boundary: the input is not handed out yet
            (ev, string origin) = forwarded.Dequeue();
            // A swallowed gesture event never reaches the application or the recorder; take the next one.
            // Every origin passes the filter: a release that lands in the other queue must not reach the application either.
            if (Picked(ref ev)) continue;
            Recorder.Deliver(ev, origin, TScreen.ScreenWidth, TScreen.ScreenHeight);
            return true;
        }
        if (!backend.ReadKeyEvent(out ev)) return false;
        if (Recorder.Active || Gestures.Engaged)
        {
            if (Recorder.Active) Boundaries.BeforeInput(); // read from the backend but not yet handed to the application (console keyboard)
            if (Picked(ref ev)) { ev = default; return false; }
            Recorder.Deliver(ev, ProxyInputRecorder.BackendKey, TScreen.ScreenWidth, TScreen.ScreenHeight);
        }
        return true;
    }

    // Runs one about-to-be-delivered event through the gesture engine. True = swallowed. The press that
    // begins a gesture freezes the state now (after the pre-input boundary above, before anything else runs).
    private bool Picked(ref TEvent ev)
    {
        var cursor = Recorder.Cursor();
        var decision = Gestures.Filter(ref ev, TScreen.ScreenWidth, TScreen.ScreenHeight, cursor.Active && !cursor.Paused);
        if (decision.Aborted) Boundaries.AbortPick();
        if (decision.Began) Boundaries.BeginPick(ev.mouse.where.x, ev.mouse.where.y);
        if (decision.Completed is { } outcome) Boundaries.CompletePick(outcome);
        return decision.Action == GestureAction.Swallow;
    }
    public bool SupportsMouse => backend.SupportsMouse;
    public bool SupportsTrueColor => backend.SupportsTrueColor;
    public bool SupportsGraphics => backend.SupportsGraphics;
    public KeyboardCapabilities KeyboardCapabilities => backend.KeyboardCapabilities;

    public void WriteBuf(int x, int y, int w, int h, Span<TScreenChar> buf)
    {
        // Copy before delegation: backend may alter a borrowed span during the call.
        var copy = buf.ToArray();
        int cols, rows;
        try
        {
            backend.WriteBuf(x, y, w, h, buf);
            cols = backend.GetCols(); rows = backend.GetRows();
        }
        catch { known = comparable = false; ScreenGeneration++; throw; } // a backend may have partially written before failure
        if (width != cols || height != rows) Reset(cols, rows);
        if (w < 0 || h < 0 || (long)w * h > copy.Length) { known = comparable = false; ScreenGeneration++; return; }
        // While recording, generation advances only when the clipped cells differ bitwise from the shadow (or there
        // is no valid baseline). Not recording: no compare; the baseline is invalidated so the first recorded write counts.
        bool track = Recorder.Active, changed = !comparable || !track;
        // Clip destination while retaining the original source row stride.
        for (long row = Math.Max(0L, -(long)y); row < h && (long)y + row < height; row++)
        {
            int left = (int)Math.Min(w, Math.Max(0L, -(long)x)), right = (int)Math.Min(w, (long)width - x);
            if (left >= right) continue;
            var source = copy.AsSpan(checked((int)(row * w + left)), right - left);
            var target = shadow.AsSpan(checked((int)((y + row) * width + x + left)), right - left);
            if (!changed && !MemoryMarshal.AsBytes(source).SequenceEqual(MemoryMarshal.AsBytes(target))) changed = true;
            source.CopyTo(target);
        }
        comparable = track;
        if (changed) ScreenGeneration++;
    }

    /// <summary>Queues a copied logical input through the existing FIFO and its race-safe framework wake.</summary>
    /// <remarks>Injected key/mouse/modifier events preserve their fields; mouse click classification remains in TEventQueue.
    /// FIFO ordering applies to queue submissions, not to a total ordering with native backend keyboard queues.</remarks>
    public void Inject(TEvent ev)
    {
        if (ev.What is not (Events.evKeyDown or Events.evKeyUp or Events.evModifierChanged
            or Events.evMouseDown or Events.evMouseUp or Events.evMouseMove or Events.evMouseWheel))
            throw new NotSupportedException("Only logical keyboard, modifier and basic mouse events can be injected.");
        lock (lifecycle)
        {
            if (state != 1) throw new InvalidOperationException("Input requires an initialized, running proxy.");
            if (Recorder.Active) { Recorder.RefusedInjection(); throw new InvalidOperationException("A recording session accepts no scripted input."); }
            TEventQueue.Enqueue(ev);
        }
    }

    /// <summary>UI-thread synchronization seam, called after synchronous drawing has returned and root locks are released.</summary>
    /// <remarks>Copies the root oracle and compares all cells before optional recovery. It proves a coherent logical
    /// checkpoint only; callers must use their existing readiness/stability policy for asynchronous application work.
    /// No event is raised per WriteBuf and no renderer/GPU completion is implied.</remarks>
    public ProxyCheckpoint CaptureCheckpoint(TProgram application)
    {
        if (state != 1 || !TEventQueue.IsUiThread || !ReferenceEquals(TDisplay.driver, this))
            throw new InvalidOperationException("Checkpoint requires the active proxy on the UI thread.");
        if (application.lockFlag != 0 || !ReferenceEquals(application.buffer, TScreen.ScreenBuffer))
            throw new InvalidOperationException("Root drawing must be unlocked and bound to the current screen.");
        var buffer = TScreen.ScreenBuffer ?? throw new InvalidOperationException("No root buffer.");
        var root = ScreenSnapshot.CopyRoot();
        bool replaced = rootIdentity is null || !rootIdentity.TryGetTarget(out var priorRoot) || !ReferenceEquals(priorRoot, buffer);
        var observed = new ScreenSnapshot(width == 0 ? root.Width : width, height == 0 ? root.Height : height,
            shadow.Length == 0 ? new TScreenChar[root.Width * root.Height] : shadow);
        bool equal = known && width == root.Width && height == root.Height
            && shadow.AsSpan().SequenceEqual(buffer.Data);
        bool sync = !equal || replaced;
        if (sync)
        {
            width = root.Width; height = root.Height;
            shadow = buffer.Data.ToArray(); known = true; rootIdentity = new(buffer);
        }
        if (replaced) epoch++;
        return new(++revision, root, observed, equal, sync, replaced);
    }

    internal long Epoch => epoch;

    private void Reset(int cols, int rows)
    {
        width = cols; height = rows;
        shadow = new TScreenChar[checked(cols * rows)];
        known = comparable = false;
    }
}
