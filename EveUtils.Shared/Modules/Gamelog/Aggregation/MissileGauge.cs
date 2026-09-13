using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// The yardstick for one character's missiles (ET-282): what a full volley of each missile type does to the target it
/// is shot at. A missile has no damage roll and always logs "Hits", so the damage of the volley is the only signal of
/// how it landed, and it only means something next to a full volley.
///
/// The full volley comes from the fit when the ship's fit is known (ET-101): its launchers, the character's skills and
/// implants, through the dogma engine. On Jithran's HAM Caracal that is 1,324.8 a volley; his log reads 1,325 on the
/// Offertory Sigil's hull and 1,192 — ×0.9 — on its armour, volley after volley. Without a fit the volley is learned:
/// the best volley at a target the missile fully applies to, divided by that target's least resistant layer, which can
/// only sit at or under the true volley. A learned volley above the fit's also wins, since then the detected fit is not
/// what is flying.
///
/// Learning per missile type rather than per target type is deliberate: a HAM can never land fully on a frigate, so a
/// frigate's best hit is itself a bad one, and a yardstick learned from it would call 14 % a sweet spot. Nothing is kept
/// across sessions either: a stored volley from another ship or fit would be wrong exactly where nothing corrects it.
///
/// Not thread-safe: the owning <see cref="WeaponApplicationTracker"/> calls it under its lock.
/// </summary>
public sealed class MissileGauge(Func<ISdeAccessor?> sde, Func<string, MissileVolley?> fitted)
{
    private readonly Dictionary<string, MissileCharge?> _charges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MissileTarget?> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _learned = new(StringComparer.Ordinal);

    /// <summary>A volley (or the part of it logged so far) landed on a target.</summary>
    public void Learn(string missile, string target, double volley)
    {
        if (_Charge(missile) is not { } charge || _Target(target) is not { } npc || !npc.TakesFullDamageFrom(charge))
            return;

        var beforeResists = volley / npc.WeakestLayer(charge);
        if (beforeResists > _learned.GetValueOrDefault(missile))
            _learned[missile] = beforeResists;
    }

    public MissileReference Reference(string missile, string target)
    {
        var charge = _Charge(missile);
        var npc = _Target(target);
        var fit = fitted(missile);
        double? learned = _learned.TryGetValue(missile, out var value) ? value : null;
        var volley = fit is null ? learned : Math.Max(fit.Damage, learned ?? 0);
        var full = charge is null || npc is null || volley is not > 0 ? null : volley * npc.StrongestLayer(charge);

        return new MissileReference(npc, full,
            fit?.ExplosionRadius ?? charge?.ExplosionRadius ?? 0,
            fit?.ExplosionVelocity ?? charge?.ExplosionVelocity ?? 0,
            charge?.DamageReductionFactor ?? 1);
    }

    private MissileCharge? _Charge(string name) => _Cached(_charges, name, MissileCharge.Find);

    private MissileTarget? _Target(string name) => _Cached(_targets, name, MissileTarget.Find);

    // A miss is remembered only once the SDE could have answered: before it is built, every name is unknown.
    private T? _Cached<T>(Dictionary<string, T?> cache, string name, Func<ISdeAccessor, string, T?> find) where T : class
    {
        if (cache.TryGetValue(name, out var known))
            return known;
        if (sde() is not { IsAvailable: true } store)
            return null;
        return cache[name] = find(store, name);
    }
}
