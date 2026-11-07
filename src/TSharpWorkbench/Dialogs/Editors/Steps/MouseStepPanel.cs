using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using static EditorDialog;

/// <summary>"mouse": an action (move, press, click, wheel) at a cell or a named fixture point, with a live input preview.</summary>
/// <remarks>
/// Top: what to do (action | button and wheel direction side by side). Middle: where (cell X/Y on one row, or a named point)
/// and how often (wheel notches). Bottom: the resulting input string.
/// </remarks>
internal sealed class MouseStepPanel : StepPanel
{
    private static readonly string[] Verbs = ["move", "down", "up", "click", "double", "wheel"];
    private static readonly string[] MouseButtons = ["left", "right", "middle"], WheelDirections = ["up", "down", "left", "right"];
    private static readonly Regex MousePattern = new("^(?<verb>move|(?<press>down|up|click|double)-(?<button>left|right|middle)|wheel-(?<wheel>up|down|left|right))@(?<at>[^@*]+)(\\*(?<count>[0-9]{1,2}))?$");

    private readonly bool proxy;
    private readonly TInputLine mouseX, mouseY, point, count;
    private readonly TRadioButtons verb, button, wheel;
    private readonly EditorNotice preview;

    internal TInputLine X => mouseX;
    internal TInputLine Y => mouseY;
    internal TRadioButtons Verb => verb;
    internal string PreviewText => preview.Current;
    internal override TView? FirstField => verb;

    public MouseStepPanel(ScenarioDocumentStep step, bool proxy, ScenarioDescriptor? fixture) : base(step, 76, 16)
    {
        this.proxy = proxy;
        var m = MousePattern.Match(step.String("input") ?? "");
        string at = m.Success ? m.Groups["at"].Value : "0,0";

        // What: unique mnemonics (Cl~i~ck, Middl~e~) so none collides with ~C~ancel or the other fields.
        verb = new TRadioButtons(new TRect(16, 0, 36, 6),
            new TSItem("~M~ove", new TSItem("~D~own (press)", new TSItem("~U~p (release)", new TSItem("Cl~i~ck", new TSItem("Dou~b~le-click", new TSItem("~W~heel", null!)))))));
        verb.value = (uint)(!m.Success ? 3 : m.Groups["wheel"].Success ? 5 : m.Groups["press"].Success ? Array.IndexOf(Verbs, m.Groups["press"].Value) : 0);
        Insert(verb);
        Insert(new TLabel(new TRect(0, 0, 15, 1), "~A~ction", verb));

        Insert(new TStaticText(new TRect(40, 0, 76, 1), "Button (press, click)"));
        button = new TRadioButtons(new TRect(40, 1, 58, 4), new TSItem("~L~eft", new TSItem("~R~ight", new TSItem("Middl~e~", null!))));
        button.value = (uint)(m.Success && m.Groups["button"].Success ? Array.IndexOf(MouseButtons, m.Groups["button"].Value) : 0);
        Insert(button);

        Insert(new TStaticText(new TRect(40, 4, 76, 5), "Wheel direction"));
        wheel = new TRadioButtons(new TRect(40, 5, 58, 9), new TSItem("~1~ Up", new TSItem("~2~ Down", new TSItem("~3~ Left", new TSItem("~4~ Right", null!)))));
        wheel.value = (uint)(m.Success && m.Groups["wheel"].Success ? Array.IndexOf(WheelDirections, m.Groups["wheel"].Value) : 1);
        Insert(wheel);

        // Where: cell X and Y on one row, the named point below.
        bool named = at.StartsWith(':');
        string[] xy = named ? ["", ""] : at.Split(',');
        mouseX = new TInputLine(new TRect(16, 10, 23, 11), 6);
        mouseX.SetData(xy.ElementAtOrDefault(0) ?? "");
        Insert(mouseX);
        Insert(new TLabel(new TRect(0, 10, 15, 11), "Cell ~X~", mouseX));
        mouseY = new TInputLine(new TRect(28, 10, 35, 11), 6);
        mouseY.SetData(xy.ElementAtOrDefault(1) ?? "");
        Insert(mouseY);
        Insert(new TLabel(new TRect(24, 10, 27, 11), "~Y~", mouseY));
        Insert(new TStaticText(new TRect(37, 10, 76, 12), "0,0 = top-left; negative counts\nfrom the far edge (-1 = last)."));

        point = new TInputLine(new TRect(16, 12, 36, 13), 32);
        point.SetData(named ? at[1..] : "");
        Insert(point);
        Insert(new TLabel(new TRect(0, 12, 15, 13), "Named ~p~oint", point));
        if (proxy) point.SetState(Views.sfDisabled, true);
        string[] points = fixture?.Adapters?.Points ?? [];
        Insert(new TStaticText(new TRect(38, 12, 76, 13), proxy ? "(not for Black-Box Mode)"
            : points.Length > 0 ? "Advertised: " + string.Join(", ", points) : fixture is null ? "(fixture not known: F2)" : "(fixture has no points)"));

        count = new TInputLine(new TRect(16, 13, 21, 14), 2);
        count.SetData(m.Success && m.Groups["count"].Success ? m.Groups["count"].Value : "");
        Insert(count);
        Insert(new TLabel(new TRect(0, 13, 15, 14), "Wheel ~n~otches", count));
        Insert(new TStaticText(new TRect(23, 13, 76, 14), "1..20 (wheel only)"));

        preview = new EditorNotice(new TRect(1, 15, 76, 16));
        Insert(preview);
    }

    private string Input()
    {
        string v = Verbs[Math.Clamp((int)verb.value, 0, Verbs.Length - 1)];
        string action = v switch { "move" => "move", "wheel" => "wheel-" + WheelDirections[Math.Clamp((int)wheel.value, 0, 3)], _ => v + "-" + MouseButtons[Math.Clamp((int)button.value, 0, 2)] };
        string at = Data(point).Length > 0 ? ":" + Data(point) : Data(mouseX) + "," + Data(mouseY);
        string n = v == "wheel" && Data(count).Length > 0 && Data(count) != "1" ? "*" + Data(count) : "";
        return action + "@" + at + n;
    }

    internal override void Refresh()
    {
        string input = Input();
        Enable(button, verb.value is not (0 or 5));
        Enable(wheel, verb.value == 5);
        Enable(count, verb.value == 5);
        preview.Set("Input  " + input + (InputSteps.IsValid(InputSteps.Mouse, input) ? "" : "   (not valid yet)"));
    }

    private static void Enable(TView view, bool enabled)
    {
        if (view.GetState(Views.sfDisabled) == !enabled) return;
        view.SetState(Views.sfDisabled, !enabled);
        view.DrawView();
    }

    internal override string? Apply(JsonObject o)
    {
        string input = Input();
        if (proxy && Data(point).Length > 0) return "Black-Box Mode documents use x,y cells, not named points.";
        if (!InputSteps.IsValid(InputSteps.Mouse, input)) return $"'{input}' is not a valid mouse input: enter integer cells X and Y (or a named point) and, for the wheel, 1..20 notches.";
        SetText(o, "input", input);
        return null;
    }
}
