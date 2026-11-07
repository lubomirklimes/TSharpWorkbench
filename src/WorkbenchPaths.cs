namespace TSharpWorkbench.Support;

// Workbench source ownership, independent of the framework checkout. Linked only into diagnostic tools.
internal static class WorkbenchPaths
{
    internal static string? FindRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("TSHARPWORKBENCH_ROOT");
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Path.IsPathRooted(configured) || !IsRoot(configured))
                throw new InvalidOperationException("TSHARPWORKBENCH_ROOT must be an absolute Workbench source root.");
            return Path.GetFullPath(configured);
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (IsRoot(dir.FullName)) return dir.FullName;
        return null;
    }

    private static bool IsRoot(string root) =>
        File.Exists(Path.Combine(root, "TSharpWorkbench.slnx")) ||
        File.Exists(Path.Combine(root, "diagnostics", "Workbench.Monorepo.props"));

    internal static string OutputRoot => FindRoot() ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TSharpWorkbench");
}
