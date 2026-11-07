using TSharpVision.Constants;

namespace TSharpWorkbench.SampleTarget;

// A fixture-only registration. It owns executable behaviour only (setup, deferred readiness, named actions and
// points, typed checkpoint expectations, completion and cleanup); every scenario step comes from external JSON bound to it.
// Test-only switches (ordinary child environment, never set by discovery): TSWB_FIXTURE_REVISION overrides the reviewed
// revision (models contract drift between discovery and execution); TSWB_FIXTURE_CLEANUP_MARKER names a file the cleanup
// callback appends to, outside the integrity-checked fixture directory.
internal static class FixtureOnlyScenarios
{
    internal const string Notes = "sample/fixture-only/notes", Revision = "tsw-fixture-1.sample.1";

    internal static TestScenario NotesFixture()
    {
        NotesLabel? label = null;
        var cancellation = new CancellationTokenSource();
        bool ready = false;
        static SampleApp App(TestTargetContext c) => (SampleApp)c.Application;
        return new TestScenario(Notes, "Notes fixture (fixture-only)", "sample/fixture-only",
                "Fixture-only registration: deferred readiness, a note action, a deferred label and typed expectations; no steps of its own.")
            .Kind(ScenarioKinds.Window).Grid(60, 20).Metadata("area", "fixture-only")
            .Safety(new ScenarioSafety(Notes: "No files, network or devices; synthetic model only."))
            .FixtureOnly(Environment.GetEnvironmentVariable("TSWB_FIXTURE_REVISION") ?? Revision)
            .Setup(c =>
            {
                label = new NotesLabel();
                c.Identify(label, "notes-label");
                c.Application.DeskTop!.Insert(label);
                // Deferred readiness: Start is accepted only after this background work completed.
                _ = Task.Delay(300, cancellation.Token).ContinueWith(t => ready = !t.IsCanceled, TaskScheduler.Default);
            })
            .Ready(_ => ready)
            .RegisterAction("open-note", c => { var ev = Command(SampleCommands.NewNote); c.Application.PutEvent(ref ev); })
            .RegisterAction("begin-deferred-label", context =>
            {
                _ = ChangeAsync();
                async Task ChangeAsync()
                {
                    try { await Task.Delay(600, cancellation.Token); label!.Post(NotesLabel.Command, null); }
                    catch (OperationCanceledException) { }
                }
            })
            .RegisterPoint("label", _ => label is null ? null : label.MakeGlobal(new TPoint(0, 0)))
            .RegisterCheckpointExpectation("idle", c => c.NoModalView && App(c).NoteCount == 0 && label is { Ready: false }, "desktop idle, label pending")
            .RegisterCheckpointExpectation("note-open", c => App(c).NoteCount == 1, "exactly one note window")
            .RegisterCheckpointExpectation("label-ready", _ => label is { Ready: true }, "deferred label is ready")
            .RegisterCheckpointExpectation("never-true", _ => false, "deliberately unsatisfiable (bounded failure)")
            .Completion(c => c.NoModalView ? null : "A modal view is still open.")
            .Cleanup(_ =>
            {
                cancellation.Cancel(); cancellation.Dispose();
                if (Environment.GetEnvironmentVariable("TSWB_FIXTURE_CLEANUP_MARKER") is { Length: > 0 } marker)
                    File.AppendAllText(marker, "cleanup\n");
            });
    }

    private static TEvent Command(ushort command)
    {
        var ev = new TEvent { What = Events.evCommand };
        ev.message.command = command;
        return ev;
    }

    private sealed class NotesLabel() : TStaticText(new TRect(2, 14, 50, 15), "Notes fixture pending")
    {
        internal const ushort Command = 0x7dd8;
        internal bool Ready { get; private set; }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evCommand && ev.message.command == Command)
            { Text = "Notes fixture ready"; Ready = true; DrawView(); ClearEvent(ref ev); }
            else base.HandleEvent(ref ev);
        }
    }
}
