using System.Text.RegularExpressions;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed partial class LinkRunToGroupCodeCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<LinkRunToGroupCodeCommand, Result>
{
    public async Task<Result> Handle(LinkRunToGroupCodeCommand command, CancellationToken cancellationToken = default)
    {
        if (!GroupCodePattern().IsMatch(command.GroupCode))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A group code must use the format HF-7QK2.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().WithEverything().FirstOrDefaultAsync(candidate => candidate.Id == command.RunId, cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));
        if (run.GroupCode is { } existingGroupCode && !string.Equals(existingGroupCode, command.GroupCode,
                StringComparison.Ordinal))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A run is already linked to another group code.", "Runs"));

        if (run.GroupCode != command.GroupCode)
        {
            // I7 (ET-274): the character may already have a run in the group it joins — the commander's list backfilled
            // one while this run was still under a code of its own. That run is folded into this one, the run the pilot
            // is actually flying, rather than standing beside it as a second.
            if (await db.Set<Run>().WithEverything().FirstOrDefaultAsync(candidate => candidate.GroupCode == command.GroupCode
                        && candidate.CharacterId == run.CharacterId && !candidate.DeletedAtUtc.HasValue, cancellationToken)
                    is { } already)
            {
                OneRunPerCharacter.Merge(db, run, already, DateTime.UtcNow);
                // The fold is two writes: the one it replaces leaves the group before this one takes its place in it.
                await db.SaveChangesAsync(cancellationToken);
            }

            run.GroupCode = command.GroupCode;
            run.Revision++;
        }
        if (command.FleetId is { } fleetId)
            await RunGroupOriginRecorder.RecordAsync(db, command.GroupCode, fleetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }

    [GeneratedRegex("^[A-Z]{2}-[A-Z0-9]{4}$")]
    private static partial Regex GroupCodePattern();
}
