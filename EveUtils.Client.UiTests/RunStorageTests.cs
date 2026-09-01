using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunStorageTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task LocalRun_WithoutServer_PersistsBuildsSummaryAndRemainsLocal()
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

        Result<RunLootCaptureSaveResult> added = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
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

    [AvaloniaFact]
    public async Task QueueRunForServerSync_ExplicitlyMovesLocalRunToPending()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);

        Result queued = await dispatcher.Send(new QueueRunForServerSyncCommand(started.Value), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.True(queued.IsSuccess);
        Assert.Equal(RunSyncState.Pending, run.SyncState);
    }

    [AvaloniaFact]
    public async Task Synchronize_ServerUnavailable_KeepsQueuedRunPending()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        RunSynchronizationService synchronization = instance.Services.GetRequiredService<RunSynchronizationService>();
        IClientSessionStore sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new QueueRunForServerSyncCommand(started.Value), cancellationToken);
        const string serverAddress = "https://127.0.0.1:1";
        await sessions.SaveAsync(serverAddress, new ClientSessionTokens("access", "refresh", "Pilot", 90000001), cancellationToken);

        var synchronized = await synchronization.SynchronizeAsync(serverAddress, 90000001, cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.False(synchronized.Accepted);
        Assert.Equal(RunSyncState.Pending, run.SyncState);
    }

    [AvaloniaFact]
    public async Task Synchronize_PendingRun_PushesBeforePullingFromLatestWaterline()
    {
        using var instance = TestClientInstance.Create();
        IDbContextFactory<ClientDbContext> contextFactory = instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>();
        RunSynchronizationApplier applier = instance.Services.GetRequiredService<RunSynchronizationApplier>();
        var client = new TestRunSyncClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime waterline = StartedAtUtc.AddHours(1);
        await using (ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Set<Run>().Add(new Run
            {
                Id = Guid.CreateVersion7(),
                CharacterId = 90000002,
                GroupCode = "HF-7QK2",
                ActivityKind = ActivityKind.Site,
                State = RunState.Saved,
                StartedAtUtc = StartedAtUtc,
                SavedAtUtc = StartedAtUtc,
                SiteTypeId = 1234,
                SyncState = RunSyncState.Synced,
                LastPushedAtUtc = waterline,
                Revision = 2
            });
            db.Set<Run>().Add(new Run
            {
                Id = Guid.CreateVersion7(),
                CharacterId = 90000001,
                GroupCode = "HF-7QK2",
                ActivityKind = ActivityKind.Site,
                State = RunState.Saved,
                StartedAtUtc = StartedAtUtc,
                SavedAtUtc = StartedAtUtc,
                SiteTypeId = 1234,
                SyncState = RunSyncState.Pending,
                Revision = 2
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        var synchronization = new RunSynchronizationService(contextFactory, client, applier);

        var synchronized = await synchronization.SynchronizeAsync("https://server.invalid", 90000001, cancellationToken);

        Assert.True(synchronized.Accepted);
        Assert.Equal(["push", "pull"], client.Calls);
        Assert.Equal(waterline, client.PulledSinceUtc);
    }

    [AvaloniaFact]
    public async Task ApplyPulledRuns_Tombstone_RemovesRunAndSummary()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        RunSynchronizationApplier applier = instance.Services.GetRequiredService<RunSynchronizationApplier>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            Run local = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
            local.SyncState = RunSyncState.Synced;
            await db.SaveChangesAsync(cancellationToken);
        }
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        await applier.ApplyAsync([new RunWirePayload
        {
            Run = RunWireData.FromEntity(new Run { Id = started.Value, DeletedAtUtc = DateTime.UtcNow }),
            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }], new HashSet<Guid>(), cancellationToken);

        await using ClientDbContext verification = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.Empty(await verification.Set<Run>().ToListAsync(cancellationToken));
        Assert.Empty(await verification.Set<ActivitySummary>().ToListAsync(cancellationToken));
    }

    [AvaloniaFact]
    public async Task ApplyPulledRuns_PendingLocalRun_DoesNotOverwriteIt()
    {
        using var instance = TestClientInstance.Create();
        RunSynchronizationApplier applier = instance.Services.GetRequiredService<RunSynchronizationApplier>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid runId = Guid.CreateVersion7();
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            db.Set<Run>().Add(new Run
            {
                Id = runId,
                CharacterId = 90000001,
                GroupCode = "HF-7QK2",
                ActivityKind = ActivityKind.Site,
                State = RunState.Saved,
                StartedAtUtc = StartedAtUtc,
                SavedAtUtc = StartedAtUtc,
                SiteTypeId = 1234,
                SiteName = "Local Homefront",
                SyncState = RunSyncState.Pending,
                Revision = 2
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await applier.ApplyAsync([new RunWirePayload
        {
            Run = RunWireData.FromEntity(new Run
            {
                Id = runId,
                CharacterId = 90000001,
                GroupCode = "HF-7QK2",
                ActivityKind = ActivityKind.Site,
                State = RunState.Saved,
                StartedAtUtc = StartedAtUtc,
                SavedAtUtc = StartedAtUtc,
                SiteTypeId = 1234,
                SiteName = "Remote Homefront",
                SyncState = RunSyncState.Synced,
                Revision = 1
            }),
            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }], new HashSet<Guid>(), cancellationToken);

        await using ClientDbContext verification = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run local = Assert.Single(await verification.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal("Local Homefront", local.SiteName);
        Assert.Equal(RunSyncState.Pending, local.SyncState);
    }

    [AvaloniaFact]
    public async Task ApplyPulledRuns_RemoteClockAhead_RebasesRunTimes()
    {
        using var instance = TestClientInstance.Create();
        RunSynchronizationApplier applier = instance.Services.GetRequiredService<RunSynchronizationApplier>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime receivedAtUtc = DateTime.UtcNow;
        DateTime remoteNowUtc = receivedAtUtc.AddMinutes(10);
        var remote = new Run
        {
            Id = Guid.CreateVersion7(),
            CharacterId = 90000002,
            GroupCode = "HF-7QK2",
            ActivityKind = ActivityKind.Site,
            State = RunState.Saved,
            StartedAtUtc = remoteNowUtc.AddMinutes(-15),
            StoppedAtUtc = remoteNowUtc,
            SavedAtUtc = remoteNowUtc,
            SiteTypeId = 1234,
            SyncState = RunSyncState.Synced,
            Revision = 2
        };

        await applier.ApplyAsync([new RunWirePayload
        {
            Run = RunWireData.FromEntity(remote),
            SentAtUnixMilliseconds = new DateTimeOffset(remoteNowUtc).ToUnixTimeMilliseconds()
        }], new HashSet<Guid>(), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run stored = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        DateTime stoppedAtUtc = stored.StoppedAtUtc ?? throw new InvalidOperationException("The stopped time was not synchronized.");
        Assert.InRange(stoppedAtUtc, receivedAtUtc.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.InRange(stored.StartedAtUtc, receivedAtUtc.AddMinutes(-16), receivedAtUtc.AddMinutes(-14));
    }

    [AvaloniaFact]
    public void RunWireData_ExcludedLootCapture_PreservesExclusion()
    {
        var run = new Run
        {
            Id = Guid.CreateVersion7(),
            CharacterId = 90000001,
            ActivityKind = ActivityKind.Site,
            State = RunState.Saved,
            StartedAtUtc = StartedAtUtc,
            SiteTypeId = 1234,
            Revision = 1
        };
        run.LootCaptures.Add(new RunLootCapture
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            CapturedAtUtc = StartedAtUtc,
            Source = LootCaptureSource.Clipboard,
            ContentHash = "capture",
            IsExcluded = true
        });

        Run roundTripped = RunWireData.FromEntity(run).ToEntity();

        RunLootCapture capture = Assert.Single(roundTripped.LootCaptures);
        Assert.Equal("capture", capture.ContentHash);
        Assert.True(capture.IsExcluded);
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

    private sealed class TestRunSyncClient : IServerRunSyncClient
    {
        public List<string> Calls { get; } = [];
        public DateTime? PulledSinceUtc { get; private set; }

        public Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
            string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default)
        {
            Calls.Add("push");
            return Task.FromResult((true, "accepted", (DateTime?)DateTime.UtcNow));
        }

        public Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
            string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("pull");
            PulledSinceUtc = sinceUtc;
            return Task.FromResult((true, "accepted", (IReadOnlyList<RunWirePayload>)[]));
        }
    }
}
