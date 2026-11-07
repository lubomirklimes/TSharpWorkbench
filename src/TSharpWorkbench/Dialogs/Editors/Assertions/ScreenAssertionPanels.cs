using System.Text.Json.Nodes;
using TSharpVision;

/// <summary>screen-contains: the screen contains the text somewhere.</summary>
internal sealed class ScreenContainsAssertionPanel : AssertionPanel
{
    private readonly TInputLine text;

    public ScreenContainsAssertionPanel(JsonObject a) : base(a, 7, "The screen contains the text somewhere.")
    {
        text = Load(new TInputLine(new TRect(16, 6, Width - 1, 7), 200), "text");
        Insert(text);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~T~ext", text));
    }

    protected override string? SaveFields(JsonObject o) => SaveText(o, "text");
}

/// <summary>text-at: the text appears at cell (x,y).</summary>
internal sealed class TextAtAssertionPanel : AssertionPanel
{
    private readonly TInputLine x, y, text;

    public TextAtAssertionPanel(JsonObject a) : base(a, 9, "The text appears at cell (x,y).")
    {
        x = Load(new TInputLine(new TRect(16, 6, 26, 7), 6), "x");
        Insert(x);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~X~", x));
        Insert(new TStaticText(new TRect(28, 6, Width, 7), "column, 0-based"));

        y = Load(new TInputLine(new TRect(16, 7, 26, 8), 6), "y");
        Insert(y);
        Insert(new TLabel(new TRect(0, 7, 15, 8), "~Y~", y));
        Insert(new TStaticText(new TRect(28, 7, Width, 8), "row, 0-based"));

        text = Load(new TInputLine(new TRect(16, 8, Width - 1, 9), 200), "text");
        Insert(text);
        Insert(new TLabel(new TRect(0, 8, 15, 9), "~T~ext", text));
    }

    protected override string? SaveFields(JsonObject o) =>
        SaveNumber(o, "x", true) ?? SaveNumber(o, "y", true) ?? SaveText(o, "text");
}

/// <summary>attribute-at: cells from (x,y) have the attribute value.</summary>
internal sealed class AttributeAtAssertionPanel : AssertionPanel
{
    private readonly TInputLine x, y, width, value;

    public AttributeAtAssertionPanel(JsonObject a) : base(a, 10, "Cells from (x,y) have the attribute value.")
    {
        x = Load(new TInputLine(new TRect(16, 6, 26, 7), 6), "x");
        Insert(x);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~X~", x));
        Insert(new TStaticText(new TRect(28, 6, Width, 7), "column, 0-based"));

        y = Load(new TInputLine(new TRect(16, 7, 26, 8), 6), "y");
        Insert(y);
        Insert(new TLabel(new TRect(0, 7, 15, 8), "~Y~", y));
        Insert(new TStaticText(new TRect(28, 7, Width, 8), "row, 0-based"));

        width = Load(new TInputLine(new TRect(16, 8, 26, 9), 6), "width");
        Insert(width);
        Insert(new TLabel(new TRect(0, 8, 15, 9), "Wi~d~th", width));
        Insert(new TStaticText(new TRect(28, 8, Width, 9), "cells, optional (default 1)"));

        value = Load(new TInputLine(new TRect(16, 9, 26, 10), 6), "value");
        Insert(value);
        Insert(new TLabel(new TRect(0, 9, 15, 10), "V~a~lue", value));
        Insert(new TStaticText(new TRect(28, 9, Width, 10), "attribute byte"));
    }

    protected override string? SaveFields(JsonObject o) =>
        SaveNumber(o, "x", true) ?? SaveNumber(o, "y", true) ?? SaveNumber(o, "width", true) ?? SaveNumber(o, "value");
}
