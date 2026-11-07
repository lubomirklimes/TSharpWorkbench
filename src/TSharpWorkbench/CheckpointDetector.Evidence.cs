using TSharpWorkbench.Testing;

// Evidence lookup, noise/meaning comparison and result assembly of CheckpointDetector. Pure.

internal static partial class CheckpointDetector
{
    private enum Diff { Unknown, Noise, Significant }

    private sealed record Evidence(ScreenSnapshot? Cells, CheckpointInspection? Inspection);

    private sealed partial class Run
    {
        private readonly Dictionary<long, RetainedSnapshot> snapshotsById;
        private readonly Dictionary<State, Evidence?> evidenceCache = [];
        private int undecodable;

        private static Dictionary<long, RetainedSnapshot> Index(IReadOnlyList<RetainedSnapshot> snapshots)
        {
            var map = new Dictionary<long, RetainedSnapshot>();
            foreach (var s in snapshots.OrderBy(s => s.Id)) map.TryAdd(s.Id, s);
            return map;
        }

        /// <summary>The fetched snapshot taken AT one of this state's own boundaries (by SnapshotId, or a transient-last
        /// naming the boundary), with the boundary's hash. Never a later screen; pick snapshots belong to pick boundaries.</summary>
        private RetainedSnapshot? OwnSnapshot(State s)
        {
            foreach (var b in s.All)
            {
                if (b.SnapshotId is { } id && snapshotsById.TryGetValue(id, out var own) && own.BoundaryId == b.Id && own.Hash == b.Hash) return own;
                RetainedSnapshot? named = null;
                foreach (var snap in snapshotsById.Values)
                    if (snap.BoundaryId == b.Id && snap.Hash == b.Hash && snap.Reason != Protocol.SnapshotReasons.Pick
                        && (named is null || snap.Id < named.Id)) named = snap;
                if (named is not null) return named;
            }
            return null;
        }

        /// <summary>A live RecordInspect sample of this state: equal Hash AND AfterSequence with one of its boundaries.</summary>
        private RecordInspection? LiveSample(State s)
        {
            foreach (var b in s.All)
                foreach (var sample in input.Inspections)
                    if (sample.Hash == b.Hash && sample.AfterSequence == b.AfterSequence) return sample;
            return null;
        }

        private Evidence? EvidenceOf(State s)
        {
            if (evidenceCache.TryGetValue(s, out var cached)) return cached;
            Evidence? result = null;
            if (OwnSnapshot(s) is { } snap) result = new(Decode(snap.Cells), snap.Inspection);
            if ((result?.Cells is null || result.Inspection?.ViewTree is null) && LiveSample(s) is { } live)
                result = new(result?.Cells ?? Decode(live.Cells), result?.Inspection?.ViewTree is not null ? result.Inspection : live.Inspection ?? result?.Inspection);
            evidenceCache[s] = result;
            return result;
        }

        private ScreenSnapshot? Decode(string? cells)
        {
            if (string.IsNullOrEmpty(cells)) return null;
            try { return ScreenSnapshot.Deserialize(Convert.FromBase64String(cells)); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException) { undecodable++; return null; }
        }

        /// <summary>Noise vs meaning between two evidenced states (Unknown without comparable evidence).</summary>
        private (Diff Result, string Why) Compare(State a, State b)
        {
            var ea = EvidenceOf(a);
            var eb = EvidenceOf(b);
            if (ea is null || eb is null) return (Diff.Unknown, "");
            var ta = ea.Inspection?.ViewTree;
            var tb = eb.Inspection?.ViewTree;
            if (ta is not null && tb is not null)
            {
                var wa = Windows(ta);
                var wb = Windows(tb);
                if (!wa.SequenceEqual(wb)) return (Diff.Significant, "a window or dialog appeared, closed or moved (view tree)");
                if (!string.Equals(ta.ModalPath, tb.ModalPath, StringComparison.Ordinal)) return (Diff.Significant, "the executing modal view changed (view tree)");
            }
            if (ea.Cells is { } ca && eb.Cells is { } cb && ca.Width == cb.Width && ca.Height == cb.Height)
            {
                int statusRow = eb.Inspection?.Status?.Screen.Y ?? ea.Inspection?.Status?.Screen.Y ?? -1;
                bool chars = false, attrs = false, status = false;
                for (int y = 0; y < ca.Height; y++)
                    for (int x = 0; x < ca.Width; x++)
                    {
                        var p = ca[x, y];
                        var q = cb[x, y];
                        bool c = p.Character != q.Character, t = (byte)p.Attr != (byte)q.Attr;
                        if (!c && !t) continue;
                        if (y == statusRow) { status = true; continue; }
                        chars |= c; attrs |= t;
                    }
                if (chars) return (Diff.Significant, "cell text changed outside the status line");
                if (attrs) return (Diff.Noise, ta is not null && tb is not null && !string.Equals(ta.FocusedPath, tb.FocusedPath, StringComparison.Ordinal)
                    ? "only focus/highlight attributes changed (focused view moved)" : "only highlight attributes changed");
                return (Diff.Noise, status ? "only the status line row changed (hint/status text)" : "no visible cell change");
            }
            if (ta is not null && tb is not null)
            {
                var na = Nodes(ta.Root);
                var nb = Nodes(tb.Root);
                if (!na.SequenceEqual(nb)) return (Diff.Significant, "view texts changed (view tree)");
                return (Diff.Noise, string.Equals(ta.FocusedPath, tb.FocusedPath, StringComparison.Ordinal)
                    ? "view tree unchanged" : "only the focused view changed (view tree)");
            }
            return (Diff.Unknown, "");
        }

        private static List<(string, string?, CellRect)> Windows(ViewTree tree)
        {
            var list = new List<(string, string?, CellRect)>();
            void Walk(ViewNode node)
            {
                if (node.Visible is not null && (node.Kind.EndsWith("Window", StringComparison.Ordinal) || node.Kind.EndsWith("Dialog", StringComparison.Ordinal)))
                    list.Add((node.Type, node.Text, node.Screen));
                foreach (var child in node.Children) Walk(child);
            }
            Walk(tree.Root);
            return list;
        }

        private static List<(string, string, string?)> Nodes(ViewNode root)
        {
            var list = new List<(string, string, string?)>();
            void Walk(ViewNode node) { list.Add((node.Path, node.Type, node.Text)); foreach (var c in node.Children) Walk(c); }
            Walk(root);
            return list;
        }

        // ── assembly ───────────────────────────────────────────────────────────────────────────────────────────────────

        private DetectionResult Assemble(Decision[] decisions, int stateBoundaries)
        {
            int typing = 0, unchanged = 0, unsettled = 0, periodic = 0, insignificant = 0, overflow = 0;
            var candidates = new List<StableChangeCandidate>();
            long? firstOverflow = null;
            int hashOnly = 0, degraded = 0;
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                var d = decisions[i];
                unchanged += s.All.Count - 1;
                switch (d.Bucket)
                {
                    case Bucket.Typing: typing++; continue;
                    case Bucket.Unchanged: unchanged++; continue;
                    case Bucket.Unsettled: unsettled++; continue;
                    case Bucket.Periodic: periodic++; continue;
                    case Bucket.Insignificant: insignificant++; continue;
                }
                if (candidates.Count >= Math.Max(0, options.MaximumCandidates)) { overflow++; firstOverflow ??= s.First.Id; continue; }
                var c = Build(s, d);
                if (c.Evidence == EvidenceLevels.HashOnly) hashOnly++;
                if (c.Evidence == EvidenceLevels.Missing) degraded++;
                candidates.Add(c);
            }
            if (overflow > 0)
                diagnostics.Add($"{overflow} more candidate(s) exceeded the cap of {options.MaximumCandidates} and were not reported "
                    + $"(first at boundary {firstOverflow}); raise MaximumCandidates or record a shorter interaction.");
            if (hashOnly > 0)
                diagnostics.Add($"{hashOnly} candidate(s) rely on the boundary hash/signature only (no snapshot or live inspection): "
                    + "significance was inferred from the menu/modal signature and the input kind.");
            if (degraded > 0)
                diagnostics.Add($"{degraded} candidate(s) have missing evidence (snapshot dropped, not fetched, partly omitted, or recovered).");
            if (undecodable > 0)
                diagnostics.Add($"{undecodable} snapshot/inspection cell payload(s) could not be decoded and were ignored as evidence.");
            var counters = new DetectorCounters(stateBoundaries, pickBoundaries, candidates.Count, typing, unchanged, unsettled, periodic,
                insignificant, overflow);
            return new(candidates, diagnostics, counters);
        }

        private StableChangeCandidate Build(State s, Decision d)
        {
            var flags = new List<string>();
            var snap = OwnSnapshot(s);
            var live = LiveSample(s);
            bool dropped = s.All.Any(b => b.SnapshotDropped);
            bool notFetched = s.All.Any(b => b.SnapshotId is { } id && !snapshotsById.ContainsKey(id));
            bool omitted = snap?.Omitted is not null;
            bool application = string.Equals(s.First.Cause, Protocol.BoundaryCauses.Application, StringComparison.Ordinal);
            bool gap = s.All.Any(b => b.AfterGap) || (s.Index > 0 && InputsBetween(states[s.Index - 1], s).Any(e => e.AfterGap));
            bool recovered = input.Capture.Recovered;

            string evidence = snap is not null && !omitted ? EvidenceLevels.Snapshot
                : live is not null ? EvidenceLevels.Inspection
                : dropped || notFetched || omitted || recovered ? EvidenceLevels.Missing
                : EvidenceLevels.HashOnly;

            if (dropped) flags.Add(CandidateFlags.SnapshotDropped);
            if (notFetched) flags.Add(CandidateFlags.SnapshotNotFetched);
            if (omitted) flags.Add(CandidateFlags.InspectionOmitted);
            if (evidence == EvidenceLevels.HashOnly) flags.Add(HashOnlyFlag);
            if (Incomplete) flags.Add(CandidateFlags.CoverageIncomplete);
            if (recovered) flags.Add(CandidateFlags.Recovered);
            if (gap) flags.Add(CandidateFlags.AfterGap);
            if (d.Kind == CandidateKinds.Resize) flags.Add(CandidateFlags.AfterResize);
            if (application) flags.Add(CandidateFlags.Async);
            if (d.NeverQuiescent) flags.Add(NeverQuiescentFlag);

            int level = evidence switch
            {
                EvidenceLevels.Snapshot or EvidenceLevels.Inspection => 2,
                EvidenceLevels.HashOnly => application && d.Kind == CandidateKinds.Async ? 0 : 1,
                _ => 0,
            };
            if (application && level == 2) level = 1;
            if (Incomplete) level = Math.Min(level - 1, 1);
            if (gap) level--;
            if (d.Kind == CandidateKinds.Resize) level--;
            if (d.NeverQuiescent || evidence == EvidenceLevels.Missing) level = 0;
            string confidence = level >= 2 ? Confidences.High : level == 1 ? Confidences.Medium : Confidences.Low;

            var b0 = s.First;
            return new($"c-{b0.Id}", b0.Id, s.Last.Id, b0.AfterSequence, d.EndSequence, b0.Cause, d.Kind!, d.Stability!, evidence, confidence,
                d.Reason, b0.Hash, b0.Generation, b0.Width, b0.Height, b0.ModalTop, b0.MenuDepth, snap?.Id, gap, flags);
        }
    }
}
