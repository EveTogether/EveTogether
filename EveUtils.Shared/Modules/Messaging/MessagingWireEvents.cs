using System.Text.Json;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Messaging.Dtos;
using EveUtils.Shared.Modules.Messaging.Events;

namespace EveUtils.Shared.Modules.Messaging;

/// <summary>
/// Registers the Messaging wire events so message deliveries travel over the remote bus. Registered
/// on both hosts: the server pushes the targeted <c>message.delivered</c> event, the client deserializes the
/// ones aimed at it and writes them into its local inbox.
/// </summary>
public sealed class MessagingWireEvents : IWireEventCatalog
{
    public void RegisterInto(IEventTypeRegistry registry)
    {
        registry.Register("message.delivered", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<MessageDeliveredPayload>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid message.delivered payload.");
            return new MessageDeliveredEvent(payload, characterId);
        });
    }
}
