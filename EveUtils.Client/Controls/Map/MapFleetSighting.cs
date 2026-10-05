using System;

namespace EveUtils.Client.Controls.Map;

/// <summary>One fleet member in a badge's system, and when that was last confirmed.</summary>
/// <param name="IsCommander">The member is the fleet's commander: the map marks them apart from the count (ET-398).</param>
public sealed record MapFleetSighting(string Name, DateTimeOffset ObservedAt, bool IsCommander = false);
