using System.Globalization;
using System.Text.RegularExpressions;

namespace TSharpWorkbench.Testing;

/// <summary>A scenario as advertised by the compiled target. Steps are the default script the controller drives;
/// the controller may send any other sequence of registered inputs and uniquely named checkpoints.</summary>
/// <param name="Kind">One of <see cref="ScenarioKinds"/>; descriptive, used for listing and reports.</param>
/// <param name="Safety">What the scenario declares it touches. A declaration, not a sandbox.</param>
/// <param name="Metadata">Application-owned key/value data (bounded); never interpreted by the library.</param>
/// <param name="ExpectedResult"><c>Passed</c>, or <c>Failed</c> for a deliberate failure demonstration.
/// Scenarios that are expected to fail are excluded from <c>--all</c> selections unless requested.</param>
/// <param name="Conditions">Expected operating conditions, human-readable.</param>
public sealed record ScenarioDescriptor(string Id, string Name, string Description, string Group,
    ScenarioGrid Grid, ScenarioStep[] Steps, string Kind = ScenarioKinds.Screen, ScenarioSafety? Safety = null,
    Dictionary<string, string>? Metadata = null, string ExpectedResult = ScenarioOutcomes.Passed, string? Conditions = null)
{
    public FixtureAdapters? Adapters { get; init; }
    public ExternalScenarioBinding? External { get; init; }
    public string Backend { get; init; } = "diagnostic-logical";
    /// <summary>Present only on an explicit fixture-only registration. Never written when null, so legacy
    /// descriptors serialize (wire, artifacts and fingerprints) byte-identically.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FixtureContract? Contract { get; init; }
    /// <summary>A raw fixture-only registration: bindable by external scenarios, never a direct execution target.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFixtureOnly => Contract is not null;
}

/// <summary>Only explicitly registered target callbacks are advertised. No reflection or executable data.</summary>
public sealed record FixtureAdapters(string[] Actions, string[] Points, bool Recapture = true);

/// <summary>Advertised contract of a fixture-only registration (<see cref="TestScenario.FixtureOnly"/>): the reviewed revision
/// and the names of its executable checkpoint expectations. Actions and points stay in <see cref="FixtureAdapters"/>.
/// Every checkpoint of an external scenario bound to it must name one of <see cref="Checkpoints"/>.</summary>
/// <param name="Role">Always <see cref="FixtureOnlyRole"/>; any other value is an invalid catalog.</param>
/// <param name="ContractVersion">Version of this contract and of its fingerprint encoding (<see cref="CurrentVersion"/>).</param>
/// <param name="Revision">Application-owned reviewed contract revision, nonempty (for example <c>cmd-sc-4b.1</c>).</param>
/// <param name="Checkpoints">Advertised checkpoint expectation names, ordinal order.</param>
public sealed record FixtureContract(string Role, int ContractVersion, string Revision, string[] Checkpoints)
{
    public const string FixtureOnlyRole = "fixture-only";
    public const int CurrentVersion = 1;
}
public sealed record ExternalScenarioBinding(string FixtureId, string Source, string ContentHash, string FixtureHash);
public sealed record CheckpointRetry(int Attempts = 5, int IntervalMilliseconds = 250);
public sealed record CheckpointAttempt(int Attempt, long ElapsedMilliseconds, string CellsHash, AssertionResult[] Assertions);

public static class ScenarioKinds
{
    public const string Dialog = "dialog", Window = "window", Menu = "menu", Status = "status", Screen = "screen", Mixed = "mixed";
    public static readonly string[] All = [Dialog, Window, Menu, Status, Screen, Mixed];
}

/// <summary>Grid sizes a scenario supports: an inclusive range plus the grids used when the caller asks for defaults.</summary>
public sealed record ScenarioGrid(int MinWidth, int MinHeight, int MaxWidth, int MaxHeight, string[] Defaults)
{
    public static ScenarioGrid Standard => new(GridLimits.MinWidth, GridLimits.MinHeight, GridLimits.MaxWidth, GridLimits.MaxHeight,
        ScenarioSelection.Sizes.ToArray());

    public bool Supports(int width, int height) => width >= MinWidth && width <= MaxWidth && height >= MinHeight && height <= MaxHeight;
    public bool Supports(string size) => ScenarioSelection.TryParseSize(size, out int w, out int h) && Supports(w, h);
    public string Describe() => $"{MinWidth}x{MinHeight}..{MaxWidth}x{MaxHeight}";
}

/// <summary>Absolute bounds of a logical grid. They follow the TVS1 snapshot format (1..200 x 1..100) and keep every
/// allocation (cells, PNG raster) small; the minimum keeps a menu bar, desktop and status line meaningful.</summary>
public static class GridLimits
{
    public const int MinWidth = 20, MinHeight = 6, MaxWidth = 200, MaxHeight = 100;
}

/// <summary>What a scenario declares about its external effects. The controller refuses scenarios whose
/// declaration needs a capability the run did not allow. It is NOT a sandbox: real protection comes from the
/// application's fixtures, fakes and refusal seams. Arbitrary .NET code in a target can still do anything.</summary>
public sealed record ScenarioSafety(bool FixtureFiles = false, bool Network = false, bool ExternalProcesses = false,
    bool Devices = false, bool Elevation = false, string? Notes = null)
{
    public static ScenarioSafety None => new();

    /// <summary>Capability names a run must allow (<c>--allow</c>) before this scenario executes.</summary>
    public string[] RequiredCapabilities() =>
        new[] { Network ? "network" : null, ExternalProcesses ? "processes" : null, Devices ? "devices" : null, Elevation ? "elevation" : null }
            .OfType<string>().ToArray();
}

/// <summary>Kind is <c>key</c>, <c>action</c>, <c>resize</c>, <c>checkpoint</c> or a simulated-device kind
/// (<c>key-down</c>, <c>key-up</c>, <c>text</c>, <c>modifier</c>, <c>mouse</c>, <c>release</c>; see <see cref="InputSteps"/>).
/// Note is documentation only. Checkpoints may request <see cref="Evidence"/> and carry declarative <see cref="Assertions"/>.</summary>
public sealed record ScenarioStep(string Kind, string Name, string? Note = null, string[]? Evidence = null, AssertionSpec[]? Assertions = null)
{
    public CheckpointRetry? Retry { get; init; }
    public const string Key = Protocol.InputKinds.Key, Action = Protocol.InputKinds.Action, Resize = Protocol.InputKinds.Resize, Checkpoint = "checkpoint";
    public const string KeyDown = InputSteps.KeyDown, KeyUp = InputSteps.KeyUp, Text = InputSteps.Text, Modifier = InputSteps.Modifier,
        Mouse = InputSteps.Mouse, Release = InputSteps.Release;
}

public static class ScenarioOutcomes
{
    public const string Passed = "Passed", Failed = "Failed", Error = "Error", TimedOut = "TimedOut", Blocked = "Blocked", Aborted = "Aborted";
    /// <summary>Not executed because a verified hard platform/driver prerequisite is not met (the driver is
    /// not available on this operating system, or the scenario is not an audited native workflow for the chosen native
    /// driver). Counted in Results and never a failure; it is not Blocked (a run-policy or environment failure) and not "not
    /// selected" (no result at all). A missing baseline is never a skip: the scenario runs and reports BaselineMissing.</summary>
    public const string Skipped = "Skipped";
    /// <summary>The reason prefix of every <see cref="Skipped"/> result.</summary>
    public const string SkippedPrefix = "Skipped - platform/driver unsupported: ";
}

public static class ScenarioSelection
{
    /// <summary>The standard grids: used when no size is specified.</summary>
    public static readonly string[] Sizes = ["80x25", "140x40"];

    private static readonly Regex SizePattern = new("^([0-9]{1,3})x([0-9]{1,3})$", RegexOptions.CultureInvariant);

    /// <summary>Selects by IDs, group (with sub-groups) or all. <paramref name="all"/> skips scenarios whose
    /// <see cref="ScenarioDescriptor.ExpectedResult"/> is not <c>Passed</c> unless <paramref name="includeExpectedFailures"/>.</summary>
    public static ScenarioDescriptor[] Resolve(IEnumerable<ScenarioDescriptor> catalog, IEnumerable<string> ids,
        string? group, bool all, bool includeExpectedFailures = false)
    {
        var entries = catalog.ToArray();
        var requested = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Any(id => !entries.Any(s => s.Id == id))) throw new ArgumentException("Unknown scenario ID: " + requested.First(id => !entries.Any(s => s.Id == id)));
        if (entries.FirstOrDefault(s => s.IsFixtureOnly && requested.Contains(s.Id)) is { } fixture) throw new ArgumentException(NotRunnable(fixture.Id));
        if (group is not null && !entries.Any(s => InGroup(s.Group, group))) throw new ArgumentException("Unknown group: " + group);
        bool Bulk(ScenarioDescriptor s) => !s.IsFixtureOnly && (includeExpectedFailures || s.ExpectedResult == ScenarioOutcomes.Passed);
        var selected = entries.Where(s => requested.Contains(s.Id) || ((all || (group is not null && InGroup(s.Group, group))) && Bulk(s))).ToArray();
        if (selected.Length == 0) throw new ArgumentException("No scenarios selected.");
        return selected;
    }

    /// <summary>Runnable discovery from a raw catalog: every entry except bare fixture-only registrations (compiled scenarios,
    /// external scenarios, including those bound to fixture-only registrations, and expected-failure self-tests remain).</summary>
    public static ScenarioDescriptor[] Runnable(IEnumerable<ScenarioDescriptor> raw) => raw.Where(s => !s.IsFixtureOnly).ToArray();

    internal static string NotRunnable(string id) =>
        $"'{id}' is a fixture-only registration, not a runnable scenario; run an external scenario bound to it (\"fixture\": \"{id}\").";

    public static bool InGroup(string value, string group) => value == group || value.StartsWith(group + "/", StringComparison.Ordinal);

    /// <summary>Parses <c>standard</c>/<c>both</c> or a comma-separated list of <c>WxH</c> grids within <see cref="GridLimits"/>.</summary>
    public static string[] ParseSizes(string value)
    {
        string[] sizes = value is "both" or "standard" ? Sizes.ToArray() : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        if (sizes.Length == 0) throw new ArgumentException("No grid size given. Use WxH[,WxH...] or 'standard'.");
        if (sizes.Length > 16) throw new ArgumentException("At most 16 grid sizes per run.");
        foreach (string size in sizes) ParseSize(size);
        return sizes.Select(s => { var (w, h) = ParseSize(s); return Format(w, h); }).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool TryParseSize(string? size, out int width, out int height)
    {
        width = height = 0;
        var match = size is null ? null : SizePattern.Match(size);
        if (match is null || !match.Success) return false;
        width = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        height = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return width is >= GridLimits.MinWidth and <= GridLimits.MaxWidth && height is >= GridLimits.MinHeight and <= GridLimits.MaxHeight;
    }

    public static (int Width, int Height) ParseSize(string size) => TryParseSize(size, out int w, out int h) ? (w, h)
        : throw new ArgumentException($"Unsupported grid '{size}'. Use WxH with width {GridLimits.MinWidth}..{GridLimits.MaxWidth} and height {GridLimits.MinHeight}..{GridLimits.MaxHeight}.");

    public static string Format(int width, int height) => width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Flat run-directory names: <c>scenario--checkpoint--WxH.ext</c>, where WxH is the grid of that checkpoint.
/// After an in-scenario resize the grid component is <c>WxH-from-IxJ</c> (I x J = the execution's initial grid), so
/// executions started at different grids never collide. Components never contain <c>--</c>.</summary>
public static class ArtifactNames
{
    public static string Slug(string scenarioId)
    {
        string slug = Regex.Replace(scenarioId.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "scenario" : slug[..Math.Min(80, slug.Length)].TrimEnd('-');
    }

    public static string Stem(string scenarioId, string checkpoint, string size, string? initialSize = null) =>
        Slug(scenarioId) + "--" + checkpoint + "--" + (initialSize is null || initialSize == size ? size : size + "-from-" + initialSize);

    /// <summary>Throws when two selected scenario IDs would share artifact names.</summary>
    public static void EnsureDistinct(IEnumerable<ScenarioDescriptor> selected)
    {
        var clash = selected.GroupBy(s => Slug(s.Id), StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null) throw new ArgumentException("Artifact name collision between scenario IDs: " + string.Join(", ", clash.Select(s => s.Id)));
    }
}

/// <param name="PixelsHash">SHA-256 of the uncompressed PNG raster: platform-independent, unlike <paramref name="PngHash"/>.</param>
/// <param name="Visual">Comparison with the approved baseline when the run has a baseline manifest and the checkpoint
/// is gated by it; null otherwise (legacy runs are unchanged).</param>
public sealed record CheckpointResult(string Checkpoint, string Size, string Png, string Cells, string Metadata, string CellsHash, string PngHash,
    string PixelsHash, AssertionResult[] Assertions, string[] InspectionErrors)
{
    public VisualCheckpointResult? Visual { get; init; }
}

public sealed record ScenarioResult(string ScenarioId, string Size, string Outcome, string Reason,
    CheckpointResult[] Checkpoints, bool TargetExited, bool FixtureIntegrityVerified, bool CleanupSucceeded,
    string ExpectedResult = ScenarioOutcomes.Passed)
{
    public IEnumerable<AssertionResult> FailedAssertions => Checkpoints.SelectMany(c => c.Assertions).Where(a => a.Outcome != AssertionOutcomes.Pass && a.Severity == AssertionSeverity.Error);
}

/// <summary>Exit code: 0 all passed; 1 a scenario did not pass; 3 (only with a baseline manifest) every scenario passed but
/// the visual gate did not (mismatch, missing or invalid baseline, normalization error, checkpoint not captured).
/// Skipped executions neither pass nor fail; a run with at least one pass and otherwise only skips exits
/// 0, a run where everything was skipped exits 1 (nothing was verified).</summary>
public sealed record BatchResult(string RunRoot, ScenarioResult[] Results)
{
    internal string Driver { get; init; } = "NullDriver";
    public const int VisualGateFailed = 3;
    /// <summary>Visual gate of the run; null when the run had no baseline manifest.</summary>
    public VisualRunSummary? Visual { get; init; }
    public int ExitCode => !(Results.Any(r => r.Outcome == ScenarioOutcomes.Passed) && Results.All(r => r.Outcome is ScenarioOutcomes.Passed or ScenarioOutcomes.Skipped)) ? 1
        : Visual is { Passed: false } ? VisualGateFailed : 0;
    public string? Report => RunRoot.Length == 0 ? null : Path.Combine(RunRoot, "report.md");
}
