using System.Xml.Linq;

namespace TSharpWorkbench.Tests.Infrastructure;

internal static class FrameworkPackageVersion
{
    internal static string Expected => XDocument.Load(Path.Combine(Core.TargetDiagnosticsUxTests.RepositoryRoot(), "Directory.Packages.props"))
        .Descendants("PackageVersion").Single(e => (string?)e.Attribute("Include") == "TSharpVision").Attribute("Version")!.Value;
}
