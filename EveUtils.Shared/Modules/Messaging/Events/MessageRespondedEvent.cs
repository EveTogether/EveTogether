using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Dtos;

namespace EveUtils.Shared.Modules.Messaging.Events;

/// <summary>
/// A queued message was answered (ET-382), raised on the server's local bus once the responder and the status update
/// are done. Whatever the answer changed in another module signals for itself; this one says the message left the
/// recipient's open list, for a relay that wants to tell the recipient's other connections. Local only, no relay yet.
/// </summary>
public sealed class MessageRespondedEvent(MessageResponsePayload data)
    : IntegrationEvent<MessageResponsePayload>(data)
{
    public override string EventType => "message.responded";
}
