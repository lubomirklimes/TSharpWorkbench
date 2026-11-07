using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.SampleTarget;

internal static class SampleCommands
{
    internal const ushort NewNote = 400, ShowDialog = 401, About = 402, Print = 403, Tick = 404;
}

internal static class SampleHelp
{
    internal const ushort NoteWindow = 1000;
}

/// <summary>
/// A small, ordinary TSharpVision application: a menu bar with a nested submenu, a context-sensitive
/// status line, modeless note windows and a modal dialog. It performs no file, network or device I/O.
/// </summary>
internal sealed class SampleApp : TApplication
{
    private ushort nextNote = 1;

    /// <summary>Commands processed by the queued-input burst scenario.</summary>
    internal int Ticks { get; private set; }

    internal SampleApp()
    {
        // A permanently unavailable command (also refused by the status line and menu hot keys).
        DisableCommand(SampleCommands.Print);
    }

    /// <summary>Application-level tap: every loop (including modal dialogs and menus) reads through here, so ticks from the
    /// burst self-test are counted no matter which view owns dispatch, and never reach a modal view.</summary>
    public override void GetEvent(ref TEvent ev)
    {
        base.GetEvent(ref ev);
        if (ev.What == Events.evCommand && ev.message.command == SampleCommands.Tick) { Ticks++; ev.What = Events.evNothing; }
        InputLog?.Record(ev);
    }

    /// <summary>Self-tests only: every device event any loop read, as the application received it. Null otherwise.</summary>
    internal InputLog? InputLog { get; set; }

    public override TMenuBar InitMenuBar(TRect r)
    {
        r.b.y = r.a.y + 1;
        return new TMenuBar(r,
            new TSubMenu("~F~ile", Keys.kbAltF) +
                new TMenuItem("~N~ew note", SampleCommands.NewNote, Keys.kbF4, Views.hcNoContext, "F4") +
                new TMenuItem("~D~ialog...", SampleCommands.ShowDialog, Keys.kbF3, Views.hcNoContext, "F3") +
                // Greyed by the command set alone: DisableCommand in the constructor, then cmCommandSetChanged.
                new TMenuItem("~P~rint", SampleCommands.Print, Keys.kbNoKey) +
                TMenuItem.NewLine() +
                new TMenuItem("E~x~it", Views.cmQuit, Keys.kbAltX, Views.hcNoContext, "Alt+X") +
            new TSubMenu("~W~indow", Keys.kbAltW) +
                new TMenuItem("~A~rrange", Keys.kbNoKey, new TMenu(
                    new TMenuItem("~T~ile", Views.cmTile, Keys.kbNoKey, Views.hcNoContext, null,
                    new TMenuItem("~C~ascade", Views.cmCascade, Keys.kbNoKey)))) +
                new TMenuItem("~N~ext", Views.cmNext, Keys.kbF6, Views.hcNoContext, "F6") +
                new TMenuItem("~C~lose", Views.cmClose, Keys.kbAltF3, Views.hcNoContext, "Alt+F3") +
            new TSubMenu("~H~elp", Keys.kbAltH) +
                new TMenuItem("~A~bout...", SampleCommands.About, Keys.kbNoKey));
    }

    public override TStatusLine InitStatusLine(TRect r)
    {
        r.a.y = r.b.y - 1;
        var note = new TStatusDef(SampleHelp.NoteWindow, SampleHelp.NoteWindow,
            new TStatusItem("~Alt+F3~ Close note", Keys.kbAltF3, Views.cmClose,
            new TStatusItem("~F6~ Next", Keys.kbF6, Views.cmNext,
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu))));
        var desktop = new TStatusDef(0, 0xFFFF,
            new TStatusItem("~Alt+X~ Exit", Keys.kbAltX, Views.cmQuit,
            new TStatusItem("~F4~ New note", Keys.kbF4, SampleCommands.NewNote,
            new TStatusItem("~F3~ Dialog", Keys.kbF3, SampleCommands.ShowDialog,
            new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu)))));
        // The first matching range wins, so the narrow note context precedes the catch-all.
        note.Next = desktop;
        return new SampleStatusLine(r, note);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        base.HandleEvent(ref ev);
        if (ev.What != Events.evCommand) return;
        switch (ev.message.command)
        {
            case SampleCommands.NewNote: OpenNote(); ClearEvent(ref ev); break;
            case SampleCommands.ShowDialog: ShowDialog(); ClearEvent(ref ev); break;
            case SampleCommands.About:
                MsgBox.MessageBox(DeskTop, "TSharpVision TestWorkbench sample target.", MsgBox.mfInformation | MsgBox.mfOKButton);
                ClearEvent(ref ev); break;
            case Views.cmTile: DeskTop?.Tile(DeskTop.GetExtent()); ClearEvent(ref ev); break;
            case Views.cmCascade: DeskTop?.Cascade(DeskTop.GetExtent()); ClearEvent(ref ev); break;
        }
    }

    /// <summary>The open sample dialog's input line and Cancel button (named mouse points).</summary>
    internal TInputLine? DialogInput { get; private set; }
    internal TButton? DialogCancel { get; private set; }

    internal int NoteCount
    {
        get { int count = 0; DeskTop?.ForEachView(v => { if (v is TWindow) count++; }); return count; }
    }

    private void OpenNote()
    {
        int n = nextNote++;
        var window = new TWindow(new TRect(2 + n * 2, 1 + n, 44 + n * 2, 11 + n), $"Note {n}", (ushort)n);
        window.Insert(new TStaticText(new TRect(2, 1, 40, 8),
            $"Note window {n}.\n\nThe status line shows the note context\nwhile this window has focus."));
        window.helpCtx = SampleHelp.NoteWindow;
        // Default relative growth: shrunk from 140x40 to 60x20 the frame becomes too narrow for a title.
        ViewTreeInspector.Identify(window, $"note-{n}");
        DeskTop!.Insert(window);
    }

    private void ShowDialog()
    {
        var dialog = new TDialog(new TRect(0, 0, 52, 11), "Sample dialog");
        dialog.options |= Views.ofCentered;
        ViewTreeInspector.Identify(dialog, "sample-dialog");
        dialog.Insert(new TStaticText(new TRect(3, 2, 49, 3), "A real modal TDialog from the sample target."));
        var input = new TInputLine(new TRect(15, 4, 48, 5), 64);
        dialog.Insert(input);
        dialog.Insert(new TLabel(new TRect(2, 4, 14, 5), "~N~ame:", input));
        var ok = new TButton(new TRect(13, 7, 25, 9), "~O~K", Views.cmOK, ButtonConstants.bfDefault);
        var cancel = new TButton(new TRect(27, 7, 39, 9), "Cancel", Views.cmCancel, ButtonConstants.bfNormal);
        dialog.Insert(ok); dialog.Insert(cancel);
        ViewTreeInspector.Identify(input, "name-input");
        ViewTreeInspector.Identify(ok, "ok-button");
        ViewTreeInspector.Identify(cancel, "cancel-button");
        input.SetData("TestWorkbench");
        input.Select();
        DialogInput = input; DialogCancel = cancel;
        if (ValidView(dialog) is not null) DeskTop!.ExecView(dialog);
    }
}

internal sealed class SampleStatusLine(TRect bounds, TStatusDef defs) : TStatusLine(bounds, defs)
{
    public override string Hint(ushort aHelpCtx) => aHelpCtx == SampleHelp.NoteWindow ? "Note context" : "Desktop context";
}
