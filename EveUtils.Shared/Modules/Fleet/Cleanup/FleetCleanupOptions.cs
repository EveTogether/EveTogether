namespace EveUtils.Shared.Modules.Fleet.Cleanup;

/// <summary>
/// Tuning for the fleet cleanup sweep. <see cref="InactivityGrace"/> is how long an Active fleet with no
/// active participant and no member events may linger before it is archived; <see cref="HardDeleteAfter"/> is how
/// long an archived fleet is kept before its rows are removed. A fleet whose planned end-time has passed skips the
/// grace (archived as soon as no one is participating). POC defaults; the real values are an open tuning point.
///
/// <see cref="ReconnectGrace"/> serves the automatic stop (ET-167) and belongs to the same sweep, so it is tuned
/// here rather than beside it.
/// </summary>
public sealed record FleetCleanupOptions(
    TimeSpan InactivityGrace,
    TimeSpan HardDeleteAfter,
    TimeSpan ReconnectGrace)
{
    public static FleetCleanupOptions Default { get; } = new(
        InactivityGrace: TimeSpan.FromMinutes(30),
        HardDeleteAfter: TimeSpan.FromHours(24),
        // How long clients are allowed to be back before their silence counts as news. This is the same number as
        // the cleanup service's startup delay, for the same measured reason: heartbeat + token refresh + pairing take
        // well over a few seconds, so a sweep run any sooner sees zero live members for every fleet. The service now
        // reads its delay from here, so the two cannot drift apart.
        ReconnectGrace: TimeSpan.FromMinutes(2));
}
