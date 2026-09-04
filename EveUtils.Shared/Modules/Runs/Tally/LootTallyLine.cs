using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Tally;

/// <summary>One line as it counts towards a run's loot: the fields <see cref="LootTally"/> reads, and the fields it
/// hands back. Deliberately not an entry — a difference between two cargo holds has no name, no clipboard price and
/// no row of its own anywhere.</summary>
public sealed record LootTallyLine(int ItemTypeId, long? Quantity, decimal? Volume, LootKind LootKind);
