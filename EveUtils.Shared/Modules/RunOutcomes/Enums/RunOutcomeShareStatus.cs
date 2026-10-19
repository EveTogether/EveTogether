namespace EveUtils.Shared.Modules.RunOutcomes.Enums;

/// <summary>What a share or a retraction did to the stored outcome of one activity (ET-514).</summary>
public enum RunOutcomeShareStatus
{
    Stored,
    Updated,
    Removed,

    /// <summary>The revision is not newer than the one stored; nothing changed.</summary>
    Stale,

    /// <summary>A fleet mate shared this activity first. One outcome per activity: the first sharer's stays.</summary>
    SharedByAnother,

    /// <summary>A retraction for an activity that has no stored outcome.</summary>
    NotStored
}
