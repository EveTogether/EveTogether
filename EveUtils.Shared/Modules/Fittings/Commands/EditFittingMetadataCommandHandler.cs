using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Enums;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Commands;

internal sealed class EditFittingMetadataCommandHandler(IFittingRepository repository, IEventBus eventBus)
    : ICommandHandler<EditFittingMetadataCommand, Result>
{
    public async Task<Result> Handle(EditFittingMetadataCommand command, CancellationToken cancellationToken = default)
    {
        if (await repository.FindByIdAsync(command.FittingId, cancellationToken) is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "The fit is no longer in your library.", "Fittings"));

        await repository.UpdateMetadataAsync(command.FittingId, command.Name, command.Description, command.Tags, cancellationToken);
        await eventBus.PublishAsync(
            new FittingsChangedEvent(FittingsChangeKind.Edited, 1, command.FittingId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
