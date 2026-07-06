using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// The charges a module can load: a module declares the charge groups it accepts (chargeGroup1..5) and, for
/// sized weapons, a charge size (attr 128) the charge must match. We list the published charges in each accepted group
/// from the SDE and keep those whose size matches (or all, for unsized modules like missile launchers / scriptable mods).
/// </summary>
public static class ChargeCompatibility
{
    private static readonly int[] ChargeGroupAttributes = [604, 605, 606, 609, 610];
    private const int ChargeSizeAttribute = 128;

    public static IReadOnlyList<SdeChargeType> For(int moduleTypeId, ISdeAccessor sde)
    {
        var attributes = sde.GetDogmaAttributes(moduleTypeId);
        var groups = attributes
            .Where(a => ChargeGroupAttributes.Contains(a.AttributeId) && a.Value > 0)
            .Select(a => (int)a.Value)
            .Distinct()
            .ToList();
        if (groups.Count == 0)
            return [];   // the module takes no charge

        var moduleSize = attributes.FirstOrDefault(a => a.AttributeId == ChargeSizeAttribute)?.Value;

        return groups
            .SelectMany(sde.GetChargeTypesInGroup)
            .Where(charge => moduleSize is null || charge.ChargeSize == moduleSize)
            .GroupBy(charge => charge.TypeId)
            .Select(group => group.First())
            .OrderBy(charge => charge.Name)
            .ToList();
    }
}
