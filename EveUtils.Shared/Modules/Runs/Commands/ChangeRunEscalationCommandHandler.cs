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
internal sealed class ChangeRunEscalationCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<ChangeRunEscalationCommand, Result>
{
    private static readonly RunParameterKey[] _identityKeys =
    [
        RunParameterKey.Escalation, RunParameterKey.EscalationDungeonId, RunParameterKey.EscalationSystem,
        RunParameterKey.EscalationSolarSystemId, RunParameterKey.EscalationExpiresAtUtc
    ];

    public async Task<Result> Handle(ChangeRunEscalationCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.SiteName))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "An escalation needs a site.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().Include(candidate => candidate.Parameters)
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue,
                cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        List<RunParameter> entry = [.. run.Parameters.Where(parameter => parameter.EntryId == command.EntryId)];
        if (!entry.Any(parameter => parameter.ParameterKey == RunParameterKey.Escalation))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "This run has no such escalation.", "Runs"));

        db.Set<RunParameter>().RemoveRange(entry.Where(parameter => _identityKeys.Contains(parameter.ParameterKey)));
        db.Set<RunParameter>().AddRange(RunEscalations
            .Rows(command.EntryId, command.SiteName.Trim(), command.DungeonId, command.DestinationSystem,
                command.DestinationSolarSystemId, command.ExpiresAtUtc, DateTime.UtcNow)
            .Select(row => new RunParameter
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                ParameterKey = row.ParameterKey,
                TypedValue = row.TypedValue,
                EntryId = row.EntryId,
                ObservedAtUtc = row.ObservedAtUtc
            }));

        // ET-215's rule for a change after the fact, the same one RegisterRunEscalationCommandHandler follows.
        run.Revision++;
        if (run.SyncState is RunSyncState.Synced)
            run.SyncState = RunSyncState.Outdated;

        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
