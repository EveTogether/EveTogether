using EveUtils.Shared.App;
using Xunit;

namespace EveUtils.Client.UiTests;

public class AppInfoTests
{
    [Theory]
    [InlineData("0.2.0-nightly.5+20260924.a1b2c3d", "0.2.0-nightly.5")]
    [InlineData("0.2.0-beta+f1dd19c", "v0.2.0-beta")]
    public void DisplayVersionFromInformational_NightlyShowsPackageVersion_StableShowsReleaseVersion(
        string informational, string expected) =>
        Assert.Equal(expected, AppInfo.DisplayVersionFromInformational(informational));
}
