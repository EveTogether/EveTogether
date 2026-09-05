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
    /// <param name="preferredRunId">The open activity window's own run (<c>IDialogService.ActivityWindowRunId</c>
    /// on the client), if the caller has one. Answered outright when it is still open, before the run/no-run counting below ever
    /// runs: a stray second site copied over a running one (ET-190, c69fec1) stops the pilot's own run to wait on
    /// SAVE/DISCARD/KEEP, sometimes for minutes, and any old Stopped-and-never-saved run left over from an earlier
    /// session (Raymond, 2026-09-04) then makes it one Stopped candidate among several — "N runs are running" on
    /// loot that has an unambiguous owner right there in the open window. This is that answer, not a guess: only one
    /// activity window is ever open at a time (ET-98), so the run it is on is never one of two candidates to choose
    /// between — but ET-130 (multiple concurrent runs, one per character) removes that guarantee, and this hint
    /// becomes ambiguous itself the day it lands.</param>
    public static async Task<(Run? Run, int RunningCount)> FindAsync(
        ClientDbContext db, CancellationToken cancellationToken, bool includeStopped = false, Guid? preferredRunId = null)
    {
        List<Run> open = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => !run.DeletedAtUtc.HasValue
                          && (run.State == RunState.Running || (includeStopped && run.State == RunState.Stopped)))
            .ToListAsync(cancellationToken);

        if (preferredRunId is { } preferred && open.FirstOrDefault(run => run.Id == preferred) is { } known)
            return (known, 1);

        // A run on the clock wins over one only stopped: the moment a NEXT run is running, that is the one a copy
        // belongs to — otherwise every run stopped and not yet saved would go on competing for the pilot's loot.
        List<Run> running = [.. open.Where(run => run.State == RunState.Running)];
        List<Run> candidates = running.Count > 0 ? running : open;
        return (candidates.Count == 1 ? candidates[0] : null, candidates.Count);
    }
}
