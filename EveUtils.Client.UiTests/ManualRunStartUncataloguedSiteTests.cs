using Avalonia.Headless.XUnit;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-388: CCP never publishes exploration relic and data sites in the SDE, so the manual start has to
/// start a site the catalogue does not have — from an earlier run's own name and group, or from a typed name whose
/// group the pilot states. The type is never guessed from the name.</summary>
public sealed class ManualRunStartUncataloguedSiteTests
{
    private const string RuinedSite = "Detected Ruined Rogue Drone Science Outpost";
    private static readonly DateTime StartedAtUtc = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SdeSite CatalogueSite = new(4321, "Sansha's Nest", null, null, null, null, null, null, false, []);

    private static TestClientInstance CreateInstance(params SdeSite[] sites) =>
        TestClientInstance.Create(services =>
        {
            var sde = new FakeSdeAccessor();
            foreach (SdeSite site in sites)
                sde.AddSite(site);
            services.AddSingleton<ISdeAccessor>(sde);
        });

    private static async Task<ManualRunStartViewModel> CreateViewModelAsync(TestClientInstance instance)
    {
        var vm = new ManualRunStartViewModel(instance.Services.GetRequiredService<IDispatcher>(),
            instance.Services.GetRequiredService<ISdeAccessor>(), new RecordingDialogService(),
            kind => new ActivityWindowViewModel(kind, instance.Services),
            [new Character("Manual Pilot", 90000002)]);
        await vm.LoadAsync();
        return vm;
    }

    private static long _nextEarlierCharacterId = 91000000;

    // A character of its own per run: one character has one running run at a time.
    private static Task RecordEarlierRunAsync(TestClientInstance instance, string name, string? group, DateTime startedAtUtc) =>
        instance.Services.GetRequiredService<IDispatcher>().Send(new StartRunCommand(
            Interlocked.Increment(ref _nextEarlierCharacterId), ActivityKind.Site,
            startedAtUtc, 0, name, 30000142, SignatureGroupSnapshot: group, SiteTypeSource: SiteTypeSource.Uncatalogued,
            Origin: RunOrigin.Clipboard), TestContext.Current.CancellationToken);

    private static async Task<Run> StoredRunOfAsync(TestClientInstance instance, long characterId)
    {
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.CharacterId == characterId,
            TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public async Task TypedName_WithNothingInTheCatalogue_StartsUncataloguedUnderTheChosenGroup()
    {
        using var instance = CreateInstance();
        var vm = await CreateViewModelAsync(instance);

        vm.SiteQuery = $"  {RuinedSite}  ";

        Assert.True(vm.AsksSiteGroup);
        Assert.False(vm.StartCommand.CanExecute(null), "a typed name started without saying what kind of site it is");

        vm.SelectedSiteGroup = "Relic Site";
        Assert.True(vm.StartCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);

        Run run = await StoredRunOfAsync(instance, 90000002);
        Assert.Equal(RuinedSite, run.SiteName);
        Assert.Equal(0, run.SiteTypeId);
        Assert.Equal(SiteTypeSource.Uncatalogued, run.SiteTypeSource);
        Assert.Equal("Relic Site", run.SignatureGroupSnapshot);
        Assert.Equal(RunTypeId.RelicSite, RunTypeResolver.Resolve(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId));
    }

    [AvaloniaFact]
    public async Task SuggestionFromAnEarlierRun_StartsWithItsRecordedGroup_WithoutAskingForOne()
    {
        using var instance = CreateInstance();
        await RecordEarlierRunAsync(instance, "Science Outpost", "Data Site", StartedAtUtc);
        var vm = await CreateViewModelAsync(instance);

        vm.SiteQuery = "outpost";

        UncataloguedSiteSuggestionDto suggestion = Assert.Single(vm.SuggestionResults);
        vm.SelectedSuggestion = suggestion;
        Assert.False(vm.AsksSiteGroup);
        Assert.True(vm.StartCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);

        Run run = await StoredRunOfAsync(instance, 90000002);
        Assert.Equal("Science Outpost", run.SiteName);
        Assert.Equal(SiteTypeSource.Uncatalogued, run.SiteTypeSource);
        Assert.Equal("Data Site", run.SignatureGroupSnapshot);
    }

    [AvaloniaFact]
    public async Task ExactNameOfAnEarlierRun_NeedsNoPickAndNoGroup()
    {
        using var instance = CreateInstance();
        await RecordEarlierRunAsync(instance, "Monument Site", "Relic Site", StartedAtUtc);
        var vm = await CreateViewModelAsync(instance);

        vm.SiteQuery = "monument site";

        Assert.False(vm.AsksSiteGroup);
        Assert.True(vm.StartCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);

        Run run = await StoredRunOfAsync(instance, 90000002);
        Assert.Equal("Monument Site", run.SiteName);
        Assert.Equal("Relic Site", run.SignatureGroupSnapshot);
    }

    [AvaloniaFact]
    public async Task CataloguePick_StillStartsAsACatalogueSite_WithItsDungeonId()
    {
        using var instance = CreateInstance(CatalogueSite);
        var vm = await CreateViewModelAsync(instance);

        vm.SiteQuery = "Sansha";
        vm.SelectedOption = Assert.Single(vm.SiteResults);
        Assert.False(vm.AsksSiteGroup);
        await vm.StartCommand.ExecuteAsync(null);

        Run run = await StoredRunOfAsync(instance, 90000002);
        Assert.Equal(SiteTypeSource.Site, run.SiteTypeSource);
        Assert.Equal(CatalogueSite.DungeonId, run.SiteTypeId);
        Assert.Null(run.SignatureGroupSnapshot);
    }

    [AvaloniaFact]
    public async Task ExactCatalogueName_WinsOverFreeText_AndOverAnEarlierRunOfTheSameName()
    {
        using var instance = CreateInstance(CatalogueSite);
        await RecordEarlierRunAsync(instance, "Sansha's Nest", "Combat Site", StartedAtUtc);
        var vm = await CreateViewModelAsync(instance);

        vm.SiteQuery = "sansha's nest";
        vm.SelectedSiteGroup = "Relic Site";

        Assert.False(vm.AsksSiteGroup);
        Assert.Empty(vm.SuggestionResults);
        await vm.StartCommand.ExecuteAsync(null);

        Run run = await StoredRunOfAsync(instance, 90000002);
        Assert.Equal(SiteTypeSource.Site, run.SiteTypeSource);
        Assert.Equal(CatalogueSite.DungeonId, run.SiteTypeId);
        Assert.Null(run.SignatureGroupSnapshot);
    }

    [AvaloniaFact]
    public async Task HalfTypedCatalogueName_DoesNotHideTheCatalogueRows_AndStartsAsFreeTextOnlyOnceAGroupIsChosen()
    {
        using var instance = CreateInstance(CatalogueSite);
        var vm = await CreateViewModelAsync(instance);

        vm.SiteQuery = "Sansha";

        Assert.Single(vm.SiteResults);
        Assert.True(vm.AsksSiteGroup);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SuggestionsAreOnePerName_WithTheGroupOfTheMostRecentRun_AndSkipRunsWithoutAGroup()
    {
        using var instance = CreateInstance();
        await RecordEarlierRunAsync(instance, "Science Outpost", "Data Site", StartedAtUtc);
        await RecordEarlierRunAsync(instance, "Science Outpost", "Relic Site", StartedAtUtc.AddHours(2));
        await RecordEarlierRunAsync(instance, "Science Outpost", "Data Site", StartedAtUtc.AddHours(1));
        await RecordEarlierRunAsync(instance, "No Group Site", null, StartedAtUtc);

        var suggestions = (await instance.Services.GetRequiredService<IDispatcher>()
            .Query(new GetUncataloguedSiteSuggestionsQuery())).Value ?? [];

        UncataloguedSiteSuggestionDto only = Assert.Single(suggestions);
        Assert.Equal(new UncataloguedSiteSuggestionDto("Science Outpost", "Relic Site"), only);
    }

    [AvaloniaFact]
    public async Task EverySiteGroupChoice_ResolvesToItsOwnRunType()
    {
        using var instance = CreateInstance();
        var vm = await CreateViewModelAsync(instance);

        Assert.Equal(
            [RunTypeId.CombatSite, RunTypeId.DataSite, RunTypeId.RelicSite, RunTypeId.GasSite, RunTypeId.OreSite, RunTypeId.Wormhole],
            vm.SiteGroups.Select(group => RunTypeResolver.Resolve(ActivityKind.Site, group)));
    }

    [AvaloniaFact]
    public async Task SwitchingTheKind_DropsATypedNameAndItsGroup()
    {
        using var instance = CreateInstance();
        var vm = await CreateViewModelAsync(instance);
        vm.SiteQuery = RuinedSite;
        vm.SelectedSiteGroup = "Relic Site";

        vm.SelectedActivityKind = ActivityKind.Mining;
        vm.SelectedActivityKind = ActivityKind.Site;

        Assert.Null(vm.SelectedSiteGroup);
        Assert.False(vm.StartCommand.CanExecute(null));
    }
}
