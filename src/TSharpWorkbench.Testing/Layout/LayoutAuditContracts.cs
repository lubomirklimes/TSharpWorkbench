using System.Text.Json.Serialization;

namespace TSharpWorkbench.Testing;

// Shared contracts. The composite "layout-audit" assertion audits the
// geometry, text fit and (optionally) UX heuristics of one captured state. Pure data: the engine (LayoutAudit.Run) evaluates
// the captured view tree and cells; nothing here touches a live view. Scenario documents stay schema version 1.

/// <summary>Audit profiles. <c>geometry</c>: hard geometry and text-fit rules; <c>ux</c>: geometry plus UX heuristics (warnings);
/// <c>strict</c>: the ux rules, and warning-class findings also fail the assertion.</summary>
public static class LayoutProfiles
{
    public const string Geometry = "geometry", Ux = "ux", Strict = "strict";
    public static readonly string[] All = [Geometry, Ux, Strict];
    /// <summary>Profile of a layout-audit assertion that names none.</summary>
    public const string Default = Geometry;
}

/// <summary>Class of one finding (separate from the assertion's severity). <c>error</c>: a hard, deterministic violation;
/// <c>warning</c>: a heuristic or lower-confidence observation; <c>info</c>: context only, never affects the outcome.</summary>
public static class LayoutFindingClasses
{
    public const string Error = "error", Warning = "warning", Info = "info";
    public static int Rank(string c) => c switch { Error => 0, Warning => 1, _ => 2 };
}

/// <summary>Rule families: <c>geometry</c> and <c>text</c> (every profile), <c>ux</c> (profiles ux and strict).</summary>
public static class LayoutFamilies { public const string Geometry = "geometry", Text = "text", Ux = "ux"; }

/// <param name="DefaultClass">The class a finding of this rule normally has (a rule may report a lower class for weaker evidence).</param>
public sealed record LayoutRuleInfo(string Id, string Family, string Title, string DefaultClass);

/// <summary>The rule registry. IDs are stable (documents, suppressions and results name them); unknown IDs are refused.</summary>
public static class LayoutRules
{
    public const string Degenerate = "geom.degenerate", Offscreen = "geom.offscreen", OutsideParent = "geom.outside-parent",
        OutsideDesktop = "geom.outside-desktop", FocusHidden = "geom.focus-hidden", FocusClipped = "geom.focus-clipped",
        Overlap = "geom.overlap", LabelCollision = "geom.label-collision",
        CaptionClipped = "text.caption-clipped", TitleClipped = "text.title-clipped", LabelClipped = "text.label-clipped",
        StaticClipped = "text.static-clipped", ClusterClipped = "text.cluster-clipped", CellsHeuristic = "text.cells-heuristic",
        LabelAlignment = "ux.label-alignment", ColumnAlignment = "ux.column-alignment", RowSpacing = "ux.row-spacing",
        ButtonRow = "ux.button-row", Proportions = "ux.proportions", UnusedSpace = "ux.unused-space", Balance = "ux.balance";

    public static readonly LayoutRuleInfo[] All =
    [
        new(Degenerate, LayoutFamilies.Geometry, "Invalid or degenerate geometry", LayoutFindingClasses.Error),
        new(Offscreen, LayoutFamilies.Geometry, "View outside the screen", LayoutFindingClasses.Error),
        new(OutsideParent, LayoutFamilies.Geometry, "Control outside its owner's working area", LayoutFindingClasses.Error),
        new(OutsideDesktop, LayoutFamilies.Geometry, "Window outside the desktop", LayoutFindingClasses.Error),
        new(FocusHidden, LayoutFamilies.Geometry, "Focused control hidden or fully clipped", LayoutFindingClasses.Error),
        new(FocusClipped, LayoutFamilies.Geometry, "Focused control partly clipped", LayoutFindingClasses.Error),
        new(Overlap, LayoutFamilies.Geometry, "Selectable sibling controls overlap", LayoutFindingClasses.Error),
        new(LabelCollision, LayoutFamilies.Geometry, "Label or text collides with an adjacent control", LayoutFindingClasses.Error),
        new(CaptionClipped, LayoutFamilies.Text, "Button caption does not fit", LayoutFindingClasses.Error),
        new(TitleClipped, LayoutFamilies.Text, "Window title does not fit its frame", LayoutFindingClasses.Error),
        new(LabelClipped, LayoutFamilies.Text, "Label text does not fit", LayoutFindingClasses.Error),
        new(StaticClipped, LayoutFamilies.Text, "Static text does not fit its rows", LayoutFindingClasses.Error),
        new(ClusterClipped, LayoutFamilies.Text, "Check box or radio button text does not fit", LayoutFindingClasses.Error),
        new(CellsHeuristic, LayoutFamilies.Text, "Text looks cut off in the screen cells (no source text)", LayoutFindingClasses.Warning),
        new(LabelAlignment, LayoutFamilies.Ux, "Labels not aligned with their controls", LayoutFindingClasses.Warning),
        new(ColumnAlignment, LayoutFamilies.Ux, "Related controls not aligned in a column", LayoutFindingClasses.Warning),
        new(RowSpacing, LayoutFamilies.Ux, "Uneven spacing between rows", LayoutFindingClasses.Warning),
        new(ButtonRow, LayoutFamilies.Ux, "Inconsistent action-button row", LayoutFindingClasses.Warning),
        new(Proportions, LayoutFamilies.Ux, "Dialog proportions do not fit its content or the screen", LayoutFindingClasses.Warning),
        new(UnusedSpace, LayoutFamilies.Ux, "Large unused content area", LayoutFindingClasses.Warning),
        new(Balance, LayoutFamilies.Ux, "Content grossly unbalanced in the dialog", LayoutFindingClasses.Warning),
    ];

    public static LayoutRuleInfo? Find(string id) => All.FirstOrDefault(r => r.Id == id);
    public static bool IsKnown(string id) => Find(id) is not null;

    /// <summary>Whether <paramref name="profile"/> runs the rule (geometry/text: every profile; ux: ux and strict).</summary>
    public static bool InProfile(string id, string profile) => Find(id) is { } r && (r.Family != LayoutFamilies.Ux || profile is LayoutProfiles.Ux or LayoutProfiles.Strict);

    /// <summary>The rules a request runs: the profile's rules, narrowed to <paramref name="only"/> when given.</summary>
    public static string[] Selected(string profile, IReadOnlyCollection<string>? only) =>
        All.Where(r => InProfile(r.Id, profile) && (only is null || only.Count == 0 || only.Contains(r.Id))).Select(r => r.Id).ToArray();
}

/// <summary>A justified suppression: findings of <see cref="Rule"/> (on views matching <see cref="View"/>, or every view when null)
/// are not counted; they are reported in the coverage with <see cref="Reason"/>.</summary>
public sealed record LayoutSuppression(string Rule, ViewSelector? View, string Reason);

/// <summary>What one layout-audit assertion asks for. <see cref="Scope"/>: the view whose subtree is audited (null = the
/// executing modal view when one executes, else the desktop's windows and the application's own views).</summary>
public sealed record LayoutAuditRequest(string Profile, ViewSelector? Scope, string[]? Rules, LayoutSuppression[]? Suppress)
{
    public static LayoutAuditRequest Of(AssertionSpec spec) => new(spec.Profile ?? LayoutProfiles.Default, spec.View, spec.Rules, spec.Suppress);
}

/// <summary>One finding. Coordinates are absolute screen cells. Deterministic: same capture, same request, same findings in the
/// same order (class, rule, path, y, x).</summary>
/// <param name="Path">Capture path of the view concerned.</param>
/// <param name="View">Readable description, e.g. <c>TButton "OK" (0/1/2/3)</c>.</param>
/// <param name="Selector">A stable selector for the view (id, else type + text) usable in a suppression; null when the view
/// has no stable identity (then only a rule-wide suppression covers it).</param>
/// <param name="Rect">The cells concerned (e.g. the clipped part, the overlap), absolute.</param>
/// <param name="OtherPath">Second view of a relation finding (overlap, collision, alignment reference), else null.</param>
/// <param name="Message">Actionable explanation: what is wrong and what to change.</param>
/// <param name="Evidence">The observed values the finding rests on (sizes, text lengths, gaps).</param>
public sealed record LayoutFinding(string Rule, string Class, string Confidence, string Path, string View, ViewSelector? Selector, CellRect Rect,
    string? OtherPath, CellRect? OtherRect, string Message, string? Evidence);

/// <summary>A rule (or a rule on one view) that could not be evaluated, and why (e.g. source text not exposed by the target's
/// Testing payload, tree truncated).</summary>
public sealed record LayoutRuleGap(string Rule, string Reason, string? Path);

public sealed record LayoutSuppressedFinding(string Rule, string Path, string Reason);

/// <param name="Scope">Path of the audited scope root.</param>
/// <param name="Evaluated">Rules that ran on at least one view.</param>
/// <param name="NotApplicable">Rules that ran but found nothing to examine (e.g. no buttons).</param>
/// <param name="Unavailable">Gaps: a rule entirely or for some views not evaluable on this evidence (capped; see Notes).</param>
/// <param name="ScopeNodes">Views in the scope subtree; <see cref="AuditedNodes"/> of them were visible and examined.</param>
/// <param name="Truncated">The captured tree was truncated inside the scope (<see cref="OmittedNodes"/> children not captured).</param>
public sealed record LayoutCoverage(string Profile, string? Scope, string[] Evaluated, string[] NotApplicable, LayoutRuleGap[] Unavailable,
    LayoutSuppressedFinding[] Suppressed, int ScopeNodes, int AuditedNodes, bool Truncated, int OmittedNodes, string[] Notes)
{
    /// <summary>Every selected rule was evaluated (or not applicable) for every view: nothing unavailable, nothing truncated.</summary>
    [JsonIgnore] public bool Complete => Unavailable.Length == 0 && !Truncated;
}

public sealed record LayoutAuditReport(LayoutFinding[] Findings, LayoutCoverage Coverage)
{
    [JsonIgnore] public int Errors => Findings.Count(f => f.Class == LayoutFindingClasses.Error);
    [JsonIgnore] public int Warnings => Findings.Count(f => f.Class == LayoutFindingClasses.Warning);
}
