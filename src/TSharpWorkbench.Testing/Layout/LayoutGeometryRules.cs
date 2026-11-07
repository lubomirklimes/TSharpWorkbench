namespace TSharpWorkbench.Testing;

// Deterministic geometry rules (geom.*). Pure functions of one capture's view tree (+ cells only for the
// group-box border probe). Every rule reports Evaluated/NotApplicable; rects are absolute screen cells.
//
// Intentional layering that is never a finding here: TFrame (it IS the window border), TBackground / the group box's own
// background, scroll bars and indicators placed on a window's frame line (TWindow.StandardScrollBar, TEditWindow), stacked
// or nested windows on the desktop and a modal dialog over windows (windows are not compared with each other), menu bar /
// drop-downs / popups, history and combo drop-down windows (popups), THistory beside its input (not selectable, not text),
// hidden views (sfVisible off on the view or an owner). Shadows are not views and are never measured.
internal static class LayoutGeometryRules
{
    /// <summary>Framework popups that overlay their owner by design (they are executed over it).</summary>
    internal static bool IsPopup(LayoutNode n) => n.IsMenu || n.Kind is "THistoryWindow" or "TComboBoxDropDown" or "TMenuPopup";

    private static bool IsBackground(LayoutNode n) => n.Role == LayoutRole.Background || n.Kind == "Background";

    private static bool IsWindow(LayoutNode n) => n.Role is LayoutRole.Window or LayoutRole.Dialog;

    internal static void Evaluate(LayoutContext context, LayoutRuleSink sink)
    {
        Degenerate(context, sink);
        Offscreen(context, sink);
        OutsideParent(context, sink);
        OutsideDesktop(context, sink);
        Focus(context, sink);
        Overlap(context, sink);
        LabelCollision(context, sink);
    }

    // ── geom.degenerate ─────────────────────────────────────────────────────

    private static bool IsControlRole(LayoutNode n) => n.Role switch
    {
        LayoutRole.Application or LayoutRole.Desktop or LayoutRole.Group => false,
        LayoutRole.Other => n.Selectable,
        _ => true,
    };

    private static void Degenerate(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.Degenerate;
        if (!sink.Enabled(rule)) return;
        sink.Evaluated(rule);
        var treeScreen = ctx.Tree.Screen;
        if (treeScreen.Width != ctx.Screen.Width || treeScreen.Height != ctx.Screen.Height)
            sink.Add(rule, LayoutFindingClasses.Error, "high", ctx.Scope, ctx.Screen,
                $"The captured cells ({ctx.Screen.Width}x{ctx.Screen.Height}) and the view tree's screen ({treeScreen.Width}x{treeScreen.Height}) differ; the capture is inconsistent.",
                $"cells={ctx.Screen.Width}x{ctx.Screen.Height}; tree={treeScreen.Width}x{treeScreen.Height}");
        foreach (var n in ctx.Nodes)
        {
            if (!n.VisibleFlag) continue;
            var b = n.Bounds;
            if ((b.Width <= 0 || b.Height <= 0) && IsControlRole(n))
            {
                sink.Add(rule, LayoutFindingClasses.Error, "high", n, n.Screen,
                    $"Visible {n.Kind} has a {(b.Width <= 0 && b.Height <= 0 ? "zero/negative width and height" : b.Width <= 0 ? "zero/negative width" : "zero/negative height")} ({b.Width}x{b.Height}); give it a real size or hide it.",
                    $"bounds={b}");
                continue;
            }
            if (n.Parent is { } p)
            {
                var expected = new CellRect(p.Screen.X + b.X, p.Screen.Y + b.Y, b.Width, b.Height);
                if (expected != n.Screen)
                {
                    sink.Add(rule, LayoutFindingClasses.Error, "high", n, n.Screen,
                        $"Screen rectangle {n.Screen} is inconsistent with the owner's origin ({p.Screen.X},{p.Screen.Y}) plus bounds {b} (expected {expected}).",
                        $"screen={n.Screen}; owner={p.Screen}; bounds={b}");
                    continue;
                }
            }
            // Visible must be the screen rect clipped by the screen and every owner (Inspectors.VisibleRect).
            var clip = Geometry.Intersection(n.Screen, treeScreen);
            for (var o = n.Parent; o is not null; o = o.Parent) clip = Geometry.Intersection(clip, o.Screen);
            if (n.Visible is { } v && !(v.IsEmpty && clip.IsEmpty) && v != clip)
                sink.Add(rule, LayoutFindingClasses.Error, "high", n, n.Screen,
                    $"Reported visible part {v} does not match the screen rectangle clipped by its owners ({clip}); the captured geometry is inconsistent.",
                    $"visible={v}; expected={clip}");
        }
    }

    // ── geom.offscreen ──────────────────────────────────────────────────────

    private static string Sides(CellRect r, CellRect area)
    {
        var parts = new List<string>();
        if (r.X < area.X) parts.Add($"{area.X - r.X} left of x={area.X}");
        if (r.Y < area.Y) parts.Add($"{area.Y - r.Y} above y={area.Y}");
        if (r.Right > area.Right) parts.Add($"{r.Right - area.Right} right of x={area.Right - 1}");
        if (r.Bottom > area.Bottom) parts.Add($"{r.Bottom - area.Bottom} below y={area.Bottom - 1}");
        return string.Join(", ", parts);
    }

    private static bool Sized(LayoutNode n) => n.Screen.Width > 0 && n.Screen.Height > 0;

    private static void Offscreen(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.Offscreen;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var n in ctx.Nodes)
        {
            if (!n.VisibleFlag || !Sized(n) || n.Role is LayoutRole.Application || IsBackground(n)) continue;
            any = true;
            if (Geometry.Contains(ctx.Screen, n.Screen)) continue;
            // Every control is reported (each one names lost cells); a window's own border parts (frame, frame-line scroll
            // bars and indicators) are covered by the window's finding.
            if (n.Parent is { } p && IsWindow(p) && (n.Role == LayoutRole.Frame || OnFrameLine(n, p))) continue;
            int outside = Geometry.ClippedCells(n.Screen, ctx.Screen);
            sink.Add(rule, LayoutFindingClasses.Error, "high", n, n.Screen,
                $"{n.Kind} at {n.Screen} extends outside the {ctx.Screen.Width}x{ctx.Screen.Height} screen ({Sides(n.Screen, ctx.Screen)}); {outside} cell(s) are never drawn. Move or resize it inside the screen.",
                $"screen={ctx.Screen}; rect={n.Screen}; outsideCells={outside}");
        }
        if (any) sink.Evaluated(rule); else sink.NotApplicable(rule);
    }

    // ── geom.outside-parent ─────────────────────────────────────────────────

    /// <summary>The owner's working area for its children, and whether leaving it is a hard (error) violation.</summary>
    internal static (CellRect Area, bool Hard, string What)? WorkingArea(LayoutContext ctx, LayoutNode owner)
    {
        switch (owner.Role)
        {
            case LayoutRole.Application: case LayoutRole.Desktop: return null;   // offscreen / outside-desktop cover these
            case LayoutRole.Window: case LayoutRole.Dialog:
                bool framed = owner.Children.Any(c => c.Role == LayoutRole.Frame && c.VisibleFlag);
                var r = owner.Screen;
                return framed && r.Width >= 2 && r.Height >= 2
                    ? (new CellRect(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), true, "window interior inside the frame")
                    : (r, true, "window rectangle");
            case LayoutRole.GroupBox:
                var g = owner.Screen;
                return GroupBoxBorder(ctx, owner) && g.Width >= 2 && g.Height >= 2
                    ? (new CellRect(g.X + 1, g.Y + 1, g.Width - 2, g.Height - 2), true, "group box content inside its border")
                    : (g, true, "group box rectangle");
            case LayoutRole.ComboBox: return (owner.Screen, true, "combo box rectangle");
            default:
                // A plain group may scroll its content on purpose (it owns a scroll bar): not judged.
                if (owner.Children.Any(c => c.Role == LayoutRole.ScrollBar)) return null;
                return (owner.Screen, false, "owner rectangle");
        }
    }

    /// <summary>TGroupBox draws a single-line border when ShowBorder is on (not exposed): probed from the captured corners.</summary>
    private static bool GroupBoxBorder(LayoutContext ctx, LayoutNode box)
    {
        var r = box.Screen;
        char At(int x, int y) => x >= 0 && y >= 0 && x < ctx.Cells.Width && y < ctx.Cells.Height ? ctx.Cells[x, y].Character : '\0';
        return r.Width >= 2 && r.Height >= 2 && At(r.X, r.Y) is '┌' or '╔' or '+' && At(r.X, r.Bottom - 1) is '└' or '╚' or '+';
    }

    /// <summary>Views that legitimately sit on a window's frame line: scroll bars (StandardScrollBar) and indicators.</summary>
    private static bool OnFrameLine(LayoutNode n, LayoutNode window)
    {
        if (n.Role is not (LayoutRole.ScrollBar or LayoutRole.Indicator)) return false;
        var w = window.Screen; var r = n.Screen;
        if (!Geometry.Contains(w, r)) return false;
        bool vertical = r.Width == 1 && (r.X == w.Right - 1 || r.X == w.X);
        bool horizontal = r.Height == 1 && (r.Y == w.Bottom - 1 || r.Y == w.Y);
        return vertical || horizontal;
    }

    private static void OutsideParent(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.OutsideParent;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var n in ctx.Nodes)
        {
            if (n.Parent is not { } p || !n.VisibleFlag || !Sized(n)) continue;
            if (n.Role == LayoutRole.Frame || IsBackground(n) || IsPopup(n)) continue;
            if (WorkingArea(ctx, p) is not { } wa) continue;
            if (IsWindow(p) && OnFrameLine(n, p)) continue;
            any = true;
            if (Geometry.Contains(wa.Area, n.Screen)) continue;
            int outside = Geometry.ClippedCells(n.Screen, wa.Area);
            var visible = Geometry.Intersection(n.Screen, wa.Area);
            sink.Add(rule, wa.Hard ? LayoutFindingClasses.Error : LayoutFindingClasses.Warning, wa.Hard ? "high" : "medium", n, n.Screen,
                $"{n.Kind} at {n.Screen} extends outside its owner's {wa.What} {wa.Area} ({Sides(n.Screen, wa.Area)}); {outside} cell(s) are clipped or drawn over the border."
                + (wa.Hard ? " Move it inside or enlarge the owner." : " If the owner scrolls its content on purpose, suppress this rule for it."),
                $"owner={p.Path} {p.Kind} {p.Screen}; area={wa.Area}; rect={n.Screen}; insideArea={visible}; outsideCells={outside}", p);
        }
        if (any) sink.Evaluated(rule); else sink.NotApplicable(rule);
    }

    // ── geom.outside-desktop ────────────────────────────────────────────────

    private static void OutsideDesktop(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.OutsideDesktop;
        if (!sink.Enabled(rule)) return;
        var windows = ctx.Nodes.Where(n => IsWindow(n) && n.VisibleFlag && Sized(n) && !IsPopup(n)
            && n.Parent is { Role: LayoutRole.Desktop or LayoutRole.Application }).ToArray();
        if (windows.Length == 0) { sink.NotApplicable(rule); return; }
        if (ctx.WorkingArea is not { } desk) { sink.Unavailable(rule, "The capture has no desktop working area."); return; }
        sink.Evaluated(rule);
        foreach (var w in windows)
        {
            if (Geometry.Contains(desk, w.Screen)) continue;
            int outside = Geometry.ClippedCells(w.Screen, desk);
            string over = (w.Screen.Y < desk.Y ? " It covers the menu bar row(s)." : "") + (w.Screen.Bottom > desk.Bottom ? " It covers the status line row(s) or leaves the screen." : "");
            sink.Add(rule, LayoutFindingClasses.Error, "high", w, w.Screen,
                $"{w.Kind} at {w.Screen} is not inside the desktop working area {desk} ({Sides(w.Screen, desk)}).{over} Place or size it inside the desktop.",
                $"desktop={desk}; rect={w.Screen}; outsideCells={outside}");
        }
    }

    // ── geom.focus-hidden / geom.focus-clipped ──────────────────────────────

    private static void Focus(LayoutContext ctx, LayoutRuleSink sink)
    {
        bool hiddenOn = sink.Enabled(LayoutRules.FocusHidden), clippedOn = sink.Enabled(LayoutRules.FocusClipped);
        if (!hiddenOn && !clippedOn) return;
        var f = ctx.Focused;
        // The focus chain ends at the modal (or application) itself when nothing inside is current: no focused control.
        if (f is null || !ctx.InScope(f) || f.IsMenu || f.Role is LayoutRole.Application or LayoutRole.Desktop
            || IsWindow(f) || ReferenceEquals(f, ctx.Modal))
        {
            sink.NotApplicable(LayoutRules.FocusHidden); sink.NotApplicable(LayoutRules.FocusClipped);
            return;
        }
        sink.Evaluated(LayoutRules.FocusHidden); sink.Evaluated(LayoutRules.FocusClipped);
        if (!f.VisibleFlag)
        {
            var hidden = f.Node.Flags.Visible ? FirstHiddenOwner(f) : f;
            sink.Add(LayoutRules.FocusHidden, LayoutFindingClasses.Error, "high", f, f.Screen,
                hidden is not null && !ReferenceEquals(hidden, f)
                    ? $"The focused {f.Kind} is inside a hidden owner ({hidden.Describe()}); keyboard input goes to a control the user cannot see. Show the owner or move the focus."
                    : $"The focused {f.Kind} is hidden (sfVisible off); keyboard input goes to a control the user cannot see. Show it or move the focus.",
                $"rect={f.Screen}; visible=null");
            return;
        }
        var v = f.Visible!.Value;
        if (v.IsEmpty || !Sized(f))
        {
            sink.Add(LayoutRules.FocusHidden, LayoutFindingClasses.Error, "high", f, f.Screen,
                $"The focused {f.Kind} at {f.Screen} is entirely clipped (no cell of it is on screen inside its owners); move it into view or move the focus.",
                $"rect={f.Screen}; visible={v}");
            return;
        }
        if (v != f.Screen)
        {
            int cut = Geometry.ClippedCells(f.Screen, v);
            sink.Add(LayoutRules.FocusClipped, LayoutFindingClasses.Error, "high", f, f.Screen,
                $"The focused {f.Kind} at {f.Screen} is partly clipped: only {v} is visible ({cut} cell(s) cut by its owners or the screen). Move it fully into its owner's working area.",
                $"rect={f.Screen}; visible={v}; clippedCells={cut}");
        }
    }

    private static LayoutNode? FirstHiddenOwner(LayoutNode n)
    {
        for (var o = n.Parent; o is not null; o = o.Parent) if (!o.Node.Flags.Visible) return o;
        return null;
    }

    // ── geom.overlap ────────────────────────────────────────────────────────

    private static bool OverlapCandidate(LayoutNode n) =>
        n.Selectable && n.Shown && Sized(n) && !IsPopup(n) && !IsBackground(n) && n.Role switch
        {
            LayoutRole.Application or LayoutRole.Desktop or LayoutRole.Window or LayoutRole.Dialog or LayoutRole.Frame
                or LayoutRole.ScrollBar or LayoutRole.MenuBar or LayoutRole.MenuBox or LayoutRole.StatusLine or LayoutRole.Background => false,
            _ => true,
        };

    private static IEnumerable<LayoutNode> Groups(LayoutContext ctx) => ctx.Nodes.Where(n => n.Children.Count > 1);

    private static void Overlap(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.Overlap;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var g in Groups(ctx))
        {
            var c = g.Children.Where(OverlapCandidate).ToArray();
            if (c.Length < 2) continue;
            any = true;
            for (int i = 0; i < c.Length; i++)
                for (int j = i + 1; j < c.Length; j++)
                {
                    var a = c[i]; var b = c[j];
                    var inter = Geometry.Intersection(a.Screen, b.Screen);
                    if (inter.IsEmpty) continue;
                    // A group box drawn behind sibling controls it fully contains is a decorative container.
                    if (a.Role == LayoutRole.GroupBox && Geometry.Contains(a.Screen, b.Screen) || b.Role == LayoutRole.GroupBox && Geometry.Contains(b.Screen, a.Screen)) continue;
                    var (first, second) = string.CompareOrdinal(a.Path, b.Path) <= 0 ? (a, b) : (b, a);
                    sink.Add(rule, LayoutFindingClasses.Error, "high", first, inter,
                        $"Selectable {first.Kind} {first.Screen} and {second.Kind} {second.Screen} overlap in {inter} ({Geometry.Area(inter)} cell(s)); the front one hides part of the other and mouse clicks there reach only one. Separate them.",
                        $"a={first.Screen}; b={second.Screen}; overlap={inter}; cells={Geometry.Area(inter)}", second);
                }
        }
        if (any) sink.Evaluated(rule); else sink.NotApplicable(rule);
    }

    // ── geom.label-collision ────────────────────────────────────────────────

    private static bool IsText(LayoutNode n) => n.Role is LayoutRole.Label or LayoutRole.StaticText;

    private static bool CollisionPeer(LayoutNode n) =>
        n.Shown && Sized(n) && !IsPopup(n) && !IsBackground(n) && n.Role switch
        {
            LayoutRole.Application or LayoutRole.Desktop or LayoutRole.Window or LayoutRole.Dialog or LayoutRole.Frame
                or LayoutRole.MenuBar or LayoutRole.MenuBox or LayoutRole.StatusLine or LayoutRole.Background => false,
            LayoutRole.Other or LayoutRole.Group => n.Selectable || n.Children.Count == 0,
            _ => true,
        };

    private static void LabelCollision(LayoutContext ctx, LayoutRuleSink sink)
    {
        const string rule = LayoutRules.LabelCollision;
        if (!sink.Enabled(rule)) return;
        bool any = false;
        foreach (var g in ctx.Nodes.Where(n => n.Children.Count > 0))
        {
            var texts = g.Children.Where(n => IsText(n) && n.Shown && Sized(n)).ToArray();
            if (texts.Length == 0) continue;
            any = true;
            foreach (var t in texts)
                foreach (var o in g.Children)
                {
                    if (ReferenceEquals(o, t) || !CollisionPeer(o)) continue;
                    if (IsText(o) && string.CompareOrdinal(o.Path, t.Path) < 0) continue;   // text/text pairs once
                    if (o.Role == LayoutRole.GroupBox && Geometry.Contains(o.Screen, t.Screen)) continue;
                    if (OnWindowFrame(o, g)) continue;
                    var inter = Geometry.Intersection(t.Screen, o.Screen);
                    if (inter.IsEmpty) continue;
                    bool linked = t.Property("link") == o.Path;
                    string relation = linked ? "its linked control" : IsText(o) ? "another text" : "an adjacent control";
                    sink.Add(rule, LayoutFindingClasses.Error, "high", t, inter,
                        $"{t.Kind} {t.Screen} collides with {relation} {o.Kind} {o.Screen} in {inter} ({Geometry.Area(inter)} cell(s)); TSharpVision paints the whole rectangle of each, so one overwrites the other. "
                        + (linked ? "Put the label beside or above its control without overlap." : "Move or shrink one of them."),
                        $"text={t.Screen}; other={o.Screen}; overlap={inter}; cells={Geometry.Area(inter)}; linked={(linked ? "yes" : "no")}", o);
                }
        }
        if (any) sink.Evaluated(rule); else sink.NotApplicable(rule);
    }

    private static bool OnWindowFrame(LayoutNode n, LayoutNode owner) => IsWindow(owner) && OnFrameLine(n, owner);
}
