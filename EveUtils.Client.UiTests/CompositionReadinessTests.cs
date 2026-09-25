using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class CompositionReadinessTests
{
    private const int Ship = 587;
    private const int Module = 1000;
    private const int Skill = 3300;
    private static readonly CharacterAttributeSet EffectiveAttributes = new(19, 20, 20, 25, 25);

    private static FakeDogmaDataAccessor Data() => new FakeDogmaDataAccessor()
        .Type(Ship, 25, 6)
        .Type(Module, 60, 7,
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], Skill),
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 4))
        .Type(Skill, 0, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 7),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower));

    private static CompositionReadinessCalculator Calculator(FakeDogmaDataAccessor data) =>
        new(new FitValidator(data), new SkillTrainingEstimator(data), FallbackNameResolver.Instance);

    private static FitReferenceInfo Fit()
    {
        EsiFitting fitting = new(1, "Ferox · Rails", "", Ship, [new EsiFittingItem(Module, "HiSlot0", 1)]);
        return new FitReferenceInfo(Ship, fitting.Name, JsonSerializer.Serialize(fitting), "ferox", null, null);
    }

    private static CompositionCharacterSnapshot Pilot(string name, int level,
        bool hasScope = true, IReadOnlyList<CharacterSkillQueueEntry>? queue = null) =>
        new(name, hasScope, hasScope, new Dictionary<int, int> { [Skill] = level }, EffectiveAttributes, queue ?? []);

    [Fact]
    public void Evaluate_TwelveCharacters_CountsWithoutAddingCharacterEntries()
    {
        IReadOnlyList<CompositionCharacterSnapshot> pilots = Enumerable.Range(1, 12)
            .Select(number => Pilot($"Pilot {number}", number <= 3 ? 4 : 0)).ToList();

        CompositionReadinessCalculator calculator = Calculator(Data());
        calculator.Evaluate("Mainline", Fit(), [], pilots);
        Stopwatch clock = Stopwatch.StartNew();
        CompositionReadinessEntry entry = calculator.Evaluate("Mainline", Fit(), [], pilots);
        clock.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"Readiness over 12 cached characters: {clock.Elapsed.TotalMilliseconds:F2} ms");

        Assert.Equal(12, entry.CharacterCount);
        Assert.Equal(3, entry.ReadyCount);
        Assert.Equal(0, entry.FliesCount);
        Assert.Equal(9, entry.NotYetCount);
        Assert.Equal(12, entry.ReadyCount + entry.FliesCount + entry.NotYetCount);
        Assert.Equal("YOUR 12 CHARACTERS", entry.CharactersLabel);
    }

    [Fact]
    public void CharacterList_SortsReadyThenFastest_AndSearchStartsAtNine()
    {
        CompositionCharacterReadiness slow = new("Slow", CompositionReadinessStatus.NotYet,
            TimeSpan.FromDays(10), TimeSpan.FromDays(10), [], "");
        CompositionCharacterReadiness fast = new("Fast", CompositionReadinessStatus.NotYet,
            TimeSpan.FromDays(1), TimeSpan.FromDays(1), [], "");
        CompositionCharacterReadiness ready = new("Ready", CompositionReadinessStatus.Ready,
            TimeSpan.Zero, TimeSpan.Zero, [], "");
        CompositionCharacterReadiness flies = new("Flies", CompositionReadinessStatus.Flies,
            TimeSpan.Zero, TimeSpan.FromDays(2), [], "");
        CompositionReadinessEntry eight = new("Mainline", "Ferox", "Ferox",
            [slow, fast, ready, flies, slow, slow, slow, slow]);
        CompositionReadinessEntry nine = new("Mainline", "Ferox", "Ferox",
            [slow, fast, ready, flies, slow, slow, slow, slow, slow]);

        Assert.False(eight.HasSearch);
        Assert.True(nine.HasSearch);
        Assert.Equal("Ready", nine.VisibleCharacters[0].Name);
        Assert.Equal("Flies", nine.VisibleCharacters[1].Name);
        Assert.Equal("Fast", nine.VisibleCharacters[2].Name);
        nine.SearchText = "fast";
        Assert.Equal("Fast", Assert.Single(nine.VisibleCharacters).Name);
    }

    [Fact]
    public void Evaluate_NoSkillsScope_RemainsUnknownDespiteCachedLevels()
    {
        CompositionReadinessEntry entry = Calculator(Data()).Evaluate("Mainline", Fit(), [],
            [Pilot("No scope", 0, hasScope: false), Pilot("Scoped", 0)]);

        Assert.Equal(1, entry.UnknownCount);
        Assert.Equal(1, entry.NotYetCount);
        Assert.Equal(CompositionReadinessStatus.Unknown,
            entry.VisibleCharacters.Single(character => character.Name == "No scope").Status);
    }

    [Fact]
    public void Evaluate_ToFly_EqualsEstimatorOverValidatorGaps_AndShowsQueueOverlap()
    {
        FakeDogmaDataAccessor data = Data();
        CharacterSkillQueueEntry queued = new() { SkillTypeId = Skill, FinishedLevel = 2 };
        CompositionReadinessEntry entry = Calculator(data).Evaluate("Mainline", Fit(), [],
            [Pilot("Catbank", 1, queue: [queued])]);
        CompositionCharacterReadiness pilot = Assert.Single(entry.VisibleCharacters);
        SkillTrainingEstimate expected = new SkillTrainingEstimator(data).Estimate(Skill, 1, 4, EffectiveAttributes);

        Assert.Equal(expected.TrainingTime, pilot.ToFly);
        Assert.Equal(EveDurationFormatter.Format(expected.TrainingTime), pilot.ToFlyLabel);
        Assert.Equal("Already in the queue: type 3300 2", pilot.QueueSummary);
    }

    /// <summary>ET-353 A2: a doctrine minimum at or below what the fit already requires asks nothing extra, so TO MIN
    /// stays equal to TO FLY and the editor row says "no effect".</summary>
    [Fact]
    public void Evaluate_MinimumNotAboveTheFit_LeavesToMinAtToFly_AndShowsNoEffect()
    {
        CompositionReadinessEntry entry = Calculator(Data()).Evaluate("Mainline", Fit(),
            [new SkillMinimum(Skill, 3)], [Pilot("Catbank", 1)]);
        CompositionCharacterReadiness pilot = Assert.Single(entry.VisibleCharacters);
        EditorSkillMinimumViewModel row = new(Skill, "Skill", level: 3, fitLevel: 4);

        Assert.Equal(pilot.ToFly, pilot.ToMin);
        Assert.Equal("fit: IV · no effect", row.FitHint);
    }

    /// <summary>ET-353 A6: flying the fit while under the doctrine minimum is Flies, with TO MIN the training to the
    /// minimum; at the minimum it is Ready.</summary>
    [Theory]
    [InlineData(4, CompositionReadinessStatus.Flies)]
    [InlineData(5, CompositionReadinessStatus.Ready)]
    public void Evaluate_FitFlownUnderTheMinimum_IsFliesWithToMin(int trained, CompositionReadinessStatus expected)
    {
        FakeDogmaDataAccessor data = Data();
        CompositionReadinessEntry entry = Calculator(data).Evaluate("Mainline", Fit(),
            [new SkillMinimum(Skill, 5)], [Pilot("Catbank", trained)]);
        CompositionCharacterReadiness pilot = Assert.Single(entry.VisibleCharacters);

        Assert.Equal(expected, pilot.Status);
        Assert.Equal(new SkillTrainingEstimator(data).Estimate(Skill, trained, 5, EffectiveAttributes).TrainingTime, pilot.ToMin);
    }
}
