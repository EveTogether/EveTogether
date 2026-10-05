using System.Collections.Generic;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>One skill that moves the fit: its current level, and the resulting stat values (not deltas) at its next
/// level and at level V — keyed only by the stats it actually moves. A skill with no key in either dictionary left
/// every stat within epsilon of the fit's base value, and has no entry at all.</summary>
public sealed record SkillImpactEntry(
    int SkillTypeId,
    int CurrentLevel,
    IReadOnlyDictionary<SkillImpactStat, double> AtNextLevel,
    IReadOnlyDictionary<SkillImpactStat, double> AtFive);
