using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Events;
using Microsoft.AspNetCore.SignalR;

namespace EveUtils.Server.Stream;

/// <summary>
/// Bridges the server's local event bus to the SignalR hub: every <see cref="CombatLoggedEvent"/>
/// that arrives (over the remote gRPC bus from a client) is pushed to all browser clients on the
/// DPS stream page. This is the "(b) to the web UI" branch of the server reroute.
/// </summary>
public sealed class DpsBroadcastBridge(IEventBus eventBus, IHubContext<DpsHub> hub) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = eventBus.Subscribe<CombatLoggedEvent>((evt, ct) =>
            hub.Clients.All.SendAsync("dps", new
            {
                characterId = evt.Data.CharacterId,
                characterName = evt.Data.CharacterName,
                dealt = evt.Data.DealtPerSecond,
                received = evt.Data.ReceivedPerSecond,
                at = evt.Data.At
            }, ct));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
