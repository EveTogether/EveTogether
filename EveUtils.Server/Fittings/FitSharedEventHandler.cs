using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Fittings;

/// <summary>
/// Saves an incoming <see cref="FitSharedEvent"/> to the <see cref="SharedFit"/> table.
/// The <c>fit.sync</c> permission is enforced upstream by the event-bus gate in
/// <c>EventBusStreamService</c>, which blocks both delivery here AND reroute to other clients.
/// </summary>
public sealed class FitSharedEventHandler(
    ISharedFitRepository repository,
    ILogger<FitSharedEventHandler> logger) : IScopedService
{
    public async Task HandleAsync(FitSharedEvent evt, CancellationToken cancellationToken = default)
    {
        var payload = evt.Data;
        var match = await repository.AddOrMatchAsync(new SharedFit
        {
            EsiFittingId = payload.EsiFittingId,
            Name = payload.Name,
            ShipTypeId = payload.ShipTypeId,
            RawJson = payload.RawJson,
            SharedByCharacterName = payload.SharedByCharacterName,
            SharedByCharacterId = evt.CharacterId ?? 0,
            SharedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        if (match is not null)
            logger.LogInformation(
                "Skipped duplicate shared fit '{Name}' from {Character}: same content as already-shared '{Existing}' (id {Id}).",
                payload.Name, payload.SharedByCharacterName, match.Name, match.Id);
        else
            logger.LogInformation(
                "Stored shared fit '{Name}' from {Character} (ESI id {EsiId}).",
                payload.Name, payload.SharedByCharacterName, payload.EsiFittingId);
    }
}
