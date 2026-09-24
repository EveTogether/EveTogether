using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;

namespace EveUtils.Shared.Modules.Killmails.Commands;

internal sealed class StoreKillmailsCommandHandler(ILocalKillmailRepository repository, IEventBus eventBus)
    : ICommandHandler<StoreKillmailsCommand, Result>
{
    public async Task<Result> Handle(StoreKillmailsCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Killmails.Count == 0)
        {
            return Result.Success();
        }

        await repository.AddMissingAsync(command.CharacterId, command.Killmails, cancellationToken);
        await eventBus.PublishAsync(
            new KillmailsChangedEvent(command.CharacterId, KillmailsChangeKind.Imported), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
