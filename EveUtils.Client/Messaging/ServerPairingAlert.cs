using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Messaging;

/// <summary>
/// Resolves the top-of-window banner for a server that refuses this client's stored sign-in (ET-77). Companion to the
/// red chip on the character card: the chip says WHICH character it is, this says what it costs you and does not go
/// away on its own.
///
/// The pairing itself is no longer thrown away when this happens (ET-121), so the banner has a second job it did not
/// have before: it is now the only thing that says a kept-but-refused pairing exists at all.
///
/// It has to be persistent rather than a toast, because the damage is silent and ongoing: a list read against a
/// server that no longer accepts the session comes back as an EMPTY LIST, not an error, so every fleet/composition
/// list from that server reads as "there is nothing here" for as long as the pairing stays broken. A notification
/// that fades after a few seconds would be gone long before the user next opened one of those lists.
///
/// Only <see cref="ServerConnectionState.SessionExpired"/> and <see cref="ServerConnectionState.SessionGone"/>
/// count. A reconnecting or briefly dropped link fixes itself (and an access token that has merely expired is
/// refreshed silently by the heartbeat), so raising the banner for those would train the user to ignore it.
///
/// The two that do count get their own wording (ET-123). One is being retried and may clear on its own; the other
/// has stopped and will not clear until the user couples again. Telling someone their client is still trying when
/// it has given up is worse than saying nothing.
/// </summary>
public static class ServerPairingAlert
{
    /// <summary>Builds the banner from the live per-server link states: the display names of the servers whose
    /// pairing is no longer valid. Names are de-duplicated (a server is one server however many characters are
    /// coupled to it) and ordered, so the text is stable between rebuilds.</summary>
    public static (bool Show, string Message) For(IEnumerable<(string ServerName, ServerConnectionState State)> links)
    {
        var all = links.ToList();
        var gone = Names(all, ServerConnectionState.SessionGone);
        var refused = Names(all, ServerConnectionState.SessionExpired)
            // A server in both states at once (one character gone, another merely refused) is named by the harder
            // of the two: "couple again" is the move that also settles the other one.
            .Where(n => !gone.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (gone.Count == 0 && refused.Count == 0)
            return (false, "");

        // Two messages rather than one blended into both: they ask the user for opposite things — sit tight, or act
        // now — and a sentence that covers both ends up asking for neither.
        var parts = new List<string>();
        if (gone.Count > 0)
        {
            var (verb, holder) = gone.Count == 1 ? ("no longer has", "that server") : ("no longer have", "those servers");
            parts.Add(
                $"{Join(gone)} {verb} a session for this client, so it has stopped trying to reconnect. Until you "
                + $"couple the character again, anything {holder} holds — fleets, compositions, shared fits — reads "
                + "as empty rather than as an error, and nothing can be saved there.");
        }

        if (refused.Count > 0)
        {
            var (verb, holder) = refused.Count == 1 ? ("is", "that server") : ("are", "those servers");
            parts.Add(
                $"{Join(refused)} {verb} refusing this client's stored sign-in and will not renew it. Your pairing is "
                + "kept and retried every few minutes, so this may clear on its own — but while it lasts, anything "
                + $"{holder} holds — fleets, compositions, shared fits — reads as empty rather than as an error, and "
                + "nothing can be saved there. Couple the character again if it does not clear.");
        }

        return (true, string.Join(" ", parts));
    }

    private static List<string> Names(
        IEnumerable<(string ServerName, ServerConnectionState State)> links, ServerConnectionState state) =>
        links
            .Where(l => l.State == state)
            .Select(l => l.ServerName)
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
