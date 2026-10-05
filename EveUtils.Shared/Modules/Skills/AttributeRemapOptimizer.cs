using System;
using System.Collections.Generic;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Brute-forces the base-attribute remap that trains a given set of rows fastest — the same approach EVEMon's
/// <c>AttributesOptimizer</c> takes, without its per-segment split (ET-354 v1 has one remap point: now). Each of
/// the five attributes ranges 17-27 and they sum to 99, giving 2,885 valid distributions
/// (C(18,4) - 5*C(7,4) = 3060 - 175).
/// </summary>
public static class AttributeRemapOptimizer
{
    public const int MinAttribute = 17;
    public const int MaxAttribute = 27;
    public const int TotalPoints = 99;

    /// <summary>Every valid base-attribute distribution: each of the five attributes 17-27, summing to 99.</summary>
    public static IEnumerable<CharacterAttributeSet> EnumerateBaseDistributions()
    {
        for (int charisma = MinAttribute; charisma <= MaxAttribute; charisma++)
        {
            for (int intelligence = MinAttribute; intelligence <= MaxAttribute; intelligence++)
            {
                for (int memory = MinAttribute; memory <= MaxAttribute; memory++)
                {
                    for (int perception = MinAttribute; perception <= MaxAttribute; perception++)
                    {
                        int willpower = TotalPoints - charisma - intelligence - memory - perception;
                        if (willpower < MinAttribute || willpower > MaxAttribute)
                        {
                            continue;
                        }

                        yield return new CharacterAttributeSet(charisma, intelligence, memory, perception, willpower);
                    }
                }
            }
        }
    }

    /// <summary>The base distribution that trains <paramref name="rows"/> fastest, with the character's current
    /// attribute-implant bonus (<paramref name="implantBonus"/>, e.g. <c>Resolve(...) - Base(...)</c>) added on
    /// top of every candidate — implants stay plugged in during a remap.</summary>
    public static AttributeRemapResult Best(IReadOnlyList<RemapTrainingRow> rows, CharacterAttributeSet implantBonus)
    {
        CharacterAttributeSet? best = null;
        var bestTime = TimeSpan.MaxValue;

        foreach (var candidate in EnumerateBaseDistributions())
        {
            var effective = new CharacterAttributeSet(
                candidate.Charisma + implantBonus.Charisma,
                candidate.Intelligence + implantBonus.Intelligence,
                candidate.Memory + implantBonus.Memory,
                candidate.Perception + implantBonus.Perception,
                candidate.Willpower + implantBonus.Willpower);

            var time = TotalTrainingTime(rows, effective);
            if (time < bestTime)
            {
                bestTime = time;
                best = candidate;
            }
        }

        // EnumerateBaseDistributions always yields at least one distribution (e.g. 20/20/20/20/19), so best is
        // never actually null here.
        return new AttributeRemapResult(best ?? throw new InvalidOperationException("No valid attribute distribution."), bestTime);
    }

    /// <summary>The training time <paramref name="rows"/> take with the given effective attributes
    /// (base allocation plus implants).</summary>
    public static TimeSpan TotalTrainingTime(IReadOnlyList<RemapTrainingRow> rows, CharacterAttributeSet effectiveAttributes)
    {
        double minutes = 0;
        foreach (var row in rows)
        {
            var rate = SkillPointMath.SkillPointsPerMinute(
                effectiveAttributes.For(row.PrimaryAttributeId), effectiveAttributes.For(row.SecondaryAttributeId));
            if (rate > 0)
            {
                minutes += row.RemainingSp / rate;
            }
        }

        return TimeSpan.FromMinutes(minutes);
    }
}
