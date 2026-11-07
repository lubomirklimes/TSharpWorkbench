using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Accepted owner decision: the additive, read-only inspection properties of TSharpWorkbench.Testing
// (markup + markupTruncated for labels/static text, link for TLabel, maxLen/firstPos for TInputLine) on real in-process
// TSharpVision 1.2 controls. Missing or truncated evidence is a coverage gap or a sound failure, never a pass.
[Collection("NonParallel")]
public sealed class InspectionContractTests
{
    private const string DialogId = "d1-dialog";

    private static (ViewTree Tree, LayoutAuditReport Report) Capture(int width, int height, Action<TDialog> fill, InspectionLimits? limits = null,
        Func<ViewTree, ViewTree>? rewrite = null)
    {
        using var scope = new DriverScope((ushort)width, (ushort)height);
        using var app = new TApplication();
        var dialog = new TDialog(new TRect(0, 0, width, app.DeskTop!.size.y), "D-1");
        fill(dialog);
        ViewTreeInspector.Identify(dialog, DialogId);
        app.DeskTop.Insert(dialog);
        app.Redraw();
        var cells = ScreenSnapshot.CopyRoot();
        var tree = ViewTreeInspector.Inspect(app, cells, limits);
        if (rewrite is not null) tree = rewrite(tree);
        var (report, problem) = LayoutAudit.Run(new LayoutAuditRequest("strict", ViewSelector.ById(DialogId), null, null), cells, tree);
        Assert.Null(problem);
        return (tree, report!);
    }

    private static ViewNode Single(ViewTree tree, string kind) => AssertionEvaluator.All(tree.Root).Single(n => n.Kind == kind);
    private static ViewNode With(ViewNode node, Func<Dictionary<string, string>?, Dictionary<string, string>?> change) =>
        node with { Properties = change(node.Properties is null ? null : new(node.Properties, StringComparer.Ordinal)), Children = node.Children.Select(c => With(c, change)).ToArray() };

    [Fact]
    public void LabelStaticTextAndInputLineExposeTheAcceptedProperties()
    {
        TInputLine? control = null;
        var (tree, _) = Capture(80, 25, d =>
        {
            var input = control = new TInputLine(new TRect(14, 2, 34, 3), 40);
            d.Insert(input);
            input.SetData(new string('x', 30));   // longer than the 18 visible cells: scrolled
            d.Insert(new TLabel(new TRect(2, 2, 13, 3), "~F~ile name", input));
            d.Insert(new TStaticText(new TRect(2, 5, 40, 8), "First line\nSecond line, a little longer"));
        });
        var label = Single(tree, "TLabel");
        Assert.Equal("~F~ile name", label.Properties!["markup"]);
        Assert.False(label.Properties.ContainsKey("markupTruncated"));
        var input = Single(tree, "TInputLine");
        Assert.Equal(input.Path, label.Properties["link"]);
        Assert.Equal(control!.MaxLen.ToString(), input.Properties!["maxLen"]);
        Assert.NotEqual("0", input.Properties["firstPos"]);
        Assert.Equal(new string('x', 30), input.Text);               // Text keeps its pre-D-1 meaning (the data)
        var stat = Single(tree, "TStaticText");
        Assert.Equal("First line\nSecond line, a little longer", stat.Properties!["markup"]);   // multi-line source, unchanged
        Assert.False(stat.Properties.ContainsKey("markupTruncated"));
        // Properties only: no selector or protocol field was added for D-1.
        Assert.DoesNotContain(AssertionEvaluator.All(tree.Root), n => n.Properties is { } p && p.Keys.Any(k => k is "text" or "selector" or "id"));
    }

    [Fact]
    public void LongSourceIsCutAtTheInspectionLimitAndMarked()
    {
        string source = new string('a', 300);
        var (tree, _) = Capture(80, 25, d => d.Insert(new TStaticText(new TRect(2, 2, 78, 20), source)));
        var stat = Single(tree, "TStaticText");
        Assert.Equal("true", stat.Properties!["markupTruncated"]);
        Assert.Equal(InspectionLimits.Default.MaxText + 1, stat.Properties["markup"].Length);   // 256 units + the ellipsis: the cap is not lifted
        Assert.EndsWith("…", stat.Properties["markup"]);
        Assert.StartsWith(source[..InspectionLimits.Default.MaxText], stat.Properties["markup"]);
    }

    [Fact]
    public void TruncatedTextThatMayFitIsAGapNotAPass()
    {
        // 300 characters in a 76x18 static view: the 256-unit prefix fits, the full text's fit is unknown.
        var (_, report) = Capture(80, 25, d => d.Insert(new TStaticText(new TRect(2, 2, 78, 20), new string('a', 300))));
        Assert.DoesNotContain(report.Findings, f => f.Rule == LayoutRules.StaticClipped);
        Assert.Contains(report.Coverage.Unavailable, g => g.Rule == LayoutRules.StaticClipped);
    }

    [Fact]
    public void TruncatedTextWhosePrefixOverflowsIsASoundFailure()
    {
        var (_, report) = Capture(80, 25, d => d.Insert(new TStaticText(new TRect(2, 2, 40, 4), new string('a', 300))));
        Assert.Contains(report.Findings, f => f.Rule == LayoutRules.StaticClipped);
    }

    [Fact]
    public void TheInspectorEllipsisIsNotMeasuredAsText()
    {
        // At a 20-unit limit: 20 cells plus one unit that draws nothing ('~'). The label needs 21 columns and has them.
        // Measuring the inspector's "…" as a cell would claim 22 (a false failure); a prefix that fits is a gap.
        var limits = new InspectionLimits(MaxText: 20);
        string source = new string('a', limits.MaxText) + "~";
        var (_, report) = Capture(80, 25, d =>
        {
            var input = new TInputLine(new TRect(2, 4, 20, 5), 10); d.Insert(input);
            d.Insert(new TLabel(new TRect(2, 2, 2 + limits.MaxText + 1, 3), source, input));
        }, limits);
        Assert.DoesNotContain(report.Findings, f => f.Rule == LayoutRules.LabelClipped);
        Assert.Contains(report.Coverage.Unavailable, g => g.Rule == LayoutRules.LabelClipped);
    }

    [Fact]
    public void AnOlderTestingPayloadWithoutMarkupIsAGap()
    {
        // A target prepared before Smart Recording (no D-1 properties): label and static text rules cannot judge and say so.
        var (_, report) = Capture(80, 25, d =>
        {
            var input = new TInputLine(new TRect(14, 2, 34, 3), 40); d.Insert(input);
            d.Insert(new TLabel(new TRect(2, 2, 6, 3), "~F~ile name", input));     // would be clipped if judged
            d.Insert(new TStaticText(new TRect(2, 5, 12, 6), "Far too long for this view"));
        }, rewrite: t => t with { Root = With(t.Root, p => p is null ? null : StripD1(p)) });
        Assert.DoesNotContain(report.Findings, f => f.Rule is LayoutRules.LabelClipped or LayoutRules.StaticClipped);
        Assert.Contains(report.Coverage.Unavailable, g => g.Rule == LayoutRules.LabelClipped);
        Assert.Contains(report.Coverage.Unavailable, g => g.Rule == LayoutRules.StaticClipped);
    }

    private static Dictionary<string, string>? StripD1(Dictionary<string, string> p)
    {
        // Only the D-1 additions go; a button's caption markup predates and stays.
        if (!p.ContainsKey("command")) { p.Remove("markup"); p.Remove("markupTruncated"); }
        p.Remove("link"); p.Remove("maxLen"); p.Remove("firstPos");
        return p.Count == 0 ? null : p;
    }
}
