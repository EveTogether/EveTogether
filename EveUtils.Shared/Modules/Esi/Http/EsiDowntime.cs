namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// EVE's daily server maintenance window (Tranquility goes down at ~11:00 UTC for a couple of minutes).
/// Used to proactively gate non-essential calls even before the <c>/status/</c> poll confirms downtime, so
/// we are a good ESI citizen and do not burst into a dead API right at 11:00.
/// </summary>
public static class EsiDowntime
{
    // Narrow window (11:00:00–11:02:59 UTC): downtime is essentially always at 11:00 and over within ~2 min;
    // keeping it tight avoids blocking legitimate calls on the rare day downtime is skipped.
    public static bool IsScheduledWindow(DateTimeOffset utcNow) =>
        utcNow.Hour == 11 && utcNow.Minute < 3;
}
