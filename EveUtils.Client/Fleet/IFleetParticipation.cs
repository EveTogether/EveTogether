namespace EveUtils.Client.Fleet;

/// <summary>
/// The set of fleets the client is currently in — what the metric publisher pushes for. Membership-driven:
/// being a member of a fleet on a connected server (or owning a client-only fleet) is what shares metrics, replacing
/// the old explicit "enter" gate. Refreshed from the fleet listing whenever it loads.
/// </summary>
public interface IFleetParticipation
{
    IReadOnlyList<FleetParticipant> Current { get; }

    /// <summary>Replaces the current participation set (called when the fleet listing reloads).</summary>
    void Set(IReadOnlyList<FleetParticipant> participants);

    /// <summary>
    /// Drops one pilot from one fleet, right now. The set is otherwise only ever rewritten by the fleet listing, which
    /// reloads while the fleets window is open — so a pilot removed from a fleet kept being published for at 1 Hz
    /// until that window happened to sweep, and with it closed, indefinitely. Those samples put the pilot's card
    /// straight back on the fleet-metrics screen the FC had just cleared them from (ET-49).
    /// </summary>
    void Remove(long fleetId, int characterId);
}
