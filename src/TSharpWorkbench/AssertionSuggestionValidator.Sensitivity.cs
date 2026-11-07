using System.Text;
using System.Text.RegularExpressions;
using TSharpWorkbench.Testing;

// Sensitivity rules of the validator. Findings are fixed descriptions that never quote the text.

internal static partial class AssertionSuggestionValidator
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const string Months = "jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec|january|february|march|april|june|july|august|september|october|november|december";

    /// <summary>Dynamic-looking text: (category, pattern). Any match makes a text unsafe for an assertion.</summary>
    internal static readonly (string Category, Regex Pattern)[] DynamicPatterns =
    [
        ("clock time", new(@"(?<!\d)\d{1,2}:\d{2}(?::\d{2})?(?!\d)", Options)),
        ("date", new(@"(?<!\d)\d{1,4}[-./]\d{1,2}[-./]\d{1,4}(?!\d)", Options)),
        ("date", new(@"\b(?:" + Months + @")\.?\s+\d{1,2}\b|\b\d{1,2}\.?\s+(?:" + Months + @")\b", Options | RegexOptions.IgnoreCase)),
        ("long number", new(@"\d{4,}", Options)),
        ("percentage", new(@"\d+(?:[.,]\d+)?\s?%", Options)),
        ("counter", new(@"(?<!\d)\d+\s?/\s?\d+(?!\d)|\b\d+\s+of\s+\d+\b", Options | RegexOptions.IgnoreCase)),
        ("GUID", new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", Options)),
        ("URL", new(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://\S+|\bwww\.\S+", Options | RegexOptions.IgnoreCase)),
        ("e-mail address", new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", Options)),
        ("absolute path", new(@"(?:^|(?<=[\s""'(=\[]))(?:[A-Za-z]:[\\/]\S*|\\\\[^\s\\]+\\\S*|~/\S*|/[^\s/]+/\S*)", Options)),
        ("hex or base64 token", new(@"[A-Za-z0-9+/=_-]{12,}", Options)),
    ];

    /// <summary>Whether a 12+ character token looks generated (hex, or letters mixed with several digits / both cases and digits).</summary>
    internal static bool IsGeneratedToken(string token)
    {
        static bool Generated(string s)
        {
            if (s.Length < 12) return false;
            bool letter = s.Any(char.IsAsciiLetter), upper = s.Any(char.IsAsciiLetterUpper), lower = s.Any(char.IsAsciiLetterLower);
            int digits = s.Count(char.IsAsciiDigit);
            bool hex = s.All(char.IsAsciiHexDigit);
            return hex || (letter && digits >= 3) || (upper && lower && digits >= 1 && !s.Any(c => c is '-' or '_' or '+'));
        }
        return Generated(token) || token.Split('-', '_', '+').Any(Generated);
    }

    /// <summary>Categories of dynamic-looking content in <paramref name="text"/> (empty when static).</summary>
    internal static IReadOnlyList<string> DynamicCategories(string text)
    {
        var found = new List<string>();
        foreach (var (category, pattern) in DynamicPatterns)
        {
            bool hit = category == "hex or base64 token" ? pattern.Matches(text).Any(m => IsGeneratedToken(m.Value)) : pattern.IsMatch(text);
            if (hit && !found.Contains(category)) found.Add(category);
        }
        return found;
    }

    /// <summary>Typed-text overlap rule: <paramref name="text"/> contains a typed run of length ≥ 2, or (length ≥ 3) is contained
    /// in one; case-insensitive.</summary>
    internal static bool Overlaps(string text, string run) =>
        text.Length > 0 && run.Length > 0
        && ((run.Length >= 2 && text.Contains(run, StringComparison.OrdinalIgnoreCase))
            || (text.Length >= 3 && run.Contains(text, StringComparison.OrdinalIgnoreCase)));

    internal static bool IsInputLine(ViewNode node) =>
        node.Kind == "TInputLine" || node.Type == "TInputLine" || node.Type.EndsWith(".TInputLine", StringComparison.Ordinal);

    /// <summary>Per-evaluation context of the sensitive values (typed runs, input-line values and rectangles).</summary>
    private sealed class Sensitivity
    {
        private readonly string[] typed;
        private readonly string[] inputValues;
        private readonly ViewNode[] inputLines;
        private readonly ScreenSnapshot? cells;

        internal Sensitivity(CheckpointEvidence evidence, ValidationContext context)
        {
            typed = (context.TypedTexts ?? []).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal).ToArray();
            // Any captured tree is used for safety, also an omitted or truncated one (more data can only reject more).
            var tree = evidence.Inspection?.ViewTree;
            inputLines = tree is null ? [] : AssertionEvaluator.All(tree.Root).Where(IsInputLine).ToArray();
            inputValues = inputLines.SelectMany(n => new[] { n.Text, n.CellText?.Trim() }).OfType<string>()
                .Where(v => v.Trim().Length >= 2).Distinct(StringComparer.Ordinal).ToArray();
            cells = evidence.Cells;
        }

        /// <summary>Reasons (never quoting text) why the spec's text is sensitive; empty when safe.</summary>
        internal IReadOnlyList<string> Inspect(AssertionSpec spec)
        {
            var findings = new List<string>();
            void Add(string finding) { if (!findings.Contains(finding)) findings.Add(finding); }
            foreach (var (field, value) in new[] { ("text", spec.Text), ("view selector text", spec.View?.Text), ("other selector text", spec.Other?.Text) })
            {
                if (string.IsNullOrEmpty(value)) continue;
                if (typed.Any(t => Overlaps(value, t))) Add($"the {field} overlaps text typed during the recording");
                if (inputValues.Any(v => Overlaps(value, v))) Add($"the {field} overlaps the value of an input line");
                foreach (string category in DynamicCategories(value)) Add($"the {field} looks dynamic ({category})");
            }
            foreach (var selector in new[] { spec.View, spec.Other })
                if (selector?.Text is not null && inputLines.Any(selector.Matches)) Add("the selector selects an input line by its text");
            if (spec.Text is { Length: > 0 } text && spec.Kind is AssertionKinds.TextAt or AssertionKinds.ScreenContains && cells is not null && inputLines.Length > 0)
            {
                var areas = inputLines.Select(n => n.Visible).OfType<CellRect>().Where(r => !r.IsEmpty).ToArray();
                bool inside = spec.Kind == AssertionKinds.TextAt
                    ? spec.X is { } x && spec.Y is { } y && areas.Any(a => Geometry.Intersects(a, new CellRect(x, y, text.Length, 1)))
                    : Occurrences(text).Any(r => areas.Any(a => Geometry.Intersects(a, r)));
                if (inside) Add("the text is shown inside an input line");
            }
            return findings;
        }

        private IEnumerable<CellRect> Occurrences(string text)
        {
            for (int y = 0; y < cells!.Height; y++)
            {
                string row = Inspectors.Row(cells, 0, y, cells.Width);
                for (int x = row.IndexOf(text, StringComparison.Ordinal); x >= 0; x = row.IndexOf(text, x + 1, StringComparison.Ordinal))
                    yield return new CellRect(x, y, text.Length, 1);
            }
        }

        /// <summary>Replaces typed runs, input-line values, <paramref name="extra"/> strings and dynamic-looking content with
        /// "(redacted)" (one marker per contiguous redacted range). Typed runs and input values of length ≥ 3 only (shorter ones would
/// mangle ordinary words of a sentence); dynamic patterns only when <paramref name="dynamic"/>.</summary>
        internal string Scrub(string text, IReadOnlyList<string> extra, bool dynamic)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var mask = new bool[text.Length];
            void Mark(int start, int length) { for (int i = start; i < start + length && i < mask.Length; i++) mask[i] = true; }
            foreach (string needle in typed.Concat(inputValues).Where(t => t.Length >= 3).Concat(extra.Where(e => e.Length > 0)))
                for (int i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase))
                    Mark(i, needle.Length);
            foreach (var (category, pattern) in dynamic ? DynamicPatterns : [])
                foreach (Match m in pattern.Matches(text))
                    if (category != "hex or base64 token" || IsGeneratedToken(m.Value)) Mark(m.Index, m.Length);
            if (!mask.Contains(true)) return text;
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (!mask[i]) { sb.Append(text[i]); continue; }
                if (i == 0 || !mask[i - 1]) sb.Append(Redacted);
            }
            return sb.ToString();
        }
    }
}
