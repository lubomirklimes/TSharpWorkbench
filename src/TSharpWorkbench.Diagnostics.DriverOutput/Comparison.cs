namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>What a driver path can express, used to account for legitimate capability differences.</summary>
public sealed record PathCapabilities(
    CaretModel Caret,
    bool CursorShape,
    bool ForegroundOfBlankCells,
    string Notes);

/// <summary>Compares a Layer B capture with the expected logical screen, cell by cell.</summary>
public static class CaptureComparer
{
    public const int MaxListedMismatches = 50;

    public static StepComparison Compare(LogicalScreen expected, StepCapture capture, CaretRequest caret, PathCapabilities caps)
    {
        var result = new StepComparison { Step = capture.Step, Outcome = Outcomes.Match };
        GridSize grid = expected.Grid;
        if (capture.Grid != grid || capture.Cells.Length != grid.Width * grid.Height)
        {
            result.Outcome = Outcomes.Mismatch;
            result.Failures.Add($"captured grid {capture.Grid} ({capture.Cells.Length} cells) differs from expected {grid}");
            return result;
        }

        bool caretShown = caret.CursorType != 0;
        int textMismatches = 0, fgMismatches = 0, bgMismatches = 0, unobservable = 0;
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                SemanticCell want = expected.Semantic(x, y);
                SemanticCell got = capture.Cells[y * grid.Width + x];
                bool isCaret = caretShown && caps.Caret == CaretModel.Drawn && x == caret.X && y == caret.Y;
                if (isCaret && caret.CursorType >= 100) want = want with { Fg = want.Bg, Bg = want.Fg };
                result.CellsCompared++;

                if (got.Text != want.Text) Mismatch(result, x, y, "text", want.Text, got.Text, ref textMismatches);
                bool blank = want.Text is " ";
                if (got.Fg != want.Fg && got.Fg != Vga.Unobservable && !(blank && !caps.ForegroundOfBlankCells))
                    Mismatch(result, x, y, "fg", Vga.Describe(want.Fg), Vga.Describe(got.Fg), ref fgMismatches);
                if (got.Bg == Vga.Unobservable || got.Fg == Vga.Unobservable) unobservable++;
                if (got.Bg != want.Bg && got.Bg != Vga.Unobservable)
                    Mismatch(result, x, y, "bg", Vga.Describe(want.Bg), Vga.Describe(got.Bg), ref bgMismatches);
            }
        }
        result.TextMismatches = textMismatches;
        result.FgMismatches = fgMismatches;
        result.BgMismatches = bgMismatches;
        result.Checks.Add($"{result.CellsCompared} cells compared (text, fg, bg)" + (caps.ForegroundOfBlankCells ? "" : "; fg of blank cells not rendered by this path"));

        if (unobservable > 0) result.Checks.Add($"{unobservable} cell colour(s) not observable on this path (e.g. background under a full block)");
        CompareCaret(result, capture, caret, caps);

        result.Checks.AddRange(capture.Checks);
        result.Failures.AddRange(capture.Failures);
        if (result.TextMismatches + result.FgMismatches + result.BgMismatches > 0 || result.Failures.Count > 0)
            result.Outcome = Outcomes.Mismatch;
        return result;
    }

    private static void CompareCaret(StepComparison result, StepCapture capture, CaretRequest caret, PathCapabilities caps)
    {
        bool shown = caret.CursorType != 0;
        string shape = !shown ? "hidden" : caret.CursorType >= 100 ? "block" : "underline";
        if (caps.Caret == CaretModel.Native)
        {
            if (capture.Cursor is not { } c)
            {
                result.Failures.Add("no cursor state captured");
                return;
            }
            Check(result, c.Visible == shown, $"cursor visibility {(shown ? "shown" : "hidden")} (got {(c.Visible ? "shown" : "hidden")})");
            if (shown)
                Check(result, c.X == caret.X && c.Y == caret.Y, $"cursor at ({caret.X},{caret.Y}) (got ({c.X},{c.Y}))");
            if (shown && caps.CursorShape)
                Check(result, c.Shape == shape, $"cursor shape {shape} (got {c.Shape})");
            else if (shown)
                result.Checks.Add($"cursor shape {shape}: not conveyed by this path (capability difference)");
        }
        else
        {
            string expected = !shown ? "none" : shape == "block" ? $"block at ({caret.X},{caret.Y})" : $"underline at ({caret.X},{caret.Y})";
            Check(result, (capture.DrawnCaret ?? "none") == expected, $"drawn caret {expected} (got {capture.DrawnCaret ?? "none"})");
        }
    }

    private static void Check(StepComparison result, bool ok, string text)
    {
        if (ok) result.Checks.Add(text); else result.Failures.Add(text);
    }

    private static void Mismatch(StepComparison result, int x, int y, string field, string want, string got, ref int counter)
    {
        counter++;
        if (result.FirstMismatches.Count < MaxListedMismatches)
            result.FirstMismatches.Add(new CellMismatch(x, y, field, want, got));
    }
}
