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
    Task RecordRefreshFailureAsync(int esiCharacterId, DateTimeOffset failedAt, int failureCount, CancellationToken cancellationToken = default);

    Task AddSessionAsync(ServerSession session, CancellationToken cancellationToken = default);
    Task<ServerSession?> FindSessionByAccessHashAsync(string accessHash, CancellationToken cancellationToken = default);
    Task<ServerSession?> FindSessionByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken = default);
    /// <summary>
    /// The session with this id, whatever token pair it currently holds. The id is the only thing about a session
    /// that survives a rotation, so it is what separates "this row was deleted" from "your copy of its token is
    /// stale" — which the token itself cannot tell apart (ET-123).
    /// </summary>
    Task<ServerSession?> FindSessionByIdAsync(int sessionId, CancellationToken cancellationToken = default);
    Task TouchHeartbeatAsync(string accessHash, DateTimeOffset at, CancellationToken cancellationToken = default);
    /// <summary>
    /// Rotates the session to a new token pair, but only while its refresh hash is still
    /// <paramref name="expectedRefreshHash"/>. Returns false when another rotation got there first, so two
    /// overlapping refreshes can never both succeed and invalidate each other's tokens.
    /// </summary>
    Task<bool> RotateSessionAsync(int sessionId, string expectedRefreshHash, string newAccessHash, string newRefreshHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset refreshExpiresAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ServerSession>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Deletes every session that can no longer become a working credential: past its hard refresh window
    /// (RefreshExpiresAt &lt;= <paramref name="now"/>), or with no sign of life since
    /// <paramref name="idleSince"/>. Returns the number removed.
    /// </summary>
    Task<int> DeleteLapsedSessionsAsync(DateTimeOffset now, DateTimeOffset idleSince, CancellationToken cancellationToken = default);
}
