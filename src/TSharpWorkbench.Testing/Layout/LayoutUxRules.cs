using static TSharpWorkbench.Testing.LayoutUxGeometry;

namespace TSharpWorkbench.Testing;

// UX heuristics (ux.*). Every finding is class WARNING (never an authoritative failure; only the strict
// profile lets warnings fail the assertion). Each rule states in its Message/Evidence why it applied, the observed numbers,
// the tolerance and its confidence. Normal hand-made variation is tolerated: one-cell differences are never reported, except
// where a single cell is plainly visible (tops of buttons in one row, left edges of buttons in one column).
//
// Units of evaluation: every window/dialog in the scope separately (never across stacked windows); controls are the shown
// views owned by that window (recursing into TGroup/TGroupBox, not into nested windows); frames, scroll bars, backgrounds,
// menus, the status line and hidden views are ignored. Relations (alignment, spacing, button rows) compare only controls
// with the same owner (a group box is its own unit). Dynamic/scrollable controls (lists, editors, scrollers, input lines with
// long data) count by their BOUNDS: their blank interior is content, never unused space, and their data never matters.
internal static class LayoutUxRules
{
    private static readonly string[] Owned =
    [
        LayoutRules.LabelAlignment, LayoutRules.ColumnAlignment, LayoutRules.RowSpacing, LayoutRules.ButtonRow,
        LayoutRules.Proportions, LayoutRules.UnusedSpace, LayoutRules.Balance,
    ];

    private const string Warning = LayoutFindingClasses.Warning;
    private const ushort CmOK = 10, CmCancel = 11, CmYes = 12, CmNo = 13;

    internal static void Evaluate(LayoutContext context, LayoutRuleSink sink)
    {
        var rules = Owned.Where(sink.Enabled).ToArray();
        if (rules.Length == 0) return;
        var examined = new HashSet<string>(StringComparer.Ordinal);
        var gapped = new HashSet<string>(StringComparer.Ordinal);

        if (context.Scope.Node.OmittedChildren > 0 && context.Scope.Role is not (LayoutRole.Window or LayoutRole.Dialog))
            foreach (string rule in rules)
            {
                sink.Unavailable(rule, $"The captured tree was truncated: {context.Scope.Node.OmittedChildren} child view(s) of the scope were not inspected.", context.Scope.Path);
                gapped.Add(rule);
            }

        foreach (var window in Units(context))
        {
            int omitted = window.Node.Descendants().Prepend(window.Node).Sum(n => n.Node.OmittedChildren);
            if (omitted > 0)
            {
                foreach (string rule in rules)
                {
                    sink.Unavailable(rule, $"The captured tree of this window was truncated ({omitted} view(s) not inspected); its layout cannot be judged.", window.Node.Path);
                    gapped.Add(rule);
                }
                continue;
            }
            var pairs = LabelPairs(context, window, sink);
            if (sink.Enabled(LayoutRules.LabelAlignment) && LabelAlignment(window, pairs, sink)) examined.Add(LayoutRules.LabelAlignment);
            if (sink.Enabled(LayoutRules.ColumnAlignment) && ColumnAlignment(window, sink)) examined.Add(LayoutRules.ColumnAlignment);
            if (sink.Enabled(LayoutRules.RowSpacing) && RowSpacing(window, pairs, sink)) examined.Add(LayoutRules.RowSpacing);
            if (sink.Enabled(LayoutRules.ButtonRow) && ButtonRows(window, sink)) examined.Add(LayoutRules.ButtonRow);
            if (window.DialogLike && window.Content is { IsEmpty: false } content)
            {
                var (overW, overH) = sink.Enabled(LayoutRules.Proportions) || sink.Enabled(LayoutRules.Balance) ? Oversized(window, content) : (false, false);
                if (sink.Enabled(LayoutRules.Proportions)) { Proportions(context, window, content, overW, overH, sink); examined.Add(LayoutRules.Proportions); }
                if (sink.Enabled(LayoutRules.Balance)) { Balance(window, content, overW, overH, sink); examined.Add(LayoutRules.Balance); }
                if (sink.Enabled(LayoutRules.UnusedSpace) && UnusedSpace(window, content, sink)) examined.Add(LayoutRules.UnusedSpace);
            }
        }

        foreach (string rule in rules)
        {
            if (examined.Contains(rule)) sink.Evaluated(rule);
            else if (!gapped.Contains(rule)) sink.NotApplicable(rule);
        }
    }

    // ── units ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Unit
    {
        public required LayoutNode Node { get; init; }
        /// <summary>A dialog, or a window holding only form controls: its size is the author's choice and can be judged.</summary>
        public required bool DialogLike { get; init; }
        public required CellRect Interior { get; init; }
        /// <summary>Shown controls owned by the window (through groups, not nested windows), pre-order.</summary>
        public required List<LayoutNode> Controls { get; init; }
        /// <summary>Shown direct children that occupy the interior (a group box counts as one block).</summary>
        public required List<LayoutNode> Blocks { get; init; }
        /// <summary>Bounding box of the blocks inside the interior (empty when there is none).</summary>
        public CellRect? Content { get; init; }
        public string Name => Node.Describe();
    }

    private static bool Ignored(LayoutRole role) => role is LayoutRole.Frame or LayoutRole.Background or LayoutRole.MenuBar or LayoutRole.MenuBox
        or LayoutRole.StatusLine or LayoutRole.Desktop or LayoutRole.Application;

    private static bool IsField(LayoutRole role) => role is LayoutRole.InputLine or LayoutRole.ComboBox or LayoutRole.Cluster or LayoutRole.ListViewer
        or LayoutRole.Editor;

    private static bool LabelTarget(LayoutRole role) => IsField(role) || role is LayoutRole.Scroller or LayoutRole.Display or LayoutRole.Other;

    private static bool FormBlock(LayoutRole role) => role is LayoutRole.Button or LayoutRole.Label or LayoutRole.StaticText or LayoutRole.InputLine
        or LayoutRole.ListViewer or LayoutRole.Cluster or LayoutRole.ComboBox or LayoutRole.GroupBox or LayoutRole.History or LayoutRole.ScrollBar
        or LayoutRole.Display;

    private static IEnumerable<Unit> Units(LayoutContext context)
    {
        var scope = context.Scope;
        IEnumerable<LayoutNode> windows = scope.Role is LayoutRole.Window or LayoutRole.Dialog
            ? [scope]
            : scope.Descendants().Where(n => n.Role is LayoutRole.Window or LayoutRole.Dialog && n.Shown);
        bool any = false;
        foreach (var w in windows)
        {
            if (!w.Shown) continue;
            any = true;
            yield return Make(w, framed: true);
        }
        // A plain group as the scope (e.g. a panel inside a dialog): relation rules only.
        if (!any && scope.Role is LayoutRole.Group or LayoutRole.GroupBox && scope.Shown) yield return Make(scope, framed: false);
    }

    private static Unit Make(LayoutNode window, bool framed)
    {
        var controls = new List<LayoutNode>();
        void Walk(LayoutNode owner)
        {
            foreach (var child in owner.Children)
            {
                if (!child.Shown || Ignored(child.Role) || child.Role is LayoutRole.Window or LayoutRole.Dialog) continue;
                controls.Add(child);
                if (child.Role is LayoutRole.Group or LayoutRole.GroupBox) Walk(child);
            }
        }
        Walk(window);
        var interior = framed ? Inset(window.Screen, 1) : window.Screen;
        var blocks = window.Children.Where(c => c.Shown && !Ignored(c.Role) && c.Role is not (LayoutRole.Window or LayoutRole.Dialog)).ToList();
        var content = Union(blocks.Select(b => Geometry.Intersection(b.Screen, interior)).Where(r => !r.IsEmpty));
        bool dialogLike = framed && (window.Role == LayoutRole.Dialog || blocks.Count > 0 && blocks.All(b => FormBlock(b.Role)));
        return new Unit { Node = window, DialogLike = dialogLike, Interior = interior, Controls = controls, Blocks = blocks, Content = content.IsEmpty ? null : content };
    }

    private static string Name(LayoutNode n)
    {
        string? text = n.Role is LayoutRole.Label or LayoutRole.StaticText && n.Markup is { } m ? LayoutModel.Strip(m) : n.Node.Text;
        if (string.IsNullOrEmpty(text)) text = n.Node.CellText?.Split('\n')[0].Trim();
        return string.IsNullOrEmpty(text) ? n.Kind + " (" + n.Path + ")" : n.Kind + " \"" + Inspectors.Bound(text, 30) + "\"";
    }

    private static string Conf(string c) => "confidence " + c;

    // ── ux.label-alignment ──────────────────────────────────────────────────────────────────────────────────────────────

    private enum Placement { Above, Left, Other }

    private sealed record Pair(LayoutNode Label, LayoutNode Control, bool Linked, Placement Place);

    private static Placement Place(CellRect l, CellRect c) =>
        l.Bottom <= c.Y && OverlapX(l, c) > 0 ? Placement.Above
        : l.Y >= c.Y && l.Y < c.Bottom && l.X < c.X ? Placement.Left
        : Placement.Other;

    /// <summary>Label/control pairs: the label's link when the capture exposes it (high evidence), else the adjacent control
    /// directly below or right of it on the same row (low evidence).</summary>
    private static List<Pair> LabelPairs(LayoutContext context, Unit unit, LayoutRuleSink sink)
    {
        var pairs = new List<Pair>();
        var targets = unit.Controls.Where(c => LabelTarget(c.Role)).ToList();
        foreach (var label in unit.Controls.Where(c => c.Role == LayoutRole.Label))
        {
            if (label.Property("link") is { } link)
            {
                if (context.Find(link) is { Shown: true } linked && ReferenceEquals(linked.Window, unit.Node.Window ?? unit.Node) && !ReferenceEquals(linked, label))
                    pairs.Add(new(label, linked, true, Place(label.Screen, linked.Screen)));
                continue;   // a label of a hidden control or of another window is not a layout relation here
            }
            var l = label.Screen;
            var below = targets.Where(f => f.Screen.Y == l.Bottom && OverlapX(l, f.Screen) > 0)
                .OrderBy(f => Math.Abs(f.Screen.X - l.X)).ThenBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();
            var right = targets.Where(f => f.Screen.Y <= l.Y && l.Y < f.Screen.Bottom && f.Screen.X > l.X && f.Screen.X - l.Right is >= -1 and <= 3)
                .OrderBy(f => f.Screen.X - l.Right).ThenBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();
            var adjacent = below ?? right;
            if (adjacent is not null) pairs.Add(new(label, adjacent, false, Place(l, adjacent.Screen)));
            else if (label.Markup is null)
                sink.Unavailable(LayoutRules.LabelAlignment, "The target's Testing payload does not expose label links (older) and no adjacent control was found; this label could not be paired.", label.Path);
        }
        return pairs;
    }

    private static bool LabelAlignment(Unit unit, List<Pair> pairs, LayoutRuleSink sink)
    {
        if (!unit.Controls.Any(c => c.Role == LayoutRole.Label)) return false;
        string rule = LayoutRules.LabelAlignment;
        var flagged = new HashSet<string>(StringComparer.Ordinal);
        string Basis(Pair p) => p.Linked ? "the label is linked to this control" : "no link exposed; paired by adjacency (lower confidence)";

        // 1. Each linked label against its own control.
        foreach (var p in pairs.Where(p => p.Linked))
        {
            CellRect l = p.Label.Screen, c = p.Control.Screen;
            string pairText = $"{Name(p.Label)} at {l} ↔ {Name(p.Control)} at {c}";
            if (p.Place == Placement.Above)
            {
                int vgap = c.Y - l.Bottom, dx = l.X + 1 - c.X;   // TLabel draws its text from x=1
                if (vgap >= 2)
                {
                    flagged.Add(p.Label.Path);
                    sink.Add(rule, Warning, Medium, p.Label, l,
                        $"Label {Name(p.Label)} is {vgap} empty rows above its control {Name(p.Control)}; it reads as detached. Move it to the row directly above (y={c.Y - l.Height}) or beside the control.",
                        $"applies: {Basis(p)}; {pairText}; vertical gap {vgap} rows (tolerated ≤ 1); {Conf(Medium)}", p.Control);
                }
                else if (dx is < -1 or > 2)
                {
                    flagged.Add(p.Label.Path);
                    sink.Add(rule, Warning, Medium, p.Label, l,
                        $"Label {Name(p.Label)} above {Name(p.Control)} starts its text {Math.Abs(dx)} cell(s) {(dx > 0 ? "right" : "left")} of the control's left edge; put the label at x={c.X - 1} so its text lines up with the control.",
                        $"applies: {Basis(p)}; {pairText}; text start x={l.X + 1} vs control x={c.X} (offset {dx}, tolerated -1..+2); {Conf(Medium)}", p.Control);
                }
            }
            else if (p.Place == Placement.Other)
            {
                flagged.Add(p.Label.Path);
                bool rightOf = OverlapY(l, c) > 0 && l.X >= c.Right;
                string conf = rightOf ? Low : Medium;
                string where = rightOf ? "to the right of" : l.Y >= c.Bottom ? "below" : "diagonally off";
                sink.Add(rule, Warning, conf, p.Label, l,
                    $"Label {Name(p.Label)} sits {where} its control {Name(p.Control)} instead of directly above it or to its left on the same row; move it to ({c.X - 1},{c.Y - 1}) or to the left of ({c.X},{c.Y}).",
                    $"applies: {Basis(p)}; {pairText}; neither above (label bottom = control top, columns shared) nor left on the control's rows; {Conf(conf)}", p.Control);
            }
        }

        // 2. Consistency of labels placed above their controls, per owner.
        foreach (var group in pairs.Where(p => p.Place == Placement.Above).GroupBy(p => p.Label.Parent?.Path ?? "", StringComparer.Ordinal))
        {
            var list = group.OrderBy(p => p.Label.Screen.Y).ThenBy(p => p.Label.Screen.X).ToList();
            if (list.Count < 2) continue;
            var offsets = list.Select(p => p.Label.Screen.X - p.Control.Screen.X).ToList();
            var (mode, count) = Mode(offsets);
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                int d = offsets[i] - mode;
                if (Math.Abs(d) < 2 || flagged.Contains(p.Label.Path)) continue;
                string conf = !p.Linked || list.Count < 3 || count < 2 ? Low : Math.Abs(d) >= 3 ? Medium : Low;
                flagged.Add(p.Label.Path);
                var reference = list[offsets.IndexOf(mode)];
                sink.Add(rule, Warning, conf, p.Label, p.Label.Screen,
                    $"Label {Name(p.Label)} is offset {offsets[i]} cell(s) from its control while the other labels above their controls use {mode}; move it to x={p.Control.Screen.X + mode}.",
                    $"applies: {list.Count} labels placed above their controls in {unit.Name}; label.x - control.x = {Ints(offsets)}; dominant {mode} ({count}/{list.Count}); tolerance ±1; {Basis(p)}; {Conf(conf)}", reference.Label);
            }
        }

        // 3. Left edges of labels to the left of one column of controls, per owner. Left- or right-aligned labels both pass.
        foreach (var group in pairs.Where(p => p.Place == Placement.Left).GroupBy(p => p.Label.Parent?.Path ?? "", StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(p => p.Label.Screen.Y).ThenBy(p => p.Label.Screen.X).ToList();
            foreach (var column in Components(ordered, (a, b) => Math.Abs(a.Control.Screen.X - b.Control.Screen.X) <= 1 && OverlapY(a.Label.Screen, b.Label.Screen) <= 0))
            {
                if (column.Count < 2) continue;
                var xs = column.Select(p => p.Label.Screen.X).ToList();
                var rs = column.Select(p => p.Label.Screen.Right).ToList();
                if (xs.Max() - xs.Min() <= 1 || rs.Max() - rs.Min() <= 1) continue;
                var (mx, cx) = Mode(xs);
                var (mr, cr) = Mode(rs);
                bool byRight = cr > cx;
                var values = byRight ? rs : xs;
                int mode = byRight ? mr : mx, count = byRight ? cr : cx;
                for (int i = 0; i < column.Count; i++)
                {
                    var p = column[i];
                    int d = values[i] - mode;
                    if (Math.Abs(d) < 2 || flagged.Contains(p.Label.Path)) continue;
                    string conf = p.Linked && column.Count >= 3 && count >= 2 ? Medium : Low;
                    flagged.Add(p.Label.Path);
                    var reference = column[values.IndexOf(mode)];
                    string edge = byRight ? "right" : "left";
                    sink.Add(rule, Warning, conf, p.Label, p.Label.Screen,
                        $"Label {Name(p.Label)} is not aligned with the other labels of its column: its {edge} edge is at x={values[i]}, the others use x={mode}. Align the labels' {edge} edges.",
                        $"applies: {column.Count} labels left of controls stacked at x≈{p.Control.Screen.X} in {unit.Name}; label left x={Ints(xs)}, right x={Ints(rs)}; dominant {edge} x={mode} ({count}/{column.Count}); tolerance ±1; {Basis(p)}; {Conf(conf)}", reference.Label);
                }
            }
        }
        return true;
    }

    // ── ux.column-alignment ─────────────────────────────────────────────────────────────────────────────────────────────

    private static bool ColumnAlignment(Unit unit, LayoutRuleSink sink)
    {
        string rule = LayoutRules.ColumnAlignment;
        bool any = false;
        foreach (var group in unit.Controls.Where(c => IsField(c.Role)).GroupBy(c => c.Parent?.Path ?? "", StringComparer.Ordinal))
        {
            var fields = group.OrderBy(f => f.Screen.Y).ThenBy(f => f.Screen.X).ToList();
            if (fields.Count < 2) continue;
            any = true;
            bool Related(LayoutNode a, LayoutNode b)
            {
                CellRect ra = a.Screen, rb = b.Screen;
                return OverlapY(ra, rb) <= 0 && -OverlapY(ra, rb) <= 8 && Math.Abs(ra.X - rb.X) <= 4
                    && OverlapX(ra, rb) >= Math.Max(1, Math.Min(ra.Width, rb.Width) / 2);
            }
            foreach (var column in Components(fields, Related))
            {
                if (column.Count < 2) continue;
                var xs = column.Select(f => f.Screen.X).ToList();
                if (xs.Max() - xs.Min() <= 1) continue;
                var (mode, count) = Mode(xs);
                var reference = column[xs.IndexOf(mode)];
                for (int i = 0; i < column.Count; i++)
                {
                    int d = xs[i] - mode;
                    if (Math.Abs(d) is < 2 or > 4) continue;
                    var f = column[i];
                    string conf = column.Count >= 3 && count >= 2 ? Medium : Low;
                    sink.Add(rule, Warning, conf, f, f.Screen,
                        $"{Name(f)} starts at x={xs[i]}, {Math.Abs(d)} cell(s) {(d > 0 ? "right" : "left")} of the column it is stacked in (x={mode}); align the left edges of the stacked controls.",
                        $"applies: {column.Count} stacked controls of {unit.Name} share columns (left x={Ints(xs)}); dominant x={mode} ({count}/{column.Count}); differences of 1 tolerated, ≥ 5 treated as intentional indentation; {Conf(conf)}", reference);
                }
            }
        }
        return any;
    }

    // ── ux.row-spacing ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Band(LayoutNode Field, CellRect Rect, string Kind);

    private static bool RowSpacing(Unit unit, List<Pair> pairs, LayoutRuleSink sink)
    {
        string rule = LayoutRules.RowSpacing;
        bool any = false;
        foreach (var group in unit.Controls.Where(c => IsField(c.Role)).GroupBy(c => c.Parent?.Path ?? "", StringComparer.Ordinal))
        {
            var bands = group.Select(f =>
            {
                var label = pairs.FirstOrDefault(p => ReferenceEquals(p.Control, f) && p.Place != Placement.Other && p.Label.Parent == f.Parent);
                var rect = label is null ? f.Screen : Union(f.Screen, label.Label.Screen);
                return new Band(f, rect, $"{f.Kind}/h{f.Screen.Height}/{(label?.Place == Placement.Above ? "label-above" : "inline")}");
            }).OrderBy(b => b.Rect.Y).ThenBy(b => b.Rect.X).ToList();
            if (bands.Count < 2) continue;

            // Vertical rhythm: bands stacked in one column.
            foreach (var column in Components(bands, (a, b) => OverlapX(a.Rect, b.Rect) > 0 && OverlapY(a.Rect, b.Rect) <= 0))
            {
                var seq = column.OrderBy(b => b.Rect.Y).ToList();
                var gaps = new List<(int Gap, Band Prev, Band Next)>();
                for (int i = 0; i + 1 < seq.Count; i++)
                    if (seq[i].Kind == seq[i + 1].Kind && seq[i + 1].Rect.Y >= seq[i].Rect.Bottom)
                        gaps.Add((seq[i + 1].Rect.Y - seq[i].Rect.Bottom, seq[i], seq[i + 1]));
                if (gaps.Count == 0) continue;
                any = true;
                Check(gaps, vertical: true);
            }
            // Horizontal rhythm: three or more same-kind controls side by side on one row.
            foreach (var row in Components(bands, (a, b) => a.Rect.Y == b.Rect.Y && a.Kind == b.Kind && OverlapX(a.Rect, b.Rect) <= 0))
            {
                var seq = row.OrderBy(b => b.Rect.X).ToList();
                if (seq.Count < 3) continue;
                var gaps = new List<(int Gap, Band Prev, Band Next)>();
                for (int i = 0; i + 1 < seq.Count; i++) gaps.Add((seq[i + 1].Rect.X - seq[i].Rect.Right, seq[i], seq[i + 1]));
                any = true;
                Check(gaps, vertical: false);
            }
        }
        return any;

        void Check(List<(int Gap, Band Prev, Band Next)> gaps, bool vertical)
        {
            if (gaps.Count < 2) return;
            var values = gaps.Select(g => g.Gap).ToList();
            var (mode, count) = Mode(values);
            string unitWord = vertical ? "row(s)" : "column(s)";
            var outliers = new List<(int Index, string Conf)>();
            if (gaps.Count >= 3 && count >= 2)
            {
                for (int i = 0; i < gaps.Count; i++)
                    if (Math.Abs(values[i] - mode) >= 2) outliers.Add((i, Math.Abs(values[i] - mode) >= 3 ? Medium : Low));
            }
            else if (values.Max() - values.Min() >= 3)
            {
                int i = values.IndexOf(values.Max());
                mode = values.Where((_, j) => j != i).Min();
                outliers.Add((i, Low));
            }
            foreach (var (i, conf) in outliers)
            {
                var (gap, prev, next) = gaps[i];
                CellRect rect = gap <= 0 ? next.Field.Screen : vertical
                    ? new CellRect(Math.Min(prev.Rect.X, next.Rect.X), prev.Rect.Bottom, Math.Max(prev.Rect.Right, next.Rect.Right) - Math.Min(prev.Rect.X, next.Rect.X), gap)
                    : new CellRect(prev.Rect.Right, Math.Min(prev.Rect.Y, next.Rect.Y), gap, Math.Max(prev.Rect.Bottom, next.Rect.Bottom) - Math.Min(prev.Rect.Y, next.Rect.Y));
                string direction = vertical ? "above" : "left of";
                sink.Add(rule, Warning, conf, next.Field, rect,
                    $"The spacing {direction} {Name(next.Field)} is {gap} empty {unitWord} while the other {(vertical ? "rows" : "columns")} of the same kind use {mode}; make the spacing even (or separate sections more clearly).",
                    $"applies: {gaps.Count + 1} same-kind controls ({next.Kind}) {(vertical ? "stacked" : "side by side")} in {unit.Name}; gaps {Ints(values)}; dominant {mode}; differences of 1 tolerated; {Conf(conf)}", prev.Field);
            }
        }
    }

    // ── ux.button-row ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool ButtonRows(Unit unit, LayoutRuleSink sink)
    {
        string rule = LayoutRules.ButtonRow;
        bool any = false;
        var fields = unit.Controls.Where(c => IsField(c.Role)).ToList();
        foreach (var group in unit.Controls.Where(c => c.Role == LayoutRole.Button).GroupBy(c => c.Parent?.Path ?? "", StringComparer.Ordinal))
        {
            var buttons = group.OrderBy(b => b.Screen.Y).ThenBy(b => b.Screen.X).ToList();
            if (buttons.Count < 2) continue;
            any = true;
            var flagged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Components(buttons, (a, b) => OverlapY(a.Screen, b.Screen) > 0))
                if (row.Count >= 2) Line(row.OrderBy(b => b.Screen.X).ToList(), horizontal: true);
            foreach (var col in Components(buttons, (a, b) => OverlapX(a.Screen, b.Screen) > 0 && OverlapY(a.Screen, b.Screen) <= 0 && -OverlapY(a.Screen, b.Screen) <= 4))
                if (col.Count >= 2) Line(col.OrderBy(b => b.Screen.Y).ToList(), horizontal: false);

            void Line(List<LayoutNode> line, bool horizontal)
            {
                string kind = horizontal ? "row" : "column";
                string ids = string.Join(", ", line.Select(Name));
                // Alignment across the line: tops of a row / left edges of a column. One cell is visible here, so it counts.
                var edges = line.Select(b => horizontal ? b.Screen.Y : b.Screen.X).ToList();
                var sizes = line.Select(b => horizontal ? b.Screen.Height : b.Screen.Width).ToList();
                var (edge, edgeCount) = Mode(edges);
                for (int i = 0; i < line.Count; i++)
                {
                    int d = edges[i] - edge;
                    if (d == 0 || !flagged.Add(line[i].Path)) continue;
                    var reference = line[edges.IndexOf(edge)];
                    const string conf = Medium;   // even a one-cell offset of buttons in one row/column is plainly visible
                    sink.Add(rule, Warning, conf, line[i], line[i].Screen,
                        horizontal
                            ? $"Button {Name(line[i])} is {Math.Abs(d)} row(s) {(d > 0 ? "lower" : "higher")} than {Name(reference)} in the same button row; put all buttons of the row on y={edge}."
                            : $"Button {Name(line[i])} is {Math.Abs(d)} column(s) {(d > 0 ? "right" : "left")} of {Name(reference)} in the same button column; put all buttons of the column at x={edge}.",
                        $"applies: {line.Count} buttons share a {kind} in {unit.Name} ({ids}); {(horizontal ? "tops y" : "left x")}={Ints(edges)}; dominant {edge} ({edgeCount}/{line.Count}); tolerance 0 for button {kind}s; {Conf(conf)}", reference);
                }
                // Gaps between neighbours (three or more buttons). A much wider gap is an intentional separation (e.g. Help apart).
                if (line.Count >= 3)
                {
                    var gaps = new List<int>();
                    for (int i = 0; i + 1 < line.Count; i++)
                        gaps.Add(horizontal ? line[i + 1].Screen.X - line[i].Screen.Right : line[i + 1].Screen.Y - line[i].Screen.Bottom);
                    var nonNegative = gaps.Where(g => g >= 0).ToList();
                    if (nonNegative.Count >= 2)
                    {
                        int min = nonNegative.Min();
                        var regular = Enumerable.Range(0, gaps.Count).Where(i => gaps[i] >= 0 && gaps[i] < 2 * min + 4).ToList();
                        var values = regular.Select(i => gaps[i]).ToList();
                        if (values.Count >= 2)
                        {
                            var (mode, count) = Mode(values);
                            var outliers = new List<(int Index, int Ref, string Conf)>();
                            if (values.Count >= 3 && count >= 2)
                            {
                                foreach (int i in regular) if (Math.Abs(gaps[i] - mode) >= 2) outliers.Add((i, mode, Medium));
                            }
                            else if (values.Max() - values.Min() >= 2)
                            {
                                int i = regular[values.IndexOf(values.Max())];
                                outliers.Add((i, values.Min(), values.Max() - values.Min() >= 3 ? Medium : Low));
                            }
                            foreach (var (i, reference, conf) in outliers)
                            {
                                var a = line[i]; var b = line[i + 1];
                                if (!flagged.Add(b.Path)) continue;
                                CellRect rect = horizontal
                                    ? new CellRect(a.Screen.Right, Math.Min(a.Screen.Y, b.Screen.Y), Math.Max(1, gaps[i]), Math.Max(a.Screen.Bottom, b.Screen.Bottom) - Math.Min(a.Screen.Y, b.Screen.Y))
                                    : new CellRect(Math.Min(a.Screen.X, b.Screen.X), a.Screen.Bottom, Math.Max(a.Screen.Right, b.Screen.Right) - Math.Min(a.Screen.X, b.Screen.X), Math.Max(1, gaps[i]));
                                sink.Add(rule, Warning, conf, b, rect,
                                    $"The gap between {Name(a)} and {Name(b)} is {gaps[i]} {(horizontal ? "column(s)" : "row(s)")}, the other buttons of the {kind} use {reference}; space the buttons evenly.",
                                    $"applies: {line.Count} buttons in one {kind} of {unit.Name} ({ids}); gaps {Ints(gaps)}; gaps ≥ {2 * min + 4} count as intentional separation; tolerance ±1; {Conf(conf)}", a);
                            }
                        }
                    }
                }
                // Widths along a row / column: only with a majority width (three or more buttons, two of them equal), and only
                // where the caption would fit that width. Two buttons of different widths are a design choice, not reported.
                var widths = line.Select(b => b.Screen.Width).ToList();
                var (w, wc) = Mode(widths);
                int tolerance = horizontal ? 3 : 2;
                for (int i = 0; i < line.Count && line.Count >= 3 && wc >= 2; i++)
                {
                    int d = widths[i] - w;
                    if (Math.Abs(d) < tolerance || flagged.Contains(line[i].Path)) continue;
                    int caption = LayoutModel.Strip(line[i].Markup ?? line[i].Node.Text).Length;
                    if (caption + 3 > w) continue;   // the wider face is needed by this caption: justified
                    flagged.Add(line[i].Path);
                    const string conf = Medium;
                    var reference = line[widths.IndexOf(w)];
                    sink.Add(rule, Warning, conf, line[i], line[i].Screen,
                        $"Button {Name(line[i])} is {widths[i]} cells wide while the other buttons of its {kind} are {w}; its caption fits {w}, so give the buttons one width.",
                        $"applies: {line.Count} buttons in one {kind} of {unit.Name} ({ids}); widths {Ints(widths)}; common {w}; caption {caption} cells; tolerance ±{tolerance - 1}; {Conf(conf)}", reference);
                }
                // Placement of the action group (OK/Cancel/Yes/No): bottom row or right column of the dialog.
                if (line.Any(b => b.Property("command") is { } c && ushort.TryParse(c, out ushort cmd) && cmd is CmOK or CmCancel or CmYes or CmNo))
                {
                    var box = Union(line.Select(b => b.Screen));
                    var after = fields.Where(f => horizontal ? f.Screen.Y >= box.Bottom : f.Screen.X >= box.Right).ToList();
                    if (after.Count > 0 && flagged.Add(line[0].Path))
                        sink.Add(rule, Warning, Low, line[0], box,
                            $"The action buttons ({ids}) are placed {(horizontal ? "above" : "left of")} {after.Count} input control(s) such as {Name(after[0])}; put the action {kind} {(horizontal ? "below" : "right of")} the content.",
                            $"applies: the {kind} holds a dialog action command (OK/Cancel/Yes/No) in {unit.Name}; button {kind} at {box}; controls entirely {(horizontal ? "below" : "right of")} it: {after.Count}; {Conf(Low)}", after[0]);
                }
            }
        }
        return any;
    }

    // ── ux.proportions / ux.balance / ux.unused-space (dialog-like windows) ─────────────────────────────────────────────

    // The content needs its bounding box plus a typical margin (2 columns, 1 row on each side).
    private static (int W, int H) Need(CellRect content) => (content.Width + 4, content.Height + 2);

    private static (bool W, bool H) Oversized(Unit unit, CellRect content)
    {
        var (needW, needH) = Need(content);
        var i = unit.Interior;
        return (i.Width >= 2 * needW && i.Width - needW >= 20, i.Height >= 2 * needH && i.Height - needH >= 8);
    }

    private static void Proportions(LayoutContext context, Unit unit, CellRect content, bool overW, bool overH, LayoutRuleSink sink)
    {
        if (!overW && !overH) return;
        var (needW, needH) = Need(content);
        var i = unit.Interior;
        var area = context.WorkingArea ?? context.Screen;
        var w = unit.Node.Screen;
        // High confidence when the excess is threefold, or the window takes three quarters of the desktop on an oversized axis.
        bool gross = overW && (i.Width >= 3 * needW || 4 * w.Width >= 3 * area.Width) || overH && (i.Height >= 3 * needH || 4 * w.Height >= 3 * area.Height);
        string conf = gross ? High : Medium;
        string axes = overW && overH ? "wide and tall" : overW ? "wide" : "tall";
        sink.Add(LayoutRules.Proportions, Warning, conf, unit.Node, w,
            $"{Name(unit.Node)} is {w.Width}x{w.Height} but its content only needs about {needW + 2}x{needH + 2}; the dialog is far too {axes} for what it shows. Shrink it to fit the content.",
            $"applies: dialog-like window with {unit.Blocks.Count} content block(s); content box {content}; interior {i.Width}x{i.Height}; needed {needW}x{needH} (content + 2/1 margins); excess {i.Width - needW}x{i.Height - needH} (reported at ≥ 2x the need and ≥ 20 columns / 8 rows); window covers {100 * w.Width / Math.Max(1, area.Width)}% x {100 * w.Height / Math.Max(1, area.Height)}% of the desktop {area.Width}x{area.Height}; {Conf(conf)}");
    }

    private static void Balance(Unit unit, CellRect content, bool overW, bool overH, LayoutRuleSink sink)
    {
        var i = unit.Interior;
        int l = content.X - i.X, r = i.Right - content.Right, t = content.Y - i.Y, b = i.Bottom - content.Bottom;
        string margins = $"margins left {l}, right {r}, top {t}, bottom {b}";
        if (!overW && Math.Abs(l - r) >= Math.Max(10, (int)Math.Ceiling(0.35 * i.Width)) && Math.Max(l, r) >= 3 * Math.Min(l, r) + 6)
        {
            bool right = r > l;
            var rect = right ? new CellRect(content.Right, i.Y, r, i.Height) : new CellRect(i.X, i.Y, l, i.Height);
            string conf = Math.Max(l, r) * 2 >= i.Width ? High : Medium;
            sink.Add(LayoutRules.Balance, Warning, conf, unit.Node, rect,
                $"The content of {Name(unit.Node)} is pushed to the {(right ? "left" : "right")}: {Math.Max(l, r)} empty columns on the {(right ? "right" : "left")} versus {Math.Min(l, r)} on the other side. Center the content or narrow the dialog.",
                $"applies: dialog-like window, content box {content} in interior {i}; {margins}; reported when the difference ≥ max(10, 35% of the width) and the larger margin ≥ 3x the smaller + 6; {Conf(conf)}");
        }
        if (!overH && Math.Abs(t - b) >= Math.Max(5, (int)Math.Ceiling(0.35 * i.Height)) && Math.Max(t, b) >= 3 * Math.Min(t, b) + 3)
        {
            bool bottom = b > t;
            var rect = bottom ? new CellRect(i.X, content.Bottom, i.Width, b) : new CellRect(i.X, i.Y, i.Width, t);
            string conf = Math.Max(t, b) * 2 >= i.Height ? High : Medium;
            sink.Add(LayoutRules.Balance, Warning, conf, unit.Node, rect,
                $"The content of {Name(unit.Node)} is pushed to the {(bottom ? "top" : "bottom")}: {Math.Max(t, b)} empty rows {(bottom ? "below" : "above")} it versus {Math.Min(t, b)} on the other side. Move the content (or the buttons) to use the height, or make the dialog shorter.",
                $"applies: dialog-like window, content box {content} in interior {i}; {margins}; reported when the difference ≥ max(5, 35% of the height) and the larger margin ≥ 3x the smaller + 3; {Conf(conf)}");
        }
    }

    /// <summary>Empty bands strictly inside the content box (content on both sides), from the blocks' bounds: a list or editor
    /// occupies its whole rectangle however few items it shows, so blank list space is never unused space.</summary>
    private static bool UnusedSpace(Unit unit, CellRect content, LayoutRuleSink sink)
    {
        if (unit.Blocks.Count < 2) return false;
        var rects = unit.Blocks.Select(b => Geometry.Intersection(b.Screen, unit.Interior)).Where(r => !r.IsEmpty).ToList();
        if (rects.Count < 2) return false;
        var rows = new bool[content.Height];
        var cols = new bool[content.Width];
        foreach (var r in rects)
        {
            for (int y = r.Y; y < r.Bottom; y++) rows[y - content.Y] = true;
            for (int x = r.X; x < r.Right; x++) cols[x - content.X] = true;
        }
        int hMin = Math.Max(4, (int)Math.Ceiling(0.3 * content.Height)), wMin = Math.Max(12, (int)Math.Ceiling(0.3 * content.Width));
        foreach (var (start, length) in InnerEmptyRuns(rows))
        {
            if (length < hMin) continue;
            string conf = length >= Math.Max(6, 0.45 * content.Height) ? Medium : Low;
            var band = new CellRect(content.X, content.Y + start, content.Width, length);
            var above = unit.Blocks.Where(b => b.Screen.Bottom <= band.Y).OrderByDescending(b => b.Screen.Bottom).FirstOrDefault() ?? unit.Node;
            var below = unit.Blocks.Where(b => b.Screen.Y >= band.Bottom).OrderBy(b => b.Screen.Y).FirstOrDefault() ?? unit.Node;
            sink.Add(LayoutRules.UnusedSpace, Warning, conf, unit.Node, band,
                $"{length} empty rows separate the content of {Name(unit.Node)} between {Name(above)} and {Name(below)}; close the gap (move the lower controls up and shorten the dialog) or use the space.",
                $"applies: empty full-width band inside the content box {content} with controls above and below; band {band}; reported at ≥ max(4 rows, 30% of the content height = {hMin}); lists/editors count by their bounds; {Conf(conf)}", above);
        }
        foreach (var (start, length) in InnerEmptyRuns(cols))
        {
            if (length < wMin) continue;
            string conf = length >= Math.Max(16, 0.45 * content.Width) ? Medium : Low;
            var band = new CellRect(content.X + start, content.Y, length, content.Height);
            var left = unit.Blocks.Where(b => b.Screen.Right <= band.X).OrderByDescending(b => b.Screen.Right).FirstOrDefault() ?? unit.Node;
            var right = unit.Blocks.Where(b => b.Screen.X >= band.Right).OrderBy(b => b.Screen.X).FirstOrDefault() ?? unit.Node;
            sink.Add(LayoutRules.UnusedSpace, Warning, conf, unit.Node, band,
                $"{length} empty columns separate the content of {Name(unit.Node)} between {Name(left)} and {Name(right)}; move the right-hand controls closer and narrow the dialog.",
                $"applies: empty full-height band inside the content box {content} with controls left and right; band {band}; reported at ≥ max(12 columns, 30% of the content width = {wMin}); lists/editors count by their bounds; {Conf(conf)}", left);
        }
        return true;
    }
}
