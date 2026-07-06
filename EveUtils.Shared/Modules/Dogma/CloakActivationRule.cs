using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// EVE refuses to run a cloak and another active module at the same time ("You can't cloak because the module X is
/// active", and the reverse). The in-game 5-second grace for non-offensive modules is a runtime timeline that a static
/// fit has no axis for, so the rule treats a cloak and any other active module as mutually exclusive. Cloak detection is
/// data-driven: a category-1 effect whose name starts with "cloaking" (cloaking 607 / cloakingWarpSafe 980 /
/// cloakingPrototype 5945), never a hard-coded type list.
/// </summary>
internal sealed class CloakActivationRule : IModuleActivationRule
{
    public ModuleActivationConflict? CheckActivation(int targetTypeId, IReadOnlyList<ModuleInput> others, IDogmaDataAccessor data)
    {
        // A module counts as active once its state reaches Active (active or overloaded).
        var activeOthers = others.Where(other => other.State >= ModuleState.Active).ToList();

        // Activating the cloak is barred while any other (non-cloak) module is active.
        if (IsCloak(targetTypeId, data))
        {
            var blocker = activeOthers.FirstOrDefault(other => !IsCloak(other.TypeId, data));
            return blocker is null ? null : new ModuleActivationConflict(blocker.TypeId, ModuleActivationReason.CloakMutualExclusion);
        }

        // Activating any other module is barred while a cloak is active.
        var cloak = activeOthers.FirstOrDefault(other => IsCloak(other.TypeId, data));
        return cloak is null ? null : new ModuleActivationConflict(cloak.TypeId, ModuleActivationReason.CloakMutualExclusion);
    }

    private static bool IsCloak(int typeId, IDogmaDataAccessor data) =>
        data.GetTypeEffects(typeId)
            .Any(typeEffect => data.GetEffect(typeEffect.EffectId) is { EffectCategoryId: 1 } effect
                && effect.Name.StartsWith("cloaking", StringComparison.Ordinal));
}
