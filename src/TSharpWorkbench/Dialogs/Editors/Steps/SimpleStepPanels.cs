using System.Text.Json.Nodes;
using TSharpVision;
using TSharpWorkbench.Testing;
using static EditorDialog;

/// <summary>"text": 1 to 48 characters typed one key event each.</summary>
internal sealed class TextStepPanel : StepPanel
{
    private readonly TInputLine text;

    internal TInputLine TextField => text;
    internal override TView? FirstField => text;

    public TextStepPanel(ScenarioDocumentStep step) : base(step, 68, 3)
    {
        text = new TInputLine(new TRect(16, 0, 67, 1), 48);
        text.SetData(step.String("text") ?? "");
        Insert(text);
        Insert(new TLabel(new TRect(0, 0, 15, 1), "~T~ext", text));
        Insert(new TStaticText(new TRect(16, 1, 68, 3), "1 to 48 characters, typed one key event each;\nno control characters."));
    }

    internal override string? Apply(JsonObject o)
    {
        if (!InputSteps.IsValid(InputSteps.Text, text.Data)) return "Text holds 1 to 48 characters without control characters; split longer text into several steps.";
        SetText(o, "text", text.Data);
        return null;
    }
}

/// <summary>"modifiers": holds or releases Shift, Ctrl or Alt.</summary>
internal sealed class ModifiersStepPanel : StepPanel
{
    private static readonly string[] Modifiers = ["shift", "ctrl", "alt"];
    private readonly TRadioButtons modifier, state;

    internal override TView? FirstField => modifier;

    public ModifiersStepPanel(ScenarioDocumentStep step) : base(step, 60, 6)
    {
        // Ct~r~l: ~C~ belongs to Cancel.
        modifier = new TRadioButtons(new TRect(16, 0, 34, 3), new TSItem("~S~hift", new TSItem("Ct~r~l", new TSItem("~A~lt", null!))));
        modifier.value = (uint)Math.Max(0, Array.IndexOf(Modifiers, step.String("modifier")));
        Insert(modifier);
        Insert(new TLabel(new TRect(0, 0, 15, 1), "~M~odifier", modifier));

        state = new TRadioButtons(new TRect(16, 4, 34, 6), new TSItem("~D~own (hold)", new TSItem("~U~p (release)", null!)));
        state.value = step.String("state") == "up" ? 1u : 0u;
        Insert(state);
        Insert(new TLabel(new TRect(0, 4, 15, 5), "Sta~t~e", state));
        Insert(new TStaticText(new TRect(36, 4, 60, 6), "Down holds it for the\nfollowing steps."));
    }

    internal override string? Apply(JsonObject o)
    {
        SetText(o, "modifier", Modifiers[Math.Clamp((int)modifier.value, 0, 2)]);
        SetText(o, "state", state.value == 1 ? "up" : "down");
        return null;
    }
}

/// <summary>"resize": the new screen size in cells.</summary>
internal sealed class ResizeStepPanel : StepPanel
{
    private readonly TInputLine width, height;

    internal override TView? FirstField => width;

    public ResizeStepPanel(ScenarioDocumentStep step) : base(step, 60, 2)
    {
        width = new TInputLine(new TRect(16, 0, 24, 1), 4);
        width.SetData(step.String("width") ?? "");
        Insert(width);
        Insert(new TLabel(new TRect(0, 0, 15, 1), "~W~idth", width));
        Insert(new TStaticText(new TRect(26, 0, 60, 1), "columns (cells)"));

        height = new TInputLine(new TRect(16, 1, 24, 2), 4);
        height.SetData(step.String("height") ?? "");
        Insert(height);
        Insert(new TLabel(new TRect(0, 1, 15, 2), "~H~eight", height));
        Insert(new TStaticText(new TRect(26, 1, 60, 2), "rows (cells)"));
    }

    internal override string? Apply(JsonObject o)
    {
        if (!TryNumber(width, out int? cw) || !TryNumber(height, out int? ch) || cw is not > 0 || ch is not > 0) return "Width and height are positive integers (cells).";
        SetNumber(o, "width", cw);
        SetNumber(o, "height", ch);
        return null;
    }
}

/// <summary>"action": a registered fixture action, picked from the names the fixture advertises.</summary>
internal sealed class ActionStepPanel : StepPanel
{
    private readonly TInputLine adapter;

    internal override TView? FirstField => adapter;

    public ActionStepPanel(ScenarioDocumentStep step, ScenarioDescriptor? fixture)
        : base(step, 72, (fixture?.Adapters?.Actions ?? []).Length > 0 ? 10 : 3)
    {
        adapter = new TInputLine(new TRect(16, 0, 71, 1), 64);
        adapter.SetData(step.String("adapter") ?? "");
        Insert(adapter);
        Insert(new TLabel(new TRect(0, 0, 15, 1), "~A~ction", adapter));

        string[] actions = fixture?.Adapters?.Actions ?? [];
        if (actions.Length > 0)
            Insert(new NamePicker(new TRect(16, 2, 72, 10), "Advertised ~f~ixture actions (Enter copies)", actions, adapter, step.String("adapter")));
        else
            Insert(new TStaticText(new TRect(16, 1, 72, 3), fixture is null
                ? "The fixture is not known yet (refresh with F2);\nthe name is checked at binding."
                : "This fixture advertises no actions."));
    }

    internal override string? Apply(JsonObject o)
    {
        if (Data(adapter).Length == 0) return "Name the registered fixture action.";
        SetText(o, "adapter", Data(adapter));
        return null;
    }
}

/// <summary>"release": has no fields of its own, only an explanation.</summary>
internal sealed class ReleaseStepPanel : StepPanel
{
    public ReleaseStepPanel(ScenarioDocumentStep step) : base(step, 72, 2)
    {
        Insert(new TStaticText(new TRect(1, 0, 72, 2),
            "Releases every held mouse button, key and modifier\nthrough the input pipeline. This step has no other fields."));
    }
}
