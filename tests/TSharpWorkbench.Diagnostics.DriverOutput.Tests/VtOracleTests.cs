using System.Text;
using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.DriverOutput.Tests;

/// <summary>The terminal test oracle itself: what it understands, and that it reports what it does not.</summary>
public sealed class VtOracleTests
{
    private static VtOracle Feed(int w, int h, string text)
    {
        var oracle = new VtOracle(w, h);
        oracle.Feed(Encoding.UTF8.GetBytes(text));
        oracle.Settle();
        return oracle;
    }

    [Fact]
    public void PositionsAndColoursCells()
    {
        VtOracle o = Feed(10, 3, "\u001b[2;4H\u001b[0;38;2;255;255;85;48;2;0;0;170mAž\u001b[91;102mB");
        SemanticCell[] cells = o.SemanticCells();
        Assert.Equal(new SemanticCell("A", 14, 1), cells[1 * 10 + 3]);
        Assert.Equal(new SemanticCell("ž", 14, 1), cells[1 * 10 + 4]);
        Assert.Equal(new SemanticCell("B", 12, 10), cells[1 * 10 + 5]); // ANSI bright red / bright green
        Assert.Equal((6, 1), (o.CursorX, o.CursorY));
        Assert.Empty(o.Diagnostics);
    }

    [Fact]
    public void BottomRightCellDefersTheWrapAndDoesNotScroll()
    {
        VtOracle o = Feed(3, 2, "\u001b[1;1Htop\u001b[2;3HZ\u001b[0m\u001b[1;1H");
        Assert.Equal("top", string.Concat(o.SemanticCells().Take(3).Select(c => c.Text)));
        Assert.Equal("Z", o.SemanticCells()[5].Text);
        Assert.Empty(o.Diagnostics);

        VtOracle scrolled = Feed(3, 2, "\u001b[2;3HZY");
        Assert.Contains(scrolled.Diagnostics, d => d.Contains("scrolled"));
    }

    [Theory]
    [InlineData("\u001b[5n", "unsupported CSI")]
    [InlineData("\u001b[?2004h", "unsupported private mode 2004")]
    [InlineData("\u001b[4m", "unsupported SGR parameter 4")]
    [InlineData("\u001b7", "unsupported escape")]
    [InlineData("\u0008", "unsupported control")]
    [InlineData("\u001b[1;1", "ended inside an escape")]
    public void UnsupportedSequencesAreReportedNotDropped(string sequence, string expected)
    {
        VtOracle o = Feed(5, 2, sequence);
        Assert.Contains(o.Diagnostics, d => d.Contains(expected));
    }

    [Fact]
    public void InvalidUtf8IsReported()
    {
        var o = new VtOracle(5, 1);
        o.Feed([0xC3, 0x28, 0xFF]);
        o.Settle();
        Assert.Equal(2, o.Diagnostics.Count);
        Assert.Equal("�", o.SemanticCells()[0].Text);
    }

    [Fact]
    public void Utf8SplitAcrossFeedsIsReassembled()
    {
        var o = new VtOracle(5, 1);
        byte[] bytes = Encoding.UTF8.GetBytes("ů");
        o.Feed(bytes.AsSpan(0, 1));
        o.Feed(bytes.AsSpan(1));
        o.Settle();
        Assert.Equal("ů", o.SemanticCells()[0].Text);
        Assert.Empty(o.Diagnostics);
    }

    [Fact]
    public void AlternateScreenContentSurvivesLeaving()
    {
        VtOracle o = Feed(4, 1, "\u001b[?1049h\u001b[1;1Halt!\u001b[?1049l");
        Assert.False(o.OnAlternateScreen);
        Assert.Equal(" ", o.SemanticCells()[0].Text);
        Assert.Equal("a", o.SemanticCells(lastAlternateScreen: true)[0].Text);
    }

    [Fact]
    public void OutOfRangeCursorIsClampedAndReported()
    {
        VtOracle o = Feed(4, 2, "\u001b[9;9HX");
        Assert.Equal("X", o.SemanticCells()[7].Text);
        Assert.Contains(o.Diagnostics, d => d.Contains("outside"));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, -1)]
    public void InvalidDimensionsAreRejected(int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VtOracle(w, h));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VtOracle(3, 3).Resize(w, h));
    }

    [Fact]
    public void DiagnosticsAreBounded()
    {
        VtOracle o = Feed(5, 1, string.Concat(Enumerable.Repeat("\u0008", 5000)));
        Assert.Equal(VtOracle.MaxDiagnostics, o.Diagnostics.Count);
    }
}
