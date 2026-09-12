using System;
using System.Collections.Generic;
using System.IO;
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
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
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

    // ── ET-211: a clipboard copy with a known sender lands on that character's own run ─────────────

    /// <summary>
    /// Counter-proof 1 from the ET-211 grooming: two characters each running their own site, a copy whose
    /// <c>CharacterId</c> (resolved from the clipboard's own <c>CopiedByCharacter</c>) is B lands on B's run, not
    /// A's — even with <c>PreferredRunId</c> pointing at A's own run, the way an open activity window for A would
    /// set it. A known copier scopes <c>RunningRunLookup</c> to their own run; it does not defer to whichever window
    /// happens to be open for somebody else. Red against the pre-fix handler, which never read the character at all.
    /// </summary>
    [AvaloniaFact]
    public async Task LootWithAKnownCopier_LandsOnThatCharactersOwnRun_EvenWithAnotherRunPreferred()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Guid runA = await _StartAsync(dispatcher, CharacterA, "Sansha Refuge");
        Guid runB = await _StartAsync(dispatcher, CharacterB, "Blood Raider Burrow");

        Result<RunLootCaptureSaveResult> result = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = DateTime.UtcNow,
            Source = LootCaptureSource.Clipboard,
            CharacterId = CharacterB,
            PreferredRunId = runA,
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 1, LootKind = LootKind.Gained }]
        }));

        Assert.True(result.IsSuccess);
        Result<RunLootOverview> lootB = await dispatcher.Query(new GetRunLootQuery(runB));
        Assert.Single(lootB.Value!.Captures);
        Result<RunLootOverview> lootA = await dispatcher.Query(new GetRunLootQuery(runA));
        Assert.Empty(lootA.Value!.Captures);
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
        await ActivityWindowHarness.WaitUntil(() => model.Enemies().EnemyObservations.Count == 1);
        model.Enemies().EnemyObservations[0].Count = 4;

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
        await ActivityWindowHarness.WaitUntil(() => model.Enemies().EnemyObservations.Count == 1);
        model.Enemies().EnemyObservations[0].Count = 2;

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

        // ET-212: the row reads the recorded CharacterNameSnapshot, not this live nameOf lookup — "Starter" here
        // would never be shown, regardless of what this function answers for the starter's id.
        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            nameOf: id => id == ActivityWindowHarness.CharacterId ? "Starter" : "Second Pilot");
        await viewModel.LoadAsync();

        // Each character's own hand-typed count, kept over the switch and broken out per character with a total.
        Assert.Equal(2, viewModel.Enemies().EnemyCharacterRows.Count);
        Assert.Contains(viewModel.Enemies().EnemyCharacterRows,
            r => r.CharacterText == ActivityWindowHarness.CharacterName && r.CountText == "4 enemies");
        Assert.Contains(viewModel.Enemies().EnemyCharacterRows, r => r.CharacterText == "Second Pilot" && r.CountText == "2 enemies");
        Assert.Equal("6 enemies", viewModel.Enemies().EnemyTotalCountText);
    }

    /// <summary>
    /// ET-268: Jithran's own report, reproduced end to end — a Raid: Hall of Sacrifice started on several own
    /// characters must keep reading Homefront once the column switches to a sibling and back, not fall back to
    /// "Site" the way it did in his own database. <c>_SwitchToRunAsync</c> clears <c>MatchedSites</c> on every
    /// switch on purpose (a fresh copy must get a fresh match, not the previous column's) — RunType has to read the
    /// switched-to run's own stored dungeon id instead, or a switch throws away the very fact that made it Homefront.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingTheColumn_KeepsReadingHomefront_NotSite()
    {
        using var harness = await _TwoCharacters();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Raid: Hall of Sacrifice",
            [new SdeSite(10347, "Raid: Hall of Sacrifice", 70, "Homefront Operations", null, null, null, null, false, [])]);
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);

        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2);
        Assert.Equal("Homefront · Raid", model.RunType.Name);

        RunCharacterRowViewModel second = model.RunCharacters.Single(row => row.CharacterId == 90000002);
        model.SelectRunCharacterCommand.Execute(second);
        await ActivityWindowHarness.WaitUntil(() => model.RunId == second.RunId);
        Assert.Equal("Homefront · Raid", model.RunType.Name);

        RunCharacterRowViewModel first = model.RunCharacters.Single(row => row.CharacterId == ActivityWindowHarness.CharacterId);
        model.SelectRunCharacterCommand.Execute(first);
        await ActivityWindowHarness.WaitUntil(() => model.RunId == first.RunId);
        Assert.Equal("Homefront · Raid", model.RunType.Name);
    }

    // ── The live window's own running total covers the group, not just the viewed character (ET-210 review, round 4 follow-up) ──

    /// <summary>
    /// Counter-proof, red against the pre-fix code (no such property existed at all, so the live window had nothing
    /// beside ELAPSED but the acting character's own "135,000 ISK — own character" bounty figure — wrong for a
    /// group, and Jithran's own screenshot showed exactly that during a five-character run). Must include a
    /// character this window never watches directly (the second pilot's bounty only lives in
    /// <c>GamelogClientService.GetFleetRunBounty</c>), and must not move when the character column is switched —
    /// the same kind of confusion the round 3 bounty/enemies fix already cleaned up for the saved screen.
    /// </summary>
    [AvaloniaFact]
    public async Task LiveGroupTotalIsk_CoversTheWholeGroup_AndDoesNotChangeWhenTheColumnIsSwitched()
    {
        const long fleetId = 902;
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

        // The starter's own bounty, watched directly through this window's own gamelog filter.
        await gamelog.AddBountyAsync(ActivityWindowHarness.CharacterName, new BountyEvent(DateTime.UtcNow, 337_500));
        await ActivityWindowHarness.WaitUntil(() => model.HasGroupTotalIsk);
        Assert.Equal("337,500 ISK", model.GroupTotalIskText);

        // The second character's own bounty — this window never watches their gamelog directly; only
        // GamelogClientService's own per-run tally knows about it, and the group total must count it in anyway.
        await gamelog.AddBountyAsync("Second Pilot", new BountyEvent(DateTime.UtcNow, 675_000));
        model.Refresh(DateTime.UtcNow); // the tick the live clock runs on every second
        Assert.Equal("1,012,500 ISK", model.GroupTotalIskText);

        // Switching the column to the second character's own run must not move the figure: it is the group's
        // total, not whichever character the column happens to be showing.
        RunCharacterRowViewModel second = model.RunCharacters.Single(row => row.CharacterId == 90000002);
        model.SelectRunCharacterCommand.Execute(second);
        await ActivityWindowHarness.WaitUntil(() => model.RunId == second.RunId);
        model.Refresh(DateTime.UtcNow);
        Assert.Equal("1,012,500 ISK", model.GroupTotalIskText);
    }

    /// <summary>
    /// ET-211 follow-up to the counter-proof above: loot revisits the same "held sticky, never summed per
    /// character" decision bounty made under ET-210 (deliberately, because this ticket was still open) — now that a
    /// capture is attributed to whichever character's client actually copied it, the group total must add both
    /// participants' own runs together, and switching the column away and back must not move it either.
    /// </summary>
    [AvaloniaFact]
    public async Task LiveGroupTotalIsk_CoversLootPerCharacter_AndDoesNotChangeWhenTheColumnIsSwitched()
    {
        using var harness = await _TwoCharacters();
        await harness.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }
        ]);

        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Guid ownRunId = model.RunId!.Value;
        Guid secondRunId = model.Participants.Single(participant => participant.CharacterId == 90000002).RunId;

        // The starter's own capture, watched directly the way RunLoot already did before this ticket. The per-run
        // loot cache fills in asynchronously off the RunLootCapturedEvent, so the wait re-triggers the clock tick's
        // own recompute on every poll rather than the tick itself, which nothing here is driving during the test.
        await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = DateTime.UtcNow, Source = LootCaptureSource.Clipboard, PreferredRunId = ownRunId,
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, LootKind = LootKind.Gained }]
        }));
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.GroupTotalIskText == "300 ISK";
        });
        Assert.Equal("300 ISK", model.GroupTotalIskText);

        // The second character's own capture — this window's LOOT section never shows it (RunLoot follows whichever
        // run the column displays), yet the group total must count it in anyway.
        await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = DateTime.UtcNow, Source = LootCaptureSource.Clipboard, PreferredRunId = secondRunId,
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 2, LootKind = LootKind.Gained }]
        }));
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.GroupTotalIskText == "500 ISK";
        });

        // Switching the column to the second character's own run must not move the figure.
        RunCharacterRowViewModel second = model.RunCharacters.Single(row => row.CharacterId == 90000002);
        model.SelectRunCharacterCommand.Execute(second);
        await ActivityWindowHarness.WaitUntil(() => model.RunId == second.RunId);
        model.Refresh(DateTime.UtcNow);
        Assert.Equal("500 ISK", model.GroupTotalIskText);
    }

    /// <summary>
    /// ET-257: an own-toon group with no fleet at all — <c>FleetId</c> stays null throughout, unlike the round-4
    /// counter-proof above. Counter-proof, red against the pre-fix code: <c>_RefreshGroupTotalIsk</c> only summed
    /// per participant when <c>FleetId is { } fleetId</c>, so without one it fell back to the acting character's own
    /// <c>BountyIsk</c> and dropped the second toon's payout entirely — even though <c>RunBountyEntry</c> already
    /// held it (ET-219), live, before any save. Also proves AC-2: the BOUNTY section itself lists both toons' own
    /// shares, the same breakdown the detail screen already gives.
    /// </summary>
    [AvaloniaFact]
    public async Task LiveGroupTotalIsk_CoversBountyPerCharacter_EvenWithoutAFleet()
    {
        using var harness = await _TwoCharacters();
        var gamelog = harness.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000002, "Second Pilot");

        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2);
        Assert.Null(model.FleetId);

        await gamelog.AddBountyAsync(ActivityWindowHarness.CharacterName, new BountyEvent(DateTime.UtcNow, 337_500));
        await gamelog.AddBountyAsync("Second Pilot", new BountyEvent(DateTime.UtcNow, 675_000));
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.GroupTotalIskText == "1,012,500 ISK";
        });

        Assert.Equal(2, model.Bounty().BountyRows.Count);
        Assert.Contains(model.Bounty().BountyRows, row => row.CharacterText == ActivityWindowHarness.CharacterName && row.IskText == "337,500 ISK");
        Assert.Contains(model.Bounty().BountyRows, row => row.CharacterText == "Second Pilot" && row.IskText == "675,000 ISK");
    }

    /// <summary>
    /// ET-215: the run window's LOOT list is the per-character component the saved detail screen shows — a block per
    /// character with their own run's subtotal, whichever character the column shows, and a correction made in one
    /// block moves the group total at the top. Counter-proof: bind the list to the column's own run again and the
    /// second character's block is not there to correct.
    /// </summary>
    [AvaloniaFact]
    public async Task LiveLootSection_ShowsABlockPerCharacter_AndACorrectionInOneMovesTheGroupTotal()
    {
        using var harness = await _TwoCharacters();
        await harness.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }
        ]);
        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacters = (_, options) =>
            Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.Participants.Count == 2);
        ActivityLootViewModel loot = Assert.IsType<ActivityLootViewModel>(model.LootOverview);
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        foreach ((Guid runId, long quantity) in model.Participants.Select(participant =>
                     (participant.RunId, participant.CharacterId == 90000002 ? 2L : 3L)))
            await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
            {
                CapturedAtUtc = DateTime.UtcNow, Source = LootCaptureSource.Clipboard, PreferredRunId = runId,
                Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
            }));
        await ActivityWindowHarness.WaitUntil(() => loot.NetIskDisplay == "500 ISK");

        Assert.Equal(2, loot.Characters.Count);
        ActivityLootCharacterViewModel secondBlock = loot.Characters.Single(block => block.CharacterId == 90000002);
        Assert.Equal("200 ISK", secondBlock.SubtotalText);
        Assert.Equal("300 ISK", loot.Characters.Single(block => block.CharacterId == 90000001).SubtotalText);

        Assert.True(await secondBlock.Loot.ToggleExcludedAsync(secondBlock.Loot.Captures[0]));
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.GroupTotalIskText == "300 ISK";
        });
        Assert.Equal("300 ISK", model.GroupTotalIskText);
        Assert.Equal("300 ISK", loot.NetIskDisplay);

        // The column moving to the second character changes which run the holds belong to, not the list.
        model.SelectRunCharacterCommand.Execute(model.RunCharacters.Single(row => row.CharacterId == 90000002));
        await ActivityWindowHarness.WaitUntil(() => model.RunId == secondBlock.RunId);
        Assert.Equal(2, loot.Characters.Count);
        Assert.Same(secondBlock, loot.Characters.Single(block => block.CharacterId == 90000002));
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

    // ── The pilot's own system, stored at mission start the same way a site's already is (ET-253) ──

    /// <summary>
    /// Counter-proof, red against the pre-fix code: a regular agent's mission (no "Report to" line in the capture —
    /// mission-captures.md) has no agent, and so no <c>MissionSolarSystemId</c> either (ET-176's own station-only
    /// source). <c>Run.SolarSystemId</c> stayed null even though the pilot's own live location was known the whole
    /// time the run window was open — the same source a site's own fix already reads via
    /// <c>ActivityWindowViewModel._ResolveSolarSystemId</c>. LOCATION and the ACTIVITY header both read "not
    /// recorded" for a run measured this way in Jithran's own <c>client.db</c> (ET-253).
    /// </summary>
    [AvaloniaFact]
    public async Task SavedMissionActivity_CarriesThePilotsOwnSystem_EvenWithNoAgentInTheCapture()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30004079, "Aphend", 0.6))));

        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Mission);
        model.SignatureName = "Mining Misappropriation";
        harness.Services.GetRequiredService<GamelogClientService>()
            .SetLocation(ActivityWindowHarness.CharacterName, "Aphend", DateTime.UtcNow);

        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => model.RunId is not null);

        model.StopRun(DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => model.RunState == ActivityRunState.Stopped);
        await model.SaveRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery());
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);
        Assert.Equal(30004079, row.SolarSystemId);

        Result<ActivityDetailDto> detail = await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId));
        Assert.True(detail.IsSuccess);
        Assert.Equal(30004079, detail.Value!.SolarSystemId);
    }

    // ── Reopening a window on an already-running own-toon group must not lose a sibling's ENEMIES (ET-259) ──

    /// <summary>
    /// Counter-proof, red against the pre-fix code: Jithran ran an own-toon-group mission (Angel Extravaganza,
    /// 2026-09-12, group HF-WR43, two of his own characters) that read "no enemies seen yet" LIVE for the whole
    /// 50 minutes he watched it, and saved with zero <c>RunEnemyObservation</c> rows despite 118 bounty payouts
    /// landing correctly on the same run. Measured cause: the acting character's own <c>OnRunStarted</c>, and a
    /// sibling just started this tick's own <c>OnCharacterRunStarted</c> (<c>_SendAdditionalStartRunCommandAsync</c>),
    /// both create that character's ENEMIES collector — but a window that instead ADOPTS an already-running group
    /// (RESUME after a crash, ET-254/258, or simply reopening a run window that was closed while the group kept
    /// going) only ever adopts the ONE run it is pointed at. A sibling <c>_RefreshParticipantsAsync</c> discovers
    /// afterwards never had <c>OnCharacterRunStarted</c> called for them at all, so every hit on their own gamelog
    /// had nowhere to go for the rest of that window's life — bounty and loot never depended on this per-window
    /// wiring and kept working, which is exactly why only ENEMIES went quiet.
    ///
    /// The combat lines below are copied byte for byte from Jithran's own gamelog
    /// (C:\Users\info\Documents\EVE\logs\Gamelogs\20260911_225312_90250177.txt, lines 1844-1846 — Angel
    /// Extravaganza), fed through the real <see cref="GamelogWatcherService"/>, not a cleaned-up fixture.
    /// </summary>
    [AvaloniaFact]
    public async Task ReopeningOnAnAlreadyRunningOwnToonGroup_StillCollectsTheSiblingsEnemies()
    {
        const int siblingId = 90250177;
        const string siblingName = "Jithran";

        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(
                inGame: true, ActivityWindowHarness.CharacterId, siblingId));
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
                .Add(24007, "Gistatis Tribunus", 900, 11)
                .Add(16561, "Angel Viper", 900, 11));
        });
        await harness.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character(siblingName, siblingId));

        // Jithran's own real gamelog file: created the evening before the mission and still growing when it
        // started — an already-tracked file (GameLogWatcher.Baseline), not the brand-new one every other test
        // in this suite writes after the watcher starts.
        string logPath = Path.Combine(harness.GamelogDirectory, "20260911_225312_90250177.txt");
        await File.WriteAllTextAsync(logPath,
            "------------------------------------------------------------\n"
            + $"  Gamelog\n  Listener: {siblingName}\n  Session Started: 2026.09.11 22:53:12\n"
            + "------------------------------------------------------------\n"
            + "[ 2026.09.11 22:53:15 ] (hint) Attempting to join a channel\n");
        var watcher = harness.Services.GetRequiredService<GamelogWatcherService>();
        await watcher.StartAsync();
        await Task.Delay(150);

        ActivityWindowViewModel first = await harness.OpenAsync(ActivityKind.Mission);
        first.UseCharacter(ActivityWindowHarness.CharacterId, ActivityWindowHarness.CharacterName);
        first.UseAdditionalCharacters([(siblingId, siblingName)]);
        await first.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => first.RunState == ActivityRunState.Running
            && first.Participants.Any(row => row.CharacterId == siblingId));
        Guid siblingRunId = first.Participants.Single(row => row.CharacterId == siblingId).RunId;
        first.Dispose();

        // A second window, opened fresh on the run the acting character (Abnoba, standing in for
        // ActivityWindowHarness's own pilot) already has going — exactly what RESUME, or simply reopening a
        // closed run window, both do.
        ActivityWindowViewModel second = await harness.OpenAsync(ActivityKind.Mission);
        second.UseCharacter(ActivityWindowHarness.CharacterId, ActivityWindowHarness.CharacterName);
        await second.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => second.RunState == ActivityRunState.Running
            && second.Participants.Any(row => row.CharacterId == siblingId));

        await File.AppendAllTextAsync(logPath,
            "[ 2026.09.12 08:20:41 ] (combat) <color=0xff00ffff><b>975</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Gistatis Tribunus</b><font size=10><color=0x77ffffff> - Mega Pulse Laser II - Hits\n"
            + "[ 2026.09.12 08:20:42 ] (combat) <color=0xffcc0000><b>39</b> <color=0x77ffffff><font size=10>from</font> <b><color=0xffffffff>Angel Viper</b><font size=10><color=0x77ffffff> - Nova Light Missile - Hits\n"
            + "[ 2026.09.12 08:20:45 ] (bounty) <font size=12><b><color=0xff00aa00>146,250 ISK</b><color=0x77ffffff> added to next bounty payout\n");
        await ActivityWindowHarness.WaitUntil(() =>
            second.Participants.SingleOrDefault(row => row.CharacterId == siblingId)?.BountyIsk > 0);

        second.StopRun(DateTime.UtcNow);
        await ActivityWindowHarness.WaitUntil(() => second.RunState == ActivityRunState.Stopped);
        await second.SaveRunCommand.ExecuteAsync(null);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        List<RunEnemyObservation> observations = await db.Set<RunEnemyObservation>()
            .Where(row => row.RunId == siblingRunId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, observations.Count);
        Assert.Contains(observations, o => o.EnemyName == "Gistatis Tribunus");
        Assert.Contains(observations, o => o.EnemyName == "Angel Viper");
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
