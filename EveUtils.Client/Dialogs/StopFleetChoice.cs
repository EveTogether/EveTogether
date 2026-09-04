namespace EveUtils.Client.Dialogs;

/// <summary>
/// The three ways out of an active fleet, plus backing out (ET-166). Deliberately one enum and one dialog: the
/// point of the screen is that these are different acts with different consequences, which is only visible when
/// they are offered side by side. Deleting the fleet is NOT among them — that is Disband, it lives on the fleet
/// overview, and putting it in this window is exactly what made stopping feel dangerous.
/// </summary>
public enum StopFleetChoice
{
    /// <summary>Back out; the fleet keeps running.</summary>
    Cancel,

    /// <summary>Active → Forming. Reversible: the roster stays and the fleet starts again next time.</summary>
    Stop,

    /// <summary>→ Concluded. Terminal: kept for history, never started or joined again.</summary>
    Conclude,

    /// <summary>Pull one of my own characters out and leave the fleet running for everyone else.</summary>
    LeaveOnly
}
