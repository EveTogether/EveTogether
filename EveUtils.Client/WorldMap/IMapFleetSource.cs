using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.WorldMap;

/// <summary>The fleets the map can follow and who is in them (ET-394).</summary>
public interface IMapFleetSource
{
    /// <summary>The active fleets one of your own characters is in right now, as the participation set last saw them.</summary>
    IReadOnlyList<MapFleetChoice> ActiveFleets();

    /// <summary>Asks the servers and the local store which fleets your characters are in and updates what
    /// <see cref="ActiveFleets"/> answers with — a fleet that started, ended or was joined since the set was last written.</summary>
    Task RefreshActiveFleetsAsync();

    /// <summary>The fleet commander the roster names, or null when it could not be read.</summary>
    int? CommanderOf(MapFleetChoice fleet);

    /// <summary>Everyone in the fleet: its roster plus, on the in-game boss's client, the in-game fleet — pilots who do not
    /// use EVE Together included. Empty when the roster cannot be read right now.</summary>
    Task<IReadOnlyCollection<int>> MembersOfAsync(MapFleetChoice fleet);

    /// <summary>A character's name — your own registry first, then public ESI; null when neither knows it.</summary>
    Task<string?> NameOfAsync(int characterId);

    /// <summary>Calls <paramref name="rosterChanged"/> with each change to a fleet's roster, on the UI thread — a fleet
    /// starting, ending or gaining or losing a member arrives here as <see cref="FleetRosterChangeKind.RosterReloaded"/>
    /// from the fleet module's change signal. Dispose to stop.</summary>
    IDisposable WatchRosters(Action<FleetRosterChange> rosterChanged);
}
