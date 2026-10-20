using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-517: what the BY ACTIVITY list and the run drawer read from the own runs — the activities of a range, the three best
/// and worst with the reference that opens them, every yield for the distribution, and one saved activity's yield at
/// today's price.
/// </summary>
public sealed class ActivityStatsQueryTests
{
    private const int SiteTypeId = 2311;
    private const int Tritanium = 34;
    private const long Pilot = 90000001;
    private static readonly DateTime Today = DateTime.UtcNow.Date;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Six runs a month and a half ago and five this week of one site. A range over this week counts the five and
    /// not the six, and a range over the old runs counts the six: the list's YOURS is the range on screen, not the
    /// statistic window. Counter-proof: drop the range filter and the week reads eleven.</summary>
    [AvaloniaFact]
    public async Task GetActivityEarnings_InARange_CountsOnlyThatRange()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync([_Price(Tritanium, 100)], Token);
        for (int index = 0; index < 6; index++)
            await _SaveAsync(dispatcher, Today.AddDays(-45 + index).AddHours(10), 10 + index);
        for (int index = 0; index < 5; index++)
            await _SaveAsync(dispatcher, Today.AddDays(-4 + index).AddHours(10), 10 * (index + 1));

        ActivityEarningsDto week = Assert.Single((await dispatcher.Query(
            new GetActivityEarningsQuery(FromUtc: Today.AddDays(-7), ToUtc: Today.AddDays(1)), Token)).Value ?? []);
        ActivityEarningsDto old = Assert.Single((await dispatcher.Query(
            new GetActivityEarningsQuery(FromUtc: Today.AddDays(-50), ToUtc: Today.AddDays(-30)), Token)).Value ?? []);

        Assert.Equal(5, week.Figures.RunCount);
        Assert.Equal(3_000m, week.Figures.Typical);
        Assert.Equal(Today.AddHours(10).AddMinutes(15), week.LastFlownUtc);
        Assert.Equal(6, old.Figures.RunCount);
    }

    /// <summary>The three best and worst are the real activities, ordered, and each carries the reference of the pilot's own
    /// activity so the detail can open it. Counter-proof: leave the reference off the run DTO and the detail has nothing to open.</summary>
    [AvaloniaFact]
    public async Task TheDistribution_NamesTheThreeBestAndWorstRuns_WithTheirReference()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync([_Price(Tritanium, 100)], Token);
        Dictionary<int, Guid> runByTritanium = [];
        foreach (int tritanium in new[] { 10, 20, 30, 40, 50, 60, 70 })
            runByTritanium[tritanium] = await _SaveAsync(dispatcher, Today.AddDays(-tritanium / 10).AddHours(10), tritanium);

        EarningsFiguresDto figures = Assert.Single((await dispatcher.Query(new GetActivityEarningsQuery(), Token)).Value ?? []).Figures;

        Assert.Equal([7_000m, 6_000m, 5_000m], figures.BestRuns.Select(run => run.YieldIsk));
        Assert.Equal([1_000m, 2_000m, 3_000m], figures.WorstRuns.Select(run => run.YieldIsk));
        Assert.Equal(runByTritanium[70].ToString(), figures.BestRuns[0].ActivityRef);
        Assert.Equal(runByTritanium[10].ToString(), figures.WorstRuns[0].ActivityRef);
        Assert.Equal(7, figures.Yields.Count);
        Assert.Equal(figures.Worst?.YieldIsk, figures.Yields[0].YieldIsk);
        Assert.Equal(figures.Best?.YieldIsk, figures.Yields[^1].YieldIsk);
    }

    /// <summary>Below the minimum nothing is named: no worst, no best and no distribution, like every other figure.</summary>
    [Fact]
    public void BelowTheMinimum_NoRunIsNamed()
    {
        EarningsSample[] samples = [.. Enumerable.Range(1, 4).Select(index => new EarningsSample
        {
            PilotBucket = "p", CompletedAtUtc = DateTime.UtcNow.AddDays(-index), FixedIsk = index * 1_000_000m,
            LootBasis = LootBasis.CargoDifference, ActivityRef = $"run-{index}"
        })];

        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), DateTime.UtcNow);

        Assert.Null(figures.Typical);
        Assert.Empty(figures.BestRuns);
        Assert.Empty(figures.WorstRuns);
        Assert.Empty(figures.Yields);
    }

    /// <summary>The run drawer's line needs the activity's key and its take at today's price — saved at 100, read at 300.
    /// Counter-proof: the price fixed at capture gives 1,000.</summary>
    [AvaloniaFact]
    public async Task GetActivityYield_ValuesASavedActivityAtTodaysPrice()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        IMarketPriceRepository prices = instance.Services.GetRequiredService<IMarketPriceRepository>();
        await prices.ReplaceAllAsync([_Price(Tritanium, 100)], Token);
        Guid runId = await _SaveAsync(dispatcher, Today.AddHours(9), 10);
        await prices.ReplaceAllAsync([_Price(Tritanium, 300)], Token);

        ActivityYieldDto? yield = (await dispatcher.Query(new GetActivityYieldQuery(runId.ToString()), Token)).Value;
        ActivityYieldDto? unknown = (await dispatcher.Query(new GetActivityYieldQuery("no-such-activity"), Token)).Value;

        Assert.Equal(ActivityStatsKey.Site(SiteTypeSource.Site, SiteTypeId), yield?.Key);
        Assert.Equal(3_000m, yield?.YieldIsk);
        Assert.Null(unknown);
    }

    private static async Task<Guid> _SaveAsync(IDispatcher dispatcher, DateTime startedAtUtc, int tritanium)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, SiteTypeId,
            "Angel Sanctum", 30000142), Token);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                    ContentHash = $"{startedAtUtc:O}",
                    Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = tritanium, LootKind = LootKind.Gained }]
                }
            ], [], [], []), Token);
        return started.Value;
    }

    private static LocalMarketPrice _Price(int typeId, double price) =>
        new() { TypeId = typeId, AveragePrice = price, AdjustedPrice = price, UpdatedAt = DateTimeOffset.UtcNow };
}
