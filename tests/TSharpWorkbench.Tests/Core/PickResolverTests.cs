using System.Text.Json;
using TSharpVision;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Workstream C: the pure pick resolver over hand-built frozen snapshots (cells + inspection), no process.
public sealed class PickResolverTests
{
    private const int W = 40, H = 12;
    private const ulong Hash = 0xabcdef;

    // ── views ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IdSelectorWinsWhenTheHitViewHasAUniqueId()
    {
        var s = Resolve(Pick(27, 6));
        Assert.Equal(EvidenceLevels.Snapshot, s.Evidence);
        Assert.Equal((PickAreas.View, "0/2/1/1"), (s.Target!.Area, s.Target.Path));
        var c = Assert.Single(s.Selectors);
        Assert.Equal((SelectorBases.Id, 1, true), (c.Basis, c.Matches, c.Unique));
        Assert.Equal(ViewSelector.ById("name-input"), c.Selector);
    }

    [Fact]
    public void UniqueTypeAndTextIsUsedWithoutAnId()
    {
        var s = Resolve(Pick(13, 4));
        var c = Assert.Single(s.Selectors);
        Assert.Equal(SelectorBases.TypeText, c.Basis);
        Assert.Equal(new ViewSelector(Type: "TButton", Text: "Cancel"), c.Selector);
        Assert.Equal("Cancel", s.Target!.Text);
    }

    [Fact]
    public void UniqueTypeIsTheLastResort()
    {
        var s = Resolve(Pick(18, 5));
        var c = Assert.Single(s.Selectors);
        Assert.Equal((SelectorBases.Type, "TScrollBar"), (c.Basis, c.Selector.Type));
        Assert.Null(c.Selector.Text);
    }

    [Fact]
    public void AmbiguousViewKeepsTheTargetButNoSelector()
    {
        var s = Resolve(Pick(27, 4));                            // "OK" exists in both windows, TButton three times
        Assert.Equal((PickAreas.View, "0/2/1/0"), (s.Target!.Area, s.Target.Path));
        Assert.Empty(s.Selectors);
        Assert.Contains(PickFlags.AmbiguousSelector, s.Flags);
    }

    [Fact]
    public void DuplicateIdFallsBackToTheNextUniqueBasis()
    {
        var tree = Tree(duplicateId: true);
        var s = Resolve(Pick(27, 6), Snapshot(Inspection(tree)));
        var c = Assert.Single(s.Selectors);
        Assert.Equal(SelectorBases.TypeText, c.Basis);
        Assert.Equal(new ViewSelector(Type: "TInputLine", Text: "abc"), c.Selector);
    }

    [Fact]
    public void FrameHitIsPromotedToItsWindow()
    {
        var s = Resolve(Pick(2, 5));
        Assert.Equal("0/2/0", s.Target!.Path);
        Assert.Equal(new ViewSelector(Type: "TWindow", Text: "Editor"), Assert.Single(s.Selectors).Selector);
    }

    [Fact]
    public void HiddenViewInFrontIsSkippedByTheSnapshotHitTest()
    {
        var s = Resolve(Pick(30, 8));                            // hidden popup covers (28,7..) but Visible is null
        Assert.Equal("0/2/1/3", s.Target!.Path);                  // the visible view behind it
        Assert.Equal((SelectorBases.Type, "Gauge"), (Assert.Single(s.Selectors).Basis, s.Selectors[0].Selector.Type));
    }

    [Fact]
    public void LiveHitPathIsPreferredAndADisagreementIsFlagged()
    {
        var agree = Resolve(Pick(13, 4, hitPath: "0/2/0/1"));
        Assert.DoesNotContain(PickFlags.HitPathMismatch, agree.Flags);
        var disagree = Resolve(Pick(13, 4, hitPath: "0/2/0/0"));
        Assert.Contains(PickFlags.HitPathMismatch, disagree.Flags);
        Assert.Equal("0/2/0/0", disagree.Target!.Path);
        var unknown = Resolve(Pick(13, 4, hitPath: "0/9/9"));
        Assert.Contains(PickFlags.HitPathMismatch, unknown.Flags);
        Assert.Equal("0/2/0/1", unknown.Target!.Path);          // falls back to the snapshot hit test
    }

    [Fact]
    public void BackgroundOnlyOrRootHitHasNoViewSelectorUnlessUnique()
    {
        var s = Resolve(Pick(30, 10));
        Assert.Equal("TBackground", s.Target!.Kind);
        Assert.Equal((SelectorBases.Type, "TBackground"), (Assert.Single(s.Selectors).Basis, s.Selectors[0].Selector.Type));
    }

    [Fact]
    public void TruncatedTreeGivesNoSelector()
    {
        var s = Resolve(Pick(27, 6), Snapshot(Inspection(Tree() with { Truncated = true })));
        Assert.NotNull(s.Target);
        Assert.Empty(s.Selectors);
        Assert.Contains(PickFlags.TreeTruncated, s.Flags);
    }

    // ── menus and status line ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MenuItemOfTheOpenBoxIsCheckedBeforeTheBarAndViews()
    {
        var box = Resolve(Pick(3, 3));
        Assert.Equal((PickAreas.MenuItem, "Open", "0/3"), (box.Target!.Area, box.Target.MenuItem, box.Target.Path));
        Assert.Equal(new CellRect(1, 3, 10, 1), box.Target.Screen);
        Assert.Equal((SelectorBases.Type, "TMenuBox"), (Assert.Single(box.Selectors).Basis, box.Selectors[0].Selector.Type));
        var bar = Resolve(Pick(2, 0));
        Assert.Equal((PickAreas.MenuItem, "File", "0/0"), (bar.Target!.Area, bar.Target.MenuItem, bar.Target.Path));
        Assert.Equal(ViewSelector.OfType("TMenuBar"), Assert.Single(bar.Selectors).Selector); // current-item text is volatile
        var barGap = Resolve(Pick(30, 0));
        Assert.Equal(PickAreas.Menu, barGap.Target!.Area);
    }

    [Fact]
    public void StatusItemAndStatusLine()
    {
        var item = Resolve(Pick(3, 11));
        Assert.Equal((PickAreas.StatusItem, "Alt-X Exit"), (item.Target!.Area, item.Target.StatusItem));
        Assert.Equal((SelectorBases.Type, "TStatusLine"), (Assert.Single(item.Selectors).Basis, item.Selectors[0].Selector.Type));
        var line = Resolve(Pick(30, 11));
        Assert.Equal(PickAreas.Status, line.Target!.Area);
    }

    // ── degraded evidence ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoViewTreeKeepsTheRegionOnly()
    {
        var s = Resolve(Pick(13, 4), Snapshot(new CheckpointInspection(null, null, null, ["view-tree: inspector failed"])));
        Assert.Null(s.Target);
        Assert.Empty(s.Selectors);
        Assert.Contains(PickFlags.NoViewTree, s.Flags);
        Assert.Contains(PickFlags.RegionOnly, s.Flags);
        Assert.Equal(new CellRect(13, 4, 1, 1), s.Region);
        Assert.Equal("a", s.RegionText);                          // "Cancel" starts at 12
    }

    [Fact]
    public void OmittedInspectionIsHashOnlyWithCellsStillRead()
    {
        var s = Resolve(Pick(13, 4), Snapshot(null) with { Omitted = Protocol.SnapshotOmissions.InspectionSize });
        Assert.Equal(EvidenceLevels.HashOnly, s.Evidence);
        Assert.Contains(CandidateFlags.InspectionOmitted, s.Flags);
        Assert.Equal("a", s.RegionText);
        Assert.Null(s.Target);
    }

    [Theory]
    [InlineData(5L, false, CandidateFlags.SnapshotNotFetched)]
    [InlineData(null, true, CandidateFlags.SnapshotDropped)]
    [InlineData(null, false, PickFlags.NoSnapshot)]
    public void MissingSnapshotIsEvidenceMissingWithRegionOnly(long? snapshotId, bool dropped, string flag)
    {
        var pick = Pick(13, 4) with { SnapshotId = snapshotId, SnapshotDropped = dropped };
        var s = RecordingPickResolver.Resolve(pick, null)!;
        Assert.Equal(EvidenceLevels.Missing, s.Evidence);
        Assert.Contains(flag, s.Flags);
        Assert.Contains(PickFlags.RegionOnly, s.Flags);
        Assert.Null(s.Target); Assert.Empty(s.Selectors); Assert.Null(s.RegionText);
        Assert.Equal(new CellRect(13, 4, 1, 1), s.Region);
    }

    [Fact]
    public void ASnapshotOfAnotherStateIsNeverUsed()
    {
        var s = Resolve(Pick(13, 4), Snapshot(Inspection(Tree())) with { Hash = 1 });
        Assert.Equal(EvidenceLevels.Missing, s.Evidence);
        Assert.Contains(PickFlags.SnapshotMismatch, s.Flags);
        Assert.Null(s.Target);
    }

    [Fact]
    public void RegionPickKeepsNormalizedClippedRegionTextAndNoSelector()
    {
        var s = Resolve(Pick(4, 4, region: new CellRect(4, 4, 10, 1), kind: Protocol.PickKinds.Region));
        Assert.Equal((PickAreas.Region, "OK      Ca"), (s.Target!.Area, s.RegionText));
        Assert.Empty(s.Selectors);
        var clipped = Resolve(Pick(36, 10, region: new CellRect(36, 10, 10, 5), kind: Protocol.PickKinds.Region));
        Assert.Equal(new CellRect(36, 10, 4, 2), clipped.Region);
        Assert.Contains(PickFlags.RegionClipped, clipped.Flags);
    }

    [Fact]
    public void NeverAPathSelectorAndDeterministic()
    {
        var points = new[] { (27, 6), (13, 4), (18, 5), (27, 4), (2, 5), (3, 3), (2, 0), (3, 11), (30, 10), (0, 0) };
        foreach (var (x, y) in points)
        {
            var a = Resolve(Pick(x, y));
            var b = Resolve(Pick(x, y));
            Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
            Assert.All(a.Selectors, c => Assert.Null(c.Selector.Path));
            Assert.All(a.Selectors, c => Assert.Equal(1, c.Matches));
            Assert.All(a.Selectors, c => Assert.Equal(1, All(Tree().Root).Count(c.Selector.Matches)));
        }
    }

    [Fact]
    public void ResolveAllIgnoresStateBoundariesAndMatchesSnapshotsById()
    {
        var state = new RecordedBoundary(1, Protocol.BoundaryKinds.Idle, Protocol.BoundaryCauses.Input, 0, 0, 1, Hash, 0, W, H, null, 0);
        var first = Pick(13, 4) with { Id = 2, SnapshotId = 7 };
        var second = Pick(27, 6) with { Id = 3, SnapshotId = 8 };
        var snapshots = new[] { Snapshot(Inspection(Tree())) with { Id = 8, BoundaryId = 3 } };
        var picks = RecordingPickResolver.ResolveAll([state, first, second], snapshots);
        Assert.Equal([2L, 3L], picks.Select(p => p.PickId));
        Assert.Equal([EvidenceLevels.Missing, EvidenceLevels.Snapshot], picks.Select(p => p.Evidence));
        Assert.Null(RecordingPickResolver.Resolve(state, null));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static PickSelection Resolve(RecordedBoundary pick, RetainedSnapshot? snapshot = null) =>
        RecordingPickResolver.Resolve(pick, snapshot ?? Snapshot(Inspection(Tree())))!;

    private static RecordedBoundary Pick(int x, int y, string? hitPath = null, CellRect? region = null, string kind = Protocol.PickKinds.Control) =>
        new(4, Protocol.BoundaryKinds.Pick, Protocol.BoundaryCauses.Pick, 3, 3, 9, Hash, 100, W, H, null, 0, SnapshotId: 5)
        {
            Pick = new(kind, Protocol.PickTriggers.CtrlShift, x, y, region?.Right - 1 ?? x, region?.Bottom - 1 ?? y,
                region ?? new CellRect(x, y, 1, 1), 2, hitPath, 2),
        };

    private static RetainedSnapshot Snapshot(CheckpointInspection? inspection) =>
        new(5, 4, Protocol.SnapshotReasons.Pick, 3, 3, 9, Hash, 100, Cells(), W, H, inspection);

    private static string Cells()
    {
        var cells = Enumerable.Range(0, W * H).Select(_ => new TScreenChar(' ', 7)).ToArray();
        void Put(int x, int y, string text) { for (int i = 0; i < text.Length; i++) cells[y * W + x + i] = new TScreenChar(text[i], 7); }
        Put(1, 0, "File"); Put(4, 4, "OK"); Put(12, 4, "Cancel"); Put(26, 4, "OK"); Put(26, 6, "abc"); Put(1, 11, "Alt-X Exit");
        return Convert.ToBase64String(new ScreenSnapshot(W, H, cells).Serialize());
    }

    private static CheckpointInspection Inspection(ViewTree? tree) => new(tree, Menu(), Status(), []);

    private static MenuState Menu()
    {
        MenuEntry Entry(int i, string text, CellRect screen) =>
            new(i, text, "~" + text, null, 100 + i, true, false, false, false, null, 0, null, 0, false, screen, "match", 7);
        var bar = new MenuViewState(0, "TSharpVision.Menus.TMenuBar", "0/0", new(0, 0, W, 1), true, true, 0, "File", null,
            [Entry(0, "File", new(0, 0, 6, 1)), Entry(1, "Edit", new(6, 0, 6, 1))]);
        var box = new MenuViewState(1, "TSharpVision.Menus.TMenuBox", "0/3", new(0, 1, 12, 4), true, true, 0, "New", "File",
            [Entry(0, "New", new(1, 2, 10, 1)), Entry(1, "Open", new(1, 3, 10, 1))]);
        return new(true, 1, bar, [box]);
    }

    private static StatusState Status() =>
        new("TSharpVision.Menus.TStatusLine", "0/1", new(0, 11, W, 1), 0, 0, 0, 0xFFFF, null, null, "none",
            [new(0, "Alt-X Exit", "~Alt-X~ Exit", 0x2d00, "Alt+X", 1, true, true, new(0, 11, 12, 1), "match", 7)], " Alt-X Exit");

    private static ViewTree Tree(bool duplicateId = false)
    {
        ViewNode N(string path, string type, string kind, CellRect screen, string? text = null, string? id = null, bool visible = true,
            params ViewNode[] children) =>
            new(path, id, type, kind, screen, screen, visible ? screen : null,
                new ViewFlags(visible, true, false, false, false, false, false, false, true, false, 0, 0), 0, text, null, null, children, 0);
        var editor = N("0/2/0", "TSharpVision.Views.TWindow", "TWindow", new(2, 2, 20, 8), "Editor", null, true,
            N("0/2/0/0", "TSharpVision.Dialogs.TButton", "TButton", new(4, 4, 6, 1), "OK"),
            N("0/2/0/1", "TSharpVision.Dialogs.TButton", "TButton", new(12, 4, 8, 1), "Cancel"),
            N("0/2/0/2", "TSharpVision.Views.TScrollBar", "TScrollBar", new(18, 3, 1, 6)),
            N("0/2/0/3", "TSharpVision.Views.TFrame", "TFrame", new(2, 2, 20, 8)));
        var other = N("0/2/1", "TSharpVision.Views.TWindow", "TWindow", new(24, 2, 14, 8), "Other", null, true,
            N("0/2/1/0", "TSharpVision.Dialogs.TButton", "TButton", new(26, 4, 6, 1), "OK"),
            N("0/2/1/1", "TSharpVision.Dialogs.TInputLine", "TInputLine", new(26, 6, 10, 1), "abc", "name-input"),
            N("0/2/1/2", "Sample.PopupHint", "TView", new(28, 7, 8, 2), null, null, false),
            N("0/2/1/3", "Sample.Gauge", "TView", new(26, 8, 10, 1), null, duplicateId ? "name-input" : null),
            N("0/2/1/4", "TSharpVision.Views.TFrame", "TFrame", new(24, 2, 14, 8)));
        var desk = N("0/2", "TSharpVision.App.TDeskTop", "TDeskTop", new(0, 1, W, 10), null, null, true,
            editor, other, N("0/2/2", "TSharpVision.App.TBackground", "TBackground", new(0, 1, W, 10)));
        var root = N("0", "TSharpVision.App.TApplication", "TApplication", new(0, 0, W, H), null, null, true,
            N("0/0", "TSharpVision.Menus.TMenuBar", "TMenuBar", new(0, 0, W, 1), "File"),
            N("0/1", "TSharpVision.Menus.TStatusLine", "TStatusLine", new(0, 11, W, 1)),
            desk,
            N("0/3", "TSharpVision.Menus.TMenuBox", "TMenuBox", new(0, 1, 12, 4), "New"));
        return new(root, 16, false, "front-to-back", new(0, 0, W, H), new(0, 1, W, 10), "0/3", null);
    }

    private static IEnumerable<ViewNode> All(ViewNode n) => new[] { n }.Concat(n.Children.SelectMany(All));
}
