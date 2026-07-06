using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Messaging.Repositories.Implementations;

/// <summary>
/// SQLite-backed client inbox. Mirrors the client-store pattern (factory per call, no shared
/// context). Upsert is keyed on (recipient, server-message id) so a re-delivered invite refreshes its row;
/// <see cref="ClientInboxMessage.ReceivedAt"/> and <see cref="ClientInboxMessage.IsRead"/> are preserved on
/// re-delivery so the user's read state survives a reconnect.
/// </summary>
internal sealed class EfClientInboxStore(IDbContextFactory<SharedDbContext> contextFactory) : IClientInboxStore, ISingletonService
{
    public async Task<bool> UpsertAsync(ClientInboxMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<ClientInboxMessage>()
            .FirstOrDefaultAsync(m => m.RecipientCharacterId == message.RecipientCharacterId
                                      && m.ServerMessageId == message.ServerMessageId, cancellationToken);
        var inserted = existing is null;
        if (existing is null)
        {
            db.Set<ClientInboxMessage>().Add(message);
        }
        else
        {
            existing.Kind = message.Kind;
            existing.ServerAddress = message.ServerAddress; // refresh the origin (e.g. backfills a pre-column row on re-delivery)
            existing.RefId = message.RefId;
            existing.SenderCharacterId = message.SenderCharacterId;
            existing.Title = message.Title;
            existing.Body = message.Body;
            existing.PayloadJson = message.PayloadJson;
            existing.CreatedAt = message.CreatedAt;
            existing.Status = message.Status; // ReceivedAt + IsRead intentionally kept from the first delivery.
        }

        await db.SaveChangesAsync(cancellationToken);
        return inserted;
    }

    public async Task<ClientInboxMessage?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ClientInboxMessage>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<ClientInboxMessage>> ListForRecipientAsync(int recipientCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Order by the local autoincrement Id (newest first) — SQLite cannot ORDER BY a DateTimeOffset.
        return await db.Set<ClientInboxMessage>().AsNoTracking()
            .Where(m => m.RecipientCharacterId == recipientCharacterId)
            .OrderByDescending(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClientInboxMessage>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ClientInboxMessage>().AsNoTracking()
            .OrderByDescending(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkReadAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<ClientInboxMessage>().Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true), cancellationToken);
    }

    public async Task SetStatusAsync(long id, MessageStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<ClientInboxMessage>().Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, status), cancellationToken);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<ClientInboxMessage>().Where(m => m.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<ClientInboxMessage>().ExecuteDeleteAsync(cancellationToken);
    }
}
