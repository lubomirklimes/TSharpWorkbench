using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSharpWorkbench.Testing;

/// <summary>A search rectangle of a normalization rule. Negative <see cref="X"/>/<see cref="Y"/> count from the far edge
/// (-1 = last column/row); <see cref="Width"/>/<see cref="Height"/> of 0 or less extend to the far edge minus that many cells.
/// <see cref="Grids"/> (checkpoint grids, <c>WxH</c>) limits the region to those grids; null = every grid.</summary>
public sealed record VisualRegion(int X, int Y, int Width, int Height, string[]? Grids = null)
{
    internal bool AppliesTo(string grid) => Grids is null || Grids.Contains(grid, StringComparer.Ordinal);

    internal (int X0, int Y0, int X1, int Y1)? Resolve(int width, int height)
    {
        int x0 = X < 0 ? width + X : X, y0 = Y < 0 ? height + Y : Y;
        int x1 = Width > 0 ? x0 + Width : width + Width, y1 = Height > 0 ? y0 + Height : height + Height; // exclusive
        return x0 < 0 || y0 < 0 || x1 > width || y1 > height || x1 <= x0 || y1 <= y0 ? null : (x0, y0, x1, y1);
    }

    public override string ToString() => $"({X},{Y} {Width}x{Height}{(Grids is null ? "" : " @" + string.Join('|', Grids))})";
}

public static class NormalizationKinds
{
    /// <summary>Masks the cells of the regex group <c>id</c>, each of which must be a tail (suffix, possibly the whole) of THIS
    /// execution's controller-assigned fixture identifier (the 32 hex digits of <c>tsharp-diagnostics-&lt;id&gt;</c>): a displayed
    /// path is left-truncated, so a visible fragment always ends where the identifier ends. Any other capture is a
    /// normalization error, never a mask.</summary>
    public const string RunFixtureId = "run-fixture-id";
    /// <summary>Masks the cells of the regex group <c>dynamic</c>; the regex itself is the semantic validation.</summary>
    public const string Pattern = "pattern";
    public static readonly string[] All = [RunFixtureId, Pattern];
}

/// <summary>One explicit, documented dynamic-content rule. Within its regions (each region row is matched as its own string
/// of cells, so regex indices are cell columns), every match's group cells have their GLYPH replaced by
/// <see cref="VisualNormalizer.Placeholder"/>; attributes are never masked and every other cell stays exact. The number of
/// matches must lie in <see cref="MinMatches"/>..<see cref="MaxMatches"/> and at most <see cref="MaxCells"/> cells may be
/// masked, otherwise the rule fails (a missing dynamic field is itself a difference worth reporting).</summary>
public sealed record VisualNormalizationRule(string Id, string Kind, string Description, string Pattern, VisualRegion[] Regions,
    int MinMatches = 1, int MaxMatches = 1, int MaxCells = 64)
{
    internal string Group => Kind == NormalizationKinds.RunFixtureId ? "id" : "dynamic";

    /// <summary>Stable digest of the rule's semantics; recorded in a baseline so a later rule change requires re-approval.</summary>
    internal string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this with { Description = "" })));
}

/// <summary>Cells one rule masked in one screen: spans <c>[x, y, length]</c>. <see cref="Values"/> (the masked text) is kept for
/// the run's own comparison report only and never written into a baseline.</summary>
public sealed record AppliedNormalization(string Rule, string Kind, string Description, int[][] Spans, string[]? Values = null)
{
    public int Cells => Spans.Sum(s => s[2]);
}

public sealed record NormalizationResult(ScreenSnapshot? Canonical, AppliedNormalization[] Applied, string? Error)
{
    public int MaskedCells => Applied.Sum(a => a.Cells);
}

/// <summary>Context of one capture. <see cref="FixtureId"/> is the controller's per-execution fixture identifier.</summary>
public sealed record NormalizationContext(string? FixtureId)
{
    private static readonly Regex Root = new("^" + Regex.Escape(TempRoots.Prefix) + "([0-9a-f]{32})$", RegexOptions.CultureInvariant);

    /// <summary>The identifier from a fixture directory (<c>…/tsharp-diagnostics-&lt;id&gt;/fixture</c>) or its root.</summary>
    public static NormalizationContext FromFixture(string? fixture)
    {
        if (fixture is null) return new((string?)null);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture));
        foreach (string? candidate in new[] { Path.GetFileName(full), Path.GetFileName(Path.GetDirectoryName(full)) })
            if (candidate is not null && Root.Match(candidate) is { Success: true } m) return new(m.Groups[1].Value);
        return new((string?)null);
    }
}

/// <summary>Applies normalization rules to a captured screen, producing the canonical screen that baselines store and
/// comparisons use. Never masks attributes; never masks outside declared regions; refuses oversized rules.</summary>
public static class VisualNormalizer
{
    /// <summary>Canonical glyph of a masked cell (U+FFFD REPLACEMENT CHARACTER; the logical PNG draws it as a box).</summary>
    public const char Placeholder = '�';
    /// <summary>Safety bounds: the regions of all rules of one checkpoint cover at most this share of the screen, and at
    /// most <see cref="MaximumMaskedShare"/> of the screen's cells are masked.</summary>
    public const double MaximumRegionShare = 0.25, MaximumMaskedShare = 0.05;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Configuration errors of a rule, independent of any screen (empty = valid).</summary>
    public static IEnumerable<string> Validate(VisualNormalizationRule? rule)
    {
        if (rule is null) { yield return "null rule"; yield break; }
        if (!Protocol.IsValidName(rule.Id)) yield return $"rule id '{rule.Id}' is not a lowercase hyphenated name";
        if (!NormalizationKinds.All.Contains(rule.Kind)) yield return $"rule '{rule.Id}': unknown kind '{rule.Kind}' (use {string.Join(", ", NormalizationKinds.All)})";
        if (string.IsNullOrWhiteSpace(rule.Description)) yield return $"rule '{rule.Id}': a description is required";
        if (rule.Regions is not { Length: > 0 and <= 16 } || rule.Regions.Any(r => r is null)) yield return $"rule '{rule.Id}': 1..16 regions are required";
        else foreach (var r in rule.Regions.Where(r => r.Grids is not null && (r.Grids.Length == 0 || r.Grids.Any(g => !ScenarioSelection.TryParseSize(g, out _, out _)))))
            yield return $"rule '{rule.Id}': region {r} has an invalid grid list";
        if (rule.MinMatches < 0 || rule.MaxMatches < Math.Max(1, rule.MinMatches) || rule.MaxMatches > 32) yield return $"rule '{rule.Id}': matches must satisfy 0 <= min <= max, 1 <= max <= 32";
        if (rule.MaxCells is < 1 or > 400) yield return $"rule '{rule.Id}': maxCells must be 1..400";
        Regex? regex = null; string? error = null;
        try { regex = new Regex(rule.Pattern ?? throw new ArgumentException("missing"), RegexOptions.CultureInvariant, MatchTimeout); }
        catch (ArgumentException ex) { error = ex.Message; }
        if (regex is null) yield return $"rule '{rule.Id}': invalid pattern ({error})";
        else if (NormalizationKinds.All.Contains(rule.Kind) && !regex.GetGroupNames().Contains(rule.Group))
            yield return $"rule '{rule.Id}': the pattern must contain the named group (?<{rule.Group}>...)";
    }

    /// <summary>Applies <paramref name="rules"/> (already validated) to <paramref name="screen"/> at checkpoint grid
    /// <c>screen.Width x screen.Height</c>. On any error the canonical screen is null and nothing is masked.</summary>
    public static NormalizationResult Apply(ScreenSnapshot screen, IReadOnlyList<VisualNormalizationRule> rules, NormalizationContext context)
    {
        string grid = ScenarioSelection.Format(screen.Width, screen.Height);
        int screenCells = screen.Width * screen.Height, regionCells = 0;
        var masked = new bool[screen.Width, screen.Height];
        var applied = new List<AppliedNormalization>();
        NormalizationResult Fail(string why) => new(null, applied.ToArray(), why);
        foreach (var rule in rules)
        {
            var regions = rule.Regions.Where(r => r.AppliesTo(grid)).ToArray();
            if (regions.Length == 0) return Fail($"rule '{rule.Id}' declares no region for grid {grid}");
            if (rule.Kind == NormalizationKinds.RunFixtureId && context.FixtureId is null)
                return Fail($"rule '{rule.Id}' needs this execution's fixture identifier, which is unavailable");
            var regex = new Regex(rule.Pattern, RegexOptions.CultureInvariant, MatchTimeout);
            var spans = new List<int[]>(); var values = new List<string>();
            int matches = 0;
            foreach (var region in regions)
            {
                var resolved = region.Resolve(screen.Width, screen.Height);
                if (resolved is null) return Fail($"rule '{rule.Id}': region {region} lies outside the {grid} screen");
                var (x0, y0, x1, y1) = resolved.Value;
                regionCells += (x1 - x0) * (y1 - y0);
                for (int y = y0; y < y1; y++)
                {
                    string row = RowText(screen, y, x0, x1);
                    MatchCollection found;
                    try { found = regex.Matches(row); _ = found.Count; }
                    catch (RegexMatchTimeoutException) { return Fail($"rule '{rule.Id}': pattern timed out"); }
                    foreach (Match match in found)
                    {
                        matches++;
                        var group = match.Groups[rule.Group];
                        if (!group.Success || group.Captures.Count == 0) return Fail($"rule '{rule.Id}': a match at ({x0 + match.Index},{y}) has no '{rule.Group}' capture");
                        foreach (Capture capture in group.Captures)
                        {
                            if (capture.Length == 0) return Fail($"rule '{rule.Id}': empty '{rule.Group}' capture at ({x0 + capture.Index},{y})");
                            if (rule.Kind == NormalizationKinds.RunFixtureId && !context.FixtureId!.EndsWith(capture.Value, StringComparison.Ordinal))
                                return Fail($"rule '{rule.Id}': '{capture.Value}' at ({x0 + capture.Index},{y}) is not a tail of this execution's fixture identifier");
                            for (int x = x0 + capture.Index; x < x0 + capture.Index + capture.Length; x++) masked[x, y] = true;
                            spans.Add([x0 + capture.Index, y, capture.Length]); values.Add(capture.Value);
                        }
                    }
                }
            }
            if (matches < rule.MinMatches || matches > rule.MaxMatches)
                return Fail($"rule '{rule.Id}': {matches} match(es) at {grid}, expected {rule.MinMatches}..{rule.MaxMatches}");
            int cells = spans.Sum(s => s[2]);
            if (cells > rule.MaxCells) return Fail($"rule '{rule.Id}': would mask {cells} cells, more than its maxCells {rule.MaxCells}");
            applied.Add(new(rule.Id, rule.Kind, rule.Description, spans.ToArray(), values.ToArray()));
        }
        if (regionCells > screenCells * MaximumRegionShare)
            return Fail($"normalization regions cover {regionCells} of {screenCells} cells (more than {(int)(MaximumRegionShare * 100)}% of the screen)");
        int total = 0;
        foreach (bool m in masked) if (m) total++;
        if (total > screenCells * MaximumMaskedShare)
            return Fail($"normalization would mask {total} of {screenCells} cells (more than {(int)(MaximumMaskedShare * 100)}% of the screen)");
        var cellsCopy = new TScreenChar[screenCells];
        for (int y = 0; y < screen.Height; y++)
        for (int x = 0; x < screen.Width; x++)
        {
            var cell = screen[x, y];
            cellsCopy[y * screen.Width + x] = masked[x, y] ? new TScreenChar(Placeholder, cell.Attr) : cell;
        }
        return new(new ScreenSnapshot(screen.Width, screen.Height, cellsCopy), applied.ToArray(), null);
    }

    private static string RowText(ScreenSnapshot screen, int y, int x0, int x1)
    {
        var text = new StringBuilder(x1 - x0);
        for (int x = x0; x < x1; x++) text.Append(screen[x, y].Character);
        return text.ToString();
    }
}
