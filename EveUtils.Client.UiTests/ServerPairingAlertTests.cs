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

    /// <summary>
    /// A session the server no longer has (ET-123) reads differently from one it is refusing. The refused wording
    /// reassures — kept, retried, may clear on its own — and every word of that is wrong here: nothing is retrying
    /// and it will not clear until the user couples again. Telling someone to sit tight while their client has
    /// given up is worse than saying nothing at all.
    /// </summary>
    [Fact]
    public void ASessionTheServerNoLongerHas_AsksTheUserToAct_InsteadOfPromisingARetry()
    {
        var (show, message) = ServerPairingAlert.For([("ET", ServerConnectionState.SessionGone)]);

        Assert.True(show);
        Assert.Contains("ET", message);
        Assert.Contains("couple the character again", message);
        Assert.Contains("empty", message); // the silent cost is the same, and still worth naming
        Assert.DoesNotContain("retried every few minutes", message);
    }

    /// <summary>Both kinds at once — one character's session swept while another is merely being refused. Each gets
    /// its own sentence rather than one blended into both, because they ask for opposite things.</summary>
    [Fact]
    public void ARefusedServerAndAGoneOne_AreBothNamed_WithTheirOwnInstructions()
    {
        var (show, message) = ServerPairingAlert.For(
        [
            ("ET", ServerConnectionState.SessionGone),
            ("Wormhole Co-op", ServerConnectionState.SessionExpired)
        ]);

        Assert.True(show);
        Assert.Contains("couple the character again", message);
        Assert.Contains("retried every few minutes", message);
        Assert.Contains("Wormhole Co-op", message);
    }

    /// <summary>One server in both states — one character gone, another still being retried — is named by the
    /// harder of the two only. Coupling again settles both, and two sentences about one server would read as a
    /// contradiction.</summary>
    [Fact]
    public void OneServerInBothStates_IsNamedOnlyAsGone()
    {
        var (_, message) = ServerPairingAlert.For(
        [
            ("ET", ServerConnectionState.SessionGone),
            ("ET", ServerConnectionState.SessionExpired)
        ]);

        Assert.Contains("couple the character again", message);
        Assert.DoesNotContain("retried every few minutes", message);
        Assert.Equal(1, message.Split("ET").Length - 1);
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
