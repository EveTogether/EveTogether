using System;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Where a fit's skill levels come from: every skill at a uniform planning level (1-5), or a character's actual levels.
/// The character path is fed by an ESI skill import (esi-skills.read_skills.v1). Either way the engine only asks
/// <see cref="LevelFor"/> and <see cref="InjectsAllSkills"/>.
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

    /// <summary>True for an "all skills" baseline, where every skill from the SDE is injected at <see cref="LevelFor"/>;
    /// false for a character snapshot, which injects only its trained skills.</summary>
    public bool InjectsAllSkills => _levels is null;

    /// <summary>The explicit skill type ids to inject (a character snapshot's trained skills); empty for an all-skills
    /// baseline, which instead injects every skill from the SDE.</summary>
    public IReadOnlyCollection<int> ExplicitSkillTypeIds => _levels?.Keys.ToArray() ?? [];

    public int LevelFor(int skillTypeId) =>
        _levels is null ? _allLevel
        : _levels.TryGetValue(skillTypeId, out var level) ? level
        : 0;
}
