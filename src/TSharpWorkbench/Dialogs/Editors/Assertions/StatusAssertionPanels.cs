using System.Text.Json.Nodes;
using TSharpVision;
using TSharpWorkbench.Testing;

/// <summary>status-item: the status line item with the text is visible / absent / enabled / disabled.</summary>
internal sealed class StatusItemAssertionPanel : AssertionPanel
{
    private readonly TInputLine text;
    private readonly TRadioButtons state;
    private readonly string[] states = ScenarioSchemaV1.AssertionStates("status-item");

    public StatusItemAssertionPanel(JsonObject a) : base(a, 10, "The status line item with the text is in the state.")
    {
        text = Load(new TInputLine(new TRect(16, 6, Width - 1, 7), 200), "text");
        Insert(text);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~T~ext", text));

        state = new TRadioButtons(new TRect(16, 8, 50, 10),
            new TSItem("~v~isible", new TSItem("~a~bsent", new TSItem("~e~nabled", new TSItem("~d~isabled", null!)))));
        state.value = ChoiceIndex("state", states);
        Insert(state);
        Insert(new TLabel(new TRect(0, 8, 15, 9), "~S~tate", state));
    }

    protected override string? SaveFields(JsonObject o)
    {
        if (SaveText(o, "text") is { } error) return error;
        SaveChoice(o, "state", state, states);
        return null;
    }
}

/// <summary>status-context: the status line uses this help context.</summary>
internal sealed class StatusContextAssertionPanel : AssertionPanel
{
    private readonly TInputLine value;

    public StatusContextAssertionPanel(JsonObject a) : base(a, 7, "The status line uses this help context.")
    {
        value = Load(new TInputLine(new TRect(16, 6, 26, 7), 6), "value");
        Insert(value);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~C~ontext", value));
        Insert(new TStaticText(new TRect(28, 6, Width, 7), "help context number"));
    }

    protected override string? SaveFields(JsonObject o) => SaveNumber(o, "value");
}

/// <summary>status-hint: the status line hint contains the text.</summary>
internal sealed class StatusHintAssertionPanel : AssertionPanel
{
    private readonly TInputLine text;

    public StatusHintAssertionPanel(JsonObject a) : base(a, 7, "The status line hint contains the text.")
    {
        text = Load(new TInputLine(new TRect(16, 6, Width - 1, 7), 200), "text");
        Insert(text);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~T~ext", text));
    }

    protected override string? SaveFields(JsonObject o) => SaveText(o, "text");
}
