using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>The one place that answers "which run is running right now" — shared by whatever attaches loot to it
/// (<see cref="AddRunLootCaptureCommandHandler"/>) and whatever reads it back (<c>GetRunningRunLootQueryHandler</c>),
/// so the "exactly one" rule and its filters can't drift apart between the two.</summary>
internal static class RunningRunLookup
{
    /// <summary><see cref="Run"/> is null when the count isn't exactly one; the caller phrases the failure for its
    /// own context (recording vs. showing), because "no run" and "which one" only differ in that.</summary>
    public static async Task<(Run? Run, int RunningCount)> FindAsync(ClientDbContext db, CancellationToken cancellationToken)
    {
        List<Run> running = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Running && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        return (running.Count == 1 ? running[0] : null, running.Count);
    }
}
