using TSharpVision.Constants;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>
/// One real driver output path driven step by step. Implementations call the driver's own output
/// methods and observe the result at the last deterministic point before native presentation.
/// </summary>
public interface IOutputPath : IDisposable
{
    /// <summary>Stable artifact id, e.g. <c>console-win32</c>.</summary>
    string Id { get; }
    /// <summary>The driver class whose code runs.</summary>
    string Driver { get; }
    /// <summary>Where the output is observed.</summary>
    string Boundary { get; }
    PathCapabilities Capabilities { get; }
    /// <summary>Null when usable, otherwise why the path cannot run here.</summary>
    string? Unavailable { get; }

    void Begin(GridSize grid);
    /// <summary>Resizes (if the step says so), writes the row segments and sets the caret.</summary>
    void Apply(FixtureStep step, StepCapture capture);
    /// <summary>Flushes/renders and fills <paramref name="capture"/> with what the driver produced.</summary>
    void Observe(StepCapture capture);
}

/// <summary>Helpers shared by the output paths.</summary>
internal static class PathSupport
{
    /// <summary>Drains <see cref="TEventQueue"/> and reports whether a cmScreenResized command was queued.</summary>
    public static bool DrainResizeCommand()
    {
        TEventQueue.Resume();
        int saved = TEventQueue.DoubleDelay;
        bool resized = false;
        TEvent ev = default;
        for (int i = 0; i < 1000; i++)
        {
            TEventQueue.GetNextEvent(ref ev);
            if (ev.What == Events.evNothing) break;
            if (ev.What == Events.evCommand && ev.message.command == Views.cmScreenResized) resized = true;
        }
        TEventQueue.DoubleDelay = saved;
        return resized;
    }

    /// <summary>Drains all queued events (mouse and commands) in order.</summary>
    public static List<TEvent> DrainQueue()
    {
        TEventQueue.Resume();
        var events = new List<TEvent>();
        TEvent ev = default;
        for (int i = 0; i < 1000; i++)
        {
            TEventQueue.GetNextEvent(ref ev);
            if (ev.What == Events.evNothing) break;
            events.Add(ev);
        }
        return events;
    }

    public static string Hex(int index) => Vga.Describe(index);
}
