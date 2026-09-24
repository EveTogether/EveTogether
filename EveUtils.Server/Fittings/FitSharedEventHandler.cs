using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Events;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Fittings;

/// <summary>
/// Saves an incoming <see cref="FitSharedEvent"/> to the <see cref="SharedFit"/> table through
/// <see cref="StoreSharedFitCommand"/>, the same command the gRPC share uses (ET-383).
/// The <c>fit.sync</c> permission is enforced upstream by the event-bus gate in
/// <c>EventBusStreamService</c>, which blocks both delivery here AND reroute to other clients.
/// </summary>
public sealed class FitSharedEventHandler(
    IDispatcher dispatcher,
    ILogger<FitSharedEventHandler> logger) : IScopedService
{
    public async Task HandleAsync(FitSharedEvent evt, CancellationToken cancellationToken = default)
    {
        var payload = evt.Data;
        var stored = await dispatcher.Send(new StoreSharedFitCommand(payload, evt.CharacterId ?? 0), cancellationToken);
        if (!stored.IsSuccess)
            logger.LogError("Storing shared fit '{Name}' from {Character} failed: {Reason}",
                payload.Name, payload.SharedByCharacterName, stored.Messages.FirstOrDefault()?.Text);
        else if (stored.Value == 0)
            logger.LogInformation("Skipped duplicate shared fit '{Name}' from {Character}: {Reason}",
                payload.Name, payload.SharedByCharacterName, stored.Messages.FirstOrDefault()?.Text);
        else
            logger.LogInformation(
                "Stored shared fit '{Name}' from {Character} (ESI id {EsiId}).",
                payload.Name, payload.SharedByCharacterName, payload.EsiFittingId);
    }
}
