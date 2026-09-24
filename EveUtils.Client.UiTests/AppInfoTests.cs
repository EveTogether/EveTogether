using EveUtils.Shared.App;
using Xunit;

namespace EveUtils.Client.UiTests;

public class AppInfoTests
{
    [Theory]
    [InlineData("0.0.0-nightly.5+nightly-20260924.a1b2c3d.5", "nightly-20260924.a1b2c3d.5")]
    [InlineData("0.2.0-beta+f1dd19c", "v0.2.0-beta")]
    public void DisplayVersionFromInformational_NightlyShowsBuildIdentity_StableShowsReleaseVersion(
        string informational, string expected) =>
        Assert.Equal(expected, AppInfo.DisplayVersionFromInformational(informational));
}
