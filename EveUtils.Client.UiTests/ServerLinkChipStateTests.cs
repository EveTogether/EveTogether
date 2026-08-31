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
        Assert.Contains("re-pair", link.LinkTooltip);
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
