using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Skill-training estimate (fit-validation): SP to train + Omega time, from the skill's rank (skillTimeConstant)
/// and its primary/secondary training attributes, against the character's effective attributes (base + implants). The
/// CCP formulas are SP(L) = 250·rank·√32^(L−1) and SP/min = primary + secondary/2.
/// </summary>
public class SkillTrainingEstimatorTests
{
    private const int SkillType = 3300;

    // A rank-1 skill that trains on Perception (primary) + Willpower (secondary).
    private static SkillTrainingEstimator Estimator() => new(new FakeDogmaDataAccessor()
        .Type(SkillType, 0, 0,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 1),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower)));

    [Fact]
    public void Estimate_ComputesSpAndTime_FromRankAndEffectiveAttributes()
    {
        var attributes = new CharacterAttributeSet(Charisma: 19, Intelligence: 20, Memory: 20, Perception: 20, Willpower: 20);

        var estimate = Estimator().Estimate(SkillType, currentLevel: 4, requiredLevel: 5, attributes);

        // SP(5) − SP(4) for rank 1 = 256000 − 45254.83 ≈ 210745
        Assert.Equal(210745.17, estimate.SkillPointsRequired, 1);
        // 30 SP/min (20 + 20/2) → 210745 / 30 ≈ 7024.8 min
        Assert.Equal(7024.84, estimate.TrainingTime.TotalMinutes, 1);
    }

    [Fact]
    public void Estimate_HigherAttributes_TrainFaster()
    {
        // A +5 Perception attribute implant (folded into the effective set) raises the primary attribute → less time for
        // the same SP — exactly why implants must be folded into the estimate.
        var withoutImplant = new CharacterAttributeSet(19, 20, 20, Perception: 20, Willpower: 20);
        var withImplant = new CharacterAttributeSet(19, 20, 20, Perception: 25, Willpower: 20);

        var slow = Estimator().Estimate(SkillType, 4, 5, withoutImplant);
        var fast = Estimator().Estimate(SkillType, 4, 5, withImplant);

        Assert.Equal(fast.SkillPointsRequired, slow.SkillPointsRequired);   // same SP, only the rate changed
        Assert.True(fast.TrainingTime < slow.TrainingTime);
        // 35 SP/min (25 + 20/2) vs 30 → 6021.3 min
        Assert.Equal(6021.29, fast.TrainingTime.TotalMinutes, 1);
    }
}
