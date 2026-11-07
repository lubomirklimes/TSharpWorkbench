using System.Text.Json;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

/// <summary>Bounded, thread-safe in-memory store of immutable transient-state snapshots of the active recording.
/// Written on the UI thread (never blocks, never does I/O), read by the transport thread. A snapshot stays owned by the store
/// until the controller has fetched past it (RecordSnapshots After = its id) or the recording is restarted/cleared.</summary>
/// <remarks>Accounting: each entry is charged <see cref="Estimate"/> = RAM of its cells (8 bytes/cell, at least the 4 bytes/cell
/// of its base64 TVS1) + a conservative upper bound of its JSON envelope and inspection (every string charged 6 bytes/char,
/// the \uXXXX worst case). Overflow refuses the NEW snapshot (dropped++); nothing unfetched is evicted. The lock only guards
/// list/counter updates: estimation runs before it on the UI thread, base64/JSON after it on the reading thread.</remarks>
internal sealed class ProxyRetainedSnapshots
{
    internal const int MaximumCount = 32;
    internal const long MaximumBytes = 4L * 1024 * 1024;

    private sealed record Entry(long Id, long BoundaryId, string Reason, long AfterSequence, long Delivered, long Generation,
        ulong Hash, long Milliseconds, ScreenSnapshot Cells, CheckpointInspection? Inspection, long Bytes);

    private readonly object gate = new();
    private readonly List<Entry> entries = []; // ascending id
    private long nextId, added, dropped, released, bytes;
    private int mode; // 0 inactive/cleared, 1 accepting, 2 frozen (readable)

    /// <summary>UI thread: retain one snapshot; null when refused (store full, frozen or inactive) - counted as dropped.</summary>
    internal long? TryAdd(long boundaryId, string reason, long afterSequence, long delivered, long generation, ulong hash,
        long milliseconds, ScreenSnapshot cells, CheckpointInspection? inspection)
    {
        long cost = Estimate(cells, inspection);
        lock (gate)
        {
            if (mode != 1 || entries.Count >= MaximumCount || bytes + cost > MaximumBytes) { dropped++; return null; }
            var entry = new Entry(++nextId, boundaryId, reason, afterSequence, delivered, generation, hash, milliseconds, cells, inspection, cost);
            entries.Add(entry); added++; bytes += cost;
            return entry.Id;
        }
    }

    /// <summary>Any thread: releases every snapshot with id &lt;= <paramref name="after"/>, then returns the following ones,
    /// at most <paramref name="maximumCount"/> and <paramref name="maximumBytes"/> of serialized payload (at least one when any
    /// is available and it fits the protocol frame on its own).</summary>
    /// <remarks>Sizes are exact (System.Text.Json UTF-8 length of each <see cref="RetainedSnapshot"/>), measured on the calling
    /// thread. A first snapshot too large for a frame even alone is returned without its inspection, with an explicit error.</remarks>
    internal RetainedSnapshot[] Read(long after, int maximumCount, int maximumBytes)
    {
        Entry[] pending;
        lock (gate)
        {
            int release = 0;
            while (release < entries.Count && entries[release].Id <= after) { bytes -= entries[release].Bytes; release++; }
            if (release > 0) { entries.RemoveRange(0, release); released += release; }
            pending = entries.Take(Math.Max(0, maximumCount)).ToArray();
        }
        var result = new List<RetainedSnapshot>(pending.Length);
        long total = 0;
        foreach (var e in pending)
        {
            var snapshot = new RetainedSnapshot(e.Id, e.BoundaryId, e.Reason, e.AfterSequence, e.Delivered, e.Generation, e.Hash,
                e.Milliseconds, Convert.ToBase64String(e.Cells.Serialize()), e.Cells.Width, e.Cells.Height, e.Inspection);
            // Frame fitting (view-tree/inspection omission with an explicit marker) is RecordingTransport.Fit's job.
            long size = JsonSerializer.SerializeToUtf8Bytes(snapshot).Length;
            if (result.Count > 0 && total + size > maximumBytes) break;
            result.Add(snapshot); total += size;
        }
        return result.ToArray();
    }

    /// <summary>Highest retained id (0 = none).</summary>
    internal long Latest { get { lock (gate) return entries.Count == 0 ? 0 : entries[^1].Id; } }

    /// <summary>Counters for <see cref="CaptureCoverage"/>.</summary>
    internal (long Added, long Dropped, long Released, int Retained, long Bytes) Counters()
    {
        lock (gate) return (added, dropped, released, entries.Count, bytes);
    }

    /// <summary>Reset for a new recording: drops everything (no persistence) and resets ids and counters.</summary>
    internal void Reset()
    {
        lock (gate) { entries.Clear(); nextId = added = dropped = released = bytes = 0; mode = 1; }
    }

    /// <summary>Stop: refuse further additions; what is held stays readable until <see cref="Clear"/>.</summary>
    internal void Freeze() { lock (gate) if (mode == 1) mode = 2; }

    /// <summary>Session end/discard: drop every payload now (privacy) and refuse additions.</summary>
    internal void Clear() { lock (gate) { entries.Clear(); bytes = 0; mode = 0; } }

    /// <summary>Charged bytes of one snapshot (see remarks on the class); an upper bound of its serialized JSON.</summary>
    internal static long Estimate(ScreenSnapshot cells, CheckpointInspection? inspection)
    {
        long n = (long)cells.Width * cells.Height;
        long total = 512 + Math.Max(8 * n, 4 * ((12 + 3 * n + 2) / 3));
        if (inspection is null) return total;
        total += 64;
        foreach (string error in inspection.Errors) total += 16 + Text(error);
        if (inspection.ViewTree is { } tree)
        {
            total += 1024 + Text(tree.ModalPath) + Text(tree.FocusedPath);
            var stack = new Stack<ViewNode>(); stack.Push(tree.Root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                total += 1024 + Text(node.Path) + Text(node.Id) + Text(node.Type) + Text(node.Kind) + Text(node.Text) + Text(node.CellText);
                if (node.Properties is { } properties) foreach (var (key, value) in properties) total += 8 + Text(key) + Text(value);
                foreach (var child in node.Children) stack.Push(child);
            }
        }
        if (inspection.Menu is { } menu)
        {
            total += 64;
            foreach (var level in menu.Open.Prepend(menu.Bar))
            {
                if (level is null) continue;
                total += 512 + Text(level.Type) + Text(level.Path) + Text(level.SelectedText) + Text(level.ParentItem);
                foreach (var item in level.Items)
                    total += 640 + Text(item.Text) + Text(item.Markup) + Text(item.HotChar) + Text(item.Accelerator) + Text(item.KeyName) + Text(item.Observed);
            }
        }
        if (inspection.Status is { } status)
        {
            total += 768 + Text(status.Type) + Text(status.Path) + Text(status.Hint) + Text(status.HintObserved) + Text(status.ObservedRow);
            foreach (var item in status.Items) total += 384 + Text(item.Text) + Text(item.Markup) + Text(item.KeyName) + Text(item.Observed);
        }
        return total;
    }

    private static long Text(string? value) => value is null ? 0 : 6L * value.Length;
}
