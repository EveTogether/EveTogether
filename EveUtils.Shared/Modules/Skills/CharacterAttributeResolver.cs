using System.Linq;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Folds a character's attribute implants into the base attributes to get the effective training attributes.
/// The base allocation comes from ESI <c>/attributes/</c> (without implants); each attribute-enhancer implant
/// carries its +stat on an "xxxBonus" attribute (175-179) which maps to a character attribute (164-168) — read
/// data-driven from the SDE. This is what makes the SP/min rate match in-game once a character has +stat implants.
/// </summary>
public sealed class CharacterAttributeResolver(IDogmaDataAccessor dogma)
{
    // An attribute-enhancer implant carries its +stat on the "xxxBonus" attribute (175-179), which raises the matching
    // character attribute (164-168) — e.g. perceptionBonus (178) → Perception (167). Verified against the SDE
    // dogmaAttributes; the implant type does NOT carry the bare character attribute (167) itself.
    private static readonly (int BonusAttributeId, int CharacterAttributeId)[] ImplantBonuses =
    [
        (DogmaAttributeIds.CharismaBonus, DogmaAttributeIds.Charisma),
        (DogmaAttributeIds.IntelligenceBonus, DogmaAttributeIds.Intelligence),
        (DogmaAttributeIds.MemoryBonus, DogmaAttributeIds.Memory),
        (DogmaAttributeIds.PerceptionBonus, DogmaAttributeIds.Perception),
        (DogmaAttributeIds.WillpowerBonus, DogmaAttributeIds.Willpower)
    ];

    /// <summary>The effective attributes = base allocation + the sum of the implant bonuses for each attribute.</summary>
    public CharacterAttributeSet Resolve(CharacterAttributes baseAttributes, IReadOnlyList<int> implantTypeIds)
    {
        var totals = new Dictionary<int, double>
        {
            [DogmaAttributeIds.Charisma] = baseAttributes.Charisma,
            [DogmaAttributeIds.Intelligence] = baseAttributes.Intelligence,
            [DogmaAttributeIds.Memory] = baseAttributes.Memory,
            [DogmaAttributeIds.Perception] = baseAttributes.Perception,
            [DogmaAttributeIds.Willpower] = baseAttributes.Willpower
        };

        foreach (var implantTypeId in implantTypeIds)
        {
            var attributes = dogma.GetBaseAttributes(implantTypeId);
            foreach (var (bonusAttributeId, characterAttributeId) in ImplantBonuses)
            {
                var bonus = attributes.FirstOrDefault(attribute => attribute.AttributeId == bonusAttributeId);
                if (bonus is not null)
                    totals[characterAttributeId] += bonus.Value;
            }
        }

        return new CharacterAttributeSet(
            totals[DogmaAttributeIds.Charisma], totals[DogmaAttributeIds.Intelligence],
            totals[DogmaAttributeIds.Memory], totals[DogmaAttributeIds.Perception],
            totals[DogmaAttributeIds.Willpower]);
    }
}
