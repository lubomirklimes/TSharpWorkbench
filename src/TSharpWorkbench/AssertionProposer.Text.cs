using System.Text;
using System.Text.RegularExpressions;
using TSharpWorkbench.Testing;

// Text, selector and region helpers of the proposer. Fail closed: anything that may be dynamic, typed
// or volatile is not proposed (the validator re-checks with the typed texts and the replay grid).

internal static partial class AssertionProposer
{
    private const int MaxText = 60, MaxRegionRows = 50;

    private static readonly Regex[] Dynamic =
    [
        new(@"\d{1,2}:\d{2}", RegexOptions.CultureInvariant),                                 // clock time
        new(@"\d{1,4}[-./]\d{1,2}[-./]\d{1,4}", RegexOptions.CultureInvariant),                // date
        new(@"\d{4,}", RegexOptions.CultureInvariant),                                        // long digit run
        new(@"\d\s*%|%\s*\d", RegexOptions.CultureInvariant),                                  // percentage
        new(@"\d+\s*/\s*\d+", RegexOptions.CultureInvariant),                                  // counter 3/10
        new(@"\d+\s+of\s+\d+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),       // 3 of 10
        new(@"[:=#]\s*[-+]?\d", RegexOptions.CultureInvariant),                                // label: number
        new(@"\(\s*\d+\s*\)", RegexOptions.CultureInvariant),                                  // (3)
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant), // GUID
        new(@"(?=[A-Za-z0-9+/=_-]*\d)[A-Za-z0-9+/=_-]{12,}", RegexOptions.CultureInvariant),  // hex/base64-like token
        new(@"\b[0-9a-fA-F]{12,}\b", RegexOptions.CultureInvariant),                           // long hex
        new(@"[A-Za-z]:[\\/]|\\\\|(^|\s)~?/[^\s/]+/", RegexOptions.CultureInvariant),          // absolute path
        new(@"[A-Za-z][A-Za-z0-9+.-]*://|www\.", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), // URL
        new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.CultureInvariant),                       // e-mail
    ];

    /// <summary>Static-looking text: nonempty, at least two characters with a letter, at most <see cref="MaxText"/> characters,
    /// no control characters and none of the dynamic patterns (times, dates, long digit runs, percentages, counters, GUIDs,
    /// hex/base64 tokens, absolute paths, URLs, e-mail addresses, "label: number").</summary>
    internal static bool IsStatic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2 || text.Length > MaxText) return false;
        if (text.Any(c => char.IsControl(c) || char.IsSurrogate(c) || c == '�')) return false;
        if (!text.Any(char.IsLetter)) return false;
        return !Dynamic.Any(r => r.IsMatch(text));
    }

    /// <summary>Input lines and editors: their text may be what the user typed. Never their text, never their cells.</summary>
    private static bool IsTextEntry(ViewNode node) =>
        Has(node.Kind) || Has(node.Type);

    private static bool Has(string name) =>
        name.Contains("InputLine", StringComparison.Ordinal) || name.Contains("Editor", StringComparison.Ordinal)
        || name.Contains("Memo", StringComparison.Ordinal) || name.Contains("Terminal", StringComparison.Ordinal)
        || name.Contains("ComboBox", StringComparison.Ordinal);

    /// <summary>Kinds whose model text is a static label (button title, window/dialog title) and may be a selector basis.
    /// Clusters, lists and menus report their CURRENT item (volatile); input lines their value (typed).</summary>
    private static bool TextIsLabel(ViewNode node) =>
        !IsTextEntry(node) && (node.Kind == "TButton" || node.Kind.EndsWith("Window", StringComparison.Ordinal) || node.Kind.EndsWith("Dialog", StringComparison.Ordinal));

    private static bool IsChrome(ViewNode node) => node.Kind is "TApplication" or "TProgram" or "TDeskTop" or "TBackground" or "TFrame"
        or "TStatusLine" or "TScrollBar" || node.Kind.StartsWith("TMenu", StringComparison.Ordinal);

    private static bool IsMenuView(ViewNode node) => node.Kind.StartsWith("TMenu", StringComparison.Ordinal);

    /// <summary>A selector the proposer may emit for <paramref name="node"/>: no Path, text only for label kinds and static.</summary>
    private static bool SafeSelector(ViewSelector selector, ViewNode node) =>
        selector.Path is null && (selector.Type is not null || selector.Id is not null)
        && (selector.Text is null || (TextIsLabel(node) && IsStatic(selector.Text)))
        && (selector.Id is null || Protocol.IsValidName(selector.Id));

    internal enum Basis { Id, TypeText, Type }

    /// <summary>First unique safe selector for a node of a usable tree: Id, else Type+Text (labels only), else short Type.</summary>
    private static (ViewSelector Selector, Basis Basis)? UniqueSelector(Context context, ViewNode? node)
    {
        if (node is null || !context.TreeUsable) return null;
        string type = ShortType(node);
        var tries = new List<(ViewSelector, Basis)>();
        if (node.Id is { } id && Protocol.IsValidName(id)) tries.Add((ViewSelector.ById(id), Basis.Id));
        if (node.Text is { } text && TextIsLabel(node) && IsStatic(text)) tries.Add((new ViewSelector(Type: type, Text: text), Basis.TypeText));
        tries.Add((ViewSelector.OfType(type), Basis.Type));
        foreach (var (selector, basis) in tries)
            if (selector.Matches(node) && SafeSelector(selector, node) && context.Count(selector) == 1) return (selector, basis);
        return null;
    }

    private static Basis BasisOf(ViewSelector selector) =>
        selector.Id is not null ? Basis.Id : selector.Text is not null ? Basis.TypeText : Basis.Type;

    private static string Describe(Basis basis) => basis switch
    {
        Basis.Id => "its diagnostic id",
        Basis.TypeText => "its type and label",
        _ => "its type alone",
    };

    private static string ShortType(ViewNode node)
    {
        string name = node.Type;
        int cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+'));
        string shortName = cut >= 0 ? name[(cut + 1)..] : name;
        return new ViewSelector(Type: shortName).Matches(node) ? shortName : name;
    }

    /// <summary>Readable id stem of a view: label/id words plus its kind (<c>ok-button</c>, <c>find-dialog</c>, <c>dialog</c>).</summary>
    private static string Stem(ViewNode node, ViewSelector selector)
    {
        string kind = Slug(node.Kind.StartsWith('T') && node.Kind.Length > 1 ? node.Kind[1..] : node.Kind, 16);
        if (kind.Length == 0) kind = "view";
        if (selector.Id is { } id && Slug(id, 32) is { Length: > 0 } named) return named;     // a diagnostic id is already a name
        string name = selector.Text is { } text ? Slug(text, 24) : "";
        return name.Length == 0 ? kind : name.EndsWith(kind, StringComparison.Ordinal) ? name : name + "-" + kind;
    }

    /// <summary>Lowercase ASCII words joined by hyphens, cut at a word boundary to at most <paramref name="max"/> characters.</summary>
    internal static string Slug(string text, int max)
    {
        var sb = new StringBuilder();
        // Split camel case (EditWindow → edit-window) before lowering.
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= 'A' and <= 'Z' && i > 0 && text[i - 1] is >= 'a' and <= 'z') sb.Append('-');
            sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }
        string slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        if (slug.Length > max)
        {
            int cut = slug.LastIndexOf('-', max);
            slug = (cut > 0 ? slug[..cut] : slug[..max]).Trim('-');
        }
        return slug;
    }

    // ── regions ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A run of static-looking text inside a region row (split at 2+ spaces, frame/graphic glyphs and masked cells).</summary>
    private sealed record Segment(int X, int Y, string Text, bool Cut, int Occurrences);

    private static bool Graphic(char c) => c is >= '─' and <= '◿' || c is '·' or '•' or '∙';

    private static bool Separator(Context context, int x, int y)
    {
        char c = context.Cells[x, y].Character;
        return context.Mask[x, y] || c == '\0' || char.IsControl(c) || char.IsSurrogate(c) || Graphic(c);
    }

    private static bool WordChar(Context context, int x, int y) =>
        x >= 0 && x < context.Cells.Width && !context.Mask[x, y] && char.IsLetterOrDigit(context.Cells[x, y].Character);

    /// <summary>Static segments of the region's rows (clipped to the evidence grid), best first: unique on screen, not cut,
    /// longer, top-left.</summary>
    private static List<Segment> Segments(Context context, CellRect region)
    {
        var cells = context.Cells;
        int x0 = Math.Clamp(region.X, 0, cells.Width), x1 = Math.Clamp(region.Right, x0, cells.Width);
        int y0 = Math.Clamp(region.Y, 0, cells.Height), y1 = Math.Clamp(region.Bottom, y0, Math.Min(cells.Height, y0 + MaxRegionRows));
        var found = new List<Segment>();
        for (int y = y0; y < y1; y++)
        {
            int x = x0;
            while (x < x1)
            {
                if (Separator(context, x, y) || cells[x, y].Character == ' ') { x++; continue; }
                int start = x, end = x;              // [start, end) of non-space content, single spaces allowed inside
                while (x < x1 && !Separator(context, x, y))
                {
                    if (cells[x, y].Character == ' ')
                    {
                        if (x + 1 >= x1 || cells[x + 1, y].Character == ' ' || Separator(context, x + 1, y)) break;
                    }
                    x++; end = x;
                }
                Add(context, found, start, end, y, x0, x1);
            }
        }
        var screenRows = Enumerable.Range(0, cells.Height).Select(r => Inspectors.Row(cells, 0, r, cells.Width)).ToArray();
        return found.Select(s => s with { Occurrences = screenRows.Sum(row => Occurrences(row, s.Text)) })
            .OrderBy(s => s.Occurrences == 1 ? 0 : 1).ThenBy(s => s.Cut ? 1 : 0).ThenByDescending(s => Math.Min(s.Text.Length, 40))
            .ThenBy(s => s.Y).ThenBy(s => s.X).ToList();
    }

    private static void Add(Context context, List<Segment> found, int start, int end, int y, int x0, int x1)
    {
        string text = new(Enumerable.Range(start, end - start).Select(i => context.Cells[i, y].Character).ToArray());
        bool cutLeft = start == x0 && WordChar(context, start - 1, y) && WordChar(context, start, y);
        bool cutRight = end == x1 && WordChar(context, end, y) && WordChar(context, end - 1, y);
        if (cutRight && text.LastIndexOf(' ') is > 0 and var space) { text = text[..space].TrimEnd(); cutRight = false; }
        if (cutLeft && text.IndexOf(' ') is > 0 and var first) { start += first + 1; text = text[(first + 1)..]; cutLeft = false; }
        if (text.Length > MaxText)
        {
            int cut = text.LastIndexOf(' ', MaxText);
            text = cut > 1 ? text[..cut] : text[..MaxText];
        }
        text = text.TrimEnd();
        if (IsStatic(text)) found.Add(new(start, y, text, cutLeft || cutRight, 0));
    }

    private static int Occurrences(string row, string text)
    {
        int count = 0;
        for (int i = row.IndexOf(text, StringComparison.Ordinal); i >= 0; i = row.IndexOf(text, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
