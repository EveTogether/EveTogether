using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
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
    private const string ServerAddress = "https://server.invalid";

    [AvaloniaFact]
    public async Task LocalRun_WithoutServer_PersistsBuildsSummaryAndRemainsLocal()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Tritanium and Mexallon are cached; Pyerite is deliberately left out so LootEntriesWithoutPrice still has
        // something to count.
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 34, AveragePrice = 40, AdjustedPrice = 40, UpdatedAt = DateTimeOffset.UtcNow },
            new LocalMarketPrice { TypeId = 36, AveragePrice = 20, AdjustedPrice = 20, UpdatedAt = DateTimeOffset.UtcNow }
        ]);

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
            [new RunEnemyObservationInput { Count = 1, EnemyTypeId = 111, EnemyName = "Raid Leader", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }],
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

    /// <summary>ET-159 AC-1: local-first means a run does not wait for a server sync to show up in the summary.
    /// Counter-proof: drop the rebuild dispatch that SaveRunCommandHandler now sends after SAVE, and this goes red
    /// on zero rows.</summary>
    [AvaloniaFact]
    public async Task SavedRun_WithoutAnyExplicitRebuildOrSync_AppearsInTheSummary()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(started.Value, summary.RunId);
    }

    /// <summary>ET-178 AC-2: a site named from a copied signature the catalogue never carries stores
    /// SiteTypeSource.Uncatalogued, not Site — both still carry SiteTypeId 0, and without the distinct source that
    /// run reads back no differently from one nobody ever named at all. Counter-proof: collapse Uncatalogued back
    /// to Site and the two rows below stop being tellable apart.</summary>
    [AvaloniaFact]
    public async Task ACatalogueLessSiteRun_ReadsBackDifferentlyFromAnUnfilledOne_EvenWithTheSameZeroId()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> catalogueLess = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            StartedAtUtc, 0, "Ruined Blood Raider Crystal Quarry", null, SiteTypeSource: SiteTypeSource.Uncatalogued),
            cancellationToken);
        Result<Guid> unfilled = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site,
            StartedAtUtc, 0, null, null), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run named = await db.Set<Run>().SingleAsync(r => r.Id == catalogueLess.Value, cancellationToken);
        Run blank = await db.Set<Run>().SingleAsync(r => r.Id == unfilled.Value, cancellationToken);
        Assert.Equal(SiteTypeSource.Uncatalogued, named.SiteTypeSource);
        Assert.Equal(SiteTypeSource.Site, blank.SiteTypeSource);
        Assert.Equal(0, named.SiteTypeId);
        Assert.Equal(0, blank.SiteTypeId);
    }

    /// <summary>ET-159 AC-2: loot is valued through the type-id price lookup, never the clipboard's own ISK column —
    /// including when the clipboard carried no price at all but the lookup does. Counter-proof: this is the test
    /// that stood red before the fix (it used to see 1,000,000 from the clipboard instead of 30,000 from the
    /// lookup).</summary>
    [AvaloniaFact]
    public async Task Rebuild_ValuesLootFromThePriceLookup_NotTheClipboard()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 34, AveragePrice = 10_000, AdjustedPrice = 10_000, UpdatedAt = DateTimeOffset.UtcNow },
            new LocalMarketPrice { TypeId = 35, AveragePrice = 500, AdjustedPrice = 500, UpdatedAt = DateTimeOffset.UtcNow }
        ]);

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                Source = LootCaptureSource.Clipboard,
                Entries =
                [
                    // A clipboard price a million times too high for the lookup's 10,000 — the lookup must win.
                    new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, ClipboardPrice = 1_000_000m, LootKind = LootKind.Gained },
                    // No clipboard price at all, yet the lookup knows one — it must still be counted.
                    new RunLootEntryInput { ItemTypeId = 35, Name = "Pyerite", Quantity = 2, ClipboardPrice = null, LootKind = LootKind.Gained }
                ]
            }], [], [], []), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(31_000m, summary.LootIskGained);   // 10,000×3 + 500×2, not 1,000,000
    }

    /// <summary>ET-159 AC-3: "without a price" is decided by the lookup, not by whether the clipboard happened to
    /// carry a figure. Counter-proof: switch the counter back to <c>ClipboardPrice is null</c> and this goes red —
    /// two of the three entries below flip which side of the count they land on.</summary>
    [AvaloniaFact]
    public async Task Rebuild_CountsEntriesWithoutPrice_FromTheLookupNotTheClipboard()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 36, AveragePrice = 20, AdjustedPrice = 20, UpdatedAt = DateTimeOffset.UtcNow }]);

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                Source = LootCaptureSource.Clipboard,
                Entries =
                [
                    // Has a clipboard price but no market price — counts as without a price.
                    new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 1, ClipboardPrice = 100m, LootKind = LootKind.Gained },
                    // Same again, so the aggregate count actually differs from the buggy reading below.
                    new RunLootEntryInput { ItemTypeId = 35, Name = "Pyerite", Quantity = 1, ClipboardPrice = 50m, LootKind = LootKind.Gained },
                    // No clipboard price but a known market price — does not count.
                    new RunLootEntryInput { ItemTypeId = 36, Name = "Mexallon", Quantity = 1, ClipboardPrice = null, LootKind = LootKind.Gained }
                ]
            }], [], [], []), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(2, summary.LootEntriesWithoutPrice);   // the two lookup-misses, not the one null clipboard price
    }

    /// <summary>ET-159 AC-4: the expected payout follows the same equal split <c>RunPayoutSplit</c> makes, over the
    /// activity's payout-eligible runs. Counter-proof: hardcode the field back to <c>0m</c> and this goes red.</summary>
    [AvaloniaFact]
    public async Task Rebuild_SplitsExpectedPayoutAcrossEligibleRuns_RatherThanAConstantZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100_000, AdjustedPrice = 100_000, UpdatedAt = DateTimeOffset.UtcNow }]);

        async Task SaveEligibleRunAsync(long characterId)
        {
            Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
                1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
            await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
                [new RunLootCaptureInput
                {
                    CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                    Source = LootCaptureSource.Clipboard,
                    Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 1, LootKind = LootKind.Gained }]
                }], [], [], []), cancellationToken);
        }
        await SaveEligibleRunAsync(90000001);
        await SaveEligibleRunAsync(90000002);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(200_000m, summary.LootIskGained);
        Assert.Equal(2, summary.PayoutEligibleCount);
        Assert.Equal(100_000m, summary.ExpectedPayoutIsk);   // 200,000 split evenly over the two eligible runs
    }

    /// <summary>Review fix on AC-4: one character can hold more than one payout-eligible run in the same activity
    /// (ET-130), so the split must divide by eligible characters, not eligible runs — dividing by runs would halve
    /// this character's share of their own two runs without anyone noticing.</summary>
    [AvaloniaFact]
    public async Task Rebuild_SplitsExpectedPayoutAcrossEligibleCharacters_NotRuns()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100_000, AdjustedPrice = 100_000, UpdatedAt = DateTimeOffset.UtcNow }]);

        async Task SaveEligibleRunAsync(long characterId)
        {
            Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
                1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
            await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
                [new RunLootCaptureInput
                {
                    CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                    Source = LootCaptureSource.Clipboard,
                    Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 1, LootKind = LootKind.Gained }]
                }], [], [], []), cancellationToken);
        }
        await SaveEligibleRunAsync(90000001);   // this character's second eligible run in the same activity
        await SaveEligibleRunAsync(90000001);
        await SaveEligibleRunAsync(90000002);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(300_000m, summary.LootIskGained);
        Assert.Equal(3, summary.PayoutEligibleCount);   // three eligible runs...
        Assert.Equal(150_000m, summary.ExpectedPayoutIsk);   // ...but 300,000 split over two eligible characters
    }

    /// <summary>Review fix on AC-2/AC-3: <c>LootItemCount</c> already reads a missing quantity as zero pieces, so
    /// the valuation must land on zero too, not on "one piece" — otherwise the same row counts as nothing in one
    /// column and money in another.</summary>
    [AvaloniaFact]
    public async Task Rebuild_ValuesALootEntryWithoutAQuantity_AsZero_LikeItsItemCount()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 10_000, AdjustedPrice = 10_000, UpdatedAt = DateTimeOffset.UtcNow }]);

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                Source = LootCaptureSource.Clipboard,
                Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = null, LootKind = LootKind.Gained }]
            }], [], [], []), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(0m, summary.LootIskGained);
        Assert.Equal(0, summary.LootItemCount);
    }

    /// <summary>ET-159 AC-5: discard unlinks, it does not delete — <c>GroupCode</c> goes null and
    /// <c>FormerGroupCode</c> keeps the audit trail. Two runs that shared a group and were both discarded must land
    /// as two separate activities. Counter-proof: group on <c>GroupCode ?? FormerGroupCode ?? RunId</c> instead, and
    /// the two silently merge back into one.</summary>
    [AvaloniaFact]
    public async Task Rebuild_TwoDiscardedRunsThatSharedAGroup_StayTwoSeparateActivities()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> first = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(first.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        Result<Guid> second = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(second.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);

        await dispatcher.Send(new DiscardRunCommand(first.Value, StartedAtUtc.AddMinutes(16)), cancellationToken);
        await dispatcher.Send(new DiscardRunCommand(second.Value, StartedAtUtc.AddMinutes(16)), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        List<ActivitySummary> summaries = await db.Set<ActivitySummary>().ToListAsync(cancellationToken);
        Assert.Equal(2, summaries.Count);
        Assert.All(summaries, summary => Assert.Null(summary.GroupCode));
    }

    /// <summary>ET-159 AC-6: a run that has not been saved yet is not half an activity — the rebuild filters on
    /// <c>State == Saved</c>. Counter-proof: drop that filter and a running run shows up with
    /// <c>DurationSeconds == 0</c>.</summary>
    [AvaloniaFact]
    public async Task Rebuild_ExcludesARunThatIsNotYetSaved()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);

        Result<int> rebuilt = await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Assert.True(rebuilt.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.Empty(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
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

        Result queued = await dispatcher.Send(new QueueRunForServerSyncCommand(started.Value, ServerAddress), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.True(queued.IsSuccess);
        Assert.Equal(RunSyncState.Pending, run.SyncState);
        Assert.Equal(ServerAddress, run.SyncServerAddress);
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
        const string serverAddress = "https://127.0.0.1:1";
        await dispatcher.Send(new QueueRunForServerSyncCommand(started.Value, serverAddress), cancellationToken);
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
                SyncServerAddress = ServerAddress,
                Revision = 2
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        var synchronization = new RunSynchronizationService(contextFactory, client, applier);

        var synchronized = await synchronization.SynchronizeAsync(ServerAddress, 90000001, cancellationToken);

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

        await applier.ApplyAsync(ServerAddress, [new RunWirePayload
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

        await applier.ApplyAsync(ServerAddress, [new RunWirePayload
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

        await applier.ApplyAsync(ServerAddress, [new RunWirePayload
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
            [new RunEnemyObservationInput { Count = 1, EnemyTypeId = 111, EnemyName = "Raid Leader", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }], []), cancellationToken);
        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
    }

    private static async Task _SaveGroupRun(IDispatcher dispatcher, long characterId, int enemyTypeId, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        Guid runId = started.Value;
        Result saved = await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [],
            [new RunEnemyObservationInput { Count = 1, EnemyTypeId = enemyTypeId, EnemyName = "Raid Leader", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }], []), cancellationToken);
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
