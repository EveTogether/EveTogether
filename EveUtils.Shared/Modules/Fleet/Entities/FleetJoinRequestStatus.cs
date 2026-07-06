namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>Lifecycle of a request-to-join an invite-only fleet.</summary>
public enum FleetJoinRequestStatus
{
    Pending = 0,
    Accepted = 1,
    Denied = 2
}
