using System.Collections.Generic;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// A scan's outcome: the fit's own stat values (the scoring baseline — also which of the fifteen stats even apply,
/// e.g. Optimal/Falloff/Tracking are absent for a fit with no turret or drone weapon), every skill that moves at
/// least one of them, and which stats have no untrained mover only because every skill that moves them is already
/// at level V (distinct from a stat no skill moves at all — the two read as different reasons in the UI).
/// </summary>
public sealed record SkillImpactResult(
    IReadOnlyDictionary<SkillImpactStat, double> BaseValues,
    IReadOnlyList<SkillImpactEntry> Entries,
    IReadOnlySet<SkillImpactStat> StatsWithOnlyMaxedMovers);
