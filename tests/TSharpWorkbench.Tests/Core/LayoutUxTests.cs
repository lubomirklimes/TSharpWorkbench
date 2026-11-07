using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TSharpWorkbench.Tests.Core;

// UX heuristics (ux.*) of the composite layout-audit, run through the real engine (LayoutAudit.Run) on captures of
// real in-process TSharpVision views. Only ux.* findings are asserted (geometry/text rules of Agent A also run in the ux
// profile and are deliberately ignored here). Every layout is checked at 80x25, 120x36, 140x40 and 160x50.
[Collection("NonParallel")]
public sealed class LayoutUxTests(ITestOutputHelper output)
{
    public static readonly TheoryData<int, int> Sizes = new() { { 80, 25 }, { 120, 36 }, { 140, 40 }, { 160, 50 } };
    private const string DialogId = "audit-dialog";

    // ── capture + audit ─────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Audit(LayoutAuditReport Report, ViewTree Tree)
    {
        public LayoutFinding[] Ux => Report.Findings.Where(f => f.Rule.StartsWith("ux.", StringComparison.Ordinal)).ToArray();
        public LayoutFinding[] Of(string rule) => Report.Findings.Where(f => f.Rule == rule).ToArray();
        public string Path(Func<ViewNode, bool> match) => AssertionEvaluator.All(Tree.Root).Single(match).Path;
    }

    /// <summary>Builds the views, captures cells + view tree, runs the engine. <paramref name="build"/> returns the views to
    /// insert into the desktop (the first one is identified as the audit scope).</summary>
    private static Audit Run(int width, int height, Func<int, int, TView[]> build, string profile = "ux", LayoutSuppression[]? suppress = null,
        bool rootScope = false, Func<ViewTree, ViewTree>? rewrite = null, InspectionLimits? limits = null)
    {
        using var scope = new DriverScope((ushort)width, (ushort)height);
        using var app = new TApplication();
        int deskW = app.DeskTop!.size.x, deskH = app.DeskTop.size.y;
        currentDesk = app.DeskTop;
        var views = build(deskW, deskH);
        ViewTreeInspector.Identify(views[0], DialogId);
        foreach (var v in views) app.DeskTop.Insert(v);
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var tree = ViewTreeInspector.Inspect(app, cells, limits);
        if (rewrite is not null) tree = rewrite(tree);
        var (report, problem) = LayoutAudit.Run(new LayoutAuditRequest(profile, rootScope ? null : ViewSelector.ById(DialogId), null, suppress), cells, tree);
        Assert.Null(problem);
        return new(report!, tree);
    }

    /// <summary>A dialog of the given size centered on the desktop.</summary>
    private static TDialog Centered(int deskW, int deskH, int w, int h, string title)
    {
        int x = Math.Max(0, (deskW - w) / 2), y = Math.Max(0, (deskH - h) / 2);
        return new TDialog(new TRect(x, y, x + w, y + h), title);
    }

    private static TInputLine Input(TGroup d, int x, int y, int w, string? label = null, int labelX = -1, int labelY = -1, int labelW = 12)
    {
        var input = new TInputLine(new TRect(x, y, x + w, y + 1), 80);
        d.Insert(input);
        if (label is not null) d.Insert(new TLabel(new TRect(labelX, labelY, labelX + labelW, labelY + 1), label, input));
        return input;
    }

    private static TButton Button(TGroup d, int x, int y, int w, string title, ushort command) =>
        Insert(d, new TButton(new TRect(x, y, x + w, y + 2), title, command, command == Views.cmOK ? ButtonConstants.bfDefault : ButtonConstants.bfNormal));

    private static T Insert<T>(TGroup d, T view) where T : TView { d.Insert(view); return view; }

    private string Dump(Audit a) => string.Join("\n", a.Ux.Select(f => $"{f.Rule} [{f.Class}/{f.Confidence}] {f.Rect} {f.View} other={f.OtherPath}{f.OtherRect}: {f.Message} | {f.Evidence}"));

    private void NoUx(Audit a, string what)
    {
        output.WriteLine($"{what}: {a.Ux.Length} ux finding(s)\n{Dump(a)}");
        Assert.True(a.Ux.Length == 0, $"{what}: unexpected ux findings:\n{Dump(a)}");
        foreach (var rule in LayoutRules.All.Where(r => r.Family == LayoutFamilies.Ux))
            Assert.True(a.Report.Coverage.Evaluated.Contains(rule.Id) || a.Report.Coverage.NotApplicable.Contains(rule.Id), $"{rule.Id} did not report");
    }

    // ── corpus: well-formed layouts ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A typical hand-made form: left labels in a column, inputs in a column, a check box cluster, OK/Cancel below.</summary>
    private static TView[] TypicalForm(int dw, int dh)
    {
        var d = Centered(dw, dh, 50, 16, "Customer");
        Input(d, 15, 2, 30, "~N~ame", 2, 2);
        Input(d, 15, 4, 30, "~S~treet", 2, 4);
        Input(d, 15, 6, 30, "~C~ity", 2, 6);
        var options = Insert(d, new TCheckBoxes(new TRect(15, 8, 45, 10), new TSItem("~A~ctive", new TSItem("~V~IP", null))));
        d.Insert(new TLabel(new TRect(2, 8, 14, 9), "~O~ptions", options));
        Button(d, 13, 12, 12, "~O~K", Views.cmOK);
        Button(d, 27, 12, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>Labels above their inputs (the TSharpVision convention: label one column left, one row up).</summary>
    private static TView[] LabelsAboveForm(int dw, int dh)
    {
        var d = Centered(dw, dh, 44, 15, "Search");
        Input(d, 3, 2, 36, "~F~ind", 2, 1, 20);
        Input(d, 3, 5, 36, "~R~eplace with", 2, 4, 20);
        var where = Insert(d, new TRadioButtons(new TRect(3, 8, 39, 10), new TSItem("~W~hole file", new TSItem("~S~election", null))));
        d.Insert(new TLabel(new TRect(2, 7, 20, 8), "~S~cope", where));
        Button(d, 9, 11, 12, "~O~K", Views.cmOK);
        Button(d, 23, 11, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>One-cell variations everywhere: tolerated, never reported.</summary>
    private static TView[] NormalVariationForm(int dw, int dh)
    {
        var d = Centered(dw, dh, 52, 17, "Variation");
        Input(d, 16, 2, 30, "~N~ame", 2, 2, 12);
        Input(d, 17, 4, 29, "~E~-mail", 3, 4, 12);     // label and input both one column off
        Input(d, 16, 7, 30, "~P~hone", 2, 7, 12);      // gaps 1 and 2 rows
        Input(d, 16, 9, 30, "~F~ax", 2, 9, 12);
        Button(d, 8, 13, 10, "~O~K", Views.cmOK);
        Button(d, 20, 13, 11, "Cancel", Views.cmCancel); // widths 10/11/12, gaps 2/3
        Button(d, 34, 13, 12, "~H~elp", Views.cmHelp);
        return [d];
    }

    /// <summary>An empty list in a correctly sized dialog: blank list space is content, never unused space.</summary>
    private static TView[] EmptyListDialog(int dw, int dh)
    {
        var d = Centered(dw, dh, 50, 17, "Pick an item");
        var sb = Insert(d, new TScrollBar(new TRect(45, 3, 46, 13)));
        var list = Insert(d, new TListBox(new TRect(3, 3, 45, 13), 1, sb));
        list.NewList(new TStringCollection());
        d.Insert(new TLabel(new TRect(2, 2, 20, 3), "~I~tems", list));
        Button(d, 12, 14, 12, "~O~K", Views.cmOK);
        Button(d, 26, 14, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>Dynamic/scrollable content: an input scrolled through long data, a long list with a scroll bar, a memo with
    /// scroll bars and text beyond its view.</summary>
    private static TView[] DynamicDialog(int dw, int dh)
    {
        var d = Centered(dw, dh, 60, 20, "Dynamic");
        var path = Input(d, 3, 2, 40, "~P~ath", 2, 1, 20);
        path.SetData(string.Concat(Enumerable.Repeat("C:\\very\\long\\directory\\name", 3)));   // 78 chars in a 40-cell field
        var sb = Insert(d, new TScrollBar(new TRect(28, 5, 29, 15)));
        var list = Insert(d, new TListBox(new TRect(3, 5, 28, 15), 1, sb));
        var items = new TStringCollection();
        for (int i = 0; i < 200; i++) items.Insert($"Item number {i} with a rather long description that does not fit");
        list.NewList(items);
        d.Insert(new TLabel(new TRect(2, 4, 20, 5), "~I~tems", list));
        var vs = Insert(d, new TScrollBar(new TRect(56, 5, 57, 15)));
        var hs = Insert(d, new TScrollBar(new TRect(31, 15, 56, 16)));
        var memo = Insert(d, new TMemo(new TRect(31, 5, 56, 15), hs, vs, null, 4096));
        memo.InsertText(string.Join("\n", Enumerable.Range(0, 40).Select(i => $"Line {i}: " + new string('x', 60))));
        d.Insert(new TLabel(new TRect(30, 4, 46, 5), "~N~otes", memo));
        Button(d, 17, 17, 12, "~O~K", Views.cmOK);
        Button(d, 31, 17, 12, "Cancel", Views.cmCancel);
        path.Select();   // focused: SelectAll keeps it scrolled to the end of its data
        return [d];
    }

    private static TView[] FileDialog(int dw, int dh) =>
        [new TFileDialog("*.cs", "Open a file", "~N~ame", FileDialogOptions.fdOpenButton | FileDialogOptions.fdHelpButton | FileDialogOptions.fdNoLoadDir, 0)];

    private static TView[] FileDialogPlain(int dw, int dh) =>
        [new TFileDialog("*.*", "Save as", "~F~ile name", FileDialogOptions.fdOKButton | FileDialogOptions.fdNoLoadDir, 0)];

    private static TView[] ChDirDialog(int dw, int dh) => [new TChDirDialog(ChDirDialogOptions.cdNoLoadDir | ChDirDialogOptions.cdHelpButton, 0)];

    private static TView[] FindDialog(int dw, int dh) => [TEditorFindDialog.Build(new TFindDialogRec(Array.Empty<char>(), 0), out _, out _)];

    private static TView[] ReplaceDialog(int dw, int dh) => [TEditorReplaceDialog.Build(new TReplaceDialogRec(Array.Empty<char>(), Array.Empty<char>(), 0), out _, out _, out _)];

    private static TView[] MessageBoxDialog(int dw, int dh)
    {
        var r = new TRect(0, 0, 40, 9);
        r.Move((dw - 40) / 2, (dh - 9) / 2);
        return [MsgBox.BuildMessageBox(r, "Save changes to the scenario before closing?", MsgBox.mfConfirmation | MsgBox.mfYesNoCancel)];
    }

    private static TView[] GroupBoxDialog(int dw, int dh)
    {
        int x = (dw - 64) / 2, y = Math.Max(0, (dh - 20) / 2);
        return [GroupBoxCaptureFixture.CreateGroupBoxDemoDialog(new TRect(x, y, x + 64, y + 20))];
    }

    private static TView[] ColorDialog(int dw, int dh)
    {
        var palette = ((TProgram)currentDesk!.owner!).GetPalette().Clone();
        var groups = new TColorGroup("Desktop", new TColorItem("Background", 1))
            + new TColorGroup("Menu", new TColorItem("Normal", 2) + new TColorItem("Selected", 4))
            + new TColorGroup("Dialog", new TColorItem("Frame", 32) + new TColorItem("Title", 34));
        return [new TColorDialog(palette, groups)];
    }

    private static readonly (string Name, Func<int, int, TView[]> Build)[] GoodCorpus =
    [
        ("typical form", TypicalForm), ("labels above", LabelsAboveForm), ("normal variation", NormalVariationForm),
        ("empty list", EmptyListDialog), ("dynamic controls", DynamicDialog), ("TFileDialog open+help", FileDialog),
        ("TFileDialog OK", FileDialogPlain), ("TChDirDialog", ChDirDialog), ("Find dialog", FindDialog), ("Replace dialog", ReplaceDialog),
        ("MessageBox", MessageBoxDialog), ("group boxes", GroupBoxDialog), ("TColorDialog", ColorDialog),
    ];

    // ── corpus: layouts with one deliberate defect each ─────────────────────────────────────────────────────────────────

    /// <summary>Left labels: the third label starts two columns right of the others (x=4 vs 2; right edges 16 vs 14).</summary>
    private static TView[] BadLeftLabels(int dw, int dh)
    {
        var d = Centered(dw, dh, 52, 14, "Bad labels");
        Input(d, 16, 2, 30, "~N~ame", 2, 2, 12);
        Input(d, 16, 4, 30, "~S~treet", 2, 4, 12);
        var city = Input(d, 16, 6, 30);
        var label = Insert(d, new TLabel(new TRect(4, 6, 16, 7), "~C~ity", city));
        ViewTreeInspector.Identify(label, "city-label");
        Button(d, 14, 10, 12, "~O~K", Views.cmOK);
        Button(d, 28, 10, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>Labels above: the second label sits four columns right of its input's left edge.</summary>
    private static TView[] BadAboveLabels(int dw, int dh)
    {
        var d = Centered(dw, dh, 44, 13, "Bad above");
        Input(d, 3, 2, 36, "~F~ind", 2, 1, 20);
        Input(d, 3, 5, 36, "~R~eplace with", 6, 4, 20);
        Button(d, 9, 9, 12, "~O~K", Views.cmOK);
        Button(d, 23, 9, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>Four inputs at y=2,3,4,8: gaps 0,0,3.</summary>
    private static TView[] UnevenRows(int dw, int dh)
    {
        var d = Centered(dw, dh, 50, 15, "Uneven rows");
        Input(d, 15, 2, 30, "~F~irst", 2, 2);
        Input(d, 15, 3, 30, "~S~econd", 2, 3);
        Input(d, 15, 4, 30, "~T~hird", 2, 4);
        Input(d, 15, 8, 30, "F~o~urth", 2, 8);
        Button(d, 12, 11, 12, "~O~K", Views.cmOK);
        Button(d, 26, 11, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>OK/Cancel/Help where Cancel is one row lower than the others.</summary>
    private static TView[] MisalignedButtons(int dw, int dh)
    {
        var d = Centered(dw, dh, 50, 12, "Misaligned");
        Input(d, 15, 2, 30, "~N~ame", 2, 2);
        Input(d, 15, 4, 30, "~S~treet", 2, 4);
        Button(d, 6, 7, 12, "~O~K", Views.cmOK);
        Button(d, 20, 8, 12, "Cancel", Views.cmCancel);
        Button(d, 34, 7, 12, "~H~elp", Views.cmHelp);
        return [d];
    }

    /// <summary>Four buttons with gaps 2,2,5 and one button narrower than the rest although its caption fits.</summary>
    private static TView[] UnevenButtonRow(int dw, int dh)
    {
        var d = Centered(dw, dh, 64, 10, "Uneven buttons");
        Input(d, 15, 2, 40, "~N~ame", 2, 2);
        Button(d, 3, 6, 12, "~O~K", Views.cmOK);
        Button(d, 17, 6, 12, "Cancel", Views.cmCancel);
        Button(d, 31, 6, 7, "~A~pply", 100);
        Button(d, 43, 6, 12, "~H~elp", Views.cmHelp);
        return [d];
    }

    /// <summary>A near-full-screen dialog holding one input and two buttons in its top-left corner.</summary>
    private static TView[] OversizedDialog(int dw, int dh)
    {
        var d = new TDialog(new TRect(1, 1, dw - 1, dh - 1), "Too big");
        Input(d, 3, 2, 22, "~N~ame", 2, 1, 12);
        Button(d, 3, 4, 10, "~O~K", Views.cmOK);
        Button(d, 15, 4, 10, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>Content squeezed into the left part of a dialog that is not oversized as a whole.</summary>
    private static TView[] UnbalancedDialog(int dw, int dh)
    {
        var d = Centered(dw, dh, 52, 12, "Unbalanced");
        Input(d, 13, 2, 14, "~N~ame", 1, 2, 12);
        Input(d, 13, 4, 14, "~C~ode", 1, 4, 12);
        Button(d, 1, 8, 12, "~O~K", Views.cmOK);
        Button(d, 15, 8, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    /// <summary>Inputs at the top, buttons at the bottom, eleven empty rows between them.</summary>
    private static TView[] HollowDialog(int dw, int dh)
    {
        int h = Math.Min(21, dh);
        var d = Centered(dw, dh, 50, h, "Hollow");
        Input(d, 15, 2, 30, "~N~ame", 2, 2);
        Input(d, 15, 4, 30, "~S~treet", 2, 4);
        Button(d, 12, h - 4, 12, "~O~K", Views.cmOK);
        Button(d, 26, h - 4, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    private static TView[] RaggedColumn(int dw, int dh)
    {
        var d = Centered(dw, dh, 52, 14, "Ragged");
        Input(d, 12, 2, 30, "~N~ame", 2, 2, 8);
        Input(d, 12, 4, 30, "~S~treet", 2, 4, 8);
        Input(d, 15, 6, 27, "~P~ostal code", 2, 6, 13);
        Button(d, 14, 10, 12, "~O~K", Views.cmOK);
        Button(d, 28, 10, 12, "Cancel", Views.cmCancel);
        return [d];
    }

    private static readonly (string Name, Func<int, int, TView[]> Build, string Rule)[] BadCorpus =
    [
        ("bad left labels", BadLeftLabels, LayoutRules.LabelAlignment), ("bad above labels", BadAboveLabels, LayoutRules.LabelAlignment),
        ("uneven rows", UnevenRows, LayoutRules.RowSpacing), ("misaligned buttons", MisalignedButtons, LayoutRules.ButtonRow),
        ("uneven button row", UnevenButtonRow, LayoutRules.ButtonRow), ("oversized dialog", OversizedDialog, LayoutRules.Proportions),
        ("unbalanced dialog", UnbalancedDialog, LayoutRules.Balance), ("hollow dialog", HollowDialog, LayoutRules.UnusedSpace),
        ("ragged column", RaggedColumn, LayoutRules.ColumnAlignment),
    ];

    // ── well-formed: no ux finding ──────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Sizes))]
    public void WellFormedDialogsHaveNoUxFindings(int w, int h)
    {
        foreach (var (name, build) in GoodCorpus) NoUx(Run(w, h, build), $"{name} at {w}x{h}");
    }

    [Theory, MemberData(nameof(Sizes))]
    public void BlankListSpaceAloneIsNotUnusedSpace(int w, int h)
    {
        var a = Run(w, h, EmptyListDialog);
        var list = AssertionEvaluator.All(a.Tree.Root).Single(n => n.Kind == "TListBox");
        Assert.Equal("0", list.Properties!["range"]);            // the list really is empty
        NoUx(a, $"empty list at {w}x{h}");
        Assert.Contains(LayoutRules.UnusedSpace, a.Report.Coverage.Evaluated);   // the rule ran on this dialog and found nothing
    }

    [Theory, MemberData(nameof(Sizes))]
    public void DynamicAndScrollableControlsAreNotFlagged(int w, int h)
    {
        var a = Run(w, h, DynamicDialog);
        var input = AssertionEvaluator.All(a.Tree.Root).Single(n => n.Kind == "TInputLine");
        Assert.NotEqual("0", input.Properties!["firstPos"]);                   // scrolled through long data
        Assert.Equal("200", AssertionEvaluator.All(a.Tree.Root).Single(n => n.Kind == "TListBox").Properties!["range"]);
        NoUx(a, $"dynamic at {w}x{h}");
    }

    [Theory, MemberData(nameof(Sizes))]
    public void DocumentWindowIsNotJudgedLikeADialog(int w, int h)
    {
        var a = Run(w, h, (dw, dh) =>
        {
            var win = new TWindow(new TRect(0, 0, dw, dh), "Notes", 1) { };
            var vs = new TScrollBar(new TRect(dw - 1, 1, dw, dh - 1)); win.Insert(vs);
            win.Insert(new TMemo(new TRect(1, 1, dw / 3, 4), null, vs, null, 1024));   // small editor in a big window: user-sized
            return [win];
        });
        NoUx(a, $"document window at {w}x{h}");
        Assert.Contains(LayoutRules.Proportions, a.Report.Coverage.NotApplicable);
        Assert.Contains(LayoutRules.UnusedSpace, a.Report.Coverage.NotApplicable);
    }

    // ── defects: justified warnings ─────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Sizes))]
    public void InconsistentLabelAlignmentIsWarned(int w, int h)
    {
        var a = Run(w, h, BadLeftLabels);
        var f = Assert.Single(a.Of(LayoutRules.LabelAlignment));
        var label = AssertionEvaluator.All(a.Tree.Root).Single(n => n.Kind == "TLabel" && n.Properties!["markup"] == "~C~ity");
        Assert.Equal(label.Path, f.Path);
        Assert.Equal(label.Screen, f.Rect);
        Assert.Equal(LayoutFindingClasses.Warning, f.Class);
        Assert.Equal("medium", f.Confidence);
        int x0 = AssertionEvaluator.All(a.Tree.Root).Single(n => n.Id == DialogId).Screen.X;
        Assert.Contains($"x={x0 + 2}", f.Message);
        Assert.Contains("applies:", f.Evidence);
        Assert.Contains($"[{x0 + 2},{x0 + 2},{x0 + 4}]", f.Evidence);
        Assert.Equal(ViewSelector.ById("city-label"), f.Selector);
        Assert.NotNull(f.OtherPath);
        Assert.Single(a.Ux);   // nothing else is reported for this layout

        var above = Run(w, h, BadAboveLabels);
        var g = Assert.Single(above.Of(LayoutRules.LabelAlignment));
        Assert.Contains("Replace with", g.Message);
        Assert.Contains("offset 4", g.Evidence);
        Assert.Single(above.Ux);
    }

    [Theory, MemberData(nameof(Sizes))]
    public void UnevenRowSpacingIsWarned(int w, int h)
    {
        var a = Run(w, h, UnevenRows);
        var f = Assert.Single(a.Of(LayoutRules.RowSpacing));
        var fourth = AssertionEvaluator.All(a.Tree.Root).Where(n => n.Kind == "TInputLine").OrderBy(n => n.Screen.Y).Last();
        Assert.Equal(fourth.Path, f.Path);
        Assert.Equal(new CellRect(fourth.Screen.X - 13, fourth.Screen.Y - 3, 43, 3), f.Rect);   // the three empty rows above it
        Assert.Contains("gaps [0,0,3]", f.Evidence);
        Assert.Equal("medium", f.Confidence);
        Assert.Single(a.Ux);
    }

    [Theory, MemberData(nameof(Sizes))]
    public void MisalignedButtonGroupIsWarned(int w, int h)
    {
        var a = Run(w, h, MisalignedButtons);
        var f = Assert.Single(a.Of(LayoutRules.ButtonRow));
        var cancel = AssertionEvaluator.All(a.Tree.Root).Single(n => n.Kind == "TButton" && n.Text == "Cancel");
        Assert.Equal(cancel.Path, f.Path);
        Assert.Equal(cancel.Screen, f.Rect);
        Assert.Equal(new ViewSelector(Type: "TButton", Text: "Cancel"), f.Selector);
        Assert.Contains("1 row(s) lower", f.Message);
        Assert.Single(a.Ux);

        var uneven = Run(w, h, UnevenButtonRow);
        var findings = uneven.Of(LayoutRules.ButtonRow);
        output.WriteLine(Dump(uneven));
        Assert.Equal(2, findings.Length);
        Assert.Contains(findings, x => x.Message.Contains("gap between") && x.Evidence!.Contains("gaps [2,2,5]"));
        Assert.Contains(findings, x => x.Message.Contains("7 cells wide") && x.View.Contains("Apply"));
        Assert.Equal(2, uneven.Ux.Length);
    }

    [Theory, MemberData(nameof(Sizes))]
    public void GrosslyOddProportionsAreWarned(int w, int h)
    {
        var a = Run(w, h, OversizedDialog);
        var f = Assert.Single(a.Of(LayoutRules.Proportions));
        var dialog = AssertionEvaluator.All(a.Tree.Root).Single(n => n.Id == DialogId);
        Assert.Equal(dialog.Path, f.Path);
        Assert.Equal(dialog.Screen, f.Rect);
        Assert.Equal("high", f.Confidence);
        Assert.Contains("of the desktop", f.Evidence);
        Assert.Empty(a.Of(LayoutRules.Balance));            // the size is the problem, not reported twice as imbalance
        Assert.Single(a.Ux);

        var b = Run(w, h, UnbalancedDialog);
        var g = Assert.Single(b.Of(LayoutRules.Balance));
        Assert.Contains("pushed to the left", g.Message);
        Assert.Single(b.Ux);
    }

    [Theory, MemberData(nameof(Sizes))]
    public void HollowContentAndRaggedColumnsAreWarned(int w, int h)
    {
        var a = Run(w, h, HollowDialog);
        var f = Assert.Single(a.Of(LayoutRules.UnusedSpace));
        Assert.Contains("empty rows", f.Message);
        Assert.Single(a.Ux);

        var r = Run(w, h, RaggedColumn);
        var g = Assert.Single(r.Of(LayoutRules.ColumnAlignment));
        int x0 = AssertionEvaluator.All(r.Tree.Root).Single(n => n.Id == DialogId).Screen.X;
        Assert.Contains($"x={x0 + 15}", g.Message);
        Assert.Single(r.Ux);
    }

    // ── engine integration: suppression, scope, coverage, determinism ───────────────────────────────────────────────────

    private static TView[] TwoButtonGroups(int dw, int dh)
    {
        var d = Centered(dw, dh, 56, 12, "Two groups");
        Input(d, 15, 2, 26, "~N~ame", 2, 2);
        Button(d, 44, 2, 10, "~A~dd", 101);
        Button(d, 45, 4, 10, "~R~emove", 102);   // column: one cell right of Add
        Button(d, 10, 8, 12, "~O~K", Views.cmOK);
        Button(d, 24, 9, 12, "Cancel", Views.cmCancel);   // row: one row lower than OK
        return [d];
    }

    [Theory, MemberData(nameof(Sizes))]
    public void SuppressionByRuleAndBySelectorGoesThroughTheEngine(int w, int h)
    {
        var all = Run(w, h, TwoButtonGroups);
        Assert.Equal(2, all.Of(LayoutRules.ButtonRow).Length);

        var byRule = Run(w, h, TwoButtonGroups, suppress: [new LayoutSuppression(LayoutRules.ButtonRow, null, "Both groups are hand-placed by design.")]);
        Assert.Empty(byRule.Of(LayoutRules.ButtonRow));
        Assert.Equal(2, byRule.Report.Coverage.Suppressed.Count(s => s.Rule == LayoutRules.ButtonRow));

        var bySelector = Run(w, h, TwoButtonGroups, suppress: [new LayoutSuppression(LayoutRules.ButtonRow, new ViewSelector(Type: "TButton", Text: "Cancel"), "Cancel sits lower on purpose.")]);
        var left = Assert.Single(bySelector.Of(LayoutRules.ButtonRow));
        Assert.Contains("Remove", left.View);
        var suppressed = Assert.Single(bySelector.Report.Coverage.Suppressed);
        Assert.Equal("Cancel sits lower on purpose.", suppressed.Reason);

        // A label has no model text: it is suppressed through its diagnostic id.
        var labels = Run(w, h, BadLeftLabels, suppress: [new LayoutSuppression(LayoutRules.LabelAlignment, ViewSelector.ById("city-label"), "Indented on purpose.")]);
        Assert.Empty(labels.Ux);
        Assert.Single(labels.Report.Coverage.Suppressed);
        // A selector for another view suppresses nothing.
        var other = Run(w, h, BadLeftLabels, suppress: [new LayoutSuppression(LayoutRules.LabelAlignment, ViewSelector.ById("no-such-view"), "x")]);
        Assert.Single(other.Of(LayoutRules.LabelAlignment));
    }

    [Theory, MemberData(nameof(Sizes))]
    public void AuditIsDeterministic(int w, int h)
    {
        foreach (var (name, build, _) in BadCorpus.Append(("two groups", TwoButtonGroups, LayoutRules.ButtonRow)))
        {
            var first = Run(w, h, build).Report;
            var second = Run(w, h, build).Report;
            Assert.Equal(first.Findings, second.Findings);
            Assert.Equal(first.Coverage.Evaluated, second.Coverage.Evaluated);
            Assert.Equal(first.Coverage.NotApplicable, second.Coverage.NotApplicable);
        }
    }

    [Theory, MemberData(nameof(Sizes))]
    public void RootScopeJudgesEachWindowSeparately(int w, int h)
    {
        // Two overlapping, individually fine windows whose buttons are offset against each other.
        var a = Run(w, h, (dw, dh) =>
        {
            var one = new TDialog(new TRect(2, 1, 42, 10), "First");
            Input(one, 3, 2, 34, "~N~ame", 2, 1, 12);
            Button(one, 8, 5, 12, "~O~K", Views.cmOK); Button(one, 22, 5, 12, "Cancel", Views.cmCancel);
            var two = new TDialog(new TRect(20, 3, 60, 12), "Second");
            Input(two, 3, 2, 34, "~C~ity", 2, 1, 12);
            Button(two, 8, 5, 12, "~O~K", Views.cmOK); Button(two, 22, 5, 12, "Cancel", Views.cmCancel);
            return [one, two];
        }, rootScope: true);
        NoUx(a, $"stacked windows at {w}x{h}");
        Assert.Contains(LayoutRules.ButtonRow, a.Report.Coverage.Evaluated);
    }

    [Fact]
    public void TruncatedWindowIsAGapNeverAPass()
    {
        var a = Run(80, 25, BadLeftLabels, limits: new InspectionLimits(MaxDepth: 2));
        Assert.Empty(a.Ux);
        foreach (var rule in LayoutRules.All.Where(r => r.Family == LayoutFamilies.Ux))
            Assert.Contains(a.Report.Coverage.Unavailable, g => g.Rule == rule.Id && g.Reason.Contains("truncated"));
        Assert.False(a.Report.Coverage.Complete);
    }

    [Fact]
    public void GeometryProfileRunsNoUxRule()
    {
        var a = Run(80, 25, BadLeftLabels, profile: LayoutProfiles.Geometry);
        Assert.Empty(a.Ux);
        Assert.DoesNotContain(a.Report.Coverage.Evaluated, r => r.StartsWith("ux.", StringComparison.Ordinal));
    }

    [Fact]
    public void PreSr6PayloadFallsBackToAdjacencyWithLowConfidence()
    {
        // Simulate an older Testing payload: labels carry neither markup nor link.
        static ViewNode Strip(ViewNode n) => n with
        {
            Properties = n.Kind is "TLabel" or "TStaticText" ? null : n.Properties,
            Children = n.Children.Select(Strip).ToArray(),
        };
        var a = Run(80, 25, BadLeftLabels, rewrite: t => t with { Root = Strip(t.Root) });
        var f = Assert.Single(a.Of(LayoutRules.LabelAlignment));
        Assert.Equal("low", f.Confidence);
        Assert.Contains("adjacency", f.Evidence);
    }

    // ── real Workbench dialogs (product layouts not written for this test) ──────────────────────────────────────────────

    private static TView[] PropertiesDialog(TDeskTop desk) =>
        [new WorkbenchPropertiesDialog(desk, new WorkbenchProject(), "C:\\apps\\target.exe", "C:\\runs", null, 0)];

    private static TView[] ProjectInfoDialog(TDeskTop desk) =>
        [new WorkbenchProjectInfoDialog(desk, () => [("Project", "Demo"), ("Target", "C:\\apps\\target.exe"), ("Scenarios", "12"), ("Baselines", "C:\\runs\\baselines")])];

    private Audit RunDesk(int w, int h, Func<TDeskTop, TView[]> build) => Run(w, h, (_, _) => build(currentDesk!));
    [ThreadStatic] private static TDeskTop? currentDesk;

    [Theory, MemberData(nameof(Sizes))]
    public void RealWorkbenchDialogsHaveNoUxFindings(int w, int h)
    {
        foreach (var (name, build) in new (string, Func<TDeskTop, TView[]>)[] { ("Project Properties", PropertiesDialog), ("Project Information", ProjectInfoDialog) })
        {
            if (name == "Project Properties" && w < 120) continue;   // the Workbench supports 120x36 and larger grids for this dialog
            NoUx(RunDesk(w, h, build), $"{name} at {w}x{h}");
        }
    }

    // ── FP/FN counts over the corpus (reported in the hand-back) ────────────────────────────────────────────────────────

    [Fact]
    public void CorpusFalsePositiveAndFalseNegativeCounts()
    {
        int goodRuns = 0, fp = 0, badRuns = 0, fn = 0, extra = 0;
        foreach (var (w, h) in new[] { (80, 25), (120, 36), (140, 40), (160, 50) })
        {
            foreach (var (name, build) in GoodCorpus)
            {
                goodRuns++;
                var a = Run(w, h, build);
                fp += a.Ux.Length;
                if (a.Ux.Length > 0) output.WriteLine($"FP {name} {w}x{h}:\n{Dump(a)}");
            }
            foreach (var (name, build) in new (string, Func<TDeskTop, TView[]>)[] { ("Project Properties", PropertiesDialog), ("Project Information", ProjectInfoDialog) })
            {
                if (name == "Project Properties" && w < 120) continue;
                goodRuns++;
                var a = RunDesk(w, h, build);
                fp += a.Ux.Length;
                if (a.Ux.Length > 0) output.WriteLine($"FP {name} {w}x{h}:\n{Dump(a)}");
            }
            foreach (var (name, build, rule) in BadCorpus)
            {
                badRuns++;
                var a = Run(w, h, build);
                if (a.Of(rule).Length == 0) { fn++; output.WriteLine($"FN {name} {w}x{h} (expected {rule})"); }
                else if (w == 80) output.WriteLine($"FOUND {name} {w}x{h}: {Dump(a)}");
                extra += a.Ux.Count(f => f.Rule != rule);
                if (a.Ux.Any(f => f.Rule != rule)) output.WriteLine($"EXTRA {name} {w}x{h}:\n{Dump(a)}");
            }
        }
        output.WriteLine($"corpus: {goodRuns} well-formed runs, {fp} false-positive ux findings; {badRuns} defect runs, {fn} missed, {extra} additional findings");
        Assert.Equal(0, fp);
        Assert.Equal(0, fn);
        Assert.Equal(0, extra);
    }
}
