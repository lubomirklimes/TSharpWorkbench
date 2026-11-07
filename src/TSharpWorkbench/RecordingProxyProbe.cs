using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TSharpWorkbench.Testing;

// (coordinator, owner decision 5): before a Smart recording starts, a read-only capability probe of the Workbench Proxy
// deployed beside the target. It reads assembly METADATA only (nothing is loaded or executed): the deployed Testing assembly
// must define Protocol.RecordFeatures with this Workbench's Smart Recorder feature names, and the deployed Proxy must use
// them. Capability decides, not file hashes or versions: a Proxy from another build that has the features is Capable (PROXY008
// stays a diagnostic warning only). The runtime handshake remains authoritative (RecordingSession.RequireSmartCapture).

internal enum ProxyCapability
{
    /// <summary>The deployed Proxy advertises every Smart Recorder feature this Workbench uses.</summary>
    Capable,
    /// <summary>The deployed Proxy predates Smart Recorder capture: a Smart recording would contain input only.</summary>
    Stale,
    /// <summary>No readable deployed Proxy beside the target (or not a managed assembly): decided at the handshake.</summary>
    Unknown,
}

internal sealed record ProxyProbeResult(ProxyCapability Capability, string Detail);

internal static class RecordingProxyProbe
{
    /// <summary>Test seam: replaces the probe (null = the metadata probe below).</summary>
    internal static Func<string, ProxyProbeResult>? Override { get; set; }

    internal static ProxyProbeResult Probe(string targetExecutable)
    {
        if (Override is { } seam) return seam(targetExecutable);
        string folder = Path.GetDirectoryName(Path.GetFullPath(targetExecutable)) ?? "";
        string testing = Path.Combine(folder, TargetDiagnostics.TestingFile), proxy = Path.Combine(folder, TargetDiagnostics.ProxyFile);
        if (!File.Exists(testing) || !File.Exists(proxy))
            return new(ProxyCapability.Unknown, "No Workbench Proxy is deployed beside the target; the connection decides.");
        try
        {
            var features = FeatureConstants(testing);
            string[] wanted = [.. Protocol.RecordFeatures.All, .. Protocol.RecordFeatures.Optional];
            var missing = wanted.Where(f => !features.Contains(f)).ToArray();
            if (missing.Length > 0)
                return new(ProxyCapability.Stale, features.Count == 0
                    ? "The deployed Workbench Proxy predates Smart capture (no Smart Recorder features)."
                    : "The deployed Workbench Proxy lacks Smart capture features: " + string.Join(", ", missing) + ".");
            if (!UsesRecordFeatures(proxy))
                return new(ProxyCapability.Stale, "The deployed Workbench Proxy does not advertise Smart Recorder features.");
            return new(ProxyCapability.Capable, "The deployed Workbench Proxy supports Smart capture.");
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(ProxyCapability.Unknown, "The deployed Workbench Proxy could not be read (" + ex.Message + "); the connection decides."); }
    }

    // String constants of the fields of TSharpWorkbench.Testing.Protocol/RecordFeatures in the deployed Testing assembly.
    private static HashSet<string> FeatureConstants(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new BadImageFormatException("not a managed assembly");
        var md = pe.GetMetadataReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if (md.GetString(type.Name) != "RecordFeatures" || type.GetDeclaringType().IsNil) continue;
            var owner = md.GetTypeDefinition(type.GetDeclaringType());
            if (md.GetString(owner.Name) != "Protocol" || md.GetString(owner.Namespace) != "TSharpWorkbench.Testing") continue;
            foreach (var f in type.GetFields())
            {
                var field = md.GetFieldDefinition(f);
                if (field.GetDefaultValue() is { IsNil: false } c && md.GetConstant(c) is { TypeCode: ConstantTypeCode.String } constant)
                {
                    var blob = md.GetBlobReader(constant.Value);
                    result.Add(blob.ReadUTF16(blob.Length));
                }
            }
        }
        return result;
    }

    // The deployed Proxy references Protocol.RecordFeatures (it advertises the features in its handshake).
    private static bool UsesRecordFeatures(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new BadImageFormatException("not a managed assembly");
        var md = pe.GetMetadataReader();
        return md.TypeReferences.Any(h => md.GetString(md.GetTypeReference(h).Name) == "RecordFeatures");
    }
}
