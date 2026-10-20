using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-516: the run window hands the panel the activity it is on, with no click, and re-points it when the site
/// changes. The key must be the one the saved runs of that site are filed under, or the panel reads other runs.
/// </summary>
public sealed class ExpectedEarningsWindowTests
{
    private static SdeSite Dungeon(int id, string name) => new(id, name, 1, null, null, null, null, null, false, []);

    [AvaloniaFact]
    public async Task ACopiedSignature_PointsThePanelAtItsSite_WithoutAClick()
    {
        var source = new RecordingSource();
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<IExpectedEarningsSource>(source));
        ActivityWindowViewModel model = await harness.OpenAsync();
        Assert.Null(model.ExpectedEarnings!.Key);
        Assert.False(model.ExpectedEarnings.IsShown);

        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [Dungeon(2311, "Angel Sanctum")]);

        Assert.Equal(ActivityStatsKey.Site(SiteTypeSource.Site, 2311), model.ExpectedEarnings.Key);
        Assert.Equal(ActivityStatsKey.Site(SiteTypeSource.Site, 2311), Assert.Single(source.Asked));
        Assert.True(model.ExpectedEarnings.IsShown);
        Assert.True(model.ExpectedEarnings.IsExpanded);
    }

    /// <summary>A site switched inside the same window: the panel follows, and an unchanged tick asks nothing again.</summary>
    [AvaloniaFact]
    public async Task ASiteSwitchedInTheSameWindow_ChangesTheKey_AndATickDoesNotAskAgain()
    {
        var source = new RecordingSource();
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<IExpectedEarningsSource>(source));
        ActivityWindowViewModel model = await harness.OpenAsync();

        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Drone Cluster", [Dungeon(2001, "Drone Cluster")]);
        model.Refresh(DateTime.UtcNow);
        model.Refresh(DateTime.UtcNow);
        await model.ApplySignatureAsync("BBB-002", "Combat Site", "Sansha Hideaway", [Dungeon(2002, "Sansha Hideaway")]);

        Assert.Equal([ActivityStatsKey.Site(SiteTypeSource.Site, 2001), ActivityStatsKey.Site(SiteTypeSource.Site, 2002)], source.Asked);
    }

    /// <summary>A site the catalogue does not carry is filed by its name, the same way its saved runs are.</summary>
    [AvaloniaFact]
    public async Task AnUncataloguedSite_IsKeyedOnItsName()
    {
        var source = new RecordingSource();
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<IExpectedEarningsSource>(source));
        ActivityWindowViewModel model = await harness.OpenAsync();

        await model.ApplySignatureAsync("CCC-003", "Gas Site", "  Barren   Perimeter Reservoir ", []);

        Assert.Equal(ActivityStatsKey.SiteNamed(SiteTypeSource.Uncatalogued, "barren perimeter reservoir"), model.ExpectedEarnings!.Key);
    }

    [AvaloniaFact]
    public async Task TheCurrentTotalIsk_ReachesThePanel()
    {
        var source = new RecordingSource();
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<IExpectedEarningsSource>(source));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Drone Cluster", [Dungeon(2001, "Drone Cluster")]);

        model.Refresh(DateTime.UtcNow);

        Assert.False(model.ExpectedEarnings!.HasCurrent);
    }

    [Fact]
    public void TheKeyFromAWindow_IsTheKeyOfTheStoredRun()
    {
        Func<string, IReadOnlyList<SdeSite>> catalogue = name => name == "Twin" ? [Dungeon(77, "Twin")] : [];

        Assert.Equal(ActivityStatsKey.Site(SiteTypeSource.Site, 77),
            ActivityStatsKeys.For(ActivityKind.Site, SiteTypeSource.Site, 0, "Twin", null, null, catalogue));
        Assert.Equal(ActivityStatsKey.MissionNamed("the blockade", 3),
            ActivityStatsKeys.For(ActivityKind.Mission, SiteTypeSource.Mission, 0, "The  Blockade", 3, null, catalogue));
        Assert.Equal(ActivityStatsKey.Abyssal(2, "Dark"),
            ActivityStatsKeys.For(ActivityKind.Abyssal, SiteTypeSource.Site, 0, null, null, (2, "Dark"), catalogue));
        Assert.Null(ActivityStatsKeys.For(ActivityKind.Abyssal, SiteTypeSource.Site, 0, null, null, null, catalogue));
        Assert.Null(ActivityStatsKeys.For(ActivityKind.Mining, SiteTypeSource.Site, 0, "Veldspar", null, null, catalogue));
        Assert.Null(ActivityStatsKeys.For(ActivityKind.Site, SiteTypeSource.Site, 0, null, null, null, catalogue));
    }

    private sealed class RecordingSource : IExpectedEarningsSource
    {
        public List<ActivityStatsKey> Asked { get; } = [];

        public Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default)
        {
            Asked.Add(key);
            EarningsFiguresDto none = EarningsStatistics.Compute([], _ => null, EarningsPolicy.For(key.Kind), DateTime.UtcNow);
            return Task.FromResult(new ExpectedEarnings(EarningsBasis.YourRuns, none, 1));
        }

        public Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId, CancellationToken cancellationToken = default, string? activityRef = null) =>
            Task.FromResult(new SharedEarningsAttempt(null, OwnRunsReason.None));
    }
}
