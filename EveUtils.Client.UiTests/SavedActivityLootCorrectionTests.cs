using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-215: the loot of a SAVED activity can still be corrected — a capture left out, counted again, or the list
/// rewritten by hand — through the very commands the run window always used. The three counter-proofs the ticket
/// asks for are the first three tests; each was shown red against the code without the change.
/// </summary>
public sealed class SavedActivityLootCorrectionTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string GroupCode = "HF-AG43";
    private const string ServerAddress = "https://alpha.invalid";

    private static readonly IReadOnlyList<Character> Crew =
        [new("Jithran", 90000001), new("Abnoba Auscent", 90000002), new("Third Toon", 90000003)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Counter-proof 1: leaving a capture out on a saved activity moves every figure that is made of it at once — the
    /// character's subtotal, the group total, TOTAL ISK at the top and the day total on the runs screen. Red without
    /// the change: the command refused nothing but rebuilt nothing either, so TOTAL ISK and the day stayed at 1.000
    /// while the section under them said 700.
    /// </summary>
    [AvaloniaFact]
    public async Task LeavingACaptureOut_OnASavedActivity_MovesSubtotalGroupTotalTotalIskAndTheDay()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceTritaniumAsync(instance);
        await _SaveRunAsync(dispatcher, 90000001, GroupCode, [3, 3]);
        await _SaveRunAsync(dispatcher, 90000002, GroupCode, [4]);
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        Assert.Contains("+1k ISK net", Assert.Single(overview.Tabs[0].Days).SummaryText);
        ActivityDetailViewModel detail = await _DetailAsync(instance, overview);
        Assert.Equal($"{1_000m:N0} ISK", detail.TotalIskText);

        ActivityLootCharacterViewModel jithran = _Block(detail, "Jithran");
        Assert.True(await jithran.Loot.ToggleExcludedAsync(jithran.Loot.Captures[1], Token));
        await ActivityWindowHarness.WaitUntil(() => detail.TotalIskText == $"{700m:N0} ISK"
                                                    && overview.Tabs[0].Days.Single().SummaryText.Contains("+700 ISK net"));

        Assert.Equal($"{300m:N0} ISK", jithran.SubtotalText);
        Assert.Equal($"{400m:N0} ISK", _Block(detail, "Abnoba Auscent").SubtotalText);
        Assert.Equal($"{700m:N0} ISK", detail.LootOverview.NetIskDisplay);
        Assert.Equal($"{700m:N0} ISK", detail.TotalIskText);
        Assert.Contains("+700 ISK net", overview.Tabs[0].Days.Single().SummaryText);
        Assert.Null(detail.StatusMessage);

        // Counting it again puts every figure back.
        Assert.True(await jithran.Loot.ToggleExcludedAsync(jithran.Loot.Captures[1], Token));
        await ActivityWindowHarness.WaitUntil(() => detail.TotalIskText == $"{1_000m:N0} ISK");
        Assert.Equal($"{1_000m:N0} ISK", detail.TotalIskText);
        Assert.Equal($"{600m:N0} ISK", jithran.SubtotalText);
    }

    /// <summary>
    /// Counter-proof 2: a correction rebuilds the activity summary once, whatever the size of the group — ET-210's
    /// five-to-six-second SAVE was that rebuild run once per run. Three characters here, one exclusion and one list
    /// rewritten by hand: two rebuilds, each of them only this activity's. Red without the change on the count (no
    /// rebuild at all, so zero), and red with a rebuild sent per group member on the count again (six).
    /// </summary>
    [AvaloniaFact]
    public async Task ACorrection_RebuildsTheSummaryOnce_NotOncePerRunInTheGroup()
    {
        List<RebuildActivitySummariesCommand> rebuilds = [];
        using var instance = _Instance(services => services.Decorate<ICommandHandler<RebuildActivitySummariesCommand, Result<int>>>(
            (inner, _) => new RecordingRebuildHandler(inner, rebuilds)));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceTritaniumAsync(instance);
        await _SaveRunAsync(dispatcher, 90000001, GroupCode, [3]);
        await _SaveRunAsync(dispatcher, 90000002, GroupCode, [4]);
        await _SaveRunAsync(dispatcher, 90000003, GroupCode, [5]);
        ActivityDetailViewModel detail = await _DetailAsync(instance, await _OverviewAsync(instance));
        rebuilds.Clear();

        ActivityLootCharacterViewModel first = detail.LootOverview.Characters[0];
        Assert.True(await first.Loot.ToggleExcludedAsync(first.Loot.Captures[0], Token));
        Assert.Single(rebuilds);

        ActivityLootCharacterViewModel second = detail.LootOverview.Characters[1];
        second.Loot.BeginLootEditCommand.Execute(null);
        second.Loot.LootText = "Tritanium\t1";
        Assert.True(await second.Loot.ReplaceLootWithTextAsync(Token));
        Assert.Equal(2, rebuilds.Count);
        Assert.All(rebuilds, rebuild => Assert.NotNull(rebuild.ActivityOfRunId));
    }

    /// <summary>
    /// Counter-proof 3: correcting an activity that was published says that the server's copy is now behind, offers
    /// to publish it again, and pushes nothing by itself — the run turns Outdated, never Pending, so a sync the pilot
    /// starts for another activity cannot carry it along. Red without the change: no notice at all, and the run went
    /// on reading Synced as if the server still had what the screen showed.
    /// </summary>
    [AvaloniaFact]
    public async Task CorrectingAPublishedActivity_SaysThePublishedCopyIsBehind_AndPushesNothing()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceTritaniumAsync(instance);
        Guid runId = await _SaveRunAsync(dispatcher, 90000001, groupCode: null, [3, 3]);
        await _MarkPublishedAsync(instance, runId);
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        int republished = 0;
        ActivityDetailViewModel detail = await _DetailAsync(instance, overview, () =>
        {
            republished++;
            return Task.CompletedTask;
        });
        Assert.False(detail.IsPublishedCopyBehind);

        ActivityLootCharacterViewModel block = Assert.Single(detail.LootOverview.Characters);
        Assert.True(await block.Loot.ToggleExcludedAsync(block.Loot.Captures[0], Token));
        await ActivityWindowHarness.WaitUntil(() => detail.IsPublishedCopyBehind
                                                    && _Row(overview).IsBehindServer);

        Assert.True(detail.IsPublishedCopyBehind);
        Assert.True(detail.CanRepublish);
        Assert.Equal("changed since published", _Row(overview).SyncText);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal(RunSyncState.Outdated, run.SyncState);
        Assert.Equal(0, republished);

        await detail.RepublishCommand.ExecuteAsync(null);
        Assert.Equal(1, republished);
    }

    /// <summary>
    /// The pull half of the same promise: a sync that brings the server's older copy of the run back must not lay it
    /// over the correction. Counter-proof: leave the corrected run Synced, as it was before ET-215, and the applier
    /// replaces it — the excluded capture comes back counted.
    /// </summary>
    [AvaloniaFact]
    public async Task APullAfterACorrection_DoesNotLayTheServersOlderCopyBackOverIt()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _SaveRunAsync(dispatcher, 90000001, GroupCode, [3, 3]);
        await _MarkPublishedAsync(instance, runId);
        RunWirePayload serverCopy = new()
        {
            Run = RunWireData.FromEntity(await _RunWithLootAsync(instance, runId)),
            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        RunLootOverview loot = Assert.IsType<RunLootOverview>((await dispatcher.Query(new GetRunLootQuery(runId), Token)).Value);
        Assert.True((await dispatcher.Send(new SetRunLootCaptureExclusionCommand(loot.Captures[1].CaptureId, true), Token)).IsSuccess);

        using (IServiceScope scope = instance.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RunSynchronizationApplier>()
                .ApplyAsync(ServerAddress, [serverCopy], new HashSet<Guid>(), Token);

        Run after = await _RunWithLootAsync(instance, runId);
        Assert.Single(after.LootCaptures, capture => capture.IsExcluded);
        Assert.Equal(RunSyncState.Outdated, after.SyncState);
    }

    /// <summary>
    /// A group saved before loot was filed per character (ET-211) carries all of it on one run. It reads that way:
    /// the character who holds it has the figure, the other has none, and nothing is shared out after the fact.
    /// Counter-proof: split the group's loot evenly over its characters and Abnoba's block shows 300.
    /// </summary>
    [AvaloniaFact]
    public async Task AGroupFromBeforeLootWasFiledPerCharacter_ShowsItWhereItLies_WithoutASplitMadeUpAfterwards()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceTritaniumAsync(instance);
        await _SaveRunAsync(dispatcher, 90000001, GroupCode, [3, 3]);
        await _SaveRunAsync(dispatcher, 90000002, GroupCode, []);

        ActivityDetailViewModel detail = await _DetailAsync(instance, await _OverviewAsync(instance));

        ActivityLootCharacterViewModel jithran = _Block(detail, "Jithran");
        ActivityLootCharacterViewModel abnoba = _Block(detail, "Abnoba Auscent");
        Assert.Equal($"{600m:N0} ISK", jithran.SubtotalText);
        Assert.False(abnoba.Loot.HasCaptures);
        Assert.Equal("no price", abnoba.SubtotalText);
        Assert.Equal($"{600m:N0} ISK", detail.LootOverview.NetIskDisplay);
        Assert.Same(jithran, detail.LootOverview.Characters[0]);   // largest first
    }

    /// <summary>
    /// Only this machine's own pilots' runs are correctable: anyone else's came in from a server and could never be
    /// published back. Counter-proof: leave the block editable and the exclusion lands on a run this pilot does not own.
    /// </summary>
    [AvaloniaFact]
    public async Task SomeoneElsesRun_IsShownReadOnly_AndCannotBeCorrected()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceTritaniumAsync(instance);
        await _SaveRunAsync(dispatcher, 90000001, GroupCode, [3]);
        await _SaveRunAsync(dispatcher, 90000009, GroupCode, [4]);
        RunsOverviewViewModel overview = await _OverviewAsync(instance);

        ActivityDetailViewModel detail = await _DetailAsync(instance, overview);

        ActivityLootCharacterViewModel theirs = Assert.Single(detail.LootOverview.Characters, block => block.CharacterId == 90000009);
        Assert.True(theirs.Loot.IsReadOnly);
        Assert.False(theirs.Loot.CanEditLoot);
        Assert.False(await theirs.Loot.ToggleExcludedAsync(theirs.Loot.Captures[0], Token));
        Assert.False(theirs.Loot.Captures[0].IsExcluded);
        Assert.False(_Block(detail, "Jithran").Loot.IsReadOnly);
    }

    private sealed class RecordingRebuildHandler(
        ICommandHandler<RebuildActivitySummariesCommand, Result<int>> inner, List<RebuildActivitySummariesCommand> rebuilds)
        : ICommandHandler<RebuildActivitySummariesCommand, Result<int>>
    {
        public Task<Result<int>> Handle(RebuildActivitySummariesCommand command, CancellationToken cancellationToken = default)
        {
            rebuilds.Add(command);
            return inner.Handle(command, cancellationToken);
        }
    }

    private static TestClientInstance _Instance(Action<IServiceCollection>? configure = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(34, "Tritanium", 18, 4));
            configure?.Invoke(services);
        });

    private static Task _PriceTritaniumAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            Token);

    private static async Task<Guid> _SaveRunAsync(IDispatcher dispatcher, long characterId, string? groupCode,
        IReadOnlyList<long> captureQuantities)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Blood Refuge", 30000142, groupCode), Token);
        Assert.True(started.IsSuccess);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [.. captureQuantities.Select((quantity, index) => new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(5 + index), Source = LootCaptureSource.Clipboard,
                ContentHash = $"{characterId}-{index}",
                Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
            })], [], [], []), Token);
        Assert.True(saved.IsSuccess);
        return started.Value;
    }

    private static async Task<RunsOverviewViewModel> _OverviewAsync(TestClientInstance instance)
    {
        var overview = new RunsOverviewViewModel(instance.Services.GetRequiredService<IDispatcher>(),
            new RecordingDialogService(), instance.Services, Crew, runClock: false);
        await overview.LoadAsync(Token);
        return overview;
    }

    /// <summary>Built the way the runs screen builds it (<c>RunsOverviewViewModel._OpenDetailAsync</c>): the appraisal
    /// provider the run window prices with, the SDE the list by hand is read against, and this machine's pilots.</summary>
    private static async Task<ActivityDetailViewModel> _DetailAsync(TestClientInstance instance, RunsOverviewViewModel overview,
        Func<Task>? republish = null)
    {
        var detail = new ActivityDetailViewModel(instance.Services.GetRequiredService<IDispatcher>(), _Row(overview).ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            id => Crew.FirstOrDefault(character => character.EsiCharacterId == id)?.Name ?? $"character {id}",
            sde: instance.Services.GetRequiredService<ISdeAccessor>(),
            ownCharacterIds: Crew.Select(character => (long)character.EsiCharacterId.GetValueOrDefault()).ToHashSet(),
            republish: republish);
        await detail.LoadAsync(Token);
        return detail;
    }

    private static ActivityOverviewRowViewModel _Row(RunsOverviewViewModel overview) =>
        Assert.Single(Assert.Single(overview.Tabs[0].Days).Rows);

    private static ActivityLootCharacterViewModel _Block(ActivityDetailViewModel detail, string name) =>
        Assert.Single(detail.LootOverview.Characters, block => block.CharacterText == name);

    /// <summary>What <c>RunSynchronizationService._MarkSyncedAsync</c> leaves behind once a server accepted the push.</summary>
    private static async Task _MarkPublishedAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        await db.Set<Run>().Where(run => run.Id == runId).ExecuteUpdateAsync(properties => properties
            .SetProperty(run => run.SyncState, RunSyncState.Synced)
            .SetProperty(run => run.SyncServerAddress, ServerAddress)
            .SetProperty(run => run.LastPushedAtUtc, DateTime.UtcNow), Token);
    }

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId, Token);
    }

    private static async Task<Run> _RunWithLootAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        return await db.Set<Run>().AsNoTracking()
            .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.EnemyObservations)
            .Include(run => run.Parameters)
            .SingleAsync(run => run.Id == runId, Token);
    }
}
