using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>The one arithmetic behind every "TOTAL ISK" figure in the app (ET-210): gamelog bounty, priced loot net
/// of what was lost, and every ISK-denominated reward parameter — never <see cref="RunParameterKey.Bounty"/>, a
/// mission's own stated reward line that would double-count against the same gamelog bounty under a different name.
/// Mirrors <c>ActivityDetailViewModel._ApplyTotalIsk</c> and <c>ActivityWindowViewModel._RefreshGroupTotalIsk</c>'s
/// formula verbatim, so a caller assembling the same three ingredients from a different source — an unfinished run
/// that has no <c>ActivitySummary</c> yet (ET-217) — agrees with both instead of adding a third way to add them up.
/// </summary>
public static class TotalIskCalculator
{
    public static decimal RewardIsk(IEnumerable<(RunParameterKey Key, decimal? Amount)> parameters) =>
        parameters
            .Where(parameter => parameter.Key is RunParameterKey.Isk or RunParameterKey.BonusIsk
                or RunParameterKey.FixedPayout or RunParameterKey.Escrow)
            .Sum(parameter => parameter.Amount.GetValueOrDefault());

    public static decimal Total(
        decimal bountyIsk, decimal? lootIskNet, IEnumerable<(RunParameterKey Key, decimal? Amount)> parameters) =>
        bountyIsk + lootIskNet.GetValueOrDefault() + RewardIsk(parameters);
}
