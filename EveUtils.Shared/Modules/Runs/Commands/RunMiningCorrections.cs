using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>What the two mining corrections share (ET-424): the run is opened the way a loot correction opens it, and a
/// change on a saved run is marked, rebuilt into its activity summary and signalled once the write has landed.</summary>
internal static class RunMiningCorrections
{
    public static async Task<Result> ApplyAsync(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus,
        IDispatcher dispatcher, Guid runId, string oreType, Func<RunMiningEntry, ClientDbContext, Result> change,
        CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Result<Run> opened = await RunLootWrites.OpenForCorrectionAsync(db, runId, cancellationToken);
        if (!opened.IsSuccess || opened.Value is not { } run)
            return Result.Failure([.. opened.Messages]);

        RunMiningEntry? entry = await db.Set<RunMiningEntry>()
            .FirstOrDefaultAsync(candidate => candidate.RunId == runId && candidate.OreType == oreType, cancellationToken);
        if (entry is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                $"This run has no {oreType} line any more.", "Runs"));

        Result changed = change(entry, db);
        if (!changed.IsSuccess)
            return changed;

        bool isSaved = run.State is RunState.Saved;
        if (isSaved)
            RunLootWrites.MarkCorrected(run);

        await db.SaveChangesAsync(cancellationToken);
        if (isSaved)
            await dispatcher.Send(new RebuildActivitySummariesCommand(runId), cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
