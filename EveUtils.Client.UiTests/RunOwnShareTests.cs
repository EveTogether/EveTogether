using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-296: every total on the runs overview is what this pilot's own characters earned, never the whole fleet's.
/// The worked example from the ticket is the fixture: a five-pilot homefront, three of them this machine's own.
///
/// The arithmetic is invisible — a group total and an own share look equally plausible on a row — so the numbers are
/// pinned rather than the shape. Counter-proof: on the code before this ticket the row and the day read 79.35M,
/// because <c>RebuildActivitySummariesCommandHandler</c> added up every run under the group code, fleet mates' runs
/// pulled in by server sync included.
/// </summary>
public sealed class RunOwnShareTests
{
    private const string GroupCode = "HF-TEST";

    /// <summary>A five-person homefront ("Raid"): in the site at completion, N = 5 and completed pays each character
    /// 15,000,000 from the table (<c>HomefrontPayoutTable</c>, 2026-03-19).</summary>
    private const int RaidDungeonId = 10347;

    /// <summary>Tritanium, priced at 1 ISK here so a loot quantity reads as its own ISK figure.</summary>
    private const int Tritanium = 34;

    private const string ServerAddress = "https://server.test";
    private const long A = 90000001, B = 90000002, C = 90000003, D = 90000004, E = 90000005;
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Acceptance 1: the whole worked example at once — the row, the day, the month and "ISK today" all read
    /// the own share, while the activity's own total stays the group's for the detail screen to show.</summary>
    [AvaloniaFact]
    public async Task AFleetOfFive_WithThreeOwnCharacters_CountsOnlyTheirShareInEveryTotal()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance);

        ActivityOverviewRowDto row = await _RowAsync(instance, A, B, C);

        Assert.Equal(79_350_000m, row.Isk.Total);
        Assert.Equal(47_700_000m, row.OwnIsk.Total);
        Assert.True(row.IsFlownByOwnCharacter);
        // The sources of the own share, not the group's: payout 45M, bounty 700k, loot 2M.
        Assert.Equal(45_000_000m, row.OwnIsk.Of(IskSource.HomefrontPayout)!.Amount);
        Assert.Equal(700_000m, row.OwnIsk.Of(IskSource.Bounty)!.Amount);
        Assert.Equal(2_000_000m, row.OwnIsk.Of(IskSource.Loot)!.Amount);

        ActivityOverviewRowViewModel screenRow = _RowViewModel(row);
        Assert.Equal(47_700_000m, screenRow.NetIsk);
        Assert.Equal("+47.7M ISK", screenRow.NetText);
        // The day header and the month bar are one formula over the same rows (ET-290), so they follow the row.
        Assert.Equal("+47.7M ISK net", RunsActivitySummaryText.NetFor([screenRow]));
        Assert.Equal(47_700_000m, RunsActivitySummaryText.SourcesFor([screenRow]).Total);

        EarningsPeriodFigures today = EarningsPeriods.For(EarningsPeriodKind.Today,
            [RunsActivityFacts.From(row, new RunRowFacts(null))], StartedAtUtc.ToLocalTime().AddHours(1), DayOfWeek.Monday, null);
        Assert.Equal(47_700_000m, today.Net);
    }

    /// <summary>Acceptance 1, the other half: FLEET on the detail screen shows every character, this machine's own
    /// and the group's alike, and those rows add up to the header's group total.</summary>
    [AvaloniaFact]
    public async Task TheDetailScreen_KeepsTheGroupTotal_AndSplitsItOverEveryCharacter()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance);
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        ActivityOverviewRowDto row = await _RowAsync(instance, A, B, C);
        ActivityDetailDto detail = _Value(await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId)));

        Assert.Equal(79_350_000m, detail.Isk.Total);
        IReadOnlyDictionary<long, IskBreakdown> byCharacter = Assert.IsAssignableFrom<IReadOnlyDictionary<long, IskBreakdown>>(detail.IskByCharacter);
        Assert.Equal(15_400_000m, byCharacter[A].Total);
        Assert.Equal(17_300_000m, byCharacter[B].Total);   // the 2M capture is on B's run, whole and unsplit
        Assert.Equal(15_000_000m, byCharacter[C].Total);
        Assert.Equal(16_500_000m, byCharacter[D].Total);
        Assert.Equal(15_150_000m, byCharacter[E].Total);
        Assert.Equal(detail.Isk.Total, byCharacter.Values.Sum(character => character.Total));
    }

    /// <summary>Acceptance 2: with nobody else's runs in the store, the own share is the group total and nothing on
    /// screen changes from what it read before this ticket.</summary>
    [AvaloniaFact]
    public async Task WithoutTheGroupMatesRuns_TheOwnShareIsTheWholeTotal()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance, withGroupMates: false);

        ActivityOverviewRowDto row = await _RowAsync(instance, A, B, C);

        Assert.Equal(47_700_000m, row.Isk.Total);
        Assert.Equal(47_700_000m, row.OwnIsk.Total);
    }

    /// <summary>Acceptance 3: loot counts whole, at the character whose run carries the capture — never divided over
    /// the eligible characters, never the group's whole loot. Taking the own characters out of the split changes
    /// nothing, and moving the capture from B's run to A's moves the ISK without changing the own share.</summary>
    [AvaloniaFact]
    public async Task Loot_CountsWholeAtTheRunItWasPastedOn_AndTheSplitDoesNotTouchIt()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance);
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        ActivityOverviewRowDto row = await _RowAsync(instance, A, B, C);
        Assert.Equal(2_000_000m, row.OwnIsk.Of(IskSource.Loot)!.Amount);   // not 1.8M (3M × 3/5), not 3M

        // The expected payout goes on dividing the group's loot over its eligible characters — that is the split
        // FLEET shows, and it has no say in the own share.
        ActivityDetailDto detail = _Value(await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId)));
        Assert.Equal(600_000m, detail.ExpectedPayoutIsk);

        await using (ClientDbContext db = await _DbAsync(instance))
            await db.Set<Run>().Where(run => run.GroupCode == GroupCode && (run.CharacterId == A || run.CharacterId == B))
                .ExecuteUpdateAsync(properties => properties.SetProperty(run => run.IsPayoutEligible, false));
        await dispatcher.Send(new RebuildActivitySummariesCommand());

        Assert.Equal(47_700_000m, (await _RowAsync(instance, A, B, C)).OwnIsk.Total);
    }

    /// <summary>Acceptance 3, last clause: the capture moved from B's run to A's. The two characters swap 2M, and
    /// the own share is the same 47,700,000.</summary>
    [AvaloniaFact]
    public async Task TheCaptureMovedToAnotherOwnRun_MovesTheIsk_AndLeavesTheOwnShareStanding()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance, lootOn: A);
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        ActivityOverviewRowDto row = await _RowAsync(instance, A, B, C);
        ActivityDetailDto detail = _Value(await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId)));

        Assert.Equal(47_700_000m, row.OwnIsk.Total);
        Assert.Equal(17_400_000m, detail.IskByCharacter![A].Total);
        Assert.Equal(15_300_000m, detail.IskByCharacter[B].Total);
    }

    /// <summary>Acceptance 4: a mission reward copied onto two of this pilot's own runs (old data, ET-210) counts
    /// once for the activity, and once in the own share — at the character of the earliest run that carries it.
    /// Counter-proof: splitting per character without an owner would count it twice, 2,000,000 for one paid line.</summary>
    [AvaloniaFact]
    public async Task ARewardCopiedOntoTwoOwnRuns_CountsOnce_AtTheEarliestRunsCharacter()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _RegisterAsync(instance, A, B);
        RunParameterInput reward = new()
        {
            ParameterKey = RunParameterKey.Isk, TypedValue = "1,000,000", Amount = 1_000_000m,
            ObservedAtUtc = StartedAtUtc.AddMinutes(1)
        };
        await _SaveOwnRunAsync(dispatcher, A, StartedAtUtc, parameters: [reward]);
        await _SaveOwnRunAsync(dispatcher, B, StartedAtUtc.AddMinutes(2), parameters: [reward]);
        await dispatcher.Send(new RebuildActivitySummariesCommand());

        ActivityOverviewRowDto row = await _RowAsync(instance, A, B);
        ActivityDetailDto detail = _Value(await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId)));

        Assert.Equal(1_000_000m, row.Isk.Total);
        Assert.Equal(1_000_000m, row.OwnIsk.Total);
        Assert.Equal(1_000_000m, detail.IskByCharacter![A].Total);
        Assert.Equal(0m, detail.IskByCharacter[B].Total);
    }

    /// <summary>Acceptance 5, the invariant the whole ticket rests on: per source, the split adds up to the
    /// activity's own breakdown. Checked over the worked example, whose six sources include a deduplicated reward
    /// and a payout counted once per character.</summary>
    [AvaloniaFact]
    public async Task PerSource_TheSplitAddsUpToTheActivitysOwnBreakdown()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance);

        ActivityOverviewRowDto row = await _RowAsync(instance, A, B, C);
        ActivityDetailDto detail = _Value(await instance.Services.GetRequiredService<IDispatcher>()
            .Query(new GetActivityDetailQuery(row.ActivitySummaryId)));

        IskBreakdown summed = IskBreakdown.Sum(detail.IskByCharacter!.Values);
        foreach (IskContribution source in detail.Isk.Contributions)
            Assert.Equal(source.Amount, summed.Of(source.Source)?.Amount ?? 0m);
        Assert.Equal(detail.Isk.Total, summed.Total);
    }

    /// <summary>Acceptance 6: a character taken out of the registry stops counting, without a rebuild — which is why
    /// the split is stored per character rather than as one "own ISK" figure. Putting them back restores it.</summary>
    [AvaloniaFact]
    public async Task ACharacterOutOfTheRegistry_StopsCounting_WithoutARebuild()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance);

        Assert.Equal(32_700_000m, (await _RowAsync(instance, A, B)).OwnIsk.Total);   // C's 15M gone, nothing rebuilt
        Assert.Equal(47_700_000m, (await _RowAsync(instance, A, B, C)).OwnIsk.Total);
    }

    /// <summary>Acceptance 4 of the ticket's change list: a row none of this pilot's characters flew says so, with a
    /// dash rather than a zero and the reason on hover — and counts for nothing in the totals around it.</summary>
    [AvaloniaFact]
    public async Task ARowNoneOfHisCharactersFlew_ReadsADash_AndCountsForNothing()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _FlyTheExampleAsync(instance);

        ActivityOverviewRowDto row = await _RowAsync(instance, D);   // only the group mate is "own" here
        ActivityOverviewRowDto foreign = row with { OwnIsk = IskBreakdown.None, IsFlownByOwnCharacter = false };
        ActivityOverviewRowViewModel screenRow = _RowViewModel(foreign);

        Assert.False(screenRow.HasNet);
        Assert.Equal("—", screenRow.NoNetText);
        Assert.Equal("none of your characters flew this — the group total is in the detail", screenRow.NoNetTooltip);
        Assert.Equal("nothing recorded to value", RunsActivitySummaryText.NetFor([screenRow]));
    }

    /// <summary>Three silences on the ISK column, told apart. Measured on Jithran's own store (11 Sep 2026): five
    /// abyssal duos whose loot his mate pasted read "nothing valued" — which says nobody could price it, where the
    /// truth is that it was priced at 47M and the money is somebody else's. Counter-proof: one rule for all three
    /// makes any two of these assertions collide.</summary>
    [AvaloniaFact]
    public async Task TheThreeSilencesOnTheIskColumn_SayDifferentThings()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _RegisterAsync(instance, A);
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = Tritanium, AveragePrice = 1, AdjustedPrice = 1, UpdatedAt = DateTimeOffset.UtcNow }]);

        // 1. He flew it and pasted nothing; his mate pasted all of it.
        await _SaveOwnRunAsync(dispatcher, A, StartedAtUtc, groupCode: "AB-DUO");
        await instance.Services.GetRequiredService<RunSynchronizationApplier>().ApplyAsync(ServerAddress,
            [_MatesRun(D, bounty: 0m, loot: 2_000_000, groupCode: "AB-DUO", name: "RaymondKrah")], new HashSet<Guid>());
        // 2. He flew it alone and there was nothing on it to value.
        await _SaveOwnRunAsync(dispatcher, A, StartedAtUtc.AddHours(1), groupCode: "AB-EMPTY");
        // 3. A group he has no run in at all, the way a server tab holds one.
        await instance.Services.GetRequiredService<RunSynchronizationApplier>().ApplyAsync(ServerAddress,
            [_MatesRun(E, bounty: 750_000m, groupCode: "AB-THEIRS", name: "Kav Orn")], new HashSet<Guid>());
        await dispatcher.Send(new RebuildActivitySummariesCommand());

        Dictionary<string, ActivityOverviewRowViewModel> rows = (await _RowsAsync(instance, A))
            .ToDictionary(row => row.GroupCode!, row => _RowViewModel(row));

        ActivityOverviewRowViewModel onAMatesRun = rows["AB-DUO"];
        Assert.False(onAMatesRun.HasNet);
        Assert.True(onAMatesRun.IsRecordedOnFleetMate);
        Assert.Equal("on a fleet mate's run", onAMatesRun.NoNetText);
        // 15M payout for the one character ticked into the site, plus the 2M he pasted — all of it his, none of it mine.
        Assert.Equal("17M ISK recorded on RaymondKrah's run", onAMatesRun.NoNetTooltip);

        ActivityOverviewRowViewModel nothingThere = rows["AB-EMPTY"];
        Assert.False(nothingThere.IsRecordedOnFleetMate);
        Assert.Equal("nothing valued", nothingThere.NoNetText);
        Assert.Null(nothingThere.NoNetTooltip);

        ActivityOverviewRowViewModel notHis = rows["AB-THEIRS"];
        Assert.False(notHis.IsFlownByOwnCharacter);
        Assert.False(notHis.IsRecordedOnFleetMate);
        Assert.Equal("—", notHis.NoNetText);
        Assert.Equal("none of your characters flew this — the group total is in the detail", notHis.NoNetTooltip);

        // None of the three adds anything to the day around them.
        Assert.Equal("nothing recorded to value", RunsActivitySummaryText.NetFor([.. rows.Values]));
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ticket's worked example. A, B and C are this machine's own; D and E come in over server sync, as
    /// a fleet mate's runs really do. Payout 15,000,000 each, bounty 400k/300k/0/500k/150k, and two loot captures —
    /// 2,000,000 on <paramref name="lootOn"/>'s run and 1,000,000 on D's.</summary>
    private static async Task _FlyTheExampleAsync(TestClientInstance instance, bool withGroupMates = true, long lootOn = B)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _RegisterAsync(instance, A, B, C);
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = Tritanium, AveragePrice = 1, AdjustedPrice = 1, UpdatedAt = DateTimeOffset.UtcNow }]);

        foreach ((long characterId, decimal bounty) in new[] { (A, 400_000m), (B, 300_000m), (C, 0m) })
            await _SaveOwnRunAsync(dispatcher, characterId, StartedAtUtc, bounty,
                loot: characterId == lootOn ? 2_000_000 : null);

        // What a five-person homefront's runs carry once the site reads Completed — set on the rows rather than
        // driven through the run window, which is a screen this test has no need of.
        await using (ClientDbContext db = await _DbAsync(instance))
            await db.Set<Run>().Where(run => run.GroupCode == GroupCode)
                .ExecuteUpdateAsync(properties => properties
                    .SetProperty(run => run.InSiteAtCompletion, true)
                    .SetProperty(run => run.AttendanceCount, 5)
                    .SetProperty(run => run.HomefrontOutcome, HomefrontOutcome.Completed));

        if (withGroupMates)
            await instance.Services.GetRequiredService<RunSynchronizationApplier>().ApplyAsync(ServerAddress,
                [_MatesRun(D, 500_000m, loot: 1_000_000), _MatesRun(E, 150_000m)], new HashSet<Guid>());

        await dispatcher.Send(new RebuildActivitySummariesCommand());
    }

    private static async Task _SaveOwnRunAsync(IDispatcher dispatcher, long characterId, DateTime startedAtUtc,
        decimal bounty = 0m, long? loot = null, IReadOnlyList<RunParameterInput>? parameters = null,
        string? groupCode = null)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAtUtc,
            RaidDungeonId, "Raid: Hall of Sacrifice", 30000142, groupCode ?? GroupCode));
        await dispatcher.Send(new SaveRunCommand(_Value(started), startedAtUtc.AddMinutes(20), startedAtUtc.AddMinutes(21),
            loot is { } quantity ? [_Capture(quantity)] : [],
            bounty > 0m ? [new RunBountyEntryInput { OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = bounty }] : [],
            [], parameters ?? [], RebuildSummaries: false));
    }

    /// <summary>A group mate's run as server sync hands it over: their own character id, their own bounty and loot,
    /// and the same homefront facts every run of the site carries.</summary>
    private static RunWirePayload _MatesRun(long characterId, decimal bounty, long? loot = null,
        string? groupCode = null, string? name = null)
    {
        Run run = new()
        {
            Id = Guid.CreateVersion7(), CharacterId = characterId, GroupCode = groupCode ?? GroupCode, ActivityKind = ActivityKind.Site,
            State = RunState.Saved, StartedAtUtc = StartedAtUtc, StoppedAtUtc = StartedAtUtc.AddMinutes(20),
            SavedAtUtc = StartedAtUtc.AddMinutes(21), SiteTypeId = RaidDungeonId, SiteName = "Raid: Hall of Sacrifice",
            SolarSystemId = 30000142, InSiteAtCompletion = true, AttendanceCount = 5, CharacterNameSnapshot = name,
            HomefrontOutcome = HomefrontOutcome.Completed, IsPayoutEligible = true, Revision = 1
        };
        run.BountyEntries.Add(new RunBountyEntry
        {
            Id = Guid.CreateVersion7(), RunId = run.Id, OccurredAtUtc = StartedAtUtc.AddMinutes(5), Isk = bounty
        });
        if (loot is { } quantity)
        {
            RunLootCapture capture = new()
            {
                Id = Guid.CreateVersion7(), RunId = run.Id, CapturedAtUtc = StartedAtUtc.AddMinutes(19),
                Source = LootCaptureSource.Clipboard, Role = LootCaptureRole.Snapshot, ContentHash = $"MATE-{characterId}"
            };
            capture.Entries.Add(new RunLootEntry
            {
                Id = Guid.CreateVersion7(), RunLootCaptureId = capture.Id, ItemTypeId = Tritanium, Name = "Tritanium",
                Quantity = quantity, LootKind = LootKind.Gained
            });
            run.LootCaptures.Add(capture);
        }

        return new RunWirePayload
        {
            Run = RunWireData.FromEntity(run),
            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private static RunLootCaptureInput _Capture(long quantity) => new()
    {
        CapturedAtUtc = StartedAtUtc.AddMinutes(19), Source = LootCaptureSource.Clipboard, ContentHash = $"OWN-{quantity}",
        Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
    };

    private static async Task _RegisterAsync(TestClientInstance instance, params long[] characterIds)
    {
        ICharacterRegistry registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (long characterId in characterIds)
            await registry.AddOrUpdateAsync(new Character($"Pilot {characterId}", (int)characterId));
    }

    private static async Task<ActivityOverviewRowDto> _RowAsync(TestClientInstance instance, params long[] ownCharacterIds) =>
        Assert.Single(await _RowsAsync(instance, ownCharacterIds));

    private static async Task<IReadOnlyList<ActivityOverviewRowDto>> _RowsAsync(
        TestClientInstance instance, params long[] ownCharacterIds) =>
        _Value(await instance.Services.GetRequiredService<IDispatcher>()
            .Query(new GetActivityOverviewQuery(OwnCharacterIds: ownCharacterIds)));

    private static ActivityOverviewRowViewModel _RowViewModel(ActivityOverviewRowDto row) =>
        new(row, id => $"character {id}", _ => Task.CompletedTask, _ => Task.CompletedTask);

    private static async Task<ClientDbContext> _DbAsync(TestClientInstance instance) =>
        await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();

    private static T _Value<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Messages[0].Text);
        return result.Value!;
    }
}
