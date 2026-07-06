using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging.Dtos;

/// <summary>
/// Wire payload of a delivered message, pushed to the recipient (live on enqueue and again on
/// reconnect for an open invite). Carries everything the client needs to persist it into its local inbox
/// without a second lookup; <see cref="ServerMessageId"/> is the response target and the dedupe key.
/// </summary>
public sealed record MessageDeliveredPayload(
    long ServerMessageId,
    int RecipientCharacterId,
    int? SenderCharacterId,
    MessageKind Kind,
    long? RefId,
    string Title,
    string? Body,
    string? PayloadJson,
    DateTimeOffset CreatedAt);
