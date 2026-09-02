using System;
using EveUtils.Client.Messaging;
using EveUtils.Client.Platform;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The two things ET-121 added around a lost server link: noticing that the machine has been asleep, and telling the
/// pilot the moment a server starts refusing their sign-in.
/// </summary>
public class ResumeAndRefusalNoticeTests
{
    [Theory]
    [InlineData(5, false)]    // the ordinary tick
    [InlineData(12, false)]   // a loaded desktop letting a timer slip — not a resume
    [InlineData(29, false)]
    [InlineData(30, true)]    // the boundary counts
    [InlineData(8 * 60 * 60, true)]
    public void AGapBetweenTicks_ReadsAsAResumeOnlyOnceItIsBigEnoughToBeOne(int seconds, bool expected) =>
        Assert.Equal(expected, SystemResumeWatcher.IsResume(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheRefusalToast_NamesTheServer()
    {
        var (title, message) = ServerLinkRefusalToast.For(["ET"]);

        Assert.Contains("ET", title);
        Assert.Contains("kept", message); // the reassurance is the point — nothing was thrown away
        Assert.Contains("empty", message);
    }

    [Fact]
    public void OneServerOnSeveralCharacters_IsOneCard_NamingItOnce()
    {
        var (_, message) = ServerLinkRefusalToast.For(["ET", "ET", "ET"]);

        Assert.Equal(1, message.Split("ET").Length - 1);
    }

    [Fact]
    public void SeveralServers_AreNamedTogether_InAStableOrder()
    {
        var (title, message) = ServerLinkRefusalToast.For(["Wormhole Co-op", "ET"]);

        Assert.Contains("ET and Wormhole Co-op", message);
        Assert.DoesNotContain("ET and Wormhole Co-op", title); // the title stays short; the names live in the body
    }

    /// <summary>
    /// The toast is the transition and the banner is the state, so they have to agree about which servers those are —
    /// a card naming a server the banner does not would read as a contradiction.
    /// </summary>
    [Fact]
    public void TheToastAndTheBanner_NameTheSameServers()
    {
        var links = new[]
        {
            ("ET", ServerConnectionState.SessionExpired),
            ("Healthy One", ServerConnectionState.Connected)
        };

        var (bannerShown, bannerMessage) = ServerPairingAlert.For(links);
        var (_, toastMessage) = ServerLinkRefusalToast.For(["ET"]);

        Assert.True(bannerShown);
        Assert.Contains("ET", bannerMessage);
        Assert.Contains("ET", toastMessage);
        Assert.DoesNotContain("Healthy One", bannerMessage);
        Assert.DoesNotContain("Healthy One", toastMessage);
    }

    /// <summary>
    /// The banner used to say the pairing was "no longer valid" and to send the pilot off to re-pair. It is not gone
    /// any more, so it must not say that — a pilot who re-pairs on sight loses the retry that would have fixed it.
    /// </summary>
    [Fact]
    public void TheBanner_SaysThePairingIsKept_RatherThanThatItIsGone()
    {
        var (_, message) = ServerPairingAlert.For([("ET", ServerConnectionState.SessionExpired)]);

        Assert.Contains("kept", message);
        Assert.DoesNotContain("no longer valid", message);
    }
}
