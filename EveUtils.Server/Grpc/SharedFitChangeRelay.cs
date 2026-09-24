using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Enums;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Server.Grpc;

/// <summary>
/// The server's relay for the shared fit library (ET-383). <c>StoreSharedFitCommand</c> and
/// <c>DeleteSharedFitCommand</c> publish <see cref="FittingsChangedEvent"/> on the local bus once their write is done;
/// this turns it into the <c>fittings.shared</c> / <c>fittings.deleted</c> push every connection listens for (ET-20),
/// the sharer's and deleter's own included, by the echo rule. So a delete from the control panel reaches the clients
/// exactly like one from a client.
///
/// <para>A bus subscriber runs inside the publishing command, after its commit, so this never throws: a push that
/// fails is logged, and the change stays saved.</para>
/// </summary>
public sealed class SharedFitChangeRelay(
    IEventBus eventBus,
    IServiceScopeFactory scopes,
    ConnectedClients connectedClients,
    ILogger<SharedFitChangeRelay> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = eventBus.Subscribe<FittingsChangedEvent>(_RelayAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private async Task _RelayAsync(FittingsChangedEvent change, CancellationToken cancellationToken)
    {
        if (change.Data.FitId is not { } fitId)
            return;

        try
        {
            IIntegrationEvent? push = change.Data.Kind switch
            {
                FittingsChangeKind.Shared => await _SharedAsync(fitId, cancellationToken),
                FittingsChangeKind.SharedRemoved => new FitDeletedEvent(new FitDeletedPayload(fitId), change.CharacterId),
                _ => null
            };
            if (push is not null)
                await connectedClients.BroadcastExceptAsync("", WireEnvelopeFactory.ToEnvelope(push), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Pushing the {Kind} change of shared fit {FitId} failed; fit lists see it on their next read.",
                change.Data.Kind, fitId);
        }
    }

    private async Task<FitSharedEvent?> _SharedAsync(int fitId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var fit = await scope.ServiceProvider.GetRequiredService<ISharedFitReader>().GetAsync(fitId, cancellationToken);
        return fit is null
            ? null
            : new FitSharedEvent(
                new FitSharedPayload(fit.EsiFittingId, fit.Name, fit.ShipTypeId, fit.RawJson, fit.SharedByCharacterName),
                fit.SharedByCharacterId);
    }
}
