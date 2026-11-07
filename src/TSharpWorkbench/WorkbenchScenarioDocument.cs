using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpWorkbench.Testing;

// The editor document model of an external JSON scenario (schema tsharpworkbench.scenario v1).
//
// The model is the JSON tree itself, not the runtime ScenarioDescriptor: the header is an ordered list of the root's
// properties and every step is its own JSON object. Nothing the file contains is dropped or re-derived: property order,
// "$schema", notes, explicit defaults ("severity": "error", "view": null), unknown (invalid) properties and number
// spellings stay exactly as read until the user edits that very value. Writing re-indents (the file's own newline,
// indentation unit, BOM and final newline are kept), so an unedited document is never rewritten and an edited one differs
// only in edited values and, for hand-formatted files, in whitespace. A document the model cannot represent without loss
// (malformed JSON, comments, duplicate properties, non-object steps, another schema or version, a tree that does not
// reproduce itself) is opened read-only with its problems and is never written.
//
// Validation is the runtime's: ExternalScenarios.LoadFromBytes (= ExternalScenarios.Load semantics) is authoritative and
// gates every save; the editor's own checks only add locations ($.steps[i].field) and friendlier messages. Fixture binding
// is ExternalScenarios.Merge against the target's advertised raw catalog when it is known. Saving writes a temporary file
// beside the target, loads it with ExternalScenarios.Load, re-checks that the file on disk is still the one that was opened
// (content hash), then replaces it atomically. No baseline, project or catalog is changed here.

/// <summary>Severity of a <see cref="ScenarioDocumentProblem"/>. Errors stop saving; warnings do not.</summary>
internal enum ScenarioProblemSeverity { Error, Warning }

/// <summary>One validation finding. <see cref="Location"/> is a JSON path such as <c>$.steps[3].key</c> or <c>$.title</c>;
/// <see cref="Step"/> is the zero-based step index when the finding belongs to a step.</summary>
internal sealed record ScenarioDocumentProblem(ScenarioProblemSeverity Severity, string Location, int? Step, string Message)
{
    internal string Display => (Severity == ScenarioProblemSeverity.Error ? "Error   " : "Warning ") + Location + "  " + Message;
}

/// <summary>Fixture binding context for validation and pickers: the target's raw catalog (null = not discovered yet) and
/// the project's explicit legacy-fixture allowance.</summary>
internal sealed record ScenarioBindingContext(ScenarioDescriptor[]? Compiled, IReadOnlyCollection<string>? LegacyFixtures = null)
{
    internal static readonly ScenarioBindingContext Unknown = new(null);
    internal ScenarioDescriptor? Fixture(string? id) => id is null ? null : Compiled?.FirstOrDefault(s => s.Id == id && s.External is null);
}

/// <summary>Result of <see cref="ScenarioDocument.Save"/>. When <see cref="Saved"/> is false nothing was written.</summary>
internal sealed record ScenarioSaveResult(bool Saved, string? Path, string Message, IReadOnlyList<ScenarioDocumentProblem> Problems,
    ExternalScenarioFile? Loaded = null, bool ExternalChange = false, bool NeedsPath = false, bool TargetExists = false)
{
    /// <summary>True when bytes were written (false for "No changes to save" and every refusal).</summary>
    public bool Written { get; init; }
}

/// <summary>The schema-v1 vocabulary, exactly as ExternalScenarios' parser accepts it (verified by EditorTests).</summary>
internal static class ScenarioSchemaV1
{
    internal static readonly string[] HeaderOrder = ["$schema", "schema", "version", "id", "title", "description", "group", "tags", "fixture", "backend", "grid", "expectedOutcome", "steps"];
    internal static readonly string[] StepKinds = ["key", "text", "mouse", "checkpoint", "keyDown", "keyUp", "modifiers", "release", "resize", "action"];
    internal static readonly string[] Evidence = ["view-tree", "menu", "status"];
    internal static readonly string[] Backends = ["diagnostic-logical", "proxy-logical"];
    internal const string ProxyBackend = "proxy-logical";

    /// <summary>The kind-specific fields of a step (every step also allows "kind" and "note").</summary>
    internal static string[] StepFields(string kind) => kind switch
    {
        "key" or "keyDown" or "keyUp" => ["key"], "text" => ["text"], "modifiers" => ["modifier", "state"],
        "mouse" => ["input"], "release" => [], "resize" => ["width", "height"], "action" => ["adapter"],
        "checkpoint" => ["id", "evidence", "assertions", "retry"], _ => []
    };
    internal static bool IsStepKind(string? kind) => kind is not null && StepKinds.Contains(kind);
    internal static string StepTitle(string kind) => kind switch
    {
        "key" => "Key press", "keyDown" => "Key down (hold)", "keyUp" => "Key up (release held key)", "text" => "Type text",
        "modifiers" => "Modifier down/up", "mouse" => "Mouse input", "release" => "Release all held input", "resize" => "Resize grid",
        "action" => "Fixture action (diagnostic callback)", "checkpoint" => "Checkpoint (capture + assertions)", _ => kind
    };

    internal static readonly string[] AssertionKinds = ["screen-contains", "text-at", "attribute-at", "view-exists", "view-absent", "view-visible", "view-focused",
        "modal", "within-screen", "not-clipped", "within-view", "disjoint", "aligned", "spacing", "shadow", "menu-depth", "menu-selected", "menu-item-state",
        "submenu-open", "status-item", "status-context", "status-hint", "layout-audit"];

    /// <summary>The kind-specific fields of an assertion (every assertion also allows "id", "kind" and "severity").</summary>
    internal static string[] AssertionFields(string kind) => kind switch
    {
        "view-exists" or "view-absent" or "view-visible" or "view-focused" or "within-screen" or "not-clipped" or "modal" => ["view"],
        "within-view" or "disjoint" => ["view", "other"], "aligned" => ["view", "other", "edge"],
        "spacing" => ["view", "other", "edge", "min", "max"], "shadow" => ["view", "value"],
        "menu-depth" or "status-context" => ["value"], "menu-selected" => ["text", "level"],
        "menu-item-state" => ["text", "state", "level"], "submenu-open" or "status-hint" or "screen-contains" => ["text"],
        "status-item" => ["text", "state"], "text-at" => ["x", "y", "text"], "attribute-at" => ["x", "y", "width", "value"],
        "layout-audit" => ["view", "profile", "rules", "suppress"],
        _ => []
    };
    /// <summary>Whether <paramref name="field"/> must be present and non-null for <paramref name="kind"/> (parser rule).</summary>
    internal static bool AssertionFieldRequired(string kind, string field) =>
        !(field is "level" or "width" or "profile" or "rules" or "suppress" || (field == "other" && kind is "within-view" or "aligned") || (field == "view" && kind is "modal" or "layout-audit"));
    internal static string[] AssertionStates(string kind) => kind == "status-item" ? ["visible", "absent", "enabled", "disabled"] : ["enabled", "disabled"];
    internal static string[] AssertionEdges(string kind) => kind == "spacing" ? ["horizontal", "vertical"] : ["left", "right", "top", "bottom", "center-x", "center-y"];
    internal static bool IsNumberField(string field) => field is "x" or "y" or "width" or "value" or "min" or "max" or "level";
    internal static bool IsSelectorField(string field) => field is "view" or "other";
    /// <summary>Severity the runtime parser assumes when an assertion omits it (warning for layout-audit only).</summary>
    internal static string DefaultSeverity(string kind) => kind == "layout-audit" ? "warning" : "error";
}

/// <summary>
/// One step of a scenario document: an immutable copy of its JSON object (property order and unedited values preserved).
/// Use the factories (<see cref="Key"/>, <see cref="Text"/>, <see cref="Mouse(string)"/>, ...) to make new steps; they
/// validate their input with the runtime's own grammar and emit exactly schema-v1 syntax ("kind" first).
/// </summary>
internal sealed class ScenarioDocumentStep
{
    private readonly JsonObject json;
    internal ScenarioDocumentStep(JsonObject json) { this.json = (JsonObject)json.DeepClone(); }

    /// <summary>The step's "kind" ("key", "text", "mouse", "checkpoint", ...), or "" when missing or not a string.</summary>
    public string Kind => String("kind") ?? "";
    public string? Note => String("note");
    /// <summary>The step's JSON as a new object (a copy; changing it does not change the step).</summary>
    public JsonObject ToJson() => (JsonObject)json.DeepClone();
    public IEnumerable<string> PropertyNames => json.Select(p => p.Key);
    public bool Has(string field) => json.ContainsKey(field);
    /// <summary>A copy of a field's node (null for a missing field or JSON null).</summary>
    public JsonNode? Node(string field) => json.TryGetPropertyValue(field, out var v) ? v?.DeepClone() : null;
    /// <summary>A field as text: strings as is, numbers and literals as their JSON spelling, null when absent.</summary>
    public string? String(string field) => json.TryGetPropertyValue(field, out var v) && v is JsonValue value
        ? value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString() : null;
    /// <summary>The value the runtime uses as the step name (ScenarioStep.Name), e.g. "kbF10", "click-left@3,0", "80x25".</summary>
    public string Name => Kind switch
    {
        "key" or "keyDown" or "keyUp" => String("key") ?? "", "text" => String("text") ?? "", "modifiers" => (String("modifier") ?? "") + "-" + (String("state") ?? ""),
        "mouse" => String("input") ?? "", "release" => "all", "resize" => (String("width") ?? "?") + "x" + (String("height") ?? "?"), "action" => String("adapter") ?? "",
        _ => String("id") ?? ""
    };
    public int AssertionCount => json.TryGetPropertyValue("assertions", out var a) && a is JsonArray array ? array.Count : 0;
    /// <summary>One-line parameter summary for lists.</summary>
    public string Parameters => Kind switch
    {
        "text" => "\"" + (String("text") ?? "") + "\"",
        "checkpoint" => (String("id") ?? "?") + (AssertionCount > 0 ? $"  ({AssertionCount} assertion{(AssertionCount == 1 ? "" : "s")})" : "  (capture only)")
            + (Has("retry") ? "  retry" : "") + (Has("evidence") ? "  evidence" : ""),
        "release" => "all held input",
        "modifiers" => (String("modifier") ?? "?") + " " + (String("state") ?? "?"),
        _ => Name
    };

    /// <summary>A copy with <paramref name="field"/> set (null node = JSON null). An existing field keeps its position.</summary>
    public ScenarioDocumentStep With(string field, JsonNode? value)
    {
        var copy = (JsonObject)json.DeepClone();
        copy[field] = value?.DeepClone();
        return new(copy);
    }
    /// <summary>A copy without <paramref name="field"/>.</summary>
    public ScenarioDocumentStep Without(string field)
    {
        var copy = (JsonObject)json.DeepClone();
        copy.Remove(field);
        return new(copy);
    }
    /// <summary>A copy whose text field differs only when <paramref name="value"/> differs from the current text, so an
    /// unedited value keeps its original JSON spelling. Empty or null removes the field.</summary>
    public ScenarioDocumentStep WithText(string field, string? value) =>
        string.IsNullOrEmpty(value) ? Has(field) ? Without(field) : this : String(field) == value && json[field] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? this : With(field, JsonValue.Create(value));
    /// <summary>As <see cref="WithText"/> for an integer field.</summary>
    public ScenarioDocumentStep WithNumber(string field, int? value) =>
        value is null ? Has(field) ? Without(field) : this
        : json[field] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int current) && current == value ? this : With(field, JsonValue.Create(value.Value));

    public override string ToString() => json.ToJsonString();
    internal bool SameJson(ScenarioDocumentStep other) => JsonNode.DeepEquals(json, other.json);

    // ── factories (schema v1 syntax; every value is checked with the runtime's grammar) ──────────────────────────

    private static ScenarioDocumentStep Make(string kind, string? note, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["kind"] = kind };
        foreach (var (key, value) in fields) o[key] = value;
        if (!string.IsNullOrEmpty(note)) o["note"] = note;
        return new(o);
    }
    private static string KeyName(string key) => TestKeys.TryResolve(key, out _) ? key
        : throw new ArgumentException($"Unknown key '{key}': use a Keys constant name such as kbEnter, kbF10 or kbAltX.");
    /// <summary><c>{"kind":"key","key":"kbF10"}</c>: one key press (a TSharpVision Keys constant name).</summary>
    public static ScenarioDocumentStep Key(string key, string? note = null) => Make("key", note, ("key", KeyName(key)));
    /// <summary><c>{"kind":"keyDown","key":...}</c>: hold a key (released by keyUp or release).</summary>
    public static ScenarioDocumentStep KeyDown(string key, string? note = null) => Make("keyDown", note, ("key", KeyName(key)));
    /// <summary><c>{"kind":"keyUp","key":...}</c>: release a key held by keyDown.</summary>
    public static ScenarioDocumentStep KeyUp(string key, string? note = null) => Make("keyUp", note, ("key", KeyName(key)));
    /// <summary><c>{"kind":"text","text":"abc"}</c>: 1..48 non-control, non-surrogate characters, one key event each.</summary>
    public static ScenarioDocumentStep Text(string text, string? note = null) => InputSteps.IsValid(InputSteps.Text, text) ? Make("text", note, ("text", text))
        : throw new ArgumentException("Text steps hold 1 to 48 characters without control characters or surrogates; split longer text.");
    /// <summary><c>{"kind":"modifiers","modifier":"shift|ctrl|alt","state":"down|up"}</c>.</summary>
    public static ScenarioDocumentStep Modifier(string modifier, bool down, string? note = null) => modifier is "shift" or "ctrl" or "alt"
        ? Make("modifiers", note, ("modifier", modifier), ("state", down ? "down" : "up")) : throw new ArgumentException("Modifier must be shift, ctrl or alt.");
    /// <summary><c>{"kind":"mouse","input":"click-left@3,0"}</c> with a complete input string in the runtime grammar.</summary>
    public static ScenarioDocumentStep Mouse(string input, string? note = null) => InputSteps.IsValid(InputSteps.Mouse, input) ? Make("mouse", note, ("input", input))
        : throw new ArgumentException($"Invalid mouse input '{input}': use move@x,y, down-B@x,y, up-B@x,y, click-B@x,y, double-B@x,y or wheel-D@x,y[*N] (B left/right/middle, D up/down/left/right, N 1..20).");
    /// <summary>A mouse step at a literal cell: <paramref name="action"/> is move, down-left, up-right, click-middle,
    /// double-left, wheel-up, ... (negative coordinates count from the far edge).</summary>
    public static ScenarioDocumentStep Mouse(string action, int x, int y, int count = 1, string? note = null) => Mouse(MouseInput(action, x, y, count), note);
    /// <summary>The input string of a mouse step at a literal cell, e.g. MouseInput("click-left", 3, 0) = "click-left@3,0".</summary>
    public static string MouseInput(string action, int x, int y, int count = 1) =>
        action + "@" + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + (count == 1 ? "" : "*" + count.ToString(CultureInfo.InvariantCulture));
    /// <summary><c>{"kind":"release"}</c>: releases every held button, key and modifier.</summary>
    public static ScenarioDocumentStep Release(string? note = null) => Make("release", note);
    /// <summary><c>{"kind":"resize","width":w,"height":h}</c>.</summary>
    public static ScenarioDocumentStep Resize(int width, int height, string? note = null) => width is > 0 and <= GridLimits.MaxWidth && height is > 0 and <= GridLimits.MaxHeight
        ? Make("resize", note, ("width", width), ("height", height)) : throw new ArgumentException($"Resize needs 1..{GridLimits.MaxWidth} x 1..{GridLimits.MaxHeight} cells.");
    /// <summary><c>{"kind":"action","adapter":name}</c>: a registered fixture action (fixture-bound documents only).</summary>
    public static ScenarioDocumentStep Action(string adapter, string? note = null) => !string.IsNullOrWhiteSpace(adapter) ? Make("action", note, ("adapter", adapter))
        : throw new ArgumentException("An action step names a registered fixture action.");
    /// <summary><c>{"kind":"checkpoint","id":id}</c> without assertions (a capture; assertions are added explicitly).</summary>
    public static ScenarioDocumentStep Checkpoint(string id, string? note = null) => !string.IsNullOrWhiteSpace(id) ? Make("checkpoint", note, ("id", id))
        : throw new ArgumentException("A checkpoint needs an ID.");
    /// <summary>A step from raw JSON (an object with a "kind"); the editor validates it like any other step.</summary>
    public static ScenarioDocumentStep FromJson(JsonObject json) => new(json);
}

/// <summary>
/// An external scenario document open for editing. Steps are edited through <see cref="Insert"/>, <see cref="Append"/>,
/// <see cref="ReplaceRange"/>, <see cref="Replace"/>, <see cref="Remove"/>, <see cref="Move"/> and <see cref="Duplicate"/>;
/// header values through the typed properties. Every change raises <see cref="Changed"/> and makes it <see cref="Dirty"/>.
/// Not thread-safe: use it on the UI thread.
/// </summary>
internal sealed class ScenarioDocument
{
    private const string StepsKey = "steps";
    private readonly List<KeyValuePair<string, JsonNode?>> header = [];
    private readonly List<ScenarioDocumentStep> steps = [];
    private readonly List<ScenarioDocumentProblem> loadProblems = [];
    private bool bom, indented = true, finalNewline = true;
    private string newLine = Environment.NewLine;
    private char indentCharacter = ' ';
    private int indentSize = 2;
    private int savedRevision;

    private ScenarioDocument() { }

    /// <summary>The file this document was loaded from or last saved to; null for a new, never saved document.</summary>
    public string? Path { get; private set; }
    /// <summary>True when the file cannot be edited without loss; <see cref="ReadOnlyReason"/> says why. Never saved.</summary>
    public bool ReadOnly { get; private set; }
    public string? ReadOnlyReason { get; private set; }
    /// <summary>ScreenSnapshot.Hash of the bytes read or last written (the same value as ExternalScenarioFile.ContentHash).</summary>
    public string? LoadedContentHash { get; private set; }
    /// <summary>Whether serializing the unedited document reproduced the file byte for byte when it was loaded.</summary>
    public bool ByteExact { get; private set; }
    /// <summary>Increments on every change.</summary>
    public int Revision { get; private set; }
    public bool Dirty => Revision != savedRevision;
    public IReadOnlyList<ScenarioDocumentStep> Steps => steps;
    /// <summary>Problems found while reading (read-only reasons); empty for an editable document.</summary>
    public IReadOnlyList<ScenarioDocumentProblem> LoadProblems => loadProblems;
    /// <summary>Raised after every change of steps or header.</summary>
    public event Action? Changed;

    // ── creation ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A new, unsaved document. <paramref name="fixture"/> null: a proxy-logical document for an ordinary
    /// application; otherwise bound to that fixture. The single initial checkpoint is <paramref name="checkpoint"/> when given.</summary>
    public static ScenarioDocument New(string id, string title, string description, string group = "external", string? fixture = null, string? checkpoint = "initial")
    {
        var d = new ScenarioDocument();
        d.header.Add(new("schema", ExternalScenarios.Schema));
        d.header.Add(new("version", 1));
        d.header.Add(new("id", id));
        d.header.Add(new("title", title));
        d.header.Add(new("description", description));
        d.header.Add(new("group", group));
        if (fixture is null) d.header.Add(new("backend", ScenarioSchemaV1.ProxyBackend)); else d.header.Add(new("fixture", fixture));
        d.header.Add(new("expectedOutcome", "passed"));
        d.header.Add(new(StepsKey, null));
        if (checkpoint is { Length: > 0 }) d.steps.Add(ScenarioDocumentStep.Checkpoint(checkpoint));
        d.Revision = 1;   // dirty until saved
        return d;
    }

    /// <summary>Reads <paramref name="path"/>. Content problems never throw: the document is then <see cref="ReadOnly"/>
    /// with <see cref="LoadProblems"/>. A missing or unreadable file throws IOException/UnauthorizedAccessException.</summary>
    public static ScenarioDocument Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var d = new ScenarioDocument { Path = path };
        if (new FileInfo(path).Length > 1024 * 1024) return d.Refuse("$", "The document is larger than 1 MiB; scenario documents are limited to 1 MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        d.LoadedContentHash = ScreenSnapshot.Hash(bytes);
        d.Parse(bytes);
        return d;
    }

    /// <summary>Parses document bytes that are not on disk (tests, recorder previews). <paramref name="path"/> is only an identity.</summary>
    internal static ScenarioDocument FromBytes(string? path, byte[] bytes)
    {
        var d = new ScenarioDocument { Path = path is null ? null : System.IO.Path.GetFullPath(path), LoadedContentHash = ScreenSnapshot.Hash(bytes) };
        d.Parse(bytes);
        return d;
    }

    private ScenarioDocument Refuse(string location, string message)
    {
        ReadOnly = true;
        ReadOnlyReason ??= message;
        loadProblems.Add(new(ScenarioProblemSeverity.Error, location, null, message));
        return this;
    }

    private void Parse(byte[] bytes)
    {
        ReadOnlySpan<byte> bomBytes = [0xef, 0xbb, 0xbf];
        bom = bytes.AsSpan().StartsWith(bomBytes);
        var content = bytes.AsMemory(bom ? 3 : 0);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content.Span); }
        catch (DecoderFallbackException) { Refuse("$", "The file is not valid UTF-8 text."); return; }
        DetectFormat(text);
        JsonNode? root;
        try
        {
            using (var doc = JsonDocument.Parse(content)) { if (Duplicate(doc.RootElement, "$") is { } dup) { Refuse(dup.Location, dup.Message); return; } }
            root = JsonNode.Parse(content.Span);
        }
        catch (JsonException ex)
        {
            Refuse("$", "Not valid JSON" + (ex.LineNumber is { } line ? $" (line {line + 1}, position {ex.BytePositionInLine + 1})" : "") + ": " + FirstSentence(ex.Message)
                + " Comments and trailing commas are not part of schema v1. Fix the file in an external editor; the internal editor never rewrites it.");
            return;
        }
        if (root is not JsonObject obj) { Refuse("$", "Not a scenario document: the JSON root is not an object."); return; }
        if (!obj.TryGetPropertyValue("schema", out var schema) || schema is not JsonValue s || s.GetValueKind() != JsonValueKind.String || s.GetValue<string>() != ExternalScenarios.Schema)
        { Refuse("$.schema", $"Not a scenario document: \"schema\" is not \"{ExternalScenarios.Schema}\"."); return; }
        if (!obj.TryGetPropertyValue("version", out var version) || version is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || !v.TryGetValue(out int number) || number != 1)
        { Refuse("$.version", "Unsupported scenario schema version; this Workbench edits version 1 only."); return; }
        foreach (var (key, value) in obj.ToArray())
        {
            if (key == StepsKey)
            {
                if (value is not JsonArray array) { Refuse("$.steps", "\"steps\" is not an array; the editor cannot show it as a step list without changing the file."); return; }
                int i = 0;
                foreach (var item in array)
                {
                    if (item is not JsonObject step) { Refuse($"$.steps[{i}]", "This step is not a JSON object; the editor cannot represent it without changing the file."); return; }
                    steps.Add(new ScenarioDocumentStep(step)); i++;
                }
                header.Add(new(StepsKey, null));
            }
            else header.Add(new(key, value?.DeepClone()));
        }
        // The model must reproduce the document; otherwise it is not edited (never a lossy write).
        byte[] again = Serialize();
        ByteExact = again.AsSpan().SequenceEqual(bytes);
        if (!ByteExact)
        {
            JsonNode? reread;
            try { reread = JsonNode.Parse(again.AsSpan(bom ? 3 : 0)); }
            catch (JsonException) { reread = null; }
            if (!JsonNode.DeepEquals(root, reread)) { Refuse("$", "The editor cannot reproduce this document exactly; it is shown read-only."); return; }
        }
    }

    private void DetectFormat(string text)
    {
        int lf = text.IndexOf('\n');
        indented = lf >= 0;
        newLine = lf > 0 && text[lf - 1] == '\r' ? "\r\n" : lf >= 0 ? "\n" : Environment.NewLine;
        finalNewline = text.EndsWith('\n');
        if (!indented) return;
        foreach (string line in text.Split('\n').Skip(1))
        {
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            if (n == 0 || n == line.TrimEnd('\r').Length) continue;
            indentCharacter = line[0];
            indentSize = Math.Clamp(line[..n].All(c => c == line[0]) ? n : 2, 1, 8);
            break;
        }
    }
    private static string FirstSentence(string message) { int i = message.IndexOf(". ", StringComparison.Ordinal); return i > 0 ? message[..(i + 1)] : message; }
    private static (string Location, string Message)? Duplicate(JsonElement e, string at)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!names.Add(p.Name)) return (at, $"Duplicate JSON property '{p.Name}': editing would silently keep only one of them. Fix the file in an external editor.");
                if (Duplicate(p.Value, at + "." + p.Name) is { } inner) return inner;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array) { int i = 0; foreach (var v in e.EnumerateArray()) { if (Duplicate(v, at + $"[{i}]") is { } inner) return inner; i++; } }
        return null;
    }

    // ── serialization ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document as file bytes (UTF-8; the file's BOM, newline, indentation and final newline).</summary>
    public byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        if (bom) buffer.Write([0xef, 0xbb, 0xbf]);
        var options = new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = false };
        if (indented) { options.IndentCharacter = indentCharacter; options.IndentSize = indentSize; options.NewLine = newLine; }
        using (var w = new Utf8JsonWriter(buffer, options))
        {
            w.WriteStartObject();
            foreach (var (key, value) in header)
            {
                if (key == StepsKey)
                {
                    w.WriteStartArray(StepsKey);
                    foreach (var step in steps) step.ToJson().WriteTo(w);
                    w.WriteEndArray();
                }
                else { w.WritePropertyName(key); if (value is null) w.WriteNullValue(); else value.WriteTo(w); }
            }
            w.WriteEndObject();
        }
        if (finalNewline) buffer.Write(Encoding.UTF8.GetBytes(newLine));
        return buffer.ToArray();
    }
    public string SerializeText() => Encoding.UTF8.GetString(Serialize()).TrimStart('﻿');

    // ── header ────────────────────────────────────────────────────────────────────────────────────────────

    public IEnumerable<string> HeaderNames => header.Select(h => h.Key);
    private int HeaderIndex(string key) => header.FindIndex(h => h.Key == key);
    /// <summary>A header value as text (strings as is, other values as JSON), or null when absent.</summary>
    public string? HeaderText(string key) => HeaderIndex(key) is >= 0 and var i && header[i].Value is JsonValue v
        ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString() : null;
    public JsonNode? HeaderNode(string key) => HeaderIndex(key) is >= 0 and var i ? header[i].Value?.DeepClone() : null;
    public bool HasHeader(string key) => HeaderIndex(key) >= 0;

    /// <summary>Sets a header property (an existing one keeps its position; a new one is placed in schema order).
    /// Setting an equal value changes nothing, so its original spelling stays.</summary>
    public void SetHeader(string key, JsonNode? value)
    {
        if (key == StepsKey) throw new ArgumentException("Steps are edited through the step methods.");
        int i = HeaderIndex(key);
        if (i >= 0)
        {
            if (JsonNode.DeepEquals(header[i].Value, value)) return;
            header[i] = new(key, value?.DeepClone());
        }
        else
        {
            int order = Array.IndexOf(ScenarioSchemaV1.HeaderOrder, key);
            int at = header.FindIndex(h => Array.IndexOf(ScenarioSchemaV1.HeaderOrder, h.Key) is var o && order >= 0 && o > order);
            header.Insert(at < 0 ? header.Count : at, new(key, value?.DeepClone()));
        }
        Touch();
    }
    /// <summary>Sets a string header value; null or empty removes the property.</summary>
    public void SetHeaderText(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) { RemoveHeader(key); return; }
        if (HeaderText(key) == value && HeaderNode(key) is JsonValue v && v.GetValueKind() == JsonValueKind.String) return;
        SetHeader(key, JsonValue.Create(value));
    }
    public void RemoveHeader(string key)
    {
        if (key == StepsKey) throw new ArgumentException("Steps cannot be removed.");
        int i = HeaderIndex(key);
        if (i < 0) return;
        header.RemoveAt(i); Touch();
    }

    public string Id { get => HeaderText("id") ?? ""; set => SetHeaderText("id", value); }
    public string Title { get => HeaderText("title") ?? ""; set => SetHeaderText("title", value); }
    public string Description { get => HeaderText("description") ?? ""; set => SetHeaderText("description", value); }
    /// <summary>The group as written; null when absent (the runtime then uses "external").</summary>
    public string? Group { get => HeaderText("group"); set => SetHeaderText("group", value); }
    public string? Fixture { get => HeaderText("fixture"); set => SetHeaderText("fixture", value); }
    /// <summary>The backend as written; null when absent (the runtime then uses "diagnostic-logical").</summary>
    public string? Backend { get => HeaderText("backend"); set => SetHeaderText("backend", value); }
    public bool Proxy => Backend == ScenarioSchemaV1.ProxyBackend;
    public string ExpectedOutcome { get => HeaderText("expectedOutcome") ?? ""; set => SetHeaderText("expectedOutcome", value); }
    /// <summary>The tags, or null when the document has no "tags" property. Setting null removes it.</summary>
    public string[]? Tags
    {
        get => HeaderNode("tags") is JsonArray a ? a.Select(t => t is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : t?.ToJsonString() ?? "null").ToArray() : null;
        set
        {
            if (value is null) { RemoveHeader("tags"); return; }
            if (Tags is { } current && current.SequenceEqual(value, StringComparer.Ordinal)) return;
            SetHeader("tags", new JsonArray(value.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()));
        }
    }
    /// <summary>Switches the document kind. Proxy: "backend": "proxy-logical", no fixture. Fixture-bound: the fixture, and
    /// the backend property only if the document already had an explicit "diagnostic-logical".</summary>
    public void SetKind(bool proxy, string? fixture)
    {
        if (proxy) { RemoveHeader("fixture"); Backend = ScenarioSchemaV1.ProxyBackend; return; }
        if (Backend == ScenarioSchemaV1.ProxyBackend) RemoveHeader("backend");
        Fixture = fixture;
    }

    // ── steps ─────────────────────────────────────────────────────────────────────────────────────────────

    private void Touch() { if (ReadOnly) throw new InvalidOperationException("This document is read-only: " + ReadOnlyReason); Revision++; Changed?.Invoke(); }
    private void Writable() { if (ReadOnly) throw new InvalidOperationException("This document is read-only: " + ReadOnlyReason); }

    /// <summary>Inserts <paramref name="items"/> before <paramref name="index"/> (0..Steps.Count).</summary>
    public void Insert(int index, IEnumerable<ScenarioDocumentStep> items)
    {
        Writable();
        if (index < 0 || index > steps.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var list = items.ToList();
        if (list.Count == 0) return;
        steps.InsertRange(index, list); Touch();
    }
    public void Insert(int index, ScenarioDocumentStep step) => Insert(index, [step]);
    public void Append(IEnumerable<ScenarioDocumentStep> items) => Insert(steps.Count, items);
    public void Append(ScenarioDocumentStep step) => Insert(steps.Count, [step]);
    /// <summary>Replaces <paramref name="count"/> steps from <paramref name="start"/> with <paramref name="items"/> (count 0 = insert).</summary>
    public void ReplaceRange(int start, int count, IEnumerable<ScenarioDocumentStep> items)
    {
        Writable();
        if (start < 0 || count < 0 || start + count > steps.Count) throw new ArgumentOutOfRangeException(nameof(start));
        var list = items.ToList();
        if (count == 0 && list.Count == 0) return;
        steps.RemoveRange(start, count); steps.InsertRange(start, list); Touch();
    }
    public void Replace(int index, ScenarioDocumentStep step)
    {
        Writable();
        if (steps[index].SameJson(step)) return;
        steps[index] = step; Touch();
    }
    public void Remove(int index, int count = 1) => ReplaceRange(index, count, []);
    /// <summary>Moves <paramref name="count"/> steps starting at <paramref name="start"/> by <paramref name="delta"/> positions
    /// (negative = up). Returns the new start.</summary>
    public int Move(int start, int count, int delta)
    {
        Writable();
        if (start < 0 || count <= 0 || start + count > steps.Count) throw new ArgumentOutOfRangeException(nameof(start));
        int target = Math.Clamp(start + delta, 0, steps.Count - count);
        if (target == start) return start;
        var block = steps.GetRange(start, count);
        steps.RemoveRange(start, count); steps.InsertRange(target, block); Touch();
        return target;
    }
    /// <summary>Inserts a copy of <paramref name="count"/> steps after them. Returns the start of the copies.</summary>
    public int Duplicate(int start, int count = 1)
    {
        Writable();
        if (start < 0 || count <= 0 || start + count > steps.Count) throw new ArgumentOutOfRangeException(nameof(start));
        var copies = steps.GetRange(start, count).Select(s => new ScenarioDocumentStep(s.ToJson())).ToList();
        steps.InsertRange(start + count, copies); Touch();
        return start + count;
    }

    // ── validation ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>All problems: read-only reasons, located structural checks, the runtime's authoritative verdict
    /// (ExternalScenarios.LoadFromBytes) and, when the target's catalog is known, fixture binding (ExternalScenarios.Merge).</summary>
    public IReadOnlyList<ScenarioDocumentProblem> Validate(ScenarioBindingContext? binding = null) => Check(binding, out _);

    /// <summary>As <see cref="Validate"/>; <paramref name="parsed"/> is the runtime's parse when it accepted the document.</summary>
    internal IReadOnlyList<ScenarioDocumentProblem> Check(ScenarioBindingContext? binding, out ExternalScenarioFile? parsed)
    {
        parsed = null;
        if (ReadOnly) return loadProblems.ToArray();
        var problems = new List<ScenarioDocumentProblem>();
        StructuralChecks(problems, binding);
        string identity = Path ?? System.IO.Path.GetFullPath("untitled.scenario.json");
        try
        {
            parsed = ExternalScenarios.LoadFromBytes(identity, Serialize());
            // The runtime accepted the document: an editor check that disagrees is advisory only.
            for (int i = 0; i < problems.Count; i++) if (problems[i].Severity == ScenarioProblemSeverity.Error) problems[i] = problems[i] with { Severity = ScenarioProblemSeverity.Warning };
        }
        catch (ExternalScenarioException ex)
        {
            string message = ScenarioFiles.Detail(ex, identity);
            if (!problems.Any(p => p.Severity == ScenarioProblemSeverity.Error)) problems.Add(Locate(message));
            else if (!problems.Any(p => message.Contains(p.Message, StringComparison.Ordinal)))
                problems.Add(Locate(message) with { Message = "Runtime check: " + Locate(message).Message });
        }
        if (parsed is not null && !Proxy)
        {
            string fixture = parsed.Fixture;
            if (binding?.Compiled is not { } compiled)
                problems.Add(new(ScenarioProblemSeverity.Warning, "$.fixture", null, $"Fixture binding of '{fixture}' is not checked yet: refresh Scenario Manager (F2) with the diagnostic target selected."));
            else
            {
                try { ExternalScenarios.Merge(compiled, [parsed], binding.LegacyFixtures); }
                catch (ExternalScenarioException ex)
                {
                    string message = ScenarioFiles.Detail(ex, parsed.Path);
                    if (!compiled.Any(s => s.Id == fixture)) message = $"The target does not advertise the fixture '{fixture}'.";
                    problems.Add(Locate(message) with { Severity = ScenarioProblemSeverity.Warning, Message = "Binding: " + Locate(message).Message });
                }
            }
        }
        return problems;
    }

    // Where a runtime message belongs: "$.steps[i]: ..." prefixes, step names ('name') and header fields.
    private ScenarioDocumentProblem Locate(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"^\$\.steps\[(\d+)\]: ?(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out int index))
            return new(ScenarioProblemSeverity.Error, $"$.steps[{index}]", index, m.Groups[2].Value);
        var field = System.Text.RegularExpressions.Regex.Match(message, @"^(\$\.[A-Za-z]+): ?(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (field.Success) return new(ScenarioProblemSeverity.Error, field.Groups[1].Value, null, field.Groups[2].Value);
        var named = System.Text.RegularExpressions.Regex.Match(message, @"'([^']+)'");
        if (named.Success)
        {
            string name = named.Groups[1].Value;
            bool duplicateCheckpoint = message.StartsWith("Duplicate checkpoint", StringComparison.Ordinal);
            int hit = -1, seen = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Name != name && steps[i].String("adapter") != name && !(steps[i].Kind == "mouse" && steps[i].Name.EndsWith(":" + name, StringComparison.Ordinal))) continue;
                if (duplicateCheckpoint && seen++ == 0) continue;
                hit = i; break;
            }
            if (hit >= 0) return new(ScenarioProblemSeverity.Error, $"$.steps[{hit}]", hit, message);
        }
        return new(ScenarioProblemSeverity.Error, "$", null, message);
    }

    private void StructuralChecks(List<ScenarioDocumentProblem> problems, ScenarioBindingContext? binding)
    {
        void Error(string at, int? step, string message) => problems.Add(new(ScenarioProblemSeverity.Error, at, step, message));
        foreach (var (key, _) in header)
            if (!ScenarioSchemaV1.HeaderOrder.Contains(key)) Error("$." + key, null, $"Unknown property '{key}' (schema v1 rejects unknown properties).");
        foreach (string required in new[] { "id", "title", "description", "expectedOutcome" })
            if (string.IsNullOrWhiteSpace(HeaderText(required))) Error("$." + required, null, $"'{required}' is required and must be a nonempty string.");
        if (Backend is { } backend && !ScenarioSchemaV1.Backends.Contains(backend)) Error("$.backend", null, "Unsupported backend: use proxy-logical or diagnostic-logical.");
        if (!Proxy && string.IsNullOrWhiteSpace(Fixture)) Error("$.fixture", null, "A fixture-bound document names the diagnostic fixture it binds to.");
        if (Proxy && HasHeader("fixture")) Error("$.fixture", null, "Black-Box Mode documents cannot bind a compiled fixture; remove the fixture.");
        if (Id.Length > 0 && Id == Fixture) Error("$.id", null, "The scenario ID must differ from its fixture ID.");
        if (ExpectedOutcome is { Length: > 0 } outcome && outcome is not ("passed" or "failed")) Error("$.expectedOutcome", null, "Use passed or failed.");
        if (Tags is { } tags && (tags.Length > 16 || tags.Distinct(StringComparer.Ordinal).Count() != tags.Length || tags.Any(string.IsNullOrWhiteSpace)))
            Error("$.tags", null, "At most 16 unique nonempty tags.");
        if (steps.Count == 0) Error("$.steps", null, "A scenario needs at least one step; add a checkpoint.");
        var fixture = binding?.Fixture(Fixture);
        var checkpointIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i]; string at = $"$.steps[{i}]";
            if (!ScenarioSchemaV1.IsStepKind(s.Kind)) { Error(at + ".kind", i, s.Kind.Length == 0 ? "Missing step kind." : $"Unknown step kind '{s.Kind}'."); continue; }
            var allowed = ScenarioSchemaV1.StepFields(s.Kind);
            foreach (string p in s.PropertyNames) if (p is not ("kind" or "note") && !allowed.Contains(p)) Error($"{at}.{p}", i, $"'{p}' is not a field of a {s.Kind} step.");
            string? Required(string field) { var v = s.String(field); if (string.IsNullOrWhiteSpace(v)) Error($"{at}.{field}", i, $"Missing or empty '{field}'."); return v; }
            switch (s.Kind)
            {
                case "key" or "keyDown" or "keyUp":
                    if (Required("key") is { Length: > 0 } key && !TestKeys.TryResolve(key, out _)) Error(at + ".key", i, $"Unknown key '{key}': use a Keys constant name such as kbEnter or kbF10.");
                    break;
                case "text":
                    if (Required("text") is { Length: > 0 } text && !InputSteps.IsValid(InputSteps.Text, text)) Error(at + ".text", i, "Text holds 1 to 48 characters without control characters.");
                    break;
                case "modifiers":
                    if (Required("modifier") is { Length: > 0 } modifier && modifier is not ("shift" or "ctrl" or "alt")) Error(at + ".modifier", i, "Use shift, ctrl or alt.");
                    if (Required("state") is { Length: > 0 } state && state is not ("down" or "up")) Error(at + ".state", i, "Use down or up.");
                    break;
                case "mouse":
                    if (Required("input") is { Length: > 0 } input)
                    {
                        if (!InputSteps.IsValid(InputSteps.Mouse, input)) Error(at + ".input", i, "Invalid mouse input: move@x,y, down-B@x,y, up-B@x,y, click-B@x,y, double-B@x,y or wheel-D@x,y[*N].");
                        else if (input.Contains("@:", StringComparison.Ordinal))
                        {
                            string point = input[(input.IndexOf("@:", StringComparison.Ordinal) + 2)..].Split('*')[0];
                            if (Proxy) Error(at + ".input", i, "Proxy documents cannot use named points; use x,y cells.");
                            else if (fixture?.Adapters is { } adapters && !adapters.Points.Contains(point)) problems.Add(new(ScenarioProblemSeverity.Warning, at + ".input", i, $"Point ':{point}' is not advertised by fixture '{fixture.Id}'."));
                        }
                    }
                    break;
                case "resize":
                    foreach (string f in new[] { "width", "height" })
                        if (s.Node(f) is not JsonValue n || n.GetValueKind() != JsonValueKind.Number || !n.TryGetValue(out int cells) || cells <= 0) Error($"{at}.{f}", i, $"'{f}' must be a positive integer.");
                    break;
                case "action":
                    if (Required("adapter") is { Length: > 0 } adapter)
                    {
                        if (Proxy) Error(at + ".adapter", i, "Proxy documents cannot use fixture actions.");
                        else if (fixture?.Adapters is { } adapters && !adapters.Actions.Contains(adapter)) problems.Add(new(ScenarioProblemSeverity.Warning, at + ".adapter", i, $"Action '{adapter}' is not advertised by fixture '{fixture.Id}'."));
                    }
                    break;
                case "checkpoint":
                    if (Required("id") is { Length: > 0 } id && !checkpointIds.Add(id)) Error(at + ".id", i, $"Duplicate checkpoint ID '{id}'.");
                    if (s.Node("evidence") is { } evidence)
                    {
                        if (evidence is not JsonArray list) Error(at + ".evidence", i, "Evidence is an array of view-tree, menu, status.");
                        else foreach (var e in list) if (e is not JsonValue ev || ev.GetValueKind() != JsonValueKind.String || !ScenarioSchemaV1.Evidence.Contains(ev.GetValue<string>())) Error(at + ".evidence", i, "Evidence values are view-tree, menu, status.");
                    }
                    if (s.Node("retry") is { } retry)
                    {
                        if (retry is not JsonObject r) Error(at + ".retry", i, "Retry is an object with attempts and intervalMilliseconds.");
                        else foreach (var p in r) if (p.Key is not ("attempts" or "intervalMilliseconds")) Error($"{at}.retry.{p.Key}", i, $"'{p.Key}' is not a retry field.");
                    }
                    if (s.Node("assertions") is { } assertions)
                    {
                        if (assertions is not JsonArray list) { Error(at + ".assertions", i, "Assertions is an array."); break; }
                        var ids = new HashSet<string>(StringComparer.Ordinal);
                        int j = 0;
                        foreach (var a in list)
                        {
                            string aat = $"{at}.assertions[{j++}]";
                            if (a is not JsonObject o) { Error(aat, i, "An assertion is an object."); continue; }
                            string kind = o["kind"] is JsonValue k && k.GetValueKind() == JsonValueKind.String ? k.GetValue<string>() : "";
                            if (!ScenarioSchemaV1.AssertionKinds.Contains(kind)) { Error(aat + ".kind", i, kind.Length == 0 ? "Missing assertion kind." : $"Unknown assertion kind '{kind}'."); continue; }
                            string? aid = o["id"] is JsonValue idv && idv.GetValueKind() == JsonValueKind.String ? idv.GetValue<string>() : null;
                            if (string.IsNullOrWhiteSpace(aid)) Error(aat + ".id", i, "An assertion needs an ID.");
                            else if (!ids.Add(aid)) Error(aat + ".id", i, $"Duplicate assertion ID '{aid}' in this checkpoint.");
                            var fields = ScenarioSchemaV1.AssertionFields(kind);
                            foreach (var p in o) if (p.Key is not ("id" or "kind" or "severity") && !fields.Contains(p.Key)) Error($"{aat}.{p.Key}", i, $"'{p.Key}' is not a field of a {kind} assertion.");
                            foreach (string f in fields)
                                if (ScenarioSchemaV1.AssertionFieldRequired(kind, f) && (!o.TryGetPropertyValue(f, out var fv) || fv is null)) Error($"{aat}.{f}", i, $"{kind} requires '{f}'.");
                        }
                    }
                    break;
            }
        }
    }

    // ── saving ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the file on disk differs from the bytes this document was read from or last wrote (a deleted file
    /// counts as changed). False for a document that was never on disk.</summary>
    public bool ChangedOnDisk()
    {
        if (Path is null || LoadedContentHash is null) return false;
        try { return !File.Exists(Path) || ScreenSnapshot.Hash(File.ReadAllBytes(Path)) != LoadedContentHash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Saves to <paramref name="path"/> (Save As) or to <see cref="Path"/>. Refuses — writing nothing — when the document is
    /// read-only, when the runtime rejects it, when the file changed on disk since it was read (another editor), or when
    /// Save As would replace another existing file without <paramref name="replaceExisting"/>. Otherwise: temp file in the
    /// same folder, ExternalScenarios.Load of the temp file, a final on-disk change check, atomic replace.
    /// An unchanged document saved to its own file writes nothing.
    /// </summary>
    public ScenarioSaveResult Save(string? path = null, bool replaceExisting = false, ScenarioBindingContext? binding = null)
    {
        if (ReadOnly) return new(false, Path, "This document is read-only and is never written: " + ReadOnlyReason, loadProblems.ToArray());
        string? target = path is null ? Path : System.IO.Path.GetFullPath(path);
        if (target is null) return new(false, null, "Choose a file name (Save as).", [], NeedsPath: true);
        if (!string.Equals(System.IO.Path.GetExtension(target), ".json", StringComparison.OrdinalIgnoreCase))
            return new(false, target, "Scenario documents use the .json extension; nothing was written.", []);
        bool same = Path is not null && WorkbenchPreferences.SamePath(Path, target);
        var problems = Check(binding, out _);
        var errors = problems.Where(p => p.Severity == ScenarioProblemSeverity.Error).ToArray();
        if (same && !Dirty && !ChangedOnDisk())
            return new(errors.Length == 0, target, errors.Length == 0 ? "No changes to save." : $"Nothing was written: no changes, and the document still has {errors.Length} problem{(errors.Length == 1 ? "" : "s")}.", problems);
        if (errors.Length > 0) return new(false, target, $"Not saved: {errors.Length} problem{(errors.Length == 1 ? "" : "s")} must be fixed first ({errors[0].Location}: {errors[0].Message})", problems);
        if (same && ChangedOnDisk())
            return new(false, target, $"Not saved: {System.IO.Path.GetFileName(target)} was changed or deleted on disk after it was opened. Reload it (your edits are lost) or Save as another file.", problems, ExternalChange: true);
        if (!same && File.Exists(target) && !replaceExisting)
            return new(false, target, $"{System.IO.Path.GetFileName(target)} already exists.", problems, TargetExists: true);
        string folder = System.IO.Path.GetDirectoryName(target)!;
        if (!Directory.Exists(folder)) return new(false, target, $"The folder {folder} does not exist; nothing was written.", problems);
        byte[] bytes = Serialize();
        string temp = System.IO.Path.Combine(folder, "." + System.IO.Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        ExternalScenarioFile loaded;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            var check = ExternalScenarios.Load(temp);
            if (check.ContentHash != ScreenSnapshot.Hash(bytes)) throw new IOException("The temporary copy does not match the document.");
            if (same && ChangedOnDisk())
                return new(false, target, $"Not saved: {System.IO.Path.GetFileName(target)} was changed on disk while saving. Reload it or Save as another file.", problems, ExternalChange: true);
            File.Move(temp, target, true);
            loaded = ExternalScenarios.Load(target);
        }
        catch (Exception ex) when (ex is ExternalScenarioException or IOException or UnauthorizedAccessException)
        {
            return new(false, target, $"Not saved: {ScenarioFiles.Detail(ex, temp)}", problems);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        Path = target; LoadedContentHash = loaded.ContentHash; savedRevision = Revision; ByteExact = true;
        return new(true, target, $"Saved {System.IO.Path.GetFileName(target)}.", problems, loaded) { Written = true };
    }
}

/// <summary>Where <see cref="WorkbenchScenarioEditorWindow.ApplyRecording"/> puts recorded steps.</summary>
internal enum RecordedInsertMode
{
    /// <summary>After the last step.</summary>
    Append,
    /// <summary>Before the first selected step (at the end when the document has no steps).</summary>
    InsertAtSelection,
    /// <summary>In place of the selected step range (at least one step must be selected).</summary>
    ReplaceSelection,
}

/// <summary>
/// What a recorder needs to know about the editor it records for, captured at the time of the call. Target/project values
/// are the open project's (null when the editor has no project, e.g. the project was closed). <see cref="ScreenSizes"/>
/// are the project's run grids ("80x25", ...); <see cref="DocumentGridDefaults"/> the document's own "grid.defaults" (null
/// when the document has no grid). <see cref="SelectionStart"/>/<see cref="SelectionCount"/> is the selected step range
/// (count 0 only for an empty document).
/// </summary>
internal sealed record ScenarioEditorContext(
    string? DocumentPath, bool Proxy, string? Fixture, bool ReadOnly,
    string? TargetPath, string[] TargetArguments, IReadOnlyDictionary<string, string> TargetEnvironment, string? ProjectPath,
    string ProxyBackend, string[] ScreenSizes, string[]? DocumentGridDefaults,
    int SelectionStart, int SelectionCount, int StepCount, ScenarioDescriptor? FixtureDescriptor);
