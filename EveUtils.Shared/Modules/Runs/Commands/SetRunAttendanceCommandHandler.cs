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
internal sealed class SetRunAttendanceCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
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

        RunAttendanceDecision decision = command.Decision;
        long[] own = [.. command.OwnCharacterIds];
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .Include(run => run.AttendanceEntries)
            .Where(run => !run.DeletedAtUtc.HasValue && own.Contains(run.CharacterId)
                          && (byGroup ? run.GroupCode == command.GroupCode : run.Id == command.RunId))
            .ToListAsync(cancellationToken);

        List<Run> changed = [.. runs.Where(run => _Takes(run, decision))];
        foreach (Run run in changed)
            _Apply(db, run, decision);

        if (changed.Count == 0)
            return Result<int>.Success(0);

        await db.SaveChangesAsync(cancellationToken);
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
