using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Client.Controls.Map;

/// <summary>
/// The static part of the map, built once per graph in world coordinates: per region its in-constellation and
/// cross-constellation jumps and its system dots, plus every cross-region jump. A frame draws these under a single
/// transform instead of rebuilding ~7k lines.
/// </summary>
internal sealed class MapGeometry
{
    // A dot is a tiny line with round caps: the pen's thickness is the dot's diameter, so all of a region's systems
    // draw in one call at any zoom.
    private const double DotLength = 0.001;

    public MapGeometry(MapGraphDto graph)
    {
        int regionCount = graph.Regions.Count;
        var inConstellation = new StreamGeometry[regionCount];
        var crossConstellation = new StreamGeometry[regionCount];
        var dots = new StreamGeometry[regionCount];

        ILookup<int, MapJumpDto> byRegion = graph.Jumps
            .Where(jump => jump.Kind != MapJumpKind.CrossRegion)
            .ToLookup(jump => graph.Systems[jump.FromIndex].RegionIndex);
        ILookup<int, MapSystemDto> systemsByRegion = graph.Systems.ToLookup(system => system.RegionIndex);
        for (int region = 0; region < regionCount; region++)
        {
            inConstellation[region] = _Lines(graph, byRegion[region].Where(jump => jump.Kind == MapJumpKind.InConstellation));
            crossConstellation[region] = _Lines(graph, byRegion[region].Where(jump => jump.Kind == MapJumpKind.CrossConstellation));
            dots[region] = _Dots(systemsByRegion[region]);
        }
        InConstellation = inConstellation;
        CrossConstellation = crossConstellation;
        Dots = dots;
        CrossRegion = _Lines(graph, graph.Jumps.Where(jump => jump.Kind == MapJumpKind.CrossRegion));
    }

    public IReadOnlyList<StreamGeometry> InConstellation { get; }

    public IReadOnlyList<StreamGeometry> CrossConstellation { get; }

    public IReadOnlyList<StreamGeometry> Dots { get; }

    public StreamGeometry CrossRegion { get; }

    private static StreamGeometry _Lines(MapGraphDto graph, IEnumerable<MapJumpDto> jumps)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        foreach (MapJumpDto jump in jumps)
        {
            MapSystemDto from = graph.Systems[jump.FromIndex], to = graph.Systems[jump.ToIndex];
            context.BeginFigure(new Point(from.X, from.Y), false);
            context.LineTo(new Point(to.X, to.Y));
            context.EndFigure(false);
        }
        return geometry;
    }

    private static StreamGeometry _Dots(IEnumerable<MapSystemDto> systems)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        foreach (MapSystemDto system in systems)
        {
            context.BeginFigure(new Point(system.X, system.Y), false);
            context.LineTo(new Point(system.X + DotLength, system.Y));
            context.EndFigure(false);
        }
        return geometry;
    }
}
