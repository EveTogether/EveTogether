namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The outcome of a <see cref="CapacitorSimulator"/> run. When the capacitor never reaches zero it is stable and
/// <see cref="StablePercent"/> holds the equilibrium level; otherwise it is unstable and <see cref="DepletesInSeconds"/>
/// holds the time to empty.
/// </summary>
public sealed record CapacitorSimulation(bool Stable, double StablePercent, double DepletesInSeconds);
