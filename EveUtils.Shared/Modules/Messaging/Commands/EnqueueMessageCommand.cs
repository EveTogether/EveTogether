using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging.Commands;

/// <summary>
/// Enqueues a message for a recipient. Server-side primitive used by the mail path and by the
/// fleet-invite refactor (the invite enqueues a <see cref="MessageKind.FleetInvite"/> envelope linked
/// to its durable invite via <see cref="RefId"/>). The handler stamps <c>CreatedAt</c>/<c>ExpiresAt</c> (+30d)
/// and persists a Pending row; the transport then pushes a targeted delivery. Returns the new message id.
/// </summary>
public sealed record EnqueueMessageCommand(
    int RecipientCharacterId,
    int? SenderCharacterId,
    MessageKind Kind,
    string Title,
    string? Body,
    string? PayloadJson,
    long? RefId) : ICommand<Result<long>>;
