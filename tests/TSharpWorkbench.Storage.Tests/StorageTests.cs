using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TSharpWorkbench.Storage;
using Xunit;

namespace TSharpWorkbench.Storage.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TSW-storage-tests-" + Guid.NewGuid().ToString("N"));
    private string Archive => Path.Combine(root, "project.tsw");
    private string Work => Path.Combine(root, "work");
    public StorageTests() => Directory.CreateDirectory(root);
    public void Dispose() => StorageFiles.RemoveTree(root);
    private ProjectSession Create() => ProjectSession.Create(Archive, "Original", Work);
    private ProjectSession Open(RecoveryChoice choice = RecoveryChoice.Cancel) => ProjectSession.Open(Archive, choice, Work);
    private void Saved() { using var p = Create(); p.Close(CloseChoice.Save); }
    private static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TSharpWorkbench.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository test assets not found.");
    }
    [Theory]
    [InlineData("unsaved")]
    [InlineData("transaction")]
    public async Task KilledProcessReleasesLocksAndRollsBackUncommittedSqlite(string mode)
    {
        Saved();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string probe = Path.Combine(Repository(), "tests", "TSharpWorkbench.Storage.CrashProbe", "bin", configuration, "net10.0", "TSharpWorkbench.Storage.CrashProbe.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { probe, Archive, Work, mode }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            if (mode == "transaction") Assert.NotEmpty(Directory.GetFiles(Work, "project.sqlite-journal", SearchOption.AllDirectories));
            child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            Assert.Throws<RecoveryRequiredException>(() => Open());
            using var recovered = Open(RecoveryChoice.Recover);
            Assert.Equal("Recovered after kill", recovered.Project.Name); Assert.Empty(recovered.Environments);
            using var artifact = new MemoryStream(); recovered.CopyArtifactTo("attachments/interrupted.bin", artifact);
            Assert.Equal(new byte[] { 4, 5, 6 }, artifact.ToArray()); recovered.Close(CloseChoice.Save);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }
    [Fact]
    public void LocalCommanderScenarioDocumentsRemainByteIdentical()
    {
        string folder = Path.Combine(Repository(), "examples", "TSharpCommander", "scenarios");
        var sources = Directory.GetFiles(folder, "*.json").ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
        Assert.Equal(4, sources.Count);
        using (var p = Create())
        {
            foreach (var (name, bytes) in sources)
            {
                using var json = JsonDocument.Parse(bytes); var scenario = json.RootElement;
                string artifact = "scenarios/" + name;
                p.PutArtifact(artifact, new MemoryStream(bytes));
                p.PutScenario(new(scenario.GetProperty("id").GetString()!, artifact,
                    scenario.TryGetProperty("fixture", out var fixture) ? fixture.GetString() : null, scenario.GetProperty("backend").GetString()!));
            }
            p.Close(CloseChoice.Save);
        }
        using var reopened = Open();
        Assert.Equal(4, reopened.Scenarios.Count);
        foreach (var (name, bytes) in sources)
        {
            using var target = new MemoryStream(); reopened.CopyArtifactTo("scenarios/" + name, target);
            Assert.Equal(bytes, target.ToArray()); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, name!)));
        }
        reopened.Close(CloseChoice.Save);
    }
    [Fact]
    public void OversizedCentralDirectoryLengthIsRejectedBeforeExtraction()
    {
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create))
        { zip.CreateEntry("manifest.json"); zip.CreateEntry("project.sqlite"); }
        byte[] data = File.ReadAllBytes(Archive);
        for (int i = 0; i < data.Length - 28; i++)
            if (BitConverter.ToUInt32(data, i) == 0x02014b50)
            { BitConverter.GetBytes(uint.MaxValue).CopyTo(data, i + 24); break; }
        File.WriteAllBytes(Archive, data);
        var ex = Assert.Throws<InvalidDataException>(() => Open()); Assert.Contains("limit", ex.Message);
    }
    [Theory]
    [InlineData("count")]
    [InlineData("directory-size")]
    [InlineData("multi-volume")]
    public void ZipMetadataIsBoundedBeforeMaterialization(string kind)
    {
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create))
        { zip.CreateEntry("manifest.json"); zip.CreateEntry("project.sqlite"); }
        byte[] data = File.ReadAllBytes(Archive); int end = data.Length - 22;
        if (kind == "count") BitConverter.GetBytes((ushort)10001).CopyTo(data, end + 10);
        else if (kind == "directory-size") BitConverter.GetBytes(32 * 1024 * 1024).CopyTo(data, end + 12);
        else BitConverter.GetBytes((ushort)1).CopyTo(data, end + 4);
        File.WriteAllBytes(Archive, data);
        Assert.Throws<InvalidDataException>(() => Open());
    }
    [Fact]
    public void ArtifactCaseAliasesAndUnknownRecoveryChoicesAreRejected()
    {
        using var p = Create(); p.PutArtifact("scenarios/Folder/a.json", new MemoryStream([1]));
        Assert.Throws<ArgumentException>(() => p.PutArtifact("scenarios/folder/b.json", new MemoryStream([2])));
        Assert.Throws<ArgumentException>(() => p.PutScenario(new("s", "scenarios/folder/a.json", null, "proxy-logical")));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Close((CloseChoice)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectSession.Open(Archive, (RecoveryChoice)99, Work));
        p.Close(CloseChoice.Save);
    }
    private string ReadName(string archive)
    {
        string copy = Path.Combine(root, Guid.NewGuid() + ".tsw"); File.Copy(archive, copy);
        using var p = ProjectSession.Open(copy, workspaceRoot: Work); string name = p.Project.Name; p.Close(CloseChoice.Save); return name;
    }
    [Fact]
    public void CompleteRoundTripPreservesStableIdentitiesAndOpaqueBinary()
    {
        byte[] data = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray(); string id;
        using (var p = Create())
        {
            id = p.Project.Id;
            Assert.Empty(p.Environments); // Default provisioning is stage 2.
            p.PutEnvironment(new("env-windows", "Windows", "host", "SDLDriver", 140, 40, "proxy-logical"));
            p.PutEnvironment(new("env-linux", "Linux configuration", "linux", "NullDriver", 80, 25, "diagnostic-logical", false));
            p.PutArtifact("scenarios/original.json", new MemoryStream(data));
            p.PutArtifact("baselines/capture.png", new MemoryStream(data));
            p.PutScenario(new("commander/original", "scenarios/original.json", "fixture/original", "diagnostic-logical"));
            p.PutTest(new("test-1", "Title", "Description", "Human preconditions", "commander/original", ["env-windows", "env-linux"]));
            Assert.True(p.IsDirty); p.Save(); Assert.False(p.IsDirty); p.Close(CloseChoice.Save);
        }
        using var reopened = Open();
        Assert.Equal(id, reopened.Project.Id); Assert.Equal(2, reopened.Environments.Count);
        Assert.Equal("fixture/original", reopened.Scenarios.Single().FixtureId);
        Assert.Equal("commander/original", reopened.Tests.Single().ScenarioId);
        Assert.Equal("Human preconditions", reopened.Tests.Single().Preconditions);
        Assert.Equal(2, reopened.Tests.Single().EnvironmentIds.Count);
        using var result = new MemoryStream(); reopened.CopyArtifactTo("baselines/capture.png", result);
        Assert.Equal(data, result.ToArray()); reopened.Close(CloseChoice.Save);
    }
    [Fact]
    public void SecondAndThirdSavesRotatePreviousCompleteArchive()
    {
        using var p = Create(); p.Save(); string first = StorageFiles.Hash(Archive);
        p.Rename("Second"); p.Save(); Assert.Equal(first, StorageFiles.Hash(Archive + ".bak"));
        string second = StorageFiles.Hash(Archive); p.Rename("Third"); p.Save();
        Assert.Equal(second, StorageFiles.Hash(Archive + ".bak")); Assert.Equal("Second", ReadName(Archive + ".bak"));
        Assert.Equal("Third", ReadName(Archive)); p.Close(CloseChoice.Save);
    }
    [Fact]
    public void RecoveryCancelRecoverAndDiscardAreExplicit()
    {
        Saved(); string workspace;
        using (var p = Open())
        {
            workspace = p.WorkspacePath; p.Rename("Unsaved");
            p.PutArtifact("attachments/unsaved.bin", new MemoryStream([1, 2, 3]));
            Assert.False(p.Close(CloseChoice.Cancel)); Assert.Equal("Unsaved", p.Project.Name);
        }
        Assert.Throws<RecoveryRequiredException>(() => Open()); Assert.True(Directory.Exists(workspace));
        using (var p = Open(RecoveryChoice.Recover))
        {
            Assert.True(p.IsDirty); Assert.Equal("Unsaved", p.Project.Name);
            using var data = new MemoryStream(); p.CopyArtifactTo("attachments/unsaved.bin", data); Assert.Equal(3, data.Length);
            p.Close(CloseChoice.Save);
        }
        using (var p = Open()) p.Rename("Discard me");
        using (var p = Open(RecoveryChoice.Discard)) { Assert.Equal("Unsaved", p.Project.Name); p.Close(CloseChoice.Save); }
        Assert.Single(Directory.GetDirectories(Work, "*.discarded-*"));
    }
    [Fact]
    public void UnsavedNewProjectCanBeRecoveredWithoutAnArchive()
    {
        using (var p = Create()) p.PutArtifact("attachments/new.bin", new MemoryStream([9]));
        Assert.False(File.Exists(Archive)); Assert.Throws<RecoveryRequiredException>(() => Open());
        using var recovered = Open(RecoveryChoice.Recover); recovered.Close(CloseChoice.Save);
        Assert.Equal("Original", ReadName(Archive));
    }
    [Theory]
    [InlineData("before-pack")]
    [InlineData("before-replace")]
    public void FailedSavePreservesArchiveBackupAndRecovery(string boundary)
    {
        using (var p = Create())
        {
            p.Save(); p.Rename("Second"); p.Save(); string current = StorageFiles.Hash(Archive), backup = StorageFiles.Hash(Archive + ".bak");
            p.Rename("Unsaved"); p.Fault = point => { if (point == boundary) throw new IOException("Injected write/replace failure"); };
            Assert.Throws<IOException>(() => p.Save()); Assert.True(p.IsDirty);
            Assert.Equal(current, StorageFiles.Hash(Archive)); Assert.Equal(backup, StorageFiles.Hash(Archive + ".bak"));
        }
        using var recovered = Open(RecoveryChoice.Recover); Assert.Equal("Unsaved", recovered.Project.Name); recovered.Close(CloseChoice.Save);
    }
    [Fact]
    public void ReplacementCompletedBeforeCheckpointCanBeRecovered()
    {
        using (var p = Create())
        {
            p.Save(); p.Rename("Committed");
            p.Fault = point => { if (point == "after-replace") throw new IOException("Simulated stop before checkpoint"); };
            Assert.Throws<IOException>(() => p.Save()); Assert.True(p.IsDirty);
        }
        Assert.Equal("Original", ReadName(Archive + ".bak"));
        Assert.Throws<RecoveryRequiredException>(() => Open());
        using var recovered = Open(RecoveryChoice.Recover); Assert.Equal("Committed", recovered.Project.Name); recovered.Close(CloseChoice.Save);
    }
    [Fact]
    public void FinalCheckpointIoFailureKeepsDirtyRecoveryState()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var p = Create())
        {
            p.Save(); p.Rename("Checkpoint pending"); FileStream? occupied = null;
            p.Fault = point =>
            {
                if (point == "after-replace") occupied = new FileStream(Path.Combine(p.WorkspacePath, "session.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            };
            try
            {
                var failure = Record.Exception(() => p.Save());
                Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
                Assert.True(p.IsDirty);
            }
            finally { occupied?.Dispose(); }
        }
        using var recovered = Open(RecoveryChoice.Recover); Assert.Equal("Checkpoint pending", recovered.Project.Name); recovered.Close(CloseChoice.Save);
    }
    [Fact]
    public void OccupiedDestinationFailsWithoutLosingDataOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix unlink semantics differ; fault injection tests are portable.
        using (var p = Create())
        {
            p.Save(); p.Rename("Pending");
            using (var occupied = new FileStream(Archive, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => p.Save());
            Assert.True(p.IsDirty); Assert.Equal("Original", ReadName(Archive));
        }
        using var recovered = Open(RecoveryChoice.Recover); Assert.Equal("Pending", recovered.Project.Name); recovered.Close(CloseChoice.Save);
    }
    [Fact]
    public void DuplicateOpensAcrossRootsAreRejected()
    {
        using var p = Create(); p.Save();
        Assert.Throws<ProjectConflictException>(() => Open());
        Assert.Throws<ProjectConflictException>(() => ProjectSession.Open(Archive, workspaceRoot: Path.Combine(root, "other-root")));
        p.Close(CloseChoice.Save);
    }
    [Fact]
    public void ExternalModificationPreventsSaveAndRecover()
    {
        using (var p = Create())
        {
            p.Save(); p.Rename("Keep me"); File.WriteAllText(Archive, "external change");
            Assert.Throws<ProjectConflictException>(() => p.Save()); Assert.True(p.IsDirty);
        }
        Assert.Throws<ProjectConflictException>(() => Open(RecoveryChoice.Recover));
        Assert.Throws<RecoveryRequiredException>(() => Open());
    }
    [Fact]
    public void InvalidForeignKeysRollbackWholeTestUpdate()
    {
        using var p = Create();
        p.PutEnvironment(new("e", "Environment", "host", "SDLDriver", 80, 25, "proxy-logical"));
        p.PutScenario(new("s", null, null, "proxy-logical"));
        p.PutTest(new("t", "Old", "", "", "s", ["e"]));
        Assert.Throws<SqliteException>(() => p.PutTest(new("t", "Bad", "", "", "missing", ["e"])));
        Assert.Throws<SqliteException>(() => p.PutTest(new("t", "Bad", "", "", "s", ["e", "missing"])));
        Assert.Throws<ArgumentException>(() => p.PutTest(new("empty", "Bad", "", "", "s", [])));
        Assert.Equal("Old", p.Tests.Single().Title); Assert.Equal(["e"], p.Tests.Single().EnvironmentIds);
        Assert.Throws<ArgumentException>(() => p.PutScenario(new("s2", "scenarios/missing.json", null, "proxy-logical")));
        Assert.Throws<SqliteException>(() => p.PutEnvironment(new("e2", "Environment", "host", "SDLDriver", 80, 25, "proxy-logical")));
        Assert.Throws<ArgumentException>(() => p.PutEnvironment(new("", "Bad", "host", "SDLDriver", 80, 25, "proxy-logical")));
        p.Close(CloseChoice.Save);
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("attachments/../escape")]
    [InlineData("attachments\\escape")]
    [InlineData("attachments/file:stream")]
    [InlineData("attachments/CON.txt")]
    [InlineData("attachments/file.")]
    [InlineData("unknown/file")]
    public void UnsafeNamesAreRejected(string name)
    {
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create))
        { zip.CreateEntry("manifest.json"); zip.CreateEntry(name); }
        Assert.ThrowsAny<InvalidDataException>(() => Open()); Assert.False(File.Exists(Path.Combine(root, "escape")));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("case-alias")]
    [InlineData("directory-case-alias")]
    [InlineData("symlink")]
    [InlineData("directory")]
    [InlineData("conflict")]
    [InlineData("bomb")]
    public void HostileZipStructuresAreRejected(string kind)
    {
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("manifest.json"); var e = zip.CreateEntry("attachments/file");
            switch (kind)
            {
                case "duplicate": zip.CreateEntry("attachments/file"); break;
                case "case-alias": zip.CreateEntry("attachments/FILE"); break;
                case "directory-case-alias": zip.CreateEntry("attachments/Dir/a"); zip.CreateEntry("attachments/dir/b"); break;
                case "symlink": e.ExternalAttributes = unchecked((int)0xA1FF0000); break;
                case "directory": zip.CreateEntry("attachments/dir/"); break;
                case "conflict": zip.CreateEntry("attachments/file/child"); break;
                case "bomb":
                    using (var target = zip.CreateEntry("attachments/bomb", CompressionLevel.SmallestSize).Open())
                        for (int i = 0; i < 2048; i++) target.Write(new byte[8192]);
                    break;
            }
        }
        Assert.ThrowsAny<InvalidDataException>(() => Open());
    }
    [Theory]
    [InlineData("truncate")]
    [InlineData("sqlite")]
    [InlineData("db-version")]
    [InlineData("manifest-version")]
    [InlineData("missing-db")]
    [InlineData("checksum")]
    [InlineData("schema")]
    public void CorruptAndUnsupportedArchivesAreRejected(string kind)
    {
        Saved();
        if (kind == "truncate")
        { using var stream = File.OpenWrite(Archive); stream.SetLength(30); }
        else
        {
            string staging = Path.Combine(root, "edit"); var manifest = ProjectArchive.Extract(Archive, staging);
            switch (kind)
            {
                case "sqlite": File.WriteAllBytes(Path.Combine(staging, "project.sqlite"), new byte[4096]); break;
                case "db-version": case "schema":
                    using (var db = ProjectDatabase.Connect(Path.Combine(staging, "project.sqlite")))
                        ProjectDatabase.Execute(db, kind == "db-version" ? "PRAGMA user_version=2" : "CREATE TABLE Unrecognized(Id TEXT)");
                    break;
            }
            File.Delete(Archive); ProjectArchive.Pack(staging, Archive, manifest.ProjectId);
            if (kind is "manifest-version" or "missing-db" or "checksum")
            {
                using var zip = ZipFile.Open(Archive, ZipArchiveMode.Update);
                if (kind == "missing-db") zip.GetEntry("project.sqlite")!.Delete();
                else if (kind == "checksum")
                { using var db = zip.GetEntry("project.sqlite")!.Open(); db.WriteByte(0); }
                else
                {
                    var old = zip.GetEntry("manifest.json")!; string json;
                    using (var reader = new StreamReader(old.Open())) json = reader.ReadToEnd(); old.Delete();
                    using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(json.Replace("\"Version\": 1", "\"Version\": 99"));
                }
            }
        }
        var failure = Record.Exception(() => Open());
        Assert.True(failure is InvalidDataException or SqliteException or JsonException, failure?.ToString() ?? "Expected rejection");
        // Failed input never establishes an active recovery workspace.
        Assert.Empty(Directory.GetDirectories(Work));
    }
    [Fact]
    public void LargeArtifactIsReadWithBoundedBuffersAndRetainsHash()
    {
        using var p = Create(); using var source = new GeneratedStream(32 * 1024 * 1024);
        p.PutArtifact("runs/large.bin", source); Assert.InRange(source.LargestRequest, 1, 81920);
        p.Close(CloseChoice.Save);
        using var reopened = Open(); using var sink = new CountingStream(); reopened.CopyArtifactTo("runs/large.bin", sink);
        Assert.Equal(32 * 1024 * 1024, sink.Count); Assert.InRange(sink.LargestWrite, 1, 81920); reopened.Close(CloseChoice.Save);
    }
    [Fact]
    public void FailedArtifactCopyKeepsPreviousCompleteFile()
    {
        using var p = Create(); p.PutArtifact("attachments/data", new MemoryStream([1, 2, 3]));
        Assert.Throws<IOException>(() => p.PutArtifact("attachments/data", new FailingStream()));
        using var result = new MemoryStream(); p.CopyArtifactTo("attachments/data", result); Assert.Equal(new byte[] { 1, 2, 3 }, result.ToArray());
        p.Close(CloseChoice.Save);
    }
    private class GeneratedStream(long length) : Stream
    {
        private long position; public int LargestRequest { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRequest = Math.Max(count, LargestRequest); int take = (int)Math.Min(count, length - position);
            for (int i = 0; i < take; i++) buffer[offset + i] = (byte)((position + i) % 251);
            position += take; return take;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class FailingStream() : GeneratedStream(0)
    { public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Injected stream failure"); }
    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; } public int LargestWrite { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) Assert.Equal((byte)((Count + i) % 251), buffer[offset + i]);
            Count += count; LargestWrite = Math.Max(count, LargestWrite);
        }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Count; public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
