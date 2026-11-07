using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;

// Runtime WorkbenchProject remains the runner's DTO; SQLite rows are the sole native persistence owner.
internal sealed class NativeProject : IDisposable
{
    internal const string BaselineArtifact = "baselines/store/visual-baselines.json";
    internal ProjectSession Session { get; }
    internal static string? WorkspaceRoot { get; set; }
    private static readonly HashSet<string> RuntimeOnly = ["Schema", "Version", "Name", "ScenarioFiles", "Group", "Search", "CollapsedGroups"];
    private static readonly HashSet<string> SettingsKeys = typeof(WorkbenchProject).GetProperties()
        .Select(p => p.Name).Where(p => !RuntimeOnly.Contains(p)).ToHashSet(StringComparer.Ordinal);
    private NativeProject(ProjectSession session) => Session = session;
    internal static NativeProject Create(string path, string name) => new(ProjectSession.Create(path, name, WorkspaceRoot));
    internal static NativeProject Open(string path, RecoveryChoice choice = RecoveryChoice.Cancel) => new(ProjectSession.Open(path, choice, WorkspaceRoot));
    internal WorkbenchProject Read()
    {
        var data = JsonSerializer.SerializeToNode(new WorkbenchProject())!.AsObject();
        foreach (var pair in Session.Settings)
        {
            if (!SettingsKeys.Contains(pair.Key)) throw new InvalidDataException("Unsupported native project setting: " + pair.Key);
            data[pair.Key] = JsonNode.Parse(pair.Value);
        }
        var project = data.Deserialize<WorkbenchProject>()! with
        {
            Name = Session.Project.Name,
            ScenarioFiles = Session.Scenarios.Where(s => s.ArtifactPath is not null).Select(s => Session.ResolveArtifact(s.ArtifactPath!)).ToArray()
        };
        if (Session.Settings.Count == 0)
            project = project with { OutputRoot = Path.Combine(Path.GetDirectoryName(Session.ArchivePath)!, "runs") };
        if (IsBaselineArtifact(project.BaselineStore)) project = project with { BaselineStore = Session.ResolveArtifact(project.BaselineStore!) };
        project.Validate(); return project;
    }
    internal void Store(WorkbenchProject project)
    {
        project.Validate();
        var persisted = OwnedBaselineArtifact(project.BaselineStore) is { } owned
            ? project with { BaselineStore = owned } : project;
        var json = JsonSerializer.SerializeToNode(persisted)!.AsObject();
        var settings = SettingsKeys.ToDictionary(key => key, key => json[key]?.ToJsonString() ?? "null");
        if (Session.Project.Name != project.Name) Session.Rename(project.Name);
        var existing = Session.Settings;
        if (settings.Count != existing.Count || settings.Any(p => !existing.TryGetValue(p.Key, out var value) || value != p.Value))
            Session.PutSettings(settings);
        var managed = project.ScenarioFiles.Select(path => (Path: path, Artifact: ArtifactName(path))).ToArray();
        foreach (var item in managed)
        {
            var info = ScenarioFiles.Inspect(item.Path);
            var prior = Session.Scenarios.FirstOrDefault(s => s.ArtifactPath == item.Artifact);
            if (info.Document is null)
            {
                if (prior is null) throw new InvalidDataException("A managed scenario cannot be registered: " + info.Message);
                continue; // Keep invalid managed content and its existing reference visible for repair.
            }
            var scenario = ReferenceFor(info.Document, item.Artifact);
            if (prior is not null && prior.Id != scenario.Id)
                throw new InvalidDataException("Managed scenario ID changed: " + prior.Id);
            if (prior != scenario)
            {
                try { Session.PutScenario(scenario); }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException($"Scenario file '{item.Artifact}': {ex.Message}", ex);
                }
            }
        }
        foreach (var prior in Session.Scenarios.Where(s => s.ArtifactPath is not null && !managed.Any(m => m.Artifact == s.ArtifactPath)))
            Session.RemoveScenario(prior.Id); // Artifacts are retained; removal cannot orphan a Test.
    }
    private static ScenarioReference ReferenceFor(ExternalScenarioFile file, string artifact) =>
        new(file.Scenario.Id, artifact, file.Fixture.Length == 0 ? null : file.Fixture, file.Scenario.Backend);

    private static bool IsBaselineArtifact(string? path) =>
        path is not null && path.StartsWith("baselines/", StringComparison.Ordinal) &&
        path.EndsWith("/visual-baselines.json", StringComparison.Ordinal);

    internal string? OwnedBaselineArtifact(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string relative = Path.GetRelativePath(Path.Combine(Session.WorkspacePath, "content"), Path.GetFullPath(path)).Replace('\\', '/');
        return IsBaselineArtifact(relative) && WorkbenchPreferences.SamePath(Session.ResolveArtifact(relative), path) ? relative : null;
    }
    internal string ArtifactName(string path)
    {
        string relative = Path.GetRelativePath(Path.Combine(Session.WorkspacePath, "content"), Path.GetFullPath(path)).Replace('\\', '/');
        if (!relative.StartsWith("scenarios/", StringComparison.Ordinal) || relative.Split('/').Any(s => s is ".." or "."))
            throw new InvalidDataException("Native projects must own their scenario copies.");
        return relative;
    }
    internal string Attach(string source) => Import([source]).Single();

    internal string[] Import(IEnumerable<string> sources)
    {
        var batch = new List<(ScenarioReference Reference, byte[] Payload)>();
        long total = 0;
        foreach (string source in sources)
        {
            // Deny concurrent writes while the same bytes are parsed and copied.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024 || (total += input.Length) > 64 * 1024 * 1024)
                throw new IOException("Import limit is 1 MiB per scenario and 64 MiB per batch.");
            var info = ScenarioFiles.Inspect(source);
            if (info.Document is null) throw new InvalidDataException(info.Message);
            string stem = new(Path.GetFileNameWithoutExtension(source).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').Take(70).ToArray());
            string name = "scenarios/" + stem + "-" + Guid.NewGuid().ToString("N") + ".json";
            byte[] bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            batch.Add((ReferenceFor(info.Document, name), bytes));
        }
        Session.ImportScenarios(batch);
        return batch.Select(i => Session.ResolveArtifact(i.Reference.ArtifactPath!)).ToArray();
    }
    internal string CreateScenario(string text)
    {
        string name = "scenarios/scenario-" + Guid.NewGuid().ToString("N") + ".json";
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        Session.PutArtifact(name, input);
        return Session.EditArtifact(name);
    }
    public void Dispose() => Session.Dispose();
}
