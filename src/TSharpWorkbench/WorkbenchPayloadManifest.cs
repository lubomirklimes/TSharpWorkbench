using System.Text.Json;
using System.Text.Json.Serialization;

// Provenance and integrity of the Proxy payload (proxy-payload/payload-manifest.json). The manifest is
// written at build/publish time by src/Workbench.ProxyPayload.targets from the Proxy's deps.json closure: package
// identities (nuget.org sha512), restore sources, configuration, the RIDs whose native assets are carried, and the size
// and SHA-256 of every staged file. ProxyPayload.Load refuses a payload without a manifest, with an incomplete file set
// or with a file whose content differs from the manifest. Nothing is ever repaired or replaced from here.
internal sealed record PayloadManifestFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("package")] string? Package,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("rid")] string? Rid);

internal sealed record PayloadManifestPackage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("sha512")] string? Sha512);

internal sealed class PayloadManifest
{
    internal const string FileName = "payload-manifest.json", CurrentSchema = "tsw-proxy-payload/1";
    [JsonPropertyName("schema")] public string Schema { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("configuration")] public string Configuration { get; init; } = "";
    [JsonPropertyName("targetFramework")] public string TargetFramework { get; init; } = "";
    [JsonPropertyName("frameworkMode")] public string FrameworkMode { get; init; } = "";
    [JsonPropertyName("rids")] public string[] Rids { get; init; } = [];
    [JsonPropertyName("closureRids")] public string[] ClosureRids { get; init; } = [];
    [JsonPropertyName("sources")] public string[] Sources { get; init; } = [];
    [JsonPropertyName("nugetConfigFiles")] public string[] NuGetConfigFiles { get; init; } = [];
    [JsonPropertyName("packages")] public PayloadManifestPackage[] Packages { get; init; } = [];
    [JsonPropertyName("files")] public PayloadManifestFile[] Files { get; init; } = [];

    [JsonIgnore] internal string Directory { get; private set; } = "";
    [JsonIgnore] private Dictionary<string, PayloadManifestFile> byPath = new(StringComparer.Ordinal);

    internal bool Carries(string rid) => Rids.Contains(rid, StringComparer.Ordinal);

    // Reads and structurally checks the manifest; every listed file must exist with its recorded size (cheap). Content
    // hashes are compared by Verify for the files a preparation actually uses. Throws InvalidDataException.
    internal static PayloadManifest Load(string directory)
    {
        string path = System.IO.Path.Combine(directory, FileName);
        if (!File.Exists(path))
            throw new InvalidDataException($"The Proxy payload has no {FileName}, so its origin and integrity cannot be verified. Reinstall or republish the Workbench; nothing was deployed.");
        PayloadManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllBytes(path)); }
        catch (JsonException ex) { throw new InvalidDataException($"{FileName} cannot be read ({ex.Message}). Reinstall or republish the Workbench; nothing was deployed."); }
        if (manifest is null || manifest.Schema != CurrentSchema)
            throw new InvalidDataException($"{FileName} has an unknown schema '{manifest?.Schema}' (expected {CurrentSchema}). Reinstall or republish the Workbench; nothing was deployed.");
        manifest.Directory = System.IO.Path.GetFullPath(directory);
        var missing = new List<string>();
        foreach (var file in manifest.Files)
        {
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, file.Path));
            if (!Preparation.IsUnder(full, manifest.Directory)) throw new InvalidDataException($"{FileName} names a file outside the payload: {file.Path}.");
            manifest.byPath[file.Path] = file;
            var info = new FileInfo(full);
            if (!info.Exists) missing.Add(file.Path);
            else if (info.Length != file.Size) throw Tampered(file.Path, $"size {info.Length} bytes, the manifest records {file.Size}");
        }
        if (missing.Count > 0)
            throw new InvalidDataException($"The Proxy payload is incomplete: {missing.Count} file{(missing.Count == 1 ? "" : "s")} listed in {FileName} {(missing.Count == 1 ? "is" : "are")} missing ({string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? ", ..." : "")}). Reinstall or republish the Workbench; nothing was deployed.");
        return manifest;
    }

    // The file (payload-relative, '/' separators) must be listed and have the recorded content.
    internal void Verify(string relative, string sha256)
    {
        relative = relative.Replace('\\', '/');
        if (!byPath.TryGetValue(relative, out var file))
            throw new InvalidDataException($"The Proxy payload is not consistent with its {FileName}: {relative} is not listed. Reinstall or republish the Workbench; nothing was deployed.");
        if (!string.Equals(file.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw Tampered(relative, $"SHA-256 {sha256[..12]}..., the manifest records {file.Sha256[..Math.Min(12, file.Sha256.Length)]}...");
    }
    internal void VerifyFile(string relative) => Verify(relative, Preparation.FileHash(System.IO.Path.Combine(Directory, relative)));

    private static InvalidDataException Tampered(string relative, string detail) =>
        new($"The Proxy payload was modified: {relative} does not match {FileName} ({detail}). The payload is refused and nothing was deployed; reinstall or republish the Workbench.");

    // A one-line provenance summary (diagnostics, CLI).
    internal string Describe() =>
        $"{Kind} payload ({Configuration}, {FrameworkMode} TSharpVision {Packages.FirstOrDefault(p => p.Id == "TSharpVision")?.Version ?? "?"}), native assets for {(Rids.Length == 0 ? "no RID" : string.Join(", ", Rids))}"
        + $"; sources: {(Sources.Length == 0 ? "unknown" : string.Join(", ", Sources))}; {Files.Length} files, {Files.Sum(f => f.Size) / 1048576.0:F1} MB";
}
