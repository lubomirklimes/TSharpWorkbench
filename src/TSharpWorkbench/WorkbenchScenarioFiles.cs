using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using TSharpWorkbench.Testing;

// The external JSON scenario documents a project references (.twproj scenarioFiles). Everything here reads
// only: it never launches the target, and never copies, moves or rewrites a document. Three kinds of scenario exist:
// A. proxy-logical external documents (backend "proxy-logical", no fixture) for ordinary applications;
// B. fixture-bound external documents (diagnostic-logical, "fixture") bound to a fixture the diagnostic target advertises;
// C. compiled diagnostic scenarios advertised by the target itself (not files; listed, never managed here).
internal enum ScenarioFileStatus { Valid, Missing, JsonError, NotScenario, UnsupportedSchema, Invalid }

// One document as read from disk: its classification and, when valid, the parsed document.
internal sealed record ScenarioFileInfo(string Path, ScenarioFileStatus Status, string Message, ExternalScenarioFile? Document = null, ScenarioDescriptor? Compiled = null)
{
    internal string? Id => Document?.Scenario.Id ?? Compiled?.Id;
    internal bool Proxy => Document?.Scenario.Backend == "proxy-logical";
    internal string Kind => Compiled is not null ? "White-Box (compiled)" : Document is null ? "-" : Proxy ? "Black-Box" : "White-Box: " + Document.Fixture;
}

// An attached document in the context of its project and target: duplicates, mixed kinds and fixture binding.
// Status is the short list label; Problem the actionable explanation (null when the scenario is usable).
internal sealed record AttachedScenario(ScenarioFileInfo Info, string Status, string? Problem, ScenarioDescriptor? Bound = null)
{
    // A requirement that does not stop loading but will stop a run (for example the proxy deployment beside the target).
    internal string? Note { get; init; }
    internal string Path => Info.Path;
    internal bool Usable => Problem is null && Bound is not null;
}

internal static class ScenarioFiles
{
    internal const string NoneAttached = "No external scenarios are attached. Use Scenario Manager > Add files... or Add folder....";
    internal const int FolderLimit = 2000;

    internal static ScenarioFileInfo Inspect(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(path, ScenarioFileStatus.Missing, "The path is not valid: " + ex.Message); }
        if (Directory.Exists(full)) return new(full, ScenarioFileStatus.Missing, "This is a folder, not a JSON file. Use Scenario Manager > Add folder... for folders.");
        if (!File.Exists(full)) return new(full, ScenarioFileStatus.Missing, "The file does not exist (moved, renamed or deleted?). Remove it from the project or restore the file.");
        try
        {
            if (new FileInfo(full).Length > 1024 * 1024) return new(full, ScenarioFileStatus.Invalid, "The document is larger than 1 MiB; scenario documents are limited to 1 MiB.");
            byte[] bytes = File.ReadAllBytes(full);
            using (var doc = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0)))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new(full, ScenarioFileStatus.NotScenario, "Not a scenario document: the JSON root is not an object.");
                if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String)
                    return new(full, ScenarioFileStatus.NotScenario, $"Not a scenario document: it has no \"schema\": \"{ExternalScenarios.Schema}\" property.");
                if (schema.GetString() != ExternalScenarios.Schema)
                    return new(full, ScenarioFileStatus.NotScenario, $"Not a scenario document: its schema is '{schema.GetString()}', not '{ExternalScenarios.Schema}'.");
                if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != 1)
                    return new(full, ScenarioFileStatus.UnsupportedSchema, $"Unsupported scenario schema version {(root.TryGetProperty("version", out var raw) ? raw.GetRawText() : "(missing)")}; this Workbench reads version 1.");
            }
            return new(full, ScenarioFileStatus.Valid, "", ExternalScenarios.Load(full));
        }
        catch (JsonException ex) { return new(full, ScenarioFileStatus.JsonError, "Not valid JSON: " + ex.Message); }
        catch (ExternalScenarioException ex) { return new(full, ScenarioFileStatus.Invalid, Detail(ex, full)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(full, ScenarioFileStatus.Missing, "The file cannot be read: " + ex.Message); }
    }

    // The library prefixes messages with the document path; the Workbench shows the path separately.
    internal static string Detail(Exception ex, string path)
    {
        string message = ex.Message;
        while (message.StartsWith(path + ": ", StringComparison.OrdinalIgnoreCase)) message = message[(path.Length + 2)..];
        return message.Length == 0 ? "The document is not valid." : message;
    }

    internal static string StatusLabel(ScenarioFileStatus status) => status switch
    {
        ScenarioFileStatus.Missing => "Missing", ScenarioFileStatus.JsonError => "JSON error", ScenarioFileStatus.NotScenario => "Not scenario",
        ScenarioFileStatus.UnsupportedSchema => "Unsupported", ScenarioFileStatus.Invalid => "Invalid", _ => "Valid"
    };

    // The attached documents in order, checked as a project: document validity, duplicate paths and IDs, one scenario
    // kind per project (the first valid document decides), and binding. compiled is the target's raw catalog when it
    // was discovered; null when it was not (fixture bindings are then "not checked"). ordinaryTarget: the executable
    // cannot host diagnostic fixtures (no TSharpWorkbench diagnostic host).
    internal static AttachedScenario[] Check(IReadOnlyList<string> files, string target, ScenarioDescriptor[]? compiled, bool ordinaryTarget,
        IReadOnlyDictionary<string, ScenarioFileInfo>? inspected = null, IReadOnlyCollection<string>? legacyFixtures = null)
    {
        var result = new List<AttachedScenario>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new List<string>();
        bool? proxyProject = null;
        bool targetSet = target.Length > 0 && File.Exists(target);
        foreach (string file in files)
        {
            var info = inspected is not null && inspected.TryGetValue(file, out var known) ? known : Inspect(file);
            if (paths.Any(p => WorkbenchPreferences.SamePath(p, info.Path)))
            { result.Add(new(info, "Duplicate", "This file is attached twice; remove one reference.")); continue; }
            paths.Add(info.Path);
            if (info.Status != ScenarioFileStatus.Valid) { result.Add(new(info, StatusLabel(info.Status), info.Message)); continue; }
            var scenario = info.Document!.Scenario;
            if (ids.TryGetValue(scenario.Id, out string? other))
            { result.Add(new(info, "Duplicate ID", $"Scenario ID '{scenario.Id}' is also defined by {System.IO.Path.GetFileName(other)}; IDs must be unique in a project.")); continue; }
            if (proxyProject is { } kind && kind != info.Proxy)
            {
                result.Add(new(info, "Mixed", kind ? "This project already uses Black-Box Mode scenarios for an ordinary application; fixture-bound scenarios need a separate project with the diagnostic target."
                    : "This project already uses fixture-bound scenarios of a diagnostic target; Black-Box Mode scenarios need a separate project.")); continue;
            }
            proxyProject ??= info.Proxy;
            ids[scenario.Id] = info.Path;
            if (info.Proxy)
            {
                var bound = ExternalScenarios.Merge([], [info.Document]).Single();
                // The document is valid; whether the target deployment can run it is target diagnostics' answer
                // (the same Proxy deployment rules as Run Configuration). Not ready: status "Valid" plus the deployment note.
                string? note = null;
                if (targetSet)
                {
                    var report = TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(target), new(Proxy: true, Diagnostic: false, ProxyBackend: "NullDriver"));
                    if (report.Blocking.FirstOrDefault() is { } blocking)
                        note = $"Target deployment: not ready - {blocking.Message} {blocking.Action}";
                }
                result.Add(targetSet ? new(info, note is null ? "Ready" : "Valid", null, bound) { Note = note }
                    : new(info, "No target", "Set the application executable in Project Properties (Alt+O); Proxy scenarios run against that ordinary application.", bound));
                continue;
            }
            string fixture = info.Document.Fixture;
            if (ordinaryTarget)
            {
                result.Add(new(info, "Unbound", $"Fixture '{fixture}' cannot be provided by {System.IO.Path.GetFileName(target)}: it is an ordinary application without the "
                    + "TSharpWorkbench diagnostic host. Select the diagnostic build that registers this fixture, or write a Black-Box Mode scenario instead.")); continue;
            }
            if (compiled is null)
            {
                result.Add(new(info, "Not bound", targetSet ? $"Fixture '{fixture}' is bound when the catalog is refreshed (F2 or Tests > Scenario Manager, then F2)."
                    : $"Set the diagnostic target executable in Project Properties (Alt+O); fixture '{fixture}' is bound against it.")); continue;
            }
            try { result.Add(new(info, "Bound", null, ExternalScenarios.Merge(compiled, [info.Document], legacyFixtures).Last())); }
            catch (ExternalScenarioException ex)
            {
                string detail = Detail(ex, info.Path);
                if (!compiled.Any(s => s.Id == fixture))
                    detail = $"The selected target {System.IO.Path.GetFileName(target)} does not advertise the diagnostic fixture '{fixture}'. Select the diagnostic build that registers it.";
                result.Add(new(info, "Unbound", detail));
            }
        }
        return result.ToArray();
    }

    // The runnable raw catalog of a project: the target's compiled registrations plus every usable external scenario.
    internal static ScenarioDescriptor[] Catalog(ScenarioDescriptor[] compiled, IEnumerable<AttachedScenario> attached) =>
        [.. compiled, .. attached.Where(a => a.Usable).Select(a => a.Bound!)];

    // Catalog freshness at the run boundary. A catalog entry is an in-memory snapshot of the document read by
    // the last successful load. Stamps (length and write time, no content) of every attached file are taken BEFORE that load
    // reads them, so any later edit, deletion, addition or replacement changes them.
    internal sealed record FileStamp(long Length, DateTime WrittenUtc);

    internal static Dictionary<string, FileStamp?> Stamp(IEnumerable<string> files)
    {
        var result = new Dictionary<string, FileStamp?>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(file); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = file; }
            try { var info = new FileInfo(full); result[full] = info.Exists ? new(info.Length, info.LastWriteTimeUtc) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { result[full] = null; }
        }
        return result;
    }

    // Why a run of these selected scenarios must not start, or null. Checked synchronously before any target is launched; it
    // never patches the catalog or the marks (Refresh, F2, reloads them) and never falls back to the cached descriptor:
    // - a selected external scenario whose document is no longer attached (Remove, even while a reload is pending, was
    //   cancelled or failed) is refused;
    // - its document must still exist with exactly the bytes that were loaded (ContentHash), which also covers malformed JSON,
    //   a replacement defining another ID and a changed fixture reference;
    // - when external scenarios are selected, the attached set and every attached file's stamp must equal the loaded ones,
    //   so a duplicate ID or other change in a different file is refused until the catalog is refreshed.
    // Cost is bounded: one stat per attached file and one read+hash of each selected document (each at most 1 MiB).
    internal static string? StaleRun(IReadOnlyList<ScenarioDescriptor> selected, IReadOnlyList<string> attachedNow, IReadOnlyDictionary<string, FileStamp?>? loaded)
    {
        var external = selected.Where(s => s.External is { Source.Length: > 0 }).ToArray();
        if (external.Length == 0) return null;
        var problems = new List<string>();
        foreach (var s in external)
        {
            string source = s.External!.Source;
            string name = System.IO.Path.GetFileName(source);
            if (!attachedNow.Any(f => WorkbenchPreferences.SamePath(f, source))) { problems.Add($"'{s.Id}': {name} is no longer attached to the project"); continue; }
            byte[] bytes;
            try
            {
                if (!File.Exists(source)) { problems.Add($"'{s.Id}': {name} no longer exists on disk"); continue; }
                if (new FileInfo(source).Length > 1024 * 1024) { problems.Add($"'{s.Id}': {name} changed on disk (now larger than 1 MiB)"); continue; }
                bytes = File.ReadAllBytes(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"'{s.Id}': {name} cannot be read ({ex.Message})"); continue; }
            if (ScreenSnapshot.Hash(bytes) == s.External.ContentHash) continue;
            // Different bytes: say what the document is now (the cached descriptor is never used).
            var now = Inspect(source);
            problems.Add(now.Status != ScenarioFileStatus.Valid ? $"'{s.Id}': {name} was changed on disk and is now not usable ({StatusLabel(now.Status)})"
                : now.Id != s.Id ? $"'{s.Id}': {name} was changed on disk and now defines '{now.Id}'"
                : $"'{s.Id}': {name} was edited on disk after the catalog was loaded");
        }
        if (problems.Count == 0 && loaded is not null)
        {
            var current = Stamp(attachedNow);
            if (current.Count != loaded.Count || current.Any(kv => !loaded.TryGetValue(kv.Key, out var stamp) || stamp != kv.Value))
                problems.Add("the attached scenario files changed after the catalog was loaded");
        }
        if (problems.Count == 0) return null;
        return "Nothing was run: the loaded scenarios no longer match the attached JSON files. " + string.Join("; ", problems.Take(5))
            + (problems.Count > 5 ? $"; and {problems.Count - 5} more" : "") + ". Refresh Scenario Manager with F2, check the marks, then run again.";
    }

    // A folder of candidate documents. Only *.json files whose content declares the scenario schema are candidates;
    // anything else (manifests, run.json, project metadata) is listed as ignored, never attached.
    internal sealed record FolderScan(string Folder, bool Recursive, ScenarioFileInfo[] Add, ScenarioFileInfo[] AlreadyAttached,
        (ScenarioFileInfo Info, string Reason)[] Rejected, ScenarioFileInfo[] Ignored, bool Truncated);

    internal static FolderScan ScanFolder(string folder, bool recursive, IReadOnlyList<string> attached, CancellationToken token = default)
    {
        folder = System.IO.Path.GetFullPath(folder);
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(folder, "*.json", options).Take(FolderLimit + 1).ToList();
        bool truncated = files.Count > FolderLimit;
        if (truncated) files.RemoveAt(FolderLimit);
        // The chosen folder's own files first, then each subfolder's, alphabetically.
        files = files.OrderBy(f => System.IO.Path.GetRelativePath(folder, f).Count(c => c == System.IO.Path.DirectorySeparatorChar))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var current = Check(attached, "", null, false);
        var ids = new HashSet<string>(current.Where(a => a.Info.Id is not null).Select(a => a.Info.Id!), StringComparer.Ordinal);
        bool? proxyProject = current.FirstOrDefault(a => a.Info.Status == ScenarioFileStatus.Valid && a.Status != "Mixed" && a.Status != "Duplicate ID")?.Info.Proxy;
        List<ScenarioFileInfo> add = [], already = [], ignored = []; var rejected = new List<(ScenarioFileInfo, string)>();
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            var info = Inspect(file);
            if (attached.Any(a => WorkbenchPreferences.SamePath(a, info.Path))) { already.Add(info); continue; }
            if (info.Status == ScenarioFileStatus.NotScenario) { ignored.Add(info); continue; }
            if (info.Status != ScenarioFileStatus.Valid) { rejected.Add((info, StatusLabel(info.Status) + ": " + info.Message)); continue; }
            if (!ids.Add(info.Id!)) { rejected.Add((info, $"Duplicate ID '{info.Id}': another attached or found document already uses it.")); continue; }
            if (proxyProject is { } kind && kind != info.Proxy)
            {
                ids.Remove(info.Id!);
                rejected.Add((info, kind ? "Fixture-bound document; this project uses Black-Box Mode scenarios." : "Black-Box Mode document; this project uses fixture-bound scenarios.")); continue;
            }
            proxyProject ??= info.Proxy;
            add.Add(info);
        }
        return new(folder, recursive, add.ToArray(), already.ToArray(), rejected.ToArray(), ignored.ToArray(), truncated);
    }

    // Whether the target can host compiled diagnostic scenarios or fixtures: its managed assembly references
    // TSharpWorkbench.Testing, directly or through assemblies deployed beside it. False only when that is known to be
    // absent (an ordinary application: launching it for discovery would wait for a handshake that never comes);
    // null when the executable cannot be inspected (discovery then decides, as before).
    internal static bool? HostsDiagnostics(string executable)
    {
        try
        {
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(executable))!;
            string? main = new[] { executable, System.IO.Path.ChangeExtension(executable, ".dll") }.Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(p => File.Exists(p) && References(p) is not null);
            if (main is null) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>([main]);
            while (pending.Count > 0 && seen.Count < 64)
            {
                string assembly = pending.Dequeue();
                if (!seen.Add(System.IO.Path.GetFileNameWithoutExtension(assembly))) continue;
                foreach (string name in References(assembly) ?? [])
                {
                    if (name == "TSharpWorkbench.Testing") return true;
                    string local = System.IO.Path.Combine(folder, name + ".dll");
                    if (!seen.Contains(name) && File.Exists(local)) pending.Enqueue(local);
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException or InvalidOperationException) { return null; }
    }
    private static string[]? References(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var reader = pe.GetMetadataReader();
            return reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    // A minimal valid schema-v1 document. Proxy: one checkpoint of the ordinary application's first screen. Fixture-bound:
    // the chosen advertised fixture and, for a fixture-only registration, its first advertised checkpoint expectation
    // (strict binding); nothing else is invented. The result is validated by the caller before it is written.
    internal static string Template(string id, string title, string group, ScenarioDescriptor? fixture)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", ExternalScenarios.Schema);
            w.WriteNumber("version", 1);
            w.WriteString("id", id);
            w.WriteString("title", title);
            w.WriteString("description", fixture is null
                ? "Black-Box Mode scenario for an ordinary application. Add key, text and mouse steps, and assertions to the checkpoints."
                : $"Fixture-bound scenario for the diagnostic fixture {fixture.Id}. Add steps that use its advertised actions, points and checkpoint expectations.");
            w.WriteString("group", group);
            if (fixture is null) w.WriteString("backend", "proxy-logical"); else w.WriteString("fixture", fixture.Id);
            w.WriteString("expectedOutcome", "passed");
            w.WriteStartArray("steps");
            w.WriteStartObject();
            w.WriteString("kind", "checkpoint");
            w.WriteString("id", fixture?.Contract?.Checkpoints.FirstOrDefault() ?? "initial");
            w.WriteString("note", fixture is null ? "First screen of the application. Add assertions such as screen-contains."
                : fixture.IsFixtureOnly ? "Checkpoint IDs must name the fixture's advertised checkpoint expectations." : "Initial state of the fixture.");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine;
    }

    // Fixtures an external document can bind: registrations of the target that advertise adapters (not external ones).
    // Fixture-only registrations (which exist only to be bound) come first, then compiled scenarios usable as legacy fixtures.
    // Strict by default: fixture-only registrations, plus compiled scenarios the project explicitly allows as legacy fixtures.
    internal static ScenarioDescriptor[] BindableFixtures(IEnumerable<ScenarioDescriptor> compiled, IReadOnlyCollection<string>? legacyFixtures = null) =>
        compiled.Where(s => s.External is null && s.Adapters is not null && (s.IsFixtureOnly || legacyFixtures?.Contains(s.Id) == true)).OrderBy(s => !s.IsFixtureOnly).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();

    // Open in Editor: an explicitly configured editor (TSHARPWORKBENCH_EDITOR: an executable path or name, no arguments),
    // otherwise Notepad on Windows, "open -t" on macOS and xdg-open elsewhere. The document is passed as one argument
    // without a shell, so spaces in paths are safe and opening never executes the document.
    internal const string EditorVariable = "TSHARPWORKBENCH_EDITOR";
    internal static ProcessStartInfo EditorCommand(string path)
    {
        string? configured = Environment.GetEnvironmentVariable(EditorVariable)?.Trim().Trim('"');
        var start = new ProcessStartInfo(configured is { Length: > 0 } ? configured : OperatingSystem.IsWindows() ? "notepad.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(path)! };
        if (configured is not { Length: > 0 } && OperatingSystem.IsMacOS()) start.ArgumentList.Add("-t");
        start.ArgumentList.Add(path);
        return start;
    }
}
