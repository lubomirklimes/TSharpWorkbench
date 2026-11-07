using TSharpVision;
using TSharpVision.Constants;

// Reuse the framework directory tree without its process-current-directory side effect.
internal sealed class WorkbenchDirectoryDialog : TChDirDialog
{
    internal string SelectedPath { get; private set; } = "";
    internal WorkbenchDirectoryDialog(string initial, string caption = "Select output directory") : base(ChDirDialogOptions.cdNoLoadDir, 0)
    {
        title = caption;
        dirInput!.MaxLen = 32767;
        dirList!.NewDirectory(Path.TrimEndingDirectorySeparator(initial) + Path.DirectorySeparatorChar);
        dirInput.SetData(initial);
    }

    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return true;
        try
        {
            string path = Path.GetFullPath(dirInput!.Data);
            // Verify access by enumeration; browsing never creates or deletes anything.
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            SelectedPath = path;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            if (owner is not null) MsgBox.MessageBox(owner, "Cannot select directory: " + LastError, MsgBox.mfError | MsgBox.mfOKButton);
            return false;
        }
    }
}

