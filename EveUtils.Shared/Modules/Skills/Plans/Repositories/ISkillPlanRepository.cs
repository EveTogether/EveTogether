using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Shared.Modules.Skills.Plans.Repositories;

/// <summary>
/// Stores a character's skill plans and their rows (ET-355), local-only and never synced (D-179); taken only by the
/// skill-plan command handlers, so every write publishes <c>SkillPlansChangedEvent</c>. Every mutation beyond create is
/// scoped to a <paramref name="characterId"/> as well as a plan id.
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

    /// <summary>Appends <paramref name="rows"/> after the plan's last position, skipping any (skill, level) already in
    /// the plan or repeated in the batch. Returns how many rows were added, 0 when <paramref name="planId"/> is not one
    /// of <paramref name="characterId"/>'s plans.</summary>
    Task<int> AddRowsAsync(int characterId, int planId, IReadOnlyList<SkillPlanRow> rows, CancellationToken cancellationToken = default);

    /// <summary>Removes a plan's row for this (skill, level) — the dedupe key, and unambiguous the same way
    /// <c>RemoveMatchingProvisionalKillmailCommand</c> matches on a natural key instead of a synthetic row id.
    /// Returns false when the plan carries no such row, or <paramref name="planId"/> is not one of
    /// <paramref name="characterId"/>'s.</summary>
    Task<bool> RemoveRowAsync(int characterId, int planId, int skillTypeId, int level, CancellationToken cancellationToken = default);
}
