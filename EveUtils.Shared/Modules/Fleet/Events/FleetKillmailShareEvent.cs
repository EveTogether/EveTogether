using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// One pilot's complete current killmail share for a fleet. The server relays it without storing it and includes the
/// sender so every client observes the same server-authorized stream.
/// </summary>
[RequiresPermission(FleetPermissions.Metrics)]
public sealed class FleetKillmailShareEvent(
    FleetKillmailShare data,
    int? characterId = null,
    string? serverAddress = null)
    : IntegrationEvent<FleetKillmailShare>(data, characterId), IFleetScopedEvent, IEchoToSenderEvent,
        IRemoteServerTargetedEvent
{
    public override string EventType => "fleet.killmail-share";

    public long FleetId => Data.FleetId;

    public string ServerAddress { get; } = serverAddress ?? "";
}
