using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// The ISK-denominated reward lines a run carries — Isk, BonusIsk, Escrow — on any type, not only a mission (ET-210).
/// Never <see cref="RunParameterKey.Bounty"/>: that is a mission's own stated reward line, and the gamelog's bounty
/// already counts the same money. Never <see cref="RunParameterKey.FixedPayout"/> either: that is a homefront payout
/// typed over the table's, which <see cref="HomefrontPayoutIskContributor"/> counts only while the payout is owed
/// (ET-271). LP and Evermarks have no ISK rate and count nothing. A bonus whose deadline had passed when its run
/// stopped does not count (ET-237).
/// </summary>
internal sealed class RewardIskContributor : IIskContributor
{
    public IskSource Source => IskSource.Rewards;

    // Distinct over the whole activity: every own toon's run started from one mission window carries its own copy of
    // the same stated line (ET-210), and the mission pays it once. A line is its key, amount and the capture it came
    // from, and one capture never states the same ISK line twice.
    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        RunIskParameter[] counted = [.. runs
            .SelectMany(run => run.Parameters.Where(parameter => Counts(parameter, run.StoppedAtUtc ?? nowUtc)))
            .Distinct()];
        return counted.Length == 0
            ? null
            : new IskContribution(Source, counted.Sum(parameter => parameter.Amount.GetValueOrDefault()), IskCertainty.Measured);
    }

    /// <summary>Whether this line counts on a run that stopped at <paramref name="judgedAtUtc"/> — the very rule
    /// <see cref="Contribute"/> filters by, so <see cref="IskContributors.BreakdownByCharacter"/> can hand the one
    /// copy of a duplicated line to one character and still add up to what the activity counts (ET-296).</summary>
    internal static bool Counts(RunIskParameter parameter, DateTime judgedAtUtc) =>
        parameter.Key is RunParameterKey.Isk or RunParameterKey.BonusIsk or RunParameterKey.Escrow
        && parameter.Amount is not null
        && !(parameter.Key is RunParameterKey.BonusIsk
             && MissionBonusDeadline.HasPassed(parameter.BonusWindowSeconds, parameter.ObservedAtUtc, judgedAtUtc));
}
