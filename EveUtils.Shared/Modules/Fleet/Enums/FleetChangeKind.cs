namespace EveUtils.Shared.Modules.Fleet.Enums;

/// <summary>What changed about a fleet, carried by <c>FleetChangedEvent</c> so a client refreshes the right surface
/// live: activation/conclusion drives the participation + metrics set, a roster change drives the roster window, a
/// composition change drives the coupled-doctrine band + the scoped fit picker.</summary>
public enum FleetChangeKind
{
    Activated,
    Concluded,
    RosterChanged,
    CompositionChanged,

    /// <summary>The fleet went back to standing by (Active → Forming, ET-166) — its members are coupled to nothing
    /// again, so the surfaces that react to <see cref="Activated"/> have to re-read. Appended rather than slotted in
    /// beside <see cref="Activated"/> on purpose: this travels the wire between a client and a server that update
    /// separately, so the existing values must keep the numbers they already have.</summary>
    Stopped
}
