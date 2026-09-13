namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// What one missile's volleys at one target are measured against (ET-282). <see cref="FullDamage"/> is a full volley
/// after the target's resists — null while there is nothing to measure against yet. <see cref="Target"/> is null when
/// the target is not an NPC type, whose resists nobody here knows.
/// </summary>
public sealed record MissileReference(MissileTarget? Target, double? FullDamage, double ExplosionRadius,
    double ExplosionVelocity, double DamageReductionFactor)
{
    /// <summary>The most the missile can land on this target standing still: its signature against the explosion
    /// radius, 1 when the target is at least as big.</summary>
    public double SignatureCeiling =>
        Target is null || ExplosionRadius <= 0 ? 1 : Math.Min(1, Target.Signature / ExplosionRadius);

    /// <summary>The most it can land with the target at its top speed: the explosion also has to keep up with it.</summary>
    public double TopSpeedCeiling =>
        Target is not { MaxVelocity: > 0 } target || ExplosionRadius <= 0
            ? SignatureCeiling
            : Math.Min(SignatureCeiling, Math.Pow(
                target.Signature / ExplosionRadius * ExplosionVelocity / target.MaxVelocity, DamageReductionFactor));
}
