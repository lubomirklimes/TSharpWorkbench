using TSharpVision.Constants;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class FrameworkLifecycleCompatibilityTests
{
    [Fact]
    public void SequentialApplicationsDoNotInheritThePreviousPendingCancel()
    {
        using var driver = new DriverScope();
        TEventQueue.Resume();
        TEventQueue.ClearPosted();
        try
        {
            for (int cycle = 0; cycle < 2; cycle++)
            {
                using (var previous = new TApplication())
                {
                    var cancel = new TEvent { What = Events.evCommand };
                    cancel.message.command = Views.cmCancel;
                    previous.PutEvent(ref cancel);
                    previous.ShutDown();
                }

                using var next = new TApplication();
                TEvent first = default;
                next.GetEvent(ref first);
                Assert.Equal(Events.evNothing, first.What);
                next.ShutDown();
            }
        }
        finally
        {
            TEventQueue.ClearPosted();
        }
    }
}
