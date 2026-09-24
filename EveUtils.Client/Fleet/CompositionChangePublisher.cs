using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Announces on the in-process bus that a composition this client changed, so every open compositions window
/// refreshes. Both composition paths end here — the local library (<see cref="ClientFleetService"/>) and a server's
/// (<see cref="Transport.FleetClient"/>) — so a window subscribes once and hears about either. A change made on a
/// server is additionally pushed by the server to every other client (the acting one is left out, it has already
/// published here).
/// </summary>
public sealed class CompositionChangePublisher(IEventBus bus) : ISingletonService
{
    /// <param name="serverAddress">The server the composition lives on; null for the client-only library.</param>
    public Task PublishAsync(
        long compositionId, CompositionChangeKind kind, string? serverAddress, CancellationToken cancellationToken = default) =>
        bus.PublishAsync(
            new CompositionChangedEvent(new CompositionChangePayload(compositionId, kind, IsClientOnly: serverAddress is null))
            {
                SourceServerAddress = serverAddress
            },
            cancellationToken: cancellationToken);
}
