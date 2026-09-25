using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Shared.Modules.Skills.Plans.Repositories;

/// <summary>
/// Stores a character's skill plans and their rows (ET-355) — local-only data, never synced to a server (D-179).
/// Taken only by the skill-plan command handlers, so every write publishes <c>SkillPlansChangedEvent</c>. Every
/// mutation beyond create is scoped to a <paramref name="characterId"/> as well as a plan id, so a plan can only ever
/// be changed through the character it belongs to.
/// </summary>
public interface ISkillPlanRepository : ISkillPlanReader
{
    /// <summary>Creates an empty plan and returns its id.</summary>
    Task<int> CreateAsync(SkillPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Returns false when no plan carries <paramref name="planId"/> for <paramref name="characterId"/>
    /// (already gone, or owned by a different character).</summary>
    Task<bool> RenameAsync(int characterId, int planId, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the plan and every one of its rows. Returns false when no plan carries
    /// <paramref name="planId"/> for <paramref name="characterId"/> (already gone, or owned by a different character).</summary>
    Task<bool> DeleteAsync(int characterId, int planId, CancellationToken cancellationToken = default);

    /// <summary>Appends <paramref name="rows"/> after the plan's current last position, skipping any row whose
    /// (skill, level) is already in the plan — either already stored or repeated within this same batch. Returns how
    /// many rows were actually added, so a caller can tell an all-duplicate add ("nothing to add") from a real one.
    /// Returns 0 without adding anything when <paramref name="planId"/> is not one of <paramref name="characterId"/>'s.</summary>
    Task<int> AddRowsAsync(int characterId, int planId, IReadOnlyList<SkillPlanRow> rows, CancellationToken cancellationToken = default);

    /// <summary>Removes a plan's row for this (skill, level) — the dedupe key, and unambiguous the same way
    /// <c>RemoveMatchingProvisionalKillmailCommand</c> matches on a natural key instead of a synthetic row id.
    /// Returns false when the plan carries no such row, or <paramref name="planId"/> is not one of
    /// <paramref name="characterId"/>'s.</summary>
    Task<bool> RemoveRowAsync(int characterId, int planId, int skillTypeId, int level, CancellationToken cancellationToken = default);
}
