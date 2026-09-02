using EveUtils.Client.Messaging;
using EveUtils.Client.ViewModels;
using Material.Icons;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The character card's server chip (cloud + server name) has to say out loud when the pairing has lapsed, instead of
/// leaving the user to find out on a save that comes back "Not authenticated — pair with the server first." (ET-77).
/// A lapsed pairing is red and only the user can fix it; a dropped/reconnecting link is amber and fixes itself. The
/// two style variants are mutually exclusive so they can never stack on one Border.
/// </summary>
public class ServerLinkChipStateTests
{
    private static ServerLinkViewModel Link(ServerConnectionState state) =>
        new(96000001, "https://eve-together.com", "ET", state, _ => Task.CompletedTask);

    [Fact]
    public void SessionExpired_IsRed_AndNotAlsoAmber()
    {
        var link = Link(ServerConnectionState.SessionExpired);

        Assert.True(link.HasExpired);
        Assert.False(link.HasIssue);
        Assert.Equal(MaterialIconKind.CloudOffOutline, link.ChipIcon);
        // Not "re-pair": the pairing is kept and retried now, so the chip stops instructing and starts reporting
        // (ET-121). Red all the same — everything that server holds reads as empty while it lasts.
        Assert.Contains("refused", link.LinkTooltip);
        Assert.DoesNotContain("re-pair", link.LinkTooltip);
    }

    /// <summary>
    /// A session the server has dropped altogether (ET-123). Red like a refused one, but it must not read like it:
    /// the retrying has stopped, so a chip still saying "retrying" would tell the user the app is busy at the very
    /// moment they are the only one who can fix it. And it is the one state that offers the way back, because the
    /// link's only actions were decouple and view-trust — nothing that repairs the thing it says is broken.
    /// </summary>
    [Fact]
    public void SessionGone_IsRed_AsksTheUserToAct_AndOffersTheWayBack()
    {
        var link = Link(ServerConnectionState.SessionGone);

        Assert.True(link.HasExpired);
        Assert.False(link.HasIssue);
        Assert.Equal(MaterialIconKind.CloudRemoveOutline, link.ChipIcon);
        Assert.True(link.CanRecouple);
        Assert.Contains("couple again", link.LinkTooltip);
        Assert.DoesNotContain("retrying", link.LinkTooltip);
    }

    /// <summary>Coupling again is offered only where it is the actual remedy. Notably not on a refused certificate:
    /// there the user has a fingerprint to check against the server first, and a one-click re-pair would walk them
    /// straight past it (ET-95).</summary>
    [Theory]
    [InlineData(ServerConnectionState.Connected)]
    [InlineData(ServerConnectionState.Reconnecting)]
    [InlineData(ServerConnectionState.Disconnected)]
    [InlineData(ServerConnectionState.SessionExpired)]
    [InlineData(ServerConnectionState.CertificateRejected)]
    public void EveryOtherState_DoesNotOfferToCoupleAgain(ServerConnectionState state) =>
        Assert.False(Link(state).CanRecouple);

    [Fact]
    public void CertificateRejected_IsRed_AndSaysWhichOfTheTwoProblemsItIs()
    {
        // Also red, also the user's to fix, but a different question — and unlike a refused session this one really
        // does end the reconnect loop, because the next handshake is refused identically (ET-95).
        var link = Link(ServerConnectionState.CertificateRejected);

        Assert.True(link.HasExpired);
        Assert.False(link.HasIssue);
        Assert.Equal(MaterialIconKind.ShieldAlertOutline, link.ChipIcon);
        Assert.Contains("certificate", link.LinkTooltip);
    }

    [Theory]
    [InlineData(ServerConnectionState.Reconnecting)]
    [InlineData(ServerConnectionState.Disconnected)]
    public void ATransientProblem_StaysAmber(ServerConnectionState state)
    {
        var link = Link(state);

        Assert.True(link.HasIssue);
        Assert.False(link.HasExpired);
        Assert.Equal(MaterialIconKind.AlertOutline, link.ChipIcon);
    }

    [Theory]
    [InlineData(ServerConnectionState.Connected)]
    [InlineData(ServerConnectionState.Connecting)]
    public void AHealthyLink_KeepsThePlainCloud(ServerConnectionState state)
    {
        var link = Link(state);

        Assert.False(link.HasIssue);
        Assert.False(link.HasExpired);
        Assert.Equal(MaterialIconKind.CloudOutline, link.ChipIcon);
    }

    [Fact]
    public void GoingExpiredWhileLive_RepaintsTheChip()
    {
        var link = Link(ServerConnectionState.Connected);
        var raised = new List<string?>();
        link.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        link.State = ServerConnectionState.SessionExpired; // what the 30s heartbeat now reports

        Assert.Contains(nameof(ServerLinkViewModel.HasExpired), raised);
        Assert.Contains(nameof(ServerLinkViewModel.ChipIcon), raised);
    }
}
