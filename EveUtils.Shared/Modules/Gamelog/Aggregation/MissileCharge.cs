using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// A missile type as the SDE describes it before any skill or fit touches it (ET-282): its damage per type, which only
/// matters here as the mix a target's resists apply to, and how its explosion applies — radius, velocity and the
/// exponent that softens a target outrunning it.
/// </summary>
public sealed record MissileCharge(double Em, double Thermal, double Kinetic, double Explosive,
    double ExplosionRadius, double ExplosionVelocity, double DamageReductionFactor)
{
    private const int DamageReductionFactorAttribute = 1353;

    public static MissileCharge? Find(ISdeAccessor sde, string name)
    {
        if (!sde.IsAvailable || !sde.TryGetTypeId(name, out var typeId))
            return null;

        var attributes = sde.GetDogmaAttributes(typeId).ToDictionary(attribute => attribute.AttributeId, attribute => attribute.Value);
        var charge = new MissileCharge(
            attributes.GetValueOrDefault(DogmaAttributeIds.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeIds.ThermalDamage),
            attributes.GetValueOrDefault(DogmaAttributeIds.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeIds.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeIds.ExplosionRadius),
            attributes.GetValueOrDefault(DogmaAttributeIds.ExplosionVelocity),
            attributes.GetValueOrDefault(DamageReductionFactorAttribute, 1));
        return charge.Em + charge.Thermal + charge.Kinetic + charge.Explosive > 0 ? charge : null;
    }

    /// <summary>What share of this missile's damage a layer with these resonances lets through.</summary>
    public double Through(LayerResonances layer) =>
        (Em * layer.Em + Thermal * layer.Thermal + Kinetic * layer.Kinetic + Explosive * layer.Explosive)
        / (Em + Thermal + Kinetic + Explosive);
}
