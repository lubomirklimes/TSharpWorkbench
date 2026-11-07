using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using TSharpVision;
using TSharpVision.Constants;
using TSharpVision.Drivers;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Proxy.Tests;

// Workstream A: RecordInspect against a real in-process ProxySessionHost. The host is created exactly as in a target
// (WorkbenchProxyDriver.Initialize with the session environment) over a NullDriver; the test talks to it through the real
// local named pipe with Protocol.WriteAsync/ReadAsync, and the test thread is the UI thread: it serves boundaries by calling
// OnIdleBoundary itself (or deliberately not, to make the UI "busy"). No process or window.
public sealed class RecordInspectTests : IDisposable
{
    private readonly IDriver? oldDriver = TDisplay.driver;
    private readonly ScreenBuffer? oldBuffer = TScreen.ScreenBuffer;
    private readonly string? oldSelection = Environment.GetEnvironmentVariable("TSHARPVISION_DRIVER");
    private static readonly string[] Variables = ["TSHARPVISION_PROXY_SESSION", "TSHARPVISION_DRIVER_DECORATOR", TestTargetHost.InstanceVariable,
        TestTargetHost.SecretVariable, TestTargetHost.RecordVariable, TestTargetHost.OperationLimitVariable, TestTargetHost.LifetimeVariable,
        TestTargetHost.StateVariable, TestTargetHost.SizeVariable];
    private readonly Dictionary<string, string?> oldVariables = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
    private string instance = "", secret = "";
    private int ids;
    private NullDriver native = null!;
    private WorkbenchProxyDriver proxy = null!;
    private InspectApp app = null!;
    private NamedPipeClientStream? client;

    public RecordInspectTests()
    {
        TDisplay.driver = null;
        TEventQueue.Resume(); Drain(); TEventQueue.ClearPosted(); TEventQueue.ClaimUiThread();
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", "NullDriver");
    }

    public void Dispose()
    {
        client?.Dispose();
        if (app is not null) { app.ShutDown(); app.Dispose(); }
        TDisplay.driver?.Shutdown();
        TDisplay.driver = oldDriver; TScreen.ScreenBuffer = oldBuffer;
        Drain(); TEventQueue.ClearPosted();
        Environment.SetEnvironmentVariable("TSHARPVISION_DRIVER", oldSelection);
        foreach (var (name, value) in oldVariables) Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public void InspectionDuringRecordingIsCoherentWithTheScreen()
    {
        Open();
        var window = AddWindow(out _);
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordStart).State);
        native.EnqueueKey(TestKeys.KeyDown(Keys.kbF2));
        TEvent ev = default; TScreen.GetEvent(ref ev);
        Assert.Equal(Keys.kbF2, ev.keyDown.keyCode); // one physical input delivered while recording

        var reply = Send(Protocol.Operations.RecordInspect);
        Assert.Equal(Protocol.States.Inspected, reply.State);
        var inspect = reply.Inspect!;
        var now = ScreenSnapshot.CopyRoot(); // nothing drew since the inspection pass
        Assert.Equal(Convert.ToBase64String(now.Serialize()), inspect.Cells);
        Assert.Equal((40, 12), (inspect.Width, inspect.Height));
        Assert.Equal(ProxyScreenHash.Compute(now), inspect.Hash);
        Assert.Equal(proxy.ScreenGeneration, inspect.Generation);
        Assert.Equal(Environment.CurrentManagedThreadId, inspect.UiThread);
        Assert.Equal((1L, 1L), (inspect.AfterSequence, inspect.Delivered));
        var cells = ScreenSnapshot.Deserialize(Convert.FromBase64String(inspect.Cells));
        Assert.Contains(Enumerable.Range(0, cells.Height), y => Row(cells, y).Contains(" Inspect ", StringComparison.Ordinal));

        var evidence = inspect.Inspection!;
        Assert.Empty(evidence.Errors);
        var nodes = Flatten(evidence.ViewTree!.Root).ToArray();
        Assert.Contains(nodes, n => n.Type == typeof(TWindow).FullName && n.Text == "Inspect");
        Assert.Equal("first item", nodes.Single(n => n.Type == typeof(ItemList).FullName).Text);
        var file = evidence.Menu!.Bar!.Items[0];
        Assert.Equal(("~F~ile", "match"), (file.Markup, file.Observed)); // model and cells agree
        Assert.False(evidence.Menu.Active);
        Assert.Equal(11, evidence.Status!.Screen.Y);

        // Paused is still a started, unstopped recording; requested evidence kinds are honoured.
        Assert.True(Send(Protocol.Operations.RecordPause).Recording!.Paused);
        var menuOnly = Send(Protocol.Operations.RecordInspect, evidence: [Protocol.EvidenceKinds.Menu]);
        Assert.Equal(Protocol.States.Inspected, menuOnly.State);
        Assert.Equal((false, true, false), (menuOnly.Inspect!.Inspection!.ViewTree is not null, menuOnly.Inspect.Inspection.Menu is not null,
            menuOnly.Inspect.Inspection.Status is not null));
        Assert.Equal(inspect.Hash, menuOnly.Inspect.Hash);
        GC.KeepAlive(window);
    }

    [Fact]
    public void BusyUiGivesNonfatalBusyAndTheSessionContinues()
    {
        Open(operationMs: 500);
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordStart).State);
        var clock = Stopwatch.StartNew();
        var busy = Send(Protocol.Operations.RecordInspect, pump: false); // the UI never reaches a boundary
        clock.Stop();
        Assert.Equal(Protocol.States.Rejected, busy.State);
        Assert.StartsWith(Protocol.BusyPrefix, busy.Reason);
        Assert.InRange(clock.ElapsedMilliseconds, 400, 5000);
        Assert.Equal(0, QueuedCommands()); // the unclaimed work was removed from the bounded(1) channel
        proxy.OnIdleBoundary(app);         // a late boundary serves nothing and writes nothing
        // The session still answers transport and UI-path operations normally (each reply matches its own request id).
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordPoll, pump: false).State);
        Assert.Equal(Protocol.States.Inspected, Send(Protocol.Operations.RecordInspect).State);
        var stop = Send(Protocol.Operations.RecordStop);
        Assert.Equal(Protocol.States.Recording, stop.State);
        Assert.False(stop.Recording!.Active);
    }

    [Fact]
    public void UiThatClaimedTheWorkButFinishesLateIsHarmless()
    {
        Open(operationMs: 500);
        AddWindow(out var list);
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordStart).State);
        using var release = new ManualResetEventSlim();
        list.Hold = release; // the UI pass blocks inside Inspectors.Capture until the Busy reply has arrived
        var busy = Send(Protocol.Operations.RecordInspect, afterRead: release.Set);
        Assert.True(list.Claimed);
        Assert.Equal(Protocol.States.Rejected, busy.State);
        Assert.StartsWith(Protocol.BusyPrefix, busy.Reason);
        Assert.Equal(0, QueuedCommands());
        // The abandoned completion produced no second reply: the next replies carry their own ids (asserted by Send).
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordPoll, pump: false).State);
        Assert.Equal(Protocol.States.Inspected, Send(Protocol.Operations.RecordInspect).State);
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordStop).State);
    }

    [Fact]
    public void InspectionInjectsNothingAndLeavesCheckpointStateAlone()
    {
        Open();
        var window = AddWindow(out var list);
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordStart).State);
        native.EnqueueKey(TestKeys.KeyDown(Keys.kbF2));
        TEvent ev = default; TScreen.GetEvent(ref ev);
        var checkpoint = proxy.CaptureCheckpoint(app);
        long epoch = proxy.Epoch;
        TEventQueue.Enqueue(Command(0x7a01)); // an application event already queued must stay exactly as it is
        var focus = (app.current, app.DeskTop!.current, window.current, app.TopView(), (list.state & Views.sfFocused) != 0);
        var cursor = proxy.Recorder.Cursor();
        var poll = Send(Protocol.Operations.RecordPoll, pump: false).Recording!;

        for (int i = 0; i < 3; i++) Assert.Equal(Protocol.States.Inspected, Send(Protocol.Operations.RecordInspect).State);

        var after = proxy.Recorder.Cursor();
        Assert.Equal((cursor.Sequence, cursor.Delivered, cursor.Active, cursor.Paused, cursor.RecordingId),
            (after.Sequence, after.Delivered, after.Active, after.Paused, after.RecordingId));
        var again = Send(Protocol.Operations.RecordPoll, pump: false).Recording!;
        Assert.Equal((poll.Events.Length, poll.RefusedInjections, poll.Unrecorded, poll.OtherQueue, poll.Dropped),
            (again.Events.Length, again.RefusedInjections, again.Unrecorded, again.OtherQueue, again.Dropped));
        Assert.Equal(0, again.RefusedInjections);
        Assert.Equal(focus, (app.current, app.DeskTop.current, window.current, app.TopView(), (list.state & Views.sfFocused) != 0));
        Assert.Equal(Events.evNothing, app.PendingWhat);       // no PutEvent
        Assert.False(proxy.ReadKeyEvent(out _));               // forward FIFO empty, nothing synthesized
        var queued = new List<TEvent>();
        for (TEvent q = default; ; ) { TEventQueue.GetNextEvent(ref q); if (q.What == Events.evNothing) break; queued.Add(q); }
        Assert.Equal((ushort)0x7a01, Assert.Single(queued).message.command);
        Assert.Equal(epoch, proxy.Epoch);
        Assert.Equal(checkpoint.Revision + 1, proxy.CaptureCheckpoint(app).Revision); // inspection never bumped the revision
    }

    [Fact]
    public void RecordingPermissionsAreUnchangedAndInspectNeedsAnActiveRecording()
    {
        Open();
        var early = Send(Protocol.Operations.RecordInspect);
        Assert.Equal(Protocol.States.Rejected, early.State);
        Assert.DoesNotContain(Protocol.BusyPrefix, early.Reason);
        Assert.Contains("active recording", early.Reason);
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordStart).State);

        var input = Send(Protocol.Operations.Input, input: new InputStep(ScenarioStep.Key, "kbF2"));
        Assert.Equal(Protocol.States.Rejected, input.State);
        Assert.Contains("no scripted input", input.Reason);
        foreach (var operation in new[] { Protocol.Operations.Checkpoint, Protocol.Operations.Recapture })
        {
            var refused = Send(operation, checkpoint: "inspect");
            Assert.Equal(Protocol.States.Rejected, refused.State);
            Assert.Contains("no scripted input or checkpoints", refused.Reason);
        }
        Assert.Equal(1, Send(Protocol.Operations.RecordPoll, pump: false).Recording!.RefusedInjections);
        Assert.Equal(Protocol.States.Inspected, Send(Protocol.Operations.RecordInspect).State);
        Assert.Equal(1, Send(Protocol.Operations.RecordPoll, pump: false).Recording!.RefusedInjections);

        Assert.False(Send(Protocol.Operations.RecordStop).Recording!.Active);
        var late = Send(Protocol.Operations.RecordInspect);
        Assert.Equal(Protocol.States.Rejected, late.State);
        Assert.DoesNotContain(Protocol.BusyPrefix, late.Reason);
        Assert.Equal(Protocol.States.Recording, Send(Protocol.Operations.RecordPoll, pump: false).State); // session alive
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Open(int operationMs = 10000)
    {
        instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        foreach (var name in Variables) Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable("TSHARPVISION_PROXY_SESSION", "1");
        Environment.SetEnvironmentVariable(TestTargetHost.InstanceVariable, instance);
        Environment.SetEnvironmentVariable(TestTargetHost.SecretVariable, secret);
        Environment.SetEnvironmentVariable(TestTargetHost.RecordVariable, "1");
        Environment.SetEnvironmentVariable(TestTargetHost.OperationLimitVariable, operationMs.ToString());
        native = new NullDriver(40, 12);
        proxy = WorkbenchProxyDriver.Wrap(native);
        proxy.Initialize(); TDisplay.driver = proxy; // creates the ProxySessionHost and its pipe
        app = new InspectApp();
        app.Redraw();
        client = new NamedPipeClientStream(".", "tsharp-diagnostics-" + instance, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(5000);
        Assert.Equal(Protocol.States.Ready, Send(Protocol.Operations.Handshake).State);
        Assert.Equal(Protocol.States.Started, Send(Protocol.Operations.Start).State);
    }

    private TWindow AddWindow(out ItemList list)
    {
        var window = new TWindow(new TRect(2, 1, 30, 9), "Inspect", 1);
        list = new ItemList(new TRect(1, 1, 20, 4));
        window.Insert(list);
        app.DeskTop!.Insert(window);
        app.Redraw();
        return window;
    }

    // Sends one request; while waiting the test thread acts as the UI and serves boundaries unless pump is false.
    private Reply Send(string operation, bool pump = true, string[]? evidence = null, InputStep? input = null, string? checkpoint = null,
        Action? afterRead = null)
    {
        var request = new Request(Protocol.Version, Protocol.Schema, instance, "recording-" + ++ids, secret, operation,
            Checkpoint: checkpoint, Input: input, Evidence: evidence);
        using var limit = new CancellationTokenSource(10000);
        Protocol.WriteAsync(client!, request, limit.Token).GetAwaiter().GetResult();
        var read = Protocol.ReadAsync<Reply>(client!, limit.Token);
        if (afterRead is not null) _ = read.ContinueWith(_ => afterRead(), TaskScheduler.Default);
        while (!((IAsyncResult)read).AsyncWaitHandle.WaitOne(2)) if (pump) proxy.OnIdleBoundary(app);
        var reply = read.GetAwaiter().GetResult();
        Assert.Equal(request.Id, reply.Id); // never a stale or second reply
        return reply;
    }

    // White-box: number of items waiting in the session's bounded(1) UI channel.
    private int QueuedCommands()
    {
        var session = typeof(WorkbenchProxyDriver).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(proxy)!;
        var channel = session.GetType().GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
        return (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!;
    }

    private static IEnumerable<ViewNode> Flatten(ViewNode node) => node.Children.SelectMany(Flatten).Prepend(node);
    private static string Row(ScreenSnapshot cells, int y) => new(Enumerable.Range(0, cells.Width).Select(x => cells[x, y].Character).ToArray());
    private static void Drain() { TEventQueue.Resume(); TEvent ev = default; do { TEventQueue.GetNextEvent(ref ev); } while (ev.What != Events.evNothing); }
    private static TEvent Command(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; return ev; }

    private sealed class InspectApp : TApplication
    {
        public ushort PendingWhat => Pending.What;
        public override TMenuBar InitMenuBar(TRect r)
        {
            r.b.y = r.a.y + 1;
            return new TMenuBar(r, new TSubMenu("~F~ile", Keys.kbAltF) + new TMenuItem("E~x~it", Views.cmQuit, Keys.kbAltX, Views.hcNoContext, "Alt+X"));
        }
    }

    internal sealed class ItemList : TListViewer
    {
        public ManualResetEventSlim? Hold;
        public bool Claimed;
        public ItemList(TRect bounds) : base(bounds, 1, null, null) => SetRange(1);
        public override string GetText(int item, int maxLen)
        {
            if (Hold is { } hold) { Hold = null; Claimed = true; hold.Wait(5000); }
            return "first item";
        }
    }
}
