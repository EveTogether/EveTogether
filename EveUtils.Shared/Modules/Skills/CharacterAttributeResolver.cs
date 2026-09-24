using System.Linq;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// ESI <c>/attributes/</c> already includes attribute-implant bonuses, so its values are effective training attributes.
/// For remapping, the base allocation is recovered by subtracting implant bonuses read from the SDE.
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

    /// <summary>The effective training attributes reported by ESI, including implants.</summary>
    public CharacterAttributeSet Resolve(CharacterAttributes esiAttributes, IReadOnlyList<int> _) => new(
        esiAttributes.Charisma, esiAttributes.Intelligence, esiAttributes.Memory,
        esiAttributes.Perception, esiAttributes.Willpower);

    /// <summary>The base allocation, with SDE attribute-implant bonuses removed from the ESI values.</summary>
    public CharacterAttributeSet Base(CharacterAttributes esiAttributes, IReadOnlyList<int> implantTypeIds)
    {
        var totals = new Dictionary<int, double>
        {
            [DogmaAttributeIds.Charisma] = esiAttributes.Charisma,
            [DogmaAttributeIds.Intelligence] = esiAttributes.Intelligence,
            [DogmaAttributeIds.Memory] = esiAttributes.Memory,
            [DogmaAttributeIds.Perception] = esiAttributes.Perception,
            [DogmaAttributeIds.Willpower] = esiAttributes.Willpower
        };

        foreach (var implantTypeId in implantTypeIds)
        {
            var attributes = dogma.GetBaseAttributes(implantTypeId);
            foreach (var (bonusAttributeId, characterAttributeId) in ImplantBonuses)
            {
                var bonus = attributes.FirstOrDefault(attribute => attribute.AttributeId == bonusAttributeId);
                if (bonus is not null)
                {
                    totals[characterAttributeId] -= bonus.Value;
                }
            }
        }

        return new CharacterAttributeSet(
            totals[DogmaAttributeIds.Charisma], totals[DogmaAttributeIds.Intelligence],
            totals[DogmaAttributeIds.Memory], totals[DogmaAttributeIds.Perception],
            totals[DogmaAttributeIds.Willpower]);
    }
}
