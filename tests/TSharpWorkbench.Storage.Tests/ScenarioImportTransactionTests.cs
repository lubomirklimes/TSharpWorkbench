using Xunit;

namespace TSharpWorkbench.Storage.Tests;

public sealed class ScenarioImportTransactionTests
{
    [Fact]
    public void FailedSecondReferenceRollsBackFirstReferenceAndBothPayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "tsw-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "import.tsw");
            string work = Path.Combine(root, "work");
            using var project = ProjectSession.Create(archive, "Import", work);
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => project.ImportScenarios([
                (new("first", "scenarios/first.json", null, "proxy-logical"), new byte[] { 1 }),
                (new("second", "scenarios/second.json", null, "invalid"), new byte[] { 2 })
            ]));
            Assert.Empty(project.Scenarios);
            Assert.False(File.Exists(project.ResolveArtifact("scenarios/first.json")));
            Assert.False(File.Exists(project.ResolveArtifact("scenarios/second.json")));
            project.Close(CloseChoice.Save);
        }
        finally { StorageFiles.RemoveTree(root); }
    }
}
