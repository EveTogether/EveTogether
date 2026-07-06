using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// Maps SDE dogma effect/attribute ids to fitting metadata. Verified against the live SDE build 3374020:
/// the slot a module occupies is the marker effect present in its <c>typeDogma.dogmaEffects</c>, the
/// turret/launcher hardpoint flags come from effects 40/42, and the number of slots a module consumes is
/// dogma attribute 47 ("slots"). These ids are CCP-stable; see <c>SDE-Loader.md</c> §slot-type.
/// </summary>
public static class SlotEffects
{
    public const int LoPower = 11;     // Low
    public const int HiPower = 12;     // High
    public const int MedPower = 13;    // Medium
    public const int RigSlot = 2663;   // Rig
    public const int SubSystem = 3772; // Subsystem
    public const int ServiceSlot = 6306; // Service

    public const int LauncherFitted = 40;
    public const int TurretFitted = 42;

    /// <summary>Dogma attribute id "slots" — how many slots a module consumes (rigs can use several).</summary>
    public const int SlotsConsumedAttribute = 47;

    public static SdeSlotType ToSlotType(int effectId) => effectId switch
    {
        HiPower => SdeSlotType.High,
        MedPower => SdeSlotType.Medium,
        LoPower => SdeSlotType.Low,
        RigSlot => SdeSlotType.Rig,
        SubSystem => SdeSlotType.Subsystem,
        ServiceSlot => SdeSlotType.Service,
        _ => SdeSlotType.None
    };
}
