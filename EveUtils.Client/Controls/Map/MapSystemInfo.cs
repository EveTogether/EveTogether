using System;
using System.Collections.Generic;

namespace EveUtils.Client.Controls.Map;

/// <summary>What the hover popover says about one system (ET-399): only what the map already holds.</summary>
/// <param name="RegionColourIndex">Index into <see cref="MapPalette.Regions"/> (already wrapped) for the region's name.</param>
/// <param name="RouteStep">1-based place on the planned route, or null when the route does not pass here.</param>
/// <param name="Neighbours">Names of the systems the stargates lead to.</param>
public sealed record MapSystemInfo(
    string Name,
    double Security,
    string Constellation,
    string Region,
    int RegionColourIndex,
    string? Faction,
    IReadOnlyList<MapSystemDistance> Distances,
    int? RouteStep,
    int RouteLength,
    IReadOnlyList<MapSystemOccupant> Here,
    IReadOnlyList<string> Neighbours);

/// <param name="From">Who the jumps are counted from.</param>
/// <param name="Jumps">The jump count; null while it is being counted, or when no route leads there.</param>
public sealed record MapSystemDistance(string From, int? Jumps, bool IsPending);

/// <param name="ObservedAt">When the position was last seen; null for a character known only from the game log's last jump.</param>
public sealed record MapSystemOccupant(string Name, MapOccupantKind Kind, DateTimeOffset? ObservedAt);
