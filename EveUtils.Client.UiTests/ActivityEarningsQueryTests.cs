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

/// <summary>ET-513: the expected yield out of the own saved runs.</summary>
public sealed class ActivityEarningsQueryTests
{
    private const int SiteTypeId = 2311;
    private const int Tritanium = 34;
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow.Date.AddDays(-10);

    /// <summary>Five activities of one site — four solo runs of 10 Tritanium and one fleet group of two characters
    /// with 10 each — saved while Tritanium was 100 and read once it is 300: the loot counts at 300, the group is one
    /// activity of 20 and not two runs, and a mining run beside them has no key. Counter-proof: valuing at the price
    /// fixed at capture gives a typical 1,000, and a sample per run gives six runs.</summary>
    [AvaloniaFact]
    public async Task GetActivityEarnings_ValuesLootAtTodaysPrice_AndCountsAFleetGroupAsOneActivity()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        IMarketPriceRepository prices = instance.Services.GetRequiredService<IMarketPriceRepository>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await prices.ReplaceAllAsync([_Price(100)], cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc, ActivityKind.Site, null, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(1), ActivityKind.Site, null, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(2), ActivityKind.Site, null, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(3), ActivityKind.Site, null, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(4), ActivityKind.Site, "ET-FLEET1", cancellationToken);
        await _SaveRunAsync(dispatcher, 90000002, StartedAtUtc.AddHours(4), ActivityKind.Site, "ET-FLEET1", cancellationToken);
        await _SaveRunAsync(dispatcher, 90000001, StartedAtUtc.AddHours(5), ActivityKind.Mining, null, cancellationToken);
        await prices.ReplaceAllAsync([_Price(300)], cancellationToken);

        Result<IReadOnlyList<ActivityEarningsDto>> earnings = await dispatcher.Query(new GetActivityEarningsQuery(), cancellationToken);

        ActivityEarningsDto site = Assert.Single(earnings.Value ?? []);
        Assert.Equal(ActivityStatsKey.Site(SiteTypeSource.Site, SiteTypeId), site.Key);
        Assert.Equal(5, site.Figures.RunCount);
        Assert.Equal(EarningsConfidence.Low, site.Figures.Confidence);
        Assert.Equal(3_000m, site.Figures.Typical);
        Assert.Equal(6_000m, site.Figures.Best?.YieldIsk);
        Assert.Equal(2, site.Figures.Best?.FleetSize);
    }

    private static async Task _SaveRunAsync(IDispatcher dispatcher, long characterId, DateTime startedAtUtc, ActivityKind kind,
        string? groupCode, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, kind, startedAtUtc, SiteTypeId,
            "Angel Sanctum", 30000142, groupCode), cancellationToken);
        RunLootCaptureInput loot = new()
        {
            CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard, ContentHash = $"{characterId}{startedAtUtc:O}",
            Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 10, LootKind = LootKind.Gained }]
        };
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [loot], [], [], []), cancellationToken);
    }

    private static LocalMarketPrice _Price(double price) =>
        new() { TypeId = Tritanium, AveragePrice = price, AdjustedPrice = price, UpdatedAt = DateTimeOffset.UtcNow };
}
