namespace EveUtils.Shared.Modules.Fleet.Cleanup;

/// <summary>
/// Tuning for the fleet cleanup sweep. <see cref="InactivityGrace"/> is how long an Active fleet with no
/// active participant and no member events may linger before it is archived; <see cref="HardDeleteAfter"/> is how
/// long an archived fleet is kept before its rows are removed. A fleet whose planned end-time has passed skips the
/// grace (archived as soon as no one is participating). POC defaults; the real values are an open tuning point.
/// </summary>
public sealed record FleetCleanupOptions(TimeSpan InactivityGrace, TimeSpan HardDeleteAfter)
{
    public static FleetCleanupOptions Default { get; } = new(
        InactivityGrace: TimeSpan.FromMinutes(30),
        HardDeleteAfter: TimeSpan.FromHours(24));
}
