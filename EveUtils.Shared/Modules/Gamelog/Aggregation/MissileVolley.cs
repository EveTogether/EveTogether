namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// One volley of a character's launchers with one missile type, as their fit, skills and implants make it (ET-282):
/// the damage of every launcher that fires it together, before a target's resists, and the explosion radius and
/// velocity the missile leaves with.
/// </summary>
public sealed record MissileVolley(double Damage, double ExplosionRadius, double ExplosionVelocity);
