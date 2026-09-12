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
        // A homefront outcome already here is never erased by a copy that carries none (ET-271, the rule
        // RunAttendanceDecision.KeepingOutcomeOf keeps for every list): a server older than the column hands runs back
        // without it, and replacing the row with that copy dropped the Completed the pilot had set.
        var storedOutcomes = (await db.Set<Run>().AsNoTracking()
                .Where(run => runIds.Contains(run.Id) && (run.HomefrontOutcome != null || run.HomefrontCompletedWaveCount != null))
                .Select(run => new
                {
                    run.Id, run.HomefrontOutcome, run.HomefrontCompletedWaveCount, run.HomefrontOutcomeFromGameLog,
                    run.HomefrontPayoutTableVersion
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(run => run.Id);
        // I7 (ET-274): one live run per character per group, here as everywhere. Which run each (group, character)
        // pulled here already has — a group mate's run from an earlier pull, or this pilot's own — so a server copy that
        // holds a second one (an older client published both halves of a doubled start) never files it beside the first.
        string[] groupCodes = [.. payloads.Select(payload => payload.Run.GroupCode).OfType<string>().Distinct()];
        Dictionary<(string GroupCode, long CharacterId), Guid> holders = (await db.Set<Run>().AsNoTracking()
                .Where(run => run.GroupCode != null && groupCodes.Contains(run.GroupCode) && !run.DeletedAtUtc.HasValue)
                .Select(run => new { GroupCode = run.GroupCode ?? string.Empty, run.CharacterId, run.Id })
                .ToListAsync(cancellationToken))
            .ToDictionary(run => (run.GroupCode, run.CharacterId), run => run.Id);
        List<Run> applied = [];
        // Tombstones first, so a deleted copy has left its place before a live one is weighed against it; then the
        // oldest id first, the same run every client keeps when a server holds two.
        foreach (RunWirePayload payload in payloads.OrderBy(payload => payload.Run.DeletedAtUtc is null).ThenBy(payload => payload.Run.Id))
        {
            Run run = payload.Run.ToEntity();
            if (protectedRunIds.Contains(run.Id) || pushedRunIds.Contains(run.Id))
                continue;

            if (run.DeletedAtUtc is not null)
            {
                applied.Add(run);
                await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
                if (run.GroupCode is { } deletedFrom && holders.GetValueOrDefault((deletedFrom, run.CharacterId)) == run.Id)
                    holders.Remove((deletedFrom, run.CharacterId));
                continue;
            }

            if (run.GroupCode is { } groupCode)
            {
                if (holders.TryGetValue((groupCode, run.CharacterId), out Guid holder) && holder != run.Id)
                    continue;
                holders[(groupCode, run.CharacterId)] = run.Id;
            }

            applied.Add(run);

            if (run is { HomefrontOutcome: null, HomefrontCompletedWaveCount: null }
                && storedOutcomes.TryGetValue(run.Id, out var kept))
            {
                run.HomefrontOutcome = kept.HomefrontOutcome;
                run.HomefrontCompletedWaveCount = kept.HomefrontCompletedWaveCount;
                run.HomefrontOutcomeFromGameLog = kept.HomefrontOutcomeFromGameLog;
                run.HomefrontPayoutTableVersion ??= kept.HomefrontPayoutTableVersion;
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
