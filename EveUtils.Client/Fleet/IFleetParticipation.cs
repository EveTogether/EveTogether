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
}
