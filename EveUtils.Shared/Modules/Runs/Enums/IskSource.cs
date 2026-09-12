namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where one share of a run's TOTAL ISK comes from (ET-256) — one member per contributor in
/// <c>IskContributors</c>. Stored by number inside a summary's breakdown, so members are only ever added.</summary>
public enum IskSource
{
    Bounty = 0,
    Loot = 1,
    Rewards = 2,
    Consumables = 3
}
