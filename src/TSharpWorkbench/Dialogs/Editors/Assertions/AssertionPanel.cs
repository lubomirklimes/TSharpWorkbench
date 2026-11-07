using System.Text.Json.Nodes;
using TSharpVision;
using TSharpWorkbench.Testing;

/// <summary>
/// The fields of one assertion kind as a plain Turbo Vision group. Common rows are inserted here:
/// row 0 description, row 2 Assertion ID, row 4 Severity; each kind inserts its own controls from row 6
/// and writes them back in <see cref="SaveFields"/>.
/// </summary>
internal abstract class AssertionPanel : FieldGroup
{
    internal const int Width = 76;
    protected const int FirstRow = 6;

    protected readonly JsonObject assertion;
    protected readonly string kind;
    private readonly TInputLine id;
    private readonly TRadioButtons severity;

    /// <summary>Text fields by JSON name (used by tests and validation messages).</summary>
    internal Dictionary<string, TInputLine> Fields { get; } = [];
    /// <summary>Selector groups by JSON name.</summary>
    internal Dictionary<string, TInputLine[]> Selectors { get; } = [];
    internal TInputLine IdField => id;
    internal TRadioButtons SeverityField => severity;
    internal string Kind => kind;

    protected AssertionPanel(JsonObject assertion, int height, string description) : base(new TRect(0, 0, Width, height))
    {
        this.assertion = assertion;
        kind = EditorDialog.Text(assertion["kind"]) ?? "";

        Insert(new TStaticText(new TRect(0, 0, Width, 1), description));

        id = new TInputLine(new TRect(16, 2, Width - 1, 3), 64);
        id.SetData(EditorDialog.Text(assertion["id"]) ?? "");
        Insert(id);
        Insert(new TLabel(new TRect(0, 2, 15, 3), "Assertion ~I~D", id));

        severity = new TRadioButtons(new TRect(16, 4, 44, 5), new TSItem("~e~rror", new TSItem("~w~arning", null!)));
        // An omitted severity shows the kind's default (warning for layout-audit, error otherwise).
        severity.value = (EditorDialog.Text(assertion["severity"]) ?? ScenarioSchemaV1.DefaultSeverity(kind)) == "warning" ? 1u : 0u;
        Insert(severity);
        Insert(new TLabel(new TRect(0, 4, 15, 5), "Se~v~erity", severity));
    }

    internal static AssertionPanel Create(JsonObject a) => (EditorDialog.Text(a["kind"]) ?? "") switch
    {
        "screen-contains" => new ScreenContainsAssertionPanel(a),
        "text-at" => new TextAtAssertionPanel(a),
        "attribute-at" => new AttributeAtAssertionPanel(a),
        "view-exists" => new ViewAssertionPanel(a, "A view matches the selector."),
        "view-absent" => new ViewAssertionPanel(a, "No view matches the selector."),
        "view-visible" => new ViewAssertionPanel(a, "The view matching the selector is visible."),
        "view-focused" => new ViewAssertionPanel(a, "The view matching the selector is focused."),
        "within-screen" => new ViewAssertionPanel(a, "The view matching the selector lies within the screen."),
        "not-clipped" => new ViewAssertionPanel(a, "The view matching the selector is not clipped."),
        "modal" => new ModalAssertionPanel(a),
        "within-view" => new RelationAssertionPanel(a, "The view lies within the other view (default: its owner)."),
        "disjoint" => new RelationAssertionPanel(a, "The two views do not overlap."),
        "aligned" => new AlignedAssertionPanel(a),
        "spacing" => new SpacingAssertionPanel(a),
        "shadow" => new ShadowAssertionPanel(a),
        "menu-depth" => new MenuDepthAssertionPanel(a),
        "menu-selected" => new MenuSelectedAssertionPanel(a),
        "menu-item-state" => new MenuItemStateAssertionPanel(a),
        "submenu-open" => new SubmenuOpenAssertionPanel(a),
        "status-item" => new StatusItemAssertionPanel(a),
        "status-context" => new StatusContextAssertionPanel(a),
        "status-hint" => new StatusHintAssertionPanel(a),
        "layout-audit" => new LayoutAuditAssertionPanel(a),
        _ => throw new ArgumentException("Unknown assertion kind.", nameof(a))
    };

    /// <summary>Validates and writes ID, severity and the kind's fields into <paramref name="o"/>; returns an error or null.</summary>
    internal string? Save(JsonObject o)
    {
        string idText = EditorDialog.Data(id);
        if (idText.Length == 0) return "An assertion needs an ID (unique within the checkpoint).";
        EditorDialog.SetText(o, "id", idText);
        // Written when present or when it differs from the kind's default; an omitted default stays omitted.
        string chosen = severity.value == 1 ? "warning" : "error";
        if (o.ContainsKey("severity") || chosen != ScenarioSchemaV1.DefaultSeverity(kind)) EditorDialog.SetText(o, "severity", chosen);
        return SaveFields(o);
    }

    protected abstract string? SaveFields(JsonObject o);

    // ── JSON helpers (no layout here: every kind inserts its own controls) ──────────

    protected bool Required(string field) => ScenarioSchemaV1.AssertionFieldRequired(kind, field);

    protected TInputLine Load(TInputLine line, string field)
    {
        line.SetData(EditorDialog.Text(assertion[field]) ?? "");
        Fields[field] = line;
        return line;
    }

    protected string? SaveText(JsonObject o, string field)
    {
        string value = Fields[field].Data;
        if (value.Length == 0 && Required(field)) return $"{kind} requires a nonempty '{field}'.";
        EditorDialog.SetText(o, field, value);
        return null;
    }

    protected string? SaveNumber(JsonObject o, string field, bool nonNegative = false)
    {
        if (!EditorDialog.TryNumber(Fields[field], out int? n)) return $"'{field}' is an integer.";
        if (n is null && Required(field)) return $"{kind} requires '{field}'.";
        if (n is < 0 && nonNegative) return $"'{field}' cannot be negative.";
        EditorDialog.SetNumber(o, field, n);
        return null;
    }

    protected uint ChoiceIndex(string field, string[] values) => (uint)Math.Max(0, Array.IndexOf(values, EditorDialog.Text(assertion[field])));

    protected static void SaveChoice(JsonObject o, string field, TRadioButtons radio, string[] values) =>
        EditorDialog.SetText(o, field, values[Math.Clamp((int)radio.value, 0, values.Length - 1)]);

    protected SelectorGroup Selector(SelectorGroup group, string field)
    {
        Selectors[field] = group.Parts;
        return group;
    }
}
