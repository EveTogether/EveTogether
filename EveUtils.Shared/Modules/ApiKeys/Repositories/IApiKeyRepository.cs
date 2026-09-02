using EveUtils.Shared.Modules.ApiKeys.Entities;

namespace EveUtils.Shared.Modules.ApiKeys.Repositories;

public interface IApiKeyRepository
{
    Task<int> AddAsync(ApiKey key, CancellationToken cancellationToken = default);

    /// <summary>The validation path: one indexed read on the plaintext prefix, before any hashing.</summary>
    Task<ApiKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    Task<ApiKey?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default);
    Task<bool> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);
    Task<bool> SetScopesAsync(int id, string scopes, CancellationToken cancellationToken = default);
    Task TouchLastUsedAsync(int id, DateTimeOffset usedAt, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
