using System;

namespace EveUtils.Client.Controls.Map;

/// <summary>One fleet member in a badge's system, and when that was last confirmed.</summary>
public sealed record MapFleetSighting(string Name, DateTimeOffset ObservedAt);
