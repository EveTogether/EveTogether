using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The loot table of an activity out of the own saved runs (ET-519), valued at today's price.</summary>
public sealed record GetActivityLootTableQuery(ActivityStatsKey Key, LootTableWindow Window = LootTableWindow.Days90)
    : IQuery<Result<LootTableDto>>;
