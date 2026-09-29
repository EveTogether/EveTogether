namespace EveUtils.Client.Controls.Map;

/// <summary>One system of a trail, oldest first.</summary>
/// <param name="SystemIndex">Index into the graph's systems.</param>
/// <param name="IsGapBefore">The character was not seen in between: the previous system is no gate neighbour, so the
/// jump to this one is drawn dotted instead of dashed — a leap the trail could not follow, not a route it knows.</param>
public sealed record MapTrailStep(int SystemIndex, bool IsGapBefore);
