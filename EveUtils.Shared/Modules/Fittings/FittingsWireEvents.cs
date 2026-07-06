using System.Text.Json;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fittings.Events;

namespace EveUtils.Shared.Modules.Fittings;

/// <summary>Registers the Fittings wire events so shared fits propagate over the remote bus.</summary>
public sealed class FittingsWireEvents : IWireEventCatalog
{
    public void RegisterInto(IEventTypeRegistry registry)
    {
        registry.Register("fittings.shared", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<FitSharedPayload>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fittings.shared payload.");
            return new FitSharedEvent(payload, characterId);
        });
    }
}
