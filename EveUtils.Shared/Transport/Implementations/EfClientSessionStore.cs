using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Transport.Implementations;

/// <summary>
/// SQLite-backed session store — replaces the former JSON-file <c>FileClientSessionStore</c>.
/// Sessions are kept per (server, character) in the <see cref="ClientServerSession"/> table so multiple
/// characters can be paired to the same server. POC note: the server session token is short-lived
/// and stored as plaintext (parity with the former JSON file).
///
/// Server identity is the TLS cert fingerprint, not the address: the same server reached via
/// different address spellings (<c>eve-together.com:7443</c> vs <c>www.…</c> vs a trailing slash vs an IP) shares one
/// pinned fingerprint and must be treated as ONE server. So every lookup resolves across all addresses that share the
/// given address's fingerprint, and <see cref="ListServersAsync"/> returns one canonical address per fingerprint — so
/// a character coupled under one spelling is found when the fleet was loaded under another. An address with no pinned
/// fingerprint (not yet paired) is its own identity, so behaviour is unchanged until a fingerprint exists.
/// </summary>
internal sealed class EfClientSessionStore(IDbContextFactory<SharedDbContext> contextFactory, IServerTrustStore trust) : IClientSessionStore, ISingletonService
{
    public async Task SaveAsync(string serverAddress, ClientSessionTokens tokens, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await IdentityAddressesAsync(db, serverAddress, cancellationToken);
        // One session per (identity, character): update the existing row wherever in the identity it physically lives
        // (e.g. an earlier coupling under another spelling), so a token refresh never spawns a duplicate.
        var row = await db.Set<ClientServerSession>()
            .FirstOrDefaultAsync(s => identity.Contains(s.Address) && s.CharacterId == tokens.CharacterId, cancellationToken);
        var savedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (row is null)
        {
            db.Set<ClientServerSession>().Add(new ClientServerSession
            {
                Address = Canonical(identity),
                CharacterId = tokens.CharacterId,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                CharacterName = tokens.CharacterName,
                SavedAtUnixMs = savedAt
            });
        }
        else
        {
            row.AccessToken = tokens.AccessToken;
            row.RefreshToken = tokens.RefreshToken;
            row.CharacterName = tokens.CharacterName;
            row.SavedAtUnixMs = savedAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ClientSessionTokens?> LoadAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await IdentityAddressesAsync(db, serverAddress, cancellationToken);
        // Any session is a valid bus credential; return the most recently saved one for this server identity.
        var row = await db.Set<ClientServerSession>().AsNoTracking()
            .Where(s => identity.Contains(s.Address))
            .OrderByDescending(s => s.SavedAtUnixMs)
            .FirstOrDefaultAsync(cancellationToken);
        return Map(row);
    }

    public async Task<ClientSessionTokens?> LoadForCharacterAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await IdentityAddressesAsync(db, serverAddress, cancellationToken);
        var row = await db.Set<ClientServerSession>().AsNoTracking()
            .Where(s => identity.Contains(s.Address) && s.CharacterId == characterId)
            .OrderByDescending(s => s.SavedAtUnixMs)
            .FirstOrDefaultAsync(cancellationToken);
        return Map(row);
    }

    public async Task<IReadOnlyList<ClientSessionTokens>> LoadAllAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await IdentityAddressesAsync(db, serverAddress, cancellationToken);
        var rows = await db.Set<ClientServerSession>().AsNoTracking()
            .Where(s => identity.Contains(s.Address))
            .ToListAsync(cancellationToken);
        // One per character across the identity (a character coupled under two spellings → keep the most recent).
        return rows
            .GroupBy(r => r.CharacterId)
            .Select(g => g.OrderByDescending(r => r.SavedAtUnixMs).First())
            .Select(Map).Where(t => t is not null).Cast<ClientSessionTokens>().ToList();
    }

    public async Task RemoveAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await IdentityAddressesAsync(db, serverAddress, cancellationToken);
        await db.Set<ClientServerSession>()
            .Where(s => identity.Contains(s.Address) && s.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListServersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var addresses = await db.Set<ClientServerSession>().AsNoTracking()
            .Select(s => s.Address).Distinct().ToListAsync(cancellationToken);
        // One canonical address per fingerprint identity, so two spellings of the same server are a single entry.
        return addresses.GroupBy(IdentityKey).Select(Canonical).ToList();
    }

    public async Task<IReadOnlyList<string>> ListServersForCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var addresses = await db.Set<ClientServerSession>().AsNoTracking()
            .Where(s => s.CharacterId == characterId)
            .Select(s => s.Address).Distinct().ToListAsync(cancellationToken);
        return addresses.GroupBy(IdentityKey).Select(Canonical).ToList();
    }

    // The addresses that share serverAddress's fingerprint identity (serverAddress included even with no sessions yet).
    private async Task<List<string>> IdentityAddressesAsync(SharedDbContext db, string serverAddress, CancellationToken cancellationToken)
    {
        var key = IdentityKey(serverAddress);
        var addresses = await db.Set<ClientServerSession>().AsNoTracking()
            .Select(s => s.Address).Distinct().ToListAsync(cancellationToken);
        var identity = addresses.Where(a => IdentityKey(a) == key).ToList();
        if (!identity.Contains(serverAddress))
            identity.Add(serverAddress);
        return identity;
    }

    // The identity key: the pinned cert fingerprint, or the raw address (own identity) until one is pinned. The
    // "addr:" prefix keeps an un-pinned address from colliding with a fingerprint value.
    private string IdentityKey(string address) => trust.GetFingerprint(address) is { } fingerprint ? "fp:" + fingerprint : "addr:" + address;

    // A deterministic representative address for an identity group (the smallest), so the canonical is stable.
    private static string Canonical(IEnumerable<string> identity) => identity.OrderBy(a => a, StringComparer.Ordinal).First();
    private static string Canonical(IGrouping<string, string> group) => Canonical((IEnumerable<string>)group);

    private static ClientSessionTokens? Map(ClientServerSession? row) =>
        row is null ? null : new ClientSessionTokens(row.AccessToken, row.RefreshToken, row.CharacterName, row.CharacterId);
}
