namespace EveUtils.Shared.Modules.Skills;

/// <summary>One skill still to train: its primary/secondary training attribute (SDE ids 164-168) and the skill
/// points remaining to finish it. Rank has already been folded into <see cref="RemainingSp"/> by the caller
/// (<see cref="SkillPointMath.SkillPointsForLevel"/>) — the optimizer only needs the attribute rate.</summary>
public readonly record struct RemapTrainingRow(int PrimaryAttributeId, int SecondaryAttributeId, double RemainingSp);
