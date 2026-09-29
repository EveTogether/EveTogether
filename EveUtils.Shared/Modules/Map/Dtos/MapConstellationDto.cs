namespace EveUtils.Shared.Modules.Map.Dtos;

/// <param name="CenterX">Centroid of the constellation's systems, where its label goes.</param>
public sealed record MapConstellationDto(int Index, int ConstellationId, string Name, int RegionIndex, double CenterX, double CenterY);
