using System;
using System.Linq;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Estimates the SP + Omega training time to take one skill from its current to its required level (fit-validation
/// "Skills Required" panel). Pure: the skill's rank (<c>skillTimeConstant</c> 275) and its primary/secondary
/// training attributes (180/181, each pointing at a character attribute 164-168) come from the SDE; the per-minute rate
/// reads the character's effective attributes (base allocation folded with attribute implants by
/// <see cref="CharacterAttributeResolver"/>), so a +stat implant shortens the estimate exactly as in-game.
/// </summary>
public sealed class SkillTrainingEstimator(IDogmaDataAccessor dogma)
{
    public SkillTrainingEstimate Estimate(int skillTypeId, int currentLevel, int requiredLevel, CharacterAttributeSet attributes)
    {
        var skillAttributes = dogma.GetBaseAttributes(skillTypeId);
        double Attribute(int attributeId) =>
            skillAttributes.FirstOrDefault(attribute => attribute.AttributeId == attributeId)?.Value ?? 0;

        var rank = (int)Attribute(DogmaAttributeIds.SkillTimeConstant);
        if (rank <= 0)
            rank = 1;   // every published skill carries a rank; default to 1 rather than divide nonsense

        var skillPoints = SkillPointMath.SkillPointsForLevel(rank, requiredLevel)
                          - SkillPointMath.SkillPointsForLevel(rank, currentLevel);

        var primary = attributes.For((int)Attribute(DogmaAttributeIds.SkillPrimaryAttribute));
        var secondary = attributes.For((int)Attribute(DogmaAttributeIds.SkillSecondaryAttribute));
        var perMinute = SkillPointMath.SkillPointsPerMinute(primary, secondary);

        var time = perMinute > 0 ? TimeSpan.FromMinutes(skillPoints / perMinute) : TimeSpan.Zero;
        return new SkillTrainingEstimate(skillPoints, time);
    }
}
