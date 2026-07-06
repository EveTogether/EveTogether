namespace EveUtils.Shared.Modules.Sde.Fighters;

/// <summary>
/// A fighter type (category 87) as the fit simulator consumes it: its class, squadron size and role, the bay volume one
/// fighter occupies and whether it carries a weapon. <see cref="Kind"/> and <see cref="IsStructureFighter"/> are derived
/// from the inventory group (the Standup structure variants carry no IsLight/IsSupport/IsHeavy flags, so the group — not
/// the flags — is the authoritative source). <see cref="SquadronMaxSize"/> is the squadron's fighter count (attr 2215,
/// fighter-owned, never a ship attribute) and multiplies a squadron's DPS. <see cref="Volume"/> is one fighter's volume
/// (the type's intrinsic volume, not a dogma attribute on these types) — bay use is volume × squadron size against the
/// platform's fighter capacity. <see cref="DealsDamage"/> is false for support and superiority squadrons (no attack
/// multiplier), which surface EWAR/utility info instead of DPS.
/// </summary>
public sealed record FighterType(
    int TypeId,
    string Name,
    FighterKind Kind,
    bool IsStructureFighter,
    int SquadronMaxSize,
    FighterRole Role,
    double Volume,
    bool DealsDamage);
