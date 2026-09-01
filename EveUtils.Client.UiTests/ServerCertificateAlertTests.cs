using System;
using EveUtils.Client.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stopping the reconnect loop on a refused certificate only helps if the user is told (ET-95): the alternative to an
/// invisible retry once a second is not an invisible silence. The banner has to name the server and carry both
/// fingerprints, because deciding whether the certificate legitimately changed is a comparison only the user can make.
/// </summary>
public class ServerCertificateAlertTests
{
    private const string Pinned = "AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12";
    private const string Presented = "9911FFEE9911FFEE9911FFEE9911FFEE9911FFEE9911FFEE9911FFEE9911FFEE";

    [Fact]
    public void NoRefusedCertificate_ShowsNoBanner()
    {
        var (show, message) = ServerCertificateAlert.For([]);

        Assert.False(show);
        Assert.Empty(message);
    }

    [Fact]
    public void ARefusedCertificate_NamesTheServerAndBothFingerprints()
    {
        var (show, message) = ServerCertificateAlert.For(
            [new ServerCertificateAlert.RejectedCertificate("ET", Pinned, Presented)]);

        Assert.True(show);
        Assert.Contains("ET", message);
        Assert.Contains(Pinned, message);
        Assert.Contains(Presented, message);
    }

    [Fact]
    public void SeveralCharactersOnOneServer_AreOneEntry()
    {
        // One server is one server however many characters were coupled to it; each of their connections reports the
        // refusal separately.
        var (_, message) = ServerCertificateAlert.For([
            new ServerCertificateAlert.RejectedCertificate("ET", Pinned, Presented),
            new ServerCertificateAlert.RejectedCertificate("ET", Pinned, Presented)
        ]);

        Assert.Equal(1, CountOccurrences(message, Presented));
    }

    [Fact]
    public void SeveralServers_AreListedInAStableOrder()
    {
        var (_, message) = ServerCertificateAlert.For([
            new ServerCertificateAlert.RejectedCertificate("Wormhole Co-op", Pinned, Presented),
            new ServerCertificateAlert.RejectedCertificate("ET", Pinned, Presented)
        ]);

        Assert.True(message.IndexOf("ET", StringComparison.Ordinal)
                    < message.IndexOf("Wormhole Co-op", StringComparison.Ordinal));
    }

    [Fact]
    public void AHandshakeThatNeverReachedACertificate_SaysSoRatherThanShowingNothing()
    {
        var (show, message) = ServerCertificateAlert.For(
            [new ServerCertificateAlert.RejectedCertificate("ET", Pinned, null)]);

        Assert.True(show);
        Assert.Contains("unknown fingerprint", message);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
