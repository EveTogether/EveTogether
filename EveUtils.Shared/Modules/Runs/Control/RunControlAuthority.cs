namespace EveUtils.Shared.Modules.Runs.Control;

/// <summary>
/// The one place that answers "may this character start, stop or discard the shared run right now?" — ET-105's
/// single decision point, so that where the FC right hangs is one edit rather than a thread through the controls.
///
/// Raymond's ruling (ET-105, 2026-09-01): the right hangs on the <b>current ESI fleet boss</b>, not on whoever
/// started the run. A boss handover mid-run moves the buttons with it, and that is an ordinary state change rather
/// than an edge case — so this is re-evaluated per action instead of captured once at start.
/// </summary>
public enum RunControlAuthorityLevel
{
    /// <summary>Who commands the fleet cannot be established — ESI lags or has dropped out. Distinct from
    /// <see cref="Denied"/> on purpose: discard reaches four other machines, so not knowing must not read as "no"
    /// (which would be silent) nor as "yes" (which would hand a destructive button to everyone).</summary>
    Unknown = 0,

    /// <summary>The fleet has a known boss and it is somebody else.</summary>
    Denied = 1,

    /// <summary>This character commands the run: the fleet boss, or a solo pilot over their own run.</summary>
    Granted = 2
}

/// <param name="Level">The verdict the controls bind to.</param>
/// <param name="FleetBossCharacterId">Who the verdict was reached against, for the tooltip; null when unknown.</param>
public readonly record struct RunControlAuthority(RunControlAuthorityLevel Level, int? FleetBossCharacterId)
{
    /// <summary>
    /// Decide against the fleet boss as ESI currently reports them.
    /// </summary>
    /// <param name="fleetId">The fleet the run belongs to, or null for a solo run.</param>
    /// <param name="fleetBossCharacterId">The current ESI fleet boss, or null when ESI cannot say.</param>
    /// <param name="actingCharacterId">The character whose window this is, or null when no character is chosen.</param>
    public static RunControlAuthority From(long? fleetId, int? fleetBossCharacterId, int? actingCharacterId)
    {
        // A solo run has no commander to be, and no other machine to reach: the pilot owns it outright. The risk
        // this guard exists for is the fleet case, so a solo run is never held back for want of a fleet boss.
        if (fleetId is null)
            return new RunControlAuthority(RunControlAuthorityLevel.Granted, actingCharacterId);

        if (fleetBossCharacterId is not { } boss || actingCharacterId is null)
            return new RunControlAuthority(RunControlAuthorityLevel.Unknown, fleetBossCharacterId);

        return new RunControlAuthority(
            boss == actingCharacterId ? RunControlAuthorityLevel.Granted : RunControlAuthorityLevel.Denied, boss);
    }

    public bool CanControl => Level is RunControlAuthorityLevel.Granted;

    /// <summary>Whether the reason there are no buttons has to be said out loud. In the spirit of ET-65 AC-7: an
    /// empty state is a state, not silence.</summary>
    public bool IsUnknown => Level is RunControlAuthorityLevel.Unknown;

    public string StatusText => Level switch
    {
        RunControlAuthorityLevel.Granted => "You command this run.",
        RunControlAuthorityLevel.Denied =>
            $"Only the fleet commander (character {FleetBossCharacterId}) can start, stop or discard this run.",
        _ => "Who commands this fleet is not known right now, so the run controls are hidden."
    };
}
