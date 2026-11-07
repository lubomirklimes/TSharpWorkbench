using TSharpVision.Constants;

internal sealed class WorkbenchOverview : TDialog
{
    internal const string Caption = "Project Information";
    private const int TextTop = 2, FooterHeight = 3;
    private readonly TDeskTop desktop;
    private readonly WorkbenchDialog project;
    private readonly Summary summary, hint;
    private readonly TButton close;
    private (string Label, string Value, bool IsPath)[] settings = [];
    private string[] contents = [], runs = [];
    private int firstLine;

    private sealed class Summary() : TStaticText(new(0, 0, 1, 1), "")
    {
        internal void Update(string text) { Text = text; DrawView(); }
    }

    internal WorkbenchOverview(TDeskTop desktop, WorkbenchDialog project)
        : base(new TRect(0, 0, 1, 1), Caption)
    {
        this.desktop = desktop;
        this.project = project;
        helpCtx = WorkbenchDialog.Context;
        summary = new Summary(); Insert(summary);
        hint = new Summary(); Insert(hint);
        close = new TButton(new(0, 0, 1, 2), "~C~lose", Views.cmCancel, ButtonConstants.bfDefault);
        Insert(close);
        Reload();
        close.Select();
    }

    internal void Reload()
    {
        settings = project.OverviewSettings().ToArray();
        contents = project.OverviewContents().ToArray();
        runs = project.OverviewRuns().ToArray();
        FitToDesktop();
    }

    internal void FitToDesktop()
    {
        int width = Math.Min(84, Math.Max(1, desktop.size.x - 2));
        int lineCount = SummaryLines(Math.Max(1, width - 6)).Length;
        int height = Math.Min(desktop.size.y, lineCount + TextTop + FooterHeight);
        int x = (desktop.size.x - width) / 2, y = (desktop.size.y - height) / 2;
        Locate(new(x, y, x + width, y + height));

        int footer = Math.Max(1, height - FooterHeight);
        summary.Locate(new(3, TextTop, Math.Max(4, width - 3), Math.Max(TextTop + 1, footer)));
        int closeLeft = Math.Max(1, width - 14);
        hint.Locate(new(3, footer, Math.Max(4, closeLeft - 1), footer + 1));
        close.Locate(new(closeLeft, footer, Math.Max(closeLeft + 1, width - 3), footer + 2));
        DrawSummary();
    }

    private string[] SummaryLines(int width) =>
    [
        "Project Information",
        .. settings.SelectMany(row => FormatSetting(row, width)),
        "",
        "Contents",
        .. contents.Select(line => "  " + line),
        "",
        "Recent Runs",
        .. runs.Select(line => "  " + line)
    ];

    private static IEnumerable<string> FormatSetting((string Label, string Value, bool IsPath) row, int width)
    {
        const int valueColumn = 24;
        int available = Math.Max(1, width - valueColumn);
        string prefix = "  " + WorkbenchText.Column(row.Label, 21) + " ";
        var lines = row.IsPath ? [WorkbenchText.Path(row.Value, available)] : WorkbenchText.Wrap(row.Value, available);
        foreach (string line in lines)
        {
            yield return prefix + line;
            prefix = new string(' ', valueColumn);
        }
    }

    private void DrawSummary()
    {
        int width = summary.size.x;
        string[] lines = SummaryLines(width);
        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, lines.Length - summary.size.y));
        summary.Update(string.Join('\n', lines.Skip(firstLine).Take(summary.size.y).Select(line => WorkbenchText.Fit(line, width))));
        hint.Update(WorkbenchText.Fit(lines.Length > summary.size.y ? "Ctrl+P Full paths   PgUp/PgDn Scroll" : "Ctrl+P Full paths", hint.size.x));
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown)
        {
            switch (ev.keyDown.keyCode)
            {
                case Keys.kbCtrlP:
                    ClearEvent(ref ev);
                    WorkbenchDialog.ShowMessage(desktop, "Project Paths",
                        string.Join("\n\n", settings.Where(row => row.IsPath).Select(row => $"{row.Label}\n{row.Value}")), helpCtx);
                    return;
                case Keys.kbPgUp:
                case Keys.kbPgDn:
                    firstLine += ev.keyDown.keyCode == Keys.kbPgUp ? -summary.size.y : summary.size.y;
                    ClearEvent(ref ev);
                    DrawSummary();
                    return;
                case Keys.kbF4:
                    EndModal(WorkbenchDialog.Advanced);
                    ClearEvent(ref ev);
                    return;
            }
        }
        if (ev.What == Events.evCommand && ev.message.command == WorkbenchDialog.Advanced)
        {
            EndModal(WorkbenchDialog.Advanced);
            ClearEvent(ref ev);
            return;
        }
        base.HandleEvent(ref ev);
    }
}

internal sealed partial class WorkbenchDialog
{
    internal Action? OverviewChanged { get; set; }

    internal IEnumerable<(string Label, string Value, bool IsPath)> OverviewSettings()
    {
        var settings = CaptureProject();
        yield return ("Archive", ProjectPath ?? "", true);
        yield return ("Target", settings.Target.Length == 0 ? "Choose an executable in Settings (F4)" : settings.Target, settings.Target.Length > 0);
        yield return ("Readiness", summary.State.Replace("Project > Properties", "Settings > Project...", StringComparison.Ordinal), false);
        yield return ("Run output (external)", settings.OutputRoot, true);
        string store = settings.BaselineStore is null ? "Not configured"
            : Native!.OwnedBaselineArtifact(settings.BaselineStore) is not null ? "Project-owned store" : "External: " + settings.BaselineStore;
        yield return ("Baselines", store, false);
    }

    internal IEnumerable<string> OverviewContents()
    {
        var session = Native!.Session;
        var profiles = session.Environments;
        int enabled = profiles.Count(profile => profile.Enabled);
        yield return $"{"Scenario references",-21} {session.Scenarios.Count,-9} {"Tests",-18} {session.Tests.Count}";
        yield return $"{"Runnable scenarios",-21} {catalog.Length,-9} {"Attachment issues",-18} {AttachedScenarios.Count(s => s.Problem is not null)}";
        yield return $"{"Environments",-21} {profiles.Count} total  |  {enabled} enabled  |  {profiles.Count - enabled} disabled";
    }

    internal IEnumerable<string> OverviewRuns()
    {
        var archived = Native!.Session.ListArtifacts("runs/").ToHashSet(StringComparer.Ordinal);
        var runs = ReadMatrixHistory().Where(run => archived.Contains($"runs/{run.Id}/matrix.json")).Take(3).ToArray();
        if (runs.Length == 0)
            yield return "No archived project runs.";
        foreach (var run in runs)
        {
            string outcomes = WorkbenchMatrix.Summary(run.Results);
            yield return $"{run.Began.ToLocalTime():yyyy-MM-dd HH:mm}   {(outcomes.Length == 0 ? "No selected executions" : outcomes)}";
        }
    }
}
