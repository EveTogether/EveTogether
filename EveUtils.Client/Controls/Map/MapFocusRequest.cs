using System.Collections.Generic;

namespace EveUtils.Client.Controls.Map;

/// <summary>
/// Asks the map to bring systems into view: one system is flown to at system zoom, several are framed together.
/// A class rather than a record on purpose — each request is a new instance, so asking twice for the same system
/// still moves a map the pilot has dragged away since.
/// </summary>
public sealed class MapFocusRequest(IReadOnlyList<int> systemIndexes)
{
    public IReadOnlyList<int> SystemIndexes { get; } = systemIndexes;
}
