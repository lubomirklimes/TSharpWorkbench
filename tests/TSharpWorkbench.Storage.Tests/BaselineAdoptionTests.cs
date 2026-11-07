using Xunit;

namespace TSharpWorkbench.Storage.Tests;

public sealed class BaselineAdoptionTests
{
    [Fact]
    public void RejectedAdoptionDoesNotPublishAndSuccessfulAdoptionPreservesSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "tsw-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.json"), archive = Path.Combine(root, "test.tsw");
            byte[] bytes = [1, 2, 3];
            File.WriteAllBytes(source, bytes);
            string work = Path.Combine(root, "work");
            using (var session = ProjectSession.Create(archive, "Baseline", work))
            {
                Assert.Throws<InvalidDataException>(() => session.AdoptBaselineStore(source, _ => throw new InvalidDataException()));
                Assert.Empty(session.ListArtifacts("baselines/"));
                session.AdoptBaselineStore(source, copy => Assert.Equal(bytes, File.ReadAllBytes(copy)));
                Assert.Equal(bytes, File.ReadAllBytes(source));
                session.Close(CloseChoice.Save);
            }
            File.Move(source, Path.Combine(root, "source-away.json"));
            using var reopened = ProjectSession.Open(archive, workspaceRoot: work);
            Assert.Equal(bytes, File.ReadAllBytes(reopened.ResolveArtifact("baselines/store/visual-baselines.json")));
            reopened.Close(CloseChoice.Save);
        }
        finally { StorageFiles.RemoveTree(root); }
    }
}
