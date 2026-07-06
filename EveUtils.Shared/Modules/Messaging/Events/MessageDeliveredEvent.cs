using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Dtos;

namespace EveUtils.Shared.Modules.Messaging.Events;

/// <summary>
/// Pushed to a single character when a queued message is delivered to them. Targeted: the
/// server reroutes it only to the recipient's connections. The durable <c>QueuedMessage</c> (server) and
/// <c>ClientInboxMessage</c> (client) are the stores; this event is the live delivery, re-sent on attach for
/// an still-open invite.
/// </summary>
public sealed class MessageDeliveredEvent(MessageDeliveredPayload data, int? characterId = null)
    : IntegrationEvent<MessageDeliveredPayload>(data, characterId), ITargetedEvent, IServerSourcedEvent
{
    public override string EventType => "message.delivered";

    public int TargetCharacterId => Data.RecipientCharacterId;

    /// <summary>The server this delivery was received from — stamped by the client receive loop, not sent
    /// on the wire, so the inbox can answer on the originating server. Null until stamped.</summary>
    public string? SourceServerAddress { get; set; }
}
