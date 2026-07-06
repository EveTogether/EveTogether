using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging.Queries;

/// <summary>A recipient's still-pending queued messages, for the on-connect delivery sweep.</summary>
public sealed record ListPendingMessagesQuery(int RecipientCharacterId) : IQuery<IReadOnlyList<QueuedMessage>>;
