using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

/// <summary>
/// The fields of one step kind as a plain Turbo Vision group: each kind inserts its own controls with fixed
/// rectangles in its constructor (labels in column 0, fields from column 16) and writes them back in <see cref="Apply"/>.
/// <see cref="WorkbenchScenarioStepDialog"/> sizes itself around the group and owns the note, notice and buttons.
/// </summary>
internal abstract class StepPanel : FieldGroup
{
    protected readonly ScenarioDocumentStep step;

    protected StepPanel(ScenarioDocumentStep step, int width, int height) : base(new TRect(0, 0, width, height))
    {
        this.step = step;
    }

    /// <summary>Shows a message in the dialog's notice row.</summary>
    internal Action<string> Notify { get; set; } = _ => { };
    /// <summary>The field focused when the dialog opens; null focuses the note.</summary>
    internal virtual TView? FirstField => null;
    /// <summary>1 = a one-line note; more = a multi-line note of that many rows.</summary>
    internal virtual int NoteLines => 1;
    /// <summary>Commands of the panel's own buttons; the dialog routes them here whatever is focused.</summary>
    internal virtual bool OwnsCommand(ushort command) => false;
    /// <summary>Validates the fields and writes changed values into <paramref name="o"/>; returns an error or null.</summary>
    internal virtual string? Apply(JsonObject o) => null;
    /// <summary>Refreshes derived state (enabled controls, previews) after an event.</summary>
    internal virtual void Refresh() { }

    internal static StepPanel Create(ScenarioDocumentStep step, bool proxy, ScenarioDescriptor? fixture, int maxWidth) => step.Kind switch
    {
        "key" or "keyDown" or "keyUp" => new KeyStepPanel(step),
        "text" => new TextStepPanel(step),
        "modifiers" => new ModifiersStepPanel(step),
        "mouse" => new MouseStepPanel(step, proxy, fixture),
        "resize" => new ResizeStepPanel(step),
        "action" => new ActionStepPanel(step, fixture),
        "release" => new ReleaseStepPanel(step),
        "checkpoint" => new CheckpointStepPanel(step, fixture),
        _ => new OtherStepPanel(step)
    };

    private sealed class OtherStepPanel : StepPanel
    {
        public OtherStepPanel(ScenarioDocumentStep step) : base(step, 72, 1)
        {
            Insert(new TStaticText(new TRect(0, 0, 72, 1), "This step kind has no fields in the editor; its properties are kept."));
        }
    }
}
