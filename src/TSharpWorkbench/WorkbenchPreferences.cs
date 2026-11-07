using System.Text.Json;

// Workbench settings only. No target state, fixture directories, or IPC credentials.
// The original fields keep `tui --target` sessions compatible; the fields are additive user view state
// (recent projects, per-project search/group/collapse, window geometry) and never mark a project dirty.
internal sealed record WorkbenchPreferences(string Target, string Output, string? Group, string Size)
{
    internal const int MaxRecentProjects = 8;
    internal string? StoragePath { get; init; }
    public string[] SelectedIds { get; init; } = [];
    public string Search { get; init; } = "";
    public string[] RecentProjects { get; init; } = [];
    public Dictionary<string, WorkbenchViewState> ViewStates { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int[]> Windows { get; init; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> LaunchOptions { get; init; } = [];
    // The Run Configuration presentation the user last chose ("basic" or "advanced"); a user preference, never
    // project data. Missing (new users and settings files written before the redesign) means the product default, Basic.
    public string? RunView { get; init; }
    internal const string BasicView = "basic", AdvancedView = "advanced";
    // Where Prepare target stores prepared copies (missing = the per-user data folder, see PreparationStore).
    public string? PreparedTargetsRoot { get; init; }
    // The destination folder last used for a prepared copy, per original executable (additive; missing in
    // older settings files). A folder that holds that copy proposes a free sibling next time.
    public Dictionary<string, string> PrepareDestinations { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    // The recorder's Smart capture choice (picks, proposals, assertion suggestions). Missing/null = OFF (product default).
    // The environment variable TSW_SMART_RECORDER (1/0) overrides it; see WorkbenchRecorder.SmartCapture.
    public bool? SmartRecorder { get; init; }
    // Test seam: suites written for the dialog pin the Advanced presentation for settings without a choice.
    internal static string? DefaultRunViewOverride { get; set; }
    internal static string ResolveRunView(string? stored, string? fallback = null) =>
        stored is BasicView or AdvancedView ? stored : fallback is BasicView or AdvancedView ? fallback : BasicView;
    internal string EffectiveRunView => ResolveRunView(RunView, DefaultRunViewOverride);
    internal static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TSharpVision", "TestWorkbench", "settings.json");
    internal static WorkbenchPreferences Load(string? path = null)
    {
        try { return Normalize(JsonSerializer.Deserialize<WorkbenchPreferences>(File.ReadAllText(path ?? SettingsPath)) ?? Defaults); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Defaults; }
    }
    // Older settings files have no fields; JSON null is treated the same as a missing field.
    private static WorkbenchPreferences Normalize(WorkbenchPreferences p) => p with
    {
        SelectedIds = p.SelectedIds ?? [], Search = p.Search ?? "", RecentProjects = p.RecentProjects ?? [],
        ViewStates = new(p.ViewStates ?? [], StringComparer.OrdinalIgnoreCase), Windows = new(p.Windows ?? [], StringComparer.Ordinal),
        PrepareDestinations = new(p.PrepareDestinations ?? [], StringComparer.OrdinalIgnoreCase)
    };
    private static WorkbenchPreferences Defaults => new("", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TSharpVision", "TestWorkbench", "runs"), null, "80x25");
    internal static WorkbenchPreferences Resolve(Dictionary<string, string> options, string? path = null)
    {
        var saved = Load(path);
        string size = options.GetValueOrDefault("--size", saved.Size);
        ScenarioSelectionCheck(ref size, options.ContainsKey("--size"));
        string target = options.GetValueOrDefault("--target", saved.Target);
        bool same = string.Equals(target, saved.Target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        WorkbenchCapabilities.Validate(options.GetValueOrDefault("--driver", "NullDriver"));
        return new(target, options.GetValueOrDefault("--output", saved.Output), options.TryGetValue("--group", out var group) ? group : same ? saved.Group : null, size)
        { StoragePath = path, SelectedIds = options.TryGetValue("--scenario", out var ids) ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries) : same ? saved.SelectedIds : [],
            Search = same ? saved.Search : "", LaunchOptions = options };
    }
    private static void ScenarioSelectionCheck(ref string size, bool explicitSize)
    {
        if (size is "both" or "standard") return;
        try { TSharpWorkbench.Testing.ScenarioSelection.ParseSizes(size); }
        catch (ArgumentException) when (!explicitSize) { size = "80x25"; }
    }
    internal void Save(string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // Read-modify-write so writers of different fields (workspace session, recent list, view state) never drop each other's data.
    internal static WorkbenchPreferences Update(string? path, Func<WorkbenchPreferences, WorkbenchPreferences> change)
    {
        var updated = change(Load(path));
        updated.Save(path);
        return updated;
    }

    internal static string NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static bool SamePath(string? a, string? b) => a is not null && b is not null && string.Equals(NormalizePath(a), NormalizePath(b), PathComparison);

    // Newest first, at most eight, de-duplicated by normalized path.
    internal WorkbenchPreferences WithRecent(string projectPath)
    {
        string normalized = NormalizePath(projectPath);
        return this with { RecentProjects = [normalized, .. RecentProjects.Where(p => !SamePath(p, normalized)).Take(MaxRecentProjects - 1)] };
    }
    internal WorkbenchPreferences WithoutRecent(string projectPath) =>
        this with { RecentProjects = RecentProjects.Where(p => !SamePath(p, projectPath)).ToArray() };
    internal WorkbenchViewState? ViewState(string projectPath) => ViewStates.GetValueOrDefault(NormalizePath(projectPath));
    internal WorkbenchPreferences WithViewState(string projectPath, WorkbenchViewState state)
    {
        var states = new Dictionary<string, WorkbenchViewState>(ViewStates, StringComparer.OrdinalIgnoreCase) { [NormalizePath(projectPath)] = state };
        return this with { ViewStates = states };
    }
    internal WorkbenchPreferences WithWindow(string name, TSharpVision.TRect bounds)
    {
        var windows = new Dictionary<string, int[]>(Windows, StringComparer.Ordinal) { [name] = [bounds.a.x, bounds.a.y, bounds.b.x, bounds.b.y] };
        return this with { Windows = windows };
    }
    internal TSharpVision.TRect? Window(string name) =>
        Windows.TryGetValue(name, out int[]? r) && r is { Length: 4 } ? new TSharpVision.TRect(r[0], r[1], r[2], r[3]) : null;
}

// Per-project view state, stored per normalized project path in user settings. NativeOnly is the removed "Native-runnable
// only" filter: kept so existing settings files still load, ignored when read, always written false.
internal sealed record WorkbenchViewState(string Search, string? Group, string[] CollapsedGroups, bool NativeOnly);
