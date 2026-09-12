using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetRunAttendanceCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<SetRunAttendanceCommand, Result<int>>
{
    public async Task<Result<int>> Handle(SetRunAttendanceCommand command, CancellationToken cancellationToken = default)
    {
        bool byGroup = !string.IsNullOrEmpty(command.GroupCode);
        if (byGroup == command.RunId.HasValue)
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "An attendance decision needs either the group it applies to or the one run, not both.", "Runs"));
        if (command.Decision.NotOnRosterCount < 0)
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "The number of pilots not on the roster cannot be negative.", "Runs"));

        long[] own = [.. command.OwnCharacterIds];
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .Include(run => run.AttendanceEntries)
            .Where(run => !run.DeletedAtUtc.HasValue && own.Contains(run.CharacterId)
                          && (byGroup ? run.GroupCode == command.GroupCode : run.Id == command.RunId))
            .ToListAsync(cancellationToken);
        // A proposal the store has moved on from is not written: somebody decided since it was worked out (I5). Read over
        // every run of the group, the same set GetRunAttendanceQuery gave the window its standing list from.
        if (command.IsProposal && await db.Set<Run>()
                .Where(run => !run.DeletedAtUtc.HasValue && (byGroup ? run.GroupCode == command.GroupCode : run.Id == command.RunId))
                .MaxAsync(run => run.AttendanceSetAtUtc, cancellationToken) != command.StandingSetAtUtc)
            return Result<int>.Success(0);
        // An outcome already stored is never erased by a list that carries none (RunAttendanceDecision.KeepingOutcomeOf).
        RunAttendanceDecision decision = command.Decision.KeepingOutcomeOf(runs
            .Where(run => run.HomefrontOutcome is not null || run.HomefrontCompletedWaveCount is not null)
            .MaxBy(run => run.AttendanceSetAtUtc) is { } decided
                ? RunAttendanceDecision.Of(decided)
                : null);

        // An own character ticked in the site (ET-269) but with no run of its own here: the pilot started this
        // homefront for one toon only, or a multi-pick missed one, and only found out who was really in the site once
        // the attendance was decided. A run of its own is the one thing that makes its own payout and loot ever reach
        // TOTAL ISK — ticking it here cannot do that by itself.
        IReadOnlyList<Run> backfilled = byGroup && command.GroupCode is { } groupCode
            ? _BackfillMissingOwnRuns(db, runs, decision, own, groupCode)
            : [];
        runs.AddRange(backfilled);

        List<Run> changed = [.. runs.Where(run => _Takes(run, decision))];
        foreach (Run run in changed)
            _Apply(db, run, decision);

        if (changed.Count == 0)
            return Result<int>.Success(0);

        await db.SaveChangesAsync(cancellationToken);

        // A run made just now missed every bounty line its character earned before it existed (ET-271).
        if (backfilled.Count > 0)
            await dispatcher.Send(new ImportRunBountyCommand([.. backfilled.Select(run => run.Id)]), cancellationToken);
        // The outcome, N and every tick are inputs of a saved activity's stored total (ET-271): set after SAVE, they
        // add it up again, or the overview, the month bar and the detail keep the figure from before.
        if (changed.FirstOrDefault(run => run.State is RunState.Saved) is { } saved)
            await dispatcher.Send(new RebuildActivitySummariesCommand(saved.Id), cancellationToken);
        await eventBus.PublishAsync(
            new RunsChangedEvent(changed.Count == 1 ? changed[0].Id : null, byGroup ? command.GroupCode : changed[0].GroupCode),
            EventTarget.Local, cancellationToken);
        return Result<int>.Success(changed.Count);
    }

    // The commander's list beats a pilot's own whatever the clocks say — a guess of this client's never overrides it,
    // and the two were stamped on two different machines. Between two of the same kind the newer wins, and the same
    // list again changes nothing, so a resend every half minute costs no write and no redraw.
    private static bool _Takes(Run run, RunAttendanceDecision decision)
    {
        if (run.AttendanceSource is { } storedSource && storedSource != decision.Source)
            return decision.Source is AttendanceSource.FleetCommander;
        if (run.AttendanceSetAtUtc is { } storedAt && storedAt > decision.SetAtUtc)
            return false;

        return RunAttendanceDecision.Of(run) is not { } stored
               || !stored.ListsTheSameAs(decision)
               || stored.Source != decision.Source
               || stored.SetByCharacterId != decision.SetByCharacterId;
    }

    /// <summary>A run of its own for every own character the decision ticks in the site, cloned from the group's own
    /// earliest run — same site, same times, same origin — so a homefront started for one toon (or a multi-pick that
    /// missed one) still counts every other own character's payout and loot once who was really there is known
    /// (ET-269, Jithran's own HF-V7MB: one run, five own characters ticked in).</summary>
    private static IReadOnlyList<Run> _BackfillMissingOwnRuns(
        ClientDbContext db, IReadOnlyList<Run> runs, RunAttendanceDecision decision, long[] own, string groupCode)
    {
        if (runs.Count == 0)
            return [];

        Run template = runs.OrderBy(run => run.StartedAtUtc).First();
        HashSet<long> present = [.. runs.Select(run => run.CharacterId)];
        List<Run> created = [];
        foreach (RunAttendanceEntryInput entry in decision.Entries)
        {
            if (!entry.IsInSite || !own.Contains(entry.CharacterId) || !present.Add(entry.CharacterId))
                continue;

            Run sibling = new()
            {
                Id = Guid.CreateVersion7(),
                CharacterId = entry.CharacterId,
                GroupCode = groupCode,
                ActivityKind = template.ActivityKind,
                State = template.State,
                StartedAtUtc = template.StartedAtUtc,
                StoppedAtUtc = template.StoppedAtUtc,
                SavedAtUtc = template.SavedAtUtc,
                SiteTypeId = template.SiteTypeId,
                SiteTypeSource = template.SiteTypeSource,
                SiteName = template.SiteName,
                SolarSystemId = template.SolarSystemId,
                Signature = template.Signature,
                Role = RunRole.Member,
                IsParticipant = true,
                IsPayoutEligible = template.IsPayoutEligible,
                CharacterNameSnapshot = entry.CharacterName,
                SignatureGroupSnapshot = template.SignatureGroupSnapshot,
                Origin = template.Origin,
                FleetSizeAtStop = template.FleetSizeAtStop,
                SyncState = RunSyncState.Local,
                Revision = 1
            };
            db.Set<Run>().Add(sibling);
            created.Add(sibling);
        }

        return created;
    }

    private static void _Apply(ClientDbContext db, Run run, RunAttendanceDecision decision)
    {
        db.Set<RunAttendanceEntry>().RemoveRange(run.AttendanceEntries);
        foreach (RunAttendanceEntryInput entry in decision.Entries)
            db.Set<RunAttendanceEntry>().Add(new RunAttendanceEntry
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                CharacterId = entry.CharacterId,
                CharacterName = entry.CharacterName,
                IsInSite = entry.IsInSite,
                IsExternal = entry.IsExternal,
                Reason = entry.Reason,
                ReasonAmount = entry.ReasonAmount
            });

        // A character the list does not name stays undecided rather than read as "not in site": the one who decided
        // never saw them, which is not the same as having seen them leave.
        run.InSiteAtCompletion = decision.Entries.FirstOrDefault(entry => entry.CharacterId == run.CharacterId)?.IsInSite;
        run.AttendanceCount = decision.InSiteCount;
        run.AttendanceNotOnRosterCount = decision.NotOnRosterCount;
        run.AttendanceSource = decision.Source;
        run.AttendanceSetByCharacterId = decision.SetByCharacterId;
        run.AttendanceSetAtUtc = decision.SetAtUtc;
        run.HomefrontOutcome = decision.Outcome;
        run.HomefrontCompletedWaveCount = decision.CompletedWaveCount;
        run.HomefrontOutcomeFromGameLog = decision.OutcomeFromGameLog;

        // The table version this run's own expected figure is computed against (ET-231) — recorded so two clients on
        // two app versions never silently disagree about the same site. Null, like the figure itself, until there is
        // one to compute.
        run.HomefrontPayoutTableVersion = HomefrontCatalogue.KindByDungeonId.TryGetValue(run.SiteTypeId, out string? kind)
            && HomefrontPayoutTable.TryGetExpected(kind, run.InSiteAtCompletion, run.AttendanceCount,
                run.HomefrontOutcome, run.HomefrontCompletedWaveCount, run.StoppedAtUtc ?? decision.SetAtUtc) is { } expected
            ? expected.Version
            : null;

        // ET-215's rule for a change after the fact: the revision moves, and a published copy is now behind the server
        // until it goes up again — which for a fleet run happens by itself (ET-245).
        run.Revision++;
        if (run.SyncState is RunSyncState.Synced)
            run.SyncState = RunSyncState.Outdated;
    }
}
