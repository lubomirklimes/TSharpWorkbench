using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// In-process: input acknowledgement against the real TProgram.GetEvent, read-only inspectors on a live view tree,
// assertion evaluation (including deliberately wrong expectations), geometry, grids, PNG glyphs and the orphan sweep.
[Collection("NonParallel")]
public sealed class TestWorkbenchInspectionTests
{
    private const ushort Print = 410, Open = 411;

    private sealed class InspectedApp : TApplication
    {
        public override TMenuBar InitMenuBar(TRect r)
        {
            r.b.y = r.a.y + 1;
            return new TMenuBar(r, new TSubMenu("~F~ile", Keys.kbAltF) +
                new TMenuItem("~O~pen", Open, Keys.kbF3, Views.hcNoContext, "F3") +
                new TMenuItem("~P~rint", Print, Keys.kbNoKey) { Disabled = true } +
                new TSubMenu("~W~indow", Keys.kbAltW) + new TMenuItem("~T~ile", Views.cmTile, Keys.kbNoKey));
        }
        public override TStatusLine InitStatusLine(TRect r)
        {
            r.a.y = r.b.y - 1;
            return new HintedStatus(r, new TStatusDef(0, 0xFFFF,
                new TStatusItem("~Alt+X~ Exit", Keys.kbAltX, Views.cmQuit,
                new TStatusItem("~F3~ Open", Keys.kbF3, Open,
                new TStatusItem(null, Keys.kbCtrlF5, Views.cmResize,
                new TStatusItem("~F10~ Menu", Keys.kbF10, Views.cmMenu))))));
        }
        private sealed class HintedStatus(TRect r, TStatusDef d) : TStatusLine(r, d)
        {
            public override string Hint(ushort aHelpCtx) => "Ready for input";
        }
    }

    private static (TDialog Dialog, TButton Ok, TButton Cancel, TInputLine Input) AddDialog(TApplication app)
    {
        var dialog = new TDialog(new TRect(10, 3, 50, 13), "Inspect me");
        var input = new TInputLine(new TRect(3, 2, 30, 3), 40); dialog.Insert(input);
        var ok = new TButton(new TRect(3, 6, 13, 8), "~O~K", Views.cmOK, ButtonConstants.bfDefault); dialog.Insert(ok);
        var cancel = new TButton(new TRect(15, 6, 25, 8), "Cancel", Views.cmCancel, ButtonConstants.bfNormal); dialog.Insert(cancel);
        input.SetData("hello"); input.Select();
        ViewTreeInspector.Identify(dialog, "dialog"); ViewTreeInspector.Identify(ok, "ok"); ViewTreeInspector.Identify(cancel, "cancel");
        ViewTreeInspector.Identify(input, "input");
        app.DeskTop!.Insert(dialog);
        return (dialog, ok, cancel, input);
    }

    private static void Drain(TApplication app)
    {
        var ev = new TEvent();
        for (int i = 0; i < 200; i++) { ev = default; app.GetEvent(ref ev); if (ev.What == Events.evNothing) return; }
    }

    // ── input acknowledgement ────────────────────────────────────────────────

    [Fact]
    public void TrackerSeesKeysOnlyAfterQueuedEventsAndQuiescenceOnlyAfterAnEmptyProbe()
    {
        using var scope = new DriverScope();
        var tracker = new TestInputDriver(scope.Driver);
        TDisplay.driver = tracker;
        using var app = new TApplication();
        Drain(app);
        long quietBefore = tracker.EmptyProbes;
        // Pre-existing framework events: they take priority over keyboard input.
        for (int i = 0; i < 4; i++) scope.Driver.EnqueueCommand(0x7d00);
        long first = tracker.Submit(TestKeys.KeyDown(Keys.kbDown));
        long second = tracker.Submit(TestKeys.KeyDown(Keys.kbDown)); // a repeated identical key is tracked separately
        var ev = new TEvent();
        for (int i = 0; i < 4; i++)
        {
            ev = default; app.GetEvent(ref ev);
            Assert.Equal((Events.evCommand, (ushort)0x7d00), (ev.What, ev.message.command));
            Assert.Equal(0, tracker.Consumed - (first - 1)); // not consumed while queued events remain
        }
        ev = default; app.GetEvent(ref ev);
        Assert.Equal((Events.evKeyDown, Keys.kbDown, first), (ev.What, ev.keyDown.keyCode, tracker.Consumed));
        Assert.Equal(quietBefore, tracker.EmptyProbes); // reading a key is not quiescence
        ev = default; app.GetEvent(ref ev);
        Assert.Equal((Events.evKeyDown, second), (ev.What, tracker.Consumed));
        Assert.False(tracker.EmptyProbes > tracker.ConsumedMark);
        ev = default; app.GetEvent(ref ev);
        Assert.Equal(Events.evNothing, ev.What);
        Assert.True(tracker.EmptyProbes > tracker.ConsumedMark); // only now is the loop quiescent
        // A pending (put-back) event is served before the keyboard is probed at all.
        var put = new TEvent { What = Events.evCommand }; put.message.command = 0x7d01; app.PutEvent(ref put);
        long probes = tracker.EmptyProbes;
        ev = default; app.GetEvent(ref ev);
        Assert.Equal((ushort)0x7d01, ev.message.command); Assert.Equal(probes, tracker.EmptyProbes);
        TDisplay.driver = scope.Driver;
    }

    // ── inspectors ───────────────────────────────────────────────────────────

    [Fact]
    public void ViewTreeIsBoundedReadOnlyAndSeparatesLocalScreenAndVisibleGeometry()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new InspectedApp();
        var (dialog, ok, cancel, input) = AddDialog(app);
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var tree = ViewTreeInspector.Inspect(app, cells);
        var nodes = AssertionEvaluator.All(tree.Root).ToArray();
        Assert.Equal(nodes.Length, tree.Nodes); Assert.False(tree.Truncated);
        Assert.Equal("front-to-back", tree.Order);
        Assert.DoesNotContain(nodes, n => n.Type.Contains("DispatchView"));
        var d = nodes.Single(n => n.Id == "dialog");
        Assert.Equal(("TDialog", "Inspect me"), (d.Kind, d.Text));
        Assert.Equal(new CellRect(10, 3, 40, 10), d.Bounds);           // desktop-relative
        Assert.Equal(new CellRect(10, 4, 40, 10), d.Screen);           // absolute: desktop starts at row 1
        Assert.Equal(d.Screen, d.Visible);
        var okNode = nodes.Single(n => n.Id == "ok");
        Assert.Equal(new CellRect(3, 6, 10, 2), okNode.Bounds);
        Assert.Equal(new CellRect(13, 10, 10, 2), okNode.Screen);
        Assert.Equal(("OK", "~O~K"), (okNode.Text, okNode.Properties!["markup"]));
        Assert.Equal("hello", nodes.Single(n => n.Id == "input").Text);
        Assert.Contains("OK", okNode.CellText);
        Assert.Equal(tree.FocusedPath, nodes.Single(n => n.Id == "input").Path);
        Assert.Null(tree.ModalPath); // inserted, not executed
        Assert.Equal(new CellRect(0, 1, 80, 23), tree.WorkingArea);
        Assert.Equal(ViewTreeInspector.PathOf(ok, app), okNode.Path);
        // Clipping: a view pushed past the screen edge keeps its true screen rectangle; Visible is the clipped part.
        dialog.MoveTo(60, 3);
        var moved = AssertionEvaluator.All(ViewTreeInspector.Inspect(app).Root).Single(n => n.Id == "dialog");
        Assert.Equal(new CellRect(60, 4, 40, 10), moved.Screen);
        Assert.Equal(new CellRect(60, 4, 20, 10), moved.Visible);
        // Bounded: limits cut the tree and say so.
        var small = ViewTreeInspector.Inspect(app, null, new InspectionLimits(MaxNodes: 4, MaxDepth: 2));
        Assert.True(small.Truncated); Assert.True(small.Nodes <= 4);
        Assert.True(AssertionEvaluator.All(small.Root).Sum(n => n.OmittedChildren) > 0);
        // JSON-ready and deterministic.
        Assert.Equal(JsonSerializer.Serialize(ViewTreeInspector.Inspect(app, cells)), JsonSerializer.Serialize(ViewTreeInspector.Inspect(app, cells)));
    }

    [Fact]
    public void MenuAndStatusInspectorsReportModelGeometryAndObservedCells()
    {
        using var scope = new DriverScope(30, 10);
        using var app = new InspectedApp();
        app.StatusLine!.Update();
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var menu = MenuInspector.Inspect(app, cells);
        Assert.Equal((false, 0), (menu.Active, menu.Depth));
        Assert.Equal(new[] { "File", "Window" }, menu.Bar!.Items.Select(i => i.Text));
        Assert.All(menu.Bar.Items, i => Assert.Equal("match", i.Observed));
        Assert.Equal(("F", true), (menu.Bar.Items[0].HotChar, menu.Bar.Items[0].Submenu));
        Assert.Equal(new CellRect(1, 0, 6, 1), menu.Bar.Items[0].Screen);

        var status = StatusInspector.Inspect(app, cells)!;
        Assert.Equal((0, 0, 0, 65535), (status.HelpContext, status.Definition, status.DefinitionMin, status.DefinitionMax));
        Assert.Equal(new CellRect(0, 9, 30, 1), status.Screen);
        // 30 columns: " Alt+X Exit " (12) + " F3 Open " (9) fit, " F10 Menu " (10) does not; the key-only item is hidden.
        Assert.Equal(new[] { "match", "match", "hidden", "not-drawn" }, status.Items.Select(i => i.Observed));
        Assert.Equal(new CellRect(12, 9, 9, 1), status.Items[1].Screen);
        Assert.Equal(("kbF3", (int)Open), (status.Items[1].KeyName, status.Items[1].Command));
        Assert.Null(status.Items[2].Text); Assert.Equal("kbCtrlF5", status.Items[2].KeyName);
        Assert.Equal("Ready for input", status.Hint);
        Assert.Equal("not-drawn", status.HintObserved); // no room after the items
        Assert.Equal(status.ObservedRow, Inspectors.Row(cells, 0, 9, 30).TrimEnd('\0').TrimEnd());

        using var wide = new DriverScope(120, 30);
        using var app2 = new InspectedApp();
        app2.StatusLine!.Update(); app2.Redraw();
        var wideStatus = StatusInspector.Inspect(app2, ScreenSnapshot.CopyRoot())!;
        Assert.Equal("match", wideStatus.HintObserved);
        Assert.All(wideStatus.Items.Where(i => i.Text is not null), i => Assert.Equal("match", i.Observed));
    }

    // ── assertions ───────────────────────────────────────────────────────────

    [Fact]
    public void AssertionsPassOnTruthFailOnWrongExpectationsAndAreUnsupportedWithoutEvidence()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new InspectedApp();
        AddDialog(app);
        app.StatusLine!.Update(); app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var inspection = Inspectors.Capture(app, cells, Protocol.EvidenceKinds.All);
        Assert.Empty(inspection.Errors);
        var id = ViewSelector.ById;
        AssertionResult One(AssertionSpec spec, CheckpointInspection? evidence = null) =>
            AssertionEvaluator.Evaluate([spec], cells, evidence ?? inspection, "scenario/x", "cp").Single();

        var truths = new[]
        {
            UiAssert.Exists("a1", id("dialog")), UiAssert.Absent("a2", ViewSelector.OfType("TMenuBox")), UiAssert.Visible("a3", id("ok")),
            UiAssert.Focused("a4", id("input")), UiAssert.Modal("a5", null), UiAssert.WithinScreen("a6", id("dialog")),
            UiAssert.Within("a7", id("dialog")), UiAssert.Within("a8", id("cancel"), id("dialog")), UiAssert.NotClipped("a9", id("dialog")),
            UiAssert.Aligned("a10", ViewSelector.OfType("TButton"), "top"), UiAssert.Spacing("a11", id("ok"), id("cancel"), "horizontal", 2, 2),
            UiAssert.Disjoint("a12", id("ok"), id("cancel")), UiAssert.Shadow("a13", id("dialog"), 0x08), UiAssert.MenuDepth("a14", 0),
            UiAssert.MenuItem("a15", "File", "enabled", 0), UiAssert.StatusItem("a16", "Alt+X Exit"), UiAssert.StatusItem("a17", "F3 Open", "enabled"),
            UiAssert.StatusItem("a18", "Close", "absent"), UiAssert.StatusContext("a19", 0), UiAssert.StatusHint("a20", "Ready for input"),
            UiAssert.TextAt("a21", 2, 0, "File"), UiAssert.ScreenContains("a22", "Inspect me"),
            UiAssert.AttributeAt("a23", 0, 24, cells[0, 24].Attr),
        };
        foreach (var spec in truths)
        {
            var r = One(spec);
            Assert.True(r.Outcome == AssertionOutcomes.Pass, $"{spec.Id} {spec.Kind}: {r.Expected} / {r.Actual} / {r.Explanation}");
            Assert.Equal(("scenario/x", "cp", "80x25"), (r.Scenario, r.Checkpoint, r.Grid));
        }
        var lies = new[]
        {
            UiAssert.Exists("b1", id("nope")), UiAssert.Absent("b2", id("dialog")), UiAssert.Focused("b3", id("ok")),
            UiAssert.Modal("b4", ViewSelector.OfType("TDialog")), UiAssert.Within("b5", id("dialog"), id("ok")),
            UiAssert.Aligned("b6", ViewSelector.OfType("TButton"), "left"), UiAssert.Spacing("b7", id("ok"), id("cancel"), "horizontal", 3, 9),
            UiAssert.Disjoint("b8", id("dialog"), id("ok")), UiAssert.Shadow("b9", id("dialog"), 0x70), UiAssert.MenuDepth("b10", 1),
            UiAssert.MenuItem("b11", "File", "disabled", 0), UiAssert.StatusItem("b12", "F10 Menu", "absent"), UiAssert.StatusContext("b13", 7),
            UiAssert.StatusHint("b14", "Busy"), UiAssert.TextAt("b15", 2, 0, "Edit"), UiAssert.ScreenContains("b16", "Not there"),
            UiAssert.AttributeAt("b17", 0, 24, cells[0, 24].Attr ^ 0x11), UiAssert.Focused("b18", ViewSelector.OfType("TButton")), // ambiguous
            UiAssert.TextAt("b19", 79, 0, "XY"),
        };
        foreach (var spec in lies)
        {
            var r = One(spec);
            Assert.True(r.Outcome == AssertionOutcomes.Fail, $"{spec.Id} {spec.Kind} should fail: {r.Outcome} {r.Actual} {r.Explanation}");
            Assert.False(string.IsNullOrEmpty(r.Expected)); Assert.False(string.IsNullOrEmpty(r.Explanation));
        }
        Assert.Contains("ambiguous", One(lies[^2]).Explanation);
        Assert.Contains("0x70", One(UiAssert.Shadow("c", id("dialog"), 0x70)).Expected);
        // Missing or failed evidence is never a guessed PASS.
        Assert.Equal(AssertionOutcomes.Unsupported, One(UiAssert.Focused("u1", id("input")), new CheckpointInspection(null, null, null, [])).Outcome);
        var failed = One(UiAssert.MenuDepth("u2", 0), new CheckpointInspection(null, null, null, ["menu: inspector failed (X: boom)"]));
        Assert.Equal(AssertionOutcomes.Unsupported, failed.Outcome); Assert.Contains("boom", failed.Explanation);
        Assert.Equal(AssertionOutcomes.Unsupported, One(UiAssert.Shadow("u3", id("dialog"), null)).Outcome);
        Assert.Equal(AssertionOutcomes.Unsupported, One(new AssertionSpec("u4", "no-such-kind")).Outcome);
        Assert.Equal(AssertionOutcomes.Pass, One(UiAssert.ScreenContains("u5", "File"), new CheckpointInspection(null, null, null, [])).Outcome);
        Assert.Equal(AssertionSeverity.Warning, One(UiAssert.MenuDepth("w", 3).AsWarning()).Severity);
    }

    [Fact]
    public void InspectorFailuresAreReportedNotHidden()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var withoutStatus = Inspectors.Capture(app, cells, [Protocol.EvidenceKinds.Status, "pixels"]);
        // TApplication has a default status line; a fabricated failure is an unknown evidence kind.
        Assert.Contains(withoutStatus.Errors, e => e.StartsWith("pixels:", StringComparison.Ordinal));
        app.StatusLine = null;
        var none = Inspectors.Capture(app, cells, [Protocol.EvidenceKinds.Status]);
        Assert.Null(none.Status);
        Assert.Contains(none.Errors, e => e.StartsWith("status: inspector failed", StringComparison.Ordinal));
    }

    [Fact]
    public void GeometryPrimitivesAreExact()
    {
        var a = new CellRect(0, 0, 10, 5); var b = new CellRect(12, 0, 4, 5); var c = new CellRect(5, 2, 10, 10);
        Assert.True(Geometry.Contains(a, new CellRect(1, 1, 9, 4))); Assert.False(Geometry.Contains(a, c));
        Assert.False(Geometry.Intersects(a, b)); Assert.True(Geometry.Intersects(a, c));
        Assert.Equal(new CellRect(5, 2, 5, 3), Geometry.Intersection(a, c));
        Assert.Equal(2, Geometry.Gap(a, b, "horizontal")); Assert.Equal(2, Geometry.Gap(b, a, "horizontal"));
        Assert.Equal(-3, Geometry.Gap(a, c, "vertical"));
        Assert.Equal(100 - 15, Geometry.ClippedCells(c, a));
        Assert.Equal(Geometry.Edge(new CellRect(0, 0, 3, 1), "center-x"), Geometry.Edge(new CellRect(1, 5, 1, 1), "center-x"));
        Assert.Null(Geometry.Edge(a, "middle"));
        Assert.Throws<ArgumentException>(() => Geometry.Gap(a, b, "diagonal"));
    }

    // ── grids and glyphs ─────────────────────────────────────────────────────

    [Fact]
    public void GridSizesAreParsedAndBounded()
    {
        Assert.Equal(new[] { "80x25", "140x40" }, ScenarioSelection.ParseSizes("standard"));
        Assert.Equal(new[] { "100x30", "60x20" }, ScenarioSelection.ParseSizes("100x30, 60x20,100x30"));
        Assert.Equal(new[] { "20x6", "200x100" }, ScenarioSelection.ParseSizes("20x6,200x100"));
        foreach (string bad in new[] { "", "19x25", "80x5", "201x25", "80x101", "080x25x1", "80X25", "-80x25", "1e2x25", "80x 25a", "9999x9999" })
            Assert.Throws<ArgumentException>(() => ScenarioSelection.ParseSizes(bad));
        Assert.Throws<ArgumentException>(() => ScenarioSelection.ParseSizes(string.Join(",", Enumerable.Range(30, 17).Select(w => w + "x20"))));
        var grid = new ScenarioGrid(60, 20, 120, 50, ["80x25"]);
        Assert.True(grid.Supports("60x20")); Assert.False(grid.Supports("59x20")); Assert.False(grid.Supports("121x50"));
        Assert.Equal("60x20..120x50", grid.Describe());
    }

    [Fact]
    public void LogicalPngDrawsStandardSymbolsDeterministicallyAndKeepsAnExplicitFallback()
    {
        static string Pattern(char ch) => string.Concat(Enumerable.Range(0, LogicalPng.CellHeight).SelectMany(y =>
            Enumerable.Range(0, LogicalPng.CellWidth).Select(x => LogicalPng.Glyph(ch, x, y) ? '#' : '.')));
        string fallback = Pattern('');
        Assert.Equal(fallback, Pattern('☃')); // any unknown symbol: the same visible replacement box
        var seen = new HashSet<string>();
        foreach (char symbol in LogicalPng.Symbols)
        {
            string pattern = Pattern(symbol);
            Assert.NotEqual(fallback, pattern);
            Assert.Contains('#', pattern);
            Assert.Equal(pattern, Pattern(symbol));
            if (symbol is not ('▶' or '◀' or '✓')) Assert.True(seen.Add(pattern), "Duplicate glyph for " + symbol);
        }
        Assert.Equal(Pattern('►'), Pattern('▶'));
        Assert.Contains(TSharpVisionGlyphs.MenuSubmenuArrow, LogicalPng.Symbols);
        Assert.Contains(TSharpVisionGlyphs.ScrollArrowLeft, LogicalPng.Symbols);
        Assert.Contains(TSharpVisionGlyphs.RadioChecked, LogicalPng.Symbols);
        Assert.Equal("diagnostics-original-bitmap-v2", LogicalPng.Renderer);
        // TVS1 cells are independent of the renderer; the PNG of the same cells is byte-identical across encodes.
        var snapshot = new ScreenSnapshot(3, 1, [new('►', 0x1f), new('A', 0x2e), new('●', 0x70)]);
        Assert.Equal(LogicalPng.Encode(snapshot), LogicalPng.Encode(ScreenSnapshot.Deserialize(snapshot.Serialize())));
    }

    // ── abandoned temp roots ─────────────────────────────────────────────────

    [Fact]
    public void OrphanSweepDeletesOnlyProvablyAbandonedToolRoots()
    {
        string temp = Path.Combine(Path.GetTempPath(), "tw-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            long start = self.StartTime.ToUniversalTime().Ticks;
            var old = DateTimeOffset.UtcNow.AddHours(-3);
            string Root(string name, TempRoots.Owner? owner, bool extra = false, DateTimeOffset? written = null)
            {
                string root = Path.Combine(temp, name);
                Directory.CreateDirectory(Path.Combine(root, "fixture")); Directory.CreateDirectory(Path.Combine(root, "state"));
                File.WriteAllText(Path.Combine(root, "state", "data.txt"), "x");
                if (owner is not null) File.WriteAllText(Path.Combine(root, TempRoots.Marker), JsonSerializer.Serialize(owner));
                if (extra) File.WriteAllText(Path.Combine(root, "user-notes.txt"), "keep me");
                Directory.SetLastWriteTimeUtc(root, (written ?? old).UtcDateTime);
                return root;
            }
            string Hex() => Guid.NewGuid().ToString("N");
            // PID reuse is detected by the recorded start time: same PID, different start = the owner is gone.
            string abandoned = Root(TempRoots.Prefix + Hex(), new(TempRoots.Tool, self.Id, start - 1, old));
            string running = Root(TempRoots.Prefix + Hex(), new(TempRoots.Tool, self.Id, start, old));
            string recent = Root(TempRoots.Prefix + Hex(), new(TempRoots.Tool, self.Id, start - 1, DateTimeOffset.UtcNow), written: DateTimeOffset.UtcNow);
            string unmarked = Root(TempRoots.Prefix + Hex(), null);
            string foreign = Root(TempRoots.Prefix + Hex(), new("someone-else", self.Id, start - 1, old));
            string unexpected = Root(TempRoots.Prefix + Hex(), new(TempRoots.Tool, self.Id, start - 1, old), extra: true);
            string badName = Root(TempRoots.Prefix + "not-a-guid", new(TempRoots.Tool, self.Id, start - 1, old));
            string json = JsonSerializer.Serialize(TempRoots.SweepOrphans(temp, TimeSpan.FromHours(1)));
            Assert.False(Directory.Exists(abandoned), json);
            foreach (string kept in new[] { running, recent, unmarked, foreign, unexpected, badName }) Assert.True(Directory.Exists(kept), kept + " " + json);
            Assert.Contains("\"deleted\":1", json); Assert.Contains("owner-running", json); Assert.Contains("no-marker", json);
            Assert.Contains("unexpected-content", json); Assert.Contains("foreign-marker", json); Assert.Contains("recent", json);
        }
        finally { Directory.Delete(temp, true); }
    }
}
