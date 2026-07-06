using System.Text.Json;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Gamelog.Dtos;
using EveUtils.Shared.Modules.Gamelog.Events;

namespace EveUtils.Shared.Modules.Gamelog;

/// <summary>Registers the Gamelog module's wire events (the live DPS stream) for the remote bus.</summary>
public sealed class GamelogWireEvents : IWireEventCatalog
{
    public void RegisterInto(IEventTypeRegistry registry)
    {
        registry.Register("gamelog.combat", (payloadJson, characterId) =>
        {
            var dto = JsonSerializer.Deserialize<DpsSampleDto>(payloadJson)
                      ?? throw new InvalidOperationException("Invalid gamelog.combat payload.");
            return new CombatLoggedEvent(dto, characterId);
        });
    }
}
