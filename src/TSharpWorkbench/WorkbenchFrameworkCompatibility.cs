// Workbench 1.x accepts TSharpVision 1.x for preparation. Version and file identity are
// diagnostics; protocol negotiation, API loading and actual scenarios establish runtime support.
internal enum FrameworkMatch { Identical, SameVersion, DifferentVersion, Unknown, SameMajor }

internal sealed record FrameworkComparison(FrameworkMatch Match, string Declared, string Expected, Version? Assembly, Version? ExpectedAssembly,
    string? Sha256, string? ExpectedSha256, string Reason)
{
    internal bool Compatible => Match is FrameworkMatch.Identical or FrameworkMatch.SameVersion or FrameworkMatch.SameMajor;
    internal string Short(string sha) => sha.Length >= 12 ? sha[..12] : sha;
    // One line for plans, diagnostics and the Compatibility view.
    internal string Describe() => Match switch
    {
        FrameworkMatch.Identical => $"TSharpVision {Declared}: identical build (SHA-256 {Short(Sha256 ?? "")}).",
        FrameworkMatch.SameVersion => $"TSharpVision {Declared}: same version as the Proxy's {Expected}, another build (SHA-256 {Short(Sha256 ?? "")}, Proxy {Short(ExpectedSha256 ?? "")}). "
            + "Supported; Verify target confirms that the Proxy loads it.",
        FrameworkMatch.SameMajor => $"TSharpVision {Declared}: same major as the Proxy's {Expected}; the application's framework is never replaced. "
            + "Verify target and the scenario must establish runtime compatibility.",
        FrameworkMatch.DifferentVersion => "Different TSharpVision version: " + Reason,
        _ => "The framework version cannot be determined: " + Reason
    };
}

internal static class FrameworkCompatibility
{
    internal const string Policy = "Workbench 1.x targets TSharpVision 1.x. Minor, patch, assembly build and SHA-256 differences are not rejection criteria. "
        + "The application's framework is never replaced. A matching major does not guarantee functionality: "
        + "the real Proxy handshake, negotiated capabilities and scenarios decide runtime compatibility.";
    internal const string RuntimeLimits = "Verify target proves that the application starts, loads the Proxy and completes the handshake. Behaviour differences "
        + "of another build (changed members, virtual dispatch, static initialisation, native libraries) show up only when a scenario uses them, "
        + "as MissingMethodException, TypeLoadException, a binding error or a failed check; the run reports them.";

    // The declared version of an assembly: its informational version without build metadata, else its assembly version.
    internal static string Declared(AssemblyFacts? facts) =>
        facts?.Informational is { Length: > 0 } informational ? Core(informational) : facts?.Version is { } v ? v.ToString(3) : "";
    internal static string Core(string version) => version.Split('+')[0].Trim();

    // application: the deployed TSharpVision assembly; depsVersion: the version its deps.json names (null when not listed).
    // expected: the payload's framework assembly and the package version its Proxy was built against.
    // sha / expectedSha: already known hashes (the diagnostics cache them); computed when null.
    internal static FrameworkComparison Compare(AssemblyFacts? application, string? depsVersion, AssemblyFacts? expected, string expectedVersion,
        string? sha = null, string? expectedSha = null)
    {
        string declared = Declared(application), wanted = Core(expectedVersion);
        sha ??= application is { Readable: true } && File.Exists(application.Path) ? Preparation.FileHash(application.Path) : null;
        expectedSha ??= expected is { Readable: true } && File.Exists(expected.Path) ? Preparation.FileHash(expected.Path) : null;
        FrameworkComparison Result(FrameworkMatch match, string reason) =>
            new(match, declared.Length > 0 ? declared : depsVersion ?? "unknown", wanted, application?.Version, expected?.Version, sha, expectedSha, reason);
        if (application is not { Readable: true }) return Result(FrameworkMatch.Unknown, application?.Problem ?? "TSharpVision.dll is missing.");
        if (!TryMajor(declared, out int major) || !TryMajor(wanted, out int expectedMajor))
            return Result(FrameworkMatch.Unknown, "the framework major cannot be determined.");
        if (major != expectedMajor)
            return Result(FrameworkMatch.DifferentVersion, $"TSharpVision {declared} is outside the supported {expectedMajor}.x major. The application's framework is never replaced.");
        if (depsVersion is { Length: > 0 } listed && (!TryMajor(listed, out int depsMajor) || depsMajor != major))
            return Result(FrameworkMatch.DifferentVersion, $"The application's deps.json framework major ({listed}) disagrees with its deployed assembly ({declared}).");
        if (application.Version is { } identity && identity.Major != major)
            return Result(FrameworkMatch.DifferentVersion, "The deployed assembly identity and declared framework major disagree.");
        return sha is not null && sha == expectedSha ? Result(FrameworkMatch.Identical, "identical file content")
            : string.Equals(declared, wanted, StringComparison.OrdinalIgnoreCase) && application.Version == expected?.Version
                ? Result(FrameworkMatch.SameVersion, "same declared and assembly version, other content")
                : Result(FrameworkMatch.SameMajor, "same major; verify protocol, capabilities and actual behavior at runtime");
    }

    internal static bool TryMajor(string version, out int major)
    {
        major = 0;
        return Version.TryParse(Core(version).Split('-')[0], out var parsed) && (major = parsed.Major) > 0;
    }
    internal static bool SameMajor(string left, string right) => TryMajor(left, out int a) && TryMajor(right, out int b) && a == b;

    // Framework-family assemblies are preserved across minor/patch builds; loading and negotiated capabilities
    // remain the runtime check. Unreadable/wrong identities and different majors remain deployment errors.
    internal static string? FamilyProblem(AssemblyFacts application, AssemblyFacts expected)
    {
        if (!application.Readable || application.Name != expected.Name)
            return $"the deployed framework assembly is unreadable or has the wrong identity for {expected.Name}";
        string have = Declared(application), want = Declared(expected);
        if (!SameMajor(have, want))
            return $"the application deploys {application.Name} {have}; the supported framework major is {want}";
        if (application.Version is { } a && expected.Version is { } b && a.Major != b.Major)
            return $"the application deploys {application.Name} with another assembly major ({a.Major} vs {b.Major})";
        return null;
    }
}
