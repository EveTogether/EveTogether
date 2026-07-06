namespace EveUtils.Shared.Modules.Sde.Fighters;

/// <summary>The squadron role (attr 2270): drives eos' reload/numShots mapping for sustained DPS and separates
/// the damage-dealing attack roles from the no-weapon support and superiority (tackle) roles. Values match the raw
/// fighterSquadronRole attribute.</summary>
public enum FighterRole
{
    Unknown = 0,
    Superiority = 1,
    LightAttack = 2,
    Support = 3,
    HeavyAttack = 4,
    LongRange = 5
}
