namespace TSharpWorkbench.Testing;

// Shared model (coordinator): the captured tree as the rule families see it. Built once per audit from the evidence of
// ONE capture (view tree + cells + optional menu state); immutable for the rules. Classification uses the node's nearest
// framework type (ViewNode.Kind), so application subclasses classify as what they are built on.

/// <summary>What a view is for the layout rules.</summary>
public enum LayoutRole
{
    Application, Desktop, Background, Window, Dialog, Frame, ScrollBar, Button, Label, StaticText, InputLine, ListViewer, Cluster,
    ComboBox, GroupBox, History, Scroller, Editor, Indicator, Display, MenuBar, MenuBox, StatusLine, Group, Other
}

/// <summary>One captured view with its place in the audited tree.</summary>
public sealed class LayoutNode
{
    internal LayoutNode(ViewNode node, LayoutNode? parent, int depth, int index)
    {
        Node = node; Parent = parent; Depth = depth; Index = index; Role = LayoutModel.RoleOf(node.Kind);
    }

    public ViewNode Node { get; }
    public LayoutNode? Parent { get; }
    public IReadOnlyList<LayoutNode> Children { get; internal set; } = [];
    public int Depth { get; }
    /// <summary>Position among the parent's children, front-to-back (0 = frontmost, drawn last).</summary>
    public int Index { get; }
    public LayoutRole Role { get; }
    public string Path => Node.Path;
    public string Kind => Node.Kind;
    public CellRect Screen => Node.Screen;
    public CellRect? Visible => Node.Visible;
    /// <summary>The view and every owner have sfVisible and part of it is on screen.</summary>
    public bool Shown => Node.Visible is { IsEmpty: false };
    /// <summary>The view and every owner have sfVisible (it may still be entirely clipped).</summary>
    public bool VisibleFlag => Node.Visible is not null;
    public bool Selectable => Node.Flags.Selectable;
    public bool Disabled => Node.Flags.Disabled;
    /// <summary>Raw source text with mnemonic markers (<c>~F~ile</c>) when the target's inspection exposes it: buttons (markup),
    /// and, with an Testing payload, labels and static text (markup). Null when not exposed.</summary>
    public string? Markup => Node.Properties is { } p && p.TryGetValue("markup", out string? m) ? m : null;
    /// <summary>The exposed markup was cut at the inspection limit (the full text is longer).</summary>
    public bool MarkupTruncated => Node.Properties is { } p && p.TryGetValue("markupTruncated", out string? t) && t == "true";
    /// <summary>The markup to measure: a truncated value without the ellipsis the inspector appends, so it is a true prefix of
    /// the source (D-1). A prefix never needs more cells or rows than the full text: what overflows is a sound failure, what
    /// fits is still unknown.</summary>
    public string? MeasuredMarkup => Markup is { } m && MarkupTruncated && m.EndsWith('…') ? m[..^1] : Markup;
    /// <summary>A property the inspector reported for this view, else null.</summary>
    public string? Property(string key) => Node.Properties is { } p && p.TryGetValue(key, out string? v) ? v : null;
    /// <summary>Owner-relative bounds of this view.</summary>
    public CellRect Bounds => Node.Bounds;
    public bool IsMenu => Role is LayoutRole.MenuBar or LayoutRole.MenuBox;
    /// <summary>Nearest window/dialog ancestor (or itself), else null.</summary>
    public LayoutNode? Window { get { for (var n = this; n is not null; n = n.Parent) if (n.Role is LayoutRole.Window or LayoutRole.Dialog) return n; return null; } }
    /// <summary>Siblings (the parent's other children) in front-to-back order.</summary>
    public IEnumerable<LayoutNode> Siblings => Parent is null ? [] : Parent.Children.Where(c => !ReferenceEquals(c, this));
    public IEnumerable<LayoutNode> Descendants() { foreach (var c in Children) { yield return c; foreach (var d in c.Descendants()) yield return d; } }

    /// <summary>A stable selector: the diagnostic id, else type (short framework kind) + model text when the view has text.
    /// Null when neither exists. Paths are never used (they change with the tree).</summary>
    public ViewSelector? Identity => Node.Id is { } id ? ViewSelector.ById(id) : Node.Text is { Length: > 0 } text ? new ViewSelector(Type: Node.Kind, Text: text) : null;

    /// <summary>Readable one-line description: <c>TButton "OK" (0/1/2/3)</c>.</summary>
    public string Describe() => $"{Node.Kind}{(Node.Id is { } id ? " #" + id : "")}{(Node.Text is { Length: > 0 } t ? " \"" + Inspectors.Bound(t, 40) + "\"" : "")} ({Path})";

    public override string ToString() => Describe();
}

/// <summary>Everything the rule families may read for one audit.</summary>
public sealed class LayoutContext
{
    internal LayoutContext(ScreenSnapshot cells, ViewTree tree, MenuState? menu, LayoutNode root, LayoutNode scope, string profile)
    {
        Cells = cells; Tree = tree; Menu = menu; Root = root; Scope = scope; Profile = profile;
        Screen = new CellRect(0, 0, cells.Width, cells.Height);
        All = [root, .. root.Descendants()];
        Nodes = [scope, .. scope.Descendants()];
        byPath = All.ToDictionary(n => n.Path, StringComparer.Ordinal);
    }

    private readonly Dictionary<string, LayoutNode> byPath;
    public ScreenSnapshot Cells { get; }
    public ViewTree Tree { get; }
    /// <summary>Menu evidence when it was captured (optional; menu views are also classified from the tree).</summary>
    public MenuState? Menu { get; }
    public string Profile { get; }
    public CellRect Screen { get; }
    public CellRect? WorkingArea => Tree.WorkingArea;
    /// <summary>The whole captured tree (application root).</summary>
    public LayoutNode Root { get; }
    /// <summary>The audited subtree's root.</summary>
    public LayoutNode Scope { get; }
    /// <summary>Every captured view, pre-order.</summary>
    public IReadOnlyList<LayoutNode> All { get; }
    /// <summary>The audited views (scope root and its descendants), pre-order.</summary>
    public IReadOnlyList<LayoutNode> Nodes { get; }
    public LayoutNode? Focused => Tree.FocusedPath is { } p ? Find(p) : null;
    public LayoutNode? Modal => Tree.ModalPath is { } p ? Find(p) : null;
    public LayoutNode? Find(string path) => byPath.GetValueOrDefault(path);
    public bool InScope(LayoutNode node) { for (var n = node; n is not null; n = n.Parent) if (ReferenceEquals(n, Scope)) return true; return false; }
    /// <summary>Views drawn in front of <paramref name="node"/> anywhere on screen that are menus (open drop-downs/popups or the bar).</summary>
    public IEnumerable<LayoutNode> MenuOverlays => All.Where(n => n.IsMenu && n.Shown);
}

/// <summary>Builds <see cref="LayoutContext"/> and classifies views.</summary>
public static class LayoutModel
{
    /// <summary>Role by nearest framework type name (<see cref="ViewNode.Kind"/>).</summary>
    public static LayoutRole RoleOf(string kind) => kind switch
    {
        "TApplication" or "TProgram" or "TEditorApp" => LayoutRole.Application,
        "TDeskTop" => LayoutRole.Desktop,
        "TBackground" => LayoutRole.Background,
        "TDialog" or "TFileDialog" or "TChDirDialog" or "TColorDialog" => LayoutRole.Dialog,
        "TWindow" or "TEditWindow" or "THelpWindow" or "THistoryWindow" => LayoutRole.Window,
        "TFrame" => LayoutRole.Frame,
        "TScrollBar" => LayoutRole.ScrollBar,
        "TButton" => LayoutRole.Button,
        "TLabel" => LayoutRole.Label,
        "TStaticText" or "TParamText" => LayoutRole.StaticText,
        "TInputLine" or "TFileInputLine" => LayoutRole.InputLine,
        "TListViewer" or "TListBox" or "TSortedListBox" or "TFileList" or "TDirListBox" or "THistoryViewer" or "TColorGroupList" or "TColorItemList" => LayoutRole.ListViewer,
        "TCluster" or "TCheckBoxes" or "TRadioButtons" or "TMultiCheckBoxes" or "TMonoSelector" => LayoutRole.Cluster,
        "TComboBox" => LayoutRole.ComboBox,
        "TGroupBox" => LayoutRole.GroupBox,
        "THistory" => LayoutRole.History,
        "TScroller" or "THelpViewer" or "TOutlineViewer" or "TOutline" => LayoutRole.Scroller,
        "TEditor" or "TMemo" or "TFileEditor" => LayoutRole.Editor,
        "TIndicator" => LayoutRole.Indicator,
        "TProgressBar" or "TColorDisplay" or "TColorSelector" or "TFileInfoPane" => LayoutRole.Display,
        "TMenuBar" => LayoutRole.MenuBar,
        "TMenuBox" or "TMenuPopup" or "TMenuView" => LayoutRole.MenuBox,
        "TStatusLine" => LayoutRole.StatusLine,
        "TGroup" => LayoutRole.Group,
        _ => LayoutRole.Other,
    };

    /// <summary>Builds the context, resolving the scope. Returns the problem instead when the scope selector does not match
    /// exactly one view (the evaluator reports it as FAIL, like every single-view selector).</summary>
    public static (LayoutContext? Context, string? Problem) Build(ScreenSnapshot cells, ViewTree tree, MenuState? menu, ViewSelector? scope, string profile)
    {
        var root = Wrap(tree.Root, null, 0, 0);
        LayoutNode[] all = [root, .. root.Descendants()];
        LayoutNode target;
        if (scope is not null)
        {
            var found = all.Where(n => scope.Matches(n.Node)).ToArray();
            if (found.Length != 1) return (null, found.Length == 0 ? $"No view matched the audit scope [{scope}]."
                : $"The audit scope [{scope}] is ambiguous: {string.Join(", ", found.Take(5).Select(n => n.Path))}.");
            target = found[0];
        }
        else
        {
            var modal = tree.ModalPath is { } mp ? all.FirstOrDefault(n => n.Path == mp) : null;
            target = modal is not null && !modal.IsMenu ? modal : root;
        }
        return (new LayoutContext(cells, tree, menu, root, target, profile), null);
    }

    private static LayoutNode Wrap(ViewNode node, LayoutNode? parent, int depth, int index)
    {
        var n = new LayoutNode(node, parent, depth, index);
        n.Children = node.Children.Select((c, i) => Wrap(c, n, depth + 1, i)).ToArray();
        return n;
    }

    /// <summary>Removes the mnemonic markers of a caption (<c>~F~ile</c> → <c>File</c>).</summary>
    public static string Strip(string? markup) => Inspectors.Strip(markup);
}

/// <summary>What a rule family reports to the engine. Suppression and ordering are applied by the engine, not the rules.</summary>
public sealed class LayoutRuleSink
{
    private readonly HashSet<string> enabled, evaluated = new(StringComparer.Ordinal), notApplicable = new(StringComparer.Ordinal);
    internal LayoutRuleSink(IEnumerable<string> rules) { enabled = new(rules, StringComparer.Ordinal); }
    internal List<LayoutFinding> Findings { get; } = [];
    internal List<LayoutRuleGap> Gaps { get; } = [];
    internal IEnumerable<string> EvaluatedRules => LayoutRules.All.Select(r => r.Id).Where(evaluated.Contains);
    internal IEnumerable<string> NotApplicableRules => LayoutRules.All.Select(r => r.Id).Where(r => notApplicable.Contains(r) && !evaluated.Contains(r));
    internal bool Ran(string rule) => evaluated.Contains(rule) || notApplicable.Contains(rule);

    /// <summary>Whether the request runs <paramref name="rule"/>. A family must not examine or report a disabled rule.</summary>
    public bool Enabled(string rule) => enabled.Contains(rule);
    /// <summary>The rule examined at least one view (with or without findings).</summary>
    public void Evaluated(string rule) { if (Enabled(rule)) evaluated.Add(rule); }
    /// <summary>The rule ran but had nothing to examine (e.g. no buttons in scope).</summary>
    public void NotApplicable(string rule) { if (Enabled(rule)) notApplicable.Add(rule); }
    /// <summary>The rule (for <paramref name="path"/>, or entirely when null) cannot be evaluated on this evidence.</summary>
    public void Unavailable(string rule, string reason, string? path = null) { if (Enabled(rule)) Gaps.Add(new(rule, reason, path)); }
    public void Add(LayoutFinding finding)
    {
        if (!Enabled(finding.Rule)) return;
        evaluated.Add(finding.Rule);
        Findings.Add(finding);
    }
    /// <summary>Convenience: a finding about <paramref name="node"/>.</summary>
    public void Add(string rule, string @class, string confidence, LayoutNode node, CellRect rect, string message, string? evidence = null, LayoutNode? other = null) =>
        Add(new LayoutFinding(rule, @class, confidence, node.Path, node.Describe(), node.Identity, rect, other?.Path, other?.Screen, message, evidence));
}
