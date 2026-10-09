using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Tells a character's connected fleet mates that it connected or went away (ET-492). The fleet screens read the
/// server's connection flag with the roster, and nothing else ever told them it changed: a mate who came online read
/// offline until the screen was reopened. Only roster co-members hear it — who is connected is nobody else's business.
///
/// <para>The transition is raised inside the attach or the keepalive sweep, so the push runs off it and never throws:
/// a push that fails is logged, and the mate's screen catches up on its next read.</para>
/// </summary>
public sealed class FleetMateConnectionRelay(
    ConnectedClients connectedClients,
    IServiceScopeFactory scopes,
    ILogger<FleetMateConnectionRelay> logger) : IHostedService, IDisposable
{
    private bool _started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connectedClients.CharacterConnectionChanged += _OnConnectionChanged;
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_started)
            return;
        connectedClients.CharacterConnectionChanged -= _OnConnectionChanged;
        _started = false;
    }

    private void _OnConnectionChanged(int characterId, bool isConnected) => _ = RelayAsync(characterId, isConnected);

    /// <summary>Sends the transition to the character's connected fleet mates. Internal so a test awaits it.</summary>
    internal async Task RelayAsync(int characterId, bool isConnected, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var mates = await scope.ServiceProvider.GetRequiredService<FleetBroadcastResolver>()
                .ConnectedFleetMatesAsync(characterId, cancellationToken);
            if (mates.Count == 0)
                return;

            var change = new FleetMateConnectionEvent(new FleetMateConnectionPayload(characterId, isConnected));
            await connectedClients.SendToCharactersAsync(mates, WireEnvelopeFactory.ToEnvelope(change), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Telling the fleet mates of character {CharacterId} that it {Change} failed.",
                characterId, isConnected ? "connected" : "disconnected");
        }
    }
}
