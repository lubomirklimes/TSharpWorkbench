using System.Text.Json;
using TSharpWorkbench.Testing;

// Private serialized copies prevent later catalog/project edits from changing an accepted run.
internal sealed class WorkbenchRunRequest
{
    private readonly string configuration, catalog;
    internal string Build { get; }
    internal string? BaselineManifestHash { get; }
    internal WorkbenchProject Project => JsonSerializer.Deserialize<WorkbenchProject>(configuration)!;
    internal ScenarioDescriptor[] Scenarios => JsonSerializer.Deserialize<ScenarioDescriptor[]>(catalog,ScenarioRunner.Json)!;
    internal WorkbenchRunRequest(WorkbenchProject project, ScenarioDescriptor[] scenarios, string build)
    {
        configuration=JsonSerializer.Serialize(project);catalog=JsonSerializer.Serialize(scenarios,ScenarioRunner.Json);Build=build;
        if(project.TestType=="visual"&&project.BaselineStore is not null)
            BaselineManifestHash=ScreenSnapshot.Hash(File.ReadAllBytes(project.BaselineStore));
    }
    internal RunOptions Options()
    {
        var p=Project;
        return new(p.OutputRoot,p.ScreenSizes,p.Inspect,p.Allow,p.Timeout,p.ScenarioTimeout,
            p.TestType=="visual"?p.BaselineStore:null){Driver=p.Mode,ProxyBackend=p.ProxyBackend,ProxyCapture=p.ProxyCapture,ProxyShutdownKey=p.ProxyShutdownKey,TargetArguments=p.TargetArguments,TargetEnvironment=p.TargetEnvironment};
    }
}
