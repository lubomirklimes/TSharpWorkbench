using System.Text.Json;
using System.Text.Json.Serialization;
using TSharpWorkbench.Testing;

// A project is configuration only; never serialize a RunOptions, result or environment object.
internal sealed record WorkbenchProject
{
    public string Schema { get; init; } = "TSharpVision.TestWorkbench";
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "Untitled project";
    public string Target { get; init; } = "";
    public string? ProxyShutdownKey { get; init; }
    public string[] TargetArguments { get; init; } = [];
    public Dictionary<string, string> TargetEnvironment { get; init; } = new();
    public string OutputRoot { get; init; } = "runs";
    public string[] ScenarioFiles { get; init; } = [];
    // Ordinary compiled scenario IDs explicitly allowed as legacy fixtures (bindings). Absent = strict
    // fixture-only binding; written only when set, so other projects keep their exact file content.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? LegacyFixtures { get; init; }
    public string[] SelectedIds { get; init; } = [];
    public string? Group { get; init; }
    public string Search { get; init; } = "";
    public string[] CollapsedGroups { get; init; } = [];
    public string Mode { get; init; } = "NullDriver";
    public string ProxyBackend { get; init; } = "NullDriver";
    public string ProxyCapture { get; init; } = ProxyCaptureKinds.None;
    public string? PreferredNativeMode { get; init; }
    public string RunScope { get; init; } = "selected";
    public string TestType { get; init; } = "functional";
    public string VisualBehavior { get; init; } = "compare";
    public string[] ScreenSizes { get; init; } = ["80x25"];
    public int CustomWidth { get; init; } = 100;
    public int CustomHeight { get; init; } = 30;
    public string? BaselineStore { get; init; }
    public string Inspect { get; init; } = "declared";
    public string[] Allow { get; init; } = [];
    public int? Timeout { get; init; }
    public int? ScenarioTimeout { get; init; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal string Fingerprint() => JsonSerializer.Serialize(this, Json);
    internal void Validate()
    {
        if (Schema != "TSharpVision.TestWorkbench" || Version != 1)
            throw new ArgumentException($"Unsupported project schema/version '{Schema}'/{Version}; this Workbench supports version 1.");
        if (string.IsNullOrWhiteSpace(Name) || Target is null || string.IsNullOrWhiteSpace(OutputRoot) || Search is null
            || SelectedIds is null || CollapsedGroups is null || ScreenSizes is null || Allow is null
            || SelectedIds.Any(string.IsNullOrWhiteSpace) || CollapsedGroups.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Project name, paths, filters and selection arrays must have valid values.");
        if (ProxyShutdownKey is not null && ProxyShutdownKey != "kbEnter") throw new ArgumentException("Supported proxy shutdown confirmation is kbEnter.");
        TargetLaunch.Validate(TargetArguments, TargetEnvironment);
        if (ScenarioFiles is null || ScenarioFiles.Any(string.IsNullOrWhiteSpace) || ScenarioFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ScenarioFiles.Length)
            throw new ArgumentException("Scenario files must be explicit, nonempty, unique paths.");
        if (LegacyFixtures is not null && (LegacyFixtures.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl) || id.Contains('*'))
            || LegacyFixtures.Distinct(StringComparer.Ordinal).Count() != LegacyFixtures.Length))
            throw new ArgumentException("Legacy fixtures must be explicit, unique compiled scenario IDs (no wildcards).");
        if (PreferredNativeMode is not null && !NativeExecution.IsMode(PreferredNativeMode)) throw new ArgumentException("Unsupported preferred native driver; no fallback.");
        if (ProxyBackend is not ("NullDriver" or "SDLDriver" or "SDLGpuDriver" or "Win32ConsoleDriver")) throw new ArgumentException("Unsupported proxy backend; no fallback.");
        if (!ProxyCaptureKinds.All.Contains(ProxyCapture) || ProxyCapture != ProxyCaptureKinds.None && !ProxyCaptureKinds.ForBackend(ProxyBackend).Contains(ProxyCapture)) throw new ArgumentException("Unsupported proxy native capture kind for backend; no fallback.");
        if (Mode != "NullDriver" && !NativeExecution.IsMode(Mode)) throw new ArgumentException("Unsupported project driver mode; no fallback.");
        if (RunScope is not ("selected" or "group" or "all")) throw new ArgumentException("Run scope must be selected, group or all.");
        if (TestType is not ("functional" or "visual" or "rendering") || VisualBehavior is not ("compare" or "candidates"))
            throw new ArgumentException("Invalid test type or visual behavior.");
        if (Inspect is not ("none" or "declared" or "all") || Timeout is <= 0 || ScenarioTimeout is <= 0
            || Allow.Any(a => a is not ("network" or "processes" or "devices" or "elevation")))
            throw new ArgumentException("Invalid inspection, timeout or allowed capability defaults.");
        if (ScreenSizes.Length == 0) throw new ArgumentException("Select at least one screen size.");
        foreach (string size in ScreenSizes) ScenarioSelection.ParseSize(size);
        ScenarioSelection.ParseSize(ScenarioSelection.Format(CustomWidth, CustomHeight));
    }
    internal static string Resolve(string reference, string projectPath) => Path.GetFullPath(reference, Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
    internal static string Portable(string reference, string projectPath) => string.IsNullOrEmpty(reference) ? reference
        : Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, Path.GetFullPath(reference));
    internal static WorkbenchProject Load(string path)
    {
        Extension(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("schema", out var schema) || !doc.RootElement.TryGetProperty("version", out var version)
            || schema.ValueKind != JsonValueKind.String || version.ValueKind != JsonValueKind.Number
            || schema.GetString() != "TSharpVision.TestWorkbench" || !version.TryGetInt32(out int v) || v != 1)
            throw new ArgumentException("Unsupported or missing project schema/version; expected TSharpVision.TestWorkbench version 1.");
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
            if (!propertyNames.Add(property.Name)) throw new ArgumentException("Duplicate project property: " + property.Name);
        var project = JsonSerializer.Deserialize<WorkbenchProject>(doc.RootElement.GetRawText(), Json) ?? throw new ArgumentException("Empty project.");
        if(!doc.RootElement.TryGetProperty("testType",out _))
            project=project with {TestType=project.Mode!="NullDriver"?"rendering":project.BaselineStore is not null?"visual":"functional"};
        project.Validate();
        var resolved = project with { Target = project.Target.Length == 0 ? "" : Resolve(project.Target, path), OutputRoot = Resolve(project.OutputRoot, path),
            BaselineStore = project.BaselineStore is null ? null : Resolve(project.BaselineStore, path),
            ScenarioFiles = project.ScenarioFiles.Select(f => Resolve(f, path)).ToArray() };
        resolved.Validate();
        return resolved;
    }
    internal void Save(string path)
    {
        Extension(path); Validate(); path = Path.GetFullPath(path);
        var portable = this with { Target = Portable(Target, path), OutputRoot = Portable(OutputRoot, path),
            BaselineStore = BaselineStore is null ? null : Portable(BaselineStore, path),
            ScenarioFiles = ScenarioFiles.Select(f => Portable(f, path)).ToArray() };
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, portable.Fingerprint() + Environment.NewLine); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Extension(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".twproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TSharpWorkbench projects must use the .twproj extension (JSON content).");
    }
}

// Where a project lives and its automatic Runs folder. A saved project keeps its runs in "runs" next to the
// project file. An unsaved project uses the folder Save as suggests, <Documents>\TSharpWorkbench\<project name>, never
// the process current directory or the executable's (possibly read-only) folder. Nothing here creates a directory.
internal static class ProjectLocation
{
    internal const string RunsFolder = "runs", DefaultName = "Untitled project";
    // Overridable so tests never touch the real Documents folder.
    internal static string Home { get; set; } = DefaultHome();
    private static string DefaultHome()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length == 0) documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(documents.Length == 0 ? Path.GetTempPath() : documents, "TSharpWorkbench");
    }
    // A folder and file name from the project name: invalid characters become '-', trailing dots and spaces are dropped.
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).TrimEnd('.', ' ');
        return safe.Length == 0 ? DefaultName : safe;
    }
    internal static string SuggestedFolder(string name) => Path.Combine(Home, SafeName(name));
    internal static string SuggestedFile(string name) => Path.Combine(SuggestedFolder(name), SafeName(name) + ".twproj");
    // The project folder: the saved file's folder, otherwise the suggested one.
    internal static string Folder(string? projectPath, string name) =>
        projectPath is null ? SuggestedFolder(name) : Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
    internal static string DefaultRuns(string? projectPath, string name) => Path.Combine(Folder(projectPath, name), RunsFolder);
    internal static bool IsDefaultRuns(string runs, string? projectPath, string name) =>
        WorkbenchPreferences.SamePath(runs.Trim(), DefaultRuns(projectPath, name));
}
