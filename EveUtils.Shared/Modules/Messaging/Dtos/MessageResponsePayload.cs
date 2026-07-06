using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging.Dtos;

/// <summary>Result of responding to a message: which message, its kind and whether it was accepted.</summary>
public sealed record MessageResponsePayload(long MessageId, MessageKind Kind, bool Accepted);
