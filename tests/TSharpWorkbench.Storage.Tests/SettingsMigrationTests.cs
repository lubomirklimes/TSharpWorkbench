using Xunit;

namespace TSharpWorkbench.Storage.Tests;

public sealed class SettingsMigrationTests
{
    [Fact]
    public void VersionOneArchiveKeepsIdentityAndPersistsTypedSettingsOnUpgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "tsw-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "test.tsw");
            var work = Path.Combine(root, "work");
            string id;
            using (var created = ProjectSession.Create(archive, "Existing", work))
            {
                id = created.Project.Id;
                created.Close(CloseChoice.Save);
            }
            using (var project = ProjectSession.Open(archive, workspaceRoot: work))
            {
                Assert.Empty(project.Settings);
                project.PutSettings(new Dictionary<string, string> { ["TargetArguments"] = """["one","two"]""", ["Timeout"] = "50" });
                project.Close(CloseChoice.Save);
            }
            using var reopened = ProjectSession.Open(archive, workspaceRoot: work);
            Assert.Equal(id, reopened.Project.Id);
            Assert.Equal("""["one","two"]""", reopened.Settings["TargetArguments"]);
            Assert.Equal("50", reopened.Settings["Timeout"]);
            reopened.Close(CloseChoice.Save);
        }
        finally { StorageFiles.RemoveTree(root); }
    }
}
