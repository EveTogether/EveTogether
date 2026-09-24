using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Transport.Implementations;

internal sealed class EfPendingServerRevokeStore(IDbContextFactory<SharedDbContext> contextFactory) : IPendingServerRevokeStore, ISingletonService
{
    public async Task QueueAsync(string serverAddress, int characterId, string accessToken, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alreadyQueued = await db.Set<PendingServerRevoke>()
            .AnyAsync(r => r.Address == serverAddress && r.AccessToken == accessToken, cancellationToken);
        if (alreadyQueued)
            return;

        db.Set<PendingServerRevoke>().Add(new PendingServerRevoke
        {
            Address = serverAddress,
            CharacterId = characterId,
            AccessToken = accessToken
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingServerRevoke>> ListAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<PendingServerRevoke>().AsNoTracking()
            .Where(r => r.Address == serverAddress)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<PendingServerRevoke>().Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
