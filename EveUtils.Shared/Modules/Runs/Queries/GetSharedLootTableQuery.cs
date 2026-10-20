using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The loot table of an activity out of the outcomes a server handed out (ET-519), valued at today's price.
/// <paramref name="Outcomes"/> and <paramref name="Loot"/> are the same activity over the same window, as for
/// <see cref="GetSharedEarningsQuery"/>.</summary>
public sealed record GetSharedLootTableQuery(ActivityOutcomesDto Outcomes, ActivityOutcomeLootDto Loot, LootTableWindow Window)
    : IQuery<Result<LootTableDto>>;
