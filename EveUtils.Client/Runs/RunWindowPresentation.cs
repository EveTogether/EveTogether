namespace EveUtils.Client.Runs;

/// <summary>Who asked for the activity window to come up.</summary>
public enum RunWindowOpenTrigger
{
    /// <summary>This pilot clicked something. They are looking at the app, so taking focus is what they asked for.</summary>
    LocalUser,

    /// <summary>The fleet commander started the shared run and this window is coming up on someone else's machine
    /// (ET-105 AC-2). That pilot may be mid-fight in EVE, and a window that grabs the keyboard there costs a ship.</summary>
    RemoteFleetCommander,

    /// <summary>A copied combat-site signature opened this window and started its run by itself (ET-158). The whole
    /// point is that the pilot never leaves EVE, so this must not take the keyboard either.</summary>
    CopiedSignature
}

public enum RunWindowActivation
{
    /// <summary>Show and take focus.</summary>
    Activate,

    /// <summary>Put it on screen behind whatever has focus, and leave the keyboard where it is.</summary>
    ShowWithoutActivating,

    /// <summary>It is already up. Do nothing at all — re-showing or raising it would reach for focus.</summary>
    LeaveAsIs
}

/// <summary>
/// Whether the activity window may take the keyboard. The one place this is decided, so no caller can quietly reach
/// for <c>Activate()</c> on the remote path.
/// </summary>
public static class RunWindowPresentation
{
    public static RunWindowActivation Decide(RunWindowOpenTrigger trigger, bool isAlreadyOpen) =>
        trigger is RunWindowOpenTrigger.LocalUser
            ? RunWindowActivation.Activate
            : isAlreadyOpen
                ? RunWindowActivation.LeaveAsIs
                : RunWindowActivation.ShowWithoutActivating;
}
