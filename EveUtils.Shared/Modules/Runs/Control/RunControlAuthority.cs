namespace EveUtils.Shared.Modules.Runs.Control;

/// <summary>
/// The one place that answers "may this character start, stop or discard the shared run right now?" — ET-105's
/// single decision point, so that where the FC right hangs is one edit rather than a thread through the controls.
///
/// Raymond's ruling (ET-105, 2026-09-01): the right hangs on <b>whoever commands the fleet right now</b>, not on
/// whoever started the run. A handover mid-run moves the buttons with it, and that is an ordinary state change rather
/// than an edge case — so this is re-evaluated per action instead of captured once at start.
///
/// Who that is comes from the <b>ET roster</b>, where a human appoints an FC, and not from the ESI fleet boss. That
/// endpoint only answers for a fleet coupled to an in-game one, so an ordinary ET fleet never produced a boss, always
/// landed on <see cref="RunControlAuthorityLevel.Unknown"/>, and hid the controls from the very person who should
/// have them (ET-152, reported by Jithran 2026-09-03).
/// </summary>
public enum RunControlAuthorityLevel
{
    /// <summary>The fleet's roster could not be read, so who commands it cannot be established. Distinct from
    /// <see cref="Denied"/> on purpose: discard reaches four other machines, so not knowing must not read as "no"
    /// (which would be silent) nor as "yes" (which would hand a destructive button to everyone). A client-only
    /// fleet's roster is local and always readable, so in practice this is a server that went quiet.</summary>
    Unknown = 0,

    /// <summary>The fleet has a known commander and it is somebody else.</summary>
    Denied = 1,

    /// <summary>This character commands the run: the fleet commander, or a pilot over a run of their own.</summary>
    Granted = 2
}

/// <param name="Level">The verdict the controls bind to.</param>
/// <param name="FleetCommanderCharacterId">Who the verdict was reached against, for the tooltip; null when the
/// roster could not say.</param>
/// <param name="IsFleetCommander">Whether this character commands the FLEET. A different question from whether they
/// may steer this run, and the only one that decides whether a start is announced to the fleet.</param>
/// <param name="FleetCommanderName">What to call that commander on screen. Handed in by the layer that already
/// resolves character names; this one holds ids and never looks a name up. Null is an unresolved name rather than an
/// unknown commander, so the sentence drops the who and keeps the rule.</param>
public readonly record struct RunControlAuthority(
    RunControlAuthorityLevel Level, int? FleetCommanderCharacterId, bool IsFleetCommander = false,
    string? FleetCommanderName = null)
{
    /// <summary>
    /// Answer both questions against the fleet's own roster: may this character steer this run, and do they command
    /// the fleet. They are not the same question — a member flying their own run steers it
    /// without commanding anything — and keeping them in one place is what ET-105 asked for.
    /// </summary>
    /// <param name="fleetId">The fleet this client is in, or null when it is in none.</param>
    /// <param name="fleetCommanderCharacterId">Who holds FleetCommander on the ET roster, or null when the roster
    /// could not be read.</param>
    /// <param name="actingCharacterId">The character whose window this is, or null when no character is chosen.</param>
    /// <param name="groupCode">The run's own group code, null for a run that belongs to no group.</param>
    /// <param name="fleetCommanderName">That commander's name, where the caller already knows it.</param>
    public static RunControlAuthority From(
        long? fleetId, int? fleetCommanderCharacterId, int? actingCharacterId, string? groupCode,
        string? fleetCommanderName = null)
    {
        // Commanding the fleet takes a roster that names a commander and this character being them. Not knowing is
        // never "yes" here: no group code is made and nothing is announced, which is the safe way round for a
        // question that has no answer yet.
        bool commandsTheFleet = fleetId is not null
            && fleetCommanderCharacterId is { } commander
            && actingCharacterId == commander;

        // Solo is what the RUN says it is: no group code means no group to command and no other machine a button
        // could reach. Being in a fleet does not make your own run somebody else's to steer — the fleet id says
        // where a run is filed, not who commands it (ET-152). So this is safe even with the commander unknown, which
        // is the case Unknown below exists for: there, DISCARD reaches four other machines.
        if (groupCode is null)
            return new RunControlAuthority(
                RunControlAuthorityLevel.Granted, fleetCommanderCharacterId, commandsTheFleet, fleetCommanderName);

        // Shared, so a button here reaches other people's machines. With no fleet, no commander the roster could
        // name, or no character to compare, not knowing must not read as "yes" — nor as a silent "no" (ET-105).
        if (fleetId is null || fleetCommanderCharacterId is not { } named || actingCharacterId is null)
            return new RunControlAuthority(
                RunControlAuthorityLevel.Unknown, fleetCommanderCharacterId, commandsTheFleet, fleetCommanderName);

        return new RunControlAuthority(
            named == actingCharacterId ? RunControlAuthorityLevel.Granted : RunControlAuthorityLevel.Denied,
            named, commandsTheFleet, fleetCommanderName);
    }

    public bool CanControl => Level is RunControlAuthorityLevel.Granted;

    /// <summary>Whether the reason there are no buttons has to be said out loud. In the spirit of ET-65 AC-7: an
    /// empty state is a state, not silence.</summary>
    public bool IsUnknown => Level is RunControlAuthorityLevel.Unknown;

    public string StatusText => Level switch
    {
        RunControlAuthorityLevel.Granted => "You command this run.",
        // A bare 90250177 on screen names nobody (Raymond, 2026-09-03). Without a resolved name the sentence states
        // the rule and leaves the who out, rather than printing the id this record happens to hold.
        RunControlAuthorityLevel.Denied => FleetCommanderName is { Length: > 0 } commander
            ? $"Only {commander}, who commands this fleet, can start, stop or discard this run."
            : "Only the fleet commander can start, stop or discard this run.",
        _ => "Who commands this fleet is not known right now, so the run controls are hidden."
    };
}
