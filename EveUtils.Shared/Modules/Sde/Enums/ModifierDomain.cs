namespace EveUtils.Shared.Modules.Sde.Enums;

/// <summary>
/// The domain a <see cref="EveUtils.Shared.Modules.Sde.Dtos.ModifierInfo"/> routes to — which object the modifier applies to (the ship, the
/// character, the item itself, the paired charge via <see cref="OtherId"/>, etc.). Mirrors the CCP SDE string
/// values verbatim. <see cref="Unknown"/> is the inert fallback for an unmapped value.
/// </summary>
public enum ModifierDomain
{
    Unknown = 0,
    ItemId,
    ShipId,
    CharId,
    OtherId,
    StructureId,
    Target,
    TargetId
}
