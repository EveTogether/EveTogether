using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging.Repositories;

/// <summary>
/// Client-local durable inbox: the persisted copy of delivered messages (mail + open invites). The
/// server queue is transient and now delivers each message exactly once (mail is dropped, an invite is marked
/// Delivered after its first push) — so this store is the authoritative copy the inbox window reads across restarts.
/// </summary>
public interface IClientInboxStore
{
    /// <summary>Insert or update by (recipient, server-message id) so a re-delivered invite stays one row. Returns
    /// true when the row was newly inserted, false when it updated an existing one — lets a caller distinguish a
    /// first delivery from a reconnect replay (the same delivery event re-fires on attach).</summary>
    Task<bool> UpsertAsync(ClientInboxMessage message, CancellationToken cancellationToken = default);

    Task<ClientInboxMessage?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>A character's inbox, newest first.</summary>
    Task<IReadOnlyList<ClientInboxMessage>> ListForRecipientAsync(int recipientCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Every local character's inbox, newest first (the combined inbox view).</summary>
    Task<IReadOnlyList<ClientInboxMessage>> ListAllAsync(CancellationToken cancellationToken = default);

    Task MarkReadAsync(long id, CancellationToken cancellationToken = default);

    Task SetStatusAsync(long id, MessageStatus status, CancellationToken cancellationToken = default);

    /// <summary>Permanently remove a single message from the local inbox.</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Clear the whole local inbox. The clear sticks: the server delivers each message once (an invite is
    /// already marked Delivered after its first push), so a cleared message is not re-pushed on the next reconnect.
    /// An unanswered invite still exists server-side until answered or it expires.</summary>
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}
