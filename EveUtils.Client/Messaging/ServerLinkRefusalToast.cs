using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Messaging;

/// <summary>
/// The wording for the toast raised the moment a server stops accepting a character's stored sign-in (ET-121).
///
/// <para>A toast AND a banner, because they answer different questions. The banner (<see cref="ServerPairingAlert"/>)
/// is the state: it stays up for as long as the pairing is refused, which is exactly as long as that server's lists
/// read as empty. The toast is the transition — it marks the moment, and it does it on the window the user is
/// actually looking at. The banner only exists on the main window, so a pilot working in a floated fleet or fit
/// window would otherwise get no signal at all until they went back.</para>
///
/// <para>Raised once per server per run and grouped under one <see cref="ReplacementKey"/>, so a server that keeps
/// failing its slow retry does not re-announce itself every five minutes.</para>
///
/// <para>Like the banner, it names the CHARACTER (ET-123). Several characters share one server, so a card that names
/// only the server leaves the reader to work out which of their pilots it is about.</para>
/// </summary>
public static class ServerLinkRefusalToast
{
    /// <summary>Groups every refused-server card into one, replacing rather than stacking.</summary>
    public const string ReplacementKey = "server-link-refused";

    /// <summary>Its own group for the sessions that are gone rather than refused (ET-123), so the card that asks
    /// the user to act cannot be replaced by the one that asks them to wait, or the other way round.</summary>
    public const string SessionGoneReplacementKey = "server-link-session-gone";

    /// <summary>The card for a character whose stored sign-in a server is refusing but still retrying.</summary>
    public static (string Title, string Message) For(IEnumerable<(string Server, string Character)> affected)
    {
        var (title, who) = Describe(affected, "is refusing the sign-in for", "Some sign-ins were refused");
        return (title,
            $"The pairing for {who} is kept and will keep retrying. Until it takes, that server's fleets, "
            + "compositions and shared fits read as empty.");
    }

    /// <summary>The card for a session the server no longer has at all. Deliberately not a variation on the wording
    /// above: that one reassures ("will keep retrying"), and here nothing is retrying any more — the sentence has to
    /// end with the thing the user has to do.</summary>
    public static (string Title, string Message) ForSessionGone(IEnumerable<(string Server, string Character)> affected)
    {
        var (title, who) = Describe(affected, "no longer has a session for", "Some sessions are gone");
        return (title,
            $"Reconnecting for {who} has stopped — the session was cleaned up or revoked, so retrying cannot bring "
            + "it back. Open the character and couple it to the server again.");
    }

    /// <summary>
    /// The card's title and the phrase the body refers to it by. One affected coupling gets a title that says the
    /// whole thing — which is the case that matters, because it is the one a user can act on without opening
    /// anything. Several fall back to a summary title, with the names carried in the body instead.
    /// </summary>
    private static (string Title, string Who) Describe(
        IEnumerable<(string Server, string Character)> affected, string verb, string summaryTitle)
    {
        var pairs = affected
            .DistinctBy(a => $"{a.Server} {a.Character}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Character, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pairs.Count == 1)
            return ($"{pairs[0].Server} {verb} {pairs[0].Character}", pairs[0].Character);

        var names = Join(pairs.Select(p => p.Character).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return (summaryTitle, names);
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };
}
