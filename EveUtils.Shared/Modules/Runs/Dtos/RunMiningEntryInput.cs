namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One ore's aggregated mining on a run, as it travels in <see cref="RunWireData"/> (ET-229) — the same
/// shape <see cref="Entities.RunMiningEntry"/> stores, so a fleetmate's mining survives a sync exactly like theirs.</summary>
public sealed class RunMiningEntryInput
{
    public required string OreType { get; init; }
    public required int Units { get; init; }
    public required int CriticalUnits { get; init; }
    public required int ResidueUnits { get; init; }
    public required DateTime FirstObservedAtUtc { get; init; }
    public required DateTime LastObservedAtUtc { get; init; }
}
