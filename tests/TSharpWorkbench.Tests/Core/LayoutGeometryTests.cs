using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TSharpWorkbench.Tests.Core;

// Geometry (geom.*) and text-fit (text.*) rules of the layout-audit engine, on real in-process TSharpVision 1.2.0
// views (NullDriver + ViewTreeInspector, the same capture path the Proxy uses) and on synthetic trees for defects a real
// view cannot produce. Coordinates in the expectations are computed by hand from the view bounds: on an 80x25 TApplication
// the desktop starts at row 1, so a dialog inserted at TRect(10,3,..) has its origin at screen (10,4).
[Collection("NonParallel")]
public sealed class LayoutGeometryTests(ITestOutputHelper output)
{
    // ── harness ─────────────────────────────────────────────────────────────

    private static LayoutAuditReport Audit(TApplication app, ViewSelector? scope = null, string[]? rules = null, string profile = LayoutProfiles.Geometry)
    {
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var tree = ViewTreeInspector.Inspect(app, cells);
        return Run(cells, tree, MenuInspector.Inspect(app, cells), scope, rules, profile);
    }

    private static LayoutAuditReport Run(ScreenSnapshot cells, ViewTree tree, MenuState? menu = null, ViewSelector? scope = null, string[]? rules = null, string profile = LayoutProfiles.Geometry)
    {
        var (report, problem) = LayoutAudit.Run(new LayoutAuditRequest(profile, scope, rules, null), cells, tree, menu);
        Assert.Null(problem);
        Assert.NotNull(report);
        // Every geometry/text rule must have reported (evaluated, not applicable or an explicit gap) — never "did not run".
        Assert.DoesNotContain(report!.Coverage.Unavailable, g => g.Reason == "The rule did not run on this capture.");
        return report;
    }

    private static TDialog Dialog(TApplication app, TRect r, string title = "Dialog")
    {
        var d = new TDialog(r, title);
        app.DeskTop!.Insert(d);
        return d;
    }

    private static string Describe(LayoutAuditReport r) =>
        string.Join("\n", r.Findings.Select(f => $"{f.Class} {f.Rule} {f.Rect} {f.Path} {f.Message}"));

    private static LayoutFinding[] ErrorsOf(LayoutAuditReport r) => r.Findings.Where(f => f.Class == LayoutFindingClasses.Error).ToArray();

    private static void AssertErrors(LayoutAuditReport r, params (string Rule, CellRect Rect)[] expected)
    {
        var actual = ErrorsOf(r).Select(f => (f.Rule, f.Rect)).OrderBy(x => x.Rule).ThenBy(x => x.Rect.Y).ThenBy(x => x.Rect.X).ToArray();
        var want = expected.OrderBy(x => x.Rule).ThenBy(x => x.Rect.Y).ThenBy(x => x.Rect.X).ToArray();
        Assert.True(want.SequenceEqual(actual), "Expected:\n" + string.Join("\n", want) + "\nActual:\n" + Describe(r));
    }

    private void AssertClean(LayoutAuditReport r, string name)
    {
        output.WriteLine($"{name}: {r.Errors} error, {r.Warnings} warning; evaluated={r.Coverage.Evaluated.Length} na={r.Coverage.NotApplicable.Length} gaps={r.Coverage.Unavailable.Length}");
        foreach (var f in r.Findings) output.WriteLine($"  {f.Class} {f.Rule} {f.Rect} {f.Message}");
        foreach (var g in r.Coverage.Unavailable) output.WriteLine($"  gap {g.Rule} {g.Path}: {g.Reason}");
        Assert.True(r.Errors == 0, name + ":\n" + Describe(r));
        Assert.NotEmpty(r.Coverage.Evaluated);
    }

    // ── defects on real views (each must be found with exact rects, and nothing else of class error) ──────────────

    [Fact]
    public void OffscreenChildIsFoundOutsideScreenAndOutsideTheDialogInterior()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(40, 3, 79, 13));                         // screen (40,4 39x10), interior (41,5 37x8)
        var input = new TInputLine(new TRect(3, 2, 20, 3), 30); d.Insert(input);
        d.Insert(new TButton(new TRect(30, 5, 45, 7), "~O~K", Views.cmOK, ButtonConstants.bfDefault)); // screen (70,9 15x2)
        input.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.Offscreen, new CellRect(70, 9, 15, 2)), (LayoutRules.OutsideParent, new CellRect(70, 9, 15, 2)));
        var off = r.Findings.Single(f => f.Rule == LayoutRules.Offscreen);
        Assert.Contains("5 right of x=79", off.Message);
        Assert.Contains("outsideCells=10", off.Evidence);
    }

    [Fact]
    public void OverlappingSelectableControlsAreFoundWithTheIntersection()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));                         // origin (10,4)
        var a = new TInputLine(new TRect(3, 2, 30, 3), 40); d.Insert(a);       // (13,6 27x1)
        var b = new TInputLine(new TRect(20, 2, 38, 3), 40); d.Insert(b);      // (30,6 18x1)
        a.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.Overlap, new CellRect(30, 6, 10, 1)));
        var f = r.Findings.Single(x => x.Rule == LayoutRules.Overlap);
        Assert.NotNull(f.OtherPath);
        Assert.Contains("cells=10", f.Evidence);
    }

    [Fact]
    public void ClippedButtonCaptionIsFoundMnemonicAware()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));
        var ok = new TButton(new TRect(3, 6, 10, 8), "~C~ancel me", Views.cmCancel, ButtonConstants.bfNormal); d.Insert(ok); // (13,10 7x2)
        // "~O~K" in 10 columns and "Cancel" (6 cells) in exactly 9 columns fit (w <= size.x - 3).
        d.Insert(new TButton(new TRect(12, 6, 22, 8), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
        d.Insert(new TButton(new TRect(24, 6, 33, 8), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        ok.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.CaptionClipped, new CellRect(15, 10, 9, 1)));   // caption starts at x+2, 9 cells
        var f = r.Findings.Single(x => x.Rule == LayoutRules.CaptionClipped);
        Assert.Contains("needWidth=12", f.Evidence);
        Assert.Contains("at least 12 columns", f.Message);
    }

    [Fact]
    public void ButtonUnderTwoRowsHasNoCaptionRow()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));
        var b = new TButton(new TRect(3, 6, 15, 7), "~O~K", Views.cmOK, ButtonConstants.bfDefault); d.Insert(b);   // (13,10 12x1)
        b.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.CaptionClipped, new CellRect(13, 10, 12, 1)));
        Assert.Contains("never drawn", r.Findings.Single(x => x.Rule == LayoutRules.CaptionClipped).Message);
    }

    [Fact]
    public void ClippedLabelIsFoundAndAnExactFitIsNot()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));
        var input = new TInputLine(new TRect(10, 2, 30, 3), 40); d.Insert(input);
        d.Insert(new TLabel(new TRect(2, 2, 8, 3), "~F~ile name", input));                 // (12,6 6x1): 9 cells from x=13
        var other = new TInputLine(new TRect(10, 4, 30, 5), 40); d.Insert(other);
        d.Insert(new TLabel(new TRect(2, 4, 7, 5), "~N~ame", other));                        // 1 + 4 = 5: exact fit
        input.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.LabelClipped, new CellRect(13, 6, 9, 1)));
        Assert.Contains("at least 10 columns", r.Findings.Single(x => x.Rule == LayoutRules.LabelClipped).Message);
    }

    [Fact]
    public void OverflowingWindowTitleIsFoundByLengthAndByTheCapturedFrameRow()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        // 30 characters in a 30-column dialog: longer than the largest budget (30 - 10) — definite.
        Dialog(app, new TRect(10, 3, 40, 13), "A very long dialog title here!");             // frame row (10,4 30x1)
        // 25 characters in a 40-column window with number + close/zoom: budget 20 (decided by the frame row cells).
        var w = new TWindow(new TRect(42, 3, 78, 13), "Twenty-five character tit", 1);       // (42,4 36x1)
        app.DeskTop!.Insert(w);
        // 22 characters in a 40-column dialog: budget 24 (close icon) — the cells show it whole; no finding.
        var ok = Dialog(app, new TRect(10, 14, 50, 22), "Twenty-two characters!");
        ok.Insert(new TButton(new TRect(3, 3, 13, 5), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.TitleClipped, new CellRect(10, 4, 30, 1)), (LayoutRules.TitleClipped, new CellRect(42, 4, 36, 1)));
        var byCells = r.Findings.Single(f => f.Rule == LayoutRules.TitleClipped && f.Rect.X == 42);
        Assert.Contains("truncated", byCells.Message);
        Assert.Contains("budget 16", byCells.Message);
    }

    [Fact]
    public void PartlyClippedFocusedControlIsMalformedFocus()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));
        var input = new TInputLine(new TRect(3, 2, 60, 3), 80); d.Insert(input);           // (13,6 57x1), visible (13,6 37x1)
        input.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.FocusClipped, new CellRect(13, 6, 57, 1)), (LayoutRules.OutsideParent, new CellRect(13, 6, 57, 1)));
        Assert.Contains("visible=(13,6 37x1)", r.Findings.Single(f => f.Rule == LayoutRules.FocusClipped).Evidence);
    }

    [Fact]
    public void LabelCollidingWithItsLinkedValueIsFound()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));
        var input = new TInputLine(new TRect(10, 2, 30, 3), 40); d.Insert(input);          // (20,6 20x1)
        d.Insert(new TLabel(new TRect(2, 2, 15, 3), "~N~ame", input));                       // (12,6 13x1)
        input.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.LabelCollision, new CellRect(20, 6, 5, 1)));
        var f = r.Findings.Single(x => x.Rule == LayoutRules.LabelCollision);
        Assert.Contains("linked control", f.Message);
        Assert.Contains("linked=yes", f.Evidence);
    }

    [Fact]
    public void StaticTextNeedingMoreRowsAndClusterItemsBeyondTheirColumnAreFound()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));
        d.Insert(new TStaticText(new TRect(3, 2, 20, 3), "This text is far too long for one row"));   // (13,6 17x1)
        var boxes = new TCheckBoxes(new TRect(3, 4, 15, 6), new TSItem("~A~lpha option", new TSItem("Beta", null))); // (13,8 12x2)
        d.Insert(boxes);
        boxes.Select();
        var r = Audit(app);
        AssertErrors(r, (LayoutRules.StaticClipped, new CellRect(13, 6, 17, 1)), (LayoutRules.ClusterClipped, new CellRect(18, 8, 12, 1)));
        Assert.Contains("rowsNeeded=3", r.Findings.Single(f => f.Rule == LayoutRules.StaticClipped).Evidence);
    }

    [Fact]
    public void WindowOutsideTheDesktopIsFound()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(60, 3, 90, 13), "Off");                                  // (60,4 30x10)
        var r = Audit(app, rules: [LayoutRules.OutsideDesktop, LayoutRules.Offscreen]);
        AssertErrors(r, (LayoutRules.OutsideDesktop, new CellRect(60, 4, 30, 10)), (LayoutRules.Offscreen, new CellRect(60, 4, 30, 10)));
    }

    // ── port checks: the metrics agree with what TSharpVision 1.2.0 actually drew ──────────────────────────────

    [Fact]
    public void StaticTextAndClusterMetricsMatchTheDrawnCells()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(2, 2, 70, 20));                                        // origin (2,3)
        const string text = "\x03Centered heading\nWord wrapped body text that keeps going over several rows  with  gaps\n\nLast";
        d.Insert(new TStaticText(new TRect(2, 2, 30, 10), text));                           // (4,5 28x8)
        var radios = new TRadioButtons(new TRect(32, 2, 66, 4),
            new TSItem("~O~ne", new TSItem("Two two", new TSItem("~T~hree", new TSItem("Four", new TSItem("Five", null))))));
        d.Insert(radios);                                                                     // (34,5 34x2), 3 columns
        radios.Select();
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var rows = LayoutTextMetrics.StaticRows(text, 28);
        Assert.True(rows.Count <= 8);
        for (int y = 0; y < 8; y++)
        {
            string drawn = Inspectors.Row(cells, 4, 5 + y, 28);
            string expected = y < rows.Count ? new string(' ', rows[y].X) + rows[y].Text : "";
            Assert.Equal(expected.PadRight(28)[..28], drawn);
        }
        string[] items = ["One", "Two two", "Three", "Four", "Five"];
        var widths = items.Select(i => i.Length).ToArray();
        for (int k = 0; k < items.Length; k++)
        {
            int col = LayoutTextMetrics.ClusterColumn(widths, 2, k);
            Assert.Equal(items[k], Inspectors.Row(cells, 34 + col + 5, 5 + k % 2, items[k].Length));
        }
        var tree = ViewTreeInspector.Inspect(app, cells);
        AssertClean(Run(cells, tree), "metrics dialog");
    }

    // ── correct windows: zero error-class findings ──────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyListScrollingInputHistoryAndFrameScrollBarsAreClean()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(5, 2, 60, 20), "Clean form");
        var input = new TInputLine(new TRect(3, 2, 25, 3), 200); d.Insert(input);
        input.SetData(new string('x', 120) + " far longer than the field");                  // scrolls: not clipping
        d.Insert(new THistory(new TRect(25, 2, 28, 3), input, 7));                           // beside its input
        d.Insert(new TLabel(new TRect(2, 1, 12, 2), "~N~ame", input));
        var sb = new TScrollBar(new TRect(30, 5, 31, 15)); d.Insert(sb);
        var list = new TListBox(new TRect(3, 5, 30, 15), 1, sb); d.Insert(list);              // empty list
        d.Insert(new TLabel(new TRect(2, 4, 12, 5), "~I~tems", list));
        d.Insert(new TButton(new TRect(40, 2, 52, 4), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
        d.Insert(new TButton(new TRect(40, 4, 52, 6), "Cancel", Views.cmCancel, ButtonConstants.bfNormal));
        input.Select();
        var w = new TWindow(new TRect(30, 8, 78, 22), "Scrolled window", 2);                 // frame scroll bars
        w.StandardScrollBar(Views.sbVertical | Views.sbHandleKeyboard);
        w.StandardScrollBar(0);
        app.DeskTop!.Insert(w);
        app.DeskTop!.Insert(d);                                                               // back in front
        var r = Audit(app);
        AssertClean(r, "clean form + framed window");
    }

    [Fact]
    public void NestedStackedWindowsModalOverWindowsAndMenuOverlayAreClean()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var w1 = new TWindow(new TRect(0, 0, 50, 15), "First", 1); app.DeskTop!.Insert(w1);
        var w2 = new TWindow(new TRect(10, 4, 70, 20), "Second", 2); app.DeskTop!.Insert(w2);
        var inner = new TWindow(new TRect(5, 3, 30, 10), "Inner", 3); w2.Insert(inner);       // nested window inside a window
        var d = Dialog(app, new TRect(20, 6, 60, 16), "Modal over windows");
        var b = new TButton(new TRect(14, 6, 26, 8), "~O~K", Views.cmOK, ButtonConstants.bfDefault); d.Insert(b);
        b.Select();
        var menu = new TMenu(new TMenuItem("~O~pen", 100, Keys.kbNoKey, Views.hcNoContext, null, new TMenuItem("~S~ave all", 101, Keys.kbNoKey)));
        app.Insert(new TMenuBox(new TRect(0, 1, 30, 10), menu, null));                       // a drop-down over everything
        var r = Audit(app);
        AssertClean(r, "stacked/nested windows + modal + menu overlay");
    }

    private static void GroupBoxForm(TApplication app)
    {
        var d = Dialog(app, new TRect(5, 2, 70, 20), "Options");
        var box = new TGroupBox(new TRect(2, 1, 32, 8), "Style");                            // children inside its border
        var radios = new TRadioButtons(new TRect(1, 1, 29, 4), new TSItem("~P~lain", new TSItem("~B~old", new TSItem("~I~talic", null))));
        box.Insert(radios);
        box.Insert(new TStaticText(new TRect(1, 5, 29, 6), "Applies to new text."));
        d.Insert(box);
        var combo = new TComboBox(new TRect(36, 2, 60, 3), ["Small", "Medium", "Large"]);
        d.Insert(combo);
        d.Insert(new TLabel(new TRect(35, 1, 45, 2), "~S~ize", combo));
        var multi = new TMultiCheckBoxes(new TRect(36, 5, 60, 8), new TSItem("~A~", new TSItem("~B~", null)), 3, 0x0003, "-+*");
        d.Insert(multi);
        d.Insert(new TButton(new TRect(50, 14, 62, 16), "~O~K", Views.cmOK, ButtonConstants.bfDefault));
        radios.Select();
    }

    [Fact]
    public void GroupBoxComboBoxAndMultiCheckBoxesAreClean()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        GroupBoxForm(app);
        AssertClean(Audit(app), "group box + combo + multi check boxes");
    }

    public static TheoryData<ushort, ushort> Screens => new() { { 80, 25 }, { 120, 36 } };

    [Theory, MemberData(nameof(Screens))]
    public void StandardFileDialogsAreClean(ushort cols, ushort rows)
    {
        using var scope = new DriverScope(cols, rows);
        using var app = new TApplication();
        var open = new TFileDialog("*.txt", "Open a file", "~N~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdHelpButton | FileDialogOptions.fdNoLoadDir, 1);
        app.DeskTop!.Insert(open);
        AssertClean(Audit(app), $"TFileDialog open+help {cols}x{rows}");
        open.Hide(); app.DeskTop!.Remove(open);
        var save = new TFileDialog("*", "Save as", "~N~ame", FileDialogOptions.fdOKButton | FileDialogOptions.fdNoLoadDir, 2);
        app.DeskTop!.Insert(save);
        AssertClean(Audit(app), $"TFileDialog ok {cols}x{rows}");
    }

    [Theory, MemberData(nameof(Screens))]
    public void StandardDirectoryMessageAndInputDialogsAreClean(ushort cols, ushort rows)
    {
        using var scope = new DriverScope(cols, rows);
        using var app = new TApplication();
        var chdir = new TChDirDialog(ChDirDialogOptions.cdNoLoadDir | ChDirDialogOptions.cdHelpButton, 3);
        app.DeskTop!.Insert(chdir);
        AssertClean(Audit(app), $"TChDirDialog {cols}x{rows}");
        app.DeskTop!.Remove(chdir);
        var msg = MsgBox.BuildMessageBox(MsgBox.DefaultRect(app.DeskTop), "The file has been modified. Save the changes before closing?",
            MsgBox.mfConfirmation | MsgBox.mfYesNoCancel);
        app.DeskTop!.Insert(msg);
        AssertClean(Audit(app), $"MessageBox {cols}x{rows}");
        app.DeskTop!.Remove(msg);
        var input = MsgBox.BuildInputBox(new TRect(10, 5, 60, 12), "Rename", "~N~ew name", "draft.txt", 64);
        app.DeskTop!.Insert(input);
        AssertClean(Audit(app), $"InputBox {cols}x{rows}");
    }

    // ── evidence limits: older payloads, synthetic defects ───────────────────────────────────────────────────

    private static ViewNode StripMarkup(ViewNode n) => n with
    {
        Properties = n.Kind is "TLabel" or "TStaticText" or "TParamText" && n.Properties is { } p
            ? p.Where(kv => kv.Key is not ("markup" or "markupTruncated" or "link")).ToDictionary(kv => kv.Key, kv => kv.Value) is { Count: > 0 } left ? left : null
            : n.Properties,
        Children = n.Children.Select(StripMarkup).ToArray(),
    };

    [Fact]
    public void PreSr6PayloadWithoutLabelMarkupGivesGapsNotAPass()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15));
        var input = new TInputLine(new TRect(10, 2, 30, 3), 40); d.Insert(input);
        d.Insert(new TLabel(new TRect(2, 2, 8, 3), "~F~ilename", input));                    // clipped, but unknowable without markup (cells " Filen")
        d.Insert(new TStaticText(new TRect(3, 4, 30, 6), "Fits in two rows."));
        input.Select();
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var tree = ViewTreeInspector.Inspect(app, cells);
        var old = tree with { Root = StripMarkup(tree.Root) };
        var r = Run(cells, old);
        Assert.DoesNotContain(r.Findings, f => f.Rule is LayoutRules.LabelClipped or LayoutRules.StaticClipped);
        Assert.Equal(0, r.Errors);
        Assert.False(r.Coverage.Complete);
        Assert.Contains(r.Coverage.Unavailable, g => g.Rule == LayoutRules.LabelClipped && g.Path is not null);
        Assert.Contains(r.Coverage.Unavailable, g => g.Rule == LayoutRules.StaticClipped && g.Path is not null);
        // The cell-only suspicion is a low-confidence warning, never an error: the clipped label fills its last column.
        var h = Assert.Single(r.Findings, f => f.Rule == LayoutRules.CellsHeuristic);
        Assert.Equal((LayoutFindingClasses.Warning, "low"), (h.Class, h.Confidence));
        // The same capture with markup: the label is an exact error, coverage complete for the text rules.
        var full = Run(cells, tree);
        Assert.Contains(full.Findings, f => f.Rule == LayoutRules.LabelClipped && f.Class == LayoutFindingClasses.Error);
        Assert.DoesNotContain(full.Coverage.Unavailable, g => g.Rule is LayoutRules.LabelClipped or LayoutRules.StaticClipped);
        Assert.Contains(LayoutRules.CellsHeuristic, full.Coverage.NotApplicable);
    }

    // Synthetic tree builder: bounds are owner-relative; Screen/Visible are derived exactly as the inspector does.
    private sealed record S(string Kind, CellRect Bounds, string? Text = null, Dictionary<string, string>? Props = null,
        bool Visible = true, bool Selectable = false, S[]? Kids = null);

    private static ViewTree Synthetic(S root, int w, int h, CellRect? desk, string? modal, string? focused)
    {
        var screen = new CellRect(0, 0, w, h);
        int count = 0;
        ViewNode Make(S s, string path, CellRect origin, CellRect? clip, bool shown)
        {
            count++;
            var sr = new CellRect(origin.X + s.Bounds.X, origin.Y + s.Bounds.Y, s.Bounds.Width, s.Bounds.Height);
            bool vis = shown && s.Visible;
            CellRect? visible = vis ? Geometry.Intersection(Geometry.Intersection(sr, screen), clip ?? screen) : null;
            var kids = (s.Kids ?? []).Select((k, i) => Make(k, path + "/" + i, sr, clip is { } c ? Geometry.Intersection(c, sr) : sr, vis)).ToArray();
            return new ViewNode(path, null, "TSharpVision." + s.Kind, s.Kind, s.Bounds, sr, visible,
                new ViewFlags(s.Visible, s.Selectable, false, false, false, false, false, false, true, false, 0, 0), 0, s.Text, null, s.Props, kids, 0);
        }
        var r = Make(root, "0", new CellRect(0, 0, 0, 0), null, true);
        return new ViewTree(r, count, false, "front-to-back", screen, desk, modal, focused);
    }

    private static ScreenSnapshot Blank(int w, int h) => new(w, h, Enumerable.Range(0, w * h).Select(_ => new TScreenChar(' ', 7)).ToArray());

    private static S Frame(int w, int h) => new("TFrame", new(0, 0, w, h));

    [Fact]
    public void HiddenFocusedControlAndDegenerateGeometryAreFoundOnSyntheticTrees()
    {
        var tree = Synthetic(new S("TApplication", new(0, 0, 60, 20), Kids:
        [
            new S("TDeskTop", new(0, 1, 60, 18), Selectable: true, Kids:
            [
                new S("TDialog", new(5, 2, 40, 12), "Form", Selectable: true, Kids:
                [
                    Frame(40, 12),
                    new S("TInputLine", new(3, 2, 20, 1), "", new() { ["maxLen"] = "20", ["firstPos"] = "0" }, Visible: false, Selectable: true),
                    new S("TButton", new(3, 5, 0, 2), "OK", new() { ["markup"] = "~O~K" }, Selectable: true),
                ]),
            ]),
        ]), 60, 20, new CellRect(0, 1, 60, 18), "0/0/0", "0/0/0/1");
        var r = Run(Blank(60, 20), tree);
        AssertErrors(r, (LayoutRules.FocusHidden, new CellRect(8, 5, 20, 1)), (LayoutRules.Degenerate, new CellRect(8, 8, 0, 2)));
        Assert.Contains("hidden", r.Findings.Single(f => f.Rule == LayoutRules.FocusHidden).Message);
        Assert.Contains(LayoutRules.CaptionClipped, r.Coverage.NotApplicable);   // the only button is degenerate
    }

    [Fact]
    public void FocusInsideAHiddenOwnerAndAnInconsistentRectAreFound()
    {
        var tree = Synthetic(new S("TApplication", new(0, 0, 60, 20), Kids:
        [
            new S("TDeskTop", new(0, 1, 60, 18), Kids:
            [
                new S("TDialog", new(5, 2, 40, 12), "Form", Selectable: true, Kids:
                [
                    Frame(40, 12),
                    new S("TGroup", new(2, 2, 30, 5), Visible: false, Selectable: true, Kids:
                    [
                        new S("TInputLine", new(1, 1, 20, 1), "", Selectable: true),
                    ]),
                ]),
            ]),
        ]), 60, 20, new CellRect(0, 1, 60, 18), null, "0/0/0/1/0");
        // Corrupt one screen rect (an inconsistent payload): geom.degenerate reports it.
        var dialog = tree.Root.Children[0].Children[0];
        var broken = dialog with { Children = [dialog.Children[0] with { Screen = dialog.Children[0].Screen with { X = 1 } }, dialog.Children[1]] };
        var bad = tree with { Root = tree.Root with { Children = [tree.Root.Children[0] with { Children = [broken] }] } };
        var r = Run(Blank(60, 20), bad, rules: [LayoutRules.FocusHidden, LayoutRules.Degenerate]);
        Assert.Contains(r.Findings, f => f.Rule == LayoutRules.FocusHidden && f.Message.Contains("hidden owner"));
        Assert.Contains(r.Findings, f => f.Rule == LayoutRules.Degenerate && f.Path == "0/0/0/0" && f.Message.Contains("inconsistent"));
    }

    [Fact]
    public void TruncatedMarkupAndScrollingGroupsAreHandledHonestly()
    {
        string longText = new string('w', 256);
        var tree = Synthetic(new S("TApplication", new(0, 0, 80, 25), Kids:
        [
            new S("TDeskTop", new(0, 1, 80, 23), Kids:
            [
                new S("TDialog", new(2, 1, 76, 20), "Form", Selectable: true, Kids:
                [
                    Frame(76, 20),
                    // Cut at the limit but already too wide: a definite error.
                    new S("TLabel", new(2, 2, 30, 1), Props: new() { ["markup"] = longText, ["markupTruncated"] = "true" }),
                    // Cut at the limit and fits so far in 3 x 74... rows: unknowable -> gap.
                    new S("TStaticText", new(1, 4, 74, 10), Props: new() { ["markup"] = longText.Replace("wwww", "www "), ["markupTruncated"] = "true" }),
                    // A plain group that scrolls its content: children outside it are not judged.
                    new S("TGroup", new(2, 15, 30, 3), Selectable: true, Kids:
                    [
                        new S("TScrollBar", new(29, 0, 1, 3)),
                        new S("TInputLine", new(0, 5, 20, 1), "", Selectable: true),
                    ]),
                ]),
            ]),
        ]), 80, 25, new CellRect(0, 1, 80, 23), "0/0/0", null);
        var r = Run(Blank(80, 25), tree);
        Assert.Contains(r.Findings, f => f.Rule == LayoutRules.LabelClipped && f.Class == LayoutFindingClasses.Error && f.Message.Contains("256+"));
        Assert.Contains(r.Coverage.Unavailable, g => g.Rule == LayoutRules.StaticClipped && g.Path == "0/0/0/2");
        Assert.DoesNotContain(r.Findings, f => f.Rule == LayoutRules.OutsideParent);
    }

    [Fact]
    public void TitleWhoseBudgetIsUnknownAndUnobservedIsAGap()
    {
        // 25 characters in a 40-column frame: fits with budget 30/26/24... only the cells could tell — and they are blank.
        var tree = Synthetic(new S("TApplication", new(0, 0, 60, 20), Kids:
        [
            new S("TDeskTop", new(0, 1, 60, 18), Kids: [ new S("TWindow", new(5, 2, 40, 12), "Twenty-five character tit", Selectable: true, Kids: [Frame(40, 12)]) ]),
        ]), 60, 20, new CellRect(0, 1, 60, 18), null, null);
        var r = Run(Blank(60, 20), tree);
        Assert.DoesNotContain(r.Findings, f => f.Rule == LayoutRules.TitleClipped);
        Assert.Contains(r.Coverage.Unavailable, g => g.Rule == LayoutRules.TitleClipped && g.Path == "0/0/0");
    }

    // TSharpVision 1.2 places encoding controls below the last action button, including Help.
    // Both configurations must be clean; synthetic overlap/collision cases above still test detection.
    [Fact]
    public void FileDialogEncodingSelectorIsCleanWithAndWithoutTheHelpButton()
    {
        using (var scope = new DriverScope(80, 25))
        using (var app = new TApplication())
        {
            app.DeskTop!.Insert(new TFileDialog("*", "Open", "~N~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdEncodingSelector | FileDialogOptions.fdNoLoadDir, 1));
            AssertClean(Audit(app), "TFileDialog open+encoding 80x25");
        }
        using (var scope = new DriverScope(80, 25))
        using (var app = new TApplication())
        {
            app.DeskTop!.Insert(new TFileDialog("*", "Open", "~N~ame",
                FileDialogOptions.fdOpenButton | FileDialogOptions.fdHelpButton | FileDialogOptions.fdEncodingSelector | FileDialogOptions.fdNoLoadDir, 1));
            AssertClean(Audit(app), "TFileDialog open+help+encoding 80x25");
        }
    }

    [Fact]
    public void DisabledRulesAreNotReportedAndTheAuditIsDeterministic()
    {
        using var scope = new DriverScope(80, 25);
        using var app = new TApplication();
        var d = Dialog(app, new TRect(10, 3, 50, 15), "A very long dialog title that cannot fit at all");
        var a = new TInputLine(new TRect(3, 2, 30, 3), 40); d.Insert(a);
        d.Insert(new TInputLine(new TRect(20, 2, 38, 3), 40));
        d.Insert(new TLabel(new TRect(2, 4, 6, 5), "~L~ong label", a));
        a.Select();
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var tree = ViewTreeInspector.Inspect(app, cells);
        var only = Run(cells, tree, rules: [LayoutRules.Overlap]);
        Assert.All(only.Findings, f => Assert.Equal(LayoutRules.Overlap, f.Rule));
        Assert.Equal([LayoutRules.Overlap], only.Coverage.Evaluated);
        string first = JsonSerializer.Serialize(Run(cells, tree)), second = JsonSerializer.Serialize(Run(cells, ViewTreeInspector.Inspect(app, cells)));
        Assert.Equal(first, second);
    }

    // ── corpus: real FP/FN counts over every case above (re-built here so the numbers are measured, not claimed) ───

    [Fact]
    public void CorpusFalsePositiveAndFalseNegativeCounts()
    {
        int fn = 0, fp = 0, defects = 0, clean = 0;
        void Defect(string name, Func<TApplication, string[]> build, ushort c = 80, ushort h = 25)
        {
            using var scope = new DriverScope(c, h);
            using var app = new TApplication();
            var expected = build(app);
            var r = Audit(app);
            var found = ErrorsOf(r).Select(f => f.Rule).ToHashSet();
            int missed = expected.Count(e => !found.Contains(e)), extra = found.Count(f => !expected.Contains(f));
            fn += missed; fp += extra; defects += expected.Length;
            output.WriteLine($"defect {name}: expected [{string.Join(",", expected)}] found [{string.Join(",", found)}] FN={missed} FP={extra}");
        }
        void Clean(string name, Action<TApplication> build, ushort c = 80, ushort h = 25)
        {
            using var scope = new DriverScope(c, h);
            using var app = new TApplication();
            build(app);
            var r = Audit(app);
            fp += r.Errors; clean++;
            output.WriteLine($"clean {name} {c}x{h}: errors={r.Errors} warnings={r.Warnings} gaps={r.Coverage.Unavailable.Length}");
        }
        Defect("offscreen child", app => { var d = Dialog(app, new TRect(40, 3, 79, 13)); var i = new TInputLine(new TRect(3, 2, 20, 3), 9); d.Insert(i); d.Insert(new TButton(new TRect(30, 5, 45, 7), "~O~K", Views.cmOK, 0)); i.Select(); return [LayoutRules.Offscreen, LayoutRules.OutsideParent]; });
        Defect("overlap", app => { var d = Dialog(app, new TRect(10, 3, 50, 15)); var a = new TInputLine(new TRect(3, 2, 30, 3), 9); d.Insert(a); d.Insert(new TInputLine(new TRect(20, 2, 38, 3), 9)); a.Select(); return [LayoutRules.Overlap]; });
        Defect("caption", app => { var d = Dialog(app, new TRect(10, 3, 50, 15)); var b = new TButton(new TRect(3, 6, 10, 8), "~C~ancel me", Views.cmCancel, 0); d.Insert(b); b.Select(); return [LayoutRules.CaptionClipped]; });
        Defect("label", app => { var d = Dialog(app, new TRect(10, 3, 50, 15)); var i = new TInputLine(new TRect(10, 2, 30, 3), 9); d.Insert(i); d.Insert(new TLabel(new TRect(2, 2, 8, 3), "~F~ile name", i)); i.Select(); return [LayoutRules.LabelClipped]; });
        Defect("title", app => { Dialog(app, new TRect(10, 3, 40, 13), "A very long dialog title here!"); return [LayoutRules.TitleClipped]; });
        Defect("title by cells", app => { app.DeskTop!.Insert(new TWindow(new TRect(42, 3, 78, 13), "Twenty-five character tit", 1)); return [LayoutRules.TitleClipped]; });
        Defect("focus clipped", app => { var d = Dialog(app, new TRect(10, 3, 50, 15)); var i = new TInputLine(new TRect(3, 2, 60, 3), 9); d.Insert(i); i.Select(); return [LayoutRules.FocusClipped, LayoutRules.OutsideParent]; });
        Defect("collision", app => { var d = Dialog(app, new TRect(10, 3, 50, 15)); var i = new TInputLine(new TRect(10, 2, 30, 3), 9); d.Insert(i); d.Insert(new TLabel(new TRect(2, 2, 15, 3), "~N~ame", i)); i.Select(); return [LayoutRules.LabelCollision]; });
        Defect("static", app => { var d = Dialog(app, new TRect(10, 3, 50, 15)); d.Insert(new TStaticText(new TRect(3, 2, 20, 3), "This text is far too long for one row")); return [LayoutRules.StaticClipped]; });
        Defect("cluster", app => { var d = Dialog(app, new TRect(10, 3, 50, 15)); var c = new TCheckBoxes(new TRect(3, 4, 15, 6), new TSItem("~A~lpha option", new TSItem("Beta", null))); d.Insert(c); c.Select(); return [LayoutRules.ClusterClipped]; });
        Defect("outside desktop", app => { Dialog(app, new TRect(60, 3, 90, 13), "Off"); return [LayoutRules.OutsideDesktop, LayoutRules.Offscreen]; });
        foreach (var (c, h) in new (ushort, ushort)[] { (80, 25), (120, 36) })
        {
            Clean("file open", app => app.DeskTop!.Insert(new TFileDialog("*", "Open", "~N~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdHelpButton | FileDialogOptions.fdNoLoadDir, 1)), c, h);
            Clean("file ok", app => app.DeskTop!.Insert(new TFileDialog("*", "Save", "~N~ame", FileDialogOptions.fdOKButton | FileDialogOptions.fdNoLoadDir, 1)), c, h);
            Clean("chdir", app => app.DeskTop!.Insert(new TChDirDialog(ChDirDialogOptions.cdNoLoadDir | ChDirDialogOptions.cdHelpButton, 1)), c, h);
            Clean("message", app => app.DeskTop!.Insert(MsgBox.BuildMessageBox(MsgBox.DefaultRect(app.DeskTop), "Save changes?", MsgBox.mfConfirmation | MsgBox.mfYesNoCancel)), c, h);
            Clean("input box", app => app.DeskTop!.Insert(MsgBox.BuildInputBox(new TRect(10, 5, 60, 12), "Rename", "~N~ew name", "a.txt", 64)), c, h);
            Clean("file encoding", app => app.DeskTop!.Insert(new TFileDialog("*", "Open", "~N~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdEncodingSelector | FileDialogOptions.fdNoLoadDir, 1)), c, h);
            Clean("file help+encoding", app => app.DeskTop!.Insert(new TFileDialog("*", "Open", "~N~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdHelpButton | FileDialogOptions.fdEncodingSelector | FileDialogOptions.fdNoLoadDir, 1)), c, h);
            Clean("group box form", GroupBoxForm, c, h);
            Clean("empty list + scrolling input + history", app =>
            {
                var d = Dialog(app, new TRect(5, 2, 60, 20), "Form");
                var i = new TInputLine(new TRect(3, 2, 25, 3), 200); d.Insert(i); i.SetData(new string('x', 150));
                d.Insert(new THistory(new TRect(25, 2, 28, 3), i, 7));
                var sb = new TScrollBar(new TRect(30, 5, 31, 15)); d.Insert(sb); d.Insert(new TListBox(new TRect(3, 5, 30, 15), 1, sb));
                i.Select();
            }, c, h);
            Clean("menu overlay", app =>
            {
                var d = Dialog(app, new TRect(10, 3, 50, 15), "Behind a menu");
                var b = new TButton(new TRect(3, 3, 13, 5), "~O~K", Views.cmOK, ButtonConstants.bfDefault); d.Insert(b); b.Select();
                app.Insert(new TMenuBox(new TRect(5, 1, 40, 12), new TMenu(new TMenuItem("~O~pen", 100, Keys.kbNoKey, Views.hcNoContext, null, new TMenuItem("~C~lose", 101, Keys.kbNoKey))), null));
            }, c, h);
            Clean("stacked windows", app => { app.DeskTop!.Insert(new TWindow(new TRect(0, 0, 50, 15), "First", 1)); var w = new TWindow(new TRect(10, 4, 70, 20), "Second", 2); w.StandardScrollBar(Views.sbVertical); w.StandardScrollBar(0); app.DeskTop!.Insert(w); Dialog(app, new TRect(20, 6, 60, 16), "Modal"); }, c, h);
        }
        output.WriteLine($"corpus: {defects} expected defect finding(s) in 12 defect states, {clean} clean states; FN={fn} FP={fp}");
        Assert.Equal(0, fn);
        Assert.Equal(0, fp);
    }
}
