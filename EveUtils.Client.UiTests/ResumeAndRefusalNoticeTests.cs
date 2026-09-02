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

    /// <summary>The card has to say which PILOT, not just which server: several characters share one server, and a
    /// card naming only the server leaves the reader to work out who it is about (ET-123).</summary>
    [Fact]
    public void TheRefusalToast_NamesTheCharacterAndTheServer()
    {
        var (title, message) = ServerLinkRefusalToast.For([("ET", "Abnoba Auscent")]);

        Assert.Contains("ET", title);
        Assert.Contains("Abnoba Auscent", title);
        Assert.Contains("Abnoba Auscent", message);
        Assert.Contains("kept", message); // the reassurance is the point — nothing was thrown away
        Assert.Contains("empty", message);
    }

    [Fact]
    public void OneCharacterOnOneServer_IsOneCard_NamingTheServerOnce()
    {
        var (_, message) = ServerLinkRefusalToast.For(
            [("ET", "Abnoba Auscent"), ("ET", "Abnoba Auscent"), ("ET", "Abnoba Auscent")]);

        Assert.Equal(1, message.Split("Abnoba Auscent").Length - 1);
    }

    [Fact]
    public void SeveralAffectedCharacters_AreNamedTogether_InAStableOrder()
    {
        var (title, message) = ServerLinkRefusalToast.For([("Wormhole Co-op", "Jithran"), ("ET", "Abnoba Auscent")]);

        Assert.Contains("Abnoba Auscent and Jithran", message);
        // The title stays short once there is more than one; the names live in the body.
        Assert.DoesNotContain("Abnoba Auscent and Jithran", title);
    }

    /// <summary>The card for a session that is gone asks for an action instead of promising a retry (ET-123).</summary>
    [Fact]
    public void TheSessionGoneToast_NamesTheCharacter_AndAsksForAnAction()
    {
        var (title, message) = ServerLinkRefusalToast.ForSessionGone([("ET", "Abnoba Auscent")]);

        Assert.Contains("Abnoba Auscent", title);
        Assert.Contains("couple it to the server again", message);
        Assert.DoesNotContain("keep retrying", message);
    }

    /// <summary>
    /// The toast is the transition and the banner is the state, so they have to agree about which servers those are —
    /// a card naming a server the banner does not would read as a contradiction.
    /// </summary>
    [Fact]
    public void TheToastAndTheBanner_NameTheSameServers()
    {
        ServerPairingAlert.Link[] links =
        [
            new("ET", "Abnoba Auscent", ServerConnectionState.SessionExpired),
            new("Healthy One", "Noahmarr", ServerConnectionState.Connected)
        ];

        var (bannerShown, bannerMessage) = ServerPairingAlert.For(links);
        var (_, toastMessage) = ServerLinkRefusalToast.For([("ET", "Abnoba Auscent")]);

        Assert.True(bannerShown);
        // Both name the same pilot on the same server, so the card and the banner cannot disagree about who.
        Assert.Contains("Abnoba Auscent", bannerMessage);
        Assert.Contains("Abnoba Auscent", toastMessage);
        Assert.Contains("ET", bannerMessage);
        Assert.DoesNotContain("Healthy One", bannerMessage);
        Assert.DoesNotContain("Noahmarr", bannerMessage);
        Assert.DoesNotContain("Noahmarr", toastMessage);
    }

    /// <summary>
    /// The banner used to say the pairing was "no longer valid" and to send the pilot off to re-pair. It is not gone
    /// any more, so it must not say that — a pilot who re-pairs on sight loses the retry that would have fixed it.
    /// </summary>
    [Fact]
    public void TheBanner_SaysThePairingIsKept_RatherThanThatItIsGone()
    {
        var (_, message) = ServerPairingAlert.For(
            [new ServerPairingAlert.Link("ET", "Abnoba Auscent", ServerConnectionState.SessionExpired)]);

        Assert.Contains("kept", message);
        Assert.DoesNotContain("no longer valid", message);
    }
}
