using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunStorageTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task LocalRun_WithoutServer_PersistsAndBuildsSummary()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Guid runId = started.Value;
        Result saved = await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                Source = LootCaptureSource.Clipboard,
                Entries =
                [
                    new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, Volume = 0.01m, ClipboardPrice = 120m, LootKind = LootKind.Gained },
                    new RunLootEntryInput { ItemTypeId = 35, Name = "Pyerite", Quantity = 2, Volume = 0.01m, ClipboardPrice = null, LootKind = LootKind.Gained },
                    new RunLootEntryInput { ItemTypeId = 36, Name = "Mexallon", Quantity = 1, Volume = 0.01m, ClipboardPrice = 20m, LootKind = LootKind.Lost }
                ]
            }],
            [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(5), Isk = 75m }],
            [new RunEnemyObservationInput { Count = 1, EnemyTypeId = 111, EnemyName = "Raid Leader", Direction = EnemyObservationDirection.To, FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }],
            [new RunParameterInput { ParameterKey = RunParameterKey.Smugglers, TypedValue = "3", ObservedAtUtc = StartedAtUtc.AddMinutes(2) }]), cancellationToken);
        Result<int> rebuilt = await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
        Assert.True(rebuilt.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Null(run.GroupCode);
        Assert.Equal(RunSyncState.Local, run.SyncState);
        Assert.Equal(run.Id, summary.RunId);
        Assert.Equal(120m, summary.LootIskGained);
        Assert.Equal(20m, summary.LootIskLost);
        Assert.Equal(100m, summary.LootIskNet);
        Assert.Equal(1, summary.LootEntriesWithoutPrice);
        Assert.Equal(6, summary.LootItemCount);
        Assert.Equal(0.03m, summary.LootVolume);   // the volume column of an EVE inventory is the stack, not one unit
        Assert.Equal(75m, summary.BountyIsk);
        Assert.Equal(2, summary.SourceRevisionSum);
    }

    /// <summary>Loot copied while nothing is running is refused with a reason rather than filed somewhere.</summary>
    [AvaloniaFact]
    public async Task LootCapture_WithoutARunningRun_IsRefusedWithAReason()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<DateTime?> added = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = StartedAtUtc,
            Source = LootCaptureSource.Clipboard,
            ContentHash = "ABC",
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 1, LootKind = LootKind.Gained }]
        }), cancellationToken);

        Assert.False(added.IsSuccess);
        Assert.Contains("No run is running", added.Messages[0].Text);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.Empty(await db.Set<RunLootCapture>().ToListAsync(cancellationToken));
    }

    [AvaloniaFact]
    public async Task Rebuild_CorruptedSummary_RestoresValuesFromRuns()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await _SaveSoloRun(dispatcher, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
            summary.BountyIsk = -1m;
            summary.EnemyTypeCount = 0;
            await db.SaveChangesAsync(cancellationToken);
        }

        Result<int> rebuilt = await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Assert.True(rebuilt.IsSuccess);
        await using ClientDbContext verification = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary restored = Assert.Single(await verification.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(75m, restored.BountyIsk);
        Assert.Equal(1, restored.EnemyTypeCount);
    }

    [AvaloniaFact]
    public async Task Rebuild_GroupRuns_UnionsEnemyTypes()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await _SaveGroupRun(dispatcher, 90000001, 111, cancellationToken);
        await _SaveGroupRun(dispatcher, 90000002, 111, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal("HF-7QK2", summary.GroupCode);
        Assert.Null(summary.RunId);
        Assert.Equal(2, summary.RunsIncluded);
        Assert.Equal(2, summary.ParticipantCount);
        Assert.Equal(1, summary.EnemyTypeCount);
        Assert.True(summary.CompletenessUnknown);
    }

    private static async Task _SaveSoloRun(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Guid runId = started.Value;
        Result saved = await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [],
            [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(5), Isk = 75m }],
            [new RunEnemyObservationInput { Count = 1, EnemyTypeId = 111, EnemyName = "Raid Leader", Direction = EnemyObservationDirection.To, FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }], []), cancellationToken);
        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
    }

    private static async Task _SaveGroupRun(IDispatcher dispatcher, long characterId, int enemyTypeId, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        Guid runId = started.Value;
        Result saved = await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [],
            [new RunEnemyObservationInput { Count = 1, EnemyTypeId = enemyTypeId, EnemyName = "Raid Leader", Direction = EnemyObservationDirection.To, FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }], []), cancellationToken);
        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
    }
}
