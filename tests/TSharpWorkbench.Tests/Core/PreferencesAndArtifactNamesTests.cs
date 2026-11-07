using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

public sealed class PreferencesAndArtifactNamesTests
{
    [Fact]
    public void PreferencesSurviveRestartAndExplicitArgumentsWin()
    {
        string root = Path.Combine(Path.GetTempPath(), "preferences-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "settings.json");
        try
        {
            var saved = new WorkbenchPreferences("saved.exe", "saved-output", "App/D1", "both");
            saved.Save(path);
            Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(WorkbenchPreferences.Load(path)));
            var explicitOptions = new Dictionary<string, string> { ["--target"] = "explicit.exe", ["--output"] = "explicit-output", ["--size"] = "140x40" };
            var resolved = WorkbenchPreferences.Resolve(explicitOptions, path);
            Assert.Equal("explicit.exe", resolved.Target); Assert.Equal("explicit-output", resolved.Output);
            Assert.Null(resolved.Group); Assert.Equal("140x40", resolved.Size); // target change invalidates catalog-specific preferences
            File.WriteAllText(path, "broken json");
            Assert.Equal("explicit.exe", WorkbenchPreferences.Resolve(explicitOptions, path).Target);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            string json = JsonSerializer.Serialize(saved);
            Assert.DoesNotContain("Secret", json); Assert.DoesNotContain("Token", json);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FlatNamesEncodeScenarioCheckpointAndGridAndRejectCollisions()
    {
        Assert.Equal("sample-menu-navigation--menu-open--80x25", ArtifactNames.Stem("sample/menu/navigation", "menu-open", "80x25"));
        Assert.Equal("sample-menu-navigation--menu-open--80x25", ArtifactNames.Stem("sample/menu/navigation", "menu-open", "80x25", "80x25"));
        // After an in-scenario resize the current grid names the file; the initial grid keeps executions apart.
        Assert.Equal("sample-screen-resize--resized--100x30-from-140x40", ArtifactNames.Stem("sample/screen/resize", "resized", "100x30", "140x40"));
        Assert.Equal("con", ArtifactNames.Slug("../../CON"));
        Assert.True(ArtifactNames.Slug(new string('x', 300)).Length <= 80);
        Assert.DoesNotContain("--", ArtifactNames.Slug("a//b--c__d"));
        static ScenarioDescriptor Entry(string id, string size) =>
            new(id, id, "unsupported grid", "App", Only(size), [new(ScenarioStep.Checkpoint, "initial")]);
        static ScenarioGrid Only(string size) { var (w, h) = ScenarioSelection.ParseSize(size); return new(w, h, w, h, [size]); }
        string root = Path.Combine(Path.GetTempPath(), "tw-flat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new ScenarioRunner();
            // Slug collisions (path separators, case) are refused before any run directory exists.
            foreach (var clash in new[] { ("app/dialog/a-b", "app/dialog-a/b"), ("app/dialog/a-b", "APP/dialog/a-b") })
                await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync("unused", [Entry(clash.Item1, "80x25"), Entry(clash.Item2, "80x25")], ["80x25"], root));
            Assert.False(Directory.Exists(root) && Directory.EnumerateDirectories(root).Any());
            var entries = new[] { Entry("app/one", "140x40"), Entry("café/状態", "140x40") };
            var batch = await runner.RunAsync("unused", entries, ["80x25", "80x25"], root);
            Assert.Equal(2, batch.Results.Length);
            Assert.All(batch.Results, r => Assert.Equal("Blocked", r.Outcome));
            // One flat directory: no png/, json/, bin/ or per-scenario folders.
            Assert.Empty(Directory.GetDirectories(batch.RunRoot));
            Assert.Equal(new[] { "report.md", "run.json" }, Directory.GetFiles(batch.RunRoot).Select(Path.GetFileName).Order());
            var subset = await runner.RunAsync("unused", [entries[1]], ["80x25"], root);
            Assert.NotEqual(batch.RunRoot, subset.RunRoot);
            Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(subset.RunRoot));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
