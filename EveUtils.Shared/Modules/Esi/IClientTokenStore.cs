namespace EveUtils.Shared.Modules.Esi;

/// <summary>Stores the local-character ESI tokens encrypted at rest on the client.</summary>
public interface IClientTokenStore
{
    Task SaveAsync(EsiTokenSet tokens, CancellationToken cancellationToken = default);

    Task<EsiTokenSet?> LoadAsync(CancellationToken cancellationToken = default);
}
