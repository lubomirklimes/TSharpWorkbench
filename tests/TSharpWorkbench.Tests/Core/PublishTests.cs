using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// the Proxy payload contract of a published Workbench. The payload under test is the one staged
// beside the Workbench build (TSWB_PROXY_PAYLOAD, else ../TSharpWorkbench/<configuration>/proxy-payload, every RID, with
// payload-manifest.json). Each mutation test works on a lean copy that mirrors what dotnet publish stages (managed closure
// plus runtimes/<this RID>, manifest filtered the same way). A published payload can be checked as well:
// TSWB_PUBLISHED_PAYLOAD=<publish dir>/proxy-payload. The ordinary target is the package-mode ProxyOrdinary build
// (TSWB_PRODUCT_TARGET, see FixIssuesTests).
[Collection("NonParallel")]
public sealed class PublishTests
{
    private static string RequiredPayload()
    {
        string? payload = TvDemoDeploymentUxTests.Payload();
        Assert.False(string.IsNullOrEmpty(payload), "Set TSWB_PROXY_PAYLOAD to the built payload; integrity tests must execute.");
        return payload!;
    }
    private static string Temp() => Path.Combine(TargetDiagnosticsUxTests.RepositoryRoot(), "artifacts", "tsw-product-3a", "publish", "t", Guid.NewGuid().ToString("N")[..8]);
    private static void Delete(string root)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(root)) { foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)) f.IsReadOnly = false; Directory.Delete(root, true); }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }

    // A copy of the payload as dotnet publish stages it for one RID: managed files, the deps.json, runtimes/<rid>, and the
    // manifest restricted to them (kind "publish").
    private static string LeanCopy(string root, string? rid = null)
    {
        rid ??= ProxyPayload.CurrentRid();
        string source = TvDemoDeploymentUxTests.Payload()!, target = Path.Combine(root, ProxyPayload.Folder);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, PayloadManifest.FileName)))!.AsObject();
        var files = new JsonArray();
        foreach (var file in manifest["files"]!.AsArray().Select(f => f!.AsObject()))
        {
            string? fileRid = (string?)file["rid"];
            if (fileRid is not null && fileRid != rid) continue;
            string path = (string)file["path"]!;
            string to = Path.Combine(target, path);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(source, path), to);
            files.Add(file.DeepClone());
        }
        manifest["files"] = files; manifest["kind"] = "publish"; manifest["rids"] = new JsonArray(rid);
        File.WriteAllText(Path.Combine(target, PayloadManifest.FileName), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return target;
    }
    private static void FlipByte(string path, long offset = 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = Math.Min(offset, stream.Length - 1);
        int b = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(b ^ 0xFF));
    }
    private static InvalidDataException Refused(string payload) => Assert.Throws<InvalidDataException>(() => ProxyPayload.Load(payload));
    private static string Rel(string path) => path.Replace('\\', '/');

    // ── the staged payload and its manifest ─────────────────────────────────────────────

    [Fact]
    public void BuildPayloadManifestListsEveryFileWithItsHashAndNuGetOrgProvenance()
    {
        string payload = RequiredPayload();
        var manifest = PayloadManifest.Load(payload);
        Assert.Equal(PayloadManifest.CurrentSchema, manifest.Schema);
        Assert.Equal("build", manifest.Kind);
        Assert.Equal("package", manifest.FrameworkMode);
        Assert.Contains("https://api.nuget.org/v3/index.json", manifest.Sources);
        Assert.All(manifest.Sources.Where(s => s.StartsWith("http", StringComparison.OrdinalIgnoreCase)), s => Assert.Equal("https://api.nuget.org/v3/index.json", s));
        Assert.Contains(manifest.Packages, p => p.Id == "TSharpVision" && p.Version == Infrastructure.FrameworkPackageVersion.Expected && p.Type == "package" && p.Sha512!.StartsWith("sha512-", StringComparison.Ordinal));
        Assert.Contains(manifest.Packages, p => p.Id == "TSharpVision.Drivers.SDL" && p.Type == "package");
        Assert.Contains(manifest.Packages, p => p.Id == "SDL3-CS" && p.Type == "package");
        Assert.Equal(manifest.ClosureRids, manifest.Rids);   // the build output carries every RID of the closure
        // Exactly the files on disk (besides the manifest itself); no symbols or documentation.
        var onDisk = Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Select(f => Rel(Path.GetRelativePath(payload, f)))
            .Where(f => f != PayloadManifest.FileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(onDisk, manifest.Files.Select(f => f.Path).OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(onDisk, f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        Assert.All(manifest.Files, f => Assert.Equal(f.Sha256, Preparation.FileHash(Path.Combine(payload, f.Path))));
        Assert.All(manifest.Files.Where(f => f.Rid is not null), f => Assert.StartsWith($"runtimes/{f.Rid}/native/", f.Path, StringComparison.Ordinal));
        // Package identities agree with the deps.json closure the preparation reads.
        var loaded = ProxyPayload.Load(payload);
        Assert.NotNull(loaded.Manifest);
        Assert.All(loaded.Assets, a => Assert.Contains(manifest.Files, f => f.Path == Rel(Path.GetRelativePath(payload, a.Source)) && f.Package == a.Package && f.Version == a.Version && f.Sha256 == a.Sha256));
        Assert.Equal(manifest.Files.Single(f => f.Path == "TSharpVision.dll").Sha256, loaded.FrameworkSha256);
        Assert.Equal(manifest.Rids, loaded.NativeRids);
    }

    [Fact]
    public void LeanPublishedLayoutLoadsForItsRidAndOffersNoNativeAssetsForOthers()
    {
        _ = RequiredPayload();
        string root = Temp();
        try
        {
            string lean = LeanCopy(root);
            string rid = ProxyPayload.CurrentRid();
            var payload = ProxyPayload.Load(lean);
            Assert.Equal([rid], payload.NativeRids);
            Assert.Contains(payload.Assets, a => a.Native && a.Component == "sdl");
            Assert.All(payload.Assets, a => Assert.StartsWith(Path.GetFullPath(lean), a.Source));
            Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(lean, "runtimes")), d => Path.GetFileName(d) != rid);
            // Another RID: the managed Proxy closure only, no native assets and no exception (SDL is then reported unsupported).
            string other = rid == "linux-x64" ? "win-x64" : "linux-x64";
            var foreign = ProxyPayload.Load(lean, other);
            Assert.DoesNotContain(foreign.Assets, a => a.Native);
            Assert.Contains(foreign.Assets, a => a.Destination == TargetDiagnostics.ProxyFile);
            long size = Directory.GetFiles(lean, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Assert.True(size < 64L << 20, $"lean payload is {size} bytes");
        }
        finally { Delete(root); }
    }

    // ── refusal: missing, incomplete, tampered ───────────────────────────────────────────

    [Fact]
    public void MissingPayloadIsReportedAndNothingCanBePrepared()
    {
        string root = Temp();
        string? saved = Environment.GetEnvironmentVariable(ProxyPayload.EnvironmentOverride);
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, Path.Combine(root, "nothing-here"));
            if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, ProxyPayload.Folder))) return; // this test output carries one
            Assert.Null(ProxyPayload.Locate());
            var service = new TargetPreparationService(Path.Combine(root, "prepared"));
            Assert.Null(service.Payload);
            Assert.Contains("carries no Proxy payload", service.PayloadProblem);
            // An empty proxy-payload folder given explicitly: the manifest is named as missing.
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            var empty = new TargetPreparationService(Path.Combine(root, "prepared"), Path.Combine(root, "empty"));
            Assert.Null(empty.Payload);
            Assert.Contains("has no payload-manifest.json", empty.PayloadProblem);
        }
        finally { Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, saved); Delete(root); }
    }

    [Fact]
    public void PayloadWithoutManifestIsRefused()
    {
        _ = RequiredPayload();
        string root = Temp();
        try
        {
            string lean = LeanCopy(root);
            File.Delete(Path.Combine(lean, PayloadManifest.FileName));
            var ex = Refused(lean);
            Assert.Contains("has no payload-manifest.json", ex.Message);
            Assert.Contains("nothing was deployed", ex.Message);
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData("native")]
    [InlineData("TSharpWorkbench.Proxy.dll")]
    [InlineData("TSharpWorkbench.Proxy.deps.json")]
    [InlineData("TSharpVision.dll")]
    public void IncompletePayloadIsRefusedNamingTheMissingFile(string which)
    {
        _ = RequiredPayload();
        string root = Temp();
        try
        {
            string lean = LeanCopy(root);
            string relative = which == "native" ? $"runtimes/{ProxyPayload.CurrentRid()}/native/" + (OperatingSystem.IsWindows() ? "SDL3.dll" : OperatingSystem.IsMacOS() ? "libSDL3.dylib" : "libSDL3.so") : which;
            File.Delete(Path.Combine(lean, relative));
            var ex = Refused(lean);
            Assert.Contains("incomplete", ex.Message);
            Assert.Contains(relative, ex.Message);
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData("TSharpWorkbench.Proxy.dll")]
    [InlineData("TSharpWorkbench.Testing.dll")]
    [InlineData("TSharpVision.dll")]
    [InlineData("SDL3-CS.dll")]
    [InlineData("TSharpWorkbench.Proxy.deps.json")]
    [InlineData("native")]
    public void TamperedPayloadIsRefusedNamingTheModifiedFile(string which)
    {
        _ = RequiredPayload();
        string root = Temp();
        try
        {
            string lean = LeanCopy(root);
            string relative = which == "native" ? $"runtimes/{ProxyPayload.CurrentRid()}/native/" + (OperatingSystem.IsWindows() ? "SDL3.dll" : OperatingSystem.IsMacOS() ? "libSDL3.dylib" : "libSDL3.so") : which;
            string path = Path.Combine(lean, relative);
            long size = new FileInfo(path).Length;
            if (which.EndsWith(".json", StringComparison.Ordinal))
            {   // same size, different content: a changed version digit inside the closure
                string text = File.ReadAllText(path);
                string identity = "\"TSharpVision/" + Infrastructure.FrameworkPackageVersion.Expected + "\"";
                int at = text.IndexOf(identity, StringComparison.Ordinal);
                Assert.True(at >= 0, "The current framework package must be present before mutating its identity.");
                int digit = at + identity.Length - 2;
                char replacement = text[digit] == '9' ? '8' : '9';
                File.WriteAllText(path, text[..digit] + replacement + text[(digit + 1)..]);
            }
            else FlipByte(path);
            Assert.Equal(size, new FileInfo(path).Length);
            var ex = Refused(lean);
            Assert.Contains("was modified", ex.Message);
            Assert.Contains(relative, ex.Message);
            // A changed size is refused as well (cheap check for every file, including other components).
            File.AppendAllText(path, "x");
            Assert.Contains("size", Refused(lean).Message);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ManifestThatOmitsAStagedFileIsRefused()
    {
        _ = RequiredPayload();
        string root = Temp();
        try
        {
            string lean = LeanCopy(root);
            string manifestPath = Path.Combine(lean, PayloadManifest.FileName);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var files = manifest["files"]!.AsArray();
            files.Remove(files.First(f => (string)f!["path"]! == "TSharpVision.Drivers.SDL.dll"));
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var ex = Refused(lean);
            Assert.Contains("TSharpVision.Drivers.SDL.dll is not listed", ex.Message);
            File.WriteAllText(manifestPath, "{ \"schema\": \"something-else\" }");
            Assert.Contains("unknown schema", Refused(lean).Message);
            File.WriteAllText(manifestPath, "{ not json");
            Assert.Contains("cannot be read", Refused(lean).Message);
        }
        finally { Delete(root); }
    }

    // ── an ordinary target with a refused payload: nothing is planned, the application is untouched ──

    [Fact]
    public void TamperedPayloadBlocksFixIssuesAndLeavesTheApplicationUntouched()
    {
        if (TvDemoDeploymentUxTests.Payload() is null || FixIssuesTests.Original() is not { } original) return;
        string root = Temp();
        string? saved = Environment.GetEnvironmentVariable(ProxyPayload.EnvironmentOverride);
        var previous = TargetDiagnostics.Preparation;
        try
        {
            string lean = LeanCopy(root);
            string app = Path.Combine(root, "app");
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(original)!, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                string to = Path.Combine(app, Path.GetRelativePath(Path.GetDirectoryName(original)!, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(file, to);
            }
            string exe = Path.Combine(app, Path.GetFileName(original));
            var before = Directory.GetFiles(app, "*", SearchOption.AllDirectories).ToDictionary(f => f, Preparation.FileHash);
            FlipByte(Path.Combine(lean, TargetDiagnostics.ProxyFile));
            Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, lean);
            var service = new TargetPreparationService(Path.Combine(root, "prepared"), lean);
            TargetDiagnostics.Preparation = service;
            TargetPreparationService.ForgetStatus();
            Assert.Null(service.Payload);
            Assert.StartsWith("The Proxy payload cannot be used: The Proxy payload was modified: TSharpWorkbench.Proxy.dll", service.PayloadProblem);
            TargetDiagnostics.Forget(exe);
            var report = TargetDiagnostics.Evaluate(TargetDiagnostics.Inspect(exe), new DiagnosticContext(true, false, "NullDriver", null, null, Run: true));
            var plan = new TargetRepairService(service) { IsRunning = _ => false }.Plan(exe, report);
            Assert.False(plan.CanRepair);
            Assert.Contains(plan.Blockers, b => b.Contains("was modified", StringComparison.Ordinal));
            var prepare = service.CreatePlan(service.Inspect(exe), PreparationStrategy.InPlaceInstallation, TargetPreparationService.DefaultCapabilities);
            Assert.False(prepare.CanExecute);
            var after = Directory.GetFiles(app, "*", SearchOption.AllDirectories).ToDictionary(f => f, Preparation.FileHash);
            Assert.Equal(before, after);   // the application's framework (TSharpVision.dll) and every other file unchanged
            Assert.False(Directory.Exists(Path.Combine(root, "prepared")) && Directory.EnumerateFileSystemEntries(Path.Combine(root, "prepared")).Any());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxyPayload.EnvironmentOverride, saved);
            TargetDiagnostics.Preparation = previous;
            Delete(root);
        }
    }

    // ── a real published payload (optional) ──────────────────────────────────────────────

    [Fact]
    public void PublishedPayloadIsLeanVerifiedAndCarriesItsRidOnly()
    {
        if (Environment.GetEnvironmentVariable("TSWB_PUBLISHED_PAYLOAD") is not { Length: > 0 } published || !Directory.Exists(published)) return;
        var manifest = PayloadManifest.Load(published);
        Assert.Equal("publish", manifest.Kind);
        Assert.Equal("package", manifest.FrameworkMode);
        Assert.Equal([ProxyPayload.CurrentRid()], manifest.Rids);
        var onDisk = Directory.GetFiles(published, "*", SearchOption.AllDirectories).Select(f => Rel(Path.GetRelativePath(published, f)))
            .Where(f => f != PayloadManifest.FileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(onDisk, manifest.Files.Select(f => f.Path).OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.All(manifest.Files, f => Assert.Equal(f.Sha256, Preparation.FileHash(Path.Combine(published, f.Path))));
        var payload = ProxyPayload.Load(published);
        Assert.Equal([ProxyPayload.CurrentRid()], payload.NativeRids);
    }
}
