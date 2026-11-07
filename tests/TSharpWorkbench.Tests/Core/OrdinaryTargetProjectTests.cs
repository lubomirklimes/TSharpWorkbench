using TSharpWorkbench.Testing;
using Xunit;
namespace TSharpWorkbench.Tests.Core;

public sealed class OrdinaryTargetProjectTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("left folder", "\"left folder\"")]
    [InlineData("C:\\left\\", "\"C:\\left\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void OwnedConsoleQuotesArgumentsWithoutChangingTheirValue(string argument, string expected)
        => Assert.Equal(expected, OwnedConsoleProcess.QuoteArgument(argument));
    [Theory]
    [InlineData("TSHARPVISION_DRIVER")]
    [InlineData("tsharpvision_proxy_session")]
    [InlineData("APPDATA")]
    [InlineData("home")]
    public void ProjectCannotOverrideSessionAuthenticationOrProfileIsolation(string variable)
    {
        var project = new WorkbenchProject { TargetEnvironment = new() { [variable] = "unsafe" } };
        Assert.Throws<ArgumentException>(project.Validate);
    }

    [Fact]
    public void AcceptedTuiRequestRetainsIsolatedLaunchWhenOriginalProjectIsEdited()
    {
        var project = new WorkbenchProject
        {
            TargetArguments = ["{targetDir}/../workspace/left", "{targetDir}/../workspace/right"],
            TargetEnvironment = new() { ["TSHARPCOMMANDER_USER_STATE_ROOT"] = "{state}/commander-profile" },
            ProxyShutdownKey = "kbEnter"
        };
        var request = new WorkbenchRunRequest(project, [], "test");
        project.TargetArguments[0] = "unrelated";
        project.TargetEnvironment.Clear();
        var launch = request.Options();
        Assert.Equal("{targetDir}/../workspace/left", launch.TargetArguments[0]);
        Assert.Equal("{state}/commander-profile", launch.TargetEnvironment["TSHARPCOMMANDER_USER_STATE_ROOT"]);
        Assert.Equal("kbEnter", launch.ProxyShutdownKey);
        var first = TargetLaunch.Expand(launch.TargetEnvironment["TSHARPCOMMANDER_USER_STATE_ROOT"], "run-a/state", "run-a/fixture", "target");
        var second = TargetLaunch.Expand(launch.TargetEnvironment["TSHARPCOMMANDER_USER_STATE_ROOT"], "run-b/state", "run-b/fixture", "target");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void UnexpectedShutdownConfirmationIsRejectedBeforeLaunch()
    {
        var project = new WorkbenchProject { ProxyShutdownKey = "kbAltX" };
        Assert.Throws<ArgumentException>(project.Validate);
    }
}

