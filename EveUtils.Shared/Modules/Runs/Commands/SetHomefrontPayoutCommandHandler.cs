using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetHomefrontPayoutCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<SetHomefrontPayoutCommand, Result>
{
    public async Task<Result> Handle(SetHomefrontPayoutCommand command, CancellationToken cancellationToken = default)
    {
        if (command.AmountIsk < 0)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A homefront payout cannot be negative.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().Include(candidate => candidate.Parameters)
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue,
                cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        // Replaces rather than adds: a corrected typed amount must never leave an earlier FixedPayout row standing
        // beside it, or the same character would be paid twice.
        db.Set<RunParameter>().RemoveRange(run.Parameters.Where(parameter => parameter.ParameterKey == RunParameterKey.FixedPayout));
        if (command.AmountIsk is { } amount)
            db.Set<RunParameter>().Add(new RunParameter
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                ParameterKey = RunParameterKey.FixedPayout,
                TypedValue = amount.ToString("0.##"),
                Amount = amount,
                ObservedAtUtc = DateTime.UtcNow
            });

        // ET-215's rule for a change after the fact, the same one SetRunAttendanceCommandHandler follows.
        run.Revision++;
        if (run.SyncState is RunSyncState.Synced)
            run.SyncState = RunSyncState.Outdated;

        await db.SaveChangesAsync(cancellationToken);
        // A saved activity's stored total is what the overview, the month bar and the detail read (ET-271).
        if (run.State is RunState.Saved)
            await dispatcher.Send(new RebuildActivitySummariesCommand(run.Id), cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
