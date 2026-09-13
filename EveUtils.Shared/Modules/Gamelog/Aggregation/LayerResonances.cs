namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>One hit-point layer's damage resonances: the share of each damage type that gets through (1 = no resist).</summary>
public sealed record LayerResonances(double Em, double Thermal, double Kinetic, double Explosive);
