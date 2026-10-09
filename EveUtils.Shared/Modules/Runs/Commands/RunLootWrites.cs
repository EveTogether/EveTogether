using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>How a loot write opens its run and marks a saved one corrected, written once. The rules live in the
/// commands rather than in the controls a window happens to be showing — ET-179 saves a run left standing with no
/// window open at all.</summary>
internal static class RunLootWrites
{
    /// <summary>The corrections a saved run still takes: leaving a capture out or counting it again, writing the list
    /// out by hand (ET-215), and pasting one of its two holds again (ET-488). Which capture is the starting hold stays
    /// fixed once the run is saved — <see cref="SetRunLootCaptureRoleCommandHandler"/> keeps that lock.</summary>
    public static async Task<Result<Run>> OpenForCorrectionAsync(ClientDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == runId && !candidate.DeletedAtUtc.HasValue, cancellationToken);
        return run is null
            ? Result<Run>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"))
            : Result<Run>.Success(run);
    }

    /// <summary>A saved run's loot changed after the fact. The revision moves so a server holding an older copy can
    /// tell the two apart, and a published run turns <see cref="RunSyncState.Outdated"/> rather than Pending: the
    /// server copy is only ever replaced when the pilot publishes again, never as a side effect of a later sync.</summary>
    public static void MarkCorrected(Run run)
    {
        run.Revision++;
        if (run.SyncState is RunSyncState.Synced)
            run.SyncState = RunSyncState.Outdated;
    }
}
