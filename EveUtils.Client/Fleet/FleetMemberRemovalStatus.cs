namespace EveUtils.Client.Fleet;

/// <summary>
/// How far a "remove from fleet" got. Removal from the EVE Together fleet and the in-game kick are two separate
/// steps behind two separate confirmations, so the outcome has to say which of them actually happened: a declined
/// in-game kick is a complete result, a failed one leaves the pilot off the roster but still in the live fleet and
/// must be surfaced.
/// </summary>
public enum FleetMemberRemovalStatus
{
    /// <summary>The first confirmation was declined — nothing changed anywhere.</summary>
    Cancelled,

    /// <summary>Removal from the EVE Together fleet failed; nothing changed anywhere.</summary>
    Failed,

    /// <summary>Removed from the EVE Together fleet. The whole action for a fleet with no in-game coupling.</summary>
    RemovedFromFleet,

    /// <summary>Removed from the EVE Together fleet and kicked from the coupled in-game fleet.</summary>
    RemovedFromFleetAndInGame,

    /// <summary>Removed from the EVE Together fleet; the in-game kick was offered and declined, so the pilot keeps
    /// flying in the live fleet. A full result, not a half failure.</summary>
    RemovedFromFleetInGameDeclined,

    /// <summary>Removed from the EVE Together fleet, but the in-game kick that was asked for failed — off the roster,
    /// still in the live fleet.</summary>
    RemovedFromFleetInGameFailed,
}
