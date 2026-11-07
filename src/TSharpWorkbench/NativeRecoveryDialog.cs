using TSharpVision.Constants;
using TSharpWorkbench.Storage;

internal static class NativeRecoveryDialog
{
    internal const ushort Context = 0x7b8f;
    internal static RecoveryChoice Choose(TDeskTop desktop)
    {
        int w = Math.Min(80, desktop.size.x - 2);
        using var dialog = new TDialog(new(0, 0, w, 12), "Recover Project") { helpCtx = Context };
        dialog.options |= Views.ofCentered;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, 7),
            "A previous session left working files.\nRecover restores those changes.\nDiscard opens the saved archive without those changes.\nCancel leaves the working files for later and does not open the project."));
        dialog.Insert(new TButton(new(3, 9, 19, 11), "~R~ecover", Views.cmYes, ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(21, 9, 37, 11), "~D~iscard", Views.cmNo, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 19, 9, w - 3, 11), "~C~ancel", Views.cmCancel, ButtonConstants.bfDefault);
        dialog.Insert(cancel); cancel.Select();
        return desktop.ExecView(dialog) switch { Views.cmYes => RecoveryChoice.Recover, Views.cmNo => RecoveryChoice.Discard, _ => RecoveryChoice.Cancel };
    }
}
