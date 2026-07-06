namespace EveUtils.Shared.Modules.Fleet.Enums;

/// <summary>What changed about a fleet, carried by <c>FleetChangedEvent</c> so a client refreshes the right surface
/// live: activation/conclusion drives the participation + metrics set, a roster change drives the roster window, a
/// composition change drives the coupled-doctrine band + the scoped fit picker.</summary>
public enum FleetChangeKind
{
    Activated,
    Concluded,
    RosterChanged,
    CompositionChanged
}
