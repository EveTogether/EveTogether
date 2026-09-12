namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Which payout curve a homefront kind pays from (domain/homefronts.md §3.2) — four kinds share one curve
/// each, never one curve per kind: CCP scales the fixed amount by N the same way for every 5-pilot combat site.</summary>
public enum HomefrontCurve
{
    /// <summary>Raid, Dread Assault, Emergency Aid, Suspicious Signal.</summary>
    FivePerson,

    /// <summary>Salvage Research, Stabilize Rift, Traffic Stop.</summary>
    ThreePerson,

    /// <summary>Metaliminal Meteoroid.</summary>
    Metaliminal,

    /// <summary>Abyssal Artifact Recovery — paid per wave, not once at completion.</summary>
    Aar
}
