using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TSharpWorkbench.Testing;

// Automatic target preparation. One UI-independent service prepares an ordinary TSharpVision application for
// Proxy testing: Inspection (read-only) -> Preparation Plan (read-only) -> Execution (only after explicit approval).
// It extends the established Proxy deployment contract of scripts/prepare-*.ps1 (the application's own build output plus
// the Workbench Proxy beside the executable; deps.json and runtimeconfig.json are never rewritten) instead of inventing a
// second one. What is deployed is decided by the Proxy's own build metadata: the Workbench carries the Proxy build output
// in proxy-payload/ and its TSharpWorkbench.Proxy.deps.json is the authoritative dependency closure (managed assets and
// per-RID native assets, with package identities). The target's TSharpVision framework is never replaced: the Proxy is
// installed only when the application's framework is identical to the one the Proxy was built against.
internal enum PreparationStrategy { PreparedCopy, InPlaceInstallation }
internal enum PreparedState { Ready, Outdated, Incomplete, Incompatible }
internal enum PlanAction { Copy, Add, Replace, Keep, Provided, Conflict }
internal enum OfferKind { None, Prepare, Reprepare, Unavailable }

// Optional testing capabilities. The Proxy core (Proxy + Testing + their closure) is always required.
[Flags]
internal enum PreparationCapabilities { Proxy = 1, Sdl = 2, Console = 4 }

// One deployable file of the Proxy closure, resolved for one runtime identifier.
internal sealed record PayloadAsset(string Component, string Package, string Version, string Source, string Destination, bool Native, long Size, string Sha256);

// The Proxy deployment unit the Workbench carries (the Proxy build output, CopyLocalLockFileAssemblies=true) and the
// identities preparation needs. Only assets listed in TSharpWorkbench.Proxy.deps.json are ever deployed.
internal sealed class ProxyPayload
{
    internal const string Folder = "proxy-payload", DepsFile = "TSharpWorkbench.Proxy.deps.json", EnvironmentOverride = "TSHARPWORKBENCH_PROXY_PAYLOAD";
    internal required string Directory { get; init; }
    internal required string Rid { get; init; }
    internal required int RuntimeMajor { get; init; }
    internal required PayloadAsset[] Assets { get; init; }
    // Packages of the closure by name (TSharpVision, TSharpVision.Drivers.SDL, SDL3-CS...), with their versions.
    internal required Dictionary<string, string> Packages { get; init; }
    internal required string FrameworkVersion { get; init; }
    internal required string FrameworkSha256 { get; init; }
    internal required string ProxySha256 { get; init; }
    internal required Guid TestingMvid { get; init; }
    internal required string Fingerprint { get; init; }
    // The RIDs whose native assets this payload carries (payload-manifest.json), not every RID of the closure.
    internal string[] NativeRids { get; init; } = [];
    internal PayloadManifest? Manifest { get; init; }
    internal string FrameworkPath => Path.Combine(Directory, "TSharpVision.dll");

    // proxy-payload/ beside the Workbench (or the override): null when this Workbench build carries no payload. A folder
    // with only one of deps.json / payload-manifest.json is returned so that Load names what is missing.
    internal static string? Locate()
    {
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable(EnvironmentOverride), Path.Combine(AppContext.BaseDirectory, Folder) })
            if (candidate is { Length: > 0 } && (File.Exists(Path.Combine(candidate, DepsFile)) || File.Exists(Path.Combine(candidate, PayloadManifest.FileName)))) return Path.GetFullPath(candidate);
        return null;
    }

    internal static string CurrentRid(Architecture? architecture = null) =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + (architecture ?? RuntimeInformation.ProcessArchitecture) switch
        { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "x86", Architecture.Arm => "arm", var a => a.ToString().ToLowerInvariant() };

    internal static string ComponentOf(string package) =>
        package == "TSharpVision" ? "framework"
        : package.StartsWith("SDL3-CS", StringComparison.Ordinal) || package == "TSharpVision.Drivers.SDL" ? "sdl"
        : package == "TSharpVision.Drivers.Console" ? "console" : "proxy";

    private static readonly Dictionary<string, ProxyPayload> cache = new(StringComparer.Ordinal);
    // Reads the closure for one runtime identifier (cached per payload folder state; hashing it costs a few hundred
    // milliseconds). Throws InvalidDataException when the payload is unusable.
    internal static ProxyPayload Load(string directory, string? rid = null)
    {
        rid ??= CurrentRid();
        string deps = Path.Combine(directory, DepsFile);
        string key = Path.GetFullPath(directory) + "|" + rid + "|" + File.GetLastWriteTimeUtc(deps).Ticks + "|" + File.GetLastWriteTimeUtc(Path.Combine(directory, PayloadManifest.FileName)).Ticks;
        lock (cache) if (cache.TryGetValue(key, out var known)) return known;
        var loaded = Read(directory, rid, deps);
        lock (cache) cache[key] = loaded;
        return loaded;
    }
    private static ProxyPayload Read(string directory, string rid, string deps)
    {
        // Provenance first; a payload without a matching manifest is refused before anything is read from it.
        var manifest = PayloadManifest.Load(directory);
        if (!File.Exists(deps)) throw new InvalidDataException($"The Proxy payload is incomplete: {DepsFile} is missing.");
        manifest.VerifyFile(DepsFile);
        using var document = JsonDocument.Parse(File.ReadAllBytes(deps));
        var root = document.RootElement;
        string targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString() ?? throw new InvalidDataException("deps.json has no runtime target.");
        int major = targetName.LastIndexOf('v') is int v and >= 0 && Version.TryParse(targetName[(v + 1)..], out var tfm) ? tfm.Major : 0;
        var assets = new List<PayloadAsset>();
        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        var rids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in root.GetProperty("targets").GetProperty(targetName).EnumerateObject())
        {
            int slash = library.Name.IndexOf('/');
            string package = library.Name[..slash], version = library.Name[(slash + 1)..];
            packages[package] = version;
            string component = ComponentOf(package);
            if (library.Value.TryGetProperty("runtime", out var runtime))
                foreach (var asset in runtime.EnumerateObject())
                {
                    string name = Path.GetFileName(asset.Name);
                    if (component == "framework") continue; // never deployed: the application's framework stays
                    assets.Add(Asset(directory, component, package, version, name, name, false));
                }
            if (library.Value.TryGetProperty("runtimeTargets", out var targets))
                foreach (var asset in targets.EnumerateObject())
                {
                    if (asset.Value.GetProperty("assetType").GetString() != "native") continue;
                    string assetRid = asset.Value.GetProperty("rid").GetString() ?? "";
                    if (!manifest.Carries(assetRid)) continue; // a lean (published) payload carries its RID(s) only
                    rids.Add(assetRid);
                    if (assetRid != rid) continue;
                    // Optional backend loaders resolve native libraries beside the executable (the established contract).
                    assets.Add(Asset(directory, component, package, version, asset.Name.Replace('/', Path.DirectorySeparatorChar), Path.GetFileName(asset.Name), true));
                }
        }
        if (!packages.TryGetValue("TSharpVision", out string? framework)) throw new InvalidDataException("The Proxy closure names no TSharpVision package.");
        string proxy = Path.Combine(directory, TargetDiagnostics.ProxyFile), testing = Path.Combine(directory, TargetDiagnostics.TestingFile);
        foreach (string required in new[] { proxy, testing, Path.Combine(directory, "TSharpVision.dll") })
            if (!File.Exists(required)) throw new InvalidDataException("The Proxy payload is incomplete: " + Path.GetFileName(required) + " is missing.");
        foreach (var asset in assets) manifest.Verify(Path.GetRelativePath(directory, asset.Source), asset.Sha256);
        string frameworkSha256 = Preparation.FileHash(Path.Combine(directory, "TSharpVision.dll")), proxySha256 = Preparation.FileHash(proxy);
        manifest.Verify("TSharpVision.dll", frameworkSha256); manifest.Verify(TargetDiagnostics.ProxyFile, proxySha256); manifest.VerifyFile(TargetDiagnostics.TestingFile);
        string fingerprint = Preparation.Hash(string.Join("\n", assets.OrderBy(a => a.Destination, StringComparer.Ordinal).Select(a => a.Destination + "|" + a.Sha256)) + "\n" + framework);
        return new()
        {
            Directory = Path.GetFullPath(directory), Rid = rid, RuntimeMajor = major, Assets = assets.ToArray(), Packages = packages,
            FrameworkVersion = framework, FrameworkSha256 = frameworkSha256,
            ProxySha256 = proxySha256, TestingMvid = Preparation.Mvid(testing) ?? Guid.Empty, Fingerprint = fingerprint,
            NativeRids = rids.OrderBy(r => r, StringComparer.Ordinal).ToArray(), Manifest = manifest
        };
    }
    private static PayloadAsset Asset(string directory, string component, string package, string version, string relative, string destination, bool native)
    {
        string path = Path.Combine(directory, relative);
        if (!File.Exists(path)) throw new InvalidDataException($"The Proxy payload is incomplete: {relative} ({package} {version}) is listed in {DepsFile} but missing.");
        return new(component, package, version, path, destination, native, new FileInfo(path).Length, Preparation.FileHash(path));
    }
}

// Where prepared deployments live: outside the Workbench installation, per user. Configurable (settings.json
// PreparedTargetsRoot, or TSHARPWORKBENCH_PREPARED_TARGETS); the test suites always pass their own root.
internal static class PreparationStore
{
    internal const string EnvironmentOverride = "TSHARPWORKBENCH_PREPARED_TARGETS";
    internal static string? Configured { get; set; }
    internal static string Root => Configured is { Length: > 0 } c ? Path.GetFullPath(c)
        : Environment.GetEnvironmentVariable(EnvironmentOverride) is { Length: > 0 } e ? Path.GetFullPath(e) : DefaultRoot();
    internal static string DefaultRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string data = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support")
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
        return Path.Combine(data, "TSharpWorkbench", "PreparedTargets");
    }
}

// preparation.json: what a prepared copy was made from and of, to validate and manage it. Versioned; never trusted alone.
internal sealed record PreparedFile(string Path, long Size, string Sha256, long Modified, string Origin, string Component);
internal sealed record PreparationManifest
{
    internal const string FileName = "preparation.json", CurrentSchema = "tsharpworkbench-preparation/1";
    public string Schema { get; init; } = CurrentSchema;
    public string Strategy { get; init; } = nameof(PreparationStrategy.PreparedCopy);
    public string DeploymentId { get; init; } = "";
    public string ApplicationId { get; init; } = "";
    public DateTime PreparedUtc { get; init; }
    public string SourceExecutable { get; init; } = "";
    public string SourceDirectory { get; init; } = "";
    public string SourceFingerprint { get; init; } = "";
    public string PreparedExecutable { get; init; } = "";
    public string ProxyVersion { get; init; } = "";
    public string ProxySha256 { get; init; } = "";
    public string TestingMvid { get; init; } = "";
    public string FrameworkPackage { get; init; } = "TSharpVision";
    public string FrameworkVersion { get; init; } = "";
    public string FrameworkSha256 { get; init; } = "";
    public string PayloadFingerprint { get; init; } = "";
    public string[] Capabilities { get; init; } = [];
    public string Platform { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Rid { get; init; } = "";
    public string WorkbenchVersion { get; init; } = "";
    public PreparedFile[] Files { get; init; } = [];
    public bool Verified { get; init; }
    public DateTime? VerifiedUtc { get; init; }
    public string[] Verification { get; init; } = [];
}

// The state of one prepared deployment, from its manifest and the files actually on disk.
internal sealed record PreparedStatus(PreparedState State, string[] Reasons, PreparationManifest? Manifest, string Folder)
{
    internal string Label => "Prepared target: " + State;
    internal string Summary => Label + (Reasons.Length > 0 ? " - " + Reasons[0] : "");
    internal string? Source => Manifest?.SourceExecutable;
}

internal sealed record ComponentState(string Name, string State, DiagnosticSeverity Severity, string Detail);
internal sealed record PlanEntry(PlanAction Action, string Path, long Size, string Component, string? Source, string? Sha256, string Note = "");

internal sealed record PreparationInspection(string Executable, TargetInspection Target, TargetReport Report, PreparedStatus? Prepared, string? SourceExecutable,
    PreparedStatus? Existing, string? Unsupported, string[] Notes)
{
    // The application a preparation starts from: the original of a prepared copy, or the executable itself.
    internal string Source => SourceExecutable ?? Executable;
    internal bool IsPreparedCopy => Prepared is not null;
}

internal sealed record PreparationPlan(PreparationStrategy Strategy, PreparationCapabilities Capabilities, PreparationInspection Inspection, string SourceExecutable,
    string SourceDirectory, string Destination, string PreparedExecutable, ComponentState[] Components, PlanEntry[] Entries, string[] Problems, string[] Warnings,
    string[] Compatibility, long RequiredBytes, long? AvailableBytes, string ApplicationId, string DeploymentId, string SourceFingerprint, string Fingerprint)
{
    internal bool CanExecute => Problems.Length == 0;
    // The destination the user chose for a prepared copy (null = the proposed one in the store) and what
    // it is. How the application's framework compares with the Proxy's (by version, FrameworkCompatibility);
    // OtherBuild = a retained framework build that needs runtime verification (same version or major).
    internal string? ChosenDestination { get; init; }
    internal DestinationState? DestinationCheck { get; init; }
    internal FrameworkComparison? Framework { get; init; }
    internal bool OtherBuild => Framework?.Match is FrameworkMatch.SameVersion or FrameworkMatch.SameMajor;
    internal IEnumerable<PlanEntry> Changes => Entries.Where(e => e.Action is PlanAction.Copy or PlanAction.Add or PlanAction.Replace);
    internal int Count(PlanAction action) => Entries.Count(e => e.Action == action);
    internal string Describe(PlanAction action) => action switch
    {
        PlanAction.Copy => "COPY", PlanAction.Add => "ADD", PlanAction.Replace => "REPLACE", PlanAction.Keep => "KEEP",
        PlanAction.Provided => "APP", _ => "CONFLICT"
    };
    internal IEnumerable<string> FileLines()
    {
        yield return Strategy == PreparationStrategy.PreparedCopy
            ? $"Prepared copy of {Path.GetFileName(SourceExecutable)}: the original folder is only read."
            : $"In-place installation into {SourceDirectory}: the original application folder is modified.";
        yield return "Source       " + SourceDirectory;
        yield return "Destination  " + Destination + (DestinationCheck is { } d ? "  (" + d.Label + ")" : "");
        yield return $"Files        {Count(PlanAction.Copy)} copied, {Count(PlanAction.Add)} added, {Count(PlanAction.Replace)} replaced, {Count(PlanAction.Keep) + Count(PlanAction.Provided)} kept, {Count(PlanAction.Conflict)} conflicts";
        yield return "Disk         " + Preparation.Size(RequiredBytes) + " needed" + (AvailableBytes is { } free ? ", " + Preparation.Size(free) + " free" : "");
        foreach (string p in Problems) yield return "! " + p;
        foreach (string w in Warnings) yield return "Note: " + w;
        yield return "";
        foreach (var e in Entries.OrderBy(e => e.Action == PlanAction.Copy ? 1 : 0).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            yield return $"{Describe(e.Action),-8} {e.Path}  {Preparation.Size(e.Size)}  [{e.Component}]" + (e.Note.Length > 0 ? "  " + e.Note : "");
    }
}

// The one summary of a plan, shown first by both modes (Prepare a copy, Fix original target): what happens
// (counts by category, size), the outcome in words that tell the kinds of problem apart, and the next action. It only
// reads the plan; it never decides anything the plan did not.
internal sealed record PlanSummary(string Outcome, string Next, string Counts, string DebugDocs, bool RequiresRebuild, bool Manual);

internal static class PreparationVerdict
{
    // The kinds of a refused file or framework, as the plan names them (prefix of the note) and the dialogs show them.
    internal const string DifferentBuild = "Different build", VersionMismatch = "Version mismatch", UserOwned = "User-owned file";
    internal const string AlreadyValid = "Already valid", Fixable = "Missing (fixable)", RequiresRebuild = "Incompatible - requires rebuild",
        ManualIntervention = "Manual intervention needed", Ready = "Ready to prepare", Refused = "Refused", NotSupported = "Not supported";
    // The same declared version from another build (for example a local ProjectReference build). Supported:
    // prepared and fixed like an identical build; Verify target confirms that the Proxy loads it.
    internal const string SameVersion = "Same version, other build", SameMajor = "Same major, runtime verification required";
    internal const string OtherBuildNote = "The application's framework is another build of the same version: supported, never replaced. "
        + "Verify target (the real Proxy handshake) confirms that the Proxy loads it.";

    internal static string KindOf(string note) =>
        new[] { DifferentBuild, VersionMismatch, UserOwned }.FirstOrDefault(k => note.StartsWith(k + ":", StringComparison.Ordinal)) ?? UserOwned;
    internal static string Detail(string note) => note.IndexOf(": ", StringComparison.Ordinal) is int i and > 0 && KindOf(note) == note[..i] ? note[(i + 2)..] : note;
    internal static bool NeedsRebuild(string kind) => kind.StartsWith(DifferentBuild, StringComparison.Ordinal) || kind.StartsWith(VersionMismatch, StringComparison.Ordinal);

    internal static string RebuildAdvice(string version) =>
        $"Rebuild the application against the public TSharpVision {version} package (package mode, e.g. scripts/prepare-tvdemo.ps1), "
        + "or build the Workbench and the application from the same framework source (docs/framework-build-workflow.md). Nothing is replaced.";

    // Whether the application's deps.json names a library as a project reference (built from source), not a package.
    internal static bool ProjectReferenced(string depsPath, string package)
    {
        try
        {
            if (!File.Exists(depsPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries)) return false;
            foreach (var library in libraries.EnumerateObject())
                if (library.Name.StartsWith(package + "/", StringComparison.Ordinal) && library.Value.TryGetProperty("type", out var type))
                    return type.GetString() == "project";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return false;
    }

    // Optional debug documentation: a .pdb or .xml beside an assembly of the same name. Shown as its own category; it is
    // copied like everything else (a lean copy is not offered: nothing proves the application never reads these files).
    internal static PlanEntry[] DebugDocs(IEnumerable<PlanEntry> copies)
    {
        var all = copies.ToArray();
        var assemblies = all.Where(e => Path.GetExtension(e.Path) is var x && (x.Equals(".dll", StringComparison.OrdinalIgnoreCase) || x.Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            .Select(e => Path.ChangeExtension(e.Path, null)).ToHashSet(Preparation.PathComparer);
        return all.Where(e => Path.GetExtension(e.Path) is var x && (x.Equals(".pdb", StringComparison.OrdinalIgnoreCase) || x.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            && assemblies.Contains(Path.ChangeExtension(e.Path, null))).ToArray();
    }

    internal static PlanSummary For(PreparationPlan plan, string? frameworkVersion)
    {
        var copies = plan.Entries.Where(e => e.Action == PlanAction.Copy).ToArray();
        var docs = DebugDocs(copies);
        int conflicts = plan.Count(PlanAction.Conflict);
        int refusals = plan.Problems.Count(p => !plan.Entries.Any(e => e.Action == PlanAction.Conflict && p.StartsWith(e.Path + ":", StringComparison.Ordinal)));
        long app = copies.Sum(e => e.Size) - docs.Sum(e => e.Size);
        string counts = plan.Strategy == PreparationStrategy.PreparedCopy
            ? $"copy {copies.Length - docs.Length} app-required ({Preparation.Size(app)}), add {plan.Count(PlanAction.Add) + plan.Count(PlanAction.Replace)} Proxy/diagnostic, "
              + $"keep {plan.Count(PlanAction.Keep) + plan.Count(PlanAction.Provided)}, {conflicts} conflict{(conflicts == 1 ? "" : "s")}, {refusals} refused"
            : $"add {plan.Count(PlanAction.Add)}, replace {plan.Count(PlanAction.Replace)}, keep {plan.Count(PlanAction.Keep) + plan.Count(PlanAction.Provided)}, {conflicts} conflict{(conflicts == 1 ? "" : "s")}, {refusals} refused";
        string debug = docs.Length == 0 ? "none" : $"{docs.Length} PDB/XML file{(docs.Length == 1 ? "" : "s")} ({Preparation.Size(docs.Sum(e => e.Size))}) copied as well; a lean copy is not offered (not provably unused)";
        var kinds = plan.Components.Where(c => !c.State.EndsWith("(not selected)", StringComparison.Ordinal)).Select(c => c.State).ToArray();
        bool rebuild = kinds.Any(NeedsRebuild);
        bool manual = kinds.Any(k => k.StartsWith(UserOwned, StringComparison.Ordinal));
        string version = frameworkVersion ?? "1.1.0";
        string other = plan.OtherBuild ? " Application framework retained: Verify target and scenarios confirm runtime compatibility." : "";
        var (outcome, next) =
            plan.Inspection.Unsupported is { } unsupported ? (NotSupported, unsupported)
            : rebuild ? (RequiresRebuild, RebuildAdvice(version))
            : manual ? (ManualIntervention, "A file the Proxy needs exists with other content and is not a Workbench file. It is kept; rename or remove it, or clear the optional display that needs it.")
            : !plan.CanExecute ? (Refused, plan.Problems[0])
            : !plan.Changes.Any() ? (AlreadyValid, "Nothing to prepare (static checks). Verify target confirms that the Proxy loads at runtime.")
            : plan.Strategy == PreparationStrategy.PreparedCopy ? (Ready, "Prepare creates the copy in the destination folder above; the original folder is only read." + other)
            : (Ready, "The original folder would be changed; Fix asks first and keeps backups." + other);
        return new(outcome, next, counts, debug, rebuild, manual);
    }
}

// Where a prepared copy goes. The user may choose any folder; only a new or empty one is ever written, and
// only after Prepare. Checking reads the file system and changes nothing.
internal enum DestinationKind { Proposed, New, Empty, ExistingCopy, NotEmpty, Conflict, Invalid }
internal sealed record DestinationState(DestinationKind Kind, string Path, string Message, string[] Missing, PreparedStatus? Existing = null)
{
    internal bool Usable => Kind is DestinationKind.Proposed or DestinationKind.New or DestinationKind.Empty;
    internal string Label => Kind switch
    {
        DestinationKind.Proposed => "proposed (new folder in the prepared-targets store)",
        DestinationKind.New => Missing.Length > 1 ? $"new folder ({Missing.Length} folders will be created)" : "new folder (created by Prepare)",
        DestinationKind.Empty => "empty folder",
        DestinationKind.ExistingCopy => "existing prepared copy",
        DestinationKind.NotEmpty => "not empty",
        DestinationKind.Conflict => "conflict",
        _ => "invalid"
    };
}

internal static class PreparationDestination
{
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    // One folder name (or a nested relative path for New folder): no traversal, no invalid or reserved names.
    internal static string? NameProblem(string name, bool nested)
    {
        if (name.Trim().Length == 0) return "Enter a folder name.";
        string[] parts = name.Split(['\\', '/']);
        if (!nested && parts.Length > 1) return "A folder name cannot contain \\ or /.";
        if (System.IO.Path.IsPathRooted(name)) return "Enter a name relative to the parent folder, not a full path.";
        foreach (string part in parts)
        {
            if (part.Length == 0) return "Empty folder names (\\\\ or a trailing \\) are not allowed.";
            if (part is "." or "..") return "\".\" and \"..\" are not allowed: the new folder must stay inside the parent folder.";
            if (part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || OperatingSystem.IsWindows() && part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
                return $"\"{part}\" contains a character that is not allowed in folder names.";
            if (OperatingSystem.IsWindows() && (part.EndsWith('.') || part.EndsWith(' '))) return $"\"{part}\" ends with a dot or space, which Windows does not keep.";
            if (OperatingSystem.IsWindows() && Reserved.Contains(System.IO.Path.GetFileNameWithoutExtension(part), StringComparer.OrdinalIgnoreCase)) return $"\"{part}\" is a reserved device name.";
        }
        return null;
    }

    // The first existing folder on the way up (the folder itself included), or null.
    internal static string? ExistingAncestor(string full)
    {
        for (string? d = full; d is not null; d = System.IO.Path.GetDirectoryName(d))
            if (Directory.Exists(d)) return d;
        return null;
    }

    internal static DestinationState Check(string? input, PreparationInspection inspection, TargetPreparationService service, string proposed)
    {
        if (input is null) return new(DestinationKind.Proposed, proposed, "A new folder in the prepared-targets store (" + service.Root + ").", []);
        string raw = input.Trim();
        DestinationState Invalid(string message, string path = "") => new(DestinationKind.Invalid, path.Length > 0 ? path : raw, message, []);
        if (raw.Length == 0) return Invalid("Enter a destination folder, or choose one with Browse... or New folder....");
        if (!System.IO.Path.IsPathFullyQualified(raw)) return Invalid("Enter a full path (for example D:\\Tests\\" + System.IO.Path.GetFileNameWithoutExtension(inspection.Source) + "-copy).");
        string root = System.IO.Path.GetPathRoot(raw) ?? "";
        string rest = raw[root.Length..];
        if (rest.Trim('\\', '/').Length == 0) return Invalid("A drive or share root is not a destination; choose or create a folder in it.");
        if (NameProblem(rest.Trim('\\', '/'), nested: true) is { } problem) return Invalid(problem);
        string full;
        try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(raw)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Invalid("The path is not valid: " + ex.Message); }
        string sourceDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(inspection.Source))!;
        if (Preparation.IsUnder(full, sourceDir)) return Invalid("The destination is inside the application folder; copying would recurse. Choose a folder outside it.", full);
        if (Preparation.IsUnder(sourceDir, full)) return Invalid("The destination contains the application folder; a copy never goes around its source. Choose a folder outside it.", full);
        if (Preparation.IsUnder(full, AppContext.BaseDirectory) || Preparation.IsUnder(AppContext.BaseDirectory, full))
            return Invalid("The destination overlaps the Workbench installation. Choose another folder.", full);
        if (Preparation.ProtectedLocation(full) is { } protectedReason) return Invalid($"The destination is protected: {protectedReason}. The Workbench never elevates.", full);
        if (File.Exists(full)) return new(DestinationKind.Conflict, full, "A file with this name exists; it is never replaced. Choose another name.", []);
        if (ExistingAncestor(full) is not { } existing) return Invalid("The drive or share does not exist.", full);
        if (TargetRepairService.LinkOnPath(existing) is { } link) return Invalid($"The destination is reached through a symbolic link or junction ({link}); links are never followed.", full);
        var missing = new List<string>();
        for (string? d = full; d is not null && !Directory.Exists(d); d = System.IO.Path.GetDirectoryName(d)) missing.Add(d);
        if (missing.Count > 0)
            return new(DestinationKind.New, full, missing.Count == 1 ? "Prepare creates this folder." : $"Prepare creates this folder and {missing.Count - 1} missing parent folder(s).", missing.ToArray());
        if (new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReadOnly) && !OperatingSystem.IsWindows())
            return Invalid("The destination folder is read-only.", full);
        if (service.Status(full) is { } prepared)
        {
            bool same = WorkbenchPreferences.SamePath(prepared.Manifest?.SourceExecutable, inspection.Source);
            string exe = System.IO.Path.Combine(full, prepared.Manifest?.PreparedExecutable ?? "");
            bool running = prepared.Manifest is not null && File.Exists(exe) && TargetRepairService.ProcessRunning(exe);
            return new(DestinationKind.ExistingCopy, full, (same ? $"An existing prepared copy of this application ({prepared.State})" : "A prepared copy of another application")
                + " is in this folder; it is never overwritten." + (running ? " Its executable is running." : "")
                + (same && prepared.State == PreparedState.Ready ? " Use existing uses it; for a new copy choose New folder...." : " Choose New folder... or another folder."), [], prepared);
        }
        int entries;
        try { entries = Directory.EnumerateFileSystemEntries(full).Take(1000).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Invalid("The destination folder cannot be read: " + ex.Message, full); }
        return entries == 0 ? new(DestinationKind.Empty, full, "An empty folder: Prepare puts the copy into it.", [])
            : new(DestinationKind.NotEmpty, full, $"The folder is not empty ({(entries >= 1000 ? "1000+" : entries.ToString())} item{(entries == 1 ? "" : "s")}). A prepared copy needs a new or empty folder; nothing is ever overwritten. Choose New folder... to create one inside it.", []);
    }

    // A free sibling for a folder that already holds a copy: "<folder>-2", "<folder>-3", ... (checked, not created).
    internal static string NextFree(string full)
    {
        for (int i = 2; i < 1000; i++)
        {
            string candidate = full + "-" + i;
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        return full + "-" + Guid.NewGuid().ToString("N")[..6];
    }
}

internal sealed record PreparationProgress(string Phase, int Files, int TotalFiles, long Bytes, long TotalBytes);
internal sealed record PreparationResult(bool Success, bool Cancelled, string Message, string? PreparedExecutable, string? Deployment, string[] Changes, string[] Details)
{
    internal bool RolledBack { get; init; }
    internal bool RollbackComplete { get; init; } = true;
    // The install record of a successful in-place installation (UndoInstall removes its added files).
    internal string? Record { get; init; }
}

// Install record of an in-place installation: every added or replaced file with the hash Workbench wrote, the original's
// hash and its backup. Rollback deletes or restores only files that still hold exactly what Workbench installed.
internal sealed record InstalledFile(string Path, string Action, string InstalledSha256, string? OriginalSha256, string? Backup, bool Done);
internal sealed record InstallRecord
{
    internal const string FileName = "install.json", CurrentSchema = "tsharpworkbench-install/1";
    public string Schema { get; init; } = CurrentSchema;
    public string Id { get; init; } = "";
    public string TargetDirectory { get; init; } = "";
    public string Executable { get; init; } = "";
    public string State { get; init; } = "planned";
    public DateTime StartedUtc { get; init; }
    public string[] Capabilities { get; init; } = [];
    public string ProxySha256 { get; init; } = "";
    public string FrameworkVersion { get; init; } = "";
    public InstalledFile[] Files { get; init; } = [];
    public string[] Notes { get; init; } = [];
}

internal sealed class PreparationException(string message) : Exception(message);

internal static class Preparation
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] Excluded = [".git", ".svn", ".hg", ".vs", ".idea"];
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string FileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    internal static Guid? Mvid(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            return md.GetGuid(md.GetModuleDefinition().Mvid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }
    internal static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB" : $"{bytes} B";
    internal static bool IsUnder(string path, string folder)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, PathComparison) || p.StartsWith(f + Path.DirectorySeparatorChar, PathComparison);
    }
    // Stable, collision-free per application: the executable name for people, a hash of its normalized full path for
    // uniqueness (two TVDemo.exe in different folders get different ids). The path itself never appears in a file name.
    internal static string ApplicationId(string executable)
    {
        string full = Path.GetFullPath(executable);
        string key = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        string name = new string(Path.GetFileNameWithoutExtension(full).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        if (name.Length > 40) name = name[..40];
        return name + "-" + Hash(key)[..12];
    }
    internal static string NewDeploymentId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];

    // ── source enumeration ───────────────────────────────────────────────────────────

    internal sealed record SourceFile(string Relative, string Full, long Size, long Modified);
    // The complete application deployment: every file under the executable's folder, recursively, except source-control
    // and IDE metadata. A symbolic link, junction or other reparse point is refused (never followed): its target could be
    // outside the tree or loop back into it.
    internal static List<SourceFile> Enumerate(string directory, List<string> problems, int limit = 20000)
    {
        var files = new List<SourceFile>();
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> entries;
            try { entries = System.IO.Directory.EnumerateFileSystemEntries(current).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"The folder {Path.GetRelativePath(directory, current)} cannot be read: {ex.Message}"); continue; }
            foreach (string entry in entries)
            {
                string relative = Path.GetRelativePath(directory, entry);
                FileSystemInfo info = System.IO.Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                {
                    problems.Add($"{relative} is a symbolic link or junction. Preparation never follows links; copy the application into a plain folder first.");
                    continue;
                }
                if (info is DirectoryInfo)
                {
                    if (!Excluded.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) pending.Push(entry);
                    continue;
                }
                var file = (FileInfo)info;
                files.Add(new(relative, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks));
                if (files.Count > limit) { problems.Add($"The application folder has more than {limit} files; select the folder of the application's build output."); return files; }
            }
        }
        return files;
    }
    // runtimes/<os>-<arch>/... of another operating system or architecture than rid (os-only, "any", "unix" and unknown
    // folders are kept: the host's RID fallback may use them).
    internal static bool ForeignRuntime(string relative, string rid)
    {
        string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 3 || !parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase)) return false;
        string[] folder = parts[1].Split('-', 2), current = rid.Split('-', 2);
        string[] known = ["win", "linux", "linux-musl", "osx", "android", "ios", "iossimulator", "tvos", "maccatalyst", "browser", "freebsd"];
        if (folder.Length < 2 || !known.Contains(folder[0], StringComparer.OrdinalIgnoreCase)) return false;
        string[] arches = ["x64", "x86", "arm64", "arm", "musl-x64", "musl-arm64", "wasm"];
        if (!arches.Contains(folder[1], StringComparer.OrdinalIgnoreCase)) return false;
        return !folder[0].Equals(current[0], StringComparison.OrdinalIgnoreCase) || !folder[1].Equals(current[1], StringComparison.OrdinalIgnoreCase);
    }
    internal static string Fingerprint(IEnumerable<SourceFile> files) =>
        Hash(string.Join("\n", files.OrderBy(f => f.Relative, StringComparer.Ordinal).Select(f => $"{f.Relative.Replace('\\', '/')}|{f.Size}|{f.Modified}")));

    // ── target facts beyond the diagnostics inspection ─────────────────────────────────

    // Package name and version of every runtime asset the application's own deps.json lists (file name -> identity).
    internal static Dictionary<string, (string Package, string Version)> ApplicationAssets(string? depsPath, out Dictionary<string, string> packages)
    {
        var assets = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        packages = new(StringComparer.Ordinal);
        if (depsPath is null || !File.Exists(depsPath)) return assets;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
            var root = document.RootElement;
            string name = root.GetProperty("runtimeTarget").GetProperty("name").GetString() ?? "";
            foreach (var library in root.GetProperty("targets").GetProperty(name).EnumerateObject())
            {
                int slash = library.Name.IndexOf('/');
                if (slash < 0) continue;
                string package = library.Name[..slash], version = library.Name[(slash + 1)..];
                packages[package] = version;
                foreach (string kind in new[] { "runtime", "native" })
                    if (library.Value.TryGetProperty(kind, out var list))
                        foreach (var asset in list.EnumerateObject()) assets[Path.GetFileName(asset.Name)] = (package, version);
                if (library.Value.TryGetProperty("runtimeTargets", out var targets))
                    foreach (var asset in targets.EnumerateObject()) assets.TryAdd(Path.GetFileName(asset.Name), (package, version));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        return assets;
    }

    // The runtime major can come from the shared framework or a bundled runtime.
    internal static (int? RuntimeMajor, string? Problem) RuntimeConfig(string? path)
    {
        if (path is null || !File.Exists(path)) return (null, "runtimeconfig.json is missing");
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))?["runtimeOptions"];
            JsonNode? framework = node?["framework"] ?? (node?["includedFrameworks"] ?? node?["frameworks"])?.AsArray().FirstOrDefault(f => (string?)f?["name"] == "Microsoft.NETCore.App");
            return (Version.TryParse((string?)framework?["version"], out var v) ? v.Major : null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return (null, "runtimeconfig.json cannot be read: " + ex.Message); }
    }

    // The machine type of a Windows apphost or managed image (x64/arm64/x86), or null.
    internal static Architecture? ImageArchitecture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine switch
            {
                Machine.Amd64 => Architecture.X64, Machine.Arm64 => Architecture.Arm64, Machine.I386 => Architecture.X86, _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    // Locations where an in-place installation is refused: system and program folders (no elevation is ever attempted)
    // and signed macOS application bundles.
    internal static string? ProtectedLocation(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (full.Contains(".app" + Path.DirectorySeparatorChar + "Contents", StringComparison.Ordinal))
            return "it is inside a macOS application bundle; modifying a signed bundle breaks its code signature";
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows, Environment.SpecialFolder.CommonProgramFiles })
                if (Environment.GetFolderPath(f) is { Length: > 0 } p) roots.Add(p);
        }
        else roots.AddRange(["/usr", "/bin", "/sbin", "/lib", "/opt/homebrew", "/System", "/Library", "/Applications"]);
        return roots.FirstOrDefault(r => IsUnder(full, r)) is { } root ? $"it is under the protected location {root}" : null;
    }

    internal static long? FreeSpace(string path)
    {
        try
        {
            string? existing = Path.GetFullPath(path);
            while (existing is not null && !System.IO.Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
            return existing is null ? null : new DriveInfo(Path.GetPathRoot(existing)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // Copies through a hashing stream (one pass) into a new file; returns the hash of what was written.
    internal static async Task<string> CopyHashed(string source, string destination, CancellationToken token, Action<long>? progress = null)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, true))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                progress?.Invoke(read);
            }
        }
        // Executable permissions on macOS/Linux are preserved (the apphost must stay executable).
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void WriteJson<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }
    internal static T? ReadJson<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
    // Removes a folder this task created (staging), never anything else.
    internal static bool DeleteOwned(string folder, string within)
    {
        if (!IsUnder(folder, within) || PathComparer.Equals(Path.GetFullPath(folder), Path.GetFullPath(within))) return false;
        for (int i = 0; i < 5; i++)
        {
            try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        return !System.IO.Directory.Exists(folder);
    }
}

// The service. Instances are cheap; the payload and store root are explicit so tests and the CLI can isolate them.
internal sealed class TargetPreparationService
{
    private readonly Func<ProxyPayload?> payload;
    internal string Root { get; }
    // Test seams: available bytes of a path (null = unknown), and a hook called before each file is written.
    internal Func<string, long?> FreeSpace { get; init; } = Preparation.FreeSpace;
    internal Action<string>? BeforeWrite { get; init; }
    private ProxyPayload? loaded;
    private string? payloadProblem;

    internal TargetPreparationService(string? root = null, string? payloadDirectory = null)
    {
        Root = Path.GetFullPath(root ?? PreparationStore.Root);
        string? directory = payloadDirectory ?? ProxyPayload.Locate();
        payload = () =>
        {
            if (loaded is not null || payloadProblem is not null) return loaded;
            if (directory is null) { payloadProblem = $"This Workbench installation carries no Proxy payload ({ProxyPayload.Folder}/ beside the Workbench), so Prepare target and Fix Issues cannot deploy the Proxy. Install a complete Workbench (dotnet publish includes {ProxyPayload.Folder}/ with {PayloadManifest.FileName}); nothing was changed."; return null; }
            try { loaded = ProxyPayload.Load(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { payloadProblem = "The Proxy payload cannot be used: " + ex.Message; }
            return loaded;
        };
    }
    internal ProxyPayload? Payload => payload();
    internal string? PayloadProblem { get { payload(); return payloadProblem; } }
    internal string ApplicationFolder(string sourceExecutable) => Path.Combine(Root, Preparation.ApplicationId(sourceExecutable));

    // ── status of prepared deployments ─────────────────────────────────────────────────

    private static readonly Dictionary<string, (DateTime At, PreparedStatus Status)> statusCache = new(StringComparer.OrdinalIgnoreCase);
    // Quick (sizes and write times; cached for two seconds, for headers and Run Configuration) or deep (every hash).
    internal PreparedStatus? Status(string folder, bool deep = false, PreparationCapabilities? wanted = null)
    {
        string manifestPath = Path.Combine(folder, PreparationManifest.FileName);
        if (!File.Exists(manifestPath)) return null;
        string key = manifestPath + "|" + deep + "|" + wanted;
        lock (statusCache)
            if (!deep && statusCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(2)) return cached.Status;
        var status = Check(folder, manifestPath, deep, wanted);
        lock (statusCache) { if (statusCache.Count > 64) statusCache.Clear(); statusCache[key] = (DateTime.UtcNow, status); }
        return status;
    }
    internal static void ForgetStatus() { lock (statusCache) statusCache.Clear(); }

    private PreparedStatus Check(string folder, string manifestPath, bool deep, PreparationCapabilities? wanted)
    {
        var manifest = Preparation.ReadJson<PreparationManifest>(manifestPath);
        if (manifest is null || manifest.Schema != PreparationManifest.CurrentSchema)
            return new(PreparedState.Incomplete, [manifest is null ? "preparation.json cannot be read" : "unknown preparation schema " + manifest.Schema], manifest, folder);
        var incomplete = new List<string>(); var outdated = new List<string>(); var incompatible = new List<string>();
        if (!manifest.Verified) incomplete.Add("the preparation did not finish its verification");
        string here = ProxyPayload.CurrentRid(Preparation.ImageArchitecture(Path.Combine(folder, manifest.PreparedExecutable)));
        if (!string.Equals(manifest.Rid, here, StringComparison.Ordinal)) incompatible.Add($"prepared for {manifest.Rid}; this machine runs it as {here}");
        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(folder, file.Path);
            if (!Preparation.IsUnder(path, folder)) { incomplete.Add("the manifest names a file outside the deployment: " + file.Path); continue; }
            var info = new FileInfo(path);
            if (!info.Exists) { incomplete.Add(file.Path + " is missing"); continue; }
            if (info.Length != file.Size) { incomplete.Add(file.Path + " was modified (size)"); continue; }
            if (deep ? Preparation.FileHash(path) != file.Sha256 : info.LastWriteTimeUtc.Ticks != file.Modified && Preparation.FileHash(path) != file.Sha256)
                incomplete.Add(file.Path + " was modified (hash)");
            if (incomplete.Count > 20) break;
        }
        if (payload() is { } p)
        {
            if (p.ProxySha256 != manifest.ProxySha256 || p.TestingMvid.ToString() != manifest.TestingMvid) outdated.Add("the Workbench Proxy changed since preparation");
            if (p.FrameworkVersion != manifest.FrameworkVersion) outdated.Add($"the Workbench Proxy now uses TSharpVision {p.FrameworkVersion} (prepared with {manifest.FrameworkVersion})");
        }
        if (File.Exists(manifest.SourceExecutable))
        {
            var problems = new List<string>();
            var files = Preparation.Enumerate(manifest.SourceDirectory, problems);
            if (problems.Count == 0 && Preparation.Fingerprint(files) != manifest.SourceFingerprint) outdated.Add("the original application changed since preparation");
        }
        if (wanted is { } w && !manifest.Capabilities.SequenceEqual(Names(w))) outdated.Add("prepared with other testing capabilities (" + string.Join(", ", manifest.Capabilities) + ")");
        if (incompatible.Count > 0) return new(PreparedState.Incompatible, [.. incompatible, .. incomplete, .. outdated], manifest, folder);
        if (incomplete.Count > 0) return new(PreparedState.Incomplete, [.. incomplete, .. outdated], manifest, folder);
        if (outdated.Count > 0) return new(PreparedState.Outdated, outdated.ToArray(), manifest, folder);
        var notes = File.Exists(manifest.SourceExecutable) ? Array.Empty<string>() : ["the original application no longer exists at " + manifest.SourceExecutable];
        return new(PreparedState.Ready, notes, manifest, folder);
    }
    internal static string[] Names(PreparationCapabilities c) =>
        new[] { (PreparationCapabilities.Proxy, "proxy"), (PreparationCapabilities.Sdl, "sdl"), (PreparationCapabilities.Console, "console") }
            .Where(x => c.HasFlag(x.Item1)).Select(x => x.Item2).ToArray();

    // The active (last successfully prepared) deployment of an application, if any.
    internal PreparedStatus? Existing(string sourceExecutable, bool deep = false)
    {
        string app = ApplicationFolder(sourceExecutable);
        var active = Preparation.ReadJson<ActiveDeployment>(Path.Combine(app, ActiveDeployment.FileName));
        if (active is null || active.DeploymentId.Length == 0 || active.DeploymentId.IndexOfAny(['/', '\\', '.']) >= 0) return null;
        // A copy in a folder the user chose is recorded by its full path; it counts only while its own
        // preparation.json names the same deployment and application.
        string folder = active.Folder is { Length: > 0 } chosen && Path.IsPathFullyQualified(chosen) ? chosen : Path.Combine(app, active.DeploymentId);
        if (!System.IO.Directory.Exists(folder)) return null;
        var status = Status(folder, deep);
        return active.Folder is null || status?.Manifest is { } m && m.DeploymentId == active.DeploymentId && m.ApplicationId == Preparation.ApplicationId(sourceExecutable) ? status : null;
    }
    // Folder (optional): the full path of a copy outside the store; missing for copies in the store.
    internal sealed record ActiveDeployment(string DeploymentId, string Executable, DateTime PreparedUtc, string SourceExecutable, string? Folder = null)
    {
        internal const string FileName = "active.json";
    }

    // ── inspection (read-only) ─────────────────────────────────────────────────────────

    internal Task<PreparationInspection> InspectAsync(string executable, CancellationToken token = default) => Task.Run(() => Inspect(executable), token);
    internal PreparationInspection Inspect(string executable)
    {
        var target = TargetDiagnostics.Inspect(executable);
        var report = TargetDiagnostics.Evaluate(target, new DiagnosticContext(true, false, "NullDriver", null, null, Run: true));
        var prepared = target.Exists ? Status(target.Folder, deep: true) : null;
        string? source = prepared?.Manifest?.SourceExecutable is { Length: > 0 } s ? s : null;
        var notes = new List<string>();
        string? unsupported = null;
        if (!target.Set) unsupported = "No target executable is set.";
        else if (!target.Exists) unsupported = $"{Path.GetFileName(target.Target)} does not exist.";
        else if (prepared is not null)
        {
            if (source is null || !File.Exists(source)) unsupported = "This is a prepared copy whose original application no longer exists" + (source is null ? "." : $" ({source}). Prepare the original again.");
            else notes.Add($"This is a prepared copy of {source}. Re-prepare creates a new prepared copy from the original; this one is kept until the new one is verified.");
        }
        else unsupported = Unsupported(target, report);
        var existing = source is null && target.Exists && prepared is null ? Existing(target.Target) : null;
        if (unsupported is null && PayloadProblem is { } problem) unsupported = problem;
        return new(target.Target, target, report, prepared, source, existing, unsupported, notes.ToArray());
    }

    private string? Unsupported(TargetInspection i, TargetReport report)
    {
        string name = Path.GetFileName(i.Target);
        if (i.AccessProblem is { } access) return $"{name} cannot be read: {access}";
        if (!i.Portable) return $"{name} is not a Windows or .NET executable; only .NET TSharpVision applications can be prepared.";
        if (i.Main is null) return $"{name} has no .NET application assembly beside it. Native programs and single-file bundles cannot load the Workbench Proxy; use a multi-file .NET build of the application.";
        if (!i.Main.Readable) return $"The application assembly cannot be inspected: {i.Main.Problem}";
        var config = Preparation.RuntimeConfig(Path.ChangeExtension(i.Main.Path, ".runtimeconfig.json"));
        if (config.Problem is { } p) return $"{name}: {p}. Use a .NET deployment with its runtime configuration.";
        if (i.HostsDiagnostics == true) return $"{name} is a diagnostic build (it references TSharpWorkbench.Testing and hosts its own scenarios); it needs no Proxy preparation.";
        if (i.Framework is null) return $"{name} has no TSharpVision.dll beside it; it is not a TSharpVision application (or its deployment is incomplete).";
        if (payload() is { } p2 && config.RuntimeMajor is { } runtime && runtime < p2.RuntimeMajor)
            return $"{name} runs on .NET {runtime}; the Workbench Proxy needs .NET {p2.RuntimeMajor} or later.";
        return null;
    }

    // ── planning (read-only) ───────────────────────────────────────────────────────────

    internal Task<PreparationPlan> CreatePlanAsync(PreparationInspection inspection, PreparationStrategy strategy, PreparationCapabilities capabilities, CancellationToken token = default)
        => Task.Run(() => CreatePlan(inspection, strategy, capabilities), token);

    internal static PreparationCapabilities DefaultCapabilities => PreparationCapabilities.Proxy | PreparationCapabilities.Sdl | (OperatingSystem.IsWindows() ? PreparationCapabilities.Console : 0);

    // destination: the folder the user chose for a prepared copy; null proposes a new folder in the store.
    // Both modes use one framework policy, by version (FrameworkCompatibility); no acknowledgment, no probe.
    internal PreparationPlan CreatePlan(PreparationInspection inspection, PreparationStrategy strategy, PreparationCapabilities capabilities,
        string? destination = null)
    {
        // The SDL component is always part of the Proxy closure: the Proxy's handshake loads
        // TSharpVision.Drivers.SDL on every display driver, headless included (PROXY007 without it).
        capabilities |= PreparationCapabilities.Proxy | PreparationCapabilities.Sdl;
        if (!OperatingSystem.IsWindows()) capabilities &= ~PreparationCapabilities.Console;
        var problems = new List<string>(); var warnings = new List<string>(inspection.Notes); var compatibility = new List<string>();
        var components = new List<ComponentState>(); var entries = new List<PlanEntry>();
        string sourceExe = Path.GetFullPath(inspection.Source);
        string sourceDir = Path.GetDirectoryName(sourceExe)!;
        string appId = Preparation.ApplicationId(sourceExe), deploymentId = Preparation.NewDeploymentId();
        DestinationState? chosen = strategy == PreparationStrategy.PreparedCopy
            ? PreparationDestination.Check(destination, inspection, this, Path.Combine(Root, appId, deploymentId)) : null;
        string target = chosen?.Path ?? sourceDir;
        string preparedExe = Path.Combine(target, Path.GetFileName(sourceExe));
        if (chosen is { Usable: false }) problems.Add("Destination: " + chosen.Message);
        FrameworkComparison? framework = null;
        string frameworkState = "";
        if (inspection.Unsupported is { } unsupported) problems.Add(unsupported);
        var p = payload();
        if (p is null && PayloadProblem is { } payloadProblem && !problems.Contains(payloadProblem)) problems.Add(payloadProblem);
        if (inspection.IsPreparedCopy && strategy == PreparationStrategy.InPlaceInstallation)
            problems.Add("A prepared copy is not installed into; Re-prepare creates a new prepared copy from the original.");
        string sourceFingerprint = "";
        long required = 0;
        // The source is inspected again (the selected executable may be a prepared copy of it).
        var source = inspection.IsPreparedCopy && File.Exists(sourceExe) ? TargetDiagnostics.Inspect(sourceExe) : inspection.Target;
        if (problems.Count == 0 && p is not null)
        {
            if (strategy == PreparationStrategy.InPlaceInstallation && Preparation.ProtectedLocation(sourceDir) is { } protectedReason)
                problems.Add($"In-place installation is refused: {protectedReason}. Elevation is never attempted; use a prepared copy.");
            if (Preparation.IsUnder(Root, sourceDir))
                problems.Add($"The prepared-targets folder {Root} is inside the application folder; copying would recurse. Configure another PreparedTargetsRoot.");
            if (Preparation.IsUnder(sourceDir, Root))
                problems.Add("The selected application is inside the prepared-targets folder. Select the original application.");
            var files = Preparation.Enumerate(sourceDir, problems);
            sourceFingerprint = Preparation.Fingerprint(files);
            var appAssets = Preparation.ApplicationAssets(Path.ChangeExtension(source.Main?.Path ?? sourceExe, ".deps.json"), out var appPackages);
            var present = files.ToDictionary(f => f.Relative, Preparation.PathComparer);

            // Preserve the application's framework across the supported major. Loading, protocol negotiation and
            // scenarios establish actual runtime compatibility; the framework is never replaced.
            string frameworkPath = Path.Combine(sourceDir, "TSharpVision.dll");
            string? targetVersion = appPackages.GetValueOrDefault("TSharpVision");
            if (!File.Exists(frameworkPath)) { frameworkState = "Missing"; problems.Add("TSharpVision.dll is missing beside the executable."); }
            else
            {
                framework = FrameworkCompatibility.Compare(TargetDiagnostics.ReadAssembly(frameworkPath), targetVersion, TargetDiagnostics.ReadAssembly(p.FrameworkPath), p.FrameworkVersion, null, p.FrameworkSha256);
                compatibility.Add("Framework: " + framework.Describe());
                if (framework.Match == FrameworkMatch.Identical) frameworkState = "Compatible";
                else if (framework.Match is FrameworkMatch.SameVersion or FrameworkMatch.SameMajor)
                {
                    frameworkState = framework.Match == FrameworkMatch.SameVersion ? PreparationVerdict.SameVersion : PreparationVerdict.SameMajor;
                    if (PreparationVerdict.ProjectReferenced(Path.ChangeExtension(source.Main?.Path ?? sourceExe, ".deps.json"), "TSharpVision"))
                        compatibility.Add("Framework origin: the application's deps.json references TSharpVision as a project (built from framework source), not as the package.");
                    warnings.Add(framework.Match == FrameworkMatch.SameVersion ? PreparationVerdict.OtherBuildNote : FrameworkCompatibility.Policy);
                }
                else
                {
                    frameworkState = PreparationVerdict.VersionMismatch;
                    problems.Add($"{PreparationVerdict.VersionMismatch}: {framework.Reason}");
                }
            }
            bool frameworkOk = framework?.Compatible == true;
            components.Add(new("Framework", frameworkState, frameworkOk ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                $"TSharpVision {framework?.Declared ?? targetVersion ?? "?"} (application) / {p.FrameworkVersion} (Proxy)"
                + (frameworkState == "Compatible" ? "" : frameworkOk ? " - application framework kept; runtime verification required" : " - requires rebuild")));
            compatibility.Add($"Workbench Proxy: TSharpWorkbench.Proxy {Preparation.Mvid(Path.Combine(p.Directory, TargetDiagnostics.ProxyFile))?.ToString()[..8]}, closure from {ProxyPayload.DepsFile} ({p.Assets.Length} assets for {p.Rid}).");
            if (Preparation.Mvid(typeof(ScenarioRunner).Assembly.Location) is { } controller && controller != p.TestingMvid)
                problems.Add("The Proxy payload was not built with this Workbench (TSharpWorkbench.Testing differs). Reinstall the Workbench.");

            // Native assets are resolved for the application's process architecture.
            var architecture = Preparation.ImageArchitecture(sourceExe) ?? RuntimeInformation.ProcessArchitecture;
            var rid = ProxyPayload.CurrentRid(architecture);
            var assets = p.Assets;
            if (rid != p.Rid)
            {
                try { assets = ProxyPayload.Load(p.Directory, rid).Assets; }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { problems.Add("The Proxy payload cannot be resolved for " + rid + ": " + ex.Message); }
            }
            compatibility.Add($"Platform: {rid} (application {architecture}); this payload carries native assets for {string.Join(", ", p.NativeRids)}" + (p.Manifest is { } m ? $" ({m.Kind}, {m.Configuration})." : "."));
            string carried = p.NativeRids.Length == 0 ? "no RID" : string.Join(", ", p.NativeRids);
            // The framework assemblies the Proxy itself references (TSharpVision.Drivers.SDL: its handshake loads
            // it on every backend) are loaded from the application's own copy. The application's own build of the
            // same version is kept and used (never replaced); another version is refused whatever optional displays are
            // selected: one framework version per process.
            var proxyFamily = TargetDiagnostics.ReadAssembly(Path.Combine(p.Directory, TargetDiagnostics.ProxyFile)).References.Keys
                .Where(n => n.StartsWith("TSharpVision.", StringComparison.Ordinal)).ToArray();
            var familyConflicts = assets.Where(a => proxyFamily.Contains(Path.GetFileNameWithoutExtension(a.Destination)) && !a.Native)
                .Select(a => Classify(a, sourceDir, present, appAssets, appPackages)).Where(r => r.Action == PlanAction.Conflict).ToArray();
            foreach (var row in familyConflicts) problems.Add($"{row.Path}: {row.Note}");

            foreach (var (component, label, flag) in new[] { ("proxy", "Proxy and Testing", PreparationCapabilities.Proxy), ("sdl", "SDL window/GPU", PreparationCapabilities.Sdl), ("console", "Windows console", PreparationCapabilities.Console) })
            {
                var wanted = assets.Where(a => a.Component == component).ToArray();
                if (component == "console" && !OperatingSystem.IsWindows()) { components.Add(new(label, "Not available", DiagnosticSeverity.Info, "Windows only; Unix terminals are not a Proxy backend.")); continue; }
                if (component == "sdl" && !wanted.Any(a => a.Native))
                {
                    components.Add(new(label, "Unsupported", capabilities.HasFlag(flag) ? DiagnosticSeverity.Error : DiagnosticSeverity.Info, $"this Workbench's Proxy payload carries SDL native libraries for {carried} only"));
                    if (capabilities.HasFlag(flag)) problems.Add($"SDL support is unavailable for {rid}: this Workbench's Proxy payload carries SDL native libraries for {carried} only. Use a Workbench published for {rid} (dotnet publish -r {rid}, or -p:WorkbenchPayloadRuntimeIdentifiers={rid}).");
                    continue;
                }
                var rows = wanted.Select(a => Classify(a, sourceDir, present, appAssets, appPackages)).ToArray();
                int add = rows.Count(r => r.Action == PlanAction.Add), replace = rows.Count(r => r.Action == PlanAction.Replace), conflict = rows.Count(r => r.Action == PlanAction.Conflict);
                // A conflict is named by its kind (different build, version mismatch, user-owned file), never just "Conflict".
                string? conflictNote = rows.FirstOrDefault(r => r.Action == PlanAction.Conflict)?.Note
                    ?? (component == "proxy" ? familyConflicts.FirstOrDefault()?.Note : null);
                string state = conflictNote is not null ? PreparationVerdict.KindOf(conflictNote) : add + replace == 0 ? "Already valid" : add == rows.Length ? "Missing (fixable)" : replace > 0 && add == 0 ? "Outdated" : "Partial";
                bool selected = capabilities.HasFlag(flag);
                components.Add(new(label, state + (selected ? "" : " (not selected)"), conflictNote is not null && selected ? DiagnosticSeverity.Error : state == "Already valid" ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
                    conflictNote is not null ? PreparationVerdict.Detail(conflictNote)
                    : state switch { "Already valid" => "already deployed (identical)", "Missing (fixable)" => $"{add} file{(add == 1 ? "" : "s")} to add", _ => $"{add} to add, {replace} to replace" }));
                if (!selected) continue;
                foreach (var row in rows)
                {
                    entries.Add(row);
                    if (row.Action == PlanAction.Conflict) problems.Add($"{row.Path}: {row.Note}");
                }
            }
            if (strategy == PreparationStrategy.PreparedCopy)
            {
                var replaced = entries.Where(e => e.Action is PlanAction.Add or PlanAction.Replace).Select(e => e.Path).ToHashSet(Preparation.PathComparer);
                // A prepared copy runs on this machine only (its manifest records the RID): native asset folders of other
                // platforms and architectures (runtimes/<os>-<arch>/) can never load here and are not copied.
                var foreign = files.Where(f => Preparation.ForeignRuntime(f.Relative, rid)).ToArray();
                if (foreign.Length > 0) warnings.Add($"{foreign.Length} native files for other platforms (runtimes/<other RID>/, {Preparation.Size(foreign.Sum(f => f.Size))}) are not copied; the prepared copy is for {rid}.");
                foreach (var f in files.Where(f => !replaced.Contains(f.Relative) && !Preparation.ForeignRuntime(f.Relative, rid)))
                    entries.Add(new(PlanAction.Copy, f.Relative, f.Size, "application", f.Full, null));
                required = entries.Where(e => e.Action is PlanAction.Copy or PlanAction.Add or PlanAction.Replace).Sum(e => e.Size);
                if (inspection.Existing is { } existing)
                    warnings.Add($"An earlier prepared copy exists ({existing.State}) at {existing.Folder}; it is kept. Preparing again creates a new copy and switches to it only after verification.");
            }
            else
            {
                required = entries.Where(e => e.Action is PlanAction.Add or PlanAction.Replace).Sum(e => e.Size) * 2; // staging + destination
                required += entries.Where(e => e.Action == PlanAction.Replace).Sum(e => e.Size); // backups
                foreach (var e in entries.Where(e => e.Action == PlanAction.Replace))
                    if (File.Exists(Path.Combine(sourceDir, e.Path)) && new FileInfo(Path.Combine(sourceDir, e.Path)).IsReadOnly)
                        problems.Add($"{e.Path} is read-only; it is never forced. Clear its read-only attribute, or use a prepared copy.");
                if (!entries.Any(e => e.Action is PlanAction.Add or PlanAction.Replace)) warnings.Add("Every selected component is already deployed; nothing would be installed.");
            }
            long? free = FreeSpace(target);
            long margin = 64L << 20;
            if (free is { } available && available < required + margin)
                problems.Add($"Insufficient disk space: {Preparation.Size(required)} plus a {Preparation.Size(margin)} margin are needed, {Preparation.Size(available)} are free on {Path.GetPathRoot(Path.GetFullPath(target))}.");
            if (strategy == PreparationStrategy.PreparedCopy && Path.GetFullPath(preparedExe).Length >= 250 && OperatingSystem.IsWindows())
                warnings.Add("The prepared path is long; some tools limit paths to 260 characters.");
        }
        var distinct = problems.Distinct().ToArray();
        string fingerprint = Preparation.Hash(strategy + "|" + capabilities + "|" + sourceFingerprint + "|" + string.Join("\n", entries.Select(e => e.Action + ":" + e.Path + ":" + (e.Sha256 ?? e.Size.ToString())))
            + "|" + (chosen?.Kind == DestinationKind.Proposed ? "" : target) + "|" + framework?.Match);
        return new(strategy, capabilities, inspection, sourceExe, sourceDir, target, preparedExe, components.ToArray(), entries.ToArray(), distinct,
            warnings.ToArray(), compatibility.ToArray(), required, FreeSpace(target), appId, deploymentId, sourceFingerprint, fingerprint)
        {
            ChosenDestination = chosen?.Kind == DestinationKind.Proposed ? null : chosen?.Path, DestinationCheck = chosen, Framework = framework
        };
    }

    // The static probe of the Proxy files (Proxy, Testing) against the application's own builds of the
    // assemblies they reference, with the payload's builds as the reference. Information only (the
    // Compatibility view of Fix Issues); it never decides eligibility.
    internal static ProbeResult ProbeFor(ProxyPayload p, string sourceDir)
    {
        string[] consumers = [Path.Combine(p.Directory, TargetDiagnostics.ProxyFile), Path.Combine(p.Directory, TargetDiagnostics.TestingFile)];
        var consumerNames = consumers.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reference = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string consumer in consumers)
            foreach (string name in TargetDiagnostics.ReadAssembly(consumer).References.Keys)
            {
                if (consumerNames.Contains(name)) continue;
                string app = Path.Combine(sourceDir, name + ".dll"), own = Path.Combine(p.Directory, name + ".dll");
                if (File.Exists(app)) providers[name] = app;
                if (File.Exists(own)) reference[name] = own;
            }
        return CompatibilityProbe.Run(consumers, providers, reference);
    }

    // What one Proxy closure asset means for this application.
    private static PlanEntry Classify(PayloadAsset a, string sourceDir, Dictionary<string, Preparation.SourceFile> present,
        Dictionary<string, (string Package, string Version)> appAssets, Dictionary<string, string> appPackages)
    {
        string destination = a.Destination;
        string kind = a.Component;
        bool owned = a.Package is "TSharpWorkbench.Proxy" or "TSharpWorkbench.Testing";
        bool frameworkFamily = a.Package.StartsWith("TSharpVision", StringComparison.Ordinal);
        // The application itself depends on this package at another version: its own copy decides what it loads.
        if (!owned && appPackages.TryGetValue(a.Package, out var appVersion) && appVersion != a.Version
            && !(frameworkFamily && FrameworkCompatibility.SameMajor(appVersion, a.Version)))
            return new(PlanAction.Conflict, destination, a.Size, kind, a.Source, a.Sha256, $"{PreparationVerdict.VersionMismatch}: the application depends on {a.Package} {appVersion}; the Proxy closure needs {a.Version}");
        if (present.TryGetValue(destination, out var existing))
        {
            string hash = Preparation.FileHash(existing.Full);
            if (hash == a.Sha256) return new(PlanAction.Keep, destination, a.Size, kind, a.Source, a.Sha256, "identical file already deployed");
            if (owned)
            {
                // An earlier Workbench Proxy/Testing: replaced (in a prepared copy only the copy; in place with a backup).
                string? identity = TargetDiagnostics.ReadAssembly(existing.Full) is { Readable: true } facts ? facts.Name : null;
                return identity == a.Package
                    ? new(PlanAction.Replace, destination, a.Size, kind, a.Source, a.Sha256, "an earlier Workbench build of " + a.Package)
                    : new(PlanAction.Conflict, destination, a.Size, kind, a.Source, a.Sha256, $"{PreparationVerdict.UserOwned}: an unrelated file with the name of {a.Package} exists (kept, never replaced)");
            }
            // Preserve the application's framework-family assemblies across the supported major. Native and
            // unrelated third-party conflicts still require their original integrity/version checks.
            bool family = a.Package.StartsWith("TSharpVision", StringComparison.Ordinal) && !a.Native;
            string? familyProblem = family ? FrameworkCompatibility.FamilyProblem(TargetDiagnostics.ReadAssembly(existing.Full), TargetDiagnostics.ReadAssembly(a.Source)) : null;
            // A native library beside the executable with other content is not something a version identifies: never used, never replaced.
            if (a.Native)
                return new(PlanAction.Conflict, destination, a.Size, kind, a.Source, a.Sha256, appAssets.TryGetValue(destination, out var native) && native.Version == a.Version
                    ? $"{PreparationVerdict.DifferentBuild}: the application deploys {native.Package} {native.Version} with a different {destination} beside the executable (kept, never replaced)"
                    : $"{PreparationVerdict.UserOwned}: a different file with this name already exists (kept, never replaced)");
            if (appAssets.TryGetValue(destination, out var listed))
                return listed.Version != a.Version && !(frameworkFamily && FrameworkCompatibility.SameMajor(listed.Version, a.Version))
                    ? new(PlanAction.Conflict, destination, a.Size, kind, a.Source, a.Sha256, $"{PreparationVerdict.VersionMismatch}: the application deploys {listed.Package} {listed.Version}; the Proxy closure needs {a.Version}")
                    : familyProblem is not null
                        ? new(PlanAction.Conflict, destination, a.Size, kind, a.Source, a.Sha256, $"{PreparationVerdict.VersionMismatch}: {familyProblem}")
                        : new(PlanAction.Provided, destination, a.Size, kind, a.Source, a.Sha256, $"{(listed.Version == a.Version ? PreparationVerdict.SameVersion : PreparationVerdict.SameMajor)}: the application's own {listed.Package} {listed.Version} is kept and used (never replaced)");
            // Not listed by the application, but a framework assembly of the same version (for example a local build): kept.
            if (family && TargetDiagnostics.ReadAssembly(existing.Full) is { Readable: true } deployedFacts && deployedFacts.Name == a.Package)
                return familyProblem is null
                    ? new(PlanAction.Provided, destination, a.Size, kind, a.Source, a.Sha256, $"{PreparationVerdict.SameVersion}: the application's own {a.Package} {FrameworkCompatibility.Declared(deployedFacts)} is kept and used (never replaced)")
                    : new(PlanAction.Conflict, destination, a.Size, kind, a.Source, a.Sha256, $"{PreparationVerdict.VersionMismatch}: {familyProblem}");
            return new(PlanAction.Conflict, destination, a.Size, kind, a.Source, a.Sha256, $"{PreparationVerdict.UserOwned}: a different file with this name already exists (kept, never replaced)");
        }
        // A native asset the application's own deps.json already provides at the same package version (runtimes/<rid>/native).
        int runtimes = a.Source.LastIndexOf(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (a.Native && runtimes >= 0 && appPackages.TryGetValue(a.Package, out var same) && same == a.Version
            && present.TryGetValue(a.Source[(runtimes + 1)..], out var provided) && Preparation.FileHash(provided.Full) == a.Sha256)
            return new(PlanAction.Provided, destination, a.Size, kind, a.Source, a.Sha256, "provided by the application (" + provided.Relative + ")");
        return new(PlanAction.Add, destination, a.Size, kind, a.Source, a.Sha256, a.Package + " " + a.Version);
    }

    // ── execution (only after explicit approval) ─────────────────────────────────────────

    internal async Task<PreparationResult> ExecuteAsync(PreparationPlan approved, IProgress<PreparationProgress>? progress = null, CancellationToken token = default)
    {
        if (!approved.CanExecute) return new(false, false, "The plan has problems: " + approved.Problems[0], null, null, [], approved.Problems);
        // The approved plan must still describe the target: anything changed since it was shown is refused.
        var current = CreatePlan(Inspect(approved.Inspection.Executable), approved.Strategy, approved.Capabilities, approved.ChosenDestination);
        string Shape(PreparationPlan p) => Preparation.Hash(p.SourceFingerprint + "|" + string.Join("\n", p.Entries.Select(e => e.Action + ":" + e.Path + ":" + (e.Sha256 ?? e.Size.ToString())))
            + "|" + p.ChosenDestination + "|" + p.DestinationCheck?.Kind + "|" + p.Framework?.Match);
        if (!current.CanExecute || Shape(current) != Shape(approved))
            return new(false, false, "The target changed since the plan was shown; nothing was modified. Review the new plan.", null, null, [], current.Problems);
        return await Task.Run(() => approved.Strategy == PreparationStrategy.PreparedCopy
            ? CopyAsync(approved, progress, token) : InstallAsync(approved, progress, token), CancellationToken.None);
    }

    private async Task<PreparationResult> CopyAsync(PreparationPlan plan, IProgress<PreparationProgress>? progress, CancellationToken token)
    {
        var p = payload()!;
        string app = Path.Combine(Root, plan.ApplicationId);
        string final = plan.Destination;
        // A folder the user chose is staged beside it (same volume, one rename publishes it). Only what this
        // task creates is ever removed: the staging folder, the published folder, missing parents it created, and an empty
        // chosen folder is recreated empty if publishing fails.
        bool chosen = plan.ChosenDestination is not null;
        string within = chosen ? Path.GetDirectoryName(final)! : app;
        string staging = chosen ? Path.Combine(within, ".tsw-staging-" + plan.DeploymentId) : Path.Combine(app, ".staging-" + plan.DeploymentId);
        var created = new List<string>();
        for (string? d = within; d is not null && !System.IO.Directory.Exists(d); d = Path.GetDirectoryName(d)) created.Add(d);
        bool wasEmpty = chosen && System.IO.Directory.Exists(final);
        var work = plan.Changes.ToArray();
        long total = work.Sum(e => e.Size), done = 0; int count = 0;
        var written = new List<PreparedFile>();
        bool published = false, emptied = false;
        try
        {
            try { System.IO.Directory.CreateDirectory(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new PreparationException($"The destination is not writable ({ex.Message})."); }
            foreach (var e in work)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("Copying files", count, work.Length, done, total));
                BeforeWrite?.Invoke(e.Path);
                string target = Path.Combine(staging, e.Path);
                if (!Preparation.IsUnder(target, staging)) throw new PreparationException("Refused a path outside the deployment: " + e.Path);
                string hash = await Preparation.CopyHashed(e.Source!, target, token, n => done += n);
                if (e.Sha256 is { } expected && hash != expected) throw new PreparationException($"{e.Path}: the copied content does not match the Proxy payload (hash mismatch).");
                var info = new FileInfo(target);
                written.Add(new(e.Path.Replace('\\', '/'), info.Length, hash, info.LastWriteTimeUtc.Ticks, e.Action == PlanAction.Copy ? "application" : "workbench", e.Component));
                count++;
            }
            progress?.Report(new("Validating", count, work.Length, done, total));
            token.ThrowIfCancellationRequested();
            // Validation before publishing: the staged deployment must pass the same static checks a run uses.
            string stagedExe = Path.Combine(staging, Path.GetFileName(plan.SourceExecutable));
            var problems = Validate(stagedExe, plan.Capabilities);
            if (problems.Length > 0) throw new PreparationException("The staged deployment failed validation: " + string.Join(" ", problems));
            var manifest = new PreparationManifest
            {
                DeploymentId = plan.DeploymentId, ApplicationId = plan.ApplicationId, PreparedUtc = DateTime.UtcNow, SourceExecutable = plan.SourceExecutable,
                SourceDirectory = plan.SourceDirectory, SourceFingerprint = plan.SourceFingerprint, PreparedExecutable = Path.GetFileName(plan.SourceExecutable),
                ProxyVersion = TargetDiagnostics.ReadAssembly(Path.Combine(p.Directory, TargetDiagnostics.ProxyFile)).Version?.ToString() ?? "", ProxySha256 = p.ProxySha256,
                TestingMvid = p.TestingMvid.ToString(), FrameworkVersion = p.FrameworkVersion, FrameworkSha256 = p.FrameworkSha256, PayloadFingerprint = p.Fingerprint,
                Capabilities = Names(plan.Capabilities), Platform = RuntimeInformation.OSDescription, Architecture = (Preparation.ImageArchitecture(plan.SourceExecutable) ?? RuntimeInformation.ProcessArchitecture).ToString(),
                Rid = ProxyPayload.CurrentRid(Preparation.ImageArchitecture(plan.SourceExecutable)), WorkbenchVersion = typeof(TargetPreparationService).Assembly.GetName().Version?.ToString() ?? "",
                Files = written.ToArray(), Verified = false, Verification = ["static checks passed before publishing"]
            };
            Preparation.WriteJson(Path.Combine(staging, PreparationManifest.FileName), manifest);
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Publishing", count, work.Length, done, total));
            BeforeWrite?.Invoke("<publish>");
            // An empty folder the user chose (for example made with New folder) gives way to the staged folder, if it is
            // still empty; anything that appeared in it stops the preparation.
            if (wasEmpty)
            {
                if (System.IO.Directory.EnumerateFileSystemEntries(final).Any()) throw new PreparationException("The destination folder is no longer empty; nothing in it was changed.");
                System.IO.Directory.Delete(final, false); emptied = true;
            }
            // Publishing is one rename of the complete staged folder: no partially copied deployment ever has the final name.
            System.IO.Directory.Move(staging, final);
            published = true;
            progress?.Report(new("Verifying", count, work.Length, done, total));
            ForgetStatus(); TargetDiagnostics.Forget(Path.Combine(final, manifest.PreparedExecutable));
            BeforeWrite?.Invoke("<verify>");
            var check = Check(final, Path.Combine(final, PreparationManifest.FileName), true, null);
            if (check.Reasons.FirstOrDefault(r => r != "the preparation did not finish its verification" && !r.StartsWith("the original application changed", StringComparison.Ordinal)) is { } reason)
                throw new PreparationException("The published deployment failed verification: " + reason);
            var verified = manifest with { Verified = true, VerifiedUtc = DateTime.UtcNow, Verification = [.. manifest.Verification, "every file hash verified after publishing"] };
            Preparation.WriteJson(Path.Combine(final, PreparationManifest.FileName), verified);
            System.IO.Directory.CreateDirectory(app);
            Preparation.WriteJson(Path.Combine(app, ActiveDeployment.FileName), new ActiveDeployment(plan.DeploymentId, Path.Combine(final, verified.PreparedExecutable), verified.PreparedUtc, plan.SourceExecutable,
                chosen ? final : null));
            ForgetStatus();
            progress?.Report(new("Prepared", count, work.Length, total, total));
            int added = work.Count(e => e.Action != PlanAction.Copy);
            return new(true, false, $"Prepared {Path.GetFileName(plan.SourceExecutable)}: {count} files ({Preparation.Size(total)}), {added} Workbench component file{(added == 1 ? "" : "s")} added, in {final}. The original application was not modified.",
                Path.Combine(final, verified.PreparedExecutable), final, work.Where(e => e.Action != PlanAction.Copy).Select(e => $"{(e.Action == PlanAction.Add ? "added" : "replaced")} {e.Path}").ToArray(),
                [.. verified.Verification, "Deployment: " + final]);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or PreparationException or InvalidDataException)
        {
            // Only task-owned folders are removed: the staging folder, or the folder this task has just published.
            bool cleaned = Preparation.DeleteOwned(staging, within) & (!published || Preparation.DeleteOwned(final, within));
            if (emptied && !System.IO.Directory.Exists(final)) { try { System.IO.Directory.CreateDirectory(final); } catch (Exception io) when (io is IOException or UnauthorizedAccessException) { cleaned = false; } }
            foreach (string d in created) { try { if (System.IO.Directory.Exists(d) && !System.IO.Directory.EnumerateFileSystemEntries(d).Any()) System.IO.Directory.Delete(d); } catch (Exception io) when (io is IOException or UnauthorizedAccessException) { } }
            ForgetStatus();
            bool cancelled = ex is OperationCanceledException;
            string previous = Existing(plan.SourceExecutable) is { } e2 ? $" The previous prepared copy ({e2.State}) is unchanged." : "";
            return new(false, cancelled, (cancelled ? "Preparation cancelled. " : "Preparation failed: " + ex.Message + " ")
                + (cleaned ? "The partial copy was removed; " : "The partial copy could not be removed completely (" + staging + "); ") + "the original application was not modified." + previous,
                null, null, [], [ex.ToString()]) { RolledBack = true, RollbackComplete = cleaned };
        }
    }

    // Static validation of a deployment for the selected capabilities: no blocking finding. A same-version
    // other framework build (FW003) is information, not a blocking finding; Verify target decides at runtime.
    internal static string[] Validate(string executable, PreparationCapabilities capabilities)
    {
        TargetDiagnostics.Forget(executable);
        var inspection = TargetDiagnostics.Inspect(executable);
        var problems = new List<string>();
        var backends = new List<string> { "NullDriver" };
        if (capabilities.HasFlag(PreparationCapabilities.Sdl)) backends.AddRange(["SDLDriver", "SDLGpuDriver"]);
        if (capabilities.HasFlag(PreparationCapabilities.Console) && OperatingSystem.IsWindows()) backends.Add("Win32ConsoleDriver");
        foreach (string backend in backends)
        {
            var report = TargetDiagnostics.Evaluate(inspection, new DiagnosticContext(true, false, backend, null, null, Run: true));
            foreach (var f in report.Blocking.Where(f => f.Component != "Preparation")) problems.Add($"{f.Code} ({backend}): {f.Message}");
        }
        return problems.Distinct().ToArray();
    }

    private async Task<PreparationResult> InstallAsync(PreparationPlan plan, IProgress<PreparationProgress>? progress, CancellationToken token)
    {
        var p = payload()!;
        string id = Preparation.NewDeploymentId();
        string work = Path.Combine(Root, plan.ApplicationId, "installs", id);
        string stage = Path.Combine(work, "stage"), backup = Path.Combine(work, "backup"), recordPath = Path.Combine(work, InstallRecord.FileName);
        var changes = plan.Entries.Where(e => e.Action is PlanAction.Add or PlanAction.Replace).ToArray();
        long total = changes.Sum(e => e.Size) * 2, done = 0; int count = 0, steps = changes.Length * 2;
        var record = new InstallRecord
        {
            Id = id, TargetDirectory = plan.SourceDirectory, Executable = plan.SourceExecutable, StartedUtc = DateTime.UtcNow, Capabilities = Names(plan.Capabilities),
            ProxySha256 = p.ProxySha256, FrameworkVersion = p.FrameworkVersion,
            Files = changes.Select(e => new InstalledFile(e.Path, e.Action == PlanAction.Add ? "added" : "replaced", e.Sha256!,
                e.Action == PlanAction.Replace ? Preparation.FileHash(Path.Combine(plan.SourceDirectory, e.Path)) : null,
                e.Action == PlanAction.Replace ? Path.Combine("backup", e.Path) : null, false)).ToArray()
        };
        bool publishing = false;
        try
        {
            System.IO.Directory.CreateDirectory(work);
            Preparation.WriteJson(recordPath, record);
            // 1. Stage every file privately and verify its hash before the application folder is touched.
            foreach (var e in changes)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("Staging files", count, steps, done, total));
                BeforeWrite?.Invoke("stage:" + e.Path);
                string hash = await Preparation.CopyHashed(e.Source!, Path.Combine(stage, e.Path), token, n => done += n);
                if (hash != e.Sha256) throw new PreparationException($"{e.Path}: staged content does not match the Proxy payload.");
                count++;
            }
            // 2. Permission probe: a refused write is reported before any application file changes.
            string probe = Path.Combine(plan.SourceDirectory, ".tsharpworkbench-probe-" + id);
            try { File.WriteAllBytes(probe, []); File.Delete(probe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new PreparationException($"The application folder is not writable ({ex.Message}). Nothing was changed; use a prepared copy."); }
            // 3. Backups of every file that will be replaced, verified.
            foreach (var f in record.Files.Where(f => f.Backup is not null))
            {
                string original = Path.Combine(plan.SourceDirectory, f.Path);
                string copy = await Preparation.CopyHashed(original, Path.Combine(work, f.Backup!), token);
                if (copy != f.OriginalSha256) throw new PreparationException($"{f.Path} changed while it was backed up; nothing was changed.");
            }
            record = record with { State = "publishing" };
            Preparation.WriteJson(recordPath, record);
            publishing = true;
            // 4. Publish file by file, recording each one. A file that appeared or changed since planning stops the installation.
            for (int index = 0; index < record.Files.Length; index++)
            {
                var f = record.Files[index];
                token.ThrowIfCancellationRequested();
                progress?.Report(new("Installing files", count, steps, done, total));
                BeforeWrite?.Invoke(f.Path);
                string destination = Path.Combine(plan.SourceDirectory, f.Path);
                if (!Preparation.IsUnder(destination, plan.SourceDirectory)) throw new PreparationException("Refused a path outside the application folder: " + f.Path);
                if (f.Action == "added" && File.Exists(destination)) throw new PreparationException($"{f.Path} appeared in the application folder after planning; it is not overwritten.");
                if (f.Action == "replaced" && Preparation.FileHash(destination) != f.OriginalSha256) throw new PreparationException($"{f.Path} changed after planning; it is not overwritten.");
                string temporary = destination + ".tsw-" + id + ".tmp";
                string hash = await Preparation.CopyHashed(Path.Combine(stage, f.Path), temporary, token, n => done += n);
                if (hash != f.InstalledSha256) { File.Delete(temporary); throw new PreparationException($"{f.Path}: content mismatch while installing."); }
                File.Move(temporary, destination, f.Action == "replaced");
                record = record with { Files = record.Files.Select((x, i) => i == index ? x with { Done = true } : x).ToArray() };
                Preparation.WriteJson(recordPath, record);
                count++;
            }
            // 5. Verify what is now in the folder: installed hashes and the static readiness for the selected capabilities.
            progress?.Report(new("Verifying", count, steps, done, total));
            BeforeWrite?.Invoke("<verify>");
            foreach (var f in record.Files)
                if (Preparation.FileHash(Path.Combine(plan.SourceDirectory, f.Path)) != f.InstalledSha256) throw new PreparationException($"{f.Path} does not hold the installed content.");
            var problems = Validate(plan.SourceExecutable, plan.Capabilities);
            if (problems.Length > 0) throw new PreparationException("The installation failed validation: " + string.Join(" ", problems));
            record = record with { State = "installed", Notes = ["Backups of replaced files: " + backup] };
            Preparation.WriteJson(recordPath, record);
            Preparation.DeleteOwned(stage, work);
            progress?.Report(new("Installed", count, steps, total, total));
            TargetDiagnostics.Forget(plan.SourceExecutable);
            var lines = record.Files.Select(f => $"{f.Action} {Path.Combine(plan.SourceDirectory, f.Path)}").ToArray();
            return new(true, false, $"Installed {record.Files.Count(f => f.Action == "added")} file(s) and replaced {record.Files.Count(f => f.Action == "replaced")} in {plan.SourceDirectory}. "
                + "No application assembly, deps.json or runtimeconfig.json was changed.", plan.SourceExecutable, plan.SourceDirectory, lines,
                [.. lines, "Install record: " + recordPath, "Backups: " + backup]) { Record = recordPath };
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or PreparationException or InvalidDataException)
        {
            var (complete, notes) = publishing ? Rollback(record, work) : (true, Array.Empty<string>());
            record = record with { State = !publishing ? "abandoned before publishing" : complete ? "rolled back" : "rollback incomplete", Notes = notes };
            try { Preparation.WriteJson(recordPath, record); } catch (Exception io) when (io is IOException or UnauthorizedAccessException) { }
            Preparation.DeleteOwned(stage, work);
            TargetDiagnostics.Forget(plan.SourceExecutable);
            bool cancelled = ex is OperationCanceledException;
            string what = !publishing ? "Nothing in the application folder was changed." : complete ? "Every change was rolled back; the application folder is as before."
                : "Rollback was incomplete: " + string.Join(" ", notes) + " Install record: " + recordPath;
            return new(false, cancelled, (cancelled ? "Installation cancelled. " : "Installation failed: " + ex.Message + " ") + what, null, null, notes, [ex.ToString(), .. notes])
            { RolledBack = publishing, RollbackComplete = complete };
        }
    }

    // Undoes the installed files of a record in reverse order. A file is deleted or restored only while it still holds
    // exactly what Workbench installed; anything modified afterwards is left in place and reported.
    internal static (bool Complete, string[] Notes) Rollback(InstallRecord record, string work)
    {
        var notes = new List<string>();
        bool complete = true;
        foreach (var f in Enumerable.Reverse(record.Files))
        {
            string path = Path.Combine(record.TargetDirectory, f.Path);
            foreach (string stray in SafeGlob(record.TargetDirectory, Path.GetFileName(f.Path) + ".tsw-" + record.Id + ".tmp")) TryDelete(stray);
            if (!f.Done) continue;
            if (!File.Exists(path)) { if (f.Action == "replaced") { complete = false; notes.Add($"{f.Path} is missing; its backup is {Path.Combine(work, f.Backup!)}."); } continue; }
            if (Preparation.FileHash(path) != f.InstalledSha256) { complete = false; notes.Add($"{f.Path} was modified after installation and was left unchanged."); continue; }
            try
            {
                if (f.Action == "added") File.Delete(path);
                else
                {
                    string temporary = path + ".tsw-restore-" + record.Id + ".tmp";
                    File.Copy(Path.Combine(work, f.Backup!), temporary, false);
                    if (Preparation.FileHash(temporary) != f.OriginalSha256) { File.Delete(temporary); throw new IOException("backup content mismatch"); }
                    File.Move(temporary, path, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; notes.Add($"{f.Path} could not be restored: {ex.Message}"); }
        }
        return (complete, notes.ToArray());
    }
    // Undoes a completed, add-only installation (a fix beside another framework build whose
    // verification failed). Only
    // files the record added and that still hold exactly the installed content are deleted; a replacement is never part of
    // such an installation, and a record with one is refused. Nothing else in the folder is touched.
    internal static (bool Complete, string[] Notes) UndoInstall(string recordPath, string reason)
    {
        var record = Preparation.ReadJson<InstallRecord>(recordPath);
        if (record is null || record.Schema != InstallRecord.CurrentSchema || record.State != "installed")
            return (false, [$"The install record {recordPath} cannot be used (state {record?.State ?? "unreadable"}); nothing was removed."]);
        if (record.Files.Any(f => f.Action != "added"))
            return (false, ["The installation replaced files; it is not undone automatically (its backups are kept in " + Path.GetDirectoryName(recordPath) + ")."]);
        var (complete, notes) = Rollback(record, Path.GetDirectoryName(recordPath)!);
        var undone = record with { State = complete ? "rolled back: " + reason : "rollback incomplete: " + reason, Notes = [.. record.Notes, .. notes] };
        try { Preparation.WriteJson(recordPath, undone); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        TargetDiagnostics.Forget(record.Executable);
        return (complete, notes);
    }

    private static IEnumerable<string> SafeGlob(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        return File.Exists(path) ? [path] : [];
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    // ── what the UI offers for a target (from the diagnostics; no second readiness engine) ──

    internal (OfferKind Kind, string Text) Offer(TargetReport report)
    {
        var i = report.Inspection;
        if (!i.Set || !i.Exists) return (OfferKind.None, "");
        if (Status(i.Folder) is { } prepared)
            return (OfferKind.Reprepare, prepared.Label);
        if (i.HostsDiagnostics == true) return (OfferKind.None, "Diagnostic build: no preparation needed");
        bool inspectable = i.AccessProblem is null && i.Main is { Readable: true };
        var proxy = report.Findings.Where(f => f.Component == "Proxy").ToArray();
        bool needs = proxy.Any(f => f.Code is "PROXY001" or "PROXY002" or "PROXY003" or "PROXY006" or "PROXY007");
        if (!needs) return (OfferKind.None, proxy.Any(f => f.Code == "PROXY000") ? "Proxy deployed"
            : proxy.Any(f => f.Code == "PROXY008") ? "Proxy from another build (Fix Issues)" : "");
        if (!inspectable) return (OfferKind.Unavailable, "Target not inspectable");
        if (report.Findings.Any(f => f.Code == "FW002")) return (OfferKind.Unavailable, "Framework incompatible");
        return (OfferKind.Prepare, report.Findings.First(f => f.Component == "Proxy" && f.Severity != DiagnosticSeverity.Ok).Short);
    }
}
