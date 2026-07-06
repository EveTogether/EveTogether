namespace EveUtils.Shared.Modules.Messaging.Entities;

/// <summary>
/// A durable, server-side delivery row in the internal message queue. The server holds a message for
/// up to ~30 days (<see cref="ExpiresAt"/>) and delivers it on (re)connect. Mail is fire-and-forget — deleted
/// once delivered; an invite is marked <see cref="MessageStatus.Delivered"/> after its first push, so the invitee
/// receives it exactly once (it stays answerable until <see cref="MessageStatus.Responded"/> or the expiry sweep
/// removes it). The fleet domain keeps its own canonical <c>FleetInvite</c>
/// (roster/status); this row is the transport + inbox envelope, linked to it via <see cref="RefId"/>.
/// </summary>
public sealed class QueuedMessage
{
    public long Id { get; set; }

    public int RecipientCharacterId { get; set; }

    /// <summary>Sender character; <c>null</c> for a system/server-originated message.</summary>
    public int? SenderCharacterId { get; set; }

    public MessageKind Kind { get; set; }

    /// <summary>Correlation to the domain entity a wrapping kind refers to (e.g. the <c>FleetInvite</c> id);
    /// <c>null</c> for plain mail.</summary>
    public long? RefId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    /// <summary>Typed payload per kind, serialized (e.g. the fleet-invite payload); <c>null</c> for plain mail.</summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Retention cap (CreatedAt + 30d); the cleanup sweep removes rows past this moment.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public MessageStatus Status { get; set; }
}
