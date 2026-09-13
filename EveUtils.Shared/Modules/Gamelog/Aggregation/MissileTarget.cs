using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// What a target brings to a missile hit (ET-282), for an NPC the log names by its type: its signature, its top speed
/// and the resonances of each layer that has hit points. A player ship is not a type name and gets none of this — its
/// resists come from a fit nobody here can see.
///
/// The log never says which layer a hit landed on, so a missile is measured against the layer that takes the least of
/// it: a full hit on any layer then reads as a full hit, and only a hit that has already reached a weaker layer can read
/// high. On the Offertory Sigil the difference is ×0.9 shield and armour against ×1.0 hull; the engagement is spent on
/// armour.
/// </summary>
public sealed record MissileTarget(string Name, double Signature, double MaxVelocity, IReadOnlyList<LayerResonances> Layers)
{
    // (hit points, em, thermal, kinetic, explosive resonance) per layer: shield, armour, hull.
    private static readonly int[][] LayerAttributes =
    [
        [263, 271, 274, 273, 272],
        [265, 267, 270, 269, 268],
        [9, 113, 110, 109, 111],
    ];

    private const int SignatureRadius = 552;
    private const int MaxVelocityAttribute = 37;

    public static MissileTarget? Find(ISdeAccessor sde, string name)
    {
        if (!sde.IsAvailable || !sde.TryGetTypeId(name, out var typeId))
            return null;

        var attributes = sde.GetDogmaAttributes(typeId).ToDictionary(attribute => attribute.AttributeId, attribute => attribute.Value);
        // A resonance the SDE leaves out is its default, 1.0: no resist on that layer.
        var layers = LayerAttributes
            .Where(layer => attributes.GetValueOrDefault(layer[0]) > 0)
            .Select(layer => new LayerResonances(
                attributes.GetValueOrDefault(layer[1], 1), attributes.GetValueOrDefault(layer[2], 1),
                attributes.GetValueOrDefault(layer[3], 1), attributes.GetValueOrDefault(layer[4], 1)))
            .ToList();
        if (layers.Count == 0 || !attributes.TryGetValue(SignatureRadius, out var signature))
            return null;

        return new MissileTarget(name, signature, attributes.GetValueOrDefault(MaxVelocityAttribute), layers);
    }

    /// <summary>The share of this missile's damage the target's most resistant layer lets through.</summary>
    public double StrongestLayer(MissileCharge missile) => Layers.Min(missile.Through);

    /// <summary>The share its least resistant layer lets through.</summary>
    public double WeakestLayer(MissileCharge missile) => Layers.Max(missile.Through);

    /// <summary>
    /// The missile lands its whole damage on this target even at the target's top speed: its signature is no smaller
    /// than the explosion, and it cannot outrun the explosion velocity that signature allows. Radius and velocity are
    /// the missile's own, before skills, which only ever make it apply better — so this errs on the safe side.
    /// </summary>
    public bool TakesFullDamageFrom(MissileCharge missile) =>
        missile.ExplosionRadius > 0 && Signature >= missile.ExplosionRadius
        && MaxVelocity <= Signature / missile.ExplosionRadius * missile.ExplosionVelocity;
}
