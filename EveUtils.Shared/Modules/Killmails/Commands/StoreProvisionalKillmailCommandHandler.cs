using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;

namespace EveUtils.Shared.Modules.Killmails.Commands;

internal sealed class StoreProvisionalKillmailCommandHandler(IProvisionalKillmailRepository repository, IEventBus eventBus)
    : ICommandHandler<StoreProvisionalKillmailCommand, Result>
{
    public async Task<Result> Handle(StoreProvisionalKillmailCommand command, CancellationToken cancellationToken = default)
    {
        await repository.AddAsync(command.Killmail, cancellationToken);
        await eventBus.PublishAsync(
            new KillmailsChangedEvent(command.CharacterId, KillmailsChangeKind.ProvisionalChanged), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
