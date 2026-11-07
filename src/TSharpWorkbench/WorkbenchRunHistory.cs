using System.Text.Json;
using TSharpWorkbench.Testing;

internal static class WorkbenchRunHistory
{
    internal sealed record Loaded(BatchResult Batch,string Target,DateTimeOffset Began,TimeSpan Duration);
    internal static Loaded Load(string path)
    {
        path=Path.GetFullPath(path);
        if(Directory.Exists(path))throw new InvalidDataException("Select run.json inside a completed run directory, not the directory itself: "+path);
        if(Path.GetFileName(path)!="run.json")throw new InvalidDataException("Select run.json from a completed flat run directory.");
        if(!File.Exists(path))throw new InvalidDataException("Run metadata not found: "+path);
        if(new FileInfo(path).Length>32*1024*1024)throw new InvalidDataException("Run metadata exceeds 32 MiB.");
        string root=Path.GetDirectoryName(path)!;
        if(!File.Exists(Path.Combine(root,"report.md")))throw new InvalidDataException("Completed report.md is missing.");
        using var doc=JsonDocument.Parse(File.ReadAllBytes(path));var json=doc.RootElement;
        if(json.GetProperty("schema").GetString()!=Protocol.Schema)throw new InvalidDataException("Unknown run schema.");
        string driver=json.TryGetProperty("requestedDriver",out var d)?d.GetString()!:"NullDriver";WorkbenchCapabilities.Validate(driver);
        var results=json.GetProperty("results").Deserialize<ScenarioResult[]>(ScenarioRunner.Json)??throw new InvalidDataException("Missing results.");
        if(results.Length==0||results.Length>10000)throw new InvalidDataException("Invalid execution count.");
        foreach(var r in results)
        {
            ScenarioSelection.ParseSize(r.Size);
            if(string.IsNullOrWhiteSpace(r.ScenarioId)||r.Checkpoints is null||r.Reason is null||r.Outcome is not ("Passed" or "Failed" or "Blocked" or "Aborted" or "TimedOut" or "Error" or "Skipped"))throw new InvalidDataException("Invalid execution.");
            foreach(var c in r.Checkpoints)
            {
                if(c.Assertions is null||c.InspectionErrors is null)throw new InvalidDataException("Invalid checkpoint.");
                byte[] cells=File.ReadAllBytes(WorkbenchDialog.SafeArtifact(root,c.Cells));
                if(ScreenSnapshot.Hash(cells)!=c.CellsHash)throw new InvalidDataException("Checkpoint cells hash mismatch.");
                var snapshot=ScreenSnapshot.Deserialize(cells);
                if(ScenarioSelection.Format(snapshot.Width,snapshot.Height)!=c.Size)throw new InvalidDataException("Checkpoint geometry mismatch.");
                WorkbenchDialog.SafeArtifact(root,c.Metadata);
                string png=WorkbenchDialog.SafeArtifact(root,c.Png);
                if(new FileInfo(png).Length>32*1024*1024||ScreenSnapshot.Hash(File.ReadAllBytes(png))!=c.PngHash)throw new InvalidDataException("PNG integrity check failed.");
                if(c.Visual is {} v){if(v.Diff is {} diff)WorkbenchDialog.SafeArtifact(root,diff);if(v.Candidate is {} candidate)WorkbenchDialog.SafeArtifact(root,candidate);}
            }
        }
        VisualRunSummary? visual=null;
        if(json.TryGetProperty("visual",out var vj)&&vj.ValueKind==JsonValueKind.Object)
        {
            // Historical runner JSON deliberately stores only a manifest filename. Never guess an external store.
            // Keep statuses and paths for diagnostics; comparison/approval explains the missing store reference.
            var entries=vj.GetProperty("entries").Deserialize<VisualEntryResult[]>(ScenarioRunner.Json)??throw new InvalidDataException("Missing visual entries.");
            if(entries.Any(e=>e is null||e.Status is not (VisualStatuses.Match or VisualStatuses.Mismatch or VisualStatuses.BaselineMissing or VisualStatuses.BaselineInvalid or VisualStatuses.NormalizationError or VisualStatuses.NotCaptured or VisualStatuses.NotRun)))
                throw new InvalidDataException("Invalid visual status.");
            string manifest="";
            string requestPath=Path.Combine(root,"workbench-request.json");
            if(File.Exists(requestPath)){using var request=JsonDocument.Parse(File.ReadAllBytes(requestPath));if(request.RootElement.TryGetProperty("baselineStore",out var store))manifest=store.GetString()??"";}
            visual=new(manifest,entries);
        }
        var batch=new BatchResult(root,results){Driver=driver,Visual=visual};
        var began=json.GetProperty("began").GetDateTimeOffset();var ended=json.GetProperty("ended").GetDateTimeOffset();
        if(ended<began)throw new InvalidDataException("Invalid run timestamps.");
        return new(batch,json.GetProperty("target").GetString()??"Unknown target",began,ended-began);
    }
}
