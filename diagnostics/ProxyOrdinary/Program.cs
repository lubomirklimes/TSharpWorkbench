using TSharpVision;
using TSharpVision.Constants;

using var application = new CounterApplication();
if (args.Contains("--smoke"))
{
    Console.WriteLine("ACTIVE_DRIVER=" + TDisplay.driver!.GetType().FullName);
    TEvent quit = new() { What = Events.evCommand };
    quit.message.command = Views.cmQuit;
    TEventQueue.Enqueue(quit);
}
return AppLifecycleGuard.Run(application);

// An ordinary framework application: no Testing, proxy, scenario catalog or diagnostic host.
sealed class CounterApplication : TApplication
{
    private int count;
    private readonly TParamText label;
    private readonly TParamText telemetry;
    private ushort seen;
    public CounterApplication()
    {
        var window = new TWindow(new TRect(2, 2, 44, 9), "Ordinary counter", 1);
        label = new TParamText(new TRect(2, 2, 38, 3), "Count: 0", 0);
        telemetry = new TParamText(new TRect(2, 4, 38, 5), "Events: 0000", 0);
        window.Insert(telemetry); window.Insert(label); DeskTop!.Insert(window);
    }
    // Normal application-level event observation, including events retrieved by nested modal loops.
    public override void GetEvent(ref TEvent ev)
    {
        base.GetEvent(ref ev);
        ushort next = (ushort)(seen | ev.What);
        if (seen != next) { seen = next; telemetry.SetText("Events: " + seen.ToString("X4")); }
    }
    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown)
        {
            switch (ev.keyDown.keyCode)
            {
                case Keys.kbF2: label.SetText("Count: " + ++count); ClearEvent(ref ev); return;
                case Keys.kbF3:
                    using (var dialog = new TDialog(new TRect(5, 4, 40, 12), "Ordinary modal"))
                    {
                        dialog.Insert(new TButton(new TRect(10, 4, 24, 6), "OK", Views.cmOK, ButtonConstants.bfDefault));
                        DeskTop!.ExecView(dialog);
                    }
                    label.SetText("Modal closed"); ClearEvent(ref ev); return;
                // Deterministic failure probes for owned-child lifecycle qualification.
                case Keys.kbF8: Environment.Exit(23); return;
                case Keys.kbF6: Thread.Sleep(2000); ClearEvent(ref ev); return;
                case Keys.kbF7: Thread.Sleep(10000); ClearEvent(ref ev); return;
            }
        }
        base.HandleEvent(ref ev);
    }
}
