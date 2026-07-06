using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Pass 3 (design §3): resolves an attribute's value by folding its registered modifiers in the canonical operator
/// order, with stacking penalties applied per bucket. Lazy + memoised — a source value is resolved recursively (the
/// memoisation walks the dependency graph; EVE dogma is acyclic, a self-cycle falls back to base). Operator folds:
/// assignment picks min/max on highIsGood; ModAdd/ModSub are additive; the multiplicative operators stack with
/// <c>Factor^(rank^2)</c> after sorting each penalised bucket by |delta| descending (the VC-01 fix: one bucket per
/// attribute x domain, never per type).
/// </summary>
public sealed class DogmaEvaluator(IDogmaDataAccessor data) : ISingletonService
{
    public double Resolve(DogmaItem item, int attributeId)
    {
        var meta = data.GetAttributeMeta(attributeId);
        var resolved = item.TryGet(attributeId, out var value)
            ? Resolve(value, attributeId)
            : meta?.DefaultValue ?? 0;
        // EVE caps an attribute at the value of its maxAttributeID on the same item: damage resonances cap at 1.0
        // (0% resist), so a Polarized weapon's resistanceKiller (100) cannot push EHP below the raw hit points.
        if (meta?.MaxAttributeId is { } capAttributeId)
            resolved = Math.Min(resolved, Resolve(item, capAttributeId));
        return resolved;
    }

    private double Resolve(DogmaValue value, int attributeId)
    {
        if (value.Resolved is { } cached)
            return cached;
        if (value.IsForced)
            return (value.Resolved = value.BaseValue).Value;
        if (value.Resolving)
            return value.BaseValue;   // cycle break

        value.Resolving = true;
        var current = value.BaseValue;
        foreach (var op in DogmaOperators.ApplyOrder)
        {
            var modifiers = value.Modifiers.Where(modifier => modifier.Operator == op).ToList();
            if (modifiers.Count == 0)
                continue;
            current = DogmaOperators.IsAssignment(op) ? Assign(op, current, modifiers, attributeId)
                : DogmaOperators.IsPenalizable(op) ? Multiply(op, current, modifiers)
                : Add(op, current, modifiers);
        }
        value.Resolving = false;
        value.Resolved = current;
        return current;
    }

    private double Add(EffectOperator op, double current, List<Modifier> modifiers)
    {
        var sum = modifiers.Sum(SourceValue);
        return op == EffectOperator.ModAdd ? current + sum : current - sum;
    }

    private double Assign(EffectOperator op, double current, List<Modifier> modifiers, int attributeId)
    {
        // An assignment overrides the base: pick the most favourable assigned value (highIsGood -> max, else min).
        var highIsGood = data.GetAttributeMeta(attributeId)?.HighIsGood ?? true;
        var values = modifiers.Select(SourceValue);
        return highIsGood ? values.Max() : values.Min();
    }

    private double Multiply(EffectOperator op, double current, List<Modifier> modifiers)
    {
        var unpenalised = new List<double>();
        var positive = new List<double>();
        var negative = new List<double>();
        foreach (var modifier in modifiers)
        {
            var delta = Delta(op, SourceValue(modifier));
            if (!modifier.Penalize)
                unpenalised.Add(delta);
            else if (delta >= 0)
                positive.Add(delta);
            else
                negative.Add(delta);
        }

        foreach (var delta in unpenalised)
            current *= 1 + delta;
        current = ApplyBucket(current, positive);
        current = ApplyBucket(current, negative);
        return current;
    }

    private static double ApplyBucket(double current, List<double> deltas)
    {
        // Strongest modifier first (rank 0 = unpenalised); weaker ones decay by Factor^(rank^2).
        deltas.Sort((left, right) => Math.Abs(right).CompareTo(Math.Abs(left)));
        for (var rank = 0; rank < deltas.Count; rank++)
            current *= 1 + deltas[rank] * DogmaPenalty.StackingMultiplier(rank);
        return current;
    }

    // The fractional change a multiplicative operator contributes, normalised to "current *= 1 + delta".
    private static double Delta(EffectOperator op, double sourceValue) => op switch
    {
        EffectOperator.PreMul or EffectOperator.PostMul => sourceValue - 1,
        // Guard against a zero source (offline/unloaded module): 1/0 would yield Infinity/NaN and poison the whole
        // memoised resolve chain via current *= 1 + delta. A zero divisor contributes no change.
        EffectOperator.PreDiv or EffectOperator.PostDiv => sourceValue != 0 ? 1 / sourceValue - 1 : 0,
        EffectOperator.PostPercent => sourceValue / 100,
        _ => 0
    };

    private double SourceValue(Modifier modifier) => Resolve(modifier.Source, modifier.SourceAttributeId);
}
