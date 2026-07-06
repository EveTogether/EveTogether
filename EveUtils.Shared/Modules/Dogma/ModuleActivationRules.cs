using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The registered cross-module activation rules. Evaluates them in order and returns the first conflict, so
/// the fit-detail wheel can refuse an activation and tell the user why. Add a rule to <see cref="Rules"/> to cover a new
/// exclusivity relation without touching the call-sites.
/// </summary>
public static class ModuleActivationRules
{
    private static readonly IReadOnlyList<IModuleActivationRule> Rules = [new CloakActivationRule()];

    /// <summary>The first conflict that bars activating <paramref name="targetTypeId"/> alongside the fit's other
    /// modules, or null when the activation is allowed.</summary>
    public static ModuleActivationConflict? FirstConflict(int targetTypeId, IReadOnlyList<ModuleInput> others, IDogmaDataAccessor data) =>
        Rules.Select(rule => rule.CheckActivation(targetTypeId, others, data)).FirstOrDefault(conflict => conflict is not null);
}
