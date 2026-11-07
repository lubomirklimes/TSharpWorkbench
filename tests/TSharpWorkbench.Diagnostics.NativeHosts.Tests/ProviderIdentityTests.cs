using TSharpWorkbench.Diagnostics.NativeHosts;

namespace TSharpWorkbench.Diagnostics.NativeHosts.Tests;

public sealed class ProviderIdentityTests
{
    [Xunit.Fact]
    public void CanonicalTypeAndAssemblyIdentity()
    {
        var type = typeof(ApplicationExecution);
        Xunit.Assert.Equal("TSharpWorkbench.Diagnostics.NativeHosts", type.Assembly.GetName().Name);
        Xunit.Assert.Equal("TSharpWorkbench.Diagnostics.NativeHosts", type.Namespace);
        Xunit.Assert.Same(type.Assembly, TSharpWorkbench.Testing.NativeProviderDeployment.Load(type.Assembly.GetName().Name!));
        Xunit.Assert.DoesNotContain(System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies,
            a => a.GetName().Name?.StartsWith("TSharpVision.Diagnostics.", StringComparison.Ordinal) == true);
        Xunit.Assert.Same(type, type.Assembly.GetType("TSharpWorkbench.Diagnostics.NativeHosts.ApplicationExecution", throwOnError: true));
    }
}
