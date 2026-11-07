using System.Reflection;
using TSharpWorkbench.Testing;

// Product identity; contextual documentation is compiled from Help/TSharpWorkbench.help.json.
internal static class WorkbenchHelp
{
    // Compatibility for existing keyboard-content checks; the installed HLP is the single source.
    internal static string[] Keyboard()
    {
        using var help = new WorkbenchHelpResource(Path.Combine(AppContext.BaseDirectory, "Help", "TSharpWorkbench.hlp"));
        var lines = new List<string>();
        for (var p = help.File.GetTopic((int)HelpTopic.Keyboard).paragraphs; p is not null; p = p.next)
            lines.Add(p.Text.TrimEnd('\n'));
        return lines.ToArray();
    }

    // Product identity shown by the Start Page and About. The version is always the Workbench
    // assembly's own, never the framework's. The copyright years are the LICENSE file's (2021-2026).
    internal const string ProductName = "TSharpWorkbench", Tagline = "UI Testing & Automation for TSharpVision";
    internal const string Author = "Lubomir Klimes", CopyrightYears = "2021–2026", Copyright = "Copyright © " + CopyrightYears + " " + Author;
    internal static string WorkbenchVersion => Version(typeof(WorkbenchHelp).Assembly);
    private static string Version(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? assembly.GetName().Version?.ToString() ?? "unknown";

    internal static string[] About() =>
    [
        ProductName,
        Tagline,
        "Version " + WorkbenchVersion,
        "TSharpVision " + Version(typeof(TSharpVision.TView).Assembly),
        "",
        "Created by " + Author,
        Copyright,
        "MIT License (see the LICENSE file)",
        "",
        "Discovers external JSON scenarios, runs them against an owned TSharpVision target,",
        "keeps logical and native evidence apart and gates visual baselines.",
        "Projects are native .tsw archives containing SQLite data and project-owned evidence.",
    ];
}
