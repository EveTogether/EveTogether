using System.Collections.Generic;

namespace EveUtils.Client.Controls.Map;

/// <summary>
/// Asks the map to bring systems into view: one system is flown to at system zoom, several are framed together.
/// A class rather than a record on purpose — each request is a new instance, so asking twice for the same system
/// still moves a map the pilot has dragged away since.
/// </summary>
/// <param name="systemIndexes">The systems to show.</param>
/// <param name="minZoom">For a single system: the least zoom (1 = all of New Eden) to arrive at. A map already zoomed
/// further in stays where it is.</param>
public sealed class MapFocusRequest(IReadOnlyList<int> systemIndexes, double? minZoom = null)
{
    public IReadOnlyList<int> SystemIndexes { get; } = systemIndexes;

    public double? MinZoom { get; } = minZoom;
}
