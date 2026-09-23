using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-324: the home's TODAY | THIS WEEK | this month. Which activities land in which tile is invisible on a
/// render — a run at 00:05 and one at 23:55 look the same on screen — so the boundaries are pinned here.</summary>
public sealed class HomeEarningsTests
{
    private const long CharacterId = 90000001;

    /// <summary>Tuesday 22 September 2026, 21:10 — the mockup's own clock.</summary>
    private static readonly DateTime Now = new(2026, 9, 22, 21, 10, 0);

    private static RunsActivityFacts _Activity(DateTime startedAtLocal, decimal isk) =>
        new(startedAtLocal, TimeSpan.FromMinutes(30), isk,
            new IskBreakdown([new IskContribution(IskSource.Bounty, isk, IskCertainty.Measured)]),
            [], RunTypeId.CombatSite, [CharacterId]);

    /// <summary>The week follows the "Week starts on" setting (ET-297): with Monday, Sunday's run is last week's; with
    /// Sunday, it is this week's. Counter-proof: a week hard-coded to either day fails one of the two.</summary>
    [Fact]
    public void Week_StartsOnTheConfiguredDay()
    {
        RunsActivityFacts[] activities =
        [
            _Activity(new DateTime(2026, 9, 20, 20, 0, 0), 5_000_000m),
            _Activity(new DateTime(2026, 9, 21, 20, 0, 0), 2_000_000m),
            _Activity(new DateTime(2026, 9, 22, 20, 0, 0), 1_000_000m)
        ];

        EarningsPeriodFigures fromMonday = EarningsPeriods.For(EarningsPeriodKind.Week, activities, Now, DayOfWeek.Monday, null);
        EarningsPeriodFigures fromSunday = EarningsPeriods.For(EarningsPeriodKind.Week, activities, Now, DayOfWeek.Sunday, null);

        Assert.Equal(new DateOnly(2026, 9, 21), fromMonday.Start);
        Assert.Equal(3_000_000m, fromMonday.Net);
        Assert.Equal(2, fromMonday.Runs);
        Assert.Equal(new DateOnly(2026, 9, 20), fromSunday.Start);
        Assert.Equal(8_000_000m, fromSunday.Net);
    }

    /// <summary>Each tile compares with the previous period up to the same point in it, never the whole of it: last
    /// Tuesday until 21:10, last week until Tuesday 21:10, August until the 22nd 21:10. Counter-proof: comparing with
    /// the whole previous period counts the run after the cut-off.</summary>
    [Fact]
    public void EachPeriod_ComparesWithTheSamePointInThePreviousOne()
    {
        RunsActivityFacts[] activities =
        [
            _Activity(new DateTime(2026, 8, 22, 21, 0, 0), 4_000_000m),
            _Activity(new DateTime(2026, 8, 22, 21, 30, 0), 9_000_000m),
            _Activity(new DateTime(2026, 9, 15, 21, 0, 0), 1_000_000m),
            _Activity(new DateTime(2026, 9, 15, 21, 30, 0), 9_000_000m),
            _Activity(new DateTime(2026, 9, 21, 21, 0, 0), 3_000_000m),
            _Activity(new DateTime(2026, 9, 21, 21, 30, 0), 9_000_000m),
            _Activity(new DateTime(2026, 9, 22, 20, 0, 0), 6_000_000m)
        ];
        var tracked = new DateOnly(2026, 8, 1);

        Assert.Equal(3_000_000m, EarningsPeriods.For(EarningsPeriodKind.Today, activities, Now, DayOfWeek.Monday, tracked).PreviousNet);
        Assert.Equal(1_000_000m, EarningsPeriods.For(EarningsPeriodKind.Week, activities, Now, DayOfWeek.Monday, tracked).PreviousNet);
        Assert.Equal(4_000_000m, EarningsPeriods.For(EarningsPeriodKind.Month, activities, Now, DayOfWeek.Monday, tracked).PreviousNet);
    }

    /// <summary>With tracking starting inside the previous period there is nothing fair to compare with: no figure,
    /// and the tile says "not tracked" rather than a fake percentage. The design's own case: tracked since 2 Sep, so
    /// August reads "not tracked".</summary>
    [Fact]
    public void APreviousPeriodBeforeTracking_HasNoComparison()
    {
        RunsActivityFacts[] activities = [_Activity(new DateTime(2026, 9, 2, 20, 0, 0), 1_000_000m)];

        EarningsPeriodFigures month = EarningsPeriods.For(EarningsPeriodKind.Month, activities, Now, DayOfWeek.Monday, new DateOnly(2026, 9, 2));
        var tile = new HomeEarningsTileViewModel(EarningsPeriodKind.Month, (_, _) => { });
        tile.Show(month, new HomeEarningsInput(activities, Now, DayOfWeek.Monday, new DateOnly(2026, 9, 2)));

        Assert.Null(month.PreviousNet);
        Assert.Equal("August: not tracked", tile.ComparisonText);
        Assert.Equal("1–22 Sep · tracked since 2 Sep", tile.RangeText);
    }

    /// <summary>"Today" starts at local midnight (decision, ET-324), read through the real query with its local→UTC
    /// conversion: a run five minutes before midnight is yesterday's, five minutes after is today's. With the boundary
    /// removed, any boundary reads as correct.</summary>
    [AvaloniaFact]
    public async Task Today_StartsAtLocalMidnight()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Noahmarr", (int)CharacterId), TestContext.Current.CancellationToken);
        DateTime localMidnightUtc = DateTime.Now.Date.ToUniversalTime();
        await _SaveRunWithBountyAsync(dispatcher, localMidnightUtc.AddMinutes(-5), 9_000_000m);
        await _SaveRunWithBountyAsync(dispatcher, localMidnightUtc.AddMinutes(5), 2_000_000m);

        using var home = new HomeDashboardViewModel(instance.Services, HomeNavigation.None, []);
        await home.LoadAsync();

        Assert.Equal("2M", home.Earnings.Today.IskText);
    }

    /// <summary>ET-195's first case still holds on the new home: the figure is read from saved runs, so a run saved
    /// before a restart counts after it, with nothing live in memory.</summary>
    [AvaloniaFact]
    public async Task Today_SurvivesARestart()
    {
        string instanceName = "uitest-homeearnings-" + Guid.NewGuid().ToString("N");
        using (var first = TestClientInstance.Create(instanceName: instanceName))
        {
            first.KeepDataOnDispose = true;
            await first.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Noahmarr", (int)CharacterId), TestContext.Current.CancellationToken);
            await _SaveRunWithBountyAsync(first.Services.GetRequiredService<IDispatcher>(), DateTime.UtcNow, 4_200_000m);
        }

        using var restarted = TestClientInstance.Create(instanceName: instanceName);
        using var home = new HomeDashboardViewModel(restarted.Services, HomeNavigation.None, []);
        await home.LoadAsync();

        Assert.Equal("4.2M", home.Earnings.Today.IskText);
    }

    private static async Task _SaveRunWithBountyAsync(IDispatcher dispatcher, DateTime startedAtUtc, decimal bountyIsk)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(CharacterId, ActivityKind.Site, startedAtUtc, 1234, "Homefront", 30000142));
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16), [],
            [new RunBountyEntryInput { OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = bountyIsk }], [], []));
        await dispatcher.Send(new RebuildActivitySummariesCommand());
    }
}
