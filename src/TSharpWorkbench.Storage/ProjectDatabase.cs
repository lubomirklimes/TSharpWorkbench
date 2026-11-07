using Microsoft.Data.Sqlite;

namespace TSharpWorkbench.Storage;

internal static class ProjectDatabase
{
    internal const string SettingsMigration = "CREATE TABLE ProjectSettings(Key TEXT PRIMARY KEY NOT NULL, JsonValue TEXT NOT NULL CHECK(json_valid(JsonValue))); PRAGMA user_version=2;";
    internal const string Schema = """
        PRAGMA user_version=1;
        CREATE TABLE Project(Id TEXT PRIMARY KEY NOT NULL, Name TEXT NOT NULL CHECK(length(trim(Name))>0));
        CREATE TABLE Environments(Id TEXT PRIMARY KEY NOT NULL, Name TEXT NOT NULL UNIQUE CHECK(length(trim(Name))>0),
          OperatingSystem TEXT NOT NULL CHECK(length(OperatingSystem)>0), Driver TEXT NOT NULL CHECK(length(Driver)>0),
          Width INTEGER NOT NULL CHECK(Width>0 AND Width<=1000), Height INTEGER NOT NULL CHECK(Height>0 AND Height<=1000),
          Capture TEXT NOT NULL CHECK(length(Capture)>0), Enabled INTEGER NOT NULL CHECK(Enabled IN (0,1)));
        CREATE TABLE Scenarios(Id TEXT PRIMARY KEY NOT NULL, ArtifactPath TEXT, FixtureId TEXT,
          Backend TEXT NOT NULL CHECK(Backend IN ('proxy-logical','diagnostic-logical')));
        CREATE TABLE Tests(Id TEXT PRIMARY KEY NOT NULL, Title TEXT NOT NULL CHECK(length(trim(Title))>0),
          Description TEXT NOT NULL, Preconditions TEXT NOT NULL, ScenarioId TEXT NOT NULL REFERENCES Scenarios(Id));
        CREATE TABLE TestEnvironments(TestId TEXT NOT NULL REFERENCES Tests(Id) ON DELETE CASCADE,
          EnvironmentId TEXT NOT NULL REFERENCES Environments(Id), PRIMARY KEY(TestId,EnvironmentId));
        """;
    internal static SqliteConnection Connect(string file, bool create = false, bool readOnly = false)
    {
        StorageFiles.NoLinks(file);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, ForeignKeys = true }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL;");
            if (!readOnly) Execute(connection, "PRAGMA journal_mode=DELETE;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    internal static void Execute(SqliteConnection db, string sql, params object?[] values)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$" + i, values[i] ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    internal static void Create(string file, ProjectInfo project)
    {
        using var db = Connect(file, create: true);
        using var tx = db.BeginTransaction();
        Execute(db, Schema);
        Execute(db, "INSERT INTO Project VALUES($0,$1)", project.Id, project.Name);
        tx.Commit();
    }
    internal static List<T> Query<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader(); var items = new List<T>();
        while (reader.Read()) items.Add(map(reader));
        return items;
    }
    internal static ProjectInfo Identity(SqliteConnection db) => Query(db, "SELECT Id,Name FROM Project", r => new ProjectInfo(r.GetString(0), r.GetString(1))).Single();
    internal static void Validate(string file, string? expectedId, string content)
    {
        using var db = Connect(file, readOnly: true);
        int version = Query(db, "PRAGMA user_version", r => r.GetInt32(0)).Single();
        if (version is not (1 or 2))
            throw new InvalidDataException("Unsupported database version.");
        if (!Query(db, "PRAGMA integrity_check", r => r.GetString(0)).SequenceEqual(["ok"]) ||
            Query(db, "PRAGMA foreign_key_check", r => r.GetValue(0)).Count != 0)
            throw new InvalidDataException("Invalid SQLite integrity or foreign keys.");
        // Reject triggers/views/extra tables and altered constraints, not just a matching version number.
        using var reference = new SqliteConnection("Data Source=:memory:"); reference.Open(); Execute(reference, Schema);
        if (version == 2) Execute(reference, SettingsMigration);
        const string schemaQuery = "SELECT type || ':' || name || ':' || coalesce(sql,'') FROM sqlite_schema ORDER BY name";
        if (!Query(db, schemaQuery, r => r.GetString(0)).SequenceEqual(Query(reference, schemaQuery, r => r.GetString(0))))
            throw new InvalidDataException("Unexpected database schema.");
        var project = Identity(db); RequireId(project.Id, "Project ID");
        if (string.IsNullOrWhiteSpace(project.Name) || expectedId is not null && project.Id != expectedId)
            throw new InvalidDataException("Project identity mismatch.");
        foreach (string table in new[] { "Environments", "Scenarios", "Tests" })
            foreach (var id in Query(db, "SELECT Id FROM " + table, r => r.GetString(0))) RequireId(id, $"ID in the {table} table");
        foreach (var path in Query(db, "SELECT ArtifactPath FROM Scenarios WHERE ArtifactPath IS NOT NULL", r => r.GetString(0)))
        {
            StorageFiles.ArtifactName(path);
            if (!path.StartsWith("scenarios/", StringComparison.Ordinal) || !File.Exists(Path.Combine(content, path)))
                throw new InvalidDataException("Missing scenario attachment.");
            string parent = content;
            foreach (string segment in path.Split('/'))
            {
                if (!Directory.EnumerateFileSystemEntries(parent).Any(p => Path.GetFileName(p) == segment))
                    throw new InvalidDataException("Scenario attachment case does not match its portable path.");
                parent = Path.Combine(parent, segment); StorageFiles.NoLinks(parent);
            }
        }
        if (Query(db, "SELECT Id FROM Tests WHERE NOT EXISTS (SELECT 1 FROM TestEnvironments WHERE TestId=Tests.Id)", r => r.GetString(0)).Count != 0)
            throw new InvalidDataException("Each test requires at least one environment.");
    }
    internal static void RequireId(string id, string field)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
            throw new ArgumentException($"{field} must be nonempty, at most 256 characters, and contain no control characters.");
    }
}
