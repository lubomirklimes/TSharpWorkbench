using System.Text.Json;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Logical-screen comparison, explicit normalization and baseline governance. The engine tests use synthetic screens
// (every "UI mutation" here is a change to test data, never to production rendering); the process test drives the sample
// target through the real runner twice, with different per-execution fixture identifiers, against a scratch baseline store.
[Collection("NonParallel")]
public sealed class TestWorkbenchVisualTests
{
    private const byte Text = 0x1F, Frame = 0x1E, Shadow = ScreenComparer.ShadowAttribute;
    private const string IdA = "0123456789abcdef0123456789abcdef", IdB = "fedcba9876543210fedcba9876543210";
    // The Commander rule: six or more hex digits before "\fi"/"/fi", or any number directly after the ".." truncation mark.
    private const string FixturePattern = @"(?:(?<=\.\.)(?<id>[0-9a-f]{1,32})|(?<![0-9a-f])(?<id>[0-9a-f]{6,32}))(?=[\\/]fi)";

    private static ScreenSnapshot Screen(int width, int height, params string[] rows)
    {
        var cells = new TScreenChar[width * height];
        for (int i = 0; i < cells.Length; i++) cells[i] = new(' ', 0x17);
        for (int y = 0; y < rows.Length; y++)
        for (int x = 0; x < rows[y].Length && x < width; x++) cells[y * width + x] = new(rows[y][x], y == 0 || rows[y][x] is '║' or '═' ? Frame : Text);
        return new(width, height, cells);
    }

    private static ScreenSnapshot With(ScreenSnapshot screen, params (int X, int Y, char? Ch, byte? Attr)[] changes)
    {
        var cells = new TScreenChar[screen.Width * screen.Height];
        for (int y = 0; y < screen.Height; y++) for (int x = 0; x < screen.Width; x++) cells[y * screen.Width + x] = screen[x, y];
        foreach (var (x, y, ch, attr) in changes)
        {
            var old = cells[y * screen.Width + x];
            cells[y * screen.Width + x] = new(ch ?? old.Character, attr ?? (byte)old.Attr);
        }
        return new(screen.Width, screen.Height, cells);
    }

    private static readonly string[] Dialog =
    [
        "  ≡  File  Window  Help        □1 ►0",
        "╔═[■]══ Žluťoučký kůň ═══════╗",
        "║  Název: příliš.txt         ║",
        "║   [ OK ]   [ Cancel ]      ║",
        "╚════════════════════════════╝",
    ];

    // ── comparison engine ──────────────────────────────────────────────────

    [Fact]
    public void IdenticalScreensCompareEqualIncludingUnicodeAndSurviveTvs1RoundTrip()
    {
        var a = Screen(40, 8, Dialog);
        var b = ScreenSnapshot.Deserialize(a.Serialize());
        var result = ScreenComparer.Compare(a, b);
        Assert.True(result.Equal);
        Assert.Equal((0, 320), (result.DifferingCells, result.ComparedCells));
        Assert.Null(result.FirstDifference);
        Assert.Empty(result.Differences); Assert.Empty(result.Regions);
        Assert.Equal('Ž', b[8, 1].Character); Assert.Equal('ů', b[19, 1].Character); // code units preserved, no normalization
        // A precomposed letter and its decomposed form are different cells: no lossy string normalization.
        var decomposed = With(a, (8, 1, 'Z', null));
        Assert.False(ScreenComparer.Compare(a, decomposed).Equal);
        // Repeated comparisons are deterministic, also at the larger standard grid.
        var large = Screen(140, 40, Dialog);
        Assert.True(ScreenComparer.Compare(large, ScreenSnapshot.Deserialize(large.Serialize())).Equal);
        Assert.Equal(ScreenComparer.Compare(a, decomposed).Differences, ScreenComparer.Compare(a, decomposed).Differences, new DifferenceComparer());
    }

    [Fact]
    public void OneAlteredCharacterIsReportedAtItsExactCellWithCodeUnitsAndAttributes()
    {
        var a = Screen(40, 8, Dialog);
        var b = With(a, (19, 1, 'u', null)); // "kůň" -> "kuň"
        var r = ScreenComparer.Compare(a, b);
        Assert.False(r.Equal);
        Assert.Equal((1, 1, 0), (r.DifferingCells, r.GlyphDifferences, r.AttributeDifferences));
        var d = Assert.Single(r.Differences);
        Assert.Equal((19, 1, true, false, false), (d.X, d.Y, d.Glyph, d.Foreground, d.Background));
        Assert.Equal(("U+016F", "ů", "U+0075", "u"), (d.Expected.Code, d.Expected.Text, d.Actual.Code, d.Actual.Text));
        Assert.Equal(("0x1F", 15, 1), (d.Expected.Attribute, d.Expected.Foreground, d.Expected.Background));
        Assert.Same(r.FirstDifference, r.Differences[0]);
        Assert.Equal(new DifferenceRegion(19, 1, 1, 1, 1, 1, 0), Assert.Single(r.Regions));
    }

    [Fact]
    public void ForegroundBackgroundShadowAndBorderChangesAreEachDetected()
    {
        var a = With(Screen(40, 8, Dialog), (30, 2, null, Shadow), (30, 3, null, Shadow));
        var fg = ScreenComparer.Compare(a, With(a, (3, 2, null, 0x1E)));
        Assert.Equal((1, 0, 1, 1, 0), (fg.DifferingCells, fg.GlyphDifferences, fg.AttributeDifferences, fg.ForegroundDifferences, fg.BackgroundDifferences));
        var bg = ScreenComparer.Compare(a, With(a, (3, 2, null, 0x2F)));
        Assert.Equal((1, 1, 0, 1), (bg.DifferingCells, bg.AttributeDifferences, bg.ForegroundDifferences, bg.BackgroundDifferences));
        Assert.Equal(("0x1F", "0x2F", 1, 2), (bg.Differences[0].Expected.Attribute, bg.Differences[0].Actual.Attribute, bg.Differences[0].Expected.Background, bg.Differences[0].Actual.Background));
        // A shadow cell keeps its glyph and loses the shadow attribute.
        var shadow = Assert.Single(ScreenComparer.Compare(a, With(a, (30, 3, null, 0x17))).Differences);
        Assert.Contains("shadow", shadow.Hints);
        // A double border becomes single at one cell.
        var border = Assert.Single(ScreenComparer.Compare(a, With(a, (10, 4, '─', null))).Differences);
        Assert.Equal((10, 4, true), (border.X, border.Y, border.Glyph));
        Assert.Contains("border", border.Hints);
    }

    [Fact]
    public void MovedLabelsClippingAndDimensionChangesAreDetectedAndExplained()
    {
        var a = Screen(40, 8, Dialog);
        var moved = Screen(40, 8, Dialog[0], Dialog[1], "║   Název: příliš.txt        ║", Dialog[3], Dialog[4]);
        var m = ScreenComparer.Compare(a, moved);
        Assert.False(m.Equal);
        var observation = Assert.Single(m.Observations);
        Assert.Equal((2, "moved", 1), (observation.Y, observation.Kind, observation.Shift));

        var clipped = Screen(40, 8, Dialog[0], Dialog[1], Dialog[2], "║   [ OK ]   [ Canc          ║", Dialog[4]);
        var c = ScreenComparer.Compare(a, clipped);
        Assert.Equal("content-missing", Assert.Single(c.Observations).Kind);
        Assert.All(c.Differences, d => Assert.Contains("content-to-blank", d.Hints));

        var shorter = Screen(40, 7, Dialog);
        var s = ScreenComparer.Compare(a, shorter);
        Assert.False(s.DimensionsMatch); Assert.False(s.Equal);
        Assert.Equal((40, 8, 40, 7, 0), (s.ExpectedWidth, s.ExpectedHeight, s.ActualWidth, s.ActualHeight, s.DifferingCells));
        Assert.False(ScreenComparer.Compare(Screen(80, 25, Dialog), Screen(81, 25, Dialog)).Equal);
        // Every changed cell is counted even when the listed differences are bounded.
        var many = ScreenComparer.Compare(Screen(40, 8), Screen(40, 8, Dialog), limit: 5);
        Assert.Equal(5, many.Differences.Length); Assert.True(many.DifferencesTruncated); Assert.True(many.DifferingCells > 100);
    }

    // ── normalization ──────────────────────────────────────────────────────

    private static VisualNormalizationRule FixtureRule(int min = 1, int max = 3, int maxCells = 96, params VisualRegion[] regions) =>
        new("fixture-id", NormalizationKinds.RunFixtureId, "per-run fixture identifier in the header and prompt", FixturePattern,
            regions.Length > 0 ? regions : [new(0, 1, 0, 1), new(0, -2, 0, 1)], min, max, maxCells);

    private static ScreenSnapshot Panels(string id, int width = 80, int height = 25)
    {
        string tail = id[^15..];
        return Screen(width, height, ["  ≡  File  Panel  Window        □1 ►0",
            $"╔═[■] ..{tail}\\fixture\\source ╗┌ ..{id[^6..]}\\fixture\\destination ─┐",
            "║ Name↑   gallery-file.txt          ║", .. Enumerable.Repeat("", height - 5), $"{id[^10..]}\\fixture\\source>", " F1 Help"]);
    }

    [Fact]
    public void FixtureIdentifierNormalizationMasksOnlyValidatedIdentifierGlyphs()
    {
        var rule = FixtureRule();
        var a = VisualNormalizer.Apply(Panels(IdA), [rule], new(IdA));
        var b = VisualNormalizer.Apply(Panels(IdB), [rule], new(IdB));
        Assert.Null(a.Error); Assert.Null(b.Error);
        Assert.Equal(15 + 6 + 10, a.MaskedCells);
        var applied = Assert.Single(a.Applied);
        Assert.Equal(new[] { new[] { 8, 1, 15 }, [44, 1, 6], [0, 23, 10] }, applied.Spans);
        Assert.True(ScreenComparer.Compare(a.Canonical!, b.Canonical!).Equal);
        Assert.Equal(VisualNormalizer.Placeholder, a.Canonical![8, 1].Character);
        Assert.Equal('\\', a.Canonical[23, 1].Character); // the path text around the identifier stays exact
        Assert.False(ScreenComparer.Compare(Panels(IdA), Panels(IdB)).Equal); // without the rule the runs differ

        // Attributes of masked cells are still compared; so is everything outside the identifier.
        var colored = VisualNormalizer.Apply(With(Panels(IdB), (9, 1, null, 0x4F)), [rule], new(IdB));
        Assert.Equal(1, ScreenComparer.Compare(a.Canonical, colored.Canonical!).AttributeDifferences);
        var renamed = VisualNormalizer.Apply(With(Panels(IdB), (32, 1, 'S', null)), [rule], new(IdB)); // "\Source"
        Assert.Equal(1, ScreenComparer.Compare(a.Canonical, renamed.Canonical!).GlyphDifferences);
        // A different context identifier is the same as an unrelated hex string: refused, never masked.
        Assert.Contains("is not a tail of this execution's fixture identifier", VisualNormalizer.Apply(Panels(IdA), [rule], new(IdB)).Error);
        // Narrow header: a 3-digit tail right after the ".." mark is masked; short hex in ordinary names ("space\file") is not.
        var narrow = VisualNormalizer.Apply(Screen(80, 25, "", "┌ ..def\\fixture\\destination ─┐", "", "", "space\\file.txt", ""), [FixtureRule(1, 1, 8, new VisualRegion(0, 1, 0, 4))], new(IdA));
        Assert.Null(narrow.Error);
        Assert.Equal(new[] { new[] { 4, 1, 3 } }, Assert.Single(narrow.Applied).Spans);
        // Six hex digits from the middle of the identifier are not the displayed tail: refused.
        Assert.Contains("is not a tail", VisualNormalizer.Apply(Screen(80, 25, "", "x " + IdA[4..12] + "\\fixture"), [FixtureRule(1, 1, 8, new VisualRegion(0, 1, 0, 1))], new(IdA)).Error);
        Assert.Contains("unavailable", VisualNormalizer.Apply(Panels(IdA), [rule], new((string?)null)).Error);
        Assert.Equal(IdA, NormalizationContext.FromFixture(Path.Combine(Path.GetTempPath(), TempRoots.Prefix + IdA, "fixture")).FixtureId);
        Assert.Null(NormalizationContext.FromFixture(Path.GetTempPath()).FixtureId);
    }

    [Fact]
    public void NormalizationRefusesUnsafeOrNonApplyingRulesInsteadOfMasking()
    {
        var screen = Panels(IdA);
        Assert.Contains("1..1", VisualNormalizer.Apply(screen, [FixtureRule(1, 1)], new(IdA)).Error); // three matches, one declared
        Assert.Contains("expected 4..4", VisualNormalizer.Apply(screen, [FixtureRule(4, 4)], new(IdA)).Error); // a dynamic field disappeared
        Assert.Contains("maxCells", VisualNormalizer.Apply(screen, [FixtureRule(maxCells: 20)], new(IdA)).Error);
        Assert.Contains("outside", VisualNormalizer.Apply(screen, [FixtureRule(regions: new VisualRegion(70, 1, 20, 1))], new(IdA)).Error);
        Assert.Contains("more than 25%", VisualNormalizer.Apply(screen, [FixtureRule(regions: new VisualRegion(0, 0, 0, 0))], new(IdA)).Error);
        Assert.Contains("no region for grid 80x25", VisualNormalizer.Apply(screen, [FixtureRule(regions: new VisualRegion(0, 1, 0, 1, ["140x40"]))], new(IdA)).Error);
        var everyLetter = new VisualNormalizationRule("letters", NormalizationKinds.Pattern, "would hide a dialog", "(?<dynamic>[^ ]+)", [new(0, 1, 0, 6)], 0, 32, 400);
        Assert.Contains("of the screen", VisualNormalizer.Apply(Screen(40, 25, Dialog), [everyLetter], new((string?)null)).Error);
        // Rule configuration errors are found before any screen.
        Assert.Contains(VisualNormalizer.Validate(FixtureRule() with { Pattern = "(?<other>x)" }), e => e.Contains("(?<id>"));
        Assert.Contains(VisualNormalizer.Validate(FixtureRule() with { Pattern = "(" }), e => e.Contains("invalid pattern"));
        Assert.Contains(VisualNormalizer.Validate(FixtureRule() with { Kind = "everything" }), e => e.Contains("unknown kind"));
        Assert.Contains(VisualNormalizer.Validate(FixtureRule() with { Regions = [] }), e => e.Contains("regions"));
        Assert.Contains(VisualNormalizer.Validate(FixtureRule() with { MaxCells = 5000 }), e => e.Contains("maxCells"));
        Assert.Empty(VisualNormalizer.Validate(FixtureRule()));
        // A generic counter rule: only its digits, still layout-exact.
        var counter = new VisualNormalizationRule("clock", NormalizationKinds.Pattern, "clock seconds", @"\d\d:\d\d:(?<dynamic>\d\d)", [new(-9, 0, 9, 1)]);
        var t1 = VisualNormalizer.Apply(Screen(40, 3, new string(' ', 31) + "12:00:01"), [counter], new((string?)null));
        var t2 = VisualNormalizer.Apply(Screen(40, 3, new string(' ', 31) + "12:00:59"), [counter], new((string?)null));
        Assert.True(ScreenComparer.Compare(t1.Canonical!, t2.Canonical!).Equal);
        Assert.False(ScreenComparer.Compare(t1.Canonical!, VisualNormalizer.Apply(Screen(40, 3, new string(' ', 31) + "12:01:59"), [counter], new((string?)null)).Canonical!).Equal);
    }

    // ── manifest, store and governance ──────────────────────────────────────

    private const string ScenarioId = "demo/panels", Checkpoint = "desktop";

    private static string Manifest(string root, string? patch = null, string format = VisualBaselineSet.ManifestFormat)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "visual-baselines.json");
        File.WriteAllText(path, patch ?? JsonSerializer.Serialize(new
        {
            format,
            normalizations = new[] { FixtureRule() },
            baselines = new[] { new VisualBaselineEntry(ScenarioId, Checkpoint, ["80x25"], "dual panels", ["fixture-id"], "two owned directories") },
        }, ScenarioRunner.Json));
        return path;
    }

    private static readonly ScenarioDescriptor Demo = new(ScenarioId, "Demo", "demo", "demo", ScenarioGrid.Standard, [new("checkpoint", Checkpoint)]);
    private static readonly TargetIdentity Identity = new("Demo", "1.0", null, null, null, "NullDriver", 80, 25);

    private static async Task<VisualCheckpointResult> Evaluate(VisualBaselineSet set, string run, ScreenSnapshot screen, string id)
    {
        Directory.CreateDirectory(run);
        string stem = VisualBaselineSet.Key(ScenarioId, Checkpoint, "80x25");
        File.WriteAllBytes(Path.Combine(run, stem + ".tvs1"), screen.Serialize());
        return (await new VisualGate(set, run).EvaluateAsync(Demo, Checkpoint, "80x25", "80x25", screen, stem, new(id), Identity))!;
    }

    private static string Scratch() => Path.Combine(Path.GetTempPath(), "tsv-wb-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingBaselineIsNeverAPassAndApprovalIsExplicitTargetedAndGuarded()
    {
        string root = Scratch();
        try
        {
            var set = VisualBaselineSet.Load(Manifest(Path.Combine(root, "baselines")));
            string key = VisualBaselineSet.Key(ScenarioId, Checkpoint, "80x25"), run1 = Path.Combine(root, "run1");
            var missing = await Evaluate(set, run1, Panels(IdA), IdA);
            Assert.Equal(VisualStatuses.BaselineMissing, missing.Status);
            Assert.Equal(key + ".candidate.tvs1", missing.Candidate);
            Assert.False(Directory.Exists(set.ApprovedDirectory)); // nothing approved by capturing
            var gate = new VisualGate(set, Path.Combine(root, "empty")); Directory.CreateDirectory(Path.Combine(root, "empty"));
            Assert.False(gate.Finish([]).Passed); // nothing evaluated is not a pass
            var candidate = JsonSerializer.Deserialize<VisualBaselineFile>(File.ReadAllBytes(Path.Combine(run1, missing.CandidateMetadata!)), VisualBaselineSet.Strict)!;
            Assert.Equal(("candidate", null, "80x25", "80x25"), (candidate.Status, candidate.ReplacesSha256, candidate.Grid, candidate.InitialGrid));
            string metadataText = File.ReadAllText(Path.Combine(run1, missing.CandidateMetadata!));
            Assert.DoesNotContain(IdA[^10..], metadataText); Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/'), metadataText);
            using (var diff = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run1, missing.Diff!))))
                Assert.Equal("fixture-id", diff.RootElement.GetProperty("normalizations")[0].GetProperty("rule").GetString());

            Assert.Contains("--confirm approve-1", Assert.Throws<ArgumentException>(() => set.Approve(run1, [key], null, false)).Message);
            Assert.Contains("--confirm approve-1", Assert.Throws<ArgumentException>(() => set.Approve(run1, [key], "approve-2", false)).Message);
            Assert.Contains("not a baseline key", Assert.Throws<ArgumentException>(() => set.Approve(run1, ["../x"], "approve-1", false)).Message);
            Assert.Contains("No candidate", Assert.Throws<ArgumentException>(() => set.Approve(run1, ["demo-other--desktop--80x25"], "approve-1", false)).Message);
            Assert.False(Directory.Exists(set.ApprovedDirectory));
            Assert.Equal(new[] { key }, set.Approve(run1, [key], "approve-1", false));
            Assert.Equal(("Approved", key), (set.Status().Single().Status, set.Status().Single().Key));
            byte[] approved = File.ReadAllBytes(set.ApprovedCells(key));

            // A second execution with another fixture identifier: equal after the rule.
            var match = await Evaluate(set, Path.Combine(root, "run2"), Panels(IdB), IdB);
            Assert.Equal(VisualStatuses.Match, match.Status);
            Assert.Null(match.Candidate); Assert.Null(match.DiffPng);

            // Negative control: a deliberate UI mutation in the synthetic capture fails the gate and changes no baseline.
            string run3 = Path.Combine(root, "run3");
            var mismatch = await Evaluate(set, run3, With(Panels(IdB), (13, 2, 'G', null), (2, 2, null, 0x70)), IdB);
            Assert.Equal((VisualStatuses.Mismatch, 2, 1, 1), (mismatch.Status, mismatch.DifferingCells, mismatch.GlyphDifferences, mismatch.AttributeDifferences));
            Assert.Contains("first at (13,2)", mismatch.Reason);
            Assert.Equal(approved, File.ReadAllBytes(set.ApprovedCells(key)));
            byte[] png = File.ReadAllBytes(Path.Combine(run3, mismatch.DiffPng!));
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, png[..4]);
            Assert.Equal((80 * 3 + 2) * LogicalPng.CellWidth, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
            // Replacing an approved baseline needs --replace and the candidate must have been compared against exactly it.
            Assert.Contains("--replace", Assert.Throws<ArgumentException>(() => set.Approve(run3, [key], "approve-1", false)).Message);
            Assert.Contains("needs --replace", Assert.Throws<ArgumentException>(() => set.Approve(run1, [key], "approve-1", false)).Message);
            Assert.Contains("changed since", Assert.Throws<ArgumentException>(() => set.Approve(run1, [key], "approve-1", true)).Message);
            Assert.Equal(approved, File.ReadAllBytes(set.ApprovedCells(key)));
            Assert.Equal(new[] { key }, set.Approve(run3, [key], "approve-1", true));
            Assert.NotEqual(approved, File.ReadAllBytes(set.ApprovedCells(key)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CorruptUnsupportedOrStaleBaselinesAndBadManifestsFailClearly()
    {
        string root = Scratch();
        try
        {
            string manifest = Manifest(Path.Combine(root, "baselines"));
            var set = VisualBaselineSet.Load(manifest);
            string key = VisualBaselineSet.Key(ScenarioId, Checkpoint, "80x25"), run = Path.Combine(root, "run1");
            await Evaluate(set, run, Panels(IdA), IdA);
            set.Approve(run, [key], "approve-1", false);
            string cells = set.ApprovedCells(key), meta = set.ApprovedMetadata(key);
            byte[] goodCells = File.ReadAllBytes(cells); string goodMeta = File.ReadAllText(meta);

            byte[] corrupt = (byte[])goodCells.Clone(); corrupt[200] ^= 0x01;
            File.WriteAllBytes(cells, corrupt);
            var r = await Evaluate(set, Path.Combine(root, "run2"), Panels(IdB), IdB);
            Assert.Equal(VisualStatuses.BaselineInvalid, r.Status); Assert.Contains("integrity", r.Reason);
            Assert.NotNull(r.Candidate); // a replacement can still be reviewed

            File.WriteAllBytes(cells, goodCells);
            File.WriteAllText(meta, goodMeta.Replace(VisualBaselineSet.BaselineFormat, "tsharpvision.visual-baseline/9"));
            Assert.Contains("Unsupported baseline format version", (await Evaluate(set, Path.Combine(root, "run3"), Panels(IdB), IdB)).Reason);
            File.WriteAllText(meta, "{ not json");
            Assert.Equal(VisualStatuses.BaselineInvalid, (await Evaluate(set, Path.Combine(root, "run4"), Panels(IdB), IdB)).Status);
            File.WriteAllText(meta, goodMeta);
            File.WriteAllBytes(cells, [.. "TVS1"u8, 1, 2, 3]);
            Assert.Equal(VisualStatuses.BaselineInvalid, (await Evaluate(set, Path.Combine(root, "run5"), Panels(IdB), IdB)).Status);
            File.WriteAllBytes(cells, goodCells);
            Assert.Equal(VisualStatuses.Match, (await Evaluate(set, Path.Combine(root, "run6"), Panels(IdB), IdB)).Status);

            // Normalization error at capture: the field moved out of the declared region.
            var lost = await Evaluate(set, Path.Combine(root, "run7"), Screen(80, 25, "x"), IdB);
            Assert.Equal(VisualStatuses.NormalizationError, lost.Status); Assert.Null(lost.Candidate);

            // A rule edited after approval invalidates the baseline until it is re-reviewed.
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"maxCells\": 96", "\"maxCells\": 97"));
            var edited = VisualBaselineSet.Load(manifest);
            Assert.Contains("rules changed", (await Evaluate(edited, Path.Combine(root, "run8"), Panels(IdB), IdB)).Reason);

            // Manifest configuration errors are refused before anything runs.
            string bad = Path.Combine(root, "bad");
            Assert.Contains("unsupported manifest format", Assert.Throws<VisualBaselineConfigurationException>(() => VisualBaselineSet.Load(Manifest(bad, format: "tsharpvision.visual-baselines/2"))).Message);
            string text = File.ReadAllText(manifest);
            Assert.Contains("unknown normalization rule", Load(bad, JsonSerializer.Serialize(new
            {
                format = VisualBaselineSet.ManifestFormat, normalizations = new[] { FixtureRule() },
                baselines = new[] { new VisualBaselineEntry(ScenarioId, Checkpoint, ["80x25"], "dual panels", ["nope"]) },
            }, ScenarioRunner.Json)));
            Assert.Contains("duplicate rule id", Load(bad, JsonSerializer.Serialize(new
            {
                format = VisualBaselineSet.ManifestFormat, normalizations = new[] { FixtureRule(), FixtureRule() },
                baselines = new[] { new VisualBaselineEntry(ScenarioId, Checkpoint, ["80x25"], "dual panels", ["fixture-id"]) },
            }, ScenarioRunner.Json)));
            Assert.Contains("invalid grid key", Load(bad, text.Replace("\"80x25\"", "\"80x25-from-80x25\"")));
            Assert.Contains("not valid", Load(bad, text.Replace("\"purpose\"", "\"purpse\"")));
            Assert.Contains("(?<id>", Load(bad, text.Replace("(?<id>", "(?<x>")));
            Assert.Throws<VisualBaselineConfigurationException>(() => VisualBaselineSet.Load(Path.Combine(bad, "absent.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class DifferenceComparer : IEqualityComparer<CellDifference>
    {
        public bool Equals(CellDifference? a, CellDifference? b) => a is not null && b is not null && (a.X, a.Y, a.Glyph, a.Foreground, a.Background, a.Expected, a.Actual)
            == (b.X, b.Y, b.Glyph, b.Foreground, b.Background, b.Expected, b.Actual) && a.Hints.SequenceEqual(b.Hints);
        public int GetHashCode(CellDifference d) => HashCode.Combine(d.X, d.Y);
    }

    private static string Load(string dir, string text) =>
        Assert.Throws<VisualBaselineConfigurationException>(() => VisualBaselineSet.Load(Manifest(dir, text))).Message;

    [Fact]
    public async Task CliApprovalNeedsExplicitCandidatesAndConfirmationAndRefusesBadManifestsBeforeRunning()
    {
        string root = Scratch();
        try
        {
            string manifest = Manifest(Path.Combine(root, "baselines"));
            string key = VisualBaselineSet.Key(ScenarioId, Checkpoint, "80x25"), run = Path.Combine(root, "run1");
            await Evaluate(VisualBaselineSet.Load(manifest), run, Panels(IdA), IdA);
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await Cli.RunAsync(["baseline", "status", "--baselines", manifest], output, error));
            Assert.Contains("BaselineMissing\t" + key, output.ToString());
            Assert.Equal(2, await Cli.RunAsync(["baseline", "approve", "--baselines", manifest, "--run", run, "--candidate", key], output, error));
            Assert.Equal(2, await Cli.RunAsync(["baseline", "approve", "--baselines", manifest, "--run", run, "--confirm", "approve-1"], output, error));
            Assert.Equal(2, await Cli.RunAsync(["run", "--target", "x", "--all", "--output", root, "--replace"], output, error));
            Assert.Equal(2, await Cli.RunAsync(["baseline", "status", "--baselines", manifest, "--run", run], output, error));
            Assert.False(Directory.Exists(Path.Combine(root, "baselines", VisualBaselineSet.ApprovedFolder)));
            Assert.Equal(0, await Cli.RunAsync(["baseline", "approve", "--baselines", manifest, "--run", run, "--candidate", key, "--confirm", "approve-1"], output, error));
            Assert.Contains("Approved: approved/" + key + ".tvs1", output.ToString());
            // An invalid manifest is refused (exit 2) before any target is started.
            File.WriteAllText(manifest, "{}");
            error = new StringWriter();
            Assert.Equal(2, await Cli.RunAsync(["run", "--target", TestWorkbenchHostTests.SampleTarget, "--scenario", "sample/fixture/path", "--size", "60x20",
                "--output", Path.Combine(root, "out"), "--baselines", manifest], output, error));
            Assert.Contains("Baseline manifest is not valid", error.ToString());
            Assert.False(Directory.Exists(Path.Combine(root, "out")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // ── real pipeline ──────────────────────────────────────────────────────

    [Fact]
    public async Task SampleFixturePathIsGatedAcrossExecutionsWithDifferentFixtureIdentifiers()
    {
        string root = Scratch();
        try
        {
            const string id = "sample/fixture/path";
            string manifest = Manifest(Path.Combine(root, "baselines"), JsonSerializer.Serialize(new
            {
                format = VisualBaselineSet.ManifestFormat,
                normalizations = new[] { new VisualNormalizationRule("fixture-id", NormalizationKinds.RunFixtureId, "run id in the path line",
                    FixturePattern, [new(0, 3, 0, 3)], 1, 1, 32) },
                baselines = new[] { new VisualBaselineEntry(id, "path-shown", ["60x20", "80x25"], "fixture path window", ["fixture-id"]) },
            }, ScenarioRunner.Json));
            var runner = new ScenarioRunner();
            var selected = ScenarioSelection.Resolve(await runner.DiscoverAsync(TestWorkbenchHostTests.SampleTarget), [id], null, false);
            var options = new RunOptions(Path.Combine(root, "out"), ["60x20"], Baselines: manifest);

            var first = await runner.RunAsync(TestWorkbenchHostTests.SampleTarget, selected, options);
            Assert.Equal(ScenarioOutcomes.Passed, Assert.Single(first.Results).Outcome);
            Assert.Equal(BatchResult.VisualGateFailed, first.ExitCode); // missing baseline: approval required, never a pass
            Assert.Equal(1, first.Visual!.Count(VisualStatuses.BaselineMissing));
            Assert.Equal(1, first.Visual.Count(VisualStatuses.NotRun)); // 80x25 was not part of this run
            string key = VisualBaselineSet.Key(id, "path-shown", "60x20");
            Assert.True(File.Exists(Path.Combine(first.RunRoot, key + ".candidate.png")));
            Assert.True(File.Exists(Path.Combine(first.RunRoot, "visual.json")));
            Assert.Contains("## Visual baselines", File.ReadAllText(first.Report!));
            Assert.Empty(Directory.GetDirectories(first.RunRoot));
            var set = VisualBaselineSet.Load(manifest);
            set.Approve(first.RunRoot, [key], "approve-1", false);

            var second = await runner.RunAsync(TestWorkbenchHostTests.SampleTarget, selected, options);
            Assert.Equal(0, second.ExitCode);
            var visual = second.Results[0].Checkpoints.Single(c => c.Checkpoint == "path-shown").Visual!;
            Assert.Equal((VisualStatuses.Match, 1), (visual.Status, visual.Normalizations.Length));
            Assert.InRange(visual.MaskedCells, 6, 32);
            Assert.Null(second.Results[0].Checkpoints.Single(c => c.Checkpoint == "closed").Visual); // not gated

            // Negative control on the real capture: change one approved cell (re-hashed so the file is intact) -> Mismatch.
            var approved = ScreenSnapshot.Deserialize(File.ReadAllBytes(set.ApprovedCells(key)));
            Assert.Equal('ř', approved[5, 5].Character);
            var mutated = With(approved, (5, 5, 'X', null)).Serialize();
            File.WriteAllBytes(set.ApprovedCells(key), mutated);
            File.WriteAllText(set.ApprovedMetadata(key), File.ReadAllText(set.ApprovedMetadata(key))
                .Replace(ScreenSnapshot.Hash(approved.Serialize()), ScreenSnapshot.Hash(mutated)));
            var third = await runner.RunAsync(TestWorkbenchHostTests.SampleTarget, selected, options);
            Assert.Equal(BatchResult.VisualGateFailed, third.ExitCode);
            var mismatch = third.Results[0].Checkpoints.Single(c => c.Checkpoint == "path-shown").Visual!;
            Assert.Equal((VisualStatuses.Mismatch, 1), (mismatch.Status, mismatch.DifferingCells));
            Assert.True(File.Exists(Path.Combine(third.RunRoot, mismatch.DiffPng!)));
            Assert.Equal(mutated, File.ReadAllBytes(set.ApprovedCells(key))); // the gate never rewrites the baseline

            // Legacy runs are unchanged: no visual files, no visual property.
            var legacy = await runner.RunAsync(TestWorkbenchHostTests.SampleTarget, selected, new RunOptions(Path.Combine(root, "out"), ["60x20"]));
            Assert.Equal(0, legacy.ExitCode); Assert.Null(legacy.Visual);
            Assert.Equal(2 * 3 + 2, Directory.GetFiles(legacy.RunRoot).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
