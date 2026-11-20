using VNotch;
using Xunit;

namespace VNotch.Tests;

public sealed class StartupModeTests
{
    [Theory]
    [InlineData("--setup")]
    [InlineData("--uninstall")]
    public void MaintenanceFlagsUseIsolatedStartup(string flag)
        => Assert.True(App.IsSetupOrUninstall([flag], "ATS-Notch.exe"));

    [Fact]
    public void SetupSourceAndInstallerNameUseIsolatedStartup()
    {
        Assert.True(App.IsSetupOrUninstall(["--setup-source", "payload"], "ATS-Notch.exe"));
        Assert.True(App.IsSetupOrUninstall([], "ATS-Notch-Setup.exe"));
    }

    [Fact]
    public void OrdinaryAndRestartLaunchesInitializeSynchronously()
    {
        Assert.False(App.IsSetupOrUninstall([], "ATS-Notch.exe"));
        Assert.False(App.IsSetupOrUninstall(["--restart"], "ATS-Notch.exe"));
        Assert.False(App.IsSetupOrUninstall(["--setup-source", "  "], "ATS-Notch.exe"));
    }
}
