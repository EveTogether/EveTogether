using System.Text.Json;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Events;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>
/// The Runs wire events. Registered on the client only: the server builds <c>runs.group-updated</c> itself and never
/// has to read one, so a client sending it in is dropped as an unknown type instead of being rerouted to everybody.
/// </summary>
public sealed class RunsWireEvents : IWireEventCatalog
{
    public void RegisterInto(IEventTypeRegistry registry)
    {
        registry.Register("runs.group-updated", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupUpdate>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid runs.group-updated payload.");
            return new RunGroupUpdatedEvent(payload, characterId);
        });
    }
}
