using System.Security.Cryptography;
using TSharpVision;
using TSharpVision.Constants;

internal sealed class WorkbenchHelpResource : IDisposable
{
    private readonly FileStream stream;
    internal THelpFile File { get; }
    internal WorkbenchHelpResource(string path)
    {
        // Verify before deserializing: a truncated/damaged resource must never become an empty help store.
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try {
            if (stream.Length is < 12 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid help resource size.");
            string expected = System.IO.File.ReadAllText(path + ".sha256").Trim();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Help resource integrity check failed.");
            stream.Position = 0;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != THelpFile.magicHeaderV2) throw new InvalidDataException("Unsupported help format (expected FBH2).");
            THelpFile.RegisterStreamableTypes();
            File = new THelpFile(new Fpstream(stream));
            foreach (HelpTopic id in Enum.GetValues<HelpTopic>()) {
                if (File.index.Position((int)id) <= 0) throw new InvalidDataException($"Missing help context: {id}.");
                var topic = File.GetTopic((int)id);
                if (topic.paragraphs is null) throw new InvalidDataException("Empty help topic.");
                if (topic.crossRefs is { } links)
                    foreach (var link in links)
                        if (link is null || File.index.Position(link.@ref) <= 0) throw new InvalidDataException("Broken help link.");
            }
        } catch { stream.Dispose(); throw; }
    }
    public void Dispose() => stream.Dispose();
}

internal sealed class WorkbenchHelpWindow : THelpWindow
{
    internal THelpViewer Viewer
    {
        get { THelpViewer? result = null; ForEachView(v => { if (v is THelpViewer viewer) result = viewer; }); return result!; }
    }
    internal const ushort Context = 0x721f;
    internal WorkbenchHelpWindow(TRect bounds, THelpFile file, HelpTopic topic) : base(bounds, file, (ushort)topic)
    {
        helpCtx = Context;
        title = "TSharpWorkbench Help";
        options |= Views.ofCentered;
    }
}
