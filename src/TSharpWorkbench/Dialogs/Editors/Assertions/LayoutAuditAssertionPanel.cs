using System.Text.Json.Nodes;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

/// <summary>
/// layout-audit: optional audit scope (view selector), profile, optional rule filter and justified suppressions.
/// Rows: 6-8 scope selector, 9 profile, 10 rules, 11 rule hint, 12-13 suppressions with Add/Edit/Remove. Every save is
/// checked by the runtime schema-v1 parser; untouched fields keep their original JSON.
/// </summary>
internal sealed class LayoutAuditAssertionPanel : AssertionPanel
{
    private const int ListRight = 49;
    /// <summary>Command of the panel's buttons (never posted: they act directly; enabled so they draw active).</summary>
    internal const ushort Command = 0x7d4e;
    private readonly SelectorGroup view;
    private readonly TRadioButtons profile;
    private readonly TInputLine rules;
    private readonly TListBox suppressList;
    private readonly List<JsonObject> suppressions;
    private static readonly string[] Profiles = LayoutProfiles.All;

    /// <summary>Test seam: edits one suppression (rule, view?, reason) for the given profile. Null = cancelled.</summary>
    internal static Func<JsonObject?, string, JsonObject?>? SuppressionEditor { get; set; }

    internal TRadioButtons Profile => profile;
    internal TListBox SuppressionList => suppressList;
    internal IReadOnlyList<JsonObject> Suppressions => suppressions;
    internal string SelectedProfile => Profiles[Math.Clamp((int)profile.value, 0, Profiles.Length - 1)];

    public LayoutAuditAssertionPanel(JsonObject a)
        : base(a, 14, "Layout audit of the scope (empty: modal view or screen); ux adds UX rules.")
    {
        view = Selector(new SelectorGroup(6, "Audit scope (optional)", a["view"]), "view");
        Insert(view);

        profile = new TRadioButtons(new TRect(16, 9, 58, 10), new TSItem("~g~eometry", new TSItem("u~x~", new TSItem("~s~trict", null!))));
        profile.value = (uint)Math.Max(0, Array.IndexOf(Profiles, EditorDialog.Text(a["profile"]) ?? LayoutProfiles.Default));
        Insert(profile);
        Insert(new TLabel(new TRect(0, 9, 15, 10), "~P~rofile", profile));

        rules = new TInputLine(new TRect(16, 10, Width - 1, 11), 32 * 24);
        rules.SetData(a["rules"] is JsonArray list ? string.Join(", ", list.Select(EditorDialog.Text)) : "");
        Fields["rules"] = rules;
        Insert(rules);
        Insert(new TLabel(new TRect(0, 10, 15, 11), "~R~ules", rules));
        Insert(new TStaticText(new TRect(16, 11, Width, 12), "rule IDs, comma-separated; empty = every rule of the profile"));

        EnableCommand(Command);
        suppressions = a["suppress"] is JsonArray s ? s.OfType<JsonObject>().Select(x => (JsonObject)x.DeepClone()).ToList() : [];
        var scroll = new TScrollBar(new TRect(ListRight, 12, ListRight + 1, 14));
        Insert(scroll);
        suppressList = new TListBox(new TRect(16, 12, ListRight, 14), 1, scroll);
        Insert(suppressList);
        Insert(new TLabel(new TRect(0, 12, 15, 13), "S~u~ppress", suppressList));
        Insert(new ActionButton(new TRect(51, 12, 58, 14), "A~d~d", () => Suppression(add: true)));
        Insert(new ActionButton(new TRect(58, 12, 66, 14), "Edi~t~", () => Suppression(add: false)));
        Insert(new ActionButton(new TRect(66, 12, 76, 14), "Re~m~ove", RemoveSuppression));
        ShowSuppressions();
    }

    /// <summary>One line of the suppression list: <c>rule TType#id "text" @path: reason</c> (no view: <c>(all)</c>).</summary>
    internal static string Describe(JsonObject s)
    {
        string selector = s["view"] is JsonObject v
            ? " " + (EditorDialog.Text(v["type"]) ?? "") + (EditorDialog.Text(v["id"]) is { } id ? "#" + id : "")
                + (EditorDialog.Text(v["text"]) is { } t ? " \"" + t + "\"" : "") + (EditorDialog.Text(v["path"]) is { } path ? " @" + path : "")
            : " (all)";
        return (EditorDialog.Text(s["rule"]) ?? "?") + selector + ": " + (EditorDialog.Text(s["reason"]) ?? "");
    }

    private void ShowSuppressions()
    {
        var lines = new TStringCollection();
        foreach (var s in suppressions) lines.Insert(WorkbenchText.Fit(Describe(s), suppressList.size.x - 1));
        if (suppressions.Count == 0) lines.Insert("(none)");
        int keep = suppressList.focused;
        suppressList.NewList(lines);
        if (suppressions.Count > 0) suppressList.FocusItemNum(Math.Clamp(keep, 0, suppressions.Count - 1));
    }

    /// <summary>Adds a suppression (add) or changes the focused one through the suppression dialog (or the test seam).</summary>
    internal void Suppression(bool add)
    {
        int index = suppressList.focused;
        JsonObject? existing = add ? null : suppressions.ElementAtOrDefault(index);
        if (!add && existing is null) return;
        var edited = SuppressionEditor is { } seam ? seam(existing, SelectedProfile) : SuppressionDialog.Edit(Desktop(), existing, SelectedProfile);
        if (edited is null) return;
        if (add) { suppressions.Add(edited); index = suppressions.Count - 1; }
        else suppressions[index] = edited;
        ShowSuppressions();
        suppressList.FocusItemNum(index);
    }

    internal void RemoveSuppression()
    {
        if (suppressions.Count == 0) return;
        suppressions.RemoveAt(Math.Clamp(suppressList.focused, 0, suppressions.Count - 1));
        ShowSuppressions();
    }

    private TDeskTop Desktop()
    {
        for (TView? v = owner; v is not null; v = v.owner) if (v is TDeskTop d) return d;
        throw new InvalidOperationException("The assertion editor is not on the desktop.");
    }

    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evKeyDown && GetState(Views.sfFocused) && ReferenceEquals(current, suppressList))
        {
            switch (ev.keyDown.keyCode)
            {
                case Keys.kbIns: ClearEvent(ref ev); Suppression(add: true); return;
                case Keys.kbEnter when suppressions.Count > 0: ClearEvent(ref ev); Suppression(add: false); return;
                case Keys.kbDel: ClearEvent(ref ev); RemoveSuppression(); return;
            }
        }
        base.HandleEvent(ref ev);
    }

    protected override string? SaveFields(JsonObject o)
    {
        if (view.Save(o, "view", Required("view"), kind) is { } error) return error;

        string chosen = SelectedProfile;
        if (o.ContainsKey("profile") || chosen != LayoutProfiles.Default) EditorDialog.SetText(o, "profile", chosen);

        var ids = EditorDialog.Data(rules).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var before = (o["rules"] as JsonArray)?.Select(EditorDialog.Text).ToArray();
        if (ids.Length == 0) o.Remove("rules");
        else if (before is null || !before.SequenceEqual(ids)) o["rules"] = new JsonArray(ids.Select(r => (JsonNode?)r).ToArray());

        var now = new JsonArray(suppressions.Select(s => (JsonNode?)s.DeepClone()).ToArray());
        if (suppressions.Count == 0 && o["suppress"] is not JsonArray { Count: 0 }) o.Remove("suppress");
        else if (o["suppress"] is not JsonArray old || old.ToJsonString() != now.ToJsonString()) o["suppress"] = now;

        // The runtime parser is the authority (profile/rule membership, uniqueness, limits, reasons).
        var (_, problem) = AssertionJson.Parse([o], 80, 25);
        return problem is null ? null : "Not valid schema v1: " + Strip(problem);
    }

    // The parser's message carries the in-memory document's path and step prefix; keep the actionable part.
    private static string Strip(string message)
    {
        int i = message.LastIndexOf(": ", StringComparison.Ordinal);
        return i >= 0 && i + 2 < message.Length ? message[(i + 2)..] : message;
    }

    /// <summary>A button that runs its action directly (works whatever control of the dialog is focused).</summary>
    private sealed class ActionButton(TRect bounds, string title, Action action) : TButton(bounds, title, LayoutAuditAssertionPanel.Command, ButtonConstants.bfNormal)
    {
        public override void Press() => action();
    }
}

/// <summary>Edits one layout-audit suppression: the rule (from the profile's rules), an optional view selector, the reason.</summary>
internal sealed class SuppressionDialog : EditorDialog
{
    private readonly TListBox rule;
    private readonly TInputLine type, id, text, path, reason;
    private readonly string[] ids;
    private readonly JsonObject? original;
    internal JsonObject? Result { get; private set; }

    internal SuppressionDialog(TDeskTop desktop, JsonObject? suppression, string profile)
        : base(Math.Min(76, desktop.size.x), Math.Min(21, desktop.size.y), suppression is null ? "Add Suppression" : "Change Suppression")
    {
        original = suppression;
        ids = LayoutRules.All.Where(r => LayoutRules.InProfile(r.Id, profile)).Select(r => r.Id).ToArray();
        int w = size.x;
        rule = Picker(3, 2, w - 4, 9, $"~R~ule (profile {profile})", LayoutRules.All.Where(r => ids.Contains(r.Id)).Select(r => r.Id.PadRight(22) + r.Title));
        if (Array.IndexOf(ids, Text(suppression?["rule"])) is >= 0 and var at) rule.FocusItemNum(at);
        Insert(new TStaticText(new(LabelX, 10, w - 2, 11), "View (optional; empty = the rule on every view):"));
        var v = suppression?["view"] as JsonObject;
        type = Input(11, "~t~ype", Text(v?["type"]), 200, 44);
        id = Line(52, 11, "~i~d", Text(v?["id"]));
        text = Input(12, "te~x~t", Text(v?["text"]), 200, 44);
        path = Line(52, 12, "~p~ath", Text(v?["path"]));
        reason = Input(14, "Re~a~son", Text(suppression?["reason"]), ExternalScenarios.MaxSuppressionReason);
        Insert(notice); notice.Set("A suppression needs a reason (at most 200 characters); it is reported with the results.");
        Buttons();
        rule.Select();
    }

    private TInputLine Line(int x, int y, string label, string? value)
    {
        var line = new TInputLine(new(x, y, size.x - 3, y + 1), 200);
        line.SetData(value ?? ""); Insert(line);
        Insert(new TLabel(new(x - 6, y, x - 1, y + 1), label, line));
        return line;
    }

    internal static JsonObject? Edit(TDeskTop desktop, JsonObject? suppression, string profile)
    {
        using var dialog = new SuppressionDialog(desktop, suppression, profile);
        return desktop.ExecView(dialog) == Views.cmOK ? dialog.Result : null;
    }

    public override bool Valid(ushort command)
    {
        if (command != Views.cmOK) return base.Valid(command);
        if (ids.Length == 0) return Refuse("The profile has no rules.");
        var o = original is null ? new JsonObject() : (JsonObject)original.DeepClone();
        SetText(o, "rule", ids[Math.Clamp(rule.focused, 0, ids.Length - 1)]);
        var parts = new[] { ("type", type), ("id", id), ("text", text), ("path", path) };
        if (parts.All(p => Data(p.Item2).Length == 0)) o.Remove("view");
        else
        {
            var selector = o["view"] as JsonObject ?? new JsonObject();
            foreach (var (key, line) in parts) SetText(selector, key, Data(line));
            o["view"] = selector;
        }
        string why = Data(reason);
        if (why.Length == 0) return Refuse("Enter the reason for this suppression (why the finding is intended).");
        if (why.Length > ExternalScenarios.MaxSuppressionReason) return Refuse($"The reason is limited to {ExternalScenarios.MaxSuppressionReason} characters.");
        SetText(o, "reason", why);
        // Keep the schema field order: rule, view, reason.
        var ordered = new JsonObject { ["rule"] = o["rule"]!.DeepClone() };
        if (o["view"] is { } sel) ordered["view"] = sel.DeepClone();
        ordered["reason"] = o["reason"]!.DeepClone();
        Result = ordered;
        return base.Valid(command);
    }
}
