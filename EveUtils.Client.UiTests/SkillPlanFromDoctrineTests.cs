using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Skills;
using EveUtils.Client.ViewModels.Skills.Plans;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Sde;
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
/// ET-386 acceptance AC1-AC4, against a small synthetic skill tree (never the real SDE, per the ticket's own testing
/// rule): a ship (700) requiring skill X (3100) at II, and a separate skill W (3101) that only exists as skill Z
/// (3102)'s own prerequisite — so a doctrine minimum on Z pulls in a whole chain the fit itself never mentions.
/// </summary>
public sealed class SkillPlanFromDoctrineTests
{
    private const int Ship = 700, SkillX = 3100, SkillW = 3101, SkillZ = 3102;
    private const int Owner = 95001690;

    private static FakeDogmaDataAccessor Dogma() => new FakeDogmaDataAccessor()
        .Type(Ship, 6, 6,
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], SkillX), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 2))
        .Type(SkillX, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 3),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Charisma),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower))
        .Type(SkillW, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 3),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Charisma),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower))
        .Type(SkillZ, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 5),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower),
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], SkillW), new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 2));

    [Theory]
    [InlineData(SkillZ, 3, 7, true)]
    [InlineData(SkillX, 1, 2, false)]
    public void FromDoctrine_AddsMinimumLevelsRelativeToFit_WithPrerequisites(
        int minimumSkillTypeId, int minimumLevel, int expectedRowCount, bool expectsPrerequisiteSkill)
    {
        var validator = new FitValidator(Dogma());
        var minimums = new List<SkillMinimum> { new(minimumSkillTypeId, minimumLevel) };

        var result = SkillPlanRowFactory.FromDoctrine(validator, [Ship], minimums, new Dictionary<int, int>(), "Test Entry");

        // Above the fit's own II (SkillZ case): the fit's X levels, plus Z's own levels, plus W as Z's prerequisite —
        // nothing lost, nothing doubled (AC1). At or below it (SkillX case): exactly the fit's own two rows, the
        // minimum adds nothing extra and no duplicate (skill, level) pair appears (AC2).
        Assert.Equal(expectedRowCount, result.Rows.Count);
        Assert.Equal(expectedRowCount, result.Rows.Select(r => (r.SkillTypeId, r.Level)).Distinct().Count());
        Assert.Equal(expectsPrerequisiteSkill, result.Rows.Any(r => r.SkillTypeId == SkillW));
    }

    [Fact]
    public void DoctrineMilestones_FlyableLandsBeforeMinimumMet_AtTheirRespectiveLastLevels()
    {
        var dogma = Dogma();
        var validator = new FitValidator(dogma);
        var minimums = new List<SkillMinimum> { new(SkillX, 4) }; // above the fit's own II — two extra levels
        var built = SkillPlanRowFactory.FromDoctrine(validator, [Ship], minimums, new Dictionary<int, int>(), "Test Entry");
        var rows = built.Rows.Select(draft => new SkillPlanRow
        {
            SkillTypeId = draft.SkillTypeId, Level = draft.Level,
            Source = SkillPlanRowSource.Doctrine, SourceRef = "entry-1", SourceLabel = "Test Entry"
        }).ToList();

        var ordered = SkillPlanOrdering.Order(rows, SkillPlanOrderMode.FlyFirst, dogma);
        var fitRequiredLevels = SkillPlanRowFactory.RequiredLevelPairs(validator, [Ship], new Dictionary<int, int>());
        int? flyableIndex = SkillPlanOrdering.DoctrineFlyableMilestoneIndex(ordered, "entry-1", fitRequiredLevels);
        int? minimumIndex = SkillPlanOrdering.DoctrineMinimumMilestoneIndex(ordered, "entry-1");

        // X trains I-IV as one contiguous block (AC3): the fit's own I-II land the ✈ flyable milestone right after
        // row index 1, the minimum's III-IV land ◆ doctrine minimum met at the very last row — never swapped, never
        // missing.
        Assert.Equal(1, flyableIndex);
        Assert.Equal(3, minimumIndex);
    }

    [Fact]
    public async Task AddFromDoctrine_DispatchesOnlyAddSkillPlanRowsCommand_WithDoctrineSourceAndLabel()
    {
        var dogma = Dogma();
        var validator = new FitValidator(dogma);
        var dispatcher = new _RecordingDispatcher();
        var dialogs = new RecordingDialogService();
        var entry = new FleetCompositionEntry
        {
            Id = 42,
            RoleId = 1,
            Fit = new FitReference
            {
                ShipTypeId = Ship, FitName = "Test Fit", ContentHash = "hash",
                RawJson = $"{{\"fitting_id\":0,\"name\":\"Test Fit\",\"description\":\"\",\"ship_type_id\":{Ship},\"items\":[]}}"
            },
            SkillMinimums = [new FleetCompositionEntrySkillMinimum { SkillTypeId = SkillX, Level = 4 }]
        };
        dialogs.OnPickDoctrineEntry = _ => Task.FromResult<DoctrineEntryPick?>(new DoctrineEntryPick(entry, "Test Doctrine", "DPS"));

        var services = new ServiceCollection()
            .AddSingleton<IDispatcher>(dispatcher)
            .AddSingleton<ISkillPlanReader>(new _EmptySkillPlanReader())
            .AddSingleton<IFitValidator>(validator)
            .AddSingleton<IDogmaDataAccessor>(dogma)
            .AddSingleton<IDialogService>(dialogs)
            .AddSingleton<IFleetCompositionReader>(new _EmptyFleetCompositionReader())
            .BuildServiceProvider();
        var snapshot = new SkillsCharacterSnapshot(new FakeSdeAccessor(), new Dictionary<int, int>(), [], null, DateTimeOffset.UtcNow);
        var vm = new SkillsPlansViewModel(services, snapshot, Owner) { SelectedPlan = new SkillPlan { Id = 1, CharacterId = Owner, Name = "Test Plan" } };

        await vm.AddFromDoctrineCommand.ExecuteAsync(null);

        // AC4: the entry came from the reader-backed picker (never a write-repository, ET-383 guard — WriteRepositoryGuardTests
        // holds that structurally) and the only thing that reached the dispatcher is this one command.
        var sent = Assert.Single(dispatcher.SentCommands);
        var command = Assert.IsType<AddSkillPlanRowsCommand>(sent);
        Assert.Equal(SkillPlanRowSource.Doctrine, command.Source);
        Assert.Equal("42", command.SourceRef);
        Assert.Equal("Test Doctrine · DPS · Test Fit", command.Rows[0].SourceLabel);
    }

    private sealed class _RecordingDispatcher : IDispatcher
    {
        public List<object> SentCommands { get; } = [];

        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("_RecordingDispatcher: only Send(AddSkillPlanRowsCommand) is wired for this test.");

        public Task Send(ICommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("_RecordingDispatcher: only Send(AddSkillPlanRowsCommand) is wired for this test.");

        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
        {
            SentCommands.Add(command);
            if (command is AddSkillPlanRowsCommand draft)
            {
                return Task.FromResult((TResult)(object)Result<int>.Success(draft.Rows.Count));
            }

            throw new NotSupportedException($"_RecordingDispatcher: unexpected command {command.GetType().Name}.");
        }
    }

    private sealed class _EmptySkillPlanReader : ISkillPlanReader
    {
        public Task<IReadOnlyList<SkillPlan>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillPlan>>([]);

        public Task<SkillPlan?> GetAsync(int planId, CancellationToken cancellationToken = default) => Task.FromResult<SkillPlan?>(null);

        public Task<IReadOnlyList<SkillPlanRow>> GetRowsAsync(int planId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillPlanRow>>([]);
    }

    private sealed class _EmptyFleetCompositionReader : IFleetCompositionReader
    {
        public Task<FleetComposition?> GetAsync(long compositionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FleetComposition?>(null);

        public Task<IReadOnlyList<FleetComposition>> ListByOwnerAsync(int ownerCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FleetComposition>>([]);

        public Task<IReadOnlyList<FleetComposition>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FleetComposition>>([]);

        public Task<FleetCompositionGraph?> GetGraphAsync(long compositionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FleetCompositionGraph?>(null);

        public Task<FleetCompositionRole?> GetRoleAsync(long roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FleetCompositionRole?>(null);

        public Task<IReadOnlyList<FleetCompositionRole>> ListRolesAsync(long compositionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FleetCompositionRole>>([]);

        public Task<FleetCompositionEntry?> GetEntryAsync(long entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FleetCompositionEntry?>(null);

        public Task<IReadOnlyList<FleetCompositionEntry>> ListEntriesAsync(long roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FleetCompositionEntry>>([]);
    }
}
