using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Maps an <see cref="EsiFitting"/> to the Dogma engine's <see cref="ModuleInput"/> / <see cref="DroneInput"/> lists
/// . Each fitted module is paired with the charge sitting in the same slot flag (a no-slot-type item), so turret
/// and missile DPS are computed; drone-bay items are grouped per type. Each module gets its in-game default state via
/// <see cref="ModuleStateResolver"/> (the same logic the validation harness uses). Walks the items in fit order so the
/// max-active group clamp keeps the first propulsion module active. Pure + SDE-driven, so it is unit-testable apart
/// from the engine.
/// </summary>
public static class FitInputMapper
{
    public static IReadOnlyList<ModuleInput> BuildModules(EsiFitting fit, ISdeAccessor sde, IDogmaDataAccessor data)
    {
        var modules = new List<ModuleInput>();
        var accumulator = new ModuleStateAccumulator();
        var seenFlags = new HashSet<string>();
        foreach (var entry in fit.Items)
        {
            if (!IsSlotFlag(entry.Flag) || !seenFlags.Add(entry.Flag)) continue;
            var slot = fit.Items.Where(i => i.Flag == entry.Flag).ToList();
            var module = slot.FirstOrDefault(i => sde.GetSlotType(i.TypeId) != SdeSlotType.None);
            if (module is null) continue;
            var charge = slot.FirstOrDefault(i => sde.GetSlotType(i.TypeId) == SdeSlotType.None);
            var state = ModuleStateResolver.DefaultState(module.TypeId, data, accumulator);
            modules.Add(new ModuleInput(module.TypeId, state, charge?.TypeId));
        }
        return modules;
    }

    private static bool IsSlotFlag(string flag) => FitSlotClassifier.Classify(flag) is
        FitSlotCategory.High or FitSlotCategory.Medium or FitSlotCategory.Low
        or FitSlotCategory.Rig or FitSlotCategory.Subsystem;

    public static IReadOnlyList<DroneInput> BuildDrones(EsiFitting fit) =>
        fit.Items
            .Where(i => i.Flag.StartsWith("DroneBay", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.TypeId)
            .Select(g => new DroneInput(g.Key, g.Sum(i => i.Quantity)))
            .ToList();
}
