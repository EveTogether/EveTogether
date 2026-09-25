using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Skills.Plans;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Commands;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-355 acceptance A1-A8, against a small synthetic skill tree (never the real SDE, per the ticket's own testing
/// rule): a ship (600) requiring skill B (3001) at III, a module (601) requiring skill A (3000) at II, and skill B
/// itself requiring skill A at IV — so + FROM FIT on the ship+module pulls in both skills with A's prerequisite level
/// (IV) beating its direct requirement (II).
/// </summary>
public sealed class SkillsPlansTests
{
    private const int Ship = 600, Module = 601, SkillA = 3000, SkillB = 3001;
    private const int Owner = 95001680, Other = 95001681;

    private static FakeDogmaDataAccessor Dogma() => new FakeDogmaDataAccessor()
        .Type(Ship, 6, 6,
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], SkillB), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 3))
        .Type(Module, 7, 7,
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], SkillA), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 2))
        .Type(SkillA, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 3),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Charisma),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower))
        .Type(SkillB, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 5),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower),
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], SkillA), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 4));

    private static readonly CharacterAttributeSet Attributes = new(20, 20, 20, 20, 20);

    [Fact]
    public void AddFromFit_GivesExactGapsWithPrerequisites_AndMarksQueuedLevels()
    {
        var validator = new FitValidator(Dogma());
        var trained = new Dictionary<int, int> { [SkillA] = 1 }; // partially trained — the counter-proof: a filtered-out level (1) must not reappear
        var result = SkillPlanRowFactory.FromFit(validator, [Ship, Module], trained, "Test Fit");

        // A requires level 4 (from B's prerequisite, beating the module's direct 2) — levels 2,3,4 (not 1: already trained).
        // The set, not the sequence: SkillPlanOrdering (tested separately) owns display order, not this expansion step.
        Assert.Equal(
            new HashSet<(int, int)> { (SkillA, 2), (SkillA, 3), (SkillA, 4), (SkillB, 1), (SkillB, 2), (SkillB, 3) },
            result.Rows.Select(r => (r.SkillTypeId, r.Level)).ToHashSet());
        Assert.Null(result.Message);

        // A level already trained (1) never appears among the rows — the trained-level filter this test guards.
        Assert.DoesNotContain(result.Rows, r => r.SkillTypeId == SkillA && r.Level == 1);
    }

    [Fact]
    public void AddFromItem_WhenEverythingTrained_AddsNothingAndReportsMessage()
    {
        var validator = new FitValidator(Dogma());
        var trained = new Dictionary<int, int> { [SkillA] = 5 }; // already at V — the module only needs II

        var result = SkillPlanRowFactory.FromItem(validator, Module, trained, "Test Module");

        Assert.Empty(result.Rows);
        Assert.NotNull(result.Message);
        Assert.Contains("Test Module", result.Message);
    }

    [Fact]
    public void FlyFirstOrder_PlacesFlyableMilestone_RightAfterFitsLastRequiredLevel()
    {
        var dogma = Dogma();
        var validator = new FitValidator(dogma);
        var built = SkillPlanRowFactory.FromFit(validator, [Ship, Module], new Dictionary<int, int>(), "Test Fit");
        var rows = built.Rows.Select(draft => new SkillPlanRow
        {
            SkillTypeId = draft.SkillTypeId, Level = draft.Level, Source = SkillPlanRowSource.Fit, SourceRef = "fit-hash", SourceLabel = "Test Fit"
        }).ToList();

        var ordered = SkillPlanOrdering.Order(rows, SkillPlanOrderMode.FlyFirst, dogma);
        int? milestoneIndex = SkillPlanOrdering.FlyableMilestoneIndex(ordered, "fit-hash");

        // The milestone sits after the very last row (every row here is required by the fit) — never before one.
        Assert.Equal(ordered.Count - 1, milestoneIndex);
    }

    [Theory]
    [InlineData(SkillPlanOrderMode.FlyFirst)]
    [InlineData(SkillPlanOrderMode.ShortestFirst)]
    [InlineData(SkillPlanOrderMode.ByAttribute)]
    public void TotalTime_IsEqualAcrossEveryOrderMode(SkillPlanOrderMode mode)
    {
        var dogma = Dogma();
        var validator = new FitValidator(dogma);
        var estimator = new SkillTrainingEstimator(dogma);
        var built = SkillPlanRowFactory.FromFit(validator, [Ship, Module], new Dictionary<int, int>(), "Test Fit");
        var rows = built.Rows.Select(draft => new SkillPlanRow { SkillTypeId = draft.SkillTypeId, Level = draft.Level }).ToList();

        var timePerSkill = mode == SkillPlanOrderMode.ShortestFirst ? SkillPlanTiming.TimePerSkill(estimator, rows, Attributes) : null;
        var ordered = SkillPlanOrdering.Order(rows, mode, dogma, timePerSkill);

        var expectedTotal = SkillPlanTiming.TotalTime(estimator, rows, Attributes);
        var orderedTotal = SkillPlanTiming.TotalTime(estimator, ordered, Attributes);
        Assert.Equal(expectedTotal, orderedTotal); // same row set, any order — the total never depends on sequence
    }

    [Fact]
    public void CopyAsTextThenImportFromText_RoundTripsTheSamePlan()
    {
        var sde = new FakeSdeAccessor().Add(SkillA, "Skill A", 16, 16).Add(SkillB, "Skill B", 16, 16);
        var rows = new List<(int SkillTypeId, int Level)> { (SkillA, 2), (SkillA, 3), (SkillB, 1) };

        string text = SkillPlanTextCodec.ToText(rows, sde);
        var parsed = SkillPlanTextCodec.Parse(text, sde);

        Assert.Empty(parsed.Unrecognized);
        Assert.Equal(rows, parsed.Rows.Select(r => (r.SkillTypeId, r.Level)).ToList());
    }

    [Fact]
    public void Handlers_NeverTakeATransportOrEsiClient()
    {
        var handlers = typeof(CreateSkillPlanCommand).Assembly.GetTypes()
            .Where(type => type.Namespace == "EveUtils.Shared.Modules.Skills.Plans.Commands" && type.Name.EndsWith("Handler"))
            .ToList();
        Assert.NotEmpty(handlers);

        var offenders = handlers
            .SelectMany(handler => handler.GetConstructors().SelectMany(c => c.GetParameters()))
            .Select(p => p.ParameterType)
            .Where(t => t.Name.Contains("Transport") || t.Name.Contains("Esi") || t.Name.Contains("Grpc"))
            .ToList();
        Assert.Empty(offenders); // a plan write reaches ISkillPlanRepository + IEventBus only — never a network client
    }

    [Fact]
    public async Task PlansAreScopedPerCharacter_AnotherCharacterSeesADifferentList()
    {
        using var instance = TestClientInstance.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var reader = instance.Services.GetRequiredService<ISkillPlanReader>();

        Result<int> ownerPlan = await dispatcher.Send(new CreateSkillPlanCommand(Owner, "Owner's plan"), cancellationToken);
        Result<int> otherPlan = await dispatcher.Send(new CreateSkillPlanCommand(Other, "Other's plan"), cancellationToken);
        Assert.True(ownerPlan.IsSuccess);
        Assert.True(otherPlan.IsSuccess);

        var ownerPlans = await reader.GetForCharacterAsync(Owner, cancellationToken);
        var otherPlans = await reader.GetForCharacterAsync(Other, cancellationToken);

        Assert.Single(ownerPlans);
        Assert.Single(otherPlans);
        Assert.NotEqual(ownerPlans[0].Id, otherPlans[0].Id);
        Assert.DoesNotContain(ownerPlans, p => p.Id == otherPlan.Value);
        Assert.DoesNotContain(otherPlans, p => p.Id == ownerPlan.Value);
    }

    [Fact]
    public void Ordering_AlwaysPlacesAPrerequisiteBeforeTheSkillThatNeedsIt()
    {
        var dogma = Dogma();
        var validator = new FitValidator(dogma);
        var estimator = new SkillTrainingEstimator(dogma);
        var built = SkillPlanRowFactory.FromFit(validator, [Ship, Module], new Dictionary<int, int>(), "Test Fit");
        var rows = built.Rows.Select(draft => new SkillPlanRow { SkillTypeId = draft.SkillTypeId, Level = draft.Level }).ToList();
        var timePerSkill = SkillPlanTiming.TimePerSkill(estimator, rows, Attributes);

        // Shortest-first would put the cheaper skill B rows ahead of A's on time alone if the constraint were dropped —
        // this is the mode where the counter-proof ("sort shortest-first without the constraint") actually bites.
        var ordered = SkillPlanOrdering.Order(rows, SkillPlanOrderMode.ShortestFirst, dogma, timePerSkill).ToList();

        int lastA = ordered.FindLastIndex(row => row.SkillTypeId == SkillA && row.Level == 4);
        int firstB = ordered.FindIndex(row => row.SkillTypeId == SkillB);
        Assert.True(lastA < firstB, "Skill A IV (skill B's own prerequisite) must precede every skill B row.");
    }
}
