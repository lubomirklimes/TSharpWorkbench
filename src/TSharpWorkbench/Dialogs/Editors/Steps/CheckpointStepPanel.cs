using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using static EditorDialog;

/// <summary>"checkpoint": ID (with the fixture's expectations), evidence, retry and the assertion list.</summary>
/// <remarks>The multi-line note (<see cref="NoteLines"/>) is placed by the dialog below the panel.</remarks>
internal sealed class CheckpointStepPanel : StepPanel
{
    internal const ushort AssertionAdd = 0x7d2d, AssertionEdit = 0x7d2e, AssertionDelete = 0x7d2f;
    private const int W = 76, AssertionRows = 5;

    private readonly TInputLine id, attempts, interval;
    private readonly TCheckBoxes evidence;
    private readonly TListBox assertionList;
    private readonly JsonArray assertions;

    internal TInputLine Id => id;
    internal TListBox AssertionList => assertionList;
    internal override TView? FirstField => id;
    internal override int NoteLines => 3;
    internal override bool OwnsCommand(ushort command) => command is AssertionAdd or AssertionEdit or AssertionDelete;

    private static int Top(ScenarioDescriptor? fixture) => (fixture?.Contract?.Checkpoints ?? []).Length > 0 ? 7 : 2;

    public CheckpointStepPanel(ScenarioDocumentStep step, ScenarioDescriptor? fixture)
        : base(step, W, Top(fixture) + 4 + 2 + AssertionRows + 2)
    {
        // Row 0: ID; rows 1..5: the fixture's expected checkpoint IDs (only when advertised).
        id = new TInputLine(new TRect(16, 0, W - 1, 1), 64);
        id.SetData(step.String("id") ?? "");
        Insert(id);
        Insert(new TLabel(new TRect(0, 0, 15, 1), "Checkpoint ~I~D", id));
        string[] expectations = fixture?.Contract?.Checkpoints ?? [];
        if (expectations.Length > 0)
            Insert(new NamePicker(new TRect(16, 1, W, 6), "E~x~pectations (Enter copies)", expectations, id, step.String("id")));

        int y = Top(fixture);

        var ev = step.Node("evidence") as JsonArray;
        uint mask = 0;
        for (int i = 0; i < ScenarioSchemaV1.Evidence.Length; i++) if (ev?.Any(e => Text(e) == ScenarioSchemaV1.Evidence[i]) == true) mask |= 1u << i;
        evidence = new TCheckBoxes(new TRect(16, y, 58, y + 1), new TSItem("~v~iew-tree", new TSItem("~m~enu", new TSItem("~s~tatus", null!))));
        evidence.value = mask;
        Insert(evidence);
        Insert(new TLabel(new TRect(0, y, 15, y + 1), "Evidence", evidence));
        y += 2;

        // Attempts and interval belong together: one row, with the rule next to them.
        var retry = step.Node("retry") as JsonObject;
        attempts = new TInputLine(new TRect(16, y, 21, y + 1), 3);
        attempts.SetData(Text(retry?["attempts"]) ?? "");
        Insert(attempts);
        Insert(new TLabel(new TRect(0, y, 15, y + 1), "Retry a~t~tempts", attempts));
        interval = new TInputLine(new TRect(39, y, 47, y + 1), 6);
        interval.SetData(Text(retry?["intervalMilliseconds"]) ?? "");
        Insert(interval);
        Insert(new TLabel(new TRect(23, y, 38, y + 1), "Interva~l~ (ms)", interval));
        Insert(new TStaticText(new TRect(49, y, W, y + 1), "both or neither"));
        y += 2;

        // Assertions: caption, list, one free row, buttons.
        assertions = step.Node("assertions") as JsonArray ?? [];
        var scroll = new TScrollBar(new TRect(W - 2, y + 1, W - 1, y + 1 + AssertionRows));
        Insert(scroll);
        assertionList = new TListBox(new TRect(0, y + 1, W - 2, y + 1 + AssertionRows), 1, scroll);
        Insert(assertionList);
        Insert(new TLabel(new TRect(0, y, 30, y + 1), "~A~ssertions", assertionList));
        y += 2 + AssertionRows;
        Insert(new TButton(new TRect(0, y, 12, y + 2), "A~d~d...", AssertionAdd, ButtonConstants.bfNormal));
        Insert(new TButton(new TRect(14, y, 26, y + 2), "~E~dit...", AssertionEdit, ButtonConstants.bfNormal));
        Insert(new TButton(new TRect(28, y, 41, y + 2), "~R~emove", AssertionDelete, ButtonConstants.bfNormal));
        ShowAssertions();
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && GetState(Views.sfFocused) && ReferenceEquals(current, assertionList))
        {
            ushort c = ev.keyDown.keyCode switch { Keys.kbEnter => AssertionEdit, Keys.kbIns => AssertionAdd, Keys.kbDel => AssertionDelete, _ => (ushort)0 };
            if (c != 0) { ClearEvent(ref ev); Assertion(c); return; }
        }
        if (ev.What == Events.evCommand && OwnsCommand(ev.message.command))
        {
            ushort c = ev.message.command;
            ClearEvent(ref ev);
            Assertion(c);
            return;
        }
        base.HandleEvent(ref ev);
    }

    private void ShowAssertions()
    {
        var lines = new TStringCollection();
        int i = 1;
        foreach (var a in assertions)
            lines.Insert(WorkbenchText.Fit(a is JsonObject o ? $"{i++,2}. {Text(o["kind"]),-16} {Text(o["id"]),-16} " + string.Join("  ", o.Where(p => p.Key is not ("kind" or "id")).Select(p => p.Key + "=" + (p.Value?.ToJsonString() ?? "null")))
                : $"{i++,2}. {a?.ToJsonString()}", assertionList.size.x - 1));
        if (assertions.Count == 0) lines.Insert("No assertions (capture only).");
        int keep = assertionList.focused;
        assertionList.NewList(lines);
        if (assertions.Count > 0) assertionList.FocusItemNum(Math.Clamp(keep, 0, assertions.Count - 1));
    }

    private void Assertion(ushort command)
    {
        var desktop = (TDeskTop)owner!.owner!;
        int index = assertionList.focused;
        if (command == AssertionDelete)
        {
            if (assertions.Count == 0) return;
            assertions.RemoveAt(Math.Clamp(index, 0, assertions.Count - 1));
            ShowAssertions();
            return;
        }
        if (command == AssertionEdit && assertions.ElementAtOrDefault(index) is JsonObject existing)
        {
            if (WorkbenchAssertionDialog.Edit(desktop, existing, false) is { } edited) { assertions[index] = edited; ShowAssertions(); }
            return;
        }
        if (command == AssertionEdit && assertions.Count > 0) { Notify("This assertion is not an object; edit it in the external editor."); return; }
        string? kind = WorkbenchAssertionDialog.ChooseKind(desktop, null);
        if (kind is null) return;
        int n = assertions.Count + 1;
        string newId = kind + (assertions.OfType<JsonObject>().Any(a => Text(a["id"]) == kind) ? "-" + n : "");
        if (WorkbenchAssertionDialog.Edit(desktop, new JsonObject { ["id"] = newId, ["kind"] = kind }, true) is { } created)
        {
            assertions.Add(created);
            ShowAssertions();
            assertionList.FocusItemNum(assertions.Count - 1);
        }
    }

    internal override string? Apply(JsonObject o)
    {
        if (Data(id).Length == 0) return "A checkpoint needs an ID.";
        SetText(o, "id", Data(id));
        var chosen = ScenarioSchemaV1.Evidence.Where((_, i) => (evidence.value & (1u << i)) != 0).ToArray();
        var before = (o["evidence"] as JsonArray)?.Select(Text).ToArray();
        if (chosen.Length == 0) o.Remove("evidence");
        else if (before is null || !before.Order().SequenceEqual(chosen.Order())) o["evidence"] = new JsonArray(chosen.Select(e => (JsonNode?)e).ToArray());
        if (!TryNumber(attempts, out int? a) || !TryNumber(interval, out int? ms)) return "Retry attempts and interval are integers.";
        if (a is null && ms is null) o.Remove("retry");
        else
        {
            if (a is null || ms is null) return "Retry needs both attempts (1..20) and an interval (0..30000 ms), or neither.";
            if (a is < 1 or > 20 || ms is < 0 or > 30000) return "Retry attempts are 1..20 and the interval 0..30000 ms.";
            var retry = o["retry"] as JsonObject ?? new JsonObject();
            SetNumber(retry, "attempts", a);
            SetNumber(retry, "intervalMilliseconds", ms);
            if (o["retry"] is null) o["retry"] = retry;
        }
        var previous = step.Node("assertions") as JsonArray;
        if (assertions.Count == 0 && previous is null) { }
        else if (!JsonNode.DeepEquals(previous, assertions))
            o["assertions"] = assertions.Count == 0 && previous is { Count: > 0 } ? new JsonArray() : assertions.DeepClone();
        return null;
    }
}
