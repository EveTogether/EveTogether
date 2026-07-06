using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;

/// <summary>Server-only data access. Loaded by the server context, so these tables live in the server DB.</summary>
internal sealed class ServerAuthRepository(IDbContextFactory<SharedDbContext> contextFactory) : IServerAuthRepository
{
    public async Task<AllowedCharacter?> FindAllowedAsync(int? esiCharacterId, string characterName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Match in the DB (lower() for the case-insensitive name compare) instead of materializing the whole
        // allow-list and filtering client-side — the latter is an unbounded memory/DoS vector on the pairing path.
        var name = characterName.ToLower();
        return await db.Set<AllowedCharacter>().AsNoTracking()
            .FirstOrDefaultAsync(a =>
                (esiCharacterId != null && a.EsiCharacterId == esiCharacterId)
                || a.CharacterName.ToLower() == name,
                cancellationToken);
    }

    public async Task<IReadOnlyList<AllowedCharacter>> ListAllowedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<AllowedCharacter>().AsNoTracking().OrderBy(a => a.CharacterName).ToListAsync(cancellationToken);
    }

    public async Task<int> AddAllowedAsync(AllowedCharacter allowed, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<AllowedCharacter>().Add(allowed);
        await db.SaveChangesAsync(cancellationToken);
        return allowed.Id;
    }

    public async Task RemoveAllowedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Set<AllowedCharacter>().FindAsync([id], cancellationToken);
        if (entity is not null)
        {
            db.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task EnsureAllowedSeedAsync(IEnumerable<string> characterNames, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<AllowedCharacter>();
        var existing = await set.AsNoTracking().Select(a => a.CharacterName).ToListAsync(cancellationToken);
        var known = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var added = false;
        foreach (var name in characterNames.Where(n => !string.IsNullOrWhiteSpace(n) && !known.Contains(n)))
        {
            set.Add(new AllowedCharacter { CharacterName = name, Note = "seed" });
            added = true;
        }

        if (added)
            await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SyncedCharacter> UpsertSyncedAsync(int esiCharacterId, string characterName, EncryptedToken refreshToken, IReadOnlyList<string>? grantedScopes = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<SyncedCharacter>();
        var entity = await set.FirstOrDefaultAsync(c => c.EsiCharacterId == esiCharacterId, cancellationToken);
        if (entity is null)
        {
            entity = new SyncedCharacter { EsiCharacterId = esiCharacterId, PairedAt = DateTimeOffset.UtcNow };
            set.Add(entity);
        }

        entity.CharacterName = characterName;
        entity.RefreshTokenCipher = refreshToken.Cipher;
        entity.RefreshTokenNonce = refreshToken.Nonce;
        entity.RefreshTokenTag = refreshToken.Tag;
        entity.LastRefreshedAt = DateTimeOffset.UtcNow;

        if (grantedScopes is not null)
            entity.GrantedScopes = grantedScopes;

        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<IReadOnlyList<SyncedCharacter>> ListSyncedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<SyncedCharacter>().AsNoTracking().OrderBy(c => c.CharacterName).ToListAsync(cancellationToken);
    }

    public async Task AddSessionAsync(ServerSession session, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<ServerSession>().Add(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ServerSession?> FindSessionByAccessHashAsync(string accessHash, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ServerSession>().AsNoTracking().Include(s => s.SyncedCharacter)
            .FirstOrDefaultAsync(s => s.AccessTokenHash == accessHash, cancellationToken);
    }

    public async Task<ServerSession?> FindSessionByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ServerSession>().AsNoTracking().Include(s => s.SyncedCharacter)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshHash, cancellationToken);
    }

    public async Task TouchHeartbeatAsync(string accessHash, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Set<ServerSession>().FirstOrDefaultAsync(s => s.AccessTokenHash == accessHash, cancellationToken);
        if (session is null)
            return;
        session.LastHeartbeat = at;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RotateSessionAsync(int sessionId, string newAccessHash, string newRefreshHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset refreshExpiresAt, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Set<ServerSession>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
            return false;
        session.AccessTokenHash = newAccessHash;
        session.RefreshTokenHash = newRefreshHash;
        session.IssuedAt = issuedAt;
        session.ExpiresAt = expiresAt;
        session.RefreshExpiresAt = refreshExpiresAt;
        session.LastHeartbeat = issuedAt;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ServerSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ServerSession>().AsNoTracking().Include(s => s.SyncedCharacter)
            .OrderByDescending(s => s.Id).ToListAsync(cancellationToken);
    }

    public async Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Set<ServerSession>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is not null)
        {
            db.Remove(session);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> DeleteExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // SQLite can't compare DateTimeOffset in SQL → filter client-side on materialized rows.
        // Purge on the hard refresh window, not the 1h access expiry, so a still-refreshable
        // session survives an overnight gap.
        var all = await db.Set<ServerSession>().ToListAsync(cancellationToken);
        var expired = all.Where(s => s.RefreshExpiresAt <= now).ToList();
        if (expired.Count == 0) return 0;
        db.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }
}
