using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// A homefront's fixed payout, counted the moment the site reads Completed (ET-269) — never "expected until
/// confirmed": there is no wallet scope to check it against and never will be, so the table's own figure for a
/// ticked character counts as <see cref="IskCertainty.Measured"/> straight away, the same as a measured bounty line.
///
/// A pilot who typed a different figure because something else really arrived carries a
/// <see cref="RunParameterKey.FixedPayout"/> row for that run, and that figure counts instead of the table's — but only
/// while the payout is owed at all (ET-271): a site set to Failed, or a character ticked out, pays nothing, typed
/// figure or not.
/// </summary>
internal sealed class HomefrontPayoutIskContributor : IIskContributor
{
    public IskSource Source => IskSource.HomefrontPayout;

    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        decimal sum = 0m;
        bool any = false;
        foreach (RunIskFacts run in runs)
        {
            if (run.HomefrontExpectedPayoutIsk is not { } expected)
                continue;

            sum += CorrectedPayout(run.Parameters) ?? expected;
            any = true;
        }

        return any ? new IskContribution(Source, sum, IskCertainty.Measured) : null;
    }

    /// <summary>The figure the pilot typed over the table's for this run, or null when they typed none.</summary>
    public static decimal? CorrectedPayout(IReadOnlyList<RunIskParameter> parameters) =>
        parameters.FirstOrDefault(parameter => parameter.Key == RunParameterKey.FixedPayout)?.Amount;
}
