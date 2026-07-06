using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging.Repositories;

/// <summary>
/// Server-side persistence for the internal message queue. The queue is a transient delivery buffer:
/// a recipient's <see cref="MessageStatus.Pending"/> rows are pushed on connect, after which mail is deleted
/// while an invite is marked <see cref="MessageStatus.Delivered"/> (delivered once, no longer re-pushed). The
/// retention sweep removes rows past <see cref="QueuedMessage.ExpiresAt"/>.
/// </summary>
public interface IMessageRepository
{
    Task<long> AddAsync(QueuedMessage message, CancellationToken cancellationToken = default);

    Task<QueuedMessage?> GetAsync(long messageId, CancellationToken cancellationToken = default);

    Task UpdateAsync(QueuedMessage message, CancellationToken cancellationToken = default);

    Task DeleteAsync(long messageId, CancellationToken cancellationToken = default);

    /// <summary>A recipient's still-pending messages, for the on-connect delivery sweep (oldest first).</summary>
    Task<IReadOnlyList<QueuedMessage>> ListPendingForRecipientAsync(int recipientCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Deletes messages past their retention cap; returns the number removed.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default);
}
