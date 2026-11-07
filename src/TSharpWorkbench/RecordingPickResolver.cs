using TSharpWorkbench.Testing;

// Pure pick resolver.

/// <summary>Flags a resolved pick can carry in addition to <see cref="CandidateFlags"/>.</summary>
internal static class PickFlags
{
    internal const string NoSnapshot = "no-snapshot", SnapshotMismatch = "snapshot-mismatch", CellsUnreadable = "cells-unreadable",
        NoInspection = "no-inspection", NoViewTree = "no-view-tree", TreeTruncated = "view-tree-truncated",
        AmbiguousSelector = "ambiguous-selector", NoTarget = "no-target", HitPathMismatch = "hit-path-mismatch",
        RegionClipped = "region-clipped", RegionOnly = "region-only";
}

/// <summary>Pure: resolves pick boundaries against their own frozen snapshots (region always; target/selectors when unique).</summary>
/// <remarks>Rules: region data always (clipped to the snapshot). Control picks: menus first (open levels innermost first,
/// then the bar; an entry's model Screen), then the status line (StatusEntry Screen), then views (the live HitPath when it
/// names a node of the captured tree, else a front-to-back hit test of the captured tree by <see cref="ViewNode.Visible"/>;
/// a TFrame hit is promoted to its window). Selectors, first unique wins: Id, else Type+Text, else Type; each verified with
/// <see cref="ViewSelector.Matches"/> on the whole captured tree (exactly one match). Never a Path selector. A truncated
/// tree yields no selector (uniqueness unverifiable). Region picks keep the region only (no view selector).</remarks>
internal static class RecordingPickResolver
{
    private const int MaxRegionText = 4096;

    /// <param name="pick">A boundary of Kind <c>pick</c> (others are ignored: returns null).</param>
    /// <param name="snapshot">The snapshot whose Id equals pick.SnapshotId, or null when it is missing.</param>
    internal static PickSelection? Resolve(RecordedBoundary pick, RetainedSnapshot? snapshot)
    {
        if (pick.Kind != Protocol.BoundaryKinds.Pick || pick.Pick is not { } p) return null;
        var flags = new List<string>();
        if (snapshot is not null && (snapshot.Id != pick.SnapshotId || snapshot.BoundaryId != pick.Id || snapshot.Hash != pick.Hash))
        {
            flags.Add(PickFlags.SnapshotMismatch);
            snapshot = null;
        }
        CellRect region = p.Region;
        if (snapshot is null)
        {
            flags.Add(pick.SnapshotDropped ? CandidateFlags.SnapshotDropped
                : pick.SnapshotId is null ? PickFlags.NoSnapshot : CandidateFlags.SnapshotNotFetched);
            flags.Add(PickFlags.RegionOnly);
            return Selection(pick, p, region, EvidenceLevels.Missing, null, [], null, flags);
        }

        ScreenSnapshot? cells = null;
        try { cells = ScreenSnapshot.Deserialize(Convert.FromBase64String(snapshot.Cells)); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        { flags.Add(PickFlags.CellsUnreadable); }
        int width = cells?.Width ?? snapshot.Width, height = cells?.Height ?? snapshot.Height;
        var clipped = Clip(region, width, height);
        if (clipped != region) { flags.Add(PickFlags.RegionClipped); region = clipped; }
        string? regionText = cells is not null && !region.IsEmpty ? Inspectors.Cells(cells, region, MaxRegionText) : null;

        var inspection = snapshot.Inspection;
        if (snapshot.Omitted is not null) flags.Add(CandidateFlags.InspectionOmitted);
        string evidence = inspection is null ? EvidenceLevels.HashOnly : EvidenceLevels.Snapshot;
        if (inspection is null) flags.Add(PickFlags.NoInspection);

        if (p.Kind == Protocol.PickKinds.Region)
        {
            flags.Add(PickFlags.RegionOnly);
            return Selection(pick, p, region, evidence, new(PickAreas.Region, null, null, null, null, regionText, region, null, null),
                [], regionText, flags);
        }

        ViewTree? tree = inspection?.ViewTree;
        if (inspection is not null && tree is null) flags.Add(PickFlags.NoViewTree);
        var (target, node) = Target(p, inspection, tree, flags);
        IReadOnlyList<SelectorCandidate> selectors = [];
        if (node is not null && tree is not null)
        {
            if (tree.Truncated) flags.Add(PickFlags.TreeTruncated);
            else if (Selector(node, tree) is { } selector) selectors = [selector];
            else flags.Add(PickFlags.AmbiguousSelector);
        }
        if (target is null) { flags.Add(PickFlags.NoTarget); flags.Add(PickFlags.RegionOnly); }
        return Selection(pick, p, region, evidence, target, selectors, regionText, flags);
    }

    /// <summary>Every pick boundary of the timeline resolved against the fetched snapshots, in boundary order.</summary>
    internal static IReadOnlyList<PickSelection> ResolveAll(IReadOnlyList<RecordedBoundary> boundaries, IReadOnlyList<RetainedSnapshot> snapshots) =>
        boundaries.Where(b => b.Kind == Protocol.BoundaryKinds.Pick)
            .Select(b => Resolve(b, snapshots.FirstOrDefault(s => s.Id == b.SnapshotId))).OfType<PickSelection>().ToArray();

    private static PickSelection Selection(RecordedBoundary b, RecordedPick p, CellRect region, string evidence, PickTarget? target,
        IReadOnlyList<SelectorCandidate> selectors, string? regionText, List<string> flags) =>
        new(b.Id, p.Kind, p.Trigger, b.AfterSequence, p.AnchorBoundaryId, b.Hash, b.Generation, b.Width, b.Height, region, b.SnapshotId,
            evidence, target, selectors, regionText, flags.Distinct(StringComparer.Ordinal).ToArray());

    private static CellRect Clip(CellRect r, int width, int height)
    {
        int x = Math.Clamp(r.X, 0, Math.Max(0, width)), y = Math.Clamp(r.Y, 0, Math.Max(0, height));
        int right = Math.Clamp(r.Right, x, Math.Max(0, width)), bottom = Math.Clamp(r.Bottom, y, Math.Max(0, height));
        return new(x, y, right - x, bottom - y);
    }

    private static bool Contains(CellRect r, int x, int y) => !r.IsEmpty && x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;

    private static (PickTarget? Target, ViewNode? Node) Target(RecordedPick p, CheckpointInspection? inspection, ViewTree? tree, List<string> flags)
    {
        int x = p.X, y = p.Y;
        if (inspection?.Menu is { } menu)
        {
            var levels = menu.Open.Reverse().Concat(menu.Bar is { } bar ? [bar] : Array.Empty<MenuViewState>());
            foreach (var level in levels.Where(l => l.Visible && Contains(l.Screen, x, y)))
            {
                var node = level.Path is { } path && tree is not null ? Find(tree.Root, path) : null;
                var item = level.Items.FirstOrDefault(i => !i.Separator && i.Screen is { } s && Contains(s, x, y));
                return item is not null
                    ? (new(PickAreas.MenuItem, level.Path, level.Type, node?.Kind, node?.Id, item.Text, item.Screen, item.Text, null), node)
                    : (new(PickAreas.Menu, level.Path, level.Type, node?.Kind, node?.Id, level.SelectedText, level.Screen, null, null), node);
            }
        }
        if (inspection?.Status is { } status && Contains(status.Screen, x, y))
        {
            var node = status.Path is { } path && tree is not null ? Find(tree.Root, path) : null;
            var item = status.Items.FirstOrDefault(i => i.Drawn && i.Screen is { } s && Contains(s, x, y));
            return item is not null
                ? (new(PickAreas.StatusItem, status.Path, status.Type, node?.Kind, node?.Id, item.Text, item.Screen, null, item.Text), node)
                : (new(PickAreas.Status, status.Path, status.Type, node?.Kind, node?.Id, null, status.Screen, null, null), node);
        }
        if (tree is null) return (null, null);
        ViewNode? hit = HitTest(tree.Root, x, y);
        if (p.HitPath is { } live)
        {
            var named = Find(tree.Root, live);
            if (named is null || !ReferenceEquals(named, hit)) flags.Add(PickFlags.HitPathMismatch);
            if (named is not null) hit = named;
        }
        if (hit is not null && hit.Kind == "TFrame" && Parent(tree.Root, hit) is { } window) hit = window;
        if (hit is null || ReferenceEquals(hit, tree.Root)) return (null, null);
        return (new(PickAreas.View, hit.Path, hit.Type, hit.Kind, hit.Id, hit.Text, hit.Visible ?? hit.Screen, null, null), hit);
    }

    /// <summary>Front-to-back descent over the captured tree (children are front-to-back), by each node's visible rect.</summary>
    private static ViewNode? HitTest(ViewNode root, int x, int y)
    {
        if (root.Visible is not { } v || !Contains(v, x, y)) return null;
        ViewNode hit = root;
        for (int depth = 0; depth < 64; depth++)
        {
            var child = hit.Children.FirstOrDefault(c => c.Flags.Visible && c.Visible is { } cv && Contains(cv, x, y));
            if (child is null) break;
            hit = child;
        }
        return hit;
    }

    private static SelectorCandidate? Selector(ViewNode node, ViewTree tree)
    {
        var all = All(tree.Root).ToArray();
        string type = ShortType(node);
        var tries = new List<(ViewSelector Selector, string Basis)>();
        if (node.Id is { } id) tries.Add((ViewSelector.ById(id), SelectorBases.Id));
        // A menu view's model text is its current item: volatile, never a selector basis.
        if (node.Text is { } text && !node.Kind.StartsWith("TMenu", StringComparison.Ordinal)) tries.Add((new ViewSelector(Type: type, Text: text), SelectorBases.TypeText));
        tries.Add((ViewSelector.OfType(type), SelectorBases.Type));
        foreach (var (selector, basis) in tries)
        {
            if (!selector.Matches(node)) continue;
            int matches = all.Count(selector.Matches);
            if (matches == 1) return new(selector, basis, matches);
        }
        return null;
    }

    private static string ShortType(ViewNode node)
    {
        string name = node.Type;
        int cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+'));
        string shortName = cut >= 0 ? name[(cut + 1)..] : name;
        return new ViewSelector(Type: shortName).Matches(node) ? shortName : name;
    }

    private static IEnumerable<ViewNode> All(ViewNode node) => new[] { node }.Concat(node.Children.SelectMany(All));

    private static ViewNode? Find(ViewNode node, string path) =>
        node.Path == path ? node : node.Children.Select(c => Find(c, path)).FirstOrDefault(n => n is not null);

    private static ViewNode? Parent(ViewNode root, ViewNode child) =>
        root.Children.Any(c => ReferenceEquals(c, child)) ? root : root.Children.Select(c => Parent(c, child)).FirstOrDefault(n => n is not null);
}
