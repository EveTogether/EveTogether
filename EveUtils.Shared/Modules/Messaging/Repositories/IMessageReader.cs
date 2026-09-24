using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging.Repositories;

/// <summary>The read half of <see cref="IMessageRepository"/> (ET-383): what a type outside the messaging command
/// handlers takes, so enqueueing and answering stay with the handlers that signal them.</summary>
public interface IMessageReader
{
    Task<QueuedMessage?> GetAsync(long messageId, CancellationToken cancellationToken = default);

    /// <summary>A recipient's still-pending messages, for the on-connect delivery sweep (oldest first).</summary>
    Task<IReadOnlyList<QueuedMessage>> ListPendingForRecipientAsync(int recipientCharacterId, CancellationToken cancellationToken = default);
}
