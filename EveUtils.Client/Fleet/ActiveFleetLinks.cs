using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Fleet;

/// <summary>Identifies one fleet across every place a fleet can live: a server address, or null for a client-only fleet.</summary>
public readonly record struct FleetKey(string? ServerAddress, long FleetId);

/// <summary>One started fleet as the link rule sees it: when it started and who is on its roster.</summary>
public sealed record ActiveFleetRoster(FleetKey Key, DateTimeOffset? ActivatedAt, IReadOnlyCollection<int> MemberCharacterIds);

/// <summary>
/// Which started fleet a character is linked to, when they are on the roster of more than one (ET-170). A character
/// counts for at most one active fleet, and the one that wins is the one that was started first — the same
/// activated-first tiebreak the server's <c>FleetBroadcastResolver</c> applies on <c>Fleet.ActivatedAt</c>, so what
/// this screen calls "linked" is what the server actually publishes for. Applied over everything this client can see,
/// local fleets included, because a pilot who flies a local fleet and a server fleet at once is the very situation
/// the overview has to make visible rather than hide.
///
/// A character on the roster of an active fleet they are not linked to is "elsewhere active" there. Never
/// "offline": that word means not logged in and nothing else in this client.
/// </summary>
public sealed class ActiveFleetLinks
{
    private readonly Dictionary<int, FleetKey> _linkedFleet = [];
    private readonly Dictionary<int, List<FleetKey>> _activeFleetsOf = [];

    public ActiveFleetLinks(IEnumerable<ActiveFleetRoster> activeFleets)
    {
        // Earliest activation first; a fleet that was never stamped sorts last, and the id keeps the order stable.
        foreach (var fleet in activeFleets
                     .OrderBy(f => f.ActivatedAt ?? DateTimeOffset.MaxValue)
                     .ThenBy(f => f.Key.FleetId))
        {
            foreach (int characterId in fleet.MemberCharacterIds)
            {
                _linkedFleet.TryAdd(characterId, fleet.Key);
                if (!_activeFleetsOf.TryGetValue(characterId, out var list))
                    _activeFleetsOf[characterId] = list = [];
                list.Add(fleet.Key);
            }
        }
    }

    public static ActiveFleetLinks Empty { get; } = new([]);

    /// <summary>The one active fleet this character counts for, or null when they are in none.</summary>
    public FleetKey? LinkedFleetOf(int characterId) =>
        _linkedFleet.TryGetValue(characterId, out var key) ? key : null;

    /// <summary>True when the character is on this active fleet's roster and it is the one they count for.</summary>
    public bool IsLinked(FleetKey fleet, int characterId) =>
        _linkedFleet.TryGetValue(characterId, out var key) && key == fleet;

    /// <summary>True when the character is on this active fleet's roster but counts for an earlier-started one.</summary>
    public bool IsElsewhereActive(FleetKey fleet, int characterId) =>
        _activeFleetsOf.TryGetValue(characterId, out var list) && list.Contains(fleet) && !IsLinked(fleet, characterId);

    /// <summary>Every started fleet the character is rostered in, the linked one included, earliest first.</summary>
    public IReadOnlyList<FleetKey> ActiveFleetsOf(int characterId) =>
        _activeFleetsOf.TryGetValue(characterId, out var list) ? list : [];
}
