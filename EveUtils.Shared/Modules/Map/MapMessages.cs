using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Map;

internal static class MapMessages
{
    private const string Source = "Map";

    public static readonly ResultMessage NoMapData = new(MessageSeverity.Error, MessageCodes.SdeOutdated,
        "The map needs the latest static data. It appears once the SDE download has finished.", Source);

    public static readonly ResultMessage RouteNotFound = new(MessageSeverity.Warning, MessageCodes.RouteNotFound,
        "No stargate route with these settings: wormhole-only space, Pochven, or avoided space in between.", Source);

    public static ResultMessage UnknownSystem(int solarSystemId) => new(MessageSeverity.Error, MessageCodes.NotFound,
        $"System {solarSystemId} is not on the map.", Source);
}
