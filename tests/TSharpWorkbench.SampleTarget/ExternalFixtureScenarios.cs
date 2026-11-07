using TSharpVision.Constants;

namespace TSharpWorkbench.SampleTarget;

// A diagnostic fixture for external checkpoint retry qualification. The delayed label models asynchronous application work.
internal static class ExternalFixtureScenarios
{
    internal static TestScenario Deferred()
    {
        DeferredLabel? label = null;
        var cancellation = new CancellationTokenSource();
        return new TestScenario("selftest/fixture/deferred-label", "Deferred label fixture", "selftest/fixture", "A label becomes ready after 1200 ms; no files or external effects.")
            .Setup(c => { label = new(); c.Application.DeskTop!.Insert(label); })
            .Action("begin-deferred-label", context =>
            {
                _ = ChangeAsync();
                async Task ChangeAsync()
                {
                    try { await Task.Delay(1200, cancellation.Token); if (!cancellation.IsCancellationRequested) label!.Post(0x7dd9, null); }
                    catch (OperationCanceledException) { }
                }
            })
            .Checkpoint("ready", _ => label!.Ready)
            .Cleanup(_ => { cancellation.Cancel(); cancellation.Dispose(); });
    }

    private sealed class DeferredLabel() : TStaticText(new TRect(2, 2, 50, 3), "Pending asynchronous fixture work")
    {
        internal bool Ready { get; private set; }
        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evCommand && ev.message.command == 0x7dd9)
            { Text = "Deferred fixture is ready"; Ready = true; DrawView(); ClearEvent(ref ev); }
            else base.HandleEvent(ref ev);
        }
    }
}
