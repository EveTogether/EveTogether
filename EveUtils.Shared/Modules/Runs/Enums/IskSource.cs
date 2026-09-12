namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where one share of a run's TOTAL ISK comes from (ET-256) — one member per contributor in
/// <c>IskContributors</c>. Stored by number inside a summary's breakdown, so members are only ever added.</summary>
public enum IskSource
{
    Bounty = 0,
    Loot = 1,
    Rewards = 2,
    Consumables = 3,
    Mining = 4,

    /// <summary>A homefront's expected fixed payout, before the pilot confirms or types what actually arrived
    /// (ET-231). Once confirmed it is a <see cref="RunParameterKey.FixedPayout"/> row instead, counted by
    /// <see cref="Rewards"/> — the two never both count the same run.</summary>
    HomefrontPayout = 5
}
