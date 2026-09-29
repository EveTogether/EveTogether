using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Map.Graph;

/// <summary>Turns the SDE's map snapshot into the <see cref="MapGraphDto"/> the map draws and routes on.</summary>
internal static class MapGraphBuilder
{
    /// <summary>World width the raw SDE coordinates (about 1e18 across) are scaled to; height follows the aspect.</summary>
    public const double WorldWidth = 10000;

    // Regions whose label centres are closer than this also count as neighbours for colouring, so two regions that
    // touch on the map without a gate between them still get different colours.
    private const double NearRegionDistance = 1100;

    public static MapGraphDto? Build(SdeMapSnapshot snapshot, long buildNumber, Func<int, string?> factionName)
    {
        List<SdeMapSystem> placed = snapshot.Systems
            .Where(system => system is { X2d: not null, Y2d: not null })
            .OrderBy(system => system.SolarSystemId)
            .ToList();
        if (placed.Count == 0)
            return null;

        double minX = placed.Min(system => system.X2d ?? 0), maxX = placed.Max(system => system.X2d ?? 0);
        double minY = placed.Min(system => system.Y2d ?? 0), maxY = placed.Max(system => system.Y2d ?? 0);
        double scale = WorldWidth / Math.Max(maxX - minX, 1);

        List<int> regionIds = placed.Select(system => system.RegionId).Distinct().Order().ToList();
        List<int> constellationIds = placed.Select(system => system.ConstellationId).Distinct().Order().ToList();
        Dictionary<int, int> regionIndex = regionIds.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        Dictionary<int, int> constellationIndex = constellationIds.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);

        var systems = new List<MapSystemDto>(placed.Count);
        foreach (SdeMapSystem system in placed)
        {
            double display = MapSecurity.Display(system.SecurityStatus);
            systems.Add(new MapSystemDto(
                systems.Count, system.SolarSystemId, system.Name, system.SecurityStatus, display, MapSecurity.Band(display),
                ((system.X2d ?? 0) - minX) * scale, ((system.Y2d ?? 0) - minY) * scale,
                constellationIndex[system.ConstellationId], regionIndex[system.RegionId]));
        }

        Dictionary<int, int> systemIndex = systems.ToDictionary(system => system.SolarSystemId, system => system.Index);
        var jumps = new List<MapJumpDto>(snapshot.Jumps.Count);
        foreach (SdeMapJump jump in snapshot.Jumps)
        {
            if (!systemIndex.TryGetValue(jump.FromSystemId, out int from) || !systemIndex.TryGetValue(jump.ToSystemId, out int to))
                continue;
            jumps.Add(new MapJumpDto(from, to, _KindOf(systems[from], systems[to])));
        }

        List<MapConstellationDto> constellations = _Constellations(snapshot, constellationIds, systems);
        List<MapRegionDto> regions = _Regions(snapshot, regionIds, systems, jumps, factionName);
        return new MapGraphDto(buildNumber, WorldWidth, (maxY - minY) * scale, systems, constellations, regions, jumps);
    }

    private static MapJumpKind _KindOf(MapSystemDto from, MapSystemDto to)
    {
        if (from.RegionIndex != to.RegionIndex)
            return MapJumpKind.CrossRegion;
        return from.ConstellationIndex != to.ConstellationIndex ? MapJumpKind.CrossConstellation : MapJumpKind.InConstellation;
    }

    private static List<MapConstellationDto> _Constellations(SdeMapSnapshot snapshot, List<int> constellationIds, List<MapSystemDto> systems)
    {
        Dictionary<int, SdeMapConstellation> byId = snapshot.Constellations.ToDictionary(constellation => constellation.ConstellationId);
        ILookup<int, MapSystemDto> members = systems.ToLookup(system => system.ConstellationIndex);
        return constellationIds.Select((id, index) =>
        {
            List<MapSystemDto> inside = members[index].ToList();
            string name = byId.GetValueOrDefault(id)?.Name ?? id.ToString();
            return new MapConstellationDto(index, id, name, inside[0].RegionIndex,
                inside.Average(system => system.X), inside.Average(system => system.Y));
        }).ToList();
    }

    private static List<MapRegionDto> _Regions(
        SdeMapSnapshot snapshot, List<int> regionIds, List<MapSystemDto> systems, List<MapJumpDto> jumps, Func<int, string?> factionName)
    {
        Dictionary<int, SdeMapRegion> byId = snapshot.Regions.ToDictionary(region => region.RegionId);
        ILookup<int, MapSystemDto> members = systems.ToLookup(system => system.RegionIndex);
        var gated = new HashSet<int>(jumps.SelectMany(jump => new[] { systems[jump.FromIndex].RegionIndex, systems[jump.ToIndex].RegionIndex }));
        int[] colours = _ColourRegions(regionIds.Count, members, systems, jumps);

        return regionIds.Select((id, index) =>
        {
            List<MapSystemDto> inside = members[index].ToList();
            SdeMapRegion? region = byId.GetValueOrDefault(id);
            string? faction = region?.FactionId is { } factionId ? factionName(factionId) : null;
            return new MapRegionDto(
                index, id, region?.Name ?? id.ToString(), faction,
                inside.Average(system => system.X), inside.Average(system => system.Y),
                inside.Min(system => system.X), inside.Min(system => system.Y),
                inside.Max(system => system.X), inside.Max(system => system.Y),
                colours[index], gated.Contains(index));
        }).ToList();
    }

    // Greedy graph colouring, busiest region first: each takes a colour none of its already-coloured neighbours has,
    // and among those the one used least so far, so the palette is spread evenly.
    private static int[] _ColourRegions(int regionCount, ILookup<int, MapSystemDto> members, List<MapSystemDto> systems, List<MapJumpDto> jumps)
    {
        var neighbours = Enumerable.Range(0, regionCount).Select(_ => new HashSet<int>()).ToArray();
        foreach (MapJumpDto jump in jumps.Where(jump => jump.Kind == MapJumpKind.CrossRegion))
        {
            int a = systems[jump.FromIndex].RegionIndex, b = systems[jump.ToIndex].RegionIndex;
            neighbours[a].Add(b);
            neighbours[b].Add(a);
        }

        (double X, double Y)[] centres = Enumerable.Range(0, regionCount)
            .Select(index => (members[index].Average(system => system.X), members[index].Average(system => system.Y)))
            .ToArray();
        for (int a = 0; a < regionCount; a++)
        for (int b = 0; b < regionCount; b++)
        {
            double dx = centres[a].X - centres[b].X, dy = centres[a].Y - centres[b].Y;
            if (a != b && dx * dx + dy * dy < NearRegionDistance * NearRegionDistance)
                neighbours[a].Add(b);
        }

        var colours = Enumerable.Repeat(-1, regionCount).ToArray();
        var used = new int[MapGraphDto.RegionColourCount];
        foreach (int region in Enumerable.Range(0, regionCount).OrderByDescending(index => neighbours[index].Count))
        {
            var taken = neighbours[region].Where(other => colours[other] >= 0).Select(other => colours[other]).ToHashSet();
            List<int> free = Enumerable.Range(0, MapGraphDto.RegionColourCount).Where(colour => !taken.Contains(colour)).ToList();
            if (free.Count == 0)
                free = Enumerable.Range(0, MapGraphDto.RegionColourCount).ToList();
            int pick = free.MinBy(colour => used[colour]);
            colours[region] = pick;
            used[pick]++;
        }
        return colours;
    }
}
