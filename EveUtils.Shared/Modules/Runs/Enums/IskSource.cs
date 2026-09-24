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

    /// <summary>A homefront's fixed payout per character in the site (ET-231) — the table's figure, or the one the
    /// pilot typed over it as a <see cref="RunParameterKey.FixedPayout"/> row (ET-271), never both for one run.</summary>
    HomefrontPayout = 5,

    /// <summary>The ship, and the pod with its implants, a run lost as its linked killmails tell it (ET-331) — a cost.</summary>
    ShipLoss = 6
}
