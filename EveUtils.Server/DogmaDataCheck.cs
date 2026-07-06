using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Headless real-SDE proof of <see cref="IDogmaDataAccessor"/> (<c>--dogma-data-test</c>):
/// ensures the store exists, then reads the Rifter and the two VC-01 magnetic field stabilizers and asserts base
/// attributes, effects, parsed modifierInfo, attribute flags and the type-&gt;category chain against the live static
/// data (build 3374020). Finally it builds a cold accessor and verifies <see cref="IDogmaDataAccessor.PrefetchAsync"/>
/// warms the same values from in-memory caches. Exit 0 = pass, 1 = fail.
/// </summary>
public static class DogmaDataCheck
{
    private const int Rifter = 587;
    private const int MagneticFieldStabilizerIi = 10190;
    private const int VortexCompactMfs = 11105;
    private const int DamageMultiplierAttribute = 64;
    private const int HybridWeaponDamageMultiplyEffect = 93;
    private const int HybridWeaponGroup = 74;
    private const int WarpScrambleBlockMwdEffect = 5934;   // carries an EffectStopper modifier
    private const int ShipCategory = 6;
    private const int ModuleCategory = 7;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE Together dogma-data check (IDogmaDataAccessor against the real SDE) ==");

        var importResult = await services.GetRequiredService<ISdeImporter>().EnsureUpToDateAsync();
        if (!importResult.Success)
        {
            Console.WriteLine($"FAIL: SDE not available ({importResult.Error})");
            return 1;
        }

        var dogma = services.GetRequiredService<IDogmaDataAccessor>();
        var ok = true;

        // --- attribute metadata: damageMultiplier (64) is penalised (non-stackable) and high-is-good ---
        var damageMultiplier = dogma.GetAttributeMeta(DamageMultiplierAttribute);
        ok &= Check(damageMultiplier is not null, "damageMultiplier (64) meta resolves");
        if (damageMultiplier is not null)
        {
            ok &= Check(Math.Abs(damageMultiplier.DefaultValue - 1.0) < 1e-9, $"damageMultiplier default 1.0 (got {damageMultiplier.DefaultValue})");
            ok &= Check(!damageMultiplier.Stackable, "damageMultiplier is non-stackable (penalised)");
            ok &= Check(damageMultiplier.HighIsGood, "damageMultiplier highIsGood");
        }
        ok &= Check(dogma.GetAttributeMeta(int.MaxValue) is null, "unknown attribute -> null");

        // --- Rifter: base attributes, effects and the type->category chain ---
        ok &= Check(dogma.GetBaseAttributes(Rifter).Count > 0, "Rifter (587) has base attributes");
        ok &= Check(dogma.GetTypeEffects(Rifter).Count > 0, "Rifter (587) has effects");
        ok &= Check(dogma.GetCategoryId(Rifter) == ShipCategory, $"Rifter category -> Ship (6), got {dogma.GetCategoryId(Rifter)}");

        // --- the magnetic field stabilizer carries the damage-multiply effect, parsed correctly ---
        ok &= Check(dogma.GetTypeEffects(MagneticFieldStabilizerIi).Any(e => e.EffectId == HybridWeaponDamageMultiplyEffect),
            "MFS II carries hybridWeaponDamageMultiply (93)");
        ok &= Check(dogma.GetCategoryId(MagneticFieldStabilizerIi) == ModuleCategory, "MFS II category -> Module (7), not penalty-exempt");

        var effect = dogma.GetEffect(HybridWeaponDamageMultiplyEffect);
        ok &= Check(effect is not null, "effect 93 resolves");
        if (effect is not null)
        {
            ok &= Check(effect.EffectCategoryId == 4, $"effect 93 activation state -> online (4), got {effect.EffectCategoryId}");
            ok &= Check(effect.Modifiers.Count == 1, $"effect 93 has 1 modifier, got {effect.Modifiers.Count}");
            var modifier = effect.Modifiers[0];
            ok &= Check(modifier.Func == ModifierFunc.LocationGroupModifier, $"modifier func -> LocationGroupModifier, got {modifier.Func}");
            ok &= Check(modifier.Domain == ModifierDomain.ShipId, $"modifier domain -> ShipId, got {modifier.Domain}");
            ok &= Check(modifier.Operation == 4, $"modifier operation -> 4 (PostMul), got {modifier.Operation}");
            ok &= Check(modifier.ModifiedAttributeId == DamageMultiplierAttribute, "modifier modifies damageMultiplier (64)");
            ok &= Check(modifier.ModifyingAttributeId == DamageMultiplierAttribute, "modifier reads damageMultiplier (64)");
            ok &= Check(modifier.GroupId == HybridWeaponGroup, $"modifier targets hybrid group (74), got {modifier.GroupId}");
            ok &= Check(modifier.SkillTypeId is null, "modifier has no skillTypeId");
        }

        // --- VC-01: both stabilizers share the same effect -> one stacking bucket (pass-3 acceptance later) ---
        ok &= Check(dogma.GetTypeEffects(VortexCompactMfs).Any(e => e.EffectId == HybridWeaponDamageMultiplyEffect),
            "Vortex Compact MFS carries the same effect 93 (shared attr x domain stacking bucket)");
        ok &= Check(dogma.GetCategoryId(VortexCompactMfs) == ModuleCategory, "Vortex Compact MFS category -> Module (7)");

        // --- EffectStopper parses to a no-operation, no-attribute modifier ---
        var stopperHost = dogma.GetEffect(WarpScrambleBlockMwdEffect);
        ok &= Check(stopperHost is not null, "effect 5934 resolves");
        var stopper = stopperHost?.Modifiers.FirstOrDefault(m => m.Func == ModifierFunc.EffectStopper);
        ok &= Check(stopper is not null, "effect 5934 carries an EffectStopper modifier");
        if (stopper is not null)
        {
            ok &= Check(stopper.Operation == ModifierInfo.NoOperation, "EffectStopper has no operation");
            ok &= Check(stopper.ModifiedAttributeId is null, "EffectStopper modifies no attribute");
        }

        // --- batched prefetch warms the same values from a cold accessor (IO-free hot path) ---
        var options = services.GetRequiredService<SdeOptions>();
        var cold = new SqliteDogmaDataAccessor(options.DatabasePath);
        await cold.PrefetchAsync([Rifter, MagneticFieldStabilizerIi, VortexCompactMfs]);
        ok &= Check(cold.GetBaseAttributes(Rifter).Count == dogma.GetBaseAttributes(Rifter).Count, "after prefetch: Rifter base attrs match");
        ok &= Check(cold.GetEffect(HybridWeaponDamageMultiplyEffect)?.Modifiers.Count == 1, "after prefetch: effect 93 parsed");
        ok &= Check(cold.GetAttributeMeta(DamageMultiplierAttribute) is { Stackable: false }, "after prefetch: damageMultiplier meta cached");
        ok &= Check(cold.GetCategoryId(MagneticFieldStabilizerIi) == ModuleCategory, "after prefetch: MFS II category cached");

        Console.WriteLine(ok ? "\nPASS" : "\nFAIL");
        return ok ? 0 : 1;
    }

    private static bool Check(bool condition, string label)
    {
        Console.WriteLine($"  [{(condition ? "OK" : "XX")}] {label}");
        return condition;
    }
}
