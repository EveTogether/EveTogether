using System.Linq;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-354 A1-A2: <see cref="AttributeRemapOptimizer"/> picks the base-attribute distribution that trains a set of
/// rows fastest, brute-forcing exactly the 2,885 valid 17-27-per-attribute, sum-99 distributions.
/// </summary>
public class AttributeRemapOptimizerTests
{
    // A synthetic three-skill queue (not the RaymondKrah probe referenced in the grooming, which this session has
    // no access to) with a +4 Ocular Filter (Perception) plugged in. Independently brute-forced in plain JS —
    // not derived from AttributeRemapOptimizer itself — to get the expected split and total time below.
    private static readonly RemapTrainingRow[] Rows =
    [
        new(DogmaAttributeIds.Perception, DogmaAttributeIds.Willpower, 1_000_000),
        new(DogmaAttributeIds.Intelligence, DogmaAttributeIds.Memory, 600_000),
        new(DogmaAttributeIds.Charisma, DogmaAttributeIds.Perception, 200_000),
    ];
    private static readonly CharacterAttributeSet ImplantBonus = new(0, 0, 0, 4, 0);

    /// <summary>Criterion 1. Red if a row's rate is computed from the raw ESI attributes instead of Base(), or if
    /// the fixture's rows change.</summary>
    [Fact]
    public void Best_PicksTheDistributionThatTrainsTheRowsFastest()
    {
        var result = AttributeRemapOptimizer.Best(Rows, ImplantBonus);

        Assert.Equal(new CharacterAttributeSet(17, 21, 17, 27, 17), result.BaseAttributes);
        Assert.Equal(51809.28, result.TotalTime.TotalMinutes, 2);
    }

    /// <summary>Criterion 2. Red if a bound moves to 16 or 28 — the candidate count changes.</summary>
    [Fact]
    public void EnumerateBaseDistributions_Has2885ValidSplits()
    {
        var distributions = AttributeRemapOptimizer.EnumerateBaseDistributions().ToList();

        Assert.Equal(2885, distributions.Count);
        Assert.All(distributions, d => Assert.Equal(99,
            d.Charisma + d.Intelligence + d.Memory + d.Perception + d.Willpower));
        Assert.All(distributions, d => Assert.True(
            d.Charisma is >= 17 and <= 27 && d.Willpower is >= 17 and <= 27));
    }
}
