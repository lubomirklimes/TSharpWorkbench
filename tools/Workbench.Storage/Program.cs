using TSharpWorkbench.Storage;

// Deliberately separate from the legacy runner/TUI: no implied conversion or execution of a .tsw project.
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Workbench.Storage <create|inspect|rename|attach> <project.tsw> [name | artifact-path source-file] [--recover|--discard]");
    return 2;
}
try
{
    RecoveryChoice recovery = args.Contains("--recover") ? RecoveryChoice.Recover : args.Contains("--discard") ? RecoveryChoice.Discard : RecoveryChoice.Cancel;
    if (args.Contains("--recover") && args.Contains("--discard")) throw new ArgumentException("Choose one recovery action.");
    var arguments = args.ToList();
    string? workspaceRoot = null;
    int rootOption = arguments.IndexOf("--workspace-root");
    if (rootOption >= 0)
    {
        if (rootOption + 1 >= arguments.Count) throw new ArgumentException("Workspace root is required.");
        workspaceRoot = arguments[rootOption + 1]; arguments.RemoveRange(rootOption, 2);
    }
    string[] positional = arguments.Where(a => a is not ("--recover" or "--discard")).ToArray();
    if (positional[0] is not ("create" or "inspect" or "rename" or "attach")) throw new ArgumentException("Unknown storage command.");
    int expected = positional[0] == "attach" ? 4 : positional[0] == "inspect" ? 2 : 3;
    if (positional.Length != expected) throw new ArgumentException("Incorrect number of arguments.");
    using var project = positional[0] == "create" ? ProjectSession.Create(positional[1], positional[2], workspaceRoot) : ProjectSession.Open(positional[1], recovery, workspaceRoot);
    switch (positional[0])
    {
        case "rename": project.Rename(positional[2]); break;
        case "attach":
            using (var source = File.OpenRead(positional[3])) project.PutArtifact(positional[2], source);
            break;
    }
    Console.WriteLine($"{project.Project.Id}: {project.Project.Name}");
    Console.WriteLine($"{project.Tests.Count} tests, {project.Environments.Count} environments, {project.Scenarios.Count} scenario references");
    // Inspect never commits recovered choices; Dispose preserves the recoverable workspace.
    if (positional[0] != "inspect" || !project.IsDirty) project.Close(CloseChoice.Save);
    return 0;
}
catch (RecoveryRequiredException ex)
{
    Console.Error.WriteLine("Recovery required. Retry with --recover or --discard; default Cancel preserves all data.");
    Console.Error.WriteLine("Workspace: " + ex.Workspace); return 3;
}
catch (Exception ex) when (ex is IOException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Storage operation failed (" + ex.GetType().Name + "). Archive and recovery state were not intentionally discarded.");
    return 1;
}
