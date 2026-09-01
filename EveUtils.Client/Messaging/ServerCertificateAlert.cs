using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Messaging;

/// <summary>
/// Resolves the top-of-window banner for a coupled server whose TLS certificate no longer matches the fingerprint
/// pinned at pairing (ET-95). The reconnect loop stops on that failure rather than retrying it, because every further
/// handshake is refused identically — and a loop that stops without saying so is no better for the user than the
/// invisible one-per-second retry it replaces.
///
/// Separate from <see cref="ServerPairingAlert"/> on purpose. A lapsed pairing has one answer, "couple the character
/// again"; a changed certificate has a question in front of it. A reinstalled server or a reissued certificate is
/// ordinary, and someone else answering for the address looks exactly the same from here — only the fingerprint tells
/// the two apart, so the banner has to carry it.
/// </summary>
public static class ServerCertificateAlert
{
    /// <summary>One server whose certificate was refused, with the two fingerprints the user has to compare. Either
    /// may be absent: no pin at all is not a state pairing can leave behind, and the presented value is unknown if the
    /// handshake never got as far as a certificate.</summary>
    public readonly record struct RejectedCertificate(string ServerName, string? Pinned, string? Presented);

    /// <summary>Builds the banner from the servers currently in that state. De-duplicated by name and ordered, so the
    /// text is stable between rebuilds however many characters are coupled to the same server.</summary>
    public static (bool Show, string Message) For(IEnumerable<RejectedCertificate> rejected)
    {
        var servers = rejected
            .DistinctBy(r => r.ServerName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (servers.Count == 0)
            return (false, "");

        var fingerprints = string.Join(" ", servers.Select(s =>
            $"{s.ServerName} now presents {Describe(s.Presented)} instead of the pinned {Describe(s.Pinned)}."));

        return (true,
            "Stopped connecting: a coupled server's TLS certificate no longer matches the fingerprint pinned when you "
            + $"paired with it. {fingerprints} Check the new fingerprint against the server itself before you re-pair — "
            + "a reinstall or a reissued certificate explains it, and anything else means that address is being "
            + "answered by something that is not your server.");
    }

    private static string Describe(string? fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint) ? "an unknown fingerprint" : fingerprint;
}
