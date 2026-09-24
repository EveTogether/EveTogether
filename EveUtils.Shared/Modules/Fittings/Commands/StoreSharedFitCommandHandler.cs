using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Enums;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Commands;

internal sealed class StoreSharedFitCommandHandler(ISharedFitRepository repository, IEventBus eventBus)
    : ICommandHandler<StoreSharedFitCommand, Result<int>>
{
    public async Task<Result<int>> Handle(StoreSharedFitCommand command, CancellationToken cancellationToken = default)
    {
        var fit = new SharedFit
        {
            EsiFittingId = command.Fit.EsiFittingId,
            Name = command.Fit.Name,
            ShipTypeId = command.Fit.ShipTypeId,
            RawJson = command.Fit.RawJson,
            SharedByCharacterName = command.Fit.SharedByCharacterName,
            SharedByCharacterId = command.SharedByCharacterId,
            SharedAt = DateTimeOffset.UtcNow
        };
        var match = await repository.AddOrMatchAsync(fit, cancellationToken);
        if (match is not null)
            return Result<int>.Success(0, new ResultMessage(MessageSeverity.Info, MessageCodes.Duplicate,
                $"Already shared as '{match.Name}' — not added again (same fit).", "Fittings"));

        await eventBus.PublishAsync(
            new FittingsChangedEvent(FittingsChangeKind.Shared, 1, fit.Id, command.SharedByCharacterId),
            EventTarget.Local, cancellationToken);
        return Result<int>.Success(1);
    }
}
