namespace TSharpWorkbench.Testing;

/// <summary>Ordinary child launch configuration shared by project CLI and TUI execution.</summary>
public static class TargetLaunch
{
    public static void Validate(string[] arguments, IReadOnlyDictionary<string, string> environment)
    {
        if (arguments is null || environment is null || arguments.Length > 64 || environment.Count > 32
            || arguments.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException("Invalid target launch configuration.");
        foreach (var pair in environment)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0')
                || pair.Value is null || pair.Value.Contains('\0')
                || pair.Key.StartsWith("TSHARPVISION_", StringComparison.OrdinalIgnoreCase)
                || new[] { "HOME", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Target environment cannot replace session-owned or profile isolation variables.");
    }

    public static string Expand(string value, string state, string fixture, string targetDirectory)
        => value.Replace("{state}", state, StringComparison.Ordinal)
            .Replace("{fixture}", fixture, StringComparison.Ordinal)
            .Replace("{targetDir}", targetDirectory, StringComparison.Ordinal);
}
