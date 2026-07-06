namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Stores ESI token sets per character, encrypted at rest. Replaces the single-slot
/// <c>IClientTokenStore</c>; each character gets its own encrypted file keyed by ESI character id.
/// </summary>
public interface IPerCharacterTokenStore
{
    Task SaveAsync(int characterId, EsiTokenSet tokens, CancellationToken cancellationToken = default);

    Task<EsiTokenSet?> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> ListCharacterIdsAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(int characterId, CancellationToken cancellationToken = default);
}
