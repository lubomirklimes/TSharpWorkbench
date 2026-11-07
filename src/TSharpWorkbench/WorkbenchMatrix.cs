using System.Text.Json;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;

internal static class WorkbenchMatrix
{
    internal sealed record Entry(string TestId, string Title, string ScenarioId, EnvironmentDefinition Environment, string Description = "", string Preconditions = "", ScenarioDescriptor? ScenarioSnapshot = null);
    internal sealed record Result(Entry Entry, string Outcome, string Reason, string? Report = null,
        string? ActualOs = null, string? ActualDriver = null, string? ActualCapture = null, string? ActualGrid = null, string? InitialGrid = null, string? Functional = null, string? Visual = null)
    {
        internal string Overall => Outcome == "Error" ? "Error" : Functional is null ? (Outcome == "Passed" && Reason.Contains("Visual:") ? "Visual failed" : Outcome) : Aggregate(Functional, Visual);
    }
    internal sealed record Run(string Id, DateTimeOffset Began, DateTimeOffset Ended, Result[] Results);

    internal static string Aggregate(string functional, string? visual) =>
        functional != "Passed" ? functional : visual is null or "Not requested" or "Match" ? "Passed" : "Visual failed";
    internal static string Summary(IEnumerable<Result> results) => string.Join(", ", results
        .Where(r => r.Outcome != "Not selected").GroupBy(r => r.Overall).Select(g => $"{g.Count()} {g.Key}"));

    internal static Entry[] Plan(IEnumerable<TestDefinition> tests, IEnumerable<EnvironmentDefinition> environments)
    {
        var profiles = environments.ToDictionary(e => e.Id, StringComparer.Ordinal);
        return tests.DistinctBy(t => t.Id).OrderBy(t => t.Id, StringComparer.Ordinal)
            .SelectMany(t => t.EnvironmentIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Where(id => profiles.TryGetValue(id, out var e) && e.Enabled)
                .Select(id => new Entry(t.Id, t.Title, t.ScenarioId, profiles[id], t.Description, t.Preconditions))).ToArray();
    }

    internal static string LocalOs => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsMacOS() ? "macos" : "unsupported";

    internal static async Task<Result> Execute(Entry entry, WorkbenchRunRequest request, ScenarioRunner runner,
        Action<string> progress, Func<BatchResult, string> archive, CancellationToken token)
    {
        var e = entry.Environment;
        if (token.IsCancellationRequested) return new(entry, "Aborted", "Cancelled before launch.");
        if (e.OperatingSystem != "host" && !e.OperatingSystem.Equals(LocalOs, StringComparison.OrdinalIgnoreCase))
            return new(entry, "Skipped", $"Requested {e.OperatingSystem}. This runner executes locally on {LocalOs}.");
        if (e.Driver == "Win32ConsoleDriver" && !OperatingSystem.IsWindows())
            return new(entry, "Skipped", "Win32ConsoleDriver requires Windows.");
        var scenario = request.Scenarios.FirstOrDefault(s => s.Id == entry.ScenarioId);
        if (scenario is null) return new(entry, "Blocked", "Scenario is unavailable or its fixture is unresolved. Refresh scenarios and check Scenario Manager.");
        string grid = $"{e.Width}x{e.Height}";
        if (!scenario.Grid.Supports(grid)) return new(entry, "Skipped", "Scenario does not support grid " + grid + ".");
        bool proxy = scenario.Backend == "proxy-logical";
        if (!proxy && e.Capture != ProxyCaptureKinds.None)
            return new(entry, "Skipped", "Proxy capture settings require Black-Box Mode.");
        if (proxy && e.Capture != ProxyCaptureKinds.None && !ProxyCaptureKinds.ForBackend(e.Driver).Contains(e.Capture))
            return new(entry, "Skipped", "Requested capture is unsupported by this Proxy driver.");
        var project = request.Project;
        if (project.TestType == "visual" && (project.VisualBehavior != "compare" || project.BaselineStore is null))
            return new(entry, "Blocked", "Configure a baseline store and Compare mode. Candidate generation remains in scenario runs.");
        try
        {
            if (WorkbenchDialog.BuildIdentity(project.Target) != request.Build)
                return new(entry, "Blocked", "Target build changed. Refresh scenarios before running.");
            if (request.BaselineManifestHash is { } expected &&
                ScreenSnapshot.Hash(File.ReadAllBytes(project.BaselineStore!)) != expected)
                return new(entry, "Blocked", "The baseline manifest changed after run configuration.");
            var options = request.Options() with
            {
                Sizes = [grid], Driver = proxy ? "NullDriver" : e.Driver,
                ProxyBackend = proxy ? e.Driver : "NullDriver",
                ProxyCapture = proxy ? e.Capture : ProxyCaptureKinds.None
            };
            var batch = await runner.RunAsync(project.Target, [scenario], options, progress, token);
            var result = batch.Results.Single();
            string? actualDriver = null, actualCapture = null, actualGrid = null, initialGrid = null;
            if (result.Checkpoints.FirstOrDefault() is { } checkpoint)
            {
                using var metadata = JsonDocument.Parse(File.ReadAllBytes(WorkbenchDialog.SafeArtifact(batch.RunRoot, checkpoint.Metadata)));
                if (metadata.RootElement.TryGetProperty("driver", out var driver))
                    actualDriver = driver.GetString();
                actualCapture = metadata.RootElement.TryGetProperty("proxyNative", out var capture) &&
                    capture.ValueKind == JsonValueKind.Object && capture.TryGetProperty("kind", out var kind) ? kind.GetString() : metadata.RootElement.TryGetProperty("captureLayer", out var layer) ? layer.GetString() : null;
                actualGrid = checkpoint.Size;
                if (metadata.RootElement.TryGetProperty("initialSize", out var initial)) initialGrid = initial.GetString();
            }
            string visualStatus = batch.Visual is null ? "Not requested" : batch.Visual.Passed ? "Match" : string.Join(", ", batch.Visual.Entries.Select(v => v.Status).Distinct());
            string overall = Aggregate(result.Outcome, visualStatus);
            string reason = result.Reason;
            if (batch.Visual is { Passed: false } visual)
                reason += " Visual: " + string.Join(", ", visual.Entries.Select(v => v.Status).Distinct());
            try
            {
                return new(entry, overall, reason, archive(batch), actualGrid is null ? null : LocalOs, actualDriver, actualCapture, actualGrid, initialGrid, result.Outcome, visualStatus);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                return new(entry, "Error", reason + " Evidence archive failed: " + ex.Message + " External evidence remains at " + batch.RunRoot,
                    null, actualGrid is null ? null : LocalOs, actualDriver, actualCapture, actualGrid, initialGrid, result.Outcome, visualStatus);
            }
        }
        catch (OperationCanceledException) { return new(entry, "Aborted", "Cancelled. Target cleanup finished."); }
        catch (Exception ex) { return new(entry, "Error", ex.Message); }
    }
}
