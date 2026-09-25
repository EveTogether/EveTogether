using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Skills;
using EveUtils.Client.ViewModels.Skills;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-16 AC1, AC2, AC6: the header character choice only grows a search field once scrolling stops being faster
/// (AC1), the module opens on the character it was launched from — a pilot row's id, or failing that the last
/// character it was left on, or failing that the first in the character column's own order (AC2) — and the header's
/// total skill points come straight from ESI's own <c>total_sp</c>, never a sum over trained levels (AC6). One test
/// per acceptance criterion, run against the real client DI on a throwaway instance.
/// </summary>
public sealed class SkillsWindowViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task _SeedCharactersAsync(TestClientInstance instance, params (int Id, string Name)[] characters)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (var (id, name) in characters)
        {
            await registry.AddOrUpdateAsync(new Character(name, id), Ct);
        }
    }

    /// <summary>Criterion 1. Red if the search field shows below the threshold, or stays hidden at or above it.</summary>
    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public async Task ShowCharacterSearch_OnlyFromTheThreshold(int characterCount, bool expectedShowSearch)
    {
        using var instance = TestClientInstance.Create();
        await _SeedCharactersAsync(instance,
            Enumerable.Range(1, characterCount).Select(i => (i, $"Pilot{i}")).ToArray());
        var viewModel = new SkillsWindowViewModel(instance.Services, startingCharacterId: null);

        await viewModel.LoadAsync(Ct);

        Assert.Equal(expectedShowSearch, viewModel.ShowCharacterSearch);
    }

    /// <summary>Criterion 2. Red if the module always opens on the first character regardless of how it was launched.</summary>
    [Theory]
    [InlineData(30, null, 30)]     // launched from a pilot row: that character wins over everything else
    [InlineData(null, 20, 20)]     // launched from the rail: falls back to the character last left on
    [InlineData(null, null, 10)]   // launched from the rail, nothing remembered yet: the column's own first character
    public async Task OpensOnTheRightCharacter(int? startingCharacterId, int? rememberedCharacterId, int expectedCharacterId)
    {
        using var instance = TestClientInstance.Create();
        await _SeedCharactersAsync(instance, (10, "First"), (20, "Second"), (30, "Third"));
        if (rememberedCharacterId is { } remembered)
        {
            await instance.Services.GetRequiredService<ISettingRepository>()
                .UpsertAsync(SkillsWindowViewModel.LastCharacterSettingKey, remembered.ToString(CultureInfo.InvariantCulture), Ct);
        }
        var viewModel = new SkillsWindowViewModel(instance.Services, startingCharacterId);

        await viewModel.LoadAsync(Ct);

        Assert.Equal(expectedCharacterId, viewModel.SelectedCharacterId);
    }

    /// <summary>Criterion 6. Red if the header total reads as a sum over trained levels instead of ESI's own
    /// total_sp/unallocated_sp, or if a character whose skills were never imported gets a blank header instead of a
    /// clear placeholder (Raymond's preview feedback, 2026-09-24).</summary>
    [Fact]
    public async Task TotalSpText_ReadsEsisTotalSp_NeverASumOverTrainedLevels()
    {
        using var instance = TestClientInstance.Create();
        await _SeedCharactersAsync(instance, (1, "RaymondKrah"));
        // A trained level whose own SP-per-level formula sums to a wildly different figure than the stored total —
        // if TotalSpText ever regresses to summing levels instead of reading Attributes.TotalSp, this proves it.
        await instance.Services.GetRequiredService<ICharacterSkillRepository>()
            .ReplaceForCharacterAsync(1, new Dictionary<int, int> { [3300] = 3 }, Ct);
        await instance.Services.GetRequiredService<ICharacterAttributesRepository>().ReplaceForCharacterAsync(
            new CharacterAttributes { CharacterId = 1, Charisma = 17, Intelligence = 17, Memory = 17, Perception = 27,
                Willpower = 21, TotalSp = 187_783_359, UnallocatedSp = 6_378_705 }, Ct);
        var viewModel = new SkillsWindowViewModel(instance.Services, startingCharacterId: 1);

        await viewModel.LoadAsync(Ct);

        Assert.Equal("187,783,359 Total Skill Points", viewModel.TotalSpText);
        Assert.Equal("6,378,705 unallocated skill points", viewModel.UnallocatedSpText);
    }

    /// <summary>Criterion 6, fresh-database case. Red if a character with no skill import yet leaves the header
    /// blank instead of a placeholder that says so.</summary>
    [Fact]
    public async Task TotalSpText_ShowsAPlaceholder_WhenNeverImported()
    {
        using var instance = TestClientInstance.Create();
        await _SeedCharactersAsync(instance, (1, "FreshCharacter"));
        var viewModel = new SkillsWindowViewModel(instance.Services, startingCharacterId: 1);

        await viewModel.LoadAsync(Ct);

        Assert.Equal("Total Skill Points not imported yet", viewModel.TotalSpText);
        Assert.Equal("", viewModel.UnallocatedSpText);
    }

    /// <summary>
    /// Regression from the independent review, not one of ET-16's own acceptance criteria: re-opening SKILLS while
    /// it is already open (ModuleHostService's ET-48 "route to existing" pattern) calls RefreshModule on the
    /// running instance. RefreshModule used to re-run the constructor's starting-character resolution, which threw
    /// away whatever the pilot had since picked from the header and snapped back to the character SKILLS first
    /// opened on. LoadAsync now keeps the current selection on any call after the first.
    /// </summary>
    [Fact]
    public async Task RefreshModule_KeepsTheCharacterOnScreen_NeverTheOneItFirstOpenedOn()
    {
        using var instance = TestClientInstance.Create();
        await _SeedCharactersAsync(instance, (10, "First"), (20, "Second"));
        var viewModel = new SkillsWindowViewModel(instance.Services, startingCharacterId: 10);
        await viewModel.LoadAsync(Ct);
        await viewModel.GoToCharacterAsync(20); // the pilot picks a different character from the header

        await viewModel.LoadAsync(Ct); // RefreshModule's own call, verbatim — ModuleHostService.Open re-selects the running instance

        Assert.Equal(20, viewModel.SelectedCharacterId);
    }

    /// <summary>
    /// ET-387, criterion A1. Red before this change: <c>EsiSkillImporter</c> wrote straight to the skill/queue/
    /// attribute repositories with nothing telling an already-open window, so TRAINING QUEUE kept showing whatever
    /// it read when it was last (re-)opened, even after a background import for the same character landed.
    /// </summary>
    [AvaloniaFact]
    public async Task ImportAsync_RefreshesTheOpenWindowsTrainingQueue_WithoutReopening()
    {
        var esi = new RoutingEsiClient();
        esi.Responses["/characters/1/skills/"] = new EsiCharacterSkills { Skills = [] };
        esi.Responses["/characters/1/skillqueue/"] = Array.Empty<EsiSkillQueueEntry>();
        esi.Responses["/characters/1/attributes/"] = new EsiCharacterAttributes
        {
            Charisma = 17, Intelligence = 17, Memory = 17, Perception = 17, Willpower = 17
        };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiClient>(esi));
        await _SeedCharactersAsync(instance, (1, "Pilot"));
        var viewModel = new SkillsWindowViewModel(instance.Services, startingCharacterId: 1);
        await viewModel.LoadAsync(Ct);
        Assert.Equal("0/150", viewModel.Queue!.SkillCountText);

        // The background refresh's own call (SkillRefreshService.RefreshAllAsync), verbatim — the window stays open.
        // The queued skill's type id (3300) needs no SDE fixture here: SkillCountText counts future queue entries
        // before the per-row detail loop that resolves each skill's name against the SDE.
        esi.Responses["/characters/1/skillqueue/"] = new[]
        {
            new EsiSkillQueueEntry
            {
                SkillId = 3300, FinishedLevel = 4, QueuePosition = 0,
                StartDate = DateTimeOffset.UtcNow, FinishDate = DateTimeOffset.UtcNow.AddDays(1)
            }
        };
        var importer = instance.Services.GetRequiredService<IEsiSkillImporter>();
        var result = await importer.ImportAsync(1, Ct);
        Assert.True(result.IsSuccess);

        await ActivityWindowHarness.WaitUntil(() => viewModel.Queue!.SkillCountText != "0/150");

        Assert.Equal("1/150", viewModel.Queue!.SkillCountText);
    }
}
