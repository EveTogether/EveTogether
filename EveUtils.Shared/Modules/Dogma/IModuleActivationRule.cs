using System.Collections.Generic;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// A cross-module activation constraint: given a module the user is switching to active and the other fitted modules'
/// current states, decides whether the activation is allowed. Rules are data-driven (keyed on dogma effects/attributes,
/// never a hard-coded type list) so a new exclusivity relation plugs in without touching the call-sites. The
/// first registered rule that returns a conflict wins.
/// </summary>
public interface IModuleActivationRule
{
    /// <summary>A conflict when <paramref name="targetTypeId"/> may not be activated alongside <paramref name="others"/>,
    /// or null when the activation is allowed.</summary>
    ModuleActivationConflict? CheckActivation(int targetTypeId, IReadOnlyList<ModuleInput> others, IDogmaDataAccessor data);
}
