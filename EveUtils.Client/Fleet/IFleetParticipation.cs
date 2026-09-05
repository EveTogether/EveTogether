namespace EveUtils.Client.Fleet;

/// <summary>
/// The set of fleets the client is currently in — what the metric publisher pushes for. Membership-driven:
/// being a member of a fleet on a connected server (or owning a client-only fleet) is what shares metrics, replacing
/// the old explicit "enter" gate. Refreshed from the fleet listing whenever it loads.
/// </summary>
public interface IFleetParticipation
{
    IReadOnlyList<FleetParticipant> Current { get; }

    /// <summary>Every fleet this client's characters belong to, started or not. See <see cref="FleetMembership"/>
    /// for why this is a second list rather than a widened <see cref="Current"/>.</summary>
    IReadOnlyList<FleetMembership> AllMemberships { get; }

    /// <summary>Replaces the current participation set (called when the fleet listing reloads).</summary>
    void Set(IReadOnlyList<FleetParticipant> participants);

    /// <summary>Replaces the membership set, from the same sweep as <see cref="Set"/>.</summary>
    void SetMemberships(IReadOnlyList<FleetMembership> memberships);

    /// <summary>
    /// Drops one pilot from one fleet, right now, rather than at the mercy of the next sweep. Removal is news that
    /// has already happened, and a sample sent in the meantime puts the pilot's card straight back on the
    /// fleet-metrics screen the FC had just cleared them from (ET-49).
    /// </summary>
    void Remove(long fleetId, int characterId);
}
