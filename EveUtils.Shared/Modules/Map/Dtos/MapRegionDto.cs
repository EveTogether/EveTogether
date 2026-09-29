namespace EveUtils.Shared.Modules.Map.Dtos;

/// <param name="CenterX">Centroid of the region's systems, where its label goes.</param>
/// <param name="MinX">Bounding box of the region's systems, so a view can skip a region that is off screen.</param>
/// <param name="ColourIndex">0 up to <see cref="MapGraphDto.RegionColourCount"/>; neighbouring regions never share one.
/// It only tells neighbours apart and carries no faction meaning.</param>
/// <param name="HasGates">False for the Jove regions, which no stargate reaches.</param>
public sealed record MapRegionDto(
    int Index,
    int RegionId,
    string Name,
    string? FactionName,
    double CenterX,
    double CenterY,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    int ColourIndex,
    bool HasGates);
