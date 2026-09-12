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
internal sealed class SetHomefrontPayoutCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
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

        DateTime nowUtc = DateTime.UtcNow;
        // Replaces rather than adds: a second confirm or a corrected typed amount must never leave an earlier
        // FixedPayout row standing beside it, or RewardIskContributor would sum both for the same character.
        db.Set<RunParameter>().RemoveRange(run.Parameters.Where(parameter => parameter.ParameterKey == RunParameterKey.FixedPayout));
        db.Set<RunParameter>().Add(new RunParameter
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            ParameterKey = RunParameterKey.FixedPayout,
            TypedValue = command.AmountIsk.ToString("0.##"),
            Amount = command.AmountIsk,
            ObservedAtUtc = nowUtc
        });

        // ET-215's rule for a change after the fact, the same one SetRunAttendanceCommandHandler follows.
        run.Revision++;
        if (run.SyncState is RunSyncState.Synced)
            run.SyncState = RunSyncState.Outdated;

        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
