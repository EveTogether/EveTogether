using System;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>Hypothetical attribute-implant bonus from plugging in a matched set (e.g. +4/+5): the higher of the
/// character's current per-attribute bonus and the set's flat bonus, since a weaker current implant would be
/// swapped out but a stronger one would not. Reused by ET-358's plan-remap scenario, per the ET-354 grooming.</summary>
public static class ImplantSetBonus
{
    public static CharacterAttributeSet Apply(CharacterAttributeSet currentImplantBonus, int setBonus) => new(
        Math.Max(currentImplantBonus.Charisma, setBonus),
        Math.Max(currentImplantBonus.Intelligence, setBonus),
        Math.Max(currentImplantBonus.Memory, setBonus),
        Math.Max(currentImplantBonus.Perception, setBonus),
        Math.Max(currentImplantBonus.Willpower, setBonus));
}
