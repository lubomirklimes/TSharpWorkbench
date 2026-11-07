using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TSharpWorkbench.Storage;

/// <summary>A single-owner synchronous project session. Dispose preserves recovery state; Close makes an explicit choice.</summary>
public sealed class ProjectSession : IDisposable
{
    private readonly object gate = new();
    private FileStream? destinationLock, workspaceLock;
    private Checkpoint checkpoint;
    private bool disposed;
    private readonly string workspace, content, stateFile;
    public string ArchivePath { get; }
    public string WorkspacePath => workspace;
    public bool IsDirty { get { lock (gate) { EnsureOpen(); return checkpoint.Dirty; } } }
    // Focused fault/crash boundary tests, never enabled by the application.
    internal Action<string>? Fault { get; set; }
    private string Database => Path.Combine(content, "project.sqlite");

    private ProjectSession(string archive, string root)
    {
        ArchivePath = Path.GetFullPath(archive);
        if (!string.Equals(Path.GetExtension(ArchivePath), ".tsw", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Native projects use the .tsw extension.");
        StorageFiles.NoLinks(ArchivePath);
        StorageFiles.NoLinks(ArchivePath + ".bak");
        if (!Directory.Exists(Path.GetDirectoryName(ArchivePath))) throw new DirectoryNotFoundException("Destination directory does not exist.");
        root = Path.GetFullPath(root); StorageFiles.PrivateDirectory(root);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? ArchivePath.ToUpperInvariant() : ArchivePath)));
        workspace = Path.Combine(root, key);
        content = Path.Combine(workspace, "content"); stateFile = Path.Combine(workspace, "session.json");
        checkpoint = new(ArchivePath, null, true);
        try
        {
            destinationLock = Acquire(ArchivePath + ".lock");
            workspaceLock = Acquire(workspace + ".lock");
        }
        catch { destinationLock?.Dispose(); workspaceLock?.Dispose(); throw; }
    }
    private static FileStream Acquire(string path)
    {
        StorageFiles.NoLinks(path);
        try
        {
            if (!File.Exists(path)) { using var created = StorageFiles.NewPrivateFile(path); }
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { throw new ProjectConflictException("Project/workspace is locked or inaccessible: " + ex.GetType().Name); }
    }
    public static string DefaultWorkspaceRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TSharpWorkbench", "Storage");
    public static ProjectSession Create(string archive, string name, string? workspaceRoot = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.");
        var session = new ProjectSession(archive, workspaceRoot ?? DefaultWorkspaceRoot);
        try
        {
            if (Directory.Exists(session.workspace)) throw new RecoveryRequiredException(session.workspace);
            if (File.Exists(session.ArchivePath)) throw new ProjectConflictException("Destination already exists.");
            StorageFiles.PrivateDirectory(session.content);
            session.Persist();
            ProjectDatabase.Create(session.Database, new(Guid.NewGuid().ToString("D"), name));
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public static ProjectSession Open(string archive, RecoveryChoice recovery = RecoveryChoice.Cancel, string? workspaceRoot = null)
    {
        if (!Enum.IsDefined(recovery)) throw new ArgumentOutOfRangeException(nameof(recovery));
        var session = new ProjectSession(archive, workspaceRoot ?? DefaultWorkspaceRoot);
        try
        {
            if (Directory.Exists(session.workspace))
            {
                StorageFiles.NoLinks(session.workspace);
                if (recovery == RecoveryChoice.Cancel) throw new RecoveryRequiredException(session.workspace);
                if (recovery == RecoveryChoice.Recover)
                {
                    session.checkpoint = StorageFiles.ReadJson<Checkpoint>(session.stateFile);
                    if (!string.Equals(session.checkpoint.Origin, session.ArchivePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidDataException("Recovery origin mismatch.");
                    var current = StorageFiles.ExistingHash(session.ArchivePath);
                    if (current != session.checkpoint.ArchiveHash &&
                        (session.checkpoint.PendingHash is null || current != session.checkpoint.PendingHash))
                        throw new ProjectConflictException("Archive changed outside this session. Recovery workspace has been preserved.");
                    // A killed writer can leave a hot DELETE journal. SQLite must roll it back before read-only validation.
                    using (var recoveredDatabase = ProjectDatabase.Connect(session.Database)) { }
                    ProjectDatabase.Validate(session.Database, null, session.content);
                    // Conservatively dirty even after an ambiguous successful save; never silently drop recovered files.
                    session.checkpoint = session.checkpoint with { ArchiveHash = current, PendingHash = null, Dirty = true };
                    session.Persist();
                    return session;
                }
                session.Quarantine();
            }
            string staging = session.workspace + ".opening-" + Guid.NewGuid().ToString("N");
            try
            {
                string hash = StorageFiles.Hash(session.ArchivePath);
                ProjectArchive.Extract(session.ArchivePath, staging);
                if (StorageFiles.Hash(session.ArchivePath) != hash) throw new ProjectConflictException("Archive changed during open.");
                StorageFiles.PrivateDirectory(session.workspace);
                Directory.Move(staging, session.content);
                session.checkpoint = new(session.ArchivePath, hash, false); session.Persist();
            }
            finally { if (Directory.Exists(staging)) StorageFiles.RemoveTree(staging); }
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    private void Persist() => StorageFiles.WriteJson(stateFile, checkpoint);
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);
    private void Dirty()
    {
        EnsureOpen(); checkpoint = checkpoint with { Dirty = true }; Persist();
    }
    private void Change(Action<SqliteConnection> mutation)
    {
        lock (gate)
        {
            Dirty(); using var db = ProjectDatabase.Connect(Database); using var transaction = db.BeginTransaction();
            mutation(db); transaction.Commit();
        }
    }
    private T Read<T>(Func<SqliteConnection, T> read)
    {
        lock (gate) { EnsureOpen(); using var db = ProjectDatabase.Connect(Database, readOnly: true); return read(db); }
    }
    public ProjectInfo Project => Read(ProjectDatabase.Identity);
    /// <summary>Versioned application settings; JSON preserves value types without a second project document.</summary>
    public IReadOnlyDictionary<string, string> Settings => Read(db =>
        ProjectDatabase.Query(db, "PRAGMA user_version", r => r.GetInt32(0)).Single() == 1
        ? new Dictionary<string, string>()
        : ProjectDatabase.Query(db, "SELECT Key,JsonValue FROM ProjectSettings", r => (r.GetString(0), r.GetString(1))).ToDictionary(r => r.Item1, r => r.Item2));
    public void PutSettings(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Count > 100 || settings.Sum(p => (long)p.Key.Length + p.Value.Length) > 1024 * 1024)
            throw new ArgumentException("Project settings exceed limits.");
        foreach (var pair in settings)
        {
            ProjectDatabase.RequireId(pair.Key, "Project setting key");
            using var json = System.Text.Json.JsonDocument.Parse(pair.Value);
        }
        Change(db =>
        {
            if (ProjectDatabase.Query(db, "PRAGMA user_version", r => r.GetInt32(0)).Single() == 1)
                ProjectDatabase.Execute(db, ProjectDatabase.SettingsMigration);
            ProjectDatabase.Execute(db, "DELETE FROM ProjectSettings");
            foreach (var pair in settings) ProjectDatabase.Execute(db, "INSERT INTO ProjectSettings VALUES($0,$1)", pair.Key, pair.Value);
        });
    }
    /// <summary>Borrow a managed file for the existing atomic scenario editor. Marks recovery dirty before allowing writes.</summary>
    public string EditArtifact(string name)
    {
        StorageFiles.ArtifactName(name);
        lock (gate)
        {
            EnsureOpen(); string path = Path.Combine(content, name); StorageFiles.NoLinks(path);
            if (!ArtifactNames().Contains(name, StringComparer.Ordinal)) throw new FileNotFoundException("Managed artifact not found.");
            Dirty(); return path;
        }
    }
    public string CheckoutArtifact(string name)
    {
        lock (gate)
        {
            EnsureOpen();
            string folder = Path.Combine(Path.GetDirectoryName(workspace)!, "checkouts", Guid.NewGuid().ToString("N"));
            StorageFiles.PrivateDirectory(folder);
            string file = Path.Combine(folder, "scenario.json");
            using var output = StorageFiles.NewPrivateFile(file);
            CopyArtifactTo(name, output);
            return file;
        }
    }
    public string ResolveArtifact(string name)
    {
        StorageFiles.ArtifactName(name);
        lock (gate) { EnsureOpen(); string path = Path.Combine(content, name); StorageFiles.NoLinks(path); return path; }
    }
    public void RemoveScenario(string id) => Change(db => ProjectDatabase.Execute(db, "DELETE FROM Scenarios WHERE Id=$0", id));
    public IReadOnlyList<EnvironmentDefinition> Environments => Read(db => ProjectDatabase.Query(db,
        "SELECT Id,Name,OperatingSystem,Driver,Width,Height,Capture,Enabled FROM Environments ORDER BY Id",
        r => new EnvironmentDefinition(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6), r.GetBoolean(7))));
    public IReadOnlyList<ScenarioReference> Scenarios => Read(db => ProjectDatabase.Query(db,
        "SELECT Id,ArtifactPath,FixtureId,Backend FROM Scenarios ORDER BY Id",
        r => new ScenarioReference(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3))));
    public IReadOnlyList<TestDefinition> Tests => Read(db =>
    {
        var links = ProjectDatabase.Query(db, "SELECT TestId,EnvironmentId FROM TestEnvironments ORDER BY EnvironmentId", r => (Test: r.GetString(0), Env: r.GetString(1)));
        return ProjectDatabase.Query(db, "SELECT Id,Title,Description,Preconditions,ScenarioId FROM Tests ORDER BY Id",
            r => new TestDefinition(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), links.Where(l => l.Test == r.GetString(0)).Select(l => l.Env).ToArray()));
    });
    public void Rename(string name) => Change(db => ProjectDatabase.Execute(db, "UPDATE Project SET Name=$0", name));
    public void RemoveEnvironment(string id) => Change(db => ProjectDatabase.Execute(db, "DELETE FROM Environments WHERE Id=$0", id));
    public void PutEnvironment(EnvironmentDefinition environment)
    {
        ProjectDatabase.RequireId(environment.Id, "Environment ID");
        Change(db => ProjectDatabase.Execute(db, """
            INSERT INTO Environments VALUES($0,$1,$2,$3,$4,$5,$6,$7) ON CONFLICT(Id) DO UPDATE SET
            Name=excluded.Name,OperatingSystem=excluded.OperatingSystem,Driver=excluded.Driver,Width=excluded.Width,
            Height=excluded.Height,Capture=excluded.Capture,Enabled=excluded.Enabled
            """, environment.Id, environment.Name, environment.OperatingSystem, environment.Driver,
            environment.Width, environment.Height, environment.Capture, environment.Enabled));
    }
    public void ImportScenarios(IReadOnlyList<(ScenarioReference Reference, byte[] Payload)> items)
    {
        if (items.Count == 0) return;
        lock (gate)
        {
            EnsureOpen();
            var ids = Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var names = ArtifactNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (reference, payload) in items)
            {
                ProjectDatabase.RequireId(reference.Id, "Scenario ID");
                if (!ids.Add(reference.Id)) throw new ArgumentException("Duplicate scenario ID: " + reference.Id);
                if (reference.ArtifactPath is not { } name || !name.StartsWith("scenarios/", StringComparison.Ordinal))
                    throw new ArgumentException("Expected a managed scenario artifact.");
                StorageFiles.ArtifactName(name);
                if (!names.Add(name) || payload.Length > 1024 * 1024) throw new ArgumentException("Invalid or duplicate scenario artifact.");
            }

            var created = new List<string>();
            try
            {
                Change(db =>
                {
                    foreach (var (reference, payload) in items)
                    {
                        string path = Path.Combine(content, reference.ArtifactPath!);
                        StorageFiles.PrivateDirectory(Path.GetDirectoryName(path)!);
                        using (var output = StorageFiles.NewPrivateFile(path))
                        {
                            created.Add(path);
                            output.Write(payload);
                            output.Flush(true);
                        }
                        ProjectDatabase.Execute(db, "INSERT INTO Scenarios VALUES($0,$1,$2,$3)",
                            reference.Id, reference.ArtifactPath, reference.FixtureId, reference.Backend);
                    }
                });
            }
            catch
            {
                // A commit error may be ambiguous. Check the durable references before removing staged bytes.
                var registered = Scenarios.Select(s => s.ArtifactPath).ToHashSet(StringComparer.Ordinal);
                foreach (string path in created)
                {
                    string name = Path.GetRelativePath(content, path).Replace(Path.DirectorySeparatorChar, '/');
                    if (!registered.Contains(name)) File.Delete(path);
                }
                throw;
            }
        }
    }

    public void RemoveTest(string id) => Change(db => ProjectDatabase.Execute(db, "DELETE FROM Tests WHERE Id=$0", id));

    public void PutScenario(ScenarioReference scenario)
    {
        ProjectDatabase.RequireId(scenario.Id, "Scenario ID");
        if (scenario.FixtureId is not null) ProjectDatabase.RequireId(scenario.FixtureId, $"Fixture ID for scenario '{scenario.Id}'");
        lock (gate)
        {
            EnsureOpen();
            if (scenario.ArtifactPath is not null)
            {
                StorageFiles.ArtifactName(scenario.ArtifactPath);
                if (!scenario.ArtifactPath.StartsWith("scenarios/", StringComparison.Ordinal) || !ArtifactNames().Contains(scenario.ArtifactPath, StringComparer.Ordinal))
                    throw new ArgumentException("Scenario attachment must already exist under scenarios/.");
            }
            Change(db => ProjectDatabase.Execute(db, """
                INSERT INTO Scenarios VALUES($0,$1,$2,$3) ON CONFLICT(Id) DO UPDATE SET
                ArtifactPath=excluded.ArtifactPath,FixtureId=excluded.FixtureId,Backend=excluded.Backend
                """, scenario.Id, scenario.ArtifactPath, scenario.FixtureId, scenario.Backend));
        }
    }
    public void PutTest(TestDefinition test)
    {
        ProjectDatabase.RequireId(test.Id, "Test ID");
        if (test.EnvironmentIds is null || test.EnvironmentIds.Count == 0 || test.EnvironmentIds.Distinct(StringComparer.Ordinal).Count() != test.EnvironmentIds.Count)
            throw new ArgumentException("A test requires one or more distinct environments.");
        Change(db =>
        {
            ProjectDatabase.Execute(db, """
                INSERT INTO Tests VALUES($0,$1,$2,$3,$4) ON CONFLICT(Id) DO UPDATE SET
                Title=excluded.Title,Description=excluded.Description,Preconditions=excluded.Preconditions,ScenarioId=excluded.ScenarioId
                """, test.Id, test.Title, test.Description, test.Preconditions, test.ScenarioId);
            ProjectDatabase.Execute(db, "DELETE FROM TestEnvironments WHERE TestId=$0", test.Id);
            foreach (string environment in test.EnvironmentIds)
                ProjectDatabase.Execute(db, "INSERT INTO TestEnvironments VALUES($0,$1)", test.Id, environment);
        });
    }
    public void PutArtifact(string name, Stream source)
    {
        StorageFiles.ArtifactName(name);
        lock (gate)
        {
            EnsureOpen(); string target = Path.Combine(content, name); StorageFiles.NoLinks(target);
            // Case aliases are never silently collapsed on case-insensitive filesystems.
            foreach (string existing in ArtifactNames())
            {
                string[] oldParts = existing.Split('/'), newParts = name.Split('/');
                for (int i = 0; i < Math.Min(oldParts.Length, newParts.Length); i++)
                {
                    if (!string.Equals(oldParts[i], newParts[i], StringComparison.OrdinalIgnoreCase)) break;
                    if (oldParts[i] != newParts[i]) throw new ArgumentException("Artifact name has a case alias.");
                }
            }
            Dirty(); StorageFiles.PrivateDirectory(Path.GetDirectoryName(target)!);
            string pending = Path.Combine(workspace, "artifact-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var output = StorageFiles.NewPrivateFile(pending))
                { StorageFiles.CopyBounded(source, output, ProjectArchive.MaxFile); output.Flush(true); }
                File.Move(pending, target, true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }
    public void CopyArtifactTo(string name, Stream destination)
    {
        StorageFiles.ArtifactName(name);
        lock (gate)
        {
            EnsureOpen(); string path = Path.Combine(content, name); StorageFiles.NoLinks(path);
            using var source = File.OpenRead(path); StorageFiles.CopyBounded(source, destination, ProjectArchive.MaxFile);
        }
    }
    /// <summary>Streams a completed flat run into a private staging directory before publishing it.</summary>
    public void PutRunEvidence(string prefix, string sourceDirectory)
    {
        StorageFiles.ArtifactName(prefix + "/run.json");
        if (!prefix.StartsWith("runs/", StringComparison.Ordinal)) throw new ArgumentException("Expected a run artifact path.");
        lock (gate)
        {
            EnsureOpen();
            StorageFiles.NoLinks(sourceDirectory);
            string target = Path.Combine(content, prefix);
            StorageFiles.NoLinks(target);
            string stage = Path.Combine(workspace, "evidence-" + Guid.NewGuid().ToString("N"));
            Dirty();
            StorageFiles.PrivateDirectory(stage);
            try
            {
                int count = 0;
                foreach (string source in Directory.EnumerateFileSystemEntries(sourceDirectory))
                {
                    StorageFiles.NoLinks(source);
                    if (Directory.Exists(source) || ++count > ProjectArchive.MaxEntries)
                        throw new IOException("Expected bounded flat run evidence.");
                    string file = Path.GetFileName(source);
                    StorageFiles.ArtifactName(prefix + "/" + file);
                    using var input = File.OpenRead(source);
                    using var output = StorageFiles.NewPrivateFile(Path.Combine(stage, file));
                    StorageFiles.CopyBounded(input, output, ProjectArchive.MaxFile);
                    output.Flush(true);
                }
                StorageFiles.PrivateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(stage, target);
            }
            finally
            {
                if (Directory.Exists(stage)) StorageFiles.RemoveTree(stage);
            }
        }
    }

    public void AdoptBaselineStore(string manifest, Action<string> validate, string prefix = "baselines/store")
    {
        lock (gate)
        {
            EnsureOpen();
            if (!prefix.StartsWith("baselines/", StringComparison.Ordinal)) throw new ArgumentException("Expected a baseline artifact prefix.");
            StorageFiles.ArtifactName(prefix + "/visual-baselines.json");
            string target = Path.Combine(content, prefix);
            StorageFiles.NoLinks(target);
            if (Directory.Exists(target)) throw new IOException("This project already owns a baseline store.");
            string stage = Path.Combine(workspace, "baselines-" + Guid.NewGuid().ToString("N"));
            StorageFiles.PrivateDirectory(stage);
            try
            {
                long total = 0;
                int count = 0;
                void Copy(string source, string relative)
                {
                    StorageFiles.ArtifactName(prefix + "/" + relative);
                    StorageFiles.NoLinks(source);
                    using var input = File.OpenRead(source);
                    total = checked(total + input.Length);
                    if (++count > ProjectArchive.MaxEntries || total > ProjectArchive.MaxTotal)
                        throw new IOException("Baseline store exceeds archive limits.");
                    string path = Path.Combine(stage, relative);
                    StorageFiles.PrivateDirectory(Path.GetDirectoryName(path)!);
                    using var output = StorageFiles.NewPrivateFile(path);
                    StorageFiles.CopyBounded(input, output, Math.Min(input.Length, ProjectArchive.MaxFile));
                    output.Flush(true);
                }
                Copy(manifest, "visual-baselines.json");
                string approved = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, "approved");
                void CopyApproved(string dir, string relative, int depth)
                {
                    StorageFiles.NoLinks(dir);
                    foreach (string file in Directory.EnumerateFileSystemEntries(dir))
                    {
                        StorageFiles.NoLinks(file);
                        string name = relative + "/" + Path.GetFileName(file);
                        if (Directory.Exists(file))
                        {
                            if (depth == 0) CopyApproved(file, name, 1);
                            else throw new IOException("Unexpected nested baseline directory.");
                        }
                        else if (name.EndsWith(".baseline.json", StringComparison.Ordinal) || name.EndsWith(".tvs1", StringComparison.Ordinal) || name.EndsWith(".png", StringComparison.Ordinal))
                            Copy(file, name);
                    }
                }
                if (Directory.Exists(approved)) CopyApproved(approved, "approved", 0);
                validate(Path.Combine(stage, "visual-baselines.json"));
                Dirty();
                StorageFiles.PrivateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(stage, target);
            }
            finally { if (Directory.Exists(stage)) StorageFiles.RemoveTree(stage); }
        }
    }

    public IReadOnlyList<string> ListArtifacts(string prefix)
    {
        lock (gate)
        {
            EnsureOpen();
            return ArtifactNames().Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        }
    }

    private List<string> ArtifactNames()
    {
        var result = new List<string>();
        void Walk(string dir)
        {
            StorageFiles.NoLinks(dir);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                StorageFiles.NoLinks(path);
                if (Directory.Exists(path)) Walk(path);
                else if (path != Database) result.Add(Path.GetRelativePath(content, path).Replace('\\', '/'));
            }
        }
        Walk(content); return result;
    }
    public void Save()
    {
        lock (gate)
        {
            EnsureOpen(); CheckOrigin();
            ProjectDatabase.Validate(Database, Project.Id, content);
            string temporary = ArchivePath + "." + Guid.NewGuid().ToString("N") + ".saving";
            string verify = Path.Combine(workspace, "verify-" + Guid.NewGuid().ToString("N"));
            try
            {
                Fault?.Invoke("before-pack");
                ProjectArchive.Pack(content, temporary, Project.Id);
                ProjectArchive.Extract(temporary, verify);
                StorageFiles.RemoveTree(verify);
                string newHash = StorageFiles.Hash(temporary);
                checkpoint = checkpoint with { PendingHash = newHash, Dirty = true }; Persist();
                Fault?.Invoke("before-replace"); CheckOrigin();
                StorageFiles.NoLinks(ArchivePath + ".bak");
                if (File.Exists(ArchivePath)) File.Replace(temporary, ArchivePath, ArchivePath + ".bak");
                else File.Move(temporary, ArchivePath); // Must not overwrite a concurrently created destination.
                Fault?.Invoke("after-replace");
                var committed = checkpoint with { ArchiveHash = newHash, PendingHash = null, Dirty = false };
                StorageFiles.WriteJson(stateFile, committed);
                checkpoint = committed;
            }
            finally
            {
                // Only scratch snapshots: active working data and the prior archive/backup are never cleaned on failure.
                if (Directory.Exists(verify)) StorageFiles.RemoveTree(verify);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
    /// <summary>Writes an independent verified copy to a new path, preserving the project identity.</summary>
    public void SaveCopy(string destination)
    {
        lock (gate)
        {
            EnsureOpen();
            using var copy = new ProjectSession(destination, Path.GetDirectoryName(workspace)!);
            if (Directory.Exists(copy.workspace) || File.Exists(copy.ArchivePath) || File.Exists(copy.ArchivePath + ".bak"))
                throw new ProjectConflictException("Choose a new destination without an archive, backup, or recovery workspace.");
            string temporary = copy.ArchivePath + "." + Guid.NewGuid().ToString("N") + ".saving";
            string verify = Path.Combine(workspace, "copy-" + Guid.NewGuid().ToString("N"));
            try
            {
                ProjectArchive.Pack(content, temporary, Project.Id);
                ProjectArchive.Extract(temporary, verify);
                File.Move(temporary, copy.ArchivePath);
            }
            finally
            {
                if (Directory.Exists(verify)) StorageFiles.RemoveTree(verify);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private void CheckOrigin()
    {
        if (StorageFiles.ExistingHash(ArchivePath) != checkpoint.ArchiveHash)
            throw new ProjectConflictException("Archive changed outside this session. Working state is preserved.");
    }
    private void Quarantine() => Directory.Move(workspace, workspace + ".discarded-" + Guid.NewGuid().ToString("N"));
    public bool Close(CloseChoice choice)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
            if (choice == CloseChoice.Cancel) return false;
            if (choice == CloseChoice.Save)
            {
                if (checkpoint.Dirty) Save(); else CheckOrigin();
                StorageFiles.RemoveTree(workspace);
            }
            else Quarantine();
            Dispose(); return true;
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; workspaceLock?.Dispose(); destinationLock?.Dispose();
        }
    }
}
