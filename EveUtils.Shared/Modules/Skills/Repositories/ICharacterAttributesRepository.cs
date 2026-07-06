using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Shared.Modules.Skills.Repositories;

/// <summary>Stores a character's training attributes. A re-import replaces the single row.</summary>
public interface ICharacterAttributesRepository
{
    /// <summary>Stores (insert-or-replace) the character's five training attributes.</summary>
    Task ReplaceForCharacterAsync(CharacterAttributes attributes, CancellationToken cancellationToken = default);

    /// <summary>The character's stored attributes, or null when nothing was imported yet.</summary>
    Task<CharacterAttributes?> GetAsync(int characterId, CancellationToken cancellationToken = default);
}
