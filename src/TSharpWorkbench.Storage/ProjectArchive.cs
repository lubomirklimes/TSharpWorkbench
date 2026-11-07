using System.IO.Compression;
using System.Text.Json;

namespace TSharpWorkbench.Storage;

internal static class ProjectArchive
{
    internal const long MaxFile = 2L * 1024 * 1024 * 1024, MaxTotal = 8L * 1024 * 1024 * 1024;
    internal const int MaxEntries = 10000, MaxManifest = 4 * 1024 * 1024;
    internal static void Pack(string content, string destination, string projectId)
    {
        var files = new Dictionary<string, Payload>(StringComparer.Ordinal);
        // No recursive enumeration through a reparse-point directory.
        void Walk(string folder)
        {
            StorageFiles.NoLinks(folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                StorageFiles.NoLinks(path);
                if (Directory.Exists(path)) { Walk(path); continue; }
                string name = Path.GetRelativePath(content, path).Replace('\\', '/');
                if (name != "project.sqlite") StorageFiles.ArtifactName(name);
                long length = new FileInfo(path).Length;
                if (length > MaxFile || files.Count >= MaxEntries - 1) throw new InvalidDataException("Project exceeds archive limits.");
                files.Add(name, new(length, StorageFiles.Hash(path)));
            }
        }
        Walk(content);
        if (files.Values.Sum(f => f.Length) > MaxTotal) throw new InvalidDataException("Project exceeds archive limits.");
        using var output = StorageFiles.NewPrivateFile(destination);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, payload) in files)
            {
                using var input = File.OpenRead(Path.Combine(content, name));
                // Stored entries avoid CPU-heavy recompression of PNGs and self-rejected extreme compression ratios.
                using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                StorageFiles.CopyBounded(input, entry, payload.Length);
            }
            using var manifest = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
            JsonSerializer.Serialize(manifest, new Manifest("TSharpWorkbench.Project", 1, projectId, files), StorageFiles.Json);
        }
        output.Flush(true);
    }
    internal static Manifest Extract(string archive, string staging)
    {
        StorageFiles.PrivateDirectory(staging);
        using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        Preflight(input);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 2 or > MaxEntries) throw new InvalidDataException("Invalid entry count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            StorageFiles.CanonicalName(name);
            if (name is not ("manifest.json" or "project.sqlite")) StorageFiles.ArtifactName(name);
            int kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind is not (0 or 0x8000) || (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || !names.Add(name))
                throw new InvalidDataException("Duplicate or nonregular ZIP entry.");
            long limit = name == "manifest.json" ? MaxManifest : MaxFile;
            if (entry.Length > limit || entry.Length < 0 || entry.Length > Math.Max(1, entry.CompressedLength) * 1000m)
                throw new InvalidDataException("ZIP expansion exceeds limits.");
            total = checked(total + entry.Length);
            if (total > MaxTotal) throw new InvalidDataException("ZIP expanded size exceeds limit.");
        }
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
            {
                if (names.Contains(name[..slash])) throw new InvalidDataException("Conflicting ZIP paths.");
                string prefix = name[..slash];
                if (prefixes.TryGetValue(prefix, out var prior) && prior != prefix)
                    throw new InvalidDataException("Case-aliased ZIP directories.");
                prefixes[prefix] = prefix;
            }
        }
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing manifest.");
        Manifest manifest;
        using (var data = new MemoryStream())
        {
            using var source = manifestEntry.Open(); StorageFiles.CopyBounded(source, data, MaxManifest);
            data.Position = 0;
            using var json = JsonDocument.Parse(data);
            RejectDuplicateProperties(json.RootElement);
            manifest = json.RootElement.Deserialize<Manifest>(StorageFiles.Json) ?? throw new InvalidDataException("Invalid manifest.");
        }
        if (manifest.Format != "TSharpWorkbench.Project" || manifest.Version != 1 || manifest.Files is null ||
            !manifest.Files.ContainsKey("project.sqlite") || manifest.Files.Count != zip.Entries.Count - 1)
            throw new InvalidDataException("Unsupported or incomplete manifest.");
        foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json"))
        {
            if (!manifest.Files.TryGetValue(entry.FullName, out var payload) || payload is null || payload.Length != entry.Length)
                throw new InvalidDataException("Manifest payload mismatch.");
            string path = Path.Combine(staging, entry.FullName);
            StorageFiles.PrivateDirectory(Path.GetDirectoryName(path)!);
            using (var target = StorageFiles.NewPrivateFile(path))
            using (var source = entry.Open()) { StorageFiles.CopyBounded(source, target, entry.Length); target.Flush(true); }
            if (new FileInfo(path).Length != payload.Length || StorageFiles.Hash(path) != payload.Sha256)
                throw new InvalidDataException("Payload checksum mismatch.");
        }
        ProjectDatabase.Validate(Path.Combine(staging, "project.sqlite"), manifest.ProjectId, staging);
        return manifest;
    }
    // Bound metadata before ZipArchive materializes its central directory. ZIP64 offsets are allowed;
    // sentinel entry counts / central-directory sizes exceed this format's deliberately small limits.
    private static void Preflight(FileStream input)
    {
        const int maxDirectory = 16 * 1024 * 1024;
        if (input.Length > MaxTotal + maxDirectory + MaxManifest || input.Length < 22)
            throw new InvalidDataException("Archive physical size exceeds limits or is truncated.");
        byte[] tail = new byte[(int)Math.Min(input.Length, 65535 + 22)];
        input.Position = input.Length - tail.Length; input.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(tail, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(tail, i + 20) == tail.Length)
            { end = i; break; }
        if (end < 0 || BitConverter.ToUInt16(tail, end + 4) != 0 || BitConverter.ToUInt16(tail, end + 6) != 0)
            throw new InvalidDataException("Missing ZIP end record or unsupported multi-volume ZIP.");
        int count = BitConverter.ToUInt16(tail, end + 10);
        if (count is < 2 or > MaxEntries || BitConverter.ToUInt16(tail, end + 8) != count ||
            BitConverter.ToUInt32(tail, end + 12) > maxDirectory)
            throw new InvalidDataException("ZIP directory exceeds limits.");
        input.Position = 0;
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate manifest property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
