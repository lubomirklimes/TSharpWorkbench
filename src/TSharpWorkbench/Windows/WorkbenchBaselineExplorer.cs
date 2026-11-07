using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;

internal sealed class WorkbenchBaselineExplorer : TWindow
{
    internal const ushort Context = 0x7b96;
    private const ushort Adopt = 0x7b97, Review = 0x7b98, Filter = 0x7b99, CloseWindow = 0x7b9a;
    internal sealed record Row(string Text, string Detail, string? Report = null, string? Key = null);
    private readonly WorkbenchDialog project;
    private readonly TListBox list;
    private readonly TInputLine query;
    private Row[] rows = [];
    internal Action? Closed { get; set; }

    internal WorkbenchBaselineExplorer(TDeskTop desktop, WorkbenchDialog project)
        : base(new(0, 0, desktop.size.x, desktop.size.y), "Baseline Explorer", 5)
    {
        this.project = project;
        flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom); helpCtx = Context;
        int w = size.x, h = size.y;
        int restoreWidth = Math.Clamp(w - 4, Math.Min(72, w), 110);
        int restoreHeight = Math.Clamp(h - 2, Math.Min(12, h), 22);
        int restoreX = (w - restoreWidth) / 2, restoreY = (h - restoreHeight) / 2;
        zoomRect = new(restoreX, restoreY, restoreX + restoreWidth, restoreY + restoreHeight);
        const byte bottom = Views.gfGrowLoY | Views.gfGrowHiY;
        const byte right = Views.gfGrowLoX | Views.gfGrowHiX;
        Insert(new TStaticText(new(3, 2, w - 3, 3), "Logical slots use recorded OS, driver, capture and grid. Profile names do not select a slot.") { growMode = Views.gfGrowHiX });
        query = new TInputLine(new(13, 4, w - 4, 5), 100) { growMode = Views.gfGrowHiX }; Insert(query);
        Insert(new WindowLabel(new(3, 4, 12, 5), "Searc~h~", query));
        var scroll = new TScrollBar(new(w - 4, 6, w - 3, h - 4)) { growMode = right | Views.gfGrowHiY }; Insert(scroll);
        list = new TListBox(new(3, 6, w - 4, h - 4), 1, scroll) { growMode = Views.gfGrowHiX | Views.gfGrowHiY }; Insert(list);
        Button(3, 17, "~A~dopt store...", Adopt);
        Button(22, 16, "Deta~i~ls", Review);
        Button(40, 17, "App~l~y filter", Filter);
        Button(w - 14, 11, "~C~lose", CloseWindow, bottom | right);
        list.Select();

        void Button(int left, int width, string text, ushort command, byte grow = bottom) =>
            Insert(new TButton(new(left, h - 3, left + width, h - 1), text, command, ButtonConstants.bfNormal) { growMode = grow });
    }

    public override void SizeLimits(ref TPoint min, ref TPoint max)
    {
        base.SizeLimits(ref min, ref max);
        min = new(Math.Min(72, max.x), Math.Min(12, max.y));
    }

    internal void Reload()
    {
        var selected = rows.ElementAtOrDefault(list.focused);
        rows = project.BaselineRows().Where(r => (r.Text + " " + r.Detail).Contains(query.Data.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var lines = new TStringCollection();
        foreach (var row in rows) lines.Insert(row.Text);
        if (rows.Length == 0) lines.Insert("No matching baseline definitions or candidates.");
        list.NewList(lines);
        int index = Array.IndexOf(rows, selected);
        if (index >= 0) list.FocusItemNum(index);
    }

    public override void HandleEvent(ref TEvent ev)
    {
        ushort command = ev.What == Events.evCommand ? ev.message.command : ev.What == Events.evKeyDown
            ? ev.keyDown.keyCode switch { Keys.kbEsc => CloseWindow, Keys.kbEnter when current == list => Review, Keys.kbEnter when current == query => Filter, _ => (ushort)0 } : (ushort)0;
        if (command is Adopt or Review or Filter or CloseWindow)
        {
            ClearEvent(ref ev);
            if (command == CloseWindow) Close();
            else if (command == Adopt) { project.AdoptBaselineStore(); Reload(); }
            else if (command == Filter) Reload();
            else if (rows.ElementAtOrDefault(list.focused) is { } row) project.OpenBaselineRow(row);
            return;
        }
        base.HandleEvent(ref ev);
    }
    public override void ShutDown() { base.ShutDown(); Closed?.Invoke(); }
}

internal sealed partial class WorkbenchDialog
{
    private WorkbenchBaselineExplorer? baselineExplorer;

    internal void ShowBaselineExplorer()
    {
        if (Native is null) { BrowseBaselines(); return; }
        if (baselineExplorer is null)
        {
            baselineExplorer = new(desktop, this);
            baselineExplorer.Closed = () => baselineExplorer = null;
            desktop.Insert(baselineExplorer);
        }
        baselineExplorer.Reload();
        baselineExplorer.Select();
    }

    internal void AdoptBaselineStore()
    {
        if (Native is not { } native || busy) { Error("Wait for the current operation before adopting a store."); return; }
        using var picker = new TFileDialog("*.json", "Adopt Baseline Store", "~M~anifest", FileDialogOptions.fdOpenButton | FileDialogOptions.fdNoLoadDir, 0);
        picker.helpCtx = WorkbenchBaselineExplorer.Context;
        Widen(picker, desktop);
        picker.SetData(Path.Combine(InitialDirectory(projectDefaults.BaselineStore ?? ProjectPath!), "*.json"));
        if (desktop.ExecView(picker) is not (Views.cmOK or Views.cmFileOpen)) return;
        picker.GetFileName(out string path);
        if (Confirm(desktop, "Adopt Baseline Store", "Copy this manifest and its approved artifacts into the project? Future test runs will compare with the project store. The source remains intact.", "~A~dopt") != Views.cmYes) return;
        AdoptBaselinePath(path);
    }

    private bool AdoptBaselinePath(string path)
    {
        if (Native is not { } native) return false;
        try
        {
            native.Session.AdoptBaselineStore(path, ValidateAdoptedStore);
            string manifest = native.Session.ResolveArtifact(NativeProject.BaselineArtifact);
            projectDefaults = projectDefaults with { BaselineStore = manifest, TestType = "visual", VisualBehavior = "compare" };
            sessionRun = sessionRun with { BaselineStore = manifest, TestType = "visual", VisualBehavior = "compare" };
            native.Store(CaptureProject());
            UpdateProjectTitle(); UpdateHeader();
            Status("Baseline store adopted. Save the project to commit it.");
            return true;
        }
        catch (Exception ex) { Error("Cannot adopt baseline store: " + ex.Message); return false; }
    }

    internal static void ValidateAdoptedStore(string path)
    {
        var store = VisualBaselineSet.Load(path);
        if (!Directory.Exists(store.ApprovedDirectory)) return;
        foreach (string file in Directory.EnumerateFiles(store.ApprovedDirectory, "*.baseline.json", SearchOption.AllDirectories))
        {
            using var input = File.OpenRead(file);
            var record = JsonSerializer.Deserialize<VisualBaselineFile>(input, ScenarioRunner.Json)
                ?? throw new InvalidDataException("Empty approved baseline record.");
            string grid = record.Grid == record.InitialGrid ? record.Grid : record.Grid + "-from-" + record.InitialGrid;
            var entry = store.Find(record.ScenarioId, record.CheckpointId, grid)
                ?? throw new InvalidDataException("Approved file has no manifest definition: " + record.Key);
            var environment = BaselineEnvironment.Of(record);
            if (!WorkbenchPreferences.SamePath(store.Slot(record.Key, environment).Metadata, file) ||
                store.LoadApproved(entry, grid, environment).Status != VisualStatuses.Match)
                throw new InvalidDataException("Invalid approved baseline: " + file);
        }
    }

    internal IEnumerable<WorkbenchBaselineExplorer.Row> BaselineRows()
    {
        var rows = new List<WorkbenchBaselineExplorer.Row>();
        if (Native is not { } native) return rows;
        if (projectDefaults.BaselineStore is { } manifest)
        {
            try
            {
                var store = VisualBaselineSet.Load(manifest);
                var identities = new Dictionary<string, HashSet<BaselineEnvironment>>(StringComparer.Ordinal);
                if (Directory.Exists(store.ApprovedDirectory))
                    foreach (string metadata in Directory.EnumerateFiles(store.ApprovedDirectory, "*.baseline.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            using var input = File.OpenRead(metadata);
                            if (JsonSerializer.Deserialize<VisualBaselineFile>(input, ScenarioRunner.Json) is { } record)
                            {
                                if (!identities.TryGetValue(record.Key, out var known)) identities[record.Key] = known = [];
                                known.Add(BaselineEnvironment.Of(record));
                            }
                        }
                        catch (Exception ex) { rows.Add(new("Invalid | " + Path.GetFileName(metadata), ex.Message)); }
                    }
                foreach (var entry in store.Manifest.Baselines)
                foreach (string grid in entry.Grids)
                foreach (var identity in (identities.GetValueOrDefault(VisualBaselineSet.Key(entry.Scenario, entry.Checkpoint, grid)) ?? []).Append(BaselineEnvironment.Current()).Distinct().OrderBy(e => e.Folder, StringComparer.Ordinal))
                {
                    var result = store.LoadApproved(entry, grid, identity);
                    string status = result.Status switch { VisualStatuses.Match => "Approved", VisualStatuses.BaselineMissing => "Missing", VisualStatuses.BaselineInvalid => "Invalid", _ => result.Status };
                    rows.Add(new($"{status} | {entry.Scenario} / {entry.Checkpoint} | {grid} | {identity}", result.Reason.Length == 0
                        ? $"Approved logical reference. SHA-256: {result.CellsSha256}. Run again for a new comparison." : result.Reason));
                }
            }
            catch (Exception ex) { rows.Add(new("Invalid baseline store", ex.Message)); }
        }
        else rows.Add(new("No baseline store. Adopt a manifest to enable logical comparisons.", "Adoption copies the source manifest and approved reference files."));

        foreach (var run in ReadMatrixHistory())
        foreach (var result in run.Results)
        {
            if (result.Report is not { } report) continue;
            string directory = Path.GetDirectoryName(native.Session.ResolveArtifact(report))!;
            if (!Directory.Exists(directory)) continue;
            foreach (string candidate in Directory.EnumerateFiles(directory, "*.candidate.json"))
            {
                try
                {
                    using var input = File.OpenRead(candidate);
                    var file = JsonSerializer.Deserialize<VisualBaselineFile>(input, ScenarioRunner.Json)
                        ?? throw new InvalidDataException("Empty candidate record.");
                    rows.Add(new($"Candidate | {file.ScenarioId} / {file.CheckpointId} | {file.Grid} | {BaselineEnvironment.Of(file)}",
                        $"Run {run.Id}, Test {result.Entry.Title}, requested profile {result.Entry.Environment.Name}", report, file.Key));
                }
                catch (Exception ex) { rows.Add(new("Invalid candidate | " + Path.GetFileName(candidate), ex.Message)); }
            }
        }
        return rows;
    }

    internal void OpenBaselineRow(WorkbenchBaselineExplorer.Row row)
    {
        if (row.Report is null || row.Key is null || Native is not { } native)
        { ShowMessage(desktop, "Baseline Details", row.Text + "\n\n" + row.Detail); return; }
        try
        {
            var loaded = WorkbenchRunHistory.Load(native.Session.ResolveArtifact(row.Report));
            var execution = loaded.Batch.Results.Single();
            var checkpoint = execution.Checkpoints.Single(c => c.Visual?.Baseline == row.Key);
            ReviewCandidate(loaded.Batch, execution, checkpoint);
        }
        catch (Exception ex) { Error("Cannot review this candidate: " + ex.Message); }
    }
}
