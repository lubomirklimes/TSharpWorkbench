using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Tests.Core;

// Since Commander's product scenarios are fixture-only registrations, runnable only through the external
// JSON documents of CommanderExternal.twproj (IDs "external/commander/..."). The native, console and GPU tests keep
// their scenarios and assertions and select them through this catalog. The Commander project is only read; app-level tests
// get a portable copy beside their evidence, so marks and saves never touch the Commander checkout.
internal static class CommanderExternalCatalog
{
    internal const string ProjectVariable = "TSWB_COMMANDER_PROJECT";

    internal static string DefaultProject => Path.Combine(ExternalCompatTests.CommanderRoot,
        "diagnostics", "CommanderTestTarget", "ExternalScenarios", "CommanderExternal.twproj");

    internal static string ProjectPath => Environment.GetEnvironmentVariable(ProjectVariable) is { Length: > 0 } custom ? custom : DefaultProject;

    // The external execution identity of a product flow formerly compiled as "commander/<flow>".
    internal static string Id(string flow) => flow.StartsWith("external/", StringComparison.Ordinal) ? flow : "external/" + flow;

    internal static string[] Files()
    {
        string path = ProjectPath;
        if (!File.Exists(path)) throw new InvalidOperationException($"Set {ProjectVariable} to Commander's diagnostics/CommanderTestTarget/ExternalScenarios/CommanderExternal.twproj.");
        return WorkbenchProject.Load(path).ScenarioFiles;
    }

    internal static Task<ScenarioDescriptor[]> DiscoverAsync(ScenarioRunner runner, string target) => runner.DiscoverAsync(target, Files());

    internal static async Task<ScenarioDescriptor> SingleAsync(ScenarioRunner runner, string target, string flow) =>
        (await DiscoverAsync(runner, target)).Single(s => s.Id == Id(flow));

    internal static (WorkbenchProject Project, string Path) Project(string folder, string target, string output, params string[] selected)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "commander-external.twproj");
        (WorkbenchProject.Load(ProjectPath) with { Target = target, OutputRoot = output, SelectedIds = selected.Select(Id).ToArray() }).Save(path);
        return (WorkbenchProject.Load(path), path);
    }
}
