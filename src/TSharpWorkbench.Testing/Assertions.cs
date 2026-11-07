using System.Globalization;
using System.Text.Json.Serialization;

namespace TSharpWorkbench.Testing;

/// <summary>Selects views in a captured <see cref="ViewTree"/>. Every given field must match: <see cref="Type"/> matches the
/// full type name, the short name or the nearest framework type (<see cref="ViewNode.Kind"/>); <see cref="Id"/> a diagnostic
/// identity; <see cref="Text"/> the model text exactly; <see cref="Path"/> a capture path. Hidden views match too.</summary>
public sealed record ViewSelector(string? Type = null, string? Id = null, string? Text = null, string? Path = null)
{
    public static ViewSelector OfType(string type) => new(Type: type);
    public static ViewSelector ById(string id) => new(Id: id);

    public bool Matches(ViewNode node) =>
        (Type is null || node.Type == Type || node.Kind == Type || node.Type.EndsWith("." + Type, StringComparison.Ordinal))
        && (Id is null || node.Id == Id) && (Text is null || node.Text == Text) && (Path is null || node.Path == Path);

    public override string ToString() => string.Join(" ", new[] { Type is null ? null : "type=" + Type, Id is null ? null : "id=" + Id,
        Text is null ? null : "text=\"" + Text + "\"", Path is null ? null : "path=" + Path }.OfType<string>());
}

/// <summary>A declarative check of one checkpoint. Assertions are data: the target declares them with its scenario,
/// the controller evaluates them against the captured cells and evidence. Fields used depend on <see cref="Kind"/>
/// (see <see cref="AssertionKinds"/>); build them with <see cref="UiAssert"/>.</summary>
public sealed record AssertionSpec(string Id, string Kind, string Severity = AssertionSeverity.Error,
    ViewSelector? View = null, ViewSelector? Other = null, string? Text = null, string? State = null, string? Edge = null,
    int? X = null, int? Y = null, int? Width = null, int? Value = null, int? Min = null, int? Max = null, int? Level = null)
{
    /// <summary>Layout-audit: the profile (<see cref="LayoutProfiles"/>; null = <see cref="LayoutProfiles.Default"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Profile { get; init; }
    /// <summary>Layout-audit: run only these rules of the profile (<see cref="LayoutRules"/>); null = all of them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Rules { get; init; }
    /// <summary>Layout-audit: justified suppressions (by rule, optionally by view selector).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LayoutSuppression[]? Suppress { get; init; }

    /// <summary>Evidence the controller must request for this assertion to be decidable.</summary>
    public string[] RequiredEvidence() => Kind switch
    {
        AssertionKinds.TextAt or AssertionKinds.ScreenContains or AssertionKinds.AttributeAt => [],
        AssertionKinds.MenuDepth or AssertionKinds.MenuSelected or AssertionKinds.MenuItemState or AssertionKinds.SubmenuOpen => [Protocol.EvidenceKinds.Menu],
        AssertionKinds.StatusItem or AssertionKinds.StatusContext or AssertionKinds.StatusHint => [Protocol.EvidenceKinds.Status],
        _ => [Protocol.EvidenceKinds.ViewTree],
    };
}

public static class AssertionSeverity { public const string Error = "error", Warning = "warning"; }

public static class AssertionOutcomes { public const string Pass = "PASS", Fail = "FAIL", Unsupported = "UNSUPPORTED", Skipped = "SKIPPED"; }

public static class AssertionKinds
{
    public const string ViewExists = "view-exists", ViewAbsent = "view-absent", ViewVisible = "view-visible", ViewFocused = "view-focused",
        Modal = "modal", WithinScreen = "within-screen", WithinView = "within-view", NotClipped = "not-clipped",
        Aligned = "aligned", Spacing = "spacing", Disjoint = "disjoint", Shadow = "shadow",
        MenuDepth = "menu-depth", MenuSelected = "menu-selected", MenuItemState = "menu-item-state", SubmenuOpen = "submenu-open",
        StatusItem = "status-item", StatusContext = "status-context", StatusHint = "status-hint",
        TextAt = "text-at", ScreenContains = "screen-contains", AttributeAt = "attribute-at",
        /// <summary>Composite audit of geometry, text fit and (profile ux/strict) UX heuristics of one state.</summary>
        LayoutAudit = "layout-audit";
    public static readonly string[] All = [ViewExists, ViewAbsent, ViewVisible, ViewFocused, Modal, WithinScreen, WithinView, NotClipped,
        Aligned, Spacing, Disjoint, Shadow, MenuDepth, MenuSelected, MenuItemState, SubmenuOpen, StatusItem, StatusContext, StatusHint,
        TextAt, ScreenContains, AttributeAt, LayoutAudit];
}

/// <summary>Result of one assertion at one checkpoint. <see cref="Location"/> names the view path or cell range concerned.</summary>
public sealed record AssertionResult(string Id, string Kind, string Outcome, string Severity, string Expected, string? Actual,
    string Explanation, string Scenario, string Checkpoint, string Grid, string? Location)
{
    /// <summary>The findings and coverage of a layout-audit assertion (null for every other kind).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LayoutAuditReport? Layout { get; init; }
}

/// <summary>Factory for <see cref="AssertionSpec"/>. IDs are stable names (lowercase words joined by hyphens), unique per checkpoint.</summary>
public static class UiAssert
{
    public static AssertionSpec Exists(string id, ViewSelector view) => new(id, AssertionKinds.ViewExists, View: view);
    public static AssertionSpec Absent(string id, ViewSelector view) => new(id, AssertionKinds.ViewAbsent, View: view);
    public static AssertionSpec Visible(string id, ViewSelector view) => new(id, AssertionKinds.ViewVisible, View: view);
    public static AssertionSpec Focused(string id, ViewSelector view) => new(id, AssertionKinds.ViewFocused, View: view);
    /// <summary>The executing modal view matches <paramref name="view"/>; null asserts that no modal view executes.</summary>
    public static AssertionSpec Modal(string id, ViewSelector? view) => new(id, AssertionKinds.Modal, View: view);
    public static AssertionSpec WithinScreen(string id, ViewSelector view) => new(id, AssertionKinds.WithinScreen, View: view);
    /// <summary><paramref name="view"/> lies inside <paramref name="container"/>; null container = the desktop working area.</summary>
    public static AssertionSpec Within(string id, ViewSelector view, ViewSelector? container = null) => new(id, AssertionKinds.WithinView, View: view, Other: container);
    public static AssertionSpec NotClipped(string id, ViewSelector view) => new(id, AssertionKinds.NotClipped, View: view);
    /// <summary>Every view matched by <paramref name="views"/> shares <paramref name="edge"/> (left, right, top, bottom, center-x, center-y)
    /// with the first view matched by <paramref name="reference"/> (or with each other when reference is null).</summary>
    public static AssertionSpec Aligned(string id, ViewSelector views, string edge, ViewSelector? reference = null) =>
        new(id, AssertionKinds.Aligned, View: views, Other: reference, Edge: edge);
    /// <summary>Gap in cells between two views along <paramref name="axis"/> (<c>horizontal</c> or <c>vertical</c>) is within min..max.</summary>
    public static AssertionSpec Spacing(string id, ViewSelector first, ViewSelector second, string axis, int min, int max) =>
        new(id, AssertionKinds.Spacing, View: first, Other: second, Edge: axis, Min: min, Max: max);
    public static AssertionSpec Disjoint(string id, ViewSelector first, ViewSelector second) => new(id, AssertionKinds.Disjoint, View: first, Other: second);
    /// <summary>The view's shadow cells (right and bottom bands of <c>TView.shadowSize</c>, inside the screen) carry
    /// <paramref name="attribute"/>. The framework exposes no public shadow attribute, so the application supplies it.</summary>
    public static AssertionSpec Shadow(string id, ViewSelector view, int? attribute) => new(id, AssertionKinds.Shadow, View: view, Value: attribute);
    public static AssertionSpec MenuDepth(string id, int depth) => new(id, AssertionKinds.MenuDepth, Value: depth);
    /// <summary>The selected entry of menu level <paramref name="level"/> (0 = bar, default = deepest open level) is <paramref name="text"/>
    /// and the cells show it highlighted.</summary>
    public static AssertionSpec MenuSelected(string id, string text, int? level = null) => new(id, AssertionKinds.MenuSelected, Text: text, Level: level);
    /// <summary>The entry <paramref name="text"/> of menu level <paramref name="level"/> (default deepest) is <c>enabled</c> or <c>disabled</c>.</summary>
    public static AssertionSpec MenuItem(string id, string text, string state, int? level = null) => new(id, AssertionKinds.MenuItemState, Text: text, State: state, Level: level);
    /// <summary>An open level whose parent entry is <paramref name="parentItem"/> exists.</summary>
    public static AssertionSpec SubmenuOpen(string id, string parentItem) => new(id, AssertionKinds.SubmenuOpen, Text: parentItem);
    /// <summary>Status item <paramref name="text"/> is <c>visible</c> (drawn and matching the cells), <c>absent</c>, <c>enabled</c> or <c>disabled</c>.</summary>
    public static AssertionSpec StatusItem(string id, string text, string state = "visible") => new(id, AssertionKinds.StatusItem, Text: text, State: state);
    public static AssertionSpec StatusContext(string id, int helpContext) => new(id, AssertionKinds.StatusContext, Value: helpContext);
    public static AssertionSpec StatusHint(string id, string hint) => new(id, AssertionKinds.StatusHint, Text: hint);
    public static AssertionSpec TextAt(string id, int x, int y, string text) => new(id, AssertionKinds.TextAt, X: x, Y: y, Text: text);
    public static AssertionSpec ScreenContains(string id, string text) => new(id, AssertionKinds.ScreenContains, Text: text);
    /// <summary>Cells x..x+width-1 on row y carry the logical attribute <paramref name="attribute"/> (background&lt;&lt;4 | foreground).</summary>
    public static AssertionSpec AttributeAt(string id, int x, int y, int attribute, int width = 1) => new(id, AssertionKinds.AttributeAt, X: x, Y: y, Value: attribute, Width: width);

    /// <summary>Audits the layout of <paramref name="scope"/> (null = the executing modal view, else the whole screen).
    /// Default severity warning: findings never fail a scenario unless the severity is raised to error.</summary>
    public static AssertionSpec LayoutAudit(string id, ViewSelector? scope = null, string profile = LayoutProfiles.Default, string[]? rules = null,
        LayoutSuppression[]? suppress = null) =>
        new(id, AssertionKinds.LayoutAudit, AssertionSeverity.Warning, View: scope) { Profile = profile, Rules = rules, Suppress = suppress };

    public static AssertionSpec AsWarning(this AssertionSpec spec) => spec with { Severity = AssertionSeverity.Warning };
}

/// <summary>Rectangle primitives used by geometry assertions; usable directly by application-owned rules.</summary>
public static class Geometry
{
    public static bool Contains(CellRect outer, CellRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    public static bool Intersects(CellRect a, CellRect b) => !Intersection(a, b).IsEmpty;
    public static CellRect Intersection(CellRect a, CellRect b) => Inspectors.Intersect(a, b);
    /// <summary>Cells of <paramref name="rect"/> that fall outside <paramref name="clip"/>.</summary>
    public static int ClippedCells(CellRect rect, CellRect clip) => rect.Width * rect.Height - Area(Intersection(rect, clip));
    public static int Area(CellRect r) => r.IsEmpty ? 0 : r.Width * r.Height;

    public static int? Edge(CellRect r, string edge) => edge switch
    {
        "left" => r.X, "right" => r.Right, "top" => r.Y, "bottom" => r.Bottom,
        "center-x" => r.X * 2 + r.Width, "center-y" => r.Y * 2 + r.Height, // doubled: exact for odd sizes
        _ => null,
    };

    /// <summary>Empty cells between the two rectangles along the axis (negative when they overlap on it).</summary>
    public static int Gap(CellRect a, CellRect b, string axis) => axis switch
    {
        "horizontal" => b.X >= a.X ? b.X - a.Right : a.X - b.Right,
        "vertical" => b.Y >= a.Y ? b.Y - a.Bottom : a.Y - b.Bottom,
        _ => throw new ArgumentException("Axis must be horizontal or vertical."),
    };
}

/// <summary>Evaluates <see cref="AssertionSpec"/>s against one checkpoint's cells and inspection. Missing evidence makes an
/// assertion UNSUPPORTED (never a guessed PASS).</summary>
public static class AssertionEvaluator
{
    public static AssertionResult[] Evaluate(IEnumerable<AssertionSpec> specs, ScreenSnapshot cells, CheckpointInspection? inspection,
        string scenario, string checkpoint)
    {
        string grid = ScenarioSelection.Format(cells.Width, cells.Height);
        return specs.Select(spec =>
        {
            AssertionResult R(string outcome, string expected, string? actual, string explanation, string? location = null) =>
                new(spec.Id, spec.Kind, outcome, spec.Severity, expected, actual, explanation, scenario, checkpoint, grid, location);
            try
            {
                foreach (string evidence in spec.RequiredEvidence())
                {
                    bool missing = evidence switch
                    {
                        Protocol.EvidenceKinds.ViewTree => inspection?.ViewTree is null,
                        Protocol.EvidenceKinds.Menu => inspection?.Menu is null,
                        _ => inspection?.Status is null,
                    };
                    if (missing)
                    {
                        string? failure = inspection?.Errors.FirstOrDefault(e => e.StartsWith(evidence + ":", StringComparison.Ordinal));
                        return R(AssertionOutcomes.Unsupported, spec.Kind, null, failure ?? $"'{evidence}' evidence was not captured at this checkpoint.");
                    }
                }
                return Evaluate(spec, cells, inspection, R);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { return R(AssertionOutcomes.Unsupported, spec.Kind, null, "Assertion could not be evaluated: " + ex.Message); }
        }).ToArray();
    }

    private delegate AssertionResult Make(string outcome, string expected, string? actual, string explanation, string? location = null);

    private static AssertionResult Evaluate(AssertionSpec s, ScreenSnapshot cells, CheckpointInspection? inspection, Make R)
    {
        const string Pass = AssertionOutcomes.Pass, Fail = AssertionOutcomes.Fail, Unsupported = AssertionOutcomes.Unsupported;
        var screen = new CellRect(0, 0, cells.Width, cells.Height);
        ViewTree? tree = inspection?.ViewTree;
        ViewNode[] Find(ViewSelector? selector) => selector is null || tree is null ? [] : All(tree.Root).Where(selector.Matches).ToArray();
        string Show(CellRect? r) => r?.ToString() ?? "hidden";

        // Assertions about one view need an unambiguous match.
        (ViewNode? Node, AssertionResult? Problem) Single(ViewSelector? selector, string role = "view")
        {
            if (selector is null) return (null, R(Unsupported, s.Kind, null, $"No {role} selector given."));
            var found = Find(selector);
            return found.Length switch
            {
                1 => (found[0], null),
                0 => (null, R(Fail, $"{role} [{selector}]", "no match", $"No view matched [{selector}].")),
                _ => (null, R(Fail, $"one {role} [{selector}]", $"{found.Length} matches",
                    $"Selector is ambiguous: {string.Join(", ", found.Take(5).Select(n => n.Path))}.", found[0].Path)),
            };
        }

        switch (s.Kind)
        {
            case AssertionKinds.ViewExists:
            {
                var found = Find(s.View);
                return found.Length > 0 ? R(Pass, $"view [{s.View}]", $"{found.Length} match(es)", "View present in the tree.", found[0].Path)
                    : R(Fail, $"view [{s.View}]", "no match", "No view in the captured tree matched.");
            }
            case AssertionKinds.ViewAbsent:
            {
                var found = Find(s.View);
                return found.Length == 0 ? R(Pass, $"no view [{s.View}]", "no match", "No view matched.")
                    : R(Fail, $"no view [{s.View}]", $"{found.Length} match(es)", "A view that must be absent is present.", found[0].Path);
            }
            case AssertionKinds.ViewVisible:
            {
                var (n, p) = Single(s.View); if (p is not null) return p;
                bool ok = n!.Visible is { IsEmpty: false };
                return R(ok ? Pass : Fail, "visible", n.Visible is null ? "hidden" : n.Visible.Value.IsEmpty ? "fully clipped" : "visible " + n.Visible,
                    ok ? "View and all owners are visible and part of it is on screen." : "View is hidden or entirely clipped.", n.Path);
            }
            case AssertionKinds.ViewFocused:
            {
                var (n, p) = Single(s.View); if (p is not null) return p;
                string? focusedType = tree!.FocusedPath is { } fp ? All(tree.Root).FirstOrDefault(x => x.Path == fp)?.Type : null;
                bool ok = tree.FocusedPath == n!.Path;
                return R(ok ? Pass : Fail, $"focus on {n.Path} ({n.Kind})", tree.FocusedPath is null ? "none" : $"{tree.FocusedPath} ({focusedType})",
                    ok ? "Deepest current view of the modal view is the expected one." : "Focus is on a different view.", n.Path);
            }
            case AssertionKinds.Modal:
            {
                ViewNode? modal = tree!.ModalPath is { } mp ? All(tree.Root).FirstOrDefault(x => x.Path == mp) : null;
                if (s.View is null)
                    return R(modal is null ? Pass : Fail, "no modal view", modal is null ? "none" : $"{modal.Type} at {modal.Path}",
                        modal is null ? "Only the application loop runs." : "A modal view is executing.", modal?.Path);
                bool ok = modal is not null && s.View.Matches(modal);
                return R(ok ? Pass : Fail, $"modal [{s.View}]", modal is null ? "none" : $"{modal.Type} at {modal.Path}",
                    ok ? "The executing modal view matches." : "The executing modal view does not match.", modal?.Path);
            }
            case AssertionKinds.WithinScreen:
            {
                var (n, p) = Single(s.View); if (p is not null) return p;
                bool ok = Geometry.Contains(screen, n!.Screen);
                return R(ok ? Pass : Fail, "inside " + screen, n.Screen.ToString(),
                    ok ? "View lies inside the screen." : $"{Geometry.ClippedCells(n.Screen, screen)} cell(s) outside the screen.", n.Path);
            }
            case AssertionKinds.WithinView:
            {
                var (n, p) = Single(s.View); if (p is not null) return p;
                CellRect container;
                if (s.Other is null)
                {
                    if (tree!.WorkingArea is not { } area) return R(Unsupported, "inside working area", null, "The application has no desktop.", n!.Path);
                    container = area;
                }
                else { var (c, q) = Single(s.Other, "container"); if (q is not null) return q; container = c!.Screen; }
                bool ok = Geometry.Contains(container, n!.Screen);
                return R(ok ? Pass : Fail, "inside " + container, n.Screen.ToString(),
                    ok ? "View lies inside the container." : $"{Geometry.ClippedCells(n.Screen, container)} cell(s) outside the container.", n.Path);
            }
            case AssertionKinds.NotClipped:
            {
                var (n, p) = Single(s.View); if (p is not null) return p;
                bool ok = n!.Visible == n.Screen;
                return R(ok ? Pass : Fail, "visible " + n.Screen, Show(n.Visible),
                    ok ? "No part of the view is clipped by an owner or the screen." : "Part of the view is clipped (or the view is hidden).", n.Path);
            }
            case AssertionKinds.Aligned:
            {
                var views = Find(s.View);
                if (views.Length == 0) return R(Fail, $"views [{s.View}]", "no match", "No view matched.");
                ViewNode reference = views[0];
                if (s.Other is not null) { var (r, q) = Single(s.Other, "reference"); if (q is not null) return q; reference = r!; }
                int? expected = Geometry.Edge(reference.Screen, s.Edge ?? "");
                if (expected is null) return R(Unsupported, "edge " + s.Edge, null, "Edge must be left, right, top, bottom, center-x or center-y.");
                var off = views.Where(v => Geometry.Edge(v.Screen, s.Edge!) != expected).ToArray();
                return R(off.Length == 0 ? Pass : Fail, $"{s.Edge} = {Describe(s.Edge!, expected.Value)} for {views.Length} view(s)",
                    off.Length == 0 ? "aligned" : string.Join(", ", off.Take(5).Select(v => $"{v.Path}: {Describe(s.Edge!, Geometry.Edge(v.Screen, s.Edge!)!.Value)}")),
                    off.Length == 0 ? "All matched views share the edge." : $"{off.Length} view(s) are not aligned.", off.FirstOrDefault()?.Path ?? reference.Path);
            }
            case AssertionKinds.Spacing:
            {
                var (a, p) = Single(s.View, "first"); if (p is not null) return p;
                var (b, q) = Single(s.Other, "second"); if (q is not null) return q;
                if (s.Edge is not ("horizontal" or "vertical")) return R(Unsupported, "axis", s.Edge, "Axis must be horizontal or vertical.");
                int gap = Geometry.Gap(a!.Screen, b!.Screen, s.Edge);
                bool ok = gap >= (s.Min ?? int.MinValue) && gap <= (s.Max ?? int.MaxValue);
                return R(ok ? Pass : Fail, $"{s.Edge} gap {s.Min}..{s.Max}", gap.ToString(CultureInfo.InvariantCulture),
                    ok ? "Spacing within the application's constraint." : "Spacing outside the application's constraint.", a.Path + " / " + b.Path);
            }
            case AssertionKinds.Disjoint:
            {
                var (a, p) = Single(s.View, "first"); if (p is not null) return p;
                var (b, q) = Single(s.Other, "second"); if (q is not null) return q;
                var overlap = Geometry.Intersection(a!.Screen, b!.Screen);
                return R(overlap.IsEmpty ? Pass : Fail, "no overlap", overlap.IsEmpty ? "none" : overlap.ToString(),
                    overlap.IsEmpty ? "Views do not overlap." : "Views overlap.", a.Path + " / " + b.Path);
            }
            case AssertionKinds.Shadow:
            {
                var (n, p) = Single(s.View); if (p is not null) return p;
                if (!n!.Flags.Shadow) return R(Fail, "shadowed view", "no sfShadow", "The view does not cast a shadow.", n.Path);
                if (s.Value is null) return R(Unsupported, "shadow attribute", null,
                    "The framework exposes no public shadow attribute; supply the application's expected attribute.", n.Path);
                // Right band: shadowSize.x columns beside the view, rows offset by shadowSize.y; bottom band below it.
                var size = TView.shadowSize;
                CellRect r = n.Screen;
                var bands = new[] { new CellRect(r.Right, r.Y + size.y, size.x, r.Height), new CellRect(r.X + size.x, r.Bottom, r.Width - size.x, size.y) };
                var wrong = new List<string>(); int checkedCells = 0;
                foreach (var band in bands.Select(b => Geometry.Intersection(b, screen)))
                    for (int y = band.Y; y < band.Bottom; y++)
                        for (int x = band.X; x < band.Right; x++)
                        {
                            checkedCells++;
                            if (cells[x, y].Attr != s.Value) wrong.Add($"({x},{y})=0x{cells[x, y].Attr:x2}");
                        }
                if (checkedCells == 0) return R(Unsupported, $"shadow 0x{s.Value:x2}", null, "No shadow cell lies inside the screen.", n.Path);
                return R(wrong.Count == 0 ? Pass : Fail, $"attribute 0x{s.Value:x2} on {checkedCells} shadow cell(s)",
                    wrong.Count == 0 ? "all match" : string.Join(" ", wrong.Take(6)),
                    wrong.Count == 0 ? "Shadow bands carry the expected attribute. Cells covered by a view in front would also be reported."
                        : $"{wrong.Count} shadow cell(s) differ (a view in front of the shadow also counts as a difference).", n.Path);
            }
            case AssertionKinds.MenuDepth:
            {
                var menu = inspection!.Menu!;
                return R(menu.Depth == s.Value ? Pass : Fail, $"{s.Value} open level(s)", menu.Depth.ToString(CultureInfo.InvariantCulture),
                    "Open drop-down/popup levels below the bar: " + string.Join(" > ", menu.Open.Select(o => o.ParentItem ?? o.Type)));
            }
            case AssertionKinds.MenuSelected:
            {
                var level = MenuLevel(inspection!.Menu!, s.Level);
                if (level is null) return R(Fail, $"menu level {(s.Level?.ToString() ?? "deepest")}", "not open", "That menu level is not present.");
                var selected = level.Selected is { } i ? level.Items[i] : null;
                if (selected is null || selected.Text != s.Text)
                    return R(Fail, $"selected \"{s.Text}\"", selected?.Text ?? "none", "Model selection differs.", level.Path);
                // Observed evidence: the highlighted entry must look different from an unselected enabled one.
                var plain = level.Items.FirstOrDefault(e => !e.Selected && !e.Separator && !e.Disabled && e.Attribute is not null);
                if (selected.Observed != "match")
                    return R(Fail, $"selected \"{s.Text}\" drawn", selected.Observed, "The selected entry's text is not shown in the cells at its rectangle.", Show(selected.Screen));
                if (plain is not null && plain.Attribute == selected.Attribute)
                    return R(Fail, "highlighted selection", $"attribute 0x{selected.Attribute:x2} = unselected", "The selected entry is not visibly highlighted.", Show(selected.Screen));
                return R(Pass, $"selected \"{s.Text}\"", $"\"{selected.Text}\" attribute 0x{selected.Attribute:x2}",
                    "Model selection matches and the cells show the entry highlighted.", Show(selected.Screen));
            }
            case AssertionKinds.MenuItemState:
            {
                var level = MenuLevel(inspection!.Menu!, s.Level);
                var item = level?.Items.FirstOrDefault(e => e.Text == s.Text);
                if (item is null) return R(Fail, $"entry \"{s.Text}\"", "missing", "No such entry in that menu level.", level?.Path);
                if (s.State is not ("enabled" or "disabled")) return R(Unsupported, "state", s.State, "State must be enabled or disabled.");
                string actual = item.Disabled ? "disabled" : "enabled";
                return R(actual == s.State ? Pass : Fail, s.State, actual, $"Entry \"{s.Text}\" (command {item.Command}, command enabled: {item.CommandEnabled}).", Show(item.Screen));
            }
            case AssertionKinds.SubmenuOpen:
            {
                var menu = inspection!.Menu!;
                var level = menu.Open.FirstOrDefault(o => o.ParentItem == s.Text);
                return R(level is not null ? Pass : Fail, $"submenu of \"{s.Text}\" open",
                    menu.Open.Length == 0 ? "no open menu" : string.Join(" > ", menu.Open.Select(o => o.ParentItem ?? "(root)")),
                    level is not null ? $"Level {level.Level} opened from \"{s.Text}\"." : "No open level was opened from that entry.", level?.Path);
            }
            case AssertionKinds.StatusItem:
            {
                var status = inspection!.Status!;
                var item = status.Items.FirstOrDefault(i => i.Text == s.Text);
                string state = s.State ?? "visible";
                if (state == "absent")
                    return R(item is null ? Pass : Fail, $"no status item \"{s.Text}\"", item is null ? "absent" : $"present ({item.Observed})", "Active status definition items checked.");
                if (item is null) return R(Fail, $"status item \"{s.Text}\"", "absent", $"Active items: {string.Join(", ", status.Items.Select(i => i.Text ?? "(key only)"))}.");
                return state switch
                {
                    "visible" => R(item.Drawn && item.Observed == "match" ? Pass : Fail, "drawn and shown in the cells", $"{(item.Drawn ? "drawn" : "not drawn")}, cells {item.Observed}",
                        item.Drawn ? "Model layout and observed cells compared." : "The item does not fit the status line width.", Show(item.Screen)),
                    "enabled" or "disabled" => R((item.Enabled ? "enabled" : "disabled") == state ? Pass : Fail, state, item.Enabled ? "enabled" : "disabled",
                        $"Command {item.Command} availability.", Show(item.Screen)),
                    _ => R(Unsupported, "state", state, "State must be visible, absent, enabled or disabled."),
                };
            }
            case AssertionKinds.StatusContext:
            {
                var status = inspection!.Status!;
                return R(status.HelpContext == s.Value ? Pass : Fail, $"help context {s.Value}", status.HelpContext.ToString(CultureInfo.InvariantCulture),
                    $"Active definition {status.Definition?.ToString() ?? "none"} ({status.DefinitionMin}..{status.DefinitionMax}).", status.Path);
            }
            case AssertionKinds.StatusHint:
            {
                var status = inspection!.Status!;
                bool ok = status.Hint == s.Text && status.HintObserved is "match";
                return R(ok ? Pass : Fail, $"hint \"{s.Text}\" shown", $"\"{status.Hint}\" ({status.HintObserved})",
                    "Model hint for the active context compared with the cells after the separator.", status.HintScreen?.ToString());
            }
            case AssertionKinds.TextAt:
            {
                if (s.X is not { } x || s.Y is not { } y || s.Text is null) return R(Unsupported, "text-at", null, "X, Y and Text are required.");
                if (y < 0 || y >= cells.Height || x < 0 || x + s.Text.Length > cells.Width)
                    return R(Fail, $"\"{s.Text}\" at ({x},{y})", "outside the grid", "The requested cells are outside the captured grid.");
                string actual = Inspectors.Row(cells, x, y, s.Text.Length);
                return R(actual == s.Text ? Pass : Fail, $"\"{s.Text}\"", $"\"{actual}\"", "Logical cell characters compared.", $"({x},{y}) {s.Text.Length} cell(s)");
            }
            case AssertionKinds.ScreenContains:
            {
                if (string.IsNullOrEmpty(s.Text)) return R(Unsupported, "screen-contains", null, "Text is required.");
                for (int y = 0; y < cells.Height; y++)
                {
                    int x = Inspectors.Row(cells, 0, y, cells.Width).IndexOf(s.Text, StringComparison.Ordinal);
                    if (x >= 0) return R(Pass, $"\"{s.Text}\" on screen", $"at ({x},{y})", "Found in the logical cells.", $"({x},{y})");
                }
                return R(Fail, $"\"{s.Text}\" on screen", "not found", "No row of the logical cells contains the text.");
            }
            case AssertionKinds.AttributeAt:
            {
                if (s.X is not { } x || s.Y is not { } y || s.Value is not { } attribute) return R(Unsupported, "attribute-at", null, "X, Y and Value are required.");
                int width = s.Width ?? 1;
                if (y < 0 || y >= cells.Height || x < 0 || x + width > cells.Width) return R(Fail, $"attribute at ({x},{y})", "outside the grid", "Outside the captured grid.");
                var wrong = Enumerable.Range(x, width).Where(i => cells[i, y].Attr != attribute).Select(i => $"({i},{y})=0x{cells[i, y].Attr:x2}").ToArray();
                return R(wrong.Length == 0 ? Pass : Fail, $"0x{attribute:x2}", wrong.Length == 0 ? $"0x{attribute:x2}" : string.Join(" ", wrong.Take(6)),
                    "Logical attribute byte (background<<4 | foreground) compared.", $"({x},{y}) {width} cell(s)");
            }
            case AssertionKinds.LayoutAudit:
                return LayoutAuditAssertion.Evaluate(s, cells, inspection, (outcome, expected, actual, explanation, location) => R(outcome, expected, actual, explanation, location));
            default:
                return R(Unsupported, s.Kind, null, "Unknown assertion kind.");
        }
    }

    private static string Describe(string edge, int value) => edge.StartsWith("center", StringComparison.Ordinal)
        ? (value / 2.0).ToString("0.#", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    private static MenuViewState? MenuLevel(MenuState menu, int? level) => level switch
    {
        null => menu.Open.Length > 0 ? menu.Open[^1] : menu.Bar,
        0 => menu.Bar,
        _ => menu.Open.FirstOrDefault(o => o.Level == level),
    };

    public static IEnumerable<ViewNode> All(ViewNode root)
    {
        yield return root;
        foreach (var child in root.Children) foreach (var node in All(child)) yield return node;
    }
}
