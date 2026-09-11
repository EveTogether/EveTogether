using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ICqrsCommand = EveUtils.Shared.Cqrs.ICommand;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-222: the runs overview, the dashboard and an open detail screen keep up with a change made anywhere else,
/// without being reopened. One test per gap in the ticket's table, each shown red against the code before this change
/// — the screen it names stayed as it was until it was opened again — and the pitfalls the ticket names: a refresh that
/// folds a day or loses an open row, a list half-typed by hand, a burst of payouts.
/// </summary>
public sealed class RunsLiveRefreshTests
{
    private const long Pilot = 90000001;
    private const long Crewmate = 90000002;
    private const string GroupCode = "HF-LV22";
    private const string ServerAddress = "https://server.invalid";
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Character> Crew = [new("Ra Vinter", (int)Pilot), new("Kav Orn", (int)Crewmate)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Table row 1: a run stopped from its own window while the overview sits open lands in UNFINISHED.
    /// Red before: STOP only ever reloaded the RUNNING band, so the run left its lane and appeared nowhere.</summary>
    [AvaloniaFact]
    public async Task RunStoppedElsewhere_AppearsInUnfinished_WithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _StartAsync(dispatcher, DateTime.UtcNow.AddMinutes(-20));
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        Assert.Empty(overview.UnfinishedRuns);

        await dispatcher.Send(new SetRunStoppedCommand(runId, DateTime.UtcNow), Token);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(runId, Assert.Single(overview.UnfinishedRuns).RunId);
        Assert.All(overview.Lanes, lane => Assert.False(lane.IsRunning));
    }

    /// <summary>Table row 2: a run saved while the overview sits open leaves UNFINISHED and lands under its day. Red
    /// before: a save refilled the days only, so the same run stood in both places at once.</summary>
    [AvaloniaFact]
    public async Task RunSavedElsewhere_LeavesUnfinished_WithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _StartAsync(dispatcher, DateTime.UtcNow.AddMinutes(-20));
        await dispatcher.Send(new SetRunStoppedCommand(runId, DateTime.UtcNow.AddMinutes(-5)), Token);
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        Assert.Single(overview.UnfinishedRuns);

        await dispatcher.Send(new SaveRunCommand(runId, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, [], [], [], []), Token);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(overview.UnfinishedRuns);
        Assert.Single(Assert.Single(overview.Tabs[0].Days).Rows);
    }

    /// <summary>Table rows 5 and 6: the last kill's payout lands a beat after STOP, and the UNFINISHED row's total
    /// moves with it. Red before: a bounty line published nothing at all, so the row went on reading what it read when
    /// the screen was opened.</summary>
    [AvaloniaFact]
    public async Task BountyLandingAfterStop_MovesTheUnfinishedTotal_WithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _StartAsync(dispatcher, DateTime.UtcNow.AddMinutes(-20));
        await dispatcher.Send(new SetRunStoppedCommand(runId, DateTime.UtcNow.AddMinutes(-1)), Token);
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        string before = Assert.Single(overview.UnfinishedRuns).TotalIskText;

        await dispatcher.Send(new AddRunBountyEntryCommand(Pilot, DateTime.UtcNow, 1_250_000m), Token);
        Dispatcher.UIThread.RunJobs();

        string after = Assert.Single(overview.UnfinishedRuns).TotalIskText;
        Assert.NotEqual(before, after);
        Assert.Equal("1,250,000 ISK", after);
    }

    /// <summary>Table row 8, the widest gap: an activity a server hands back appears on the open overview. Red before:
    /// neither the pull nor the rebuild after it published anything.</summary>
    [AvaloniaFact]
    public async Task ActivityPulledFromAServer_AppearsWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        Assert.Empty(overview.Tabs[0].Days);

        await _PullCrewmateRunAsync(instance);
        Dispatcher.UIThread.RunJobs();

        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(overview.Tabs[0].Days).Rows);
        Assert.Equal("Homefront", row.SiteText);
    }

    /// <summary>Table row 7: queueing an activity for a server says "queued" on its row straight away. Red before: the
    /// queue command published nothing, and only the publish flow's own reload ever showed it.</summary>
    [AvaloniaFact]
    public async Task ActivityQueuedForAServer_SaysSoOnItsRow_WithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _SaveAsync(dispatcher, Pilot, StartedAtUtc);
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        Assert.False(_OnlyRow(overview).IsQueuedForServer);

        await dispatcher.Send(new QueueRunForServerSyncCommand(runId, ServerAddress), Token);
        Dispatcher.UIThread.RunJobs();

        Assert.True(_OnlyRow(overview).IsQueuedForServer);
    }

    /// <summary>
    /// AC-5: a refresh folds no day, closes no row and moves nothing on screen. Ten activities on one evening, the
    /// first one opened, the list scrolled to its end — then a save lands on an older evening. The rows that did not
    /// change are the very same objects afterwards, the opened one still open with its runs, and the last row sits
    /// exactly where it sat. Red with the refill this replaces: every row is rebuilt closed, the rows below the
    /// opened one move up by the height of its runs, and the evening's band is a new object.
    /// </summary>
    [AvaloniaFact]
    public async Task ALiveRefresh_KeepsOpenRowsOpen_AndLeavesWhatIsOnScreenWhereItIs()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        for (int index = 0; index < 10; index++)
            await _SaveAsync(dispatcher, Pilot, StartedAtUtc.AddMinutes(index * 20), rebuild: false);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), Token);

        (RunsOverviewViewModel overview, Window root) = await _PresentAsync(instance, height: 700);
        RunsDayViewModel day = Assert.Single(overview.Tabs[0].Days);
        ActivityOverviewRowViewModel opened = day.Rows[0];
        await opened.ToggleCommand.ExecuteAsync(null);
        Assert.NotEmpty(opened.SubRuns);
        ActivityOverviewRowViewModel[] rowsBefore = [.. day.Rows];
        ScrollViewer scroller = _Scroller(root);
        root.UpdateLayout();
        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        root.UpdateLayout();
        Vector offsetBefore = scroller.Offset;
        Point lastRowBefore = _PositionOf(root, scroller, day.Rows[^1]);

        await _SaveAsync(dispatcher, Crewmate, StartedAtUtc.AddDays(-7));
        await ActivityWindowHarness.WaitUntil(() => overview.Tabs[0].Days.Count == 2);
        root.UpdateLayout();

        Assert.Equal(2, overview.Tabs[0].Days.Count);
        Assert.Same(day, overview.Tabs[0].Days.Single(band => band.Day == day.Day));
        Assert.True(day.IsExpanded);
        Assert.Equal(rowsBefore, day.Rows);
        Assert.True(opened.IsExpanded);
        Assert.NotEmpty(opened.SubRuns);
        Assert.Equal(offsetBefore, scroller.Offset);
        Assert.Equal(lastRowBefore, _PositionOf(root, scroller, day.Rows[^1]));
    }

    /// <summary>An opened row whose activity did change is a new row in the same place, still open, its runs read
    /// again. Red with the refill this replaces: the row comes back closed.</summary>
    [AvaloniaFact]
    public async Task AChangedRow_ComesBackOpen_WhenItWasOpen()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _SaveAsync(dispatcher, Pilot, StartedAtUtc);
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        ActivityOverviewRowViewModel before = _OnlyRow(overview);
        await before.ToggleCommand.ExecuteAsync(null);

        await dispatcher.Send(new QueueRunForServerSyncCommand(runId, ServerAddress), Token);
        await ActivityWindowHarness.WaitUntil(() => _OnlyRow(overview).IsQueuedForServer && _OnlyRow(overview).SubRuns.Count > 0);

        ActivityOverviewRowViewModel after = _OnlyRow(overview);
        Assert.NotSame(before, after);
        Assert.True(after.IsExpanded);
        Assert.Single(after.SubRuns);
    }

    /// <summary>Table rows 3 and 4 on the dashboard: deleting a saved activity, and undoing it, move "ISK today"
    /// without a REFRESH. Red before: the dashboard listened to a save and a loot correction and nothing else.</summary>
    [AvaloniaFact]
    public async Task DeletingAndRestoringASavedActivity_MovesIskToday_WithoutRefresh()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Ra Vinter", (int)Pilot), Token);
        Guid runId = await _SaveAsync(dispatcher, Pilot, DateTime.UtcNow.AddMinutes(-30),
            bounties: [new RunBountyEntryInput { OccurredAtUtc = DateTime.UtcNow.AddMinutes(-20), Isk = 3_000_000m }]);
        var home = new HomeDashboardViewModel(instance.Services, []);
        await home.RebuildRosterAsync();
        Assert.Equal("3.0M", home.IskTodayText);

        await dispatcher.Send(new DeleteRunCommand(runId, DateTime.UtcNow), Token);
        await ActivityWindowHarness.WaitUntil(() => home.IskTodayText == "0");
        Assert.Equal("0", home.IskTodayText);

        await dispatcher.Send(new RestoreRunCommand(runId), Token);
        await ActivityWindowHarness.WaitUntil(() => home.IskTodayText == "3.0M");
        Assert.Equal("3.0M", home.IskTodayText);
    }

    /// <summary>The detail screen had no subscription at all: a loot correction made in a second window left it
    /// showing the old figures. Opened the way a click opens it, through the runs screen's own row.</summary>
    [AvaloniaFact]
    public async Task ADetailScreen_FollowsALootCorrectionMadeElsewhere()
    {
        using var instance = _PricedInstance();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await _PriceTritaniumAsync(instance);
        await _SaveAsync(dispatcher, Pilot, StartedAtUtc, captureQuantities: [3, 3]);
        ActivityDetailViewModel detail = await _DetailThroughTheRowAsync(instance);
        Assert.Equal($"{600m:N0} ISK", detail.TotalIskText);

        Guid secondCapture = detail.Loot().LootOverview.Characters[0].Loot.Captures[1].CaptureId;
        await dispatcher.Send(new SetRunLootCaptureExclusionCommand(secondCapture, true), Token);
        await ActivityWindowHarness.WaitUntil(() => detail.TotalIskText == $"{300m:N0} ISK");

        Assert.Equal($"{300m:N0} ISK", detail.TotalIskText);
        Assert.True(detail.Loot().LootOverview.Characters[0].Loot.Captures[1].IsExcluded);
    }

    /// <summary>A group-mate's run a server hands back joins the open detail screen of that activity. It is only
    /// recognisable by the group code — none of the runs the screen already shows changed.</summary>
    [AvaloniaFact]
    public async Task ADetailScreen_TakesInAGroupMatesRunPulledFromAServer()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await _SaveAsync(dispatcher, Pilot, StartedAtUtc, groupCode: GroupCode);
        ActivityDetailViewModel detail = await _DetailThroughTheRowAsync(instance);
        Assert.Single(detail.Fleet().RunRows);

        await _PullCrewmateRunAsync(instance, GroupCode);
        await ActivityWindowHarness.WaitUntil(() => detail.Fleet().RunRows.Count == 2);

        Assert.Equal(2, detail.Fleet().RunRows.Count);
    }

    /// <summary>An activity deleted from somewhere else — a second detail window, the runs screen — shows ET-214's
    /// deleted state here, and Undo from here puts it back. Red before: the screen went on showing the activity, and a
    /// delete pressed on it answered "The activity no longer exists".</summary>
    [AvaloniaFact]
    public async Task ADetailScreen_OnAnActivityDeletedElsewhere_SaysSo_AndUndoPutsItBack()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _SaveAsync(dispatcher, Pilot, StartedAtUtc);
        ActivityDetailViewModel detail = await _DetailThroughTheRowAsync(instance);

        await dispatcher.Send(new DeleteRunCommand(runId, DateTime.UtcNow), Token);
        await ActivityWindowHarness.WaitUntil(() => detail.IsDeleted);
        Assert.True(detail.IsDeleted);
        Assert.False(detail.CanDelete);

        await detail.UndoDeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(detail.IsDeleted);
        Assert.True(detail.CanDelete);
        Assert.Null(detail.StatusMessage);
        Assert.Equal("Homefront", detail.SiteText);
    }

    /// <summary>...and one restored from somewhere else comes back on this screen by itself.</summary>
    [AvaloniaFact]
    public async Task ADetailScreen_OnAnActivityRestoredElsewhere_ShowsItAgain()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _SaveAsync(dispatcher, Pilot, StartedAtUtc);
        ActivityDetailViewModel detail = await _DetailThroughTheRowAsync(instance);
        await dispatcher.Send(new DeleteRunCommand(runId, DateTime.UtcNow), Token);
        await ActivityWindowHarness.WaitUntil(() => detail.IsDeleted);
        Assert.True(detail.IsDeleted);

        await dispatcher.Send(new RestoreRunCommand(runId), Token);
        await ActivityWindowHarness.WaitUntil(() => !detail.IsDeleted);

        Assert.False(detail.IsDeleted);
        Assert.Null(detail.StatusMessage);
    }

    /// <summary>
    /// The pitfall cockpit-assistant named on the design: a change landing while the pilot is writing the loot out by
    /// hand on the detail screen must not swap the captures out from under the list being typed from them. The typed
    /// text stays, the captures stay as they were until the edit ends, and the change is shown the moment it does.
    /// Red with the block read straight away (<c>LoadAsync</c> instead of <c>LoadWhenIdleAsync</c>): the capture turns
    /// excluded under the open edit.
    /// </summary>
    [AvaloniaFact]
    public async Task AChangeDuringAnOpenLootEdit_WaitsForTheEdit_AndLeavesTheTypedListAlone()
    {
        using var instance = _PricedInstance();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await _PriceTritaniumAsync(instance);
        await _SaveAsync(dispatcher, Pilot, StartedAtUtc, captureQuantities: [3, 3]);
        ActivityDetailViewModel detail = await _DetailThroughTheRowAsync(instance);
        RunLootViewModel loot = detail.Loot().LootOverview.Characters[0].Loot;
        loot.BeginLootEditCommand.Execute(null);
        loot.LootText = "Tritanium\t9";

        await dispatcher.Send(new SetRunLootCaptureExclusionCommand(loot.Captures[1].CaptureId, true), Token);
        await ActivityWindowHarness.WaitUntil(() => detail.TotalIskText == $"{300m:N0} ISK");

        Assert.Equal($"{300m:N0} ISK", detail.TotalIskText);
        Assert.True(loot.IsEditingLoot);
        Assert.Equal("Tritanium\t9", loot.LootText);
        Assert.False(loot.Captures[1].IsExcluded);

        loot.CancelLootEditCommand.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => loot.Captures[1].IsExcluded);

        Assert.True(loot.Captures[1].IsExcluded);
    }

    /// <summary>cockpit-assistant's question on the design: ET-215 made the summary id stable, so why would a screen
    /// have to follow a restored activity to a new one? Because a delete of the whole activity removed its summary
    /// row, and the restore built a new row with a freshly drawn id. It is now derived from the activity's own key,
    /// so the restore brings back the same one. Red before: a new id.</summary>
    [AvaloniaFact]
    public async Task AnActivityDeletedWholeAndRestored_KeepsItsSummaryId()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Guid runId = await _SaveAsync(dispatcher, Pilot, StartedAtUtc);
        Guid before = _OnlyRow(await _OverviewAsync(instance)).ActivitySummaryId;

        await dispatcher.Send(new DeleteRunCommand(runId, DateTime.UtcNow), Token);
        await dispatcher.Send(new RestoreRunCommand(runId), Token);

        Assert.Equal(before, _OnlyRow(await _OverviewAsync(instance)).ActivitySummaryId);
    }

    /// <summary>
    /// AC-4, measured: two hundred payouts landing while the overview is open over three hundred saved activities, with
    /// the window the app really runs with. The overview is read a handful of times, not two hundred; the figures go
    /// to the test output. Red with no window (every signal its own read): two hundred reads.
    /// </summary>
    [AvaloniaFact]
    public async Task ABurstOfPayouts_ReadsTheOverviewAHandfulOfTimes_NotOncePerPayout()
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton(provider =>
            new RunChangeFeed(provider.GetRequiredService<IEventBus>(), provider.GetRequiredService<ILogger<RunChangeFeed>>())));
        await _SeedSavedActivitiesAsync(instance, 300);
        var dispatcher = new CountingDispatcher(_Dispatcher(instance));
        await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, DateTime.UtcNow.AddMinutes(-10), 1234,
            "Homefront", 30000142), Token);

        var overview = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services, Crew, runClock: false);
        Stopwatch load = Stopwatch.StartNew();
        await overview.LoadAsync(Token);
        load.Stop();
        // One page of them: the overview never reads the whole history, whatever a refresh is answering.
        Assert.Equal(50, overview.Tabs[0].Days.Sum(day => day.Rows.Count));
        dispatcher.OverviewReads = 0;

        Stopwatch burst = Stopwatch.StartNew();
        for (int payout = 0; payout < 200; payout++)
        {
            await dispatcher.Send(new AddRunBountyEntryCommand(Pilot, DateTime.UtcNow, 10_000m), Token);
            await Task.Delay(5, Token);
        }
        burst.Stop();
        await Task.Delay(RunChangeFeed.DefaultWindow * 3, Token);
        Dispatcher.UIThread.RunJobs();

        int reads = dispatcher.OverviewReads;
        // A whole re-read, warm: LoadAsync does everything a refresh does and the auto-save and server tabs besides.
        Stopwatch reread = Stopwatch.StartNew();
        await overview.LoadAsync(Token);
        reread.Stop();

        int ceiling = (int)(burst.Elapsed / RunChangeFeed.DefaultWindow) + 2;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"300 activities stored, a page of 50 shown: first load {load.ElapsedMilliseconds} ms. "
            + $"200 payouts over {burst.ElapsedMilliseconds} ms read the overview {reads} times (ceiling {ceiling}); "
            + $"a warm whole re-read takes {reread.ElapsedMilliseconds} ms.");
        Assert.InRange(reads, 1, ceiling);
    }

    private static ICqrsDispatcher _Dispatcher(TestClientInstance instance) => instance.Services.GetRequiredService<ICqrsDispatcher>();

    private static TestClientInstance _PricedInstance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(34, "Tritanium", 18, 4)));

    private static Task _PriceTritaniumAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            Token);

    private static async Task<Guid> _StartAsync(ICqrsDispatcher dispatcher, DateTime startedAtUtc, string? groupCode = null,
        long characterId = Pilot)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAtUtc, 1234,
            "Homefront", 30000142, groupCode), Token);
        Assert.True(started.IsSuccess);
        return started.Value;
    }

    private static async Task<Guid> _SaveAsync(ICqrsDispatcher dispatcher, long characterId, DateTime startedAtUtc,
        string? groupCode = null, IReadOnlyList<long>? captureQuantities = null,
        IReadOnlyList<RunBountyEntryInput>? bounties = null, bool rebuild = true)
    {
        Guid runId = await _StartAsync(dispatcher, startedAtUtc, groupCode, characterId);
        Result saved = await dispatcher.Send(new SaveRunCommand(runId, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [.. (captureQuantities ?? []).Select((quantity, index) => new RunLootCaptureInput
            {
                CapturedAtUtc = startedAtUtc.AddMinutes(5 + index), Source = LootCaptureSource.Clipboard,
                ContentHash = $"{characterId}-{index}",
                Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
            })], bounties ?? [], [], [], RebuildSummaries: rebuild), Token);
        Assert.True(saved.IsSuccess);
        return runId;
    }

    /// <summary>What a server's pull hands back for a crewmate's saved run, through the real applier.</summary>
    private static Task _PullCrewmateRunAsync(TestClientInstance instance, string groupCode = "HF-PL01") =>
        instance.Services.GetRequiredService<RunSynchronizationApplier>().ApplyAsync(ServerAddress, [new RunWirePayload
        {
            Run = RunWireData.FromEntity(new Run
            {
                Id = Guid.CreateVersion7(),
                CharacterId = Crewmate,
                GroupCode = groupCode,
                ActivityKind = ActivityKind.Site,
                State = RunState.Saved,
                StartedAtUtc = StartedAtUtc,
                StoppedAtUtc = StartedAtUtc.AddMinutes(15),
                SavedAtUtc = StartedAtUtc.AddMinutes(16),
                SiteTypeId = 1234,
                SiteName = "Homefront",
                Revision = 1
            }),
            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }], new HashSet<Guid>(), Token);

    /// <summary>Written straight to the store and summarised once: three hundred SAVEs would each rebuild every
    /// summary, which is the cost of seeding, not of what is measured.</summary>
    private static async Task _SeedSavedActivitiesAsync(TestClientInstance instance, int count)
    {
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
                         .CreateDbContextAsync(Token))
        {
            for (int index = 0; index < count; index++)
            {
                DateTime startedAtUtc = StartedAtUtc.AddHours(-index * 3);
                var run = new Run
                {
                    Id = Guid.CreateVersion7(),
                    CharacterId = index % 2 == 0 ? Pilot : Crewmate,
                    ActivityKind = ActivityKind.Site,
                    State = RunState.Saved,
                    StartedAtUtc = startedAtUtc,
                    StoppedAtUtc = startedAtUtc.AddMinutes(15),
                    SavedAtUtc = startedAtUtc.AddMinutes(16),
                    SiteTypeId = 1234,
                    SiteName = "Homefront",
                    Revision = 1
                };
                run.BountyEntries.Add(new RunBountyEntry { Id = Guid.CreateVersion7(), RunId = run.Id, OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = 400_000m });
                db.Set<Run>().Add(run);
            }

            await db.SaveChangesAsync(Token);
        }

        await _Dispatcher(instance).Send(new RebuildActivitySummariesCommand(), Token);
    }

    private static async Task<RunsOverviewViewModel> _OverviewAsync(TestClientInstance instance, RecordingDialogService? dialogs = null)
    {
        var overview = new RunsOverviewViewModel(_Dispatcher(instance), dialogs ?? new RecordingDialogService(),
            instance.Services, Crew, runClock: false);
        await overview.LoadAsync(Token);
        return overview;
    }

    private static async Task<(RunsOverviewViewModel Overview, Window Root)> _PresentAsync(TestClientInstance instance, double height)
    {
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        var window = new RunsWindow(overview);
        var content = (Control)(window.Content ?? throw new InvalidOperationException("The runs window has no content."));
        window.Content = null;
        var root = new Window { Width = 758, Height = height, Content = content, DataContext = overview };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return (overview, root);
    }

    /// <summary>Opened the way a click opens it: the runs screen's own row, through its own dialog service, so the
    /// detail screen gets exactly what production hands it — the run change feed included.</summary>
    private static async Task<ActivityDetailViewModel> _DetailThroughTheRowAsync(TestClientInstance instance)
    {
        var dialogs = new RecordingDialogService();
        RunsOverviewViewModel overview = await _OverviewAsync(instance, dialogs);
        await _OnlyRow(overview).OpenDetailCommand.ExecuteAsync(null);
        ActivityDetailViewModel detail = dialogs.LastActivityDetail ?? throw new InvalidOperationException("No detail screen opened.");
        await detail.LoadAsync(Token);
        return detail;
    }

    private static ActivityOverviewRowViewModel _OnlyRow(RunsOverviewViewModel overview) =>
        Assert.Single(Assert.Single(overview.Tabs[0].Days).Rows);

    /// <summary>The scroller holding the day bands — the one with the most to scroll.</summary>
    private static ScrollViewer _Scroller(Window root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>().MaxBy(scroller => scroller.Extent.Height)
        ?? throw new InvalidOperationException("The runs screen has no scroller.");

    private static Point _PositionOf(Window root, ScrollViewer scroller, ActivityOverviewRowViewModel row)
    {
        Control shown = root.GetVisualDescendants().OfType<Control>().First(control => ReferenceEquals(control.DataContext, row));
        return shown.TranslatePoint(new Point(0, 0), scroller) ?? throw new InvalidOperationException("The row is not on screen.");
    }

    /// <summary>Counts the overview reads, which is the cost a burst of signals turns into.</summary>
    private sealed class CountingDispatcher(ICqrsDispatcher inner) : ICqrsDispatcher
    {
        public int OverviewReads;

        public Task<TResult> Query<TResult>(EveUtils.Shared.Cqrs.IQuery<TResult> query, CancellationToken cancellationToken = default)
        {
            if (query is GetActivityOverviewQuery)
                Interlocked.Increment(ref OverviewReads);
            return inner.Query(query, cancellationToken);
        }

        public Task Send(ICqrsCommand command, CancellationToken cancellationToken = default) => inner.Send(command, cancellationToken);

        public Task<TResult> Send<TResult>(EveUtils.Shared.Cqrs.ICommand<TResult> command, CancellationToken cancellationToken = default) =>
            inner.Send(command, cancellationToken);
    }
}
