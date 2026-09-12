namespace EveUtils.Shared.Modules.Runs.Enums;

public enum RunSyncState
{
    Local,
    Pending,
    Synced,

    /// <summary>Published, then corrected here (ET-215): the server's copy is behind and stays behind until the pilot
    /// publishes it again. Not <see cref="Pending"/> on purpose — a sync only pushes Pending runs, so a correction must
    /// never ride along on a publish the pilot started for a different activity. Not <see cref="Synced"/> either, or
    /// the next pull would lay the server's older copy back over the correction. A fleet run on its fleet's server does
    /// not wait for the pilot (ET-245): the automatic publisher queues it again right away, unless that is switched off.</summary>
    Outdated
}
