using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-130 deel 2 (<c>RunningRunLookup</c>/<c>GetRunningRunQuery</c> scoped per character) and ET-210 (starting a run
/// for several toons at once, sharing a group code) together — the second is what actually produces the state the
/// first has to read: N runs under one group code, one per character.
/// </summary>
public class MultipleConcurrentRunsTests
{
    private const int CharacterA = 90000010;
    private const int CharacterB = 90000011;

    // ── ET-130 deel 2: RunningRunLookup / GetRunningRunQuery per character ─────────────────────────

    /// <summary>
    /// Counter-proof from the grooming (2026-09-05): two different characters each running their own site must give
    /// two different answers. Against the pre-fix lookup (an app-wide <c>Count == 1</c>) this is RED — asking for
    /// A's run while B is also running makes the count 2 and the answer null, not A's row. A test with only one
    /// running run would stay green either way and prove nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task TwoCharactersEachRunning_GetRunningRunQuery_AnswersPerCharacter()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Guid runA = await _StartAsync(dispatcher, CharacterA, "Sansha Refuge");
        Guid runB = await _StartAsync(dispatcher, CharacterB, "Blood Raider Burrow");

        Result<RunningRunDto> forA = await dispatcher.Query(new GetRunningRunQuery(CharacterA));
        Result<RunningRunDto> forB = await dispatcher.Query(new GetRunningRunQuery(CharacterB));

        Assert.True(forA.IsSuccess, "character A's own run should not be ambiguous just because B is also running");
        Assert.Equal(runA, forA.Value!.Id);
        Assert.True(forB.IsSuccess, "character B's own run should not be ambiguous just because A is also running");
        Assert.Equal(runB, forB.Value!.Id);
    }

    /// <summary>
    /// Counter-proof from the grooming (2026-09-05): a character with nothing running gets nothing, even while
    /// another character has a run going. "Just hand back the one candidate there is" would be green here by
    /// accident; the app-wide count before this fix would call it unambiguous (count 1) and hand A's run to B.
    /// </summary>
    [AvaloniaFact]
    public async Task OneCharacterRunning_TheOtherCharactersOwnLookup_IsEmpty()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        await _StartAsync(dispatcher, CharacterA, "Sansha Refuge");

        Result<RunningRunDto> forB = await dispatcher.Query(new GetRunningRunQuery(CharacterB));

        Assert.False(forB.IsSuccess, "character B has nothing running, and A's run must not be handed to it");
    }

    private static async Task<Guid> _StartAsync(IDispatcher dispatcher, long characterId, string siteName)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(
            characterId, ActivityKind.Site, DateTime.UtcNow, SiteTypeId: 0, SiteName: siteName, SolarSystemId: null));
        Assert.True(started.IsSuccess);
        return started.Value;
    }

    // ── ET-210: starting a run for several toons at once ───────────────────────────────────────────

    /// <summary>AC-1/AC-2: two clients flying, both ticked at START — each gets its own stored run, and the two
    /// share one group code.</summary>
    [AvaloniaFact]
    public async Task StartWithTwoCharactersFlying_TickingBoth_RegistersARunForEach()
    {
        using var harness = await _TwoCharacters();
        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);

        await model.StartRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<RunningRunDto> runA = await dispatcher.Query(new GetRunningRunQuery(ActivityWindowHarness.CharacterId));
        Result<RunningRunDto> runB = await dispatcher.Query(new GetRunningRunQuery(90000002));
        Assert.True(runA.IsSuccess, "the acting character's own run was not stored");
        Assert.True(runB.IsSuccess, "the second ticked character got no run of its own");
        Assert.NotNull(runA.Value!.GroupCode);
        Assert.Equal(runA.Value.GroupCode, runB.Value!.GroupCode);
    }

    /// <summary>AC-3: ticking only one of the offered characters is exactly today's behaviour — one run, and
    /// (unlike the two-ticked case above) no group code minted for it.</summary>
    [AvaloniaFact]
    public async Task StartWithTwoCharactersFlying_TickingOnlyOne_BehavesLikeASingleStart()
    {
        using var harness = await _TwoCharacters();
        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([options[0].CharacterId]);

        await model.StartRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<RunningRunDto> runA = await dispatcher.Query(new GetRunningRunQuery(ActivityWindowHarness.CharacterId));
        Result<RunningRunDto> runB = await dispatcher.Query(new GetRunningRunQuery(90000002));
        Assert.True(runA.IsSuccess);
        Assert.False(runB.IsSuccess, "only one character was ticked; the other must get no run");
        Assert.Null(runA.Value!.GroupCode);
    }

    /// <summary>AC-5: one running client asks nothing extra — the picker is never even shown.</summary>
    [AvaloniaFact]
    public async Task StartWithOneCharacterFlying_AsksNothing()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();

        await model.StartRunCommand.ExecuteAsync(null);

        Assert.Null(harness.Dialogs.LastPrompt);
    }

    private static async Task<ActivityWindowHarness> _TwoCharacters()
    {
        var harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddSingleton<ILocalCharacterPresence>(
                new ActivityWindowHarness.StubPresence(inGame: true, ActivityWindowHarness.CharacterId, 90000002)));
        await harness.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Second Pilot", 90000002));
        return harness;
    }

    // ── STOP and SAVE apply to the whole group (ET-210) ────────────────────────────────────────────

    /// <summary>AC-6: STOP followed by SAVE leaves no run of the group Running — unlike ET-105, where each member
    /// only ever answers for their own row, here one pilot's STOP/SAVE settles every toon in the group.</summary>
    [AvaloniaFact]
    public async Task StopThenSave_OnAGroupRun_LeavesNoRunOfTheGroupRunning()
    {
        using var harness = await _TwoCharacters();
        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2);

        model.StopRun(DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => model.RunState == ActivityRunState.Stopped);
        await model.SaveRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Assert.False((await dispatcher.Query(new GetRunningRunQuery(ActivityWindowHarness.CharacterId))).IsSuccess,
            "the acting character's run was left Running");
        Assert.False((await dispatcher.Query(new GetRunningRunQuery(90000002))).IsSuccess,
            "the second character's run was left Running");
    }

    // ── The saved activity carries everyone's bounty, not just the acting character's (ET-210 review, 2026-09-09) ──

    /// <summary>
    /// Counter-proof, red against the pre-fix code: Jithran saved a five-character activity and its detail screen
    /// showed one 286,875 ISK bounty line where five were flown — the other four participants' bounty was simply
    /// missing, and the runs-overview row and the day total (which sum <c>ActivitySummary.BountyIsk</c>, itself
    /// correctly summed over every run in the group) both showed that same undercounted figure. The window only
    /// ever watches the ACTING character's gamelog directly; a sibling's own bounty has to come from
    /// <c>GamelogClientService.GetFleetRunBounty</c> or SAVE has nothing to write for them at all.
    /// </summary>
    [AvaloniaFact]
    public async Task SavedActivity_CarriesBountyForEveryParticipant_NotJustTheActingCharacter()
    {
        const long fleetId = 900;
        using var harness = await _TwoCharacters();
        harness.Services.GetRequiredService<IFleetParticipation>().Set(
        [
            new FleetParticipant(ActivityWindowHarness.CharacterId, fleetId, ClientOnly: true),
            new FleetParticipant(90000002, fleetId, ClientOnly: true)
        ]);
        var gamelog = harness.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000002, "Second Pilot");

        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2 && model.FleetId == fleetId);

        // The acting character's own kill — captured the way it always was, through this window's gamelog watch.
        await gamelog.AddBountyAsync(ActivityWindowHarness.CharacterName, new BountyEvent(DateTime.UtcNow, 675_000));
        // The second character's own kill — this window never watches their gamelog directly; only
        // GamelogClientService's own per-run tally (GetFleetRunBounty) knows about it.
        await gamelog.AddBountyAsync("Second Pilot", new BountyEvent(DateTime.UtcNow, 675_000));

        model.StopRun(DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => model.RunState == ActivityRunState.Stopped);
        await model.SaveRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery());
        Assert.True(overview.IsSuccess);
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);
        Assert.Equal(2, row.ParticipantCount); // this harness runs two characters; Jithran's own report had five
        Assert.Equal(1_350_000m, row.BountyIsk);
    }

    // ── Switching the character column mid-run must not lose the starter's own data (ET-210 review, 2026-09-09, round 3) ──

    /// <summary>
    /// Counter-proof, red against the pre-fix code: Jithran ran a site on five characters, switched the character
    /// column (deel 3) to check on his alts' loot, and switched back — and the SAVED activity was then missing his
    /// own bounty (4 of 5 participants shown) and every enemy he had hand-typed a count for. His own hypothesis,
    /// confirmed here: <c>_SwitchToRunAsync</c> cleared <c>_bounties</c> and rebuilt <c>_enemyObservations</c> on
    /// every switch, discarding whatever the character switched AWAY FROM had accumulated — including the starter's
    /// own data the moment he switched off himself and back.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingTheColumnAwayAndBack_DoesNotLoseTheStartersBountyOrEnemies()
    {
        const long fleetId = 901;
        using var harness = await _TwoCharacters();
        harness.Services.GetRequiredService<IFleetParticipation>().Set(
        [
            new FleetParticipant(ActivityWindowHarness.CharacterId, fleetId, ClientOnly: true),
            new FleetParticipant(90000002, fleetId, ClientOnly: true)
        ]);
        var gamelog = harness.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000002, "Second Pilot");

        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2 && model.FleetId == fleetId);

        // The starter's own bounty and a hand-typed enemy count, entered BEFORE switching away — exactly Jithran's
        // own sequence ("bij jithran enemies ingevoerd... [toen] de site runde").
        await gamelog.AddBountyAsync(ActivityWindowHarness.CharacterName, new BountyEvent(DateTime.UtcNow, 337_500));
        await gamelog.AddHitAsync(ActivityWindowHarness.CharacterName, DamageDirection.Outgoing, 500,
            "Centii Servant", HitQuality.Hits, DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => model.EnemyObservations.Count == 1);
        model.EnemyObservations[0].Count = 4;

        // Switch the column to the second character — exactly what deel 3's character column lets a pilot do
        // mid-run, and exactly what threw the starter's own data away before this fix.
        RunCharacterRowViewModel second = model.RunCharacters.Single(row => row.CharacterId == 90000002);
        model.SelectRunCharacterCommand.Execute(second);
        await ActivityWindowHarness.WaitUntil(() => model.RunId == second.RunId);

        // The starter's SECOND kill, landing on his own gamelog WHILE the window is showing someone else — the
        // half of the bug _bounties alone (filtered on whichever name is "acting" right now) can never fix, no
        // matter whether it is cleared on switch: GamelogClientService.GetFleetRunBounty is what still has this,
        // because it tracks every character's own tally independently of which one this window is looking at.
        await gamelog.AddBountyAsync(ActivityWindowHarness.CharacterName, new BountyEvent(DateTime.UtcNow, 337_500));

        // The second character makes their own kill, on their own gamelog, while their own run is what the column
        // shows — Jithran's chosen design (round 4): each character's own combat counts towards their own total,
        // not one shared tally.
        await gamelog.AddHitAsync("Second Pilot", DamageDirection.Outgoing, 500, "Centii Servant",
            HitQuality.Hits, DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => model.EnemyObservations.Count == 1);
        model.EnemyObservations[0].Count = 2;

        // Switch back to the starter.
        RunCharacterRowViewModel first = model.RunCharacters.Single(row => row.CharacterId == ActivityWindowHarness.CharacterId);
        model.SelectRunCharacterCommand.Execute(first);
        await ActivityWindowHarness.WaitUntil(() => model.RunId == first.RunId);
        await gamelog.AddBountyAsync("Second Pilot", new BountyEvent(DateTime.UtcNow, 675_000));

        model.StopRun(DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => model.RunState == ActivityRunState.Stopped);
        await model.SaveRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery());
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);
        Assert.Equal(2, row.ParticipantCount);
        Assert.Equal(1_350_000m, row.BountyIsk); // not 675,000: the starter's own share must not be missing

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            nameOf: id => id == ActivityWindowHarness.CharacterId ? "Starter" : "Second Pilot");
        await viewModel.LoadAsync();

        // Each character's own hand-typed count, kept over the switch and broken out per character with a total.
        Assert.Equal(2, viewModel.EnemyCharacterRows.Count);
        Assert.Contains(viewModel.EnemyCharacterRows, r => r.CharacterText == "Starter" && r.CountText == "4 enemies");
        Assert.Contains(viewModel.EnemyCharacterRows, r => r.CharacterText == "Second Pilot" && r.CountText == "2 enemies");
        Assert.Equal("6 enemies", viewModel.EnemyTotalCountText);
    }

    // ── The saved activity carries everyone's location and fit, not just the acting character's (ET-210 review, 2026-09-09) ──

    private const int SecondCharacterId = 90000002;

    /// <summary>
    /// Counter-proof, red against the pre-fix code: Jithran's saved five-character activity showed LOCATION "not
    /// recorded" and FIT "not recognised", even though the live window showed both for every toon during the run.
    /// Measured, not assumed to be the same shape as the bounty bug: <c>Run.SolarSystemId</c> was never set for a
    /// SITE run AT ALL (Mission is the only kind <c>StartRunCommand</c> ever resolved a system id for) — this is not
    /// a "siblings get nothing" gap, it is nobody ever asking the question. Fit, by contrast, genuinely is the same
    /// shape as the bounty bug: <see cref="IShipFitDetectionService.GetReading"/> already answers per character, but
    /// nothing ever called it for anyone.
    /// </summary>
    [AvaloniaFact]
    public async Task SavedActivity_CarriesLocationAndFitForEveryParticipant()
    {
        List<LocalFitting> fittings =
        [
            new() { Id = 101, Name = "Hall of Sacrifice 2x", ShipTypeId = 17715 },
            new() { Id = 102, Name = "Hall of Sacrifice 2x (alt)", ShipTypeId = 17715 }
        ];
        Dictionary<int, ShipFitDetectionReading> readings = new()
        {
            [ActivityWindowHarness.CharacterId] = _Observed(new ShipFitCandidate(101, fittings[0].Name, 17715)),
            [SecondCharacterId] = _Observed(new ShipFitCandidate(102, fittings[1].Name, 17715))
        };

        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(
                inGame: true, ActivityWindowHarness.CharacterId, SecondCharacterId));
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30004552, "Mabnen", 0.4)));
            services.AddSingleton<IShipFitDetectionService>(new FakePerCharacterFitDetection(readings));
            services.AddSingleton<IFittingRepository>(new FakeFittingRepository(fittings));
        });
        await harness.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Second Pilot", SecondCharacterId));

        ActivityWindowViewModel model = await harness.OpenAsync();
        // What the LOCATION section already had live, from the gamelog — the same source _ResolveSolarSystemId
        // reads (ActivityWindowViewModel.SolarSystem). Set through the gamelog service itself, not directly on the
        // window: a plain property assignment is wiped by the very first Refresh() tick, same as it is live.
        harness.Services.GetRequiredService<GamelogClientService>()
            .SetLocation(ActivityWindowHarness.CharacterName, "Mabnen", DateTime.UtcNow);
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2);

        model.StopRun(DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => model.RunState == ActivityRunState.Stopped);
        await model.SaveRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery());
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);
        Assert.Equal(30004552, row.SolarSystemId);

        Result<ActivityDetailDto> detail = await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId));
        Assert.True(detail.IsSuccess);
        Assert.Equal(30004552, detail.Value!.SolarSystemId);
        Assert.Equal(2, detail.Value.Runs.Count);
        Assert.All(detail.Value.Runs, run => Assert.NotNull(run.FitNameSnapshot));
        // Each toon's OWN fit, not one name copied onto both rows.
        Assert.Equal(["Hall of Sacrifice 2x", "Hall of Sacrifice 2x (alt)"],
            detail.Value.Runs.Select(run => run.FitNameSnapshot).OrderBy(name => name));
    }

    private static ShipFitDetectionReading _Observed(ShipFitCandidate selected) =>
        new(ShipFitDetectionState.Observed, DateTimeOffset.UtcNow, selected.ShipTypeId, 1, "Ship",
            selected, ShipFitMatchReason.Manual, [selected]);

    private sealed class FakePerCharacterFitDetection(IReadOnlyDictionary<int, ShipFitDetectionReading> readings)
        : IShipFitDetectionService
    {
        public ShipFitDetectionReading GetReading(int characterId) =>
            readings.TryGetValue(characterId, out ShipFitDetectionReading? reading) ? reading : ShipFitDetectionReading.Unobserved;

        public Task<Result> SetManualFitAsync(int characterId, int? fittingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> DetachFitAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeFittingRepository(IReadOnlyList<LocalFitting> fittings) : IFittingRepository
    {
        public Task UpsertAsync(LocalFitting fitting, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LocalFitting>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(fittings);
        public Task<IReadOnlyList<LocalFitting>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalFitting>>([.. fittings.Where(fitting => fitting.OwnerId == ownerId)]);
        public Task<LocalFitting?> FindByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(fittings.FirstOrDefault(fitting => fitting.Id == id));
        public Task<LocalFitting?> FindByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(fittings.FirstOrDefault(fitting => fitting.OwnerId == ownerId && fitting.EsiFittingId == esiFittingId));
        public Task<LocalFitting?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(fittings.FirstOrDefault(fitting => fitting.ContentHash == contentHash));
        public Task BackfillContentHashesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string name, string? description, string? tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByIdAsync(int id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
