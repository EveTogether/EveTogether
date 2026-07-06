namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>What happens to a member's roster spot when they go offline (per-fleet setting).</summary>
public enum FleetOfflineBehavior
{
    /// <summary>Keep the member on the roster while offline.</summary>
    StayOffline = 0,

    /// <summary>Remove the member from the roster when they go offline.</summary>
    AutoLeave = 1
}
