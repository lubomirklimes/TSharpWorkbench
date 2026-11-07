using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Interaction recorder.
// - Converter tests: pure, always run.
// - Window tests: the real WorkbenchApp on NullDriver; logical screenshots when TSWB_PRODUCT_EVIDENCE is set (NOT native).
// - Physical-path tests: need TSWB_PRODUCT_TARGET = a ProxyOrdinary deployment that carries THIS build's Workbench Proxy/Testing.
//   Console input is written into the target's own console input buffer by a SEPARATE helper process (AttachConsole +
//   WriteConsoleInputW): the OS console path the real Win32ConsoleDriver reads. SDL input is posted as window messages to the
//   target's SDL window (needs TSW_PROXY_SDL_TEST=1). Workbench never sends protocol Input during a recording.
[Collection("NonParallel")]
public sealed class RecorderTests
{
    private static string? Target => Environment.GetEnvironmentVariable("TSWB_PRODUCT_TARGET") is { Length: > 0 } t && File.Exists(t) ? t : null;
    private static string? Evidence => Environment.GetEnvironmentVariable("TSWB_PRODUCT_EVIDENCE") is { Length: > 0 } e ? e : null;
    private const string NeedsTarget = "Set TSWB_PRODUCT_TARGET to a ProxyOrdinary.exe deployment carrying this build's TSharpWorkbench.Proxy/Testing.";

    // ── converter ───────────────────────────────────────────────────────────────────────────────────────────

    private static long seq;
    private static RecordedInput Key(ushort code, string? text = null, uint state = 0, long ms = 0, bool gap = false) =>
        new(++seq, "backend-key", ms, "key", Events.evKeyDown, code, text, state, 0, 0, 0, 0, 80, 25, gap);
    private static RecordedInput Char(char c, uint state = 0) => Key((ushort)(0x1e00 | c), c.ToString(), state);
    private static RecordedInput Mouse(ushort what, int x, int y, byte buttons = 0, uint flags = 0, long ms = 0) =>
        new(++seq, "backend-queue", ms, "mouse", what, 0, null, 0, x, y, buttons, flags, 80, 25);
    private static RecordedInput Grid(int w, int h) => new(++seq, "backend-queue", 0, "resize", Events.evCommand, 0, null, 0, 0, 0, 0, 0, w, h);
    private static string[] Json(RecordingConversion c) => c.Steps.Select(s => s.Step.ToJson().ToJsonString()).ToArray();

    [Fact]
    public void KeysAndPrintableTextBecomeKeyStepsAndTextRuns()
    {
        var events = new List<RecordedInput> { Char('h'), Char('i'), Key(Keys.kbEnter, "\r"), Key(Keys.kbF2), Key(Keys.kbAltX, null, Keys.kbAltShift) };
        events.AddRange(Enumerable.Range(0, 50).Select(i => Char((char)('a' + i % 26))));
        var c = RecordingConverter.Convert(events, 80, 25);
        Assert.Equal("""{"kind":"text","text":"hi"}""", Json(c)[0]);
        Assert.Equal("""{"kind":"key","key":"kbEnter"}""", Json(c)[1]);
        Assert.Equal("""{"kind":"key","key":"kbF2"}""", Json(c)[2]);
        Assert.Equal("""{"kind":"key","key":"kbAltX"}""", Json(c)[3]);
        Assert.Equal(48, c.Steps[4].Step.Name.Length); Assert.Equal(2, c.Steps[5].Step.Name.Length);
        Assert.Empty(c.Mismatches);
    }

    [Fact]
    public void ShiftedNavigationKeysAreWrappedInModifierStepsSoReplayCarriesTheState()
    {
        var c = RecordingConverter.Convert([Key(Keys.kbUp, null, Keys.kbShift), Key(Keys.kbUp, null, Keys.kbShift), Key(Keys.kbDown)], 80, 25);
        Assert.Equal(["modifiers shift-down", "key kbUp", "key kbUp", "modifiers shift-up", "key kbDown"], c.Steps.Select(s => s.Step.Kind + " " + s.Step.Name));
        Assert.Empty(c.Mismatches);
    }

    [Fact]
    public void MousePressesBecomeClickDoubleDragAndCoalescedWheelSteps()
    {
        var c = RecordingConverter.Convert([
            Mouse(Events.evMouseDown, 5, 3, 1), Mouse(Events.evMouseUp, 5, 3),
            Mouse(Events.evMouseDown, 9, 4, 1), Mouse(Events.evMouseUp, 9, 4), Mouse(Events.evMouseDown, 9, 4, 1, Events.meDoubleClick), Mouse(Events.evMouseUp, 9, 4),
            Mouse(Events.evMouseDown, 2, 2, 2), Mouse(Events.evMouseMove, 6, 2, 2), Mouse(Events.evMouseMove, 12, 7, 2), Mouse(Events.evMouseUp, 12, 7),
            Mouse(Events.evMouseWheel, 1, 1, 0, Events.meWheelDown), Mouse(Events.evMouseWheel, 1, 1, 0, Events.meWheelDown), Mouse(Events.evMouseWheel, 1, 1, 0, Events.meWheelDown),
        ], 80, 25);
        Assert.Equal(["click-left@5,3", "double-left@9,4", "down-right@2,2", "up-right@12,7", "wheel-down@1,1*3"], c.Steps.Select(s => s.Step.Name));
        Assert.Empty(c.Mismatches);
    }

    [Fact]
    public void ChordsAreRefusedHeldButtonsReleasedAndRepeatedClicksFlagged()
    {
        var c = RecordingConverter.Convert([
            Mouse(Events.evMouseDown, 1, 1, 1), Mouse(Events.evMouseDown, 1, 1, 3), Mouse(Events.evMouseUp, 1, 1),
            Mouse(Events.evMouseDown, 4, 4, 1), Mouse(Events.evMouseUp, 4, 4), Mouse(Events.evMouseDown, 4, 4, 1), Mouse(Events.evMouseUp, 4, 4),
            Mouse(Events.evMouseDown, 7, 7, 1),
        ], 80, 25);
        Assert.Equal(["click-left@4,4", "click-left@4,4", "down-left@7,7", "all"], c.Steps.Select(s => s.Step.Name));
        Assert.Equal(1, c.Skipped);
        Assert.Contains(c.Notes, n => n.Contains("chord", StringComparison.Ordinal));
        Assert.Contains(c.Notes, n => n.Contains("double click", StringComparison.Ordinal));
        Assert.Contains(c.Notes, n => n.Contains("release step", StringComparison.Ordinal));
    }

    [Fact]
    public void PressesReachingCellsOutsideTheGridAreNotConverted()
    {
        // Observed on SDLDriver: a pointer captured outside the window reports cells beyond the grid (e.g. 104,30 on 80x25).
        var c = RecordingConverter.Convert([
            Mouse(Events.evMouseDown, 79, 24, 1), Mouse(Events.evMouseMove, 104, 30, 1), Mouse(Events.evMouseUp, 104, 30),
            Mouse(Events.evMouseWheel, 90, 3, 0, Events.meWheelUp), Mouse(Events.evMouseDown, 3, 3, 1), Mouse(Events.evMouseUp, 3, 3),
        ], 80, 25);
        Assert.Equal(["click-left@3,3"], c.Steps.Select(s => s.Step.Name));
        Assert.Equal(2, c.Skipped);
        Assert.NotEmpty(c.Mismatches); // the self-check reports that recorded input was left out; it is never hidden
        Assert.Contains(c.Notes, n => n.Contains("outside the target grid", StringComparison.Ordinal));
    }

    [Fact]
    public void ResizePauseGapAndLongPausesAreNotesNeverTimingSteps()
    {
        var c = RecordingConverter.Convert([Key(Keys.kbF2, ms: 100), Grid(100, 30), Grid(110, 32), Key(Keys.kbF3, ms: 5000, gap: true), Key(0xfffe, ms: 5100)], 80, 25);
        Assert.Equal(["""{"kind":"key","key":"kbF2"}""", """{"kind":"resize","width":110,"height":32}""",
            """{"kind":"key","key":"kbF3","note":"recorded after a pause; input made while paused was not recorded"}"""], Json(c));
        Assert.Contains(c.Notes, n => n.Contains("pause before step", StringComparison.Ordinal));
        Assert.Contains(c.Notes, n => n.Contains("110x32", StringComparison.Ordinal));
        Assert.Equal(1, c.Skipped); // an unnamed key without text is never invented
        Assert.DoesNotContain(c.Steps, s => s.Step.Kind is "checkpoint" or "action");
    }

    [Fact]
    public void ConvertedStepsAreValidSchemaV1ProxyDocuments()
    {
        var c = RecordingConverter.Convert([Char('x'), Key(Keys.kbF10), Mouse(Events.evMouseDown, 3, 0, 1), Mouse(Events.evMouseUp, 3, 0),
            Key(Keys.kbLeft, null, Keys.kbCtrlShift)], 80, 25);
        var doc = ScenarioDocument.New("rec/valid", "Recorded", "Recorder output validation.", checkpoint: null);
        doc.Append(c.DocumentSteps);
        doc.Append(ScenarioDocumentStep.Checkpoint("after"));
        Assert.DoesNotContain(doc.Validate(), p => p.Severity == ScenarioProblemSeverity.Error);
        var loaded = ExternalScenarios.LoadFromBytes(Path.Combine(Path.GetTempPath(), "memory.scenario.json"), doc.Serialize());
        Assert.Equal("proxy-logical", loaded.Scenario.Backend);
        Assert.Equal(c.Steps.Count + 1, loaded.Scenario.Steps.Length);
    }

    // ── window (logical captures) ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(140, 40)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public async Task EditorAndRecordingWindowStatesCapture(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60); // the startup catalog load must finish first
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                // BEFORE: the editor as Agent B ships it (no recorder installed: no Record button).
                WorkbenchScenarioEditorWindow.RecordRequested = null;
                var before = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                await Shot(bridge, "before", "editor-without-recorder");
                await bridge.Invoke(() => before.Close()).WaitAsync(TimeSpan.FromSeconds(20));
                // AFTER: recorder installed -> Rec~o~rd... in the editor's step row; the recording window states.
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                await Shot(bridge, "after", "editor-with-record-button");
                await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                var window = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                Assert.Contains("Ready. Choose the target window kind", await Text(bridge, window));
                await Shot(bridge, "after", "recording-ready");
                var events = new[] { Char('o'), Char('k'), Key(Keys.kbF2), Mouse(Events.evMouseDown, 20, 9, 1), Mouse(Events.evMouseUp, 20, 9) };
                await bridge.Invoke(() => window.Attach(RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Recording, events)));
                Assert.Contains("RECORDING", await Text(bridge, window));
                await Shot(bridge, "after", "recording-live");
                await bridge.Invoke(() => window.Attach(RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Stopped,
                    [.. events, Key(Keys.kbF3, ms: 4000), Mouse(Events.evMouseDown, 20, 9, 1, 0, 4100), Mouse(Events.evMouseUp, 20, 9), Mouse(Events.evMouseDown, 20, 9, 1), Mouse(Events.evMouseUp, 20, 9)])));
                string review = await Text(bridge, window);
                Assert.Contains("click-left@20,9", review);
                Assert.Contains("Stopped", review);
                await Shot(bridge, "after", "recording-review");
                // Apply: Append puts the steps into the editor (never saved) and closes the recording window.
                int before2 = await Invoke(bridge, () => editor.Document.Steps.Count);
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.AppendCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Equal(before2 + 6, await Invoke(bridge, () => editor.Document.Steps.Count));
                Assert.True(await Invoke(bridge, () => editor.Document.Dirty));
                await Shot(bridge, "after", "editor-after-append");
                await bridge.Invoke(() => { var saved = editor.Document.Save(); Assert.True(saved.Saved, saved.Message); });
            });
        }
        finally { WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root); }
    }

    [Fact]
    public async Task RecordIsRefusedForDiagnosticDocumentsAndMissingTargets()
    {
        var context = new ScenarioEditorContext(null, false, "fixture/x", false, TestWorkbenchHostTests.SampleTarget, [], new Dictionary<string, string>(), null,
            "NullDriver", ["80x25"], null, 0, 0, 0, null);
        Assert.Contains("ordinary-application", WorkbenchRecorder.Unavailable(context));
        Assert.Contains("does not exist", WorkbenchRecorder.Unavailable(context with { Proxy = true, TargetPath = Path.Combine(Temp(), "missing.exe") }));
        Assert.Null(WorkbenchRecorder.Unavailable(context with { Proxy = true }));
        Assert.Equal("100x30", WorkbenchRecorder.Grid(context with { DocumentGridDefaults = ["100x30"] }));
        Assert.Equal(0, WorkbenchRecorder.DefaultBackend(context));
        Assert.Throws<ArgumentException>(() => new RecordingSession(TestWorkbenchHostTests.SampleTarget, "NullDriver", "80x25"));
        await Task.CompletedTask;
    }

    // ── physical path: Win32ConsoleDriver ───────────────────────────────────────────────────────────────────────

    [RecorderTargetFact]
    public async Task ConsoleRecordingFromTheConsoleInputBufferRoundTripsOnConsoleAndNullDriver()
    {
        string target = Target!;
        await using var session = new RecordingSession(target, "Win32ConsoleDriver", "80x25");
        await session.StartAsync();
        Assert.True(session.State == RecordingState.Recording, session.Failure);
        Assert.True(session.WindowVisible, "The owned console window must be visible for a person to type into it.");
        int pid = session.ProcessId!.Value;
        // F2, F2, F3 (opens the ordinary modal), a real pointer move then a left click on its OK button, F2 again.
        Inject(pid, "k,113,60;k,113,60;k,114,61;m,22,8;c,20,9;k,113,60");
        await UntilAsync(() => session.Events.Count(e => e.What is Events.evKeyDown or Events.evMouseDown or Events.evMouseUp) >= 6, 30);
        await session.StopAsync();
        Assert.Equal(RecordingState.Stopped, session.State);
        var events = session.Events;
        var batch = session.Last!;
        Assert.All(events, e => Assert.StartsWith("backend-", e.Origin));
        Assert.Equal(0, batch.RefusedInjections);
        Assert.True(batch.Unrecorded > 0, "Key releases/hover moves of the real driver are delivered but not recorded.");
        AssertCleanedUp(session);
        Assert.Contains("Count: 3", Screen(session.EndScreen!));
        var conversion = RecordingConverter.Convert(events, session.Width, session.Height);
        Assert.Equal(["kbF2", "kbF2", "kbF3", "click-left@20,9", "kbF2"], conversion.Steps.Select(s => s.Step.Name));
        Assert.Empty(conversion.Mismatches);
        var replays = new Dictionary<string, object>();
        foreach (string backend in new[] { "Win32ConsoleDriver", "NullDriver" })
            replays[backend] = await ReplayAsync(target, conversion, backend, session.EndScreen!);
        WriteEvidence("console-roundtrip", session, conversion, replays);
    }

    // The real console target opens and closes its ordinary modal between Workbench polls; the open-state
    // snapshot retained on the target UI thread is fetched afterwards, and boundaries line up with delivered inputs/steps.
    [RecorderTargetFact]
    public async Task ConsoleRecordingRetainsTheModalOpenStateAndCausalBoundaries()
    {
        await using var session = new RecordingSession(Target!, "Win32ConsoleDriver", "80x25");
        await session.StartAsync();
        Assert.True(session.State == RecordingState.Recording, session.Failure);
        Assert.Equal(Protocol.RecordFeatures.All, session.Capture.Features);
        Assert.Null(session.CaptureDiagnostic);
        var inspected = await session.InspectAsync();
        Assert.NotNull(inspected);
        Assert.Contains("Count: 0", Screen(ScreenSnapshot.Deserialize(Convert.FromBase64String(inspected.Cells))));
        Assert.NotNull(inspected.Inspection?.ViewTree);
        int pid = session.ProcessId!.Value;
        // F2, F3 (opens the ordinary modal), click on its OK button (closes it), F2: all queued at once, faster than a poll.
        Inject(pid, "k,113,60;k,114,61;m,22,8;c,20,9;k,113,60");
        await UntilAsync(() => session.Events.Count(e => e.What is Events.evKeyDown or Events.evMouseDown or Events.evMouseUp) >= 5, 30);
        await session.StopAsync();
        Assert.Equal(RecordingState.Stopped, session.State);
        var events = session.Events;
        var boundaries = session.Capture.Boundaries;
        Assert.NotEmpty(boundaries);
        Assert.True(boundaries.Zip(boundaries.Skip(1)).All(p => p.First.Id < p.Second.Id && p.First.AfterSequence <= p.Second.AfterSequence));
        Assert.All(boundaries, b => Assert.InRange(b.AfterSequence, 0, events[^1].Sequence));
        long f3 = events.First(e => e.KeyCode == Keys.kbF3).Sequence;
        var open = Assert.Single(session.Capture.Snapshots, s => s.Reason == Protocol.SnapshotReasons.ModalOpen);
        Assert.Equal(f3, open.AfterSequence);
        Assert.Contains("Ordinary modal", Screen(ScreenSnapshot.Deserialize(Convert.FromBase64String(open.Cells))));
        Assert.NotNull(open.Inspection?.ViewTree);
        var mark = Assert.Single(boundaries, b => b.Id == open.BoundaryId);
        Assert.Equal((open.Id, open.Generation, open.Hash, open.AfterSequence), (mark.SnapshotId, mark.Generation, mark.Hash, mark.AfterSequence));
        Assert.DoesNotContain("Ordinary modal", Screen(session.EndScreen!)); // it was gone long before anything fetched it
        Assert.True(session.Capture.CoverageComplete, "No boundary or snapshot may have been lost in this short recording.");
        var conversion = RecordingConverter.Convert(events, session.Width, session.Height);
        Assert.Equal("kbF3", conversion.Steps[RecordingCaptureTimeline.StepIndexAfter(mark, conversion.Steps)].Step.Name);
        AssertCleanedUp(session);
    }

    [RecorderTargetFact]
    public async Task PauseResumeAndScriptedInputRefusalOnTheConsole()
    {
        string target = Target!;
        await using var session = new RecordingSession(target, "Win32ConsoleDriver", "80x25");
        await session.StartAsync();
        Assert.True(session.State == RecordingState.Recording, session.Failure);
        int pid = session.ProcessId!.Value;
        Inject(pid, "k,113,60");
        await UntilAsync(() => session.Events.Count >= 1, 20);
        await session.PauseAsync(true);
        Assert.Equal(RecordingState.Paused, session.State);
        Inject(pid, "k,113,60");
        await UntilAsync(() => (session.Last?.Unrecorded ?? 0) >= 3, 20);
        await session.PauseAsync(false);
        Inject(pid, "k,113,60");
        await UntilAsync(() => session.Events.Count >= 2, 20);
        await session.StopAsync();
        Assert.Equal(2, session.Events.Count);
        Assert.True(session.Events[1].AfterGap);
        string end = Screen(session.EndScreen!);
        Assert.True(end.Contains("Count: 3", StringComparison.Ordinal), "The paused key must still reach the application:" + end + " " + string.Join(" ", session.Events.Select(RecordingConverter.Describe)) + " unrecorded=" + session.Last?.Unrecorded);
        var conversion = RecordingConverter.Convert(session.Events, 80, 25);
        Assert.NotNull(conversion.Steps[1].Step.Note);
        AssertCleanedUp(session);

        // The recording session refuses scripted protocol input and checkpoints outright.
        await using var raw = new TargetSession(target, null, "80x25", default, null, 60, "NullDriver", true, record: true);
        var hello = await raw.ConnectAsync();
        Assert.Equal(["key", "text", "mouse", "resize"], hello.Capabilities!.RecordKinds);
        await raw.SendAsync(Protocol.Operations.Start, Protocol.States.Started);
        await raw.SendAsync(Protocol.Operations.RecordStart, Protocol.States.Recording);
        var refused = await Assert.ThrowsAsync<IOException>(() => raw.SendAsync(Protocol.Operations.Input, Protocol.States.Routed, input: new(ScenarioStep.Key, "kbF2")));
        Assert.Contains("no scripted input", refused.Message);
        await Assert.ThrowsAsync<IOException>(() => raw.SendAsync(Protocol.Operations.Checkpoint, Protocol.States.Captured, "during"));
        var poll = (await raw.SendAsync(Protocol.Operations.RecordPoll, Protocol.States.Recording, after: 0)).Recording!;
        Assert.Empty(poll.Events); Assert.Equal(1, poll.RefusedInjections);
        await raw.SendAsync(Protocol.Operations.RecordStop, Protocol.States.Recording, after: 0);
        await raw.SendAsync(Protocol.Operations.Shutdown, Protocol.States.ShutdownAccepted);
    }

    [RecorderTargetFact]
    public async Task CancelDiscardsAndTargetExitKeepsJournalAndCleansUp()
    {
        string target = Target!;
        await using (var cancelled = new RecordingSession(target, "Win32ConsoleDriver", "80x25"))
        {
            await cancelled.StartAsync();
            Assert.Equal(RecordingState.Recording, cancelled.State);
            Inject(cancelled.ProcessId!.Value, "k,113,60");
            await UntilAsync(() => cancelled.Events.Count >= 1, 20);
            await cancelled.CancelAsync();
            Assert.Equal(RecordingState.Cancelled, cancelled.State);
            Assert.Empty(cancelled.Events);
            AssertCleanedUp(cancelled);
        }
        await using var ended = new RecordingSession(target, "Win32ConsoleDriver", "80x25");
        await ended.StartAsync();
        Inject(ended.ProcessId!.Value, "k,113,60;k,119,66"); // F2, then F8: the application exits by itself (code 23)
        await UntilAsync(() => ended.State == RecordingState.Stopped, 30);
        Assert.NotNull(ended.Ended);
        Assert.Contains(ended.Events, e => e.KeyCode == Keys.kbF2);
        AssertCleanedUp(ended);
        WriteEvidence("console-target-exit", ended, RecordingConverter.Convert(ended.Events, 80, 25), new Dictionary<string, object>());
    }

    // ── physical path: SDL window messages ─────────────────────────────────────────────────────────────────────

    [RecorderSdlFact]
    public async Task SdlRecordingFromWindowMessagesRoundTrips()
    {
        string target = Target!;
        await using var session = new RecordingSession(target, "SDLDriver", "80x25");
        await session.StartAsync();
        Assert.True(session.State == RecordingState.Recording, session.Failure);
        var window = await UntilValueAsync(() => SdlWindow(session.ProcessId!.Value), 20);
        // Keys only: posted mouse messages do not carry the pointer position for SDL3 (it reads the real cursor; probe in the
        // agent report), and moving the person's real cursor is out of bounds for an automated test.
        Post(window, Keys.kbF2); Post(window, Keys.kbF2); Post(window, Keys.kbF3); Post(window, Keys.kbEsc); Post(window, Keys.kbF2);
        await UntilAsync(() => session.Events.Count(e => e.What == Events.evKeyDown) >= 5, 20);
        await session.StopAsync();
        Assert.All(session.Events, e => Assert.StartsWith("backend-", e.Origin));
        AssertCleanedUp(session);
        var conversion = RecordingConverter.Convert(session.Events, session.Width, session.Height);
        Assert.True(conversion.Steps.Select(s => s.Step.Name).SequenceEqual(["kbF2", "kbF2", "kbF3", "kbEsc", "kbF2"]),
            string.Join(" ", conversion.Steps.Select(s => s.Step.Name)) + " | " + LastClick + " | " + string.Join(" ", session.Events.Select(RecordingConverter.Describe)));
        var replays = new Dictionary<string, object>();
        foreach (string backend in new[] { "SDLDriver", "NullDriver" })
            replays[backend] = await ReplayAsync(target, conversion, backend, session.EndScreen!);
        WriteEvidence("sdl-roundtrip", session, conversion, replays);
    }

    // ── whole TUI flow: editor -> Record -> physical console input -> Stop -> Append ───────────────────────────

    [RecorderTargetFact]
    public async Task EditorRecordStopAppendWithARealConsoleTarget()
    {
        string target = Target!;
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        // The legacy input-only flow (made Smart capture the default; its physical flow is covered by the review and layout tests).
        WorkbenchRecorder.SmartCaptureOverride = false;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder E2E", Target = target, OutputRoot = Path.Combine(root, "runs") });
            await Drive(140, 40, root, home, async (app, bridge) =>
            {
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60); // the startup catalog load must finish first
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                var window = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.StartCommand));
                await Until(bridge, () => window.Session?.State == RecordingState.Recording, 60);
                Inject(window.Session!.ProcessId!.Value, "k,113,60;k,114,61;m,22,8;c,20,9");
                await Until(bridge, () => window.Session!.Events.Count >= 4, 30);
                await Shot(bridge, "after", "recording-live-real-target");
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.StopCommand));
                await Until(bridge, () => window.Session!.State == RecordingState.Stopped, 60);
                await bridge.Invoke(() => window.UpdateView());
                await Shot(bridge, "after", "recording-review-real-target");
                AssertCleanedUp(window.Session!);
                int count = await Invoke(bridge, () => editor.Document.Steps.Count);
                await bridge.Invoke(() => editor.SelectSteps(0));
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.InsertCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                var names = await Invoke(bridge, () => editor.Document.Steps.Select(s => s.Name).ToArray());
                Assert.Equal(count + 3, names.Length);
                Assert.Equal(["kbF2", "kbF3", "click-left@20,9"], names.Take(3));
                await Shot(bridge, "after", "editor-after-insert-real-target");
                await bridge.Invoke(() => { var saved = editor.Document.Save(); Assert.True(saved.Saved, saved.Message); });
            });
        }
        finally { WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root); }
    }

    // ── helpers: replay and evidence ───────────────────────────────────────────────────────────────────────────

    private static async Task<object> ReplayAsync(string target, RecordingConversion conversion, string backend, ScreenSnapshot recorded)
    {
        string folder = Temp(); Directory.CreateDirectory(folder);
        try
        {
            var doc = ScenarioDocument.New("recorded/roundtrip", "Recorded round trip", "Steps produced by the recorder, replayed unchanged.", checkpoint: null);
            doc.Append(conversion.DocumentSteps);
            doc.Append(ScenarioDocumentStep.Checkpoint("after-recording"));
            string path = Path.Combine(folder, "recorded.scenario.json");
            var saved = doc.Save(path);
            Assert.True(saved.Saved, saved.Message);
            var scenario = ExternalScenarios.Merge([], [ExternalScenarios.Load(path)]).Single();
            var batch = await new ScenarioRunner().RunAsync(target, [scenario], new RunOptions(Path.Combine(folder, "runs"), ["80x25"]) { ProxyBackend = backend });
            var result = batch.Results.Single();
            Assert.True(result.Outcome == ScenarioOutcomes.Passed, $"{backend}: {result.Outcome} {result.Reason}");
            var replayed = ScreenSnapshot.Deserialize(File.ReadAllBytes(Path.Combine(batch.RunRoot, result.Checkpoints.Single().Cells)));
            // ProxyOrdinary's telemetry row (screen row 7) lists every event kind the application retrieved, including the real
            // driver's key releases; key steps replay presses only (documented limitation). Every other cell must be equal.
            var differing = Enumerable.Range(0, recorded.Height).Where(y => Row(recorded, y) != Row(replayed, y)).ToArray();
            Assert.True(differing.All(y => y == 7), $"{backend}: rows {string.Join(",", differing)} differ from the recording.\nrecorded:\n{Screen(recorded)}\nreplayed:\n{Screen(replayed)}");
            if (Evidence is { } root)
            {
                Directory.CreateDirectory(root);
                File.WriteAllBytes(Path.Combine(root, $"p3c-replay-{backend}.logical.tvs1"), replayed.Serialize());
                File.WriteAllText(Path.Combine(root, $"p3c-replay-{backend}.logical.txt"), Screen(replayed));
                File.WriteAllBytes(Path.Combine(root, $"p3c-replay-{backend}.logical.png"), LogicalPng.Encode(replayed));
            }
            return new { backend, outcome = result.Outcome, differingRows = differing, telemetryRecorded = Row(recorded, 7).Trim(), telemetryReplayed = Row(replayed, 7).Trim(), result.CleanupSucceeded, result.TargetExited };
        }
        finally { Delete(folder); }
    }

    private static void WriteEvidence(string name, RecordingSession session, RecordingConversion conversion, Dictionary<string, object> replays)
    {
        if (Evidence is not { } root) return;
        Directory.CreateDirectory(root);
        if (session.EndScreen is { } end)
        {
            File.WriteAllBytes(Path.Combine(root, $"p3c-{name}-recording-end.logical.tvs1"), end.Serialize());
            File.WriteAllText(Path.Combine(root, $"p3c-{name}-recording-end.logical.txt"), Screen(end));
            File.WriteAllBytes(Path.Combine(root, $"p3c-{name}-recording-end.logical.png"), LogicalPng.Encode(end));
        }
        File.WriteAllText(Path.Combine(root, $"p3c-{name}.json"), JsonSerializer.Serialize(new
        {
            name, backend = session.Backend, grid = $"{session.Width}x{session.Height}", session.HostMode, session.WindowVisible,
            state = session.State.ToString(), session.Ended, session.JournalUsed, counters = session.Last,
            events = session.Events, origins = session.Events.GroupBy(e => e.Origin).ToDictionary(g => g.Key, g => g.Count()),
            steps = conversion.Steps.Select(s => s.Step.ToJson().ToJsonString()), conversion.Notes, conversion.Mismatches,
            cleanup = new { session.Cleanup, session.TargetExited, session.StopOutcome, session.ProcessId },
            replays,
            injection = name.StartsWith("sdl", StringComparison.Ordinal)
                ? "test process PostMessage(WM_KEYDOWN/WM_KEYUP/WM_MOUSEMOVE/WM_LBUTTONDOWN/UP) to the target's SDL window -> SDL wndproc -> SDL queue -> SDLDriver -> Proxy"
                : "separate pwsh helper: AttachConsole(target) + WriteConsoleInputW into the target's console input buffer -> Win32ConsoleDriver.PumpMessages/ReadConsoleInputW -> Proxy",
            protocolInputSent = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssertCleanedUp(RecordingSession session)
    {
        Assert.True(session.TargetExited, "The owned target process must have exited.");
        Assert.True(session.Cleanup, "The session's temporary root must have been removed.");
        if (session.ProcessId is { } pid) Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid).Dispose());
    }

    // Separate process: attaches to the TARGET's console and writes ordinary INPUT_RECORDs into its input buffer.
    private const string InjectorScript = """
        param([int]$ProcessId, [string]$Plan)
        $ErrorActionPreference = 'Stop'
        Add-Type -TypeDefinition @'
        using System; using System.Runtime.InteropServices; using System.Threading;
        public static class P3cConsoleInput {
            [StructLayout(LayoutKind.Explicit)] public struct Rec {
                [FieldOffset(0)] public ushort Type;
                [FieldOffset(4)] public int Down; [FieldOffset(8)] public ushort Repeat; [FieldOffset(10)] public ushort Vk; [FieldOffset(12)] public ushort Scan; [FieldOffset(14)] public ushort Ch; [FieldOffset(16)] public uint KeyState;
                [FieldOffset(4)] public short X; [FieldOffset(6)] public short Y; [FieldOffset(8)] public uint Buttons; [FieldOffset(12)] public uint MouseState; [FieldOffset(16)] public uint Flags; }
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteConsoleInputW(IntPtr h, Rec[] r, uint n, out uint w);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
            static Rec Key(bool down, ushort vk, ushort scan) { var r = new Rec(); r.Type = 1; r.Down = down ? 1 : 0; r.Repeat = 1; r.Vk = vk; r.Scan = scan; return r; }
            static Rec Mouse(short x, short y, uint buttons, uint flags) { var r = new Rec(); r.Type = 2; r.X = x; r.Y = y; r.Buttons = buttons; r.Flags = flags; return r; }
            public static int Run(uint pid, string plan) {
                FreeConsole();
                if (!AttachConsole(pid)) return 10;
                IntPtr h = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) return 11;
                try {
                    foreach (string token in plan.Split(';')) {
                        string[] p = token.Split(',');
                        Rec[] recs;
                        if (p[0] == "k") recs = new[] { Key(true, ushort.Parse(p[1]), ushort.Parse(p[2])), Key(false, ushort.Parse(p[1]), ushort.Parse(p[2])) };
                        else if (p[0] == "m") recs = new[] { Mouse(short.Parse(p[1]), short.Parse(p[2]), 0, 1) };
                        else if (p[0] == "c") recs = new[] { Mouse(short.Parse(p[1]), short.Parse(p[2]), 0, 1), Mouse(short.Parse(p[1]), short.Parse(p[2]), 1, 0), Mouse(short.Parse(p[1]), short.Parse(p[2]), 0, 0) };
                        else return 12;
                        uint written;
                        if (!WriteConsoleInputW(h, recs, (uint)recs.Length, out written) || written != recs.Length) return 13;
                        Thread.Sleep(250); // person-like pacing on the injector side only; never in the recorded JSON
                    }
                    return 0;
                } finally { CloseHandle(h); FreeConsole(); }
            }
        }
        '@
        exit [P3cConsoleInput]::Run([uint32]$ProcessId, $Plan)
        """;

    private static void Inject(int pid, string plan)
    {
        string script = Path.Combine(Path.GetTempPath(), "p3c-console-input.ps1");
        File.WriteAllText(script, InjectorScript);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-ProcessId", pid.ToString(), "-Plan", plan }) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60000), "Console input helper did not finish.");
        Assert.True(p.ExitCode == 0, $"Console input helper failed ({p.ExitCode}): {err}");
    }

    // SDL: ordinary window messages to the target's own top-level SDL window (no foreground or focus needed).
    private static IntPtr SdlWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint owner); if (owner == pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    private static void Post(IntPtr window, ushort key)
    {
        (uint vk, uint scan) = key switch { Keys.kbF2 => (0x71u, 0x3Cu), Keys.kbF3 => (0x72u, 0x3Du), Keys.kbEsc => (0x1Bu, 0x01u), _ => throw new ArgumentException("key") };
        PostMessage(window, 0x0100, (nint)vk, (nint)(1 | (scan << 16)));
        Thread.Sleep(60);
        PostMessage(window, 0x0101, (nint)vk, unchecked((nint)(uint)(1 | (scan << 16) | 0xC0000000)));
        Thread.Sleep(120);
    }
    private static void Click(IntPtr window, int cellX, int cellY, int cols, int rows)
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            GetClientRect(window, out var rect);
            var unaware = SetThreadDpiAwarenessContext(-1); GetClientRect(window, out var logical); SetThreadDpiAwarenessContext(-4);
            LastClick = $"client {rect.Right}x{rect.Bottom} (dpi-aware), {logical.Right}x{logical.Bottom} (unaware), dpi {GetDpiForWindow(window)}";
            int px = (int)((cellX + 0.5) * rect.Right / cols), py = (int)((cellY + 0.5) * rect.Bottom / rows);
            nint at = (nint)((py << 16) | (px & 0xffff));
            PostMessage(window, 0x0200, 0, at); Thread.Sleep(80);
            PostMessage(window, 0x0201, 1, at); Thread.Sleep(80);
            PostMessage(window, 0x0202, 0, at); Thread.Sleep(150);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    private static string LastClick = "";
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);

    // ── generic helpers ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs only with a deployed ProxyOrdinary target (TSWB_PRODUCT_TARGET); otherwise reported as SKIPPED, never as passed.</summary>
    public sealed class RecorderTargetFactAttribute : FactAttribute
    {
        public RecorderTargetFactAttribute() { if (Target is null) Skip = NeedsTarget; }
    }
    public sealed class RecorderSdlFactAttribute : FactAttribute
    {
        public RecorderSdlFactAttribute()
        {
            if (Target is null) Skip = NeedsTarget;
            else if (Environment.GetEnvironmentVariable("TSW_PROXY_SDL_TEST") != "1") Skip = "Set TSW_PROXY_SDL_TEST=1 (opens a real SDL window).";
        }
    }
    private static string SavedDocument(string root)
    {
        string path = Path.Combine(root, "docs", "recorded-" + Guid.NewGuid().ToString("N")[..6] + ".scenario.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saved = Document().Save(path);
        Assert.True(saved.Saved, saved.Message);
        return path;
    }
    private static ScenarioDocument Document() => ScenarioDocument.New("recorded/demo", "Recorded demo", "Recorder window capture document.", checkpoint: "initial");
    private static string Row(ScreenSnapshot s, int y) => new(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character == 0 ? ' ' : s[x, y].Character).ToArray());
    private static string Screen(ScreenSnapshot s) => string.Join("\n", Enumerable.Range(0, s.Height).Select(y => Row(s, y).TrimEnd()));
    private static async Task UntilAsync(Func<bool> condition, int seconds)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Condition not reached."); await Task.Delay(50); }
    }
    private static async Task<IntPtr> UntilValueAsync(Func<IntPtr> value, int seconds)
    {
        var clock = Stopwatch.StartNew();
        while (true) { var v = value(); if (v != IntPtr.Zero) return v; if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Window not found."); await Task.Delay(100); }
    }
    private static TEvent Cmd(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; return ev; }
    private static string SaveProject(string folder, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, "recorder.twproj"); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "p3c-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static async Task<string> Text(Bridge bridge, WorkbenchRecordingWindow window) { string text = ""; await bridge.Invoke(() => { window.UpdateView(); text = window.Text; }); return text; }
    private static async Task<T> Invoke<T>(Bridge bridge, Func<T> func) { T result = default!; await bridge.Invoke(() => result = func()); return result; }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            string target = TestWorkbenchHostTests.SampleTarget;
            using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { foreach (var w in new[] { WorkbenchRecordingWindow.Active }) w?.Close(); app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                    }).WaitAsync(TimeSpan.FromSeconds(30));
                }
            });
            AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(300)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }

    // Logical screenshots (NullDriver cell screen of the real WorkbenchApp, diagnostic bitmap font; not desktop-native).
    private static async Task Shot(Bridge bridge, string phase, string state)
    {
        await bridge.Invoke(() => { }); await Task.Delay(80); await bridge.Invoke(() => { });
        if (Evidence is not { } root) return;
        await bridge.Invoke(() =>
        {
            string folder = Path.Combine(root, phase); Directory.CreateDirectory(folder);
            var screen = ScreenSnapshot.CopyRoot();
            string name = $"p3c-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(folder, name + ".logical.png"), png);
            File.WriteAllBytes(Path.Combine(folder, name + ".logical.tvs1"), cells);
            File.WriteAllText(Path.Combine(folder, name + ".logical.txt"), Screen(screen));
            File.WriteAllText(Path.Combine(folder, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, phase, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(RecorderTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
