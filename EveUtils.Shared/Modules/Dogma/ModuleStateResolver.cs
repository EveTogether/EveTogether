using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Derives the default activation state of a fitted module the way EVE does, and the states a module can be switched
/// to. A module defaults to active unless it carries an effect that cannot run active for a static stat readout (cloak,
/// jump portal/bridge, micro jump drive, cyno, …, clamped to online), it is barred from activating (attr 2363
/// <c>activationBlocked</c>), or a group-capacity limit is already reached (attr 763 <c>maxGroupActive</c> — only one
/// propulsion module runs at a time; attr 764 <c>maxGroupOnline</c> — only N of a group sit online at once). Shared by
/// the validation harness and the fit-detail window so both default a fit to the same in-game states; the caller keeps a
/// per-fit <see cref="ModuleStateAccumulator"/> so the group counters span the fit's modules.
/// </summary>
public static class ModuleStateResolver
{
    public const int MaxGroupActiveAttribute = 763;
    public const int MaxGroupOnlineAttribute = 764;
    public const int ActivationBlockedAttribute = 2363;

    private const int OverloadCategory = 5;
    // The generic "online" effect (16) is itself Active-category but sits on every onlineable module, so it is excluded —
    // a module is only activatable when it has a *non-online* effect in an activatable category: Active (1), Target
    // (2, e.g. a nosferatu / neutraliser / web / scram) or Area (3, an NPC-only smartbomb effect, effectively unused —
    // player smartbombs activate via empWave (38), which is category 1).
    private const int OnlineEffectId = 16;
    private static readonly IReadOnlySet<int> ActivatableCategories = new HashSet<int> { 1, 2, 3 };

    /// <summary>Effects clamped to online for a static readout: their active behaviour — e.g. a jump portal's -100%
    /// velocity penalty or a cloak's targeting lock-out — must not apply to a parked fit, so such modules default to
    /// online. Keyed on the SDE effect name, so the set survives a CCP effect-id renumber and tracks newly added
    /// clamp effects by name.</summary>
    public static readonly IReadOnlySet<string> OnlineOnlyEffectNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "cloaking", "cloakingWarpSafe", "cloakingPrototype", "cynosuralGeneration", "jumpPortalGeneration",
        "jumpPortalGenerationBO", "cloneJumpAccepting", "microJumpDrive", "microJumpPortalDrive", "emergencyHullEnergizer",
        "moduleBonusAssaultDamageControl", "moduleBonusIndustrialInvulnerability", "massEntanglerEffect5",
        "electronicAttributeModifyOnline", "targetPassively", "cargoScan", "shipScan", "surveyScan",
        "targetSpectrumBreakerBonus", "interdictionNullifierBonus", "warpCoreStabilizerActive", "industrialItemCompression"
    };

    /// <summary>The state a freshly mapped module should take, mirroring how an EFT import works. <paramref name="accumulator"/>
    /// accumulates the active/online modules per group across the fit so the second propulsion module of a group falls
    /// back to online and the surplus over a group's online cap falls back to offline.</summary>
    public static ModuleState DefaultState(int moduleTypeId, IDogmaDataAccessor data, ModuleStateAccumulator accumulator)
    {
        var groupId = data.GetGroupId(moduleTypeId) ?? 0;
        var state = _IntendedState(moduleTypeId, data, accumulator, groupId);

        // maxGroupOnline (attr 764): only N of the group may sit online-or-higher at once; the surplus drops to offline.
        if (state >= ModuleState.Online
            && MaxGroupLimit(moduleTypeId, data, MaxGroupOnlineAttribute) is { } onlineLimit
            && accumulator.OnlinePerGroup.GetValueOrDefault(groupId) >= onlineLimit)
            state = ModuleState.Passive;

        if (state >= ModuleState.Online)
            accumulator.OnlinePerGroup[groupId] = accumulator.OnlinePerGroup.GetValueOrDefault(groupId) + 1;
        if (state >= ModuleState.Active)
            accumulator.ActivePerGroup[groupId] = accumulator.ActivePerGroup.GetValueOrDefault(groupId) + 1;

        return state;
    }

    // The default state before the maxGroupOnline clamp: active unless online-clamped, passive, activation-blocked, or
    // already at the group's max-active limit.
    private static ModuleState _IntendedState(int moduleTypeId, IDogmaDataAccessor data, ModuleStateAccumulator accumulator, int groupId)
    {
        if (HasOnlineOnlyEffect(moduleTypeId, data))
            return ModuleState.Online;

        // Passive modules — those without any active-category effect (plates, damage controls, heat sinks, shield
        // extenders, …) — can't run active; they default to online, like the in-game fit.
        if (!HasActiveEffect(moduleTypeId, data))
            return ModuleState.Online;

        // activationBlocked (attr 2363): the module carries an active effect but is barred from activating (state
        // validation rejects ACTIVE) → clamp to online.
        if (AttributeValue(moduleTypeId, data, ActivationBlockedAttribute) > 0)
            return ModuleState.Online;

        if (MaxGroupLimit(moduleTypeId, data, MaxGroupActiveAttribute) is { } activeLimit
            && accumulator.ActivePerGroup.GetValueOrDefault(groupId) >= activeLimit)
            return ModuleState.Online;

        return ModuleState.Active;
    }

    /// <summary>The states the user may switch a module to: every fitted module can be offline (passive) or online;
    /// active is offered only when the module carries an activatable effect and is not activation-blocked, overload only
    /// when it carries an overload-category effect.</summary>
    public static IReadOnlyList<ModuleState> ValidStates(int moduleTypeId, IDogmaDataAccessor data)
    {
        var categories = data.GetTypeEffects(moduleTypeId)
            .Where(effect => effect.EffectId != OnlineEffectId)   // the online effect is Active-category on every module
            .Select(effect => data.GetEffect(effect.EffectId)?.EffectCategoryId)
            .Where(category => category is not null)
            .Select(category => category!.Value)
            .ToHashSet();

        var states = new List<ModuleState> { ModuleState.Passive, ModuleState.Online };
        if (categories.Overlaps(ActivatableCategories) && AttributeValue(moduleTypeId, data, ActivationBlockedAttribute) <= 0)
            states.Add(ModuleState.Active);
        if (categories.Contains(OverloadCategory))
            states.Add(ModuleState.Overload);
        return states;
    }

    // Whether the module carries a named online-clamp effect (cloak, cyno, jump portal, MJD, …) — keyed on the SDE
    // effect name.
    private static bool HasOnlineOnlyEffect(int moduleTypeId, IDogmaDataAccessor data) =>
        data.GetTypeEffects(moduleTypeId)
            .Any(typeEffect => data.GetEffect(typeEffect.EffectId) is { } effect && OnlineOnlyEffectNames.Contains(effect.Name));

    // Whether the module carries any active-category effect (so it can run "active" rather than just online/passive).
    private static bool HasActiveEffect(int moduleTypeId, IDogmaDataAccessor data) =>
        data.GetTypeEffects(moduleTypeId)
            .Any(effect => effect.EffectId != OnlineEffectId
                && data.GetEffect(effect.EffectId)?.EffectCategoryId is { } category && ActivatableCategories.Contains(category));

    // A type's group-capacity limit for the given attribute (763 maxGroupActive / 764 maxGroupOnline): how many modules
    // of this type's group may be active/online at once (null = no limit).
    private static int? MaxGroupLimit(int typeId, IDogmaDataAccessor data, int attributeId)
    {
        var value = AttributeValue(typeId, data, attributeId);
        return value > 0 ? (int)value : null;
    }

    // The type's base value for an attribute, or 0 when the attribute is absent.
    private static double AttributeValue(int typeId, IDogmaDataAccessor data, int attributeId)
    {
        foreach (var attribute in data.GetBaseAttributes(typeId))
            if (attribute.AttributeId == attributeId)
                return attribute.Value;
        return 0;
    }
}
