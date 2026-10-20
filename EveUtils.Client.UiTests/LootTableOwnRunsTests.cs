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
/// ET-522: the shape of Jithran's own runs on Crimson Harvest Network Node — an uncatalogued site (id 0, kept by name),
/// solo, with loose pastes of what each container held and no starting or ending hold. Pastes and a cargo difference
/// are the same footing for a loot table, so these runs are the table.
/// </summary>
public sealed class LootTableOwnRunsTests
{
    private const int Tritanium = 34;
    private const int Pyerite = 35;
    private const long Pilot = 90250177;
    private static readonly DateTime Today = DateTime.UtcNow.Date;
    private static readonly ActivityStatsKey Node =
        ActivityStatsKey.SiteNamed(SiteTypeSource.Uncatalogued, "crimson harvest network node");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Six pasted runs, a seventh never pasted and an eighth of 30 s. The six are the table, the seventh is the only
    /// one reported as without a loot count, and the short one is in neither. A type pasted in two containers of one run
    /// drops once for the rate and its quantities add up. Counter-proof: require a cargo difference and the table has
    /// 0 runs and 7 uncounted — the "0 of 5 needed" this was filed for.</summary>
    [AvaloniaFact]
    public async Task PastedRuns_OfAnUncataloguedSite_AreTheLootTable()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync([_Price(Tritanium, 10), _Price(Pyerite, 20)], Token);
        for (int index = 0; index < 6; index++)
            await _SaveAsync(dispatcher, Today.AddHours(index * 2), TimeSpan.FromMinutes(2),
                [[(Tritanium, 10)], [(Tritanium, 20), (Pyerite, 5)], [(Pyerite, 5)]]);
        await _SaveAsync(dispatcher, Today.AddHours(13), TimeSpan.FromMinutes(2), []);
        await _SaveAsync(dispatcher, Today.AddHours(15), TimeSpan.FromSeconds(30), [[(Tritanium, 10)]]);

        LootTableDto table = (await dispatcher.Query(new GetActivityLootTableQuery(Node), Token)).Value
                             ?? throw new InvalidOperationException("no table");

        Assert.Equal(6, table.RunCount);
        Assert.Equal(1, table.UncountedRunCount);
        Assert.Equal(0, table.UnrecognisedRunCount);
        Assert.Equal(LootTableConfidence.Seen, table.Confidence);
        LootTableItemDto tritanium = table.Items.Single(item => item.TypeId == Tritanium);
        Assert.Equal(6, tritanium.RunCount);
        Assert.Equal(1d, tritanium.DropRate, 9);
        Assert.Equal(30d, tritanium.AverageQuantity, 9);
        Assert.Equal(10d, table.Items.Single(item => item.TypeId == Pyerite).AverageQuantity, 9);
        Assert.Equal(500m, table.ExpectedLoot);
    }

    /// <summary>The picker says what a site has to go on with the same rule as the table.</summary>
    [AvaloniaFact]
    public async Task ThePicker_CountsThePastedRuns()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        for (int index = 0; index < 5; index++)
            await _SaveAsync(dispatcher, Today.AddHours(index * 2), TimeSpan.FromMinutes(2), [[(Tritanium, 10)]]);

        IReadOnlyDictionary<ActivityStatsKey, int> counts = (await dispatcher.Query(new GetLootRunCountsQuery(), Token)).Value
                                                              ?? throw new InvalidOperationException("no counts");

        Assert.Equal(5, counts[Node]);
    }

    private static async Task _SaveAsync(IDispatcher dispatcher, DateTime startedAtUtc, TimeSpan duration,
        (int Type, long Quantity)[][] pastes)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, 0,
            "Crimson Harvest Network Node", 30000142, SiteTypeSource: SiteTypeSource.Uncatalogued), Token);
        RunLootCaptureInput[] captures =
        [
            .. pastes.Select((paste, index) => new RunLootCaptureInput
            {
                CapturedAtUtc = startedAtUtc.AddSeconds(index + 1),
                Source = LootCaptureSource.Clipboard,
                ContentHash = $"{startedAtUtc:O}-{index}",
                Entries = [.. paste.Select(line => new RunLootEntryInput
                {
                    ItemTypeId = line.Type, Name = $"Type {line.Type}", Quantity = line.Quantity, LootKind = LootKind.Gained
                })]
            })
        ];
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc + duration, startedAtUtc + duration, captures, [], [], []), Token);
    }

    private static LocalMarketPrice _Price(int typeId, double price) =>
        new() { TypeId = typeId, AveragePrice = price, AdjustedPrice = price, UpdatedAt = DateTimeOffset.UtcNow };
}
