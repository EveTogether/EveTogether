using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Messaging;

/// <summary>
/// Resolves the top-of-window banner for a server pairing that has lapsed (ET-77). Companion to the red chip on the
/// character card: the chip says WHICH character lost its pairing, this says what it costs you and does not go away
/// on its own.
///
/// It has to be persistent rather than a toast, because the damage is silent and ongoing: a list read against a
/// server that no longer accepts the session comes back as an EMPTY LIST, not an error, so every fleet/composition
/// list from that server reads as "there is nothing here" for as long as the pairing stays broken. A notification
/// that fades after a few seconds would be gone long before the user next opened one of those lists.
///
/// Only <see cref="ServerConnectionState.SessionExpired"/> counts. A reconnecting or briefly dropped link fixes
/// itself (and an access token that has merely expired is refreshed silently by the heartbeat), so raising the
/// banner for those would train the user to ignore it.
/// </summary>
public static class ServerPairingAlert
{
    /// <summary>Builds the banner from the live per-server link states: the display names of the servers whose
    /// pairing is no longer valid. Names are de-duplicated (a server is one server however many characters are
    /// coupled to it) and ordered, so the text is stable between rebuilds.</summary>
    public static (bool Show, string Message) For(IEnumerable<(string ServerName, ServerConnectionState State)> links)
    {
        var expired = links
            .Where(l => l.State is ServerConnectionState.SessionExpired)
            .Select(l => l.ServerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (expired.Count == 0)
            return (false, "");

        return (true,
            $"Your pairing with {Join(expired)} is no longer valid. Until you couple the character to it again, "
            + "anything that server holds — fleets, compositions, shared fits — reads as empty rather than as an "
            + "error, and nothing can be saved to it.");
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };
}
