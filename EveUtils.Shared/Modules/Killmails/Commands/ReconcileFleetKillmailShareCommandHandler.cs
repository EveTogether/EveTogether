using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;

namespace EveUtils.Shared.Modules.Killmails.Commands;

internal sealed class ReconcileFleetKillmailShareCommandHandler(
    ILocalKillmailRepository repository, IDispatcher dispatcher, IEventBus eventBus)
    : ICommandHandler<ReconcileFleetKillmailShareCommand, Result>
{
    public async Task<Result> Handle(ReconcileFleetKillmailShareCommand command, CancellationToken cancellationToken = default)
    {
        (bool changed, IReadOnlyList<Guid> withdrawnFromRunIds) = await repository.ReconcileFleetShareAsync(
            command.CharacterId, command.ServerIdentity, command.FleetId, command.SharedKillmailIds, command.Fetched, cancellationToken);
        // A withdrawn loss leaves its run's stored totals behind otherwise, the same as an unlink (SetKillmailRunLink).
        foreach (Guid runId in withdrawnFromRunIds)
        {
            await dispatcher.Send(new RebuildActivitySummariesCommand(runId), cancellationToken);
        }

        if (changed)
        {
            await eventBus.PublishAsync(
                new KillmailsChangedEvent(command.CharacterId, KillmailsChangeKind.FleetShareChanged), EventTarget.Local, cancellationToken);
        }

        return Result.Success();
    }
}
