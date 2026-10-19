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
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-513: the expected yield out of the own saved runs.</summary>
public sealed class ActivityEarningsQueryTests
{
    private const int SiteTypeId = 2311;
    private const int MetaliminalDungeonId = 10312;
    private const int Tritanium = 34;
    private const int Veldspar = 1230;
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow.Date.AddDays(-10);

    /// <summary>Five activities of one site — four solo runs of 10 Tritanium and one fleet group of two characters
    /// with 10 each — saved while Tritanium was 100 and read once it is 300: the loot counts at 300, the group is one
    /// activity of 20 and not two runs, a site run with no loot counted is left out and reported (Jithran 18-10), and a
    /// mining run beside them has no key. Counter-proof: valuing at the price fixed at capture gives a typical 1,000,
    /// a sample per run gives six runs, and counting the run without loot gives six.</summary>
    [AvaloniaFact]
    public async Task GetActivityEarnings_ValuesLootAtTodaysPrice_AndCountsAFleetGroupAsOneActivity()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        IMarketPriceRepository prices = instance.Services.GetRequiredService<IMarketPriceRepository>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await prices.ReplaceAllAsync([_Price(Tritanium, 100)], cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc, ActivityKind.Site, null, 10, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(1), ActivityKind.Site, null, 10, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(2), ActivityKind.Site, null, 10, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(3), ActivityKind.Site, null, 10, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(4), ActivityKind.Site, "ET-FLEET1", 10, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000002, StartedAtUtc.AddHours(4), ActivityKind.Site, "ET-FLEET1", 10, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(5), ActivityKind.Site, null, 0, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(6), ActivityKind.Mining, null, 10, cancellationToken);
        await prices.ReplaceAllAsync([_Price(Tritanium, 300)], cancellationToken);

        Result<IReadOnlyList<ActivityEarningsDto>> earnings = await dispatcher.Query(new GetActivityEarningsQuery(), cancellationToken);

        ActivityEarningsDto site = Assert.Single(earnings.Value ?? []);
        Assert.Equal(ActivityStatsKey.Site(SiteTypeSource.Site, SiteTypeId), site.Key);
        Assert.Equal(5, site.Figures.RunCount);
        Assert.Equal(1, site.Figures.WithoutLootCount);
        Assert.Equal(EarningsConfidence.Low, site.Figures.Confidence);
        Assert.Equal(3_000m, site.Figures.Typical);
        Assert.Equal(6_000m, site.Figures.Best?.YieldIsk);
        Assert.Equal(2, site.Figures.Best?.FleetSize);
    }

    /// <summary>Jithran 18-10: a homefront's ore is part of what the site yields, valued at today's price like loot;
    /// a site's mining is not. Each mines 100 Veldspar, mined while it was 20 and read at 30, and the site also loots
    /// 10 Tritanium at 300. Counter-proof: leaving the ore out gives the homefront 0, and counting it everywhere gives
    /// the site 6,000.</summary>
    [AvaloniaFact]
    public async Task GetActivityEarnings_CountsAHomefrontsOre_ButNotASitesMining()
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(Tritanium, "Tritanium", 18, 4)
            .Add(Veldspar, "Veldspar", 462, 25)));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        IMarketPriceRepository prices = instance.Services.GetRequiredService<IMarketPriceRepository>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await prices.ReplaceAllAsync([_Price(Tritanium, 300), _Price(Veldspar, 20)], cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc, MetaliminalDungeonId, "Metaliminal Meteoroid", 0, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(1), MetaliminalDungeonId, "Metaliminal Meteoroid", 0, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(2), MetaliminalDungeonId, "Metaliminal Meteoroid", 0, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(3), MetaliminalDungeonId, "Metaliminal Meteoroid", 0, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(4), MetaliminalDungeonId, "Metaliminal Meteoroid", 0, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(5), SiteTypeId, "Angel Sanctum", 10, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(6), SiteTypeId, "Angel Sanctum", 10, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(7), SiteTypeId, "Angel Sanctum", 10, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(8), SiteTypeId, "Angel Sanctum", 10, cancellationToken);
        await _MineAsync(dispatcher, StartedAtUtc.AddHours(9), SiteTypeId, "Angel Sanctum", 10, cancellationToken);
        await prices.ReplaceAllAsync([_Price(Tritanium, 300), _Price(Veldspar, 30)], cancellationToken);

        Result<IReadOnlyList<ActivityEarningsDto>> earnings = await dispatcher.Query(new GetActivityEarningsQuery(), cancellationToken);

        Dictionary<string, decimal?> typicalByKey = (earnings.Value ?? [])
            .ToDictionary(activity => activity.Key.Code, activity => activity.Figures.Typical);
        Assert.Equal(new Dictionary<string, decimal?> { ["homefront:10312"] = 3_000m, ["site:Site:2311"] = 3_000m }, typicalByKey);
    }

    private static async Task _SaveRunAsync(IDispatcher dispatcher, long characterId, DateTime startedAtUtc, ActivityKind kind,
        string? groupCode, int tritanium, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, kind, startedAtUtc, SiteTypeId,
            "Angel Sanctum", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            _Loot(characterId, startedAtUtc, tritanium), [], [], []), cancellationToken);
    }

    private static async Task _MineAsync(IDispatcher dispatcher, DateTime startedAtUtc, int siteTypeId, string siteName,
        int tritanium, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, startedAtUtc, siteTypeId,
            siteName, 30000142), cancellationToken);
        await dispatcher.Send(new AddRunMiningEntryCommand(90000001, startedAtUtc.AddMinutes(5), "Veldspar", 100, false, 0),
            cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            _Loot(90000001, startedAtUtc, tritanium), [], [], []), cancellationToken);
    }

    private static IReadOnlyList<RunLootCaptureInput> _Loot(long characterId, DateTime startedAtUtc, int tritanium) =>
        tritanium == 0
            ? []
            :
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                    ContentHash = $"{characterId}{startedAtUtc:O}",
                    Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = tritanium, LootKind = LootKind.Gained }]
                }
            ];

    private static LocalMarketPrice _Price(int typeId, double price) =>
        new() { TypeId = typeId, AveragePrice = price, AdjustedPrice = price, UpdatedAt = DateTimeOffset.UtcNow };
}
