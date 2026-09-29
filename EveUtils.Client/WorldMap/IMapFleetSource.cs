using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EveUtils.Client.WorldMap;

/// <summary>The fleets the map can follow and who is in them (ET-394).</summary>
public interface IMapFleetSource
{
    /// <summary>The active fleets one of your own characters is in right now.</summary>
    IReadOnlyList<MapFleetChoice> ActiveFleets();

    /// <summary>Everyone in the fleet: its roster plus, on the in-game boss's client, the in-game fleet — pilots who do not
    /// use EVE Together included. Empty when the roster cannot be read right now.</summary>
    Task<IReadOnlyCollection<int>> MembersOfAsync(MapFleetChoice fleet);

    /// <summary>A character's name — your own registry first, then public ESI; null when neither knows it.</summary>
    Task<string?> NameOfAsync(int characterId);

    /// <summary>Calls <paramref name="rosterChanged"/> with the fleet id, on the UI thread, whenever a fleet's roster moves.
    /// Dispose to stop.</summary>
    IDisposable WatchRosters(Action<long> rosterChanged);
}
