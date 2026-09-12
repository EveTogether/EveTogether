using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-269 (overriding ET-231's "expected until confirmed"): a homefront's payout counts as
/// <see cref="IskCertainty.Measured"/> the moment the site reads Completed — there is no wallet scope to verify it
/// against and never will be, so it cannot stay "expected" waiting for a confirmation that can never arrive. A run
/// the pilot typed a different figure for already carries a <see cref="RunParameterKey.FixedPayout"/> row and is
/// skipped here, so it is never counted twice — once here at the table figure, once by
/// <see cref="RewardIskContributor"/> at the typed one.
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
    public void Contribute_ATypedCorrection_IsSkipped_SoItIsNeverCountedTwice()
    {
        RunIskFacts facts = _Facts(expected: 15_000_000m,
            parameters: [new RunIskParameter(RunParameterKey.FixedPayout, 15_000_000m, null, DateTime.UtcNow)]);

        IskContribution? contribution = new HomefrontPayoutIskContributor().Contribute([facts], DateTime.UtcNow);

        Assert.Null(contribution);
    }

    [Fact]
    public void Contribute_SumsAcrossRuns_ButOnlyTheOnesWithNoTypedCorrection()
    {
        RunIskFacts corrected = _Facts(expected: 15_000_000m,
            parameters: [new RunIskParameter(RunParameterKey.FixedPayout, 15_000_000m, null, DateTime.UtcNow)]);
        RunIskFacts atTableFigure = _Facts(expected: 15_000_000m);

        IskContribution? contribution = new HomefrontPayoutIskContributor().Contribute([corrected, atTableFigure], DateTime.UtcNow);

        Assert.NotNull(contribution);
        Assert.Equal(15_000_000m, contribution.Amount);
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
