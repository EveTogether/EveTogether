using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Repositories;

/// <summary>
/// Client-local persistent cache of public-ESI external-character lookups. Backs
/// <c>ExternalCharacterLookup</c>: a fresh row (&lt; 1 day) is served without an ESI round-trip, and a
/// successful fetch is written back here so it survives restarts.
/// </summary>
public interface IExternalCharacterCache
{
    /// <summary>The cached row for the id, or null when the character has never been resolved on this client.</summary>
    Task<CachedExternalCharacter?> GetAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Insert or update (keyed on character id) the resolved info with its fetch timestamp.</summary>
    Task UpsertAsync(CachedExternalCharacter entry, CancellationToken cancellationToken = default);
}
