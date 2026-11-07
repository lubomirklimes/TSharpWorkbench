using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSharpWorkbench.Testing;

/// <summary>One visually gated checkpoint of a manifest. <see cref="Grids"/> are artifact grid keys: <c>WxH</c>, or
/// <c>WxH-from-IxJ</c> for a checkpoint after an in-scenario resize (checkpoint grid W x H, execution started at I x J).</summary>
/// <param name="Scenario">Baseline identity: the scenario ID the approved files are keyed and recorded under.</param>
/// <param name="ExecutionScenario">explicit identity mapping, null = the entry executes as <paramref name="Scenario"/>.
/// When set, the entry is evaluated for the run of this external scenario only, and only while that scenario's validated
/// fixture binding names <paramref name="Scenario"/> as its fixture (<see cref="VisualBaselineSet.BindingProblem"/>).</param>
public sealed record VisualBaselineEntry(string Scenario, string Checkpoint, string[] Grids, string Purpose,
    string[]? Normalizations = null, string? Fixture = null, string? ExecutionScenario = null)
{
    /// <summary>The scenario ID whose execution this entry gates.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Execution => ExecutionScenario ?? Scenario;
}

/// <summary>The application-owned baseline manifest (<c>visual-baselines.json</c>): which checkpoints are visually gated and
/// which explicit normalization rules each uses. Approved baselines live in <c>approved/</c> next to it.</summary>
public sealed record VisualBaselineManifest(string Format, VisualNormalizationRule[] Normalizations, VisualBaselineEntry[] Baselines,
    string? Description = null);

public sealed record BaselineNormalization(string Rule, string Kind, string Description, int[][] Spans);

/// <summary>Metadata of a candidate (<c>&lt;key&gt;.candidate.json</c> in a run) or an approved baseline
/// (<c>approved/&lt;key&gt;.baseline.json</c>). The cells are the canonical (normalized) TVS1 file beside it; its SHA-256
/// is <see cref="CellsSha256"/>. No absolute path, run directory, fixture identifier or timestamp is recorded.</summary>
/// <param name="ReplacesSha256">For a candidate: SHA-256 of the approved TVS1 it was compared against (null = there was none).
/// Approval refuses when the approved file is no longer exactly that one.</param>
/// <param name="ExecutionScenarioId">The executing external scenario of a mapped entry (never written when null, so
/// unmapped candidates and every existing approved file keep their exact format). <paramref name="ScenarioId"/> stays the
/// baseline identity.</param>
/// <param name="Driver">The TSharpVision driver the logical cells were produced under (the Proxy backend of
/// an ordinary application). Null means NullDriver, which every file written before this field had; it is never written for
/// NullDriver, so headless candidates and approved files keep their exact format. With <paramref name="Platform"/> and the
/// capture layer it is the baseline's <see cref="BaselineEnvironment"/>.</param>
public sealed record VisualBaselineFile(string Format, string Status, string Key, string ScenarioId, string CheckpointId, string Grid,
    string InitialGrid, int Width, int Height, string CellsFormat, string CellsSha256, string PixelsSha256, string Renderer,
    string CaptureLayer, string Platform, BaselineNormalization[] Normalizations, string NormalizationFingerprint, string Purpose,
    string? Fixture, string? ReplacesSha256, string? Target, string Review, string? ExecutionScenarioId = null, string? Driver = null);

/// <summary>
/// The environment an approved logical baseline belongs to - operating system, TSharpVision driver (the
/// headless NullDriver, or the Proxy backend an ordinary application ran on) and capture type. The grid is part of the
/// baseline key. Baselines are compared only within one environment: logical cells are not proven identical across operating
/// systems (paths, fonts, file dialogs, environment text) or drivers, so another environment's baseline is never used, never
/// copied and never a fallback. A scenario without a baseline for the current environment is BaselineMissing ("no matching
/// baseline"), with the environments that do have one and the dimensions that differ in the reason.
/// </summary>
public sealed record BaselineEnvironment(string Platform, string Driver, string Capture = BaselineEnvironment.LogicalCapture)
{
    public const string LogicalCapture = "logical", DefaultDriver = "NullDriver";
    /// <summary>This machine's operating system with <paramref name="driver"/> (NullDriver for headless diagnostic runs).</summary>
    public static BaselineEnvironment Current(string driver = DefaultDriver) => new(VisualGate.Platform(), driver);
    /// <summary>The environment a candidate or approved file records (legacy files: their platform, NullDriver, logical).</summary>
    public static BaselineEnvironment Of(VisualBaselineFile file) => new(file.Platform, file.Driver ?? DefaultDriver,
        file.CaptureLayer.StartsWith(LogicalCapture, StringComparison.Ordinal) ? LogicalCapture : file.CaptureLayer);
    /// <summary>The <c>approved/&lt;folder&gt;/</c> name of this environment, e.g. <c>linux-sdldriver-logical</c>.</summary>
    public string Folder => Slug(Platform) + "-" + Slug(Driver) + "-" + Slug(Capture);
    /// <summary>The dimensions in which <paramref name="other"/> differs from this environment.</summary>
    public string[] Differences(BaselineEnvironment other) =>
    [
        .. Platform == other.Platform ? Array.Empty<string>() : ["platform " + other.Platform],
        .. Driver == other.Driver ? Array.Empty<string>() : ["driver " + other.Driver],
        .. Capture == other.Capture ? Array.Empty<string>() : ["capture " + other.Capture],
    ];
    public override string ToString() => $"{Platform} / {Driver} / {Capture}";
    /// <summary>The environment a comparison (<c>*.diff.json</c>) was made in; runs before the redesign did not record it
    /// and were headless logical runs, so they read as this machine with NullDriver.</summary>
    public static BaselineEnvironment FromComparison(JsonElement diff) =>
        diff.ValueKind == JsonValueKind.Object && diff.TryGetProperty("environment", out var e) && e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty("platform", out var p) && p.GetString() is { Length: > 0 } platform
        && e.TryGetProperty("driver", out var d) && d.GetString() is { Length: > 0 } driver
            ? new(platform, driver, e.TryGetProperty("capture", out var c) && c.GetString() is { Length: > 0 } capture ? capture : LogicalCapture)
            : Current();
    private static string Slug(string value)
    {
        string slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "unknown" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}

/// <summary>Where the approved baseline of one key lives for one environment. The flat
/// <c>approved/&lt;key&gt;.*</c> slot is the pre-existing layout and holds exactly one environment, the one its metadata records
/// (files written before environments were recorded: their platform, NullDriver, logical); every other environment has its
/// own <c>approved/&lt;environment folder&gt;/&lt;key&gt;.*</c> slot. <paramref name="Others"/> are the environments that have an
/// approved baseline for this key but are not this one (never compared, reported only).</summary>
public sealed record BaselineSlot(string Cells, string Metadata, string Png, string Relative, bool Exists, BaselineEnvironment Environment, BaselineEnvironment[] Others);

public static class VisualStatuses
{
    /// <summary>Canonical capture equals the approved baseline cell for cell.</summary>
    public const string Match = "Match";
    public const string Mismatch = "Mismatch";
    /// <summary>No approved baseline: approval required. Never a pass.</summary>
    public const string BaselineMissing = "BaselineMissing";
    /// <summary>Approved files unreadable, corrupt (hash), wrong identity, unsupported format, or approved under other rules.</summary>
    public const string BaselineInvalid = "BaselineInvalid";
    /// <summary>A rule did not apply exactly as declared (region, match count, validation, size bound).</summary>
    public const string NormalizationError = "NormalizationError";
    /// <summary>The execution ran at the entry's initial grid but never captured the checkpoint.</summary>
    public const string NotCaptured = "NotCaptured";
    /// <summary>The entry's scenario or initial grid was not part of this run (informational).</summary>
    public const string NotRun = "NotRun";
    public static readonly string[] Failing = [Mismatch, BaselineMissing, BaselineInvalid, NormalizationError, NotCaptured];
}

/// <summary>Visual result of one captured checkpoint. Artifact names are relative to the run directory; <see cref="Approved"/>
/// is relative to the manifest directory.</summary>
public sealed record VisualCheckpointResult(string Status, string Baseline, string Reason, int DifferingCells, int GlyphDifferences,
    int AttributeDifferences, string[] Normalizations, int MaskedCells, string Approved, string? Diff, string? DiffPng,
    string? Candidate, string? CandidatePng, string? CandidateMetadata);

/// <param name="ScenarioId">The executing scenario (run identity, never rewritten).</param>
/// <param name="BaselineScenarioId">The mapped baseline identity when it differs from <paramref name="ScenarioId"/>.</param>
public sealed record VisualEntryResult(string ScenarioId, string Checkpoint, string Grid, string Status, string Reason,
    string? Diff = null, string? DiffPng = null, string? Candidate = null, string? BaselineScenarioId = null)
{
    /// <summary>The approved-baseline key of this entry (baseline identity, not the execution identity).</summary>
    public string BaselineKey() => VisualBaselineSet.Key(BaselineScenarioId ?? ScenarioId, Checkpoint, Grid);
}

public sealed record VisualRunSummary(string Manifest, VisualEntryResult[] Entries)
{
    public int Count(string status) => Entries.Count(e => e.Status == status);
    /// <summary>Every evaluated entry matched, and at least one did.</summary>
    public bool Passed => Entries.Any(e => e.Status == VisualStatuses.Match) && !Entries.Any(e => VisualStatuses.Failing.Contains(e.Status));
}

/// <summary>Invalid manifest: refused before any scenario runs.</summary>
public sealed class VisualBaselineConfigurationException(string message) : ArgumentException(message);

/// <summary>A loaded, validated baseline manifest and its approved store. Read-only except for <see cref="Approve"/>, which
/// only the explicit <c>baseline approve</c> command calls; the runner never writes here.</summary>
public sealed class VisualBaselineSet
{
    public const string ManifestFormat = "tsharpvision.visual-baselines/1", BaselineFormat = "tsharpvision.visual-baseline/1",
        ComparisonFormat = "tsharpvision.visual-comparison/1", CellsFormat = "TVS1", ApprovedFolder = "approved";
    private static readonly Regex GridKey = new("^([0-9]{1,3}x[0-9]{1,3})(?:-from-([0-9]{1,3}x[0-9]{1,3}))?$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyPattern = new("^[a-z0-9]+(-[a-z0-9]+)*--[a-z0-9]+(-[a-z0-9]+)*--[0-9]{1,3}x[0-9]{1,3}(-from-[0-9]{1,3}x[0-9]{1,3})?$", RegexOptions.CultureInvariant);
    internal static readonly JsonSerializerOptions Strict = new(ScenarioRunner.Json)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public string ManifestPath { get; }
    public string Root { get; }
    public string ApprovedDirectory { get; }
    public VisualBaselineManifest Manifest { get; }

    private VisualBaselineSet(string path, VisualBaselineManifest manifest)
    {
        ManifestPath = path; Root = Path.GetDirectoryName(path)!; ApprovedDirectory = Path.Combine(Root, ApprovedFolder); Manifest = manifest;
    }

    public static VisualBaselineSet Load(string manifestPath)
    {
        string path = Path.GetFullPath(manifestPath);
        if (!File.Exists(path)) throw new VisualBaselineConfigurationException("Baseline manifest not found: " + manifestPath);
        VisualBaselineManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<VisualBaselineManifest>(File.ReadAllBytes(path), Strict); }
        catch (JsonException ex) { throw new VisualBaselineConfigurationException("Baseline manifest is not valid: " + ex.Message); }
        var errors = Validate(manifest).ToArray();
        if (errors.Length > 0) throw new VisualBaselineConfigurationException("Baseline manifest is not valid:\n  " + string.Join("\n  ", errors));
        return new(path, manifest!);
    }

    internal static IEnumerable<string> Validate(VisualBaselineManifest? manifest)
    {
        if (manifest is null) { yield return "empty manifest"; yield break; }
        if (manifest.Format != ManifestFormat) { yield return $"unsupported manifest format '{manifest.Format}' (expected {ManifestFormat})"; yield break; }
        var rules = manifest.Normalizations ?? [];
        foreach (string error in rules.SelectMany(VisualNormalizer.Validate)) yield return error;
        foreach (var dup in rules.Where(r => r is not null).GroupBy(r => r.Id).Where(g => g.Count() > 1)) yield return $"duplicate rule id '{dup.Key}'";
        if (manifest.Baselines is not { Length: > 0 and <= 512 }) { yield return "1..512 baseline entries are required"; yield break; }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in manifest.Baselines)
        {
            if (e is null) { yield return "null baseline entry"; continue; }
            string where = $"entry {e.Scenario} / {e.Checkpoint}";
            if (string.IsNullOrWhiteSpace(e.Scenario) || e.Scenario.Length > 128) yield return where + ": scenario id required";
            if (!Protocol.IsValidName(e.Checkpoint)) yield return where + ": invalid checkpoint name";
            if (string.IsNullOrWhiteSpace(e.Purpose)) yield return where + ": purpose required";
            if (e.Grids is not { Length: > 0 and <= 16 }) { yield return where + ": 1..16 grids required"; continue; }
            foreach (string g in e.Grids)
            {
                if (!TryParseGridKey(g, out _, out _)) yield return $"{where}: invalid grid key '{g}' (WxH or WxH-from-IxJ)";
                else if (!keys.Add(Key(e.Scenario ?? "", e.Checkpoint ?? "", g))) yield return $"{where}: duplicate grid '{g}'";
            }
            foreach (string r in e.Normalizations ?? [])
                if (!rules.Any(x => x?.Id == r)) yield return $"{where}: unknown normalization rule '{r}'";
            if (e.ExecutionScenario is { } execution)
            {
                if (string.IsNullOrWhiteSpace(execution) || execution.Length > 128 || execution.Any(char.IsControl) || execution.Trim() != execution)
                    yield return where + ": executionScenario must be a scenario ID";
                else if (execution == e.Scenario) yield return where + ": executionScenario equals scenario; omit it (no mapping)";
            }
        }
        // Identity mapping: baseline identity <-> execution identity must be one-to-one across the whole manifest, so no
        // execution can reach two baseline identities and no baseline identity can be reached from two executions.
        var valid = manifest.Baselines.Where(e => e is { Scenario: not null }).ToArray();
        foreach (var g in valid.GroupBy(e => e.Scenario, StringComparer.Ordinal))
            if (g.Select(e => e.ExecutionScenario).Distinct(StringComparer.Ordinal).Count() > 1)
                yield return $"baseline identity '{g.Key}': conflicting executionScenario mappings ({string.Join(", ", g.Select(e => e.ExecutionScenario ?? "unmapped").Distinct(StringComparer.Ordinal))})";
        foreach (var g in valid.GroupBy(e => e.Execution, StringComparer.Ordinal))
            if (g.Select(e => e.Scenario).Distinct(StringComparer.Ordinal).Count() > 1)
                yield return $"execution '{g.Key}': ambiguous mapping to several baseline identities ({string.Join(", ", g.Select(e => e.Scenario).Distinct(StringComparer.Ordinal))})";
    }

    /// <summary>True when any entry maps an execution scenario to a different baseline identity.</summary>
    public bool HasIdentityMappings => Manifest.Baselines.Any(e => e.ExecutionScenario is not null);

    /// <summary>
    /// Why the mapped entries of an execution scenario must not be used with this catalog, or null when they may. An unmapped
    /// entry never needs a binding. A mapped entry needs the executing scenario to be present, to be an external scenario with a
    /// validated fixture binding (fixture ID and recorded contract fingerprint, as produced by <see cref="ExternalScenarios.Merge"/>),
    /// and that binding's fixture ID to be exactly the entry's baseline identity. Nothing else (name similarity, prefixes) counts.
    /// </summary>
    public string? BindingProblem(string executionId, IEnumerable<ScenarioDescriptor> catalog)
    {
        var mapped = Manifest.Baselines.FirstOrDefault(e => e.ExecutionScenario == executionId);
        if (mapped is null) return null;
        var scenario = catalog.FirstOrDefault(s => s.Id == executionId);
        string where = $"Baseline identity mapping {executionId} -> {mapped.Scenario}";
        if (scenario is null) return where + ": the execution scenario is not in the current catalog.";
        if (scenario.External is not { FixtureId.Length: > 0, FixtureHash.Length: > 0 } binding || scenario.Backend != "diagnostic-logical")
            return where + ": the execution scenario has no validated fixture binding; baselines are only shared with the fixture it is bound to.";
        if (binding.FixtureId != mapped.Scenario)
            return where + $": the execution scenario is bound to fixture '{binding.FixtureId}', not to '{mapped.Scenario}'; refused.";
        return null;
    }

    /// <summary>
    /// Unmapped entries whose baseline identity is the fixture an executing external scenario is bound to: they are never
    /// evaluated for that execution (no implicit borrowing) and are reported as an explicit refusal instead of NotRun.
    /// </summary>
    public string? UnmappedFixtureIdentity(VisualBaselineEntry entry, ScenarioDescriptor executed) =>
        entry.ExecutionScenario is null && executed.External is { FixtureId.Length: > 0 } b && b.FixtureId == entry.Scenario && executed.Id != entry.Scenario
            && !Manifest.Baselines.Any(e => e.Execution == executed.Id)
            ? $"Refused: '{executed.Id}' is bound to fixture '{entry.Scenario}', but this entry has no executionScenario mapping; approved baselines are never borrowed implicitly."
            : null;

    /// <summary>Checkpoint grid and initial grid of a grid key; for a plain <c>WxH</c> both are that grid.</summary>
    public static bool TryParseGridKey(string? key, out string grid, out string initial)
    {
        grid = initial = "";
        var m = key is null ? null : GridKey.Match(key);
        if (m is null || !m.Success || !ScenarioSelection.TryParseSize(m.Groups[1].Value, out _, out _)
            || (m.Groups[2].Success && (!ScenarioSelection.TryParseSize(m.Groups[2].Value, out _, out _) || m.Groups[2].Value == m.Groups[1].Value))) return false;
        grid = m.Groups[1].Value; initial = m.Groups[2].Success ? m.Groups[2].Value : grid;
        return true;
    }

    /// <summary>Baseline identity = the flat artifact stem: scenario slug, checkpoint, grid key.</summary>
    public static string Key(string scenario, string checkpoint, string gridKey) => ArtifactNames.Slug(scenario) + "--" + checkpoint + "--" + gridKey;

    /// <summary>The entry of a baseline identity (as recorded in candidate and approved metadata).</summary>
    public VisualBaselineEntry? Find(string scenario, string checkpoint, string gridKey) =>
        Manifest.Baselines.FirstOrDefault(e => e.Scenario == scenario && e.Checkpoint == checkpoint && e.Grids.Contains(gridKey, StringComparer.Ordinal));

    /// <summary>The entry gating a checkpoint of an executing scenario (its own ID, or an explicit executionScenario mapping).</summary>
    public VisualBaselineEntry? FindForExecution(string executionId, string checkpoint, string gridKey) =>
        Manifest.Baselines.FirstOrDefault(e => e.Execution == executionId && e.Checkpoint == checkpoint && e.Grids.Contains(gridKey, StringComparer.Ordinal));

    /// <summary>The entries gating an executing scenario.</summary>
    public IEnumerable<VisualBaselineEntry> EntriesForExecution(string executionId) => Manifest.Baselines.Where(e => e.Execution == executionId);

    public VisualNormalizationRule[] RulesFor(VisualBaselineEntry entry) =>
        (entry.Normalizations ?? []).Select(id => Manifest.Normalizations.Single(r => r.Id == id)).ToArray();

    public string Fingerprint(VisualBaselineEntry entry) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(";", RulesFor(entry).Select(r => r.Id + "=" + r.Fingerprint())))));

    public string ApprovedCells(string key) => Path.Combine(ApprovedDirectory, key + ".tvs1");
    public string ApprovedMetadata(string key) => Path.Combine(ApprovedDirectory, key + ".baseline.json");
    public string RelativeApproved(string key) => ApprovedFolder + "/" + key + ".tvs1";

    /// <summary>The slot of <paramref name="key"/> for <paramref name="environment"/>: its own environment folder when that holds
    /// the key; else the flat slot when it is free or records exactly this environment (or cannot be read, so the loader reports
    /// it as invalid); else the (empty) environment folder. Reads only.</summary>
    public BaselineSlot Slot(string key, BaselineEnvironment environment)
    {
        string folder = Path.Combine(ApprovedDirectory, environment.Folder);
        var others = OtherEnvironments(key, environment);
        BaselineSlot Flat(bool exists) => new(ApprovedCells(key), ApprovedMetadata(key), Path.Combine(ApprovedDirectory, key + ".png"), RelativeApproved(key), exists, environment, others);
        BaselineSlot Own(bool exists) => new(Path.Combine(folder, key + ".tvs1"), Path.Combine(folder, key + ".baseline.json"), Path.Combine(folder, key + ".png"),
            ApprovedFolder + "/" + environment.Folder + "/" + key + ".tvs1", exists, environment, others);
        if (File.Exists(Path.Combine(folder, key + ".tvs1")) || File.Exists(Path.Combine(folder, key + ".baseline.json"))) return Own(true);
        if (!File.Exists(ApprovedCells(key)) && !File.Exists(ApprovedMetadata(key))) return Flat(false);
        var flat = RecordedEnvironment(ApprovedMetadata(key));
        return flat is null || flat == environment ? Flat(true) : Own(false);
    }

    /// <summary>The relative path of the approved cells of <paramref name="key"/> for <paramref name="environment"/>.</summary>
    public string RelativeApproved(string key, BaselineEnvironment environment) => Slot(key, environment).Relative;

    /// <summary>The environment a candidate in <paramref name="runDirectory"/> records (where approving it writes).</summary>
    public static BaselineEnvironment CandidateEnvironment(string runDirectory, string key) =>
        RecordedEnvironment(Path.Combine(runDirectory, key + ".candidate.json")) ?? throw new ArgumentException($"Candidate '{key}' has no readable environment (platform).");

    // The environment recorded in a metadata file, read leniently (null: missing or unreadable; the strict loader reports why).
    private static BaselineEnvironment? RecordedEnvironment(string metadata)
    {
        try
        {
            if (!File.Exists(metadata)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(metadata));
            var root = doc.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Text("platform") is not { Length: > 0 } platform || Text("captureLayer") is not { } layer) return null;
            return new(platform, Text("driver") ?? BaselineEnvironment.DefaultDriver, layer.StartsWith(BaselineEnvironment.LogicalCapture, StringComparison.Ordinal) ? BaselineEnvironment.LogicalCapture : layer);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    // Environments other than this one that have an approved file for the key (flat slot and environment folders).
    private BaselineEnvironment[] OtherEnvironments(string key, BaselineEnvironment environment)
    {
        if (!Directory.Exists(ApprovedDirectory)) return [];
        var found = new List<BaselineEnvironment>();
        if (RecordedEnvironment(ApprovedMetadata(key)) is { } flat) found.Add(flat);
        foreach (string folder in Directory.GetDirectories(ApprovedDirectory).Order(StringComparer.Ordinal))
            if (RecordedEnvironment(Path.Combine(folder, key + ".baseline.json")) is { } own) found.Add(own);
        return found.Where(e => e != environment).Distinct().ToArray();
    }

    /// <summary>"No matching baseline": what is missing for this environment and, when other environments have one, why those
    /// are not comparable (the differing dimensions). Never a reason to compare against them.</summary>
    public static string MissingReason(BaselineEnvironment environment, IReadOnlyCollection<BaselineEnvironment> others) =>
        $"No approved baseline for {environment}: approval required."
        + (others.Count == 0 ? "" : " Not comparable with the approved baseline" + (others.Count == 1 ? "" : "s") + " of "
            + string.Join("; ", others.Select(o => $"{o} ({string.Join(", ", environment.Differences(o))} differ{(environment.Differences(o).Length == 1 ? "s" : "")})"))
            + "; baselines are never compared or reused across environments.");

    /// <summary>The approved baseline of an entry/grid for this machine's headless environment (NullDriver), fully verified.</summary>
    public (string Status, string Reason, ScreenSnapshot? Cells, VisualBaselineFile? File, string? CellsSha256) LoadApproved(VisualBaselineEntry entry, string gridKey) =>
        LoadApproved(entry, gridKey, BaselineEnvironment.Current());

    /// <summary>The approved baseline of an entry/grid for <paramref name="environment"/>, fully verified; on failure Status says
    /// why. Another environment's baseline is never loaded: without one for this environment the status is BaselineMissing.</summary>
    public (string Status, string Reason, ScreenSnapshot? Cells, VisualBaselineFile? File, string? CellsSha256) LoadApproved(VisualBaselineEntry entry, string gridKey, BaselineEnvironment environment)
    {
        string key = Key(entry.Scenario, entry.Checkpoint, gridKey);
        var slot = Slot(key, environment);
        string cells = slot.Cells, meta = slot.Metadata;
        bool hasCells = File.Exists(cells), hasMeta = File.Exists(meta);
        if (!hasCells && !hasMeta) return (VisualStatuses.BaselineMissing, MissingReason(environment, slot.Others), null, null, null);
        string? sha = hasCells ? ScreenSnapshot.Hash(File.ReadAllBytes(cells)) : null;
        (string, string, ScreenSnapshot?, VisualBaselineFile?, string?) Invalid(string why) => (VisualStatuses.BaselineInvalid, why, null, null, sha);
        if (!hasCells || !hasMeta) return Invalid($"Incomplete approved baseline: {(hasCells ? "metadata" : "cells")} file missing.");
        VisualBaselineFile? file;
        try { file = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllBytes(meta), Strict); }
        catch (JsonException ex) { return Invalid("Baseline metadata is not valid JSON for this format: " + ex.Message); }
        if (file is null) return Invalid("Empty baseline metadata.");
        if (file.Format != BaselineFormat) return Invalid($"Unsupported baseline format version '{file.Format}' (expected {BaselineFormat}).");
        if (file.CellsFormat != CellsFormat) return Invalid($"Unsupported cells format '{file.CellsFormat}'.");
        if (file.Status != "approved") return Invalid($"Baseline status is '{file.Status}', not 'approved'.");
        var (grid, initial) = TryParseGridKey(gridKey, out string g, out string i) ? (g, i) : ("", "");
        if (file.Key != key || file.ScenarioId != entry.Scenario || file.CheckpointId != entry.Checkpoint || file.Grid != grid || file.InitialGrid != initial)
            return Invalid("Baseline identity (key, scenario, checkpoint, grid) does not match its file name and manifest entry.");
        // Legacy approvals carry no execution identity (approved under the baseline identity itself); a recorded one must be this entry's.
        if (file.ExecutionScenarioId is { } executed && (entry.ExecutionScenario is null || executed != entry.ExecutionScenario))
            return Invalid($"Baseline was approved for execution '{executed}', but the manifest entry executes as '{entry.Execution}'.");
        if (BaselineEnvironment.Of(file) != environment)
            return Invalid($"Baseline records environment {BaselineEnvironment.Of(file)}, but it is stored as the baseline of {environment}.");
        if (!string.Equals(file.CellsSha256, sha, StringComparison.Ordinal)) return Invalid("Baseline integrity failure: cells SHA-256 differs from the recorded value (corrupt or edited).");
        ScreenSnapshot snapshot;
        try { snapshot = ScreenSnapshot.Deserialize(File.ReadAllBytes(cells)); }
        catch (InvalidDataException ex) { return Invalid("Baseline cells are not a valid TVS1 snapshot: " + ex.Message); }
        if (ScenarioSelection.Format(snapshot.Width, snapshot.Height) != grid || file.Width != snapshot.Width || file.Height != snapshot.Height)
            return Invalid($"Baseline cells are {snapshot.Width}x{snapshot.Height}, but the baseline is for grid {grid}.");
        if (file.NormalizationFingerprint != Fingerprint(entry))
            return Invalid("The entry's normalization rules changed since this baseline was approved; re-review and re-approve it.");
        return (VisualStatuses.Match, "", snapshot, file, sha);
    }

    /// <summary>Per entry and grid: approved and valid, missing, or invalid (with the reason) for <paramref name="environment"/>
    /// (default: this machine, NullDriver). Reads only.</summary>
    public IEnumerable<(string Key, string Status, string Reason)> Status(BaselineEnvironment? environment = null) =>
        Manifest.Baselines.SelectMany(e => e.Grids.Select(g =>
        {
            var (status, reason, _, _, _) = LoadApproved(e, g, environment ?? BaselineEnvironment.Current());
            return (Key(e.Scenario, e.Checkpoint, g), status == VisualStatuses.Match ? "Approved" : status, reason);
        }));

    /// <summary>Promotes reviewed candidates of a run into <c>approved/</c>. Deliberate owner action only: every candidate is named
    /// explicitly, <paramref name="confirm"/> must be <c>approve-&lt;count&gt;</c>, each candidate must belong to a manifest entry,
    /// be intact, have been produced under the current rules, and replace exactly the approved file it was compared against
    /// (and replacing requires <paramref name="replace"/>). All checks pass before any file is written; nothing else is touched.
    /// A candidate is approved into the slot of the environment it records (<see cref="Slot"/>); it never
    /// replaces another environment's baseline.</summary>
    public string[] Approve(string runDirectory, IReadOnlyList<string> candidates, string? confirm, bool replace)
    {
        if (candidates.Count is 0 or > 512) throw new ArgumentException("Name 1..512 candidates explicitly (--candidate <key,key>); there is no wildcard.");
        if (candidates.Distinct(StringComparer.Ordinal).Count() != candidates.Count) throw new ArgumentException("A candidate is named twice.");
        string expected = "approve-" + candidates.Count.ToString(CultureInfo.InvariantCulture);
        if (confirm != expected) throw new ArgumentException($"Approval needs the explicit confirmation --confirm {expected} (the number of named candidates).");
        string run = Path.GetFullPath(runDirectory);
        if (!Directory.Exists(run)) throw new ArgumentException("Run directory not found: " + runDirectory);
        var plan = new List<(string Key, byte[] Cells, VisualBaselineFile File, byte[] Png, BaselineSlot Slot)>();
        foreach (string key in candidates)
        {
            if (!KeyPattern.IsMatch(key)) throw new ArgumentException($"'{key}' is not a baseline key (<scenario-slug>--<checkpoint>--<grid>).");
            string cellsPath = Path.Combine(run, key + ".candidate.tvs1"), metaPath = Path.Combine(run, key + ".candidate.json");
            if (!File.Exists(cellsPath) || !File.Exists(metaPath)) throw new ArgumentException($"No candidate '{key}' in {run}.");
            byte[] cells = File.ReadAllBytes(cellsPath);
            VisualBaselineFile file;
            try { file = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllBytes(metaPath), Strict) ?? throw new JsonException("empty"); }
            catch (JsonException ex) { throw new ArgumentException($"Candidate '{key}' metadata is not valid: {ex.Message}"); }
            if (file.Format != BaselineFormat || file.Status != "candidate" || file.Key != key) throw new ArgumentException($"'{key}' is not a candidate of format {BaselineFormat}.");
            if (ScreenSnapshot.Hash(cells) != file.CellsSha256) throw new ArgumentException($"Candidate '{key}' failed its integrity check.");
            var snapshot = ScreenSnapshot.Deserialize(cells);
            string gridKey = file.Grid == file.InitialGrid ? file.Grid : file.Grid + "-from-" + file.InitialGrid;
            var entry = Find(file.ScenarioId, file.CheckpointId, gridKey) ?? throw new ArgumentException($"Candidate '{key}' has no entry in the manifest; unrelated baselines are never written.");
            if (Key(entry.Scenario, entry.Checkpoint, gridKey) != key || ScenarioSelection.Format(snapshot.Width, snapshot.Height) != file.Grid)
                throw new ArgumentException($"Candidate '{key}' identity does not match its cells and the manifest.");
            if (file.ExecutionScenarioId != entry.ExecutionScenario)
                throw new ArgumentException($"Candidate '{key}' was produced by execution '{file.ExecutionScenarioId ?? file.ScenarioId}', but the manifest entry executes as '{entry.Execution}'; capture it again.");
            if (file.NormalizationFingerprint != Fingerprint(entry)) throw new ArgumentException($"Candidate '{key}' was produced under other normalization rules; capture it again.");
            if (string.IsNullOrWhiteSpace(file.Platform)) throw new ArgumentException($"Candidate '{key}' records no platform; capture it again.");
            var slot = Slot(key, BaselineEnvironment.Of(file));
            string current = slot.Cells;
            bool exists = slot.Exists;
            string? currentSha = File.Exists(current) ? ScreenSnapshot.Hash(File.ReadAllBytes(current)) : null;
            if (!exists && file.ReplacesSha256 is not null) throw new ArgumentException($"Candidate '{key}' was compared against an approved baseline that no longer exists.");
            if (exists)
            {
                if (!replace) throw new ArgumentException($"'{key}' already has an approved baseline; replacing it needs --replace.");
                if (file.ReplacesSha256 != currentSha) throw new ArgumentException($"The approved '{key}' changed since this candidate was compared (or the candidate predates it); capture again.");
            }
            plan.Add((key, cells, file, LogicalPng.Encode(snapshot), slot));
        }
        Directory.CreateDirectory(ApprovedDirectory);
        foreach (var (key, cells, file, png, slot) in plan)
        {
            var approved = file with { Status = "approved", ReplacesSha256 = null, Review = "Approved by explicit owner action (baseline approve)." };
            Directory.CreateDirectory(Path.GetDirectoryName(slot.Cells)!);
            Replace(slot.Cells, cells);
            Replace(slot.Metadata, JsonSerializer.SerializeToUtf8Bytes(approved, ScenarioRunner.Json));
            // Review aid only: never compared (PNG bytes depend on the platform's deflate implementation).
            Replace(slot.Png, png);
        }
        return plan.Select(p => p.Key).ToArray();
    }

    private static void Replace(string path, byte[] bytes)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Compares the checkpoints a run captures with the approved baselines of one manifest and writes the comparison
/// artifacts (flat, into the run directory, never overwriting). It only reads the baseline store.
/// <paramref name="environment"/> is the run's baseline environment (default: this machine, NullDriver);
/// only that environment's approved baselines are compared, and candidates record it.</summary>
internal sealed class VisualGate(VisualBaselineSet set, string runRoot, BaselineEnvironment? environment = null)
{
    private readonly List<VisualEntryResult> entries = [];
    private readonly HashSet<string> evaluated = new(StringComparer.Ordinal);
    private readonly BaselineEnvironment env = environment ?? BaselineEnvironment.Current();
    internal VisualBaselineSet Set => set;
    internal BaselineEnvironment Environment => env;

    internal async Task<VisualCheckpointResult?> EvaluateAsync(ScenarioDescriptor scenario, string checkpoint, string grid, string initial,
        ScreenSnapshot actual, string stem, NormalizationContext context, TargetIdentity identity)
    {
        string gridKey = grid == initial ? grid : grid + "-from-" + initial;
        var entry = set.FindForExecution(scenario.Id, checkpoint, gridKey);
        if (entry is null) return null;
        // Baseline identity (key, metadata, approval) is the entry's; artifacts of this capture keep the execution stem.
        string key = VisualBaselineSet.Key(entry.Scenario, checkpoint, gridKey);
        string? mapped = entry.ExecutionScenario is null ? null : entry.Scenario;
        evaluated.Add(key);
        string? binding = set.BindingProblem(scenario.Id, [scenario]);
        var rules = set.RulesFor(entry);
        var normalized = VisualNormalizer.Apply(actual, rules, context);
        var (approvedStatus, approvedReason, expected, approvedFile, approvedSha) = set.LoadApproved(entry, gridKey, env);
        string relativeApproved = set.RelativeApproved(key, env);
        string status, reason;
        ScreenComparison? comparison = null;
        if (binding is not null) { status = VisualStatuses.BaselineInvalid; reason = binding; }
        else if (normalized.Canonical is null) { status = VisualStatuses.NormalizationError; reason = "Normalization failed: " + normalized.Error; }
        else if (approvedStatus != VisualStatuses.Match) { status = approvedStatus; reason = approvedReason; }
        else
        {
            comparison = ScreenComparer.Compare(expected!, normalized.Canonical);
            var maskedNow = Spans(normalized.Applied.SelectMany(a => a.Spans));
            var maskedThen = Spans(approvedFile!.Normalizations.SelectMany(a => a.Spans));
            bool sameMask = maskedNow.SetEquals(maskedThen);
            status = comparison.Equal && sameMask ? VisualStatuses.Match : VisualStatuses.Mismatch;
            reason = status == VisualStatuses.Match ? "Equal to the approved baseline after the listed normalizations."
                : !comparison.DimensionsMatch ? $"Screen dimensions differ: expected {comparison.ExpectedWidth}x{comparison.ExpectedHeight}, actual {comparison.ActualWidth}x{comparison.ActualHeight}."
                : comparison.DifferingCells == 0 ? "The masked dynamic cells are not the cells masked in the baseline."
                : $"{comparison.DifferingCells} cell(s) differ ({comparison.GlyphDifferences} glyph, {comparison.AttributeDifferences} attribute)"
                  + (comparison.FirstDifference is { } f ? $"; first at ({f.X},{f.Y}): expected {Describe(f.Expected)}, actual {Describe(f.Actual)}." : ".")
                  + (sameMask ? "" : " The masked dynamic cells also differ from the baseline's.");
        }

        string? candidate = null, candidatePng = null, candidateMeta = null, diffPng = null;
        if (status != VisualStatuses.Match && binding is null && normalized.Canonical is { } canonical)
        {
            byte[] cells = canonical.Serialize(), raster = LogicalPng.Raster(canonical);
            var file = new VisualBaselineFile(VisualBaselineSet.BaselineFormat, "candidate", key, entry.Scenario, checkpoint, grid, initial,
                canonical.Width, canonical.Height, VisualBaselineSet.CellsFormat, ScreenSnapshot.Hash(cells), ScreenSnapshot.Hash(raster), LogicalPng.Renderer,
                $"logical (Layer A, {env.Driver} TScreen.ScreenBuffer cells)", env.Platform,
                normalized.Applied.Select(a => new BaselineNormalization(a.Rule, a.Kind, a.Description, a.Spans)).ToArray(), set.Fingerprint(entry),
                entry.Purpose, entry.Fixture, approvedSha, $"{identity.Assembly} {identity.AssemblyVersion}".Trim(),
                "Unreviewed candidate: NOT approved. Promote only after review with 'baseline approve'.", entry.ExecutionScenario,
                env.Driver == BaselineEnvironment.DefaultDriver ? null : env.Driver);
            // Candidates are named by the baseline key: that is what review and 'baseline approve' name (equal to the stem when unmapped).
            candidate = key + ".candidate.tvs1"; candidatePng = key + ".candidate.png"; candidateMeta = key + ".candidate.json";
            await WriteNewAsync(candidate, cells);
            await WriteNewAsync(candidatePng, LogicalPng.Encode(canonical, raster));
            await WriteNewAsync(candidateMeta, JsonSerializer.SerializeToUtf8Bytes(file, ScenarioRunner.Json));
        }
        if (status == VisualStatuses.Mismatch && comparison is not null)
        {
            diffPng = stem + ".diff.png";
            await WriteNewAsync(diffPng, VisualDiffImage.Encode(expected!, normalized.Canonical!, normalized.Applied));
        }
        string diff = stem + ".diff.json";
        await WriteNewAsync(diff, JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = VisualBaselineSet.ComparisonFormat, scenarioId = scenario.Id, baselineScenarioId = mapped, checkpointId = checkpoint, grid, initialGrid = initial, key, status, reason,
            environment = new { platform = env.Platform, driver = env.Driver, capture = env.Capture },
            unitOfComparison = "one TVS1 cell: one UTF-16 code unit (ordinal) + the raw attribute byte (foreground low nibble, background high nibble)",
            purpose = entry.Purpose, fixture = entry.Fixture,
            expected = new { approved = relativeApproved, approvedStatus, approvedReason = approvedReason.Length == 0 ? null : approvedReason,
                width = expected?.Width, height = expected?.Height, cellsSha256 = approvedSha },
            actual = new { cells = stem + ".tvs1", png = stem + ".png", width = actual.Width, height = actual.Height,
                canonicalSha256 = normalized.Canonical is { } c ? ScreenSnapshot.Hash(c.Serialize()) : null },
            candidate = candidate is null ? null : new { cells = candidate, png = candidatePng, metadata = candidateMeta, review = "Unreviewed" },
            diffPng,
            normalizations = normalized.Applied, normalizationError = normalized.Error, maskedCells = normalized.MaskedCells,
            comparison,
        }, ScenarioRunner.Json));
        entries.Add(new(scenario.Id, checkpoint, gridKey, status, reason, diff, diffPng, candidate, mapped));
        return new(status, key, reason, comparison?.DifferingCells ?? 0, comparison?.GlyphDifferences ?? 0, comparison?.AttributeDifferences ?? 0,
            normalized.Applied.Select(a => a.Rule).ToArray(), normalized.MaskedCells, relativeApproved, diff, diffPng, candidate, candidatePng, candidateMeta);
    }

    /// <summary>Adds the entries this run did not evaluate: NotCaptured when the execution at that initial grid ran, else NotRun.
    /// <paramref name="selected"/> (the run's scenarios) turns an unmapped entry of a fixture identity that a selected external
    /// scenario is bound to into an explicit refusal (BaselineInvalid) instead of an informational NotRun.</summary>
    internal VisualRunSummary Finish(IEnumerable<ScenarioResult> results, IReadOnlyCollection<ScenarioDescriptor>? selected = null)
    {
        foreach (var e in set.Manifest.Baselines)
        foreach (string g in e.Grids)
        {
            string key = VisualBaselineSet.Key(e.Scenario, e.Checkpoint, g);
            if (evaluated.Contains(key)) continue;
            VisualBaselineSet.TryParseGridKey(g, out _, out string initial);
            string? mapped = e.ExecutionScenario is null ? null : e.Scenario;
            var result = results.FirstOrDefault(r => r.ScenarioId == e.Execution && r.Size == initial);
            var borrower = result is null ? (selected ?? []).FirstOrDefault(s => set.UnmappedFixtureIdentity(e, s) is not null) : null;
            var borrowed = borrower is null ? null : results.FirstOrDefault(r => r.ScenarioId == borrower.Id && r.Size == initial);
            entries.Add(borrowed is not null
                ? new(borrowed.ScenarioId, e.Checkpoint, g, VisualStatuses.BaselineInvalid, set.UnmappedFixtureIdentity(e, borrower!)!, BaselineScenarioId: e.Scenario)
                : result is null
                ? new(e.Execution, e.Checkpoint, g, VisualStatuses.NotRun, "Scenario or initial grid not selected in this run.", BaselineScenarioId: mapped)
                : result.Outcome == ScenarioOutcomes.Skipped
                ? new(e.Execution, e.Checkpoint, g, VisualStatuses.NotRun, "Execution skipped (platform/driver unsupported); nothing was compared.", BaselineScenarioId: mapped)
                : new(e.Execution, e.Checkpoint, g, VisualStatuses.NotCaptured, $"Execution {result.Outcome} without capturing this checkpoint: {result.Reason}", BaselineScenarioId: mapped));
        }
        return new(set.ManifestPath, entries.ToArray());
    }

    private async Task WriteNewAsync(string name, byte[] bytes)
    {
        await using var stream = new FileStream(Path.Combine(runRoot, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes);
    }

    private static HashSet<(int, int, int)> Spans(IEnumerable<int[]> spans) => spans.Select(s => (s[0], s[1], s[2])).ToHashSet();
    internal static string Describe(CellInfo c) => $"'{c.Text}' {c.Code} attr {c.Attribute}";
    internal static string Platform() => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;
}

/// <summary>Visual difference image from the logical PNG renderer's glyphs: three panels, expected | actual | difference map.
/// In the map, unchanged cells are drawn dim, glyph differences on red, attribute-only differences on yellow, masked
/// (normalized) cells on blue, cells outside the other screen on magenta.</summary>
public static class VisualDiffImage
{
    private const int Gap = 1; // cells between panels
    private const uint Separator = 0x404040, Dim = 0x606060, Ground = 0x000000, GlyphRed = 0xcc2222, AttributeYellow = 0xd0b000, MaskBlue = 0x2244cc, Outside = 0x880088;

    public static byte[] Encode(ScreenSnapshot expected, ScreenSnapshot actual, IEnumerable<AppliedNormalization>? masked = null)
    {
        int cols = Math.Max(expected.Width, actual.Width), rows = Math.Max(expected.Height, actual.Height);
        int widthCells = cols * 3 + Gap * 2, width = widthCells * LogicalPng.CellWidth, height = rows * LogicalPng.CellHeight;
        var mask = new HashSet<(int, int)>();
        foreach (var span in (masked ?? []).SelectMany(a => a.Spans)) for (int i = 0; i < span[2]; i++) mask.Add((span[0] + i, span[1]));
        using var raw = new MemoryStream(height * (width * 3 + 1));
        for (int py = 0; py < height; py++)
        {
            raw.WriteByte(0);
            int y = py / LogicalPng.CellHeight, gy = py % LogicalPng.CellHeight;
            for (int px = 0; px < width; px++)
            {
                int cx = px / LogicalPng.CellWidth, gx = px % LogicalPng.CellWidth;
                uint rgb;
                if (cx < cols) rgb = Panel(expected, cx, y, gx, gy);
                else if (cx < cols + Gap || (cx >= cols * 2 + Gap && cx < cols * 2 + Gap * 2)) rgb = Separator;
                else if (cx < cols * 2 + Gap) rgb = Panel(actual, cx - cols - Gap, y, gx, gy);
                else rgb = Map(expected, actual, cx - cols * 2 - Gap * 2, y, gx, gy, mask);
                raw.WriteByte((byte)(rgb >> 16)); raw.WriteByte((byte)(rgb >> 8)); raw.WriteByte((byte)rgb);
            }
        }
        return LogicalPng.EncodeRaster(width, height, raw.ToArray());
    }

    private static uint Panel(ScreenSnapshot screen, int x, int y, int gx, int gy)
    {
        if (x >= screen.Width || y >= screen.Height) return Outside;
        var cell = screen[x, y];
        return LogicalPng.Color(LogicalPng.Glyph(cell.Character, gx, gy) ? cell.Attr.Foreground : cell.Attr.Background >> 4);
    }

    private static uint Map(ScreenSnapshot expected, ScreenSnapshot actual, int x, int y, int gx, int gy, HashSet<(int, int)> mask)
    {
        if (x >= expected.Width || y >= expected.Height || x >= actual.Width || y >= actual.Height) return Outside;
        TScreenChar e = expected[x, y], a = actual[x, y];
        bool glyph = LogicalPng.Glyph(a.Character, gx, gy);
        if (e.Character != a.Character) return glyph ? 0xffffff : GlyphRed;
        if ((byte)e.Attr != (byte)a.Attr) return glyph ? 0x000000 : AttributeYellow;
        if (mask.Contains((x, y))) return glyph ? 0xffffff : MaskBlue;
        return glyph ? Dim : Ground;
    }
}
