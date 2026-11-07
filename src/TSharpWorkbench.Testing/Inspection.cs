using System.Text;
using System.Text.Json.Serialization;
using TSharpVision.Constants;

namespace TSharpWorkbench.Testing;

/// <summary>A rectangle of character cells. <see cref="X"/>/<see cref="Y"/> are screen or owner coordinates as the
/// containing field says; <see cref="Right"/>/<see cref="Bottom"/> are exclusive.</summary>
public readonly record struct CellRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}

/// <summary>Semantic evidence of one checkpoint, taken on the UI thread in the same pass as the captured cells.
/// A failed inspector leaves its field null and adds an entry to <see cref="Errors"/>; it is never reported as success.</summary>
public sealed record CheckpointInspection(ViewTree? ViewTree, MenuState? Menu, StatusState? Status, string[] Errors);

public sealed record InspectionLimits(int MaxNodes = 1000, int MaxDepth = 24, int MaxText = 256)
{
    public static InspectionLimits Default { get; } = new();
}

/// <summary>Runs the requested read-only inspectors. Must be called on the UI thread.</summary>
public static class Inspectors
{
    public static CheckpointInspection Capture(TProgram application, ScreenSnapshot cells, IEnumerable<string> evidence, InspectionLimits? limits = null)
    {
        var requested = evidence.Distinct(StringComparer.Ordinal).ToArray();
        var errors = new List<string>();
        T? Run<T>(string kind, Func<T?> inspector) where T : class
        {
            if (!requested.Contains(kind)) return null;
            try { return inspector() ?? throw new InvalidOperationException("no state available"); }
            catch (Exception ex) { errors.Add($"{kind}: inspector failed ({ex.GetType().Name}: {ex.Message})"); return null; }
        }
        foreach (string unknown in requested.Where(e => !Protocol.EvidenceKinds.All.Contains(e))) errors.Add($"{unknown}: unknown evidence kind");
        return new(Run(Protocol.EvidenceKinds.ViewTree, () => ViewTreeInspector.Inspect(application, cells, limits)),
            Run(Protocol.EvidenceKinds.Menu, () => MenuInspector.Inspect(application, cells)),
            Run(Protocol.EvidenceKinds.Status, () => StatusInspector.Inspect(application, cells)),
            errors.ToArray());
    }

    internal static string Strip(string? markup) => markup is null ? "" : markup.Replace("~", "", StringComparison.Ordinal);

    internal static string Bound(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    internal static CellRect ScreenRect(TView view)
    {
        TPoint origin = view.MakeGlobal(new TPoint(0, 0));
        return new(origin.x, origin.y, view.size.x, view.size.y);
    }

    internal static CellRect Intersect(CellRect a, CellRect b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
    }

    /// <summary>Visible part of a view: its screen rectangle clipped by every owner and the screen; null when the
    /// view or an owner is hidden.</summary>
    internal static CellRect? VisibleRect(TView view, CellRect screen)
    {
        CellRect visible = Intersect(ScreenRect(view), screen);
        for (TView? v = view; v is not null; v = v.owner)
        {
            if ((v.state & Views.sfVisible) == 0) return null;
            if (v.owner is not null) visible = Intersect(visible, ScreenRect(v.owner));
        }
        return visible;
    }

    internal static string Cells(ScreenSnapshot cells, CellRect area, int max)
    {
        var text = new StringBuilder();
        for (int y = area.Y; y < area.Bottom && y < cells.Height; y++)
        {
            if (y < 0) continue;
            if (text.Length > 0) text.Append('\n');
            var row = new StringBuilder();
            for (int x = Math.Max(0, area.X); x < area.Right && x < cells.Width; x++) row.Append(cells[x, y].Character is '\0' ? ' ' : cells[x, y].Character);
            text.Append(row.ToString().TrimEnd());
            if (text.Length > max) break;
        }
        return Bound(text.ToString().TrimEnd('\n'), max);
    }

    internal static string Row(ScreenSnapshot cells, int x, int y, int length)
    {
        var row = new StringBuilder();
        for (int i = 0; i < length; i++)
            row.Append(x + i >= 0 && x + i < cells.Width && y >= 0 && y < cells.Height ? cells[x + i, y].Character : '\0');
        return row.ToString();
    }

    internal static int? Attribute(ScreenSnapshot cells, int x, int y) =>
        x >= 0 && x < cells.Width && y >= 0 && y < cells.Height ? cells[x, y].Attr : null;
}

// ── view tree ───────────────────────────────────────────────────────────────

public sealed record ViewFlags(bool Visible, bool Selectable, bool Current, bool Selected, bool Active, bool Focused,
    bool Disabled, bool Modal, bool Exposed, bool Shadow, int State, int Options);

/// <summary>One view. <see cref="Bounds"/> is owner-relative; <see cref="Screen"/> is absolute; <see cref="Visible"/> is the
/// screen part left after clipping by owners and the screen (null when hidden). <see cref="Text"/> is model text read
/// through public framework members (null when the control exposes none); <see cref="CellText"/> is what the logical
/// cells show inside <see cref="Visible"/> for leaf views (observed evidence; overlapping views in front included).</summary>
public sealed record ViewNode(string Path, string? Id, string Type, string Kind, CellRect Bounds, CellRect Screen, CellRect? Visible,
    ViewFlags Flags, int HelpContext, string? Text, string? CellText, Dictionary<string, string>? Properties,
    ViewNode[] Children, int OmittedChildren);

/// <param name="Order">Child order: <c>front-to-back</c> (the order of <c>TGroup.ForEachView</c>).</param>
/// <param name="WorkingArea">The desktop's screen rectangle, when the application has a desktop.</param>
/// <param name="ModalPath">Path of the executing modal view (dialog, menu); null when only the application runs.</param>
/// <param name="FocusedPath">Deepest view on the <c>current</c> chain from the modal (or application) view.</param>
public sealed record ViewTree(ViewNode Root, int Nodes, bool Truncated, string Order, CellRect Screen, CellRect? WorkingArea,
    string? ModalPath, string? FocusedPath);

/// <summary>Read-only snapshot of the live TSharpVision view hierarchy, using public framework members only.
/// Paths are child indices in front-to-back order (<c>0/2/1</c>), valid for the capture they belong to.</summary>
public static class ViewTreeInspector
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TView, string> Ids = new();
    private static readonly System.Reflection.Assembly Framework = typeof(TView).Assembly;

    /// <summary>Gives a view a stable diagnostic identity reported as <see cref="ViewNode.Id"/> and usable in selectors.
    /// Nothing is stored in the view; the identity disappears with it.</summary>
    public static void Identify(TView view, string id)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!Protocol.IsValidName(id)) throw new ArgumentException("Diagnostic IDs use lowercase words joined by hyphens.");
        Ids.AddOrUpdate(view, id);
    }

    public static ViewTree Inspect(TProgram application, ScreenSnapshot? cells = null, InspectionLimits? limits = null)
    {
        limits ??= InspectionLimits.Default;
        var screen = new CellRect(0, 0, TScreen.ScreenWidth, TScreen.ScreenHeight);
        int count = 0; bool truncated = false;
        TView? modal = application.TopView();
        if (ReferenceEquals(modal, application)) modal = null;
        TView? focused = modal ?? application;
        while (focused is TGroup { current: { } inner }) focused = inner;
        string? modalPath = null, focusedPath = null;

        ViewNode Node(TView view, string path, int depth)
        {
            count++;
            if (ReferenceEquals(view, modal)) modalPath = path;
            if (ReferenceEquals(view, focused)) focusedPath = path;
            var children = new List<ViewNode>();
            int omitted = 0;
            if (view is TGroup group)
            {
                int index = 0;
                foreach (TView child in Children(group))
                {
                    if (depth + 1 > limits.MaxDepth || count >= limits.MaxNodes) { omitted++; truncated = true; continue; }
                    children.Add(Node(child, path + "/" + index++, depth + 1));
                }
            }
            CellRect? visible = Inspectors.VisibleRect(view, screen);
            string? cellText = view is not TGroup && cells is not null && visible is { IsEmpty: false } v ? Inspectors.Cells(cells, v, limits.MaxText) : null;
            var (text, properties) = Describe(view, limits.MaxText, application);
            TRect bounds = view.GetBounds();
            return new(path, Ids.TryGetValue(view, out string? id) ? id : null, view.GetType().FullName ?? view.GetType().Name, KindOf(view.GetType()),
                new(bounds.a.x, bounds.a.y, bounds.b.x - bounds.a.x, bounds.b.y - bounds.a.y), Inspectors.ScreenRect(view), visible,
                new((view.state & Views.sfVisible) != 0, (view.options & Views.ofSelectable) != 0, view.owner?.current == view,
                    (view.state & Views.sfSelected) != 0, (view.state & Views.sfActive) != 0, (view.state & Views.sfFocused) != 0,
                    (view.state & Views.sfDisabled) != 0, (view.state & Views.sfModal) != 0, (view.state & Views.sfExposed) != 0,
                    (view.state & Views.sfShadow) != 0, view.state, view.options),
                view.helpCtx, text, cellText, properties, children.ToArray(), omitted);
        }

        var root = Node(application, "0", 0);
        return new(root, count, truncated, "front-to-back", screen,
            application.DeskTop is { } desk ? Inspectors.ScreenRect(desk) : null, modalPath, focusedPath);
    }

    /// <summary>Children in front-to-back order, without the target host's hidden dispatcher.</summary>
    internal static IEnumerable<TView> Children(TGroup group)
    {
        var list = new List<TView>();
        group.ForEachView(v => { if (v is not TestTargetHost.DispatchView) list.Add(v); });
        return list;
    }

    /// <summary>Path of <paramref name="view"/> under <paramref name="root"/>, or null when it is not in that tree.</summary>
    public static string? PathOf(TView view, TView root)
    {
        var indices = new Stack<int>();
        for (TView v = view; !ReferenceEquals(v, root); v = v.owner!)
        {
            if (v.owner is null) return null;
            int index = Children(v.owner).TakeWhile(c => !ReferenceEquals(c, v)).Count();
            indices.Push(index);
        }
        return "0" + string.Concat(indices.Select(i => "/" + i));
    }

    /// <summary>Nearest TSharpVision framework type: lets selectors match custom controls by what they are built on.</summary>
    internal static string KindOf(Type type)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
            if (t.Assembly == Framework) return t.Name;
        return type.Name;
    }

    // (additive, read-only, Properties only so selectors and Text are unchanged): the source text of labels and static
    // text ("markup", via the public GetText; "markupTruncated" when cut at the limit), a label's linked control ("link", a
    // capture path) and an input line's maximum length and scroll position ("maxLen", "firstPos"). Layout rules use them to
    // decide text fit; an older Testing payload omits them and those rules report limited coverage instead of passing.
    private static (string? Text, Dictionary<string, string>? Properties) Describe(TView view, int max, TProgram application)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        string? text = null;
        switch (view)
        {
            case TButton button:
                text = Inspectors.Strip(button.Title); p["markup"] = button.Title; p["command"] = button.Command.ToString();
                p["commandEnabled"] = TView.CommandEnabled(button.Command) ? "true" : "false";
                break;
            case TInputLine input: text = input.Data; p["maxLen"] = input.MaxLen.ToString(); p["firstPos"] = input.FirstPos.ToString(); break;
            case TWindow window: text = window.title; break;
            case TCluster cluster:
                p["value"] = cluster.value.ToString(); p["sel"] = cluster.sel.ToString();
                p["items"] = Inspectors.Bound(string.Join(" | ", cluster.Strings.Select(Inspectors.Strip)), max);
                if (cluster.sel >= 0 && cluster.sel < cluster.Strings.Count) text = Inspectors.Strip(cluster.Strings[cluster.sel]);
                break;
            case TListViewer list:
                p["focused"] = list.focused.ToString(); p["range"] = list.range.ToString(); p["topItem"] = list.topItem.ToString();
                if (list.focused >= 0 && list.focused < list.range) text = list.GetText(list.focused, max);
                break;
            case TMenuView menu:
                if (menu.Current?.Name is { } current) { text = Inspectors.Strip(current); p["current"] = current; }
                break;
            case TStatusLine status: p["helpContext"] = status.helpCtx.ToString(); break;
            case TStaticText staticText:
                staticText.GetText(out string source);
                source ??= "";
                p["markup"] = Inspectors.Bound(source, max);
                if (source.Length > max) p["markupTruncated"] = "true";
                if (staticText is TLabel { Link: { } link } && PathOf(link, application) is { } linked) p["link"] = linked;
                break;
        }
        if (text is not null) text = Inspectors.Bound(text, max);
        return (text, p.Count == 0 ? null : p);
    }
}

// ── menus ───────────────────────────────────────────────────────────────────

/// <summary>One menu entry. <see cref="Screen"/> is model geometry (<c>GetItemRect</c> made absolute); <see cref="Observed"/> compares
/// the logical cells with the entry text: <c>match</c>, <c>mismatch</c>, <c>not-drawn</c> (outside the visible area) or
/// <c>separator</c>. <see cref="Attribute"/> is the observed attribute of the entry's first cell.</summary>
public sealed record MenuEntry(int Index, string? Text, string? Markup, string? HotChar, int Command, bool CommandEnabled, bool Disabled,
    bool Separator, bool Submenu, string? Accelerator, int KeyCode, string? KeyName, int HelpContext, bool Selected,
    CellRect? Screen, string Observed, int? Attribute);

/// <param name="Level">0 = menu bar; 1 = first drop-down or popup; deeper levels are nested submenus.</param>
/// <param name="ParentItem">Text of the parent menu's selected entry that opened this level.</param>
public sealed record MenuViewState(int Level, string Type, string? Path, CellRect Screen, bool Executing, bool Visible,
    int? Selected, string? SelectedText, string? ParentItem, MenuEntry[] Items);

/// <param name="Active">A menu modal loop is executing (the bar, a drop-down or a popup).</param>
/// <param name="Depth">Number of open drop-down/popup levels (the bar is not counted).</param>
/// <param name="Open">Executing levels below the bar, outermost first.</param>
public sealed record MenuState(bool Active, int Depth, MenuViewState? Bar, MenuViewState[] Open);

/// <summary>Read-only view of the actual current menu state, including menus whose modal loops are running.</summary>
public static class MenuInspector
{
    public static MenuState Inspect(TProgram application, ScreenSnapshot? cells = null)
    {
        var chain = new List<TMenuView>();
        for (TMenuView? m = application.TopView() as TMenuView; m is not null; m = m.ParentMenu) chain.Insert(0, m);
        var screen = new CellRect(0, 0, TScreen.ScreenWidth, TScreen.ScreenHeight);
        TMenuBar? bar = application.MenuBar;
        bool barExecuting = bar is not null && chain.Contains(bar);
        var open = chain.Where(m => !ReferenceEquals(m, bar)).ToArray();
        return new(chain.Count > 0, open.Length,
            bar is null ? null : Level(bar, 0, barExecuting, application, cells, screen),
            open.Select((m, i) => Level(m, i + 1, true, application, cells, screen)).ToArray());
    }

    private static MenuViewState Level(TMenuView view, int level, bool executing, TProgram application, ScreenSnapshot? cells, CellRect screen)
    {
        CellRect rect = Inspectors.ScreenRect(view);
        CellRect? visible = Inspectors.VisibleRect(view, screen);
        var items = new List<MenuEntry>();
        int index = 0; int? selected = null;
        for (TMenuItem? p = view.Menu?.Items; p is not null && index < 256; p = p.Next, index++)
        {
            bool separator = string.IsNullOrEmpty(p.Name);
            bool isSelected = ReferenceEquals(p, view.Current);
            if (isSelected) selected = index;
            CellRect? itemRect = null; string observed = separator ? "separator" : "not-drawn"; int? attribute = null;
            if (!separator)
            {
                TRect local = view.GetItemRect(p);
                if (local.b.x > local.a.x)
                {
                    itemRect = new(rect.X + local.a.x, rect.Y + local.a.y, local.b.x - local.a.x, local.b.y - local.a.y);
                    string label = Inspectors.Strip(p.Name);
                    bool drawn = visible is { } v && Inspectors.Intersect(itemRect.Value, v) == itemRect.Value && local.b.x <= view.size.x;
                    if (cells is not null && drawn)
                    {
                        observed = Inspectors.Row(cells, itemRect.Value.X + 1, itemRect.Value.Y, label.Length) == label ? "match" : "mismatch";
                        attribute = Inspectors.Attribute(cells, itemRect.Value.X, itemRect.Value.Y);
                    }
                    else if (drawn) observed = "unobserved";
                }
            }
            items.Add(new(index, separator ? null : Inspectors.Strip(p.Name), p.Name, separator || p.HotChar() == '\0' ? null : p.HotChar().ToString(),
                p.Command, p.Command != 0 && TView.CommandEnabled(p.Command), p.Disabled, separator, p.Command == 0 && p.SubMenu?.Items is not null,
                p.Param, p.KeyCode, p.KeyCode == Keys.kbNoKey ? null : TestKeys.NameOf(p.KeyCode), p.HelpCtx, isSelected, itemRect, observed, attribute));
        }
        return new(level, view.GetType().FullName ?? view.GetType().Name, ViewTreeInspector.PathOf(view, application), rect, executing, visible is not null,
            selected, view.Current?.Name is { } name ? Inspectors.Strip(name) : null,
            view.ParentMenu?.Current?.Name is { } parent ? Inspectors.Strip(parent) : null, items.ToArray());
    }
}

// ── status line ─────────────────────────────────────────────────────────────

/// <summary>One status item. <see cref="Drawn"/> and <see cref="Screen"/> are model-derived (the status line's own layout rule:
/// an item is drawn when <c>offset + length + 2</c> fits the line); <see cref="Observed"/> compares that range with the
/// logical cells: <c>match</c>, <c>mismatch</c>, <c>not-drawn</c>, <c>hidden</c> (an item without text: a key binding only).</summary>
public sealed record StatusEntry(int Index, string? Text, string? Markup, int KeyCode, string? KeyName, int Command, bool Enabled,
    bool Drawn, CellRect? Screen, string Observed, int? Attribute);

/// <param name="HelpContext">The context the status line selected its items for (<c>TStatusLine.helpCtx</c>).</param>
/// <param name="Definition">Index of the active <c>TStatusDef</c> in the definition chain, null when none matches.</param>
/// <param name="HintObserved">Whether the hint text is visible after the separator: <c>match</c>, <c>mismatch</c>,
/// <c>truncated</c>, <c>not-drawn</c> or <c>none</c>.</param>
/// <param name="ObservedRow">The status line's row as shown in the logical cells.</param>
public sealed record StatusState(string Type, string? Path, CellRect Screen, int HelpContext, int? Definition, int? DefinitionMin,
    int? DefinitionMax, string? Hint, CellRect? HintScreen, string HintObserved, StatusEntry[] Items, string? ObservedRow);

/// <summary>Read-only view of the application's status line: model state plus observed cell evidence.</summary>
public static class StatusInspector
{
    public static StatusState? Inspect(TProgram application, ScreenSnapshot? cells = null)
    {
        if (application.StatusLine is not { } line) return null;
        CellRect rect = Inspectors.ScreenRect(line);
        int? definition = null, min = null, max = null, d = 0;
        for (TStatusDef? def = line.Defs; def is not null && d < 256; def = def.Next, d++)
            if (line.helpCtx >= def.Min && line.helpCtx <= def.Max) { definition = d; min = def.Min; max = def.Max; break; }
        var items = new List<StatusEntry>();
        int offset = 0, index = 0;
        for (TStatusItem? t = line.Items; t is not null && index < 256; t = t.Next, index++)
        {
            if (t.Text is null)
            {
                items.Add(new(index, null, null, t.KeyCode, TestKeys.NameOf(t.KeyCode), t.Command, TView.CommandEnabled(t.Command), false, null, "hidden", null));
                continue;
            }
            string label = Inspectors.Strip(t.Text);
            bool drawn = offset + label.Length + 2 <= line.size.x;
            CellRect? itemRect = drawn ? new CellRect(rect.X + offset, rect.Y, label.Length + 2, 1) : null;
            string observed = !drawn ? "not-drawn" : cells is null ? "unobserved"
                : Inspectors.Row(cells, rect.X + offset, rect.Y, label.Length + 2) == " " + label + " " ? "match" : "mismatch";
            items.Add(new(index, label, t.Text, t.KeyCode, TestKeys.NameOf(t.KeyCode), t.Command, TView.CommandEnabled(t.Command), drawn,
                itemRect, observed, drawn && cells is not null ? Inspectors.Attribute(cells, rect.X + offset, rect.Y) : null));
            offset += label.Length + 2;
        }
        string hint = line.Hint(line.helpCtx);
        CellRect? hintRect = null; string hintObserved = "none";
        if (hint.Length > 0)
        {
            if (offset < line.size.x - 2)
            {
                int start = offset + 2, width = Math.Min(hint.Length, line.size.x - start);
                hintRect = new(rect.X + start, rect.Y, width, 1);
                hintObserved = cells is null ? "unobserved"
                    : Inspectors.Row(cells, rect.X + start, rect.Y, width) != hint[..width] ? "mismatch" : width < hint.Length ? "truncated" : "match";
            }
            else hintObserved = "not-drawn";
        }
        return new(line.GetType().FullName ?? line.GetType().Name, ViewTreeInspector.PathOf(line, application), rect, line.helpCtx,
            definition, min, max, hint.Length == 0 ? null : hint, hintRect, hintObserved, items.ToArray(),
            cells is null ? null : Inspectors.Row(cells, rect.X, rect.Y, Math.Min(rect.Width, cells.Width - Math.Max(0, rect.X))).TrimEnd('\0').TrimEnd());
    }
}
