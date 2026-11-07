// Ordinary framework bootstrap: deliberately no Workbench/Testing reference.
using TSharpVision;
using TSharpVision.Constants;

using var application = new TApplication();
Console.WriteLine("ACTIVE_DRIVER=" + TDisplay.driver!.GetType().FullName);
application.DrawView();
TEvent quit = new() { What = Events.evCommand };
quit.message.command = Views.cmQuit;
TEventQueue.Enqueue(quit);
return AppLifecycleGuard.Run(application);
