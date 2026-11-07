using System.Globalization;
using System.Text.Json;
using TSharpWorkbench.Testing;

// CLI dispatch is deliberately before TUI construction or driver configuration.
return args.Length == 0 || args[0] == "tui" ? WorkbenchEntry.Run(args.Skip(1).ToArray()) : await Cli.RunAsync(args);

internal static class Cli
{
    private static readonly string[] Flags = ["--all", "--verbose", "--include-expected-failures", "--replace"];
    private static readonly string[] Options = ["--project", "--target", "--scenario", "--group", "--driver", "--size", "--output", "--timeout",
        "--scenario-timeout", "--proxy-backend", "--proxy-capture", "--inspect", "--allow", "--results", "--baselines", "--run", "--candidate", "--confirm", "--legacy-fixture"];
    private static readonly string[] BaselineOnly = ["--run", "--candidate", "--confirm", "--replace"];

    internal static async Task<int> RunAsync(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var output = stdout ?? Console.Out; var error = stderr ?? Console.Error;
        try
        {
            if (args[0] is "help" or "--help" or "-h") { output.WriteLine(Usage); return 0; }
            if (args[0] == "baseline") return await BaselineAsync(args.Skip(1).ToArray(), output);
            if (args[0] == "diagnose") return Diagnose(args.Skip(1).ToArray(), output);
            if (args[0] == "prepare") return await PrepareAsync(args.Skip(1).ToArray(), output);
            if (args[0] is not ("run" or "list" or "groups" or "describe")) throw new ArgumentException("Unknown command '" + args[0] + "'.\n" + Usage);
            var commandArgs = args.Skip(1).ToArray();
            if (commandArgs.Length > 0 && !commandArgs[0].StartsWith("--", StringComparison.Ordinal))
                commandArgs = ["--project", commandArgs[0], .. commandArgs.Skip(1)];
            var values = Parse(commandArgs);
            WorkbenchProject? project = null;
            if (values.TryGetValue("--project", out var projectPath))
            {
                try { project = WorkbenchProject.Load(projectPath); }
                catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or UnauthorizedAccessException)
                { error.WriteLine($"Invalid project '{projectPath}': {ex.Message}"); return 4; }
                values.TryAdd("--target", project.Target); values.TryAdd("--output", project.OutputRoot);
                values.TryAdd("--proxy-capture", project.ProxyCapture); values.TryAdd("--proxy-backend", project.ProxyBackend); values.TryAdd("--driver", project.Mode); values.TryAdd("--size", string.Join(',', project.ScreenSizes));
                values.TryAdd("--inspect", project.Inspect); values.TryAdd("--allow", string.Join(',', project.Allow));
                if (project.Timeout is { } ms) values.TryAdd("--timeout", ms.ToString(CultureInfo.InvariantCulture));
                if (project.ScenarioTimeout is { } seconds) values.TryAdd("--scenario-timeout", seconds.ToString(CultureInfo.InvariantCulture));
                if (project.TestType == "visual" && project.BaselineStore is { } baseline) values.TryAdd("--baselines", baseline);
                if (!values.ContainsKey("--scenario") && !values.ContainsKey("--group") && !values.ContainsKey("--all"))
                {
                    if (project.RunScope == "all") values.Add("--all", "true");
                    else if (project.RunScope == "group" && project.Group is { } group) values.Add("--group", group);
                    else if (project.SelectedIds.Length > 0) values.Add("--scenario", string.Join(',', project.SelectedIds));
                }
            }
            if (values.Keys.Any(BaselineOnly.Contains)) throw new ArgumentException("--run, --candidate, --confirm and --replace belong to 'baseline approve'.");
            if (values.ContainsKey("--baselines") && args[0] != "run") throw new ArgumentException("--baselines applies to run (and the baseline command).");
            try { WorkbenchCapabilities.Validate(values.GetValueOrDefault("--driver", "NullDriver")); }
            catch (ArgumentException ex) when (project is not null) { error.WriteLine(ex.Message); return 6; }
            string target = values.GetValueOrDefault("--target") ?? throw new ArgumentException("--target <test-target executable> is required.");
            if (project is not null && !File.Exists(target)) { error.WriteLine("Invalid project target: " + target); return 4; }
            string[]? sizes = values.TryGetValue("--size", out string? sizeText) ? ScenarioSelection.ParseSizes(sizeText) : null;
            var options = new RunOptions(values.GetValueOrDefault("--output", ""), sizes, values.GetValueOrDefault("--inspect", "declared"),
                values.TryGetValue("--allow", out string? allow) ? allow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null,
                Number(values, "--timeout"), Number(values, "--scenario-timeout"), values.GetValueOrDefault("--baselines")) { Driver = values.GetValueOrDefault("--driver", "NullDriver"), ProxyBackend = values.GetValueOrDefault("--proxy-backend", "NullDriver"), ProxyCapture = values.GetValueOrDefault("--proxy-capture", "none"), ProxyShutdownKey = project?.ProxyShutdownKey, TargetArguments = project?.TargetArguments ?? [], TargetEnvironment = project?.TargetEnvironment ?? new() };
            if (args[0] == "run")
            {
                if (!values.ContainsKey("--output")) throw new ArgumentException("--output <directory> is required for run.");
                if (!(values.ContainsKey("--scenario") || values.ContainsKey("--group") || values.ContainsKey("--all")))
                    throw new ArgumentException("Select scenarios with --scenario <id,id>, --group <group> or --all.");
            }
            if (args[0] == "describe" && !values.ContainsKey("--scenario")) throw new ArgumentException("describe needs --scenario <id>.");
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                var runner = new ScenarioRunner();
                // Raw advertisement binds external scenarios and names fixture-only registrations in diagnostics; list, groups and
                // bulk selection see only runnable discovery.
                ScenarioDescriptor[] raw = await runner.DiscoverRawAsync(target, project?.ScenarioFiles ?? [], LegacyFixtures(values, project), cancel.Token);
                ScenarioDescriptor[] catalog = ScenarioSelection.Runnable(raw);
                bool verbose = values.ContainsKey("--verbose");
                if (args[0] == "list")
                {
                    output.WriteLine("ID\tName\tGroup\tKind\tGrid range\tDefault grids\tExpected\tCheckpoints");
                    foreach (var s in catalog)
                    {
                        output.WriteLine($"{s.Id}\t{s.Name}\t{s.Group}\t{s.Kind}\t{s.Grid.Describe()}\t{string.Join(',', s.Grid.Defaults)}\t{s.ExpectedResult}\t"
                            + string.Join(',', s.Steps.Where(x => x.Kind == ScenarioStep.Checkpoint).Select(x => x.Name)));
                        if (verbose) Describe(s, output, "    ");
                    }
                    if (FixtureOnlyNote(raw) is { Length: > 0 } note) error.WriteLine("Note:" + note);
                    return 0;
                }
                if (args[0] == "groups")
                {
                    foreach (string group in Groups(catalog)) output.WriteLine($"{group}\t{catalog.Count(s => ScenarioSelection.InGroup(s.Group, group))}");
                    return 0;
                }
                if (args[0] == "describe")
                {
                    foreach (string id in values["--scenario"].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        Describe(raw.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown scenario ID: " + id), output, "");
                    return 0;
                }
                var selected = ScenarioSelection.Resolve(raw, values.GetValueOrDefault("--scenario", "").Split(',', StringSplitOptions.RemoveEmptyEntries),
                    values.GetValueOrDefault("--group"), values.ContainsKey("--all"), values.ContainsKey("--include-expected-failures"));
                BatchResult batch = await runner.RunAsync(target, selected, options, output.WriteLine, cancel.Token);
                foreach (var r in batch.Results.Where(r => r.Outcome is not (ScenarioOutcomes.Passed or ScenarioOutcomes.Skipped)))
                {
                    error.WriteLine($"{r.Outcome}: {r.ScenarioId} @ {r.Size}: {r.Reason}");
                    if (verbose)
                        foreach (var a in r.Checkpoints.SelectMany(c => c.Assertions).Where(a => a.Outcome != AssertionOutcomes.Pass))
                            error.WriteLine($"  [{a.Outcome}/{a.Severity}] {a.Checkpoint} ({a.Grid}) {a.Id} {a.Kind}: expected {a.Expected}; actual {a.Actual ?? "n/a"}; {a.Explanation}"
                                + (a.Location is null ? "" : " @ " + a.Location));
                }
                int assertions = batch.Results.Sum(r => r.Checkpoints.Sum(c => c.Assertions.Length));
                output.WriteLine($"Artifacts: {batch.RunRoot}\nReport: {batch.Report}\nPassed: {batch.Results.Count(r => r.Outcome == ScenarioOutcomes.Passed)}; "
                    + $"Skipped: {batch.Results.Count(r => r.Outcome == ScenarioOutcomes.Skipped)}; Other: {batch.Results.Count(r => r.Outcome is not (ScenarioOutcomes.Passed or ScenarioOutcomes.Skipped))}; Checkpoints: {batch.Results.Sum(r => r.Checkpoints.Length)}; "
                    + $"Assertions: {assertions}; Review: Unreviewed");
                if (batch.Visual is { } visual)
                {
                    output.WriteLine($"Visual gate: {(visual.Passed ? "PASSED" : "NOT PASSED")} — " + string.Join(", ",
                        new[] { VisualStatuses.Match, VisualStatuses.Mismatch, VisualStatuses.BaselineMissing, VisualStatuses.BaselineInvalid,
                            VisualStatuses.NormalizationError, VisualStatuses.NotCaptured, VisualStatuses.NotRun }.Select(s => $"{visual.Count(s)} {s}")));
                    foreach (var e in visual.Entries.Where(e => VisualStatuses.Failing.Contains(e.Status)))
                        error.WriteLine($"Visual {e.Status}: {e.BaselineKey()}" + (e.BaselineScenarioId is null ? "" : $" (executed as {e.ScenarioId})") + $": {e.Reason}"
                            + (e.Candidate is null ? "" : $" Candidate: {e.Candidate} (unreviewed)."));
                }
                if (values.TryGetValue("--results", out string? resultsPath))
                {
                    resultsPath = Path.GetFullPath(resultsPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
                    await File.WriteAllTextAsync(resultsPath, JsonSerializer.Serialize(batch, ScenarioRunner.Json));
                    output.WriteLine("Results: " + resultsPath);
                }
                if (project is not null) return ProjectExitCode(batch);
                return batch.ExitCode;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (ExternalScenarioException ex) { error.WriteLine(ex.Message); return 5; }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) { error.WriteLine("Workbench failed: " + ex.GetType().Name + (ex is IOException ? ": " + ex.Message : "")); return 1; }
    }

    // Static target diagnostics for scripts (the same service as the Workbench). Reads files and metadata only;
    // nothing is started and no project or run is written. Run semantics and exit codes are unchanged.
    private static int Diagnose(string[] args, TextWriter output)
    {
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)) args = ["--project", args[0], .. args.Skip(1)];
        var values = Parse(args);
        if (values.Keys.FirstOrDefault(k => k is not ("--project" or "--target" or "--proxy-backend" or "--driver")) is { } extra)
            throw new ArgumentException($"{extra} does not apply to 'diagnose'.");
        var project = values.TryGetValue("--project", out string? path) ? WorkbenchProject.Load(path) : null;
        string target = values.GetValueOrDefault("--target") ?? project?.Target ?? throw new ArgumentException("diagnose needs a project or --target <exe>.");
        var inspection = TargetDiagnostics.Inspect(target);
        var attached = ScenarioFiles.Check(project?.ScenarioFiles ?? [], inspection.Target, null, inspection.HostsDiagnostics == false);
        var documents = attached.Where(a => a.Info.Document is not null).ToArray();
        bool proxyDocuments = documents.Any(d => d.Info.Proxy), fixtureDocuments = documents.Any(d => !d.Info.Proxy);
        bool proxy = proxyDocuments || !fixtureDocuments && inspection.HostsDiagnostics == false;
        bool diagnostic = fixtureDocuments || !proxyDocuments && inspection.HostsDiagnostics != false;
        string driver = values.GetValueOrDefault("--driver") ?? project?.Mode ?? "NullDriver";
        string? native = driver != "NullDriver" && diagnostic ? driver : null;
        var context = new DiagnosticContext(proxy, diagnostic, proxy ? values.GetValueOrDefault("--proxy-backend") ?? project?.ProxyBackend ?? "NullDriver" : null,
            proxy && native is null ? project?.ProxyCapture : null, native, Run: true);
        var report = TargetDiagnostics.Evaluate(inspection, context, new CatalogFacts(false, 0, 0, 0, null, attached));
        foreach (string line in TargetDiagnostics.Lines(report)) output.WriteLine(line);
        return report.Ready ? 0 : 2;
    }

    // Prepare target for scripts (the same service as the Workbench). Without --execute it only prints the plan.
    // A prepared copy never modifies the original; --in-place modifies the application folder (backups and rollback).
    // Exit 0: plan executable (or executed), 2: the plan has problems, 1: execution failed or was rolled back.
    private static async Task<int> PrepareAsync(string[] args, TextWriter output)
    {
        string? target = null, root = null; bool inPlace = false, execute = false; var capabilities = TargetPreparationService.DefaultCapabilities;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--target": target = ++i < args.Length ? args[i] : throw new ArgumentException("--target needs a value."); break;
                case "--root": root = ++i < args.Length ? args[i] : throw new ArgumentException("--root needs a value."); break;
                case "--in-place": inPlace = true; break;
                case "--execute": execute = true; break;
                case "--without":
                    // SDL is always deployed (the Proxy loads TSharpVision.Drivers.SDL on every display driver).
                    foreach (string c in (++i < args.Length ? args[i] : throw new ArgumentException("--without needs console.")).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        capabilities &= c switch
                        {
                            "console" => ~PreparationCapabilities.Console,
                            "sdl" => throw new ArgumentException("SDL support is always deployed: the Proxy loads TSharpVision.Drivers.SDL on every display driver. --without takes console."),
                            _ => throw new ArgumentException("--without takes console.")
                        };
                    break;
                default:
                    if (target is null && !args[i].StartsWith("--", StringComparison.Ordinal)) { target = args[i]; break; }
                    throw new ArgumentException($"{args[i]} does not apply to 'prepare'.");
            }
        if (target is null) throw new ArgumentException("prepare needs the application executable (--target <exe>).");
        var service = new TargetPreparationService(root);
        var plan = service.CreatePlan(service.Inspect(target), inPlace ? PreparationStrategy.InPlaceInstallation : PreparationStrategy.PreparedCopy, capabilities);
        foreach (var c in plan.Components) output.WriteLine($"{c.Name,-20} {c.State,-24} {c.Detail}");
        foreach (string line in plan.Compatibility) output.WriteLine(line);
        foreach (string line in plan.FileLines().Take(6)) output.WriteLine(line);
        if (!plan.CanExecute) return 2;
        if (!execute) { output.WriteLine("Plan only. Add --execute to " + (inPlace ? "install into the application folder." : "create the prepared copy.")); return 0; }
        var result = await service.ExecuteAsync(plan, new Progress<PreparationProgress>(p => { }));
        output.WriteLine(result.Message);
        foreach (string change in result.Changes) output.WriteLine("  " + change);
        if (result.PreparedExecutable is { } exe) output.WriteLine("Prepared executable: " + exe);
        return result.Success ? 0 : 1;
    }

    internal static int ProjectExitCode(BatchResult batch) =>
        batch.Results.Any(r => r.Outcome == ScenarioOutcomes.Error) ? 9 :
        batch.Results.Any(r => r.Outcome == ScenarioOutcomes.TimedOut) ? 8 :
        batch.Results.Any(r => r.Outcome == ScenarioOutcomes.Blocked) ? 6 :
        batch.Results.Any(r => r.Outcome == ScenarioOutcomes.Failed) ? 7 : batch.ExitCode;

    // Baseline governance. status only reads; approve is the one deliberate owner action that writes approved baselines.
    private static async Task<int> BaselineAsync(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is not ("status" or "approve")) throw new ArgumentException("Use 'baseline status' or 'baseline approve'.\n" + Usage);
        var values = Parse(args.Skip(1).ToArray());
        string[] allowed = args[0] == "status" ? ["--baselines", "--project", "--target", "--driver"] : ["--baselines", "--run", "--candidate", "--confirm", "--replace"];
        if (values.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } extra) throw new ArgumentException($"{extra} does not apply to 'baseline {args[0]}'.");
        var set = VisualBaselineSet.Load(values.GetValueOrDefault("--baselines") ?? throw new ArgumentException("--baselines <manifest> is required."));
        if (args[0] == "status")
        {
            // One environment at a time - this OS with NullDriver, or --driver (a Proxy backend).
            var environment = BaselineEnvironment.Current(values.GetValueOrDefault("--driver") ?? BaselineEnvironment.DefaultDriver);
            output.WriteLine($"Environment: {environment} (baselines of other environments are never compared)");
            var rows = set.Status(environment).ToArray();
            foreach (var (key, status, reason) in rows)
                output.WriteLine($"{status}\t{key}" + (reason.Length == 0 || status == VisualStatuses.BaselineMissing ? "" : "\t" + reason));
            output.WriteLine($"Approved: {rows.Count(r => r.Status == "Approved")}; missing: {rows.Count(r => r.Status == VisualStatuses.BaselineMissing)}; "
                + $"invalid: {rows.Count(r => r.Status == VisualStatuses.BaselineInvalid)}");
            // Explicit identity mappings, verified against the target's validated fixture bindings when a target is given.
            var mappings = set.Manifest.Baselines.Where(e => e.ExecutionScenario is not null).GroupBy(e => e.Execution).ToArray();
            ScenarioDescriptor[]? catalog = null;
            if (values.ContainsKey("--target") || values.ContainsKey("--project"))
            {
                var project = values.TryGetValue("--project", out string? projectPath) ? WorkbenchProject.Load(projectPath) : null;
                string target = values.GetValueOrDefault("--target") ?? project?.Target ?? throw new ArgumentException("--target <test-target executable> is required.");
                catalog = ScenarioSelection.Runnable(await new ScenarioRunner().DiscoverRawAsync(target, project?.ScenarioFiles ?? [], LegacyFixtures(values, project), CancellationToken.None));
            }
            int refused = 0;
            foreach (var g in mappings)
            {
                string? problem = catalog is null ? null : set.BindingProblem(g.Key, catalog);
                if (problem is not null) refused++;
                output.WriteLine($"Mapping\t{g.Key} -> {g.First().Scenario}\t{g.Sum(e => e.Grids.Length)} key(s)\t"
                    + (catalog is null ? "binding not verified (give --target or --project)" : problem ?? "fixture binding verified"));
            }
            if (mappings.Length > 0) output.WriteLine($"Mappings: {mappings.Length}" + (catalog is null ? "" : $"; refused: {refused}"));
            return refused > 0 ? 2 : 0;
        }
        string run = values.GetValueOrDefault("--run") ?? throw new ArgumentException("--run <run directory with the reviewed candidates> is required.");
        string[] keys = (values.GetValueOrDefault("--candidate") ?? throw new ArgumentException("--candidate <key[,key...]> is required (no wildcard)."))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string key in set.Approve(run, keys, values.GetValueOrDefault("--confirm"), values.ContainsKey("--replace")))
            output.WriteLine("Approved: " + set.RelativeApproved(key, VisualBaselineSet.CandidateEnvironment(run, key)));
        return 0;
    }

    private static int? Number(Dictionary<string, string> values, string key) =>
        !values.TryGetValue(key, out string? text) ? null
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value
        : throw new ArgumentException(key + " needs a whole number.");

    internal static void Describe(ScenarioDescriptor s, TextWriter output, string indent)
    {
        foreach (string line in DescribeLines(s)) output.WriteLine(indent + line);
    }

    internal static IEnumerable<string> DescribeLines(ScenarioDescriptor s)
    {
        yield return $"{s.Name} [{s.Id}]";
        yield return s.Description;
        yield return $"Group: {s.Group}; kind: {s.Kind}; expected: {s.ExpectedResult}";
        yield return $"Grid: {s.Grid.Describe()}; defaults: {string.Join(", ", s.Grid.Defaults)}";
        if (s.External is { } external)
        {
            yield return "External JSON: " + external.Source;
            yield return s.Backend == "proxy-logical" ? "Backend: proxy-logical; ordinary owned-child target" : "Fixture: " + external.FixtureId + "; backend: " + s.Backend;
            if (external.FixtureHash.StartsWith(ExternalScenarios.ContractFingerprintPrefix, StringComparison.Ordinal))
                yield return "Fixture-only contract: " + external.FixtureHash;
        }
        if (s.Contract is { } contract)
        {
            yield return $"Role: fixture-only registration (not directly runnable); contract v{contract.ContractVersion}, revision {contract.Revision}";
            yield return "Checkpoint expectations: " + string.Join(", ", contract.Checkpoints);
            yield return "Actions: " + (s.Adapters?.Actions is { Length: > 0 } actions ? string.Join(", ", actions) : "none");
            yield return "Points: " + (s.Adapters?.Points is { Length: > 0 } points ? string.Join(", ", points) : "none");
            yield return "Contract fingerprint: " + ExternalScenarios.ContractFingerprint(s);
        }
        var safety = s.Safety ?? ScenarioSafety.None;
        yield return "Safety: " + (safety.RequiredCapabilities().Length == 0 && !safety.FixtureFiles ? "no external effects declared"
            : string.Join(", ", new[] { safety.FixtureFiles ? "fixture files" : null }.OfType<string>().Concat(safety.RequiredCapabilities())))
            + (safety.Notes is null ? "" : " — " + safety.Notes);
        if (s.Conditions is not null) yield return "Conditions: " + s.Conditions;
        foreach (var kv in s.Metadata ?? []) yield return $"Metadata: {kv.Key} = {kv.Value}";
        int n = 0;
        foreach (var step in s.Steps)
        {
            yield return $"  {++n,2}. {step.Kind} {step.Name}" + (step.Note is null ? "" : "  — " + step.Note)
                + (step.Evidence is { Length: > 0 } e ? "  [evidence: " + string.Join(", ", e) + "]" : "");
            foreach (var a in step.Assertions ?? []) yield return $"        assert {a.Id} ({a.Kind}{(a.Severity == AssertionSeverity.Warning ? ", warning" : "")})";
        }
    }

    // The explicit legacy-fixture allowance of this invocation (project "legacyFixtures" plus --legacy-fixture).
    internal static string[] LegacyFixtures(IReadOnlyDictionary<string, string> values, WorkbenchProject? project) =>
        [.. (project?.LegacyFixtures ?? []).Concat(values.TryGetValue("--legacy-fixture", out string? ids)
            ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : []).Distinct(StringComparer.Ordinal)];

    internal static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (!Flags.Contains(key) && !Options.Contains(key)) throw new ArgumentException("Unknown option '" + key + "'.\n" + Usage);
            string value = Flags.Contains(key) ? "true" : ++i < args.Length ? args[i] : throw new ArgumentException(key + " needs a value.");
            if (!values.TryAdd(key, value)) throw new ArgumentException("Duplicate option " + key + ".");
        }
        return values;
    }

    /// <summary>Empty unless the raw catalog has fixture-only registrations that no loaded external scenario binds (for example a
    /// target opened without its project); then one sentence that explains why they are not listed.</summary>
    internal static string FixtureOnlyNote(IReadOnlyCollection<ScenarioDescriptor> raw)
    {
        int unbound = raw.Count(f => f.IsFixtureOnly && !raw.Any(s => s.External?.FixtureId == f.Id));
        return unbound == 0 ? "" : $" {unbound} fixture-only registration{(unbound == 1 ? " is" : "s are")} not runnable on {(unbound == 1 ? "its" : "their")} own; "
            + "add the external scenario files that bind them (project scenario files).";
    }

    internal static string[] Groups(IEnumerable<ScenarioDescriptor> catalog) => catalog.SelectMany(s =>
        s.Group.Split('/').Select((_, i) => string.Join('/', s.Group.Split('/').Take(i + 1)))).Distinct().ToArray();

    internal const string Usage = """
        TSharpVision TestWorkbench — logical (Layer A) UI capture for TSharpVision test targets.

        Commands:
          list      --target <exe> [--verbose]          scenarios with grid range, defaults and checkpoints
          groups    --target <exe>                      groups and sub-groups with scenario counts
          describe  --target <exe> --scenario <id,id>   steps, checkpoints, evidence and assertions
          run       --target <exe> (--scenario <id,id> | --group <group> | --all) --output <dir> [options]
          tui       [--target <exe>] [--output <dir>] [--size ...]   interactive Workbench (same runner)
          diagnose  [<project>] [--target <exe>] [--proxy-backend <driver>] [--driver <native mode>]
                    static target diagnostics (files and metadata only; the target is not started);
                    exit 0 when nothing blocks the configured run, 2 when something does
          prepare   [--target] <exe> [--in-place] [--without console] [--root <dir>] [--execute]
                    plan (and with --execute perform) Prepare target: a prepared copy by default, the original is only read;
                    --in-place modifies the application folder (backup, rollback); exit 0 ok, 2 plan blocked, 1 failed
          baseline status  --baselines <manifest> [--project <p> | --target <t>]
                    approved / missing / invalid per gated checkpoint and explicit identity mappings (reads only);
                    with a target, each mapping's fixture binding is verified (exit 2 when one is refused)
          baseline approve --baselines <manifest> --run <run-dir> --candidate <key[,key...]> --confirm approve-<count> [--replace]
                    promote reviewed candidates of one run; the only command that writes approved baselines

        Project commands (same CLI and runner):
          run project.twproj --scenario <id>     or --project <path> with run/list/groups/describe
          project exit codes: 4 invalid project, 5 invalid external scenario, 6 unsupported capability,
            7 assertion/inspection failure, 8 timeout, 9 target failure; 0/1/2/3 retain their existing meanings.

        Run options:
          --legacy-fixture <id,id>         also bind fixture-bound JSON to these ordinary compiled scenarios (legacy
                                           fixtures); by default documents bind only to fixture-only registrations
          --proxy-backend NullDriver|SDLDriver|SDLGpuDriver|Win32ConsoleDriver  backend for explicitly proxy-logical JSON
          --proxy-capture none|renderer-pixels|gpu-completed|native-console-cells  opt-in separate native evidence; no fallback
          --size <WxH[,WxH...]|standard>   grids (20x6..200x100); default: each scenario's defaults (standard = 80x25,140x40)
          --inspect declared|all|none      semantic evidence per checkpoint (default declared)
          --timeout <ms>                   bound for one target operation, 100..30000 (default 10000)
          --scenario-timeout <s>           bound for one scenario execution, 5..600 (default 40)
          --allow <network,processes,devices>  capabilities scenarios may declare (default none)
          --include-expected-failures      --all/--group also select scenarios that are expected to fail
          --results <file>                 also write the structured batch result as JSON
          --baselines <manifest>           compare gated checkpoints with approved baselines; writes diff and candidate files
          --verbose                        print every non-passing assertion
          --driver NullDriver|SDLDriver|Win32ConsoleDriver|SDLGpuDriver|SDLGpuDriver-D3D12Fallback|SDLGpuDriver-Vulkan

        Output: one new flat <dir>/run-<UTC>-<id>/ with <scenario>--<checkpoint>--<WxH>.{png,tvs1,json}, run.json, report.md.
        Exit codes: 0 all passed, 1 a scenario did not pass or the run failed, 2 invalid arguments or baseline manifest,
          3 every scenario passed but the visual gate did not (mismatch, missing/invalid baseline, normalization error).
        """;
}
