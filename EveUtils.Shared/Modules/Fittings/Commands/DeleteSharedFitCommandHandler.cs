using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Enums;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Commands;

internal sealed class DeleteSharedFitCommandHandler(ISharedFitRepository repository, IEventBus eventBus)
    : ICommandHandler<DeleteSharedFitCommand, Result>
{
    public async Task<Result> Handle(DeleteSharedFitCommand command, CancellationToken cancellationToken = default)
    {
        if (!await repository.RemoveAsync(command.SharedFitId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fit not found on the server.", "Fittings"));

        await eventBus.PublishAsync(
            new FittingsChangedEvent(FittingsChangeKind.SharedRemoved, 1, command.SharedFitId, command.ActingCharacterId),
            EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
