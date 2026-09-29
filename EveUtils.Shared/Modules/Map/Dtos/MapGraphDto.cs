namespace EveUtils.Shared.Modules.Map.Dtos;

/// <summary>
/// The k-space map of one SDE build (ET-392): every system with a 2D position, its constellation and region, and the
/// stargate connections between them. Immutable, built once per build off the UI thread and shared by every map view
/// and the route planner. Wormhole and abyssal space have no 2D position and no gates, so they are not in it.
/// </summary>
public sealed class MapGraphDto
{
    /// <summary>How many region colours <see cref="MapRegionDto.ColourIndex"/> spans.</summary>
    public const int RegionColourCount = 10;

    private readonly int[] _neighbourStart;
    private readonly int[] _neighbours;
    private readonly Dictionary<int, int> _indexBySystemId;
    private readonly Dictionary<string, int> _indexByName;

    internal MapGraphDto(
        long buildNumber, double width, double height,
        IReadOnlyList<MapSystemDto> systems, IReadOnlyList<MapConstellationDto> constellations,
        IReadOnlyList<MapRegionDto> regions, IReadOnlyList<MapJumpDto> jumps)
    {
        BuildNumber = buildNumber;
        Width = width;
        Height = height;
        Systems = systems;
        Constellations = constellations;
        Regions = regions;
        Jumps = jumps;

        _indexBySystemId = systems.ToDictionary(system => system.SolarSystemId, system => system.Index);
        _indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MapSystemDto system in systems)
            _indexByName.TryAdd(system.Name, system.Index);

        // Compressed adjacency: the neighbours of system i are _neighbours[_neighbourStart[i] .. _neighbourStart[i + 1]).
        var degree = new int[systems.Count + 1];
        foreach (MapJumpDto jump in jumps)
        {
            degree[jump.FromIndex + 1]++;
            degree[jump.ToIndex + 1]++;
        }
        for (int i = 1; i < degree.Length; i++)
            degree[i] += degree[i - 1];
        _neighbourStart = degree;
        _neighbours = new int[jumps.Count * 2];
        var fill = (int[])degree.Clone();
        foreach (MapJumpDto jump in jumps)
        {
            _neighbours[fill[jump.FromIndex]++] = jump.ToIndex;
            _neighbours[fill[jump.ToIndex]++] = jump.FromIndex;
        }
    }

    public long BuildNumber { get; }

    /// <summary>Extent of the map in world units; x runs 0..Width, y 0..Height.</summary>
    public double Width { get; }

    public double Height { get; }

    public IReadOnlyList<MapSystemDto> Systems { get; }

    public IReadOnlyList<MapConstellationDto> Constellations { get; }

    public IReadOnlyList<MapRegionDto> Regions { get; }

    public IReadOnlyList<MapJumpDto> Jumps { get; }

    public ReadOnlySpan<int> NeighboursOf(int systemIndex) =>
        _neighbours.AsSpan(_neighbourStart[systemIndex], _neighbourStart[systemIndex + 1] - _neighbourStart[systemIndex]);

    public bool TryGetIndex(int solarSystemId, out int systemIndex) =>
        _indexBySystemId.TryGetValue(solarSystemId, out systemIndex);

    /// <summary>The system whose name is exactly <paramref name="text"/> (case-insensitive), else the first one whose
    /// name starts with it; null for a blank text or no match.</summary>
    public MapSystemDto? FindByName(string? text)
    {
        string query = text?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return null;
        if (_indexByName.TryGetValue(query, out int exact))
            return Systems[exact];
        return Systems
            .Where(system => system.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
