namespace TSharpWorkbench.Testing;

// Family-internal geometry helpers of the UX heuristics (ux.*). Pure functions over absolute cell
// rectangles; every helper is deterministic (ties resolve to the first item in the given order).
internal static class LayoutUxGeometry
{
    internal const string High = "high", Medium = "medium", Low = "low";

    /// <summary>Columns two rectangles share (negative = the horizontal distance between them).</summary>
    internal static int OverlapX(CellRect a, CellRect b) => Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);

    /// <summary>Rows two rectangles share (negative = the vertical distance between them).</summary>
    internal static int OverlapY(CellRect a, CellRect b) => Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);

    internal static CellRect Union(CellRect a, CellRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    internal static CellRect Union(IEnumerable<CellRect> rects)
    {
        CellRect result = default;
        foreach (var r in rects) result = Union(result, r);
        return result;
    }

    internal static CellRect Inset(CellRect r, int d) => new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    /// <summary>The most frequent value and its count; ties resolve to the value that occurs first.</summary>
    internal static (int Value, int Count) Mode(IReadOnlyList<int> values)
    {
        int best = values.Count > 0 ? values[0] : 0, bestCount = 0;
        foreach (int v in values)
        {
            int c = values.Count(x => x == v);
            if (c > bestCount) { best = v; bestCount = c; }
        }
        return (best, bestCount);
    }

    /// <summary>Connected components of <paramref name="items"/> under the symmetric <paramref name="related"/> relation.
    /// Components are ordered by their first member; members keep the input order.</summary>
    internal static List<List<T>> Components<T>(IReadOnlyList<T> items, Func<T, T, bool> related)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
                if (related(items[i], items[j]))
                {
                    int a = Root(i), b = Root(j);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
        var groups = new SortedDictionary<int, List<T>>();
        for (int i = 0; i < items.Count; i++)
        {
            int r = Root(i);
            if (!groups.TryGetValue(r, out var list)) groups[r] = list = [];
            list.Add(items[i]);
        }
        return groups.Values.ToList();
    }

    /// <summary>Maximal runs of <c>false</c> in <paramref name="occupied"/> that have an occupied cell on both sides.</summary>
    internal static List<(int Start, int Length)> InnerEmptyRuns(bool[] occupied)
    {
        var runs = new List<(int, int)>();
        int i = 0;
        while (i < occupied.Length)
        {
            if (occupied[i]) { i++; continue; }
            int start = i;
            while (i < occupied.Length && !occupied[i]) i++;
            if (start > 0 && i < occupied.Length) runs.Add((start, i - start));
        }
        return runs;
    }

    internal static string Ints(IEnumerable<int> values) => "[" + string.Join(",", values) + "]";
}
