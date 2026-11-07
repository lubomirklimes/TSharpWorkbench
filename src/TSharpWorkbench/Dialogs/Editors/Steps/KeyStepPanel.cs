using System.Reflection;
using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using static EditorDialog;

/// <summary>"key", "keyDown" and "keyUp": one TSharpVision Keys constant, picked from the full list of names.</summary>
internal sealed class KeyStepPanel : StepPanel
{
    private static readonly string[] AllKeys = typeof(Keys).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(ushort) && f.Name.StartsWith("kb", StringComparison.Ordinal) && f.Name != "kbNoKey")
        .Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();

    private readonly TInputLine key;

    internal TInputLine Key => key;
    internal override TView? FirstField => key;

    public KeyStepPanel(ScenarioDocumentStep step) : base(step, 76, 12)
    {
        key = new TInputLine(new TRect(16, 0, 48, 1), 40);
        key.SetData(step.String("key") ?? "");
        Insert(key);
        Insert(new TLabel(new TRect(0, 0, 15, 1), "~K~ey", key));

        Insert(new NamePicker(new TRect(16, 2, 48, 12), "Key ~n~ames (Enter copies)", AllKeys, key, step.String("key")));

        Insert(new TStaticText(new TRect(50, 0, 76, 9), step.Kind switch
        {
            "key" => "One press of a\nTSharpVision Keys\nconstant, e.g. kbF10,\nkbAltX, kbEnter, kbEsc.\n\nHeld modifiers\n(modifiers steps)\ncompose with it.",
            "keyDown" => "Holds the key until a\nkeyUp or release step.",
            _ => "Releases a key held by\nan earlier keyDown step."
        }));
    }

    internal override string? Apply(JsonObject o)
    {
        if (!TestKeys.TryResolve(Data(key), out _)) return $"Unknown key '{Data(key)}': pick a name from the list (for example kbF10, kbEnter, kbAltX).";
        SetText(o, "key", Data(key));
        return null;
    }
}
