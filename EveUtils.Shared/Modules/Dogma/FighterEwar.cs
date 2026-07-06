namespace EveUtils.Shared.Modules.Dogma;

/// <summary>The kind of electronic warfare a support fighter projects. <see cref="None"/> for a damage fighter.</summary>
public enum FighterEwarKind
{
    None,
    EnergyNeutralizer,
    Ecm,
    WarpDisruption,
    StasisWeb
}

/// <summary>
/// A support fighter's EWAR readout for the per-squadron tooltip: informational only — the sim is target-less,
/// so the strength and range are surfaced, not applied. <see cref="Strength"/> is the kind's primary number (GJ neutralised,
/// ECM jam strength, point strength, or web speed penalty %); the ranges are its optimal and falloff in metres.
/// </summary>
public sealed record FighterEwar(FighterEwarKind Kind, double Strength, double OptimalRange, double FalloffRange);
