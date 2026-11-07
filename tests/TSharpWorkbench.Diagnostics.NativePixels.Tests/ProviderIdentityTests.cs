using TSharpWorkbench.Diagnostics.NativePixels;

namespace TSharpWorkbench.Diagnostics.NativePixels.Tests;

public sealed class ProviderIdentityTests
{
    [Xunit.Fact]
    public void CanonicalTypeAndAssemblyIdentity()
    {
        var type = typeof(ApplicationExecution);
        Xunit.Assert.Equal("TSharpWorkbench.Diagnostics.NativePixels", type.Assembly.GetName().Name);
        Xunit.Assert.Equal("TSharpWorkbench.Diagnostics.NativePixels", type.Namespace);
        Xunit.Assert.Same(type.Assembly, TSharpWorkbench.Testing.NativeProviderDeployment.Load(type.Assembly.GetName().Name!));
        Xunit.Assert.DoesNotContain(System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies,
            a => a.GetName().Name?.StartsWith("TSharpVision.Diagnostics.", StringComparison.Ordinal) == true);
        Xunit.Assert.Same(type, type.Assembly.GetType("TSharpWorkbench.Diagnostics.NativePixels.ApplicationExecution", throwOnError: true));
    }
}
