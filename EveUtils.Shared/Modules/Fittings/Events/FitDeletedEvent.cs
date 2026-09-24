using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Events;

/// <summary>
/// A shared fit was removed from a server's library. Pushed by the server to every connected client so open fit lists
/// drop it live. Server-sourced: fit ids are per-server, so a receiver matches the server it came from.
/// </summary>
public sealed class FitDeletedEvent(FitDeletedPayload data, int? characterId = null)
    : IntegrationEvent<FitDeletedPayload>(data, characterId), IServerSourcedEvent
{
    public override string EventType => "fittings.deleted";

    public string? SourceServerAddress { get; set; }
}
