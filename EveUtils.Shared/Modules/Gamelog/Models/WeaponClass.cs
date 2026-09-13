namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>What kind of weapon wrote an outgoing damage line, which decides what its hit-quality word can say.</summary>
public enum WeaponClass
{
    /// <summary>Not resolved (no SDE yet, or a name it does not know).</summary>
    Unknown,

    /// <summary>A turret module; a grouped turret writes one line per group volley.</summary>
    Turret,

    /// <summary>A drone or fighter, one line per drone per shot.</summary>
    Drone,

    /// <summary>A missile, rocket, torpedo or bomb: the line names the charge, and its word is always "Hits".</summary>
    Missile,
}
