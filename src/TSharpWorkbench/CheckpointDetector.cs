using System.Globalization;
using TSharpWorkbench.Testing;

// Stable-change detector. Pure and deterministic: no I/O, UI, clock or randomness; the same
// DetectorInput always yields the same DetectionResult.
//
// Pipeline (CheckpointDetector.Run):
//  1. Legacy target (no boundaries negotiated): no candidates, one diagnostic.
//  2. Pick boundaries are counted and removed: anchors, never states, never ending or splitting a state.
//  3. State boundaries in causal order (RecordingCaptureTimeline.Ordered semantics) are grouped into states: a run of
//     consecutive boundaries with the same key (Hash, ModalTop, MenuDepth, grid) is ONE state (pre-input + idle twins,
//     recovered duplicates); every boundary after the first of a run is counted Unchanged.
//  4. Each state is classified against its predecessor: resize, menu/modal transition (cancelled close = Unchanged),
//     application-caused (async / periodic), unrecorded input, retained transient screen, command-like input, or weak
//     input (text/navigation: Typing). Snapshot/inspection evidence vetoes noise (status row, highlight/focus only) and can
//     promote the end of a text run; without evidence the signature + input-kind rules apply and the Reason says so.
//  5. Stability from causal successors only: the next recorded input (input-ended, or settled for an application-caused
//     state), the next state's recording-clock Milliseconds (settled after SettleMilliseconds), the end (final only when
//     Final), or transient. Live open states are pending (Unsettled).
//  6. Evidence/flags/confidence honour coverage; MaximumCandidates overflow is counted and reported.

/// <summary>Pure, deterministic stable-change detector over one recording's capture timeline.</summary>
internal static partial class CheckpointDetector
{
    /// <summary>Extra flag (not yet in <see cref="CandidateFlags"/>): only the boundary hash/signature backs the candidate.</summary>
    internal const string HashOnlyFlag = "hash-only";
    /// <summary>Extra flag: the state was followed by never-quiescent application redraws (periodic stretch).</summary>
    internal const string NeverQuiescentFlag = "never-quiescent";

    internal static DetectionResult Detect(DetectorInput input, DetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new Run(input, options ?? DetectorOptions.Default).Execute();
    }

    private enum Bucket { Candidate, Typing, Unchanged, Unsettled, Periodic, Insignificant, Overflow }

    private enum Ending { InputEnded, SettledByTime, Superseded, Final, Pending }

    private enum InputClass { Neutral, Text, Navigation, Command, Resize }

    private sealed class State(int index, RecordedBoundary first)
    {
        internal readonly int Index = index;
        internal readonly RecordedBoundary First = first;
        internal readonly List<RecordedBoundary> All = [first];
        internal RecordedBoundary Last => All[^1];
        internal bool Menu => First.MenuDepth > 0;
        internal bool Transient => First.MenuDepth > 0 || First.ModalTop is not null;
        internal (ulong, string?, int, int, int) Key => Of(First);
        internal static (ulong, string?, int, int, int) Of(RecordedBoundary b) => (b.Hash, b.ModalTop, b.MenuDepth, b.Width, b.Height);
        internal bool SameSignature(State other) => First.MenuDepth == other.First.MenuDepth
            && string.Equals(First.ModalTop, other.First.ModalTop, StringComparison.Ordinal);
    }

    private sealed record Decision(Bucket Bucket, string Reason, string? Kind = null, bool Promotable = false, bool NeverQuiescent = false,
        string? Stability = null, long? EndSequence = null, bool EvidenceBacked = false);

    private sealed partial class Run
    {
        private readonly DetectorInput input;
        private readonly DetectorOptions options;
        private readonly RecordedInput[] events;
        private readonly List<string> diagnostics = [];
        private readonly List<State> states = [];
        private int pickBoundaries;

        internal Run(DetectorInput input, DetectorOptions options)
        {
            this.input = input;
            this.options = options;
            events = input.Events.OrderBy(e => e.Sequence).ToArray();
            snapshotsById = Index(input.Snapshots);
        }

        internal DetectionResult Execute()
        {
            var ordered = input.Boundaries.OrderBy(b => b.AfterSequence).ThenBy(b => b.Id).ToArray();
            var stateBoundaries = new List<RecordedBoundary>(ordered.Length);
            foreach (var b in ordered)
                if (IsPick(b)) pickBoundaries++; else stateBoundaries.Add(b);

            if (!input.Capture.BoundariesEnabled)
            {
                diagnostics.Add("Legacy target: no UI boundaries were captured, so no checkpoints can be proposed. "
                    + (input.Capture.Diagnostic ?? "Re-prepare the target (Fix Issues or Prepare target) to enable Smart Recorder capture."));
                return new([], diagnostics, new(stateBoundaries.Count, pickBoundaries, 0, 0, 0, 0, 0, stateBoundaries.Count, 0));
            }

            foreach (var b in stateBoundaries)
            {
                if (states.Count > 0 && states[^1].Key == State.Of(b)) states[^1].All.Add(b);
                else states.Add(new(states.Count, b));
            }

            if (stateBoundaries.Count == 0)
                diagnostics.Add(input.Boundaries.Count == 0 ? "No UI boundaries were captured yet: nothing to propose."
                    : "Only pick anchors were captured: no UI state changes to propose.");

            CoverageDiagnostics();
            var decisions = Classify();
            return Assemble(decisions, stateBoundaries.Count);
        }

        private static bool IsPick(RecordedBoundary b) =>
            b.Pick is not null || string.Equals(b.Kind, Protocol.BoundaryKinds.Pick, StringComparison.Ordinal)
            || string.Equals(b.Cause, Protocol.BoundaryCauses.Pick, StringComparison.Ordinal);

        private bool Incomplete => !input.Capture.CoverageComplete || input.Capture.BoundariesLost > 0 || input.Capture.SnapshotsLost > 0
            || input.Capture.Recovered || !input.Capture.SnapshotsEnabled || input.Capture.Coverage is { Complete: false };

        private void CoverageDiagnostics()
        {
            var c = input.Capture;
            if (Incomplete)
            {
                var parts = new List<string>();
                if (!c.SnapshotsEnabled) parts.Add("transient snapshots not supported by the target");
                if (c.BoundariesLost > 0) parts.Add($"{c.BoundariesLost} boundaries lost in Workbench");
                if (c.SnapshotsLost > 0) parts.Add($"{c.SnapshotsLost} snapshots lost");
                if (c.Coverage is { } cov)
                {
                    if (cov.BoundariesDropped > 0) parts.Add($"{cov.BoundariesDropped} boundaries dropped by the target");
                    if (cov.SnapshotsDropped > 0) parts.Add($"{cov.SnapshotsDropped} snapshots dropped by the target");
                    if (cov.CaptureFailures > 0) parts.Add($"{cov.CaptureFailures} capture failures");
                    if (cov.SkippedUnsafe > 0) parts.Add($"{cov.SkippedUnsafe} boundaries skipped (UI not safe to observe)");
                }
                if (c.Recovered) parts.Add("boundaries recovered from the crash journal (snapshots not persisted)");
                diagnostics.Add("Capture coverage is incomplete" + (parts.Count > 0 ? " (" + string.Join("; ", parts) + ")" : "")
                    + ": candidates are flagged and never rated high confidence." + (c.Diagnostic is { } d ? " " + d : ""));
            }
        }

        // ── classification ─────────────────────────────────────────────────────────────────────────────────────────────

        private Decision[] Classify()
        {
            int n = states.Count;
            var result = new Decision[n];
            var (periodic, leaderNeverQuiescent, stretches) = Periodicity();
            foreach (var (after, changes) in stretches)
                diagnostics.Add($"The screen kept changing without input after input #{after} ({changes} application-caused changes "
                    + $"within {options.PeriodicWindowMilliseconds} ms): treated as never-quiescent, no async candidates from that stretch.");

            State? beforeMenu = null, beforeModal = null, runReference = null;
            (ulong, string?, int, int, int)? lastCandidateKey = null;
            string? carryKind = null;

            for (int i = 0; i < n; i++)
            {
                var s = states[i];
                var p = i > 0 ? states[i - 1] : null;
                Decision d;
                bool weak = false;
                string? carried = carryKind;
                carryKind = null;
                if (p is null)
                    d = new(Bucket.Insignificant, "initial (baseline) state of the recording");
                else
                {
                    var inputs = InputsBetween(p, s);
                    bool application = string.Equals(s.First.Cause, Protocol.BoundaryCauses.Application, StringComparison.Ordinal);
                    bool resized = (s.First.Width, s.First.Height) != (p.First.Width, p.First.Height)
                        || inputs.Any(e => Classify(e) == InputClass.Resize && (e.Width, e.Height) != (p.First.Width, p.First.Height));
                    bool gap = s.All.Any(b => b.AfterGap) || inputs.Any(e => e.AfterGap);
                    var endInput = NextInput(s);

                    if (!application && inputs.Count == 0 && gap)
                        d = new(Bucket.Insignificant, "reached by input made while recording was paused (not recorded, not replayable)");
                    else if (resized)
                        d = endInput is { } er && Classify(er) == InputClass.Resize
                            ? new(Bucket.Insignificant, "intermediate grid size during a resize")
                            : new(Bucket.Candidate, $"grid resized to {s.First.Width}x{s.First.Height}", CandidateKinds.Resize);
                    else if (!s.SameSignature(p))
                        d = Transition(p, s, beforeMenu, beforeModal, inputs);
                    else if (lastCandidateKey == s.Key)
                        d = new(Bucket.Unchanged, "same screen and signature as the previous candidate");
                    else if (application)
                    {
                        if (periodic[i]) d = new(Bucket.Periodic, "periodic application redraw (never quiescent)");
                        else d = new(Bucket.Candidate, carried is not null
                                ? $"{Describe(carried)} settled after the application finished updating it"
                                : "application-caused change (timer, posted event or asynchronous work) without input",
                            carried ?? CandidateKinds.Async);
                    }
                    else if (inputs.Count == 0)
                        d = new(Bucket.Insignificant, "changed after unrecorded input only (hover, key-up or modifier)");
                    else if (InsidePress(inputs, endInput))
                        d = new(Bucket.Insignificant, "pressed look inside one mouse press..release (no menu/modal change at the press)");
                    else if (OwnSnapshot(s) is { } retained)
                        d = new(Bucket.Candidate, $"Retained this {(s.Menu ? "menu" : "dialog")} screen ({retained.Reason}) "
                            + $"after {DescribeInputs(inputs)}", s.Menu ? CandidateKinds.MenuChange : CandidateKinds.Content);
                    else if (s.Menu)
                        d = new(Bucket.Insignificant, "menu highlight moved inside an open menu");
                    else if (inputs.Any(e => Classify(e) == InputClass.Command))
                        d = new(Bucket.Candidate, $"screen changed after {DescribeInputs(inputs)}", CandidateKinds.Content);
                    else
                    {
                        weak = true;
                        runReference ??= p;
                        bool text = inputs.Any(e => Classify(e) == InputClass.Text);
                        bool lastOfRun = endInput is null || !IsWeak(endInput);
                        var diff = lastOfRun ? Compare(runReference, s) : default;
                        d = diff.Result == Diff.Significant
                            ? new(Bucket.Candidate, $"after a {(text ? "text" : "navigation")} run: {diff.Why}", CandidateKinds.Content,
                                EvidenceBacked: true)
                            : new(text ? Bucket.Typing : Bucket.Insignificant,
                                text ? "text input (char-by-char typing is not a checkpoint)" : "navigation/focus input only",
                                Promotable: true);
                    }

                    // Evidence veto/backing for same-signature changes (transitions and resizes are always meaningful).
                    if (d is { Bucket: Bucket.Candidate, EvidenceBacked: false } && s.SameSignature(p) && !resized && carried is null)
                    {
                        var diff = Compare(p, s);
                        if (diff.Result == Diff.Noise) d = new(Bucket.Insignificant, "evidence shows noise: " + diff.Why);
                        else if (diff.Result == Diff.Significant) d = d with { Reason = d.Reason + "; evidence: " + diff.Why, EvidenceBacked = true };
                        else d = d with { Reason = d.Reason + " (no comparable snapshot/inspection evidence: inferred from signature and input kind)" };
                    }

                    if (d.Bucket == Bucket.Candidate) d = Stabilize(i, d, application, leaderNeverQuiescent[i], ref carryKind);
                }

                if (!weak) runReference = null;
                if (d.Bucket == Bucket.Candidate) lastCandidateKey = s.Key;
                if (!s.Menu) beforeMenu = s;
                if (!s.Transient) beforeModal = s;
                result[i] = d;
            }

            // The end state at Stop is a natural checkpoint when only typing/navigation led to it (at most one candidate).
            if (input.Final && n > 1 && result[n - 1] is { Promotable: true } last && NextInput(states[n - 1]) is null
                && lastCandidateKey != states[n - 1].Key && states[0].Key != states[n - 1].Key)
                result[n - 1] = new(Bucket.Candidate, "final state at Stop after " + last.Reason, CandidateKinds.Final,
                    Stability: Stabilities.Final);
            return result;
        }

        private Decision Transition(State p, State s, State? beforeMenu, State? beforeModal, List<RecordedInput> inputs)
        {
            string how = inputs.Count > 0 ? " after " + DescribeInputs(inputs)
                : string.Equals(s.First.Cause, Protocol.BoundaryCauses.Application, StringComparison.Ordinal) ? " by the application" : "";
            if (s.Menu)
                return p.Menu
                    ? new(Bucket.Candidate, $"menu changed (depth {p.First.MenuDepth} -> {s.First.MenuDepth}){how}", CandidateKinds.MenuChange)
                    : new(Bucket.Candidate, $"menu opened (depth {s.First.MenuDepth}){how}", CandidateKinds.MenuOpen);
            if (p.Menu)
            {
                if (string.Equals(s.First.ModalTop, beforeMenu?.First.ModalTop, StringComparison.Ordinal))
                    return beforeMenu is not null && beforeMenu.Key == s.Key
                        ? new(Bucket.Unchanged, "menu closed without effect (cancelled): same screen as before it opened")
                        : new(Bucket.Candidate, $"menu closed{how}", CandidateKinds.MenuClose);
                return s.First.ModalTop is not null
                    ? new(Bucket.Candidate, $"dialog {Short(s.First.ModalTop)} opened from the menu{how}", CandidateKinds.ModalOpen)
                    : new(Bucket.Candidate, $"menu and dialog closed{how}", CandidateKinds.ModalClose);
            }
            if (s.First.ModalTop is not null)
                return p.First.ModalTop is null
                    ? new(Bucket.Candidate, $"dialog {Short(s.First.ModalTop)} opened{how}", CandidateKinds.ModalOpen)
                    : new(Bucket.Candidate, $"modal changed {Short(p.First.ModalTop)} -> {Short(s.First.ModalTop)}{how}", CandidateKinds.ModalChange);
            return beforeModal is not null && beforeModal.Key == s.Key
                ? new(Bucket.Unchanged, "dialog closed without effect (cancelled): same screen as before it opened")
                : new(Bucket.Candidate, $"dialog {Short(p.First.ModalTop)} closed{how}", CandidateKinds.ModalClose);
        }

        private Decision Stabilize(int i, Decision d, bool application, bool neverQuiescent, ref string? carryKind)
        {
            var s = states[i];
            var (ending, endSequence) = EndingOf(i);
            switch (ending)
            {
                case Ending.InputEnded:
                    return d with { Stability = application ? Stabilities.Settled : Stabilities.InputEnded, EndSequence = endSequence };
                case Ending.SettledByTime:
                    return d with { Stability = Stabilities.Settled };
                case Ending.Final:
                    return d with { Stability = Stabilities.Final };
                case Ending.Pending:
                    diagnostics.Add($"State at boundary {s.First.Id} is still showing (live analysis): it becomes a candidate when "
                        + "the next input arrives, it settles, or at Stop.");
                    return new(Bucket.Unsettled, "pending: " + d.Reason);
            }

            // Superseded by the application before SettleMilliseconds.
            var next = states[i + 1];
            if (neverQuiescent)
            {
                // Never-quiescent redraws follow: anchor the state at its input instead of waiting for a settle that never comes.
                if (NextInput(s)?.Sequence is { } e)
                    return d with { Stability = Stabilities.InputEnded, EndSequence = e, NeverQuiescent = true,
                        Reason = d.Reason + "; the screen kept redrawing afterwards (never quiescent)" };
                if (input.Final)
                    return d with { Stability = Stabilities.Final, NeverQuiescent = true,
                        Reason = d.Reason + "; the screen kept redrawing afterwards (never quiescent)" };
                return new(Bucket.Unsettled, "pending: never-quiescent redraws follow; " + d.Reason, NeverQuiescent: true);
            }
            if (s.Transient && !next.SameSignature(s))
                return d with { Stability = Stabilities.Transient,
                    Reason = d.Reason + $"; closed/changed by the application after {Duration(s, next)} ms (transient)" };
            if (d.Kind is CandidateKinds.MenuOpen or CandidateKinds.MenuChange or CandidateKinds.ModalOpen or CandidateKinds.ModalChange
                    or CandidateKinds.ModalClose or CandidateKinds.MenuClose or CandidateKinds.Resize
                && next.SameSignature(s) && string.Equals(next.First.Cause, Protocol.BoundaryCauses.Application, StringComparison.Ordinal))
                carryKind = d.Kind;
            return new(Bucket.Unsettled, $"superseded by the application after {Duration(s, next)} ms "
                + $"(< {options.SettleMilliseconds} ms settle): " + d.Reason);
        }

        private static long Duration(State a, State b) => b.First.Milliseconds - a.First.Milliseconds;

        private (Ending, long?) EndingOf(int i)
        {
            var s = states[i];
            var next = i + 1 < states.Count ? states[i + 1] : null;
            var endInput = NextInput(s);
            if (endInput is not null && (next is null || endInput.Sequence <= next.First.AfterSequence)) return (Ending.InputEnded, endInput.Sequence);
            if (next is not null) return (Duration(s, next) >= options.SettleMilliseconds ? Ending.SettledByTime : Ending.Superseded, null);
            return (input.Final ? Ending.Final : Ending.Pending, null);
        }

        /// <summary>Per state: in a never-quiescent stretch (application-caused, same signature); per leader: followed by one.</summary>
        private (bool[] Periodic, bool[] Leader, List<(long After, int Changes)> Stretches) Periodicity()
        {
            int n = states.Count, k = Math.Max(2, options.PeriodicChanges);
            var periodic = new bool[n];
            var leader = new bool[n];
            var stretches = new List<(long, int)>();
            int i = 1;
            while (i < n)
            {
                if (!IsQuiet(i)) { i++; continue; }
                int a = i;
                while (i < n && IsQuiet(i)) i++;
                int b = i - 1; // quiet run a..b, leader a-1
                bool found = false;
                for (int j = a; j + k - 1 <= b && !found; j++)
                    found = states[j + k - 1].First.Milliseconds - states[j].First.Milliseconds <= options.PeriodicWindowMilliseconds;
                if (!found) continue;
                int marked = 0;
                for (int j = a; j <= b; j++)
                {
                    if (!states[j].SameSignature(states[j - 1])) continue;
                    // The last state of the stretch counts as quiescent when it stayed unchanged for a whole window before input.
                    if (j == b && NextInput(states[j]) is { } e && e.Milliseconds - states[j].First.Milliseconds >= options.PeriodicWindowMilliseconds)
                        continue;
                    periodic[j] = true; marked++;
                }
                leader[a - 1] = true;
                stretches.Add((states[a - 1].Last.AfterSequence, marked));
            }
            return (periodic, leader, stretches);
        }

        private bool IsQuiet(int i) => i > 0 && string.Equals(states[i].First.Cause, Protocol.BoundaryCauses.Application, StringComparison.Ordinal);

        // ── inputs ─────────────────────────────────────────────────────────────────────────────────────────────────────

        private List<RecordedInput> InputsBetween(State p, State s)
        {
            long from = p.Last.AfterSequence, to = s.First.AfterSequence;
            var list = new List<RecordedInput>();
            foreach (var e in events)
                if (e.Sequence > from && e.Sequence <= to) list.Add(e);
            return list;
        }

        private RecordedInput? NextInput(State s)
        {
            long after = s.First.AfterSequence;
            foreach (var e in events) if (e.Sequence > after) return e;
            return null;
        }

        /// <summary>The state sits strictly inside one press..release: its last input is a mouse press and the input that
        /// ended it is the release (callers only ask for same-signature states, so a menu/modal opened by the press stays).</summary>
        private static bool InsidePress(List<RecordedInput> inputs, RecordedInput? endInput) =>
            inputs.Count > 0 && inputs[^1] is { Kind: "mouse", What: TSharpVision.Constants.Events.evMouseDown }
            && endInput is { Kind: "mouse", What: TSharpVision.Constants.Events.evMouseUp };

        private static bool IsWeak(RecordedInput e) => Classify(e) is InputClass.Text or InputClass.Navigation or InputClass.Neutral;

        private static readonly HashSet<ushort> Navigation =
        [
            TSharpVision.Constants.Keys.kbLeft, TSharpVision.Constants.Keys.kbRight, TSharpVision.Constants.Keys.kbUp,
            TSharpVision.Constants.Keys.kbDown, TSharpVision.Constants.Keys.kbHome, TSharpVision.Constants.Keys.kbEnd,
            TSharpVision.Constants.Keys.kbPgUp, TSharpVision.Constants.Keys.kbPgDn, TSharpVision.Constants.Keys.kbBack,
            TSharpVision.Constants.Keys.kbDel, TSharpVision.Constants.Keys.kbIns, TSharpVision.Constants.Keys.kbTab,
            TSharpVision.Constants.Keys.kbShiftTab, TSharpVision.Constants.Keys.kbCtrlLeft, TSharpVision.Constants.Keys.kbCtrlRight,
            TSharpVision.Constants.Keys.kbCtrlHome, TSharpVision.Constants.Keys.kbCtrlEnd, TSharpVision.Constants.Keys.kbCtrlBack,
            TSharpVision.Constants.Keys.kbCtrlDel, TSharpVision.Constants.Keys.kbShiftDel, TSharpVision.Constants.Keys.kbShiftIns,
            TSharpVision.Constants.Keys.kbCtrlIns,
        ];

        private static InputClass Classify(RecordedInput e)
        {
            const uint CtrlAlt = TSharpVision.Constants.Keys.kbCtrlShift | TSharpVision.Constants.Keys.kbAltShift;
            switch (e.Kind)
            {
                case "resize": return InputClass.Resize;
                case "key":
                    bool ctrlAlt = (e.ControlKeyState & CtrlAlt) != 0;
                    if (!ctrlAlt && e.Text is { Length: > 0 } t && InputSteps.IsValidText(t)) return InputClass.Text;
                    if (Navigation.Contains(e.KeyCode) && (e.ControlKeyState & TSharpVision.Constants.Keys.kbAltShift) == 0) return InputClass.Navigation;
                    return InputClass.Command;
                case "mouse":
                    return e.What is TSharpVision.Constants.Events.evMouseDown or TSharpVision.Constants.Events.evMouseUp
                        or TSharpVision.Constants.Events.evMouseWheel ? InputClass.Command : InputClass.Neutral;
                default: return InputClass.Neutral;
            }
        }

        private static string DescribeInputs(List<RecordedInput> inputs)
        {
            var command = inputs.LastOrDefault(e => Classify(e) == InputClass.Command) ?? inputs[^1];
            string what = command.Kind switch
            {
                "key" => command.Text is { Length: > 0 } t && Classify(command) == InputClass.Text ? $"text '{t}'"
                    : TestKeys.NameOf(command.KeyCode) ?? "key 0x" + command.KeyCode.ToString("x4", CultureInfo.InvariantCulture),
                "mouse" => command.What == TSharpVision.Constants.Events.evMouseWheel ? $"wheel at {command.X},{command.Y}"
                    : $"mouse at {command.X},{command.Y}",
                "resize" => $"resize to {command.Width}x{command.Height}",
                _ => command.Kind,
            };
            return $"{what} (#{command.Sequence})";
        }

        private static string Describe(string kind) => kind switch
        {
            CandidateKinds.MenuOpen => "menu opened", CandidateKinds.MenuChange => "menu changed",
            CandidateKinds.ModalOpen => "dialog opened", CandidateKinds.ModalChange => "modal changed",
            CandidateKinds.ModalClose => "dialog closed", CandidateKinds.MenuClose => "menu closed", CandidateKinds.Resize => "resize",
            _ => kind,
        };

        private static string Short(string? type) => type is null ? "(application)" : type[(type.LastIndexOf('.') + 1)..];
    }
}
