namespace EveUtils.Shared.Modules.Messaging.Entities;

/// <summary>
/// A client-local, durable copy of a delivered message — the client side of the queue. When the
/// server delivers a <see cref="QueuedMessage"/>, the client persists it here so mail and open invites
/// survive restarts even after the server drops its transient row (mail is deleted on delivery; an invite
/// only re-delivers until answered). The server's row id is kept in <see cref="ServerMessageId"/> for the
/// response round-trip and as the dedupe key for re-delivery. Client-only — applied by the ClientDbContext.
/// </summary>
public sealed class ClientInboxMessage
{
    public long Id { get; set; }

    /// <summary>The originating server's <see cref="QueuedMessage.Id"/> — the response target and the dedupe
    /// key for a re-delivered invite.</summary>
    public long ServerMessageId { get; set; }

    public int RecipientCharacterId { get; set; }

    /// <summary>The server this message was delivered from — the response target, so a reply goes to the
    /// originating server rather than the first coupled one. Null for rows stored before this column existed.</summary>
    public string? ServerAddress { get; set; }

    public int? SenderCharacterId { get; set; }

    public MessageKind Kind { get; set; }

    public long? RefId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string? PayloadJson { get; set; }

    /// <summary>When the server created the message.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this client received and stored it.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public MessageStatus Status { get; set; }

    public bool IsRead { get; set; }
}
