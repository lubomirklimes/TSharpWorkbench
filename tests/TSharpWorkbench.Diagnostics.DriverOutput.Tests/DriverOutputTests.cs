using System.Text;
using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.DriverOutput.Tests;

/// <summary>
/// Layer B: every fixture through every real driver output path at 80x25, 140x40 and the small
/// 20x6 clipping grid. A path that cannot run on this platform is skipped with its reason —
/// never reported as passed.
/// </summary>
[Collection(DriverCollection.Name)]
public sealed class DriverOutputTests(DriverPathsFixture fixture)
{
    private readonly DriverPaths _paths = fixture.Paths;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (string f in Fixtures.All)
            foreach (GridSize g in Expect.Grids) data.Add(f, g.ToString());
        return data;
    }

    private void RunPath(IOutputPath path, string fixtureId, string grid)
    {
        Skip.If(path.Unavailable != null, path.Unavailable);
        Expect.Match(QualificationRunner.Run(path, fixtureId, GridSize.Parse(grid)));
    }

    [SkippableTheory, MemberData(nameof(Cases))]
    public void Win32ConsoleOutputMatches(string fixtureId, string grid) => RunPath(_paths.Console, fixtureId, grid);

    [SkippableTheory, MemberData(nameof(Cases))]
    public void AnsiTerminalOutputMatches(string fixtureId, string grid) => RunPath(_paths.Terminal, fixtureId, grid);

    [SkippableTheory, MemberData(nameof(Cases))]
    public void SdlRendererOutputMatches(string fixtureId, string grid) => RunPath(_paths.SdlRenderer, fixtureId, grid);

    [SkippableTheory, MemberData(nameof(Cases))]
    public void SdlGpuShaderVerticesMatch(string fixtureId, string grid) => RunPath(_paths.SdlGpuShader, fixtureId, grid);

    [SkippableTheory, MemberData(nameof(Cases))]
    public void SdlGpuCompositeFrameMatches(string fixtureId, string grid) => RunPath(_paths.SdlGpuComposite, fixtureId, grid);

    [Fact]
    public void TerminalEmitsCzechTextAsUtf8AndPositionsEveryRow()
    {
        TerminalOutputPath terminal = _paths.Terminal;
        var grid = new GridSize(80, 25);
        terminal.Begin(grid);
        FixtureStep full = Fixtures.Steps(Fixtures.Screen, grid)[0];
        var capture = new StepCapture { Step = full.Name, Grid = grid };
        terminal.Apply(full, capture);
        terminal.Observe(capture);

        byte[] bytes = terminal.LastStepBytes;
        Assert.True(Contains(bytes, Encoding.UTF8.GetBytes(Fixtures.Czech)), "Czech text is not present as UTF-8");
        Assert.True(Contains(bytes, Encoding.UTF8.GetBytes("╔═")), "box drawing is not present as UTF-8");
        // Each WriteBuf positions its row and then re-parks the caret; SetCaretPosition adds one more.
        Assert.Equal(2 * grid.Height + 1, capture.OperationCounts["CUP"]);
        Assert.Empty(capture.Failures);
    }

    [SkippableFact]
    public void SdlRendererRedrawsTheWholeFrameForASingleCellChange()
    {
        SdlRendererOutputPath sdl = _paths.SdlRenderer;
        Skip.If(sdl.Unavailable != null, sdl.Unavailable);
        PathFixtureResult r = QualificationRunner.Run(sdl, Fixtures.Screen, new GridSize(80, 25));
        StepCapture single = r.Captures.Single(c => c.Step == "single-cell");
        // Capability, not a defect: the SDL renderer has no partial redraw, so stale content cannot survive.
        Assert.Equal(80 * 25, single.OperationCounts["Fill"]);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}

/// <summary>The checks have teeth: tampered observations are reported as mismatches.</summary>
[Collection(DriverCollection.Name)]
public sealed class SensitivityTests(DriverPathsFixture fixture)
{
    private static readonly GridSize Grid = new(20, 6);

    private static (LogicalScreen Expected, FixtureStep Step) FullStep()
    {
        FixtureStep full = Fixtures.Steps(Fixtures.Screen, Grid)[0];
        var model = new LogicalScreen(Grid, new TScreenChar(' ', 0x07));
        foreach (CellWrite w in full.Writes) model.Apply(w);
        return (model, full);
    }

    [Fact]
    public void ATamperedTerminalColourIsAMismatch()
    {
        var (expected, full) = FullStep();
        TerminalOutputPath terminal = fixture.Paths.Terminal;
        terminal.Begin(Grid);
        var real = new StepCapture { Step = "full", Grid = Grid };
        terminal.Apply(full, real);
        terminal.Observe(real);
        Assert.Equal(Outcomes.Match, CaptureComparer.Compare(expected, real, full.Caret, terminal.Capabilities).Outcome);

        // Re-interpret the same bytes with one background colour changed (VGA 7 light grey → 8 dark grey).
        string text = Encoding.UTF8.GetString(terminal.LastStepBytes).Replace("48;2;170;170;170", "48;2;85;85;85");
        var oracle = new VtOracle(Grid.Width, Grid.Height);
        oracle.Feed(Encoding.UTF8.GetBytes(text));
        var tampered = new StepCapture { Step = "full", Grid = Grid, Cells = oracle.SemanticCells(), Cursor = real.Cursor };
        StepComparison cmp = CaptureComparer.Compare(expected, tampered, full.Caret, terminal.Capabilities);
        Assert.Equal(Outcomes.Mismatch, cmp.Outcome);
        Assert.True(cmp.BgMismatches > 0);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("fg")]
    [InlineData("bg")]
    public void ASingleWrongCellFieldIsAMismatch(string field)
    {
        var (expected, full) = FullStep();
        SemanticCell[] cells = Enumerable.Range(0, Grid.Width * Grid.Height)
            .Select(i => expected.Semantic(i % Grid.Width, i / Grid.Width)).ToArray();
        int at = 2 * Grid.Width + 3;
        cells[at] = field switch
        {
            "text" => cells[at] with { Text = "?" },
            "fg" => cells[at] with { Fg = (cells[at].Fg + 1) % 16 },
            _ => cells[at] with { Bg = (cells[at].Bg + 1) % 16 },
        };
        var capture = new StepCapture { Step = "full", Grid = Grid, Cells = cells, Cursor = new CursorState(0, 0, false, "hidden") };
        StepComparison cmp = CaptureComparer.Compare(expected, capture, full.Caret,
            new PathCapabilities(CaretModel.Native, true, ForegroundOfBlankCells: true, ""));
        Assert.Equal(Outcomes.Mismatch, cmp.Outcome);
        Assert.Equal(new CellMismatch(3, 2, field, field switch
        {
            "text" => expected.Semantic(3, 2).Text,
            "fg" => Vga.Describe(expected.Semantic(3, 2).Fg),
            _ => Vga.Describe(expected.Semantic(3, 2).Bg),
        }, field switch
        {
            "text" => "?",
            "fg" => Vga.Describe((expected.Semantic(3, 2).Fg + 1) % 16),
            _ => Vga.Describe((expected.Semantic(3, 2).Bg + 1) % 16),
        }), cmp.FirstMismatches.Single());
    }

    [Fact]
    public void AWrongGridOrMissingCaretIsAMismatch()
    {
        var (expected, full) = FullStep();
        var wrongGrid = new StepCapture { Step = "full", Grid = new GridSize(21, 6), Cells = new SemanticCell[21 * 6] };
        Assert.Equal(Outcomes.Mismatch, CaptureComparer.Compare(expected, wrongGrid, full.Caret,
            new PathCapabilities(CaretModel.Native, true, true, "")).Outcome);

        SemanticCell[] cells = Enumerable.Range(0, Grid.Width * Grid.Height).Select(i => expected.Semantic(i % Grid.Width, i / Grid.Width)).ToArray();
        var shown = new CaretRequest(1, 1, 100);
        var noCaret = new StepCapture { Step = "c", Grid = Grid, Cells = cells, Cursor = new CursorState(1, 1, false, "hidden") };
        StepComparison cmp = CaptureComparer.Compare(expected, noCaret, shown, new PathCapabilities(CaretModel.Native, true, true, ""));
        Assert.Contains(cmp.Failures, f => f.StartsWith("cursor visibility"));
    }
}

/// <summary>Input translator parity across the drivers' real translators.</summary>
[Collection(DriverCollection.Name)]
public sealed class InputParityTests(DriverPathsFixture fixture)
{
    [Fact]
    public void EveryCaseIsVerifiedOrADocumentedCapabilityDifference()
    {
        SdlRendererOutputPath sdl = fixture.Paths.SdlRenderer;
        List<ParityResult> results = InputParity.Run(sdl.Unavailable == null ? sdl : null);

        Assert.Empty(results.Where(r => r.Outcome == Outcomes.Mismatch).Select(r => $"{r.Case}/{r.Driver}: {r.Note} — {r.Actual}"));
        // The only gaps are the legacy ANSI byte stream's: no modifier transitions, no key release,
        // no click count. Kitty negotiation closes the first two.
        Assert.Equal(
            ["double-click/terminal-ansi", "f5-release/terminal-ansi", "shift-down-up/terminal-ansi"],
            results.Where(r => r.Outcome == Outcomes.NotSupported).Select(r => $"{r.Case}/{r.Driver}").Order());
        Assert.All(results.Where(r => r.Driver is InputParity.Console or InputParity.TerminalKitty), r => Assert.Equal(Outcomes.Verified, r.Outcome));
        if (sdl.Unavailable == null)
            Assert.All(results.Where(r => r.Driver == InputParity.Sdl), r => Assert.Equal(Outcomes.Verified, r.Outcome));
    }
}

/// <summary>Capture serialization and bad input.</summary>
public sealed class CaptureContractTests
{
    [Fact]
    public void ResultsRoundTripThroughTheChildJson()
    {
        var result = new PathFixtureResult { Path = "p", Driver = "d", Fixture = "f", Grid = new GridSize(3, 1), Outcome = Outcomes.Mismatch, Reason = "r" };
        var capture = new StepCapture { Step = "s", Grid = new GridSize(3, 1), Cells = [new("a", 1, 2), new("ž", 15, 0), new(" ", -1, -2)], Cursor = new CursorState(1, 0, true, "block") };
        capture.Record(new CaptureOp("WriteConsoleOutputW", 0, 0, 3, 1));
        capture.Check(false, "a failed check");
        result.Captures.Add(capture);
        var cmp = new StepComparison { Step = "s", Outcome = Outcomes.Mismatch, TextMismatches = 1 };
        cmp.FirstMismatches.Add(new CellMismatch(1, 0, "text", "x", "ž"));
        result.Comparisons.Add(cmp);

        var doc = new ConsoleHostPath.ChildDocument { Schema = ArtifactWriter.Schema, ConsoleSource = "test" };
        doc.Results.Add(result);
        string json = System.Text.Json.JsonSerializer.Serialize(doc, ConsoleHostPath.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<ConsoleHostPath.ChildDocument>(json, ConsoleHostPath.Json)!;

        PathFixtureResult r = Assert.Single(back.Results);
        Assert.Equal((result.Path, result.Grid, result.Outcome), (r.Path, r.Grid, r.Outcome));
        Assert.Equal(capture.Cells, r.Captures.Single().Cells);
        Assert.Equal(capture.Cursor, r.Captures.Single().Cursor);
        Assert.Equal(["a failed check"], r.Captures.Single().Failures);
        Assert.Equal(cmp.FirstMismatches, r.Comparisons.Single().FirstMismatches);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Results\":[{\"Grid\":\"80x25\"}]}")]
    public void CorruptChildJsonIsRejected(string json) =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<ConsoleHostPath.ChildDocument>(json, ConsoleHostPath.Json));

    [Theory]
    [InlineData("80")]
    [InlineData("0x25")]
    [InlineData("80x-1")]
    [InlineData("axb")]
    public void InvalidGridsAreRejected(string text) => Assert.Throws<FormatException>(() => GridSize.Parse(text));

    [Fact]
    public void UnknownFixturesAreRejected() =>
        Assert.Throws<ArgumentException>(() => Fixtures.Steps("no-such-fixture", new GridSize(80, 25)));

    [Fact]
    public void TheExpectedScreenClipsWritesOutsideTheGrid()
    {
        var screen = new LogicalScreen(new GridSize(4, 2), new TScreenChar('.', 0x07));
        screen.Apply(new CellWrite(2, 1, [new('a', 1), new('b', 1), new('c', 1), new('d', 1)]));
        screen.Apply(new CellWrite(0, 5, [new('z', 1)]));
        screen.Apply(new CellWrite(-1, 0, [new('y', 1), new('x', 1)]));
        Assert.Equal("x...", string.Concat(Enumerable.Range(0, 4).Select(x => screen.Semantic(x, 0).Text)));
        Assert.Equal("..ab", string.Concat(Enumerable.Range(0, 4).Select(x => screen.Semantic(x, 1).Text)));
    }

    [Fact]
    public void FixturesFitEveryGrid()
    {
        foreach (string f in Fixtures.All)
            foreach (GridSize g in Fixtures.DefaultGrids)
                foreach (FixtureStep step in Fixtures.Steps(f, g))
                {
                    GridSize grid = step.ResizeTo ?? g;
                    Assert.All(step.Writes, w => Assert.True(w.Y >= 0 && w.Y < grid.Height && w.X >= 0 && w.X + w.Cells.Length <= grid.Width,
                        $"{f}/{step.Name}@{grid}: write ({w.X},{w.Y}) len {w.Cells.Length} leaves the grid"));
                }
    }
}

/// <summary>Linux: the real driver initialised on a PTY (raw mode, alternate screen, mouse modes, restore).</summary>
[Collection(DriverCollection.Name)]
public sealed class TerminalPtySmokeTests
{
    [SkippableFact]
    public void RealTerminalDriverOnAPtyMatchesAndRestoresTheTerminal()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "PTY smoke runs on Linux only");
        PtySmokeResult r = QualificationRunner.RunPtySmoke(new GridSize(80, 25));
        Skip.If(r.Outcome == Outcomes.Unavailable, r.Detail);
        Assert.True(r.Outcome == Outcomes.Match,
            $"{r.Outcome}: {r.Detail}; {string.Join("; ", r.Checks)}; {string.Join("; ", r.Comparison?.Failures ?? [])}; " +
            string.Join("; ", r.OracleDiagnostics.Take(5)));
    }
}
