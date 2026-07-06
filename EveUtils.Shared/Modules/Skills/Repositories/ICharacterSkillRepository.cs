namespace EveUtils.Shared.Modules.Skills.Repositories;

/// <summary>
/// Stores a character's imported effective skill levels. A re-import replaces the whole set for that character.
/// </summary>
public interface ICharacterSkillRepository
{
    /// <summary>Replaces all stored levels for the character with the given snapshot+queue-merged set.</summary>
    Task ReplaceForCharacterAsync(int characterId, IReadOnlyDictionary<int, int> levels, CancellationToken cancellationToken = default);

    /// <summary>The character's stored skill levels (skillTypeId → level); empty when nothing was imported yet.</summary>
    Task<IReadOnlyDictionary<int, int>> GetLevelsAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Whether any skills have been imported for the character.</summary>
    Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default);
}
