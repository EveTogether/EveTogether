namespace EveUtils.Client.ViewModels.Activity;

public enum ActivityRunState
{
    NotStarted,
    Running,

    /// <summary>The clock is stopped but the run is still open in the store — loot copied now still attaches to it,
    /// and it is SAVE or DISCARD that closes it.</summary>
    Stopped,

    /// <summary>Committed. Nothing more can be added to it, so only START is left.</summary>
    Saved,

    /// <summary>The fleet commander threw the shared run away while this window was on it. A member's state only:
    /// the commander's own window closes on its discard, so it is never left standing in this one (ET-155).</summary>
    Discarded
}
