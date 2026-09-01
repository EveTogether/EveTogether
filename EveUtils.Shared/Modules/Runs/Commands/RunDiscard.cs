using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// What discarding one run does, in one place — so the local button and the fleet-wide fan-out can never drift into
/// two different ideas of how much a discard takes with it.
/// </summary>
internal static class RunDiscard
{
    /// <summary>
    /// Stop the activity and unlink the group, and nothing else. Rows hanging off the run are never touched, and a
    /// run that was already saved keeps its state and its timestamps: the FC ends the shared activity, but what a
    /// member already committed to their own history stays theirs (ET-105 AC-1).
    /// </summary>
    public static void Apply(Run run, DateTime stoppedAtUtc)
    {
        run.UnlinkFromGroup(recordFormerGroup: true);

        if (run.State is RunState.Running)
        {
            run.State = RunState.Stopped;
            run.StoppedAtUtc = stoppedAtUtc;
        }

        if (run.SyncState is not RunSyncState.Local)
            run.SyncState = RunSyncState.Pending;
        run.Revision++;
    }
}
