using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The can-fly evaluator: diffs an assigned fit's required skills against a character's cached skills
/// to a can-fly/missing-skills badge. No fit, no locally known skills, or no validator (no SDE) → no verdict (null) rather than "can't fly".
/// </summary>
public class MemberFitSkillEvaluatorTests
{
    private const int Ship = 587, Module = 1000, Skill = 3300;

    private sealed class FakeSkillRepository(IReadOnlyDictionary<int, int> levels) : ICharacterSkillRepository
    {
        public Task ReplaceForCharacterAsync(int characterId, IReadOnlyDictionary<int, int> levels, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<int, int>> GetLevelsAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(levels);
        public Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(levels.Count > 0);
    }

    // A module requiring skill 3300 at level 4 on a ship — same shape as FitValidatorTests.
    private static IFitValidator Validator() => new FitValidator(new FakeDogmaDataAccessor()
        .Type(Ship, 25, 6)
        .Type(Module, 60, 7,
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], Skill),
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 4)));

    private static MemberFitSkillEvaluator Evaluator(IReadOnlyDictionary<int, int> levels, bool withValidator = true)
    {
        var collection = new ServiceCollection();
        if (withValidator)
            collection.AddSingleton(Validator());
        return new MemberFitSkillEvaluator(new FakeSkillRepository(levels), collection.BuildServiceProvider());
    }

    private static FitReferenceInfo Fit()
    {
        var esi = new EsiFitting(1, "Guardian — Armor", "", Ship, [new EsiFittingItem(Module, "HiSlot0", 1)]);
        return new FitReferenceInfo(Ship, "Guardian — Armor", JsonSerializer.Serialize(esi), "h-guardian", null, null);
    }

    [Fact]
    public async Task NoAssignedFit_NoVerdict() =>
        Assert.Null(await Evaluator(new Dictionary<int, int> { [Skill] = 5 }).EvaluateAsync(1, null));

    [Fact]
    public async Task UnknownSkills_NoVerdict() =>
        Assert.Null(await Evaluator(new Dictionary<int, int>()).EvaluateAsync(1, Fit()));

    [Fact]
    public async Task ValidatorUnavailable_NoVerdict() =>
        Assert.Null(await Evaluator(new Dictionary<int, int> { [Skill] = 5 }, withValidator: false).EvaluateAsync(1, Fit()));

    [Fact]
    public async Task TrainedHighEnough_CanFly()
    {
        var badge = await Evaluator(new Dictionary<int, int> { [Skill] = 4 }).EvaluateAsync(1, Fit());
        Assert.NotNull(badge);
        Assert.True(badge!.CanFly);
    }

    [Fact]
    public async Task MissingSkill_CannotFly_WithCount()
    {
        var badge = await Evaluator(new Dictionary<int, int> { [Skill] = 2 }).EvaluateAsync(1, Fit());
        Assert.NotNull(badge);
        Assert.False(badge!.CanFly);
        Assert.Equal("1 skill missing", badge.Tooltip);
    }
}
