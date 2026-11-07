using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

public sealed class NativeProviderDeploymentTests
{
    [Fact]
    public void RenamedStaleAssemblyIsRejectedBeforeLoading() =>
        Assert.Throws<InvalidOperationException>(() => NativeProviderDeployment.ValidateIdentity(
            typeof(ScenarioRunner).Assembly.Location, "TSharpWorkbench.Diagnostics.NativePixels"));

    [Theory]
    [InlineData("TSharpVision.Diagnostics.NativePixels")]
    [InlineData("TSharpVision.Diagnostics.NativeHosts")]
    public void LegacyProvidersAreRejected(string identity) =>
        Assert.Throws<InvalidOperationException>(() => NativeProviderDeployment.Load(identity));

    [Fact]
    public void ProviderIdentityCannotSelectAnArbitraryAssembly() =>
        Assert.Throws<InvalidOperationException>(() => NativeProviderDeployment.Load("TSharpVision"));

    [Fact]
    public void MissingProviderReportsTheDeploymentContract()
    {
        // Logical-only test deployment deliberately has no native provider payload.
        var error = Assert.Throws<FileNotFoundException>(() =>
            NativeProviderDeployment.Load("TSharpWorkbench.Diagnostics.NativePixels"));
        Assert.Contains("helper apphost", error.Message);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "TSharpWorkbench.Diagnostics.NativePixels.dll"), error.FileName);
    }
}
