using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Events;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

internal sealed class PruneRunOutcomesCommandHandler(IRunOutcomeRepository repository, IEventBus eventBus)
    : ICommandHandler<PruneRunOutcomesCommand, Result<int>>
{
    public async Task<Result<int>> Handle(PruneRunOutcomesCommand command, CancellationToken cancellationToken = default)
    {
        int removed = await repository.RemoveCompletedBeforeAsync(command.CompletedBefore, cancellationToken);
        if (removed > 0)
            await eventBus.PublishAsync(new RunOutcomesChangedEvent(RunOutcomesChangeKind.Pruned, removed),
                EventTarget.Local, cancellationToken);

        return Result<int>.Success(removed);
    }
}
