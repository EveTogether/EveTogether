using EveUtils.Shared.Modules.Sde.Enums;

namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// Pre-computed fitting metadata for a type: which slot it occupies plus turret/launcher hardpoint flags.
/// Computed during import from the type's dogma effects so fit parsers get O(1) slot lookups (no dogma join
/// at runtime). For ships, <see cref="NumberOfSlots"/> carries the slot count from dogma attribute 47.
/// </summary>
public sealed record SdeFitRequirement(
    SdeSlotType SlotType,
    int NumberOfSlots,
    bool IsLauncher,
    bool IsTurret);
