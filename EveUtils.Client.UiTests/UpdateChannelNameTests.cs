using System.Runtime.InteropServices;
using EveUtils.Client.Updates;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// One GitHub release carries all four published RIDs, so the channel a build reads has to name both the platform
/// and the architecture — otherwise an Apple Silicon install is offered the x64 package, on someone else's machine.
/// Asked through the platform/architecture seam so all four can be checked from one machine.
/// </summary>
public class UpdateChannelNameTests
{
    [Theory]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("osx", Architecture.X64, "osx-x64")]
    public void For_EachPublishedRid_NamesThePlatformAndTheArchitecture(string platform, Architecture architecture, string expected) =>
        Assert.Equal(expected, UpdateChannelName.For(platform, architecture));

    [Fact]
    public void Current_IsOneOfThePublishedChannels() =>
        Assert.Contains(UpdateChannelName.Current, new[] { "win-x64", "linux-x64", "osx-arm64", "osx-x64" });

    /// <summary>
    /// An architecture nothing was published for is a refusal, not a channel that silently reads someone else's feed.
    /// </summary>
    [Fact]
    public void For_AnArchitectureThatIsNotPublished_Refuses() =>
        Assert.Throws<PlatformNotSupportedException>(() => UpdateChannelName.For("win", Architecture.X86));
}
