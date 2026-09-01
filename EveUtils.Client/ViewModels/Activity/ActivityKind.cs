namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// What the activity window is watching. The two behave differently enough that almost every readout branches on
/// it: an abyssal run has a shared deadline but no solar system and no bounty, while a site runs open-ended in a
/// system that can be named.
/// </summary>
public enum ActivityKind
{
    Abyssal,
    Site
}
