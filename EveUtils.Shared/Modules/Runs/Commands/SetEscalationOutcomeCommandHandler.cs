using System.Globalization;
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
internal sealed class SetEscalationOutcomeCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<SetEscalationOutcomeCommand, Result>
{
    public async Task<Result> Handle(SetEscalationOutcomeCommand command, CancellationToken cancellationToken = default)
    {
        if (command.CompletedByRunId is not null && command.Outcome is not EscalationOutcome.Completed)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Only a completed escalation can name the run that flew it.", "Runs"));

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

        List<RunParameter> previous = [.. entry.Where(parameter => parameter.ParameterKey
            is RunParameterKey.EscalationOutcome or RunParameterKey.EscalationCompletedByRunId)];
        string? previousOutcome = previous
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationOutcome)?.TypedValue;
        string? previousCompletedBy = previous
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationCompletedByRunId)?.TypedValue;
        string? outcome = command.Outcome is { } chosen ? ((int)chosen).ToString(CultureInfo.InvariantCulture) : null;
        string? completedBy = command.CompletedByRunId?.ToString();
        if (previousOutcome == outcome && previousCompletedBy == completedBy)
            return Result.Success();

        db.Set<RunParameter>().RemoveRange(previous);
        DateTime nowUtc = DateTime.UtcNow;
        if (outcome is not null)
            db.Set<RunParameter>().Add(_Row(run.Id, command.EntryId, RunParameterKey.EscalationOutcome, outcome, nowUtc));
        if (completedBy is not null)
            db.Set<RunParameter>().Add(
                _Row(run.Id, command.EntryId, RunParameterKey.EscalationCompletedByRunId, completedBy, nowUtc));

        // ET-215's rule for a change after the fact, the same one SetHomefrontPayoutCommandHandler follows.
        run.Revision++;
        if (run.SyncState is RunSyncState.Synced)
            run.SyncState = RunSyncState.Outdated;

        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }

    private static RunParameter _Row(Guid runId, Guid? entryId, RunParameterKey key, string value, DateTime nowUtc) => new()
    {
        Id = Guid.CreateVersion7(),
        RunId = runId,
        ParameterKey = key,
        TypedValue = value,
        EntryId = entryId,
        ObservedAtUtc = nowUtc
    };
}
