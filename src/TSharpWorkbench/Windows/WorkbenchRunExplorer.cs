using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;

internal sealed class WorkbenchRunExplorer : TWindow
{
    private const ushort Configure = 0x7b90, CancelRun = 0x7b91, Details = 0x7b92, CloseExplorer = 0x7b93, Previous = 0x7ba0, NextRun = 0x7ba1, Rerun = 0x7ba2, ShowScenario = 0x7ba3;
    internal const ushort Context = 0x7b94;
    private readonly WorkbenchDialog project;
    private readonly TListBox list;
    private readonly Caption caption;
    private readonly TButton cancelButton;
    private (WorkbenchMatrix.Run Run, WorkbenchMatrix.Result Result)[] entries = [];
    internal Action? Closed { get; set; }

    private sealed class Caption(TRect bounds) : TStaticText(bounds, "")
    {
        internal void Set(string value) { Text = value; DrawView(); }
    }

    internal WorkbenchRunExplorer(TDeskTop desktop, WorkbenchDialog project)
        : base(new(0, 0, desktop.size.x, desktop.size.y), "Run Explorer", 3)
    {
        this.project = project;
        helpCtx = Context;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
        int w = size.x, h = size.y;
        int restoreWidth = Math.Clamp(w - 4, Math.Min(72, w), 108);
        int restoreHeight = Math.Clamp(h - 2, Math.Min(14, h), 22);
        int restoreX = (w - restoreWidth) / 2, restoreY = (h - restoreHeight) / 2;
        zoomRect = new(restoreX, restoreY, restoreX + restoreWidth, restoreY + restoreHeight);
        const byte bottom = Views.gfGrowLoY | Views.gfGrowHiY;
        const byte right = Views.gfGrowLoX | Views.gfGrowHiX;
        caption = new(new(3, 2, w - 3, 3)) { growMode = Views.gfGrowHiX }; Insert(caption);
        Insert(new TStaticText(new(3, 4, w - 3, 5), "Started     Run      Overall       Functional Visual        Test / Environment")
            { growMode = Views.gfGrowHiX });
        var scroll = new TScrollBar(new(w - 4, 5, w - 3, h - 6)) { growMode = right | Views.gfGrowHiY }; Insert(scroll);
        list = new TListBox(new(3, 5, w - 4, h - 6), 1, scroll) { growMode = Views.gfGrowHiX | Views.gfGrowHiY }; Insert(list);
        Button(3, 17, "~N~ew run...", Configure);
        cancelButton = Button(22, 16, "Cancel r~u~n", CancelRun);
        Button(40, 15, "Deta~i~ls", Details);
        Button(w - 14, 11, "~C~lose", CloseExplorer, bottom | right);
        Button(3, 14, "~O~pen run...", WorkbenchDialog.OpenRun, row: 5);
        Button(18, 11, "~P~revious", Previous, row: 5);
        Button(30, 8, "Ne~x~t", NextRun, row: 5);
        Button(39, 12, "R~e~-run...", Rerun, row: 5);
        Button(52, 19, "Show in Scen~a~rios", ShowScenario, row: 5);
        list.Select();

        TButton Button(int x, int width, string text, ushort command, byte grow = bottom, int row = 3)
        {
            var button = new TButton(new(x, h - row, x + width, h - row + 2), text, command, ButtonConstants.bfNormal) { growMode = grow };
            Insert(button);
            return button;
        }
    }

    public override void SizeLimits(ref TPoint min, ref TPoint max)
    {
        base.SizeLimits(ref min, ref max);
        min = new(Math.Min(72, max.x), Math.Min(14, max.y));
    }

    internal void Reload(IEnumerable<WorkbenchMatrix.Run> runs, string progress)
    {
        if (project.MatrixCancellable) EnableCommand(CancelRun); else DisableCommand(CancelRun);
        cancelButton.SetState(Views.sfDisabled, !project.MatrixCancellable);
        cancelButton.DrawView();
        if (current == cancelButton && !project.MatrixCancellable) list.Select();
        var selected = entries.ElementAtOrDefault(list.focused);
        entries = runs.SelectMany(run => run.Results.Select(result => (run, result))).ToArray();
        var rows = new TStringCollection();
        foreach (var (run, result) in entries)
            rows.Insert($"{run.Began.ToLocalTime():MM-dd HH:mm} {run.Id[..Math.Min(8, run.Id.Length)],-8} {result.Overall,-13} {WorkbenchText.Column(result.Functional ?? result.Outcome, 10)} {WorkbenchText.Column(result.Visual ?? "Not recorded", 13)} {result.Entry.Title} / {result.Entry.Environment.Name}");
        list.NewList(rows);
        int index = Array.IndexOf(entries, selected);
        if (index >= 0) list.FocusItemNum(index);
        caption.Set(progress.Length == 0 ? "Choose New run to configure marked Tests. Select an execution for details." : progress);
    }

    internal void FocusRun(string id)
    {
        int index = Array.FindIndex(entries, entry => entry.Run.Id == id);
        if (index >= 0) { list.FocusItemNum(index); list.Select(); }
    }

    private void Navigate(int direction)
    {
        if (entries.ElementAtOrDefault(list.focused).Run is not { } selected)
        {
            project.Post(direction > 0 ? WorkbenchDialog.EarlierRun : WorkbenchDialog.LaterRun);
            return;
        }
        for (int index = list.focused + direction; index >= 0 && index < entries.Length; index += direction)
        {
            if (entries[index].Run.Id == selected.Id) continue;
            FocusRun(entries[index].Run.Id);
            return;
        }
        project.Status(direction > 0 ? "No previous project run." : "No next project run.");
    }

    public override void HandleEvent(ref TEvent ev)
    {
        ushort command = ev.What == Events.evCommand ? ev.message.command : ev.What == Events.evKeyDown
            ? ev.keyDown.keyCode switch { Keys.kbEsc => CloseExplorer, Keys.kbEnter when current == list => Details, Keys.kbCtrlLeft => Previous, Keys.kbCtrlRight => NextRun, _ => (ushort)0 } : (ushort)0;
        if (command is Configure or CancelRun or Details or CloseExplorer or Previous or NextRun or Rerun or ShowScenario or WorkbenchDialog.OpenRun)
        {
            ClearEvent(ref ev);
            switch (command)
            {
                case Configure: project.ConfigureMatrix(); break;
                case CancelRun: if (project.MatrixCancellable) project.Post(WorkbenchDialog.CancelRun); break;
                case CloseExplorer: Close(); break;
                case WorkbenchDialog.OpenRun: project.Post(WorkbenchDialog.OpenRun); break;
                case Previous: Navigate(1); break;
                case NextRun: Navigate(-1); break;
                case Rerun:
                case ShowScenario:
                    project.UseRunExplorerSelection(entries.ElementAtOrDefault(list.focused).Result, command == Rerun);
                    break;
                case Details:
                    if (entries.ElementAtOrDefault(list.focused).Result is { } result) project.InspectMatrixResult(result);
                    break;
            }
            return;
        }
        base.HandleEvent(ref ev);
    }

    public override void ShutDown() { EnableCommand(CancelRun); base.ShutDown(); Closed?.Invoke(); }
}

internal sealed partial class WorkbenchDialog
{
    internal HashSet<string> MarkedTests { get; } = new(StringComparer.Ordinal);
    private WorkbenchRunExplorer? matrixWindow;
    private string matrixProgress = "";
    private WorkbenchMatrix.Run? activeMatrix;
    internal WorkbenchMatrix.Run? ActiveMatrix => activeMatrix;
    internal string MatrixProgress => matrixProgress;
    internal bool MatrixCancellable => activeMatrix is not null && Busy && !cancelRequested;
    private readonly List<WorkbenchMatrix.Run> matrixHistory = [];

    internal void ShowRunExplorer()
    {
        if (Native is null) return;
        if (matrixWindow is null)
        {
            matrixWindow = new(desktop, this);
            matrixWindow.Closed = () => matrixWindow = null;
            desktop.Insert(matrixWindow);
        }
        ReloadRunExplorer();
        matrixWindow.Select();
    }

    internal void UseRunExplorerSelection(WorkbenchMatrix.Result? result, bool rerun) => Guard(() =>
    {
        if (result is null) throw new ArgumentException("Select an execution in Run Explorer first.");
        if (rerun) ConfigureMatrix(result.Entry.TestId, result.Entry.Environment.Id);
        else if (CompiledForTarget?.Any(s => s.Id == result.Entry.ScenarioId && !s.IsFixtureOnly && s.External is null) == true &&
            !attached.Any(item => item.Info.Id == result.Entry.ScenarioId))
            ShowScenarios("compiled/" + result.Entry.ScenarioId);
        else ShowScenarioDocument(result.Entry.ScenarioId);
    });

    private void ShowRunHistory()
    {
        if (Native is null) { ShowResults(); return; }
        ShowRunExplorer();
        if (ReadMatrixHistory().FirstOrDefault(run => run.Results.Length > 0) is { } latest)
            matrixWindow?.FocusRun(latest.Id);
        else Status("No completed project runs.");
    }

    private void ReloadRunExplorer()
    {
        if (matrixWindow is not null) matrixWindow.Reload(activeMatrix is null ? ReadMatrixHistory() : new[] { activeMatrix }.Concat(ReadMatrixHistory()), matrixProgress);
    }

    internal void ConfigureMatrix(string? testId = null, string? environmentId = null)
    {
        if (Native is not { } native) return;
        if (busy) { Error("Wait for the active operation to finish before configuring another run."); return; }
        var tests = native.Session.Tests.Where(t => testId is null ? MarkedTests.Contains(t.Id) : t.Id == testId).ToArray();
        var entries = WorkbenchMatrix.Plan(tests, native.Session.Environments);
        if (entries.Length == 0)
        { Error("Mark Tests with Space in Test Explorer. At least one marked Test must have an enabled Environment."); return; }
        var project = CaptureProject();
        if (BuildIdentity(project.Target) is not { } build)
        { Error("Configure a readable target in Project Properties before running."); return; }
        int w = Math.Min(106, desktop.size.x - 2), h = Math.Min(23, desktop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), "Configure Test Run");
        dialog.options |= Views.ofCentered;
        dialog.helpCtx = WorkbenchRunExplorer.Context;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, 4),
            "Space toggles a combination. Selected foreign OS profiles are recorded as skipped."));
        var scroll = new TScrollBar(new(w - 4, 4, w - 3, h - 4)); dialog.Insert(scroll);
        var chosen = entries.Where(e => environmentId is not null ? e.Environment.Id == environmentId : e.Environment.OperatingSystem is "host" ||
            e.Environment.OperatingSystem.Equals(WorkbenchMatrix.LocalOs, StringComparison.OrdinalIgnoreCase)).Select(Key).ToHashSet(StringComparer.Ordinal);
        var list = new WorkbenchChoiceList<WorkbenchMatrix.Entry>(new(3, 4, w - 4, h - 4), scroll, entries,
            chosen, Key, e => $"{e.Title} / {e.Environment.Name} | {e.Environment.OperatingSystem} {e.Environment.Driver} {e.Environment.Width}x{e.Environment.Height}");
        dialog.Insert(list); list.SetRange(entries.Length);
        dialog.Insert(new TButton(new(w - 30, h - 3, w - 17, h - 1), "~S~tart", Views.cmOK, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 15, h - 3, w - 3, h - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfDefault);
        dialog.Insert(cancel); list.Select();
        if (desktop.ExecView(dialog) != Views.cmOK) return;
        var selected = entries.Where(e => chosen.Contains(Key(e))).ToArray();
        if (selected.Length == 0) { Status("No combinations selected."); return; }
        var ids = selected.Select(e => e.ScenarioId).ToHashSet(StringComparer.Ordinal);
        var descriptors = WorkbenchPreferences.SamePath(catalogTarget, project.Target) && build == catalogBuild
            ? catalog.Where(s => ids.Contains(s.Id)).ToArray() : [];
        if (ScenarioFiles.StaleRun(descriptors, projectDefaults.ScenarioFiles, catalogStamps) is { } stale)
        { Error(stale); return; }
        WorkbenchRunRequest request;
        try { request = new(project, descriptors, build); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); return; }
        StartMatrix(native.Session, request, entries, chosen);
        static string Key(WorkbenchMatrix.Entry e) => e.TestId + "\n" + e.Environment.Id;
    }

    private void StartMatrix(ProjectSession session, WorkbenchRunRequest request, WorkbenchMatrix.Entry[] entries, HashSet<string> chosen)
    {
        var snapshots = request.Scenarios.ToDictionary(s => s.Id);
        entries = entries.Select(e => e with { ScenarioSnapshot = snapshots.GetValueOrDefault(e.ScenarioId) }).ToArray();
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset began = DateTimeOffset.UtcNow;
        activeRequest = request;
        runState = RunState.Starting;
        cancelRequested = false;
        completed = cancelled = passed = failed = blocked = skipped = 0;
        runStarted = began; currentExecution = ""; activeRunDirectory = ""; resultIndex = -1;
        activeMatrix = new(id, began, began, []);
        total = chosen.Count;
        matrixProgress = "Starting test run...";
        ShowRunExplorer();
        StartWork(async token =>
        {
            var results = new List<WorkbenchMatrix.Result>();
            int index = 0;
            foreach (var entry in entries)
            {
                if (!chosen.Contains(entry.TestId + "\n" + entry.Environment.Id))
                {
                    results.Add(new(entry, "Not selected", "Excluded in Configure Test Run."));
                    continue;
                }
                int execution = ++index;
                Deliver(() => { if (!cancelRequested) runState = RunState.Running; currentExecution = $"{entry.Title} / {entry.Environment.Name}"; matrixProgress = $"{execution}/{chosen.Count}  {currentExecution}"; ReloadRunExplorer(); });
                var result = await WorkbenchMatrix.Execute(entry, request, runner,
                    message => Deliver(() => Status(message)),
                    batch => ArchiveExecution(session, id, execution, batch, request), token);
                results.Add(result);
                var snapshot = results.ToArray();
                Deliver(() => {
                    completed++;
                    switch (result.Overall) { case "Passed": passed++; break; case "Skipped": skipped++; break; case "Aborted": cancelled++; break; case "Blocked": blocked++; break; default: failed++; break; }
                    activeMatrix = new(id, began, DateTimeOffset.UtcNow, snapshot);
                    ReloadRunExplorer(); resultsWindow?.Reload();
                });
            }
            var run = new WorkbenchMatrix.Run(id, began, DateTimeOffset.UtcNow, results.ToArray());
            string? failure = null;
            try
            {
                using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(run, ScenarioRunner.Json));
                session.PutArtifact($"runs/{id}/matrix.json", json);
            }
            catch (Exception ex) { failure = ex.Message; }
            Deliver(() =>
            {
                activeMatrix = null;
                runState = failure is not null ? RunState.EndedUnexpectedly : cancelRequested || run.Results.Any(r => r.Outcome == "Aborted") ? RunState.Cancelled : RunState.Finished;
                if (failure is not null) matrixHistory.Add(run);
                foreach (var result in run.Results)
                {
                    if (result.Report is not { } report) continue;
                    try { resultIndex = Remember(WorkbenchRunHistory.Load(session.ResolveArtifact(report))); }
                    catch (Exception ex) { notifications.Warning("Cannot display archived execution: " + ex.Message); }
                }
                resultsWindow?.Reload();
                matrixProgress = failure is null ? (cancelRequested || run.Results.Any(r => r.Outcome == "Aborted") ? "Run cancelled. " : "Run completed. ") + WorkbenchMatrix.Summary(run.Results) + ". Save commits history and evidence." : "Run index could not be stored: " + failure;
                ReloadRunExplorer();
                Status(matrixProgress);
            });
        });
    }

    private string ArchiveExecution(ProjectSession session, string id, int index, BatchResult batch, WorkbenchRunRequest request)
    {
        string prefix = $"runs/{id}/execution-{index:D4}";
        if (request.BaselineManifestHash is { } manifestHash &&
            Native?.OwnedBaselineArtifact(request.Project.BaselineStore) is not null)
            File.WriteAllText(Path.Combine(batch.RunRoot, "workbench-request.json"),
                JsonSerializer.Serialize(new { baselineStore = NativeProject.BaselineArtifact, baselineManifestHash = manifestHash }, ScenarioRunner.Json));
        session.PutRunEvidence(prefix, batch.RunRoot);
        _ = WorkbenchRunHistory.Load(session.ResolveArtifact(prefix + "/run.json"));
        return prefix + "/run.json";
    }

    private IEnumerable<WorkbenchMatrix.Run> ReadMatrixHistory()
    {
        if (Native is not { } native) return [];
        var runs = new List<WorkbenchMatrix.Run>(matrixHistory);
        foreach (string artifact in native.Session.ListArtifacts("runs/").Where(p => p.EndsWith("/matrix.json", StringComparison.Ordinal)))
        {
            try
            {
                string path = native.Session.ResolveArtifact(artifact);
                if (new FileInfo(path).Length > 8 * 1024 * 1024) continue;
                using var input = File.OpenRead(path);
                if (JsonSerializer.Deserialize<WorkbenchMatrix.Run>(input, ScenarioRunner.Json) is not { Results: not null } run ||
                    run.Results.Length > 10000 || !Guid.TryParseExact(run.Id, "N", out _) ||
                    artifact != $"runs/{run.Id}/matrix.json" || run.Ended < run.Began ||
                    run.Results.Any(r => r?.Entry?.Environment is null ||
                        r.Report is { } report && (!report.StartsWith($"runs/{run.Id}/execution-", StringComparison.Ordinal) ||
                            !report.EndsWith("/run.json", StringComparison.Ordinal))))
                    throw new InvalidDataException("Invalid run index.");
                runs.Add(run);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { notifications.Warning("Cannot read archived run index: " + ex.Message); }
        }
        return runs.OrderByDescending(r => r.Began).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }

    internal void InspectMatrixResult(WorkbenchMatrix.Result result)
    {
        var e = result.Entry.Environment;
        int w = Math.Min(100, desktop.size.x - 2), h = Math.Min(17, desktop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), "Execution Details");
        dialog.options |= Views.ofCentered;
        dialog.helpCtx = WorkbenchRunExplorer.Context;
        string text = $"{result.Entry.Title}  |  Overall: {result.Overall}\nTest: {result.Entry.TestId} | Functional: {result.Functional ?? result.Outcome} | Visual: {result.Visual ?? "Not recorded"}\nScenario: {result.Entry.ScenarioId}\nRequested: {e.Name} | {e.OperatingSystem}, {e.Driver}, {e.Capture}, {e.Width}x{e.Height}\nRecorded: {result.ActualOs ?? "unknown"}, {result.ActualDriver ?? "unknown"}, {result.ActualCapture ?? "unknown"}, {result.ActualGrid ?? "unknown"}\nInitial grid: {result.InitialGrid ?? "unknown"}\n\n{result.Reason}";
        var scroll = new TScrollBar(new(w - 4, 2, w - 3, h - 4)); dialog.Insert(scroll);
        var list = new TListBox(new(3, 2, w - 4, h - 4), 1, scroll); dialog.Insert(list);
        var rows = new TStringCollection();
        foreach (string line in text.Split('\n').SelectMany(x => WorkbenchText.Wrap(x, w - 8))) rows.Insert(line);
        list.NewList(rows);
        if (result.Report is not null)
            dialog.Insert(new TButton(new(3, h - 3, 23, h - 1), "~O~pen results", Views.cmYes, ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(w - 14, h - 3, w - 3, h - 1), "~C~lose", Views.cmCancel, ButtonConstants.bfDefault));
        list.Select();
        ushort choice = desktop.ExecView(dialog);
        if (choice == Views.cmYes && result.Report is { } report && Native is { } native)
        {
            try { SelectRun(Remember(WorkbenchRunHistory.Load(native.Session.ResolveArtifact(report)))); }
            catch (Exception ex) { Error("Cannot open archived results: " + ex.Message); }
        }
    }
}
