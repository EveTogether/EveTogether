using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class StopRunsLeftRunningCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<StopRunsLeftRunningCommand, Result<IReadOnlyList<StoppedRunDto>>>
{
    public async Task<Result<IReadOnlyList<StoppedRunDto>>> Handle(StopRunsLeftRunningCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> running = await db.Set<Run>()
            .Where(run => run.State == RunState.Running && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        if (running.Count == 0)
            return Result<IReadOnlyList<StoppedRunDto>>.Success([]);

        foreach (Run run in running)
        {
            // ET-254: LastAliveAtUtc (written about once a minute while the run was on the clock, TouchRunAliveCommand)
            // is the last moment anything actually confirmed this run was still going — the restart moment only
            // confirms the process was dead at some point between then and now. Raymond's own case (the app closed
            // at 20:00, restarted at 08:00) used to read as an eight-hour run; with a heartbeat it reads as ending
            // close to 20:00. A run that never wrote one — started and crashed within the same minute, or left
            // running by a build before this ticket — falls back to the restart moment exactly as before: nothing
            // else is knowable about it.
            run.State = RunState.Stopped;
            run.StoppedAtUtc = run.LastAliveAtUtc is { } lastAlive && lastAlive > run.StartedAtUtc
                ? lastAlive
                : command.StoppedAtUtc;
            if (run.SyncState is not RunSyncState.Local)
                run.SyncState = RunSyncState.Pending;
            run.Revision++;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (Run run in running)
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result<IReadOnlyList<StoppedRunDto>>.Success([.. running.Select(run =>
            new StoppedRunDto(run.Id, run.CharacterId, run.ActivityKind, run.SiteName, run.SignatureGroupSnapshot,
                run.StoppedAtUtc!.Value))]);
    }
}
