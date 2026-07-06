using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The Reactive Armor Hardener: an active module that, each cycle, shifts armour resistance away from the two
/// least-hit damage types toward the two most-hit, until the profile repeats; the averaged loop is its resist
/// profile. CCP leaves its effect (4928 <c>adaptiveArmorHardener</c>) without <c>modifierInfo</c>, so — like turret
/// or drone DPS — it is a code aggregate of pipeline-resolved inputs rather than a data-driven modifier. Ported from
/// the standard handler against the uniform 25/25/25/25 damage profile (the EHP convention). Applied after the other
/// modules are collected (it reads their effect on the armour resonances) and before derived stats: it pre-multiplies
/// each armour resonance by the adapted profile, stacking-penalised in the same group as Damage Control / bastion.
/// </summary>
public sealed class ReactiveArmorHardener(IDogmaDataAccessor data, DogmaEvaluator evaluator) : ISingletonService
{
    private const int AdaptiveArmorHardenerEffectId = 4928;
    private const int ResistanceShiftAmountId = 1849;
    private const int CycleLimit = 50;
    private const double UniformDamage = 25.0;
    private const double LoopTolerance = 1e-6;

    public void Apply(DogmaFit fit)
    {
        var hardener = fit.Modules.FirstOrDefault(IsReactiveArmorHardener);
        if (hardener is null || hardener.State < ModuleState.Active)
            return;

        var resonances = DogmaAttributeIds.ArmorResonance;
        // The armour resonance from every other source (the RAH effect itself is empty) — what gets through before the
        // RAH adapts. The simulation runs off this, reading the modified item attributes before applying the RAH.
        var baseResonance = resonances.Select(attribute => evaluator.Resolve(fit.Ship, attribute)).ToArray();
        var shift = evaluator.Resolve(hardener, ResistanceShiftAmountId) / 100.0;
        var hardenerResonance = resonances.Select(attribute => evaluator.Resolve(hardener, attribute)).ToArray();

        var adapted = Simulate(baseResonance, hardenerResonance, shift);

        for (var i = 0; i < resonances.Length; i++)
        {
            // Carry the adapted multiplier on the RAH item so it resolves like any other source, then apply it to the
            // ship's resonance as a stacking-penalised pre-multiply and drop the value resolved above so the derived
            // pass recomputes it with the RAH in the stack.
            hardener.Force(resonances[i], adapted[i]);
            var shipResonance = fit.Ship.GetOrAdd(resonances[i], 1.0);
            shipResonance.AddModifier(new Modifier(EffectOperator.PreMul, hardener, resonances[i], Penalize: true));
            shipResonance.Resolved = null;
        }
    }

    private bool IsReactiveArmorHardener(DogmaItem module) =>
        data.GetTypeEffects(module.TypeId).Any(typeEffect => typeEffect.EffectId == AdaptiveArmorHardenerEffectId);

    // The adaptiveArmorHardener loop: each cycle the two least-damaged resonances give up to shift% resist to the
    // two most-damaged; once the profile repeats, average the cycles in the loop. Indices are EM/Thermal/Kinetic/
    // Explosive (the ArmorResonance order); the fixed (EM, Explosive, Kinetic, Thermal) scan order reproduces in-game
    // tie-breaking under a stable sort.
    private static double[] Simulate(double[] baseResonance, double[] hardenerResonance, double shift)
    {
        var baseDamage = baseResonance.Select(resonance => UniformDamage * resonance).ToArray();
        var profile = (double[])hardenerResonance.Clone();
        var cycles = new List<double[]>();
        var loopStart = -1;

        for (var cycle = 0; cycle < CycleLimit; cycle++)
        {
            var ordered = new[] { 0, 3, 2, 1 }
                .Select(index => (Index: index, Damage: baseDamage[index] * profile[index], Resonance: profile[index]))
                .OrderBy(entry => entry.Damage)
                .ToArray();

            double change0 = Math.Min(shift, 1 - ordered[0].Resonance);
            double change1 = Math.Min(shift, 1 - ordered[1].Resonance);
            double change2, change3;
            if (ordered[2].Damage == 0)
            {
                // Only one damage type: the single hit type takes all the resist the other three give up.
                change0 = 1 - ordered[0].Resonance;
                change1 = 1 - ordered[1].Resonance;
                change2 = 1 - ordered[2].Resonance;
                change3 = -(change0 + change1 + change2);
            }
            else if (ordered[1].Damage == 0)
            {
                // Two damage types: the two hit types split the resist the other two give up.
                change0 = 1 - ordered[0].Resonance;
                change1 = 1 - ordered[1].Resonance;
                change2 = change3 = -(change0 + change1) / 2;
            }
            else
            {
                change2 = change3 = -(change0 + change1) / 2;
            }

            profile[ordered[0].Index] = ordered[0].Resonance + change0;
            profile[ordered[1].Index] = ordered[1].Resonance + change1;
            profile[ordered[2].Index] = ordered[2].Resonance + change2;
            profile[ordered[3].Index] = ordered[3].Resonance + change3;

            var repeat = cycles.FindIndex(seen => seen.Zip(profile, (a, b) => Math.Abs(a - b) <= LoopTolerance).All(equal => equal));
            if (repeat >= 0)
            {
                loopStart = repeat;
                break;
            }
            cycles.Add((double[])profile.Clone());
        }

        if (loopStart < 0)
            loopStart = Math.Max(0, cycles.Count - 20);   // no equilibrium found: average the tail

        var loop = cycles.Skip(loopStart).ToList();
        return Enumerable.Range(0, baseResonance.Length)
            .Select(index => Math.Round(loop.Sum(profileInLoop => profileInLoop[index]) / loop.Count, 3))
            .ToArray();
    }
}
