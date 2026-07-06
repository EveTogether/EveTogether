using System.Linq;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Fit resource math shared by the detail view-model (the gauge) and the fit validator (the overload check).
/// Calibration used/total = the ship's upgradeCapacity (1132) and the sum of each rig's upgradeCost (1153), read off
/// the same SDE base attributes. One place so the gauge and the overload check never disagree.
/// </summary>
public static class FitResourceMath
{
    private const int UpgradeCapacity = 1132;  // ship calibration total
    private const int UpgradeCost = 1153;      // a rig's calibration cost

    public static (double Used, double Total) Calibration(EsiFitting fit, IDogmaDataAccessor? data)
    {
        if (data is null) return (0, 0);
        double Attribute(int typeId, int attributeId) =>
            data.GetBaseAttributes(typeId).FirstOrDefault(attribute => attribute.AttributeId == attributeId)?.Value ?? 0;
        var total = Attribute(fit.ShipTypeId, UpgradeCapacity);
        var used = fit.Items
            .Where(item => FitSlotClassifier.Classify(item.Flag) == FitSlotCategory.Rig)
            .Sum(item => Attribute(item.TypeId, UpgradeCost) * item.Quantity);
        return (used, total);
    }
}
