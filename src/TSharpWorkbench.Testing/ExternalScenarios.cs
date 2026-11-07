using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSharpWorkbench.Testing;

public sealed class ExternalScenarioException(string path, string detail, Exception? inner = null)
    : ArgumentException($"{path}: {detail}", inner);

/// <summary>A fixture-only binding refused at execution: by the controller (direct execution, unbound checkpoint) or by the
/// target's host boundary. The message names scenario, fixture and checkpoint IDs, never local paths.</summary>
public sealed class FixtureBindingException(string message) : IOException(message);

/// <summary>A validated external document, independent of the target's CLR classes and step serialization.</summary>
public sealed record ExternalScenarioFile(string Path, string Fixture, ScenarioDescriptor Scenario, string ContentHash, bool ExplicitGrid);

/// <summary>Version 1 diagnostic backend adapter. Documents contain data, never executable callbacks.</summary>
public static class ExternalScenarios
{
    public const string Schema = "tsharpworkbench.scenario";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ExternalScenarioFile Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("Document exceeds 1 MiB.");
            return Parse(path, File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new ExternalScenarioException(path, ex.Message, ex); }
    }

    /// <summary>Exactly the validation of <see cref="Load"/>, applied to document bytes that are
    /// not (yet) on disk, e.g. an editor's unsaved document. <paramref name="path"/> is only the identity reported in errors
    /// and in the result; nothing is read from or written to it.</summary>
    public static ExternalScenarioFile LoadFromBytes(string path, byte[] bytes)
    {
        path = System.IO.Path.GetFullPath(path);
        try
        {
            if (bytes.Length > 1024 * 1024) throw new ArgumentException("Document exceeds 1 MiB.");
            return Parse(path, bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new ExternalScenarioException(path, ex.Message, ex); }
    }

    private static ExternalScenarioFile Parse(string path, byte[] bytes)
    {
        {
            using var doc = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0));
            Unique(doc.RootElement, "$");
            var r = doc.RootElement;
            Fields(r, "$", "$schema", "schema", "version", "id", "title", "description", "group", "tags", "fixture", "backend", "grid", "expectedOutcome", "steps");
            if (Text(r, "schema") != Schema || Number(r, "version") != 1) throw new ArgumentException("$.schema/version: expected tsharpworkbench.scenario version 1.");
            string backend = Optional(r, "backend") ?? "diagnostic-logical";
            if (backend is not ("diagnostic-logical" or "proxy-logical")) throw new ArgumentException("$.backend: unsupported backend.");
            string id = Text(r, "id"), title = Text(r, "title"), description = Text(r, "description"), fixture = backend == "proxy-logical" ? "" : Text(r, "fixture");
            if (backend == "proxy-logical" && r.TryGetProperty("fixture", out _)) throw new ArgumentException("$.fixture: proxy scenarios cannot bind compiled fixtures.");
            if (id == fixture) throw new ArgumentException("$.id: external identity must differ from its compiled fixture (both remain discoverable).");
            string outcome = Text(r, "expectedOutcome") switch { "passed" => ScenarioOutcomes.Passed, "failed" => ScenarioOutcomes.Failed, _ => throw new ArgumentException("$.expectedOutcome: use passed or failed.") };
            bool explicitGrid = r.TryGetProperty("grid", out var gridJson);
            var grid = ScenarioGrid.Standard;
            if (explicitGrid)
            {
                Fields(gridJson, "$.grid", "minWidth", "minHeight", "maxWidth", "maxHeight", "defaults");
                grid = new(Number(gridJson, "minWidth"), Number(gridJson, "minHeight"), Number(gridJson, "maxWidth"), Number(gridJson, "maxHeight"), Strings(gridJson.GetProperty("defaults")));
            }
            var metadata = new Dictionary<string, string>();
            if (r.TryGetProperty("tags", out var tags))
            {
                string[] values = Strings(tags);
                if (values.Length > 16 || values.Distinct(StringComparer.Ordinal).Count() != values.Length || values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("$.tags: at most 16 unique nonempty tags.");
                metadata.Add("tags", string.Join(", ", values));
            }
            var steps = r.GetProperty("steps");
            if (steps.ValueKind != JsonValueKind.Array) throw new ArgumentException("$.steps: expected array.");
            var plan = new List<ScenarioStep>(); int index = 0;
            foreach (var s in steps.EnumerateArray())
            {
                string at = $"$.steps[{index++}]";
                try { plan.Add(ParseStep(s)); }
                catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                { throw new ArgumentException(at + ": " + ex.Message, ex); }
            }
            var scenario = new ScenarioDescriptor(id, title, description, Optional(r, "group") ?? "external", grid, plan.ToArray(),
                Metadata: metadata.Count == 0 ? null : metadata, ExpectedResult: outcome) { Backend = backend };
            if (backend == "proxy-logical" && plan.Any(x => x.Kind == ScenarioStep.Action || (x.Kind == ScenarioStep.Mouse && InputSteps.TryParseMouse(x.Name, out var mouse) && mouse.At.Name is not null)))
                throw new ArgumentException("Proxy scenarios do not support compiled actions or named points.");
            var checkpointIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in scenario.Steps)
            {
                if (step.Kind == ScenarioStep.Checkpoint && !checkpointIds.Add(step.Name)) throw new ArgumentException("Duplicate checkpoint ID '" + step.Name + "'.");
                try { ScenarioRunner.ValidateCatalog([scenario with { Steps = step.Kind == ScenarioStep.Checkpoint ? [step] : [step, new(ScenarioStep.Checkpoint, "validation-only")] }]); }
                catch (IOException ex) { throw new ArgumentException($"Invalid {step.Kind} step '{step.Name}': check input, checkpoint/assertion IDs, evidence, retry bounds and grid limits.", ex); }
            }
            ScenarioRunner.ValidateCatalog([scenario]);
            return new(path, fixture, scenario, ScreenSnapshot.Hash(bytes), explicitGrid);
        }
    }

    /// <summary>Binds external documents to the advertised raw catalog. Fixture-bound documents bind only to fixture-only
    /// registrations (strict).</summary>
    public static ScenarioDescriptor[] Merge(ScenarioDescriptor[] compiled, IEnumerable<ExternalScenarioFile> files) => Merge(compiled, files, null);

    /// <summary>As <see cref="Merge(ScenarioDescriptor[], IEnumerable{ExternalScenarioFile})"/>, with an explicit compatibility
    /// allowance: <paramref name="legacyFixtures"/> names the ordinary compiled scenarios (legacy fixtures, bound by
    /// their descriptor <see cref="Fingerprint"/>) that the caller has reviewed and allows as fixtures. Every other compiled
    /// scenario ID is refused as a fixture; there is no wildcard.</summary>
    public static ScenarioDescriptor[] Merge(ScenarioDescriptor[] compiled, IEnumerable<ExternalScenarioFile> files, IReadOnlyCollection<string>? legacyFixtures)
    {
        var result = compiled.ToList();
        foreach (var file in files)
        {
            try
            {
                var s = file.Scenario;
                if (result.Any(x => x.Id == s.Id)) throw new ArgumentException("Duplicate scenario ID '" + s.Id + "'.");
                if (s.Backend == "proxy-logical")
                {
                    result.Add(s with { External = new("", file.Path, file.ContentHash, "") });
                    continue;
                }
                var fixture = compiled.SingleOrDefault(x => x.Id == file.Fixture) ?? throw new ArgumentException("Unresolved fixture '" + file.Fixture + "'.");
                if (!fixture.IsFixtureOnly && legacyFixtures?.Contains(fixture.Id) != true) throw new ArgumentException(LegacyFixtureRefusal(fixture.Id));
                var adapters = fixture.Adapters ?? throw new ArgumentException("Fixture does not advertise adapters; rebuild the diagnostic target with current Testing.");
                if (s.Steps.Any(x => x.Retry is { Attempts: > 1 }) && !adapters.Recapture) throw new ArgumentException("Fixture does not support checkpoint recapture.");
                foreach (var step in s.Steps)
                {
                    if (step.Kind == ScenarioStep.Action && !adapters.Actions.Contains(step.Name)) throw new ArgumentException("Unresolved action adapter '" + step.Name + "'.");
                    if (step.Kind == ScenarioStep.Mouse && InputSteps.TryParseMouse(step.Name, out var mouse) && mouse.At.Name is { } point && !adapters.Points.Contains(point))
                        throw new ArgumentException("Unresolved point adapter '" + point + "'.");
                }
                // Strict only for explicit fixture-only registrations; legacy compiled fixtures keep unbound checkpoints.
                if (fixture.IsFixtureOnly && UnboundCheckpoint(s, fixture) is { } unbound) throw new ArgumentException(unbound);
                var grid = file.ExplicitGrid ? s.Grid : fixture.Grid;
                if (grid.MinWidth < fixture.Grid.MinWidth || grid.MinHeight < fixture.Grid.MinHeight || grid.MaxWidth > fixture.Grid.MaxWidth || grid.MaxHeight > fixture.Grid.MaxHeight)
                    throw new ArgumentException("External grid range must be within fixture limits.");
                s = s with { Grid = grid, Safety = fixture.Safety, Kind = fixture.Kind, Conditions = fixture.Conditions,
                    External = new(fixture.Id, file.Path, file.ContentHash, BindingFingerprint(fixture)) };
                ScenarioRunner.ValidateCatalog([s]);
                result.Add(s);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
            { throw new ExternalScenarioException(file.Path, ex.Message, ex); }
        }
        return result.ToArray();
    }

    /// <summary>Why a fixture-bound document cannot bind to the ordinary compiled scenario <paramref name="id"/> by default.</summary>
    public static string LegacyFixtureRefusal(string id) =>
        $"Fixture '{id}' is an ordinary compiled scenario, not a fixture-only registration (TestScenario.FixtureOnly). Fixture-bound documents "
        + $"bind only to fixture-only registrations; a reviewed legacy binding to this compiled scenario must be allowed explicitly "
        + $"(project \"legacyFixtures\": [\"{id}\"], or CLI --legacy-fixture {id}).";

    /// <summary>Legacy fixture descriptor fingerprint: SHA-256 of the descriptor's artifact serialization. Unchanged.</summary>
    public static string Fingerprint(ScenarioDescriptor fixture) => ScreenSnapshot.Hash(JsonSerializer.SerializeToUtf8Bytes(fixture, ScenarioRunner.Json));

    /// <summary>Prefix of a versioned fixture-only contract fingerprint (<see cref="ContractFingerprint"/>).</summary>
    public const string ContractFingerprintPrefix = "fixture-contract/1:";

    /// <summary>The fingerprint an external binding records and execution re-verifies: <see cref="ContractFingerprint"/> for a
    /// fixture-only registration, the legacy <see cref="Fingerprint"/> otherwise.</summary>
    public static string BindingFingerprint(ScenarioDescriptor fixture) => fixture.IsFixtureOnly ? ContractFingerprint(fixture) : Fingerprint(fixture);

    /// <summary>
    /// Versioned fixture-only contract fingerprint: <c>fixture-contract/1:</c> + lowercase SHA-256 of a canonical, compact UTF-8
    /// JSON encoding written field by field in a fixed order (independent of serializer options and CLR property order):
    /// role, contract version, reviewed revision, fixture ID, checkpoint expectation names, action names and point names (each
    /// ordinal-sorted), recapture support, kind, grid range and default grids, the full safety declaration, conditions and
    /// metadata (ordinal key order). Display text (name, description, group) is excluded. It identifies the ADVERTISED contract;
    /// it cannot detect changes inside callback implementations. It is computed from the live advertisement at binding and
    /// again at execution, so external documents never persist it.
    /// </summary>
    public static string ContractFingerprint(ScenarioDescriptor fixture)
    {
        var c = fixture.Contract ?? throw new ArgumentException($"'{fixture.Id}' is not a fixture-only registration.");
        var adapters = fixture.Adapters ?? throw new ArgumentException($"Fixture-only registration '{fixture.Id}' does not advertise adapters.");
        var safety = fixture.Safety ?? ScenarioSafety.None;
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            static void Names(Utf8JsonWriter w, string key, IEnumerable<string> values)
            {
                w.WriteStartArray(key);
                foreach (string v in values.Order(StringComparer.Ordinal)) w.WriteStringValue(v);
                w.WriteEndArray();
            }
            w.WriteStartObject();
            w.WriteString("role", c.Role);
            w.WriteNumber("contractVersion", c.ContractVersion);
            w.WriteString("revision", c.Revision);
            w.WriteString("fixture", fixture.Id);
            Names(w, "checkpoints", c.Checkpoints);
            Names(w, "actions", adapters.Actions);
            Names(w, "points", adapters.Points);
            w.WriteBoolean("recapture", adapters.Recapture);
            w.WriteString("kind", fixture.Kind);
            w.WriteStartObject("grid");
            w.WriteNumber("minWidth", fixture.Grid.MinWidth); w.WriteNumber("minHeight", fixture.Grid.MinHeight);
            w.WriteNumber("maxWidth", fixture.Grid.MaxWidth); w.WriteNumber("maxHeight", fixture.Grid.MaxHeight);
            w.WriteStartArray("defaults");
            foreach (string d in fixture.Grid.Defaults) w.WriteStringValue(d);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("safety");
            w.WriteBoolean("fixtureFiles", safety.FixtureFiles); w.WriteBoolean("network", safety.Network);
            w.WriteBoolean("externalProcesses", safety.ExternalProcesses); w.WriteBoolean("devices", safety.Devices);
            w.WriteBoolean("elevation", safety.Elevation);
            if (safety.Notes is null) w.WriteNull("notes"); else w.WriteString("notes", safety.Notes);
            w.WriteEndObject();
            if (fixture.Conditions is null) w.WriteNull("conditions"); else w.WriteString("conditions", fixture.Conditions);
            w.WriteStartObject("metadata");
            foreach (var kv in (fixture.Metadata ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ContractFingerprintPrefix + ScreenSnapshot.Hash(buffer.ToArray());
    }

    /// <summary>The first checkpoint of <paramref name="scenario"/> that does not name an advertised checkpoint expectation of the
    /// fixture-only registration <paramref name="fixture"/>, as an actionable message; null when every checkpoint binds.</summary>
    public static string? UnboundCheckpoint(ScenarioDescriptor scenario, ScenarioDescriptor fixture)
    {
        var advertised = fixture.Contract?.Checkpoints ?? [];
        return scenario.Steps.FirstOrDefault(x => x.Kind == ScenarioStep.Checkpoint && !advertised.Contains(x.Name, StringComparer.Ordinal)) is { } step
            ? $"Scenario '{scenario.Id}': checkpoint '{step.Name}' does not bind to an advertised checkpoint expectation of fixture-only registration "
                + $"'{fixture.Id}' (advertised: {string.Join(", ", advertised)})."
            : null;
    }

    /// <summary>Validate literal cells against each selected execution grid, including ordered resizes, before launch.</summary>
    internal static void ValidateExecution(ScenarioDescriptor scenario, string size)
    {
        if (scenario.External is null) return;
        var (w, h) = ScenarioSelection.ParseSize(size);
        foreach (var step in scenario.Steps)
        {
            if (step.Kind == ScenarioStep.Resize) (w, h) = ScenarioSelection.ParseSize(step.Name);
            if (step.Kind == ScenarioStep.Mouse && InputSteps.TryParseMouse(step.Name, out var mouse) && mouse.At.Name is null)
            {
                int x = mouse.At.X < 0 ? w + mouse.At.X : mouse.At.X, y = mouse.At.Y < 0 ? h + mouse.At.Y : mouse.At.Y;
                if (x < 0 || y < 0 || x >= w || y >= h) throw new ExternalScenarioException(scenario.External.Source, $"Mouse cell outside {w}x{h}: {step.Name}.");
            }
            foreach (var a in step.Assertions ?? [])
                if (a.X is { } x && a.Y is { } y && (x >= w || y >= h || x + (a.Width ?? a.Text?.Length ?? 1) > w))
                    throw new ExternalScenarioException(scenario.External.Source, $"Assertion '{a.Id}' cells outside {w}x{h}.");
        }
    }

    private static ScenarioStep ParseStep(JsonElement s)
    {
        string kind = Text(s, "kind");
        string[] specific = kind switch
        {
            "key" or "keyDown" or "keyUp" => ["key"], "text" => ["text"], "modifiers" => ["modifier", "state"],
            "mouse" => ["input"], "release" => [], "resize" => ["width", "height"], "action" => ["adapter"],
            "checkpoint" => ["id", "evidence", "assertions", "retry"], _ => throw new ArgumentException("Unknown action kind '" + kind + "'.")
        };
        Fields(s, "step", ["kind", "note", .. specific]);
        string name = kind switch
        {
            "key" or "keyDown" or "keyUp" => Text(s, "key"), "text" => Text(s, "text"),
            "modifiers" => Text(s, "modifier") + "-" + Text(s, "state"), "mouse" => Text(s, "input"), "release" => "all",
            "resize" => ScenarioSelection.Format(Number(s, "width"), Number(s, "height")), "action" => Text(s, "adapter"), _ => Text(s, "id")
        };
        string mapped = kind switch { "keyDown" => ScenarioStep.KeyDown, "keyUp" => ScenarioStep.KeyUp, "modifiers" => ScenarioStep.Modifier, _ => kind };
        if (kind != "checkpoint") return new(mapped, name, Optional(s, "note"));
        var assertions = s.TryGetProperty("assertions", out var array) ? array.EnumerateArray().Select(ParseAssertion).ToArray() : [];
        var retry = new CheckpointRetry();
        if (s.TryGetProperty("retry", out var r))
        {
            Fields(r, "retry", "attempts", "intervalMilliseconds");
            retry = new(Number(r, "attempts"), Number(r, "intervalMilliseconds"));
        }
        return new(mapped, name, Optional(s, "note"), s.TryGetProperty("evidence", out var e) ? Strings(e) : null, assertions) { Retry = retry };
    }

    // Explicit finite assertion contract. The JSON format never deserializes a ScenarioStep or CLR type name.
    private static AssertionSpec ParseAssertion(JsonElement r)
    {
        string kind = Text(r, "kind");
        string[] fields = kind switch
        {
            "view-exists" or "view-absent" or "view-visible" or "view-focused" or "within-screen" or "not-clipped" or "modal" => ["view"],
            "within-view" or "disjoint" => ["view", "other"], "aligned" => ["view", "other", "edge"],
            "spacing" => ["view", "other", "edge", "min", "max"], "shadow" => ["view", "value"],
            "menu-depth" or "status-context" => ["value"], "menu-selected" => ["text", "level"],
            "menu-item-state" => ["text", "state", "level"], "submenu-open" or "status-hint" or "screen-contains" => ["text"],
            "status-item" => ["text", "state"], "text-at" => ["x", "y", "text"], "attribute-at" => ["x", "y", "width", "value"],
            AssertionKinds.LayoutAudit => ["view", "profile", "rules", "suppress"],
            _ => throw new ArgumentException("Unknown assertion kind '" + kind + "'.")
        };
        Fields(r, "assertion", ["id", "kind", "severity", .. fields]);
        if (kind == AssertionKinds.LayoutAudit && r.TryGetProperty("suppress", out var items) && items.ValueKind == JsonValueKind.Array)
        { int n = 0; foreach (var item in items.EnumerateArray()) Fields(item, $"layout-audit suppress[{n++}]", "rule", "view", "reason"); }
        var d = JsonSerializer.Deserialize<AssertionDocument>(r.GetRawText(), Json)!;
        // Severity omitted: error, except layout-audit (warning: its findings never fail a scenario unless raised to error).
        string? severity = r.TryGetProperty("severity", out _) ? d.Severity : kind == AssertionKinds.LayoutAudit ? AssertionSeverity.Warning : AssertionSeverity.Error;
        if (kind == AssertionKinds.LayoutAudit) return LayoutAudit(r, d, severity);
        foreach (string property in fields)
        {
            if (property is "level" or "width" || (property == "other" && kind is "within-view" or "aligned") || (property == "view" && kind == "modal")) continue;
            if (!r.TryGetProperty(property, out var required) || required.ValueKind == JsonValueKind.Null) throw new ArgumentException($"{kind} requires '{property}'.");
        }
        foreach (var selector in new[] { d.View, d.Other }.OfType<SelectorDocument>())
            if (new[] { selector.Type, selector.Id, selector.Text, selector.Path }.All(string.IsNullOrWhiteSpace)) throw new ArgumentException("Selector must contain a nonempty type, id, text or path.");
        if (d.State is { } state && !(kind == "status-item" ? new[] { "visible", "absent", "enabled", "disabled" } : new[] { "enabled", "disabled" }).Contains(state)) throw new ArgumentException("Invalid assertion state.");
        if (d.Edge is { } edge && !(kind == "spacing" ? new[] { "horizontal", "vertical" } : new[] { "left", "right", "top", "bottom", "center-x", "center-y" }).Contains(edge)) throw new ArgumentException("Invalid assertion edge/axis.");
        if (d.X is < 0 or >= GridLimits.MaxWidth || d.Y is < 0 or >= GridLimits.MaxHeight || d.Width is <= 0 or > GridLimits.MaxWidth || d.Level is < 0 or > 32 || d.Min > d.Max || (kind == "menu-depth" && d.Value is < 0 or > 32)) throw new ArgumentException("Invalid assertion coordinates, size, depth or range.");
        if (fields.Contains("text") && string.IsNullOrEmpty(d.Text)) throw new ArgumentException("Assertion text must be nonempty.");
        return new(d.Id, d.Kind, severity!, d.View?.ToSelector(), d.Other?.ToSelector(), d.Text, d.State, d.Edge, d.X, d.Y, d.Width, d.Value, d.Min, d.Max, d.Level);
    }

    /// <summary>Limits of the layout-audit fields (schema v1).</summary>
    public const int MaxLayoutRules = 32, MaxLayoutSuppressions = 32, MaxSuppressionReason = 200;

    // Layout-audit: view (optional scope selector), profile (geometry|ux|strict), rules (1..32 unique known IDs of the
    // profile), suppress (≤32 {rule, view?, reason}). Explicit nulls are refused (omit the field instead).
    private static AssertionSpec LayoutAudit(JsonElement r, AssertionDocument d, string? severity)
    {
        foreach (string field in new[] { "view", "profile", "rules", "suppress" })
            if (r.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Null) throw new ArgumentException($"layout-audit '{field}' cannot be null; omit it for the default.");
        string profile = d.Profile ?? LayoutProfiles.Default;
        if (!LayoutProfiles.All.Contains(profile)) throw new ArgumentException($"layout-audit profile must be {string.Join(", ", LayoutProfiles.All)}.");
        if (d.View is { } scope && new[] { scope.Type, scope.Id, scope.Text, scope.Path }.All(string.IsNullOrWhiteSpace)) throw new ArgumentException("Selector must contain a nonempty type, id, text or path.");
        void Rule(string? rule, string at)
        {
            if (rule is null || !LayoutRules.IsKnown(rule)) throw new ArgumentException($"{at}: unknown layout rule '{rule}'.");
            if (!LayoutRules.InProfile(rule, profile)) throw new ArgumentException($"{at}: rule '{rule}' is not part of profile {profile}.");
        }
        if (d.Rules is { } rules)
        {
            if (rules.Length is 0 or > MaxLayoutRules) throw new ArgumentException($"layout-audit rules: 1 to {MaxLayoutRules} rule IDs.");
            if (rules.Distinct(StringComparer.Ordinal).Count() != rules.Length) throw new ArgumentException("layout-audit rules must be unique.");
            foreach (string rule in rules) Rule(rule, "layout-audit rules");
        }
        LayoutSuppression[]? suppress = null;
        if (r.TryGetProperty("suppress", out var array))
        {
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxLayoutSuppressions) throw new ArgumentException($"layout-audit suppress: at most {MaxLayoutSuppressions} objects.");
            var list = new List<LayoutSuppression>(); int i = 0;
            foreach (var item in array.EnumerateArray())
            {
                string at = $"layout-audit suppress[{i}]";
                Fields(item, at, "rule", "view", "reason");
                var s = d.Suppress![i++];
                Rule(s.Rule, at);
                if (item.TryGetProperty("view", out var sv) && sv.ValueKind == JsonValueKind.Null) throw new ArgumentException(at + ": 'view' cannot be null; omit it to suppress the rule everywhere.");
                if (s.View is { } sel && new[] { sel.Type, sel.Id, sel.Text, sel.Path }.All(string.IsNullOrWhiteSpace)) throw new ArgumentException(at + ": selector must contain a nonempty type, id, text or path.");
                if (string.IsNullOrWhiteSpace(s.Reason) || s.Reason.Length > MaxSuppressionReason) throw new ArgumentException($"{at}: 'reason' is required (nonempty, at most {MaxSuppressionReason} characters).");
                list.Add(new LayoutSuppression(s.Rule!, s.View?.ToSelector(), s.Reason));
            }
            suppress = list.ToArray();
        }
        return new AssertionSpec(d.Id, d.Kind, severity!, d.View?.ToSelector()) { Profile = d.Profile, Rules = d.Rules, Suppress = suppress };
    }

    private sealed record SelectorDocument(string? Type = null, string? Id = null, string? Text = null, string? Path = null)
    { public ViewSelector ToSelector() => new(Type, Id, Text, Path); }
    private sealed record SuppressionDocument(string? Rule = null, SelectorDocument? View = null, string? Reason = null);
    private sealed record AssertionDocument(string Id, string Kind, string? Severity = null, SelectorDocument? View = null,
        SelectorDocument? Other = null, string? Text = null, string? State = null, string? Edge = null, int? X = null, int? Y = null,
        int? Width = null, int? Value = null, int? Min = null, int? Max = null, int? Level = null,
        string? Profile = null, string[]? Rules = null, SuppressionDocument[]? Suppress = null);

    private static string Text(JsonElement e, string key) => Optional(e, key) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : throw new ArgumentException("Missing or empty '" + key + "'.");
    private static string? Optional(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetString() ?? throw new ArgumentException("Null '" + key + "'.") : null;
    private static int Number(JsonElement e, string key) => e.GetProperty(key).GetInt32();
    private static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString() ?? throw new ArgumentException("Null array entry.")).ToArray();
    private static void Fields(JsonElement e, string at, params string[] allowed)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new ArgumentException(at + ": expected object.");
        foreach (var p in e.EnumerateObject()) if (!allowed.Contains(p.Name)) throw new ArgumentException(at + ": unknown property '" + p.Name + "'.");
    }
    private static void Unique(JsonElement e, string at)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new ArgumentException(at + ": duplicate JSON property '" + p.Name + "'.");
                Unique(p.Value, at + "." + p.Name);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array) { int i = 0; foreach (var v in e.EnumerateArray()) Unique(v, at + $"[{i++}]"); }
    }
}
