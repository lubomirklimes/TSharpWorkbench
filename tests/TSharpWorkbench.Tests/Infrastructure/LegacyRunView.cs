using System.Runtime.CompilerServices;

namespace TSharpWorkbench.Tests.Infrastructure;

// Made the compact Basic presentation the product default of Run Configuration. The suites written for the
// Dialog keep driving that dialog: settings files without a stored choice resolve to Advanced in this
// test assembly. TargetDiagnosticsUxTests stores "basic" explicitly where it needs Basic, and checks the product default directly.
internal static class LegacyRunView
{
    [ModuleInitializer]
    internal static void Pin() => WorkbenchPreferences.DefaultRunViewOverride = WorkbenchPreferences.AdvancedView;
}
