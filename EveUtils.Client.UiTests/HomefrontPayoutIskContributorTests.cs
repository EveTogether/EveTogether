using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-269 (overriding ET-231's "expected until confirmed"): a homefront's payout counts as
/// <see cref="IskCertainty.Measured"/> the moment the site reads Completed — there is no wallet scope to verify it
/// against and never will be, so it cannot stay "expected" waiting for a confirmation that can never arrive. A figure
/// the pilot typed over the table's (a <see cref="RunParameterKey.FixedPayout"/> row) counts instead of it — once,
/// here, and only while the payout is owed at all (ET-271).
/// </summary>
public sealed class HomefrontPayoutIskContributorTests
{
    [Fact]
    public void Contribute_DefaultTablePayout_CountsAsMeasured()
    {
        RunIskFacts facts = _Facts(expected: 15_000_000m);

        IskContribution? contribution = new HomefrontPayoutIskContributor().Contribute([facts], DateTime.UtcNow);

        Assert.NotNull(contribution);
        Assert.Equal(15_000_000m, contribution.Amount);
        Assert.Equal(IskCertainty.Measured, contribution.Certainty);
    }

    [Fact]
    public void Contribute_ATypedFigure_CountsInsteadOfTheTables_AndNowhereElse()
    {
        RunIskFacts facts = _Facts(expected: 15_000_000m,
            parameters: [new RunIskParameter(RunParameterKey.FixedPayout, 14_000_000m, null, DateTime.UtcNow)]);

        IskBreakdown isk = IskContributors.Breakdown([facts], DateTime.UtcNow);

        Assert.Equal(14_000_000m, isk.Of(IskSource.HomefrontPayout)?.Amount);
        Assert.Null(isk.Of(IskSource.Rewards));
        Assert.Equal(14_000_000m, isk.Total);
    }

    /// <summary>AC-2: Failed takes the payout away — a typed figure included. Counter-proof: count a FixedPayout row
    /// whatever the outcome (as Rewards did) and the failed site still pays 14,000,000.</summary>
    [Fact]
    public void Contribute_ATypedFigureOnASiteNoLongerOwed_CountsNothing()
    {
        RunIskFacts failed = _Facts(expected: null,
            parameters: [new RunIskParameter(RunParameterKey.FixedPayout, 14_000_000m, null, DateTime.UtcNow)]);

        Assert.Equal(0m, IskContributors.Breakdown([failed], DateTime.UtcNow).Total);
    }

    [Fact]
    public void Contribute_SumsAcrossRuns_TheTypedFigureWhereThereIsOne()
    {
        RunIskFacts corrected = _Facts(expected: 15_000_000m,
            parameters: [new RunIskParameter(RunParameterKey.FixedPayout, 14_000_000m, null, DateTime.UtcNow)]);
        RunIskFacts atTableFigure = _Facts(expected: 15_000_000m);

        IskContribution? contribution = new HomefrontPayoutIskContributor().Contribute([corrected, atTableFigure], DateTime.UtcNow);

        Assert.NotNull(contribution);
        Assert.Equal(29_000_000m, contribution.Amount);
        Assert.Equal(IskCertainty.Measured, contribution.Certainty);
    }

    [Fact]
    public void Contribute_NothingExpectedOnAnyRun_IsNull()
    {
        RunIskFacts facts = _Facts(expected: null);

        Assert.Null(new HomefrontPayoutIskContributor().Contribute([facts], DateTime.UtcNow));
    }

    private static RunIskFacts _Facts(decimal? expected, IReadOnlyList<RunIskParameter>? parameters = null) => new()
    {
        BountyIsk = 0m,
        LootIskNet = null,
        HasLoot = false,
        ConsumableIskCost = null,
        HasConsumables = false,
        MiningIskValue = null,
        HasMining = false,
        Parameters = parameters ?? [],
        StoppedAtUtc = null,
        HomefrontExpectedPayoutIsk = expected
    };
}
