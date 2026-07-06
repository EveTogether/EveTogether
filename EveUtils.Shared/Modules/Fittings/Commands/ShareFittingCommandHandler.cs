using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Commands;

[RequiresPermission(FittingsPermissions.Sync)]
internal sealed class ShareFittingCommandHandler(
    IFittingRepository repository,
    IEventBus eventBus) : ICommandHandler<ShareFittingCommand, Result>
{
    public async Task<Result> Handle(ShareFittingCommand command, CancellationToken cancellationToken = default)
    {
        // Find by DB id regardless of owner (fits are portable).
        var local = await repository.FindByIdAsync(command.LocalFittingId, cancellationToken);

        if (local is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, $"Fitting {command.LocalFittingId} not found.", "Fittings"));

        var payload = new FitSharedPayload(
            local.EsiFittingId,
            local.Name,
            local.ShipTypeId,
            local.RawJson,
            command.OwnerCharacterName);

        await eventBus.PublishAsync(
            new FitSharedEvent(payload, command.OwnerCharacterId),
            EventTarget.Both,
            cancellationToken);

        return Result.Success();
    }
}
