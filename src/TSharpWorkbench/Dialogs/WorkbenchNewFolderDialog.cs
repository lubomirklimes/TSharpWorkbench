using TSharpVision;
using TSharpVision.Constants;

// New folder for the destination of a prepared copy. The framework's directory dialog cannot create
// folders, so this focused dialog does: the parent, the name (nested names such as "tests\TVDemo" are allowed, each part
// validated; no "..", no rooted paths, no reserved or invalid names) and the full path it would create, updated as the
// user types. Create asks nothing more: the dialog itself is the question, and it creates only the folder it shows, only
// when that folder is a valid destination (outside the application, not protected, not reached through a link). Cancel and
// Esc create nothing.
internal sealed class WorkbenchNewFolderDialog : TDialog
{
    internal const string DialogTitle = "New Destination Folder";
    private readonly string parent;
    private readonly PreparationInspection inspection;
    private readonly TargetPreparationService service;
    private readonly Line creates, message;
    private string shown = "\0";
    internal TInputLine NameField { get; }
    internal string? Created { get; private set; }
    internal string MessageText => message.Current;
    internal string CreatesText => creates.Current;

    private sealed class Line(TRect bounds) : TStaticText(bounds, "")
    {
        internal string Current => Text;
        internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
        public override void GetText(out string result) => result = string.Join("\n", WorkbenchText.Wrap(Text, size.x).Take(Math.Max(1, size.y)).Select(l => WorkbenchText.Fit(l, size.x)));
    }

    internal WorkbenchNewFolderDialog(TDeskTop desktop, string parent, string suggestion, PreparationInspection inspection, TargetPreparationService service)
        : base(new(0, 0, Math.Min(96, desktop.size.x - 2), Math.Min(16, desktop.size.y)), DialogTitle)
    {
        this.parent = parent; this.inspection = inspection; this.service = service;
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        int w = size.x, h = size.y;
        Insert(new TStaticText(new(2, 1, 12, 2), "Inside"));
        var inside = new Line(new(13, 1, w - 2, 3)); Insert(inside); inside.Set(parent);
        NameField = new TInputLine(new(13, 4, w - 3, 5), 1024); Insert(NameField);
        Insert(new TLabel(new(1, 4, 12, 5), "~N~ame", NameField));
        Insert(new TStaticText(new(2, 6, 12, 7), "Creates"));
        creates = new Line(new(13, 6, w - 2, 8)); Insert(creates);
        message = new Line(new(2, 9, w - 2, h - 4)); Insert(message);
        Insert(new TButton(new(w - 30, h - 3, w - 17, h - 1), "~C~reate", Views.cmOK, ButtonConstants.bfDefault));
        Insert(new TButton(new(w - 16, h - 3, w - 3, h - 1), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        NameField.SetData(suggestion);
        Update();
        NameField.Select();
    }

    internal static string? Run(TDeskTop desktop, string parent, string suggestion, PreparationInspection inspection, TargetPreparationService service)
    {
        using var dialog = new WorkbenchNewFolderDialog(desktop, parent, suggestion, inspection, service);
        return desktop.ExecView(dialog) == Views.cmOK ? dialog.Created : null;
    }

    // The folder the name gives, or the reason it is refused (nothing is created here).
    private (string? Full, string Message) Evaluate()
    {
        string name = NameField.Data.Trim();
        if (PreparationDestination.NameProblem(name, nested: true) is { } problem) return (null, problem);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(parent, name)));
        if (!Preparation.IsUnder(full, parent) || WorkbenchPreferences.SamePath(full, parent)) return (null, "The new folder must be inside " + parent + ".");
        var state = PreparationDestination.Check(full, inspection, service, full);
        return state.Kind switch
        {
            DestinationKind.New => (full, state.Missing.Length > 1 ? $"Create creates {state.Missing.Length} nested folders; nothing exists yet." : "Nothing exists yet; Create makes this one empty folder."),
            DestinationKind.Empty or DestinationKind.ExistingCopy or DestinationKind.NotEmpty => (null, "This folder already exists. Choose another name (it is never reused silently)."),
            _ => (null, state.Message)
        };
    }

    private void Update()
    {
        if (NameField.Data == shown) return;
        shown = NameField.Data;
        var (full, text) = Evaluate();
        creates.Set(full ?? "-");
        message.Set((full is null ? "! " : "") + text);
    }

    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return base.Valid(command);
        shown = "\0"; Update();
        var (full, _) = Evaluate();
        if (full is null) return false;
        var missing = new List<string>();
        for (string? d = full; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Add(d);
        try { Directory.CreateDirectory(full); Created = full; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A failed nested creation may have made some of the missing folders; only those, and only while empty, go again.
            foreach (string d in missing)
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (Exception io) when (io is IOException or UnauthorizedAccessException) { }
            message.Set("! The folder could not be created: " + ex.Message + " Nothing was created.");
            return false;
        }
    }

    public override void HandleEvent(ref TEvent ev)
    {
        base.HandleEvent(ref ev);
        if (NameField is not null) Update();
    }
}
