using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using TSharpWorkbench.Testing;

// Target diagnostics. One UI-independent service answers "can this target run what is selected?" for Project
// Properties, the Workspace header, the Scenarios window, Run Configuration (preflight) and the CLI (`diagnose`).
// Static inspection reads files and assembly metadata only: it never starts the target and never loads a target assembly
// into the Workbench. What it cannot prove (that every dependency really loads) stays "runtime activation not verified"
// until an explicit Verify target or an actual run says otherwise.
internal enum DiagnosticSeverity { Ok, Info, Warning, Error }

// One finding: a stable code, its severity in the evaluated context, a short label (Workspace/Properties), the message,
// a detailed explanation, the corrective action, the relevant path and whether it blocks the selected operation.
internal sealed record DiagnosticFinding(string Code, DiagnosticSeverity Severity, string Component, string Short, string Message,
    string Detail, string Action, string? Path = null, bool Blocking = false);

// Metadata of one deployed assembly, read without loading it.
internal sealed record AssemblyFacts(string Name, string Path, Version? Version, string? Informational,
    IReadOnlyDictionary<string, Version?> References, string? Problem = null)
{
    internal bool Readable => Problem is null;
    internal string Display => Version is null ? Name : $"{Name} {Informational ?? Version.ToString()}";
}

// A screen driver a deployed assembly registers ([ScreenDriver] attribute), as the framework's own discovery would see it.
internal sealed record DriverRegistration(string Driver, int Platform, string Assembly);

// Context-free static facts about one executable and its folder.
internal sealed class TargetInspection
{
    internal required string Target { get; init; }
    internal string Folder => System.IO.Path.GetDirectoryName(Target) ?? "";
    internal bool Set => Target.Length > 0;
    internal bool Exists { get; init; }
    internal string? AccessProblem { get; init; }
    internal bool Portable { get; init; }
    internal AssemblyFacts? Main { get; init; }
    internal bool AppHost { get; init; }
    internal bool RuntimeConfigMissing { get; init; }
    internal AssemblyFacts? Framework { get; init; }
    internal AssemblyFacts? Proxy { get; init; }
    internal AssemblyFacts? Testing { get; init; }
    internal DriverRegistration[] Drivers { get; init; } = [];
    internal Dictionary<string, AssemblyFacts?> Assemblies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string?> Native { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool? HostsDiagnostics { get; init; }
    // The TSharpVision package version the application's deps.json names (null when not listed).
    internal string? FrameworkPackageVersion { get; init; }
    internal string Stamp { get; init; } = "";
    internal string ProxyPath => System.IO.Path.Combine(Folder, TargetDiagnostics.ProxyFile);
    internal string TestingPath => System.IO.Path.Combine(Folder, TargetDiagnostics.TestingFile);
    internal string FrameworkPath => System.IO.Path.Combine(Folder, "TSharpVision.dll");
    internal AssemblyFacts? Assembly(string name) => Assemblies.GetValueOrDefault(name);
}

// What the evaluated operation needs: Proxy scenarios (ordinary application), compiled or fixture-bound scenarios (diagnostic
// host), the Proxy display driver and evidence, and the native driver of a Native diagnostic run.
internal sealed record DiagnosticContext(bool Proxy, bool Diagnostic, string? ProxyBackend = null, string? ProxyCapture = null, string? NativeMode = null,
    bool Run = false)
{
    internal string Describe() => (Proxy, Diagnostic) switch
    {
        (true, true) => "Proxy and diagnostic-host scenarios",
        (true, false) => "Proxy scenarios (ordinary application)",
        (false, true) => "compiled or fixture-bound scenarios (diagnostic host)",
        _ => "no scenarios selected"
    } + (NativeMode is { } n ? ", Native diagnostic on " + WorkbenchCapabilities.TargetDriverName(n)
        : Proxy && ProxyBackend is { } b ? ", " + WorkbenchCapabilities.TargetDriverName(b) : "")
      + (Proxy && ProxyCapture is { } c && c != ProxyCaptureKinds.None ? ", native evidence " + WorkbenchCapabilities.EvidenceName(c) : "");
}

// What discovery and the attached documents know (null when the catalog of this target was never loaded).
internal sealed record CatalogFacts(bool Discovered, int Compiled, int Fixtures, int NativeEligible, string? DiscoveryFailure, AttachedScenario[] Attached);

// What actually happened at runtime for this deployment: an explicit Verify target, or the last run's launches.
internal sealed record RuntimeFacts(string Stamp)
{
    internal TargetVerification? Verified { get; init; }
    internal StartupFailure? VerifyFailure { get; init; }
    internal StartupFailure? LastStartupFailure { get; init; }
    internal bool LastRunConnected { get; init; }
}

internal sealed record TargetReport(TargetInspection Inspection, DiagnosticContext Context, DiagnosticFinding[] Findings,
    (string Label, DiagnosticSeverity Severity, string Text)[] Summary, string[] Modes, string[] Drivers, string[] Captures)
{
    internal DiagnosticFinding[] Blocking => Findings.Where(f => f.Blocking).ToArray();
    internal bool Ready => Blocking.Length == 0;
    // Short readiness for the Workspace header and Run Configuration: "Ready" or the first blocking problem.
    internal string Readiness => Blocking.FirstOrDefault() is { } first ? first.Short : "Ready";
}

internal static class TargetDiagnostics
{
    internal const string ProxyFile = "TSharpWorkbench.Proxy.dll", TestingFile = "TSharpWorkbench.Testing.dll";
    internal const string PrepareAction = "Prepare the application for Proxy testing: Settings > Prepare Target Copy... (also in Project Properties and Run Tests) "
        + "creates a prepared copy with TSharpWorkbench.Proxy.dll, TSharpWorkbench.Testing.dll and their dependencies, without modifying the original. "
        + "The scripts (scripts/prepare-tvdemo.ps1, scripts/prepare-proxy-ordinary.ps1) remain available for CI.";
    // The preparation service whose prepared-deployment status the diagnostics report (PREP findings). The
    // Workbench replaces it once its settings are known; tests install their own isolated instance.
    internal static TargetPreparationService Preparation { get; set; } = new();
    private static readonly string[] SdlNative = ["SDL3.dll", "SDL3_ttf.dll"];
    private static readonly string[] GpuNative = ["SDL3_shadercross.dll"];
    private static readonly string[] GpuNativeOptional = ["dxcompiler.dll", "dxil.dll", "spirv-cross-c-shared.dll"];
    private const int Windows = 0, MacOS = 1, Linux = 2;

    internal static string Label(DiagnosticSeverity severity) => severity switch
    { DiagnosticSeverity.Ok => "OK", DiagnosticSeverity.Info => "INFO", DiagnosticSeverity.Warning => "WARN", _ => "ERR" };

    // ── static inspection ────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, TargetInspection> cache = new(StringComparer.OrdinalIgnoreCase);
    // Bounded: an inspection is cached per executable path and folder state, so the Workspace and Run Configuration can
    // ask on every refresh. A changed executable, an added or removed file in its folder, or a replaced Proxy re-inspects.
    internal static TargetInspection Inspect(string target)
    {
        string full = target.Trim();
        if (full.Length > 0) { try { full = System.IO.Path.GetFullPath(full); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { } }
        string stamp = Stamp(full);
        lock (cache)
        {
            if (cache.TryGetValue(full, out var known) && known.Stamp == stamp) return known;
            var inspected = InspectNow(full, stamp);
            if (cache.Count > 32) cache.Clear();
            cache[full] = inspected;
            return inspected;
        }
    }
    internal static void Forget(string target) { lock (cache) cache.Remove(target.Trim()); }

    // Changes whenever the executable, its managed assembly or the folder's file list change (adding or deleting a file
    // updates the folder's write time) or the deployed Proxy/Testing/framework assemblies are replaced.
    internal static string Stamp(string full)
    {
        if (full.Length == 0) return "unset";
        try
        {
            static string Time(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Directory.Exists(path) ? "d" + Directory.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
            string folder = System.IO.Path.GetDirectoryName(full) ?? "";
            return string.Join("|", new[] { full, System.IO.Path.ChangeExtension(full, ".dll"), folder, System.IO.Path.Combine(folder, ProxyFile),
                System.IO.Path.Combine(folder, TestingFile), System.IO.Path.Combine(folder, "TSharpVision.dll") }.Select(Time));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unreadable"; }
    }

    private static TargetInspection InspectNow(string full, string stamp)
    {
        if (full.Length == 0) return new() { Target = "", Stamp = stamp };
        if (!File.Exists(full)) return new() { Target = full, Stamp = stamp };
        string? access = null; bool portable = false; AssemblyFacts? exe = null;
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            portable = pe.PEHeaders.PEHeader is not null;
        }
        catch (BadImageFormatException) { portable = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { access = ex.Message; }
        if (access is not null) return new() { Target = full, Exists = true, AccessProblem = access, Stamp = stamp };
        if (portable) exe = ReadAssembly(full);
        string folder = System.IO.Path.GetDirectoryName(full)!;
        string managedPath = System.IO.Path.ChangeExtension(full, ".dll");
        // A framework-dependent .NET executable is an apphost (native) that runs <name>.dll; an IL executable is managed itself.
        AssemblyFacts? main = exe is { Readable: true } ? exe : !string.Equals(managedPath, full, StringComparison.OrdinalIgnoreCase) && File.Exists(managedPath) ? ReadAssembly(managedPath) : null;
        bool appHost = main is not null && !ReferenceEquals(main, exe);
        bool runtimeConfigMissing = main is { Readable: true } && !File.Exists(System.IO.Path.ChangeExtension(main.Path, ".runtimeconfig.json"));
        var assemblies = new Dictionary<string, AssemblyFacts?>(StringComparer.OrdinalIgnoreCase);
        AssemblyFacts? Local(string name)
        {
            if (assemblies.TryGetValue(name, out var known)) return known;
            string path = System.IO.Path.Combine(folder, name + ".dll");
            return assemblies[name] = File.Exists(path) ? ReadAssembly(path) : null;
        }
        var framework = Local("TSharpVision");
        var proxy = Local("TSharpWorkbench.Proxy");
        var testing = Local("TSharpWorkbench.Testing");
        // Every non-platform assembly the Proxy and the registered drivers reference is looked up beside the executable.
        foreach (var facts in new[] { proxy, testing }.Where(f => f is { Readable: true }))
            foreach (string reference in facts!.References.Keys.Where(r => !Platform(r))) Local(reference);
        var drivers = Drivers(folder);
        foreach (string file in drivers.Select(d => d.Assembly).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Local(System.IO.Path.GetFileNameWithoutExtension(file)) is { Readable: true } facts)
                foreach (string reference in facts.References.Keys.Where(r => !Platform(r))) Local(reference);
        var native = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in SdlNative.Concat(GpuNative).Concat(GpuNativeOptional)) native[name] = FindNative(folder, name);
        return new()
        {
            Target = full, Exists = true, Portable = portable, Main = main, AppHost = appHost, RuntimeConfigMissing = runtimeConfigMissing,
            Framework = framework, Proxy = proxy, Testing = testing, Drivers = drivers, Assemblies = assemblies, Native = native,
            HostsDiagnostics = main is null ? null : ScenarioFiles.HostsDiagnostics(full), Stamp = stamp,
            FrameworkPackageVersion = main is null ? null : PackageVersion(System.IO.Path.ChangeExtension(main.Path, ".deps.json"), "TSharpVision")
        };
    }
    private static string? PackageVersion(string deps, string package)
    {
        global::Preparation.ApplicationAssets(deps, out var packages);
        return packages.GetValueOrDefault(package);
    }

    private static bool Platform(string name) => name is "mscorlib" or "netstandard" || name.StartsWith("System", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);

    // Native libraries resolve beside the executable or from the deployment's runtimes/<rid>/native folders.
    private static string? FindNative(string folder, string name)
    {
        string local = System.IO.Path.Combine(folder, name);
        if (File.Exists(local)) return local;
        string runtimes = System.IO.Path.Combine(folder, "runtimes");
        try
        {
            if (!Directory.Exists(runtimes)) return null;
            foreach (string rid in Directory.EnumerateDirectories(runtimes, OperatingSystem.IsWindows() ? "win*" : "*").Take(16))
            {
                string candidate = System.IO.Path.Combine(rid, "native", name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static AssemblyFacts ReadAssembly(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        var empty = new Dictionary<string, Version?>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return new(name, path, null, null, empty, "not a .NET assembly (no metadata)");
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return new(name, path, null, null, empty, "a .NET module without an assembly manifest");
            var definition = md.GetAssemblyDefinition();
            var references = new Dictionary<string, Version?>(StringComparer.Ordinal);
            foreach (var handle in md.AssemblyReferences)
            {
                var reference = md.GetAssemblyReference(handle);
                references.TryAdd(md.GetString(reference.Name), reference.Version);
            }
            string? informational = null;
            try { informational = FileVersionInfo.GetVersionInfo(path).ProductVersion; } catch (Exception ex) when (ex is IOException or ArgumentException) { }
            return new(md.GetString(definition.Name), path, definition.Version, string.IsNullOrWhiteSpace(informational) ? null : informational, references);
        }
        catch (BadImageFormatException) { return new(name, path, null, null, empty, "not a valid executable image"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(name, path, null, null, empty, "cannot be read: " + ex.Message); }
    }

    // [ScreenDriver] registrations of the folder's assemblies, read from metadata with the same candidate filter the
    // framework's discovery uses (assemblies that name ScreenDriverAttribute). Nothing is loaded.
    private static DriverRegistration[] Drivers(string folder)
    {
        var found = new List<DriverRegistration>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.dll").Take(400).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        foreach (string file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) continue;
                bool core = md.StringComparer.Equals(md.GetAssemblyDefinition().Name, "TSharpVision");
                if (!core && !md.TypeReferences.Any(h => md.StringComparer.Equals(md.GetTypeReference(h).Name, "ScreenDriverAttribute"))) continue;
                foreach (var typeHandle in md.TypeDefinitions)
                    foreach (var attributeHandle in md.GetTypeDefinition(typeHandle).GetCustomAttributes())
                    {
                        var attribute = md.GetCustomAttribute(attributeHandle);
                        if (AttributeType(md, attribute) != "ScreenDriverAttribute") continue;
                        var value = attribute.DecodeValue(AttributeTypes.Instance);
                        string? driver = value.NamedArguments.FirstOrDefault(a => a.Name == "Driver").Value as string;
                        int platform = value.NamedArguments.FirstOrDefault(a => a.Name == "System").Value is int p ? p : Windows;
                        if (!string.IsNullOrWhiteSpace(driver)) found.Add(new(driver, platform, System.IO.Path.GetFileName(file)));
                    }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        return found.ToArray();
    }
    private static string? AttributeType(MetadataReader md, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                return parent.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) : null;
            case HandleKind.MethodDefinition:
                return md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()).Name);
            default: return null;
        }
    }
    private sealed class AttributeTypes : ICustomAttributeTypeProvider<string>
    {
        internal static readonly AttributeTypes Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        // The only enum on ScreenDriverAttribute is Platform (int).
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }

    // ── the Workbench's own families ────────────────────────────────────────────────────

    internal static Version? WorkbenchFramework => typeof(TSharpVision.TView).Assembly.GetName().Version;
    internal static string? WorkbenchFrameworkBuild => typeof(TSharpVision.TView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    internal static Version? WorkbenchTesting => typeof(ScenarioRunner).Assembly.GetName().Version;
    // SHA-256 of the framework assembly this Workbench runs on, and of a deployed one (cached per file state).
    private static readonly Lazy<string?> ownFrameworkHash = new(() => FrameworkHash(typeof(TSharpVision.TView).Assembly.Location));
    internal static string? WorkbenchFrameworkHash => ownFrameworkHash.Value;
    private static readonly Lazy<AssemblyFacts> ownFramework = new(() => ReadAssembly(typeof(TSharpVision.TView).Assembly.Location));
    internal static AssemblyFacts OwnFramework => ownFramework.Value;
    private static readonly Dictionary<string, (string Stamp, string Hash)> frameworkHashes = new(StringComparer.OrdinalIgnoreCase);
    internal static string? FrameworkHash(string? path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path)) return null;
        try
        {
            var info = new FileInfo(path);
            string stamp = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            lock (frameworkHashes) if (frameworkHashes.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Hash;
            string hash = global::Preparation.FileHash(path);
            lock (frameworkHashes) { if (frameworkHashes.Count > 64) frameworkHashes.Clear(); frameworkHashes[path] = (stamp, hash); }
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private static int CurrentPlatform => OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsMacOS() ? MacOS : Linux;
    private static string Family(Version? v) => v is null ? "unknown" : $"{v.Major}.x";
    private static string Build(string? informational) => informational is null ? "" : informational.Split('+')[0];

    // ── evaluation ───────────────────────────────────────────────────────────────────

    internal static TargetReport Evaluate(TargetInspection i, DiagnosticContext c, CatalogFacts? catalog = null, RuntimeFacts? runtime = null)
    {
        var findings = new List<DiagnosticFinding>();
        void Add(string code, DiagnosticSeverity severity, string component, string shortText, string message, string detail, string action, string? path = null, bool blocking = false)
            => findings.Add(new(code, severity, component, shortText, message, detail, action, path, blocking && severity == DiagnosticSeverity.Error));
        bool any = c.Proxy || c.Diagnostic || !c.Run;
        string exeName = System.IO.Path.GetFileName(i.Target);

        // Executable
        if (!i.Set) Add("EXE001", DiagnosticSeverity.Error, "Executable", "No target", "No target executable is set.",
            "The project has no application executable, so nothing can be inspected or run.", "Set the executable in Project Properties (Alt+O).", null, true);
        else if (!i.Exists) Add("EXE002", DiagnosticSeverity.Error, "Executable", "Target missing", $"The target executable does not exist: {exeName}.",
            "The file was moved, renamed or deleted, or the path is wrong.", "Correct the executable in Project Properties (Alt+O), or rebuild the target.", i.Target, true);
        else if (i.AccessProblem is { } access) Add("EXE003", DiagnosticSeverity.Error, "Executable", "Target unreadable", $"The target executable cannot be read: {exeName}.",
            access, "Check the file's permissions, or whether another program holds it.", i.Target, true);
        else if (!i.Portable) Add("EXE004", DiagnosticSeverity.Error, "Executable", "Not an executable", $"{exeName} is not a Windows or .NET executable.",
            "The file has no executable image header.", "Select the application's executable (.exe) in Project Properties (Alt+O).", i.Target, true);
        else if (i.Main is null) Add("EXE005", DiagnosticSeverity.Warning, "Executable", "Not inspectable", $"{exeName} has no .NET application assembly beside it.",
            "Static inspection needs a framework-dependent .NET application (<name>.exe with <name>.dll). This may be a native program or a single-file bundle; "
            + "neither the framework nor the Proxy can be inspected.", "Use the framework-dependent build of the TSharpVision application.", i.Target);
        else if (!i.Main.Readable) Add("EXE006", DiagnosticSeverity.Error, "Executable", "Assembly unreadable", $"The application assembly {System.IO.Path.GetFileName(i.Main.Path)} cannot be inspected.",
            i.Main.Problem!, "Rebuild or redeploy the application.", i.Main.Path, true);
        else if (i.RuntimeConfigMissing) Add("EXE007", DiagnosticSeverity.Error, "Executable", "Deployment incomplete", $"{System.IO.Path.GetFileNameWithoutExtension(i.Main.Path)}.runtimeconfig.json is missing.",
            "A framework-dependent .NET application cannot start without its runtime configuration file.", "Redeploy the complete build output of the application.",
            System.IO.Path.ChangeExtension(i.Main.Path, ".runtimeconfig.json"), true);
        else if (OperatingSystem.IsWindows() && !string.Equals(System.IO.Path.GetExtension(i.Target), ".exe", StringComparison.OrdinalIgnoreCase))
            Add("EXE008", DiagnosticSeverity.Warning, "Executable", "Unusual executable", $"{exeName} is not an .exe file.",
                "On Windows the Workbench starts the target directly; a .dll must be started through its apphost (.exe).", "Select the .exe of the application.", i.Target);
        else Add("EXE000", DiagnosticSeverity.Ok, "Executable", exeName, $"{exeName}" + (i.AppHost ? $" (apphost for {i.Main.Name}.dll)" : ""),
            "The executable exists and its .NET application assembly was inspected (metadata only; nothing was started).", "", i.Target);
        bool inspectable = i.Exists && i.AccessProblem is null && i.Main is { Readable: true };

        // Framework
        Version? workbench = WorkbenchFramework;
        if (inspectable)
        {
            if (i.Framework is null)
                Add("FW001", DiagnosticSeverity.Error, "Framework", "Framework missing", "TSharpVision.dll is missing beside the executable.",
                    $"Searched {i.FrameworkPath}. A TSharpVision application deploys its framework assembly with it.", "Redeploy the complete build output of the application.",
                    i.FrameworkPath, any);
            else if (!i.Framework.Readable)
                Add("FW004", DiagnosticSeverity.Warning, "Framework", "Framework unknown", "The deployed TSharpVision.dll cannot be inspected.", i.Framework.Problem!,
                    "Redeploy the application.", i.Framework.Path);
            else if (i.Framework.Version is null || workbench is null)
                Add("FW004", DiagnosticSeverity.Warning, "Framework", "Framework unknown", "The framework identity cannot be determined statically.",
                    "The assembly has no version; compatibility is decided by the target's handshake.", "", i.Framework.Path);
            else if (Family(i.Framework.Version) != Family(workbench))
                Add("FW002", c.Proxy ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "Framework", "Framework incompatible",
                    $"The target uses TSharpVision {Family(i.Framework.Version)}; this Workbench uses TSharpVision {Family(workbench)}.",
                    $"Deployed {i.Framework.Display}; Workbench {Family(workbench)} ({Build(WorkbenchFrameworkBuild)}). The Workbench Proxy is built for its own framework family"
                    + (c.Proxy ? "." : "; a diagnostic host decides compatibility at its handshake."),
                    "Use a target built with the same TSharpVision family as the Workbench.", i.Framework.Path, c.Proxy);
            // Matching-major builds are candidates for runtime verification, irrespective of minor/patch/build
            // identity. Protocol negotiation and scenarios still determine actual supported functionality.
            else if (FrameworkCompatibility.Compare(i.Framework, i.FrameworkPackageVersion, OwnFramework, WorkbenchFrameworkBuild ?? workbench.ToString(3),
                FrameworkHash(i.Framework.Path), WorkbenchFrameworkHash) is { Match: FrameworkMatch.DifferentVersion or FrameworkMatch.Unknown } other)
                Add("FW002", c.Proxy ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "Framework", "Framework incompatible",
                    $"The target uses TSharpVision {other.Declared}; this Workbench uses TSharpVision {other.Expected}.",
                    other.Describe() + " " + FrameworkCompatibility.Policy,
                    $"Rebuild the application with TSharpVision {other.Expected}, or use a Workbench built for TSharpVision {other.Declared}.", i.Framework.Path, c.Proxy);
            else if (FrameworkHash(i.Framework.Path) is { } deployedHash && WorkbenchFrameworkHash is { } ownHash && deployedHash != ownHash)
            {
                bool proven = runtime?.Verified is not null;
                Add("FW003", DiagnosticSeverity.Info, "Framework", "Same major, runtime verification required",
                    $"TSharpVision {FrameworkCompatibility.Declared(i.Framework)} is in the Workbench's supported major (SHA-256 {deployedHash[..12]}, Workbench {ownHash[..12]}).",
                    FrameworkCompatibility.Policy + " " + (proven ? "Verify target connected this deployment in this session." : "Verify target confirms that the Proxy loads this build."),
                    "", i.Framework.Path);
            }
            else
                Add("FW000", DiagnosticSeverity.Ok, "Framework", "TSharpVision " + Family(i.Framework.Version), $"TSharpVision {(Build(i.Framework.Informational) is { Length: > 0 } b ? b : i.Framework.Version.ToString())} (same family as the Workbench)",
                    Build(i.Framework.Informational) == Build(WorkbenchFrameworkBuild) || i.Framework.Informational is null ? "Identical framework build as the Workbench."
                        : $"Same family {Family(workbench)}; the build differs from the Workbench's {Build(WorkbenchFrameworkBuild)}. File identity is not proof that it loads; runtime activation is verified separately.",
                    "", i.Framework.Path);
        }

        // Proxy (ordinary applications). Required only when the operation runs Proxy scenarios.
        string ProxyContext(string what) => c.Proxy ? what : what + " Not needed for compiled or fixture-bound diagnostic scenarios.";
        if (inspectable)
        {
            var proxySeverity = c.Proxy ? DiagnosticSeverity.Error : DiagnosticSeverity.Info;
            if (i.Proxy is null)
                Add("PROXY001", proxySeverity, "Proxy", "Proxy missing", "Proxy assembly missing: TSharpWorkbench.Proxy.dll.",
                    $"Required assembly: {ProxyFile}\nTarget: {i.Target}\nSearched: {i.ProxyPath}\n"
                    + ProxyContext(c.Proxy ? "The selected scenarios require the Workbench Proxy, but its assembly was not found in the target deployment. "
                        + "The application would exit before connecting (assembly load failure)." : "The Workbench Proxy is not deployed with this application."),
                    PrepareAction, i.ProxyPath, true);
            else if (!i.Proxy.Readable || i.Proxy.Name != "TSharpWorkbench.Proxy")
                Add("PROXY006", proxySeverity, "Proxy", "Proxy unreadable", "The deployed TSharpWorkbench.Proxy.dll is not a usable Proxy assembly.",
                    i.Proxy.Problem ?? $"Its assembly name is {i.Proxy.Name}.", PrepareAction, i.Proxy.Path, true);
            else
            {
                bool problem = false;
                if (i.Testing is null)
                {
                    problem = true;
                    Add("PROXY002", proxySeverity, "Proxy", "Testing missing", "TSharpWorkbench.Testing.dll is missing beside the executable.",
                        $"The Proxy references TSharpWorkbench.Testing (protocol and logical capture). Searched: {i.TestingPath}.", PrepareAction, i.TestingPath, true);
                }
                foreach (var (name, wanted) in i.Proxy.References.Where(r => !Platform(r.Key)))
                {
                    var deployed = i.Assembly(name);
                    // TSW PRODUCT-3: the SDL driver assembly is needed by Proxy sessions on every backend, NullDriver included:
                    // the Proxy's handshake type-tests the SDL GPU driver, so a deployment without it fails at runtime with
                    // "Proxy session failed: FileNotFoundException" (observed with ordinary ProxyOrdinary, Proxy + Testing only).
                    // Outside a Proxy context it stays a driver matter (checked with the driver).
                    if (deployed is null && (name == "TSharpWorkbench.Testing" || name == "TSharpVision.Drivers.SDL" && !c.Proxy)) continue;
                    if (deployed is null)
                    {
                        problem = true;
                        Add("PROXY007", proxySeverity, "Proxy", "Proxy dependency missing", $"{name}.dll, which the Proxy references, is missing.",
                            $"Searched {System.IO.Path.Combine(i.Folder, name + ".dll")}."
                            + (name == "TSharpVision.Drivers.SDL" ? " The Proxy loads it during its handshake on every display driver (headless included); without it the session fails with FileNotFoundException." : ""),
                            PrepareAction, System.IO.Path.Combine(i.Folder, name + ".dll"), true);
                    }
                    else if (wanted is not null && deployed.Version is { } have && (have.Major != wanted.Major || have < wanted))
                    {
                        problem = true;
                        Add("PROXY003", proxySeverity, "Proxy", "Proxy incompatible", $"The Proxy was built for {name} {wanted}; the deployment has {have}.",
                            ".NET does not load an assembly older than (or of another major version than) the one the Proxy was built against: the target would fail at startup.",
                            "Prepare the deployment with a Proxy built from the same framework and Workbench build as the application.", deployed.Path, true);
                    }
                }
                if (i.Testing is { Readable: true, Version: { } deployedTesting } && WorkbenchTesting is { } controller && deployedTesting != controller)
                    Add("PROXY005", DiagnosticSeverity.Warning, "Proxy", "Testing version differs", $"The deployed TSharpWorkbench.Testing is {deployedTesting}; this Workbench uses {controller}.",
                        "The wire protocol schema is checked at the handshake; a different build may be refused there.", "Redeploy the Proxy from this Workbench build.", i.TestingPath);
                // An in-place Proxy from another Workbench build passes every check above (same assembly versions) but
                // may predate newer capture (Smart Recorder, picks): a recording then silently falls back to input only.
                // Prepared copies are compared with their manifest instead (PREP001).
                bool stale = !problem && i.Testing is not null && Preparation.Payload is { } payload && Preparation.Status(i.Folder) is null
                    && (global::Preparation.FileHash(i.Proxy.Path) != payload.ProxySha256 || global::Preparation.Mvid(i.TestingPath) is { } mvid && mvid != payload.TestingMvid);
                if (stale)
                    Add("PROXY008", DiagnosticSeverity.Warning, "Proxy", "Proxy from another build", "The deployed Workbench Proxy comes from another Workbench build.",
                        "Its files differ from this Workbench's Proxy payload. The versions match, so it loads, but features newer than that build "
                        + "(Smart capture checkpoints, Ctrl+Shift picks) may be missing: recordings then contain input only.",
                        "Fix Issues replaces it after your approval (with a backup); or Settings > Prepare Target Copy... makes a fresh prepared copy.", i.Proxy.Path);
                if (!problem && !stale)
                    Add("PROXY000", DiagnosticSeverity.Ok, "Proxy", "Proxy found", "Proxy assembly found; deployment appears complete.",
                        $"{i.Proxy.Display} and {i.Testing!.Display} are deployed beside the executable; every assembly the Proxy references was found. "
                        + "Runtime activation is not verified by this inspection.", "", i.Proxy.Path);
            }
        }

        // Prepared deployment: a copy made by Prepare target carries preparation.json. Its files and identities are
        // checked against the manifest; an incomplete or incompatible copy is never presented as ready.
        if (i.Exists && i.AccessProblem is null && Preparation.Status(i.Folder) is { } prepared)
        {
            string reasons = prepared.Reasons.Length == 0 ? "" : "\n" + string.Join("\n", prepared.Reasons.Take(6));
            string origin = $"Prepared copy of {prepared.Source} ({prepared.Manifest?.PreparedUtc.ToLocalTime():yyyy-MM-dd HH:mm}).";
            switch (prepared.State)
            {
                case PreparedState.Ready:
                    Add("PREP000", DiagnosticSeverity.Ok, "Preparation", "Prepared: Ready", "Prepared target: Ready.", origin + " Files and identities match its manifest." + reasons, "", i.Folder);
                    break;
                case PreparedState.Outdated:
                    Add("PREP001", DiagnosticSeverity.Warning, "Preparation", "Prepared: Outdated", "Prepared target: Outdated - " + prepared.Reasons[0] + ".", origin + reasons,
                        "Re-prepare (Settings > Prepare Target Copy...) creates a fresh prepared copy; this one stays usable until then.", i.Folder);
                    break;
                default:
                    Add(prepared.State == PreparedState.Incomplete ? "PREP002" : "PREP003", DiagnosticSeverity.Error, "Preparation", "Prepared: " + prepared.State,
                        $"Prepared target: {prepared.State} - {prepared.Reasons.FirstOrDefault()}.", origin + reasons,
                        "Re-prepare the target (Settings > Prepare Target Copy...), or select the original application in Project Properties (Alt+O).", i.Folder, any);
                    break;
            }
        }

        // Diagnostic host (compiled scenarios and fixtures the target advertises itself).
        if (inspectable || i.Exists)
        {
            var needed = c.Diagnostic ? DiagnosticSeverity.Error : DiagnosticSeverity.Info;
            if (catalog is { Discovered: true } && catalog.Compiled + catalog.Fixtures > 0)
                Add("HOST000", DiagnosticSeverity.Ok, "Diagnostic host", "Host available", $"Diagnostic host available: {catalog.Compiled} compiled scenario{(catalog.Compiled == 1 ? "" : "s")}, {catalog.Fixtures} fixture{(catalog.Fixtures == 1 ? "" : "s")} advertised.",
                    "The target answered the catalog handshake (last catalog refresh).", "", i.Target);
            else if (catalog?.DiscoveryFailure is { } failure)
                Add("HOST003", needed == DiagnosticSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "Diagnostic host", "Host incompatible",
                    "The diagnostic host did not complete its catalog handshake.", failure, "Rebuild the diagnostic target with this Workbench's TSharpWorkbench.Testing, then refresh with F2.", i.Target, c.Diagnostic);
            else if (i.HostsDiagnostics == false)
                Add("HOST001", needed, "Diagnostic host", "Host not installed", "Diagnostic host not installed: the application does not reference TSharpWorkbench.Testing.",
                    "Compiled diagnostic scenarios and fixtures exist only in a diagnostic build that registers them; this is an ordinary application.",
                    c.Diagnostic ? "Select the diagnostic build that registers the scenarios or fixtures, or use Black-Box Mode scenarios." : "", i.Target, c.Diagnostic);
            else if (i.HostsDiagnostics == true)
                Add("HOST002", c.Diagnostic && catalog is { Discovered: true } ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, "Diagnostic host", "Host not verified",
                    "Diagnostic host not yet verified: the application references TSharpWorkbench.Testing.",
                    "An assembly reference is not proof of a registered host; only the catalog handshake (F2) or Verify target confirms it.",
                    "Refresh Scenario Manager with F2.", i.Target);
            else if (i.Exists && i.AccessProblem is null)
                Add("HOST004", DiagnosticSeverity.Info, "Diagnostic host", "Host unknown", "Whether the application hosts diagnostics cannot be determined statically.",
                    "Its managed assembly could not be inspected.", "Refresh Scenario Manager with F2.", i.Target);
            // Fixture-bound documents: the existing binding checks, explained (never another fixture, never a Proxy fallback).
            foreach (var a in catalog?.Attached.Where(a => a.Info.Document is { } d && !a.Info.Proxy && a.Problem is not null) ?? [])
                Add(a.Status == "Not bound" ? "FIX003" : "FIX001", a.Status == "Not bound" ? DiagnosticSeverity.Info : DiagnosticSeverity.Error, "Fixture binding",
                    a.Status == "Not bound" ? "Fixture not checked" : "Fixture unavailable",
                    $"{System.IO.Path.GetFileName(a.Path)}: fixture '{a.Info.Document!.Fixture}' {(a.Status == "Not bound" ? "not checked yet" : "is not usable")}.",
                    a.Problem!, a.Status == "Not bound" ? "Refresh Scenario Manager with F2." : "Select the diagnostic build that advertises this fixture; no other fixture and no Proxy mode is substituted.", a.Path);
        }

        // Drivers and native dependencies of the selected display driver (Proxy backend or Native diagnostic mode).
        string? selected = c.NativeMode is { } nm ? NativeExecution.ActualDriver(nm) : c.Proxy ? c.ProxyBackend : null;
        var driverRows = new List<string>();
        foreach (string backend in WorkbenchCapabilities.ProxyBackends)
        {
            string? reason = inspectable ? DriverProblem(i, backend) : "not inspected";
            driverRows.Add($"{WorkbenchCapabilities.TargetDriverName(backend),-38} {(reason is null ? "deployed" : "unavailable: " + reason)}");
            if (selected == backend && reason is not null && inspectable)
            {
                bool native = c.NativeMode is not null;
                Add(DriverCode(i, backend), DiagnosticSeverity.Error, "Display driver", "Driver unavailable",
                    $"{WorkbenchCapabilities.TargetDriverName(backend)} is not available in the target deployment: {reason}.",
                    "The target's own TSharpVision driver must be deployed with it; the Workbench never substitutes another driver.",
                    native ? "Deploy the driver with the diagnostic target, or choose another target display." : "Choose another target display, or deploy the driver with the application.",
                    i.Folder, true);
            }
        }
        if (selected is not null && NativeExecution.IsGpu(c.NativeMode ?? selected) && inspectable && DriverProblem(i, "SDLGpuDriver") is null)
            foreach (string name in GpuNativeOptional.Where(n => i.Native.GetValueOrDefault(n) is null))
                Add("DRV006", DiagnosticSeverity.Warning, "Display driver", "GPU component missing", $"{name} was not found in the deployment.",
                    "SDL GPU shader translation uses it on some paths; static inspection cannot tell whether this path needs it.", "Deploy the complete SDL GPU runtime.", i.Folder);

        // Runtime: what actually happened, never inferred from files.
        if (runtime?.VerifyFailure is { } vf)
            Add("RUN003", DiagnosticSeverity.Error, "Runtime", vf.Title, "Verify target failed: " + vf.Reason, vf.Technical, vf.Action, i.Target, false);
        else if (runtime?.Verified is { } v)
            Add("RUN002", DiagnosticSeverity.Ok, "Runtime", "Verified", (v.Proxy ? "Proxy activation verified" : "Diagnostic host verified")
                + $": {v.Identity.Assembly} connected on {v.Identity.Driver} {v.Identity.Width}x{v.Identity.Height}" + (v.Scenarios is { } n ? $", {n} registrations advertised" : "") + ".",
                $"Verify target started the target once, completed the handshake and closed it ({v.StopOutcome}; cleanup {(v.Cleanup ? "complete" : "INCOMPLETE")}). "
                + "Other display drivers were not activated.", "", i.Target);
        if (runtime?.LastStartupFailure is { } sf)
            Add("RUN004", DiagnosticSeverity.Error, "Runtime", sf.Title, "The last run could not start the target: " + sf.Reason, sf.Technical, sf.Action, i.Target, false);
        else if (runtime?.LastRunConnected == true && runtime.Verified is null && runtime.VerifyFailure is null)
            Add("RUN005", DiagnosticSeverity.Ok, "Runtime", "Connected in last run", "The target started and connected in the last run.", "Observed during an actual scenario execution.", "", i.Target);
        if (runtime is null || runtime.Verified is null && runtime.VerifyFailure is null && runtime.LastStartupFailure is null && !runtime.LastRunConnected)
            Add("RUN001", DiagnosticSeverity.Info, "Runtime", "Not verified", "Runtime activation not verified.",
                "Static inspection reads files and metadata only; it cannot prove that every managed and native dependency loads. "
                + "Verify target (Project Properties or Settings > Target Diagnostics...) starts the target once without running a scenario.", "", i.Target);

        // Available modes and capture types for this deployment (static).
        bool proxyReady = inspectable && !findings.Any(f => f.Component == "Proxy" && f.Code is not ("PROXY000" or "PROXY005" or "PROXY008"));
        string hostState = findings.FirstOrDefault(f => f.Component == "Diagnostic host")?.Code switch
        { "HOST000" => "available", "HOST001" => "not installed", "HOST002" => "not yet verified (F2)", "HOST003" => "incompatible", _ => "unknown" };
        var modes = new List<string>
        {
            "Functional / Visual regression, Proxy (ordinary application): " + (proxyReady ? "available (static check)" : "unavailable - " + (findings.FirstOrDefault(f => f.Component == "Proxy" && f.Severity != DiagnosticSeverity.Ok)?.Short ?? "target not inspectable")),
            "Functional / Visual regression, compiled diagnostic host: " + hostState,
            "Native diagnostic: " + (!OperatingSystem.IsWindows() ? "unavailable - native drivers run on Windows only"
                : catalog is { NativeEligible: > 0 } ? $"available for {catalog.NativeEligible} audited workflow{(catalog.NativeEligible == 1 ? "" : "s")}"
                : "unavailable - no audited diagnostic workflows in the catalog (compiled, or external scenarios bound to an audited fixture)")
        };
        var captures = new List<string>();
        foreach (string kind in ProxyCaptureKinds.All.Where(k => k != ProxyCaptureKinds.None))
        {
            var backends = WorkbenchCapabilities.ProxyBackends.Where(b => ProxyCaptureKinds.ForBackend(b).Contains(kind)).ToArray();
            var ok = backends.Where(b => inspectable && DriverProblem(i, b) is null).ToArray();
            captures.Add($"{WorkbenchCapabilities.EvidenceName(kind),-22} " + (!proxyReady ? "unavailable - Proxy deployment not ready"
                : ok.Length == 0 ? "unavailable - no " + string.Join(" or ", backends.Select(WorkbenchCapabilities.TargetDriverName)) + " deployed"
                : "with " + string.Join(", ", ok.Select(WorkbenchCapabilities.TargetDriverName))));
        }
        captures.Add("Logical cells, TVS1 and logical PNG: always (every mode)");

        // Compact summary rows (Project Properties, Workspace).
        DiagnosticFinding? First(string component) => findings.Where(f => f.Component == component).OrderByDescending(f => f.Severity).FirstOrDefault();
        (string, DiagnosticSeverity, string) Row(string label, string component, string fallback)
            => First(component) is { } f ? (label, f.Severity, f.Severity == DiagnosticSeverity.Ok ? f.Short : f.Short) : (label, DiagnosticSeverity.Info, fallback);
        var blocking = findings.Where(f => f.Blocking).ToArray();
        string readiness = blocking.Length > 0
            ? (c.Proxy && blocking.Any(b => b.Component is "Proxy" or "Framework") ? "Proxy scenarios cannot run"
                : c.Diagnostic && blocking.Any(b => b.Component == "Diagnostic host") ? "Diagnostic scenarios cannot run" : blocking[0].Short)
            : runtime?.Verified is not null || runtime?.LastRunConnected == true ? "Ready (runtime verified)" : !any ? "No scenarios selected" : "Ready (static checks; runtime not verified)";
        var summary = new[]
        {
            Row("Executable", "Executable", "-"), Row("Framework", "Framework", "not inspected"), Row("Proxy", "Proxy", "not inspected"),
            Row("Diagnostic host", "Diagnostic host", "not inspected"),
            ("Test readiness", blocking.Length > 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Ok, readiness)
        };
        return new(i, c, findings.ToArray(), summary, modes.ToArray(), driverRows.ToArray(), captures.ToArray());
    }

    private static string DriverCode(TargetInspection i, string backend) =>
        !Registered(i, backend) ? "DRV001" : DriverAssemblyProblem(i, backend) is not null ? "DRV002" : backend == "Win32ConsoleDriver" ? "DRV005" : "DRV003";
    private static bool Registered(TargetInspection i, string backend) => i.Drivers.Any(d => d.Driver == backend && d.Platform == CurrentPlatform);
    private static string? DriverAssemblyProblem(TargetInspection i, string backend)
    {
        var registration = i.Drivers.FirstOrDefault(d => d.Driver == backend && d.Platform == CurrentPlatform);
        if (registration is null) return null;
        var facts = i.Assembly(System.IO.Path.GetFileNameWithoutExtension(registration.Assembly));
        if (facts is not { Readable: true }) return registration.Assembly + " cannot be inspected";
        string? missing = facts.References.Keys.Where(r => !Platform(r)).FirstOrDefault(r => i.Assembly(r) is null);
        return missing is null ? null : missing + ".dll (referenced by " + registration.Assembly + ") is missing";
    }
    // Why the deployment cannot run on this display driver, or null. Determined from the deployed assemblies' own
    // [ScreenDriver] registrations and their references, plus the native libraries SDL loads.
    internal static string? DriverProblem(TargetInspection i, string backend)
    {
        if (backend == "Win32ConsoleDriver" && !OperatingSystem.IsWindows()) return "Windows only";
        if (!Registered(i, backend)) return backend == "NullDriver" ? "TSharpVision.dll does not register it" : $"no deployed assembly registers {backend}";
        if (DriverAssemblyProblem(i, backend) is { } problem) return problem;
        if (backend is "SDLDriver" or "SDLGpuDriver" && OperatingSystem.IsWindows())
        {
            string[] missing = SdlNative.Concat(backend == "SDLGpuDriver" ? GpuNative : []).Where(n => i.Native.GetValueOrDefault(n) is null).ToArray();
            if (missing.Length > 0) return "native " + string.Join(", ", missing) + " missing";
        }
        return null;
    }

    // ── context and report text ─────────────────────────────────────────────────────────

    // Context of a selection of scenarios (Run Configuration and the runner's revalidation).
    internal static DiagnosticContext ForRun(IEnumerable<ScenarioDescriptor> run, WorkbenchProject p)
    {
        var list = run.ToArray();
        bool native = p.TestType == "rendering";
        bool proxy = !native && list.Any(s => s.Backend == "proxy-logical");
        bool diagnostic = list.Any(s => s.Backend != "proxy-logical");
        return new(proxy, diagnostic, proxy ? p.ProxyBackend : null, proxy ? p.ProxyCapture : null, native ? p.Mode : null, Run: true);
    }

    internal static IEnumerable<string> Lines(TargetReport r)
    {
        yield return "Static deployment inspection: files and assembly metadata were read; the target was not started.";
        yield return "Target       " + (r.Inspection.Set ? r.Inspection.Target : "not set");
        yield return "Context      " + r.Context.Describe();
        yield return "Readiness    " + r.Summary[^1].Text;
        foreach (var (title, component) in new[] { ("Executable", "Executable"), ("Framework", "Framework"), ("Proxy capabilities", "Proxy"),
            ("Prepared deployment", "Preparation"), ("Diagnostic host capabilities", "Diagnostic host"), ("Fixture bindings", "Fixture binding"), ("Deployment dependencies", "Display driver"), ("Runtime", "Runtime") })
        {
            var items = r.Findings.Where(f => f.Component == component).ToArray();
            if (items.Length == 0 && component is "Fixture binding" or "Display driver" or "Preparation") continue;
            yield return "";
            yield return title;
            if (items.Length == 0) yield return "  -     not inspected";
            foreach (var f in items)
            {
                yield return $"  {Label(f.Severity),-5} {f.Code,-9} {f.Message}" + (f.Blocking ? "  [blocks the selected operation]" : "");
                foreach (string line in f.Detail.Split('\n').Where(l => l.Length > 0)) yield return "                  " + line;
                if (f.Action.Length > 0 && f.Severity is not DiagnosticSeverity.Ok) yield return "                  Action: " + f.Action;
            }
        }
        yield return "";
        yield return "Available testing modes";
        foreach (string m in r.Modes) yield return "  " + m;
        yield return "";
        yield return "Available target drivers (static)";
        foreach (string d in r.Drivers) yield return "  " + d;
        yield return "";
        yield return "Available capture types (static)";
        foreach (string capture in r.Captures) yield return "  " + capture;
        var problems = r.Findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToArray();
        var warnings = r.Findings.Where(f => f.Severity == DiagnosticSeverity.Warning).ToArray();
        yield return "";
        yield return $"Problems ({problems.Length})";
        foreach (var f in problems) yield return $"  {f.Code}  {f.Message}" + (f.Blocking ? "  [blocking]" : "");
        if (problems.Length == 0) yield return "  none";
        yield return "";
        yield return $"Warnings ({warnings.Length})";
        foreach (var f in warnings) yield return $"  {f.Code}  {f.Message}";
        if (warnings.Length == 0) yield return "  none";
        yield return "";
        yield return "Recommendations";
        var actions = r.Findings.Where(f => f.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && f.Action.Length > 0).Select(f => f.Action).Distinct().ToArray();
        foreach (string a in actions) yield return "  - " + a;
        if (!r.Findings.Any(f => f.Code is "RUN002" or "RUN005")) yield return "  - Verify target starts the target once (no scenario) to confirm runtime activation.";
        if (actions.Length == 0 && r.Findings.Any(f => f.Code is "RUN002" or "RUN005")) yield return "  none";
    }

    // The compact five items Project Properties shows: Executable and Framework on the full-width top row; Proxy,
    // Diagnostic host and Test readiness on the two rows beside the buttons.
    internal static (string Top, string Body) Compact(TargetReport r, int topWidth, int bodyWidth)
    {
        string Cell((string Label, DiagnosticSeverity Severity, string Text) row, string label, int w) => WorkbenchText.Column($"{label,-11}{Label(row.Severity),-5}{row.Text}", w);
        var s = r.Summary;
        int half = Math.Max(20, topWidth / 2), proxy = Math.Max(20, bodyWidth * 45 / 100);
        // TSW PRODUCT-3: only the component the project's scenarios use gets the row (the other one is "not needed" and
        // stays in Details); both share it when the project uses both kinds.
        string middle = r.Context.Proxy == r.Context.Diagnostic ? Cell(s[2], "Proxy", proxy) + " " + Cell(s[3], "Diag. host", bodyWidth - proxy - 1)
            : r.Context.Proxy ? Cell(s[2], "Proxy", bodyWidth) : Cell(s[3], "Diag. host", bodyWidth);
        return (Cell(s[0], "Executable", half) + " " + Cell(s[1], "Framework", topWidth - half - 1), middle + "\n" + Cell(s[4], "Readiness", bodyWidth));
    }
}

// ── startup failures ──────────────────────────────────────────────────────────────────

internal enum StartupFailureKind { MissingExecutable, MissingManagedAssembly, MissingNativeDependency, IncompatibleDeployment, RuntimeInitialization, HandshakeFailure, ExitedUnexpectedly, Unknown }

// A target that failed before its first checkpoint, explained in plain words. Technical keeps the original text intact.
internal sealed record StartupFailure(StartupFailureKind Kind, string Title, string Reason, string Action, string Technical)
{
    private static readonly Regex Assembly = new(@"Could not load (?:file or )?assembly '([^,']+)", RegexOptions.CultureInvariant);
    private static readonly Regex Native = new(@"(?:Unable to load (?:DLL|shared library) '([^']+)'|DllNotFoundException[^']*'([^']+)')", RegexOptions.CultureInvariant);
    private static readonly Regex ExceptionType = new(@"\b((?:System\.)?[A-Z][A-Za-z.]*Exception)\b", RegexOptions.CultureInvariant);

    // A recorded execution that ended before the scenario started (step "launch"). Assertion failures, timeouts after
    // start and cancellations are never startup failures; the result's category is not changed.
    internal static StartupFailure? Of(ScenarioResult r) =>
        r.Outcome is ScenarioOutcomes.Error or ScenarioOutcomes.TimedOut && r.Reason.Contains("[launch, grid", StringComparison.Ordinal) ? Classify(r.Reason) : null;

    internal static StartupFailure Classify(string text)
    {
        string technical = text;
        if (text.Contains("Target executable does not exist", StringComparison.Ordinal))
            return new(StartupFailureKind.MissingExecutable, "Target executable missing", "The target executable was not found.",
                "Correct the executable in Project Properties (Alt+O), or rebuild the target.", technical);
        if (Assembly.Match(text) is { Success: true } m && text.Contains("FileNotFoundException", StringComparison.Ordinal))
        {
            string name = m.Groups[1].Value;
            return name == "TSharpWorkbench.Proxy"
                ? new(StartupFailureKind.MissingManagedAssembly, "Target startup failed", "Required assembly TSharpWorkbench.Proxy.dll was not found.",
                    "Prepare the target application for Proxy testing: Settings > Prepare Target Copy... deploys TSharpWorkbench.Proxy.dll, TSharpWorkbench.Testing.dll and their dependencies (Settings > Target Diagnostics... shows what is missing).", technical)
                : new(StartupFailureKind.MissingManagedAssembly, "Target startup failed", $"Required assembly {name}.dll was not found.",
                    "Deploy the missing assembly beside the executable (redeploy the complete build output), then check Settings > Target Diagnostics....", technical);
        }
        if (text.Contains("FileLoadException", StringComparison.Ordinal) || text.Contains("BadImageFormatException", StringComparison.Ordinal)
            || text.Contains("manifest definition", StringComparison.OrdinalIgnoreCase))
            return new(StartupFailureKind.IncompatibleDeployment, "Incompatible deployment", "An assembly of the deployment exists but could not be loaded (another version or platform).",
                "Redeploy the application and the Proxy from one consistent build; Settings > Target Diagnostics... compares the versions.", technical);
        if (Native.Match(text) is { Success: true } n)
            return new(StartupFailureKind.MissingNativeDependency, "Native dependency missing",
                $"The native library {(n.Groups[1].Success ? n.Groups[1].Value : n.Groups[2].Value)} could not be loaded.",
                "Deploy the native runtime of the selected display driver with the application, or choose another target display.", technical);
        if (text.Contains("Requested backend", StringComparison.Ordinal) && text.Contains("unavailable", StringComparison.Ordinal))
            return new(StartupFailureKind.IncompatibleDeployment, "Display driver unavailable", "The selected target display driver is not deployed with the application; nothing was substituted.",
                "Choose another target display, or deploy the driver with the application.", technical);
        if (text.Contains("protocol schema mismatch", StringComparison.OrdinalIgnoreCase) || text.Contains("Proxy capabilities missing", StringComparison.Ordinal)
            || text.Contains("Target identity missing", StringComparison.Ordinal) || text.Contains("driver/grid mismatch", StringComparison.Ordinal)
            || text.Contains("credentials missing", StringComparison.Ordinal) || text.Contains("did not advertise", StringComparison.Ordinal))
            return new(StartupFailureKind.HandshakeFailure, "Diagnostic handshake failed", "The target started but its Workbench handshake was not accepted.",
                "Redeploy the Proxy (or rebuild the diagnostic target) from this Workbench build; Settings > Target Diagnostics... shows the versions.", technical);
        bool exited = text.Contains("before connecting", StringComparison.Ordinal);
        if (exited && ExceptionType.Match(text) is { Success: true } e)
            return new(StartupFailureKind.RuntimeInitialization, "Target startup failed", $"The application stopped during startup with {e.Groups[1].Value}.",
                "See the technical details for the application's own error; fix it, then run again.", technical);
        if (exited)
            return new(StartupFailureKind.ExitedUnexpectedly, "Target exited unexpectedly", "The application exited before it connected to the Workbench.",
                "See the technical details. Check that the application starts on its own.", technical);
        return new(StartupFailureKind.Unknown, "Target failed to start or connect", "Target failed to start or connect. See technical details.",
            "See the technical details; Settings > Target Diagnostics... checks the deployment.", technical);
    }
}
