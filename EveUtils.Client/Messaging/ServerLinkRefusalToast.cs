using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Messaging;

/// <summary>
/// The wording for the toast raised the moment a server starts refusing this client's stored sign-in (ET-121).
///
/// <para>A toast AND a banner, because they answer different questions. The banner (<see cref="ServerPairingAlert"/>)
/// is the state: it stays up for as long as the pairing is refused, which is exactly as long as that server's lists
/// read as empty. The toast is the transition — it marks the moment, and it does it on the window the user is
/// actually looking at. The banner only exists on the main window, so a pilot working in a floated fleet or fit
/// window would otherwise get no signal at all until they went back.</para>
///
/// <para>Raised once per server per run and grouped under one <see cref="ReplacementKey"/>, so five characters
/// coupled to one server produce one card rather than five, and a server that keeps failing its slow retry does not
/// re-announce itself every five minutes.</para>
/// </summary>
public static class ServerLinkRefusalToast
{
    /// <summary>Groups every refused-server card into one, replacing rather than stacking.</summary>
    public const string ReplacementKey = "server-link-refused";

    /// <summary>Its own group for the sessions that are gone rather than refused (ET-123), so the card that asks
    /// the user to act cannot be replaced by the one that asks them to wait, or the other way round.</summary>
    public const string SessionGoneReplacementKey = "server-link-session-gone";

    /// <summary>The card for the servers currently refusing their session. Names are de-duplicated and ordered so
    /// the text is stable, matching the banner's.</summary>
    public static (string Title, string Message) For(IEnumerable<string> serverNames)
    {
        var names = Order(serverNames);

        var title = names.Count == 1
            ? $"{names[0]} refused this client's sign-in"
            : "Some servers refused this client's sign-in";

        return (title,
            $"Your pairing with {Join(names)} is kept and will keep retrying. Until it takes, that server's fleets, "
            + "compositions and shared fits read as empty.");
    }

    /// <summary>The card for a session the server no longer has at all. Deliberately not a variation on the wording
    /// above: that one reassures ("will keep retrying"), and here nothing is retrying any more — the sentence has to
    /// end with the thing the user has to do.</summary>
    public static (string Title, string Message) ForSessionGone(IEnumerable<string> serverNames)
    {
        var names = Order(serverNames);

        var title = names.Count == 1
            ? $"{names[0]} no longer has this client's session"
            : "Some servers no longer have this client's session";

        return (title,
            $"Reconnecting to {Join(names)} has stopped — the session was cleaned up or revoked, so retrying cannot "
            + "bring it back. Open the character and couple it to the server again.");
    }

    private static List<string> Order(IEnumerable<string> serverNames) => serverNames
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };
}
