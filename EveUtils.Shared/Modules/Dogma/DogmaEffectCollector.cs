using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Pass 2 (design §3): walks every item's effects, drops the ones its state does not activate, and registers each
/// modifier on its target attribute(s) via Func/Domain routing. Pre-computes the stacking-penalty flag per modifier
/// (penalisable operator AND non-stackable target attribute AND non-exempt source category). No values are computed
/// here — that is pass 3.
/// </summary>
public sealed class DogmaEffectCollector(IDogmaDataAccessor data) : ISingletonService
{
    public void Collect(DogmaFit fit)
    {
        foreach (var source in fit.AllItems)
            foreach (var typeEffect in data.GetTypeEffects(source.TypeId))
            {
                var effect = data.GetEffect(typeEffect.EffectId);
                if (effect is null || IsBoosterSideEffect(effect) || !IsActive(source, effect))
                    continue;
                foreach (var modifier in effect.Modifiers)
                    Register(fit, source, modifier);
            }
    }

    private static bool IsActive(DogmaItem item, DogmaEffectDef effect) =>
        item.IsAlwaysOn || EffectActivation.IsActiveAt(effect.EffectCategoryId, item.State);

    // Booster side-effects (CCP's "booster<Stat>Penalty" effects, e.g. boosterArmorHpPenalty) are off by default:
    // only a booster's primary bonuses apply unless the drawback is explicitly enabled. Skip them so a drug in
    // the fit does not silently apply its penalty (a Standard Exile would otherwise cut the ship's armor HP).
    private static bool IsBoosterSideEffect(DogmaEffectDef effect) =>
        effect.Name.StartsWith("booster", StringComparison.OrdinalIgnoreCase)
        && effect.Name.Contains("Penalty", StringComparison.OrdinalIgnoreCase);

    private void Register(DogmaFit fit, DogmaItem source, ModifierInfo modifier)
    {
        if (modifier.Func == ModifierFunc.EffectStopper)            // V-3: stoppers gate online-ability, not stats
            return;
        if (modifier.ModifiedAttributeId is not { } modifiedAttributeId)
            return;
        if (modifier.ModifyingAttributeId is not { } modifyingAttributeId)
            return;
        if (!DogmaOperators.TryFromOperation(modifier.Operation, out var op))   // skips operation 9 + unknown codes
            return;

        var meta = data.GetAttributeMeta(modifiedAttributeId);
        var penalize = DogmaOperators.IsPenalizable(op)
            && meta is { Stackable: false }
            && !DogmaPenalty.ExemptCategoryIds.Contains(source.CategoryId);
        var defaultValue = meta?.DefaultValue ?? 0;

        foreach (var target in ResolveTargets(fit, source, modifier))
            target.GetOrAdd(modifiedAttributeId, defaultValue)
                .AddModifier(new Modifier(op, source, modifyingAttributeId, penalize));
    }

    private static IReadOnlyList<DogmaItem> ResolveTargets(DogmaFit fit, DogmaItem source, ModifierInfo modifier) =>
        modifier.Func switch
        {
            ModifierFunc.ItemModifier => Anchor(fit, source, modifier.Domain) is { } anchor ? [anchor] : [],
            ModifierFunc.LocationModifier => LocationItems(fit, modifier.Domain),
            ModifierFunc.LocationGroupModifier =>
                LocationItems(fit, modifier.Domain).Where(item => item.GroupId == modifier.GroupId).ToList(),
            ModifierFunc.LocationRequiredSkillModifier =>
                LocationItems(fit, modifier.Domain).Where(item => RequiresSkill(item, modifier.SkillTypeId ?? source.TypeId)).ToList(),
            // OwnerRequiredSkillModifier targets the char-owned equipment that requires the skill (drone/charge skill
            // bonuses). A null skillTypeID is the patch convention for "the carrying skill itself" (e.g. effect 1730
            // droneDmgBonus, carried by Medium Drone Operation / Gallente Drone Specialization).
            ModifierFunc.OwnerRequiredSkillModifier =>
                OwnedItems(fit).Where(item => RequiresSkill(item, modifier.SkillTypeId ?? source.TypeId)).ToList(),
            // EffectStopper/Unknown never reach here.
            _ => []
        };

    private static DogmaItem? Anchor(DogmaFit fit, DogmaItem source, ModifierDomain domain) => domain switch
    {
        ModifierDomain.ItemId => source,
        ModifierDomain.ShipId => fit.Ship,
        // A structure is the "ship" of its own graph; structure role bonuses and Structure-Management skills route to
        // it (and its modules) via the structureID domain rather than shipID.
        ModifierDomain.StructureId => fit.Ship,
        ModifierDomain.CharId => fit.Character,
        // OtherId = the paired item: a module's loaded charge, or a charge's host module (none -> skip, V-4).
        ModifierDomain.OtherId => source.Charge ?? source.Host,
        // Target reserved.
        _ => null
    };

    private static IReadOnlyList<DogmaItem> LocationItems(DogmaFit fit, ModifierDomain domain) => domain switch
    {
        // A shipID LocationModifier targets the CONTENTS of the ship location (the modules), not the ship hull itself.
        // This is asymmetric with Anchor(shipID) (an ItemModifier, which targets the hull) on purpose, and verified
        // (2026-06-13) against the SDE + the reference oracle: every LocationModifier(shipID) effect modifies a module
        // attribute (Thermodynamics heatDamage, cpuUsageMultiply, the overload* bonuses), none a hull attribute, and
        // the Thermodynamics heat bonus applies to the module (9.6 -> 7.2) while the hull stays untouched. Adding
        // fit.Ship here would wrongly apply those module effects to the hull. Drones live in the owner location,
        // not shipID (see OwnedItems).
        ModifierDomain.ShipId => fit.Modules,
        ModifierDomain.StructureId => fit.Modules,                  // a structure's Standup modules sit in the structure location
        ModifierDomain.CharId => [.. fit.Skills, .. fit.Implants],  // skills and implants both sit in the char location
        _ => []
    };

    // Char-owned equipment: modules, their charges and drones. OwnerRequiredSkillModifier bonuses (e.g. drone damage
    // skills) apply to these where they require the skill; the skill items themselves are never targets.
    private static IEnumerable<DogmaItem> OwnedItems(DogmaFit fit)
    {
        foreach (var module in fit.Modules)
        {
            yield return module;
            if (module.Charge is { } charge)
                yield return charge;
        }
        foreach (var drone in fit.Drones)
            yield return drone;
        // Launched fighter squadrons are char-owned too: the Fighters skill (+5%/lvl damage) and the carrier/supercarrier
        // hull fighter-damage bonuses both route via OwnerRequiredSkillModifier, so they must see the fighters here.
        foreach (var fighter in fit.Fighters)
            yield return fighter;
    }

    private static bool RequiresSkill(DogmaItem item, int? skillTypeId)
    {
        if (skillTypeId is null)
            return false;
        foreach (var attributeId in DogmaAttributeIds.RequiredSkill)
            if (item.TryGet(attributeId, out var value) && (int)value.BaseValue == skillTypeId)
                return true;
        return false;
    }
}
