namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>One item type of an activity's counted loot, all its lines merged (ET-513).</summary>
public sealed record EarningsLootLine(int TypeId, long Quantity);
