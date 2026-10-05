using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;

namespace EveUtils.Shared.Modules.Killmails.Commands;

internal sealed class RemoveMatchingProvisionalKillmailCommandHandler(IProvisionalKillmailRepository repository, IEventBus eventBus)
    : ICommandHandler<RemoveMatchingProvisionalKillmailCommand, Result>
{
    public async Task<Result> Handle(RemoveMatchingProvisionalKillmailCommand command, CancellationToken cancellationToken = default)
    {
        bool removed = await repository.RemoveMatchingAsync(
            command.CharacterId, command.KillmailTimeUtc, command.VictimShipTypeId, command.VictimName, cancellationToken);
        if (removed)
        {
            await eventBus.PublishAsync(
                new KillmailsChangedEvent(command.CharacterId, KillmailsChangeKind.ProvisionalChanged), EventTarget.Local, cancellationToken);
        }

        return Result.Success();
    }
}
