using System.Text.Json;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Commands;

// No [RequiresPermission] — import is a local ESI call gated only by the ESI scope check.
internal sealed class ImportFittingsFromEsiCommandHandler(
    IFittingRepository repository) : ICommandHandler<ImportFittingsFromEsiCommand, Result<int>>
{
    public async Task<Result<int>> Handle(
        ImportFittingsFromEsiCommand command,
        CancellationToken cancellationToken = default)
    {
        var selected = command.SelectedFittingIds is { Count: > 0 }
            ? new HashSet<int>(command.SelectedFittingIds)
            : null; // null = import all

        var toImport = selected is null
            ? command.EsiFittings
            : command.EsiFittings.Where(f => selected.Contains(f.FittingId)).ToList();

        var ownerId = command.CharacterId.ToString();
        var imported = 0;
        var messages = new List<ResultMessage>();
        foreach (var fit in toImport)
        {
            var rawJson = JsonSerializer.Serialize(fit);
            var contentHash = FitContentHash.Compute(rawJson);

            // Content-hash dedup (2026-06-04, owner-agnostic): if the same fit already exists locally, skip it and
            // report which fit it matched rather than storing a duplicate.
            var duplicate = await repository.FindByContentHashAsync(contentHash, cancellationToken);
            if (duplicate is not null)
            {
                messages.Add(new ResultMessage(MessageSeverity.Info, MessageCodes.Duplicate,
                    $"Skipped '{fit.Name}' — same fit already in your library as '{duplicate.Name}'.", "Fittings"));
                continue;
            }

            await repository.UpsertAsync(new LocalFitting
            {
                OwnerId      = ownerId,
                EsiFittingId = fit.FittingId,
                Name         = fit.Name,
                ShipTypeId   = fit.ShipTypeId,
                RawJson      = rawJson,
                ContentHash  = contentHash,
                ImportedAt   = DateTimeOffset.UtcNow
            }, cancellationToken);
            imported++;
        }

        return Result<int>.Success(imported, messages.ToArray());
    }
}
