namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>Lifecycle of a durable fleet invite.</summary>
public enum FleetInviteStatus
{
    Pending = 0,
    Accepted = 1,
    Denied = 2,
    Cancelled = 3,
    Expired = 4
}
