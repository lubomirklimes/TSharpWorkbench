using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

/// <summary>Chooses the kind of a new step (or a new kind for an existing one). Proxy documents are not offered "action".</summary>
internal sealed class WorkbenchStepKindDialog : EditorDialog
{
    internal const string Title = "Add Step";
    private readonly string[] kinds;
    private readonly TListBox box;
    internal TListBox Kinds => box;
    internal string Selected => kinds[Math.Clamp(box.focused, 0, kinds.Length - 1)];

    private WorkbenchStepKindDialog(TDeskTop desktop, bool proxy, string? current)
        : base(Math.Min(76, desktop.size.x - 2), Math.Min(20, desktop.size.y), current is null ? Title : "Change Step Kind")
    {
        kinds = ScenarioSchemaV1.StepKinds.Where(k => !(proxy && k == "action")).ToArray();
        box = Picker(3, 2, size.x - 4, size.y - 4, "~K~ind of step", kinds.Select(k => k.PadRight(11) + ScenarioSchemaV1.StepTitle(k)));
        ((WorkbenchListBox)box).ActivationCommand = Views.cmOK;
        ((WorkbenchListBox)box).ActivateOnEnter = true;
        if (current is not null && Array.IndexOf(kinds, current) is >= 0 and var i) box.FocusItemNum(i);
        Buttons("C~h~oose");
        box.Select();
    }
    internal static string? Choose(TDeskTop desktop, bool proxy, string? current)
    {
        using var dialog = new WorkbenchStepKindDialog(desktop, proxy, current);
        return desktop.ExecView(dialog) == Views.cmOK ? dialog.Selected : null;
    }
}
