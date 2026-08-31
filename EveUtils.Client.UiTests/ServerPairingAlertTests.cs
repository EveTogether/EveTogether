using EveUtils.Client.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The persistent top-of-window banner for a lapsed server pairing (ET-77). It has to stay up rather than fade,
/// because the damage it warns about is silent and ongoing: lists from that server keep reading as empty for as long
/// as the pairing is broken. It must equally NOT cry wolf — a link that is merely reconnecting fixes itself.
/// </summary>
public class ServerPairingAlertTests
{
    [Fact]
    public void WithEverythingConnected_ThereIsNoBanner()
    {
        var (show, message) = ServerPairingAlert.For(
            [("ET", ServerConnectionState.Connected), ("ET", ServerConnectionState.Connected)]);

        Assert.False(show);
        Assert.Equal("", message);
    }

    [Theory]
    [InlineData(ServerConnectionState.Reconnecting)]
    [InlineData(ServerConnectionState.Disconnected)]
    [InlineData(ServerConnectionState.Connecting)]
    public void ALinkThatFixesItself_RaisesNothing(ServerConnectionState state)
    {
        var (show, _) = ServerPairingAlert.For([("ET", state)]);

        Assert.False(show);
    }

    [Fact]
    public void ALapsedPairing_NamesTheServer_AndSaysWhatItCosts()
    {
        var (show, message) = ServerPairingAlert.For([("ET", ServerConnectionState.SessionExpired)]);

        Assert.True(show);
        Assert.Contains("ET", message);
        Assert.Contains("empty", message); // the whole point: silence is what it is warning about
    }

    [Fact]
    public void TheSameServerOnSeveralCharacters_IsNamedOnce()
    {
        var (_, message) = ServerPairingAlert.For(
        [
            ("ET", ServerConnectionState.SessionExpired),
            ("ET", ServerConnectionState.SessionExpired),
            ("ET", ServerConnectionState.SessionExpired)
        ]);

        Assert.Equal(1, message.Split("ET").Length - 1);
    }

    [Fact]
    public void SeveralLapsedServers_AreAllNamed()
    {
        var (show, message) = ServerPairingAlert.For(
        [
            ("Wormhole Co-op", ServerConnectionState.SessionExpired),
            ("ET", ServerConnectionState.SessionExpired),
            ("Healthy One", ServerConnectionState.Connected)
        ]);

        Assert.True(show);
        Assert.Contains("ET and Wormhole Co-op", message); // ordered, so the text is stable between rebuilds
        Assert.DoesNotContain("Healthy One", message);
    }

    [Fact]
    public void WithNoCoupledServersAtAll_ThereIsNoBanner()
    {
        var (show, _) = ServerPairingAlert.For([]);

        Assert.False(show);
    }
}
