using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Services;

namespace EveUtils.Shared.Modules.ServerAuth.Repositories;

public interface IServerAuthRepository
{
    Task<AllowedCharacter?> FindAllowedAsync(int? esiCharacterId, string characterName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AllowedCharacter>> ListAllowedAsync(CancellationToken cancellationToken = default);
    Task<int> AddAllowedAsync(AllowedCharacter allowed, CancellationToken cancellationToken = default);
    Task RemoveAllowedAsync(int id, CancellationToken cancellationToken = default);
    Task EnsureAllowedSeedAsync(IEnumerable<string> characterNames, CancellationToken cancellationToken = default);

    Task<SyncedCharacter> UpsertSyncedAsync(int esiCharacterId, string characterName, EncryptedToken refreshToken, IReadOnlyList<string>? grantedScopes = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncedCharacter>> ListSyncedAsync(CancellationToken cancellationToken = default);

    Task AddSessionAsync(ServerSession session, CancellationToken cancellationToken = default);
    Task<ServerSession?> FindSessionByAccessHashAsync(string accessHash, CancellationToken cancellationToken = default);
    Task<ServerSession?> FindSessionByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken = default);
    Task TouchHeartbeatAsync(string accessHash, DateTimeOffset at, CancellationToken cancellationToken = default);
    Task<bool> RotateSessionAsync(int sessionId, string newAccessHash, string newRefreshHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset refreshExpiresAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ServerSession>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default);
    /// <summary>Deletes all sessions past their hard refresh window (RefreshExpiresAt &lt;= now). Returns count.</summary>
    Task<int> DeleteExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
