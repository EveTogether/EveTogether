using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Events;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

internal sealed class RetractRunOutcomeCommandHandler(IRunOutcomeRepository repository, IEventBus eventBus)
    : ICommandHandler<RetractRunOutcomeCommand, Result<RunOutcomeShareStatus>>
{
    public async Task<Result<RunOutcomeShareStatus>> Handle(RetractRunOutcomeCommand command,
        CancellationToken cancellationToken = default)
    {
        (RunOutcomeShareStatus status, long? outcomeId) = await repository.RemoveAsync(command.ActivityId,
            command.ContributorBucket, command.Revision, cancellationToken);
        if (status is RunOutcomeShareStatus.Removed)
            await eventBus.PublishAsync(new RunOutcomesChangedEvent(RunOutcomesChangeKind.Removed, 1, outcomeId),
                EventTarget.Local, cancellationToken);

        return Result<RunOutcomeShareStatus>.Success(status);
    }
}
