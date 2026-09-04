using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>The lock every loot write holds, written once. Saving is that lock, and it lives in the commands rather
/// than in the controls a window happens to be showing — ET-179 saves a run left standing with no window open at
/// all.</summary>
internal static class RunLootWrites
{
    public static async Task<Result<Run>> OpenRunAsync(ClientDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == runId && !candidate.DeletedAtUtc.HasValue, cancellationToken);
        if (run is null)
            return Result<Run>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        return run.State is RunState.Saved
            ? Result<Run>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "This run is saved, so its loot can no longer be changed.", "Runs"))
            : Result<Run>.Success(run);
    }
}
