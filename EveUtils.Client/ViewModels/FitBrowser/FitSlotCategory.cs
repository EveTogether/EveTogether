namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Display grouping for a fit's items in the detail slot-list, in EVE fitting order.
/// Derived from the raw ESI fitting <c>flag</c> string; this is a presentation concern, distinct from the
/// eveship.fit v3 slot mapping in <c>EveshipSlots</c> (which only covers the module slots, for export).
/// </summary>
public enum FitSlotCategory
{
    High,
    Medium,
    Low,
    Rig,
    Subsystem,
    Service,
    Drone,
    Fighter,
    Cargo,
    Other
}
