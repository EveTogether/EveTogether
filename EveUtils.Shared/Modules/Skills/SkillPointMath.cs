using System;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Skill-point and training-rate math, CCP-stable formulas. Pure, so it is unit-tested apart from ESI/SDE.
/// </summary>
public static class SkillPointMath
{
    private static readonly double Sqrt32 = Math.Sqrt(32.0);

    /// <summary>Total skill points to reach <paramref name="level"/> (1-5) for a skill of the given training rank:
    /// <c>250 * rank * sqrt(32)^(level-1)</c>. Level 0 (untrained) is 0 SP.</summary>
    public static double SkillPointsForLevel(int rank, int level) =>
        level <= 0 ? 0 : 250.0 * rank * Math.Pow(Sqrt32, level - 1);

    /// <summary>The skill points trained per minute on Omega for a skill whose primary/secondary attributes have the
    /// given effective values (base allocation + attribute implants): <c>primary + secondary / 2</c>.</summary>
    public static double SkillPointsPerMinute(double primaryAttribute, double secondaryAttribute) =>
        primaryAttribute + secondaryAttribute / 2.0;
}
