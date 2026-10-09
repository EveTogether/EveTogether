using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Client.Views;
using EveUtils.Client.Views.Runs.Sections;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Entities;
using EveUtils.Shared.Modules.Gamelog.Models;
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

        Assert.Equal("RaymondKrah", Assert.Single(viewModel.Fleet().Rows).Name);
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

        Assert.Equal(OpsecText.Mark("Cistuvaert"), viewModel.Activity().LocationText);
        // "Site", not "Combat Site": _SaveSiteRunAsync never records a scanner group, and ET-226 stopped that
        // defaulting to Combat Site — see ActivityDetailTests.KindText_ReadsSite_WhenNoGroupWasEverRecorded.
        Assert.Equal($"Site · {OpsecText.Mark("Cistuvaert")}", viewModel.Activity().HeaderSummary);
    }

    // ── TYPE reads the recorded scanner group, not a default (ET-226) ──────────────────────────────────

    /// <summary>
    /// Counter-proof: Jithran, 2026-09-10 — a Data Site he ran ("Local Sansha Production Installation") showed
    /// "Combat Site" here, because <c>ActivityDetailViewModel._KindLabel</c> mapped every <c>ActivityKind.Site</c>
    /// run to the literal string "Combat Site" regardless of what the scanner actually said. Red against the
    /// pre-fix code: <c>KindText</c> read "Combat Site" and <c>Activity.HeaderSummary</c> read "Combat Site ·
    /// Cistuvaert" for this same Data Site run.
    /// </summary>
    [AvaloniaFact]
    public async Task KindText_ReadsTheRecordedSiteGroup_NotTheCombatSiteDefault()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Local Sansha Production Installation", 30000142, SignatureGroupSnapshot: "Data Site"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal("Data Site", viewModel.KindText);
        Assert.StartsWith("Data Site", viewModel.Activity().HeaderSummary);
    }

    /// <summary>A run whose group was never recorded — a manual start, or one saved before this column existed —
    /// reads the honest "Site", never "Combat Site" as a default (ET-226 AC-3).</summary>
    [AvaloniaFact]
    public async Task KindText_ReadsSite_WhenNoGroupWasEverRecorded()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, null, cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal("Site", viewModel.KindText);
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

        Assert.Equal(OpsecText.Mark("system 30000142"), viewModel.Activity().LocationText);
    }

    /// <summary>AC-1, mission half: a mission names its agent and its level and shows MISSION, and carries no
    /// BOUNTY section but a LOOT one even when nothing was looted, so loot forgotten during the run can be added
    /// afterwards (ET-421). Counter-proof: give every kind the same fixed block of sections and this goes red on
    /// a visible BOUNTY heading. The agent reads as a bare id here because this render path wires no SDE — see
    /// <see cref="Mission_NamesTheAgentFromTheSde_InsteadOfTheBareId"/> for the id resolved into a name.</summary>
    [AvaloniaFact]
    public async Task Mission_ShowsAgentAndRewardsAndALootSection_AndNoBountySection()
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
        Assert.Contains(texts, text => text == "Level 2");
        Assert.Contains(texts, text => text == "MISSION");
        Assert.DoesNotContain(texts, text => text == "BOUNTY");
        Assert.Contains(texts, text => text == "LOOT");
    }

    /// <summary>
    /// ET-248, rendered end to end: Jithran + Raymond's Fierce Dark, 2026-09-11 — an abyssal's detail screen showed a
    /// MISSION section with two rows reading "ABYSSAL FILAMENT 3|Dark" (one per run of the group) and LOCATION read
    /// "not recorded", also in the ACTIVITY header ("Abyssal · not recorded"). Both runs here carry
    /// <see cref="RunParameterKey.AbyssalFilament"/>, reproducing why it showed twice. Counter-proof: no rendered
    /// text is "MISSION", "ABYSSAL FILAMENT" or the raw "3|Dark", and LOCATION reads the entry system rather than
    /// "not recorded" anywhere on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task AbyssalDetail_RendersNoMissionSection_AndNoRawFilamentValue_AndTheEntrySystemAsLocation()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string groupCode = "HF-Z6U3";
        foreach (long characterId in (long[])[90000001, 90000002])
        {
            Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Abyssal,
                StartedAtUtc, 0, null, 30004079, groupCode), cancellationToken);
            await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(20),
                StartedAtUtc.AddMinutes(21), [], [], [],
                [new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = "3|Dark", ObservedAtUtc = StartedAtUtc }]),
                cancellationToken);
        }

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.DoesNotContain(texts, text => text == "MISSION");
        Assert.DoesNotContain(texts, text => text.Contains("ABYSSAL FILAMENT", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, text => text.Contains("3|Dark", StringComparison.Ordinal));
        // Never "not recorded" for LOCATION, the header, or the panel's own title (ActivityDetailViewModel's own
        // SiteText, bound to the title above KindText — a second, independent reading of the same fact ET-241
        // already fixed once in ActivityDetailSectionViewModel, ET-248 measured it never fixed here). Matched as a
        // whole line rather than a substring, since FLEET legitimately says participant names "are not recorded
        // yet" elsewhere on this very screen and that sentence is not this bug.
        Assert.DoesNotContain(texts, text => text is "not recorded" or "site not recorded");
        Assert.DoesNotContain(texts, text => text.Contains("Abyssal · not recorded", StringComparison.Ordinal));
        Assert.Contains(texts, text => text == "Fierce Dark");
        // No FakeSdeAccessor wired into this render path, so the system falls back to its bare id — still "entered
        // from …", never a raw stored value and never silence about where the pocket was entered from.
        Assert.Contains(texts, text => text == "entered from system 30004079");
    }

    /// <summary>AC-2: the detail screen shows the agent by name, not as "agent 3018841" — ET-235's own complaint
    /// about this screen. Counter-proof: read <c>AgentId</c> straight into <c>AgentText</c> without the SDE lookup
    /// and this goes red on the bare id.</summary>
    [AvaloniaFact]
    public async Task Mission_NamesTheAgentFromTheSde_InsteadOfTheBareId()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            4022, "Paragon Requests: Ships for Tips", 30000142,
            SiteTypeSource: SiteTypeSource.Mission, AgentId: 3018841, MissionLevel: 2), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(8), StartedAtUtc.AddMinutes(9),
            [], [], [], []), cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        var sde = new FakeSdeAccessor().AddAgent(new SdeAgent(3018841, "Kaesa Baldwin", Level: 2, AgentTypeId: 2,
            AgentTypeName: "BasicAgent", DivisionId: 1, IsLocator: false, CorporationId: 1000010,
            LocationId: 60003760, SolarSystemId: 30000142, SolarSystemName: "Jita"));
        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(), sde: sde);
        await viewModel.LoadAsync(cancellationToken);

        Assert.Equal("Kaesa Baldwin", viewModel.Mission().AgentText);
        Assert.Equal("Level 2", viewModel.Mission().LevelText);
    }

    /// <summary>ET-237 comment 1: a regular agent's mission has no "Report to" line at all, so nothing states the
    /// agent — the screen says that honestly rather than showing a bare id it does not have.</summary>
    [AvaloniaFact]
    public async Task Mission_WithNoAgentInTheCapture_SaysSoRatherThanShowingAnId()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            9999, "Cargo Delivery Objectives", 30000142, SiteTypeSource: SiteTypeSource.Mission), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(8), StartedAtUtc.AddMinutes(9),
            [], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "360000", Amount = 360_000m, ObservedAtUtc = StartedAtUtc }]),
            cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);

        Assert.False(viewModel.Mission().HasAgent);
        Assert.Equal("not stated in this capture", viewModel.Mission().AgentText);
        Assert.False(viewModel.Mission().IsLevelShown);
    }

    /// <summary>ET-251: an important mission is marked as such and shown in MISSION as its own fact, not folded
    /// into the reward rows.</summary>
    [AvaloniaFact]
    public async Task Mission_MarkedImportant_ShowsInMissionSection_AndNotAsARewardRow()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            9999, "Materials For War Preparation", 30000142, SiteTypeSource: SiteTypeSource.Mission), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(8), StartedAtUtc.AddMinutes(9),
            [], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.ImportantMission, TypedValue = "important mission", ObservedAtUtc = StartedAtUtc }]),
            cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);

        Assert.True(viewModel.Mission().IsImportantMission);
        Assert.Empty(viewModel.Mission().RewardRows);
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
        Assert.Single(viewModel.Loot().LootOverview.Characters).IsCapturesShown = true;
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.Contains(texts, text => text == $"{300m:N0} ISK");            // 100 x 3 once, not twice
        Assert.Contains(texts, text => text == "EXCLUDED");
        Assert.Contains(texts, text => text == "excluded — repeat of #1");
        Assert.Equal(2, texts.Count(text => text == "Tritanium"));           // counted, and left out — both still listed
        Assert.Equal(2, texts.Count(text => text == "Tritanium ×3"));        // and both captures under them
    }

    /// <summary>ET-240 AC-5/AC-6/AC-10: the rooms of a combat site, marked with NEW ROOM, come back after SAVE and
    /// reopening — in ENEMIES and in LOOT, each copy in the room that was going when it was made, a copy after STOP in
    /// the last room. Counter-proof: save without the RoomStarted boundary and LOOT falls back to one flat list.</summary>
    [AvaloniaFact]
    public async Task SavedRunWithRooms_ShowsEnemiesAndLootPerRoom_AfterReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            cancellationToken);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        RunLootCaptureInput CaptureAt(int minute, long quantity) => new()
        {
            CapturedAtUtc = StartedAtUtc.AddMinutes(minute), Source = LootCaptureSource.Clipboard, ContentHash = $"C{minute}",
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
        };
        RunEnemyObservationInput SeenIn(int room, int count, int fromMinute) => new()
        {
            RoomNumber = room, Count = count, EnemyTypeId = 111, EnemyName = "Centii Scavenger",
            FirstObservedAtUtc = StartedAtUtc.AddMinutes(fromMinute), LastObservedAtUtc = StartedAtUtc.AddMinutes(fromMinute + 3)
        };
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(17),
            [CaptureAt(3, 1), CaptureAt(7, 2), CaptureAt(16, 4)], [], [SeenIn(1, 2, 1), SeenIn(2, 3, 6)],
            [new RunParameterInput { ParameterKey = RunParameterKey.RoomStarted, TypedValue = string.Empty, ObservedAtUtc = StartedAtUtc.AddMinutes(5) }]),
            cancellationToken);

        (ActivityDetailWindow window, Window root) = await _PresentAsync(instance, 758, cancellationToken);
        ActivityDetailViewModel viewModel = Assert.IsType<ActivityDetailViewModel>(window.DataContext);
        RunLootViewModel loot = Assert.Single(viewModel.Loot().LootOverview.Characters).Loot;
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.Equal(["ROOM 1", "ROOM 2"], viewModel.Enemies().EnemyRooms.Select(room => room.Title));
        Assert.Equal([(1, $"{100m:N0} ISK"), (2, $"{600m:N0} ISK")], loot.Rooms.Select(room => (room.Number, room.SubtotalText)));
        Assert.Equal(3, texts.Count(text => text == "ROOM 2"));      // ENEMIES, ROOMS and LOOT
    }

    /// <summary>ET-494: in a group the rooms are one set — the commander's run when it has rooms, else the fullest — and every
    /// pilot's row lands in them by its first sighting — so "NO ROOMS MARKED" is gone, and a mate with rooms of his own
    /// a few seconds off draws the same rooms on every client. One type is one row per room across the pilots: the
    /// widest window and the largest count. A group with no rooms stays one list. Counter-proof: keep the stored room
    /// and one row per pilot, and the mate's rows come back under "NO ROOMS MARKED" or twice in a room; take the fullest
    /// list over the commander's and "commander-fewer-rooms" opens a third room.</summary>
    [AvaloniaTheory]
    [InlineData("mate-without-rooms", "ROOM 1:Scavenger×2 ROOM 2:Scavenger×2,Servant×3 ROOM 3:Keeper×3")]
    [InlineData("mate-with-own-rooms", "ROOM 1:Scavenger×2 ROOM 2:Scavenger×3,Keeper×3")]
    [InlineData("commander-fewer-rooms", "ROOM 1:Scavenger×2 ROOM 2:Scavenger×3,Keeper×3")]
    [InlineData("no-rooms", "")]
    public async Task GroupRun_EveryPilotsRows_LandInTheGroupsRoomsByTime(string scenario, string expected)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RunEnemyObservationInput Seen(int typeId, string name, int count, int minute, int? room) => new()
        {
            RoomNumber = scenario == "no-rooms" ? null : room, Count = count, EnemyTypeId = typeId, EnemyName = $"Centii {name}",
            FirstObservedAtUtc = StartedAtUtc.AddMinutes(minute), LastObservedAtUtc = StartedAtUtc.AddMinutes(minute)
        };
        RunParameterInput Boundary(int minute) => new()
        {
            ParameterKey = RunParameterKey.RoomDetected, TypedValue = "sure", ObservedAtUtc = StartedAtUtc.AddMinutes(minute)
        };
        await _SaveSiteRunAsync(dispatcher, 90000001, "AB-7QK2", cancellationToken,
            enemies: [Seen(111, "Scavenger", 2, 1, 1), Seen(111, "Scavenger", 2, 6, 2)],
            parameters: scenario switch { "no-rooms" => [], "commander-fewer-rooms" => [Boundary(5)], _ => [Boundary(5), Boundary(10)] },
            role: scenario == "commander-fewer-rooms" ? RunRole.FleetCommander : RunRole.Member);
        await _SaveSiteRunAsync(dispatcher, 90000002, "AB-7QK2", cancellationToken,
            enemies: scenario is "mate-with-own-rooms" or "commander-fewer-rooms"
                ? [Seen(111, "Scavenger", 3, 7, 2), Seen(113, "Keeper", 3, 9, 3)]
                : [Seen(112, "Servant", 3, 7, null), Seen(113, "Keeper", 3, 11, null)],
            parameters: scenario is "mate-with-own-rooms" or "commander-fewer-rooms" ? [Boundary(4), Boundary(8)] : []);

        (ActivityDetailWindow window, _) = await _PresentAsync(instance, 758, cancellationToken);
        ActivityDetailViewModel viewModel = Assert.IsType<ActivityDetailViewModel>(window.DataContext);

        Assert.Equal(expected, string.Join(" ", viewModel.Enemies().EnemyRooms.Select(room =>
            $"{room.Title}:{string.Join(",", room.Rows.Select(row => $"{row.EnemyName["Centii ".Length..]}×{row.CountText}"))}")));
        if (scenario == "mate-with-own-rooms")
        {
            Assert.Equal(_Window(6, 7), viewModel.Enemies().EnemyRooms[1].Rows[0].WindowText);
        }
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

        // FLEET (ET-272) only notes what is out of the ordinary: the corrected character carries its own "times
        // corrected by hand at HH:mm" subtext, the measured one carries none at all — the difference itself is the
        // proof, not a literal "measured" label. (The header's own TimeSourceText also reads "times corrected by
        // hand" without a moment, once for the whole activity — the per-character "at HH:mm" line is FLEET's own.)
        Assert.Contains(texts, text => text.StartsWith("times corrected by hand at"));
        Assert.Equal(1, texts.Count(text => text.Contains("corrected by hand at")));
    }

    /// <summary>AC-7: the fleet section reports the real headcount and says the names are what is missing, rather
    /// than standing empty. Counter-proof, both halves: an empty list with no line goes red on the missing
    /// sentence, and counting the (never filled) name list instead of the summary's own participant count reads
    /// "0 characters" where six flew it.</summary>
    [AvaloniaFact]
    public async Task FleetSection_ReportsTheRealHeadcount_AndSaysTheNamesAreMissing()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (long characterId = 90000001; characterId <= 90000006; characterId++)
            await _SaveSiteRunAsync(dispatcher, characterId, "HF-7QK2", cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "6 characters");
        Assert.Contains(texts, text => text.StartsWith("Some names are not recorded on their runs"));
    }

    /// <summary>ET-212 counter-proof: a saved activity must still name a participant once that character is no
    /// longer logged in anywhere on this machine — <c>nameOf</c> is never passed to the window here, the same
    /// "nobody left to ask" case <see cref="FleetSection_ReportsTheRealHeadcount_AndSaysTheNamesAreMissing"/> already
    /// relies on for its own bare-id assertion. What is different is that this run's name was recorded when it
    /// started. Red before ET-212: the row read "character 90000007" and the caveat sentence still stood even though
    /// this activity's one name was in fact recorded.</summary>
    [AvaloniaFact]
    public async Task RunRow_ShowsTheRecordedName_EvenWhenTheCharacterIsNoLongerLoggedIn()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000007, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, CharacterNameSnapshot: "Abnoba Auscent"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "Abnoba Auscent");
        Assert.DoesNotContain(texts, text => text.StartsWith("Some names are not recorded on their runs"));
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

    /// <summary>A server copy corrects nothing, whatever asks: delete, re-value, republish and undo all refuse in the
    /// view-model while the local store they would change stays as it was, and the rendered screen says it is read-only
    /// and offers no re-value. Counter-proof: drop <c>IsReadOnly</c> from a command's guard (or from <c>CanDelete</c>)
    /// and that row goes red.</summary>
    [AvaloniaTheory]
    [InlineData("delete")]
    [InlineData("revalue")]
    [InlineData("republish")]
    [InlineData("undo")]
    public async Task ServerCopy_RefusesCorrections_AndSaysItIsReadOnly(string command)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));
        ActivityDetailDto copy = _Value(await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId), cancellationToken));
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        bool republished = false;
        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId, dialogs: dialogs,
            ownCharacterIds: new HashSet<long> { 90000001 }, republish: () => { republished = true; return Task.CompletedTask; },
            serverCopy: copy);
        await viewModel.LoadAsync(cancellationToken);

        await (command switch
        {
            "delete" => viewModel.DeleteCommand.ExecuteAsync(null),
            "revalue" => viewModel.RevalueCommand.ExecuteAsync(null),
            "republish" => viewModel.RepublishCommand.ExecuteAsync(null),
            _ => viewModel.UndoDeleteCommand.ExecuteAsync(null)
        });
        (_, Window root) = _Present(new ActivityDetailWindow(viewModel) { Width = 758, Height = 1400 }, 758);

        Assert.True(viewModel.IsReadOnly);
        Assert.False(viewModel.CanDelete);
        Assert.False(viewModel.IsDeleted);
        Assert.False(republished);
        Assert.Single(_Value(await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));
        Assert.NotEmpty(viewModel.Sections);
        Assert.True(root.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ReadOnlyBanner").IsEffectivelyVisible);
        Assert.DoesNotContain(root.GetVisualDescendants().OfType<Button>(),
            button => button.Name == "RevalueButton" && button.IsEffectivelyVisible);
    }

    public enum CombatArrival { Saved, FleetMateShared, FleetMateWithheld, MateBesideOwnNotSynced, MateBesideOwnWithheld, OwnPulledBackWithout }

    private static readonly string[] RealRunTiles =
    [
        "65,732 hp", "1,999 hp", "998 hp", "Nova Fury Light Missile on Ephialtes Dissipator", "74 hp",
        "Ephialtes Dissipator · Wrecks", "115 GJ", "109", "no rep line in this run", "reps in · none in this run"
    ];

    // ET-474 AC1/AC3: run 5's hit quality — the Dissipator's missiles as a row of their own, the enemy's weapon-less lines
    // apart from its named ones, and no figure for a missile that is not a verdict.
    private static readonly string[] RealRunHitQuality =
    [
        "OUT · WHAT YOU DID", "IN · WHAT THEY DID TO YOU", HitQualityDetailSectionViewModel.MissileLabel, "Nova Fury Light Missile",
        "15,178"
    ];

    public static TheoryData<GameLogEvent[]?, CombatArrival, string[], string[]> AbyssalCombat => new()
    {
        // Run 5 of 18 Sep 2026 as it was saved, with its real combat: the figures are the game log's own (AC1, AC6).
        { RunCombatTelemetryTests.RealRunEvents(), CombatArrival.Saved, [.. RealRunTiles, .. RealRunHitQuality], [CombatDetailSectionViewModel.NotRecordedText, HitQualityDetailSectionViewModel.NotRecordedText] },
        // The same run saved before combat was kept: one line in each, never a zero or an empty chart (AC2).
        // AC4 of ET-474: HIT QUALITY says why in one line, with no table and no zeros.
        { null, CombatArrival.Saved, [CombatDetailSectionViewModel.NotRecordedText, HitQualityDetailSectionViewModel.NotRecordedText], ["0 hp", "DAMAGE DEALT", "DPS out", "OUT · WHAT YOU DID", "IN · WHAT THEY DID TO YOU"] },
        // ET-472: a fleet mate's run pulled with its combat shows the same tiles here.
        { RunCombatTelemetryTests.RealRunEvents(), CombatArrival.FleetMateShared, RealRunTiles, [CombatDetailSectionViewModel.NotRecordedText] },
        // ET-472: a fleet mate who withholds combat shows none of it.
        { RunCombatTelemetryTests.RealRunEvents(), CombatArrival.FleetMateWithheld, [CombatDetailSectionViewModel.NotRecordedText], ["65,732 hp"] },
        // A mate beside this pilot: withholding reads "not shared", a build that never sent combat reads "not synced yet".
        { RunCombatTelemetryTests.RealRunEvents(), CombatArrival.MateBesideOwnNotSynced, [.. RealRunTiles, "character 90000002 · not synced yet"], [] },
        { RunCombatTelemetryTests.RealRunEvents(), CombatArrival.MateBesideOwnWithheld, [.. RealRunTiles, "character 90000002 · not shared"], [] },
        // ET-472: this pilot's own run pulled back without combat keeps the stored one.
        { RunCombatTelemetryTests.RealRunEvents(), CombatArrival.OwnPulledBackWithout, RealRunTiles, [CombatDetailSectionViewModel.NotRecordedText] }
    };

    /// <summary>
    /// ET-468: COMBAT / TIMELINE stands straight under ACTIVITY and before ENEMIES (B2), with no boundary-damage tile
    /// (B5), and LOOT says why containers are not counted (B6).
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(AbyssalCombat))]
    public async Task AbyssalDetail_ShowsItsStoredCombat_OrSaysWhyNot(GameLogEvent[]? combat, CombatArrival arrival,
        string[] shown, string[] absent)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal,
            RunCombatTelemetryTests.RunStart, 0, null, 30004079), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, RunCombatTelemetryTests.RunStop,
            RunCombatTelemetryTests.RunStop.AddMinutes(1), [], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = "3|Dark", ObservedAtUtc = RunCombatTelemetryTests.RunStart }],
            CombatEvents: combat), cancellationToken);
        await _ArriveBySyncAsync(instance, started.Value, arrival, cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.All(shown, text => Assert.Contains(text, texts));
        Assert.All(absent, text => Assert.DoesNotContain(text, texts));
        Assert.DoesNotContain(texts, text => text.Contains("BOUNDARY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(texts, text => text.StartsWith("Containers opened: not counted", StringComparison.Ordinal));
        Assert.Equal(["ACTIVITY", "COMBAT / TIMELINE", "ENEMIES", "HIT QUALITY"],
            texts.Where(text => text is "ACTIVITY" or "COMBAT / TIMELINE" or "ENEMIES" or "HIT QUALITY"));
        // AC3: a missile never carries a percentage, here or in the header.
        Assert.DoesNotContain(texts, text => Regex.IsMatch(text, @"d%"));
        if (combat is not null && arrival == CombatArrival.Saved)
        {
            _AssertRealRunHitQuality(texts, _HitQualityOf(await _PresentAsync(instance, 758, cancellationToken)));
        }
    }

    private static HitQualityDetailSectionViewModel _HitQualityOf((ActivityDetailWindow Window, Window Root) presented) =>
        presented.Root.GetVisualDescendants().OfType<HitQualityDetailSectionView>()
            .Select(view => view.DataContext).OfType<HitQualityDetailSectionViewModel>().Single();

    // AC1: Σ OUT is COMBAT's dealt; a source's named weapon and its weapon-less lines never share a row.
    private static void _AssertRealRunHitQuality(List<string> texts, HitQualityDetailSectionViewModel hit)
    {
        Assert.Equal(65_732, hit.OutRows.Sum(row => long.Parse(row.Total, NumberStyles.AllowThousands, CultureInfo.InvariantCulture)));
        Assert.Contains("65,732 hp", texts);
        HitQualityRowViewModel missiles = Assert.Single(hit.OutRows, row => row.Target == "Ephialtes Dissipator");
        Assert.Equal(("Nova Fury Light Missile", "27", "154", "998", "15,178"), (missiles.Weapon, missiles.Shots, missiles.Min, missiles.Max, missiles.Total));
        Assert.Equal(HitQualityDetailSectionViewModel.MissileLabel, missiles.Application);
        Assert.All(hit.OutRows, row => Assert.Equal(HitQualityDetailSectionViewModel.MissileLabel, row.Application));
        Assert.Equal(hit.InRows.Count, hit.InRows.Select(row => (row.Target, row.Weapon)).Distinct().Count());
        HitQualityRowViewModel noWeapon = Assert.Single(hit.InRows, row => row.Target == "Ephialtes Dissipator" && !row.HasWeapon);
        Assert.Equal(["2", "·", "1", "5", "·", "1", "9"], noWeapon.Counts);
        Assert.All(hit.InRows, row => Assert.False(row.HasApplication));
    }

    // Hands the saved run back through the pull as the server would (ET-472), as a fleet mate's or as this pilot's own.
    private static async Task _ArriveBySyncAsync(TestClientInstance instance, Guid runId, CombatArrival arrival,
        CancellationToken cancellationToken)
    {
        if (arrival == CombatArrival.Saved)
        {
            return;
        }

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().AsNoTracking().Include(saved => saved.Parameters)
            .SingleAsync(saved => saved.Id == runId, cancellationToken);
        RunCombatTimeline? timeline = await db.Set<RunCombatTimeline>().AsNoTracking()
            .Include(saved => saved.Series).Include(saved => saved.HitTallies)
            .SingleOrDefaultAsync(saved => saved.RunId == runId, cancellationToken);
        if (arrival == CombatArrival.OwnPulledBackWithout)
        {
            await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Pilot", 90000001));
            await db.Set<Run>().Where(saved => saved.Id == runId)
                .ExecuteUpdateAsync(saved => saved.SetProperty(row => row.SyncState, RunSyncState.Synced), cancellationToken);
        }
        else
        {
            if (arrival is CombatArrival.MateBesideOwnNotSynced or CombatArrival.MateBesideOwnWithheld)
            {
                await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Pilot", 90000001));
                await db.Set<Run>().Where(saved => saved.Id == runId)
                    .ExecuteUpdateAsync(saved => saved.SetProperty(row => row.GroupCode, "HF-472A"), cancellationToken);
                run.Id = Guid.CreateVersion7();
            }
            else
            {
                await db.Set<Run>().Where(saved => saved.Id == runId).ExecuteDeleteAsync(cancellationToken);
            }

            run.CharacterId = 90000002;
            run.GroupCode = "HF-472A";
        }

        RunCombatTimeline? sent = arrival == CombatArrival.FleetMateShared ? timeline : null;
        RunWirePayload payload = new()
        {
            Run = RunWireData.FromEntity(run, sent, combatWithheld: arrival is CombatArrival.FleetMateWithheld or CombatArrival.MateBesideOwnWithheld),
            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await instance.Services.GetRequiredService<RunSynchronizationApplier>()
            .ApplyAsync("https://server.example", [payload], new HashSet<Guid>(), cancellationToken);
    }

    /// <summary>ET-470: a run without a stored timeline derives its combat from the pilot's stored hits; one with a timeline never does.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderAbyssalRun_DerivesItsCombatFromStoredHits_OnlyWithoutATimeline(bool hasTimeline)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime start = RunCombatTelemetryTests.RunStart;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, start, 0, null,
            30004079), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, RunCombatTelemetryTests.RunStop,
            RunCombatTelemetryTests.RunStop.AddMinutes(1), [], [], [], [],
            CombatEvents: hasTimeline ? RunCombatTelemetryTests.RealRunEvents() : null), cancellationToken);
        // Stored as the client writes them: the EVE clock value under a +02:00 label.
        CombatSample Hit(int characterId, DamageDirection direction, int amount, int second) => new()
        {
            OwnerId = "local", CharacterId = characterId, Direction = direction, Amount = amount, Target = "Rat",
            Timestamp = new DateTimeOffset(start.AddSeconds(second), TimeSpan.FromHours(2))
        };
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
                         .CreateDbContextAsync(cancellationToken))
        {
            db.Set<CombatSample>().AddRange(Hit(90000001, DamageDirection.Outgoing, 500, 10),
                Hit(90000001, DamageDirection.Outgoing, 998, 20), Hit(90000001, DamageDirection.Incoming, 74, 30),
                Hit(90000001, DamageDirection.Incoming, 38, 31), Hit(90000001, DamageDirection.Incoming, 0, 32),
                Hit(90000001, DamageDirection.Outgoing, 7777, -1),
                Hit(90000002, DamageDirection.Outgoing, 5555, 40), Hit(90000001, DamageDirection.Outgoing, 3333, -86400));
            await db.SaveChangesAsync(cancellationToken);
        }

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Equal(!hasTimeline, texts.Any(text => text.EndsWith("from stored hits", StringComparison.Ordinal)));
        Assert.Equal(hasTimeline, texts.Contains("65,732 hp"));
        Assert.Equal(hasTimeline ? 0 : 1, texts.Count(text => text == "1,498 hp"));
        Assert.Equal(hasTimeline ? 0 : 1, texts.Count(text => text == "112 hp"));
        Assert.Equal(hasTimeline ? 0 : 4, texts.Count(text => text == "not recorded"));
        // The stored miss (0 damage) is no hit on you.
        Assert.Equal(hasTimeline ? 0 : 1, texts.Count(text => text.EndsWith("· 2 hits on you", StringComparison.Ordinal)));
        Assert.Equal(hasTimeline, texts.Any(text => text is "0 GJ" or "0 hp" or "0"));
    }

    /// <summary>ET-469 AC4/AC5: run 5 with the two rooms the pilot marked reads as three rooms whose time and damage add up to
    /// the run's, whose loot adds up to LOOT's own total; without any boundary the section says why in one line and draws no
    /// room. Counter-proof: let the whole run be "room 1" and the no-rooms row draws a room; price the room loot unlike LOOT.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbyssalDetail_ShowsItsRooms_OrOneLineWhy(bool hasRooms)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            cancellationToken);
        DateTime start = RunCombatTelemetryTests.RunStart;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, start, 0, null, 30004079),
            cancellationToken);
        RunLootCaptureInput CaptureAt(int minute, long quantity) => new()
        {
            CapturedAtUtc = start.AddMinutes(minute), Source = LootCaptureSource.Clipboard, ContentHash = $"C{minute}",
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
        };
        RunParameterInput Boundary(int seconds) => new()
        {
            ParameterKey = RunParameterKey.RoomStarted, TypedValue = string.Empty, ObservedAtUtc = start.AddSeconds(seconds)
        };
        await dispatcher.Send(new SaveRunCommand(started.Value, RunCombatTelemetryTests.RunStop,
            RunCombatTelemetryTests.RunStop.AddMinutes(1), [CaptureAt(2, 3), CaptureAt(6, 2)], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = "3|Dark", ObservedAtUtc = start },
                .. hasRooms ? new[] { Boundary(284), Boundary(481) } : []],
            CombatEvents: RunCombatTelemetryTests.RealRunEvents()), cancellationToken);

        (ActivityDetailWindow window, Window root) = await _PresentAsync(instance, 758, cancellationToken);
        RoomsDetailSectionViewModel rooms = Assert.IsType<ActivityDetailViewModel>(window.DataContext).Rooms();
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.Contains("ROOMS", texts);
        if (!hasRooms)
        {
            Assert.Equal((RoomsDetailSectionViewModel.NoRoomsText, 0), (rooms.RoomsEmptyText, rooms.Rows.Count));
            Assert.Contains(RoomsDetailSectionViewModel.NoRoomsText, texts);
            return;
        }

        Assert.Equal(["ROOM 1", "ROOM 2", "ROOM 3"], rooms.Rows.Select(row => row.Title));
        Assert.Equal(("10:36", "65,732"), (rooms.Total!.TimeText, rooms.Total.DamageText));
        Assert.StartsWith(rooms.Total.LootText, Assert.IsType<ActivityDetailViewModel>(window.DataContext).Loot().HeaderSummary);
        Assert.Equal(["300 ISK", "200 ISK", "—"], rooms.Rows.Select(row => row.LootText));
        Assert.Equal("3 rooms · by hand · 0 of 3 counted", rooms.HeaderSummary);
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
        return _Present(await _WindowAsync(instance, width, cancellationToken), width);
    }

    private static (ActivityDetailWindow Window, Window Root) _Present(ActivityDetailWindow window, double width)
    {
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
    /// An observed row is not a zero count: the detail keeps it distinct and never hides the number of types when
    /// none of them were counted.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false, "3 counted · 3 types")]
    [InlineData(true, "none counted · 3 types")]
    public async Task ObservedEnemies_WithoutCounts_RemainDistinctFromZeroCounts(bool allAreUncounted, string expectedSummary)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [],
            [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(3), Isk = 214_188m }],
            [
                new RunEnemyObservationInput { Count = allAreUncounted ? 0 : 3, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) },
                new RunEnemyObservationInput { Count = 0, EnemyTypeId = 112, EnemyName = "Centii Loyalist", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) },
                new RunEnemyObservationInput { Count = 0, EnemyTypeId = 113, EnemyName = "Centii Enslaver", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }
            ],
            []), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == $"{214_188m:N0} ISK");        // the bounty is on screen
        Assert.Contains(texts, text => text == expectedSummary);
        Assert.Equal(allAreUncounted ? 3 : 2, texts.Count(text => text == "not counted"));
        // Neither a by-type row nor the per-character breakdown and its TOTAL may read as a zero.
        Assert.DoesNotContain(texts, text => text is "0" or "0 enemies");
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
        Assert.Equal(2, viewModel.Fleet().Rows.Count);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeleted);
        Assert.False(viewModel.CanDelete);
        Assert.Equal(90000002, Assert.Single(viewModel.Fleet().Rows).CharacterId);

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
        Assert.Equal(OpsecText.Mark("Homefront"), viewModel.SiteText);
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

        // Own characters are known only to a test that registered some; the others read every run as this pilot's.
        HashSet<long> own = [.. (await instance.Services.GetRequiredService<ICharacterRegistry>().GetAllAsync(cancellationToken))
            .Select(character => (long)(character.EsiCharacterId ?? 0))];
        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(), ownCharacterIds: own.Count == 0 ? null : own);
        await viewModel.LoadAsync(cancellationToken);
        return new ActivityDetailWindow(viewModel) { Width = width, Height = 1400 };
    }

    private static async Task _SaveSiteRunAsync(ICqrsDispatcher dispatcher, long characterId, string? groupCode,
        CancellationToken cancellationToken, IReadOnlyList<RunEnemyObservationInput>? enemies = null,
        IReadOnlyList<RunParameterInput>? parameters = null, RunRole role = RunRole.Member)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, groupCode, Role: role), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], enemies ?? [], parameters ?? []), cancellationToken);
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

        Assert.Equal(2, viewModel.Bounty().BountyRows.Count);
        Assert.Contains(viewModel.Bounty().BountyRows, r => r.CharacterText == "Jithran" && r.IskText == $"{675_000m:N0} ISK");
        Assert.Contains(viewModel.Bounty().BountyRows, r => r.CharacterText == "Second Pilot" && r.IskText == $"{675_000m:N0} ISK");
        // The total is set apart from the rows, not folded into one of them, but it still has to equal their sum.
        Assert.Equal($"{1_350_000m:N0} ISK", viewModel.Bounty().BountyText);
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

        Assert.Equal(2, viewModel.Loot().LootOverview.Characters.Count);
        Assert.Contains(viewModel.Loot().LootOverview.Characters, r => r.CharacterText == "Jithran" && r.SubtotalText == $"{300m:N0} ISK");
        Assert.Contains(viewModel.Loot().LootOverview.Characters, r => r.CharacterText == "Second Pilot" && r.SubtotalText == $"{400m:N0} ISK");
        // The per-character blocks are set apart from the group's own total, but they still have to sum to it.
        Assert.Equal($"{700m:N0} ISK", viewModel.Loot().LootOverview.NetIskDisplay);
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

        Assert.Equal(2, viewModel.Enemies().EnemyCharacterRows.Count);
        Assert.Contains(viewModel.Enemies().EnemyCharacterRows, r => r.CharacterText == "Jithran" && r.CountText == "4 enemies");
        Assert.Contains(viewModel.Enemies().EnemyCharacterRows, r => r.CharacterText == "Second Pilot" && r.CountText == "2 enemies");
        // The by-type list keeps both species; the group total is set apart, not one more row of either list.
        Assert.Equal("6 enemies", viewModel.Enemies().EnemyTotalCountText);
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
