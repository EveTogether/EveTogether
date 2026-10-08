namespace EveUtils.Shared.Modules.Runs.Tally;

/// <summary>A run's counted lines split in two (ET-471): what LOOT shows and values, and the charges a before/after
/// difference shows were fired, which CONSUMABLES shows and values instead. Together they are every counted line.</summary>
public sealed record LootTallyCount(IReadOnlyList<LootTallyLine> Loot, IReadOnlyList<LootTallyLine> SpentCharges);
