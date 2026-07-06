using System.Text.Json;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Ships.Dtos;
using EveUtils.Shared.Modules.Ships.Events;

namespace EveUtils.Shared.Modules.Ships;

/// <summary>Registers the Ships wire event so a ship added on one client syncs to the others (fleet).</summary>
public sealed class ShipsWireEvents : IWireEventCatalog
{
    public void RegisterInto(IEventTypeRegistry registry)
    {
        registry.Register("ships.added", (payloadJson, characterId) =>
        {
            var dto = JsonSerializer.Deserialize<ShipDto>(payloadJson)
                      ?? throw new InvalidOperationException("Invalid ships.added payload.");
            return new ShipAddedEvent(dto, characterId);
        });
    }
}
