using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Attendance;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-230: who was in a homefront's site when it completed. The app proposes from evidence, one person decides — the
/// fleet commander, or the pilot without a fleet — and every client writes the commander's list onto its own runs only,
/// so N is the same on every member's machine. Jithran's own example throughout: he flies Jithran (damage/reps),
/// Abnoba Auscent (remote capacitor) and Noahmarr (the hauler outside the site); another member flies three toons in
/// the site. N = 5.
/// </summary>
public sealed class HomefrontAttendanceTests
{
    private const long FleetId = 7700;
    private const string ServerAddress = "fleet.example";
    private const string GroupCode = "HF-ATT1";
    private const int Jithran = 90000101;
    private const int Abnoba = 90000102;
    private const int Noahmarr = 90000103;
    private const int Ysolde = 90000201;
    private const int Brannoc = 90000202;
    private const int Tamsin = 90000203;
    private const int Corvin = 90000301;
    private static readonly DateTime StartedAtUtc = new(2026, 9, 12, 20, 54, 0, DateTimeKind.Utc);

    // ── The proposal ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Evidence proposes, by EVE's own thresholds; nothing to go on is unticked and says so — the hauler, and
    /// an external pilot from whom no evidence can arrive. Counter-proof: drop the 1,000 threshold and Noahmarr's single
    /// 999-damage volley ticks him.</summary>
    [Fact]
    public void TheProposal_TicksWhoInteractedWithTheSite_AndLeavesTheHaulerAndTheExternalUnticked()
    {
        AttendanceEvidenceCollector evidence = new();
        evidence.SetWindow(StartedAtUtc, null);
        evidence.Note(Jithran, SiteContribution.Damage, 1_200_000, StartedAtUtc.AddMinutes(3));
        evidence.Note(Abnoba, SiteContribution.RemoteCapacitor, 142_000, StartedAtUtc.AddMinutes(4));
        evidence.Note(Noahmarr, SiteContribution.Damage, 999, StartedAtUtc.AddMinutes(5));
        evidence.Note(Tamsin, SiteContribution.Damage, 50_000, StartedAtUtc.AddMinutes(-1));
        evidence.NoteFleetActivity(Ysolde, StartedAtUtc.AddMinutes(2));

        IReadOnlyList<AttendanceProposalLine> lines = AttendanceProposal.Propose(
            [_Local(Jithran), _Local(Abnoba), _Local(Noahmarr), _Other(Ysolde), _Other(Tamsin), _External(Corvin)],
            id => evidence.Best(id), lastSite: null);

        Assert.Equal(new AttendanceProposalLine(Jithran, true, AttendanceReason.DamageDealt, 1_200_000), _Line(lines, Jithran));
        Assert.Equal(new AttendanceProposalLine(Abnoba, true, AttendanceReason.RemoteCapacitor, 142_000), _Line(lines, Abnoba));
        Assert.Equal(new AttendanceProposalLine(Noahmarr, false, AttendanceReason.NoActivityLogged, null), _Line(lines, Noahmarr));
        Assert.Equal(new AttendanceProposalLine(Ysolde, true, AttendanceReason.FleetActivity, null), _Line(lines, Ysolde));
        // Before START is not this run.
        Assert.False(_Line(lines, Tamsin).IsInSite);
        Assert.False(_Line(lines, Corvin).IsInSite);
        Assert.Equal("no activity logged", AttendanceRowViewModel.Describe(AttendanceReason.NoActivityLogged, null, isLocal: true, isExternal: false));
        Assert.Equal("no evidence can arrive — tick if in site",
            AttendanceRowViewModel.Describe(AttendanceReason.NoActivityLogged, null, isLocal: false, isExternal: true));
    }

    /// <summary>A series keeps its list (Jithran, 2026-09-11): whoever the last site ended with starts ticked, evidence
    /// can add a tick and says so, and never takes one away; a character new to the fleet falls back to evidence.
    /// Counter-proof: ignore the last site and Tamsin, idle this time, drops out.</summary>
    [Fact]
    public void ASeries_StartsFromTheListTheLastSiteEndedWith_AndEvidenceOnlyEverAddsATick()
    {
        RunAttendanceDecision lastSite = _Decision((Jithran, true), (Abnoba, true), (Noahmarr, false), (Tamsin, true));
        AttendanceEvidenceCollector evidence = new();
        evidence.SetWindow(StartedAtUtc, null);
        evidence.Note(Noahmarr, SiteContribution.Damage, 1_100, StartedAtUtc.AddMinutes(1));
        evidence.NoteFleetActivity(Ysolde, StartedAtUtc.AddMinutes(1));

        IReadOnlyList<AttendanceProposalLine> lines = AttendanceProposal.Propose(
            [_Local(Jithran), _Local(Abnoba), _Local(Noahmarr), _Other(Tamsin), _Other(Ysolde), _Other(Brannoc)],
            id => evidence.Best(id), lastSite);

        Assert.Equal(new AttendanceProposalLine(Jithran, true, AttendanceReason.SameAsLastSite, null), _Line(lines, Jithran));
        Assert.Equal(new AttendanceProposalLine(Tamsin, true, AttendanceReason.SameAsLastSite, null), _Line(lines, Tamsin));
        Assert.Equal(new AttendanceProposalLine(Noahmarr, true, AttendanceReason.DamageDealt, 1_100, IsAddedToLastSite: true),
            _Line(lines, Noahmarr));
        Assert.True(_Line(lines, Ysolde).IsInSite);
        Assert.Equal(new AttendanceProposalLine(Brannoc, false, AttendanceReason.NoActivityLogged, null), _Line(lines, Brannoc));
    }

    /// <summary>A line the one who decided set by hand is not the proposal's to change: the commander who unticked
    /// Noahmarr keeps him unticked when his damage comes in later.</summary>
    [Fact]
    public void ALineSetByHand_IsNotChangedByEvidenceArrivingLater()
    {
        RunAttendanceDecision standing = new(
            [new RunAttendanceEntryInput { CharacterId = Noahmarr, IsInSite = false, Reason = AttendanceReason.SetByHand }],
            0, AttendanceSource.FleetCommander, Jithran, StartedAtUtc.AddMinutes(5));
        AttendanceEvidenceCollector evidence = new();
        evidence.SetWindow(StartedAtUtc, null);
        evidence.Note(Noahmarr, SiteContribution.Damage, 5_000, StartedAtUtc.AddMinutes(6));

        AttendanceProposalLine line = Assert.Single(AttendanceProposal.Propose([_Local(Noahmarr)], id => evidence.Best(id),
            lastSite: null, standing));

        Assert.False(line.IsInSite);
        Assert.Equal(AttendanceReason.SetByHand, line.Reason);
    }

    // ── Writing the decision ────────────────────────────────────────────────────────────────────────

    /// <summary>The commander's list lands on this client's own runs and nowhere else: a group-mate's run pulled into
    /// the same store under the same code keeps its own. N is the whole list — five — on every own run. Counter-proof:
    /// drop the own-character filter in SetRunAttendanceCommandHandler and Ysolde's run takes it.</summary>
    [AvaloniaFact]
    public async Task TheCommandersList_IsWrittenOnlyOnThisClientsOwnRuns_WithTheSameN()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid jithranRun = await _StartAsync(dispatcher, Jithran, GroupCode);
        Guid abnobaRun = await _StartAsync(dispatcher, Abnoba, GroupCode);
        Guid ysoldeRun = await _StartAsync(dispatcher, Ysolde, GroupCode);

        Result<int> written = await dispatcher.Send(new SetRunAttendanceCommand(_JithransList(StartedAtUtc.AddMinutes(20)),
            [Jithran, Abnoba, Noahmarr], GroupCode));

        Assert.True(written.IsSuccess);
        Assert.Equal(2, written.Value);
        Run jithran = await _RunAsync(instance, jithranRun);
        Run abnoba = await _RunAsync(instance, abnobaRun);
        Run ysolde = await _RunAsync(instance, ysoldeRun);
        Assert.Equal((true, 5, AttendanceSource.FleetCommander, (long?)Jithran),
            (jithran.InSiteAtCompletion, jithran.AttendanceCount ?? 0, jithran.AttendanceSource ?? default, jithran.AttendanceSetByCharacterId));
        Assert.Equal(6, jithran.AttendanceEntries.Count);
        Assert.Equal((true, 5), (abnoba.InSiteAtCompletion, abnoba.AttendanceCount ?? 0));
        Assert.Null(ysolde.InSiteAtCompletion);
        Assert.Null(ysolde.AttendanceCount);
        Assert.Empty(ysolde.AttendanceEntries);
        // The two ET-105 flags are untouched: attendance is a third fact.
        Assert.True(jithran.IsParticipant);
    }

    /// <summary>A late or resent copy never takes a correction back, and a pilot's own list never replaces the
    /// commander's — the local preselection may not override the commander's choice. Counter-proof: take the time check
    /// out of _Takes and the older list comes back.</summary>
    [AvaloniaFact]
    public async Task AnOlderList_NeverReplacesANewerOne_AndAPilotsOwnNeverReplacesTheCommanders()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid run = await _StartAsync(dispatcher, Noahmarr, GroupCode);
        RunAttendanceDecision corrected = _Decision((Noahmarr, true)) with { SetAtUtc = StartedAtUtc.AddMinutes(30) };
        RunAttendanceDecision earlier = _Decision((Noahmarr, false)) with { SetAtUtc = StartedAtUtc.AddMinutes(20) };
        RunAttendanceDecision pilotsOwn = _Decision((Noahmarr, false)) with
        {
            Source = AttendanceSource.Pilot, SetByCharacterId = Noahmarr, SetAtUtc = StartedAtUtc.AddHours(1)
        };

        await dispatcher.Send(new SetRunAttendanceCommand(corrected, [Noahmarr], GroupCode));
        Result<int> late = await dispatcher.Send(new SetRunAttendanceCommand(earlier, [Noahmarr], GroupCode));
        Result<int> guess = await dispatcher.Send(new SetRunAttendanceCommand(pilotsOwn, [Noahmarr], GroupCode));
        Result<int> resent = await dispatcher.Send(new SetRunAttendanceCommand(corrected, [Noahmarr], GroupCode));

        Assert.Equal((0, 0, 0), (late.Value, guess.Value, resent.Value));
        Run stored = await _RunAsync(instance, run);
        Assert.True(stored.InSiteAtCompletion);
        Assert.Equal(StartedAtUtc.AddMinutes(30), stored.AttendanceSetAtUtc);
    }

    /// <summary>A correction after SAVE on a run already published marks it behind the server (ET-215), so it goes up
    /// again — for a fleet run by itself (ET-245).</summary>
    [AvaloniaFact]
    public async Task ACorrectionAfterSave_MarksAPublishedRunBehindTheServer()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid run = await _StartAsync(dispatcher, Jithran, GroupCode);
        await dispatcher.Send(new SaveRunCommand(run, StartedAtUtc.AddMinutes(20), StartedAtUtc.AddMinutes(21), [], [], [], [],
            FleetSizeAtStop: 6));
        await using (ClientDbContext db = await _DbAsync(instance))
            await db.Set<Run>().Where(candidate => candidate.Id == run)
                .ExecuteUpdateAsync(properties => properties.SetProperty(candidate => candidate.SyncState, RunSyncState.Synced));
        int revision = (await _RunAsync(instance, run)).Revision;

        await dispatcher.Send(new SetRunAttendanceCommand(_JithransList(StartedAtUtc.AddMinutes(40)), [Jithran], GroupCode));

        Run stored = await _RunAsync(instance, run);
        Assert.Equal(RunSyncState.Outdated, stored.SyncState);
        Assert.Equal(revision + 1, stored.Revision);
        Assert.Equal(6, stored.FleetSizeAtStop);
        Assert.Equal(5, stored.AttendanceCount);
    }

    // ── A member's client ───────────────────────────────────────────────────────────────────────────

    /// <summary>The commander's list is written onto the member's own runs with no window open, and only when it really
    /// comes from whoever commands the fleet. Counter-proof: drop the commander check and Noahmarr's forged list lands.</summary>
    [AvaloniaFact]
    public async Task AMembersClient_WritesTheCommandersListOntoItsOwnRuns_AndNobodyElses()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _RegisterAsync(instance, (Ysolde, "Ysolde Marrow"), (Brannoc, "Brannoc Marrow"), (Tamsin, "Tamsin Marrow"));
        _Participate(instance, Ysolde);
        _ = instance.Services.GetRequiredService<FleetRunAttendance>();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid ysoldeRun = await _StartAsync(dispatcher, Ysolde, GroupCode);
        Guid brannocRun = await _StartAsync(dispatcher, Brannoc, GroupCode);
        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();

        await bus.PublishAsync(new FleetRunAttendanceEvent(_Message(_Decision((Ysolde, false), (Brannoc, false))), Noahmarr),
            EventTarget.Local);
        Assert.Null((await _RunAsync(instance, ysoldeRun)).AttendanceCount);

        await bus.PublishAsync(new FleetRunAttendanceEvent(_Message(_JithransList(StartedAtUtc.AddMinutes(20))), Jithran),
            EventTarget.Local);

        Run ysolde = await _RunAsync(instance, ysoldeRun);
        Run brannoc = await _RunAsync(instance, brannocRun);
        Assert.Equal((true, 5, (long?)Jithran), (ysolde.InSiteAtCompletion, ysolde.AttendanceCount ?? 0, ysolde.AttendanceSetByCharacterId));
        Assert.Equal((true, 5), (brannoc.InSiteAtCompletion, brannoc.AttendanceCount ?? 0));
        Assert.NotNull(instance.Services.GetRequiredService<FleetRunAttendance>().ReceivedAtUtc(GroupCode));
    }

    /// <summary>A member offline when the commander corrected the list — or online only after the fleet ended, when no
    /// fleet message reaches anyone — takes it from the commander's own run on the next pull. Counter-proof: without
    /// the adoption in RunSynchronizationApplier the member's run keeps the old list.</summary>
    [AvaloniaFact]
    public async Task AMemberOfflineWhenTheCommanderCorrected_TakesTheListFromTheCommandersRunOnThePull()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _RegisterAsync(instance, (Ysolde, "Ysolde Marrow"));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid ysoldeRun = await _StartAsync(dispatcher, Ysolde, GroupCode);
        await dispatcher.Send(new SetRunAttendanceCommand(_Decision((Ysolde, false)), [Ysolde], GroupCode));

        Run commandersRun = new()
        {
            Id = Guid.CreateVersion7(), CharacterId = Jithran, GroupCode = GroupCode, ActivityKind = ActivityKind.Site,
            State = RunState.Saved, StartedAtUtc = StartedAtUtc, StoppedAtUtc = StartedAtUtc.AddMinutes(20),
            SavedAtUtc = StartedAtUtc.AddMinutes(21), SiteTypeId = 10347, SiteName = "Raid: Hall of Sacrifice", Revision = 3
        };
        RunAttendanceDecision correction = _JithransList(StartedAtUtc.AddHours(2));
        _ApplyTo(commandersRun, correction);
        await instance.Services.GetRequiredService<RunSynchronizationApplier>().ApplyAsync(ServerAddress,
            [new RunWirePayload { Run = RunWireData.FromEntity(commandersRun), SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }],
            new HashSet<Guid>());

        Run ysolde = await _RunAsync(instance, ysoldeRun);
        Assert.True(ysolde.InSiteAtCompletion);
        Assert.Equal(5, ysolde.AttendanceCount);
        Assert.Equal(AttendanceSource.FleetCommander, ysolde.AttendanceSource);
        Assert.Equal(StartedAtUtc.AddHours(2), ysolde.AttendanceSetAtUtc);
    }

    // ── A series ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three homefronts in a row in one fleet: sites 2 and 3 start from the list site 1 ended with, and the
    /// commander's change on site 2 is the default for site 3. Another fleet's list, newer or not, is no part of it.</summary>
    [AvaloniaFact]
    public async Task ThreeHomefrontsInOneFleet_EachStartsFromTheListThePreviousOneEndedWith()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _StartAsync(dispatcher, Jithran, "HF-SITE1", FleetId);
        await _StartAsync(dispatcher, Jithran, "HF-SITE2", FleetId);
        await _StartAsync(dispatcher, Jithran, "HF-SITE3", FleetId);
        await _StartAsync(dispatcher, Jithran, "HF-OTHER", FleetId + 1);
        await dispatcher.Send(new SetRunAttendanceCommand(_JithransList(StartedAtUtc.AddMinutes(20)), [Jithran], "HF-SITE1"));
        await dispatcher.Send(new SetRunAttendanceCommand(_Decision((Jithran, false)) with { SetAtUtc = StartedAtUtc.AddHours(5) },
            [Jithran], "HF-OTHER"));

        RunAttendanceDecision? forSite2 = await _BaseAsync(dispatcher, "HF-SITE2");
        IReadOnlyList<AttendanceProposalLine> site2 = AttendanceProposal.Propose(_SixCandidates(), _ => null, forSite2);
        Assert.Equal([Jithran, Abnoba, Ysolde, Brannoc, Tamsin],
            site2.Where(line => line.IsInSite).Select(line => line.CharacterId).Order().Select(id => (int)id));

        // The commander takes Brannoc out on site 2, by hand.
        RunAttendanceDecision site2Final = new(
            [.. site2.Select(line => new RunAttendanceEntryInput
            {
                CharacterId = line.CharacterId,
                IsInSite = line.IsInSite && line.CharacterId != Brannoc,
                Reason = line.CharacterId == Brannoc ? AttendanceReason.SetByHand : line.Reason
            })], 0, AttendanceSource.FleetCommander, Jithran, StartedAtUtc.AddMinutes(50));
        await dispatcher.Send(new SetRunAttendanceCommand(site2Final, [Jithran], "HF-SITE2"));

        IReadOnlyList<AttendanceProposalLine> site3 = AttendanceProposal.Propose(_SixCandidates(), _ => null,
            await _BaseAsync(dispatcher, "HF-SITE3"));
        Assert.Equal([Jithran, Abnoba, Ysolde, Tamsin],
            site3.Where(line => line.IsInSite).Select(line => line.CharacterId).Order().Select(id => (int)id));
        Assert.All(site3.Where(line => line.IsInSite), line => Assert.Equal(AttendanceReason.SameAsLastSite, line.Reason));
    }

    // ── The run window ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Jithran's example end to end, over the fake server wire. The commander's window proposes Noahmarr out with "no
    /// activity logged", writes the list on his own run and sends it; the member's client writes it on hers, and her
    /// window shows it read-only without being reopened. N = 5 on both machines, with the same source and time. A
    /// member who connected after the first send gets the list from the commander's resend.
    /// </summary>
    [AvaloniaFact]
    public async Task JithransHomefront_TheHaulerIsProposedOut_AndNIsFiveOnBothMachines()
    {
        RunGroupCodeStart start = new(FleetId, ActivityKind.Site, GroupCode, DateTime.UtcNow.AddMinutes(-2), IsFleetCommander: true);
        using Machine fc = await Machine.CreateAsync(Jithran, (Jithran, "Jithran"), (Abnoba, "Abnoba Auscent"), (Noahmarr, "Noahmarr"));
        using Machine member = await Machine.CreateAsync(Ysolde, (Ysolde, "Ysolde Marrow"), (Brannoc, "Brannoc Marrow"), (Tamsin, "Tamsin Marrow"));
        await fc.JoinAsync(start);
        await member.JoinAsync(start);

        using HomefrontWindowSectionViewModel commander = new(fc.Window);
        await fc.TickUntilAsync(commander, () => commander.Rows.Count == 6);

        GamelogClientService gamelog = fc.Instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(Jithran, "Jithran");
        gamelog.MapCharacter(Abnoba, "Abnoba Auscent");
        gamelog.AddRemoteRep("Jithran", outgoing: true, 64_000);
        gamelog.AddCapTransfer("Abnoba Auscent", outgoing: true, 142_000);
        IEventBus fcBus = fc.Instance.Services.GetRequiredService<IEventBus>();
        foreach (int other in new[] { Ysolde, Brannoc, Tamsin })
            await fcBus.PublishAsync(new FleetMetricEvent(new MetricSample(other, FleetId, MetricKind.Dps, 350,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), other), EventTarget.Local);
        await fc.TickUntilAsync(commander, () => fc.Wire.Sent.OfType<FleetRunAttendanceEvent>().Any(_IsJithransList));

        Assert.True(commander.CanDecide);
        Assert.Equal(6, commander.Rows.Count);
        AttendanceRowViewModel hauler = commander.Rows.Single(row => row.CharacterId == Noahmarr);
        Assert.False(hauler.IsInSite);
        Assert.True(hauler.IsWithoutEvidence);
        Assert.Equal("no activity logged", hauler.ReasonText);
        Assert.Equal(["Abnoba Auscent", "Jithran", "Noahmarr"],
            commander.Rows.Where(row => row.IsLocal).Select(row => row.Name));
        Assert.Equal("6 in fleet · 5 in site", commander.CountText);
        FleetRunAttendanceEvent sent = fc.Wire.Sent.OfType<FleetRunAttendanceEvent>().Last();
        Assert.Equal(Jithran, sent.CharacterId);
        Assert.Equal([Noahmarr], sent.Data.Characters.Where(line => !line.IsInSite).Select(line => line.CharacterId));
        Run fcRun = await _RunAsync(fc.Instance, fc.Window.RunId ?? Guid.Empty);
        Assert.Equal((true, 5, AttendanceSource.FleetCommander), (fcRun.InSiteAtCompletion, fcRun.AttendanceCount ?? 0, fcRun.AttendanceSource ?? default));

        // The member was not connected for that first send: nothing on her run, and her window waits, never guessing.
        using HomefrontWindowSectionViewModel reader = new(member.Window);
        await member.TickUntilAsync(reader, () => reader.Rows.Count > 0);
        Assert.False(reader.CanDecide);
        Assert.Null((await _RunAsync(member.Instance, member.Window.RunId ?? Guid.Empty)).AttendanceCount);
        Assert.NotNull(reader.NoticeText);

        int sentBefore = fc.Wire.Sent.OfType<FleetRunAttendanceEvent>().Count();
        fc.Wire.Destinations.Add(member.Instance.Services);
        await fc.TickUntilAsync(commander, () => fc.Wire.Sent.OfType<FleetRunAttendanceEvent>().Count() > sentBefore,
            step: TimeSpan.FromSeconds(10));
        await member.TickUntilAsync(reader, () => reader.Rows.Count == 6 && reader.NoticeText is null);

        Run memberRun = await _RunAsync(member.Instance, member.Window.RunId ?? Guid.Empty);
        Assert.Equal((true, 5, (long?)Jithran, fcRun.AttendanceSetAtUtc),
            (memberRun.InSiteAtCompletion, memberRun.AttendanceCount ?? 0, memberRun.AttendanceSetByCharacterId, memberRun.AttendanceSetAtUtc));
        Assert.False(reader.CanDecide);
        Assert.All(reader.Rows, row => Assert.False(row.IsEditable));
        Assert.False(reader.Rows.Single(row => row.CharacterId == Noahmarr).IsInSite);
        Assert.Equal("6 in fleet · 5 in site", reader.CountText);
    }

    /// <summary>AC-3: flown without a fleet the pilot decides for their own run — nobody else's list to wait for, and
    /// nothing goes to a fleet. The proposal lands as their own decision, and their own tick goes over it.</summary>
    [AvaloniaFact]
    public async Task FlownWithoutAFleet_ThePilotTicksThemselves()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync();
        await window.StartRunCommand.ExecuteAsync(null);
        Guid runId = window.RunId ?? throw new InvalidOperationException("the solo run did not start");
        using HomefrontWindowSectionViewModel section = new(window);
        DateTime clock = DateTime.UtcNow;

        async Task<Run> TickUntilAsync(Func<Run, bool> until)
        {
            Run run = await _RunAsync(harness.Instance, runId);
            for (int tick = 0; tick < 30 && !until(run); tick++)
            {
                clock = clock.AddSeconds(1);
                window.Refresh(clock);
                section.Refresh(clock);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
                run = await _RunAsync(harness.Instance, runId);
            }

            return run;
        }

        Run proposed = await TickUntilAsync(run => run.AttendanceSource is not null);
        Assert.True(section.CanDecide);
        Assert.Equal("you", section.DecidedByText);
        Assert.Equal((AttendanceSource.Pilot, (long?)ActivityWindowHarness.CharacterId, false),
            (proposed.AttendanceSource ?? default, proposed.AttendanceSetByCharacterId, proposed.InSiteAtCompletion ?? true));

        section.Rows.Single(row => row.CharacterId == ActivityWindowHarness.CharacterId).IsInSite = true;
        Run ticked = await TickUntilAsync(run => run.InSiteAtCompletion == true);

        Assert.True(ticked.InSiteAtCompletion);
        Assert.Equal(1, ticked.AttendanceCount);
        Assert.Equal(AttendanceReason.SetByHand, Assert.Single(ticked.AttendanceEntries).Reason);
    }

    /// <summary>ET-105's hauler — a run in the group, no share — reads the same after this ticket as before it (AC-7).
    /// Only once a homefront's attendance is decided does the first half stop claiming "flew the site".</summary>
    [Fact]
    public void AnUndecidedRun_ReadsExactlyAsBefore_AndADecidedOneNoLongerClaimsItFlewTheSite()
    {
        Assert.Equal("flew the site · no share", RunParticipantViewModel.Describe(true, false, null));
        Assert.Equal("did not fly the site · takes a share", RunParticipantViewModel.Describe(false, true, null));
        Assert.Equal("in the group · takes a share", RunParticipantViewModel.Describe(true, true, false));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static AttendanceCandidate _Local(int id) => new(id, $"Char {id}", IsLocal: true, IsExternal: false);

    private static AttendanceCandidate _Other(int id) => new(id, $"Char {id}", IsLocal: false, IsExternal: false);

    private static AttendanceCandidate _External(int id) => new(id, $"Char {id}", IsLocal: false, IsExternal: true);

    private static AttendanceCandidate[] _SixCandidates() =>
        [_Local(Jithran), _Local(Abnoba), _Local(Noahmarr), _Other(Ysolde), _Other(Brannoc), _Other(Tamsin)];

    private static AttendanceProposalLine _Line(IReadOnlyList<AttendanceProposalLine> lines, long id) =>
        lines.Single(line => line.CharacterId == id);

    private static RunAttendanceDecision _Decision(params (int Id, bool InSite)[] ticks) => new(
        [.. ticks.Select(tick => new RunAttendanceEntryInput
        {
            CharacterId = tick.Id,
            IsInSite = tick.InSite,
            Reason = tick.InSite ? AttendanceReason.DamageDealt : AttendanceReason.NoActivityLogged
        })], 0, AttendanceSource.FleetCommander, Jithran, StartedAtUtc.AddMinutes(10));

    /// <summary>Jithran's example as the commander confirmed it: the hauler out, everyone else in — N = 5.</summary>
    private static RunAttendanceDecision _JithransList(DateTime setAtUtc) =>
        _Decision((Jithran, true), (Abnoba, true), (Noahmarr, false), (Ysolde, true), (Brannoc, true), (Tamsin, true))
            with { SetAtUtc = setAtUtc };

    private static bool _IsJithransList(FleetRunAttendanceEvent sent) =>
        sent.Data.Characters.Count(line => line.IsInSite) + sent.Data.NotOnRosterCount == 5;

    private static RunGroupAttendance _Message(RunAttendanceDecision decision) => new(FleetId, GroupCode,
        new DateTimeOffset(decision.SetAtUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(), decision.Entries, decision.NotOnRosterCount);

    private static void _ApplyTo(Run run, RunAttendanceDecision decision)
    {
        foreach (RunAttendanceEntryInput entry in decision.Entries)
            run.AttendanceEntries.Add(new RunAttendanceEntry
            {
                Id = Guid.CreateVersion7(), RunId = run.Id, CharacterId = entry.CharacterId, IsInSite = entry.IsInSite,
                Reason = entry.Reason
            });
        run.InSiteAtCompletion = decision.Entries.Single(entry => entry.CharacterId == run.CharacterId).IsInSite;
        run.AttendanceCount = decision.InSiteCount;
        run.AttendanceNotOnRosterCount = decision.NotOnRosterCount;
        run.AttendanceSource = decision.Source;
        run.AttendanceSetByCharacterId = decision.SetByCharacterId;
        run.AttendanceSetAtUtc = decision.SetAtUtc;
    }

    private static async Task<Guid> _StartAsync(IDispatcher dispatcher, long characterId, string groupCode, long? fleetId = null)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc, 10347,
            "Raid: Hall of Sacrifice", 30000142, groupCode, FleetId: fleetId));
        Assert.True(started.IsSuccess);
        return started.Value;
    }

    private static async Task<RunAttendanceDecision?> _BaseAsync(IDispatcher dispatcher, string groupCode) =>
        (await dispatcher.Query(new GetFleetAttendanceBaseQuery(FleetId, groupCode))).Value;

    private static async Task<ClientDbContext> _DbAsync(TestClientInstance instance) =>
        await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<Run>().AsNoTracking().Include(run => run.AttendanceEntries).SingleAsync(run => run.Id == runId);
    }

    private static async Task _RegisterAsync(TestClientInstance instance, params (int Id, string Name)[] characters)
    {
        foreach ((int id, string name) in characters)
            await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, id));
    }

    private static void _Participate(TestClientInstance instance, int characterId) =>
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(characterId, FleetId, ClientOnly: false, Jithran, ServerAddress)]);

    /// <summary>One member's machine: its own characters, in the fleet with Jithran commanding, reading the roster of
    /// all six from a fake server, and one run window joined on the commander's start.</summary>
    private sealed class Machine : IDisposable
    {
        private DateTime _clock = DateTime.UtcNow;

        private Machine(TestClientInstance instance, ServerWire wire, ActivityWindowViewModel window)
        {
            Instance = instance;
            Wire = wire;
            Window = window;
        }

        public TestClientInstance Instance { get; }

        public ServerWire Wire { get; }

        public ActivityWindowViewModel Window { get; }

        public static async Task<Machine> CreateAsync(int flying, params (int Id, string Name)[] own)
        {
            ServerWire wire = new();
            RecordingFleetTransportClient transport = new();
            transport.MembersByFleet[FleetId] =
            [
                .. new[] { Jithran, Abnoba, Noahmarr, Ysolde, Brannoc, Tamsin }.Select((id, index) => new FleetMemberInfo(
                    index + 1, id, 1, 1, id == Jithran ? FleetRole.FleetCommander : FleetRole.SquadMember, IsExternal: false,
                    LastSeenAt: DateTimeOffset.UtcNow))
            ];
            TestClientInstance instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<IRemoteEventTransport>(wire);
                services.AddSingleton<IFleetTransportClient>(transport);
                services.AddSingleton<IDialogService>(new RecordingDialogService());
                services.AddSingleton<IToastService>(new RecordingToastService());
                // Only the character flying this window is at the keyboard, so the window settles on it by itself.
                services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(true, flying));
                services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
                {
                    [Jithran] = "Jithran", [Abnoba] = "Abnoba Auscent", [Noahmarr] = "Noahmarr",
                    [Ysolde] = "Ysolde Marrow", [Brannoc] = "Brannoc Marrow", [Tamsin] = "Tamsin Marrow"
                });
            });
            await _RegisterAsync(instance, own);
            _Participate(instance, flying);
            _ = instance.Services.GetRequiredService<FleetRunAttendance>();

            ActivityWindowViewModel window = new(ActivityKind.Site, instance.Services);
            await window.LoadAsync();
            return new Machine(instance, wire, window);
        }

        public async Task JoinAsync(RunGroupCodeStart start)
        {
            Window.JoinFleetRun(start);
            for (int attempt = 0; attempt < 100 && Window.RunId is null; attempt++)
                await _PumpAsync();
            Window.Refresh(_clock);
        }

        /// <summary>The window's clock and the section's, a second at a time the way the real timer runs, until the
        /// condition holds.</summary>
        public async Task TickUntilAsync(HomefrontWindowSectionViewModel section, Func<bool> until, TimeSpan? step = null)
        {
            for (int tick = 0; tick < 60 && !until(); tick++)
            {
                _clock += step ?? TimeSpan.FromSeconds(1);
                Window.Refresh(_clock);
                section.Refresh(_clock);
                await _PumpAsync();
            }

            Assert.True(until(), "the condition was never reached; the section stood at: " + string.Join("; ",
                section.Rows.Select(row => $"{row.CharacterId} {row.TickText} ({row.ReasonText})"))
                + $" | {section.CountText} | decides={section.CanDecide} | sent={Wire.Sent.OfType<FleetRunAttendanceEvent>().Count()}"
                + $" | run={Window.RunId} state={Window.RunState} fleet={Window.FleetId} group={Window.GroupCode}");
        }

        private static async Task _PumpAsync()
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Dispose();
            Instance.Dispose();
        }
    }
}
