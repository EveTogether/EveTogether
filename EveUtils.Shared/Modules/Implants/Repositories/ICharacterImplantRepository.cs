namespace EveUtils.Shared.Modules.Implants.Repositories;

/// <summary>
/// Stores a character's imported implant type ids. A re-import replaces the whole set for that character.
/// </summary>
public interface ICharacterImplantRepository
{
    /// <summary>Replaces all stored implant type ids for the character with the given set.</summary>
    Task ReplaceForCharacterAsync(int characterId, IReadOnlyList<int> implantTypeIds, CancellationToken cancellationToken = default);

    /// <summary>The character's stored implant type ids; empty when nothing was imported yet.</summary>
    Task<IReadOnlyList<int>> GetTypeIdsAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Whether any implants have been imported for the character.</summary>
    Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default);
}
