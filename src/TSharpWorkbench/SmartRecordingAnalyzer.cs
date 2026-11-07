using TSharpWorkbench.Testing;

// Integration (coordinator): one pure pass over an immutable copy of a recording - detector, pick resolver,
// planner - plus a short deterministic diagnostic summary. Nothing is inserted, persisted or shown as an assertion.

internal static class SmartRecordingAnalyzer
{
    /// <summary>Analyzes a recording. <paramref name="final"/>: the recording has ended (Stop or target exit).</summary>
    internal static SmartRecordingAnalysis Analyze(IReadOnlyList<RecordedInput> events, RecordingCaptureTimeline capture,
        RecordingConversion conversion, int width, int height, bool final, IReadOnlyCollection<string> existingCheckpointIds)
    {
        var boundaries = capture.Boundaries;
        var snapshots = capture.Snapshots;
        var state = new CaptureState(capture.BoundariesEnabled, capture.SnapshotsEnabled, capture.CoverageComplete, capture.BoundariesLost,
            capture.SnapshotsLost, capture.Recovered, capture.Coverage, capture.Diagnostic);
        return Analyze(new DetectorInput(events, boundaries, snapshots, capture.Inspections, state, final), conversion, width, height,
            existingCheckpointIds);
    }

    /// <summary>Pure core (tests): the same pipeline over explicit input.</summary>
    internal static SmartRecordingAnalysis Analyze(DetectorInput input, RecordingConversion conversion, int width, int height,
        IReadOnlyCollection<string> existingCheckpointIds, DetectorOptions? detector = null, PlannerOptions? planner = null)
    {
        var detection = CheckpointDetector.Detect(input, detector);
        var picks = input.Capture.BoundariesEnabled ? RecordingPickResolver.ResolveAll(input.Boundaries, input.Snapshots) : [];
        var plan = CheckpointPlanner.Plan(new PlannerInput(input.Events, conversion, width, height, detection.Candidates, picks,
            existingCheckpointIds), planner);
        return new(detection, picks, plan, Summary(input, detection, picks, plan)) { Input = input };
    }

    private static string[] Summary(DetectorInput input, DetectionResult detection, IReadOnlyList<PickSelection> picks, CheckpointPlan plan)
    {
        var lines = new List<string>();
        if (!input.Capture.BoundariesEnabled)
        {
            lines.Add("Checkpoint proposals are unavailable: " + (input.Capture.Diagnostic ?? "the target reported no UI boundaries."));
            return [.. lines];
        }
        int review = plan.Proposals.Count(p => p.NeedsReview);
        lines.Add($"{plan.Proposals.Count} checkpoint proposal{(plan.Proposals.Count == 1 ? "" : "s")} from {detection.Candidates.Count} "
            + $"stable change{(detection.Candidates.Count == 1 ? "" : "s")} and {picks.Count} pick{(picks.Count == 1 ? "" : "s")}"
            + (review > 0 ? $"; {review} need{(review == 1 ? "s" : "")} manual review" : "") + " (not inserted).");
        if (!input.Capture.CoverageComplete)
            lines.Add("Capture coverage is incomplete (" + Losses(input.Capture) + "); proposals from it are marked degraded.");
        if (plan.StepsSplit) lines.Add("Some proposals sit inside a coalesced step; that step is split in the proposal plan only.");
        lines.AddRange(detection.Diagnostics.Take(3));
        lines.AddRange(plan.Diagnostics.Take(3));
        return [.. lines];
    }

    private static string Losses(CaptureState c)
    {
        var parts = new List<string>();
        if (c.BoundariesLost > 0) parts.Add($"{c.BoundariesLost} boundaries lost");
        if (c.SnapshotsLost > 0) parts.Add($"{c.SnapshotsLost} snapshots lost");
        if (c.Recovered) parts.Add("recovered from the crash journal");
        if (c.Coverage is { } k)
        {
            if (k.BoundariesDropped > 0) parts.Add($"{k.BoundariesDropped} boundaries dropped by the target");
            if (k.SnapshotsDropped > 0) parts.Add($"{k.SnapshotsDropped} snapshots dropped by the target");
            if (k.SkippedUnsafe > 0) parts.Add($"{k.SkippedUnsafe} unsafe boundaries skipped");
            if (k.CaptureFailures > 0) parts.Add($"{k.CaptureFailures} capture failures");
        }
        return parts.Count == 0 ? "not all capture features or snapshots were available" : string.Join(", ", parts);
    }
}
