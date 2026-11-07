using System.Text.Json.Serialization;

namespace TSharpWorkbench.Diagnostics.DriverOutput;

/// <summary>A grid size in character cells.</summary>
public readonly record struct GridSize(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";

    public static GridSize Parse(string text)
    {
        string[] parts = text.Split('x', 'X');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h) || w <= 0 || h <= 0)
            throw new FormatException($"Invalid grid '{text}' (expected WxH with positive sizes).");
        return new GridSize(w, h);
    }
}

/// <summary>One row segment handed to <c>IDriver.WriteBuf</c> (the framework always writes h = 1).</summary>
public sealed record CellWrite(int X, int Y, TScreenChar[] Cells);

/// <summary>A caret request: <c>SetCaretPosition</c> followed by <c>SetCursorType</c>.</summary>
public readonly record struct CaretRequest(int X, int Y, ushort CursorType);

/// <summary>One step of a fixture: what the application asks of the driver before a capture.</summary>
public sealed record FixtureStep(string Name, IReadOnlyList<CellWrite> Writes, CaretRequest Caret, GridSize? ResizeTo = null);

/// <summary>
/// The semantics a driver preserved for one cell: the text it shows and the 16-colour indices of
/// its foreground and background. An index of -1 means the driver produced a colour outside the
/// VGA palette or none at all (see the capture diagnostics).
/// </summary>
public readonly record struct SemanticCell(string Text, int Fg, int Bg);

/// <summary>Where a driver left its caret.</summary>
public sealed record CursorState(int X, int Y, bool Visible, string Shape);

/// <summary>One observed output operation, in order. Field use depends on the driver path.</summary>
public sealed record CaptureOp(string Kind, int X = 0, int Y = 0, int W = 0, int H = 0,
    string? Text = null, string? Fg = null, string? Bg = null, string? Detail = null);

/// <summary>How a driver path shows the caret, which decides how the caret cell is compared.</summary>
public enum CaretModel
{
    /// <summary>A native caret (console cursor, terminal cursor); cell contents are unaffected.</summary>
    Native,
    /// <summary>The caret is drawn into the cell (SDL): block swaps colours, underline adds a bar.</summary>
    Drawn,
}

/// <summary>Layer B observation of one step: what the driver's real translation produced.</summary>
public sealed class StepCapture
{
    public required string Step { get; init; }
    public required GridSize Grid { get; init; }
    public SemanticCell[] Cells { get; set; } = [];
    public CursorState? Cursor { get; set; }
    public List<CaptureOp> Operations { get; } = new();
    public int OperationsTotal { get; set; }
    public Dictionary<string, int> OperationCounts { get; } = new();
    public List<string> Diagnostics { get; } = new();

    /// <summary>Path-specific checks that passed (resize notification, native cursor shape, …).</summary>
    public List<string> Checks { get; } = new();

    /// <summary>Path-specific checks that failed.</summary>
    public List<string> Failures { get; } = new();

    public void Check(bool ok, string description)
    {
        if (ok) Checks.Add(description); else Failures.Add(description);
    }

    /// <summary>Cells the path drew the caret into, with what it drew (Drawn caret model only).</summary>
    public string? DrawnCaret { get; set; }

    public const int MaxRecordedOperations = 400;

    public void Record(CaptureOp op)
    {
        OperationsTotal++;
        OperationCounts[op.Kind] = OperationCounts.TryGetValue(op.Kind, out int n) ? n + 1 : 1;
        if (Operations.Count < MaxRecordedOperations) Operations.Add(op);
    }
}

/// <summary>Outcome categories used throughout the qualification.</summary>
public static class Outcomes
{
    public const string Match = "Match";
    public const string Mismatch = "Mismatch";
    public const string Unsupported = "Unsupported";
    public const string Unavailable = "PlatformUnavailable";
    public const string Error = "Error";

    public const string Verified = "SupportedVerified";
    public const string NotTested = "SupportedNotTested";
    public const string NotSupported = "NotSupported";
}

/// <summary>One cell that differed from the expected logical cell.</summary>
public sealed record CellMismatch(int X, int Y, string Field, string Expected, string Actual);

/// <summary>Comparison of one step capture against the expected logical screen.</summary>
public sealed class StepComparison
{
    public required string Step { get; init; }
    public required string Outcome { get; set; }
    public int CellsCompared { get; set; }
    public int TextMismatches { get; set; }
    public int FgMismatches { get; set; }
    public int BgMismatches { get; set; }
    public List<CellMismatch> FirstMismatches { get; } = new();
    public List<string> Checks { get; } = new();
    public List<string> Failures { get; } = new();
}

/// <summary>Everything one driver path produced for one fixture at one grid.</summary>
public sealed class PathFixtureResult
{
    public required string Path { get; init; }
    public required string Driver { get; init; }
    public required string Fixture { get; init; }
    public required GridSize Grid { get; init; }
    public string Outcome { get; set; } = Outcomes.Match;
    public string? Reason { get; set; }
    public List<StepCapture> Captures { get; } = new();
    public List<StepComparison> Comparisons { get; } = new();

    [JsonIgnore]
    public bool Passed => Outcome == Outcomes.Match;
}
