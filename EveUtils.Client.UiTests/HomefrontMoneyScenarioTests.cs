using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Attendance;
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
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-271, hardened on Jithran's own request after HF-7TQB and HF-ESNB — two groups flown the same way, Completed
/// clicked in the run window, one saved at 1.69M and one at 76.69M, neither with an outcome on a single run: "het moet
/// niet zo zijn dat het 60% van de tijd goed gaat". Every scenario drives the app the way Jithran does — the run window
/// with its own HOMEFRONT section, STOP, SAVE, the detail screen — and ends on the same check: the stored runs, the
/// stored summary (overview row and month bar), a fresh rebuild of that summary, the detail screen and the run window
/// all tell one story (the invariants I1–I6 on <see cref="SetRunAttendanceCommand"/>).
/// </summary>
public sealed class HomefrontMoneyScenarioTests
{
    private const decimal FivePilots = 15_000_000m;
    private const decimal ThreePilots = 9_600_000m;

    private static readonly SdeSite Raid = new(10347, "Raid: Hall of Sacrifice", 70, "Homefront Operations",
        null, null, null, null, false, []);

    // ── Outcome ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>S01, ET-271 AC-1: copy, fly, STOP — not one click on HOMEFRONT. Completed, 5 in site, 15,000,000 each.</summary>
    [AvaloniaFact]
    public async Task S01_TheDefaultCompleted_NeverTouched_IsStoredAndPaid()
    {
        using Group group = await Group.StartAsync(toons: 5);

        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);
        Assert.StartsWith("Completed · 5 in site · 15,000,000 each · 75,000,000 ISK total", group.Section.PayoutSummaryText);
    }

    /// <summary>S02: Failed and back to Completed during the run, then STOP.</summary>
    [AvaloniaFact]
    public async Task S02_FailedThenCompleted_DuringTheRun_IsStoredAsCompleted()
    {
        using Group group = await Group.StartAsync(toons: 5);

        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Failed);
        await group.SettleAsync();
        await group.AssertRunsCarryAsync(HomefrontOutcome.Failed);
        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Completed);
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);
    }

    /// <summary>S03, AC-2: Failed takes the payout away with one click — and back and forth on the saved detail.</summary>
    [AvaloniaFact]
    public async Task S03_FailedAndBack_OnTheSavedDetail_MovesEveryTotalAtOnce()
    {
        using Group group = await Group.StartAsync(toons: 5);
        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Failed);
        await group.StopAndSaveAsync();
        await group.AssertOneStoryAsync(HomefrontOutcome.Failed, n: 5, payout: 0m);

        await (await group.DetailAsync()).Homefront().SetOutcomeCommand.ExecuteAsync(HomefrontOutcome.Completed);
        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);

        await (await group.DetailAsync()).Homefront().SetOutcomeCommand.ExecuteAsync(HomefrontOutcome.Failed);
        await group.AssertOneStoryAsync(HomefrontOutcome.Failed, n: 5, payout: 0m);
    }

    /// <summary>S04, the 3-second race of HF-7TQB/HF-ESNB: a pick made right after STOP, SAVE a moment later — no tick,
    /// no bundle window in between. SAVE writes it before a single row is committed or added up.</summary>
    [AvaloniaFact]
    public async Task S04_APickRightBeforeSave_IsStoredBeforeTheSummaryIsBuilt()
    {
        using Group group = await Group.StartAsync(toons: 5);
        group.Window.StopRun(group.Clock);

        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Unknown);
        await group.Window.SaveRunCommand.ExecuteAsync(null);
        await group.SettleAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Unknown, n: 5, payout: 0m);
    }

    /// <summary>S05: a pick made in the run window after SAVE is stored and adds the summary up again.</summary>
    [AvaloniaFact]
    public async Task S05_APickInTheRunWindowAfterSave_IsStoredAndAddedUpAgain()
    {
        using Group group = await Group.StartAsync(toons: 5);
        await group.StopAndSaveAsync();
        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);

        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Failed);
        await group.SettleAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Failed, n: 5, payout: 0m);
    }

    /// <summary>S06: clicked while the column shows a sibling rather than the pilot — the group's decision, on every run.</summary>
    [AvaloniaFact]
    public async Task S06_ClickedOnASiblingsColumn_LandsOnEveryRunOfTheGroup()
    {
        using Group group = await Group.StartAsync(toons: 5);
        RunCharacterRowViewModel sibling = group.Window.RunCharacters.First(row => row.CharacterId != ActivityWindowHarness.CharacterId && row.RunId is not null);
        group.Window.SelectRunCharacterCommand.Execute(sibling);
        await ActivityWindowHarness.WaitUntil(() => group.Window.RunId == sibling.RunId);
        await group.SettleAsync();

        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Failed);
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Failed, n: 5, payout: 0m);
    }

    /// <summary>S07, I4/I5: what arrives after the summary was built — a proposal worked out from an older list, and a list
    /// with no outcome at all (a stale window, an older client, a server copy older than the column) — changes nothing.
    /// Counter-proof: drop KeepingOutcomeOf and the late list erases Completed, the HF-ESNB state.</summary>
    [AvaloniaFact]
    public async Task S07_ALateListWithoutAnOutcome_NeverErasesTheOneStored()
    {
        using Group group = await Group.StartAsync(toons: 5);
        await group.StopAndSaveAsync();
        RunAttendanceDecision stored = await group.StoredDecisionAsync();

        Result<int> staleProposal = await group.Dispatcher.Send(new SetRunAttendanceCommand(
            stored with { Outcome = null, SetAtUtc = stored.SetAtUtc.AddSeconds(3) }, group.Own, group.GroupCode,
            IsProposal: true, StandingSetAtUtc: stored.SetAtUtc.AddSeconds(-10)));
        Result<int> withoutOutcome = await group.Dispatcher.Send(new SetRunAttendanceCommand(
            stored with { Outcome = null, SetAtUtc = stored.SetAtUtc.AddSeconds(3) }, group.Own, group.GroupCode));

        Assert.Equal(0, staleProposal.Value);
        Assert.True(withoutOutcome.IsSuccess);
        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);
    }

    // ── Who was in the site ─────────────────────────────────────────────────────────────────────────

    /// <summary>S08, AC-2: one click takes a toon out and N and the total follow; one more click puts it back.</summary>
    [AvaloniaFact]
    public async Task S08_AToonUntickedThenReticked_MovesNAndTheTotalBothWays()
    {
        using Group group = await Group.StartAsync(toons: 5);
        AttendanceRowViewModel hauler = group.Section.Rows.Last(row => row.IsLocal);

        hauler.IsInSite = false;
        await group.SettleAsync();
        await group.AssertRunsCarryAsync(HomefrontOutcome.Completed, n: 4);
        Assert.Contains("12,000,000 each · 48,000,000 ISK total", group.Section.PayoutSummaryText);

        hauler.IsInSite = true;
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);
    }

    /// <summary>S09, AC-4: pilots in the site who are not this client's own count for N, never for its total — two own
    /// toons and three others: N = 5, 2 × 15,000,000.</summary>
    [AvaloniaFact]
    public async Task S09_OthersInTheSite_CountForN_AndNeverForTheTotal()
    {
        using Group group = await Group.StartAsync(toons: 2);
        for (int other = 0; other < 3; other++)
            group.Section.IncreaseNotOnRosterCommand.Execute(null);
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 2 * FivePilots);
        Assert.EndsWith("15,000,000 each · 30,000,000 ISK for 2 Local", group.Section.PayoutSummaryText);
    }

    /// <summary>S10: a sixth own character in the group taken out of the site — five paid at N = 5.</summary>
    [AvaloniaFact]
    public async Task S10_ASixthCharacterNotInTheSite_IsNotPaid_AndNIsFive()
    {
        using Group group = await Group.StartAsync(toons: 6);
        group.Section.Rows.Last(row => row.IsLocal).IsInSite = false;
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);
    }

    /// <summary>S11, HF-V7MB: a sibling put in the site after the fact gets a run of its own with the bounty its own game
    /// log shows for the run's time — once, however often the list is written again, and the startup pass does not read
    /// it twice.</summary>
    [AvaloniaFact]
    public async Task S11_ASiblingBackfilledAfterTheFact_TakesItsBountyFromItsGameLog_Once()
    {
        using Group group = await Group.StartAsync(toons: 2);
        const int late = 90000009;
        await group.RegisterAsync(late, "Late Toon");
        await group.StopAndSaveAsync();
        DateTime startedAtUtc = (await group.RunsAsync()).Min(run => run.StartedAtUtc);
        await group.WriteGameLogAsync("Late Toon", late, startedAtUtc.AddSeconds(1), 350_000);
        RunAttendanceDecision stored = await group.StoredDecisionAsync();
        RunAttendanceDecision withLate = stored with
        {
            Entries = [.. stored.Entries, new RunAttendanceEntryInput { CharacterId = late, CharacterName = "Late Toon", IsInSite = true, Reason = AttendanceReason.SetByHand }],
            SetAtUtc = stored.SetAtUtc.AddMinutes(1)
        };

        await group.Dispatcher.Send(new SetRunAttendanceCommand(withLate, [.. group.Own, late], group.GroupCode));
        await group.Dispatcher.Send(new SetRunAttendanceCommand(withLate with { SetAtUtc = withLate.SetAtUtc.AddSeconds(1) },
            [.. group.Own, late], group.GroupCode));
        await group.Dispatcher.Send(new ImportMissingGroupBountyCommand());

        List<Run> runs = await group.RunsAsync(includeBounty: true);
        Run backfilled = runs.Single(run => run.CharacterId == late);
        Assert.Equal(350_000m, Assert.Single(backfilled.BountyEntries).Isk);
        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 3, payout: 3 * ThreePilots, bounty: 350_000m,
            isWindowCompared: false);
    }

    // ── Windows, restarts, repeats ──────────────────────────────────────────────────────────────────

    /// <summary>S12: the window closed mid-run and another adopting the running group — the pick survives, and the new
    /// window does not put its own default over it.</summary>
    [AvaloniaFact]
    public async Task S12_AWindowReopenedMidRun_KeepsThePickAndDoesNotDefaultOverIt()
    {
        using Group group = await Group.StartAsync(toons: 3);
        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Unknown);
        await group.SettleAsync();

        await group.ReopenWindowAsync();
        Assert.Equal(HomefrontOutcome.Unknown, group.Section.Outcome);
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Unknown, n: 3, payout: 0m);
    }

    /// <summary>S13, I6: Completed twice, SAVE twice — the payout counts once.</summary>
    [AvaloniaFact]
    public async Task S13_CompletedTwice_SaveTwice_NeverDoublesThePayout()
    {
        using Group group = await Group.StartAsync(toons: 5);
        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Completed);
        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Completed);
        await group.StopAndSaveAsync();
        await group.Window.SaveRunCommand.ExecuteAsync(null);
        await (await group.DetailAsync()).Homefront().SetOutcomeCommand.ExecuteAsync(HomefrontOutcome.Completed);

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);
    }

    /// <summary>S14, I4: a summary left stale by an older build (HF-HP97) is added up again at the next start from what
    /// the runs hold — and the start's pass can only ever find the same figure, never lose one.</summary>
    [AvaloniaFact]
    public async Task S14_AStaleSummary_IsHealedAtStartup_FromTheStoredRuns()
    {
        using Group group = await Group.StartAsync(toons: 5);
        await group.StopAndSaveAsync();
        await using (ClientDbContext db = await group.DbAsync())
            await db.Set<ActivitySummary>().Where(summary => summary.GroupCode == group.GroupCode)
                .ExecuteUpdateAsync(properties => properties
                    .SetProperty(summary => summary.TotalIsk, 1_687_500m)
                    .SetProperty(summary => summary.IskContributions, (string?)null)
                    .SetProperty(summary => summary.IskSources, "Bounty,Loot,Rewards,Consumables,Mining,HomefrontPayout"));

        await group.Dispatcher.Send(new RebuildActivitySummariesCommand(OnlyWhenOutdated: true));

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots);
    }

    /// <summary>S15: N beyond the payout table (5 own toons and 6 others) pays nothing, and every screen says the same.</summary>
    [AvaloniaFact]
    public async Task S15_NBeyondThePayoutTable_PaysNothing_Everywhere()
    {
        using Group group = await Group.StartAsync(toons: 5);
        for (int other = 0; other < 6; other++)
            group.Section.IncreaseNotOnRosterCommand.Execute(null);
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 11, payout: 0m);
        Assert.Contains("beyond the payout table", group.Section.PayoutSummaryText);
    }

    /// <summary>S16, AC-3: a typed amount replaces one toon's table figure everywhere, only while the site pays at all.</summary>
    [AvaloniaFact]
    public async Task S16_ATypedAmount_CountsInPlaceOfTheTables_AndFailedStillPaysNothing()
    {
        using Group group = await Group.StartAsync(toons: 5);
        AttendanceRowViewModel row = group.Section.Rows.First(candidate => candidate.IsLocal);
        row.BeginPayoutEditCommand.Execute(null);
        Assert.Equal("15,000,000", row.PayoutEditText);
        row.PayoutEditText = "14,000,000";
        row.CommitPayoutEditCommand.Execute(null);
        await group.StopAndSaveAsync();
        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 4 * FivePilots + 14_000_000m);

        await (await group.DetailAsync()).Homefront().SetOutcomeCommand.ExecuteAsync(HomefrontOutcome.Failed);
        await group.AssertOneStoryAsync(HomefrontOutcome.Failed, n: 5, payout: 0m);
    }

    /// <summary>S17, the HF-7TQB suspect: the fleet read blinks (no commander named for a moment) while the pilot picks
    /// — the pick is still this client's to write, and is written.</summary>
    [AvaloniaFact]
    public async Task S17_APickWhileTheFleetReadBlinks_IsStillWritten()
    {
        const long fleetId = 4711;
        using Group group = await Group.StartAsync(toons: 3, fleetId: fleetId);
        IFleetParticipation participation = group.Services.GetRequiredService<IFleetParticipation>();
        IReadOnlyList<FleetParticipant> fleet = participation.Current;

        participation.Set([]);
        await group.SettleAsync();
        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Failed);
        await group.SettleAsync();
        participation.Set(fleet);
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Failed, n: 3, payout: 0m);
    }

    /// <summary>S18: a homefront saved before the outcome had a default keeps "not decided" — nothing fills it in, not the
    /// detail screen, not the startup pass — until one click on Completed, which then moves every total.</summary>
    [AvaloniaFact]
    public async Task S18_AnOldHomefrontWithoutAnOutcome_IsLeftAlone_UntilOneClick()
    {
        using Group group = await Group.StartAsync(toons: 5);
        await group.StopAndSaveAsync();
        await using (ClientDbContext db = await group.DbAsync())
            await db.Set<Run>().Where(run => run.GroupCode == group.GroupCode)
                .ExecuteUpdateAsync(properties => properties.SetProperty(run => run.HomefrontOutcome, (HomefrontOutcome?)null));
        await group.Dispatcher.Send(new RebuildActivitySummariesCommand());
        await group.Dispatcher.Send(new RebuildActivitySummariesCommand(OnlyWhenOutdated: true));
        await group.AssertOneStoryAsync(null, n: 5, payout: 0m, isWindowCompared: false);
        Assert.Equal("not decided", (await group.DetailAsync()).Homefront().OutcomeText);

        await (await group.DetailAsync()).Homefront().SetOutcomeCommand.ExecuteAsync(HomefrontOutcome.Completed);

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 5, payout: 5 * FivePilots, isWindowCompared: false);
    }

    /// <summary>S19: two windows deciding the same group (a run window reopened beside the first) converge on one list —
    /// a click in either is the one every run carries, and neither's own view is written back over it.</summary>
    [AvaloniaFact]
    public async Task S19_TwoWindowsOnOneGroup_ConvergeOnTheLastClick()
    {
        using Group group = await Group.StartAsync(toons: 3);
        using HomefrontWindowSectionViewModel second = new(group.Window);
        await group.SettleAsync(ticks: 3, also: second);

        second.SetOutcomeCommand.Execute(HomefrontOutcome.Failed);
        await group.SettleAsync(ticks: 8, also: second);
        await group.AssertRunsCarryAsync(HomefrontOutcome.Failed, n: 3);
        Assert.Equal(HomefrontOutcome.Failed, group.Section.Outcome);

        group.Section.SetOutcomeCommand.Execute(HomefrontOutcome.Completed);
        await group.SettleAsync(ticks: 8, also: second);
        await group.StopAndSaveAsync();

        await group.AssertOneStoryAsync(HomefrontOutcome.Completed, n: 3, payout: 3 * ThreePilots);
    }

    // ── The group ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One pilot's machine flying a Raid: Hall of Sacrifice on <c>toons</c> own characters, every one ticked
    /// at START (ET-270), driven one clock second at a time the way the window's own timer drives it.</summary>
    private sealed class Group : IDisposable
    {
        private readonly ActivityWindowHarness _harness;
        private readonly List<long> _own;

        private Group(ActivityWindowHarness harness, ActivityWindowViewModel window, long[] own)
        {
            _harness = harness;
            Window = window;
            _own = [.. own];
        }

        public ActivityWindowViewModel Window { get; private set; }

        /// <summary>This client's own characters — one registered mid-scenario included.</summary>
        public long[] Own => [.. _own];

        public DateTime Clock { get; private set; } = DateTime.UtcNow;

        public IServiceProvider Services => _harness.Services;

        public IDispatcher Dispatcher => Services.GetRequiredService<IDispatcher>();

        public string GroupCode => Window.GroupCode ?? throw new InvalidOperationException("the run has no group code");

        public HomefrontWindowSectionViewModel Section => Window.Homefront();

        public static async Task<Group> StartAsync(int toons, long? fleetId = null)
        {
            int[] ids = [.. Enumerable.Range(0, toons).Select(index => ActivityWindowHarness.CharacterId + index)];
            ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync(configure: services =>
                services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(true, ids)));
            foreach (int id in ids.Skip(1))
                await harness.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character($"Toon {id}", id));
            if (fleetId is { } fleet)
                harness.Services.GetRequiredService<IFleetParticipation>().Set(
                    [.. ids.Select(id => new FleetParticipant(id, fleet, ClientOnly: true, ActivityWindowHarness.CharacterId))]);

            ActivityWindowViewModel window = await harness.OpenAsync();
            await window.ApplySignatureAsync("AAA-001", "Combat Site", "Raid: Hall of Sacrifice", [Raid]);
            harness.Dialogs.OnPickCharacters = (_, options) =>
                Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
            await window.StartRunCommand.ExecuteAsync(null);
            await ActivityWindowHarness.WaitUntil(() => window.Participants.Count == toons, timeoutMs: 10_000);

            Group group = new(harness, window, [.. ids.Select(id => (long)id)]);
            await group.SettleAsync();
            await group.TickUntilAsync(() => group.Section.CanDecide && group.Section.Rows.Count(row => row.IsLocal) == toons);
            return group;
        }

        public async Task RegisterAsync(int characterId, string name)
        {
            await Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));
            _own.Add(characterId);
        }

        /// <summary>A few clock seconds, with the jobs they post run and the writes they start given time to land.</summary>
        public async Task SettleAsync(int ticks = 3, HomefrontWindowSectionViewModel? also = null)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                Clock = Clock.AddSeconds(1);
                Window.Refresh(Clock);
                also?.Refresh(Clock);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(30);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }

        public async Task TickUntilAsync(Func<bool> until)
        {
            for (int tick = 0; tick < 60 && !until(); tick++)
                await SettleAsync(ticks: 1);
            Assert.True(until(), "the run window never settled");
        }

        public async Task StopAndSaveAsync()
        {
            Window.StopRun(Clock);
            await Window.SaveRunCommand.ExecuteAsync(null);
            await SettleAsync();
        }

        /// <summary>The window closed and the pilot's own run opened again the way the runs screen and a restart open
        /// one (ET-254): named, and adopted by its id.</summary>
        public async Task ReopenWindowAsync()
        {
            Guid pilotRun = Window.Participants.Single(participant => participant.CharacterId == ActivityWindowHarness.CharacterId).RunId;
            Window.Dispose();
            Window = new ActivityWindowViewModel(ActivityKind.Site, Services);
            Window.UseCharacter(ActivityWindowHarness.CharacterId, ActivityWindowHarness.CharacterName);
            Window.ResumeRun(pilotRun);
            await Window.LoadAsync();
            await ActivityWindowHarness.WaitUntil(() => Window.RunId is not null && Window.Participants.Count == Own.Length,
                timeoutMs: 10_000);
            await TickUntilAsync(() => Window.Sections.OfType<HomefrontWindowSectionViewModel>().Any() && Section.CanDecide
                                       && Section.Rows.Count(row => row.IsLocal) == Own.Length);
            await SettleAsync();
        }

        /// <summary>A character's own gamelog with one bounty line, as EVE writes it.</summary>
        public async Task WriteGameLogAsync(string name, int characterId, DateTime atUtc, long bounty)
        {
            string path = Path.Combine(_harness.GamelogDirectory, $"20300102_120000_{characterId}.txt");
            string started = atUtc.AddMinutes(-5).ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
            string at = atUtc.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(path,
                "------------------------------------------------------------\n"
                + $"  Gamelog\n  Listener: {name}\n  Session Started: {started}\n"
                + "------------------------------------------------------------\n"
                + $"[ {at} ] (bounty) <font size=12><b><color=0xff00aa00>{bounty.ToString("N0", CultureInfo.InvariantCulture)} ISK</b>"
                + "<color=0x77ffffff> added to next bounty payout\n");
        }

        public async Task<ClientDbContext> DbAsync() =>
            await Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();

        public async Task<List<Run>> RunsAsync(bool includeBounty = false)
        {
            await using ClientDbContext db = await DbAsync();
            IQueryable<Run> runs = db.Set<Run>().AsNoTracking().Where(run => run.GroupCode == GroupCode && run.DeletedAtUtc == null);
            if (includeBounty)
                runs = runs.Include(run => run.BountyEntries);
            return await runs.ToListAsync();
        }

        public async Task<RunAttendanceDecision> StoredDecisionAsync() =>
            (await Dispatcher.Query(new GetRunAttendanceQuery(GroupCode, Window.RunId ?? Guid.Empty))).Value
            ?? throw new InvalidOperationException("no list was stored");

        public async Task<ActivityOverviewRowDto> RowAsync() =>
            (await Dispatcher.Query(new GetActivityOverviewQuery())).Value?.Single(row => row.GroupCode == GroupCode)
            ?? throw new InvalidOperationException("no overview row");

        public async Task<ActivityDetailViewModel> DetailAsync()
        {
            ActivityDetailViewModel detail = new(Dispatcher, (await RowAsync()).ActivitySummaryId,
                ownCharacterIds: new HashSet<long>(Own), services: Services);
            await detail.LoadAsync();
            return detail;
        }

        public async Task AssertRunsCarryAsync(HomefrontOutcome? outcome, int? n = null)
        {
            List<Run> runs = [];
            for (int attempt = 0; attempt < 50; attempt++)
            {
                runs = await RunsAsync();
                if (runs.All(run => run.HomefrontOutcome == outcome && (n is null || run.AttendanceCount == n)))
                    break;
                await SettleAsync(ticks: 1);
            }

            Assert.All(runs, run => Assert.Equal(outcome, run.HomefrontOutcome));
            if (n is { } count)
                Assert.All(runs, run => Assert.Equal(count, run.AttendanceCount));
        }

        /// <summary>The one check every scenario ends on: the stored runs, the stored summary (overview row and the
        /// month bar's own column), a fresh rebuild of it, the detail screen — header, HOMEFRONT and FLEET — and the run
        /// window's own TOTAL ISK all tell the same story.</summary>
        public async Task AssertOneStoryAsync(HomefrontOutcome? outcome, int n, decimal payout, decimal bounty = 0m,
            bool isWindowCompared = true)
        {
            await AssertRunsCarryAsync(outcome, n);

            ActivityOverviewRowDto row = await RowAsync();
            Assert.Equal(payout, row.Isk.Of(IskSource.HomefrontPayout)?.Amount ?? 0m);
            Assert.Equal(payout + bounty, row.Isk.Total);
            // The month bar's own column: the same figure, or no figure at all where nothing earned anything.
            await using (ClientDbContext db = await DbAsync())
                Assert.Equal(row.Isk.HasFigure ? row.Isk.Total : null, (await db.Set<ActivitySummary>().AsNoTracking()
                    .SingleAsync(summary => summary.GroupCode == GroupCode)).TotalIsk);

            await Dispatcher.Send(new RebuildActivitySummariesCommand());
            Assert.Equal(row.Isk, (await RowAsync()).Isk);

            string total = IskFormat.Whole(row.Isk.Total);
            ActivityDetailViewModel detail = await DetailAsync();
            Assert.Equal(total, detail.TotalIskText);
            Assert.Equal(total, detail.Fleet().TotalText);
            Assert.Equal(payout, detail.Homefront().Rows.Where(candidate => candidate.IsPayoutShown)
                .Sum(candidate => candidate.CorrectedPayoutIsk ?? candidate.ExpectedPayoutIsk ?? 0m));

            if (!isWindowCompared)
                return;
            // The window follows a change made elsewhere on its next ticks (it reads the store the tick after the change).
            await SettleAsync(ticks: 3);
            Assert.Equal(total, Window.GroupTotalIskText);
            Assert.Equal(total, Window.Fleet().TotalText);
        }

        public void Dispose()
        {
            Window.Dispose();
            _harness.Dispose();
        }
    }
}
