using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Plans.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// The fit SKILL IMPACT works on (mockup v5 "FROM A FIT"): its name, the engine input and the plan write that tags
/// rows with this fit. <see cref="PickAsync"/> is the one place a library fit becomes that input — PLANS → + FROM FIT
/// and the screen's own fit selector both go through it.
/// </summary>
public sealed record SkillImpactFit(string FitName, FitInput Input,
    Func<IReadOnlyList<SkillPlanRowDraft>, string, Task>? AddToPlan);

public static class SkillImpactFitPicker
{
    /// <summary>Opens the library fit picker and builds the picked fit at <paramref name="levels"/>; null when the
    /// pilot cancels. Throws when the fit's stored JSON cannot be read, so the caller shows why.</summary>
    public static async Task<SkillImpactFit?> PickAsync(IServiceProvider services, IDialogService dialogs,
        IReadOnlyDictionary<int, int> levels, Func<FitReferenceInfo, Func<IReadOnlyList<SkillPlanRowDraft>, string, Task>?>? addToPlanFor)
    {
        var picker = new FitPickerViewModel(services, FitPickerMode.Single, alreadyAdded: null, composition: null, currentFitHash: null);
        if (await dialogs.PickFitAsync(picker) is not { } fit)
        {
            return null;
        }

        EsiFitting? esi;
        try
        {
            esi = JsonSerializer.Deserialize<EsiFitting>(fit.RawJson);
        }
        catch (JsonException)
        {
            esi = null;
        }

        if (esi is null || services.GetService<ISdeAccessor>() is not { } sde || services.GetService<IDogmaDataAccessor>() is not { } dogma)
        {
            throw new InvalidOperationException($"The fit \"{fit.FitName}\" could not be read.");
        }

        var input = new FitInput(esi.ShipTypeId, FitInputMapper.BuildModules(esi, sde, dogma), SkillSource.From(levels),
            FitInputMapper.BuildDrones(esi));
        return new SkillImpactFit(fit.FitName, input, addToPlanFor?.Invoke(fit));
    }
}
