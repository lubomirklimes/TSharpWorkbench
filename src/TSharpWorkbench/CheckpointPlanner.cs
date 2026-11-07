using System.Globalization;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Pure checkpoint planner. Detector candidates + resolved picks -> schema-v1 capture checkpoints at
// valid converted-step boundaries. Nothing is persisted, the conversion is never mutated, no assertion is ever produced.
//
// Placement (anchor A = the state's AfterSequence, i.e. the last recorded input delivered before the state):
//  * k = last converted step with 0 < First <= A (RecordingCaptureTimeline.StepIndexAfter); none -> before the first step.
//  * Trailing synthetic steps (the converter's end-of-recording `release` and `modifiers ... up` cleanup) replay no recorded
//    input that happened before the state; a checkpoint never moves past them (it goes before the first of them).
//  * First <= A < Last (A strictly inside a coalesced step): the step is split at A when the split replays identically
//    (same InputPlanner event stream, same SelfCheck result); otherwise the proposal is `unrepresentable`.
//      text run      -> split per recorded character event       (identical: one key event per character either way)
//      wheel run     -> wheel*k + wheel*(N-k) at the same cell   (identical: N notches, no extra move)
//      click         -> down-B@x,y + up-B@x,y                    (identical: [move], down, up)
//      double click  -> unrepresentable (the second press carries meDoubleClick; no v1 step replays half a double)
//      resize run    -> unrepresentable (an intermediate resize step would add a resize the converted replay omits)
//      key/modifiers/down/up/release are single-input steps (First == Last): never strictly inside.

/// <summary>Pure planner: detector candidates + resolved picks -> schema-v1 checkpoint proposals at valid step boundaries.</summary>
internal static class CheckpointPlanner
{
    /// <summary>Planner-added flags (in addition to the detector's <see cref="CandidateFlags"/> and pick flags).</summary>
    internal static class Flags
    {
        internal const string Split = "split", Unrepresentable = "unrepresentable", ConversionMismatch = "conversion-mismatch",
            SharedPosition = "shared-position", BeforeFinalRelease = "before-final-release", PickOnly = "pick-only",
            Merged = "merged", IncompleteEvidence = "incomplete-evidence";
    }

    internal const string PickKind = "pick";

    private sealed class Group
    {
        internal readonly List<StableChangeCandidate> Candidates = [];
        internal readonly List<PickSelection> Picks = [];
        internal long Anchor, BoundaryId;
        internal string Key = "";
    }

    private sealed record Placed(int After, string Placement, string? Problem, bool BeforeRelease, bool Inside);

    internal static CheckpointPlan Plan(PlannerInput input, PlannerOptions? options = null)
    {
        options ??= PlannerOptions.Default;
        var diagnostics = new List<string>();
        var original = input.Conversion.Steps;
        var events = input.Events.OrderBy(e => e.Sequence).ToArray();
        string prefix = Prefix(options.IdPrefix, diagnostics);
        bool mismatch = input.Conversion.Mismatches.Count > 0;
        if (mismatch)
            diagnostics.Add("The conversion's replay self-check reported differences; every proposal needs review (placement is relative to "
                + "steps that do not replay the recording exactly).");

        // ── group: one group per distinct state ────────────────────────────────────────────────────────────────────
        var groups = GroupStates(input, events, diagnostics);
        if (groups.Count == 0) return new(original, false, [], diagnostics);
        groups.Sort((a, b) => a.Anchor != b.Anchor ? a.Anchor.CompareTo(b.Anchor) : a.BoundaryId != b.BoundaryId
            ? a.BoundaryId.CompareTo(b.BoundaryId) : string.CompareOrdinal(a.Key, b.Key));
        if (options.MaximumProposals >= 0 && groups.Count > options.MaximumProposals)
        {
            var dropped = groups.Skip(options.MaximumProposals).ToArray();
            diagnostics.Add($"{dropped.Length} proposal(s) over the cap of {options.MaximumProposals} were not proposed "
                + $"(first omitted: boundary {dropped[0].BoundaryId} after input {dropped[0].Anchor}).");
            groups = groups.Take(options.MaximumProposals).ToList();
        }

        // ── splits: anchors strictly inside coalesced steps ────────────────────────────────────────────────────────
        int trailing = TrailingSynthetic(original);
        var anchorsByStep = new SortedDictionary<int, SortedSet<long>>();
        foreach (var g in groups)
        {
            int k = StepIndex(g.Anchor, original);
            if (k >= trailing) continue;
            if (k >= 0 && original[k].Last > g.Anchor)
            {
                if (!anchorsByStep.TryGetValue(k, out var set)) anchorsByStep[k] = set = [];
                set.Add(g.Anchor);
            }
        }
        var splitter = new Splitter(input, events);
        var accepted = new Dictionary<int, StepSplit>();
        var refused = new Dictionary<int, string>();
        foreach (var (k, anchors) in anchorsByStep)
        {
            var split = splitter.TrySplit(k, anchors, out string? problem);
            if (split is not null) accepted[k] = split; else refused[k] = problem!;
        }
        if (accepted.Count > 0 && !splitter.ReplaysIdentically(Compose(original, accepted), out string? combined))
        {
            foreach (var k in accepted.Keys) refused[k] = "the combined split did not replay identically (" + combined + ")";
            accepted.Clear();
        }
        var steps = accepted.Count == 0 ? original : Compose(original, accepted);
        var start = new int[original.Count + 1];
        for (int i = 0, n = 0; i <= original.Count; i++) { start[i] = n; if (i < original.Count) n += accepted.TryGetValue(i, out var s) ? s.Segments.Count : 1; }
        int last(int k) => k < 0 ? -1 : start[k] + (accepted.TryGetValue(k, out var s) ? s.Segments.Count : 1) - 1;

        // ── place ──────────────────────────────────────────────────────────────────────────────────────────────────
        var placed = new List<(Group Group, Placed Where)>();
        foreach (var g in groups)
        {
            int k = StepIndex(g.Anchor, original);
            bool beforeRelease = false;
            if (k >= trailing) { k = trailing - 1; beforeRelease = true; }
            Placed where;
            if (k < 0) where = new(-1, Placements.BeforeFirst, null, beforeRelease, false);
            else if (original[k].Last <= g.Anchor || beforeRelease) where = new(last(k), Placements.AfterStep, null, beforeRelease, false);
            else if (accepted.TryGetValue(k, out var split))
            {
                int segment = split.SegmentOf(g.Anchor);
                bool inside = segment < split.Segments.Count - 1;
                where = new(start[k] + segment, inside ? Placements.Split : Placements.AfterStep, null, false, inside);
            }
            else
                where = new(k - 1 < 0 ? -1 : last(k - 1), Placements.Unrepresentable,
                    $"The state after input {g.Anchor} falls inside step {k + 1} ({original[k].Step.Kind} {original[k].Step.Parameters}, inputs "
                    + $"{original[k].First}..{original[k].Last}), which cannot be split there without changing the replay: {refused[k]}.", false, true);
            if (where.Placement != Placements.Unrepresentable && !Ordered(steps, where.After, g.Anchor, trailing - original.Count + steps.Count))
                where = where with { Placement = Placements.Unrepresentable, Problem = $"The converter emitted steps out of input order around input {g.Anchor} "
                    + "(e.g. a key pressed while a mouse button was held); no step boundary separates the inputs before and after this state." };
            placed.Add((g, where));
        }
        var positions = placed.Where(p => p.Where.Placement != Placements.Unrepresentable).GroupBy(p => p.Where.After)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();

        // ── proposals ──────────────────────────────────────────────────────────────────────────────────────────────
        var used = new HashSet<string>(input.ExistingCheckpointIds ?? [], StringComparer.OrdinalIgnoreCase);
        var proposalIds = new HashSet<string>(StringComparer.Ordinal);
        var proposals = new List<CheckpointProposal>();
        int ordinal = 0;
        foreach (var (g, where) in placed)
        {
            ordinal++;
            var rep = g.Candidates.FirstOrDefault();
            string kind = rep?.Kind ?? PickKind;
            var flags = new List<string>();
            void Flag(string f) { if (!flags.Contains(f)) flags.Add(f); }
            foreach (var c in g.Candidates) foreach (var f in c.Flags) Flag(f);
            foreach (var p in g.Picks) foreach (var f in p.Flags) Flag(f);
            if (g.Candidates.Any(c => c.AfterGap)) Flag(CandidateFlags.AfterGap);
            string evidence, confidence, reason;
            long? snapshot;
            if (rep is not null)
            {
                evidence = rep.Evidence; snapshot = rep.SnapshotId;
                confidence = g.Candidates.Select(c => c.Confidence).MinBy(Rank)!;
                reason = rep.Reason + (g.Candidates.Count > 1 ? $" (+{g.Candidates.Count - 1} merged candidate(s) of the same state)" : "");
                if (g.Candidates.Count > 1) Flag(Flags.Merged);
            }
            else
            {
                var pick = g.Picks[0];
                evidence = pick.Evidence; snapshot = pick.SnapshotId;
                confidence = pick.Evidence == EvidenceLevels.Snapshot ? Confidences.Medium : Confidences.Low;
                reason = $"Picked by the user ({pick.Kind}, {pick.Trigger}) at cells {pick.Region.X},{pick.Region.Y} {pick.Region.Width}x{pick.Region.Height}; "
                    + "no detected state change matched the pick.";
                Flag(Flags.PickOnly);
            }
            if (evidence is not (EvidenceLevels.Snapshot or EvidenceLevels.Inspection) && flags.Count == 0) Flag(Flags.IncompleteEvidence);
            if (mismatch) Flag(Flags.ConversionMismatch);
            if (where.BeforeRelease) Flag(Flags.BeforeFinalRelease);
            if (where.Placement == Placements.Split) Flag(Flags.Split);
            bool shared = where.Placement != Placements.Unrepresentable && positions.Contains(where.After);
            if (shared) Flag(Flags.SharedPosition);
            bool unrepresentable = where.Placement == Placements.Unrepresentable;
            if (unrepresentable) { Flag(Flags.Unrepresentable); diagnostics.Add(where.Problem!); }

            string checkpointId = CheckpointId(prefix, kind, ordinal, used);
            used.Add(checkpointId);
            string id = "p-" + g.BoundaryId.ToString(CultureInfo.InvariantCulture);
            for (int n = 2; !proposalIds.Add(id); n++) id = "p-" + g.BoundaryId.ToString(CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture);
            bool review = unrepresentable || mismatch || shared || confidence == Confidences.Low || evidence == EvidenceLevels.Missing
                || flags.Contains(CandidateFlags.AfterGap) || flags.Contains(CandidateFlags.Recovered);
            proposals.Add(new(id, checkpointId, where.After, where.Placement, unrepresentable ? null : ScenarioDocumentStep.Checkpoint(checkpointId),
                kind, reason, confidence, evidence, g.Anchor, g.BoundaryId, snapshot, g.Candidates.Select(c => c.Id).ToArray(), g.Picks.ToArray(),
                review, flags));
        }
        if (positions.Count > 0)
            diagnostics.Add("Several proposed states share one step boundary (no replayed input between them, e.g. an asynchronous change); "
                + "replay captures them at the same moment, so review which one to keep.");
        return new(steps, accepted.Count > 0, proposals, diagnostics);
    }

    // ── grouping ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<Group> GroupStates(PlannerInput input, RecordedInput[] events, List<string> diagnostics)
    {
        // Replay position of an anchor: the presses/notches/resizes replayed before it. Equal position + equal hash = the same
        // observable state at the same replay moment.
        var marks = events.Where(e => Replayed(e) || e.Kind == "resize").Select(e => e.Sequence).ToArray();
        long Position(long anchor) { int i = Array.BinarySearch(marks, anchor); return i >= 0 ? i + 1 : ~i; }

        var candidates = input.Candidates.OrderBy(c => c.AfterSequence).ThenBy(c => c.BoundaryId).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var parent = Enumerable.Range(0, candidates.Length).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            foreach (var key in new[] { "b:" + c.BoundaryId, $"h:{c.Hash}:{c.AfterSequence}", $"p:{c.Hash}:{Position(c.AfterSequence)}" })
                if (owner.TryGetValue(key, out int j)) { int a = Find(i), b = Find(j); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
                else owner[key] = i;
        }
        var byRoot = new SortedDictionary<int, Group>();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (!byRoot.TryGetValue(Find(i), out var g)) byRoot[Find(i)] = g = new();
            g.Candidates.Add(candidates[i]);
        }
        var groups = byRoot.Values.ToList();
        foreach (var g in groups)
        {
            g.Candidates.Sort((a, b) => a.BoundaryId != b.BoundaryId ? a.BoundaryId.CompareTo(b.BoundaryId) : string.CompareOrdinal(a.Id, b.Id));
            g.Anchor = g.Candidates.Min(c => c.AfterSequence); g.BoundaryId = g.Candidates[0].BoundaryId; g.Key = g.Candidates[0].Id;
        }

        // Picks: deduplicate by PickId, attach to the state they anchor, else one pick-only proposal per distinct state.
        var seen = new HashSet<long>();
        var picks = new List<PickSelection>();
        foreach (var p in input.Picks.OrderBy(p => p.PickId))
            if (seen.Add(p.PickId)) picks.Add(p);
            else diagnostics.Add($"Pick {p.PickId} was supplied more than once; it is attached once.");
        var pickGroups = new List<Group>();
        foreach (var p in picks)
        {
            Group? target = groups.SelectMany(g => g.Candidates.Select(c => (g, c)))
                .Where(x => x.c.BoundaryId <= p.AnchorBoundaryId && p.AnchorBoundaryId <= Math.Max(x.c.BoundaryId, x.c.LastBoundaryId))
                .OrderByDescending(x => x.c.BoundaryId).Select(x => x.g).FirstOrDefault()
                ?? groups.FirstOrDefault(g => g.Candidates.Any(c => c.Hash == p.Hash && c.AfterSequence == p.AfterSequence))
                ?? groups.FirstOrDefault(g => g.Candidates.Any(c => c.Hash == p.Hash && Position(c.AfterSequence) == Position(p.AfterSequence)));
            if (target is not null) { target.Picks.Add(p); continue; }
            var same = pickGroups.FirstOrDefault(g => g.Picks.Any(q => (p.AnchorBoundaryId > 0 && q.AnchorBoundaryId == p.AnchorBoundaryId)
                || (q.Hash == p.Hash && (q.AfterSequence == p.AfterSequence || Position(q.AfterSequence) == Position(p.AfterSequence)))));
            if (same is not null) { same.Picks.Add(p); same.Anchor = Math.Min(same.Anchor, p.AfterSequence); continue; }
            pickGroups.Add(new() { Anchor = p.AfterSequence, BoundaryId = p.AnchorBoundaryId > 0 ? p.AnchorBoundaryId : p.PickId, Key = "pick-" + p.PickId, Picks = { p } });
            if (p.AnchorBoundaryId <= 0) diagnostics.Add($"Pick {p.PickId} has no anchoring state boundary; its proposal uses the pick itself as the anchor.");
        }
        groups.AddRange(pickGroups);
        return groups;
    }

    private static int Rank(string confidence) => confidence switch { Confidences.High => 2, Confidences.Medium => 1, _ => 0 };

    // ── placement helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Same rule as RecordingCaptureTimeline.StepIndexAfter: the last step with 0 &lt; First &lt;= anchor, else -1.</summary>
    internal static int StepIndex(long anchor, IReadOnlyList<RecordedStep> steps)
    {
        int result = -1;
        for (int i = 0; i < steps.Count; i++) if (steps[i].First <= anchor && steps[i].First > 0) result = i;
        return result;
    }

    /// <summary>Index of the first converter end-of-recording cleanup step (release / modifiers up), or Count when none.</summary>
    internal static int TrailingSynthetic(IReadOnlyList<RecordedStep> steps)
    {
        int i = steps.Count;
        while (i > 0 && (steps[i - 1].Step.Kind == "release" || steps[i - 1].Step.Kind == "modifiers" && steps[i - 1].Step.String("state") == "up")) i--;
        return i;
    }

    /// <summary>Every input-carrying step at or before <paramref name="after"/> lies at or before the anchor and every later
    /// one after it (trailing cleanup steps excluded).</summary>
    private static bool Ordered(IReadOnlyList<RecordedStep> steps, int after, long anchor, int trailing)
    {
        for (int i = 0; i < trailing; i++)
        {
            if (steps[i].First <= 0) continue;
            if (i <= after ? steps[i].Last > anchor : steps[i].First <= anchor) return false;
        }
        return true;
    }

    private static bool Replayed(RecordedInput e) => e.What is Events.evKeyDown or Events.evMouseDown or Events.evMouseUp or Events.evMouseWheel
        && !(e.Kind == "mouse" && (e.X < 0 || e.Y < 0 || e.X >= e.Width || e.Y >= e.Height));

    private static IReadOnlyList<RecordedStep> Compose(IReadOnlyList<RecordedStep> steps, Dictionary<int, StepSplit> splits)
    {
        var result = new List<RecordedStep>(steps.Count + splits.Sum(s => s.Value.Segments.Count));
        for (int i = 0; i < steps.Count; i++)
            if (splits.TryGetValue(i, out var s)) result.AddRange(s.Segments); else result.Add(steps[i]);
        return result;
    }

    // ── ids ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Prefix(string? prefix, List<string> diagnostics)
    {
        string clean = Slug(prefix ?? "", 20);
        if (clean.Length > 0) return clean;
        diagnostics.Add($"The checkpoint id prefix '{prefix}' has no usable characters; 'rec' is used.");
        return "rec";
    }

    private static string Slug(string text, int max)
    {
        var chars = text.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray();
        string slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length <= max ? slug : slug[..max].TrimEnd('-');
    }

    private static string CheckpointId(string prefix, string kind, int ordinal, HashSet<string> used)
    {
        string n = ordinal.ToString(CultureInfo.InvariantCulture);
        string slug = Slug(kind, Math.Max(1, 48 - prefix.Length - n.Length - 6));
        string baseId = slug.Length == 0 ? $"{prefix}-{n}" : $"{prefix}-{slug}-{n}";
        string id = baseId;
        for (int m = 2; used.Contains(id) || !Protocol.IsValidName(id); m++)
        {
            id = baseId + "-" + m.ToString(CultureInfo.InvariantCulture);
            if (id.Length > 48) id = $"{prefix}-{n}-{m.ToString(CultureInfo.InvariantCulture)}";
        }
        return id;
    }

    // ── splitting ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record StepSplit(IReadOnlyList<RecordedStep> Segments, IReadOnlyList<long> CutAfter)
    {
        /// <summary>The segment the anchor follows: the segment that contains the last split unit at or before it.</summary>
        internal int SegmentOf(long anchor)
        {
            int s = 0;
            while (s + 1 < Segments.Count && Segments[s + 1].First <= anchor) s++;
            return s;
        }
    }

    private sealed class Splitter(PlannerInput input, RecordedInput[] events)
    {
        private readonly IReadOnlyList<RecordedStep> steps = input.Conversion.Steps;
        private IReadOnlyList<string>? baseline, baselineCheck;

        /// <summary>Splits step k after every anchor (each strictly inside it) or explains why it cannot.</summary>
        internal StepSplit? TrySplit(int k, IEnumerable<long> anchors, out string? problem)
        {
            var step = steps[k];
            var doc = step.Step;
            problem = null;
            // Units: (sequence of the recorded input, payload) in replay order; a segment is a run of consecutive units.
            List<(long Seq, string Payload)> units;
            Func<IReadOnlyList<(long Seq, string Payload)>, bool, bool, ScenarioDocumentStep> build;
            switch (doc.Kind)
            {
                case "text":
                {
                    units = events.Where(e => e.Sequence >= step.First && e.Sequence <= step.Last && Printable(e)).Select(e => (e.Sequence, e.Text!)).ToList();
                    if (string.Concat(units.Select(u => u.Payload)) != doc.Name)
                    { problem = "the recorded characters of the text run could not be matched to its text"; return null; }
                    build = (u, first, _) => ScenarioDocumentStep.Text(string.Concat(u.Select(x => x.Payload)), first ? doc.Note : null);
                    break;
                }
                case "mouse" when InputSteps.TryParseMouse(doc.Name, out var mouse) && mouse.Verb == "wheel":
                {
                    string at = At(doc.Name), name = doc.Name[..doc.Name.IndexOf('@')];
                    units = events.Where(e => e.Sequence >= step.First && e.Sequence <= step.Last && e.Kind == "mouse" && e.What == Events.evMouseWheel
                        && (e.EventFlags & mouse.WheelFlag) != 0 && $"{e.X},{e.Y}" == at).Select(e => (e.Sequence, "")).ToList();
                    if (units.Count != mouse.Count) { problem = $"{units.Count} recorded wheel notches do not match the step's count {mouse.Count}"; return null; }
                    build = (u, first, _) => ScenarioDocumentStep.Mouse(name + "@" + at + (u.Count == 1 ? "" : "*" + u.Count.ToString(CultureInfo.InvariantCulture)), first ? doc.Note : null);
                    break;
                }
                case "mouse" when InputSteps.TryParseMouse(doc.Name, out var mouse) && mouse.Verb == "click":
                {
                    string at = At(doc.Name), button = InputSteps.ButtonName(mouse.Button);
                    units = [(step.First, "down"), (step.Last, "up")];
                    build = (u, first, _) => u.Count == 2 ? doc : ScenarioDocumentStep.Mouse(u[0].Payload + "-" + button + "@" + at, first ? doc.Note : null);
                    break;
                }
                case "mouse" when InputSteps.TryParseMouse(doc.Name, out var mouse) && mouse.Verb == "double":
                    problem = "a double-click step replays both presses and flags the second one meDoubleClick; schema v1 has no step that replays "
                        + "one press of a double click, and two click steps would replay the second press without the double-click flag";
                    return null;
                case "resize":
                    problem = "the converter merged consecutive grid changes into one resize step; an intermediate resize step would replay a "
                        + "resize the converted scenario does not perform";
                    return null;
                default:
                    problem = $"a {doc.Kind} step cannot be split";
                    return null;
            }
            var cuts = new SortedSet<int>();
            foreach (long a in anchors)
            {
                int j = units.FindLastIndex(u => u.Seq <= a);
                if (j < 0 || j >= units.Count - 1) { problem = $"input {a} is not between two inputs of the step"; return null; }
                cuts.Add(j);
            }
            var segments = new List<RecordedStep>();
            int from = 0;
            foreach (int cut in cuts.Append(units.Count - 1))
            {
                var part = units.GetRange(from, cut - from + 1);
                ScenarioDocumentStep built;
                try { built = build(part, from == 0, cut == units.Count - 1); }
                catch (ArgumentException ex) { problem = "a split part is not valid schema v1: " + ex.Message; return null; }
                segments.Add(new(built, from == 0 ? step.First : part[0].Seq, cut == units.Count - 1 ? step.Last : part[^1].Seq));
                from = cut + 1;
            }
            var split = new StepSplit(segments, cuts.Select(c => units[c].Seq).ToArray());
            if (!ReplaysIdentically(Compose(steps, new() { [k] = split }), out string? difference)) { problem = "the split did not replay identically (" + difference + ")"; return null; }
            return split;
        }

        /// <summary>Same InputPlanner event stream (every event, not only presses) and same SelfCheck result as the conversion.</summary>
        internal bool ReplaysIdentically(IReadOnlyList<RecordedStep> candidate, out string? difference)
        {
            baseline ??= Stream(steps.Select(s => s.Step).ToArray(), input.Width, input.Height);
            baselineCheck ??= RecordingConverter.SelfCheck(steps.Select(s => s.Step).ToArray(), input.Events, input.Width, input.Height);
            var docs = candidate.Select(s => s.Step).ToArray();
            var stream = Stream(docs, input.Width, input.Height);
            difference = null;
            if (!stream.SequenceEqual(baseline))
            {
                int i = 0;
                while (i < Math.Min(stream.Count, baseline.Count) && stream[i] == baseline[i]) i++;
                difference = $"replayed event {i + 1} differs: {(i < baseline.Count ? baseline[i] : "none")} vs {(i < stream.Count ? stream[i] : "none")}";
                return false;
            }
            var check = RecordingConverter.SelfCheck(docs, input.Events, input.Width, input.Height);
            if (!check.SequenceEqual(baselineCheck)) { difference = "self-check differs: " + string.Join(" ", check); return false; }
            return true;
        }
    }

    private static string At(string mouseInput)
    {
        int at = mouseInput.IndexOf('@'), star = mouseInput.IndexOf('*');
        return star < 0 ? mouseInput[(at + 1)..] : mouseInput[(at + 1)..star];
    }

    private static bool Printable(RecordedInput e) => e.Kind == "key" && e.Text is { Length: > 0 } t && InputSteps.IsValidText(t)
        && (e.ControlKeyState & (Keys.kbCtrlShift | Keys.kbAltShift)) == 0;

    /// <summary>Every event InputPlanner would deliver for the steps (resize steps as pseudo-events), as comparable text.</summary>
    internal static IReadOnlyList<string> Stream(IReadOnlyList<ScenarioDocumentStep> steps, int width, int height)
    {
        var result = new List<string>();
        var state = InputSnapshot.Empty;
        foreach (var step in steps)
        {
            if (step.Kind == "resize") { var (w, h) = ScenarioSelection.ParseSize(step.Name); width = w; height = h; result.Add($"resize {w}x{h}"); continue; }
            if (step.Kind == "checkpoint") continue;
            string kind = step.Kind switch { "modifiers" => InputSteps.Modifier, "keyDown" => InputSteps.KeyDown, "keyUp" => InputSteps.KeyUp, _ => step.Kind };
            try
            {
                var plan = InputPlanner.Create(kind, step.Name, state, width, height, _ => null);
                foreach (var p in plan.Events)
                {
                    var e = p.Event;
                    result.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{e.What:x4} m{e.mouse.where.x},{e.mouse.where.y} b{e.mouse.buttons} f{e.mouse.eventFlags:x} mc{e.mouse.controlKeyState:x} k{e.keyDown.keyCode:x4} cs{e.keyDown.charScan.ToUShort():x4} kc{e.keyDown.controlKeyState:x} t'{e.keyDown.text}' {(p.Mouse ? "mouse" : "key")}"));
                }
                state = plan.After;
            }
            catch (InvalidOperationException ex) { result.Add("error " + ex.Message); return result; }
        }
        return result;
    }
}
