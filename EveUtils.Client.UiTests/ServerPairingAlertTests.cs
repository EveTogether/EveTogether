using EveUtils.Client.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The persistent top-of-window banner for a lapsed server pairing (ET-77). It has to stay up rather than fade,
/// because the damage it warns about is silent and ongoing: lists from that server keep reading as empty for as long
/// as the pairing is broken. It must equally NOT cry wolf — a link that is merely reconnecting fixes itself.
///
/// And it has to say WHO (ET-123). Six characters can share one server with only one of them in trouble, so a banner
/// that names the server alone leaves the reader guessing which of their pilots has to be dealt with.
/// </summary>
public class ServerPairingAlertTests
{
    private static ServerPairingAlert.Link Link(string server, string character, ServerConnectionState state) =>
        new(server, character, state);

    [Fact]
    public void WithEverythingConnected_ThereIsNoBanner()
    {
        var (show, message) = ServerPairingAlert.For(
            [Link("ET", "Abnoba", ServerConnectionState.Connected), Link("ET", "Jithran", ServerConnectionState.Connected)]);

        Assert.False(show);
        Assert.Equal("", message);
    }

    [Theory]
    [InlineData(ServerConnectionState.Reconnecting)]
    [InlineData(ServerConnectionState.Disconnected)]
    [InlineData(ServerConnectionState.Connecting)]
    public void ALinkThatFixesItself_RaisesNothing(ServerConnectionState state)
    {
        var (show, _) = ServerPairingAlert.For([Link("ET", "Abnoba", state)]);

        Assert.False(show);
    }

    /// <summary>The production case: one of six characters, and the banner has to pick that one out by name.</summary>
    [Fact]
    public void OneCharacterOfSeveralOnOneServer_IsNamed_AndTheHealthyOnesAreNot()
    {
        var (show, message) = ServerPairingAlert.For(
        [
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionGone),
            Link("ET", "Jithran", ServerConnectionState.Connected),
            Link("ET", "ColdSprockets", ServerConnectionState.Connected),
        ]);

        Assert.True(show);
        Assert.Contains("Abnoba Auscent", message);
        Assert.Contains("ET", message);
        Assert.DoesNotContain("Jithran", message);
        Assert.DoesNotContain("ColdSprockets", message);
        // "this client" read as though the whole application had been refused — it is one character's session.
        Assert.DoesNotContain("client", message);
    }

    [Fact]
    public void ALapsedPairing_NamesTheCharacter_AndSaysWhatItCosts()
    {
        var (show, message) = ServerPairingAlert.For([Link("ET", "Abnoba Auscent", ServerConnectionState.SessionExpired)]);

        Assert.True(show);
        Assert.Contains("Abnoba Auscent", message);
        Assert.Contains("empty", message); // the whole point: silence is what it is warning about
        Assert.DoesNotContain("client", message);
    }

    /// <summary>
    /// A session the server no longer has (ET-123) reads differently from one it is refusing. The refused wording
    /// reassures — kept, retried, may clear on its own — and every word of that is wrong here: nothing is retrying
    /// and it will not clear until the user couples again.
    /// </summary>
    [Fact]
    public void ASessionTheServerNoLongerHas_AsksTheUserToAct_InsteadOfPromisingARetry()
    {
        var (show, message) = ServerPairingAlert.For([Link("ET", "Abnoba Auscent", ServerConnectionState.SessionGone)]);

        Assert.True(show);
        Assert.Contains("couple it to ET again", message);
        Assert.Contains("empty", message);
        Assert.DoesNotContain("retried every few minutes", message);
    }

    [Fact]
    public void TheSameCharacterSeenTwice_IsNamedOnce()
    {
        var (_, message) = ServerPairingAlert.For(
        [
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionExpired),
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionExpired),
        ]);

        Assert.Equal(1, message.Split("Abnoba Auscent").Length - 1);
    }

    [Fact]
    public void SeveralCharactersOnOneServer_AreAllNamed_UnderOneServerClause()
    {
        var (show, message) = ServerPairingAlert.For(
        [
            Link("ET", "Jithran", ServerConnectionState.SessionGone),
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionGone),
        ]);

        Assert.True(show);
        Assert.Contains("Abnoba Auscent and Jithran", message); // ordered, so the text is stable between rebuilds
        Assert.Equal(1, message.Split("ET no longer has").Length - 1);
    }

    [Fact]
    public void SeveralServers_EachGetTheirOwnClause()
    {
        var (show, message) = ServerPairingAlert.For(
        [
            Link("Wormhole Co-op", "Jithran", ServerConnectionState.SessionGone),
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionGone),
            Link("Healthy One", "Noahmarr", ServerConnectionState.Connected),
        ]);

        Assert.True(show);
        Assert.Contains("ET no longer has a session for Abnoba Auscent", message);
        Assert.Contains("Wormhole Co-op no longer has a session for Jithran", message);
        Assert.DoesNotContain("Healthy One", message);
        Assert.DoesNotContain("Noahmarr", message);
    }

    /// <summary>Both kinds at once — one character's session swept while another is merely being refused. Each gets
    /// its own sentence rather than one blended into both, because they ask for opposite things.</summary>
    [Fact]
    public void ARefusedCharacterAndAGoneOne_AreBothNamed_WithTheirOwnInstructions()
    {
        var (show, message) = ServerPairingAlert.For(
        [
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionGone),
            Link("ET", "Jithran", ServerConnectionState.SessionExpired),
        ]);

        Assert.True(show);
        Assert.Contains("no longer has a session for Abnoba Auscent", message);
        Assert.Contains("refusing the stored sign-in for Jithran", message);
        Assert.Contains("retried every few minutes", message);
    }

    /// <summary>One character in both states at once is described only as gone: coupling again settles both, and two
    /// sentences about the same pilot would read as a contradiction.</summary>
    [Fact]
    public void OneCharacterInBothStates_IsNamedOnlyAsGone()
    {
        var (_, message) = ServerPairingAlert.For(
        [
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionGone),
            Link("ET", "Abnoba Auscent", ServerConnectionState.SessionExpired),
        ]);

        Assert.Contains("couple it to ET again", message);
        Assert.DoesNotContain("retried every few minutes", message);
    }

    [Fact]
    public void WithNoCoupledServersAtAll_ThereIsNoBanner()
    {
        var (show, _) = ServerPairingAlert.For([]);

        Assert.False(show);
    }
}
