using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

/// <summary>Edits the document header: identity, kind (Black-Box Mode / fixture-bound), outcome, tags and explicit grid.</summary>
internal sealed class WorkbenchScenarioMetadataDialog : EditorDialog
{
    internal const string Title = "Scenario Metadata";
    private readonly ScenarioDocument document;
    private readonly TInputLine id, name, group, tags, fixture, minWidth, minHeight, maxWidth, maxHeight, defaults;
    private readonly TMemo description;
    private readonly TRadioButtons kind, outcome;
    private readonly TCheckBoxes explicitGrid;
    private readonly TListBox? fixtures;
    private readonly string[] fixtureIds = [];
    private bool initialized;
    internal TInputLine IdField => id;
    internal TInputLine TitleField => name;
    internal TInputLine TagsField => tags;
    internal TRadioButtons Kind => kind;
    internal TMemo DescriptionField => description;
    internal TInputLine GroupField => group;
    internal TInputLine FixtureField => fixture;
    internal IReadOnlyList<TInputLine> GridFields => [minWidth, minHeight, maxWidth, maxHeight, defaults];
    internal string NoticeText => notice.Current;

    private WorkbenchScenarioMetadataDialog(TDeskTop desktop, ScenarioDocument document, ScenarioBindingContext binding)
        : base(Math.Min(94, desktop.size.x - 2), Math.Min(32, desktop.size.y), Title)
    {
        this.document = document;
        int w = size.x;
        id = Input(2, "Scenario ~I~D", document.Id, 128, w - 4);
        name = Input(4, "~T~itle", document.Title, 200, w - 4);
        var vertical = new TScrollBar(new(w - 5, 6, w - 4, 9)) { growMode = 0 }; Insert(vertical);
        var horizontal = new TScrollBar(new(FieldX, 9, w - 5, 10)) { growMode = 0 }; Insert(horizontal);
        description = new TMemo(new(FieldX, 6, w - 5, 9), horizontal, vertical, null,
            (uint)Math.Max(65536, document.Description.Length + 16384)) { growMode = 0, canUndo = true };
        description.SetData(new TMemoData((uint)document.Description.Length)
            { length = (uint)document.Description.Length, buffer = document.Description.ToCharArray() });
        Insert(description);
        Insert(new TLabel(new(LabelX, 6, FieldX - 1, 7), "~D~escription", description));
        group = Input(11, "Gro~u~p", document.Group, 128, 42);
        tags = Input(11, "", document.Tags is { } t ? string.Join(", ", t) : "", 400, w - 4);
        tags.ChangeBounds(new(52, 11, w - 4, 12));
        Insert(new TLabel(new(45, 11, 51, 12), "Ta~g~s", tags));
        kind = Radio(13, 1, "~M~ode", ["~B~lack-Box", "~W~hite-Box"], document.Proxy ? 0 : 1, 58);
        fixture = Input(15, "~F~ixture", document.Fixture, 128, 58);
        var bindable = binding.Compiled is null ? [] : ScenarioFiles.BindableFixtures(binding.Compiled, binding.LegacyFixtures);
        fixtureIds = bindable.Select(f => f.Id).ToArray();
        if (fixtureIds.Length > 0)
        {
            fixtures = Picker(60, 13, w - 5, 17, "Available (Enter)", fixtureIds);
            fixtures.vScrollBar!.growMode = 0;
            if (Array.IndexOf(fixtureIds, document.Fixture) is >= 0 and var fi) fixtures.FocusItemNum(fi);
        }
        else Insert(new TStaticText(new(60, 15, w - 4, 16), binding.Compiled is null ? "Catalog unavailable" : "No advertised fixtures"));
        outcome = Radio(17, 1, "~E~xpected", ["Pa~s~sed", "F~a~iled"], document.ExpectedOutcome == "failed" ? 1 : 0, 58);
        var grid = document.HeaderNode("grid") as JsonObject;
        explicitGrid = new TCheckBoxes(new(FieldX, 19, 58, 20), new TSItem("Explicit g~r~id", null!)) { value = grid is null ? 0u : 1u };
        Insert(explicitGrid);
        string G(string k) => Text(grid?[k]) ?? "";
        TInputLine Dimension(int y, int x, string label, string key)
        {
            var field = new TInputLine(new(x + 8, y, x + 14, y + 1), 4);
            field.SetData(G(key)); Insert(field);
            Insert(new TLabel(new(x, y, x + 7, y + 1), label, field));
            return field;
        }
        Insert(new TStaticText(new(LabelX, 21, FieldX - 1, 22), "Minimum"));
        minWidth = Dimension(21, FieldX, "Width", "minWidth");
        minHeight = Dimension(21, FieldX + 22, "Height", "minHeight");
        Insert(new TStaticText(new(LabelX, 23, FieldX - 1, 24), "Maximum"));
        maxWidth = Dimension(23, FieldX, "Width", "maxWidth");
        maxHeight = Dimension(23, FieldX + 22, "Height", "maxHeight");
        defaults = Input(25, "Defau~l~ts", grid?["defaults"] is JsonArray d ? string.Join(", ", d.Select(Text)) : "", 200, w - 4);
        Fit(26);
        Insert(notice);
        Buttons();
        initialized = true;
        UpdateEnabled();
        id.Select();
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEnter && fixtures is not null && ReferenceEquals(current, fixtures))
        { ClearEvent(ref ev); fixture.SetData(fixtureIds.ElementAtOrDefault(fixtures.focused) ?? ""); kind.value = 1; kind.DrawView(); fixture.Select(); return; }
        base.HandleEvent(ref ev);
        UpdateEnabled();
    }
    private void UpdateEnabled()
    {
        // Picker.NewList broadcasts selection changes while the remaining controls are still being built.
        if (!initialized) return;
        fixture.SetState(Views.sfDisabled, kind.value == 0);
        foreach (var line in new[] { minWidth, minHeight, maxWidth, maxHeight, defaults }) line.SetState(Views.sfDisabled, explicitGrid.value == 0);
    }

    private string DescriptionText()
    {
        var data = new TMemoData(description.bufLen);
        description.GetData(data);
        return new string(data.buffer, 0, (int)data.length);
    }

    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return base.Valid(command);
        if (Data(id).Length == 0 || Data(id).Any(char.IsWhiteSpace)) return Refuse("Enter a scenario ID without spaces, for example myapp/smoke.");
        if (Data(name).Length == 0) return Refuse("Enter a title.");
        if (string.IsNullOrWhiteSpace(DescriptionText())) return Refuse("Enter a description (schema v1 requires one).");
        bool proxy = kind.value == 0;
        if (!proxy && Data(fixture).Length == 0) return Refuse("A fixture-bound document names its fixture.");
        if (!proxy && Data(fixture) == Data(id)) return Refuse("The scenario ID must differ from the fixture ID.");
        string[] tagList = Data(tags).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tagList.Length > 16 || tagList.Distinct(StringComparer.Ordinal).Count() != tagList.Length) return Refuse("At most 16 unique tags, separated by commas.");
        JsonObject? grid = null;
        if (explicitGrid.value == 1)
        {
            var numbers = new[] { minWidth, minHeight, maxWidth, maxHeight }.Select(l => TryNumber(l, out int? n) ? n : null).ToArray();
            if (numbers.Any(n => n is not > 0)) return Refuse("An explicit grid needs positive min and max width and height.");
            string[] sizes = Data(defaults).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (sizes.Length == 0) return Refuse("Name at least one default grid, for example 80x25, 140x40.");
            grid = document.HeaderNode("grid") as JsonObject ?? new JsonObject();
            SetNumber(grid, "minWidth", numbers[0]); SetNumber(grid, "minHeight", numbers[1]); SetNumber(grid, "maxWidth", numbers[2]); SetNumber(grid, "maxHeight", numbers[3]);
            if (grid["defaults"] is not JsonArray old || !old.Select(Text).SequenceEqual(sizes)) grid["defaults"] = new JsonArray(sizes.Select(s => (JsonNode?)s).ToArray());
        }
        // Apply only what changed (the document ignores equal values).
        document.Id = Data(id); document.Title = Data(name); document.Description = DescriptionText();
        if (Data(group).Length == 0) document.RemoveHeader("group"); else document.Group = Data(group);
        if (tagList.Length == 0) { if (document.Tags is { Length: > 0 }) document.Tags = null; }
        else document.Tags = tagList;
        if (proxy != document.Proxy || (!proxy && Data(fixture) != document.Fixture)) document.SetKind(proxy, proxy ? null : Data(fixture));
        document.ExpectedOutcome = outcome.value == 1 ? "failed" : "passed";
        if (grid is null) document.RemoveHeader("grid"); else document.SetHeader("grid", grid);
        return base.Valid(command);
    }

    /// <summary>Edits the header of <paramref name="document"/> in place; true when OK was chosen.</summary>
    internal static bool Edit(TDeskTop desktop, ScenarioDocument document, ScenarioBindingContext binding)
    {
        using var dialog = new WorkbenchScenarioMetadataDialog(desktop, document, binding);
        return desktop.ExecView(dialog) == Views.cmOK;
    }
}
