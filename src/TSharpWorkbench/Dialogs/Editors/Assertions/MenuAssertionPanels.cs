using System.Text.Json.Nodes;
using TSharpVision;
using TSharpWorkbench.Testing;

/// <summary>menu-depth: exactly this many menu levels are open.</summary>
internal sealed class MenuDepthAssertionPanel : AssertionPanel
{
    private readonly TInputLine value;

    public MenuDepthAssertionPanel(JsonObject a) : base(a, 7, "Exactly this many menu levels are open.")
    {
        value = Load(new TInputLine(new TRect(16, 6, 26, 7), 6), "value");
        Insert(value);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~D~epth", value));
        Insert(new TStaticText(new TRect(28, 6, Width, 7), "open menu levels, 0..32"));
    }

    protected override string? SaveFields(JsonObject o) => SaveNumber(o, "value");
}

/// <summary>menu-selected: the menu item with the text is selected.</summary>
internal sealed class MenuSelectedAssertionPanel : AssertionPanel
{
    private readonly TInputLine text, level;

    public MenuSelectedAssertionPanel(JsonObject a) : base(a, 8, "The menu item with the text is selected.")
    {
        text = Load(new TInputLine(new TRect(16, 6, Width - 1, 7), 200), "text");
        Insert(text);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~T~ext", text));

        level = Load(new TInputLine(new TRect(16, 7, 26, 8), 6), "level");
        Insert(level);
        Insert(new TLabel(new TRect(0, 7, 15, 8), "~L~evel", level));
        Insert(new TStaticText(new TRect(28, 7, Width, 8), "menu level 0..32, optional"));
    }

    protected override string? SaveFields(JsonObject o) => SaveText(o, "text") ?? SaveNumber(o, "level", true);
}

/// <summary>menu-item-state: the menu item with the text is enabled or disabled.</summary>
internal sealed class MenuItemStateAssertionPanel : AssertionPanel
{
    private readonly TInputLine text, level;
    private readonly TRadioButtons state;
    private readonly string[] states = ScenarioSchemaV1.AssertionStates("menu-item-state");

    public MenuItemStateAssertionPanel(JsonObject a) : base(a, 10, "The menu item with the text is in the state.")
    {
        text = Load(new TInputLine(new TRect(16, 6, Width - 1, 7), 200), "text");
        Insert(text);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~T~ext", text));

        level = Load(new TInputLine(new TRect(16, 7, 26, 8), 6), "level");
        Insert(level);
        Insert(new TLabel(new TRect(0, 7, 15, 8), "~L~evel", level));
        Insert(new TStaticText(new TRect(28, 7, Width, 8), "menu level 0..32, optional"));

        state = new TRadioButtons(new TRect(16, 9, 50, 10), new TSItem("~e~nabled", new TSItem("~d~isabled", null!)));
        state.value = ChoiceIndex("state", states);
        Insert(state);
        Insert(new TLabel(new TRect(0, 9, 15, 10), "~S~tate", state));
    }

    protected override string? SaveFields(JsonObject o)
    {
        if ((SaveText(o, "text") ?? SaveNumber(o, "level", true)) is { } error) return error;
        SaveChoice(o, "state", state, states);
        return null;
    }
}

/// <summary>submenu-open: the submenu of the item with the text is open.</summary>
internal sealed class SubmenuOpenAssertionPanel : AssertionPanel
{
    private readonly TInputLine text;

    public SubmenuOpenAssertionPanel(JsonObject a) : base(a, 7, "The submenu of the item with the text is open.")
    {
        text = Load(new TInputLine(new TRect(16, 6, Width - 1, 7), 200), "text");
        Insert(text);
        Insert(new TLabel(new TRect(0, 6, 15, 7), "~T~ext", text));
    }

    protected override string? SaveFields(JsonObject o) => SaveText(o, "text");
}
