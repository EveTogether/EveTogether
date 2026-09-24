using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Server.Grpc;

/// <summary>
/// The server's relay for the composition signal (ET-381). Every composition handler publishes
/// <see cref="CompositionChangedEvent"/> on the local bus once its write is done; this pushes it to every connected
/// character. The shared library is one server-wide list that everyone connected sees (<c>ListAllFleetCompositions</c>
/// has no visibility filter), so that is the audience — the acting character included, by the echo rule: their client
/// does not publish a change it made here, it hears it back like everyone else.
///
/// <para>A bus subscriber runs inside the publishing command, after its commit, so this never throws: a push that
/// fails is logged, and the change stays saved.</para>
/// </summary>
public sealed class CompositionChangeRelay(
    IEventBus eventBus,
    ConnectedClients connectedClients,
    ILogger<CompositionChangeRelay> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = eventBus.Subscribe<CompositionChangedEvent>(_RelayAsync);
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

    private async Task _RelayAsync(CompositionChangedEvent change, CancellationToken cancellationToken)
    {
        if (change.Data.IsClientOnly)
            return;

        try
        {
            var everyone = connectedClients.ConnectedCharacters().Select(c => c.CharacterId);
            await connectedClients.SendToCharactersAsync(everyone, WireEnvelopeFactory.ToEnvelope(change), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Pushing the {Kind} change of composition {CompositionId} failed; its viewers see it on their next read.",
                change.Data.Kind, change.Data.CompositionId);
        }
    }
}
