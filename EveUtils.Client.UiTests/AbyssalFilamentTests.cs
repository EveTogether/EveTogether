using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-241 — an abyssal pocket has no bounty, and a run flown in one is never "Unnamed site": it is named after the
/// filament that opened it, from tier and weather now actually persisted on the run rather than living only in the
/// open window's own <c>WeatherIndex</c>/<c>TierIndex</c> (measured in Jithran's own <c>client.db</c>: zero
/// <c>RunParameter</c> rows for either, on a run started as "Agitated Dark"). ET-239's part of the same ticket: the
/// fleet commander's own scanner group, tier and weather now reach a joining member instead of that member's window
/// falling back to its own unrelated leftovers (Raymond seeing "T0 Dark" on Jithran's real T3 Dark, 2026-09-11).
/// </summary>
public sealed class AbyssalFilamentTests
{
    private const long FleetId = 4242;
    private const string GroupCode = "HF-F0CU";
    private const int Commander = 100;

    // ── AC-1/AC-3: no BOUNTY for an abyssal, a site keeps its own ──────────────────────────────────────

    [Fact]
    public void RunTypeCatalogue_Abyssal_HasNoBountySection_InTheWindowOrTheDetailScreen()
    {
        RunTypeDefinition abyssal = RunTypeCatalogue.For(RunTypeId.Abyssal);

        Assert.DoesNotContain(RunSectionId.Bounty, abyssal.WindowSections);
        Assert.DoesNotContain(RunSectionId.Bounty, abyssal.DetailSections);
    }

    [Fact]
    public void RunTypeCatalogue_ASite_StillHasItsBountySection()
    {
        RunTypeDefinition site = RunTypeCatalogue.For(RunTypeId.Unknown);

        Assert.Contains(RunSectionId.Bounty, site.WindowSections);
        Assert.Contains(RunSectionId.Bounty, site.DetailSections);
    }

    // ── AC-2: the name is the filament's, never "Unnamed site" ─────────────────────────────────────────

    [Theory]
    [InlineData("2|Dark", "Agitated Dark")]
    [InlineData("5|Firestorm", "Chaotic Firestorm")]
    [InlineData(null, "Abyssal")]
    [InlineData("", "Abyssal")]
    [InlineData("not a number|Dark", "Abyssal")]
    [InlineData("99|Dark", "Abyssal")]
    public void AbyssalFilamentName_ReadsTheTierWordAndWeather_OrFallsBackToTheTypesOwnName(
        string? stored, string expected) =>
        Assert.Equal(expected, AbyssalFilamentName.From(stored));

    /// <summary>
    /// Counter-proof for the whole storage half of ET-241: before <c>ActivityWindowSectionViewModel.AddToSave</c>
    /// wrote it, this table had zero rows for any abyssal run, exactly what Jithran measured. SAVE is what commits
    /// it, and the same value reads back as "Agitated Dark" on the runs overview and the detail screen — never
    /// "Unnamed site" on the one and never "site not recorded" on the other, and it is never offered as a reward
    /// chip either, since it is a fact about the run and not something the pilot earned.
    /// </summary>
    [AvaloniaFact]
    public async Task SavingAnAbyssal_PersistsTierAndWeather_AndNamesTheRunByItsFilament_EverywhereButNeverAsAReward()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        await model.Activity().SelectTierCommand.ExecuteAsync(2);
        await model.Activity().SelectWeatherCommand.ExecuteAsync(0);
        await model.StartRunCommand.ExecuteAsync(null);
        Assert.NotNull(model.RunId);
        await model.SaveRunCommand.ExecuteAsync(null);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        RunParameter filament = await db.Set<RunParameter>()
            .SingleAsync(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament);
        Assert.Equal("2|Dark", filament.TypedValue);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<System.Collections.Generic.IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery());
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);
        Assert.Null(row.SiteName);
        Assert.Equal("2|Dark", row.AbyssalFilamentText);
        Assert.DoesNotContain(row.Rewards, reward => reward.ParameterKey == RunParameterKey.AbyssalFilament);

        var overviewRow = new ActivityOverviewRowViewModel(
            row, _ => "?", _ => Task.CompletedTask, _ => Task.CompletedTask);
        Assert.Equal("Agitated Dark", overviewRow.SiteText);
        Assert.DoesNotContain(overviewRow.Chips, chip => chip.Text.Contains("FILAMENT", StringComparison.Ordinal));

        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId);
        await detail.LoadAsync();
        Assert.Equal("Agitated Dark", detail.Activity().SiteText);
    }

    // ── ET-248: MISSION never leaks onto an abyssal's detail screen ────────────────────────────────────

    /// <summary>
    /// Counter-proof: Jithran + Raymond's Fierce Dark, 2026-09-11 — the detail screen showed a MISSION section with
    /// two rows reading "ABYSSAL FILAMENT 3|Dark", once per run of the group. Measured cause:
    /// <c>RunTypeCatalogue.Abyssal.DetailSections</c> never claims <c>RunSectionId.Mission</c>, but
    /// <c>ActivityDetailViewModel._ApplySectionsPerType</c> also shows an unclaimed section when it
    /// <c>HasContent</c> — a deliberate rule (ET-236) so a reward booked against a type that never declared MISSION
    /// still reaches the screen. <c>MissionDetailSectionViewModel._IsRewardRow</c> used to exclude everything but a
    /// fixed list of non-reward keys, and <c>RunParameterKey.AbyssalFilament</c> (added by ET-241, after that list
    /// was written) was never on it, so it read as two reward rows and <c>HasContent</c> came back true. Red before
    /// the fix (an inclusion list instead): both runs of the group carry the parameter, exactly as in the report.
    /// </summary>
    [AvaloniaFact]
    public async Task AbyssalDetail_HasNoMissionSection_EvenThoughEveryRunInTheGroupCarriesTheFilamentParameter()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveAbyssalRunAsync(dispatcher, 90000001, GroupCode, solarSystemId: null, "3|Dark");
        await _SaveAbyssalRunAsync(dispatcher, 90000002, GroupCode, solarSystemId: null, "3|Dark");
        await dispatcher.Send(new RebuildActivitySummariesCommand());
        ActivityOverviewRowDto row = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery())).Value!);

        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId);
        await detail.LoadAsync();

        Assert.DoesNotContain(detail.Sections, section => section is MissionDetailSectionViewModel);
        Assert.Contains("no MISSION", detail.AbsentSectionsText!, StringComparison.Ordinal);
    }

    // ── ET-248: LOCATION on an abyssal — the entry system when known, never "not recorded" ────────────

    [AvaloniaFact]
    public async Task AbyssalDetail_LocationReadsTheEntrySystem_WhenItIsKnown()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveAbyssalRunAsync(dispatcher, 90000001, null, solarSystemId: 30004079, "2|Dark");
        ActivityOverviewRowDto row = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery())).Value!);

        var sde = new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30004079, "Dresi", 0.5));
        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId, sde: sde);
        await detail.LoadAsync();

        Assert.True(detail.Activity().IsLocationShown);
        Assert.Equal("entered from Dresi", detail.Activity().LocationText);
        Assert.Equal("Abyssal · entered from Dresi", detail.Activity().HeaderSummary);
    }

    [AvaloniaFact]
    public async Task AbyssalDetail_HasNoLocationRow_WhenTheEntrySystemIsUnknown()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveAbyssalRunAsync(dispatcher, 90000001, null, solarSystemId: null, "2|Dark");
        ActivityOverviewRowDto row = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery())).Value!);

        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId);
        await detail.LoadAsync();

        Assert.False(detail.Activity().IsLocationShown);
        // Never "not recorded" — an abyssal pocket has no location of its own, so an unknown entry system is a row
        // that is not there, not one that measured nothing.
        Assert.DoesNotContain("not recorded", detail.Activity().HeaderSummary, StringComparison.Ordinal);
        Assert.Equal("Abyssal", detail.Activity().HeaderSummary);
    }

    private static async Task<Guid> _SaveAbyssalRunAsync(
        IDispatcher dispatcher, long characterId, string? groupCode, int? solarSystemId, string filamentValue)
    {
        DateTime startedAtUtc = new(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Abyssal,
            startedAtUtc, 0, null, solarSystemId, groupCode));
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(20),
            startedAtUtc.AddMinutes(21), [], [], [],
            [
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = filamentValue,
                    ObservedAtUtc = startedAtUtc
                }
            ]));
        return started.Value;
    }

    // ── ET-239 + ET-241's fleet part: the commander's own facts reach a joining member ─────────────────

    [AvaloniaFact]
    public async Task JoinFleetRun_AdoptsTheCommandersSignatureGroup_WhenThisMemberHasNoneOfHisOwn()
    {
        using var instance = _Instance();
        await _SeedAsync(instance);

        using var joined = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        joined.JoinFleetRun(new RunGroupCodeStart(FleetId, ActivityKind.Site, GroupCode, DateTime.UtcNow, true,
            "Sansha's Data Mine", "Shousran", "RUS-326", SignatureGroupSnapshot: "Data Site"));
        await _Settle(() => joined.RunId is not null);

        Assert.Equal("Data Site", joined.RunType.Name);
    }

    [AvaloniaFact]
    public async Task JoinFleetRun_KeepsAMembersOwnSignatureGroup_WhenHeAlreadyCopiedOneHimself()
    {
        using var instance = _Instance();
        await _SeedAsync(instance);

        using var joined = new ActivityWindowViewModel(ActivityKind.Site, instance.Services)
        {
            SignatureGroup = "Relic Site"
        };
        joined.JoinFleetRun(new RunGroupCodeStart(FleetId, ActivityKind.Site, GroupCode, DateTime.UtcNow, true,
            SignatureGroupSnapshot: "Data Site"));
        await _Settle(() => joined.RunId is not null);

        Assert.Equal("Relic Site", joined.RunType.Name);
    }

    /// <summary>
    /// Reproduces Raymond's report verbatim (2026-09-11): Jithran started a T3 Dark, Raymond's own joined window
    /// showed T0 Dark. Measured cause: a member's window restores its own last-remembered tier and weather on
    /// <c>LoadAsync</c>, which in the real app runs AFTER <c>JoinFleetRun</c> — so a stale, unrelated answer from
    /// some earlier abyssal silently overwrote the commander's real one. Red without either half of the fix (the
    /// commander's own answer applied unconditionally in <c>JoinFleetRun</c>, and
    /// <c>ActivityWindowSectionViewModel.Load</c> restoring only where nothing is set yet).
    /// </summary>
    [AvaloniaFact]
    public async Task JoinFleetRun_AdoptsTheCommandersAbyssalFacts_OverALeftoverRememberedSetting()
    {
        using var instance = _Instance();
        await _SeedAsync(instance);
        ISettingRepository settings = instance.Services.GetRequiredService<ISettingRepository>();
        // Tranquil (T0) and Dark — index 0 for both, exactly what a member with no remembered choice at all would
        // also read, which is precisely how this went unnoticed.
        await settings.UpsertAsync(ActivityWindowSectionViewModel.TierSettingKey, "0");
        await settings.UpsertAsync(ActivityWindowSectionViewModel.WeatherSettingKey, "0");

        using var joined = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        // Real ordering (FleetRunWindowPresenter._Open): JoinFleetRun runs before the window's own LoadAsync.
        joined.JoinFleetRun(new RunGroupCodeStart(FleetId, ActivityKind.Abyssal, GroupCode, DateTime.UtcNow, true,
            AbyssalTierIndex: 3, AbyssalWeatherName: "Dark"));
        await joined.LoadAsync();
        await _Settle(() => joined.RunId is not null);

        Assert.Equal(3, joined.TierIndex);
        Assert.Equal("Dark", joined.Weather?.Name);
    }

    /// <summary>The other half of the same rule: a window with nothing of its own — no join, nothing remembered —
    /// still falls back to "not set", never a first-list guess.</summary>
    [AvaloniaFact]
    public async Task AWindowWithNoJoinAndNothingRemembered_StaysUnset()
    {
        using var instance = _Instance();
        var fresh = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await fresh.LoadAsync();

        Assert.Null(fresh.TierIndex);
        Assert.Null(fresh.WeatherIndex);
    }

    /// <summary>The message ET-241 asked to check for: does the commander changing tier or weather mid-run reach a
    /// member already on the group code? Before <c>FleetRunGroupAbyssalUpdatedEvent</c> existed there was nothing
    /// to carry it, so a member's window stayed on whatever it joined with for the rest of the run.</summary>
    [AvaloniaFact]
    public async Task CommandersAbyssalChange_ReachesAMembersAlreadyJoinedWindow()
    {
        using var instance = _Instance();
        await _SeedAsync(instance);
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Commander, FleetId, ClientOnly: true, Commander)]);

        using var fc = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await fc.LoadAsync();
        await fc.StartRunCommand.ExecuteAsync(null);
        Assert.NotNull(fc.GroupCode);

        using var joined = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        joined.JoinFleetRun(new RunGroupCodeStart(FleetId, ActivityKind.Abyssal, fc.GroupCode!, DateTime.UtcNow, true));
        await _Settle(() => joined.RunId is not null);

        await fc.Activity().SelectTierCommand.ExecuteAsync(3);
        await fc.Activity().SelectWeatherCommand.ExecuteAsync(0);
        await _Settle(() => joined.TierIndex == 3);

        Assert.Equal(3, joined.TierIndex);
        Assert.Equal("Dark", joined.Weather?.Name);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static TestClientInstance _Instance() => TestClientInstance.Create(services =>
    {
        services.AddSingleton<IDialogService>(new RecordingDialogService());
        services.AddSingleton<IToastService>(new RecordingToastService());
        services.AddSingleton<ILocalCharacterPresence>(new AnyoneInGame());
    });

    private static async Task _SeedAsync(TestClientInstance instance) =>
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Jithran", Commander));

    private static async Task _Settle(Func<bool> until)
    {
        for (var attempt = 0; attempt < 100 && !until(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private sealed class AnyoneInGame : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => true;
        public bool? IsInGame(int characterId) => true;
        public IDisposable Subscribe(Action handler) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
