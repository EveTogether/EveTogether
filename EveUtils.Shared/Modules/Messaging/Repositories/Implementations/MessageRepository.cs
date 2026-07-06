using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Messaging.Repositories.Implementations;

internal sealed class MessageRepository(IDbContextFactory<SharedDbContext> contextFactory) : IMessageRepository
{
    public async Task<long> AddAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<QueuedMessage>().Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return message.Id;
    }

    public async Task<QueuedMessage?> GetAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<QueuedMessage>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public async Task UpdateAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<QueuedMessage>().Update(message);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.Set<QueuedMessage>().FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null)
            return;
        db.Set<QueuedMessage>().Remove(message);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedMessage>> ListPendingForRecipientAsync(int recipientCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<QueuedMessage>()
            .Where(m => m.RecipientCharacterId == recipientCharacterId && m.Status == MessageStatus.Pending)
            .OrderBy(m => m.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // DateTimeOffset is stored as ISO-8601 text; all timestamps are UtcNow (offset +00:00) so the
        // lexicographic comparison the provider emits is chronologically correct.
        return await db.Set<QueuedMessage>()
            .Where(m => m.ExpiresAt <= asOf)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
