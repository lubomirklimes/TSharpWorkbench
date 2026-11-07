using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// Prepare target in the Workspace. Opening the dialog, applying a prepared copy to the project (explicitly
// confirmed: the project is marked changed, never saved; scenario files, marks and the session Run configuration are
// kept) and resuming Run Configuration with its draft once the catalog of the prepared copy has loaded.
internal sealed partial class WorkbenchDialog
{
    internal const ushort PrepareTargetCommand = 0x7d55;
    // Marks to restore after the catalog of the prepared copy loads, and the Run Configuration to reopen then.
    private string[]? carriedMarks;
    private Action? afterCatalog;
    // Destinations chosen in this session, per original executable (normalized path).
    private readonly Dictionary<string, string> chosenDestinations = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record PrepareOutcome(string? UseExecutable, PreparationResult? Result, bool InPlace);

    // What the Workspace header, Properties and Run Tests offer for this target (from the target diagnostics).
    internal (OfferKind Kind, string Text) PreparationOffer(string? exe = null) => TargetDiagnostics.Preparation.Offer(DiagnoseProject(exe));

    // Opens Prepare Target for an executable (the project's by default). Returns the executable the project should use
    // when the user explicitly accepted a prepared copy; the caller applies it.
    internal PrepareOutcome PrepareTarget(string? exe = null, bool fromProject = true)
    {
        string path = (exe ?? target.Data).Trim();
        var service = TargetDiagnostics.Preparation;
        PreparationInspection inspection;
        try { inspection = service.Inspect(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Error("Prepare target: " + ex.Message, ex.ToString()); return new(null, null, false); }
        ushort answer; PreparationResult? result; PreparationPlan plan;
        // The destination the user chose survives mode switches and reopens (this session), and a
        // successful preparation remembers it in the user settings. A folder that now holds a copy proposes a free sibling.
        string key = WorkbenchPreferences.NormalizePath(inspection.Source);
        string? destination = chosenDestinations.GetValueOrDefault(key) ?? WorkbenchPreferences.Load(preferences.StoragePath).PrepareDestinations.GetValueOrDefault(key);
        if (destination is not null && (Directory.Exists(destination) && service.Status(destination) is not null || File.Exists(destination)))
            destination = PreparationDestination.NextFree(destination);
        while (true)
        {
            using (var dialog = new WorkbenchPrepareTargetDialog(desktop, service, inspection, destination: destination))
            {
                answer = desktop.ExecView(dialog); result = dialog.Result; plan = dialog.Plan;
                destination = dialog.ChosenDestination;
            }
            if (destination is null) chosenDestinations.Remove(key); else chosenDestinations[key] = destination;
            if (result is { Success: true, Deployment: { } deployed } && destination is not null)
            {
                try { WorkbenchPreferences.Update(preferences.StoragePath, p => p with { PrepareDestinations = new(p.PrepareDestinations, StringComparer.OrdinalIgnoreCase) { [key] = deployed } }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error("Could not save the Workbench preferences.", ex.ToString()); }
            }
            // The mode chooser asked for "Fix the original target"; it may hand back to this mode.
            if (answer != PreparationModes.SwitchCommand) break;
            if (FixOriginal(path)) return new(null, null, true);
            inspection = service.Inspect(path);
        }
        TargetDiagnostics.Forget(path); TargetPreparationService.ForgetStatus();
        UpdateHeader(); RecheckScenarioFiles();
        if (answer == WorkbenchPrepareTargetDialog.UseExistingCommand && inspection.Existing is { State: PreparedState.Ready, Manifest: { } m } existing)
        {
            string exePath = Path.Combine(existing.Folder, m.PreparedExecutable);
            return WorkbenchPrepareTargetDialog.ConfirmUse(desktop, exePath, inspection.Executable, fromProject) == Views.cmYes
                ? new(exePath, null, false) : Kept(exePath);
        }
        if (answer != Views.cmOK || result is not { Success: true }) return new(null, result, false);
        if (plan.Strategy == PreparationStrategy.InPlaceInstallation)
        {
            Status(result.Message + " The executable path is unchanged.");
            RecordRuntime(TargetDiagnostics.Inspect(path), f => f with { Verified = null, VerifyFailure = null, LastStartupFailure = null });
            return new(null, result, true);
        }
        // The affirmative default only for a preparation started from this project's own unprepared target.
        bool defaultYes = fromProject && !inspection.IsPreparedCopy && WorkbenchPreferences.SamePath(path, target.Data);
        return WorkbenchPrepareTargetDialog.ConfirmUse(desktop, result.PreparedExecutable!, plan.SourceExecutable, defaultYes) == Views.cmYes
            ? new(result.PreparedExecutable, result, false) : Kept(result.PreparedExecutable!, result);
    }
    private PrepareOutcome Kept(string prepared, PreparationResult? result = null)
    {
        Status($"The project keeps its executable. The prepared copy stays available at {Path.GetDirectoryName(prepared)}; select it in Project Properties (Alt+O) to use it.");
        return new(null, result, false);
    }

    // Project > Prepare target (and the Workspace hint): prepare, then apply an accepted prepared copy.
    private void PrepareProjectTarget()
    {
        if (target.Data.Length == 0) { Error("No target executable is set. Set it in Project Properties (Alt+O)."); return; }
        if (busy && activeRequest is not null) { Error("Tests are running. Prepare the target when the run has finished."); return; }
        var outcome = PrepareTarget();
        if (outcome.UseExecutable is { } exe) UsePreparedTarget(exe);
    }

    // The project's executable becomes the prepared copy: dirty, not saved, marks and scenario files kept; the catalog of
    // the copy is loaded (Proxy projects need no discovery) and resume runs once it has loaded.
    internal void UsePreparedTarget(string exe, Action? resume = null)
    {
        carriedMarks = scenarios.MarkedIds.ToArray();
        afterCatalog = resume;
        target.SetData(Path.GetFullPath(exe));
        InvalidateCatalog();
        detailKey = "";
        SavePreferences();
        UpdateSelection();
        Status("The project now uses the prepared target (not saved yet; Ctrl+S saves). Loading scenarios...");
        if (busy) { afterRun = LoadCatalog; cancellation?.Cancel(); }
        else LoadCatalog();
    }

    // Whether two executables are the same application: one is a prepared copy of the other (or both of one original).
    private static bool SameApplication(string a, string b)
    {
        static string Origin(string exe) => TargetDiagnostics.Preparation.Status(Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "")?.Source ?? exe;
        try { return WorkbenchPreferences.SamePath(Origin(a), Origin(b)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}
