namespace TSharpWorkbench.Testing;

// Exact text metrics of the TSharpVision v1.1.0 drawing code, ported read-only from the v1.1.0 tag
// (TDrawBuffer.moveCStr/moveBuf, TButton.DrawTitle, TLabel.Draw, TStaticText.NextRow, TCluster.Column/DrawMultiBox,
// TFrame.Draw). v1.1.0 stores ONE UTF-16 code unit per screen cell (TScreenChar.Character is a char; moveCStr/moveBuf
// copy unit by unit), so a drawn width is a count of UTF-16 units; East Asian wide glyphs and non-BMP characters are
// still one cell per unit there, and a terminal may render them wider (reported separately as a lower-class note).
internal static class LayoutTextMetrics
{
    /// <summary>Cells moveCStr writes for <paramref name="markup"/>: every UTF-16 unit except the '~' toggles
    /// (TCluster.CStrLen / TButton.DrawTitle titleLen; there is no escape for a literal '~').</summary>
    internal static int CStrLen(string? markup)
    {
        if (string.IsNullOrEmpty(markup)) return 0;
        int n = 0;
        foreach (char c in markup) if (c != '~') n++;
        return n;
    }

    /// <summary>Text without the '~' toggles (what moveCStr shows).</summary>
    internal static string Shown(string? markup) => markup is null ? "" : markup.Replace("~", "", StringComparison.Ordinal);

    /// <summary>Characters a terminal may render two columns wide although v1.1.0 gives them one cell each: East Asian
    /// Wide/Fullwidth ranges and non-BMP characters (surrogate pairs take two cells, one per unit, already counted).</summary>
    internal static int WideUnits(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (char c in text)
            if (c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣'
                or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦')
                n++;
        return n;
    }

    // ── TButton ─────────────────────────────────────────────────────────────

    /// <summary>Caption placement of an unpressed TButton (DrawState(false) → DrawTitle): the caption is drawn on row
    /// <c>size.y/2 - 1</c> (no row when the button is under 2 rows tall) starting at column <c>1 + l</c>, where
    /// <c>l = max(1, (s - w - 1) / 2)</c> centred (or 1 for bfLeftJust) and <c>s = size.x - 1</c> is the shadow column the
    /// face ends at. It fits iff <c>1 + l + w ≤ s</c>, which is <c>w ≤ size.x - 3</c> for both justifications.</summary>
    internal static (int Row, int Start, int Width, int FaceEnd, bool RowDrawn) ButtonCaption(int width, int height, string markup)
    {
        int w = CStrLen(markup), s = width - 1, l = Math.Max(1, (s - w - 1) / 2);
        int row = height / 2 - 1;
        return (row, 1 + l, w, s, height >= 2 && row >= 0);
    }

    // ── TStaticText ─────────────────────────────────────────────────────────

    /// <summary>Exact port of TStaticText.NextRow (v1.1.0). <paramref name="width"/> must be ≥ 1.</summary>
    internal static void NextRow(string s, ref int p, int width, ref bool center, out int start, out int end, out bool centered)
    {
        int l = s.Length;
        if (s[p] == (char)3) { center = true; p++; }
        int i = p, j;
        do
        {
            j = p;
            while (p < l && s[p] == ' ') p++;
            while (p < l && s[p] != ' ' && s[p] != '\n') p++;
        } while (p < l && p < i + width && s[p] != '\n');
        if (p > i + width) p = j > i ? j : i + width;
        start = i; end = p; centered = center;
        while (p < l && s[p] == ' ') p++;
        if (p < l && s[p] == '\n') { center = false; p++; }
    }

    /// <summary>The rows TStaticText.Draw lays out for <paramref name="s"/> in a view <paramref name="width"/> cells wide:
    /// for each row its column offset (centred rows: <c>(width - len) / 2</c>) and text. Rows beyond the view height are
    /// included (that is what does not fit). Bounded by <paramref name="maxRows"/>.</summary>
    internal static List<(int X, string Text)> StaticRows(string? s, int width, int maxRows = 512)
    {
        var rows = new List<(int, string)>();
        if (string.IsNullOrEmpty(s) || width < 1) return rows;
        int p = 0; bool center = false;
        while (p < s.Length && rows.Count < maxRows)
        {
            NextRow(s, ref p, width, ref center, out int i, out int end, out bool centered);
            int len = Math.Max(0, end - i);
            rows.Add((centered ? (width - end + i) / 2 : 0, s.Substring(i, len)));
        }
        return rows;
    }

    // ── TCluster ────────────────────────────────────────────────────────────

    /// <summary>Exact port of TCluster.Column (v1.1.0): items fill columns of <paramref name="height"/> rows; each later
    /// column starts 6 cells after the widest item text of the previous column (icon " [ ] " is 5 cells, text at +5).</summary>
    internal static int ClusterColumn(IReadOnlyList<int> widths, int height, int item)
    {
        if (item < height) return 0;
        int width = 0, col = -6, l = 0;
        for (int i = 0; i <= item; i++)
        {
            if (i % height == 0) { col += width + 6; width = 0; }
            if (i < widths.Count) l = widths[i];
            if (l > width) width = l;
        }
        return col;
    }

    // ── TFrame ──────────────────────────────────────────────────────────────

    /// <summary>TFrame.Draw title budgets: <c>width - 10</c>, minus 6 when the window has wfClose or wfZoom, minus 4 when
    /// it has a number. The inspection does not expose the window flags or number, so all four are possible; ordered
    /// largest first.</summary>
    internal static int[] TitleBudgets(int frameWidth) => [frameWidth - 10, frameWidth - 14, frameWidth - 16, frameWidth - 20];

    /// <summary>What TFrame.Draw writes for a title of <paramref name="title"/> under budget <paramref name="l"/>:
    /// the column (frame-relative) and the exact text, or null when nothing is drawn (budget ≤ 0, or truncated with a
    /// budget under 2). Truncated: " .." at <c>i - 1</c> then the last <c>l - 2</c> units at <c>i + 2</c>, <c>i = (width - l) >> 1</c>.</summary>
    internal static (int X, string Text, bool Truncated)? FrameTitle(int frameWidth, int l, string title)
    {
        if (l <= 0 || title.Length == 0) return null;
        int ls = title.Length;
        if (ls <= l) return ((frameWidth - ls) >> 1, title, false);
        if (l < 2) return null;
        int start = ls - l + 2;
        if (start < ls && char.IsLowSurrogate(title[start])) start++;
        int i = (frameWidth - l) >> 1;
        return (i - 1, " .." + title[start..], true);
    }

    /// <summary>The cells of one row segment (unprintable/out-of-screen cells as '\0').</summary>
    internal static string Row(ScreenSnapshot cells, int x, int y, int length) => Inspectors.Row(cells, x, y, length);
}
