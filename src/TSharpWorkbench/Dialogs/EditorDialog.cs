using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// The small modal dialogs of the scenario editor: choose a step kind, edit one step (fields of its
// kind only), choose/edit one checkpoint assertion, and edit the document metadata. Each dialog starts from the existing JSON
// and writes back only the values the user changed, so untouched values keep their original spelling and order. Pickers
// list only names the bound fixture advertises; free text is validated with the runtime grammar.

// A one-line notice a dialog replaces (validation feedback).
internal sealed class EditorNotice(TRect bounds) : TStaticText(bounds, "")
{
    internal string Current => Text;
    internal void Set(string text) { if (Text == text) return; Text = text; DrawView(); }
}

// Shared layout helpers: rows of "label + control" at fixed columns.
internal abstract class EditorDialog : TDialog
{
    protected internal const int LabelX = 2, FieldX = 18;
    protected internal readonly EditorNotice notice;
    protected EditorDialog(int width, int height, string title) : base(new(0, 0, width, height), title)
    {
        options |= Views.ofCentered; helpCtx = WorkbenchApp.DialogContext;
        notice = new EditorNotice(new(2, height - 5, width - 2, height - 3));
    }
    protected internal TInputLine Input(int y, string label, string? value, int max = 128, int right = 0)
    {
        var line = new TInputLine(new(FieldX, y, right > 0 ? right : size.x - 3, y + 1), max);
        line.SetData(value ?? ""); Insert(line);
        if (label.Length > 0) Insert(new TLabel(new(LabelX, y, FieldX - 1, y + 1), label, line));
        return line;
    }
    protected internal TRadioButtons Radio(int y, int height, string label, string[] items, int value, int right = 0)
    {
        TSItem? chain = null;
        for (int i = items.Length - 1; i >= 0; i--) chain = new TSItem(items[i], chain!);
        var radio = new TRadioButtons(new(FieldX, y, right > 0 ? right : size.x - 3, y + height), chain!) { value = (uint)Math.Max(0, value) };
        Insert(radio); if (label.Length > 0) Insert(new TLabel(new(LabelX, y, FieldX - 1, y + 1), label, radio));
        return radio;
    }
    protected internal TListBox Picker(int x, int y, int right, int bottom, string label, IEnumerable<string> items)
    {
        var scroll = new TScrollBar(new(right, y + 1, right + 1, bottom)); Insert(scroll);
        var box = new WorkbenchListBox(new(x, y + 1, right, bottom), 1, scroll); Insert(box);
        var collection = new TStringCollection(); foreach (string i in items) collection.Insert(i); box.NewList(collection);
        Insert(new TLabel(new(x - 1, y, right, y + 1), label, box));
        return box;
    }
    protected internal void Buttons(string ok = "O~K~", string? extra = null, ushort extraCommand = 0)
    {
        int w = size.x, h = size.y;
        Insert(new TButton(new(w - 30, h - 3, w - 17, h - 1), ok, Views.cmOK, ButtonConstants.bfDefault));
        Insert(new TButton(new(w - 16, h - 3, w - 3, h - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfNormal));
        if (extra is not null) Insert(new TButton(new(2, h - 3, 22, h - 1), extra, extraCommand, ButtonConstants.bfNormal));
    }
    // Shrinks the dialog to its content (last content row = bottom), keeping notice and button rows below it.
    protected internal void Fit(int bottom)
    {
        int h = Math.Min(size.y, bottom + 6);
        if (h == size.y) return;
        ChangeBounds(new(0, 0, size.x, h));
        notice.ChangeBounds(new(2, h - 5, size.x - 2, h - 3));
    }
    protected internal bool Refuse(string message) { notice.Set(message); return false; }
    protected internal static string Data(TInputLine line) => line.Data.Trim();
    protected internal static bool TryNumber(TInputLine line, out int? value)
    {
        value = null;
        string text = Data(line);
        if (text.Length == 0) return true;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)) return false;
        value = n; return true;
    }
    protected internal static string? Text(JsonNode? node) => node is JsonValue v ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString() : null;
    // Writes a text value only when it changed (keeps the original spelling); empty removes it.
    protected internal static void SetText(JsonObject o, string key, string value)
    {
        if (value.Length == 0) { o.Remove(key); return; }
        if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == value) return;
        o[key] = value;
    }
    protected internal static void SetNumber(JsonObject o, string key, int? value)
    {
        if (value is null) { o.Remove(key); return; }
        if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int current) && current == value) return;
        o[key] = value.Value;
    }
}
