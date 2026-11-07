using System.ComponentModel;
using System.Diagnostics;
using TSharpWorkbench.Testing;

// TSW PRODUCT-3 (Fix Issues): a transparent repair of the target deployment in place, built on the existing services
// instead of a second readiness or deployment engine:
//  * what is wrong comes from the target diagnostics (codes EXE/FW/PROXY/HOST/FIX/DRV/RUN/PREP; none invented);
//  * what can be deployed, from where, with which identity, comes from the preparation service (the Proxy
//    payload carried by this Workbench, its deps.json closure, the strict framework identity check, Classify);
//  * the write itself is the in-place installation (private staging, hash checks, write probe, verified
//    backups, journal, atomic per-file publish, rollback on failure or cancellation).
// Fix Issues adds the policy on top: only absent Workbench support files are copied by default (copy-only); an existing
// different Workbench Proxy/Testing build is replaced only with explicit approval (backup kept); every other difference is
// refused; the application, its framework family, custom DLLs, prepared copies, protected or linked folders and a running
// target are never touched. Planning is read-only. Normal test execution never calls this service.
internal enum RepairState { Fix, Ok, Manual, Refused, Fixed, Unresolved, Blocked }

// One row of the plan (and, after a repair, of the outcome). Code is the diagnostic finding the row belongs to ("" for a
// further file of the same Proxy closure).
internal sealed record RepairItem(RepairState State, string Code, string Name, string Summary, string? Source = null, string? Expected = null,
    string? Destination = null, string Conflict = "", string Permission = "", string Detail = "")
{
    internal bool Safe => State == RepairState.Fix;
    internal static string Label(RepairState state) => state switch
    {
        RepairState.Fix => "FIX", RepairState.Ok => "OK", RepairState.Manual => "MANUAL", RepairState.Refused => "REFUSED",
        RepairState.Fixed => "FIXED", RepairState.Blocked => "BLOCKED", _ => "UNRESOLVED"
    };
}

internal sealed record RepairPlan(string Executable, string Folder, TargetReport Before, PreparationPlan? Install, PreparationCapabilities Capabilities,
    bool AllowReplace, RepairItem[] Items, string[] Blockers, string[] Notes, string Fingerprint)
{
    internal bool CanRepair => Blockers.Length == 0 && Install is { CanExecute: true } && Items.Any(i => i.State == RepairState.Fix);
    // Nothing to repair and nothing refused or left to the user: the deployment is healthy for the diagnosed context.
    internal bool Healthy => Blockers.Length == 0 && !Items.Any(i => i.State is RepairState.Fix or RepairState.Manual or RepairState.Refused or RepairState.Blocked);
    // A different Workbench Proxy/Testing build exists: replacing it needs the explicit option (verified backup).
    internal bool NeedsReplaceApproval => Install?.Entries.Any(e => e.Action == PlanAction.Replace) == true;
    internal int Count(RepairState state) => Items.Count(i => i.State == state);
    // The outcome in the shared vocabulary (requires rebuild, manual intervention, refused, already valid).
    internal PlanSummary? Verdict => Install is null ? null : PreparationVerdict.For(Install, null);
    internal bool RequiresRebuild => Verdict?.RequiresRebuild == true;
    // The application's framework is another build of the Proxy's version (supported; Verify confirms it).
    internal bool OtherBuild => Install?.OtherBuild == true;
    internal string Counts => $"fix {Count(RepairState.Fix)}, ok {Count(RepairState.Ok)}, "
        + $"blocked {Count(RepairState.Blocked)}, refused {Count(RepairState.Refused)}, manual {Count(RepairState.Manual)}";
    internal string Summary => Blockers.Length > 0
        ? (RequiresRebuild ? PreparationVerdict.RequiresRebuild + ": " : Verdict?.Manual == true ? PreparationVerdict.ManualIntervention + ": " : "Refused: ") + PreparationVerdict.Detail(Blockers[0])
        : Healthy ? "Nothing to fix: the Workbench support files this target needs are deployed."
        : CanRepair ? $"{Count(RepairState.Fix)} file{(Count(RepairState.Fix) == 1 ? "" : "s")} can be fixed safely"
            + (Install!.Count(PlanAction.Replace) > 0 && AllowReplace ? $" ({Install.Count(PlanAction.Replace)} replaced with a verified backup)" : " (copy only; nothing is replaced)")
            + (Count(RepairState.Manual) + Count(RepairState.Refused) > 0 ? $"; {Count(RepairState.Manual) + Count(RepairState.Refused)} item(s) need you." : ".")
            + (OtherBuild ? " Same TSharpVision version, other build: Fix and verify confirms it." : "")
        : "Nothing can be fixed automatically; see the items.";
}

internal sealed record RepairOutcome(bool Success, bool Cancelled, string Message, TargetReport? After, RepairItem[] Items, string[] Changes, string[] Details)
{
    internal bool RolledBack { get; init; }
    internal bool RollbackComplete { get; init; } = true;
    // The install record of the files this repair added (UndoInstall), and whether they were removed again.
    internal string? Record { get; init; }
    internal bool Undone { get; init; }
    internal int Count(RepairState state) => Items.Count(i => i.State == state);
}

internal sealed class TargetRepairService(TargetPreparationService preparation)
{
    // Findings a Workbench-owned payload file can address (Proxy closure, and the display driver files of the closure).
    internal static readonly string[] PayloadCodes = ["PROXY001", "PROXY002", "PROXY003", "PROXY005", "PROXY006", "PROXY007", "PROXY008", "DRV001", "DRV002", "DRV003", "DRV005"];
    // Test seam: whether the executable is running (default: a process whose main module is this file).
    internal Func<string, bool> IsRunning { get; init; } = ProcessRunning;
    internal TargetPreparationService Service => preparation;

    // Whether Fix Issues has something to say about this report (Properties and Target Diagnostics enable the button).
    // And only while something is safely fixable (copy-only, or a different Workbench build replaced with a
    // verified backup after approval). A target whose repair is refused (another framework build, a user-owned file, a
    // protected or linked folder, a prepared copy) keeps Fix disabled: Target Diagnostics and Prepare target explain it.
    internal static bool Relevant(TargetReport report)
    {
        if (!report.Inspection.Exists || !report.Findings.Any(f => f.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && PayloadCodes.Contains(f.Code))) return false;
        var service = TargetDiagnostics.Preparation;
        string key = report.Inspection.Target + "|" + report.Inspection.Stamp + "|" + service.Root + "|" + string.Join(",", report.Findings.Select(f => f.Code + f.Severity));
        lock (fixable)
            if (fixable.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromSeconds(2)) return known.Fixable;
        bool result;
        try { result = new TargetRepairService(service).Plan(report.Inspection.Target, report, allowReplace: true).CanRepair; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { result = false; }
        lock (fixable) { if (fixable.Count > 32) fixable.Clear(); fixable[key] = (DateTime.UtcNow, result); }
        return result;
    }
    private static readonly Dictionary<string, (DateTime At, bool Fixable)> fixable = new(StringComparer.OrdinalIgnoreCase);

    // A process whose main module is this executable. A process of that name whose image cannot be inspected (another
    // user, elevated) counts as running: the repair never guesses.
    internal static bool ProcessRunning(string exe)
    {
        string full = Path.GetFullPath(exe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(full)))
            using (p)
            {
                try { if (p.HasExited) continue; if (p.MainModule?.FileName is { } image && WorkbenchPreferences.SamePath(image, full)) return true; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
            }
        return false;
    }

    // The first symbolic link, junction or other reparse point on the way to the folder (the folder included), or null.
    internal static string? LinkOnPath(string folder)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(folder)); d is not null; d = d.Parent)
        {
            try { if (d.Exists && (d.Attributes.HasFlag(FileAttributes.ReparsePoint) || d.LinkTarget is not null)) return d.FullName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return d.FullName; }
        }
        return null;
    }

    // What the Proxy and the selected display driver of the diagnosed context need. The SDL component is part of it
    // whenever the payload's Proxy references the SDL driver assembly: its handshake loads that assembly on every backend
    // (headless included), so a Proxy + Testing copy alone does not start (proven: FileNotFoundException at the handshake).
    // Its native libraries come with it (the payload's SDL component is deployed as one unit and validated as such).
    internal PreparationCapabilities CapabilitiesFor(TargetReport report)
    {
        var c = PreparationCapabilities.Proxy;
        string? backend = report.Context.NativeMode is null && report.Context.Proxy ? report.Context.ProxyBackend : null;
        if (backend is "SDLDriver" or "SDLGpuDriver") c |= PreparationCapabilities.Sdl;
        if (preparation.Payload is { } p && TargetDiagnostics.ReadAssembly(Path.Combine(p.Directory, TargetDiagnostics.ProxyFile)).References.ContainsKey("TSharpVision.Drivers.SDL"))
            c |= PreparationCapabilities.Sdl;
        if (backend == "Win32ConsoleDriver" && OperatingSystem.IsWindows()) c |= PreparationCapabilities.Console;
        return c;
    }

    // ── planning (read-only: nothing is created, written or started) ──────────────────────────────

    internal RepairPlan Plan(string executable, TargetReport report, bool allowReplace = false, bool runActive = false)
    {
        var blockers = new List<string>(); var notes = new List<string>(); var items = new List<RepairItem>();
        string exe = executable.Trim(), folder = "";
        try { exe = Path.GetFullPath(exe); folder = Path.GetDirectoryName(exe) ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { blockers.Add("The executable path is not valid."); }
        string name = Path.GetFileName(exe);
        var capabilities = CapabilitiesFor(report);
        PreparationPlan? install = null;

        if (blockers.Count == 0 && !File.Exists(exe)) blockers.Add($"{name} does not exist.");
        if (blockers.Count == 0)
        {
            if (runActive) blockers.Add("A test run is active. Fix issues when it has finished; files of a running target are never changed.");
            if (IsRunning(exe)) blockers.Add($"{name} is running. Close it first; files of a running target are never changed.");
            if (LinkOnPath(folder) is { } link) blockers.Add($"The application folder is reached through a symbolic link or junction ({link}). Links are never followed; open the target from its real folder.");
            if (preparation.Status(folder) is not null)
                blockers.Add("This is a prepared copy managed by Prepare target. It is not changed in place; Re-prepare it (Settings > Prepare Target Copy...).");
            if (TargetPreparation.ProtectedLocation(folder) is { } protectedReason)
                blockers.Add($"The application folder is protected: {protectedReason}. The Workbench never elevates; use Settings > Prepare Target Copy... (a prepared copy).");
        }
        if (blockers.Count == 0)
        {
            var inspection = preparation.Inspect(exe);
            if (inspection.Unsupported is { } unsupported) blockers.Add(unsupported);
            else
            {
                install = preparation.CreatePlan(inspection, PreparationStrategy.InPlaceInstallation, capabilities);
                notes.AddRange(install.Compatibility);
                if (preparation.Payload is { } payload)
                    notes.Add($"Source: this Workbench's Proxy payload ({ProxyPayload.Folder}, {payload.Rid}, TSharpVision {payload.FrameworkVersion}).");
                Itemize(install, report, allowReplace, items, blockers, folder, exe);
            }
        }
        // Every problem the diagnostics report that no planned file addresses stays with the user, with its own action.
        foreach (var f in report.Findings.Where(f => f.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            if (!items.Any(i => i.Code == f.Code))
                items.Add(new(RepairState.Manual, f.Code, f.Short, f.Message, Destination: f.Path, Detail: f.Action.Length > 0 ? f.Action : f.Detail));
        // While anything refuses the repair, nothing is fixable: a file that would be copied is shown as BLOCKED, never FIX.
        if (blockers.Count > 0)
            for (int i = 0; i < items.Count; i++)
                if (items[i].State == RepairState.Fix)
                    items[i] = items[i] with { State = RepairState.Blocked, Summary = "would be copied, but the repair is refused (see the refused rows)" };
        string fingerprint = TargetPreparation.Hash(string.Join("\n", items.Select(i => $"{i.State}|{i.Code}|{i.Destination}|{i.Expected}"))
            + "|" + install?.Fingerprint + "|" + allowReplace + "|" + capabilities);
        return new(exe, folder, report, install, capabilities, allowReplace, items.ToArray(), blockers.Distinct().ToArray(), notes.ToArray(), fingerprint);
    }

    // Plan entries become rows: Add = copy-only fix, Replace = fix only with approval, Conflict = refused, Keep/Provided = OK.
    private void Itemize(PreparationPlan install, TargetReport report, bool allowReplace, List<RepairItem> items, List<string> blockers, string folder, string exe)
    {
        var payload = preparation.Payload;
        string main = report.Inspection.Main?.Path is { } m ? Path.GetFileNameWithoutExtension(m) : Path.GetFileNameWithoutExtension(exe);
        // Never written by Fix Issues, whatever a plan says: the application itself, its startup files and its framework.
        var untouchable = new HashSet<string>([Path.GetFileName(exe), main + ".dll", main + ".deps.json", main + ".runtimeconfig.json", "TSharpVision.dll"], TargetPreparation.PathComparer);
        string Code(PlanEntry e)
        {
            string file = Path.GetFileName(e.Path);
            if (report.Findings.FirstOrDefault(f => f.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && f.Path is { } p
                && string.Equals(Path.GetFileName(p), file, StringComparison.OrdinalIgnoreCase) && PayloadCodes.Contains(f.Code)) is { } byPath) return byPath.Code;
            string[] candidates = e.Component == "proxy" ? (file.Equals(TargetDiagnostics.ProxyFile, StringComparison.OrdinalIgnoreCase) ? ["PROXY001", "PROXY006", "PROXY008"] : ["PROXY002", "PROXY005", "PROXY007", "PROXY008"])
                : ["DRV001", "DRV002", "DRV003", "DRV005"];
            return report.Findings.FirstOrDefault(f => f.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && candidates.Contains(f.Code))?.Code ?? "";
        }
        string Expected(PlanEntry e) => (payload?.Assets.FirstOrDefault(a => a.Destination == e.Path) is { } a ? $"{a.Package} {a.Version}" : e.Component)
            + (e.Sha256 is { Length: >= 12 } h ? ", SHA-256 " + h[..12] : "");
        const string probe = "Write access is probed before anything changes (no elevation).";
        foreach (var e in install.Entries.Where(e => e.Action != PlanAction.Copy))
        {
            string destination = Path.GetFullPath(Path.Combine(folder, e.Path));
            string file = Path.GetFileName(e.Path);
            bool inside = TargetPreparation.IsUnder(destination, folder);
            if (e.Action is PlanAction.Add or PlanAction.Replace && (!inside || untouchable.Contains(file)))
            {
                items.Add(new(RepairState.Refused, Code(e), file, inside ? "belongs to the application; never written" : "outside the application folder; refused",
                    e.Source, Expected(e), destination, "path refused", "", "Fix Issues writes only Workbench support files directly beside the executable."));
                blockers.Add($"{e.Path}: refused path.");
                continue;
            }
            if (File.Exists(destination) && new FileInfo(destination) is { } existing && (existing.Attributes.HasFlag(FileAttributes.ReparsePoint) || existing.LinkTarget is not null))
            {
                items.Add(new(RepairState.Refused, Code(e), file, "is a symbolic link; never followed or replaced", e.Source, Expected(e), destination, "symbolic link", ""));
                blockers.Add($"{e.Path} is a symbolic link; it is never followed or replaced.");
                continue;
            }
            switch (e.Action)
            {
                case PlanAction.Add:
                    items.Add(new(RepairState.Fix, Code(e), file, e.Component == "proxy" ? "missing - copy from this Workbench" : "missing Proxy dependency (SDL) - copy",
                        e.Source, Expected(e), destination,
                        "none (the file is absent; nothing is overwritten)", probe, "Copy only. " + e.Note));
                    break;
                case PlanAction.Replace:
                    string have = TargetDiagnostics.ReadAssembly(destination) is { Readable: true } facts ? facts.Display : "unreadable";
                    items.Add(allowReplace
                        ? new(RepairState.Fix, Code(e), file, "different Workbench build - replace with backup", e.Source, Expected(e), destination,
                            $"deployed {have} differs from this Workbench's build", probe + " A verified backup is kept; a failure restores it.", e.Note)
                        : new(RepairState.Refused, Code(e), file, "different Workbench build - needs your approval", e.Source, Expected(e), destination,
                            $"deployed {have} differs from this Workbench's build", "",
                            "Not replaced without approval. Tick \"Replace a different Workbench build\" to replace it with a verified backup and rollback."));
                    if (!allowReplace) blockers.Add($"{file} is a different Workbench build; replacing it needs your approval (a verified backup is kept).");
                    break;
                case PlanAction.Conflict:
                    string kind = PreparationVerdict.KindOf(e.Note);
                    items.Add(new(RepairState.Refused, Code(e), file, kind.ToLowerInvariant() + " - kept, never replaced", e.Source, Expected(e), destination, e.Note, "",
                        PreparationVerdict.NeedsRebuild(kind) ? "The application ships its own version of this framework component. It is never replaced. "
                            + PreparationVerdict.RebuildAdvice(payload?.FrameworkVersion ?? "")
                        : "The existing file is not a Workbench build of this component. It is never replaced; rename or remove it yourself if it is not needed."));
                    break;
                case PlanAction.Provided when e.Note.StartsWith(PreparationVerdict.SameVersion, StringComparison.Ordinal):
                    items.Add(new(RepairState.Ok, Code(e), file, "application's own build kept (same version)", e.Source, Expected(e), destination,
                        Identity(destination), "", PreparationVerdict.Detail(e.Note) + ". Verify target confirms it at runtime."));
                    break;
                default:
                    items.Add(new(RepairState.Ok, Code(e), file, e.Action == PlanAction.Keep ? "deployed (identical)" : "provided by the application", e.Source, Expected(e), destination, "none", ""));
                    break;
            }
        }
        // The framework itself, kept as the application ships it, when it is another build of the same version.
        if (install.OtherBuild && payload is not null)
        {
            string framework = Path.Combine(folder, "TSharpVision.dll");
            items.Add(new(RepairState.Ok, report.Findings.FirstOrDefault(f => f.Code == "FW003")?.Code ?? "", "TSharpVision.dll", "same version, other build - kept (never replaced)",
                payload.FrameworkPath, $"TSharpVision {payload.FrameworkVersion}, SHA-256 {payload.FrameworkSha256[..12]}", framework, Identity(framework), "",
                PreparationVerdict.OtherBuildNote));
        }
        // Problems of the plan (framework identity, payload build, disk space, conflicts) refuse the whole repair.
        foreach (string problem in install.Problems)
        {
            if (install.Entries.Any(e => e.Action == PlanAction.Conflict && problem.StartsWith(e.Path + ":", StringComparison.Ordinal))) { blockers.Add(problem); continue; }
            string code = report.Findings.FirstOrDefault(f => f.Component == "Framework" && f.Severity != DiagnosticSeverity.Ok)?.Code
                ?? (problem.Contains("TSharpVision", StringComparison.Ordinal) ? report.Findings.FirstOrDefault(f => f.Component == "Framework")?.Code ?? "" : "");
            if (!items.Any(i => i.State == RepairState.Refused && i.Detail == problem))
                items.Add(new(RepairState.Refused, code, code.StartsWith("FW", StringComparison.Ordinal) ? "TSharpVision.dll" : "deployment",
                    PreparationVerdict.NeedsRebuild(problem) ? PreparationVerdict.KindOf(problem).ToLowerInvariant() + " - requires rebuild; never replaced" : "refused", Detail: problem,
                    Destination: code.StartsWith("FW", StringComparison.Ordinal) ? Path.Combine(folder, "TSharpVision.dll") : folder));
            blockers.Add(problem);
        }
    }

    // The identity of a deployed assembly as the Compatibility view and the kept rows show it: version, informational
    // version, SHA-256, MVID (information; compatibility is decided by version).
    internal static string Identity(string path)
    {
        if (!File.Exists(path)) return "missing";
        var facts = TargetDiagnostics.ReadAssembly(path);
        return $"application: {(facts.Readable ? facts.Display : "unreadable")}, SHA-256 {Preparation.FileHash(path)[..12]}, MVID {Preparation.Mvid(path)?.ToString()[..8] ?? "-"}";
    }

    // ── repair (only after explicit confirmation) ─────────────────────────────────────────────

    internal async Task<RepairOutcome> RepairAsync(RepairPlan approved, Func<string, TargetReport> diagnose, IProgress<PreparationProgress>? progress = null,
        CancellationToken token = default, bool runActive = false)
    {
        if (!approved.CanRepair) return new(false, false, "Nothing was changed: " + (approved.Blockers.FirstOrDefault() ?? "there is nothing to fix safely."), null, approved.Items, [], approved.Blockers);
        // The confirmed plan must still describe the target, and the target must still be closed.
        TargetDiagnostics.Forget(approved.Executable); TargetPreparationService.ForgetStatus();
        var current = Plan(approved.Executable, diagnose(approved.Executable), approved.AllowReplace, runActive);
        if (current.Blockers.FirstOrDefault() is { } blocker) return new(false, false, "Nothing was changed: " + blocker, null, current.Items, [], current.Blockers);
        if (current.Fingerprint != approved.Fingerprint)
            return new(false, false, "The target changed since the plan was shown; nothing was changed. Review the new plan.", null, current.Items, [], []);
        if (token.IsCancellationRequested) return new(false, true, "Fix issues cancelled; nothing was changed.", null, approved.Items, [], []);
        var result = await preparation.ExecuteAsync(approved.Install!, progress, token);
        TargetDiagnostics.Forget(approved.Executable); TargetPreparationService.ForgetStatus();
        // Re-verification: the same diagnostics, again (static); the caller may add the runtime Verify target.
        var after = diagnose(approved.Executable);
        var items = Outcome(approved, after, result.Success);
        string message = result.Success
            ? $"Fixed: {result.Changes.Length} file{(result.Changes.Length == 1 ? "" : "s")} copied into the application folder. "
              + (after.Ready ? "Target diagnostics: ready (static checks)." : $"Target diagnostics: {after.Readiness}.")
            : result.Message;
        return new(result.Success, result.Cancelled, message, after, items, result.Changes, result.Details) { RolledBack = result.RolledBack, RollbackComplete = result.RollbackComplete, Record = result.Record };
    }

    // Before vs after: a planned finding that is gone is Fixed; one that remains is Unresolved; everything else the user
    // must do stays Manual (with the diagnostics' own action), refused rows stay Refused.
    internal static RepairItem[] Outcome(RepairPlan plan, TargetReport after, bool success)
    {
        var open = after.Findings.Where(f => f.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).ToArray();
        var rows = new List<RepairItem>();
        foreach (var item in plan.Items)
        {
            bool remains = item.Code.Length > 0 ? open.Any(f => f.Code == item.Code) : !success;
            rows.Add(item.State switch
            {
                RepairState.Fix => item with { State = success && !remains ? RepairState.Fixed : RepairState.Unresolved,
                    Summary = success ? (remains ? "copied, but the finding remains" : "copied and verified") : "not changed (rolled back)" },
                RepairState.Manual when !remains => item with { State = RepairState.Fixed, Summary = "resolved" },
                _ => item
            });
        }
        foreach (var f in open.Where(f => !rows.Any(r => r.Code == f.Code)))
            rows.Add(new(RepairState.Unresolved, f.Code, f.Short, f.Message, Destination: f.Path, Detail: f.Action.Length > 0 ? f.Action : f.Detail));
        return rows.ToArray();
    }
}

// Shorter names for the preparation helpers Fix Issues reuses.
file static class TargetPreparation
{
    internal static string Hash(string text) => Preparation.Hash(text);
    internal static bool IsUnder(string path, string folder) => Preparation.IsUnder(path, folder);
    internal static string? ProtectedLocation(string folder) => Preparation.ProtectedLocation(folder);
    internal static StringComparer PathComparer => Preparation.PathComparer;
}
