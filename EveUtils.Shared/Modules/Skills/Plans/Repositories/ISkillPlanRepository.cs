using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Shared.Modules.Skills.Plans.Repositories;

/// <summary>
/// Stores a character's skill plans and their rows (ET-355) — local-only data, never synced to a server (D-179).
/// Taken only by the skill-plan command handlers, so every write publishes <c>SkillPlansChangedEvent</c>.
/// </summary>
public interface ISkillPlanRepository : ISkillPlanReader
{
    /// <summary>Creates an empty plan and returns its id.</summary>
    Task<int> CreateAsync(SkillPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Returns false when no plan carries <paramref name="planId"/> (already gone).</summary>
    Task<bool> RenameAsync(int planId, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the plan and every one of its rows. Returns false when no plan carries
    /// <paramref name="planId"/> (already gone).</summary>
    Task<bool> DeleteAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Appends <paramref name="rows"/> after the plan's current last position, skipping any row whose
    /// (skill, level) is already in the plan — either already stored or repeated within this same batch. Returns how
    /// many rows were actually added, so a caller can tell an all-duplicate add ("nothing to add") from a real one.</summary>
    Task<int> AddRowsAsync(int planId, IReadOnlyList<SkillPlanRow> rows, CancellationToken cancellationToken = default);

    /// <summary>Removes a plan's row for this (skill, level) — the dedupe key, and unambiguous the same way
    /// <c>RemoveMatchingProvisionalKillmailCommand</c> matches on a natural key instead of a synthetic row id.
    /// Returns false when the plan carries no such row (already gone).</summary>
    Task<bool> RemoveRowAsync(int planId, int skillTypeId, int level, CancellationToken cancellationToken = default);
}
