namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>One ore's own units, crit and residue on the sharing pilot's run (ET-283), mirroring
/// <see cref="RunShareLootLine"/> for loot — what lets a receiver draw the same per-ore group row for a fleet mate on
/// another PC as it draws for its own characters. No ISK: valued from a receiver's own price cache, by ore name, the
/// way every mining figure in this app is priced.</summary>
public sealed record RunShareMiningLine(string OreType, int Units, int CriticalUnits, int ResidueUnits);
