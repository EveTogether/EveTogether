using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;

namespace EveUtils.Shared.Modules.Killmails.Commands;

internal sealed class ReconcileFleetKillmailShareCommandHandler(ILocalKillmailRepository repository, IEventBus eventBus)
    : ICommandHandler<ReconcileFleetKillmailShareCommand, Result>
{
    public async Task<Result> Handle(ReconcileFleetKillmailShareCommand command, CancellationToken cancellationToken = default)
    {
        bool changed = await repository.ReconcileFleetShareAsync(
            command.CharacterId, command.FleetId, command.SharedKillmailIds, command.Fetched, cancellationToken);
        if (changed)
        {
            await eventBus.PublishAsync(
                new KillmailsChangedEvent(command.CharacterId, KillmailsChangeKind.FleetShareChanged), EventTarget.Local, cancellationToken);
        }

        return Result.Success();
    }
}
