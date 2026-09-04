using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>The one place that answers "which run is running right now", for the one caller that has no better
/// question to ask: <see cref="AddRunLootCaptureCommandHandler"/>, because a clipboard copy names no run. Reading
/// loot back does NOT come through here any more — a window knows the run it is on, and asking this instead meant
/// eleven runs stopped-and-never-saved made its own loot unreadable (Raymond, 2026-09-04).</summary>
internal static class RunningRunLookup
{
    /// <summary><see cref="Run"/> is null when the count isn't exactly one; the caller phrases the failure for its
    /// own context (recording vs. showing), because "no run" and "which one" only differ in that.</summary>
    /// <param name="includeStopped">Also answer with a run whose clock is at rest but which is still open — what
    /// loot needs, because it is copied out of the wreck after the last rat and belongs to the run that produced it.
    /// A window opening asks WITHOUT it: a stopped run is exactly what it must not adopt, or a pilot who closes his
    /// window gets yesterday's run back with its site, its start and its commander's group code.</param>
    public static async Task<(Run? Run, int RunningCount)> FindAsync(
        ClientDbContext db, CancellationToken cancellationToken, bool includeStopped = false)
    {
        List<Run> open = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => !run.DeletedAtUtc.HasValue
                          && (run.State == RunState.Running || (includeStopped && run.State == RunState.Stopped)))
            .ToListAsync(cancellationToken);

        // A run on the clock wins over one only stopped: the moment a NEXT run is running, that is the one a copy
        // belongs to — otherwise every run stopped and not yet saved would go on competing for the pilot's loot.
        List<Run> running = [.. open.Where(run => run.State == RunState.Running)];
        List<Run> candidates = running.Count > 0 ? running : open;
        return (candidates.Count == 1 ? candidates[0] : null, candidates.Count);
    }
}
