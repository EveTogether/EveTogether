namespace EveUtils.Client.Controls.Map;

/// <summary>Someone of your own in a system — drawn as a diamond beside it, the label once constellations show.</summary>
/// <param name="SystemIndex">Index into the graph's systems.</param>
public sealed record MapMarker(int SystemIndex, string Label);
