using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Enums;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Commands;

internal sealed class DeleteLocalFittingCommandHandler(IFittingRepository repository, IEventBus eventBus)
    : ICommandHandler<DeleteLocalFittingCommand, Result>
{
    public async Task<Result> Handle(DeleteLocalFittingCommand command, CancellationToken cancellationToken = default)
    {
        if (await repository.FindByIdAsync(command.FittingId, cancellationToken) is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "The fit is no longer in your library.", "Fittings"));

        await repository.RemoveByIdAsync(command.FittingId, cancellationToken);
        await eventBus.PublishAsync(
            new FittingsChangedEvent(FittingsChangeKind.Removed, 1, command.FittingId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
