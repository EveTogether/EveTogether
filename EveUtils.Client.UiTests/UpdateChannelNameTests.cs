using System.Runtime.InteropServices;
using EveUtils.Client.Updates;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// One GitHub release carries all four published RIDs, each on both a stable and a nightly stream (ET-339), so the
/// channel a build reads has to name the platform, the architecture and the stream — otherwise an Apple Silicon
/// install is offered the x64 package, or a stable install is offered a nightly. Asked through the
/// platform/architecture/stream seam so all eight can be checked from one machine.
/// </summary>
public class UpdateChannelNameTests
{
    [Theory]
    [InlineData("win", Architecture.X64, UpdateChannel.Stable, "win-x64-stable")]
    [InlineData("win", Architecture.X64, UpdateChannel.Nightly, "win-x64-nightly")]
    [InlineData("linux", Architecture.X64, UpdateChannel.Stable, "linux-x64-stable")]
    [InlineData("linux", Architecture.X64, UpdateChannel.Nightly, "linux-x64-nightly")]
    [InlineData("osx", Architecture.Arm64, UpdateChannel.Stable, "osx-arm64-stable")]
    [InlineData("osx", Architecture.Arm64, UpdateChannel.Nightly, "osx-arm64-nightly")]
    [InlineData("osx", Architecture.X64, UpdateChannel.Stable, "osx-x64-stable")]
    [InlineData("osx", Architecture.X64, UpdateChannel.Nightly, "osx-x64-nightly")]
    public void For_EachPublishedRidAndStream_NamesThePlatformArchitectureAndStream(
        string platform, Architecture architecture, UpdateChannel stream, string expected) =>
        Assert.Equal(expected, UpdateChannelName.For(platform, architecture, stream));

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Nightly)]
    public void For_ThisMachinesStream_IsOneOfThePublishedChannels(UpdateChannel stream) =>
        Assert.Contains(UpdateChannelName.For(stream), new[]
        {
            "win-x64-stable", "win-x64-nightly",
            "linux-x64-stable", "linux-x64-nightly",
            "osx-arm64-stable", "osx-arm64-nightly",
            "osx-x64-stable", "osx-x64-nightly",
        });

    /// <summary>
    /// An architecture nothing was published for is a refusal, not a channel that silently reads someone else's feed.
    /// </summary>
    [Fact]
    public void For_AnArchitectureThatIsNotPublished_Refuses() =>
        Assert.Throws<PlatformNotSupportedException>(() => UpdateChannelName.For("win", Architecture.X86, UpdateChannel.Stable));
}
