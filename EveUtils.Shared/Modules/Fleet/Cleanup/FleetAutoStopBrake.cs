using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Shared.Modules.Fleet.Cleanup;

/// <summary>
/// When "everybody has gone quiet" may not be believed (ET-167). Only the all-offline trigger needs this: a member
/// who left the roster stays gone through any outage, but a member who merely stopped being heard from is exactly
/// what a restart manufactures for the whole fleet at once. Read it as the question "is there a reason everyone
/// would look absent right now that has nothing to do with them?".
///
/// The codebase had already been caught by this failure mode once — <c>FleetCleanupService</c> carries a two-minute
/// startup delay with the note that sweeping at +15 s would see zero connected members for every fleet — so the
/// thresholds here are that one and the existing downtime signals, not new numbers.
/// </summary>
public static class FleetAutoStopBrake
{
    /// <summary>
    /// True while an absent-looking fleet may not be acted on.
    /// </summary>
    /// <param name="now">The sweep's clock.</param>
    /// <param name="esiUsable"><c>IEsiAvailabilityState.IsUsable</c> — unplanned unavailability, polled every 15 s
    /// while down. The server runs the <c>/status/</c> poller itself, so this is a live signal on both hosts.</param>
    /// <param name="lastSeenUnavailableAt">The last time the caller itself observed the gate closed, or null. A
    /// sweep runs every few minutes and would otherwise only ever see the recovered state, releasing the brake at
    /// the exact moment clients are still queueing to reconnect.</param>
    /// <param name="reconnectGrace"><c>FleetCleanupOptions.ReconnectGrace</c> — how long clients get to come back.</param>
    public static bool IsEngaged(
        DateTimeOffset now,
        bool esiUsable,
        DateTimeOffset? lastSeenUnavailableAt,
        TimeSpan reconnectGrace)
    {
        // The daily 11:00 UTC window, held open for the reconnect grace: at 11:03:00 sharp the pilots who went down
        // with Tranquility are still on their way back, and stopping their fleet then is the false positive this
        // whole brake exists for.
        if (EsiDowntime.IsWithinScheduledWindow(now, reconnectGrace))
            return true;

        // Unplanned: a failed /status/ poll means the same thing without the calendar.
        if (!esiUsable)
            return true;

        return lastSeenUnavailableAt is { } at && now - at < reconnectGrace;
    }
}
