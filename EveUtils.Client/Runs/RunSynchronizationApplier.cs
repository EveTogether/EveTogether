using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Client.Runs;

public sealed class RunSynchronizationApplier(
    IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher, IEventBus eventBus,
    ICharacterRegistry characters) : IScopedService
{
    /// <summary>Applies what <paramref name="serverAddress"/> handed back. The runs land under a group-mate's own
    /// character id, so the day list shows the whole activity rather than only this machine's half of it.</summary>
    public async Task ApplyAsync(string serverAddress, IReadOnlyList<RunWirePayload> payloads, IReadOnlySet<Guid> pushedRunIds,
        CancellationToken cancellationToken = default)
    {
        if (payloads.Count == 0)
            return;

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Guid[] runIds = payloads.Select(payload => payload.Run.Id).ToArray();
        HashSet<Guid> protectedRunIds = await db.Set<Run>().AsNoTracking()
            .Where(run => runIds.Contains(run.Id) && run.SyncState != RunSyncState.Synced)
            .Select(run => run.Id).ToHashSetAsync(cancellationToken);
        List<Run> applied = [];
        foreach (RunWirePayload payload in payloads)
        {
            Run run = payload.Run.ToEntity();
            if (protectedRunIds.Contains(run.Id) || pushedRunIds.Contains(run.Id))
                continue;

            applied.Add(run);
            if (run.DeletedAtUtc is not null)
            {
                await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
                continue;
            }

            run.StartedAtUtc = _Anchor(run.StartedAtUtc, payload.SentAtUnixMilliseconds);
            run.StoppedAtUtc = run.StoppedAtUtc is { } stoppedAtUtc ? _Anchor(stoppedAtUtc, payload.SentAtUnixMilliseconds) : null;
            run.SyncState = RunSyncState.Synced;
            run.SyncServerAddress = serverAddress;
            await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
            db.Set<Run>().Add(run);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        // Per run and with its group code: a group-mate's run arriving is exactly the change an open detail screen of
        // that activity cannot otherwise tell from any of the runs it already shows (ET-222).
        foreach (Run run in applied)
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);

        await _AdoptCommanderAttendanceAsync(applied, cancellationToken);
    }

    /// <summary>
    /// The fleet commander's homefront attendance list, taken from the group-mate runs that just arrived and written
    /// onto this pilot's own runs of the same group (ET-230). The live message only reaches a member connected while
    /// the fleet is still active; this is how one who was offline when the commander corrected the list — or who only
    /// comes online after the fleet ended — still ends up with it. Only a commander's decision is adopted, the newest
    /// one, and the command itself refuses to put an older one over a newer.
    /// </summary>
    private async Task _AdoptCommanderAttendanceAsync(IReadOnlyList<Run> applied, CancellationToken cancellationToken)
    {
        Dictionary<string, RunAttendanceDecision> newest = [];
        foreach (Run run in applied)
            if (run is { DeletedAtUtc: null, GroupCode: { } groupCode, AttendanceSource: AttendanceSource.FleetCommander }
                && RunAttendanceDecision.Of(run) is { } decision
                && (!newest.TryGetValue(groupCode, out RunAttendanceDecision? known) || known.SetAtUtc < decision.SetAtUtc))
                newest[groupCode] = decision;
        if (newest.Count == 0)
            return;

        long[] own = [.. (await characters.GetAllAsync(cancellationToken))
            .Select(character => character.EsiCharacterId)
            .OfType<int>()
            .Select(characterId => (long)characterId)];
        foreach ((string groupCode, RunAttendanceDecision decision) in newest)
            await dispatcher.Send(new SetRunAttendanceCommand(decision, own, groupCode), cancellationToken);
    }

    private static DateTime _Anchor(DateTime sourceUtc, long sentAtUnixMilliseconds) =>
        AbyssalSpace.AnchorFromWireUtc(sourceUtc, sentAtUnixMilliseconds, DateTime.UtcNow);
}
