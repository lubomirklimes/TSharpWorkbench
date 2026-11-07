using System.IO.Pipes;
using System.Threading.Channels;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

/// <summary>Optional owned-child endpoint. Transport I/O stays off the UI thread; one bounded UI request at a time.</summary>
internal sealed partial class ProxySessionHost : IDisposable
{
    private readonly WorkbenchProxyDriver driver;
    private readonly string instance, secret;
    private readonly CancellationTokenSource lifetime = new();
    private readonly NamedPipeServerStream pipe;
    private readonly Channel<Work> commands = Channel.CreateBounded<Work>(1);
    private readonly int operationMs;
    private volatile bool stop;
    private bool handshaken, started, completed, quitRequested;
    private long sequence;
    private InputSnapshot input = InputSnapshot.Empty;
    private readonly HashSet<string> checkpoints = new(StringComparer.Ordinal);
    private readonly bool record;
    private readonly string? stateDirectory;
    private volatile bool recordingStarted;
    private sealed record Work(Request Request, TaskCompletionSource<Reply> Result, CancellationToken Token);

    internal ProxySessionHost(WorkbenchProxyDriver driver)
    {
        this.driver = driver;
        instance = Environment.GetEnvironmentVariable(TestTargetHost.InstanceVariable) ?? "";
        secret = Environment.GetEnvironmentVariable(TestTargetHost.SecretVariable) ?? "";
        Environment.SetEnvironmentVariable(TestTargetHost.SecretVariable, null);
        if (instance.Length != 32 || !instance.All(Uri.IsHexDigit) || secret.Length != 64 || !secret.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Proxy session credentials missing or invalid.");
        operationMs = Limit(TestTargetHost.OperationLimitVariable, 10000, 100, 30000);
        // A recording session is driven by a person, so its lifetime bound may be longer (still bounded).
        record = Environment.GetEnvironmentVariable(TestTargetHost.RecordVariable) == "1";
        Environment.SetEnvironmentVariable(TestTargetHost.RecordVariable, null);
        lifetime.CancelAfter(TimeSpan.FromSeconds(Limit(TestTargetHost.LifetimeVariable, 40, 5, record ? 3600 : 600)));
        stateDirectory = Environment.GetEnvironmentVariable(TestTargetHost.StateVariable);
        pipe = LocalPipe.Create("tsharp-diagnostics-" + instance);
        _ = Task.Run(ServeAsync);
    }

    private static int Limit(string variable, int fallback, int min, int max) =>
        Environment.GetEnvironmentVariable(variable) is not { } value ? fallback :
        int.TryParse(value, out int result) && result >= min && result <= max ? result :
        throw new InvalidOperationException("Invalid proxy operation/lifetime limit.");

    private Reply Response(Request r, string state, string? reason = null) =>
        new(r.Id, state, Protocol.Schema, instance, reason);

    private async Task ServeAsync()
    {
        try
        {
            await pipe.WaitForConnectionAsync(lifetime.Token);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                Request request = await Protocol.ReadAsync<Request>(pipe, lifetime.Token);
                deadline.CancelAfter(operationMs);
                // One authenticated connection, no reconnection; cap request identities and fail closed on replay.
                if (!Protocol.Authenticate(request, instance, secret) || !seen.Add(request.Id) || seen.Count > 4096)
                {
                    await Protocol.WriteAsync(pipe, Response(request, Protocol.States.Rejected, "Authentication, schema or request identity rejected."), deadline.Token);
                    break;
                }
                // Recorder polling/pause never waits for the UI thread (a busy application must not end the
                // session through the UI deadline); the recorder buffer is thread-safe.
                // Retained-snapshot retrieval is transport-side too (ProxySessionHost.Recording.cs).
                if (request.Operation is Protocol.Operations.RecordPoll or Protocol.Operations.RecordPause or Protocol.Operations.RecordResume
                    or Protocol.Operations.RecordSnapshots or Protocol.Operations.RecordPickArm or Protocol.Operations.RecordPickCancel)
                {
                    await Protocol.WriteAsync(pipe, RespondRecording(request), deadline.Token);
                    continue;
                }
                // Read-only inspection while recording; a UI that misses the boundary deadline is a nonfatal Busy.
                if (request.Operation == Protocol.Operations.RecordInspect)
                {
                    var (inspected, fatal) = await InspectAsync(request);
                    using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    write.CancelAfter(operationMs); // fresh bound: the UI wait may have consumed the request deadline
                    await Protocol.WriteAsync(pipe, inspected, write.Token);
                    if (fatal) break;
                    continue;
                }
                var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
                await commands.Writer.WriteAsync(new(request, completion, deadline.Token), deadline.Token);
                TEventQueue.WakeEventLoop();
                Reply reply;
                try { reply = await completion.Task.WaitAsync(deadline.Token); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    // Cancelled work is skipped if the UI has not claimed it. Never reuse an interrupted session.
                    using var errorLimit = new CancellationTokenSource(500);
                    await Protocol.WriteAsync(pipe, Response(request, Protocol.States.Rejected, Protocol.TimeoutPrefix + "UI boundary deadline expired."), errorLimit.Token);
                    break;
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // Unexpected proxy failure: report it once, end the session, then ordinary quit routing.
                    using var errorLimit = new CancellationTokenSource(500);
                    await Protocol.WriteAsync(pipe, Response(request, Protocol.States.Rejected, "Proxy session failed: " + failure.GetType().Name), errorLimit.Token);
                    break;
                }
                await Protocol.WriteAsync(pipe, reply, deadline.Token);
                if (reply.State == Protocol.States.ShutdownAccepted || reply.Reason?.StartsWith(Protocol.TimeoutPrefix, StringComparison.Ordinal) == true) break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException
            or ObjectDisposedException or InvalidDataException) { /* No credentials or typed values in diagnostics. */ }
        finally
        {
            stop = true;
            commands.Writer.TryComplete();
            TEventQueue.WakeEventLoop();
            pipe.Dispose();
        }
    }

    /// <summary>RecordInspect UI-boundary bound. Never above the operation limit (the controller's per-operation
    /// expectation) and at most 2 s: inspection is a human-paced poll during a recording, a UI busy for longer is reported
    /// promptly as Busy and the controller simply asks again later.</summary>
    private int InspectMs => Math.Min(operationMs, 2000);

    // Transport thread. One request at a time, so this work is the only item the bounded(1) channel can hold.
    // Expiry: cancel (the UI skips cancelled unclaimed work), remove the item if still queued, reply Busy and keep serving.
    // A UI that already claimed the work completes an abandoned TaskCompletionSource: no second reply, nothing escapes.
    private async Task<(Reply Reply, bool Fatal)> InspectAsync(Request request)
    {
        if (!record || !recordingStarted || !driver.Recorder.Active) // fast refusal; the UI pass re-checks authoritatively
            return (Response(request, Protocol.States.Rejected, "Unsupported operation or invalid state: Inspection requires an active recording."), false);
        using var busy = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        busy.CancelAfter(InspectMs);
        var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(request, completion, busy.Token);
        try
        {
            await commands.Writer.WriteAsync(work, busy.Token);
            TEventQueue.WakeEventLoop();
            return (await completion.Task.WaitAsync(busy.Token), false);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            busy.Cancel(); // before removal: a UI read racing with us now sees a cancelled token and skips the work
            if (commands.Reader.TryPeek(out var queued) && ReferenceEquals(queued, work)) commands.Reader.TryRead(out _);
            _ = completion.Task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Completed exactly at expiry: the coherent result wins over Busy.
            return (completion.Task.IsCompletedSuccessfully ? completion.Task.Result
                : Response(request, Protocol.States.Rejected, Protocol.BusyPrefix + "UI boundary not reached in time; the session continues."), false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Unexpected proxy failure: same policy as the other UI operations (report once, end the session).
            return (Response(request, Protocol.States.Rejected, "Proxy session failed: " + failure.GetType().Name), true);
        }
    }

    // UI thread, one synchronous pass: cells, inspection, generation, hash and recorder cursor belong together.
    // Strictly read-only: no input, injection, focus change, PutEvent, redraw, native capture or checkpoint state
    // (CaptureCheckpoint is deliberately not used: it bumps revision/epoch and may resynchronize the shadow).
    private Reply Inspect(TProgram application, Request r)
    {
        var cursor = driver.Recorder.Cursor();
        if (!record || !recordingStarted || !cursor.Active) throw new InvalidOperationException("Inspection requires an active recording.");
        if (!TEventQueue.IsUiThread || !ReferenceEquals(TDisplay.driver, driver))
            throw new InvalidOperationException("Inspection requires the active proxy on the UI thread.");
        if (application.lockFlag != 0 || !ReferenceEquals(application.buffer, TScreen.ScreenBuffer))
            throw new InvalidOperationException("Root drawing must be unlocked and bound to the current screen.");
        var root = ScreenSnapshot.CopyRoot();
        var inspection = Inspectors.Capture(application, root, r.Evidence ?? Protocol.EvidenceKinds.All);
        return Response(r, Protocol.States.Inspected) with
        {
            Inspect = new(driver.ScreenGeneration, ProxyScreenHash.Compute(root), cursor.Sequence, cursor.Delivered, cursor.Milliseconds,
                Convert.ToBase64String(root.Serialize()), root.Width, root.Height, inspection, Environment.CurrentManagedThreadId)
        };
    }

    private InputPlanner.Plan? shutdownConfirmation;

    /// <summary>IEventLoopObserver callback: bounded UI-thread work only, never a wait.</summary>
    /// <remarks>Logical state is read here; a native capture is started here and completed off the UI thread.
    /// Expected operation failures become Rejected replies; anything else ends the session.</remarks>
    internal void OnIdleBoundary(TProgram application)
    {
        if (stop)
        {
            if (quitRequested) return;
            quitRequested = true;
            driver.Recorder.Stop(); // the session ended: no further recording; ordinary quit routing below
            driver.Snapshots.Clear(); // Retained transient snapshots never outlive the session (privacy)
            // Explicit project opt-in: ordinary queued key consumed by the application's quit dialog.
            if (shutdownConfirmation is { } confirmation)
                foreach (var planned in confirmation.Events) driver.Inject(planned.Event);
            // Ordinary application quit routing, including any application veto/confirmation, on the next pass.
            TEvent quit = new() { What = Events.evCommand };
            quit.message.command = Views.cmQuit;
            application.PutEvent(ref quit);
            return;
        }
        if (!commands.Reader.TryRead(out var work) || work.Token.IsCancellationRequested) return;
        _ = Serve(application, work);
    }

    // Runs synchronously on the UI thread up to the first incomplete await; never faults.
    private async Task Serve(TProgram application, Work work)
    {
        try { work.Result.TrySetResult(await Execute(application, work.Request, work.Token).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        { work.Result.TrySetResult(Response(work.Request, Protocol.States.Rejected, Protocol.TimeoutPrefix + "Native/UI capture deadline expired.")); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        { work.Result.TrySetResult(Response(work.Request, Protocol.States.Rejected, "Unsupported operation or invalid state: " + ex.Message)); }
        catch (Exception ex) { work.Result.TrySetException(ex); }
    }

    private Task<Reply> Execute(TProgram application, Request r, CancellationToken token)
    {
        var reply = Respond(application, r, token, out var native);
        return native is null ? Task.FromResult(reply) : AttachNative(reply, native);
    }

    private static async Task<Reply> AttachNative(Reply reply, Task<ProxyNativeCapture> native)
    {
        var capture = await native.ConfigureAwait(false);
        return reply with { Capture = reply.Capture! with { ProxyNative = capture } };
    }

    // UI thread. A requested native capture is only started; its task completes the reply.
    private Reply Respond(TProgram application, Request r, CancellationToken token, out Task<ProxyNativeCapture>? native)
    {
        native = null;
        if (r.Operation == Protocol.Operations.Handshake)
        {
            if (handshaken) throw new InvalidOperationException("Duplicate handshake.");
            handshaken = true;
            return Response(r, Protocol.States.Ready) with
            {
                Target = TestTargetHost.Identity(driver.ActualBackend.Split('.').Last(), TScreen.ScreenWidth, TScreen.ScreenHeight) with { Console = driver.IsConsole ? ProxyConsole.Inspect() : null },
                Capabilities = new("proxy-logical", [ScenarioStep.Key, InputSteps.KeyDown, InputSteps.KeyUp, InputSteps.Text,
                    InputSteps.Modifier, InputSteps.Mouse, InputSteps.Release], Protocol.EvidenceKinds.All,
                    driver.SupportsResize,
                    "accepted-queue", "empty-input UI boundary after Idle; native completion classified separately", driver.CaptureKinds.Length > 0)
                    { CaptureKinds = driver.CaptureKinds, RecordKinds = record ? ProxyInputRecorder.Kinds : [],
                      RecordFeatures = record ? [.. Protocol.RecordFeatures.All, .. Protocol.RecordFeatures.Optional] : [] }
            };
        }
        if (!handshaken) throw new InvalidOperationException("Handshake required.");
        if (driver.Recorder.Active && r.Operation is Protocol.Operations.Input or Protocol.Operations.Checkpoint or Protocol.Operations.Recapture)
        {
            if (r.Operation == Protocol.Operations.Input) driver.Recorder.RefusedInjection();
            throw new InvalidOperationException("A recording session accepts no scripted input or checkpoints; stop the recording first.");
        }
        switch (r.Operation)
        {
            case Protocol.Operations.RecordStart:
                if (!record || !started || completed) throw new InvalidOperationException("Recording requires a started recording session.");
                driver.Snapshots.Reset(); // A new recording starts with an empty transient store
                // Modifier gestures only when this recording opted in; Pick Mode always needs an explicit arm.
                driver.Gestures.Reset();
                driver.Gestures.ModifierGestures = r.RecordOptions?.Contains(Protocol.RecordOptions.ModifierPicks) == true;
                driver.Recorder.Start(TScreen.ScreenWidth, TScreen.ScreenHeight, stateDirectory);
                recordingStarted = true;
                return Response(r, Protocol.States.Recording) with { Recording = PollRecording(0, r.MarksAfter, Snapshot()) };
            case Protocol.Operations.RecordStop:
                if (!recordingStarted) throw new InvalidOperationException("No recording was started.");
                driver.Recorder.Stop();
                driver.Gestures.Reset(); // Disarm; an engaged gesture still swallows until its release
                driver.Snapshots.Freeze(); // No new snapshots; held ones stay fetchable until the session ends
                return Response(r, Protocol.States.Recording) with { Recording = PollRecording(r.After ?? 0, r.MarksAfter, Snapshot()) };
            case Protocol.Operations.RecordInspect: return Inspect(application, r);
            case Protocol.Operations.Start:
                if (started) throw new InvalidOperationException("Already started.");
                started = true;
                return Response(r, Protocol.States.Started);
            case Protocol.Operations.Input:
                if (!started || completed || r.Input is null) throw new InvalidOperationException("Input state.");
                var step = r.Input;
                if (step.Kind == ScenarioStep.Resize)
                {
                    var (w, h) = ScenarioSelection.ParseSize(step.Name);
                    driver.Resize((ushort)w, (ushort)h);
                }
                else
                {
                    var plan = InputPlanner.Create(step.Kind, step.Name, input, TScreen.ScreenWidth, TScreen.ScreenHeight, _ => null);
                    foreach (var ev in plan.Events) driver.Inject(ev.Event);
                    input = plan.After;
                }
                return Response(r, Protocol.States.Routed) with { Interaction = new(step.Kind, step.Name,
                    "WorkbenchProxyDriver -> TEventQueue -> ordinary application event loop", ++sequence,
                    "accepted-queue", "Accepted only; checkpoint uses a subsequent empty-input UI boundary.") };
            case Protocol.Operations.Checkpoint:
            case Protocol.Operations.Recapture:
                if (!started || completed || r.Checkpoint is null) throw new InvalidOperationException("Checkpoint state.");
                if (r.Operation == Protocol.Operations.Checkpoint ? !checkpoints.Add(r.Checkpoint) : !checkpoints.Contains(r.Checkpoint))
                    throw new InvalidOperationException("Checkpoint identity.");
                var capture = driver.CaptureCheckpoint(application);
                var captured = Response(r, Protocol.States.Captured) with
                {
                    Capture = new(r.Checkpoint, Convert.ToBase64String(capture.Root.Serialize()), capture.Root.Width,
                        capture.Root.Height, 1, DateTimeOffset.UtcNow, null,
                        r.Evidence is { Length: > 0 } evidence ? Inspectors.Capture(application, capture.Root, evidence) : null)
                    {
                        Console = driver.IsConsole ? ProxyConsole.Inspect() : null,
                        ConsoleOutputMatchesRoot = driver.IsConsole ? ProxyConsole.Matches(capture.Root) : null,
                        Proxy = new(capture.Revision, driver.Epoch, capture.WritesMatchRoot, capture.Resynchronized,
                            capture.BufferReplaced, Environment.CurrentManagedThreadId, "empty-input UI boundary after Idle",
                            driver.RequestedBackend ?? "automatic", driver.ActualBackend)
                    }
                };
                // Started last, from the same pass and root revision; completes off the UI thread.
                if (r.CaptureKind is not (null or ProxyCaptureKinds.None)) native = driver.BeginCaptureNative(r.CaptureKind, token);
                return captured;
            case Protocol.Operations.Complete:
                if (!started || completed) throw new InvalidOperationException("Completion state.");
                foreach (var ev in InputPlanner.ReleaseEverything(input)) driver.Inject(ev.Event);
                input = InputSnapshot.Empty; completed = true;
                return Response(r, Protocol.States.Completed);
            case Protocol.Operations.Shutdown:
                if (r.Input is { } confirm)
                {
                    if (confirm.Kind != ScenarioStep.Key || confirm.Name != "kbEnter") throw new ArgumentException("Unsupported shutdown confirmation.");
                    shutdownConfirmation = InputPlanner.Create(confirm.Kind, confirm.Name, InputSnapshot.Empty, TScreen.ScreenWidth, TScreen.ScreenHeight, _ => null);
                }
                return Response(r, Protocol.States.ShutdownAccepted);
            default: throw new NotSupportedException("Operation unavailable.");
        }
    }

    // UI thread: the composed logical screen at a recording boundary (evidence for review/round-trip; never a baseline).
    private static string? Snapshot()
    {
        try { return Convert.ToBase64String(ScreenSnapshot.CopyRoot().Serialize()); }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        lifetime.Cancel(); pipe.Dispose();
        driver.Snapshots.Clear(); // Thread-safe; no retained payload outlives the session host
        // The worker owns final completion; no UI-thread join on transport.
    }
}
