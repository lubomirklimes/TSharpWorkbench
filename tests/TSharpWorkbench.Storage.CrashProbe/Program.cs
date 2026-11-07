using Microsoft.Data.Sqlite;
using TSharpWorkbench.Storage;

// Test-only subprocess deliberately killed by its parent. Never part of the product CLI.
using var project = ProjectSession.Open(args[0], workspaceRoot: args[1]);
project.Rename("Recovered after kill");
project.PutArtifact("attachments/interrupted.bin", new MemoryStream([4, 5, 6]));
if (args[2] == "transaction")
{
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
        DataSource = Path.Combine(project.WorkspacePath, "content", "project.sqlite"), Pooling = false }.ToString());
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = "PRAGMA cache_size=1; PRAGMA synchronous=FULL; BEGIN IMMEDIATE; UPDATE Project SET Name='Uncommitted';";
    command.ExecuteNonQuery();
    command.CommandText = "INSERT INTO Environments VALUES('e'||$i,'env'||$i,'host','SDLDriver',80,25,'proxy-logical',1)";
    var parameter = command.Parameters.AddWithValue("$i", 0);
    for (int i = 0; i < 2000; i++) { parameter.Value = i; command.ExecuteNonQuery(); }
    Console.WriteLine("READY"); Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
}
else
{
    Console.WriteLine("READY"); Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
}
