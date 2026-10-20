using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-521: what the BY ACTIVITY list costs when the runs change. The local tab used to read every run of the last year
/// again on every batch of changes; it now recomputes the activities of the changed runs only. The timings are logged,
/// and the assertions hold the part that can be wrong in a way no timing shows: the incremental figures are the figures
/// a cold read gives, and an activity no changed run belongs to is not computed again.
/// </summary>
public sealed class ActivityStatsPerformanceTests(ITestOutputHelper output)
{
    private const long Pilot = 90000001;
    private const int Tritanium = 34;
    private const int FirstSite = 2300;
    private const int SiteCount = 16;
    private static readonly DateTime Today = DateTime.UtcNow.Date;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaTheory]
    [InlineData(80)]
    [InlineData(800)]
    public async Task ChangeBatch_AndOverviewOpen_Timings(int runs)
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = Tritanium, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }], Token);
        for (int index = 0; index < runs; index++)
            await _SaveAsync(dispatcher, index, Today.AddDays(-25).AddMinutes(index * (25 * 24 * 60 / runs)), 10 + index % 7);

        IActivityStatsSource source = instance.Services.GetRequiredService<IActivityStatsSource>();
        var request = new ActivityStatsRequest(Today.AddDays(-30), Today.AddDays(1), null, [(int)Pilot]);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ActivityStatsOverview opened = await source.ReadAsync(request, Token);
        double openMs = watch.Elapsed.TotalMilliseconds;

        List<double> batchMs = [];
        for (int batch = 0; batch < 5; batch++)
        {
            await _SaveAsync(dispatcher, runs + batch, Today.AddHours(batch), 10 + batch);
            watch.Restart();
            await source.ReadAsync(request, Token);
            batchMs.Add(watch.Elapsed.TotalMilliseconds);
        }

        output.WriteLine($"runs={runs} activities={opened.Activities.Count} open={openMs:F0} ms batch(median of 5)={batchMs.Order().ElementAt(2):F0} ms all=[{string.Join(", ", batchMs.Select(ms => ms.ToString("F0")))}]");
        Assert.Equal(SiteCount, opened.Activities.Count);
    }

    /// <summary>A run saved on one site: the next read takes that one activity from the database and no other, and what
    /// it returns is what a read from scratch returns. Counter-proof: a book that drops everything on a change reads all
    /// sixteen, and one that keeps the old sample leaves the site's run count one short.</summary>
    [AvaloniaFact]
    public async Task AChangedRun_RereadsItsActivityOnly_AndGivesTheFiguresAColdReadGives()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceAsync(instance);
        for (int index = 0; index < 5 * SiteCount; index++)
            await _SaveAsync(dispatcher, index, Today.AddDays(-20).AddHours(index));
        await _FiguresAsync(dispatcher);
        ActivityEarningsBook book = instance.Services.GetRequiredService<ActivityEarningsBook>();

        await _SaveAsync(dispatcher, 3, Today.AddDays(-1));
        IReadOnlyList<ActivityEarningsDto> incremental = await _FiguresAsync(dispatcher);

        Assert.Equal(1, book.ActivitiesRead);
        Assert.Equal(6, incremental.Single(activity => activity.Key.Code.EndsWith($":{FirstSite + 3}")).Figures.RunCount);
        await instance.Services.GetRequiredService<IEventBus>().PublishAsync(new RunsChangedEvent(null), EventTarget.Local, Token);
        IReadOnlyList<ActivityEarningsDto> cold = await _FiguresAsync(dispatcher);
        Assert.Equal(5 * SiteCount + 1, book.ActivitiesRead);
        Assert.Equal(cold.Select(_Summary), incremental.Select(_Summary));
    }

    /// <summary>A run deleted out of a fleet group names itself and not the group, yet the group's take loses its loot.
    /// Counter-proof: matching a change on the activity's reference only leaves the group at 2,000.</summary>
    [AvaloniaFact]
    public async Task ARunDeletedOutOfAGroup_ChangesTheGroupsTake()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceAsync(instance);
        for (int index = 0; index < 4; index++)
            await _SaveAsync(dispatcher, 0, Today.AddDays(-5).AddHours(index));
        await _SaveAsync(dispatcher, 0, Today.AddDays(-3), groupCode: "ET-FLEET1");
        Guid second = await _SaveAsync(dispatcher, 0, Today.AddDays(-3), groupCode: "ET-FLEET1", pilot: Pilot + 1);
        Assert.Equal(2_000m, (await _FiguresAsync(dispatcher)).Single().Figures.Best?.YieldIsk);

        await dispatcher.Send(new DeleteRunCommand(second, DateTime.UtcNow), Token);

        ActivityEarningsDto site = (await _FiguresAsync(dispatcher)).Single();
        Assert.Equal(1_000m, site.Figures.Best?.YieldIsk);
        Assert.Equal(5, site.Figures.RunCount);
    }

    private static string _Summary(ActivityEarningsDto activity) =>
        $"{activity.Key.Code}|{activity.Figures.RunCount}|{activity.Figures.Typical}|{activity.Figures.Best?.YieldIsk}|{activity.Figures.Worst?.YieldIsk}";

    private static async Task<IReadOnlyList<ActivityEarningsDto>> _FiguresAsync(IDispatcher dispatcher) =>
        (await dispatcher.Query(new GetActivityEarningsQuery(), Token)).Value ?? [];

    private static Task _PriceAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = Tritanium, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }], Token);

    private static async Task<Guid> _SaveAsync(IDispatcher dispatcher, int site, DateTime startedAtUtc, int tritanium = 10,
        string? groupCode = null, long pilot = Pilot)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(pilot, ActivityKind.Site, startedAtUtc,
            FirstSite + site % SiteCount, $"Site {site % SiteCount}", 30000142, groupCode), Token);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                    ContentHash = Guid.NewGuid().ToString(),
                    Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = tritanium, LootKind = LootKind.Gained }]
                }
            ], [], [], []), Token);
        return started.Value;
    }
}
