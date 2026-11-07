using System.Text.Json.Nodes;
using TSharpVision;

/// <summary>
/// A view selector: a title row, an optional "no view" check box, then type / id on one row and text / path below.
/// Width is <see cref="AssertionPanel.Width"/>; height is 3 rows (4 with the check box).
/// </summary>
internal sealed class SelectorGroup : FieldGroup
{
    private static readonly string[] Names = ["type", "id", "text", "path"];
    internal readonly TInputLine Type, Id, Text, Path;
    internal readonly TCheckBoxes? NoView;
    internal TInputLine[] Parts => [Type, Id, Text, Path];

    internal static int HeightFor(bool noView) => noView ? 4 : 3;

    internal SelectorGroup(int y, string title, JsonNode? node, bool noView = false, bool noViewChecked = false)
        : base(new TRect(0, y, AssertionPanel.Width, y + HeightFor(noView)))
    {
        Insert(new TStaticText(new TRect(0, 0, AssertionPanel.Width, 1), title + ": at least one of type, id, text, path"));
        int row = 1;
        if (noView)
        {
            NoView = new TCheckBoxes(new TRect(16, row, AssertionPanel.Width - 1, row + 1), new TSItem("~N~o modal view (\"view\": null)", null!));
            NoView.value = noViewChecked ? 1u : 0u;
            Insert(NoView);
            row++;
        }

        Type = new TInputLine(new TRect(16, row, 44, row + 1), 200);
        Insert(Type);
        Insert(new TLabel(new TRect(2, row, 15, row + 1), "type", Type));
        Id = new TInputLine(new TRect(52, row, AssertionPanel.Width - 1, row + 1), 200);
        Insert(Id);
        Insert(new TLabel(new TRect(46, row, 51, row + 1), "id", Id));
        row++;
        Text = new TInputLine(new TRect(16, row, 44, row + 1), 200);
        Insert(Text);
        Insert(new TLabel(new TRect(2, row, 15, row + 1), "text", Text));
        Path = new TInputLine(new TRect(52, row, AssertionPanel.Width - 1, row + 1), 200);
        Insert(Path);
        Insert(new TLabel(new TRect(46, row, 51, row + 1), "path", Path));

        var parts = Parts;
        for (int i = 0; i < 4; i++) parts[i].SetData(EditorDialog.Text((node as JsonObject)?[Names[i]]) ?? "");
    }

    /// <summary>Writes the selector to <paramref name="o"/>[<paramref name="field"/>]; returns an error or null.</summary>
    internal string? Save(JsonObject o, string field, bool required, string kind)
    {
        if (NoView is not null && NoView.value == 1)
        {
            if (!(o.ContainsKey(field) && o[field] is null)) o[field] = null;
            return null;
        }
        var values = Parts.Select(EditorDialog.Data).ToArray();
        if (values.All(v => v.Length == 0))
        {
            if (required) return $"{kind} needs a {field} selector: enter at least one of type, id, text or path.";
            o.Remove(field);
            return null;
        }
        var selector = o[field] as JsonObject ?? new JsonObject();
        for (int i = 0; i < 4; i++) EditorDialog.SetText(selector, Names[i], values[i]);
        if (o[field] is not JsonObject) o[field] = selector;
        return null;
    }
}
