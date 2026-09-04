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
    private static readonly TimeSpan WindowStart = TimeSpan.FromHours(11);
    private static readonly TimeSpan WindowEnd = WindowStart + TimeSpan.FromMinutes(3);

    public static bool IsScheduledWindow(DateTimeOffset utcNow) => IsWithinScheduledWindow(utcNow, TimeSpan.Zero);

    /// <summary>
    /// The same window, held open for <paramref name="margin"/> after it closes. The gate itself wants the tight
    /// window — a call at 11:04 has every chance of succeeding — but anything that reads a client's <i>absence</i> as
    /// news needs the tail too: at 11:03:00 sharp the pilots whose clients went down with Tranquility are still on
    /// their way back, and their silence is the downtime talking rather than them (ET-167).
    /// </summary>
    public static bool IsWithinScheduledWindow(DateTimeOffset utcNow, TimeSpan margin)
    {
        // .Hour on a DateTimeOffset is read in that value's own offset; normalise so a non-UTC stamp cannot land
        // inside — or outside — a window that is defined in UTC.
        var time = utcNow.UtcDateTime.TimeOfDay;
        return time >= WindowStart && time < WindowEnd + margin;
    }
}
