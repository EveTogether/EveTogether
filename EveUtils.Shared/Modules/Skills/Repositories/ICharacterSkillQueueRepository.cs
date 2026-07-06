using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Shared.Modules.Skills.Repositories;

/// <summary>Stores a character's training queue for the read-only queue view. A re-import replaces the whole queue.</summary>
public interface ICharacterSkillQueueRepository
{
    /// <summary>Replaces all stored queue entries for the character with the given set.</summary>
    Task ReplaceForCharacterAsync(int characterId, IReadOnlyList<CharacterSkillQueueEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>The character's stored queue entries, ordered by queue position (0 = currently training); empty when none.</summary>
    Task<IReadOnlyList<CharacterSkillQueueEntry>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}
