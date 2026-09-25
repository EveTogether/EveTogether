using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Shared.Modules.Skills.Plans.Repositories;

/// <summary>
/// The read half of <see cref="ISkillPlanRepository"/>. Everything outside the skill-plan command handlers takes
/// this one, so a stored plan or row change always arrives through a handler that publishes the signal.
/// </summary>
public interface ISkillPlanReader
{
    /// <summary>The character's plans, newest first.</summary>
    Task<IReadOnlyList<SkillPlan>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default);

    Task<SkillPlan?> GetAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>A plan's rows, ordered by their stored position.</summary>
    Task<IReadOnlyList<SkillPlanRow>> GetRowsAsync(int planId, CancellationToken cancellationToken = default);
}
