namespace EveUtils.Shared.Messaging;

/// <summary>
/// Marker for an event scoped to a fleet rather than a single character or a broadcast. The
/// transport copies <see cref="FleetId"/> onto the wire envelope's <c>fleet_id</c>; the server then reroutes
/// the event only to that fleet's <em>active participants</em>. Events that implement neither this nor
/// <see cref="ITargetedEvent"/> keep the existing broadcast behavior.
/// </summary>
public interface IFleetScopedEvent : IIntegrationEvent
{
    /// <summary>Id of the fleet whose active participants should receive this event. Must be non-zero.</summary>
    long FleetId { get; }
}
