using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Enums;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Commands;

internal sealed class DownloadSharedFitCommandHandler(IFittingRepository repository, IEventBus eventBus)
    : ICommandHandler<DownloadSharedFitCommand, Result<int>>
{
    public async Task<Result<int>> Handle(DownloadSharedFitCommand command, CancellationToken cancellationToken = default)
    {
        var fit = command.Fit;
        var contentHash = FitContentHash.Compute(fit.RawJson);
        var duplicate = await repository.FindByContentHashAsync(contentHash, cancellationToken);
        if (duplicate is not null)
            return Result<int>.Success(0, new ResultMessage(MessageSeverity.Info, MessageCodes.Duplicate,
                $"Already have '{fit.Name}' locally as '{duplicate.Name}' — not downloaded again.", "Fittings"));

        var stored = new LocalFitting
        {
            OwnerId = fit.SharedByCharacterName, // display source
            EsiFittingId = fit.EsiFittingId,
            Name = fit.Name,
            ShipTypeId = fit.ShipTypeId,
            RawJson = fit.RawJson,
            ContentHash = contentHash,
            ImportedAt = DateTimeOffset.UtcNow
        };
        await repository.UpsertAsync(stored, cancellationToken);
        await eventBus.PublishAsync(new FittingsChangedEvent(FittingsChangeKind.Imported, 1), EventTarget.Local, cancellationToken);
        return Result<int>.Success(1);
    }
}
