using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Events;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

internal sealed class WithdrawRunOutcomesCommandHandler(IRunOutcomeRepository repository, IEventBus eventBus)
    : ICommandHandler<WithdrawRunOutcomesCommand, Result<int>>
{
    public async Task<Result<int>> Handle(WithdrawRunOutcomesCommand command, CancellationToken cancellationToken = default)
    {
        int removed = await repository.RemoveAllOfAsync(command.ContributorBucket, cancellationToken);
        if (removed > 0)
            await eventBus.PublishAsync(new RunOutcomesChangedEvent(RunOutcomesChangeKind.Withdrawn, removed),
                EventTarget.Local, cancellationToken);

        return Result<int>.Success(removed);
    }
}
