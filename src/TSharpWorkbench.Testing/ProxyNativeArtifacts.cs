using System.Buffers.Binary;
using System.Text.Json;

namespace TSharpWorkbench.Testing;

internal static class ProxyNativeArtifacts
{
    internal sealed record Evidence(string Artifact, string Kind, string Format, int PixelWidth, int PixelHeight,
        string Sha256, Dictionary<string, string> Details);
    internal static async Task<Evidence> SaveAsync(string root, string stem, TargetIdentity identity, CheckpointCapture capture)
    {
        var native = capture.ProxyNative!;
        if (capture.Proxy is null || !ProxyCaptureKinds.ForBackend(identity.Driver).Contains(native.Kind)
            || native.Details.GetValueOrDefault("actualBackend") != capture.Proxy.ActualBackend
            || native.Details.GetValueOrDefault("rootRevision") != capture.Proxy.Revision.ToString()
            || native.Details.GetValueOrDefault("grid") != $"{capture.Width}x{capture.Height}")
            throw new IOException("Proxy native capture provenance mismatch.");
        if (native.Data.Length > (ProxyCaptureKinds.MaximumPayloadBytes + 2) / 3 * 4)
            throw new IOException("Proxy native payload exceeds limit.");
        byte[] data = Convert.FromBase64String(native.Data);
        if (data.Length > ProxyCaptureKinds.MaximumPayloadBytes || ScreenSnapshot.Hash(data) != native.Sha256)
            throw new IOException("Proxy native payload size/hash mismatch.");
        if (native.Format == "png")
        {
            if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
                || BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)) != native.PixelWidth
                || BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)) != native.PixelHeight)
                throw new IOException("Native PNG geometry/encoding mismatch.");
        }
        else if (native.Format != "console-cells-json" || native.Kind != ProxyCaptureKinds.Console)
            throw new IOException("Unsupported native encoding.");
        string name = stem + "--" + Path.GetFileName(root) + "--" + identity.Driver + "--" + native.Kind + (native.Format == "png" ? ".png" : ".json");
        await using (var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await output.WriteAsync(data);
        var evidence = new Evidence(name, native.Kind, native.Format, native.PixelWidth, native.PixelHeight, native.Sha256, native.Details);
        await using var meta = new FileStream(Path.Combine(root, name + ".metadata.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(meta, evidence, ScenarioRunner.Json);
        return evidence;
    }
}
