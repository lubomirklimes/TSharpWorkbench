using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSharpWorkbench.Storage;

public enum RecoveryChoice { Cancel, Recover, Discard }
public enum CloseChoice { Cancel, Save, Discard }
public sealed class RecoveryRequiredException(string workspace) : IOException("A previous session requires Recover, Discard, or Cancel.")
{
    public string Workspace { get; } = workspace;
}
public sealed class ProjectConflictException(string message) : IOException(message);
public sealed record ProjectInfo(string Id, string Name);
public sealed record EnvironmentDefinition(string Id, string Name, string OperatingSystem, string Driver,
    int Width, int Height, string Capture, bool Enabled = true);
public sealed record ScenarioReference(string Id, string? ArtifactPath, string? FixtureId, string Backend);
public sealed record TestDefinition(string Id, string Title, string Description, string Preconditions,
    string ScenarioId, IReadOnlyList<string> EnvironmentIds);
internal sealed record Checkpoint(string Origin, string? ArchiveHash, bool Dirty, string? PendingHash = null);
internal sealed record Payload(long Length, string Sha256);
internal sealed record Manifest(string Format, int Version, string ProjectId, Dictionary<string, Payload> Files);

internal static class StorageFiles
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static void NoLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links/reparse points are not supported in storage paths.");
    }
    internal static void PrivateDirectory(string path)
    {
        NoLinks(path);
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal static FileStream NewPrivateFile(string path)
    {
        NoLinks(path);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(acl);
            }
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    internal static string Hash(string path)
    {
        NoLinks(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static string? ExistingHash(string path) => File.Exists(path) ? Hash(path) : null;
    internal static void WriteJson<T>(string path, T value)
    {
        string next = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = NewPrivateFile(next)) { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
        File.Move(next, path, true);
    }
    internal static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Missing metadata.");
    internal static void RemoveTree(string path)
    {
        NoLinks(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            NoLinks(entry);
            if (Directory.Exists(entry)) RemoveTree(entry); else File.Delete(entry);
        }
        Directory.Delete(path);
    }
    internal static void ArtifactName(string name)
    {
        if (!name.StartsWith("scenarios/", StringComparison.Ordinal) && !name.StartsWith("baselines/", StringComparison.Ordinal)
            && !name.StartsWith("runs/", StringComparison.Ordinal) && !name.StartsWith("attachments/", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported artifact root.");
        CanonicalName(name);
    }
    internal static void CanonicalName(string name)
    {
        if (name.Length > 240) throw new InvalidDataException("Entry name too long.");
        foreach (string part in name.Split('/'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') ||
                part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9'))
                throw new InvalidDataException("Noncanonical archive entry.");
        }
    }
    internal static void CopyBounded(Stream source, Stream destination, long limit)
    {
        byte[] buffer = new byte[81920]; long total = 0; int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException("Payload exceeds size limit.");
            destination.Write(buffer, 0, count);
        }
    }
}
