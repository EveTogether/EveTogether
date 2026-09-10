using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-162: the detail of one saved activity. One counter-proof per acceptance criterion, taken from the ticket
/// itself — each was shown red before the screen existed.
/// </summary>
public sealed class ActivityDetailTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>Acceptatiebevinding 8, 2026-09-04: the overview named this pilot "RaymondKrah"; one click into the
    /// detail screen he read "character 883434905" — this screen built its run rows without the nameOf delegate
    /// RunsOverviewViewModel already carries for the same character. Counter-proof: take the delegate back out of
    /// the constructor call below and this goes red, back on the bare id.</summary>
    [AvaloniaFact]
    public async Task RunRow_NamesTheCharacter_WhenTheCallerHasAName()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, null, cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            nameOf: id => id == 90000001 ? "RaymondKrah" : $"character {id}");
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal("RaymondKrah", Assert.Single(viewModel.RunRows).CharacterText);
    }

    // ── LOCATION shows a name, not a bare id (ET-213) ───────────────────────────────────────────────

    /// <summary>
    /// Counter-proof: Jithran, 2026-09-10 — LOCATION and the ACTIVITY header both read "system 30000142" for a
    /// saved site run, even though the SDE carries a name for that id. Red against the pre-fix code
    /// (<c>ActivityDetailViewModel.cs:230</c>), which printed <c>Run.SolarSystemId</c> straight into the text with
    /// no lookup at all.
    /// </summary>
    [AvaloniaFact]
    public async Task LocationText_NamesTheSolarSystemFromTheSde_InBothLocationAndTheActivityHeader()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, null, cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        // 30000142, matching the fixed solar system id every _SaveSiteRunAsync run in this file is started on.
        var sde = new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30000142, "Cistuvaert", 0.8));
        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(), sde: sde);
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal("Cistuvaert", viewModel.LocationText);
        Assert.Equal("Combat Site · Cistuvaert", viewModel.Activity.HeaderSummary);
    }

    /// <summary>AC-4: a stored id the SDE does not carry — no SDE at all here, the widest version of that case —
    /// falls back to something readable rather than a blank LOCATION row or a thrown exception.</summary>
    [AvaloniaFact]
    public async Task LocationText_FallsBackToTheBareId_WhenTheSdeHasNoMatch()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, null, cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(), sde: new FakeSdeAccessor());
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal("system 30000142", viewModel.LocationText);
    }

    /// <summary>AC-1, mission half: a mission names its agent and its level and shows REWARDS, and carries no
    /// BOUNTY or LOOT section. Counter-proof: give every kind the same fixed block of sections and this goes red on
    /// a visible BOUNTY heading.</summary>
    [AvaloniaFact]
    public async Task Mission_ShowsAgentAndRewards_AndNoBountyOrLootSection()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            4022, "Paragon Requests: Ships for Tips", 30000142,
            SiteTypeSource: SiteTypeSource.Mission, AgentId: 3018841, MissionLevel: 2), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(8), StartedAtUtc.AddMinutes(9),
            [], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "1,240", Amount = 1_240m, ObservedAtUtc = StartedAtUtc }]),
            cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "agent 3018841");
        Assert.Contains(texts, text => text == "level 2");
        Assert.Contains(texts, text => text == "REWARDS");
        Assert.DoesNotContain(texts, text => text == "BOUNTY");
        Assert.DoesNotContain(texts, text => text == "LOOT");
    }

    /// <summary>AC-1, anomaly half: a site shows ENEMIES, BOUNTY and LOOT and carries no agent row. Counter-proof:
    /// the same fixed block of sections for every kind puts an agent row on a site, and this goes red.</summary>
    [AvaloniaFact]
    public async Task Site_ShowsEnemiesBountyAndLoot_AndNoAgentRow()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "ENEMIES");
        Assert.Contains(texts, text => text == "BOUNTY");
        Assert.Contains(texts, text => text == "LOOT");
        Assert.DoesNotContain(texts, text => text == "AGENT");
        Assert.DoesNotContain(texts, text => text.StartsWith("agent "));
    }

    /// <summary>AC-2: the section a kind has is drawn even when it is empty, and then says why rather than showing
    /// a figure. Counter-proof, both halves: format the totals as <c>?? 0</c> and this goes red on "0 ISK"; drop
    /// the section entirely and it goes red on the missing LOOT heading, because then nothing on screen tells
    /// "nothing was captured" apart from "this kind has no loot".</summary>
    [AvaloniaFact]
    public async Task SiteWithoutLootCapture_SaysWhyThereIsNothing_AndShowsNoZero()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "LOOT");
        Assert.Contains(texts, text => text.StartsWith("No loot capture was recorded"));
        Assert.DoesNotContain(texts, text => text.Contains("0 ISK"));
    }

    /// <summary>AC-3: a loot line whose clipboard column disagrees with the market is shown and counted at the
    /// market price, and a line the lookup has no price for is counted separately rather than valued at nothing.
    /// Counter-proof: bind the line to <see cref="RunLootEntryDto.ClipboardPrice"/> — the way the running run's
    /// LOOT list still does — and the copied 999,999,999 shows up, which this forbids.</summary>
    [AvaloniaFact]
    public async Task LootLine_IsValuedFromThePriceLookup_NotFromTheClipboardColumn()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            cancellationToken);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                Entries =
                [
                    new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, ClipboardPrice = 999_999_999m, LootKind = LootKind.Gained },
                    new RunLootEntryInput { ItemTypeId = 35, Name = "Pyerite", Quantity = 1, ClipboardPrice = 12m, LootKind = LootKind.Gained }
                ]
            }], [], [], []), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == $"{300m:N0} ISK");
        Assert.DoesNotContain(texts, text => text.Contains("999"));
        Assert.Contains(texts, text => text == "no price");
        Assert.Contains(texts, text => text.StartsWith("1 line has no price"));
    }

    /// <summary>AC-4: an excluded capture keeps its row and counts towards nothing. Counter-proof: filter excluded
    /// captures out of the list — the total still adds up, and this goes red because the capture is gone. Leaving
    /// out is not the same as not counting. Since ET-215 the item table is one row per kind: the counted Tritanium, and
    /// under it a struck-through row for the excluded copy; the captures behind them are opened to be read too.</summary>
    [AvaloniaFact]
    public async Task ExcludedCapture_StaysOnScreen_AndDoesNotCount()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            cancellationToken);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        RunLootCaptureInput CaptureAt(int minute) => new()
        {
            CapturedAtUtc = StartedAtUtc.AddMinutes(minute), Source = LootCaptureSource.Clipboard, ContentHash = "ABC",
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, LootKind = LootKind.Gained }]
        };
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [CaptureAt(10), CaptureAt(11)], [], [], []), cancellationToken);
        Result<RunLootOverview> loot = await dispatcher.Query(new GetRunLootQuery(started.Value), cancellationToken);
        RunLootCaptureDto repeat = _Value(loot).Captures.OrderBy(capture => capture.CapturedAtUtc).Last();
        await dispatcher.Send(new SetRunLootCaptureExclusionCommand(repeat.CaptureId, IsExcluded: true), cancellationToken);

        (ActivityDetailWindow window, Window root) = await _PresentAsync(instance, 758, cancellationToken);
        ActivityDetailViewModel viewModel = Assert.IsType<ActivityDetailViewModel>(window.DataContext);
        Assert.Single(viewModel.LootOverview.Characters).IsCapturesShown = true;
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.Contains(texts, text => text == $"{300m:N0} ISK");            // 100 x 3 once, not twice
        Assert.Contains(texts, text => text == "EXCLUDED");
        Assert.Contains(texts, text => text == "excluded — repeat of #1");
        Assert.Equal(2, texts.Count(text => text == "Tritanium"));           // counted, and left out — both still listed
        Assert.Equal(2, texts.Count(text => text == "Tritanium ×3"));        // and both captures under them
    }

    /// <summary>AC-5: two runs in one activity that each sighted the same enemy type stay two rows, each with its
    /// own first/last window. Counter-proof: group by enemy type alone and there is one row, with the later
    /// sighting silently overwriting the earlier one's window.</summary>
    [AvaloniaFact]
    public async Task SameEnemyTypeOnTwoRuns_StaysTwoRowsWithTheirOwnWindows()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, "HF-7QK2", cancellationToken,
            enemies: [new RunEnemyObservationInput { Count = 2, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }]);
        await _SaveSiteRunAsync(dispatcher, 90000002, "HF-7QK2", cancellationToken,
            enemies: [new RunEnemyObservationInput { Count = 3, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc.AddMinutes(4), LastObservedAtUtc = StartedAtUtc.AddMinutes(5) }]);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Equal(2, texts.Count(text => text == "Centii Scavenger"));
        Assert.Contains(texts, text => text == _Window(0, 1));
        Assert.Contains(texts, text => text == _Window(4, 5));
    }

    /// <summary>AC-6: a run whose times were corrected by hand reads differently from one that was measured. The
    /// corrected moments are written over the start and stop themselves, so the duration beside it cannot say which
    /// it is. Counter-proof: show both runs identically and the "corrected by hand" assertion goes red.</summary>
    [AvaloniaFact]
    public async Task CorrectedRun_ReadsDifferentlyFromAMeasuredOne()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, "HF-7QK2", cancellationToken);
        Result<Guid> corrected = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(corrected.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], [], StartedAtUtc.AddMinutes(-1), StartedAtUtc.AddMinutes(16)), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "measured");
        Assert.Contains(texts, text => text.StartsWith("corrected by hand at"));
    }

    /// <summary>AC-7: the fleet section reports the real headcount and says the names are what is missing, rather
    /// than standing empty. Counter-proof, both halves: an empty list with no line goes red on the missing
    /// sentence, and counting the (never filled) name list instead of the summary's own participant count reads
    /// "0 participants" where six flew it.</summary>
    [AvaloniaFact]
    public async Task FleetSection_ReportsTheRealHeadcount_AndSaysTheNamesAreMissing()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (long characterId = 90000001; characterId <= 90000006; characterId++)
            await _SaveSiteRunAsync(dispatcher, characterId, "HF-7QK2", cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "6 participants");
        Assert.Contains(texts, text => text.StartsWith("Participant names are not recorded yet"));
    }

    /// <summary>AC-8, first half: nothing falls outside the module host's own 758px docked width, and the same
    /// layout still holds at a wide floating width. Counter-proof: the wide render must pass — a check that goes
    /// green at both widths without the layout being fluid is not measuring the layout.</summary>
    [AvaloniaTheory]
    [InlineData(758)]
    [InlineData(1180)]
    public async Task Detail_FitsWithoutOverflowingItsWidth(double width)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, "HF-7QK2", cancellationToken,
            enemies: [new RunEnemyObservationInput { Count = 41, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }]);

        (_, Window root) = await _PresentAsync(instance, width, cancellationToken);

        foreach (Control control in root.GetVisualDescendants().OfType<Control>()
                     .Where(candidate => candidate is TextBlock or Button && candidate.IsEffectivelyVisible))
        {
            Point topLeft = control.TranslatePoint(default, root) ?? default;
            Assert.True(topLeft.X + control.Bounds.Width <= root.Bounds.Width + 0.5,
                $"{control.GetType().Name} overflows at width {width}: right edge " +
                $"{topLeft.X + control.Bounds.Width:F1} > {root.Bounds.Width}");
        }
    }

    /// <summary>AC-8, second half: docked and floating are the very same <c>Content</c> instance, because the
    /// module host moves it between the two. Counter-proof: build a second layout for the floating case and this
    /// goes red — two layouts is the problem, not the fix.</summary>
    [AvaloniaFact]
    public async Task Detail_IsTheSameContentDockedAndFloating()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);
        ActivityDetailWindow window = await _WindowAsync(instance, 758, cancellationToken);

        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "ACTIVITY", "runs", "activity-detail");
        var docked = (Control)Assert.Single(display.HostTabs).Content!;

        display.IsFloating = true;
        host.SwitchMode();

        Assert.Same(docked, window.Content);
    }

    private static async Task<List<string>> _RenderAsync(TestClientInstance instance, CancellationToken cancellationToken)
    {
        (_, Window root) = await _PresentAsync(instance, 758, cancellationToken);
        return RenderedText.VisibleTexts(root);
    }

    // The control tree the operator sees when the module is docked: the host lifts window.Content out and reparents
    // it, so the assertions run against that content and never against a window that is never shown.
    private static async Task<(ActivityDetailWindow Window, Window Root)> _PresentAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken)
    {
        ActivityDetailWindow window = await _WindowAsync(instance, width, cancellationToken);
        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "ACTIVITY", "runs", "activity-detail");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = 1400, Content = content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return (window, root);
    }

    /// <summary>
    /// Acceptatie 2026-09-04, bevinding 2: ENEMIES may not claim to have measured what it never measured.
    /// <c>SaveRunCommandHandler</c> stores only observations that carry a count, and the count is typed by hand
    /// (ET-106), so an empty list means nobody counted — never "no combat". Measured on a copy of the operator's
    /// own store: nine of eleven activities said "no combat measured" while the gamelog had written between 7 and
    /// 406 combat samples inside each of those very runs, and 136 bounty payouts besides.
    ///
    /// This run is exactly that shape — uncounted enemies, bounty on the same screen. Counter-proof: put the old
    /// wording back and the two assertions below go red, on a screen that shows the bounty disproving it.
    /// </summary>
    [AvaloniaFact]
    public async Task UncountedEnemiesBesideABountyFigure_DoNotReadAsAMeasurement()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [],
            [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(3), Isk = 214_188m }],
            // Seen in the gamelog, never counted — which is what SAVE drops, and what the screen then has to
            // describe without inventing a measurement.
            [new RunEnemyObservationInput { Count = 0, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }],
            []), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == $"{214_188m:N0} ISK");        // the bounty is on screen
        Assert.DoesNotContain(texts, text => text.Contains("no combat measured"));
        Assert.DoesNotContain(texts, text => text.Contains("That is a measurement, not an empty list"));
        Assert.Contains(texts, text => text == "none counted");
        Assert.Contains(texts, text => text.StartsWith("Enemies are saved only once you count them"));
    }

    // ── Delete, understated, from the bottom of the screen (ET-214 round 2) ────────────────────────────

    /// <summary>The ticket's own counter-proof, run from the detail screen this time: a group of three saved runs,
    /// all this machine's own, deleted as one activity, leaves the screen showing nothing is left. Counter-proof:
    /// send only a single-run <c>DeleteRunCommand</c> for the group's first run instead of
    /// <c>DeleteRunsInGroupCommand</c> and this goes red with two of the three runs still undeleted underneath —
    /// the DB assertion catches what <c>IsDeleted</c> alone would not.</summary>
    [AvaloniaFact]
    public async Task DeleteActivity_Group_RemovesEveryOwnRun_AndShowsDeleted()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid[] runIds =
        [
            await _StartAsync(dispatcher, 90000001, "HF-DEL1", cancellationToken),
            await _StartAsync(dispatcher, 90000002, "HF-DEL1", cancellationToken),
            await _StartAsync(dispatcher, 90000003, "HF-DEL1", cancellationToken)
        ];
        foreach (Guid runId in runIds)
            await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
                [], [], [], []), cancellationToken);
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        ActivityDetailViewModel viewModel = await _ViewModelAsync(instance, dispatcher, cancellationToken,
            ownCharacterIds: [90000001, 90000002, 90000003], dialogs: dialogs);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDeleted);
        Assert.False(viewModel.CanDelete);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        foreach (Guid runId in runIds)
            Assert.NotNull((await db.Set<Run>().SingleAsync(run => run.Id == runId, cancellationToken)).DeletedAtUtc);
    }

    /// <summary>ET-214 round 2, Jithran: "think about whether deleting is allowed there [a fleetmate's read-only
    /// run] or not, and say what you choose." Chosen: only this machine's own runs are deleted; the fleetmate's own
    /// run — read-only since ET-215 because a correction to it could never be published back — is left alone, and
    /// the activity, still real for that one run, reloads in place rather than reading as gone.</summary>
    [AvaloniaFact]
    public async Task DeleteActivity_MixedGroup_LeavesTheForeignRun_AndReloadsInPlace()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid ownRunId = await _StartAsync(dispatcher, 90000001, "HF-DEL2", cancellationToken);
        Guid foreignRunId = await _StartAsync(dispatcher, 90000002, "HF-DEL2", cancellationToken);
        await dispatcher.Send(new SaveRunCommand(ownRunId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(foreignRunId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), cancellationToken);
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        ActivityDetailViewModel viewModel = await _ViewModelAsync(instance, dispatcher, cancellationToken,
            ownCharacterIds: [90000001], dialogs: dialogs);
        Assert.Equal("2 participants", viewModel.ParticipantCountText);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeleted);
        Assert.False(viewModel.CanDelete);
        Assert.Equal("1 participants", viewModel.ParticipantCountText);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.NotNull((await db.Set<Run>().SingleAsync(run => run.Id == ownRunId, cancellationToken)).DeletedAtUtc);
        Run foreign = await db.Set<Run>().SingleAsync(run => run.Id == foreignRunId, cancellationToken);
        Assert.Null(foreign.DeletedAtUtc);
        Assert.Equal(RunSyncState.Local, foreign.SyncState);
    }

    /// <summary>A fully foreign activity — every run someone else's — has nothing here that is this machine's to
    /// delete, so the control is unavailable rather than offered and then refused.</summary>
    [AvaloniaFact]
    public async Task DeleteActivity_FullyForeign_CannotBeDeleted()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000002, groupCode: null, cancellationToken);
        ActivityDetailViewModel viewModel = await _ViewModelAsync(instance, dispatcher, cancellationToken,
            ownCharacterIds: [90000001]);

        Assert.False(viewModel.CanDelete);
    }

    /// <summary>What the confirmation says, read straight off what the screen already shows: the site, how many of
    /// the pilot's own runs go, the ISK, and — the mixed-group case — that a fleetmate's run stays behind.</summary>
    [AvaloniaFact]
    public async Task DeleteActivity_ConfirmationNamesSiteOwnRunsIskAndTheForeignRun()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid ownRunId = await _StartAsync(dispatcher, 90000001, "HF-DEL3", cancellationToken);
        Guid foreignRunId = await _StartAsync(dispatcher, 90000002, "HF-DEL3", cancellationToken);
        await dispatcher.Send(new SaveRunCommand(ownRunId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(3), Isk = 1_500_000m }], [], []),
            cancellationToken);
        await dispatcher.Send(new SaveRunCommand(foreignRunId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), cancellationToken);
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(false) };
        ActivityDetailViewModel viewModel = await _ViewModelAsync(instance, dispatcher, cancellationToken,
            ownCharacterIds: [90000001], dialogs: dialogs);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.Contains("Homefront", dialogs.LastConfirmMessage);
        Assert.Contains("1 of your own runs", dialogs.LastConfirmMessage);
        Assert.Contains($"{1_500_000m:N0} ISK", dialogs.LastConfirmMessage);
        Assert.Contains("One run from another pilot stays", dialogs.LastConfirmMessage);
    }

    /// <summary>Declining the confirmation changes nothing — no run touched, the screen exactly as it was.</summary>
    [AvaloniaFact]
    public async Task DeleteActivity_Cancelled_ChangesNothing()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid runId = await _StartAsync(dispatcher, 90000001, groupCode: null, cancellationToken);
        await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), cancellationToken);
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(false) };
        ActivityDetailViewModel viewModel = await _ViewModelAsync(instance, dispatcher, cancellationToken,
            ownCharacterIds: [90000001], dialogs: dialogs);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeleted);
        Assert.True(viewModel.CanDelete);
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.Null((await db.Set<Run>().SingleAsync(run => run.Id == runId, cancellationToken)).DeletedAtUtc);
    }

    /// <summary>Soft delete's whole point (ET-214): a delete undone from right where it happened puts the run back,
    /// and the screen reads as it did before.</summary>
    [AvaloniaFact]
    public async Task UndoDelete_RestoresTheActivity()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid runId = await _StartAsync(dispatcher, 90000001, groupCode: null, cancellationToken);
        await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), cancellationToken);
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        ActivityDetailViewModel viewModel = await _ViewModelAsync(instance, dispatcher, cancellationToken,
            ownCharacterIds: [90000001], dialogs: dialogs);
        await viewModel.DeleteCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDeleted);

        await viewModel.UndoDeleteCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeleted);
        Assert.True(viewModel.CanDelete);
        Assert.Equal("Homefront", viewModel.SiteText);
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.Null((await db.Set<Run>().SingleAsync(run => run.Id == runId, cancellationToken)).DeletedAtUtc);
    }

    private static async Task<Guid> _StartAsync(ICqrsDispatcher dispatcher, long characterId, string? groupCode,
        CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        Assert.True(started.IsSuccess);
        return started.Value;
    }

    private static async Task<ActivityDetailViewModel> _ViewModelAsync(TestClientInstance instance,
        ICqrsDispatcher dispatcher, CancellationToken cancellationToken, IReadOnlyList<long>? ownCharacterIds = null,
        RecordingDialogService? dialogs = null)
    {
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            ownCharacterIds: ownCharacterIds is null ? null : new HashSet<long>(ownCharacterIds),
            dialogs: dialogs);
        await viewModel.LoadAsync(cancellationToken);
        return viewModel;
    }

    private static async Task<ActivityDetailWindow> _WindowAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);
        return new ActivityDetailWindow(viewModel) { Width = width, Height = 1400 };
    }

    private static async Task _SaveSiteRunAsync(ICqrsDispatcher dispatcher, long characterId, string? groupCode,
        CancellationToken cancellationToken, IReadOnlyList<RunEnemyObservationInput>? enemies = null)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], enemies ?? [], []), cancellationToken);
    }

    private static string _Window(int firstMinute, int lastMinute) =>
        $"{StartedAtUtc.AddMinutes(firstMinute).ToLocalTime():HH:mm:ss} – " +
        $"{StartedAtUtc.AddMinutes(lastMinute).ToLocalTime():HH:mm:ss}";

    private static T _Value<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Messages[0].Text);
        return result.Value!;
    }

    // ── The bounty breakdown and the total ISK figure (ET-210 review, 2026-09-09) ─────────────────────

    /// <summary>
    /// Counter-proof: Jithran saved a five-character activity and its BOUNTY section showed one combined figure
    /// with no way to tell who brought in what — even though <c>RunBountyEntry</c> always carried a run id, and one
    /// run is one character. Red against the pre-fix code, where <c>BountyRows</c> did not exist at all.
    /// </summary>
    [AvaloniaFact]
    public async Task BountyRows_OneRowPerCharacter_WithTheGroupsTotal()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunWithBountyAsync(dispatcher, 90000001, "HF-7QK2", 675_000m, cancellationToken);
        await _SaveSiteRunWithBountyAsync(dispatcher, 90000002, "HF-7QK2", 675_000m, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            nameOf: id => id == 90000001 ? "Jithran" : "Second Pilot");
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(2, viewModel.BountyRows.Count);
        Assert.Contains(viewModel.BountyRows, r => r.CharacterText == "Jithran" && r.IskText == $"{675_000m:N0} ISK");
        Assert.Contains(viewModel.BountyRows, r => r.CharacterText == "Second Pilot" && r.IskText == $"{675_000m:N0} ISK");
        // The total is set apart from the rows, not folded into one of them, but it still has to equal their sum.
        Assert.Equal($"{1_350_000m:N0} ISK", viewModel.BountyText);
    }

    /// <summary>
    /// Counter-proof 4 from the ET-211 grooming, carried into ET-215's per-character blocks: two characters, each with
    /// their own priced loot capture on their own run within the same group, must show as one block per character
    /// with a group total equal to their sum — the same shape
    /// <see cref="BountyRows_OneRowPerCharacter_WithTheGroupsTotal"/> already gives bounty. Red against the pre-ET-211
    /// code, where the LOOT section only ever showed one combined figure.
    /// </summary>
    [AvaloniaFact]
    public async Task LootBlocks_OneBlockPerCharacter_WithTheGroupsTotal()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            cancellationToken);
        await _SaveSiteRunWithLootAsync(dispatcher, 90000001, "HF-7QK2", quantity: 3, cancellationToken);
        await _SaveSiteRunWithLootAsync(dispatcher, 90000002, "HF-7QK2", quantity: 4, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            nameOf: id => id == 90000001 ? "Jithran" : "Second Pilot");
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(2, viewModel.LootOverview.Characters.Count);
        Assert.Contains(viewModel.LootOverview.Characters, r => r.CharacterText == "Jithran" && r.SubtotalText == $"{300m:N0} ISK");
        Assert.Contains(viewModel.LootOverview.Characters, r => r.CharacterText == "Second Pilot" && r.SubtotalText == $"{400m:N0} ISK");
        // The per-character blocks are set apart from the group's own total, but they still have to sum to it.
        Assert.Equal($"{700m:N0} ISK", viewModel.LootOverview.NetIskDisplay);
    }

    private static async Task _SaveSiteRunWithLootAsync(ICqrsDispatcher dispatcher, long characterId,
        string? groupCode, long quantity, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
            }], [], [], []), cancellationToken);
    }

    /// <summary>
    /// Counter-proof: Jithran chose per-character enemy tracking with a group total (ET-210 review, round 4) over
    /// one shared tally. A group where different characters each counted a different enemy must show each
    /// character's own total, summed across every type they saw, with a group total equal to their sum — the same
    /// shape as <see cref="BountyRows_OneRowPerCharacter_WithTheGroupsTotal"/>. Red against the pre-fix code, where
    /// <c>EnemyCharacterRows</c> did not exist at all.
    /// </summary>
    [AvaloniaFact]
    public async Task EnemyCharacterRows_OneRowPerCharacter_WithTheGroupsTotal()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> first = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(first.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [new RunEnemyObservationInput { Count = 4, EnemyTypeId = 111, EnemyName = "Offertory Sigil", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }],
            []), cancellationToken);
        Result<Guid> second = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(second.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [new RunEnemyObservationInput { Count = 2, EnemyTypeId = 112, EnemyName = "Centii Servant", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }],
            []), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            nameOf: id => id == 90000001 ? "Jithran" : "Second Pilot");
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(2, viewModel.EnemyCharacterRows.Count);
        Assert.Contains(viewModel.EnemyCharacterRows, r => r.CharacterText == "Jithran" && r.CountText == "4 enemies");
        Assert.Contains(viewModel.EnemyCharacterRows, r => r.CharacterText == "Second Pilot" && r.CountText == "2 enemies");
        // The by-type list keeps both species; the group total is set apart, not one more row of either list.
        Assert.Equal("6 enemies", viewModel.EnemyTotalCountText);
    }

    /// <summary>
    /// Counter-proof: the total ISK figure must add bounty, priced loot net and ISK-form rewards together, but
    /// never a mission's own stated "Bounty" reward line on top of the real gamelog bounty it already counted —
    /// summing both would be the same ISK counted twice under two different names. Red against a naive
    /// <c>Parameters.Sum(p => p.Amount)</c> that does not carve that key back out.
    /// </summary>
    [AvaloniaFact]
    public async Task TotalIsk_AddsBountyAndIskRewards_ButNeverTheMissionsOwnBountyLineTwice()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(3), Isk = 1_000_000m }], [],
            [
                new RunParameterInput { ParameterKey = RunParameterKey.BonusIsk, TypedValue = "500000", Amount = 500_000m, ObservedAtUtc = StartedAtUtc },
                // A mission's own stated reward line, same key name as the real bounty above but a different thing
                // entirely — must not be added into the total a second time.
                new RunParameterInput { ParameterKey = RunParameterKey.Bounty, TypedValue = "1000000", Amount = 1_000_000m, ObservedAtUtc = StartedAtUtc },
                // Not ISK at all — must not be added in as if it were.
                new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "1,240", Amount = 1_240m, ObservedAtUtc = StartedAtUtc }
            ]), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);

        Assert.True(viewModel.HasTotalIsk);
        // 1,000,000 bounty + 500,000 BonusIsk — not the 1,000,000 "mission Bounty" line, not the 1,240 LP.
        Assert.Equal($"{1_500_000m:N0} ISK", viewModel.TotalIskText);
    }

    private static async Task _SaveSiteRunWithBountyAsync(ICqrsDispatcher dispatcher, long characterId,
        string? groupCode, decimal bountyIsk, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(3), Isk = bountyIsk }], [], []),
            cancellationToken);
    }
}
