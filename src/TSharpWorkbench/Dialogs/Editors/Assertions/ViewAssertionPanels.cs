using System.Text.Json.Nodes;
using TSharpVision;
using TSharpWorkbench.Testing;

/// <summary>view-exists, view-absent, view-visible, view-focused, within-screen, not-clipped: one view selector.</summary>
internal sealed class ViewAssertionPanel : AssertionPanel
{
    private readonly SelectorGroup view;

    public ViewAssertionPanel(JsonObject a, string description) : base(a, 9, description)
    {
        view = Selector(new SelectorGroup(6, "View selector", a["view"]), "view");
        Insert(view);
    }

    protected override string? SaveFields(JsonObject o) => view.Save(o, "view", Required("view"), kind);
}

/// <summary>modal: a modal view matches the selector, or (checked) no modal is open.</summary>
internal sealed class ModalAssertionPanel : AssertionPanel
{
    private readonly SelectorGroup view;

    public ModalAssertionPanel(JsonObject a) : base(a, 10, "A modal view matches the selector (or, with no view, no modal is open).")
    {
        bool none = !a.ContainsKey("view") || a["view"] is null;
        view = Selector(new SelectorGroup(6, "View selector (optional)", a["view"], noView: true, noViewChecked: none), "view");
        Insert(view);
    }

    protected override string? SaveFields(JsonObject o) => view.Save(o, "view", Required("view"), kind);
}

/// <summary>within-view, disjoint: the view and the other view.</summary>
internal sealed class RelationAssertionPanel : AssertionPanel
{
    private readonly SelectorGroup view, other;

    public RelationAssertionPanel(JsonObject a, string description) : base(a, 13, description)
    {
        view = Selector(new SelectorGroup(6, "View selector", a["view"]), "view");
        Insert(view);
        other = Selector(new SelectorGroup(10, Required("other") ? "Other view" : "Other view (optional)", a["other"]), "other");
        Insert(other);
    }

    protected override string? SaveFields(JsonObject o) =>
        view.Save(o, "view", Required("view"), kind) ?? other.Save(o, "other", Required("other"), kind);
}

/// <summary>aligned: the views are aligned on an edge or a center.</summary>
internal sealed class AlignedAssertionPanel : AssertionPanel
{
    private readonly SelectorGroup view, other;
    private readonly TRadioButtons edge;
    private readonly string[] edges = ScenarioSchemaV1.AssertionEdges("aligned");

    public AlignedAssertionPanel(JsonObject a) : base(a, 16, "The views are aligned on the edge.")
    {
        view = Selector(new SelectorGroup(6, "View selector", a["view"]), "view");
        Insert(view);
        other = Selector(new SelectorGroup(10, "Other view (optional)", a["other"]), "other");
        Insert(other);

        edge = new TRadioButtons(new TRect(16, 14, Width - 1, 16),
            new TSItem("~l~eft", new TSItem("~r~ight", new TSItem("~t~op", new TSItem("~b~ottom", new TSItem("center-~x~", new TSItem("center-~y~", null!)))))));
        edge.value = ChoiceIndex("edge", edges);
        Insert(edge);
        Insert(new TLabel(new TRect(0, 14, 15, 15), "~E~dge", edge));
    }

    protected override string? SaveFields(JsonObject o)
    {
        if ((view.Save(o, "view", Required("view"), kind) ?? other.Save(o, "other", Required("other"), kind)) is { } error) return error;
        SaveChoice(o, "edge", edge, edges);
        return null;
    }
}

/// <summary>spacing: the gap between the views on an axis is within min..max.</summary>
internal sealed class SpacingAssertionPanel : AssertionPanel
{
    private readonly SelectorGroup view, other;
    private readonly TRadioButtons axis;
    private readonly TInputLine min, max;
    private readonly string[] axes = ScenarioSchemaV1.AssertionEdges("spacing");

    public SpacingAssertionPanel(JsonObject a) : base(a, 18, "The gap between the views on the axis is within min..max.")
    {
        view = Selector(new SelectorGroup(6, "View selector", a["view"]), "view");
        Insert(view);
        other = Selector(new SelectorGroup(10, "Other view", a["other"]), "other");
        Insert(other);

        axis = new TRadioButtons(new TRect(16, 14, 50, 15), new TSItem("~h~orizontal", new TSItem("~v~ertical", null!)));
        axis.value = ChoiceIndex("edge", axes);
        Insert(axis);
        Insert(new TLabel(new TRect(0, 14, 15, 15), "~A~xis", axis));

        min = Load(new TInputLine(new TRect(16, 16, 26, 17), 6), "min");
        Insert(min);
        Insert(new TLabel(new TRect(0, 16, 15, 17), "Mi~n~", min));
        Insert(new TStaticText(new TRect(28, 16, Width, 17), "cells"));

        max = Load(new TInputLine(new TRect(16, 17, 26, 18), 6), "max");
        Insert(max);
        Insert(new TLabel(new TRect(0, 17, 15, 18), "Ma~x~", max));
        Insert(new TStaticText(new TRect(28, 17, Width, 18), "cells"));
    }

    protected override string? SaveFields(JsonObject o)
    {
        if ((view.Save(o, "view", Required("view"), kind) ?? other.Save(o, "other", Required("other"), kind)) is { } error) return error;
        SaveChoice(o, "edge", axis, axes);
        if ((SaveNumber(o, "min") ?? SaveNumber(o, "max")) is { } numberError) return numberError;
        if (o["min"] is JsonValue lo && o["max"] is JsonValue hi && lo.TryGetValue(out int l) && hi.TryGetValue(out int h) && l > h)
            return "min cannot be larger than max.";
        return null;
    }
}

/// <summary>shadow: the view casts a shadow (1) or not (0).</summary>
internal sealed class ShadowAssertionPanel : AssertionPanel
{
    private readonly SelectorGroup view;
    private readonly TInputLine value;

    public ShadowAssertionPanel(JsonObject a) : base(a, 11, "The view casts a shadow (1) or not (0).")
    {
        view = Selector(new SelectorGroup(6, "View selector", a["view"]), "view");
        Insert(view);

        value = Load(new TInputLine(new TRect(16, 10, 26, 11), 6), "value");
        Insert(value);
        Insert(new TLabel(new TRect(0, 10, 15, 11), "V~a~lue", value));
        Insert(new TStaticText(new TRect(28, 10, Width, 11), "1 = shadow, 0 = none"));
    }

    protected override string? SaveFields(JsonObject o) => view.Save(o, "view", Required("view"), kind) ?? SaveNumber(o, "value");
}
