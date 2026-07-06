using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// Pushed by the server to a fleet's members when its lifecycle or roster changes (activation, conclusion, a
/// join/leave/move), so their open fleet list, roster window and metrics participation refresh live instead of only
/// on a reconnect/restart. Fleet-scoped (the envelope carries the fleet id) and server-sourced (the client receive
/// loop stamps the originating server, since fleet ids are per-server and must be matched together with it).
/// </summary>
public sealed class FleetChangedEvent(FleetChangePayload data, int? characterId = null)
    : IntegrationEvent<FleetChangePayload>(data, characterId), IFleetScopedEvent, IServerSourcedEvent
{
    public override string EventType => "fleet.changed";

    public long FleetId => Data.FleetId;

    public string? SourceServerAddress { get; set; }
}
