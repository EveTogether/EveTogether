using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Messaging.Events;

/// <summary>
/// Raised on the LOCAL bus (server host) the moment a queued message is persisted, carrying the recipient. The
/// server subscribes and live-delivers that recipient's pending messages, so every enqueue path — fleet
/// start/conclude, invites, the responders — pushes live without each call site having to remember it. The durable
/// queue + on-connect sweep stay the offline fallback. Local-only: never registered as a wire event.
/// </summary>
public sealed class MessageEnqueuedEvent(int recipientCharacterId)
    : IntegrationEvent<int>(recipientCharacterId)
{
    public int RecipientCharacterId => Data;
}
