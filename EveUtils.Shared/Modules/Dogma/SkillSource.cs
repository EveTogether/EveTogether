using System;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Where a fit's skill levels come from: every skill at a uniform planning level (1-5), or a character's actual levels.
/// The character path is fed by an ESI skill import (esi-skills.read_skills.v1). Either way the engine injects
/// <see cref="SkillTypeIdsToInject"/>, each at <see cref="LevelFor"/>.
/// </summary>
public sealed record SkillSource
{
    private readonly IReadOnlyDictionary<int, int>? _levels;
    private readonly int _allLevel;   // the uniform level when _levels is null (the "all skills at level N" baseline)

    private SkillSource(IReadOnlyDictionary<int, int>? levels, int allLevel)
    {
        _levels = levels;
        _allLevel = allLevel;
    }

    /// <summary>Every skill assumed trained to level 5 (the all-V planning baseline).</summary>
    public static SkillSource AllLevelFive { get; } = new(null, 5);

    /// <summary>Every skill assumed trained to a uniform level 0-5 (the "all level N" planning baseline).</summary>
    public static SkillSource AllAtLevel(int level) => new(null, Math.Clamp(level, 0, 5));

    /// <summary>A character's actual skill levels (ESI snapshot + queue); skills absent from the map default to 0.</summary>
    public static SkillSource From(IReadOnlyDictionary<int, int> levels) => new(levels, 5);

    /// <summary>True for an "all skills" baseline at one uniform level; false for a character snapshot.</summary>
    public bool InjectsAllSkills => _levels is null;

    /// <summary>
    /// Every SDE skill, plus any skill a character snapshot holds that the SDE list lacks. A snapshot's untrained skill
    /// is injected too, at level 0: a hull bonus is stored on the ship as its per-level value and only scaled by the
    /// skill's own PreMul-by-skillLevel effect, so without the skill item the ship would count it as level I.
    /// </summary>
    public IEnumerable<int> SkillTypeIdsToInject(IReadOnlyList<int> sdeSkillTypeIds) =>
        sdeSkillTypeIds.Union(_levels?.Keys ?? []);

    public int LevelFor(int skillTypeId) =>
        _levels is null ? _allLevel
        : _levels.TryGetValue(skillTypeId, out var level) ? level
        : 0;
}
