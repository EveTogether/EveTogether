namespace EveUtils.Shared.Messaging;

/// <summary>
/// Seam for the external server↔client bus. NOT implemented yet — deliberately a separate layer.
/// Once registered, <see cref="InProcessEventBus"/> routes events with <see cref="EventTarget.Remote"/>
/// here.
///
/// Intended role per host:
/// <list type="bullet">
///   <item>Client: always sends the event to the server.</item>
///   <item>Server: receives it, publishes it locally (<see cref="EventTarget.Local"/>) so server
///     handlers process it, and reroutes it to all connected clients.</item>
///   <item>Client: receives a server event and publishes it locally — subscribers cannot tell a
///     local event from a remote one (same <see cref="IIntegrationEvent"/>).</item>
/// </list>
///
/// Authentication is mandatory: the transport must only connect/subscribe over an authenticated,
/// authorized session (per the EVE-Utils auth flow). An unauthenticated peer must not be able to
/// attach to the external bus, nor publish to or receive from it.
/// </summary>
public interface IRemoteEventTransport
{
    Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
