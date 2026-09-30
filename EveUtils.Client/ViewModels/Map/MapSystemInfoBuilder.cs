using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Controls.Map;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>Assembles a system's hover popover (ET-399) from the map's own snapshot: the graph, your characters' markers,
/// the fleet badges and the planned route. Pure — the jump counts come in already worked out.</summary>
internal static class MapSystemInfoBuilder
{
    public static MapSystemInfo? Build(
        MapGraphDto graph, int systemIndex, IReadOnlyList<MapMarker> markers, IReadOnlyList<MapFleetBadge> badges, RouteDto? route,
        IReadOnlyList<MapSystemDistance> distances)
    {
        if (systemIndex < 0 || systemIndex >= graph.Systems.Count)
            return null;

        MapSystemDto system = graph.Systems[systemIndex];
        MapRegionDto region = graph.Regions[system.RegionIndex];
        int step = route is null ? -1 : route.Steps.ToList().FindIndex(candidate => candidate.SystemIndex == systemIndex);
        return new MapSystemInfo(
            system.Name,
            system.DisplaySecurity,
            graph.Constellations[system.ConstellationIndex].Name,
            region.Name,
            region.ColourIndex % MapPalette.Regions.Count,
            region.FactionName,
            distances.Where(distance => distance.Jumps != 0).ToList(),
            step >= 0 ? step + 1 : null,
            route?.Steps.Count ?? 0,
            _Occupants(systemIndex, markers, badges),
            [.. graph.NeighboursOf(systemIndex).ToArray().Select(neighbour => graph.Systems[neighbour].Name).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    // The commander first, then your own characters, then the rest of the fleet freshest first. A character of yours in the
    // fleet is one entry; one that only the game log placed has no age to show.
    private static List<MapSystemOccupant> _Occupants(int systemIndex, IReadOnlyList<MapMarker> markers, IReadOnlyList<MapFleetBadge> badges)
    {
        HashSet<string> own = [.. markers.Where(marker => marker.SystemIndex == systemIndex).Select(marker => marker.Label)];
        IReadOnlyList<MapFleetSighting> fleet = badges.FirstOrDefault(badge => badge.SystemIndex == systemIndex)?.Members ?? [];

        IEnumerable<MapSystemOccupant> fromFleet = fleet.Select(member => new MapSystemOccupant(member.Name,
            member.IsCommander ? MapOccupantKind.Commander : own.Contains(member.Name) ? MapOccupantKind.OwnCharacter : MapOccupantKind.FleetMember,
            member.ObservedAt));
        IEnumerable<MapSystemOccupant> ownOnly = own
            .Where(name => fleet.All(member => member.Name != name))
            .Select(name => new MapSystemOccupant(name, MapOccupantKind.OwnCharacter, null));

        return
        [
            .. fromFleet.Concat(ownOnly)
                .OrderBy(occupant => occupant.Kind)
                .ThenByDescending(occupant => occupant.ObservedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(occupant => occupant.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
