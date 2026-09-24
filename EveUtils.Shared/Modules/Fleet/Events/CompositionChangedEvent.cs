using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// A composition was created, edited or deleted. Published in-process by the client that made the change (local and
/// client-only compositions included), and pushed by the server to every other connected character for a shared one,
/// so open compositions lists and editors refresh live. Server-sourced: composition ids are per-server, so a receiver
/// matches the id together with the server it came from (null for a change made on this client).
/// </summary>
public sealed class CompositionChangedEvent(CompositionChangePayload data, int? characterId = null)
    : IntegrationEvent<CompositionChangePayload>(data, characterId), IServerSourcedEvent
{
    public override string EventType => "composition.changed";

    public string? SourceServerAddress { get; set; }
}
