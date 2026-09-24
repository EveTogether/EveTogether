using EveUtils.Client.Updates;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// No silent channel drift (ET-339, mirrors Cockpit's AC-387): a channel nobody chose follows the running build's
/// own stream, and a channel somebody chose wins over it. The failure this keeps out: a nightly build with no
/// configuration file landing on stable and being offered the latest stable as its first "update" — a downgrade,
/// presented as an upgrade.
/// </summary>
public class ChannelChoiceTests
{
    [Theory]
    [InlineData("0.2.0", UpdateChannel.Stable)]
    [InlineData("0.2.0-alpha", UpdateChannel.Stable)]
    [InlineData("0.2.0-beta", UpdateChannel.Stable)]
    [InlineData("0.2.0-nightly.42", UpdateChannel.Nightly)]
    [InlineData("0.2.0-NIGHTLY.42", UpdateChannel.Nightly)]
    [InlineData("0.2.0-nightly.42+abc1234", UpdateChannel.Nightly)]
    public void BuildChannel_FromVersion_ReadsTheStreamFromTheVersionsOwnTag(string version, UpdateChannel expected) =>
        Assert.Equal(expected, BuildChannel.FromVersion(version));

    [Theory]
    [InlineData(null, UpdateChannel.Stable, "0.2.0")]                     // nobody chose, stable build -> stable
    [InlineData(null, UpdateChannel.Nightly, "0.2.0-nightly.42")]         // nobody chose, nightly build -> nightly
    [InlineData("Stable", UpdateChannel.Stable, "0.2.0-nightly.42")]      // chosen stable beats a nightly build
    [InlineData("Nightly", UpdateChannel.Nightly, "0.2.0")]               // chosen nightly beats a stable build
    [InlineData("nightly", UpdateChannel.Nightly, "0.2.0")]               // stored value is case-insensitive
    [InlineData("weekly", UpdateChannel.Stable, "0.2.0")]                 // an unknown stored value reads as unchosen
    public void ChannelChoice_Resolve_PrefersAStoredChoiceOverTheBuildsOwnStream(
        string? stored, UpdateChannel expected, string runningVersion) =>
        Assert.Equal(expected, ChannelChoice.Resolve(stored, runningVersion));

    [Theory]
    [InlineData("Stable", UpdateChannel.Stable)]
    [InlineData("nightly", UpdateChannel.Nightly)]
    public void ChannelChoice_Stored_ParsesAKnownValueRegardlessOfCasing(string stored, UpdateChannel expected) =>
        Assert.Equal(expected, ChannelChoice.Stored(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("weekly")]
    public void ChannelChoice_Stored_ReadsAnythingElseAsNobodyHavingChosen(string? stored) =>
        Assert.Null(ChannelChoice.Stored(stored));
}
