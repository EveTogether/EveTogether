namespace EveUtils.Shared.Modules.Dogma;

/// <summary>What a fitted item contributes to the fit, so a per-module readout (the later tooltip) can pick the
/// right lines. <see cref="None"/> covers passive/utility modules with no derived contribution of their own.</summary>
public enum ModuleContributionKind
{
    None,
    Turret,
    Missile,
    Drone,
    Mining,
    Propulsion,
    LocalRepair,
    Capacitor,
    RemoteRepair,
    RemoteCapTransfer
}
