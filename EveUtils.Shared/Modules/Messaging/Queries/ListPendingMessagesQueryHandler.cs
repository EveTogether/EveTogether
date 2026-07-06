using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;

namespace EveUtils.Shared.Modules.Messaging.Queries;

internal sealed class ListPendingMessagesQueryHandler(IMessageRepository repository)
    : IQueryHandler<ListPendingMessagesQuery, IReadOnlyList<QueuedMessage>>
{
    public Task<IReadOnlyList<QueuedMessage>> Handle(ListPendingMessagesQuery query, CancellationToken cancellationToken = default)
        => repository.ListPendingForRecipientAsync(query.RecipientCharacterId, cancellationToken);
}
