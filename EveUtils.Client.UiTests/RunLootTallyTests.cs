using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Tally;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>The cargo-hold difference: what a run's captures amount to once a starting hold is named.</summary>
public sealed class RunLootTallyTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Quantity 0 means the type is not in that hold at all. The last row is the one that keeps the rule
    /// honest: a stack that did not move is not a loot line worth nothing, it is no line.</summary>
    [Theory]
    [InlineData(0, 5, 5, LootKind.Gained)]
    [InlineData(5, 0, 5, LootKind.Lost)]
    [InlineData(10, 30, 20, LootKind.Gained)]
    [InlineData(30, 10, 20, LootKind.Lost)]
    [InlineData(7, 7, 0, LootKind.Gained)]
    public void Count_WithAStartingHold_IsTheDifferenceBetweenTheTwoHolds(long before, long after, long expected, LootKind expectedKind)
    {
        IReadOnlyList<LootTallyLine> counted = LootTally.Count(
        [
            _Hold(LootCaptureRole.CargoBefore, before),
            _Hold(LootCaptureRole.CargoAfter, after)
        ]);

        if (expected == 0)
        {
            Assert.Empty(counted);
            return;
        }

        LootTallyLine line = Assert.Single(counted);
        Assert.Equal(expected, line.Quantity);
        Assert.Equal(expectedKind, line.LootKind);
    }

    /// <summary>The one that matters: the open window and the saved run count the same run the same way, because
    /// they count it in the same place. Both figures come from the same seeded prices, so a difference between them
    /// can only be a difference in the rule. Counter-proof: give either side its own summing loop back and the two
    /// halves of this test disagree — the store would count the snapshots as loot on top of the difference.</summary>
    [AvaloniaFact]
    public async Task AStartingHold_CountsTheSameInTheWindowAndInTheSavedRun()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow },
            new LocalMarketPrice { TypeId = 35, AveragePrice = 50, AdjustedPrice = 50, UpdatedAt = DateTimeOffset.UtcNow }
        ]);

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc,
            1234, "Abyssal Deadspace", 30000142), Token);
        // A copy from before the hold was named, the hold itself, a moment during the run, and the hold at the end.
        await _CaptureAsync(dispatcher, 1, LootCaptureRole.Snapshot, (34, 3));
        await _CaptureAsync(dispatcher, 2, LootCaptureRole.CargoBefore, (34, 10), (35, 4));
        await _CaptureAsync(dispatcher, 3, LootCaptureRole.Snapshot, (34, 99));
        await _CaptureAsync(dispatcher, 4, LootCaptureRole.CargoAfter, (34, 30), (35, 1));

        var window = new RunLootViewModel(dispatcher, instance.Services.GetRequiredService<IAppraisalProvider>())
        {
            RunId = started.Value
        };
        await window.RefreshAsync(Token);

        Assert.Equal(2_000m, window.LootIsk);      // 20 more Tritanium at 100
        Assert.Equal(150m, window.ConsumedIsk);    // 3 fewer Pyerite at 50 — spent, not loot with a minus sign
        Assert.Equal(1_850m, window.NetIsk);

        Assert.True((await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc,
            StartedAtUtc.AddMinutes(20), [], [], [], []), Token)).IsSuccess);
        Assert.True((await dispatcher.Send(new RebuildActivitySummariesCommand(), Token)).IsSuccess);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(Token));
        Assert.Equal(window.LootIsk, summary.LootIskGained);
        Assert.Equal(window.ConsumedIsk, summary.LootIskLost);
        Assert.Equal(window.NetIsk, summary.LootIskNet);
        Assert.Equal(23, summary.LootItemCount);   // 20 gained + 3 spent, not the 112 pieces that were captured
    }

    private static LootTallyCapture _Hold(LootCaptureRole role, long quantity) =>
        new(role, IsExcluded: false, quantity == 0 ? [] : [new LootTallyLine(34, quantity, Volume: null, LootKind.Gained)]);

    private static async Task _CaptureAsync(IDispatcher dispatcher, int minute, LootCaptureRole role,
        params (int TypeId, long Quantity)[] items)
    {
        Result<RunLootCaptureSaveResult> added = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = StartedAtUtc.AddMinutes(minute),
            Source = role == LootCaptureRole.Snapshot ? LootCaptureSource.Clipboard : LootCaptureSource.Pasted,
            Role = role,
            ContentHash = $"HASH-{minute}",
            Entries = [.. items.Select(item => new RunLootEntryInput
            {
                ItemTypeId = item.TypeId,
                Name = $"Item {item.TypeId}",
                Quantity = item.Quantity,
                LootKind = LootKind.Gained
            })]
        }), Token);
        Assert.True(added.IsSuccess);
    }
}
