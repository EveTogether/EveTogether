namespace EveUtils.Shared.Modules.Sde.Enums;

/// <summary>
/// The fitting slot a module occupies, derived at import time from the type's dogma effects
/// (loPower/medPower/hiPower/rigSlot/subSystem/serviceSlot — see <c>SlotEffects</c>). A type that
/// carries none of those effects is not a fittable module (<see cref="None"/>).
/// </summary>
public enum SdeSlotType
{
    None = 0,
    High,
    Medium,
    Low,
    Rig,
    Subsystem,
    Service
}
