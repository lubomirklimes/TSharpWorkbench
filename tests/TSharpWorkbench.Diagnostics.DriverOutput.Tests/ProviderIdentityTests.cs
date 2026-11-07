using TSharpWorkbench.Diagnostics.DriverOutput;

namespace TSharpWorkbench.Diagnostics.DriverOutput.Tests;

public sealed class ProviderIdentityTests
{
    [Xunit.Fact]
    public void CanonicalTypeAndAssemblyIdentity()
    {
        var type = typeof(VtOracle);
        Xunit.Assert.Equal("TSharpWorkbench.Diagnostics.DriverOutput", type.Assembly.GetName().Name);
        Xunit.Assert.Equal("TSharpWorkbench.Diagnostics.DriverOutput", type.Namespace);
        Xunit.Assert.Same(type, type.Assembly.GetType("TSharpWorkbench.Diagnostics.DriverOutput.VtOracle", throwOnError: true));
    }
}
